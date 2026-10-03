using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Transport;

internal enum InputErrorMode { None, Submit, Status }

internal sealed class SessionAccess
{
    internal SessionAccess(string token, string? principal) { Token = token; Principal = principal; }
    internal string Token { get; }
    internal string? Principal { get; }
}

internal sealed class SessionTransport : IDisposable
{
    private readonly Uri origin;
    private readonly HttpClient client;
    private readonly HttpClient streamingClient;
    private readonly bool ownsClient;
    private readonly Func<CancellationToken, Task<string>> tokens;
    private readonly Func<string>? principalProvider;
    private readonly IReadOnlyDictionary<string, string> additionalHeaders;
    private readonly string sessionFamily;
    private readonly bool negotiateEventEnvelope;
    private static readonly ConditionalWeakTable<HttpResponseMessage, UnifiedResponseMeta> metadata = new ConditionalWeakTable<HttpResponseMessage, UnifiedResponseMeta>();
    private readonly object principalGate = new object();
    private string? principal;
    private bool principalBound;
    internal Uri Origin => origin;
    internal bool UsesDefaultHttpClient { get; }

    internal SessionTransport(TansrClientOptions options, HttpClient? injected, bool ownsInjected = false)
    {
        if (options.BaseUri is null || !options.BaseUri.IsAbsoluteUri || options.BaseUri.AbsolutePath != "/" ||
            options.BaseUri.Query.Length != 0 || options.BaseUri.Fragment.Length != 0 || options.BaseUri.UserInfo.Length != 0)
            throw new ArgumentException("BaseUri must be an absolute origin without credentials, path, query or fragment.", nameof(options));
        var uri = options.BaseUri;
        bool loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.DnsSafeHost, out var ip) && IPAddress.IsLoopback(ip);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && options.AllowInsecureLoopback && loopback))
            throw new ArgumentException("HTTPS is required; insecure HTTP requires explicit loopback development mode.", nameof(options));
        origin = new Uri(uri.AbsoluteUri);
        tokens = options.TokenProvider ?? throw new ArgumentException("TokenProvider is required.", nameof(options));
        principalProvider = options.PrincipalProvider;
        additionalHeaders = RequestHeaderSnapshot.Copy(options.AdditionalRequestHeaders);
        foreach (var header in additionalHeaders)
            if (header.Key.StartsWith("tansr-", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Unified tansr-* request headers are owned by the SDK; configure SessionContract / NegotiateEventEnvelope instead.", nameof(options));
        sessionFamily = UnifiedHeaders.FamilyValue(options.SessionContract);
        negotiateEventEnvelope = options.NegotiateEventEnvelope;
        if ((options.SessionContract == SessionContract.Sdk2OffloadV1 || options.ExecutionScopeProvider is not null) && principalProvider is null)
            throw new ArgumentException("SDK2 requires a trusted PrincipalProvider.", nameof(options));
        ownsClient = injected is null || ownsInjected;
        UsesDefaultHttpClient = injected is null;
        client = injected ?? CreateOwnedClient(false);
        if (client.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Use TokenProvider rather than default Authorization headers.", nameof(injected));
        // Persistent event subscriptions must not occupy the control request pool on CLR4.
        // Preserve a supplied client's handler, pooling policy and ownership unchanged.
        try { streamingClient = injected is null ? CreateOwnedClient(true) : client; }
        catch { if (injected is null) client.Dispose(); throw; }
    }

    private static HttpClient CreateOwnedClient(bool streaming)
    {
        var handler = new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None };
        // Bounded transport capacity: eight archive streams, eight output streams, plus
        // session/notification headroom. This is not a protocol capability/session limit.
        if (streaming) handler.MaxConnectionsPerServer = 32;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private string? ReadPrincipal()
    {
        if (principalProvider is null) return null;
        string value;
        try { value = principalProvider(); }
        catch { throw new TansrProtocolException("scope_unavailable"); }
        if (string.IsNullOrEmpty(value) || value.Length > 1024) throw new TansrProtocolException("scope_unavailable");
        return value;
    }

    internal async Task<SessionAccess> AccessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = ReadPrincipal();
        lock (principalGate)
        {
            if (principalBound && !string.Equals(principal, before, StringComparison.Ordinal)) throw new TansrProtocolException("context_changed");
            principal = before; principalBound = true;
        }
        string token;
        try { token = await tokens(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new TansrProtocolException("token_unavailable"); }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(token) || token.Length > 16384) throw new TansrProtocolException("token_unavailable");
        foreach (var c in token) if (c < 33 || c > 126) throw new TansrProtocolException("token_unavailable");
        var access = new SessionAccess(token, before);
        AssertCurrent(access);
        return access;
    }

    internal void AssertCurrent(SessionAccess access)
    {
        var current = ReadPrincipal();
        lock (principalGate)
            if (!principalBound || !string.Equals(principal, current, StringComparison.Ordinal) || !string.Equals(current, access.Principal, StringComparison.Ordinal))
                throw new TansrProtocolException("context_changed");
    }

    /// <summary>Unified header facts of a response produced by <see cref="SendAsync"/>.</summary>
    internal static UnifiedResponseMeta Meta(HttpResponseMessage response)
    {
        if (metadata.TryGetValue(response, out var meta)) return meta;
        meta = UnifiedResponseMeta.Read(response);
        metadata.Add(response, meta);
        return meta;
    }

    /// <summary>Sends one request under the unified <c>/api</c> contract (UAPI-01). Paths outside <c>/api</c> are
    /// rejected (D10: no legacy prefix). Every response must carry <c>tansr-contract: unified-v1</c> plus the other
    /// three mandatory headers; otherwise <see cref="ContractUnavailableException"/> is raised and nothing is read.
    /// <paramref name="closureId"/> sends <c>tansr-closure-id</c> for session-scoped guarded writes; <paramref name="conditions"/>
    /// sends the validated <c>Idempotency-Key</c> / <c>If-Match</c> / <c>deadline</c> (RFC §1.2). A deadline already past at send
    /// time is <c>deadline_exceeded</c> here and the request is not sent — a replay never extends it.</summary>
    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, SessionAccess access,
        byte[]? body, string accept, string? contentType, string? lastEventId, CancellationToken cancellationToken, string? closureId = null,
        ApiRequestConditions? conditions = null)
    {
        AssertCurrent(access);
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.IndexOf('\\') >= 0 || !ApiRoutes.IsApiPath(path))
            throw new TansrProtocolException("invalid_request");
        var target = new Uri(origin, path);
        if (!SameOrigin(target)) throw new TansrProtocolException("invalid_request");
        bool streaming = string.Equals(accept, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (conditions?.Deadline is DateTimeOffset deadline && deadline <= DateTimeOffset.UtcNow) throw new TansrProtocolException("deadline_exceeded");
        using var request = new HttpRequestMessage(method, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        foreach (var header in additionalHeaders) request.Headers.Add(header.Key, header.Value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        // Session family is declared on every request; the facade validates it only on session/execution domains
        // and ignores it elsewhere (手册 §16.4), so one value per client is the whole policy.
        request.Headers.Add(UnifiedHeaders.SessionFamily, sessionFamily);
        if (closureId is not null)
        {
            if (!UnifiedHeaders.Digest.IsMatch(closureId)) throw new TansrProtocolException("invalid_request");
            request.Headers.Add(UnifiedHeaders.ClosureId, closureId);
        }
        if (streaming && negotiateEventEnvelope) request.Headers.Add(UnifiedHeaders.EventEnvelope, UnifiedHeaders.EventEnvelopeContract);
        if (conditions is not null)
        {
            // Already validated against the operation (ApiRequestConditions.From); here they are emitted verbatim so a
            // replay with the same conditions is byte-identical (same key, same precondition, same deadline).
            if (conditions.IdempotencyKey is not null) request.Headers.TryAddWithoutValidation(UnifiedRequestHeaders.IdempotencyKey, conditions.IdempotencyKey);
            if (conditions.IfMatch is not null) request.Headers.TryAddWithoutValidation(UnifiedRequestHeaders.IfMatch, conditions.IfMatch);
            if (conditions.Deadline is DateTimeOffset at) request.Headers.TryAddWithoutValidation(UnifiedRequestHeaders.Deadline, UnifiedRequestHeaders.FormatDeadline(at));
        }
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
        }
        HttpResponseMessage response;
        var sender = streaming ? streamingClient : client;
        try { response = await sender.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new TansrProtocolException("network_error"); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertCurrent(access);
            // 注入的 HttpClient 属于可信宿主。其 handler 必须禁用自动跳转/自动重试/共享 cookie。
            if (response.RequestMessage?.RequestUri is Uri actual && actual != target)
                throw new TansrProtocolException("redirect_rejected");
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) throw new TansrProtocolException("redirect_rejected");
            foreach (var encoding in response.Content.Headers.ContentEncoding)
                if (!encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) throw new TansrProtocolException("invalid_content_encoding");
            // 纪律二:四个 tansr-* 头恒在(含错误响应与 SSE 首帧);缺失/异族 → contract_unavailable,绝不回退。
            var meta = Meta(response);
            if (streaming && negotiateEventEnvelope && response.IsSuccessStatusCode && !meta.EventEnvelopeNegotiated)
                throw new EnvelopeNotNegotiatedException();
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    /// <summary>True when SSE frames of this client are wrapped in the unified event envelope (negotiated per request and
    /// echoed by the server; checked in <see cref="SendAsync"/>).</summary>
    internal bool EventEnvelopeNegotiated(HttpResponseMessage response) => negotiateEventEnvelope && Meta(response).EventEnvelopeNegotiated;

    /// <summary>Returns the family-native <c>data:</c> text of one SSE frame: when the unified envelope was negotiated the
    /// frame is parsed strictly (D18 seven keys) and <c>raw</c> is returned verbatim; otherwise the text is unchanged.
    /// Empty data (retry-only control frames) is never wrapped by the server and passes through.</summary>
    internal string FrameData(HttpResponseMessage response, string data, int maximumBytes)
    {
        if (data.Length == 0 || !EventEnvelopeNegotiated(response)) return data;
        return UnifiedEventEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(data), maximumBytes).RawText;
    }

    private bool SameOrigin(Uri other) => other.Scheme == origin.Scheme && other.Host == origin.Host && other.Port == origin.Port;

    internal static void ExpectContent(HttpResponseMessage response, string expected)
    {
        var contentType = response.Content.Headers.ContentType;
        if (contentType is null || !string.Equals(contentType.MediaType, expected, StringComparison.OrdinalIgnoreCase) ||
            contentType.CharSet is not null && !string.Equals(contentType.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            throw new TansrProtocolException("invalid_content_type");
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > maximum) throw new TansrProtocolException("response_too_large");
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var closeOnCancellation = cancellationToken.Register(stream.Dispose);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            for (; ; )
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                // Closing a response stream on cancellation can complete a pending read with EOF
                // rather than an exception. Never accept the already buffered body in that case.
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0) return output.ToArray();
                if (output.Length + count > maximum) throw new TansrProtocolException("response_too_large");
                output.Write(buffer, 0, count);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (IOException) { throw new TansrProtocolException("network_error"); }
    }

    internal static async Task ThrowHttpAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken, bool strictControl = false,
        InputErrorMode inputErrorMode = InputErrorMode.None)
    {
        ExpectContent(response, "application/json");
        var bytes = await ReadBodyAsync(response, maximum, cancellationToken).ConfigureAwait(false);
        // 统一信封分支(UAPI-01):门面以普通 JSON.stringify 出线、不保证 canonical,先宽松解析识别 contract 键;
        // 是信封 → UnifiedApiException / 畸形 → contract_unavailable;不是信封(archive-sync-v1 直通)才走族解码。
        ThrowUnified(response, bytes);
        var json = strictControl ? SessionJson.Control(bytes, maximum) : SessionJson.Parse(bytes);
        if (strictControl && json.TryGetProperty("protocol", out _))
        {
            try { WireJson.ValidateNamed("ErrorResponse", json); }
            catch (WireProtocolException) { throw new TansrProtocolException("invalid_response"); }
            if (json.GetProperty("status").GetInt32() != (int)response.StatusCode) throw new TansrProtocolException("invalid_response");
            throw new TansrHttpException((int)response.StatusCode, json.GetProperty("code").GetString()!,
                retryAction: json.GetProperty("retryAction").GetString(),
                retryAfterMs: json.TryGetProperty("retryAfterMs", out var retryAfter) ? retryAfter.GetInt32() : (int?)null);
        }
        string code = "invalid_response";
        string? scope = null, reason = null;
        if (inputErrorMode != InputErrorMode.None && json.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var inputCode = SessionJson.Code(json, "code"); var state = outcome.GetString(); var status = (int)response.StatusCode;
            var valid = inputErrorMode == InputErrorMode.Status
                ? state == "rejected" && inputCode == "input_not_found" && status == 404
                : state == "closed" ? status == 409
                : state == "rejected" && status == (inputCode == "injection_limit" ? 429 : inputCode == "input_conflict" ? 409 : 422);
            if (valid && inputCode is not null) throw new TansrHttpException(status, inputCode, inputOutcome: state);
        }
        if (json.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            code = SessionJson.Code(error, "code") ?? code;
            if (error.TryGetProperty("detail", out var detail) && detail.ValueKind == System.Text.Json.JsonValueKind.Object)
            { scope = SessionJson.Code(detail, "scope"); reason = SessionJson.Code(detail, "reason"); }
        }
        throw new TansrHttpException((int)response.StatusCode, code, scope, reason);
    }

    /// <summary>Shared by every family error decoder: throws when <paramref name="bytes"/> is a unified error envelope,
    /// returns when the body carries no <c>contract</c> key. Non-JSON bodies on error responses are
    /// <c>contract_unavailable</c>/<c>invalid_json</c> rather than a family decode failure.</summary>
    /// <summary>Throws <see cref="UnifiedApiException"/> when the error body is a <c>unified-v1</c> envelope. Bodies that are
    /// not JSON objects or carry another <c>contract</c> are left to the family decoder (passthrough), which applies its own
    /// strict shape checks. The facade writes envelopes with plain <c>JSON.stringify</c>, so the parse here is lenient.</summary>
    internal static void ThrowUnified(HttpResponseMessage response, byte[] bytes)
    {
        var meta = Meta(response);
        JsonElement json;
        try { json = WireJson.Parse(bytes, Math.Max(bytes.Length, 1), 64); }
        catch (WireProtocolException) { return; }
        if (json.ValueKind != JsonValueKind.Object) return;
        UnifiedErrorEnvelope.TryThrow(response, json, meta);
    }

    public void Dispose()
    {
        if (!ownsClient) return;
        try { client.Dispose(); }
        finally { if (!ReferenceEquals(streamingClient, client)) streamingClient.Dispose(); }
    }
}
