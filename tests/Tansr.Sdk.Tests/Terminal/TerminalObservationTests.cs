using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalObservationTests
{
    private static JsonElement Goldens()
    { using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Terminal", "Fixtures", "terminal-observation-v1.golden.json"))); return doc.RootElement.Clone(); }
    public static IEnumerable<object[]> Vectors()
    {
        var root = Goldens();
        foreach (var group in new[] { "positive", "negative" })
            foreach (var item in root.GetProperty(group).EnumerateArray()) yield return [item.GetProperty("id").GetString()!, item.GetProperty("definition").GetString()!, item.GetProperty("value").GetRawText(), group == "positive"];
    }
    [Theory]
    [MemberData(nameof(Vectors))]
    public void ServeGoldensAreConsumedWithoutSchemaOrValueRewriting(string id, string definition, string json, bool valid)
    {
        Assert.NotEmpty(id); using var doc = JsonDocument.Parse(json);
        if (valid) TerminalObservationContract.Validate(definition, doc.RootElement);
        else Assert.Throws<WireProtocolException>(() => TerminalObservationContract.Validate(definition, doc.RootElement));
    }
    [Fact]
    public void EmbeddedContractAndUnmodifiedGoldenHaveTheFrozenFingerprints()
    {
        using var stream = typeof(TerminalObservationClient).Assembly.GetManifestResourceStream("Tansr.Sdk.Terminal.terminal-observation-v1.schema.json")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); Assert.Equal(TerminalObservationClient.SchemaSha256, WireJson.Sha256(buffer.ToArray()));
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Terminal", "Fixtures", "terminal-observation-v1.golden.json"));
        Assert.Equal("2148eb11fb4538736cd244b17e5ba135bc26919081105fd1018ff884ab364e1a", WireJson.Sha256(bytes));
        Assert.Equal(TerminalObservationClient.SchemaSha256, Goldens().GetProperty("schemaSha256").GetString());
    }
    [Fact]
    public void PreviewRequiresExplicitOptIn()
    { Assert.Equal("unsupported_capability", Assert.Throws<TansrProtocolException>(() => new TerminalObservationClient(Options())).Code); }

    [Fact]
    public async Task BorrowedClientRetainsItsOriginalTransportAndRemainsUsableAfterFacadeDisposal()
    {
        using var handler = new Handler((request, _, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/api/sessions"
            ? JsonSerializer.SerializeToElement(new { sessions = Array.Empty<object>(), total = 0 }) : Resource("active"))));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        using (var observation = new TerminalObservationClient(client, true)) Assert.Equal(TerminalResourceState.Active, (await observation.ReadResourcesAsync("session-1")).State);
        Assert.Equal(0, (await client.ListSessionsAsync()).GetProperty("total").GetInt32()); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ObservationWaitUsesOnlyGetAndDistinguishesAcceptanceFromCompletedResources()
    {
        using var handler = new Handler((request, count, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content); Assert.Equal("controller-only", request.Headers.Authorization!.Parameter);
            Assert.Equal("/api/terminal/observation/sessions/session-1/resources?contract=terminal-observation-v1&sessionContract=sdk1", request.RequestUri!.PathAndQuery);
            return Task.FromResult(Json(Resource(count == 1 ? "accepted" : count == 2 ? "draining" : "completed")));
        });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TerminalObservationClient(Options(), true, http);
        var result = await client.WaitForResourcesAsync("session-1", TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(10));
        Assert.True(result.Completed); Assert.Equal(9007199254740993L, result.EpochStartSequence); Assert.Equal(3, handler.Calls);
    }
    [Theory]
    [InlineData("failed", "settlement_failed")]
    [InlineData("unknown", "settlement_unavailable")]
    public async Task FailedAndUnavailableResourceEvidenceCannotBeReportedAsSuccessfulDrain(string state, string error)
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Json(Resource(state, error: error))));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TerminalObservationClient(Options(), true, http);
        var failure = await Assert.ThrowsAsync<TerminalResourceSettlementException>(() => client.WaitForResourcesAsync("session-1", TimeSpan.FromSeconds(1)));
        Assert.False(failure.Observation.Completed); Assert.Equal(error, failure.Observation.ErrorCode); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task BoundedWaitTimesOutWithoutSendingCloseOrCancellingRemoteResources()
    {
        using var handler = new Handler((request, _, _) => { Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(Json(Resource("draining"))); });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TerminalObservationClient(Options(), true, http);
        await Assert.ThrowsAsync<TimeoutException>(() => client.WaitForResourcesAsync("session-1", TimeSpan.FromMilliseconds(60), pollInterval: TimeSpan.FromMilliseconds(10)));
        Assert.True(handler.Calls >= 1);
        Assert.Equal(TerminalResourceState.Draining, (await client.ReadResourcesAsync("session-1")).State);
    }
    [Fact]
    public async Task CallerCancellationIsNotReportedAsWaitDeadlineOrResourceCompletion()
    {
        using var entered = new CancellationTokenSource();
        using var handler = new Handler(async (_, _, ct) => { entered.Cancel(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TerminalObservationClient(Options(), true, http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WaitForResourcesAsync("session-1", TimeSpan.FromSeconds(2), cancellationToken: entered.Token));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task ANewRuntimeEpochCannotFulfillTheOriginalWait()
    {
        using var handler = new Handler((_, count, _) => Task.FromResult(Json(Resource(count == 1 ? "draining" : "completed", epoch: count.ToString()))));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TerminalObservationClient(Options(), true, http);
        Assert.Equal("stale_generation", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.WaitForResourcesAsync("session-1", TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(10)))).Code);
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task AuthorityChangesDuringTheReadRejectEvenAnOtherwiseValidCompletedResponse()
    {
        var scope = ExecutionFixture.Scope(); using var handler = new Handler((_, _, _) => { scope = ExecutionFixture.Scope(revision: "2"); return Task.FromResult(Json(Resource("completed"))); });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TerminalObservationClient(Options(() => scope), true, http);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.ReadResourcesAsync("session-1"))).Code); Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("wrong-session")]
    [InlineData("duplicate-operation")]
    [InlineData("wrong-tool")]
    public async Task CorrelationsCannotMixSessionsDuplicateOperationsOrEvadeAnExactToolFilter(string attack)
    {
        var session = new { sessionContract = "sdk1", sessionId = "session-1" };
        var item = new
        {
            contract = "terminal-services-v1",
            session = new { sessionContract = "sdk1", sessionId = attack == "wrong-session" ? "other" : "session-1" },
            toolCallId = attack == "wrong-tool" ? "other" : "call-1",
            operation = new { operationId = "op-1", requestDigest = new string('a', 64) },
            outputAuthority = "tool-output"
        };
        using var handler = new Handler((_, _, _) => Task.FromResult(Json(JsonSerializer.SerializeToElement(new
        {
            contract = "terminal-observation-v1",
            session,
            correlations = attack == "duplicate-operation" ? new[] { item, item } : new[] { item },
            truncated = false
        }))));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TerminalObservationClient(Options(), true, http);
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.ReadOutputCorrelationsAsync("session-1", toolCallId: "call-1"))).Code);
    }
    [Fact]
    public async Task StructuredAuthorityErrorsArePreservedWithoutRetryOrLegacyFallback()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Json(JsonSerializer.SerializeToElement(new { contract = "terminal-observation-v1", requestId = "req-1", code = "forbidden", status = 403, retryAction = "none" }), HttpStatusCode.Forbidden)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TerminalObservationClient(Options(), true, http);
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => client.ReadResourcesAsync("session-1")); Assert.Equal(403, error.StatusCode); Assert.Equal("forbidden", error.Code); Assert.Equal("none", error.RetryAction); Assert.Equal(1, handler.Calls);
    }
    private static JsonElement Resource(string state, string epoch = "9007199254740993", string? error = null) => JsonSerializer.SerializeToElement(new
    {
        contract = "terminal-observation-v1",
        session = new { sessionContract = "sdk1", sessionId = "session-1" },
        epochStartSeq = epoch,
        state,
        closeRequested = state != "active",
        executionEnded = state != "active" && state != "accepted",
        errorCode = error
    });
    private static TansrClientOptions Options(Func<JsonElement>? scope = null) => new()
    { BaseUri = new("https://serve.test/"), TokenProvider = _ => Task.FromResult("controller-only"), PrincipalProvider = () => "app/user", ExecutionScopeProvider = scope ?? (() => ExecutionFixture.Scope()) };
    private static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(WireJson.CanonicalString(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ++Calls, ct);
    }
}
