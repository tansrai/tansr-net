using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Tests.Execution;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class WindowsBackgroundHostTests : IClassFixture<WindowsProcessTestProgram>, IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-background-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsWorkspace workspace;
    private readonly WindowsProcessTestProgram program;
    private readonly List<WindowsBackgroundHost> hosts = [];
    public WindowsBackgroundHostTests(WindowsProcessTestProgram program)
    {
        this.program = program; Directory.CreateDirectory(directory);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var current = WindowsIdentity.GetCurrent().User!; security.SetOwner(current);
        security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        workspace = new WindowsWorkspace(directory);
    }

    [Fact]
    public async Task LaunchIsWitnessedAndIncrementalArtifactCanBeReadCanceledAndDeleted()
    {
        var (host, backend) = Host();
        var launched = await Call(backend, Launch("sleep"), "launch"); var task = launched.GetProperty("task");
        Assert.Equal("running", task.GetProperty("state").GetString());
        Assert.Equal("launch", task.GetProperty("identity").GetProperty("operationId").GetString());
        JsonElement read = default;
        await Until(async () => { read = await Call(backend, Request("outputRead", task), Guid.NewGuid().ToString("N")); return read.GetProperty("byteLength").GetInt32() != 0; });
        Assert.Equal("ready", Encoding.UTF8.GetString(Convert.FromBase64String(read.GetProperty("base64").GetString()!)));
        Assert.False(read.GetProperty("complete").GetBoolean()); Assert.False(read.GetProperty("gap").GetBoolean());
        Assert.Equal("EACCES", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => Call(backend, Request("artifactDelete", task), "delete-running"))).Code);
        var canceled = await Call(backend, Request("cancel", task), "cancel");
        Assert.Equal("cancelled", canceled.GetProperty("task").GetProperty("state").GetString());
        var deleted = await Call(backend, Request("artifactDelete", task), "delete"); Assert.Equal("deleted", deleted.GetProperty("status").GetString());
        Assert.Equal("already_deleted", (await Call(backend, Request("artifactDelete", task), "delete-again")).GetProperty("status").GetString());
        var gap = await Call(backend, Request("outputRead", task), "read-deleted"); Assert.True(gap.GetProperty("gap").GetBoolean());
        await host.CloseAsync(); Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task DuplicateOriginalLaunchDoesNotStartAgainAndRestartDoesNotInventWitness()
    {
        var (first, backend) = Host(); var operation = Operation(Launch("sleep"), "launch");
        var one = Parse(await backend.ExecuteAsync(operation, _ => Task.CompletedTask, CancellationToken.None));
        var two = Parse(await backend.ExecuteAsync(operation, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(one.GetRawText(), two.GetRawText());
        var old = one.GetProperty("task"); await first.CloseAsync();
        var (second, replacement) = Host();
        var query = await Call(replacement, Request("query", old), "query-old");
        Assert.Equal("unknown", query.GetProperty("task").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, query.GetProperty("task").GetProperty("totalBytes").ValueKind);
        await Assert.ThrowsAsync<ExecutionRejectedException>(() => Call(replacement, Request("cancel", old), "cancel-old"));
        await second.CloseAsync();
    }

    [Fact]
    public async Task OutputCapKeepsProcessAliveAndPreservesGapAtTheEnd()
    {
        var (_, backend) = Host(maximumBytes: 32);
        var task = (await Call(backend, Launch("slow-flood"), "launch")).GetProperty("task");
        JsonElement state = default;
        await Until(async () => { state = (await Call(backend, Request("query", task), Guid.NewGuid().ToString("N"))).GetProperty("task"); return state.GetProperty("truncated").GetBoolean(); });
        Assert.Equal("running", state.GetProperty("state").GetString());
        var canceled = await Call(backend, Request("cancel", task), "cancel");
        Assert.Equal("cancelled", canceled.GetProperty("task").GetProperty("state").GetString());
        var read = await Call(backend, Request("outputRead", task), "read");
        Assert.False(read.GetProperty("complete").GetBoolean());
        var end = JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", action = "outputRead", identity = task.GetProperty("identity"), artifact = task.GetProperty("artifact"), offset = read.GetProperty("nextOffset").GetString(), length = 8 });
        Assert.True((await Call(backend, end, "read-end")).GetProperty("gap").GetBoolean());
    }

    [Fact]
    public async Task PerPipeUtf8IsPreservedAndKnownExitCompletesArtifact()
    {
        var (_, backend) = Host(chunkBytes: 1);
        var task = (await Call(backend, Launch("unicode"), "launch")).GetProperty("task");
        JsonElement state = default;
        await Until(async () => { state = (await Call(backend, Request("query", task), Guid.NewGuid().ToString("N"))).GetProperty("task"); return state.GetProperty("state").GetString() != "running"; });
        Assert.Equal("completed", state.GetProperty("state").GetString()); Assert.False(state.GetProperty("truncated").GetBoolean());
        var read = await Call(backend, Request("outputRead", task), "read"); string output = Encoding.UTF8.GetString(Convert.FromBase64String(read.GetProperty("base64").GetString()!));
        Assert.Contains("中文🙂", output); Assert.Contains("error-stream", output); Assert.Contains("done", output);
        Assert.True(read.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task CrossSessionAndForgedDigestCannotReadOrCancelTask()
    {
        var (_, backend) = Host(); var task = (await Call(backend, Launch("sleep"), "launch")).GetProperty("task");
        var other = Operation(Request("query", task), "cross", "other-session");
        Assert.Equal("EACCES", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(other, _ => Task.CompletedTask, CancellationToken.None))).Code);
        var forged = Operation(Request("query", task), "forged").GetRawText().Replace("\"toolName\":\"Shell\"", "\"toolName\":\"Other\"");
        await Assert.ThrowsAnyAsync<Exception>(() => backend.ExecuteAsync(JsonDocument.Parse(forged).RootElement, _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task CapacityAndWorkspaceCwdApplyBeforeAnyNewProcess()
    {
        var (_, backend) = Host(maximumTasks: 1);
        await Call(backend, Launch("sleep"), "launch");
        Assert.Equal("EFBIG", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => Call(backend, Launch("sleep"), "second"))).Code);
        var (_, safe) = Host();
        var unsafeCwd = JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", action = "launch", command = "sleep", cwd = "../outside", mode = "background", timeoutMs = 10000 });
        await Assert.ThrowsAnyAsync<Exception>(() => Call(safe, unsafeCwd, "escape"));
    }

    [Fact]
    public async Task CancelingLaunchTokenAfterAckDoesNotOwnBackgroundLifetime()
    {
        using var launchToken = new CancellationTokenSource(); var (host, backend) = Host();
        var value = Parse(await backend.ExecuteAsync(Operation(Launch("sleep"), "launch"), _ => Task.CompletedTask, launchToken.Token));
        launchToken.Cancel();
        var state = (await Call(backend, Request("query", value.GetProperty("task")), "query")).GetProperty("task");
        Assert.Equal("running", state.GetProperty("state").GetString());
        await host.CloseAsync();
    }

    private (WindowsBackgroundHost, WindowsExecutorBackend) Host(int maximumBytes = 8192, int maximumTasks = 16, int chunkBytes = 4096)
    {
        var options = new WindowsBackgroundHostOptions(directory, (request, space) => new WindowsProcessRequest(program.Executable,
            [request.GetProperty("command").GetString()!], () => space.AcquireProcessDirectory())
        { ChunkBytes = chunkBytes, MaxPendingChunks = 128 })
        { CandidateRevision = WindowsBackgroundHost.CandidateRevision, MaximumTasks = maximumTasks, MaximumArtifactBytes = maximumBytes };
        var host = new WindowsBackgroundHost(options); hosts.Add(host);
        return (host, new WindowsExecutorBackend("executor", [new WindowsExecutorWorkspace("workspace", "1", workspace)], [host.CreateTool()]));
    }
    private static JsonElement Launch(string command) => JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", action = "launch", command, cwd = "", mode = "background", timeoutMs = 10000 });
    private static JsonElement Request(string action, JsonElement task) => action == "query" || action == "cancel"
        ? JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", action, identity = task.GetProperty("identity") })
        : action == "artifactDelete"
        ? JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", action, identity = task.GetProperty("identity"), artifact = task.GetProperty("artifact") })
        : JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", action, identity = task.GetProperty("identity"), artifact = task.GetProperty("artifact"), offset = "0", length = 16384 });
    private static JsonElement Operation(JsonElement args, string id, string session = "session")
    {
        var fields = new Dictionary<string, object?>
        {
            ["protocol"] = "sdk2-ext-v1",
            ["operationId"] = id,
            ["sessionId"] = session,
            ["scope"] = new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" },
            ["binding"] = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } },
            ["toolName"] = "Shell",
            ["request"] = new { operation = "tool.invoke", args = new { name = "TansrTerminalBackground", definitionDigest = "03848c0c75af6e2e358ac64f81a61c79f595d8fc70442d06ae3c0576e39157b8", argsJson = args.GetRawText() } },
            ["expiresAt"] = "2099-01-01T00:00:00.000Z",
        };
        fields["digest"] = WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(JsonSerializer.SerializeToElement(fields), 1048576));
        return JsonSerializer.SerializeToElement(fields);
    }
    private static async Task<JsonElement> Call(WindowsExecutorBackend backend, JsonElement args, string id) => Parse(await backend.ExecuteAsync(Operation(args, id), _ => Task.CompletedTask, CancellationToken.None));
    private static JsonElement Parse(JsonElement result) => JsonDocument.Parse(JsonDocument.Parse(result.GetProperty("args").GetProperty("resultJson").GetString()!).RootElement.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    private static async Task Until(Func<Task<bool>> condition)
    { var watch = Stopwatch.StartNew(); while (!await condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("后台状态未达到。"); await Task.Delay(30); } }
    public void Dispose() { foreach (var host in hosts) host.CloseAsync().GetAwaiter().GetResult(); workspace.Dispose(); Directory.Delete(directory, true); }
}
