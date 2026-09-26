using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Tests.Execution;

public sealed class ExecutionClientTests
{
    private const string Protocol = "sdk2-ext-v1";
    private static readonly string Revision = new('a', 64);
    private static JsonObject Node(string value) => JsonNode.Parse(value)!.AsObject();
    private static JsonElement Element(JsonNode value)
    {
        using var doc = JsonDocument.Parse(value.ToJsonString());
        return doc.RootElement.Clone();
    }
    private static JsonObject Platform() => Node("""{"platform":"windows","arch":"x64","language":"csharp","runtimeVersion":"10.0","adapterVersion":"0.1"}""");
    private static JsonObject Scope(string authorization = "2", string user = "user", string application = "app") => new()
    {
        ["applicationScopeId"] = application,
        ["endUserId"] = user,
        ["authorizationRevision"] = authorization
    };
    private static JsonObject Target() => Node("""{"executorId":"executor","connectionId":"connection","connectionRevision":"1","workspaceId":"workspace","workspaceRevision":"1"}""");
    private static JsonObject Binding() => new() { ["bindingId"] = "binding", ["revision"] = "1", ["target"] = Target() };
    private static JsonObject Tool() => Node("""{"name":"Read","executionKind":"bound-device","available":true,"unavailableReason":null}""");
    private static JsonObject Capabilities() => new()
    {
        ["protocol"] = Protocol,
        ["sessionId"] = "session",
        ["platform"] = Platform(),
        ["capabilityRevision"] = Revision,
        ["effectiveTools"] = new JsonArray(Tool()),
        ["binding"] = Binding()
    };
    private static JsonObject Initialization() => new()
    {
        ["protocol"] = Protocol,
        ["sessionId"] = "session",
        ["platform"] = Platform(),
        ["requestedTools"] = new JsonArray("Read")
    };
    private static JsonObject BindRequest() => new()
    {
        ["protocol"] = Protocol,
        ["sessionId"] = "session",
        ["executorId"] = "executor",
        ["connectionId"] = "connection",
        ["workspaceId"] = "workspace",
        ["expectedCapabilityRevision"] = Revision
    };
    private static JsonObject Registration() => new()
    {
        ["protocol"] = Protocol,
        ["executorId"] = "executor",
        ["platform"] = Platform(),
        ["workspaces"] = new JsonArray(Node("""{"workspaceId":"workspace","revision":"1"}""")),
        ["operations"] = new JsonArray("fs.read")
    };
    private static JsonObject Connection() => Node("""{"protocol":"sdk2-ext-v1","executorId":"executor","connectionId":"connection","connectionRevision":"1","expiresAt":"2026-09-26T12:00:00.000Z","heartbeatAfterMs":1000}""");
    private static JsonObject Boundary() => new()
    {
        ["protocol"] = Protocol,
        ["sessionId"] = "session",
        ["revision"] = Revision,
        ["clientPlatform"] = Platform(),
        ["application"] = Node("""{"authorizationRevision":"2","policyTools":["Read"],"platformCapabilities":[],"executionProfile":"bound-device-v1"}"""),
        ["requestedTools"] = new JsonArray("Read"),
        ["executor"] = new JsonObject
        {
            ["executorId"] = "executor",
            ["connectionId"] = "connection",
            ["connectionRevision"] = "1",
            ["platform"] = Platform(),
            ["operations"] = new JsonArray("fs.read"),
            ["workspaces"] = new JsonArray(Node("""{"workspaceId":"workspace","revision":"1"}"""))
        },
        ["effectiveTools"] = new JsonArray(Tool()),
        ["binding"] = Binding()
    };
    private static JsonObject Operation(string authorization = "2", string user = "user", string application = "app")
    {
        var operation = new JsonObject
        {
            ["protocol"] = Protocol,
            ["operationId"] = "operation",
            ["sessionId"] = "session",
            ["scope"] = Scope(authorization, user, application),
            ["binding"] = Binding(),
            ["toolName"] = "Read",
            ["request"] = Node("""{"operation":"fs.read","args":{"path":"doc.txt","offset":0,"length":16}}"""),
            ["expiresAt"] = "2026-09-26T12:00:00.000Z"
        };
        operation["digest"] = WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(Element(operation)));
        return operation;
    }
    private static JsonObject Receipt(JsonObject operation) => new()
    {
        ["protocol"] = Protocol,
        ["executorId"] = "executor",
        ["connectionId"] = "connection",
        ["operationId"] = "operation",
        ["digest"] = operation["digest"]!.DeepClone(),
        ["status"] = "completed",
        ["result"] = Node("""{"operation":"fs.read","args":{"bytesBase64":"YQ=="}}"""),
        ["errorCode"] = null
    };
    private static JsonObject Status(JsonObject operation, JsonObject? receipt = null, string status = "pending") => new()
    {
        ["protocol"] = Protocol,
        ["operation"] = operation.DeepClone(),
        ["status"] = status,
        ["receipt"] = receipt?.DeepClone()
    };
    private static JsonObject Batch(params JsonObject[] operations) => new()
    {
        ["protocol"] = Protocol,
        ["executorId"] = "executor",
        ["connectionId"] = "connection",
        ["operations"] = new JsonArray(operations.Select(x => x.DeepClone()).ToArray())
    };
    private sealed record Request(string Method, string Path, JsonElement? Body, string? Authorization);
    private sealed class Handler(Func<Request, JsonObject> respond) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var observed = new Request(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : WireJson.DecodeControl(await request.Content.ReadAsByteArrayAsync(ct)), request.Headers.Authorization?.ToString());
            Requests.Add(observed);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(WireJson.EncodeControl(Element(respond(observed)))) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }
    private static TansrClient Client(HttpClient http, Func<JsonElement>? scope = null) => new(new TansrClientOptions
    {
        BaseUri = new Uri("https://serve.test/"),
        TokenProvider = _ => Task.FromResult("test-token"),
        PrincipalProvider = () => "app/user",
        ExecutionScopeProvider = scope ?? (() => Element(Scope()))
    }, http);

    [Fact]
    public async Task FrozenExecutionRoutesUseSharedAuthenticationAndCanonicalControlBodies()
    {
        var operation = Operation(); var receipt = Receipt(operation);
        using var handler = new Handler(request => request.Path switch
        {
            "/v2/sessions/session/execution-boundary?protocol=sdk2-ext-v1" => Boundary(),
            "/v2/executors/connections" or "/v2/executors/executor/heartbeats" => Connection(),
            "/v2/executors/executor/operations?protocol=sdk2-ext-v1&connectionId=connection" => Batch(operation),
            "/v2/executors/executor/receipts" or "/v2/sessions/session/executions/operation?protocol=sdk2-ext-v1" => Status(operation, receipt, "completed"),
            _ => Capabilities()
        });
        using var http = new HttpClient(handler); using var client = Client(http); var execution = new ExecutionClient(client);
        await execution.InitializeAsync(Element(Initialization()));
        await execution.GetExecutionCapabilitiesAsync("session");
        await execution.GetExecutionBoundaryAsync("session");
        await execution.BindExecutionAsync(Element(BindRequest()), Element(Target()));
        await execution.RegisterAsync(Element(Registration()));
        await execution.HeartbeatAsync(Element(Connection()));
        await execution.PollAsync(Element(Connection()));
        await execution.SubmitAsync(Element(receipt));
        await execution.GetStatusAsync("session", "operation");
        Assert.Equal(9, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer test-token", request.Authorization));
        Assert.Equal("/v2/sessions/session/initialize", handler.Requests[0].Path);
        Assert.Equal("/v2/sessions/session/execution-capabilities?protocol=sdk2-ext-v1", handler.Requests[1].Path);
        Assert.Equal("POST", handler.Requests[3].Method);
        Assert.Equal("/v2/sessions/session/execution-bindings", handler.Requests[3].Path);
        Assert.Equal("windows", handler.Requests[0].Body!.Value.GetProperty("platform").GetProperty("platform").GetString());
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("session")]
    [InlineData("duplicate-tools")]
    public async Task InitializationRejectsAResponseForAnotherPlatformSessionOrAmbiguousTool(string corruption)
    {
        var response = Capabilities();
        if (corruption == "platform") response["platform"]!["platform"] = "linux";
        else if (corruption == "session") response["sessionId"] = "foreign";
        else response["effectiveTools"]!.AsArray().Add(Tool());
        using var handler = new Handler(_ => response); using var http = new HttpClient(handler); using var client = Client(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionClient(client).InitializeAsync(Element(Initialization())));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("connectionRevision")]
    [InlineData("workspaceRevision")]
    [InlineData("interpreter")]
    public async Task BindingRequiresTheExactVerifiedTargetBeyondIds(string corruption)
    {
        var response = Capabilities();
        var target = response["binding"]!["target"]!;
        if (corruption == "interpreter") target[corruption] = Node("""{"id":"powershell","revision":"1","hostShell":"powershell"}""");
        else target[corruption] = "2";
        using var handler = new Handler(_ => response); using var http = new HttpClient(handler); using var client = Client(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionClient(client).BindExecutionAsync(Element(BindRequest()), Element(Target())));
    }

    [Fact]
    public async Task BoundaryCannotAttachAnExecutorToAnotherConnection()
    {
        var response = Boundary(); response["executor"]!["connectionRevision"] = "2";
        using var handler = new Handler(_ => response); using var http = new HttpClient(handler); using var client = Client(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionClient(client).GetExecutionBoundaryAsync("session"));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("invoke-without-tools")]
    [InlineData("tools-without-invoke")]
    [InlineData("duplicate-tool")]
    public async Task InvalidRegistrationCannotReachTheServer(string corruption)
    {
        var request = Registration();
        if (corruption == "workspace") request["workspaces"]!.AsArray().Add(Node("""{"workspaceId":"workspace","revision":"2"}"""));
        if (corruption is "invoke-without-tools" or "duplicate-tool") request["operations"] = new JsonArray("tool.invoke");
        if (corruption is "tools-without-invoke" or "duplicate-tool")
        {
            var tool = new JsonObject { ["name"] = "custom", ["definitionDigest"] = Revision };
            request["tools"] = new JsonArray(tool);
            if (corruption == "duplicate-tool") request["tools"]!.AsArray().Add(tool.DeepClone());
        }
        using var handler = new Handler(_ => Connection()); using var http = new HttpClient(handler); using var client = Client(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionClient(client).RegisterAsync(Element(request)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HistoricalReceiptsPermitOldAuthorizationRevisionForTheSameAppAndUser()
    {
        var operation = Operation("1"); var receipt = Receipt(operation); var response = Status(operation, receipt, "completed");
        using var handler = new Handler(_ => response); using var http = new HttpClient(handler); using var client = Client(http); var execution = new ExecutionClient(client);
        var read = await execution.GetStatusAsync("session", "operation");
        var submitted = await execution.SubmitAsync(Element(receipt));
        Assert.Equal("1", read.GetProperty("operation").GetProperty("scope").GetProperty("authorizationRevision").GetString());
        Assert.Equal(WireJson.CanonicalString(Element(receipt)), WireJson.CanonicalString(submitted.GetProperty("receipt")));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("application")]
    public async Task HistoryCannotExposeAnotherAppOrUsersOperation(string corruption)
    {
        var operation = Operation("1", corruption == "user" ? "foreign" : "user", corruption == "application" ? "foreign" : "app");
        var receipt = Receipt(operation);
        using var handler = new Handler(_ => Status(operation, receipt, "completed")); using var http = new HttpClient(handler); using var client = Client(http); var execution = new ExecutionClient(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => execution.GetStatusAsync("session", "operation"));
        await Assert.ThrowsAsync<InvalidDataException>(() => execution.SubmitAsync(Element(receipt)));
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("user")]
    [InlineData("connection")]
    [InlineData("duplicate")]
    [InlineData("digest")]
    public async Task PollNeverTurnsHistoricalOrCorruptDeliveryIntoCurrentExecution(string corruption)
    {
        var operation = Operation(corruption == "authorization" ? "1" : "2", corruption == "user" ? "foreign" : "user");
        if (corruption == "connection")
        {
            operation["binding"]!["target"]!["connectionRevision"] = "2";
            operation.Remove("digest");
            operation["digest"] = WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(Element(operation)));
        }
        if (corruption == "digest") operation["request"]!["args"]!["path"] = "different.txt";
        var response = corruption == "duplicate" ? Batch(operation, operation) : Batch(operation);
        using var handler = new Handler(_ => response); using var http = new HttpClient(handler); using var client = Client(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionClient(client).PollAsync(Element(Connection())));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("status")]
    [InlineData("digest")]
    public async Task AcknowledgmentMustConfirmTheOriginalReceiptAndOperation(string corruption)
    {
        var operation = Operation(); var receipt = Receipt(operation); var response = Status(operation, receipt, "completed");
        if (corruption == "receipt") response["receipt"]!["result"]!["args"]!["bytesBase64"] = "Yg==";
        else if (corruption == "status") response["status"] = "pending";
        else response["operation"]!["request"]!["args"]!["path"] = "tampered.txt";
        using var handler = new Handler(_ => response); using var http = new HttpClient(handler); using var client = Client(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionClient(client).SubmitAsync(Element(receipt)));
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("unsafe-directory-entry")]
    [InlineData("empty-tool-receipt")]
    [InlineData("exit-code-overflow")]
    public async Task MalformedReceiptCannotBePosted(string corruption)
    {
        var receipt = Receipt(Operation());
        if (corruption == "missing-result") receipt["result"] = null;
        else if (corruption == "unsafe-directory-entry") receipt["result"] = Node("""{"operation":"fs.list","args":{"entries":[{"name":"../escape","kind":"file"}]}}""");
        else if (corruption == "empty-tool-receipt") receipt["result"] = new JsonObject
        {
            ["operation"] = "tool.invoke",
            ["args"] = new JsonObject { ["resultJson"] = "{\"status\":\"ok\",\"content\":[]}" }
        };
        else receipt["result"] = Node("""{"operation":"process.exec","args":{"stdout":"","stderr":"","exitCode":"2147483648","signalName":null,"durationMs":0,"timedOut":false,"aborted":false}}""");
        using var handler = new Handler(_ => Status(Operation())); using var http = new HttpClient(handler); using var client = Client(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionClient(client).SubmitAsync(Element(receipt)));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task AFinishedStatusRequiresItsReceipt(string status)
    {
        using var handler = new Handler(_ => Status(Operation(), status: status)); using var http = new HttpClient(handler); using var client = Client(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionClient(client).GetStatusAsync("session", "operation"));
    }

    [Fact]
    public async Task AuthorizationChangingDuringTheHttpResponseInvalidatesTheResult()
    {
        var scope = Element(Scope());
        using var handler = new Handler(_ =>
        {
            scope = Element(Scope("3"));
            return Status(Operation());
        });
        using var http = new HttpClient(handler); using var client = Client(http, () => scope);
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => new ExecutionClient(client).GetStatusAsync("session", "operation"));
        Assert.Equal("context_changed", error.Code);
        Assert.Single(handler.Requests);
    }
}
