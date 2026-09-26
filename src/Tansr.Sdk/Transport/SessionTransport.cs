using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Transport;

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
    private readonly bool ownsClient;
    private readonly Func<CancellationToken, Task<string>> tokens;
    private readonly Func<string>? principalProvider;
    private readonly object principalGate = new object();
    private string? principal;
    private bool principalBound;

    internal SessionTransport(TansrClientOptions options, HttpClient? injected)
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
        if ((options.SessionContract == SessionContract.Sdk2OffloadV1 || options.ExecutionScopeProvider is not null) && principalProvider is null)
            throw new ArgumentException("SDK2 requires a trusted PrincipalProvider.", nameof(options));
        ownsClient = injected is null;
        client = injected ?? new HttpClient(new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
        { Timeout = Timeout.InfiniteTimeSpan };
        if (client.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Use TokenProvider rather than default Authorization headers.", nameof(injected));
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

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, SessionAccess access,
        byte[]? body, string accept, string? contentType, string? lastEventId, CancellationToken cancellationToken)
    {
        AssertCurrent(access);
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.IndexOf('\\') >= 0)
            throw new TansrProtocolException("invalid_request");
        var target = new Uri(origin, path);
        if (!SameOrigin(target)) throw new TansrProtocolException("invalid_request");
        using var request = new HttpRequestMessage(method, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
        }
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
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
            return response;
        }
        catch { response.Dispose(); throw; }
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
                if (count == 0) return output.ToArray();
                if (output.Length + count > maximum) throw new TansrProtocolException("response_too_large");
                output.Write(buffer, 0, count);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (IOException) { throw new TansrProtocolException("network_error"); }
    }

    internal static async Task ThrowHttpAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken, bool strictControl = false)
    {
        ExpectContent(response, "application/json");
        var bytes = await ReadBodyAsync(response, maximum, cancellationToken).ConfigureAwait(false);
        var json = strictControl ? WireJson.DecodeControl(bytes, maximum) : SessionJson.Parse(bytes);
        string code = "invalid_response";
        string? scope = null, reason = null;
        if (json.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            code = SessionJson.Code(error, "code") ?? code;
            if (error.TryGetProperty("detail", out var detail) && detail.ValueKind == System.Text.Json.JsonValueKind.Object)
            { scope = SessionJson.Code(detail, "scope"); reason = SessionJson.Code(detail, "reason"); }
        }
        throw new TansrHttpException((int)response.StatusCode, code, scope, reason);
    }

    public void Dispose() { if (ownsClient) client.Dispose(); }
}
