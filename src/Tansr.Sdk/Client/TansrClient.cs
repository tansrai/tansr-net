using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

/// <summary>原生 Serve 客户端；副作用请求不自动重试。Dispose 只释放客户端资源，不关闭远端会话。</summary>
/// <remarks>注入 HttpClient 时宿主必须禁用自动重定向、自动副作用重试和共享 Cookie；其生命周期归宿主。</remarks>
public sealed partial class TansrClient : IDisposable
{
    private readonly SessionTransport transport;
    private readonly SessionContract contract;
    private readonly TimeSpan requestTimeout;
    private readonly TimeSpan streamIdleTimeout;
    private readonly TimeSpan reconnectDelay;
    private readonly int maxResponseBytes;
    private readonly int maxEventBytes;
    private readonly int maxReconnectAttempts;
    private readonly Func<JsonElement>? scopeProvider;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim discoveryGate = new SemaphoreSlim(1, 1);
    private string? discoveredToken;
    private int disposed;

    public TansrClient(TansrClientOptions options, HttpClient? injected = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (!Enum.IsDefined(typeof(SessionContract), options.SessionContract) || options.RequestTimeout <= TimeSpan.Zero ||
            options.RequestTimeout > TimeSpan.FromHours(24) || options.StreamIdleTimeout <= TimeSpan.Zero || options.StreamIdleTimeout > TimeSpan.FromHours(24) ||
            options.ReconnectDelay < TimeSpan.Zero || options.ReconnectDelay > TimeSpan.FromMinutes(1) ||
            options.MaxResponseBytes < 1 || options.MaxResponseBytes > 32 * 1024 * 1024 || options.MaxEventBytes < 1 || options.MaxEventBytes > 2 * 1024 * 1024 ||
            options.MaxReconnectAttempts < 0 || options.MaxReconnectAttempts > 100)
            throw new ArgumentException("Invalid transport limits.", nameof(options));
        contract = options.SessionContract;
        requestTimeout = options.RequestTimeout; streamIdleTimeout = options.StreamIdleTimeout;
        maxResponseBytes = options.MaxResponseBytes; maxEventBytes = options.MaxEventBytes;
        maxReconnectAttempts = options.MaxReconnectAttempts; reconnectDelay = options.ReconnectDelay;
        scopeProvider = options.ExecutionScopeProvider;
        transport = new SessionTransport(options, injected);
    }

    internal CancellationTokenSource RequestCancellation(CancellationToken cancellationToken, bool timed = true)
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TansrClient));
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        if (timed) source.CancelAfter(requestTimeout);
        return source;
    }

    private string Route(string path) => contract == SessionContract.Sdk2OffloadV1 && path.StartsWith("/v2/", StringComparison.Ordinal)
        ? "/v3/sdk2/" + path.Substring(4) : path;

    internal string SessionPath(string id) => "/v2/sessions/" + SessionJson.Segment(id);

    public async Task<JsonElement> GetSessionCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        return await DiscoverAsync(access, cancellation.Token).ConfigureAwait(false);
    }

    private async Task<JsonElement> DiscoverAsync(SessionAccess access, CancellationToken cancellationToken)
    {
        using var response = await transport.SendAsync(HttpMethod.Get, "/v3/sdk2/session-capabilities?protocol=sdk2-ext-v1", access,
            null, "application/json", null, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, maxResponseBytes, cancellationToken).ConfigureAwait(false);
        SessionTransport.ExpectContent(response, "application/json");
        var value = WireJson.DecodeControl(await SessionTransport.ReadBodyAsync(response, WireJson.MaximumControlBytes, cancellationToken).ConfigureAwait(false));
        if (value.ValueKind != JsonValueKind.Object || SessionJson.String(value, "protocol") != "sdk2-ext-v1" ||
            !value.TryGetProperty("contracts", out var contracts) || contracts.ValueKind != JsonValueKind.Array || contracts.GetArrayLength() > 2)
            throw new TansrProtocolException("invalid_response");
        int fieldCount = 0; foreach (var unused in value.EnumerateObject()) fieldCount++;
        if (fieldCount != 2) throw new TansrProtocolException("invalid_response");
        bool sdk1 = false, sdk2 = false;
        foreach (var item in contracts.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new TansrProtocolException("invalid_response");
            var name = SessionJson.String(item, "contract");
            var availability = SessionJson.String(item, "availability");
            fieldCount = 0; foreach (var unused in item.EnumerateObject()) fieldCount++;
            if (fieldCount != 2) throw new TansrProtocolException("invalid_response");
            if (name == "sdk1" && availability == "legacy-complete" && !sdk1) sdk1 = true;
            else if (name == "sdk2-offload-v1" && availability == "source-required" && !sdk2) sdk2 = true;
            else throw new TansrProtocolException("invalid_response");
        }
        transport.AssertCurrent(access);
        if (contract == SessionContract.Sdk2OffloadV1 && !sdk2) throw new TansrProtocolException("unsupported_capability");
        return value;
    }

    private async Task EnsureContractAsync(SessionAccess access, CancellationToken cancellationToken)
    {
        if (contract == SessionContract.Sdk1) return;
        await discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(discoveredToken, access.Token, StringComparison.Ordinal)) return;
            discoveredToken = null;
            await DiscoverAsync(access, cancellationToken).ConfigureAwait(false);
            transport.AssertCurrent(access);
            discoveredToken = access.Token;
        }
        finally { discoveryGate.Release(); }
    }

    internal void VerifyFamily(JsonElement value)
    {
        if (contract == SessionContract.Sdk2OffloadV1 &&
            (SessionJson.String(value, "contract") != "sdk2-offload-v1" || SessionJson.String(value, "availability") != "source-required"))
            throw new TansrProtocolException("invalid_response");
    }

    internal async Task<JsonElement> SendSessionAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken, int requestLimit = 2 * 1024 * 1024)
    {
        if (body is not null && body.Length > requestLimit) throw new TansrProtocolException("payload_too_large");
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        await EnsureContractAsync(access, cancellation.Token).ConfigureAwait(false);
        using var response = await transport.SendAsync(method, Route(path), access, body, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, maxResponseBytes, cancellation.Token).ConfigureAwait(false);
        SessionTransport.ExpectContent(response, "application/json");
        var value = SessionJson.Parse(await SessionTransport.ReadBodyAsync(response, maxResponseBytes, cancellation.Token).ConfigureAwait(false));
        transport.AssertCurrent(access);
        return value;
    }

    internal JsonElement ReadExecutionScope()
    {
        if (scopeProvider is null) throw new TansrProtocolException("scope_unavailable");
        JsonElement scope;
        try { scope = scopeProvider().Clone(); }
        catch { throw new TansrProtocolException("scope_unavailable"); }
        WireJson.ValidateNamed("Scope", scope);
        return scope;
    }

    /// <summary>严格 SDK2 控制传输；具名请求/响应及执行语义由对应协调器校验。</summary>
    internal async Task<JsonElement> SendControlAsync(HttpMethod method, string relativePath, JsonElement? body, CancellationToken cancellationToken)
    {
        byte[]? request = body.HasValue ? WireJson.EncodeControl(body.Value) : null;
        var expectedScope = WireJson.CanonicalString(ReadExecutionScope());
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        if (expectedScope != WireJson.CanonicalString(ReadExecutionScope())) throw new TansrProtocolException("context_changed");
        await EnsureContractAsync(access, cancellation.Token).ConfigureAwait(false);
        if (expectedScope != WireJson.CanonicalString(ReadExecutionScope())) throw new TansrProtocolException("context_changed");
        using var response = await transport.SendAsync(method, Route(relativePath), access, request, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, WireJson.MaximumControlBytes, cancellation.Token, true).ConfigureAwait(false);
        SessionTransport.ExpectContent(response, "application/json");
        var value = WireJson.DecodeControl(await SessionTransport.ReadBodyAsync(response, WireJson.MaximumControlBytes, cancellation.Token).ConfigureAwait(false));
        transport.AssertCurrent(access);
        if (expectedScope != WireJson.CanonicalString(ReadExecutionScope())) throw new TansrProtocolException("context_changed");
        return value;
    }

    internal async Task<byte[]> SendBinaryAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken)
    {
        const int maximum = 32 * 1024 * 1024;
        if (body is not null && body.Length > maximum) throw new TansrProtocolException("payload_too_large");
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        await EnsureContractAsync(access, cancellation.Token).ConfigureAwait(false);
        using var response = await transport.SendAsync(method, Route(path), access, body,
            body is null ? "application/octet-stream" : "application/json", "application/octet-stream", null, cancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, maxResponseBytes, cancellation.Token).ConfigureAwait(false);
        SessionTransport.ExpectContent(response, body is null ? "application/octet-stream" : "application/json");
        var value = await SessionTransport.ReadBodyAsync(response, body is null ? maximum : maxResponseBytes, cancellation.Token).ConfigureAwait(false);
        transport.AssertCurrent(access);
        return value;
    }

    public async Task<AgentSession> CreateSessionAsync(CreateSessionOptions options, CancellationToken cancellationToken = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var resume = options.ResumeSessionId;
        var body = SessionRequestWriter.Create(options, contract);
        var value = await SendSessionAsync(HttpMethod.Post, "/v2/sessions", body, cancellationToken).ConfigureAwait(false);
        VerifyFamily(value);
        var id = SessionJson.String(value, "sessionId");
        SessionJson.Text(id, 512, "sessionId");
        var last = SessionJson.LastSequence(value);
        if (!value.TryGetProperty("resumed", out var resumed) || resumed.ValueKind != JsonValueKind.True && resumed.ValueKind != JsonValueKind.False ||
            resume is not null && !string.Equals(resume, id, StringComparison.Ordinal)) throw new TansrProtocolException("invalid_response");
        return new AgentSession(this, id, last, resumed.GetBoolean());
    }

    public async Task<AgentSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var value = await SendSessionAsync(HttpMethod.Get, SessionPath(sessionId), null, cancellationToken).ConfigureAwait(false);
        VerifyFamily(value);
        if (SessionJson.String(value, "sessionId") != sessionId) throw new TansrProtocolException("invalid_response");
        return new AgentSession(this, sessionId, SessionJson.LastSequence(value), true);
    }

    public Task<JsonElement> ListSessionsAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > 200 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        return SendSessionAsync(HttpMethod.Get, "/v2/sessions?limit=" + limit.ToString(CultureInfo.InvariantCulture) + "&offset=" + offset.ToString(CultureInfo.InvariantCulture), null, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); transport.Dispose();
        // 等待中的请求可能仍访问 CTS/semaphore；不在同步 Dispose 中销毁它们。
    }
}
