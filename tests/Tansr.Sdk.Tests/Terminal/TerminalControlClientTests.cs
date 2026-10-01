using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalControlClientTests
{
    private static JsonElement Session(string id = "旧会话/ ?#", string family = "sdk1") => JsonSerializer.SerializeToElement(new { sessionContract = family, sessionId = id });
    private static JsonElement Changes(string model = "trusted-alias") => JsonSerializer.SerializeToElement(new { model, thinking = (object?)null });
    private static JsonElement Configuration(JsonElement session, bool committed = false, string requestId = "configure-1", string status = "changed", long revision = 3)
    {
        var state = JsonSerializer.SerializeToElement(new { revision, model = "trusted-alias", thinking = (object?)null });
        return TerminalJson.Object(w =>
        {
            w.WriteString("contract", "terminal-services-v1"); TerminalJson.Field(w, "session", session);
            TerminalJson.Field(w, "configuration", state);
            if (committed) { w.WriteString("requestId", requestId); w.WriteString("status", status); }
        });
    }
    private static JsonElement Memory(JsonElement session, string user = "user", string application = "app", bool available = true) => JsonSerializer.SerializeToElement(new
    {
        contract = "terminal-services-v1",
        session,
        memory = new
        {
            identity = new { kind = "developer-managed", domain = "app/user", sourceId = "资料/user", sourceGeneration = "9007199254740993", applicationScopeId = application, endUserId = user },
            revision = "9007199254740995",
            deletionGeneration = "0",
            available
        }
    });
    private static JsonElement Command(string kind = "pin") => kind switch
    {
        "pin" => JsonSerializer.SerializeToElement(new { kind, text = "合成偏好" }),
        "forget" => JsonSerializer.SerializeToElement(new { kind, topic = "合成主题" }),
        _ => JsonSerializer.SerializeToElement(new { kind })
    };
    private static JsonElement Receipt(JsonElement request, string status = "committed", bool consumed = false) => TerminalJson.Object(w =>
    {
        foreach (var key in new[] { "requestId", "operationId", "sourceId", "sourceGeneration", "expectedRevision" }) TerminalJson.Field(w, key, request.GetProperty(key));
        w.WriteString("status", status);
        w.WriteBoolean("executionComplete", status == "committed" || status == "failed");
        w.WriteBoolean("durable", status == "committed"); w.WriteBoolean("consumed", consumed);
        if (status == "committed") w.WriteString("publishedRevision", "9007199254740996");
        if (status == "unknown") w.WriteString("errorCode", "commit_unknown");
        if (status == "failed") w.WriteString("errorCode", "operation_failed");
    });
    private static JsonElement MemoryResponse(JsonElement session, JsonElement? receipt) => JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", session, receipt });
    private static HttpResponseMessage Response(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(WireJson.CanonicalString(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
    private sealed class Fixture : IDisposable
    {
        internal JsonElement Scope = ExecutionFixture.Scope();
        internal readonly List<(string Method, string Path, JsonElement? Body)> Requests = [];
        internal int Discoveries;
        internal JsonElement Capabilities;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply = (_, _) => throw new InvalidOperationException("Unexpected HTTP");
        private readonly HttpClient http;
        internal readonly TansrClient Client;
        internal readonly TerminalControlClient Control;
        internal Fixture(SessionContract family = SessionContract.Sdk1)
        {
            var caps = new TerminalTestAdapter().Capabilities;
            foreach (var feature in caps["features"]!.AsArray())
            { feature!["supported"] = true; feature["installed"] = true; }
            Capabilities = TerminalTestAdapter.Element(caps);
            http = UnifiedStamp.Client(new Handler(async (request, ct) =>
            {
                Assert.Equal("synthetic-only-token", request.Headers.Authorization!.Parameter);
                if (request.RequestUri!.AbsolutePath == "/api/capabilities/terminal")
                { Discoveries++; return Response(Capabilities); }
                JsonElement? body = request.Content is null ? null : WireJson.DecodeControl(await request.Content.ReadAsByteArrayAsync(ct));
                Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
                return await Reply(request, ct);
            }));
            Client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri("http://127.0.0.1:34567/"),
                AllowInsecureLoopback = true,
                TokenProvider = _ => Task.FromResult("synthetic-only-token"),
                PrincipalProvider = () => "fixture-principal",
                ExecutionScopeProvider = () => Scope,
                SessionContract = family
            }, http);
            Control = new TerminalControlClient(Client, enableCandidate: true);
        }
        public void Dispose() { Client.Dispose(); http.Dispose(); }
    }
    private static TerminalMemoryOperation MemoryOperation(Fixture f, string kind = "pin") =>
        f.Control.CreateMemoryOperation(Session(), Memory(Session()), "请求 ?#", "操作/ ?#", Command(kind));

    [Fact]
    public async Task FiveOriginalControlRoutesPreserveSessionCasAndMemoryFacts()
    {
        using var f = new Fixture(); var op = MemoryOperation(f); var receipt = Receipt(op.Request);
        f.Reply = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/configuration", StringComparison.Ordinal)
            ? Response(Configuration(Session(), request.Method == HttpMethod.Post, status: "replayed"))
            : request.RequestUri.AbsolutePath.EndsWith("/memory", StringComparison.Ordinal) ? Response(Memory(Session())) : Response(MemoryResponse(Session(), receipt)));
        await f.Control.ReadConfigurationAsync(Session());
        var change = f.Control.CreateConfigurationOperation(Session(), "configure-1", 2, Changes());
        Assert.Equal("replayed", (await f.Control.ApplyConfigurationAsync(change)).GetProperty("status").GetString());
        var memory = await f.Control.ReadMemoryAsync(Session());
        Assert.Equal("9007199254740995", memory.GetProperty("memory").GetProperty("revision").GetString());
        Assert.False((await f.Control.SubmitMemoryAsync(op)).GetProperty("receipt").GetProperty("consumed").GetBoolean());
        await f.Control.QueryMemoryAsync(op);
        var path = "/api/terminal/sessions/" + Uri.EscapeDataString("旧会话/ ?#");
        Assert.Equal(new[]
        {
            "GET " + path + "/configuration?contract=terminal-services-v1&sessionContract=sdk1",
            "POST " + path + "/configuration", "GET " + path + "/memory?contract=terminal-services-v1&sessionContract=sdk1",
            "POST " + path + "/memory/commands", "GET " + path + "/memory/commands/" + Uri.EscapeDataString("操作/ ?#") +
                "?contract=terminal-services-v1&sessionContract=sdk1&requestId=" + Uri.EscapeDataString("请求 ?#")
        }, f.Requests.Select(r => r.Method + " " + r.Path));
        Assert.Equal(WireJson.CanonicalString(change.Request), WireJson.CanonicalString(f.Requests[1].Body!.Value));
        Assert.Equal(WireJson.CanonicalString(op.Request), WireJson.CanonicalString(f.Requests[3].Body!.Value));
        Assert.Equal(5, f.Discoveries);
    }

    [Theory]
    [InlineData("remember")]
    [InlineData("pin")]
    [InlineData("forget")]
    public async Task EachMemoryCommandUsesSourceGenerationAndRevisionFromActualState(string kind)
    {
        using var f = new Fixture(); var op = MemoryOperation(f, kind);
        f.Reply = (_, _) => Task.FromResult(Response(MemoryResponse(Session(), Receipt(op.Request))));
        await f.Control.SubmitMemoryAsync(op);
        Assert.Equal(kind, f.Requests.Single().Body!.Value.GetProperty("command").GetProperty("kind").GetString());
        Assert.Equal("9007199254740993", op.Request.GetProperty("sourceGeneration").GetString());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("unknown")]
    [InlineData("failed")]
    [InlineData("committed")]
    public async Task ReceiptStatusDoesNotConflateExecutionDurabilityAndConsumption(string status)
    {
        using var f = new Fixture(); var op = MemoryOperation(f);
        var receipt = Receipt(op.Request, status);
        f.Reply = (_, _) => Task.FromResult(Response(MemoryResponse(Session(), receipt)));
        Assert.Equal(WireJson.CanonicalString(receipt), WireJson.CanonicalString((await f.Control.SubmitMemoryAsync(op)).GetProperty("receipt")));
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task LostMemoryResponseQueriesOriginalOperationWithoutRepeatingSideEffect()
    {
        using var f = new Fixture(); var op = MemoryOperation(f); var saved = op.Request.Clone();
        f.Reply = (_, _) => throw new HttpRequestException("Synthetic disconnect after acceptance");
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.SubmitMemoryAsync(op))).Code);
        Assert.Equal("operation_already_attempted", (await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.SubmitMemoryAsync(op))).Code);
        f.Scope = ExecutionFixture.Scope(revision: "2");
        var restored = f.Control.RestoreMemoryOperation(saved, op.Scope);
        f.Reply = (_, _) => Task.FromResult(Response(MemoryResponse(Session(), Receipt(saved))));
        await f.Control.QueryMemoryAsync(restored);
        Assert.Equal(new[] { "POST", "GET" }, f.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task ConfigurationLostResponseRequiresExplicitExactKeyReplay()
    {
        using var f = new Fixture(); var op = f.Control.CreateConfigurationOperation(Session(), "configure-1", 2, Changes());
        f.Reply = (_, _) => throw new HttpRequestException("Synthetic disconnect");
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.ApplyConfigurationAsync(op))).Code);
        await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.ApplyConfigurationAsync(op));
        f.Reply = (_, _) => Task.FromResult(Response(Configuration(Session(), true, status: "replayed")));
        var restored = f.Control.RestoreConfigurationOperation(op.Request, op.Scope);
        Assert.Equal("replayed", (await f.Control.ReplayConfigurationAsync(restored)).GetProperty("status").GetString());
        Assert.Equal(WireJson.CanonicalString(f.Requests[0].Body!.Value), WireJson.CanonicalString(f.Requests[1].Body!.Value));
    }

    [Fact]
    public async Task NullMemoryReceiptOnlyAllowedForQueryAndNeverCausesResubmit()
    {
        using var f = new Fixture(); var op = MemoryOperation(f);
        f.Reply = (_, _) => Task.FromResult(Response(MemoryResponse(Session(), null)));
        Assert.Equal(JsonValueKind.Null, (await f.Control.QueryMemoryAsync(op)).GetProperty("receipt").ValueKind);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.SubmitMemoryAsync(op))).Code);
        Assert.Equal(2, f.Requests.Count);
    }

    [Theory]
    [InlineData("requestId")]
    [InlineData("operationId")]
    [InlineData("sourceId")]
    [InlineData("sourceGeneration")]
    [InlineData("expectedRevision")]
    public async Task BothSubmissionAndQueryRejectAnotherOperationReceipt(string field)
    {
        using var f = new Fixture(); var op = MemoryOperation(f);
        var receipt = ExecutionFixture.Set(Receipt(op.Request), field, JsonSerializer.SerializeToElement(field.EndsWith("Id", StringComparison.Ordinal) ? "other" : "0"));
        f.Reply = (_, _) => Task.FromResult(Response(MemoryResponse(Session(), receipt)));
        await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.SubmitMemoryAsync(op));
        await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.QueryMemoryAsync(op));
        Assert.Equal(2, f.Requests.Count);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("requestId")]
    [InlineData("revision")]
    [InlineData("model")]
    [InlineData("thinking")]
    public async Task ConfigurationResponseMustMatchOriginalCasAndActualRequestedChanges(string field)
    {
        using var f = new Fixture(); var op = f.Control.CreateConfigurationOperation(Session(), "configure-1", 2, Changes());
        var value = Configuration(Session(), true);
        if (field == "session") value = ExecutionFixture.Set(value, field, Session("another"));
        else if (field == "requestId") value = ExecutionFixture.Set(value, field, JsonSerializer.SerializeToElement("other"));
        else
        {
            var replacement = field == "revision" ? JsonSerializer.SerializeToElement(4) : field == "model" ? JsonSerializer.SerializeToElement("other") : JsonSerializer.SerializeToElement(new { budget = 1 });
            value = ExecutionFixture.Set(value, "configuration", ExecutionFixture.Set(value.GetProperty("configuration"), field, replacement));
        }
        f.Reply = (_, _) => Task.FromResult(Response(value));
        await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.ApplyConfigurationAsync(op));
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("revision_conflict")]
    [InlineData("stale_generation")]
    [InlineData("request_conflict")]
    public async Task OriginalTerminalErrorAndRetryAdviceRemainVisibleWithoutRetry(string code)
    {
        using var f = new Fixture(); var op = f.Control.CreateConfigurationOperation(Session(), "configure-1", 2, Changes());
        f.Reply = (_, _) => Task.FromResult(Response(JsonSerializer.SerializeToElement(new
        { contract = "terminal-services-v1", requestId = "failure", code, status = 409, retryAction = "none" }), HttpStatusCode.Conflict));
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => f.Control.ApplyConfigurationAsync(op));
        Assert.Equal(code, error.Code); Assert.Equal("none", error.RetryAction); Assert.Single(f.Requests);
        Assert.DoesNotContain("synthetic-only-token", error.ToString());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("application")]
    public async Task RejectsCrossOwnerMemoryAndRetainedOperationBeforeAnotherHttpRequest(string field)
    {
        using var f = new Fixture(); var op = MemoryOperation(f);
        f.Reply = (_, _) => Task.FromResult(Response(Memory(Session(), user: field == "user" ? "other" : "user", application: field == "application" ? "other" : "app")));
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.ReadMemoryAsync(Session()))).Code);
        f.Scope = field == "user" ? ExecutionFixture.Scope(user: "other") : ExecutionFixture.Scope(application: "other");
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.QueryMemoryAsync(op))).Code);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task AuthorizationChangeDuringResponseRejectsEvenIfBodyLooksSuccessful()
    {
        using var f = new Fixture();
        f.Reply = (_, _) => { f.Scope = ExecutionFixture.Scope(revision: "2"); return Task.FromResult(Response(Configuration(Session()))); };
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.ReadConfigurationAsync(Session()))).Code);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task InvalidRequestsAndPreCancellationNeverReachHttp()
    {
        using var f = new Fixture();
        Assert.Throws<TansrProtocolException>(() => new TerminalControlClient(f.Client));
        Assert.Throws<TansrProtocolException>(() => f.Control.CreateConfigurationOperation(Session(), "configure-1", 2, JsonSerializer.SerializeToElement(new { })));
        Assert.Throws<TansrProtocolException>(() => f.Control.CreateConfigurationOperation(Session(), "configure-1", 9007199254740991L, Changes()));
        await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.ReadConfigurationAsync(Session("..")));
        await Assert.ThrowsAsync<TansrProtocolException>(() => f.Control.ReadMemoryAsync(Session(family: "sdk2-offload-v1")));
        Assert.Throws<TansrProtocolException>(() => f.Control.CreateMemoryOperation(Session(), Memory(Session()), "request-1", ".", Command()));
        Assert.Throws<TansrProtocolException>(() => f.Control.CreateMemoryOperation(Session(), Memory(Session(), available: false), "request-1", "operation-1", Command()));
        var op = MemoryOperation(f);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Control.SubmitMemoryAsync(op, new CancellationToken(true)));
        Assert.False(op.Attempted); Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task ImmutableOperationsSurviveCallerDocumentDisposalAndSdk2KeepsItsFamily()
    {
        using var f = new Fixture(SessionContract.Sdk2OffloadV1);
        TerminalConfigurationOperation op;
        using (var changes = JsonDocument.Parse("{\"model\":\"trusted-alias\",\"thinking\":null}"))
            op = f.Control.CreateConfigurationOperation(Session(family: "sdk2-offload-v1"), "configure-1", 2, changes.RootElement);
        f.Reply = (_, _) => Task.FromResult(Response(Configuration(Session(family: "sdk2-offload-v1"), true)));
        await f.Control.ApplyConfigurationAsync(op);
        Assert.Equal("sdk2-offload-v1", f.Requests.Single().Body!.Value.GetProperty("session").GetProperty("sessionContract").GetString());
    }

    [Theory]
    [InlineData("schemaSha256")]
    [InlineData("schemaRevision")]
    [InlineData("uninstalled")]
    [InlineData("unsupported-family")]
    public async Task CandidateDiscoveryPrecedesControlsAndRejectsDrift(string difference)
    {
        using var f = new Fixture();
        if (difference == "uninstalled")
        {
            var caps = TerminalTestAdapter.Node(f.Capabilities);
            foreach (var feature in caps["features"]!.AsArray()) feature!["installed"] = false;
            f.Capabilities = TerminalTestAdapter.Element(caps);
        }
        else if (difference == "unsupported-family")
            f.Capabilities = ExecutionFixture.Set(f.Capabilities, "sessionContracts", JsonSerializer.SerializeToElement(new[] { "sdk2-offload-v1" }));
        else f.Capabilities = ExecutionFixture.Set(f.Capabilities, difference, JsonSerializer.SerializeToElement(difference == "schemaSha256" ? new string('0', 64) : "2026-09-26.candidate-6"));
        var error = await Record.ExceptionAsync(() => f.Control.ReadConfigurationAsync(Session()));
        Assert.True(error is TansrProtocolException or WireProtocolException);
        Assert.Equal(1, f.Discoveries); Assert.Empty(f.Requests);
    }
}
