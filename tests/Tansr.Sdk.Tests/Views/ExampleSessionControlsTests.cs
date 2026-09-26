using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Execution;
using Tansr.Sdk.Tests.Terminal;

namespace Tansr.Sdk.Tests.Views;

public sealed class ExampleSessionControlsTests
{
    [Theory]
    [InlineData(false, "revision_conflict", "refresh")]
    [InlineData(false, "busy", "backoff")]
    [InlineData(true, "revision_conflict", "refresh")]
    [InlineData(true, "busy", "backoff")]
    public async Task DefiniteFirstRefusalSurvivesRestartAndOnlyExplicitNewActionCreatesNewRequest(bool memory, string code, string retryAction)
    {
        using var f = new Fixture(); var controls = f.Controls();
        f.Post = _ => Error(code, 409, retryAction);
        Assert.Equal(code, (await Assert.ThrowsAsync<TansrHttpException>(() => Submit(controls, memory))).Code);
        var original = Assert.Single(f.Posts);
        var archive = Assert.Single(Directory.GetFiles(f.DirectoryPath, "*.rejected.*.json"));
        var archivedBytes = File.ReadAllBytes(archive);
        using (var document = JsonDocument.Parse(archivedBytes))
        {
            Assert.Equal(WireJson.CanonicalString(original), WireJson.CanonicalString(document.RootElement.GetProperty("request")));
            Assert.Equal(code, document.RootElement.GetProperty("rejection").GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("result").ValueKind);
        }
        controls = f.Controls();
        Assert.Contains(code, controls.DescribePending());
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => Replay(controls, memory));
        Assert.EndsWith("_rejected_start_new_explicitly", retry.Message);
        Assert.Single(f.Posts);

        f.Revision = 8; f.Post = f.Success;
        await Submit(controls, memory);
        Assert.Equal(2, f.Posts.Count);
        Assert.NotEqual(original.GetProperty("requestId").GetString(), f.Posts[1].GetProperty("requestId").GetString());
        if (memory)
        {
            Assert.NotEqual(original.GetProperty("operationId").GetString(), f.Posts[1].GetProperty("operationId").GetString());
            Assert.Equal("8", f.Posts[1].GetProperty("expectedRevision").GetString());
        }
        else Assert.Equal(8, f.Posts[1].GetProperty("expectedRevision").GetInt64());
        Assert.Equal(archivedBytes, File.ReadAllBytes(archive));
        Assert.Contains(code, controls.DescribePending());
        Assert.Contains(original.GetProperty("requestId").GetString()!, controls.DescribePending());
        Assert.DoesNotContain("synthetic-only-token", controls.DescribePending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusalDuringReplayCannotResolveAnEarlierUnknownAttempt(bool memory)
    {
        using var f = new Fixture(); var controls = f.Controls();
        f.Post = _ => throw new HttpRequestException("Synthetic lost response after acceptance");
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => Submit(controls, memory))).Code);
        var original = Assert.Single(f.Posts).GetRawText();
        controls = f.Controls();
        f.Post = _ => Error("revision_conflict", 409, "refresh");
        await Assert.ThrowsAsync<TansrHttpException>(() => Replay(controls, memory));
        Assert.Equal(original, f.Posts[1].GetRawText());
        Assert.EndsWith("_original_operation_unresolved", (await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(controls, memory))).Message);
        Assert.Equal(2, f.Posts.Count);
        Assert.Empty(Directory.GetFiles(f.DirectoryPath, "*.rejected.*.json"));
        Assert.DoesNotContain("\"rejection\"", controls.DescribePending());
    }

    [Theory]
    [InlineData("forbidden", 403, "none")]
    [InlineData("stale_generation", 409, "refresh")]
    [InlineData("source_unavailable", 503, "backoff")]
    public async Task MemoryPostCommitAuthorityFailureRequiresOriginalReceiptQuery(string code, int status, string retryAction)
    {
        using var f = new Fixture(); var controls = f.Controls();
        f.Post = _ => Error(code, status, retryAction);
        await Assert.ThrowsAsync<TansrHttpException>(() => controls.CommandAsync("pin", "synthetic preference"));
        var original = Assert.Single(f.Posts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.CommandAsync("pin", "another preference"));
        Assert.Empty(Directory.GetFiles(f.DirectoryPath, "*.rejected.*.json"));

        f.QueryReceipt = original;
        await controls.QueryMemoryAsync();
        Assert.Single(f.Posts);
        Assert.Contains(Uri.EscapeDataString(original.GetProperty("operationId").GetString()!), Assert.Single(f.Queries));
        f.Post = f.Success;
        await controls.CommandAsync("pin", "explicit new preference");
        Assert.Equal(2, f.Posts.Count);
    }

    [Fact]
    public async Task MissingMemoryReceiptIsStillUnknownAndNeverEnablesReplacement()
    {
        using var f = new Fixture(); var controls = f.Controls();
        f.Post = _ => throw new HttpRequestException("Synthetic disconnect");
        await Assert.ThrowsAsync<TansrProtocolException>(() => controls.CommandAsync("remember"));
        Assert.Equal(JsonValueKind.Null, (await controls.QueryMemoryAsync()).GetProperty("receipt").ValueKind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.CommandAsync("remember"));
        Assert.Single(f.Posts); Assert.Single(f.Queries);
        Assert.Empty(Directory.GetFiles(f.DirectoryPath, "*.rejected.*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryRefusalBeforeFirstPostAllowsOnlyExplicitNewAction(bool memory)
    {
        using var f = new Fixture(); var controls = f.Controls();
        f.RejectDiscoveryNumber = 2; // State read succeeds; discovery before the control POST rejects.
        Assert.Equal("unsupported_capability", (await Assert.ThrowsAsync<TansrProtocolException>(() => Submit(controls, memory))).Code);
        Assert.Empty(f.Posts);
        Assert.Contains("before_control_post", controls.DescribePending());
        Assert.Single(Directory.GetFiles(f.DirectoryPath, "*.rejected.*.json"));
        f.RejectDiscoveryNumber = 0;
        await Submit(f.Controls(), memory);
        Assert.Single(f.Posts);
    }

    [Fact]
    public async Task UnrecognizedRefusalAdviceDoesNotAuthorizeNewRequest()
    {
        using var f = new Fixture(); var controls = f.Controls();
        f.Post = _ => Error("revision_conflict", 409, "reconcile");
        await Assert.ThrowsAsync<TansrHttpException>(() => Submit(controls, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(controls, false));
        Assert.Single(f.Posts);
        Assert.Empty(Directory.GetFiles(f.DirectoryPath, "*.rejected.*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherInstanceCannotReplayWhileFirstOutcomeIsBeingClassified(bool memory)
    {
        using var f = new Fixture(); var controls = f.Controls();
        f.PostAsync = async _ =>
        {
            await Assert.ThrowsAsync<IOException>(() => Replay(f.Controls(), memory));
            return Error("busy", 409, "backoff");
        };
        await Assert.ThrowsAsync<TansrHttpException>(() => Submit(controls, memory));
        Assert.Single(f.Posts);
        f.PostAsync = null; f.Post = f.Success;
        await Submit(f.Controls(), memory);
        Assert.Equal(2, f.Posts.Count);
    }

    private static Task<JsonElement> Submit(ExampleSessionControls controls, bool memory) => memory
        ? controls.CommandAsync("pin", "synthetic preference")
        : controls.ChangeConfigurationAsync(JsonSerializer.SerializeToElement(new { model = "trusted-alias" }));
    private static Task<JsonElement> Replay(ExampleSessionControls controls, bool memory) => memory
        ? controls.ReplayMemoryAsync() : controls.ReplayConfigurationAsync();
    private static JsonElement Session => JsonSerializer.SerializeToElement(new { sessionContract = "sdk1", sessionId = "session" });
    private static HttpResponseMessage Error(string code, int status, string retryAction) => Response(JsonSerializer.SerializeToElement(new
    { contract = "terminal-services-v1", requestId = "original-failure", code, status, retryAction }), (HttpStatusCode)status);
    private static HttpResponseMessage Response(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(WireJson.CanonicalString(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }

    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "tansr-example-controls-" + Guid.NewGuid().ToString("N"));
        internal readonly List<JsonElement> Posts = [];
        internal readonly List<string> Queries = [];
        internal long Revision = 2;
        internal int RejectDiscoveryNumber;
        internal JsonElement? QueryReceipt;
        internal Func<JsonElement, HttpResponseMessage> Post;
        internal Func<JsonElement, Task<HttpResponseMessage>>? PostAsync;
        private int discoveries;
        private readonly HttpClient http;
        private readonly TansrClient client;
        private readonly TerminalSessionControl control;
        internal Fixture()
        {
            Directory.CreateDirectory(DirectoryPath); Post = Success;
            http = new HttpClient(new Handler(async (request, ct) =>
            {
                Assert.Equal("synthetic-only-token", request.Headers.Authorization!.Parameter);
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/v3/terminal/capabilities")
                {
                    var caps = new TerminalTestAdapter().Capabilities;
                    var installed = ++discoveries != RejectDiscoveryNumber;
                    foreach (var feature in caps["features"]!.AsArray()) { feature!["supported"] = true; feature["installed"] = installed; }
                    return Response(TerminalTestAdapter.Element(caps));
                }
                if (request.Method == HttpMethod.Post)
                {
                    var body = WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(ct));
                    Posts.Add(body); return PostAsync is null ? Post(body) : await PostAsync(body);
                }
                if (path.EndsWith("/configuration", StringComparison.Ordinal)) return Response(Configuration());
                if (path.EndsWith("/memory", StringComparison.Ordinal)) return Response(Memory());
                Queries.Add(request.RequestUri.PathAndQuery);
                return Response(MemoryResponse(QueryReceipt));
            }));
            client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri("http://127.0.0.1:34567/"),
                AllowInsecureLoopback = true,
                TokenProvider = _ => Task.FromResult("synthetic-only-token"),
                PrincipalProvider = () => "fixture-principal",
                ExecutionScopeProvider = () => ExecutionFixture.Scope()
            }, http);
            control = new TerminalSessionControl(client, enablePreview: true);
        }
        internal ExampleSessionControls Controls() => new(control, "http://127.0.0.1:34567/", "session", Path.Combine(DirectoryPath, "state.json"));
        private JsonElement Configuration(JsonElement? request = null) => TerminalJson.Object(w =>
        {
            w.WriteString("contract", "terminal-services-v1"); TerminalJson.Field(w, "session", Session);
            w.WritePropertyName("configuration"); w.WriteStartObject();
            w.WriteNumber("revision", request.HasValue ? request.Value.GetProperty("expectedRevision").GetInt64() + 1 : Revision);
            w.WriteString("model", "trusted-alias"); w.WriteNull("thinking"); w.WriteEndObject();
            if (request.HasValue) { w.WriteString("requestId", request.Value.GetProperty("requestId").GetString()); w.WriteString("status", "changed"); }
        });
        private JsonElement Memory() => JsonSerializer.SerializeToElement(new
        {
            contract = "terminal-services-v1",
            session = Session,
            memory = new
            {
                identity = new { kind = "developer-managed", domain = "app/user", sourceId = "source", sourceGeneration = "1", applicationScopeId = "app", endUserId = "user" },
                revision = Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                deletionGeneration = "0",
                available = true
            }
        });
        internal HttpResponseMessage Success(JsonElement request) => Response(request.TryGetProperty("operationId", out _) ? MemoryResponse(request) : Configuration(request));
        private static JsonElement MemoryResponse(JsonElement? request) => TerminalJson.Object(w =>
        {
            w.WriteString("contract", "terminal-services-v1"); TerminalJson.Field(w, "session", Session); w.WritePropertyName("receipt");
            if (!request.HasValue) { w.WriteNullValue(); return; }
            w.WriteStartObject();
            foreach (var key in new[] { "requestId", "operationId", "sourceId", "sourceGeneration", "expectedRevision" }) TerminalJson.Field(w, key, request.Value.GetProperty(key));
            w.WriteString("status", "committed"); w.WriteBoolean("executionComplete", true); w.WriteBoolean("durable", true); w.WriteBoolean("consumed", false);
            w.WriteString("publishedRevision", (long.Parse(request.Value.GetProperty("expectedRevision").GetString()!, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteEndObject();
        });
        public void Dispose()
        {
            client.Dispose(); http.Dispose(); Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
