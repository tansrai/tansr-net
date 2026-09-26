using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Hosting;

/// <summary>
/// 仅供本机自有 Serve 的 HTTP/1.1 通道。先建立 TCP 并核实服务端四元组的原进程身份，才发送认证及正文。
/// 每请求独立连接；无代理、Cookie、重定向、连接复用或自动重发。响应体流式读取，支持 Content-Length、chunked、EOF/SSE。
/// 不支持 TLS、压缩、升级、代理或任意目标；这些由一般远端 HttpClient 负责。
/// </summary>
public sealed class OwnedServeHttpHandler : HttpMessageHandler
{
    private const int MaximumHeaderBytes = 65536;
    private const int MaximumRequestBytes = 32 * 1024 * 1024;
    private readonly Uri _origin;
    private readonly NativeProcessHandle _owner;
    private readonly int _ownerPid;
    private readonly SemaphoreSlim _slots = new(16, 16);
    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private long _sequence;
    private int _disposed;

    public OwnedServeHttpHandler(Uri baseUri, WindowsDuplexProcess process)
    {
        if (baseUri == null || !baseUri.IsAbsoluteUri || baseUri.Scheme != "http" || baseUri.Host != "127.0.0.1" ||
            baseUri.UserInfo.Length != 0 || baseUri.Fragment.Length != 0 || baseUri.Query.Length != 0 || baseUri.AbsolutePath != "/")
            throw new ArgumentException("Expected a fixed IPv4 loopback Serve origin.", nameof(baseUri));
        if (process == null) throw new ArgumentNullException(nameof(process));
        _origin = baseUri; _ownerPid = process.ProcessId; _owner = process.DuplicateProcessHandle();
        try { RequireOwner(); } catch { _owner.Dispose(); throw; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequireOwner();
        var uri = request.RequestUri;
        if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != "http" || uri.Host != _origin.Host || uri.Port != _origin.Port ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.PathAndQuery.StartsWith("/", StringComparison.Ordinal))
            throw Failure("serve_target_mismatch");
        if (!new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" }.Contains(request.Method.Method, StringComparer.Ordinal))
            throw Failure("serve_http_method_unsupported");
        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw Failure("serve_http_capacity");
        long id = Interlocked.Increment(ref _sequence);
        Connection connection;
        try { connection = new Connection(() => { _connections.TryRemove(id, out _); _slots.Release(); }); }
        catch { _slots.Release(); throw; }
        try
        {
            _connections.TryAdd(id, connection);
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(OwnedServeHttpHandler));
            connection.RegisterCancellation(cancellationToken);
            // 先占容量，再通过有界写入接收正文；不让 HttpContent 隐式建立无限缓冲。
            byte[] body = await ReadRequestBodyAsync(request.Content, cancellationToken).ConfigureAwait(false);
            byte[] headers = RequestHeaders(request, uri, body.Length);
            cancellationToken.ThrowIfCancellationRequested(); RequireOwner();
            await connection.Socket.ConnectAsync(IPAddress.Loopback, _origin.Port).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); RequireOwner();
            // 此后不换 socket，不调用会偷偷重连的 HTTP 层；端口被抢占者在这里之前收不到认证字节。
            VerifyEstablishedPeer(connection.Socket);
            RequireOwner(); cancellationToken.ThrowIfCancellationRequested();
            connection.InitializeStream();
            await connection.Stream.WriteAsync(headers, 0, headers.Length, cancellationToken).ConfigureAwait(false);
            if (body.Length != 0) await connection.Stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
            await connection.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            var response = await ReadResponseAsync(connection, request, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { connection.Dispose(); throw new OperationCanceledException(cancellationToken); }
        catch { connection.Dispose(); throw; }
    }

    private void RequireOwner()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(OwnedServeHttpHandler));
        if (NativeProcessMethods.WaitForSingleObject(_owner, 0) != NativeProcessMethods.WaitTimeout) throw Failure("serve_owner_exited");
    }

    private void VerifyEstablishedPeer(TcpClient client)
    {
        var local = (IPEndPoint)client.Client.LocalEndPoint!; var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        if (!local.Address.Equals(IPAddress.Loopback) || !remote.Address.Equals(IPAddress.Loopback)) throw Failure("serve_peer_mismatch");
        uint length = 0; uint status = GetExtendedTcpTable(IntPtr.Zero, ref length, false, 2, 4, 0);
        if (status != 122 || length < 4 || length > 16 * 1024 * 1024) throw Failure("serve_peer_unverifiable");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint capacity = length; IntPtr table = Marshal.AllocHGlobal(checked((int)capacity));
            try
            {
                status = GetExtendedTcpTable(table, ref length, false, 2, 4, 0);
                if (status == 122 && length <= 16 * 1024 * 1024) continue;
                if (status != 0 || length > capacity || length < 4) throw Failure("serve_peer_unverifiable");
                uint count = unchecked((uint)Marshal.ReadInt32(table)); int offset = Marshal.OffsetOf<TcpTable>(nameof(TcpTable.First)).ToInt32(); int stride = Marshal.SizeOf<TcpRow>();
                if (length < offset || count > (length - offset) / stride) throw Failure("serve_peer_unverifiable");
                int matches = 0;
                for (uint i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(table, checked(offset + (int)i * stride)));
                    if (row.State != 5 || row.LocalAddress != Address(remote.Address) || row.RemoteAddress != Address(local.Address) ||
                        Port(row.LocalPort) != remote.Port || Port(row.RemotePort) != local.Port) continue;
                    if (row.Pid != _ownerPid) throw Failure("serve_peer_not_owned");
                    matches++;
                }
                if (matches != 1) throw Failure("serve_peer_unverifiable");
                return;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw Failure("serve_peer_unverifiable");
    }

    private static uint Address(IPAddress value) => BitConverter.ToUInt32(value.GetAddressBytes(), 0);
    private static int Port(uint value) => unchecked((ushort)IPAddress.NetworkToHostOrder((short)(value & 0xffff)));
    [StructLayout(LayoutKind.Sequential)] private struct TcpRow { internal uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
    [StructLayout(LayoutKind.Sequential)] private struct TcpTable { internal uint Count; internal TcpRow First; }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint length, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);

    private static async Task<byte[]> ReadRequestBodyAsync(HttpContent? content, CancellationToken token)
    {
        if (content == null) return Array.Empty<byte>();
        if (content.Headers.ContentLength > MaximumRequestBytes) throw Failure("serve_request_too_large");
        using var sink = new RequestBodyBuffer(token);
        await content.CopyToAsync(sink).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return sink.ToArray();
    }

    private static byte[] RequestHeaders(HttpRequestMessage request, Uri uri, int length)
    {
        var text = new StringBuilder(request.Method.Method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\nHost: ")
            .Append(uri.Host).Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture)).Append("\r\nConnection: close\r\n");
        if (request.Content != null) text.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = request.Headers;
        if (request.Content != null) headers = headers.Concat(request.Content.Headers);
        foreach (var header in headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (new[] { "Host", "Connection", "Transfer-Encoding", "Expect", "Proxy-Authorization", "Cookie", "Upgrade", "Accept-Encoding" }.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                throw Failure("serve_http_header_unsupported");
            ValidateHeaderName(header.Key);
            foreach (string value in header.Value)
            {
                if (value.Any(c => c < 32 || c > 126)) throw Failure("serve_http_header_invalid");
                text.Append(header.Key).Append(": ").Append(value).Append("\r\n");
                if (text.Length > MaximumHeaderBytes) throw Failure("serve_http_headers_too_large");
            }
        }
        text.Append("\r\n");
        if (text.Length > MaximumHeaderBytes || text.ToString().Any(c => c > 127)) throw Failure("serve_http_headers_too_large");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static async Task<HttpResponseMessage> ReadResponseAsync(Connection connection, HttpRequestMessage request, CancellationToken token)
    {
        var reader = new Reader(connection); int headerBytes = 0;
        for (int interim = 0; interim < 9; interim++)
        {
            string line = await reader.LineAsync(4096, token).ConfigureAwait(false); headerBytes += line.Length + 2;
            if (!line.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) && !line.StartsWith("HTTP/1.0 ", StringComparison.Ordinal)) throw Failure("serve_http_version_unsupported");
            string[] status = line.Split(new[] { ' ' }, 3);
            Version version = line.StartsWith("HTTP/1.0", StringComparison.Ordinal) ? new Version(1, 0) : new Version(1, 1);
            if (status.Length < 2 || status[1].Length != 3 || !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out int code) || code < 100 || code > 599)
                throw Failure("serve_http_status_invalid");
            var headers = new List<KeyValuePair<string, string>>();
            while ((line = await reader.LineAsync(8192, token).ConfigureAwait(false)).Length != 0)
            {
                headerBytes += line.Length + 2;
                if (headerBytes > MaximumHeaderBytes || headers.Count >= 128) throw Failure("serve_http_headers_too_large");
                headers.Add(ParseHeader(line));
            }
            if (code < 200)
            {
                if (code == 101 || interim == 8) throw Failure("serve_http_upgrade_unsupported");
                continue;
            }
            var contentLengths = headers.Where(x => x.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToArray();
            var transfers = headers.Where(x => x.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToArray();
            var encodings = headers.Where(x => x.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToArray();
            if (contentLengths.Length > 1 || transfers.Length > 1 || (contentLengths.Length != 0 && transfers.Length != 0) ||
                (transfers.Length != 0 && !transfers[0].Equals("chunked", StringComparison.OrdinalIgnoreCase)) || encodings.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw Failure("serve_http_framing_unsupported");
            long? remaining = null;
            if (contentLengths.Length == 1)
            {
                if (!long.TryParse(contentLengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size < 0) throw Failure("serve_http_length_invalid");
                remaining = size;
            }
            bool noBody = request.Method == HttpMethod.Head || code == 204 || code == 304;
            var response = new HttpResponseMessage((HttpStatusCode)code)
            {
                RequestMessage = request,
                Version = version,
                Content = new StreamContent(new ResponseBody(reader, connection, noBody ? 0 : remaining, !noBody && transfers.Length != 0))
            };
            foreach (var header in headers)
            {
                if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value)) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return response;
        }
        throw Failure("serve_http_status_invalid");
    }

    private static KeyValuePair<string, string> ParseHeader(string line)
    {
        int at = line.IndexOf(':'); if (at <= 0) throw Failure("serve_http_header_invalid");
        string name = line.Substring(0, at); ValidateHeaderName(name);
        string value = line.Substring(at + 1).Trim(' ', '\t');
        if (value.Any(c => c < 32 && c != '\t' || c > 126)) throw Failure("serve_http_header_invalid");
        return new KeyValuePair<string, string>(name, value);
    }
    private static void ValidateHeaderName(string name)
    {
        if (name.Length == 0 || name.Any(c => !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && "!#$%&'*+-.^_`|~".IndexOf(c) < 0))
            throw Failure("serve_http_header_invalid");
    }
    private static TansrProtocolException Failure(string code) => new(code);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        { foreach (var connection in _connections.Values) connection.Dispose(); _owner.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class Connection : IDisposable
    {
        internal readonly TcpClient Socket = new(AddressFamily.InterNetwork);
        internal NetworkStream Stream = null!;
        private CancellationTokenRegistration _cancellation;
        private readonly Action _released; private readonly object _gate = new(); private bool _disposed;
        internal Connection(Action released) { _released = released; }
        internal void RegisterCancellation(CancellationToken token)
        {
            var registration = token.Register(CloseSocket);
            lock (_gate)
            {
                if (!_disposed) { _cancellation = registration; return; }
            }
            // Dispose 可能与注册交错；不能把已释放连接的回调留在长寿命 token 上。
            registration.Dispose();
        }
        internal void InitializeStream() => Stream = Socket.GetStream();
        internal void CloseSocket() { try { Socket.Close(); } catch (ObjectDisposedException) { } }
        public void Dispose()
        {
            CancellationTokenRegistration cancellation;
            lock (_gate) { if (_disposed) return; _disposed = true; cancellation = _cancellation; }
            CloseSocket(); cancellation.Dispose(); _released();
        }
    }

    private sealed class RequestBodyBuffer : Stream
    {
        private readonly MemoryStream _memory = new(); private readonly CancellationToken _token;
        internal RequestBodyBuffer(CancellationToken token) { _token = token; }
        internal byte[] ToArray() => _memory.ToArray();
        public override void Write(byte[] buffer, int offset, int count)
        {
            _token.ThrowIfCancellationRequested();
            if (count > MaximumRequestBytes - _memory.Length) throw Failure("serve_request_too_large");
            _memory.Write(buffer, offset, count);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override void Flush() => _token.ThrowIfCancellationRequested();
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => _memory.Length;
        public override long Position { get => _memory.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _memory.Dispose(); base.Dispose(disposing); }
    }

    private sealed class Reader
    {
        private readonly Connection _connection; private readonly byte[] _buffer = new byte[8192]; private int _offset, _length;
        internal Reader(Connection connection) { _connection = connection; }
        internal async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (count == 0) return 0;
            if (_offset != _length)
            { int size = Math.Min(count, _length - _offset); Buffer.BlockCopy(_buffer, _offset, buffer, offset, size); _offset += size; return size; }
            return await _connection.Stream.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
        }
        private async Task<int> ByteAsync(CancellationToken token)
        {
            if (_offset == _length)
            { _length = await _connection.Stream.ReadAsync(_buffer, 0, _buffer.Length, token).ConfigureAwait(false); _offset = 0; if (_length == 0) return -1; }
            return _buffer[_offset++];
        }
        internal async Task<string> LineAsync(int maximum, CancellationToken token)
        {
            var line = new StringBuilder();
            while (true)
            {
                int value = await ByteAsync(token).ConfigureAwait(false);
                if (value < 0) throw Failure("serve_http_truncated");
                if (value == 13)
                { if (await ByteAsync(token).ConfigureAwait(false) != 10) throw Failure("serve_http_framing_invalid"); return line.ToString(); }
                if (value == 10 || value > 126 || value < 32 && value != 9 || line.Length >= maximum) throw Failure("serve_http_framing_invalid");
                line.Append((char)value);
            }
        }
    }

    private sealed class ResponseBody : Stream
    {
        private readonly Reader _reader; private readonly Connection _connection; private readonly bool _chunked;
        private long? _remaining; private long _chunkRemaining; private bool _chunkTerminator, _done; private int _reading;
        internal ResponseBody(Reader reader, Connection connection, long? remaining, bool chunked)
        { _reader = reader; _connection = connection; _remaining = remaining; _chunked = chunked; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;
            if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Concurrent response reads are not supported.");
            using var canceled = cancellationToken.Register(_connection.CloseSocket);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_done || _remaining == 0) { _done = true; _connection.Dispose(); return 0; }
                if (_chunked && _chunkRemaining == 0)
                {
                    if (_chunkTerminator && (await _reader.LineAsync(0, cancellationToken).ConfigureAwait(false)).Length != 0) throw Failure("serve_http_chunk_invalid");
                    string line = await _reader.LineAsync(1024, cancellationToken).ConfigureAwait(false);
                    if (line.Length == 0 || !long.TryParse(line, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _chunkRemaining) || _chunkRemaining < 0)
                        throw Failure("serve_http_chunk_unsupported");
                    if (_chunkRemaining == 0)
                    {
                        int bytes = 0, headers = 0;
                        while ((line = await _reader.LineAsync(8192, cancellationToken).ConfigureAwait(false)).Length != 0)
                        {
                            bytes += line.Length + 2; var header = ParseHeader(line);
                            if (bytes > 16384 || ++headers > 32 || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) throw Failure("serve_http_trailer_invalid");
                        }
                        _done = true; _connection.Dispose(); return 0;
                    }
                    _chunkTerminator = true;
                }
                int maximum = (int)Math.Min(count, _chunked ? _chunkRemaining : _remaining ?? count);
                int read = await _reader.ReadAsync(buffer, offset, maximum, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (_chunked || _remaining.HasValue) throw Failure("serve_http_truncated");
                    _done = true; _connection.Dispose(); return 0;
                }
                if (_chunked) _chunkRemaining -= read; else if (_remaining.HasValue) _remaining -= read;
                return read;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            { _connection.Dispose(); throw new OperationCanceledException(cancellationToken); }
            catch { _connection.Dispose(); throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }
        protected override void Dispose(bool disposing) { if (disposing) { _done = true; _connection.Dispose(); } base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
