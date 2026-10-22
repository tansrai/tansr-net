using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Sessions;

public sealed class SessionPersistenceTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<object>> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(await respond(request)), Encoding.UTF8, "application/json") };
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); return result;
        }
    }
    private static TansrClient Client(HttpClient http) => new(new TansrClientOptions
    {
        BaseUri = new Uri("https://serve.test"),
        TokenProvider = _ => Task.FromResult("synthetic"),
        PrincipalProvider = () => "app/user"
    }, http);
    private static object View(string mode = "cloud", bool localDisk = false) => new
    {
        contract = "serve-persistence-v1",
        sessionId = "session",
        mode,
        sourceId = "platform/app/user/session",
        sourceGeneration = "generation-1",
        coverage = new[] { "history", "memory", "executions", "input-receipts", "control" },
        handoff = new { archive = mode == "cloud" ? "cloud-immutable-publish" : "terminal-durable-ack", trustedControl = true, executionJournal = mode == "cloud", inputReceipts = true },
        localDisk
    };
    private static object Receipt(string id = "original-request", string state = "accepted", string session = "session", string acceptedAt = "2026-10-21T00:00:00.000Z") => new
    {
        sessionId = session,
        requestId = id,
        digest = new string('a', 64),
        generation = "generation-1",
        state,
        acceptedAt
    };
    [Theory]
    [InlineData("2026-10-21 00:00:00Z")]
    [InlineData("2026-10-21T00:00:00+00:00")]
    [InlineData("2026-02-30T00:00:00Z")]
    public async Task InputReceiptRequiresTheOriginalUtcIsoContract(string time)
    {
        using var handler = new Handler(_ => Task.FromResult<object>(new
        { contract = "serve-persistence-v1", sessionId = "session", receipt = Receipt(acceptedAt: time) }));
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        await Assert.ThrowsAsync<TansrProtocolException>(() => client.GetInputAcceptanceAsync("session", "original-request"));
    }
    [Theory]
    [InlineData("terminal")]
    [InlineData("cloud")]
    public async Task ExplicitModeComesFromTrustedDiscoveryAndUsesNoLocalStore(string mode)
    {
        using var handler = new Handler(request =>
        { Assert.Equal("/api/sessions/session/persistence", request.RequestUri!.PathAndQuery); return Task.FromResult(View(mode)); });
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        Assert.Equal(mode, (await client.GetSessionPersistenceAsync("session")).GetProperty("mode").GetString());
    }
    [Fact]
    public async Task LocalDiskDeclarationIsNotAcceptedAsDisklessMode()
    {
        using var handler = new Handler(_ => Task.FromResult(View(localDisk: true)));
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        await Assert.ThrowsAsync<TansrProtocolException>(() => client.GetSessionPersistenceAsync("session"));
    }
    [Theory]
    [InlineData("accepted")]
    [InlineData("unknown")]
    public async Task OriginalInputLookupPreservesStateAndNeverSubmitsAgain(string state)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/sessions/session/input-requests/original-request", request.RequestUri!.PathAndQuery);
            return Task.FromResult<object>(new { contract = "serve-persistence-v1", sessionId = "session", receipt = Receipt(state: state) });
        });
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        var result = await client.GetInputAcceptanceAsync("session", "original-request");
        Assert.Equal(state, result.GetProperty("receipt").GetProperty("state").GetString()); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task ReceiptForAnotherOriginalRequestIsRejected()
    {
        using var handler = new Handler(_ => Task.FromResult<object>(new { contract = "serve-persistence-v1", sessionId = "session", receipt = Receipt("other") }));
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        await Assert.ThrowsAsync<TansrProtocolException>(() => client.GetInputAcceptanceAsync("session", "original-request"));
    }
    [Fact]
    public async Task InputListIsBoundedOrderedAndCannotGuessTheOriginalRequest()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/api/sessions/session/input-requests?limit=2&cursor=a", request.RequestUri!.PathAndQuery);
            return Task.FromResult<object>(new { contract = "serve-persistence-v1", sessionId = "session", receipts = new[] { Receipt("b"), Receipt("c", "unknown") }, nextCursor = "c" });
        });
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        Assert.Equal(2, (await client.ListInputAcceptancesAsync("session", "a", 2)).GetProperty("receipts").GetArrayLength());
    }
    [Fact]
    public async Task NewMessageExplicitlyCarriesOriginalKeyAndLossDoesNotResend()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("/api/sessions/session/messages", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("original-request", body.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("synthetic input", body.RootElement.GetProperty("prompt").GetString());
            throw new HttpRequestException("synthetic response loss");
        });
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        var session = new AgentSession(client, "session", -1, false);
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => session.SendWithRequestAsync("synthetic input", "original-request"))).Code);
        Assert.Equal(1, handler.Calls);
    }
}
