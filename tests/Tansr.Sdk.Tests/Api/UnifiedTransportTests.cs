using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Tests.Api;

/// <summary>UAPI-01 transport discipline (手册 §16.6): four mandatory response headers with no fallback, the unified error
/// envelope ahead of family decoders, `tansr-session-family` on every request, `tansr-closure-id` 412 → rediscover,
/// and `tansr-event-envelope: unified-v1` negotiation with echo check and `raw` unwrapping.</summary>
public sealed class UnifiedTransportTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { lock (Requests) Requests.Add(request); return Task.FromResult(respond(request)); }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? domain = "session")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (domain != null) UnifiedStamp.Apply(response, domain);
        return response;
    }
    private static HttpResponseMessage Events(string text, bool echo)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(text))) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
        UnifiedStamp.Apply(response, "session", echo);
        return response;
    }
    private const string Created = "{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":0}";
    private static TansrClientOptions Options(bool negotiate = false) => new()
    { BaseUri = new("https://serve.test/"), TokenProvider = _ => Task.FromResult("token"), ReconnectDelay = TimeSpan.Zero, MaxReconnectAttempts = 1, NegotiateEventEnvelope = negotiate };

    [Theory]
    [InlineData("missing", "missing_contract_header")]
    [InlineData("foreign", "contract_mismatch")]
    [InlineData("revision", "invalid_contract_headers")]
    [InlineData("domain", "invalid_contract_headers")]
    [InlineData("hash", "invalid_contract_headers")]
    public async Task ResponsesWithoutTheFourUnifiedHeadersRaiseContractUnavailableAndNeverFallBack(string fault, string reason)
    {
        using var handler = new Handler(_ =>
        {
            var response = Json(Created, domain: fault == "missing" ? null : "session");
            if (fault == "foreign") { response.Headers.Remove(UnifiedHeaders.Contract); response.Headers.TryAddWithoutValidation(UnifiedHeaders.Contract, "sdk2-ext-v1"); }
            if (fault == "revision") { response.Headers.Remove(UnifiedHeaders.ManifestRevision); response.Headers.TryAddWithoutValidation(UnifiedHeaders.ManifestRevision, "0"); }
            if (fault == "domain") { response.Headers.Remove(UnifiedHeaders.Domain); response.Headers.TryAddWithoutValidation(UnifiedHeaders.Domain, "sessions"); }
            if (fault == "hash") { response.Headers.Remove(UnifiedHeaders.SchemaHash); response.Headers.TryAddWithoutValidation(UnifiedHeaders.SchemaHash, "md5:abc"); }
            return response;
        });
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var error = await Assert.ThrowsAsync<ContractUnavailableException>(() => client.CreateSessionAsync(new()));
        Assert.Equal(reason, error.Reason); Assert.Equal("contract_unavailable", error.Code); Assert.Equal(200, error.StatusCode);
        // No fallback request to any legacy prefix, no retry: a single /api request was made.
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/sessions", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task EveryRequestDeclaresTheSessionFamilyAndOnlyTheSdkMayEmitUnifiedHeaders()
    {
        using var handler = new Handler(_ => Json(Created));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        await client.CreateSessionAsync(new());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("sdk1", request.Headers.GetValues(UnifiedHeaders.SessionFamily).Single());
        Assert.False(request.Headers.Contains(UnifiedHeaders.EventEnvelope)); Assert.False(request.Headers.Contains(UnifiedHeaders.ClosureId));
        var options = Options(); options.AdditionalRequestHeaders = new Dictionary<string, string> { ["tansr-closure-id"] = new string('a', 64) };
        Assert.Throws<ArgumentException>(() => new TansrClient(options, http));
    }

    [Fact]
    public async Task UnifiedErrorEnvelopeIsDecodedBeforeTheFamilyShapeAndKeepsDomainFacts()
    {
        const string body = "{\"contract\":\"unified-v1\",\"traceId\":\"5f2b0e3c9a7d4b1e8c6f0a2d3e4b5c6d\",\"requestId\":\"req-1\",\"code\":\"conflict\",\"status\":409,\"retryAction\":\"refresh\"," +
            "\"message\":\"session ended\",\"detail\":{\"domain\":\"session\",\"family\":\"agent-session-v1\",\"domainCode\":\"session_ended\",\"domainStatus\":409,\"domainRetryAction\":\"none\"}}";
        using var handler = new Handler(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/sessions" ? Json(Created) : Json(body, HttpStatusCode.Conflict));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<UnifiedApiException>(() => session.CancelAsync());
        Assert.Equal("conflict", error.Code); Assert.Equal(409, error.StatusCode); Assert.Equal("refresh", error.RetryAction);
        Assert.Equal("session_ended", error.Detail.DomainCode); Assert.Equal(409, error.Detail.DomainStatus); Assert.Equal("none", error.Detail.DomainRetryAction);
#pragma warning disable CS0618 // the obsolete bridge reads the same family facts
        Assert.Equal("session_ended", error.DomainCode); Assert.Equal(409, error.DomainStatus); Assert.Equal("none", error.DomainRetryAction);
#pragma warning restore CS0618
        Assert.Equal("req-1", error.RequestId); Assert.Equal("5f2b0e3c9a7d4b1e8c6f0a2d3e4b5c6d", error.TraceId); Assert.False(error.FacadeOwned);
        Assert.Equal("session", error.Domain); Assert.Equal(ApiRoutes.ManifestRevision, error.Meta.ManifestRevision);
        // The family-native exception type is still what business code catches.
        Assert.IsAssignableFrom<TansrHttpException>(error);
    }

    [Fact]
    public async Task FamilyErrorBodiesPassThroughUnchangedWhenTheFacadeDoesNotWrapThem()
    {
        const string body = "{\"error\":{\"code\":\"session_ended\",\"message\":\"ended\"}}";
        using var handler = new Handler(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/sessions" ? Json(Created) : Json(body, HttpStatusCode.Conflict));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => session.CancelAsync());
        Assert.Equal("session_ended", error.Code); Assert.Equal(409, error.StatusCode); Assert.IsNotType<UnifiedApiException>(error);
#pragma warning disable CS0618 // passthrough: the obsolete bridge is the wire value itself
        Assert.Equal(error.Code, error.DomainCode);
#pragma warning restore CS0618
    }

    [Fact]
    public async Task MalformedUnifiedEnvelopeIsContractUnavailableNotAFamilyError()
    {
        const string body = "{\"contract\":\"unified-v1\",\"code\":\"conflict\",\"status\":409}";
        using var handler = new Handler(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/sessions" ? Json(Created) : Json(body, HttpStatusCode.Conflict));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<ContractUnavailableException>(() => session.CancelAsync());
        Assert.Equal("invalid_error_body", error.Reason); Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task ClosurePreconditionFailureRequiresRediscovery()
    {
        string body = "{\"contract\":\"unified-v1\",\"traceId\":\"5f2b0e3c9a7d4b1e8c6f0a2d3e4b5c6d\",\"requestId\":null,\"code\":\"precondition_failed\",\"status\":412,\"retryAction\":\"rediscover\"," +
            "\"message\":\"capability closure is stale; rediscover before retrying\",\"detail\":{\"domainCode\":\"closure_stale\",\"closureId\":\"" + new string('b', 64) + "\"}}";
        using var handler = new Handler(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/sessions" ? Json(Created) : Json(body, HttpStatusCode.PreconditionFailed));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<UnifiedApiException>(() => session.CancelAsync());
        Assert.True(error.RequiresRediscovery); Assert.True(error.FacadeOwned); Assert.Equal(new string('b', 64), error.ClosureId); Assert.Equal("closure_stale", error.Detail.DomainCode);
        Assert.Equal(2, handler.Requests.Count); // no automatic retry: rediscovery is the caller's decision
    }

    private static string Frame(int seq, string type, string extra = "") => "id: " + seq + "\ndata: {\"sessionId\":\"s\",\"seq\":" + seq + ",\"type\":\"" + type + "\"" + extra + "}\n\n";
    private static string Enveloped(int seq, string type, string extra = "") =>
        "id: " + seq + "\ndata: {\"contract\":\"unified-v1\",\"eventId\":\"" + seq + "\",\"domain\":\"session\",\"type\":\"" + type + "\",\"cursorSet\":{\"eventCursor\":\"" + seq +
        "\",\"archiveCoverage\":null,\"outputWatermark\":null,\"materialConsumed\":null,\"ackReceipt\":null},\"terminalStatus\":null,\"raw\":{\"sessionId\":\"s\",\"seq\":" + seq + ",\"type\":\"" + type + "\"" + extra + "}}\n\n";

    [Fact]
    public async Task EventEnvelopeIsOffByDefaultAndFramesAreReadNatively()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Json(Created) : Events(Frame(1, "msg.text.delta", ",\"text\":\"a\"") + Frame(2, "session.ended"), echo: false));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<AgentEvent>();
        await session.ObserveAsync((item, _) => { seen.Add(item); return Task.CompletedTask; });
        Assert.Equal(2, seen.Count); Assert.Equal("a", seen[0].Data.GetProperty("text").GetString());
        Assert.False(handler.Requests[1].Headers.Contains(UnifiedHeaders.EventEnvelope));
    }

    [Fact]
    public async Task NegotiatedEnvelopeIsRequestedEchoedAndUnwrappedToTheFamilyEvent()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Json(Created) : Events(Enveloped(1, "msg.text.delta", ",\"text\":\"a\"") + Enveloped(2, "session.ended"), echo: true));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(negotiate: true), http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<AgentEvent>();
        await session.ObserveAsync((item, _) => { seen.Add(item); return Task.CompletedTask; });
        Assert.Equal(2, seen.Count); Assert.Equal("a", seen[0].Data.GetProperty("text").GetString()); Assert.Equal("msg.text.delta", seen[0].Name);
        Assert.Equal(2, session.LastSequence);
        Assert.Equal(UnifiedHeaders.EventEnvelopeContract, handler.Requests[1].Headers.GetValues(UnifiedHeaders.EventEnvelope).Single());
        Assert.False(handler.Requests[0].Headers.Contains(UnifiedHeaders.EventEnvelope)); // only SSE requests negotiate
    }

    [Fact]
    public async Task NegotiatedEnvelopeWithoutServerEchoIsNotSilentlyReadAsNativeFrames()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Json(Created) : Events(Frame(1, "session.ended"), echo: false));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(negotiate: true), http);
        var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<EnvelopeNotNegotiatedException>(() => session.ObserveAsync((_, _) => Task.CompletedTask));
        Assert.Equal("envelope_not_negotiated", error.Code);
    }

    [Fact]
    public async Task NegotiatedEnvelopeRejectsFramesThatAreNotStrictSevenKeyEnvelopes()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Json(Created) : Events(Frame(1, "session.ended"), echo: true));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(negotiate: true), http);
        var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((_, _) => Task.CompletedTask));
        Assert.Equal("invalid_envelope", error.Code);
    }

    [Fact]
    public void LegacyPathsCannotBeSentThroughTheTransport()
    {
        Assert.False(ApiRoutes.IsApiPath("/v2/sessions")); Assert.False(ApiRoutes.IsApiPath("/v3/sdk2/sessions")); Assert.False(ApiRoutes.IsApiPath("/v3/terminal/capabilities"));
    }
}
