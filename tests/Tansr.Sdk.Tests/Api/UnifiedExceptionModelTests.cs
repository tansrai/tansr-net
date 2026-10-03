using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Cache;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Tests.Api;

/// <summary>D19 (U8-NET): the public face of every <c>/api</c> failure is the unified code + <c>retryAction</c>; the family code is
/// a detail (<c>Detail.DomainCode</c>) and the former <c>Domain*</c> reads are obsolete read-only bridges that fall back to the unified
/// values. The 19 codes, 6 retry actions and 17 reasons are pinned to the vendored schema so a vocabulary drift fails here, not at a catch site.</summary>
public sealed class UnifiedExceptionModelTests
{
    private const string TraceId = "5f2b0e3c9a7d4b1e8c6f0a2d3e4b5c6d";

    [Fact]
    public void DomainBridgeIsAnObsoleteReadOnlyBridgeAndNotRedeclaredOnTheUnifiedException()
    {
        foreach (var name in new[] { "DomainCode", "DomainStatus", "DomainRetryAction" })
        {
            var bridge = typeof(TansrHttpException).GetProperty(name);
            Assert.NotNull(bridge); Assert.False(bridge!.CanWrite); Assert.False(bridge.GetMethod!.IsVirtual);
            Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<ObsoleteAttribute>(bridge));
            Assert.Null(typeof(UnifiedApiException).GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly));
        }
        // The primary face carries no obsolete mark.
        foreach (var name in new[] { "Code", "StatusCode", "RetryAction", "Detail" })
            Assert.Null(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<ObsoleteAttribute>(typeof(UnifiedApiException).GetProperty(name)!));
    }
    private static readonly JsonDocument Schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "unified-v1.schema.json")));

    private static string[] Enum(string definition) =>
        Schema.RootElement.GetProperty("definitions").GetProperty(definition).GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static UnifiedApiException Decode(string body, int status, string domain = "session")
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        UnifiedStamp.Apply(response, domain);
        using var json = JsonDocument.Parse(body);
        return Assert.Throws<UnifiedApiException>(() => UnifiedErrorEnvelope.TryThrow(response, json.RootElement, UnifiedResponseMeta.Read(response)));
    }

    private static string Envelope(string code, int status, string retryAction, string? requestId = "r-1", string? detail = null, int? retryAfterMs = null) =>
        "{\"contract\":\"unified-v1\",\"traceId\":\"" + TraceId + "\",\"requestId\":" + (requestId == null ? "null" : "\"" + requestId + "\"") + ",\"code\":\"" + code + "\",\"status\":" + status +
        ",\"retryAction\":\"" + retryAction + "\"" + (retryAfterMs == null ? "" : ",\"retryAfterMs\":" + retryAfterMs) + ",\"message\":\"m\"" + (detail == null ? "" : ",\"detail\":" + detail) + "}";

    [Fact]
    public void VocabulariesArePinnedToTheVendoredSchema()
    {
        Assert.Equal(Enum("UnifiedCode"), UnifiedErrorEnvelope.Codes); Assert.Equal(19, UnifiedErrorEnvelope.Codes.Count);
        Assert.Equal(Enum("RetryAction"), UnifiedErrorEnvelope.RetryActions); Assert.Equal(6, UnifiedErrorEnvelope.RetryActions.Count);
        Assert.Equal(Enum("DomainRetryAction"), UnifiedErrorEnvelope.DomainRetryActions);
        Assert.Equal(Enum("FacadeCode"), UnifiedErrorEnvelope.FacadeCodes); Assert.Equal(8, UnifiedErrorEnvelope.FacadeCodes.Count);
        var reasons = Schema.RootElement.GetProperty("definitions").GetProperty("UnifiedErrorDetail").GetProperty("properties").GetProperty("reason").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(reasons, UnifiedErrorReason.All); Assert.Equal(17, UnifiedErrorReason.All.Count);
        foreach (var reason in reasons) Assert.True(UnifiedErrorReason.Contains(reason));
        Assert.False(UnifiedErrorReason.Contains("closure_stale"));
        // Request-header reasons (the 15 three-header causes) vs fence reasons (2).
        Assert.Equal(15, reasons.Count(UnifiedErrorReason.IsRequestHeaderReason));
        Assert.False(UnifiedErrorReason.IsRequestHeaderReason(UnifiedErrorReason.NotInstalled)); Assert.False(UnifiedErrorReason.IsRequestHeaderReason(UnifiedErrorReason.OutsideClosure));
        // Every family retry word maps to a unified action.
        foreach (var word in UnifiedErrorEnvelope.DomainRetryActions) Assert.Contains(UnifiedRetry.DomainRetryActionMap[word], UnifiedErrorEnvelope.RetryActions);
    }

    /// <summary>Every unified code decodes with the status set the schema binds it to; a status outside the set is not a business error
    /// but <c>contract_unavailable</c>. The expectations are read from the schema's <c>allOf</c> rules, not from the decoder.</summary>
    public static IEnumerable<object[]> CodeBindings()
    {
        // The schema states one status rule per code and, for three codes, a separate retryAction rule; merge them per code.
        var statuses = new Dictionary<string, int[]>(StringComparer.Ordinal); var actions = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var rule in Schema.RootElement.GetProperty("definitions").GetProperty("UnifiedError").GetProperty("allOf").EnumerateArray())
        {
            if (!rule.TryGetProperty("if", out var when) || !when.GetProperty("properties").TryGetProperty("code", out var codeRule) || !codeRule.TryGetProperty("const", out var code)) continue;
            var then = rule.GetProperty("then").GetProperty("properties");
            if (then.TryGetProperty("status", out var statusRule))
                statuses[code.GetString()!] = statusRule.TryGetProperty("enum", out var set) ? set.EnumerateArray().Select(s => s.GetInt32()).ToArray() : new[] { statusRule.GetProperty("const").GetInt32() };
            if (then.TryGetProperty("retryAction", out var retryRule))
                actions[code.GetString()!] = retryRule.TryGetProperty("enum", out var acts) ? acts.EnumerateArray().Select(s => s.GetString()!).ToArray() : new[] { retryRule.GetProperty("const").GetString()! };
        }
        Assert.Equal(19, statuses.Count);
        foreach (var code in UnifiedErrorEnvelope.Codes)
            yield return new object[] { code, statuses[code], actions.TryGetValue(code, out var bound) ? bound : UnifiedErrorEnvelope.RetryActions.ToArray() };
    }

    [Theory]
    [MemberData(nameof(CodeBindings))]
    public void EachUnifiedCodeDecodesWithItsBoundStatusesAndRetryActions(string code, int[] statuses, string[] actions)
    {
        Assert.NotEmpty(statuses); Assert.Equal(statuses.OrderBy(s => s), UnifiedErrorEnvelope.StatusesFor(code).OrderBy(s => s));
        foreach (var status in statuses)
        {
            var action = actions[0];
            string? detail = code == "precondition_failed" && action == "rediscover" ? "{\"domainCode\":\"closure_stale\",\"closureId\":\"" + new string('a', 64) + "\"}" : null;
            string? requestId = code == "precondition_failed" && action == "rediscover" ? null : "r-1";
            var error = Decode(Envelope(code, status, action, requestId, detail), status);
            Assert.Equal(code, error.Code); Assert.Equal(status, error.StatusCode); Assert.Equal(action, error.RetryAction);
            Assert.Equal(TraceId, error.TraceId); Assert.Equal(requestId, error.RequestId);
            // The (obsolete) bridge reads fall back to the unified values when no family fact was translated.
#pragma warning disable CS0618
            Assert.Equal(detail == null ? code : "closure_stale", error.DomainCode); Assert.Equal(status, error.DomainStatus); Assert.Equal(action, error.DomainRetryAction);
#pragma warning restore CS0618
            Assert.Equal(detail == null, error.Detail.IsEmpty || error.Detail.DomainCode == null);
        }
        // A retry action the code does not allow is a malformed envelope, never a misread business error.
        foreach (var forbidden in UnifiedErrorEnvelope.RetryActions.Except(actions))
        {
            var response = new HttpResponseMessage((HttpStatusCode)statuses[0]) { Content = new ByteArrayContent(Array.Empty<byte>()) }; UnifiedStamp.Apply(response);
            using var json = JsonDocument.Parse(Envelope(code, statuses[0], forbidden));
            var rejected = Assert.Throws<ContractUnavailableException>(() => UnifiedErrorEnvelope.TryThrow(response, json.RootElement, UnifiedResponseMeta.Read(response)));
            Assert.Equal("invalid_error_body", rejected.Reason);
        }
    }

    [Theory]
    [InlineData("none", false, false, "none")]
    [InlineData("same-request", false, false, "same-request")]
    [InlineData("query-status", false, false, "query-status")]
    [InlineData("rebind", false, false, "rebind")]
    [InlineData("refresh", false, true, "refresh")]
    [InlineData("rediscover", true, false, "rediscover")]
    public void RetryActionDrivesTheFollowUpPredicatesAndAdvice(string action, bool rediscover, bool refresh, string advised)
    {
        var (code, status) = action switch { "rediscover" => ("precondition_failed", 412), "refresh" => ("precondition_failed", 412), "rebind" => ("stale_generation", 409), "query-status" => ("result_unknown", 503), "same-request" => ("capacity_exceeded", 429), _ => ("forbidden", 403) };
        var detail = action == "rediscover" ? "{\"domainCode\":\"closure_stale\",\"closureId\":\"" + new string('b', 64) + "\"}" : action == "refresh" ? "{\"reason\":\"if_match_stale\"}" : null;
        var error = Decode(Envelope(code, status, action, action == "rediscover" ? null : "r-1", detail, action == "same-request" ? 250 : null), status);
        Assert.Equal(rediscover, error.RequiresRediscovery); Assert.Equal(refresh, error.RequiresRefresh);
        var advice = UnifiedRetry.Advice(error);
        Assert.Equal(advised, advice.Action); Assert.True(advice.Stated); Assert.Equal("unified", advice.Source);
        Assert.Equal(action == "same-request", advice.Replayable);
        Assert.Equal(action == "rediscover" ? new string('b', 64) : null, advice.ClosureId);
        Assert.Equal(action == "same-request" ? 250 : (int?)null, advice.RetryAfterMs);
        Assert.Equal(advice.Action, error.Advice.Action);
    }

    [Fact]
    public void FamilyFactsLiveInDetailAndTheUnifiedCodeStaysPrimary()
    {
        var error = Decode(Envelope("conflict", 409, "refresh", "cfg-1",
            "{\"domain\":\"terminal\",\"family\":\"terminal-services-v1\",\"domainCode\":\"revision_conflict\",\"domainStatus\":409,\"domainRetryAction\":\"refresh\"}"), 409, "terminal");
        Assert.Equal(UnifiedErrorCode.Conflict, error.Code); Assert.Equal(UnifiedRetryAction.Refresh, error.RetryAction);
        Assert.Equal("revision_conflict", error.Detail.DomainCode); Assert.Equal(409, error.Detail.DomainStatus); Assert.Equal("refresh", error.Detail.DomainRetryAction);
        Assert.Equal("terminal", error.Detail.Domain); Assert.Equal("terminal-services-v1", error.Family);
#pragma warning disable CS0618
        Assert.Equal(error.Detail.DomainCode, error.DomainCode); Assert.False(error.FacadeOwned);
#pragma warning restore CS0618
        // Consumption rule: switch on Code first, refine on Detail.DomainCode.
        var handled = error.Code switch { UnifiedErrorCode.Conflict when error.Detail.DomainCode == "revision_conflict" => "refresh-config", UnifiedErrorCode.Conflict => "generic", _ => "other" };
        Assert.Equal("refresh-config", handled);
        // A family that reports no retry position: domainRetryAction null on the wire ⇒ bridge reads null, unified action still primary.
        var silent = Decode(Envelope("gone", 410, "none", "s-1", "{\"domain\":\"session\",\"family\":\"agent-session-v1\",\"domainCode\":\"session_ended\",\"domainStatus\":410,\"domainRetryAction\":null}"), 410);
#pragma warning disable CS0618
        Assert.True(silent.Detail.HasDomainRetryAction); Assert.Null(silent.DomainRetryAction); Assert.Equal(UnifiedRetryAction.None, silent.RetryAction);
#pragma warning restore CS0618
        Assert.Equal("none", UnifiedRetry.Advice(silent).Action);
    }

    [Fact]
    public void ResultUnknownNeverBecomesAReplayEvenWhenStatedByAFamilyWord()
    {
        var unified = Decode(Envelope("result_unknown", 503, "query-status", "op-1", "{\"domain\":\"cache\",\"family\":\"sdk2-cache-v1\",\"domainCode\":\"commit_unknown\",\"domainStatus\":503,\"domainRetryAction\":\"reconcile\"}"), 503, "cache");
        Assert.Equal(UnifiedRetryAction.QueryStatus, UnifiedRetry.Advice(unified).Action); Assert.False(UnifiedRetry.Advice(unified).Replayable);
        // Family passthrough (no unified envelope): the family word is mapped, but an unknown-outcome code is never replayed.
        var family = new TansrHttpException(503, "commit_unknown", retryAction: "backoff", retryAfterMs: 100);
        var advice = UnifiedRetry.Advice(family);
        Assert.Equal("domain", advice.Source); Assert.True(advice.Stated); Assert.Equal(UnifiedRetryAction.QueryStatus, advice.Action); Assert.False(advice.Replayable);
        Assert.Equal(UnifiedRetryAction.SameRequest, UnifiedRetry.Advice(new TansrHttpException(503, "busy", retryAction: "backoff")).Action);
        Assert.True(UnifiedRetry.Advice(new TansrHttpException(503, "busy", retryAction: "backoff")).Replayable);
        var unstated = UnifiedRetry.Advice(new TansrHttpException(409, "conflict"));
        Assert.False(unstated.Stated); Assert.False(unstated.Replayable); Assert.Equal(UnifiedRetryAction.None, unstated.Action);
        Assert.Same(UnifiedRetryAdvice.None, UnifiedRetry.Advice(new TansrProtocolException("invalid_request")));
        Assert.Same(UnifiedRetryAdvice.None, UnifiedRetry.Advice(new InvalidOperationException()));
    }

    [Fact]
    public void FacadeOwnedClassificationFollowsRevisionSeven()
    {
        // Request-header rejections carry a requestId and a header reason: UnifiedError, never facade-owned, even with a facade code.
        var reused = Decode(Envelope("conflict", 409, "none", "idem-7", "{\"reason\":\"idempotency_key_reused\",\"header\":\"idempotency-key\"}"), 409);
        Assert.False(reused.FacadeOwned); Assert.Equal("idempotency-key", reused.Header); Assert.Equal(UnifiedErrorReason.IdempotencyKeyReused, reused.Reason);
        var tooBig = Decode(Envelope("payload_too_large", 413, "none", "k-1", "{\"reason\":\"header_body_limit\",\"limitBytes\":262144}"), 413);
        Assert.Equal(262144, tooBig.Detail.LimitBytes); Assert.Equal(UnifiedErrorReason.HeaderBodyLimit, tooBig.Reason);
        // Facade-owned: requestId null, facade code, no family status.
        var fenced = Decode(Envelope("capability_unavailable", 403, "none", null, "{\"reason\":\"outside_closure\",\"operation\":\"archive.ack.commit\",\"state\":\"disabled\",\"closureId\":\"" + new string('c', 64) + "\"}"), 403, "archive");
        Assert.True(fenced.FacadeOwned); Assert.Equal("archive.ack.commit", fenced.Operation); Assert.Equal("disabled", fenced.State); Assert.Equal(new string('c', 64), fenced.ClosureId);
        // precondition_failed/refresh (If-Match stale) is the family-translated form; /rediscover (closure_stale) is facade-owned.
        var stale = Decode(Envelope("precondition_failed", 412, "refresh", "cfg-42", "{\"domain\":\"terminal\",\"family\":\"terminal-services-v1\",\"domainCode\":\"revision_conflict\",\"domainStatus\":409,\"domainRetryAction\":\"refresh\",\"reason\":\"if_match_stale\",\"header\":\"if-match\"}"), 412, "terminal");
        Assert.False(stale.FacadeOwned); Assert.True(stale.RequiresRefresh); Assert.Equal("revision_conflict", stale.Detail.DomainCode);
        var closure = Decode(Envelope("precondition_failed", 412, "rediscover", null, "{\"domainCode\":\"closure_stale\",\"closureId\":\"" + new string('d', 64) + "\"}"), 412);
        Assert.True(closure.FacadeOwned); Assert.True(closure.RequiresRediscovery); Assert.Equal(new string('d', 64), closure.ClosureId);
    }

    [Fact]
    public void CacheContinuityExceptionCarriesTheUnifiedFaceFirst()
    {
        var unified = Decode(Envelope("stale_generation", 409, "rebind", "op-9", "{\"domain\":\"cache\",\"family\":\"sdk2-cache-v1\",\"domainCode\":\"binding_rotated\",\"domainStatus\":409,\"domainRetryAction\":\"rebind\",\"fallback\":\"none\"}"), 409, "cache");
        var error = new CacheContinuityException(unified, unified.Fallback ?? "none");
        Assert.Equal(UnifiedErrorCode.StaleGeneration, error.Code); Assert.Equal(409, error.StatusCode); Assert.Equal(UnifiedRetryAction.Rebind, error.RetryAction);
        Assert.Equal("binding_rotated", error.DomainCode); Assert.Equal(409, error.DomainStatus); Assert.Equal("rebind", error.DomainRetryAction);
        Assert.Equal("none", error.Fallback); Assert.Same(unified, error.Unified);
        var plain = Decode(Envelope("forbidden", 403, "none", "op-2"), 403, "cache");
        var bridged = new CacheContinuityException(plain, "none");
        Assert.Equal("forbidden", bridged.DomainCode); Assert.Equal(403, bridged.DomainStatus); Assert.Equal("none", bridged.DomainRetryAction); Assert.Null(bridged.Unified?.Detail.DomainCode);
    }

    [Fact]
    public void TypedDetailReadsEveryRegisteredFieldAndStaysEmptyWhenAbsent()
    {
        var error = Decode(Envelope("invalid_request", 408, "none", "msg-9", "{\"reason\":\"deadline_exceeded\",\"header\":\"deadline\"}"), 408);
        Assert.False(error.Detail.IsEmpty); Assert.Equal("deadline_exceeded", error.Detail.Reason); Assert.Equal("deadline", error.Detail.Header);
        Assert.True(error.Detail.TryGet("header", out var header)); Assert.Equal("deadline", header.GetString()); Assert.False(error.Detail.TryGet("nope", out _));
        Assert.Null(error.Detail.Domain); Assert.Null(error.Detail.LimitBytes); Assert.Null(error.Detail.ClosureId);
        var bare = Decode(Envelope("unauthorized", 401, "none", null), 401);
        Assert.True(bare.Detail.IsEmpty); Assert.Same(UnifiedErrorDetail.Empty, bare.Detail); Assert.Null(bare.Reason); Assert.Null(bare.Detail.Raw);
#pragma warning disable CS0618
        Assert.Equal("unauthorized", bare.DomainCode); Assert.True(bare.FacadeOwned);
#pragma warning restore CS0618
    }
}
