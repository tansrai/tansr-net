using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class ExampleConnectionTests
{
    [Fact]
    public async Task Sdk1ConnectionDoesNotInvokeSdk2Initialization()
    {
        using var handler = new Handler(); using var http = UnifiedStamp.Client(handler); using var client = Client(http, SessionContract.Sdk1);
        await ExampleConnection.InitializeSessionAsync(client, SessionContract.Sdk1, "original-session");
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("created-session")]
    [InlineData("resumed-session")]
    public async Task Sdk2UiInitializationKeepsOriginalSessionAndDoesNotBindOrChangeRequestedTools(string sessionId)
    {
        using var handler = new Handler(); using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        await ExampleConnection.InitializeSessionAsync(client, SessionContract.Sdk2OffloadV1, sessionId);
        Assert.Equal(new[] { ApiRoutes.SessionCapabilities.Path(), ApiRoutes.ExecutionInitialize.Path(id: sessionId) }, handler.Requests);
        var request = Assert.Single(handler.Bodies);
        Assert.Equal(sessionId, request.GetProperty("sessionId").GetString());
        Assert.Equal("sdk2-ext-v1", request.GetProperty("protocol").GetString());
        Assert.False(request.TryGetProperty("requestedTools", out _));
        Assert.Equal("windows", request.GetProperty("platform").GetProperty("platform").GetString());
        Assert.Equal("csharp", request.GetProperty("platform").GetProperty("language").GetString());
        Assert.Equal("dotnet-v1", request.GetProperty("platform").GetProperty("adapterVersion").GetString());
    }

    [Fact]
    public async Task UnknownInitializationIsNotRetriedOrTurnedIntoANewSession()
    {
        using var handler = new Handler { Disconnect = true }; using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        await Assert.ThrowsAnyAsync<Exception>(() => ExampleConnection.InitializeSessionAsync(client, SessionContract.Sdk2OffloadV1, "keep-original"));
        Assert.Equal(new[] { ApiRoutes.SessionCapabilities.Path(), ApiRoutes.ExecutionInitialize.Path(id: "keep-original") }, handler.Requests);
    }

    [Fact]
    public async Task RejectedCreationIsSentOnceWithTheOriginalRequestKeyAndNeverInitialized()
    {
        using var handler = new Handler { RejectCreation = true }; using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        var error = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CreateSessionAsync(new CreateSessionOptions { RequestId = "original-create-key" }));
        Assert.Equal("conflict", error.Code);
        Assert.Equal("resume_unavailable", error.Detail.DomainCode);
        Assert.Equal(new[] { ApiRoutes.SessionCapabilities.Path(), ApiRoutes.SessionCreate.Path() }, handler.Requests);
        Assert.Equal("original-create-key", Assert.Single(handler.Bodies).GetProperty("requestId").GetString());
    }

    private static TansrClient Client(HttpClient http, SessionContract contract = SessionContract.Sdk2OffloadV1) => new(new TansrClientOptions
    {
        BaseUri = new Uri("https://serve.test"),
        TokenProvider = _ => Task.FromResult("synthetic-ticket"),
        SessionContract = contract,
        PrincipalProvider = () => "user",
        ExecutionScopeProvider = () => Json("{\"applicationScopeId\":\"app\",\"endUserId\":\"user\",\"authorizationRevision\":\"1\"}")
    }, http);
    private static JsonElement Json(string value) => WireJson.Parse(Encoding.UTF8.GetBytes(value));
    private sealed class Handler : HttpMessageHandler
    {
        internal bool Disconnect;
        internal bool RejectCreation;
        internal List<string> Requests { get; } = new();
        internal List<JsonElement> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == ApiRoutes.SessionCapabilities.Path())
                return Response("{\"protocol\":\"sdk2-ext-v1\",\"contracts\":[{\"contract\":\"sdk1\",\"availability\":\"legacy-complete\"},{\"contract\":\"sdk2-offload-v1\",\"availability\":\"source-required\"}]}");
            var body = Json(await request.Content!.ReadAsStringAsync(cancellationToken)); Bodies.Add(body);
            if (RejectCreation && request.RequestUri.AbsolutePath == ApiRoutes.SessionCreate.Path())
            {
                var rejected = new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("{\"contract\":\"unified-v1\",\"traceId\":\"synthetic-trace\",\"requestId\":\"original-create-key\",\"code\":\"conflict\",\"status\":409,\"retryAction\":\"none\",\"message\":\"synthetic conflict\",\"detail\":{\"domainCode\":\"resume_unavailable\",\"domainStatus\":409}}", Encoding.UTF8, "application/json")
                };
                rejected.Headers.TryAddWithoutValidation("x-request-id", "synthetic-trace");
                return rejected;
            }
            if (Disconnect) throw new HttpRequestException("synthetic disconnect after initialization was sent");
            var response = "{\"protocol\":\"sdk2-ext-v1\",\"sessionId\":" + JsonSerializer.Serialize(body.GetProperty("sessionId").GetString()) +
                ",\"platform\":" + body.GetProperty("platform").GetRawText() + ",\"capabilityRevision\":\"" + new string('a', 64) + "\",\"effectiveTools\":[],\"binding\":null}";
            return Response(response);
        }
        private static HttpResponseMessage Response(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(WireJson.CanonicalString(Json(value)), Encoding.UTF8, "application/json") };
    }
}
