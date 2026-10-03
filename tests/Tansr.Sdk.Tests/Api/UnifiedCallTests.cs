using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Tests.Api;

/// <summary>U8-NET: the generic <see cref="TansrClient.CallAsync"/> entry, the three request headers (RFC-UAPI-1 §1.2; Node
/// <c>./api</c> semantics), the one permitted same-request replay, and the A29 capability-intersection replay: every closure
/// operation is called against a fake serve that answers exactly what the declared closure says — <c>enabled</c> → 200,
/// <c>disabled</c> → 403 <c>capability_unavailable</c>, <c>unavailable</c> → 404 <c>capability_unavailable</c> — and the SDK must
/// surface that code unchanged with a single request (no legacy prefix, no alternative operation, no retry). The real facade
/// fences guarded session-scoped writes itself and lets the domain answer the rest; the fake answers every fenced operation the
/// same way because what is under test is the SDK's handling of the answer, not the facade's enforcement point.</summary>
public sealed class UnifiedCallTests
{
    private const string TraceId = "5f2b0e3c9a7d4b1e8c6f0a2d3e4b5c6d";

    /// <summary>facade.ts / client.ts: <c>tansr-closure-id</c> applies to session-scoped write operations only.</summary>
    private static bool ClosureIdApplies(ApiOperation operation) => operation.Kind == "write" && operation.Template.StartsWith("/api/sessions/:id/", StringComparison.Ordinal);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string domain = "session", Action<HttpResponseMessage>? decorate = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        UnifiedStamp.Apply(response, domain); decorate?.Invoke(response);
        return response;
    }

    private static string Facade(string code, int status, string retryAction, string detail, string? requestId = null) =>
        "{\"contract\":\"unified-v1\",\"traceId\":\"" + TraceId + "\",\"requestId\":" + (requestId == null ? "null" : "\"" + requestId + "\"") + ",\"code\":\"" + code + "\",\"status\":" + status +
        ",\"retryAction\":\"" + retryAction + "\",\"message\":\"m\",\"detail\":" + detail + "}";

    private static TansrClientOptions Options() => new()
    { BaseUri = new("https://serve.test/"), TokenProvider = _ => Task.FromResult("token"), ReconnectDelay = TimeSpan.Zero, MaxReconnectAttempts = 1 };

    /// <summary>Only the placeholders the template has (ApiOperation.Path is strict about extras).</summary>
    private static ApiCallOptions Ids(ApiOperation operation, Action<ApiCallOptions>? configure = null)
    {
        var options = new ApiCallOptions();
        foreach (var placeholder in operation.Placeholders)
            switch (placeholder) { case "id": options.Id = "s"; break; case "targetId": options.TargetId = "t"; break; case "uploadId": options.UploadId = "u"; break; case "ticketId": options.TicketId = "k"; break; }
        configure?.Invoke(options);
        return options;
    }

    private static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private static JsonElement GoldenClosure(string name)
    {
        var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "unified-v1.golden.json")));
        var vector = golden.RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);
        return vector.GetProperty("value").Clone();
    }

    // ── A29: declared capability intersection ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryClosureOperationIsAnsweredExactlyAsDeclaredAndCapabilityUnavailableIsNeverDowngraded()
    {
        var closureJson = GoldenClosure("closure-partial-mixed");
        var closure = UnifiedCapabilityClosure.Parse(Encoding.UTF8.GetBytes(closureJson.GetRawText()));
        using var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sessions/s/capabilities") return Json(closureJson.GetRawText(), domain: "discovery", decorate: r => r.Headers.TryAddWithoutValidation(UnifiedHeaders.ClosureId, closure.ClosureId));
            var operation = ApiRoutes.Match(request.Method.Method, path)!;
            // facade.ts: a tansr-closure-id on anything but a session-scoped write is 400 invalid_request — a leaked header fails the replay.
            if (request.Headers.Contains(UnifiedHeaders.ClosureId) && !ClosureIdApplies(operation))
                return Json(Facade("invalid_request", 400, "none", "{\"header\":\"tansr-closure-id\"}"), HttpStatusCode.BadRequest, operation.Domain);
            var state = closure.State(operation);
            if (state == "enabled") return Json("{}", domain: operation.Domain);
            // facade.ts: outside-closure answers are facade-owned (requestId null) with the operation / state / closureId facts.
            int status = closure.ExpectedStatus(operation)!.Value;
            return Json(Facade("capability_unavailable", status, "none", "{\"reason\":\"outside_closure\",\"operation\":\"" + operation.Name + "\",\"state\":\"" + state + "\",\"closureId\":\"" + closure.ClosureId + "\"}"),
                (HttpStatusCode)status, operation.Domain);
        });
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var declared = await client.GetCapabilityClosureAsync("s");
        Assert.Equal(closure.ClosureId, declared.ClosureId);

        int enabled = 0, fenced = 0, streams = 0, guarded = 0;
        foreach (var operation in UnifiedCapabilityClosure.Operations)
        {
            if (operation.Sse) { streams++; continue; } // streams are not CallAsync subjects
            int before = handler.Requests.Count;
            // The declared closure id travels only where the facade accepts it (session-scoped writes); elsewhere the SDK must not send it.
            var options = Ids(operation, o => { if (ClosureIdApplies(operation)) { o.ClosureId = declared.ClosureId; guarded++; } });
            if (operation.Kind == "write") { options.Body = Element("{\"requestId\":\"r-1\"}"); options.IdempotencyKey = "r-1"; }
            if (declared.IsEnabled(operation))
            {
                var result = await client.CallAsync(operation, options);
                Assert.Equal(200, result.Status); enabled++;
            }
            else
            {
                var error = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CallAsync(operation, options));
                Assert.Equal(UnifiedErrorCode.CapabilityUnavailable, error.Code); Assert.Equal(UnifiedRetryAction.None, error.RetryAction);
                Assert.Equal(declared.ExpectedStatus(operation), error.StatusCode); Assert.True(error.FacadeOwned);
                Assert.Equal(UnifiedErrorReason.OutsideClosure, error.Reason); Assert.Equal(operation.Name, error.Detail.Operation); Assert.Equal(declared.State(operation), error.Detail.State);
                Assert.Equal(declared.ClosureId, error.ClosureId);
                // D19 bridge: no family translated this, so the family face equals the unified face — nothing to "downgrade" to.
                Assert.Equal(error.Code, error.DomainCode); Assert.Equal(error.StatusCode, error.DomainStatus);
                Assert.False(UnifiedRetry.Advice(error).Replayable);
                fenced++;
            }
            // Exactly one request, on the operation's own /api template, carrying the declared closure id where it applies: no fallback of any kind.
            Assert.Equal(before + 1, handler.Requests.Count);
            var sent = handler.Requests[^1];
            Assert.StartsWith("/api/", sent.RequestUri!.AbsolutePath); Assert.Same(operation, ApiRoutes.Match(sent.Method.Method, sent.RequestUri.AbsolutePath));
            if (ClosureIdApplies(operation)) Assert.Equal(declared.ClosureId, sent.Headers.GetValues(UnifiedHeaders.ClosureId).Single());
            else Assert.False(sent.Headers.Contains(UnifiedHeaders.ClosureId));
        }
        Assert.Equal(UnifiedCapabilityClosure.Operations.Count, enabled + fenced + streams); Assert.Equal(4, streams);
        Assert.Equal(declared.Enabled().Count(o => !o.Sse), enabled); Assert.Equal(declared.Fenced().Count(o => !o.Sse), fenced);
        Assert.True(fenced > 0 && enabled > 0 && guarded > 0);
        Assert.Equal(UnifiedCapabilityClosure.Operations.Count(o => !o.Sse && ClosureIdApplies(o)), guarded);
    }

    [Fact]
    public async Task ClosureIdIsRejectedLocallyOutsideSessionScopedWrites()
    {
        using var handler = new Handler(_ => Json("{}"));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var id = new string('a', 64);
        foreach (var operation in new[] { ApiRoutes.SessionGet, ApiRoutes.SessionCreate, ApiRoutes.All.First(o => o.Kind == "write" && o.Domain == "cache") })
        {
            var error = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(operation, Ids(operation, o => o.ClosureId = id)));
            Assert.Equal("closure_id_not_applicable", error.Code);
        }
        var malformed = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(ApiRoutes.SessionMessageSend, Ids(ApiRoutes.SessionMessageSend, o => o.ClosureId = "ABC")));
        Assert.Equal("invalid_closure_id", malformed.Code);
        Assert.Empty(handler.Requests);
        await client.CallAsync(ApiRoutes.SessionMessageSend, Ids(ApiRoutes.SessionMessageSend, o => { o.ClosureId = id; o.Body = Element("{}"); }));
        Assert.Equal(id, handler.Requests.Single().Headers.GetValues(UnifiedHeaders.ClosureId).Single());
    }

    [Fact]
    public async Task IdempotencyKeyOfReadsTheBodyAtTheFamilyRequestIdPathOnly()
    {
        var sdk2 = ApiRoutes.CacheBindingRenew;                                   // sdk2-cache-v1 → request.requestId
        var terminal = ApiRoutes.TerminalConfigurationCommit;                      // terminal-services-v1 → requestId
        var session = ApiRoutes.SessionCreate;                                     // agent-session-v1 → no body position (header only)
        var sync = ApiRoutes.All.First(o => o.Family == "archive-sync-v1" && o.Kind == "write"); // archive-sync-v1 → header only
        Assert.Equal(new[] { "request", "requestId" }, ApiRoutes.FamilyRequestIdPath("sdk2-cache-v1")); Assert.Equal(new[] { "requestId" }, ApiRoutes.FamilyRequestIdPath("terminal-services-v1"));
        Assert.Null(ApiRoutes.FamilyRequestIdPath("agent-session-v1")); Assert.Null(ApiRoutes.FamilyRequestIdPath("archive-sync-v1"));

        Assert.Equal("c-1", UnifiedRetry.IdempotencyKeyOf(sdk2, new ApiCallOptions { Body = Element("{\"request\":{\"requestId\":\"c-1\"}}") }));
        Assert.Null(UnifiedRetry.IdempotencyKeyOf(sdk2, new ApiCallOptions { Body = Element("{\"requestId\":\"c-1\"}") }));       // wrong position for this family
        Assert.Equal("t-1", UnifiedRetry.IdempotencyKeyOf(terminal, new ApiCallOptions { Body = Element("{\"requestId\":\"t-1\"}") }));
        Assert.Null(UnifiedRetry.IdempotencyKeyOf(terminal, new ApiCallOptions { Body = Element("{\"request\":{\"requestId\":\"t-1\"}}") }));
        Assert.Null(UnifiedRetry.IdempotencyKeyOf(session, new ApiCallOptions { Body = Element("{\"requestId\":\"s-1\",\"request\":{\"requestId\":\"s-1\"}}") }));
        Assert.Null(UnifiedRetry.IdempotencyKeyOf(sync, new ApiCallOptions { Body = Element("{\"requestId\":\"a-1\"}") }));
        Assert.Equal("h-1", UnifiedRetry.IdempotencyKeyOf(session, new ApiCallOptions { IdempotencyKey = "h-1" }));                 // header wins everywhere
        Assert.Equal("h-1", UnifiedRetry.IdempotencyKeyOf(sdk2, new ApiCallOptions { IdempotencyKey = "h-1", Body = Element("{\"request\":{\"requestId\":\"c-1\"}}") }));
        Assert.Null(UnifiedRetry.IdempotencyKeyOf(sdk2, new ApiCallOptions { Body = Element("{\"request\":{\"requestId\":\"\"}}") }));   // empty string is no key
        Assert.Null(UnifiedRetry.IdempotencyKeyOf(sdk2, new ApiCallOptions { Body = Element("{\"request\":[\"requestId\"]}") }));
        Assert.Null(UnifiedRetry.IdempotencyKeyOf(terminal, new ApiCallOptions { RawBody = Encoding.UTF8.GetBytes("{\"requestId\":\"t-1\"}") }));

        // Replay: a key at a position the family does not read is not a key — retry_key_missing, nothing sent.
        using var handler = new Handler(_ => Json(Facade("capacity_exceeded", 429, "same-request", "{}", "c-1"), HttpStatusCode.TooManyRequests, "cache", r => r.Headers.TryAddWithoutValidation("retry-after", "1")));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var options = new ApiCallOptions { Id = "b", Body = Element("{\"requestId\":\"c-1\"}") };
        var busy = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CallAsync(sdk2, options));
        Assert.True(UnifiedRetry.Advice(busy).Replayable);
        var missing = await Assert.ThrowsAsync<TansrProtocolException>(() => UnifiedRetry.RetrySameRequestAsync(client, sdk2, options, busy));
        Assert.Equal("retry_key_missing", missing.Code); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingContractHeaderOnACallIsContractUnavailableWithZeroFallbackRequests()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var error = await Assert.ThrowsAsync<ContractUnavailableException>(() => client.CallAsync(ApiRoutes.SessionList, new ApiCallOptions()));
        Assert.Equal("missing_contract_header", error.Reason); Assert.Single(handler.Requests);
        var closureError = await Assert.ThrowsAsync<ContractUnavailableException>(() => client.GetCapabilityClosureAsync("s"));
        Assert.Equal("missing_contract_header", closureError.Reason); Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ClosureBodyWhoseIdDoesNotRederiveOrDisagreesWithTheHeaderIsRejected()
    {
        var closure = JsonNode.Parse(GoldenClosure("closure-full-enabled").GetRawText())!.AsObject();
        var id = closure["closureId"]!.GetValue<string>();
        closure["operations"]!["session.close"] = "disabled"; // content changed, id stale
        using var handler = new Handler(request => request.RequestUri!.Query.Contains("header")
            ? Json(GoldenClosure("closure-full-enabled").GetRawText(), domain: "discovery", decorate: r => r.Headers.TryAddWithoutValidation(UnifiedHeaders.ClosureId, new string('a', 64)))
            : Json(closure.ToJsonString(), domain: "discovery"));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var stale = await Assert.ThrowsAsync<TansrProtocolException>(() => client.GetCapabilityClosureAsync("s"));
        Assert.Equal("invalid_closure", stale.Code);
        var error = Record.Exception(() => UnifiedCapabilityClosure.Parse(Encoding.UTF8.GetBytes(closure.ToJsonString())));
        Assert.True(error is TansrProtocolException { Code: "invalid_closure" });
        Assert.Equal(id, UnifiedCapabilityClosure.Parse(Encoding.UTF8.GetBytes(GoldenClosure("closure-full-enabled").GetRawText())).ClosureId);
    }

    // ── three headers ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThreeHeadersAreEmittedVerbatimAndOnlyWhereTheOperationAcceptsThem()
    {
        using var handler = new Handler(_ => Json("{}", domain: "terminal"));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var operation = ApiRoutes.All.First(o => o.AcceptsIfMatch && !o.Sse);
        var deadline = new DateTimeOffset(2030, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);
        var options = Ids(operation, o => { o.Body = Element("{\"requestId\":\"k-1\"}"); o.IdempotencyKey = "k-1"; o.IfMatch = "7"; o.Deadline = deadline; });
        await client.CallAsync(operation, options);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("k-1", sent.Headers.GetValues(UnifiedRequestHeaders.IdempotencyKey).Single());
        Assert.Equal("\"7\"", sent.Headers.GetValues(UnifiedRequestHeaders.IfMatch).Single()); // bare decimal normalised to the strong form
        Assert.Equal("2030-01-02T03:04:05.678Z", sent.Headers.GetValues(UnifiedRequestHeaders.Deadline).Single());
        Assert.Equal("{\"requestId\":\"k-1\"}", handler.Bodies[0]); // strict family: canonical control encoding of the same object

        // A read never carries an idempotency key; a write without an expectedRevision position never carries If-Match.
        var read = ApiRoutes.All.First(o => o.Kind == "read" && !o.Sse);
        var key = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(read, Ids(read, o => o.IdempotencyKey = "x")));
        Assert.Equal("invalid_idempotency_key", key.Code);
        var plainWrite = ApiRoutes.All.First(o => o.Kind == "write" && !o.AcceptsIfMatch);
        var ifMatch = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(plainWrite, Ids(plainWrite, o => o.IfMatch = "\"1\"")));
        Assert.Equal("if_match_not_applicable", ifMatch.Code);
        Assert.Single(handler.Requests); // local rejections send nothing
    }

    [Theory]
    [InlineData("", "invalid_idempotency_key")]
    [InlineData("has space", "invalid_idempotency_key")]
    [InlineData("é", "invalid_idempotency_key")]
    public async Task IdempotencyKeyFormIsValidatedLocally(string key, string code)
    {
        using var handler = new Handler(_ => Json("{}"));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(ApiRoutes.SessionCreate, new ApiCallOptions { IdempotencyKey = key }));
        Assert.Equal(code, error.Code); Assert.Empty(handler.Requests);
        var tooLong = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(ApiRoutes.SessionCreate, new ApiCallOptions { IdempotencyKey = new string('k', 129) }));
        Assert.Equal("invalid_idempotency_key", tooLong.Code);
    }

    [Theory]
    [InlineData("W/\"7\"")]
    [InlineData("\"07\"")]
    [InlineData("\"\"")]
    [InlineData("abc")]
    public async Task IfMatchMustBeTheStrongDecimalValidator(string value)
    {
        using var handler = new Handler(_ => Json("{}"));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var operation = ApiRoutes.All.First(o => o.AcceptsIfMatch && !o.Sse);
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(operation, Ids(operation, o => o.IfMatch = value)));
        Assert.Equal("invalid_if_match", error.Code); Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExpiredDeadlineIsRejectedBeforeSendingAndAReplayNeverExtendsIt()
    {
        using var handler = new Handler(_ => Json(Facade("capacity_exceeded", 429, "same-request", "{}", "k-1"), HttpStatusCode.TooManyRequests, decorate: r => r.Headers.TryAddWithoutValidation("retry-after", "1")));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var past = new ApiCallOptions { Body = Element("{\"requestId\":\"k-1\"}"), IdempotencyKey = "k-1", Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) };
        var expired = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(ApiRoutes.SessionCreate, past));
        Assert.Equal("deadline_exceeded", expired.Code); Assert.Empty(handler.Requests);

        // Deadline 200 ms ahead, server says wait 1 s: the replay would cross the deadline, so it is refused locally — not extended.
        var soon = new ApiCallOptions { Body = Element("{\"requestId\":\"k-1\"}"), IdempotencyKey = "k-1", Deadline = DateTimeOffset.UtcNow.AddMilliseconds(200) };
        var busy = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CallAsync(ApiRoutes.SessionCreate, soon));
        Assert.Equal(UnifiedErrorCode.CapacityExceeded, busy.Code); Assert.Equal(1000, busy.RetryAfterMs); Assert.True(UnifiedRetry.Advice(busy).Replayable);
        var refused = await Assert.ThrowsAsync<TansrProtocolException>(() => UnifiedRetry.RetrySameRequestAsync(client, ApiRoutes.SessionCreate, soon, busy));
        Assert.Equal("deadline_exceeded", refused.Code); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SameRequestReplayReusesTheSameKeyAndBytesAndIsRefusedWithoutAKeyOrOutsideTheBudget()
    {
        int calls = 0;
        using var handler = new Handler(_ => ++calls == 1
            ? Json(Facade("capacity_exceeded", 429, "same-request", "{}", "k-1"), HttpStatusCode.TooManyRequests, decorate: r => r.Headers.TryAddWithoutValidation("retry-after", "1"))
            : Json("{\"ok\":true}", decorate: r => r.Headers.TryAddWithoutValidation("x-request-id", "k-1")));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var options = new ApiCallOptions { Body = Element("{\"requestId\":\"k-1\",\"n\":1}"), IdempotencyKey = "k-1" };
        var busy = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CallAsync(ApiRoutes.SessionCreate, options));
        var result = await UnifiedRetry.RetrySameRequestAsync(client, ApiRoutes.SessionCreate, options, busy);
        Assert.Equal(200, result.Status); Assert.Equal("k-1", result.Meta.RequestId); Assert.True(result.Body!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Headers.GetValues(UnifiedRequestHeaders.IdempotencyKey).Single(), handler.Requests[1].Headers.GetValues(UnifiedRequestHeaders.IdempotencyKey).Single());
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);

        // Without a key the replay is refused (the server could not recognise it as the same request).
        var keyless = new ApiCallOptions { Body = Element("{\"n\":1}") };
        var missing = await Assert.ThrowsAsync<TansrProtocolException>(() => UnifiedRetry.RetrySameRequestAsync(client, ApiRoutes.SessionCreate, keyless, busy));
        Assert.Equal("retry_key_missing", missing.Code);
        // A stated wait beyond the budget is refused; a non-replayable error is refused.
        var longWait = new UnifiedApiException(429, UnifiedErrorCode.CapacityExceeded, UnifiedRetryAction.SameRequest, 31_000, TraceId, "k-1", null, busy.Meta, false);
        var budget = await Assert.ThrowsAsync<TansrProtocolException>(() => UnifiedRetry.RetrySameRequestAsync(client, ApiRoutes.SessionCreate, options, longWait));
        Assert.Equal("retry_after_exceeds_budget", budget.Code);
        var notRetryable = await Assert.ThrowsAsync<TansrProtocolException>(() => UnifiedRetry.RetrySameRequestAsync(client, ApiRoutes.SessionCreate, options, new InvalidOperationException()));
        Assert.Equal("not_retryable", notRetryable.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ResultUnknownIsNeverReplayedAsTheSameRequestButRoutedToQueryStatus()
    {
        // The wire itself forbids result_unknown + same-request (golden unified-error-result-unknown-replay); the stated action is query-status.
        using var handler = new Handler(_ => Json(Facade("result_unknown", 503, "query-status", "{\"domain\":\"cache\",\"family\":\"sdk2-cache-v1\",\"domainCode\":\"commit_unknown\",\"domainStatus\":503,\"domainRetryAction\":\"reconcile\"}", "op-1"),
            HttpStatusCode.ServiceUnavailable, "cache", r => r.Headers.TryAddWithoutValidation("retry-after", "1")));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var options = new ApiCallOptions { Id = "b", Body = Element("{\"request\":{\"requestId\":\"op-1\"}}"), IdempotencyKey = "op-1" };
        var error = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CallAsync(ApiRoutes.CacheBindingRenew, options));
        var advice = UnifiedRetry.Advice(error);
        Assert.Equal(UnifiedRetryAction.QueryStatus, advice.Action); Assert.False(advice.Replayable); Assert.Equal("commit_unknown", error.Detail.DomainCode);
        var refused = await Assert.ThrowsAsync<TansrProtocolException>(() => UnifiedRetry.RetrySameRequestAsync(client, ApiRoutes.CacheBindingRenew, options, error));
        Assert.Equal("not_retryable", refused.Code); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ETagIsReadOnlyInTheStrongFormAndRoundTripsIntoIfMatch()
    {
        var operation = ApiRoutes.All.First(o => o.AcceptsIfMatch && !o.Sse);
        var read = ApiRoutes.All.First(o => o.EtagPath != null && o.Kind == "read" && !o.Sse);
        int reads = 0;
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{}", domain: read.Domain, decorate: r => r.Headers.TryAddWithoutValidation("ETag", ++reads == 1 ? "\"3\"" : "W/\"3\""))
            : request.Headers.GetValues(UnifiedRequestHeaders.IfMatch).Single() == "\"3\"" ? Json("{}", domain: operation.Domain)
            : Json(Facade("precondition_failed", 412, "refresh", "{\"reason\":\"if_match_stale\"}", "k-1"), HttpStatusCode.PreconditionFailed, operation.Domain));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var current = await client.CallAsync(read, Ids(read));
        Assert.Equal("\"3\"", current.ETag); Assert.Equal("3", UnifiedRequestHeaders.RevisionOf(current.ETag!));
        var weak = await client.CallAsync(read, Ids(read));
        Assert.Null(weak.ETag);
        var ok = await client.CallAsync(operation, Ids(operation, o => { o.Body = Element("{\"requestId\":\"k-1\"}"); o.IdempotencyKey = "k-1"; o.IfMatch = current.ETag; }));
        Assert.Equal(200, ok.Status);
        var stale = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CallAsync(operation, Ids(operation, o => { o.Body = Element("{\"requestId\":\"k-1\"}"); o.IdempotencyKey = "k-1"; o.IfMatch = "\"2\""; })));
        Assert.Equal(UnifiedErrorCode.PreconditionFailed, stale.Code); Assert.Equal(UnifiedRetryAction.Refresh, stale.RetryAction); Assert.True(stale.RequiresRefresh); Assert.False(stale.RequiresRediscovery);
        Assert.Equal(UnifiedErrorReason.IfMatchStale, stale.Reason); Assert.False(stale.FacadeOwned); Assert.Equal(UnifiedRetryAction.Refresh, UnifiedRetry.Advice(stale).Action);
    }

    [Fact]
    public async Task ReusedIdempotencyKeyAndExpiredDeadlineAnswersDecodeAsTheirUnifiedCodes()
    {
        using var handler = new Handler(request => request.Headers.Contains(UnifiedRequestHeaders.Deadline)
            ? Json(Facade("invalid_request", 408, "none", "{\"reason\":\"deadline_exceeded\",\"header\":\"deadline\"}", "k-1"), HttpStatusCode.RequestTimeout)
            : Json(Facade("conflict", 409, "none", "{\"reason\":\"idempotency_key_reused\",\"header\":\"idempotency-key\"}", "k-1"), HttpStatusCode.Conflict));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var reused = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CallAsync(ApiRoutes.SessionCreate, new ApiCallOptions { Body = Element("{\"requestId\":\"k-1\"}"), IdempotencyKey = "k-1" }));
        Assert.Equal(UnifiedErrorCode.Conflict, reused.Code); Assert.Equal(UnifiedErrorReason.IdempotencyKeyReused, reused.Reason); Assert.Equal("idempotency-key", reused.Header);
        Assert.False(reused.FacadeOwned); Assert.False(UnifiedRetry.Advice(reused).Replayable);
        var late = await Assert.ThrowsAsync<UnifiedApiException>(() => client.CallAsync(ApiRoutes.SessionCreate, new ApiCallOptions { Body = Element("{\"requestId\":\"k-1\"}"), IdempotencyKey = "k-1", Deadline = DateTimeOffset.UtcNow.AddMinutes(1) }));
        Assert.Equal(UnifiedErrorCode.InvalidRequest, late.Code); Assert.Equal(408, late.StatusCode); Assert.Equal(UnifiedErrorReason.DeadlineExceeded, late.Reason);
        Assert.Equal(UnifiedRetryAction.None, late.RetryAction); Assert.False(UnifiedRetry.Advice(late).Replayable);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CallRejectsStreamsHandBuiltPathsAndMixedBodies()
    {
        using var handler = new Handler(_ => Json("{}"));
        using var http = new HttpClient(handler, false); using var client = new TansrClient(Options(), http);
        var sse = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(ApiRoutes.SessionEventsObserve, new ApiCallOptions { Id = "s" }));
        Assert.Equal("invalid_operation", sse.Code);
        var mixed = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CallAsync(ApiRoutes.SessionCreate, new ApiCallOptions { Body = Element("{}"), RawBody = new byte[] { 1 } }));
        Assert.Equal("invalid_body", mixed.Code);
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionGet.Path()); // placeholder missing: no hand-built URL can leak out
        Assert.Empty(handler.Requests);
        var list = await client.CallAsync(ApiRoutes.SessionList, new ApiCallOptions { Query = [new("limit", "5")] });
        Assert.Equal("/api/sessions", handler.Requests.Single().RequestUri!.AbsolutePath); Assert.Equal("?limit=5", handler.Requests.Single().RequestUri!.Query);
        Assert.Equal(200, list.Status); Assert.Equal(7, list.Meta.ManifestRevision);
    }

    [Fact]
    public void DeadlineFormattingIsRfc3339UtcMilliseconds()
    {
        var at = new DateTimeOffset(2026, 10, 3, 8, 0, 0, 5, TimeSpan.FromHours(8));
        Assert.Equal("2026-10-03T00:00:00.005Z", UnifiedRequestHeaders.FormatDeadline(at));
        Assert.Equal(at, UnifiedRequestHeaders.ParseDeadline("2026-10-03T00:00:00.005Z"));
        Assert.Null(UnifiedRequestHeaders.ParseDeadline("2026-10-03 00:00:00"));
        Assert.Equal(DateTimeKind.Utc, UnifiedRequestHeaders.ParseDeadline("2026-10-03T00:00:00Z")!.Value.UtcDateTime.Kind);
        Assert.Equal("7", UnifiedRequestHeaders.RevisionOf("\"7\"")); Assert.Null(UnifiedRequestHeaders.RevisionOf("W/\"7\""));
        Assert.Equal("\"7\"", UnifiedRequestHeaders.NormalizeIfMatch("7")); Assert.Equal("\"7\"", UnifiedRequestHeaders.NormalizeIfMatch("\"7\""));
        Assert.True(UnifiedRequestHeaders.IsIdempotencyKey(new string('!', 128))); Assert.False(UnifiedRequestHeaders.IsIdempotencyKey(new string('!', 129)));
        Assert.Equal("7", 7.ToString(CultureInfo.InvariantCulture));
    }
}
