using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Sdk.Cache;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Tests.Cache;

public sealed class CacheClientTests
{
    private const string Ticket = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Time = "2030-01-01T01:00:00Z";
    private const string Binding = """
        {"protocol":"sdk2-cache-v1","audience":"serve-cache","bindingId":"binding","logicalRef":"logical","revision":"1","state":"active","mode":"private-logical-v1","groupGeneration":"0","projection":null,"expiresAt":"2030-01-01T01:00:00Z","availability":"legacy-complete"}
        """;
    private const string Limits = """
        {"controlBytes":65536,"exchangeBytes":33554432,"projectionBytes":262144,"projectionSegments":64,"ticketTtlMs":60000,"mappingIdleMs":60000,"mappingLifetimeMs":60000,"bindingsPerLogical":1,"mappingsPerUser":1,"mappingsPerApplication":1,"mappingsGlobal":1,"mappingBytes":1024,"receiptsGlobal":1,"receiptBytes":1024,"receiptRetentionMs":60000,"epochLifetimeMs":86400000,"diagnosticRowsPerUser":1,"diagnosticRowsPerApplication":1,"diagnosticRowsGlobal":1,"diagnosticBytes":1024,"diagnosticRetentionMs":60000,"diagnosticSamplePerMillion":0,"diagnosticsPageRows":100,"negotiatedTtlMs":1000,"retainedRowsPerUser":1,"retainedRowsPerApplication":1,"retainedRowsGlobal":1,"retainedBytesPerUser":1,"retainedBytesPerApplication":1,"retainedBytesGlobal":1}
        """;
    private static string Caps => "{\"protocol\":\"sdk2-cache-v1\",\"audience\":\"serve-cache\",\"features\":[\"private-logical-cache-v1\"],\"gateway\":\"confirmed\",\"availability\":\"legacy-complete\",\"revision\":\"1\",\"limits\":" + Limits + ",\"operationEpoch\":{\"id\":\"epoch\",\"issuedAt\":\"2030-01-01T00:00:00Z\",\"expiresAt\":\"" + Time + "\",\"state\":\"active\"}}";
    private static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed record Request(string Method, string Path, byte[]? Body, string? Token);
    private sealed class Handler(Func<Request, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new Request(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken), request.Headers.Authorization?.Parameter);
            Requests.Add(captured); return respond(captured);
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Principal = "app/user";
        internal string User = "user";
        internal string Revision = "1";
        internal Func<Request, HttpResponseMessage>? Respond;
        internal Action? DuringToken;
        internal readonly Handler Handler;
        internal readonly HttpClient Http;
        internal readonly TansrClient Client;
        internal readonly CacheClient Cache;
        internal Fixture()
        {
            Handler = new Handler(request => Respond?.Invoke(request) ?? Json(request.Path.EndsWith("/capabilities", StringComparison.Ordinal) ? Caps : Reply(request)));
            Http = new HttpClient(Handler);
            Client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri("https://serve.test/"),
                PrincipalProvider = () => Principal,
                TokenProvider = _ => { DuringToken?.Invoke(); return Task.FromResult("test-token-" + Revision); },
                ExecutionScopeProvider = () => Element("{\"applicationScopeId\":\"app\",\"endUserId\":\"" + User + "\",\"authorizationRevision\":\"" + Revision + "\"}")
            }, Http);
            Cache = new CacheClient(Client, true, () => DateTimeOffset.Parse("2030-01-01T00:30:00Z"));
        }
        public void Dispose() { Client.Dispose(); Http.Dispose(); Handler.Dispose(); }
    }

    private static string Reply(Request request)
    {
        var body = request.Body is null ? Element("{\"request\":{\"operationEpoch\":\"epoch\",\"requestId\":\"request\"},\"intent\":{\"kind\":\"new\"}}") : CacheJson.Read(request.Body);
        var identity = body.GetProperty("request").GetRawText();
        var operation = request.Body is null ? "open" : request.Path.Substring(request.Path.LastIndexOf('/') + 1);
        if (operation == "bindings" || operation == "open")
        {
            var kind = body.GetProperty("intent").GetProperty("kind").GetString();
            var relation = kind == "new" ? "new" : kind == "import" ? "imported" : kind == "resume" ? "resumed" : "forked";
            return "{\"protocol\":\"sdk2-cache-v1\",\"request\":" + identity + ",\"binding\":" + Binding + ",\"ticket\":\"" + Ticket + "\",\"ticketExpiresAt\":\"" + Time + "\",\"relation\":\"" + relation + "\"}";
        }
        return "{\"protocol\":\"sdk2-cache-v1\",\"request\":" + identity + ",\"operation\":\"" + operation + "\",\"semanticDigest\":{\"algorithm\":\"hmac-sha256\",\"keyId\":\"server-key\",\"digest\":\"" + new string('0', 64) + "\"},\"state\":\"completed\",\"binding\":" + (operation == "close" ? Binding.Replace("\"active\"", "\"closed\"") : Binding) + ",\"ticket\":" + (operation == "close" ? "null" : "\"" + Ticket + "\"") + ",\"ticketExpiresAt\":" + (operation == "close" ? "null" : "\"" + Time + "\"") + "}";
    }

    [Fact]
    public void CandidateIsOptInAndRequiresTrustedScope()
    {
        using var fixture = new Fixture();
        Assert.Throws<TansrProtocolException>(() => new CacheClient(fixture.Client));
        using var client = new TansrClient(new TansrClientOptions { BaseUri = new("https://serve.test/"), TokenProvider = _ => Task.FromResult("token") });
        Assert.Throws<TansrProtocolException>(() => new CacheClient(client, true));
        Assert.Empty(fixture.Handler.Requests);
        Assert.DoesNotContain(typeof(TansrClient).Assembly.GetExportedTypes(), type => type.Namespace == "Tansr.Sdk.Cache");
    }

    [Theory]
    [InlineData(0, "new")]
    [InlineData(1, "imported")]
    [InlineData(2, "resumed")]
    [InlineData(3, "forked")]
    public async Task OpenConsumesServeEpochWithoutChangingSdk1SessionFamily(int kindValue, string relation)
    {
        using var fixture = new Fixture();
        var kind = (CacheOpenKind)kindValue;
        var operation = await fixture.Cache.PrepareOpenAsync("session/中文", kind, kind is CacheOpenKind.Resume or CacheOpenKind.Fork ? new CacheTicket(Ticket) : null, "request");
        var original = operation.ExportOriginalRequest(); var altered = operation.ExportOriginalRequest(); altered[0] = 0;
        var receipt = await fixture.Cache.SubmitAsync(operation);
        Assert.Equal(relation, receipt.Raw.GetProperty("relation").GetString()); Assert.Equal("logical", receipt.Binding.LogicalReference);
        Assert.Equal("/v3/sdk2/cache/capabilities", fixture.Handler.Requests[0].Path);
        var sent = fixture.Handler.Requests[1]; Assert.Equal("/v3/sdk2/cache/bindings", sent.Path); Assert.Equal(original, sent.Body);
        Assert.Equal("epoch", CacheJson.Read(sent.Body!).GetProperty("request").GetProperty("operationEpoch").GetString());
        Assert.DoesNotContain(fixture.Handler.Requests, item => item.Path.Contains("session-capabilities", StringComparison.Ordinal));
        Assert.DoesNotContain(Ticket, receipt.ToString()); Assert.DoesNotContain(Ticket, receipt.Ticket!.ToString());
    }

    [Theory]
    [InlineData("renew")]
    [InlineData("rotate")]
    [InlineData("rebind")]
    [InlineData("close")]
    public async Task MutationsUseExactExistingBodiesAndReturnOriginalFacts(string action)
    {
        using var fixture = new Fixture(); var binding = new CacheBinding(Element(Binding), fixture.Client.CacheOwner()); var ticket = new CacheTicket(Ticket);
        var operation = action switch
        {
            "renew" => await fixture.Cache.PrepareRenewAsync(binding, ticket, "request"),
            "rotate" => await fixture.Cache.PrepareRotateAsync(binding, ticket, "request"),
            "rebind" => await fixture.Cache.PrepareRebindAsync(binding, "session-new", "request"),
            _ => await fixture.Cache.PrepareCloseAsync(binding, ticket, "request")
        };
        var receipt = await fixture.Cache.SubmitAsync(operation);
        Assert.Equal(action, receipt.Raw.GetProperty("operation").GetString());
        var request = fixture.Handler.Requests[1]; var body = CacheJson.Read(request.Body!);
        Assert.Equal("/v3/sdk2/cache/bindings/binding/" + action, request.Path);
        Assert.Equal("1", body.GetProperty("expectedRevision").GetString());
        Assert.Equal(action != "rebind", body.TryGetProperty("ticket", out _));
        Assert.Equal(action == "rebind", body.TryGetProperty("sessionId", out _));
        Assert.Equal(action == "rotate", body.TryGetProperty("reason", out _));
        Assert.Equal(action == "close", receipt.Ticket is null);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("request")]
    [InlineData("epoch")]
    [InlineData("relation")]
    [InlineData("ticket")]
    [InlineData("unknown")]
    public async Task BadReceiptCannotBeAdopted(string fault)
    {
        using var fixture = new Fixture();
        var operation = await fixture.Cache.PrepareOpenAsync("session", CacheOpenKind.New, requestId: "request");
        fixture.Respond = request =>
        {
            var body = JsonNode.Parse(Reply(request))!;
            if (fault == "audience") body["binding"]!["audience"] = "gateway-cache";
            else if (fault == "request") body["request"]!["requestId"] = "other";
            else if (fault == "epoch") body["request"]!["operationEpoch"] = "other";
            else if (fault == "relation") body["relation"] = "resumed";
            else if (fault == "ticket") body["ticket"] = Ticket[..^1] + "B";
            else body["groupAuthorization"] = "forged";
            return Json(body.ToJsonString());
        };
        await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.SubmitAsync(operation));
        Assert.Null(operation.ReceiptCanonical);
    }

    [Fact]
    public async Task UnknownPostOnlyQueriesAndExplicitReplayKeepsOriginalBytes()
    {
        using var fixture = new Fixture();
        var operation = await fixture.Cache.PrepareOpenAsync("session", CacheOpenKind.New, requestId: "request");
        fixture.Respond = _ => throw new HttpRequestException("secret transport detail");
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.SubmitAsync(operation)); Assert.Equal("network_error", error.Code);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.SubmitAsync(operation)); Assert.Equal(2, fixture.Handler.Requests.Count);
        fixture.Revision = "2"; fixture.Respond = request => Json(Reply(request));
        var original = operation.ExportOriginalRequest(); var restored = fixture.Cache.RestoreOperation("open", original, fixture.Client.CacheOwner());
        await fixture.Cache.GetOperationStatusAsync(restored);
        var queried = fixture.Handler.Requests[2]; Assert.Equal("GET", queried.Method); Assert.Null(queried.Body);
        Assert.Contains("operationEpoch=epoch&requestId=request", queried.Path, StringComparison.Ordinal); Assert.Equal("test-token-2", queried.Token);
        await fixture.Cache.ReplayOriginalAsync(restored);
        Assert.Equal(original, fixture.Handler.Requests[3].Body);
        var afterRestart = fixture.Cache.RestoreOperation("open", original, fixture.Client.CacheOwner(), restored.ExportOriginalReceipt());
        fixture.Respond = request => Json(Reply(request).Replace("\"revision\":\"1\"", "\"revision\":\"2\""));
        var changed = await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.GetOperationStatusAsync(afterRestart));
        Assert.Equal("receipt_conflict", changed.Code);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("token")]
    [InlineData("response")]
    public async Task OwnerAndAuthorizationChangesAreCheckedAcrossAsyncBoundaries(string when)
    {
        using var fixture = new Fixture();
        if (when == "before") fixture.User = "other";
        if (when == "token") fixture.DuringToken = () => fixture.Principal = "app/other";
        if (when == "response") fixture.Respond = _ => { fixture.Revision = "2"; return Json(Caps); };
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.DiscoverAsync());
        Assert.Equal("context_changed", error.Code); Assert.Equal(when == "response" ? 1 : 0, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("expired")]
    [InlineData("gateway")]
    public async Task UnavailableDiscoveryNeverCreatesABinding(string fault)
    {
        using var fixture = new Fixture();
        var caps = JsonNode.Parse(Caps)!;
        if (fault == "disabled") { caps["features"] = new JsonArray(); caps["operationEpoch"] = null; caps["gateway"] = "unsupported"; }
        if (fault == "expired") caps["operationEpoch"]!["expiresAt"] = "2029-01-01T00:00:00Z";
        if (fault == "gateway") { caps["audience"] = "gateway-cache"; caps["gateway"] = "not-applicable"; }
        fixture.Respond = _ => Json(caps.ToJsonString());
        await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.PrepareOpenAsync("session", CacheOpenKind.New));
        Assert.Single(fixture.Handler.Requests); Assert.Equal("GET", fixture.Handler.Requests[0].Method);
    }

    [Fact]
    public async Task ActualServe404IsNotRewrittenAndSecretsAreRedacted()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json("{\"protocol\":\"sdk2-cache-v1\",\"requestId\":\"secret-request\",\"code\":\"mapping_unavailable\",\"status\":404,\"retryAction\":\"none\",\"fallback\":\"none\",\"message\":\"secret-body\"}", HttpStatusCode.NotFound);
        var error = await Assert.ThrowsAsync<CacheHttpException>(() => fixture.Cache.ReadBindingAsync("binding"));
        Assert.Equal(404, error.StatusCode); Assert.Equal("mapping_unavailable", error.Code);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mapping_unavailable", 404)]
    [InlineData("mapping_unavailable", 409)]
    [InlineData("mapping_unavailable", 503)]
    [InlineData("capacity_exceeded", 429)]
    [InlineData("capacity_exceeded", 503)]
    [InlineData("epoch_unavailable", 409)]
    [InlineData("epoch_unavailable", 503)]
    public async Task RevisedCandidatePreservesActualServeErrorStatuses(string code, int status)
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json("{\"protocol\":\"sdk2-cache-v1\",\"requestId\":\"r\",\"code\":\"" + code + "\",\"status\":" + status + ",\"retryAction\":\"none\",\"fallback\":\"none\",\"message\":\"x\"}", (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<CacheHttpException>(() => fixture.Cache.ReadBindingAsync("binding"));
        Assert.Equal(status, error.StatusCode); Assert.Equal(code, error.Code); Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(404, "same-request", "none")]
    [InlineData(409, "query-status", "none")]
    [InlineData(404, "none", "legacy-cold")]
    [InlineData(409, "none", "legacy-cold")]
    public async Task Mapping404And409CannotAdvertiseRetryOrFallback(int status, string retry, string fallback)
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json("{\"protocol\":\"sdk2-cache-v1\",\"requestId\":\"r\",\"code\":\"mapping_unavailable\",\"status\":" + status + ",\"retryAction\":\"" + retry + "\",\"fallback\":\"" + fallback + "\",\"message\":\"x\"}", (HttpStatusCode)status);
        await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.ReadBindingAsync("binding"));
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("{\"protocol\":\"sdk2-cache-v1\",\"requestId\":\"r\",\"code\":\"result_unknown\",\"status\":503,\"retryAction\":\"query-status\",\"fallback\":\"none\",\"message\":\"x\"}")]
    [InlineData("{\"error\":{\"code\":\"result_unknown\"}}")]
    public async Task ErrorProtocolAndHttpStatusMustMatch(string error)
    {
        using var fixture = new Fixture(); fixture.Respond = _ => Json(error, HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.ReadBindingAsync("binding"));
    }

    [Theory]
    [InlineData("{\"n\":-0}")]
    [InlineData("{\"n\":1e0}")]
    [InlineData("{\"n\":1.0}")]
    [InlineData("{\"n\":1,\"n\":2}")]
    [InlineData("{\"é\":1}")]
    [InlineData("{\"n\":9007199254740992}")]
    [InlineData("\uFEFF{}")]
    public void CacheControlRetainsItsOriginalStrictLexicalRules(string input)
        => Assert.Throws<TansrProtocolException>(() => CacheJson.Read(Encoding.UTF8.GetBytes(input)));

    [Fact]
    public async Task BodyCapsStatusAndExactBindingAreChecked()
    {
        using var fixture = new Fixture(); fixture.Respond = _ => Json(new string(' ', 65537));
        var large = await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.ReadBindingAsync("binding")); Assert.Equal("response_too_large", large.Code);
        fixture.Respond = _ => Json(Binding, HttpStatusCode.Created);
        await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.ReadBindingAsync("binding"));
        fixture.Respond = _ => Json(Binding.Replace("\"bindingId\":\"binding\"", "\"bindingId\":\"other\""));
        await Assert.ThrowsAsync<TansrProtocolException>(() => fixture.Cache.ReadBindingAsync("binding"));
    }

    [Fact]
    public async Task EmptyLegacyDiagnosticsDoNotInventHitAmountsAndMalformedRestoreIsRejected()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json("{\"protocol\":\"sdk2-cache-v1\",\"rows\":[],\"next\":null}");
        var result = await fixture.Cache.ReadDiagnosticsAsync("binding", "after-id", 2);
        Assert.Equal(0, result.GetProperty("rows").GetArrayLength());
        Assert.EndsWith("&limit=2&after=after-id", Assert.Single(fixture.Handler.Requests).Path, StringComparison.Ordinal);
        Assert.Throws<TansrProtocolException>(() => fixture.Cache.RestoreOperation("open", Encoding.UTF8.GetBytes("{}"), "other-owner"));
        Assert.Throws<TansrProtocolException>(() => fixture.Cache.RestoreOperation("open", Encoding.UTF8.GetBytes("{ }"), fixture.Client.CacheOwner()));
    }
}
