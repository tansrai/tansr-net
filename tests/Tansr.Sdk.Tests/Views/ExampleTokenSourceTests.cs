using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Examples;

namespace Tansr.Sdk.Tests.Views;

public sealed class ExampleTokenSourceTests
{
    [Fact]
    public async Task ValidTicketDoesNotCallLoginAndConcurrentExpiryRotatesOnlyOnce()
    {
        using var f = new Fixture(); using var source = f.Source();
        Assert.Equal("access-original", await source.GetTokenAsync(Fixture.Serve, default)); Assert.Equal(0, f.Calls);
        f.Now += TimeSpan.FromMinutes(5);
        var sent = Signal(); var release = Signal();
        f.Respond = async (request, ct) =>
        {
            Assert.Equal("https://login.example/refresh", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("refresh-original", body.RootElement.GetProperty("refreshToken").GetString());
            sent.SetResult(); await release.Task;
            return Json(f.Ticket("access-next", "refresh-next"));
        };
        var first = source.GetTokenAsync(Fixture.Serve, default); await sent.Task;
        var rest = Enumerable.Range(0, 12).Select(_ => source.GetTokenAsync(Fixture.Serve, default)).ToArray();
        using var cancelled = new CancellationTokenSource();
        var waiter = source.GetTokenAsync(Fixture.Serve, cancelled.Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        Assert.Equal("access-next", await first);
        Assert.All(await Task.WhenAll(rest), value => Assert.Equal("access-next", value)); Assert.Equal(1, f.Calls);
        f.Now += TimeSpan.FromMinutes(5);
        f.Respond = async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("refresh-next", body.RootElement.GetProperty("refreshToken").GetString());
            return Json(f.Ticket("access-last", "refresh-last"));
        };
        Assert.Equal("access-last", await source.GetTokenAsync(Fixture.Serve, default)); Assert.Equal(2, f.Calls);
    }

    [Fact]
    public async Task LostRotationResponseNeverReusesRefreshOrFallsBackToOldAccess()
    {
        using var f = new Fixture(); using var source = f.Source(); f.Now += TimeSpan.FromMinutes(5);
        f.Respond = (_, _) => throw new HttpRequestException("synthetic transport error with secret refresh-original");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTokenAsync(Fixture.Serve, default));
        Assert.Equal("login_refresh_unconfirmed_new_login_required", error.Message); Assert.Null(error.InnerException);
        Assert.Equal("login_required", (await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTokenAsync(Fixture.Serve, default))).Message);
        Assert.Equal(1, f.Calls);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("user")]
    [InlineData("scope")]
    [InlineData("expired")]
    [InlineData("refresh-reused")]
    [InlineData("refresh-extended")]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("status")]
    public async Task UntrustedRefreshResponseIsNeverPublishedOrRetried(string kind)
    {
        using var f = new Fixture(); using var source = f.Source(); f.Now += TimeSpan.FromMinutes(5);
        f.Respond = (_, _) => Task.FromResult(kind switch
        {
            "json" => Json("{"),
            "user" => Json(f.Ticket(user: "another-user")),
            "scope" => Json(f.Ticket(revision: "2")),
            "expired" => Json(f.Ticket(expires: f.Now - TimeSpan.FromSeconds(1))),
            "refresh-reused" => Json(f.Ticket(refresh: "refresh-original")),
            "refresh-extended" => Json(f.Ticket(refreshExpires: f.RefreshEnd + TimeSpan.FromMinutes(1))),
            "redirect" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://untrusted.example/") } },
            "oversize" => Json(new string(' ', 65537)),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTokenAsync(Fixture.Serve, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTokenAsync(Fixture.Serve, default));
        Assert.Equal(1, f.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrustedIdentityChangeOrExplicitReplacementRejectsLateResponse(bool explicitReplacement)
    {
        using var f = new Fixture(); using var source = f.Source(); f.Now += TimeSpan.FromMinutes(5);
        var sent = Signal(); var release = Signal();
        f.Respond = async (_, _) => { sent.SetResult(); await release.Task; return Json(f.Ticket()); };
        var pending = source.GetTokenAsync(Fixture.Serve, default); await sent.Task;
        if (explicitReplacement) source.Invalidate(); else f.Principal = "another-principal";
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTokenAsync(Fixture.Serve, default));
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task IdentityChangeBeforeRequestAndServeChangeNeverSendCredentials()
    {
        using var f = new Fixture(); using var source = f.Source();
        f.Scope = Fixture.ScopeValue("2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTokenAsync(Fixture.Serve, default));
        f.Scope = Fixture.ScopeValue("1");
        using var other = f.Source();
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.GetTokenAsync("https://another-serve.example/", default));
        Assert.Equal(0, f.Calls);
    }

    [Fact]
    public async Task CancellationBeforeSendingDoesNotConsumeButCancellationAfterSendingIsUnknown()
    {
        using var f = new Fixture(); using var source = f.Source();
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetTokenAsync(Fixture.Serve, before.Token));
        Assert.Equal("access-original", await source.GetTokenAsync(Fixture.Serve, default));
        f.Now += TimeSpan.FromMinutes(5); var sent = Signal();
        f.Respond = async (_, ct) => { sent.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Json(f.Ticket()); };
        using var after = new CancellationTokenSource();
        var pending = source.GetTokenAsync(Fixture.Serve, after.Token); await sent.Task; after.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTokenAsync(Fixture.Serve, default));
        Assert.Equal(1, f.Calls);
    }

    [Theory]
    [InlineData("http://login.example/", true)]
    [InlineData("http://127.0.0.1:18787/", false)]
    [InlineData("https://login.example/other", false)]
    [InlineData("https://user:password@login.example/", false)]
    public void UnsafeLoginOriginsAreRejected(string login, bool allowHttp)
    {
        using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => f.Source(login: login, allowHttp: allowHttp)); Assert.Equal(0, f.Calls);
    }

    [Fact]
    public void ExplicitLoopbackAndIndependentLoginOriginAreSupported()
    {
        using var f = new Fixture(); using var source = f.Source(login: "http://127.0.0.1:18787/", allowHttp: true);
        Assert.Equal(0, f.Calls);
    }

    [Fact]
    public void InitialTicketMustMatchTrustedIdentityAndDisplayedAccess()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => f.Source(ticket: f.Ticket(user: "other")));
        Assert.Throws<InvalidOperationException>(() => f.Source(ticket: f.Ticket(revision: "2")));
        Assert.Throws<InvalidOperationException>(() => f.Source(ticket: f.Ticket("different-access")));
        Assert.Throws<InvalidOperationException>(() => f.Source(ticket: f.Ticket().Replace("\"tokenType\":\"Bearer\"", "\"tokenType\":\"Bearer\",\"tokenType\":\"Bearer\"")));
        Assert.Equal(0, f.Calls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct); }
    private sealed class Fixture : IDisposable
    {
        internal const string Serve = "https://serve.example/";
        internal DateTimeOffset Now = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        internal readonly DateTimeOffset RefreshEnd = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
        internal string Principal = "user";
        internal JsonElement Scope = ScopeValue("1");
        internal int Calls;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond = (_, _) => throw new InvalidOperationException("unexpected_request");
        private readonly HttpClient client;
        internal Fixture() => client = new HttpClient(new Handler((request, token) => { Interlocked.Increment(ref Calls); return Respond(request, token); }));
        internal ExampleTokenSource Source(string? ticket = null, string login = "https://login.example/", bool allowHttp = false) =>
            new(new Uri(Serve), new Uri(login), ticket ?? Ticket("access-original", "refresh-original"), "access-original", allowHttp,
                () => Principal, () => Scope, client, () => Now);
        internal string Ticket(string access = "access-next", string refresh = "refresh-next", string user = "user", string revision = "1",
            DateTimeOffset? expires = null, DateTimeOffset? refreshExpires = null) => JsonSerializer.Serialize(new
            {
                tokenType = "Bearer",
                accessToken = access,
                refreshToken = refresh,
                endUserId = user,
                expiresAt = (expires ?? Now + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds(),
                refreshExpiresAt = (refreshExpires ?? RefreshEnd).ToUnixTimeMilliseconds(),
                sdk2Scope = ScopeValue(revision)
            });
        internal static JsonElement ScopeValue(string revision) => JsonSerializer.SerializeToElement(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = revision });
        public void Dispose() => client.Dispose();
    }
}
