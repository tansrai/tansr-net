using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalProfileTests
{
    private static JsonElement Goldens()
    { using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Terminal", "Fixtures", "terminal-profile-v1.golden.json"))); return document.RootElement.Clone(); }
    public static IEnumerable<object[]> Vectors()
    {
        foreach (var group in new[] { "positive", "negative" })
            foreach (var item in Goldens().GetProperty(group).EnumerateArray()) yield return [item.GetProperty("id").GetString()!, item.GetProperty("definition").GetString()!, item.GetProperty("value").GetRawText(), group == "positive"];
    }
    [Theory, MemberData(nameof(Vectors))]
    public void OriginalServeGoldensAreConsumedWithoutRewriting(string id, string definition, string json, bool valid)
    {
        Assert.NotEmpty(id); using var document = JsonDocument.Parse(json);
        if (valid) TerminalProfileContract.Validate(definition, document.RootElement);
        else Assert.Throws<WireProtocolException>(() => TerminalProfileContract.Validate(definition, document.RootElement));
    }
    [Fact]
    public void EmbeddedSchemaAndGoldensHaveTheAgreedIndependentFingerprints()
    {
        using var stream = typeof(TerminalProfileClient).Assembly.GetManifestResourceStream("Tansr.Sdk.Terminal.terminal-profile-v1.schema.json")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); Assert.Equal(TerminalProfileClient.SchemaSha256, WireJson.Sha256(buffer.ToArray()));
        Assert.Equal("6ecfea5872a8cb2052d35cb908839c7ab07c11d77f65dc7e9a230494a2cf8ea4", WireJson.Sha256(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Terminal", "Fixtures", "terminal-profile-v1.golden.json"))));
        Assert.Equal(TerminalProfileClient.SchemaSha256, Goldens().GetProperty("schemaSha256").GetString());
    }
    [Fact]
    public async Task SameAuthenticatedTransportReadsCatalogCapabilitiesAndOwnUsageWithoutTakingOwnership()
    {
        using var handler = new Handler((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content); Assert.Equal("controller-only", request.Headers.Authorization?.Parameter);
            Assert.Equal("kept", Assert.Single(request.Headers.GetValues("X-Owned-Host")));
            Assert.Contains("?contract=terminal-profile-v1&sessionContract=sdk1", request.RequestUri!.Query);
            return Task.FromResult(Response(request.RequestUri.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal) ? Usage() : Catalog()));
        });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); http.DefaultRequestHeaders.Add("X-Owned-Host", "kept"); using var owner = new TansrClient(Options(), http);
        using (var profile = new TerminalProfileClient(owner, true))
        {
            var catalog = await profile.ReadCatalogAsync("session-1"); Assert.Equal("Authorized", Assert.Single(catalog.Models).DisplayName); Assert.Equal("model-1", catalog.Aliases["main"]);
            Assert.True(catalog.Capabilities.GetProperty("platform").GetProperty("textToSpeech").GetBoolean());
            Assert.Equal("not_exposed", catalog.Quota.State); Assert.Equal("gateway", catalog.Quota.Enforcement);
            var usage = await profile.ReadUsageAsync("session-1"); Assert.Equal("user", usage.EndUserId); Assert.Equal("1d", usage.Window); Assert.Equal(7, usage.Requests);
            Assert.Equal(1234, usage.InTokens); Assert.Equal(56, usage.OutTokens); Assert.Equal(78, usage.CacheRTokens); Assert.Equal(9, usage.CacheWTokens);
        }
        using var next = new TerminalProfileClient(owner, true); Assert.Single((await next.ReadCatalogAsync("session-1")).Models); Assert.Equal(3, handler.Calls);
    }
    [Fact]
    public async Task DefaultPreviewAndMismatchedSessionFamilyMakeNoRequests()
    {
        using var handler = new Handler((_, _, _) => throw new InvalidOperationException("Unexpected request")); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var owner = new TansrClient(Options(), http);
        Assert.Equal("unsupported_capability", Assert.Throws<TansrProtocolException>(() => new TerminalProfileClient(owner)).Code);
        using var profile = new TerminalProfileClient(owner, true);
        Assert.Equal("binding_conflict", (await Assert.ThrowsAsync<TansrProtocolException>(() => profile.ReadCatalogAsync("session-1", SessionContract.Sdk2OffloadV1))).Code); Assert.Equal(0, handler.Calls);
    }
    [Theory]
    [InlineData("wrong-session")]
    [InlineData("duplicate-model")]
    [InlineData("missing-alias-target")]
    [InlineData("unknown-finance")]
    [InlineData("unknown-capability")]
    [InlineData("alias-too-many")]
    [InlineData("alias-empty")]
    [InlineData("alias-value-object")]
    public async Task CatalogRejectsUnrelatedIdentityUnconfirmedModelsUnknownFieldsAndUnboundedAliases(string attack)
    {
        var body = JsonNode.Parse(Catalog().GetRawText())!.AsObject();
        if (attack == "wrong-session") body["session"]!["sessionId"] = "other";
        if (attack == "duplicate-model") body["models"]!.AsArray().Add(body["models"]![0]!.DeepClone());
        if (attack == "missing-alias-target") body["aliases"]!["main"] = "unknown";
        if (attack == "unknown-finance") body["quota"]!["remaining"] = 999;
        if (attack == "unknown-capability") body["capabilities"]!["tools"]!["escapeHost"] = true;
        if (attack == "alias-too-many") { var aliases = new JsonObject(); for (var i = 0; i < 1025; i++) aliases["a" + i] = "model-1"; body["aliases"] = aliases; }
        if (attack == "alias-empty") body["aliases"]![""] = "model-1";
        if (attack == "alias-value-object") body["aliases"]!["main"] = new JsonObject();
        using var handler = new Handler((_, _, _) => Task.FromResult(Response(JsonSerializer.SerializeToElement(body)))); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var owner = new TansrClient(Options(), http); using var profile = new TerminalProfileClient(owner, true);
        var error = await Record.ExceptionAsync(() => profile.ReadCatalogAsync("session-1")); Assert.True(error is TansrProtocolException or WireProtocolException); Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("other-user")]
    [InlineData("negative")]
    [InlineData("unsafe")]
    [InlineData("fraction")]
    [InlineData("window")]
    [InlineData("money")]
    public async Task UsageRejectsOtherUserInvalidCountersAndDeveloperBilling(string attack)
    {
        var body = JsonNode.Parse(Usage().GetRawText())!.AsObject(); var usage = body["usage"]!;
        if (attack == "other-user") usage["endUserId"] = "other";
        if (attack == "window") usage["window"] = "30d";
        if (attack == "money") usage["amount"] = 3;
        // Invalid canonical counters are rejected by the real byte decoder, not test encoding.
        var json = WireJson.CanonicalString(JsonSerializer.SerializeToElement(body));
        if (attack == "negative") json = json.Replace("\"inTokens\":1234", "\"inTokens\":-1", StringComparison.Ordinal);
        if (attack == "unsafe") json = json.Replace("\"inTokens\":1234", "\"inTokens\":9007199254740992", StringComparison.Ordinal);
        if (attack == "fraction") json = json.Replace("\"inTokens\":1234", "\"inTokens\":1.5", StringComparison.Ordinal);
        using var handler = new Handler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var owner = new TansrClient(Options(), http); using var profile = new TerminalProfileClient(owner, true);
        var error = await Record.ExceptionAsync(() => profile.ReadUsageAsync("session-1")); Assert.True(error is TansrProtocolException or WireProtocolException); Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerScopeCannotChangeBeforeOrDuringARead(bool during)
    {
        var scope = ExecutionFixture.Scope(); using var handler = new Handler((_, _, _) => { scope = ExecutionFixture.Scope(revision: "2"); return Task.FromResult(Response(Catalog())); });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var owner = new TansrClient(Options(() => scope), http); using var profile = new TerminalProfileClient(owner, true);
        if (!during) scope = ExecutionFixture.Scope(user: "other");
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<TansrProtocolException>(() => profile.ReadCatalogAsync("session-1"))).Code); Assert.Equal(during ? 1 : 0, handler.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FacadeOrOwnerDisposalCancelsPendingReadAndProhibitsAnother(bool disposeOwner)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, _, ct) => { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var owner = new TansrClient(Options(), http); using var profile = new TerminalProfileClient(owner, true);
        var pending = profile.ReadCatalogAsync("session-1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposeOwner) owner.Dispose(); else profile.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => profile.ReadCatalogAsync("session-1")); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task SafeTypedErrorHasNoLegacyFallbackOrRetry()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Response(JsonSerializer.SerializeToElement(new { contract = "terminal-profile-v1", requestId = "req-1", code = "source_unavailable", status = 503, retryAction = "backoff" }), HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var owner = new TansrClient(Options(), http); using var profile = new TerminalProfileClient(owner, true);
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => profile.ReadUsageAsync("session-1")); Assert.Equal("source_unavailable", error.Code); Assert.Equal("backoff", error.RetryAction); Assert.Equal(503, error.StatusCode); Assert.Equal(1, handler.Calls);
    }
    private static JsonElement Catalog()
    {
        var tools = "read write edit glob grep list shell process webFetch webSearch http todoWrite askUser agent skills mcp customTools".Split(' ').ToDictionary(key => key, _ => false);
        return JsonSerializer.SerializeToElement(new { contract = "terminal-profile-v1", session = new { sessionContract = "sdk1", sessionId = "session-1" }, models = new[] { new { handle = "model-1", displayName = "Authorized", manufacturer = (string?)null, family = (string?)null } }, aliases = new { main = "model-1" }, quota = new { state = "not_exposed", enforcement = "gateway" }, capabilities = new { tools, platform = new { imageGen = false, videoGen = false, webSearch = false, speechToText = false, textToSpeech = true } } });
    }
    private static JsonElement Usage() => JsonSerializer.SerializeToElement(new { contract = "terminal-profile-v1", session = new { sessionContract = "sdk1", sessionId = "session-1" }, usage = new { window = "1d", endUserId = "user", requests = 7, inTokens = 1234, outTokens = 56, cacheRTokens = 78, cacheWTokens = 9 }, quota = new { state = "not_exposed", enforcement = "gateway" } });
    private static TansrClientOptions Options(Func<JsonElement>? scope = null) => new() { BaseUri = new("https://serve.test/"), TokenProvider = _ => Task.FromResult("controller-only"), PrincipalProvider = () => "app/user", ExecutionScopeProvider = scope ?? (() => ExecutionFixture.Scope()) };
    private static HttpResponseMessage Response(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(WireJson.CanonicalString(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ++Calls, ct);
    }
}
