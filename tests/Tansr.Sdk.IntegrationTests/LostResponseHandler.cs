using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>仅测试使用：原请求真实成功后丢弃一次响应，不制造服务端回执。</summary>
internal sealed class LostResponseHandler : DelegatingHandler
{
    private readonly Func<HttpRequestMessage, bool> _matches;
    private readonly int _maximumJsonBytes;
    private readonly ConcurrentQueue<LostResponseRequest> _requests = new();
    private readonly object _evidenceGate = new();
    private JsonElement? _lostRequest;
    private JsonElement? _lostReceipt;
    private int _state;
    private int _lostResponseCount;

    public LostResponseHandler(string pathSuffix, HttpMessageHandler? innerHandler = null, int maximumJsonBytes = 528384)
        : this(MatchSuffix(pathSuffix), innerHandler, maximumJsonBytes)
    {
    }

    public LostResponseHandler(Func<HttpRequestMessage, bool> matches, HttpMessageHandler? innerHandler = null,
        int maximumJsonBytes = 528384)
        : base(innerHandler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        })
    {
        _matches = matches ?? throw new ArgumentNullException(nameof(matches));
        if (maximumJsonBytes <= 0 || maximumJsonBytes > 528384)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumJsonBytes));
        }
        _maximumJsonBytes = maximumJsonBytes;
    }

    /// <summary>目标请求真实发送前的屏障；钩子必须传递取消，不用计时等待推断服务端提交。</summary>
    public Func<LostResponseRequest, CancellationToken, Task>? BeforeSendAsync { get; set; }

    /// <summary>仅保留方法、路径、查询与 JSON 正文；不采集请求头或认证票据。</summary>
    public IReadOnlyList<LostResponseRequest> Requests => _requests.ToArray();

    public JsonElement? LostRequest { get { lock (_evidenceGate) { return _lostRequest?.Clone(); } } }
    public JsonElement? LostReceipt { get { lock (_evidenceGate) { return _lostReceipt?.Clone(); } } }
    public int LostResponseCount => Volatile.Read(ref _lostResponseCount);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw new InvalidOperationException("请求缺少地址。");
        JsonElement? body = null;
        if (request.Content is not null && IsJson(request.Content.Headers.ContentType?.MediaType))
        {
            if (request.Content.Headers.ContentLength > _maximumJsonBytes)
            {
                throw new InvalidOperationException("测试请求超过 JSON 证据限额。");
            }
            // HttpContent 缓存正文后仍能原样发送；不能先耗尽 StreamContent 的底层流。
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            // SDK1 requests are ordinary strict JSON; only the targeted SDK2 receipt is canonical.
            body = WireJson.Parse(bytes, _maximumJsonBytes);
        }
        var evidence = new LostResponseRequest(request.Method.Method, uri.AbsolutePath, uri.Query, body);
        _requests.Enqueue(evidence);

        if (!_matches(request) || Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var beforeSend = BeforeSendAsync;
            if (beforeSend is not null)
            {
                await beforeSend(evidence, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Interlocked.Exchange(ref _state, 0);
                return response;
            }

            try
            {
                var bytes = await ReadResponseAsync(response.Content, cancellationToken).ConfigureAwait(false);
                var receipt = WireJson.DecodeControl(bytes, _maximumJsonBytes);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_evidenceGate)
                {
                    _lostRequest = body?.Clone();
                    _lostReceipt = receipt.Clone();
                }
                Interlocked.Increment(ref _lostResponseCount);
                Interlocked.Exchange(ref _state, 2);
            }
            finally
            {
                response.Dispose();
            }
            throw new HttpRequestException("受控测试已丢弃原 HTTP 200 响应。");
        }
        catch
        {
            if (Volatile.Read(ref _state) != 2)
            {
                Interlocked.Exchange(ref _state, 0);
            }
            throw;
        }
    }

    private async Task<byte[]> ReadResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _maximumJsonBytes)
        {
            throw new InvalidOperationException("测试响应超过 JSON 证据限额。");
        }
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToArray();
            }
            if (output.Length + count > _maximumJsonBytes)
            {
                throw new InvalidOperationException("测试响应超过 JSON 证据限额。");
            }
            output.Write(buffer, 0, count);
        }
    }

    private static bool IsJson(string? mediaType) => mediaType is not null &&
        (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static Func<HttpRequestMessage, bool> MatchSuffix(string pathSuffix)
    {
        if (string.IsNullOrEmpty(pathSuffix) || !pathSuffix.StartsWith("/", StringComparison.Ordinal) ||
            pathSuffix.Contains('?') || pathSuffix.Contains('#'))
        {
            throw new ArgumentException("目标必须是路径后缀。", nameof(pathSuffix));
        }
        return request => request.Method == HttpMethod.Post &&
            request.RequestUri?.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal) == true;
    }
}

internal sealed class LostResponseRequest(string method, string path, string query, JsonElement? body)
{
    public string Method { get; } = method;
    public string Path { get; } = path;
    public string Query { get; } = query;
    public JsonElement? Body { get; } = body?.Clone();
}
