using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class WindowsMemoryPublicationHostTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"memory\":\"合成设备记忆🙂\"}");
    private static readonly string Digest = WireJson.Sha256(Body);
    private const string ToolDigest = "8532a582d40d2d8993a80db59412a89671670ed7f994eaf9bf972bd544a111b0";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExplicitPreviewAndRealSqlitePublicationRoundTripPreserveOriginalOwner()
    {
        using var f = new Fixture(); using var store = await f.Open();
        Assert.Equal("ENOTSUP", Assert.Throws<ExecutionRejectedException>(() => new WindowsMemoryPublicationHost(store)).Code);
        var host = new WindowsMemoryPublicationHost(store, enablePreview: true); var backend = f.Backend(host);
        Assert.Equal("TansrTerminalMemoryPublication", host.CreateTool().Name); Assert.Equal(ToolDigest, host.CreateTool().DefinitionDigest);
        Assert.Equal(JsonValueKind.Null, Response(await Call(backend, Request("head"))).GetProperty("publication").ValueKind);
        await Stage(backend);
        var committed = Response(await Call(backend, Request("commit", transfer: "transfer")));
        Assert.Equal("committed", committed.GetProperty("transfer").GetProperty("status").GetString());
        Assert.Equal(Digest, committed.GetProperty("transfer").GetProperty("etag").GetString());
        var head = Response(await Call(backend, Request("head"))).GetProperty("publication");
        Assert.Equal(Body.Length, head.GetProperty("byteLength").GetInt32());
        var read = Response(await Call(backend, Request("read")));
        Assert.Equal(Body, Convert.FromBase64String(read.GetProperty("base64").GetString()!)); Assert.True(read.GetProperty("complete").GetBoolean());
        var replay = Response(await Call(backend, Request("commit", transfer: "transfer")));
        Assert.Equal(committed.GetRawText(), replay.GetRawText());
    }

    [Theory]
    [InlineData("application")]
    [InlineData("user")]
    [InlineData("sourceId")]
    [InlineData("sourceGeneration")]
    [InlineData("domainKey")]
    public async Task ForeignScopeOrSourceNeverTouchesPublication(string field)
    {
        using var f = new Fixture(); using var store = await f.Open(); var backend = f.Backend(new WindowsMemoryPublicationHost(store, true));
        var request = Request("begin", transfer: "foreign");
        if (field.StartsWith("source", StringComparison.Ordinal) || field == "domainKey") request = Set(request, field, Element(field == "sourceGeneration" ? "2" : "foreign"));
        var operation = Operation(request, user: field == "user" ? "other-user" : "user", application: field == "application" ? "other-app" : "app");
        Assert.Equal("EACCES", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(operation, Allow, CancellationToken.None))).Code);
        var query = Response(await Call(backend, Request("query", transfer: "foreign")));
        Assert.Equal("unknown", query.GetProperty("transfer").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("toolName")]
    [InlineData("definitionDigest")]
    [InlineData("operationDigest")]
    [InlineData("inputMismatch")]
    public async Task ForgedProfileAndDifferentDirectInputDoNotAcquireMemoryAuthority(string attack)
    {
        using var f = new Fixture(); using var store = await f.Open(); var host = new WindowsMemoryPublicationHost(store, true);
        var input = Request("begin", transfer: "foreign");
        var operation = Operation(input, toolName: attack == "toolName" ? "Shell" : "MemoryPublication", toolDigest: attack == "definitionDigest" ? new string('a', 64) : ToolDigest);
        if (attack == "operationDigest") operation = Set(operation, "digest", Element(new string('b', 64)));
        if (attack == "inputMismatch") input = Request("begin", transfer: "other");
        await Assert.ThrowsAnyAsync<Exception>(() => Direct(host, input, operation, f.Workspace, Allow, CancellationToken.None));
        var backend = f.Backend(host);
        Assert.Equal("unknown", Response(await Call(backend, Request("query", transfer: "foreign"))).GetProperty("transfer").GetProperty("status").GetString());
    }

    [Fact]
    public async Task CanonicalEquivalentArgumentsAndDedicatedPreAndPostGuardsAreAccepted()
    {
        using var f = new Fixture(); using var store = await f.Open(); var host = new WindowsMemoryPublicationHost(store, true);
        var input = Request("head"); var reordered = Element(new { domainKey = "device", sourceGeneration = "1", sourceId = "source", action = "head", contract = "terminal-services-v1" });
        int guards = 0;
        var result = await Direct(host, reordered, Operation(input), f.Workspace, _ => { guards++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, guards); Assert.Equal("ok", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TransferOwnerIncludesSessionAndBindingAndCannotBeBorrowed()
    {
        using var f = new Fixture(); using var store = await f.Open(); var backend = f.Backend(new WindowsMemoryPublicationHost(store, true));
        await Call(backend, Request("begin", transfer: "transfer"));
        var otherSession = Tool(await backend.ExecuteAsync(Operation(Request("chunk", transfer: "transfer"), session: "other"), Allow, CancellationToken.None));
        Assert.Equal("error", otherSession.GetProperty("status").GetString()); Assert.Equal("request_conflict", otherSession.GetProperty("message").GetString());
        var otherBinding = Tool(await backend.ExecuteAsync(Operation(Request("query", transfer: "transfer"), binding: "other"), Allow, CancellationToken.None));
        Assert.Equal("error", otherBinding.GetProperty("status").GetString());
        Assert.Equal(0, Response(await Call(backend, Request("query", transfer: "transfer"))).GetProperty("transfer").GetProperty("receivedBytes").GetInt32());
    }

    [Fact]
    public async Task CancellationBeforeIoDoesNotStageAndCancellationAfterIoIsUnconfirmed()
    {
        using var f = new Fixture(); using var store = await f.Open(); var host = new WindowsMemoryPublicationHost(store, true); var backend = f.Backend(host);
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Direct(host, Request("begin", transfer: "before"), Operation(Request("begin", transfer: "before")), f.Workspace, Allow, before.Token));
        Assert.Equal("unknown", Response(await Call(backend, Request("query", transfer: "before"))).GetProperty("transfer").GetProperty("status").GetString());
        using var after = new CancellationTokenSource(); int guards = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => Direct(host, Request("begin", transfer: "after"), Operation(Request("begin", transfer: "after")), f.Workspace,
            _ => { if (++guards == 2) after.Cancel(); return Task.CompletedTask; }, after.Token));
        Assert.Equal("memory_publication_outcome_unconfirmed", error.Message);
        Assert.Equal("staging", Response(await Call(backend, Request("query", transfer: "after"))).GetProperty("transfer").GetProperty("status").GetString());
    }

    [Fact]
    public async Task DefiniteBusinessRejectionRemainsDefiniteAfterAuthorizationWithdrawal()
    {
        using var f = new Fixture(); using var store = await f.Open(); var host = new WindowsMemoryPublicationHost(store, true); var backend = f.Backend(host);
        await Call(backend, Request("begin", transfer: "transfer"));
        var badChunk = Set(Request("chunk", transfer: "transfer"), "payloadDigest", Element(new string('0', 64)));
        var errorResult = Tool(await Call(backend, badChunk));
        Assert.Equal("error", errorResult.GetProperty("status").GetString()); Assert.Equal("integrity_mismatch", errorResult.GetProperty("message").GetString());
        int guards = 0;
        Assert.Equal("ESTALE", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => Direct(host, badChunk, Operation(badChunk), f.Workspace,
            _ => { if (++guards == 2) throw new OperationCanceledException(); return Task.CompletedTask; }, CancellationToken.None))).Code);
        Assert.Equal(0, Response(await Call(backend, Request("query", transfer: "transfer"))).GetProperty("transfer").GetProperty("receivedBytes").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedCommittedPublicationUsesOriginalDurableUnknownReceiptWithoutReexecution(bool sqliteCommitUnknown)
    {
        using var f = new Fixture(); var store = await f.Open();
        try
        {
            var backend = f.Backend(new WindowsMemoryPublicationHost(store, true)); await Stage(backend);
            var operation = Operation(Request("commit", transfer: "transfer"), id: "commit-operation");
            using var journal = await SqliteExecutorJournal.OpenAsync(f.JournalOptions());
            var client = new Client(operation); var counted = new Counted(backend); bool rejectPostGuard = !sqliteCommitUnknown;
            var connection = (SqliteConnection)typeof(SqliteMemoryPublicationStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            int commits = 0; WalHook hook = (_, _, _, _) => { commits++; return 10; };
            if (sqliteCommitUnknown) SqliteWalHook(connection.Handle!.DangerousGetHandle(), hook, IntPtr.Zero);
            using var execution = new ExecutionHost(client, counted, journal, async (original, ct) =>
            {
                if (!rejectPostGuard) return;
                var head = await store.ExecuteAsync(Request("head"), Owner(original), ct);
                if (head.GetProperty("publication").ValueKind != JsonValueKind.Null) throw new ExecutionRejectedException("EACCES");
            });
            var run = execution.RunAsync();
            try
            {
                var first = await client.Receipts.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
                Assert.Equal("unknown", first.GetProperty("status").GetString()); Assert.Equal("execution_outcome_unknown", first.GetProperty("errorCode").GetString());
                Assert.Equal("unknown", (await journal.ReceiptAsync(operation))!.Value.GetProperty("status").GetString());
                rejectPostGuard = false;
                client.Enqueue(operation);
                var second = await client.Receipts.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
                // 账本将原完整回执按协议规范化落盘；对象字段顺序不属于回执身份。
                Assert.Equal(WireJson.CanonicalString(first), WireJson.CanonicalString(second)); Assert.Equal(1, counted.Calls);
                if (sqliteCommitUnknown) Assert.Equal(1, commits);
            }
            finally
            {
                SqliteWalHook(connection.Handle!.DangerousGetHandle(), null, IntPtr.Zero); GC.KeepAlive(hook);
                await execution.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline);
            }
        }
        finally { store.Dispose(); }
        using var reopened = await f.Open(StorageOpenMode.Reopen);
        var current = f.Backend(new WindowsMemoryPublicationHost(reopened, true));
        Assert.Equal("committed", Response(await Call(current, Request("query", transfer: "transfer"))).GetProperty("transfer").GetProperty("status").GetString());
        Assert.Equal(Digest, Response(await Call(current, Request("head"))).GetProperty("publication").GetProperty("etag").GetString());
    }

    private static async Task Stage(WindowsExecutorBackend backend)
    { await Call(backend, Request("begin", transfer: "transfer")); await Call(backend, Request("chunk", transfer: "transfer")); }
    private static Task<JsonElement> Call(WindowsExecutorBackend backend, JsonElement request) => backend.ExecuteAsync(Operation(request), Allow, CancellationToken.None);
    private static Task Allow(CancellationToken _) => Task.CompletedTask;
    private static JsonElement Tool(JsonElement resource) => WireJson.Parse(Encoding.UTF8.GetBytes(resource.GetProperty("args").GetProperty("resultJson").GetString()!), 32768);
    private static JsonElement Response(JsonElement resource) => WireJson.Parse(Encoding.UTF8.GetBytes(Tool(resource).GetProperty("content")[0].GetProperty("text").GetString()!), 32768);
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Scope(string user = "user", string app = "app") => Element(new { applicationScopeId = app, endUserId = user, authorizationRevision = "1" });
    private static JsonElement Request(string action, string? transfer = null)
    {
        var value = new Dictionary<string, object?> { ["contract"] = "terminal-services-v1", ["sourceId"] = "source", ["sourceGeneration"] = "1", ["domainKey"] = "device", ["action"] = action };
        if (transfer != null) value["transferId"] = transfer;
        if (action == "begin") { value["expectedEtag"] = null; value["byteLength"] = Body.Length; value["sha256"] = Digest; }
        if (action == "chunk") { value["offset"] = 0; value["base64"] = Convert.ToBase64String(Body); value["byteLength"] = Body.Length; value["payloadDigest"] = Digest; }
        if (action == "read") { value["etag"] = Digest; value["offset"] = 0; value["length"] = 12288; }
        return Element(value);
    }
    private static JsonElement Operation(JsonElement request, string? id = null, string user = "user", string application = "app", string session = "session", string binding = "binding", string toolName = "MemoryPublication", string toolDigest = ToolDigest)
    {
        var unsigned = Element(new
        {
            protocol = "sdk2-ext-v1",
            operationId = id ?? Guid.NewGuid().ToString("N"),
            sessionId = session,
            scope = Scope(user, application),
            binding = new { bindingId = binding, revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } },
            toolName,
            request = new { operation = "tool.invoke", args = new { name = "TansrTerminalMemoryPublication", definitionDigest = toolDigest, argsJson = request.GetRawText() } },
            expiresAt = "2099-01-01T00:00:00.000Z",
        });
        return Set(unsigned, "digest", Element(WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(unsigned, 1048576))));
    }
    private static JsonElement Set(JsonElement value, string name, JsonElement replacement)
    { var fields = value.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone()); fields[name] = replacement; return Element(fields); }
    private static string Owner(JsonElement operation) => WireJson.CanonicalString(Element(new { scope = operation.GetProperty("scope"), sessionId = operation.GetProperty("sessionId"), binding = operation.GetProperty("binding") }));
    private static Task<JsonElement> Direct(WindowsMemoryPublicationHost host, JsonElement input, JsonElement operation, WindowsWorkspace workspace, Func<CancellationToken, Task> guard, CancellationToken ct)
    {
        var context = Activator.CreateInstance(typeof(WindowsBusinessToolContext), BindingFlags.Instance | BindingFlags.NonPublic, null, [operation, workspace, guard], null)!;
        return (Task<JsonElement>)typeof(WindowsMemoryPublicationHost).GetMethod("InvokeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [input, context, ct])!;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-memory-host-" + Guid.NewGuid().ToString("N"));
        internal WindowsWorkspace Workspace { get; }
        internal Fixture() { Directory.CreateDirectory(directory); Workspace = new WindowsWorkspace(directory); }
        internal Task<SqliteMemoryPublicationStore> Open(StorageOpenMode mode = StorageOpenMode.Create) => SqliteMemoryPublicationStore.OpenAsync(new SqliteMemoryPublicationOptions
        {
            EnablePreview = true,
            Path = Path.Combine(directory, "publication.sqlite"),
            Mode = mode,
            Identity = Element(new { scope = new { applicationScopeId = "app", endUserId = "user" }, sourceId = "source", sourceGeneration = "1", domainKey = "device" }),
            ReadContext = () => Scope(),
            MaxTransfers = 32,
        });
        internal WindowsExecutorBackend Backend(WindowsMemoryPublicationHost host) => new("executor", [new WindowsExecutorWorkspace("workspace", "1", Workspace)], [host.CreateTool()]);
        internal SqliteExecutorJournalOptions JournalOptions() => new()
        { Path = Path.Combine(directory, "journal.sqlite"), Mode = StorageOpenMode.Create, ApplicationScopeId = "app", EndUserId = "user", ExecutorId = "executor", ReadContext = () => Scope() };
        public void Dispose() { Workspace.Dispose(); Directory.Delete(directory, true); }
    }
    private sealed class Counted(IExecutionBackend inner) : IExecutionBackend
    {
        internal int Calls;
        public JsonElement Registration => inner.Registration;
        public Task<JsonElement> ExecuteAsync(JsonElement operation, Func<CancellationToken, Task> guard, CancellationToken ct)
        { Calls++; return inner.ExecuteAsync(operation, guard, ct); }
    }
    private sealed class Client : IExecutionClient
    {
        private readonly Channel<JsonElement> operations = Channel.CreateUnbounded<JsonElement>();
        private readonly JsonElement original;
        internal readonly Channel<JsonElement> Receipts = Channel.CreateUnbounded<JsonElement>();
        internal Client(JsonElement operation) { original = operation.Clone(); Enqueue(operation); }
        internal void Enqueue(JsonElement operation) => operations.Writer.TryWrite(operation);
        public JsonElement ReadScope() => Scope();
        public Task<JsonElement> RegisterAsync(JsonElement registration, CancellationToken ct) => Task.FromResult(Element(new
        { protocol = "sdk2-ext-v1", executorId = "executor", connectionId = "connection", connectionRevision = "1", expiresAt = "2099-01-01T00:00:00.000Z", heartbeatAfterMs = 30000 }));
        public Task<JsonElement> HeartbeatAsync(JsonElement connection, CancellationToken ct) => Task.FromResult(connection);
        public async Task<JsonElement> PollAsync(JsonElement connection, CancellationToken ct) => Element(new
        { protocol = "sdk2-ext-v1", executorId = "executor", connectionId = "connection", operations = new[] { await operations.Reader.ReadAsync(ct) } });
        public Task<JsonElement> SubmitAsync(JsonElement receipt, CancellationToken ct) { Receipts.Writer.TryWrite(receipt); return Task.FromResult(default(JsonElement)); }
        public Task<JsonElement> GetStatusAsync(string sessionId, string operationId, CancellationToken ct) => Task.FromResult(Element(new
        { protocol = "sdk2-ext-v1", operation = original, status = "pending", receipt = (object?)null }));
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WalHook(IntPtr argument, IntPtr database, IntPtr name, int pages);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_wal_hook", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SqliteWalHook(IntPtr database, WalHook? callback, IntPtr argument);
}
