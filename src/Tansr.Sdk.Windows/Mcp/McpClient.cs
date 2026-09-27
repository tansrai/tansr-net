using System.Globalization;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Mcp;

/// <summary>
/// 原生 MCP 协议消费。连接断开或结果未知时不自动重连/重做工具；资源释放须等待 CloseAsync。
/// 服务端元数据保持不可信资源身份，不自动变成系统提示词或本地权限。
/// </summary>
public sealed class McpClient : IMcpConnection, IDisposable
{
    private static readonly string[] Versions = { "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25" };
    private readonly IMcpTransport _transport;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<string, CancellationToken, Task>? _authorize;
    private readonly TimeSpan _requestTimeout;
    private long _id;
    private volatile McpConnectionState _state;

    private McpClient(IMcpTransport transport, McpClientOptions options)
    { _transport = transport; _slots = new SemaphoreSlim(options.MaximumPendingRequests); _authorize = options.Authorize; _requestTimeout = options.RequestTimeout; }

    public McpConnectionState State => _transport.IsClosed ? McpConnectionState.Closed : _state;
    public JsonElement InitializeResult { get; private set; }
    public string ProtocolVersion => _transport.ProtocolVersion ?? throw new McpException("not_ready");

    public static async Task<McpClient> ConnectStdioAsync(WindowsDuplexProcessOptions process,
        McpClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options = ValidateOptions(options);
        if (process.StandardOutputMode != WindowsDuplexProcessOutputMode.LineFrames) throw new ArgumentException("MCP stdio requires line frames.", nameof(process));
        if (options.Authorize != null) await options.Authorize("connect:stdio", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var channel = await WindowsDuplexProcess.StartAsync(process, cancellationToken).ConfigureAwait(false);
        return await InitializeAsync(new StdioMcpTransport(channel, options.MaximumResponseBytes), options, cancellationToken).ConfigureAwait(false);
    }

    public static Task<McpClient> ConnectHttpAsync(McpHttpOptions http,
        McpClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options = ValidateOptions(options);
        return InitializeAsync(new HttpMcpTransport(http, options.MaximumResponseBytes, options.MaximumPendingRequests), options, cancellationToken);
    }

    private static async Task<McpClient> InitializeAsync(IMcpTransport transport, McpClientOptions options, CancellationToken cancellationToken)
    {
        var client = new McpClient(transport, options);
        try
        {
            var parameters = McpJson.Object(writer =>
            {
                writer.WriteString("protocolVersion", options.ProtocolVersion); writer.WritePropertyName("capabilities"); writer.WriteStartObject(); writer.WriteEndObject();
                writer.WritePropertyName("clientInfo"); writer.WriteStartObject(); writer.WriteString("name", options.ClientName); writer.WriteString("version", options.ClientVersion); writer.WriteEndObject();
            });
            var result = await client.RequestCoreAsync("initialize", parameters, options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.String ||
                !Versions.Contains(version.GetString(), StringComparer.Ordinal) || !result.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("serverInfo", out var server) || server.ValueKind != JsonValueKind.Object ||
                !server.TryGetProperty("name", out var serverName) || serverName.ValueKind != JsonValueKind.String ||
                !server.TryGetProperty("version", out var serverVersion) || serverVersion.ValueKind != JsonValueKind.String)
                throw new McpException("invalid_initialize");
            transport.ProtocolVersion = version.GetString(); client.InitializeResult = result;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.RequestTimeout);
            await transport.SendAsync(McpJson.Message(null, "notifications/initialized", null), deadline.Token).ConfigureAwait(false);
            client._state = McpConnectionState.Ready;
            return client;
        }
        catch { await client.CloseAsync().ConfigureAwait(false); throw; }
    }

    public Task<JsonElement> RequestAsync(string method, JsonElement? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (State != McpConnectionState.Ready) throw new McpException("not_ready");
        if (string.IsNullOrWhiteSpace(method) || method.Length > 256 || method == "initialize" || method.StartsWith("notifications/", StringComparison.Ordinal))
            throw new ArgumentException("Expected a post-initialization request method.", nameof(method));
        return RequestCoreAsync(method, parameters, timeout ?? _requestTimeout, cancellationToken);
    }

    private async Task<JsonElement> RequestCoreAsync(string method, JsonElement? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (parameters.HasValue && parameters.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("MCP parameters must be an object.", nameof(parameters));
        if (parameters.HasValue && Encoding.UTF8.GetByteCount(parameters.Value.GetRawText()) > 1024 * 1024) throw new McpException("request_limit");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(timeout);
        if (!await _slots.WaitAsync(0, deadline.Token).ConfigureAwait(false)) throw new McpException("pending_limit");
        string id = Interlocked.Increment(ref _id).ToString(CultureInfo.InvariantCulture);
        bool sent = false;
        try
        {
            if (_authorize != null) await _authorize(method, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            sent = true;
            return await _transport.RequestAsync(McpJson.Message(id, method, parameters), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (sent && !_transport.IsClosed)
            {
                using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _transport.SendAsync(McpJson.Message(null, "notifications/cancelled", McpJson.Object(writer => writer.WriteString("requestId", id))), cancelDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* 取消通知是尽力发送；断连和未知结果仍由原请求明确暴露。 */ }
            }
            throw;
        }
        finally { _slots.Release(); }
    }

    /// <summary>发现先过宿主白名单；发现结果不直接注册为可执行工具。</summary>
    public async Task<IReadOnlyList<JsonElement>> ListToolsAsync(IEnumerable<string> allowedRemoteNames, CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>(allowedRemoteNames ?? throw new ArgumentNullException(nameof(allowedRemoteNames)), StringComparer.Ordinal);
        if (allowed.Count > 64 || allowed.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128)) throw new ArgumentException("Invalid tool allowlist.");
        var tools = new Dictionary<string, JsonElement>(StringComparer.Ordinal); var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        for (int page = 0; page < 32; page++)
        {
            var result = await RequestAsync("tools/list", cursor == null ? null : McpJson.Object(writer => writer.WriteString("cursor", cursor)), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("tools", out var items) || items.ValueKind != JsonValueKind.Array) throw new McpException("invalid_tools");
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) throw new McpException("invalid_tools");
                if (!allowed.Contains(name.GetString()!)) continue;
                if (!item.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object || tools.ContainsKey(name.GetString()!)) throw new McpException("invalid_tools");
                tools.Add(name.GetString()!, item.Clone());
            }
            if (!result.TryGetProperty("nextCursor", out var next)) return tools.Values.ToArray();
            if (next.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cursor = next.GetString()) || cursor!.Length > 1024 || !cursors.Add(cursor)) throw new McpException("invalid_cursor");
        }
        throw new McpException("discovery_limit");
    }

    public async Task CloseAsync()
    { _state = McpConnectionState.Closed; _lifetime.Cancel(); await _transport.CloseAsync().ConfigureAwait(false); }
    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    private static McpClientOptions ValidateOptions(McpClientOptions? options)
    {
        options ??= new McpClientOptions();
        if (string.IsNullOrWhiteSpace(options.ClientName) || options.ClientName.Length > 128 || string.IsNullOrWhiteSpace(options.ClientVersion) || options.ClientVersion.Length > 128 ||
            !Versions.Contains(options.ProtocolVersion, StringComparer.Ordinal) || options.MaximumResponseBytes < 1024 || options.MaximumResponseBytes > 16 * 1024 * 1024 ||
            options.MaximumPendingRequests < 1 || options.MaximumPendingRequests > 64 || options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentException("Invalid MCP client options.");
        return new McpClientOptions
        {
            ClientName = options.ClientName,
            ClientVersion = options.ClientVersion,
            ProtocolVersion = options.ProtocolVersion,
            RequestTimeout = options.RequestTimeout,
            MaximumResponseBytes = options.MaximumResponseBytes,
            MaximumPendingRequests = options.MaximumPendingRequests,
            Authorize = options.Authorize
        };
    }
}
