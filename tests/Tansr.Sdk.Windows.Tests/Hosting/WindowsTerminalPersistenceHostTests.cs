using System.Reflection;
using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class WindowsTerminalPersistenceHostTests
{
    [Theory]
    [InlineData("body")]
    [InlineData("body-page")]
    public async Task InjectedReadCorruptionMustRemainUnconfirmed(string part)
    {
        using var fixture = new Fixture(); var store = new Store();
        var request = Json(new Dictionary<string, object?> { ["contract"] = "terminal-persistence-v1", ["sourceId"] = "source", ["sourceGeneration"] = "1", ["domainKey"] = "domain", ["action"] = "read", ["part"] = part, ["commitRoot"] = new string('a', 64) }
            .Concat(part == "body" ? new Dictionary<string, object?> { ["offset"] = 0, ["length"] = 12288 } : new Dictionary<string, object?> { ["pageIndex"] = 0 }).ToDictionary(x => x.Key, x => x.Value));
        var fields = request.EnumerateObject().Where(x => x.Name != "length").ToDictionary(x => x.Name, x => x.Value.Clone());
        fields["byteLength"] = Json(1); fields["base64"] = Json("AQ=="); fields["payloadDigest"] = Json(new string('b', 64));
        if (part == "body") { fields["bodyEtag"] = Json(new string('a', 64)); fields["nextOffset"] = Json(1); fields["complete"] = Json(true); }
        store.Response = Json(fields);
        var host = new WindowsTerminalPersistenceHost(store, true);
        Assert.Equal("terminal_persistence_outcome_unconfirmed", (await Assert.ThrowsAsync<IOException>(() => Direct(host, request, Operation(request), fixture.Workspace, _ => Task.CompletedTask))).Message);
        Assert.Equal(1, store.Calls); Assert.Equal(0, store.Closes);
    }

    [Fact]
    public async Task RevocationAfterStorageDoesNotProduceCompletedOrReplay()
    {
        using var fixture = new Fixture(); var store = new Store(); var request = Query();
        store.Response = Json(new { contract = "terminal-persistence-v1", sourceId = "source", sourceGeneration = "1", domainKey = "domain", action = "query", transfer = new { transferId = "original", status = "unknown" } });
        int guards = 0; var host = new WindowsTerminalPersistenceHost(store, true);
        await Assert.ThrowsAsync<IOException>(() => Direct(host, request, Operation(request), fixture.Workspace, _ => { if (++guards == 2) throw new ExecutionRejectedException("ESTALE"); return Task.CompletedTask; }));
        Assert.Equal(1, store.Calls); Assert.Equal(2, guards); Assert.Equal(0, store.Closes);
    }

    [Fact]
    public async Task ForeignScopeCannotAcquireMemoryPermissionOrTouchStorage()
    {
        using var fixture = new Fixture(); var store = new Store(); var request = Query();
        var host = new WindowsTerminalPersistenceHost(store, true);
        await Assert.ThrowsAsync<ExecutionRejectedException>(() => Direct(host, request, Operation(request, "foreign"), fixture.Workspace, _ => Task.CompletedTask));
        Assert.Equal(0, store.Calls);
        Assert.Throws<ExecutionRejectedException>(() => new WindowsTerminalPersistenceHost(store));
        store.Encrypted = false; Assert.Throws<ExecutionRejectedException>(() => new WindowsTerminalPersistenceHost(store, true));
    }

    [Theory]
    [InlineData("commit", "revision_conflict")]
    [InlineData("commit", "request_conflict")]
    [InlineData("commit", "invalid_request")]
    [InlineData("commit", "integrity_mismatch")]
    [InlineData("query", "revision_conflict")]
    public async Task DurableRejectionUsesErrorExceptOriginalQuery(string action, string code)
    {
        using var fixture = new Fixture(); var store = new Store();
        var request = Json(new { contract = "terminal-persistence-v1", sourceId = "source", sourceGeneration = "1", domainKey = "domain", action, transferId = "original", intentSha256 = new string('c', 64) });
        store.Response = Rejected(action, code); int guards = 0;
        var response = await Direct(new WindowsTerminalPersistenceHost(store, true), request, Operation(request), fixture.Workspace, _ => { guards++; return Task.CompletedTask; });
        Assert.Equal(action == "query" ? "ok" : "error", response.GetProperty("status").GetString());
        if (action == "query")
        {
            using var body = JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal("rejected", body.RootElement.GetProperty("transfer").GetProperty("status").GetString());
            Assert.Equal(code, body.RootElement.GetProperty("transfer").GetProperty("rejection").GetProperty("code").GetString());
        }
        else { Assert.Equal(code, response.GetProperty("message").GetString()); Assert.False(response.TryGetProperty("content", out _)); }
        Assert.Equal(1, store.Calls); Assert.Equal(2, guards);
    }

    [Fact]
    public async Task DurableRejectionDoesNotHidePostStorageRevocationOrMalformedResult()
    {
        using var fixture = new Fixture(); var store = new Store();
        var request = Json(new { contract = "terminal-persistence-v1", sourceId = "source", sourceGeneration = "1", domainKey = "domain", action = "commit", transferId = "original", intentSha256 = new string('c', 64) });
        store.Response = Rejected("commit", "revision_conflict"); int guards = 0;
        var host = new WindowsTerminalPersistenceHost(store, true);
        await Assert.ThrowsAsync<IOException>(() => Direct(host, request, Operation(request), fixture.Workspace, _ => { if (++guards == 2) throw new ExecutionRejectedException("ESTALE"); return Task.CompletedTask; }));
        store.Response = Rejected("commit", "not_a_contract_code");
        await Assert.ThrowsAsync<IOException>(() => Direct(host, request, Operation(request), fixture.Workspace, _ => Task.CompletedTask));
        Assert.Equal(2, store.Calls);
    }

    private static JsonElement Rejected(string action, string code) => Json(new
    {
        contract = "terminal-persistence-v1",
        sourceId = "source",
        sourceGeneration = "1",
        domainKey = "domain",
        action,
        transfer = new { transferId = "original", intentSha256 = new string('c', 64), status = "rejected", progress = (object?)null, result = (object?)null, rejection = new { code, observedRoot = (object?)null } },
    });

    private static JsonElement Query() => Json(new { contract = "terminal-persistence-v1", sourceId = "source", sourceGeneration = "1", domainKey = "domain", action = "query", transferId = "original", intentSha256 = new string('c', 64) });
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Operation(JsonElement request, string user = "user")
    {
        var value = Json(new
        {
            protocol = "sdk2-ext-v1",
            operationId = "original",
            sessionId = "session",
            scope = new { applicationScopeId = "app", endUserId = user, authorizationRevision = "1" },
            binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } },
            toolName = "MemoryPublication",
            request = new { operation = "tool.invoke", args = new { name = "TansrTerminalPersistenceV1", definitionDigest = "33029a264edf81f3fda2a13fc382403d0cd7ffefa1f38088bb9366f387a13587", argsJson = WireJson.CanonicalString(request) } },
            expiresAt = "2099-01-01T00:00:00.000Z"
        });
        var fields = value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()); fields["digest"] = Json(WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(value, 1048576))); return Json(fields);
    }
    private static Task<JsonElement> Direct(WindowsTerminalPersistenceHost host, JsonElement request, JsonElement operation, WindowsWorkspace workspace, Func<CancellationToken, Task> guard)
    {
        Func<WindowsProcessRequest, CancellationToken, Task<WindowsProcessResult>> noProcess = (_, _) => throw new InvalidOperationException("No process authority");
        var context = Activator.CreateInstance(typeof(WindowsBusinessToolContext), BindingFlags.Instance | BindingFlags.NonPublic, null, [operation, workspace, guard, noProcess, false], null)!;
        return (Task<JsonElement>)typeof(WindowsTerminalPersistenceHost).GetMethod("InvokeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [request, context, CancellationToken.None])!;
    }
    private sealed class Store : ITerminalPersistenceStore
    {
        internal bool Encrypted = true; internal int Calls, Closes; internal JsonElement Response;
        public bool AtomicDurablePersistence => true; public bool EncryptedState => Encrypted;
        public JsonElement Identity => Json(new { applicationScopeId = "app", endUserId = "user", sourceId = "source", sourceGeneration = "1", domainKey = "domain" });
        public Task<JsonElement> ExecuteAsync(JsonElement request, string ownerCanonical, CancellationToken ct = default) { Calls++; return Task.FromResult(Response); }
        public Task CloseAsync(CancellationToken ct = default) { Closes++; return Task.CompletedTask; }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "pst-net-host-" + Guid.NewGuid().ToString("N"));
        internal WindowsWorkspace Workspace { get; }
        internal Fixture() { Directory.CreateDirectory(directory); Workspace = new WindowsWorkspace(directory); }
        public void Dispose() { Workspace.Dispose(); Directory.Delete(directory, true); }
    }
}
