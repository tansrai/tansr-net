using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Windows.Mcp;

internal sealed class HttpMcpTransport : IMcpTransport
{
    private readonly Uri _endpoint;
    private readonly Dictionary<string, string> _headers;
    private readonly Func<Uri, string, CancellationToken, Task>? _authorize;
    private readonly HttpClient _http;
    private readonly int _maximumBytes;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _operations = new();
    private string? _session;
    private bool _closed;
    private Task? _close;

    internal HttpMcpTransport(McpHttpOptions options, int maximumBytes, int maximumPendingRequests)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        _endpoint = options.Endpoint;
        if (!_endpoint.IsAbsoluteUri || _endpoint.UserInfo.Length != 0 || _endpoint.Fragment.Length != 0 ||
            (_endpoint.Scheme != Uri.UriSchemeHttps && !(options.AllowLoopbackHttp && _endpoint.Scheme == Uri.UriSchemeHttp && _endpoint.IsLoopback)) ||
            !options.AllowedEndpoints.Any(x => x.IsAbsoluteUri && string.Equals(x.AbsoluteUri, _endpoint.AbsoluteUri, StringComparison.Ordinal))) throw new ArgumentException("MCP endpoint is not explicitly allowed.");
        _maximumBytes = maximumBytes; _authorize = options.Authorize;
        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in options.Headers)
        {
            if (header.Key.Length < 1 || header.Key.Length > 128 || header.Key.Any(x => !(char.IsLetterOrDigit(x) && x < 128) && x != '-') ||
                header.Value == null || header.Value.Length > 8192 || header.Value.Any(x => x < 32 || x == 127) ||
                new[] { "host", "cookie", "content-length", "transfer-encoding", "connection", "accept", "content-type", "mcp-session-id", "mcp-protocol-version" }.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Invalid MCP header.");
            _headers.Add(header.Key, header.Value);
        }
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            UseDefaultCredentials = false,
            // Each pending request may hold an SSE response while replying to a server
            // request. Keep one bounded connection free for those replies on .NET Framework.
            MaxConnectionsPerServer = maximumPendingRequests + 1,
            AutomaticDecompression = DecompressionMethods.None
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool IsClosed { get { lock (_gate) return _closed; } }
    public string? ProtocolVersion { get; set; }

    public Task<JsonElement> RequestAsync(JsonElement message, CancellationToken cancellationToken) => Track(() => RequestCoreAsync(message, cancellationToken));
    public Task SendAsync(JsonElement message, CancellationToken cancellationToken) => Track(async () => { await SendCoreAsync(message, cancellationToken).ConfigureAwait(false); return true; });

    private Task<T> Track<T>(Func<Task<T>> start)
    {
        lock (_gate)
        {
            if (_closed) throw new McpException("transport_closed");
            Task<T> task = start(); _operations.Add(task);
            _ = task.ContinueWith(finished => { lock (_gate) _operations.Remove(finished); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<JsonElement> RequestCoreAsync(JsonElement message, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        using var response = await PostAsync(message, linked.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new McpException("http_error");
        string expected = McpJson.Id(message);
        string? type = response.Content.Headers.ContentType?.MediaType;
        string? charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (charset != null && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)) throw new McpException("invalid_encoding");
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        // Framework's response stream does not observe cancellation after a read has
        // begun. Closing the owned body also lets CloseAsync actually drain operations.
        using var closeOnCancellation = linked.Token.Register(stream.Dispose);
        var reader = new BoundedBody(stream, _maximumBytes, string.Equals(type, "text/event-stream", StringComparison.OrdinalIgnoreCase));
        if (string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            string raw = await reader.ReadAllAsync(linked.Token).ConfigureAwait(false);
            JsonElement? found = null;
            foreach (var item in McpJson.Frames(McpJson.Parse(raw, _maximumBytes)))
            {
                if (item.TryGetProperty("method", out _)) throw new McpException("invalid_http_response");
                if (McpJson.Id(item) != expected || found.HasValue) throw new McpException("response_mismatch");
                found = item;
            }
            if (!found.HasValue) throw new McpException("missing_response");
            return McpJson.Result(found.Value);
        }
        if (!string.Equals(type, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw new McpException("invalid_content_type");
        var data = new StringBuilder(); int events = 0;
        while (true)
        {
            string? line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            if (line == null) throw new McpException("incomplete_response");
            if (line.Length > 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line.Substring(5).TrimStart(' ')); }
                continue;
            }
            if (data.Length == 0) continue;
            if (++events > 1024) throw new McpException("event_limit");
            var eventData = McpJson.Parse(data.ToString(), _maximumBytes); data.Clear();
            JsonElement? foundResult = null;
            foreach (var item in McpJson.Frames(eventData))
            {
                if (item.TryGetProperty("method", out _))
                {
                    if (item.TryGetProperty("id", out _)) await SendCoreAsync(McpJson.ServerReply(item), linked.Token).ConfigureAwait(false);
                }
                else
                {
                    if (McpJson.Id(item) != expected || foundResult.HasValue) throw new McpException("response_mismatch");
                    foundResult = item.Clone();
                }
            }
            if (foundResult.HasValue) return McpJson.Result(foundResult.Value);
        }
    }

    private async Task SendCoreAsync(JsonElement message, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        using var response = await PostAsync(message, linked.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted) throw new McpException("notification_not_accepted");
    }

    private async Task<HttpResponseMessage> PostAsync(JsonElement message, CancellationToken cancellationToken)
    {
        bool initialize = message.TryGetProperty("method", out var method) && method.GetString() == "initialize";
        using var request = CreateRequest(HttpMethod.Post);
        request.Content = new StringContent(message.GetRawText(), Encoding.UTF8, "application/json");
        if (_authorize != null) await _authorize(_endpoint, "POST", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399) throw new McpException("redirect_rejected");
            if (response.StatusCode == HttpStatusCode.NotFound && _session != null)
            { lock (_gate) _closed = true; _stop.Cancel(); throw new McpException("session_expired"); }
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                string[] sessions = values.ToArray();
                if (sessions.Length != 1 || sessions[0].Length < 1 || sessions[0].Length > 1024 || sessions[0].Any(x => x < 0x21 || x > 0x7e) ||
                    (!initialize && sessions[0] != _session)) throw new McpException("invalid_session");
                if (initialize) _session = sessions[0];
            }
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, _endpoint);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        foreach (var header in _headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (_session != null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        if (ProtocolVersion != null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        return request;
    }

    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_close != null) return _close;
            _closed = true; _stop.Cancel();
            return _close = CloseCoreAsync(_operations.ToArray());
        }
    }

    private async Task CloseCoreAsync(Task[] operations)
    {
        try
        {
            try { await Task.WhenAll(operations).ConfigureAwait(false); } catch (Exception) { /* 调用者仍拿到原失败。 */ }
            if (_session != null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var request = CreateRequest(HttpMethod.Delete);
                if (_authorize != null) await _authorize(_endpoint, "DELETE", deadline.Token).ConfigureAwait(false);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MethodNotAllowed && response.StatusCode != HttpStatusCode.NotFound) throw new McpException("close_failed");
            }
        }
        finally { _http.Dispose(); }
    }

    /// <summary>按真实字节限额消费 JSON/SSE；不先把无限响应读成字符串。</summary>
    private sealed class BoundedBody
    {
        private readonly Stream _stream; private readonly int _maximum; private readonly byte[] _buffer = new byte[4096];
        private readonly bool _streaming;
        private int _position, _length, _total;
        internal BoundedBody(Stream stream, int maximum, bool streaming) { _stream = stream; _maximum = maximum; _streaming = streaming; }
        private async Task<int> ReadByteAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_position == _length)
            {
                try
                {
                    _length = _streaming
                        ? await StreamingBodyReader.ReadAsync(_stream, _buffer, 0, _buffer.Length, token, true).ConfigureAwait(false)
                        : await _stream.ReadAsync(_buffer, 0, _buffer.Length, token).ConfigureAwait(false);
                }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                token.ThrowIfCancellationRequested(); _position = 0;
                if (_length == 0) return -1;
                _total += _length; if (_total > _maximum) throw new McpException("response_limit");
            }
            return _buffer[_position++];
        }
        internal async Task<string?> ReadLineAsync(CancellationToken token)
        {
            using var line = new MemoryStream(); int value;
            while ((value = await ReadByteAsync(token).ConfigureAwait(false)) >= 0)
            {
                if (value == 10) return Decode(line.ToArray()).TrimEnd('\r');
                line.WriteByte((byte)value);
            }
            if (line.Length != 0) throw new McpException("incomplete_frame");
            return null;
        }
        internal async Task<string> ReadAllAsync(CancellationToken token)
        {
            using var body = new MemoryStream(); int value;
            while ((value = await ReadByteAsync(token).ConfigureAwait(false)) >= 0) body.WriteByte((byte)value);
            return Decode(body.ToArray());
        }
        private static string Decode(byte[] bytes)
        { try { return new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { throw new McpException("invalid_encoding"); } }
    }
}
