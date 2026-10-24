using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Examples;

// Demo login credentials are independent of platform/model keys. Only this process
// holds the rotating refresh ticket; a lost refresh response requires a new login.
internal sealed class ExampleTokenSource : IDisposable
{
    private readonly object state = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Uri serveOrigin, refreshUri;
    private readonly Func<string> principal;
    private readonly Func<JsonElement> scope;
    private readonly Func<DateTimeOffset> now;
    private readonly string initialPrincipal, initialScope, endUserId;
    private string accessToken, refreshToken;
    private DateTimeOffset refreshAt, refreshExpiresAt;
    private bool invalid;
    private const int MaximumTicketBytes = 65536;

    internal static ExampleTokenSource? FromEnvironment(string endpoint, string displayedAccessToken, bool allowHttp)
    {
        var login = Environment.GetEnvironmentVariable("TANSR_LOGIN_URL");
        var ticket = Environment.GetEnvironmentVariable("TANSR_LOGIN_TICKET");
        // Consume once, including malformed configuration. Never leave a refresh
        // credential in the environment inherited by subsequent child processes.
        Environment.SetEnvironmentVariable("TANSR_LOGIN_TICKET", null);
        if (login == null && ticket == null) return null;
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(ticket))
            throw new InvalidOperationException("login_configuration_incomplete");
        var authority = TrustedExampleScope.FromEnvironment() ?? throw new InvalidOperationException("login_trusted_scope_required");
        var expectedEndpoint = Environment.GetEnvironmentVariable("TANSR_SERVE_URL");
        if (string.IsNullOrWhiteSpace(expectedEndpoint) || new Uri(expectedEndpoint) != new Uri(endpoint))
            throw new InvalidOperationException("login_serve_origin_changed");
        return new ExampleTokenSource(new Uri(endpoint), new Uri(login), ticket!, displayedAccessToken, allowHttp,
            authority.ReadPrincipal, authority.ReadScope);
    }

    internal ExampleTokenSource(Uri serveOrigin, Uri loginOrigin, string ticket, string displayedAccessToken, bool allowHttp,
        Func<string> principal, Func<JsonElement> scope, HttpClient? client = null, Func<DateTimeOffset>? now = null)
    {
        ValidateOrigin(serveOrigin, allowHttp); ValidateOrigin(loginOrigin, allowHttp);
        this.serveOrigin = serveOrigin; refreshUri = new Uri(loginOrigin, "refresh");
        this.principal = principal; this.scope = scope; this.now = now ?? (() => DateTimeOffset.UtcNow);
        initialPrincipal = principal();
        if (string.IsNullOrWhiteSpace(initialPrincipal) || initialPrincipal.Length > 1024) throw new InvalidOperationException("login_scope_changed");
        var trusted = scope(); WireJson.ValidateNamed("Scope", trusted);
        initialScope = WireJson.CanonicalString(trusted); endUserId = trusted.GetProperty("endUserId").GetString()!;
        var parsed = ParseTicket(ticket, true);
        if (!string.Equals(parsed.Access, displayedAccessToken, StringComparison.Ordinal))
            throw new InvalidOperationException("login_initial_token_mismatch");
        AssertAuthority();
        accessToken = parsed.Access; refreshToken = parsed.Refresh; refreshExpiresAt = parsed.RefreshExpires;
        refreshAt = RefreshAt(parsed.Expires);
        ownsClient = client == null;
        this.client = client ?? new HttpClient(new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
        { Timeout = Timeout.InfiniteTimeSpan };
        if (this.client.DefaultRequestHeaders.Count() != 0)
            throw new InvalidOperationException("login_client_headers_forbidden");
    }

    internal async Task<string> GetTokenAsync(string endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var requested) || requested != serveOrigin)
        { Invalidate(); throw new InvalidOperationException("login_serve_origin_changed"); }
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssertAuthority();
            lock (state)
            {
                if (invalid) throw new InvalidOperationException("login_required");
                if (now() < refreshAt) return accessToken;
                if (now() >= refreshExpiresAt) { Invalidate(); throw new InvalidOperationException("login_required"); }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            string originalRefresh;
            lock (state) originalRefresh = refreshToken;
            using var request = new HttpRequestMessage(HttpMethod.Post, refreshUri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = new StringContent(JsonSerializer.Serialize(new { refreshToken = originalRefresh }), Encoding.UTF8, "application/json");
            // From this point the one-use ticket may have been consumed remotely.
            // No automatic retry or stale-token fallback is permitted on any failure.
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is Uri actual && actual != refreshUri ||
                    response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" ||
                    response.Content.Headers.ContentEncoding.Any(value => !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("login_refresh_rejected");
                if (response.Content.Headers.ContentLength > MaximumTicketBytes) throw new InvalidOperationException("login_ticket_too_large");
                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var closeOnCancel = deadline.Token.Register(input.Dispose);
                using var output = new MemoryStream(); var buffer = new byte[4096];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (output.Length + read > MaximumTicketBytes) throw new InvalidOperationException("login_ticket_too_large");
                    output.Write(buffer, 0, read);
                }
                var next = ParseTicket(new UTF8Encoding(false, true).GetString(output.ToArray()), false);
                deadline.Token.ThrowIfCancellationRequested(); AssertAuthority();
                lock (state)
                {
                    if (invalid) throw new InvalidOperationException("login_required");
                    if (next.Refresh == originalRefresh || next.RefreshExpires > refreshExpiresAt)
                        throw new InvalidOperationException("login_rotation_invalid");
                    accessToken = next.Access; refreshToken = next.Refresh; refreshExpiresAt = next.RefreshExpires;
                    refreshAt = RefreshAt(next.Expires);
                    return accessToken;
                }
            }
            catch
            {
                Invalidate();
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                throw new InvalidOperationException("login_refresh_unconfirmed_new_login_required");
            }
        }
        finally { refreshGate.Release(); }
    }

    private void AssertAuthority()
    {
        try
        {
            if (principal() != initialPrincipal || WireJson.CanonicalString(scope()) != initialScope || principal() != initialPrincipal)
                throw new InvalidOperationException("login_scope_changed");
        }
        catch { Invalidate(); throw new InvalidOperationException("login_scope_changed"); }
    }

    private (string Access, string Refresh, DateTimeOffset Expires, DateTimeOffset RefreshExpires) ParseTicket(string text, bool initial)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumTicketBytes) throw new InvalidOperationException("login_ticket_too_large");
        using var document = JsonDocument.Parse(text); var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("login_ticket_invalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidOperationException("login_ticket_invalid");
        string Token(string name, int maximum)
        {
            var token = value.GetProperty(name).GetString();
            if (string.IsNullOrEmpty(token) || token!.Length > maximum || token.Any(c => c < 33 || c > 126))
                throw new InvalidOperationException("login_ticket_invalid");
            return token;
        }
        if (value.GetProperty("tokenType").GetString() != "Bearer" || value.GetProperty("endUserId").GetString() != endUserId)
            throw new InvalidOperationException("login_scope_changed");
        var ticketScope = value.GetProperty("sdk2Scope"); WireJson.ValidateNamed("Scope", ticketScope);
        if (WireJson.CanonicalString(ticketScope) != initialScope) throw new InvalidOperationException("login_scope_changed");
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(value.GetProperty("expiresAt").GetInt64());
        var refreshExpires = DateTimeOffset.FromUnixTimeMilliseconds(value.GetProperty("refreshExpiresAt").GetInt64());
        if ((!initial && expires <= now()) || refreshExpires <= now()) throw new InvalidOperationException("login_ticket_expired");
        return (Token("accessToken", 16384), Token("refreshToken", 2048), expires, refreshExpires);
    }

    private DateTimeOffset RefreshAt(DateTimeOffset expires)
    {
        var remaining = expires - now();
        return expires - TimeSpan.FromSeconds(Math.Min(30, Math.Max(0, remaining.TotalSeconds / 5)));
    }

    private static void ValidateOrigin(Uri value, bool allowHttp)
    {
        if (!value.IsAbsoluteUri || value.AbsolutePath != "/" || value.Query.Length != 0 || value.Fragment.Length != 0 || value.UserInfo.Length != 0 ||
            value.Scheme != Uri.UriSchemeHttps && !(allowHttp && value.Scheme == Uri.UriSchemeHttp &&
                (string.Equals(value.Host, "localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(value.DnsSafeHost, out var address) && IPAddress.IsLoopback(address))))
            throw new InvalidOperationException("login_origin_invalid");
    }

    internal void Invalidate()
    {
        lock (state) { invalid = true; accessToken = ""; refreshToken = ""; }
        lifetime.Cancel();
    }

    public void Dispose() { Invalidate(); if (ownsClient) client.Dispose(); }
}
