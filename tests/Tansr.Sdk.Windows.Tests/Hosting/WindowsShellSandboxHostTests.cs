using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Storage;
using Tansr.Sdk.Windows.Tests.Execution;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class WindowsShellSandboxHostTests : IClassFixture<WindowsProcessTestProgram>, IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-shell-sandbox-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsProcessTestProgram program;
    private readonly WindowsWorkspace workspace;
    private readonly List<WindowsShellSandboxHost> hosts = [];
    private readonly List<SqliteExecutorJournal> journals = [];
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Interpreter => Json(new { id = "approved", revision = "1", hostShell = "powershell" });
    private static JsonElement Scope => Json(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" });

    public WindowsShellSandboxHostTests(WindowsProcessTestProgram program)
    {
        this.program = program; Directory.CreateDirectory(directory);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user); security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security); workspace = new WindowsWorkspace(directory);
    }

    [Theory]
    [InlineData(WindowsSandboxMode.On, true, "partial", "selected")]
    [InlineData(WindowsSandboxMode.Required, true, "partial", "selected")]
    [InlineData(WindowsSandboxMode.On, false, "none", "fake-secretselected")]
    [InlineData(WindowsSandboxMode.Off, true, "partial", "fake-secretselected")]
    public async Task WindowsHygieneMatchesOriginalCapabilityAndRealChildEnvironment(WindowsSandboxMode mode, bool installed, string capability, string expected)
    {
        var runtime = installed ? new WindowsHygieneSandboxRuntime() : null;
        var (_, backend, journal) = await Host(() => new(mode, new([directory], false), runtime));
        var result = Parse(await Execute(backend, journal, Operation(Request("call"), "operation")));
        Assert.Equal(expected, result.GetProperty("result").GetProperty("stdout").GetString());
        Assert.Equal(capability, result.GetProperty("sandbox").GetProperty("capability").GetString());
        Assert.False(result.GetProperty("sandbox").GetProperty("escalated").GetBoolean());
        if (runtime != null) Assert.Equal("env_hygiene_only", runtime.CapabilityReason);
    }

    [Fact]
    public async Task RequiredUnavailableAndLocalDenialHaveNoNativeSideEffects()
    {
        var (_, backend, journal) = await Host(() => new(WindowsSandboxMode.Required, new([directory], false)));
        var operation = Operation(Request("call"), "required"); await journal.ClaimAsync(operation);
        Assert.Equal("ENOTSUP", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(operation, _ => Task.CompletedTask, default))).Code);
        Assert.Empty(Directory.GetFiles(directory, "launches.txt"));
        var (_, guarded, guardedJournal) = await Host(() => new(WindowsSandboxMode.On, new([directory], false), new WindowsHygieneSandboxRuntime()));
        var denied = Operation(Request("call"), "local-denied"); await guardedJournal.ClaimAsync(denied);
        Assert.Equal("local_denied", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => guarded.ExecuteAsync(denied, _ => throw new ExecutionRejectedException("local_denied"), default))).Code);
        Assert.Empty(Directory.GetFiles(directory, "launches.txt"));
    }

    [Fact]
    public async Task OriginalOutputCaptureKeepsFullUnicodeBeyondReceiptPreviewAndExactDigests()
    {
        var sink = new OutputCapture();
        var (_, backend, journal) = await Host(() => new(WindowsSandboxMode.On, new([directory], false)), sink);
        var original = Operation(Request("full", command: "large-unicode", maximum: 65536), "full-output");
        var response = Parse(await Execute(backend, journal, original)); var receipt = response.GetProperty("result"); var output = response.GetProperty("output");
        var stdout = Encoding.UTF8.GetBytes(new string('中', 6000) + "🙂\uFFFD"); var stderr = Encoding.UTF8.GetBytes(new string('错', 2000) + "🙂");
        Assert.True(output.GetProperty("previewTruncated").GetBoolean());
        Assert.True(Encoding.UTF8.GetByteCount(receipt.GetProperty("stdout").GetString()! + receipt.GetProperty("stderr").GetString()!) <= 4096);
        foreach (var (channel, bytes) in new[] { ("stdout", stdout), ("stderr", stderr) })
        {
            Assert.Equal(bytes, sink.Chunks.Where(item => item.Channel == channel).SelectMany(item => item.Bytes).ToArray());
            Assert.Equal(bytes.Length, output.GetProperty(channel).GetProperty("totalBytes").GetInt32());
            Assert.Equal(WireJson.Sha256(bytes), output.GetProperty(channel).GetProperty("payloadDigest").GetString());
        }
        Assert.Equal(original.GetProperty("digest").GetString(), sink.Operation.GetProperty("digest").GetString());
        Assert.False(sink.Truncated); Assert.True(sink.Sealed); Assert.Single(await File.ReadAllLinesAsync(Path.Combine(directory, "launches.txt")));
    }

    [Fact]
    public async Task LargeOutputWithoutOriginalStreamRejectsBeforeSideEffectsAndBinaryCannotMasqueradeAsUtf8()
    {
        var (_, backend, journal) = await Host(() => new(WindowsSandboxMode.On, new([directory], false)));
        var original = Operation(Request("large", command: "large-unicode", maximum: 65536), "no-stream"); await journal.ClaimAsync(original);
        Assert.Equal("ENOTSUP", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(original, _ => Task.CompletedTask, default))).Code);
        Assert.False(File.Exists(Path.Combine(directory, "launches.txt")));
        var invalid = Operation(Request("binary", command: "binary"), "binary"); await journal.ClaimAsync(invalid);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => backend.ExecuteAsync(invalid, _ => Task.CompletedTask, default));
        Assert.Null(await journal.ReceiptAsync(invalid)); // 已执行的未知结果不得变造文字成功，也不得重授执行。
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await journal.ClaimAsync(invalid)).Status);
    }

    [Fact]
    public async Task PolicyRevokedWhileOriginalOutputTransportOpensCannotSpawnTheApprovedBypass()
    {
        var mode = WindowsSandboxMode.On; var armed = false; var runtime = new TrustedPreExecutionDenial();
        var sink = new OutputCapture { BeforeOpen = () => { if (armed) mode = WindowsSandboxMode.Required; } };
        var (_, backend, journal) = await Host(() => new(mode, new([directory], false), runtime), sink);
        await Execute(backend, journal, Operation(Request("original"), "a-denied"));
        armed = true;
        var operation = Operation(Request("approved", "a-denied"), "b-approved"); await journal.ClaimAsync(operation);
        Assert.Equal("ESTALE", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(operation, _ => Task.CompletedTask, default))).Code);
        Assert.False(File.Exists(Path.Combine(directory, "launches.txt")));
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await journal.ClaimAsync(operation)).Status);
    }

    [Fact]
    public async Task ExplicitOnceUsesOriginalDenialAndNeverConsumesAnotherOperationOrUac()
    {
        var runtime = new TrustedPreExecutionDenial();
        var (_, backend, journal) = await Host(() => new(WindowsSandboxMode.On, new([Path.Combine(directory, "elsewhere")], false), runtime));
        var denied = Operation(Request("original"), "a-denied"); var deniedResult = Parse(await Execute(backend, journal, denied));
        Assert.True(deniedResult.GetProperty("sandbox").GetProperty("isolationDenied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, deniedResult.GetProperty("result").ValueKind); Assert.False(File.Exists(Path.Combine(directory, "launches.txt")));
        var once = Operation(Request("approved", "a-denied"), "b-approved"); var result = Parse(await Execute(backend, journal, once));
        Assert.True(result.GetProperty("sandbox").GetProperty("escalated").GetBoolean());
        Assert.Equal("fake-secretselected", result.GetProperty("result").GetProperty("stdout").GetString());
        var replay = await journal.ClaimAsync(once); Assert.Equal(ExecutorJournalClaimStatus.Completed, replay.Status);
        var duplicate = Operation(Request("another-approved", "a-denied"), "c-reuse"); await journal.ClaimAsync(duplicate);
        Assert.Equal("EACCES", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(duplicate, _ => Task.CompletedTask, default))).Code);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(directory, "launches.txt")));
    }

    [Theory]
    [InlineData("required")]
    [InlineData("turn")]
    [InlineData("binding")]
    [InlineData("command")]
    [InlineData("unknown")]
    public async Task EscalationCannotBypassRequiredCrossIdentityOrUnknownEarlierAttempt(string mutation)
    {
        var mode = WindowsSandboxMode.On; var runtime = new TrustedPreExecutionDenial();
        var (_, backend, journal) = await Host(() => new(mode, new([Path.Combine(directory, "elsewhere")], false), runtime));
        var denied = Operation(Request("original"), "a-denied"); await Execute(backend, journal, denied);
        if (mutation == "required") mode = WindowsSandboxMode.Required;
        if (mutation == "unknown") await journal.ClaimAsync(Operation(Request("unknown", "a-denied"), "b-unknown"));
        var request = Request("approved", "a-denied", mutation == "turn" ? "other-turn" : "turn", mutation == "command" ? "changed" : "command");
        var operation = Operation(request, "c-try", mutation == "binding" ? "other-binding" : "binding"); await journal.ClaimAsync(operation);
        if (mutation == "required")
        {
            var response = Parse(await backend.ExecuteAsync(operation, _ => Task.CompletedTask, default));
            Assert.Equal("required", response.GetProperty("sandbox").GetProperty("mode").GetString());
            Assert.True(response.GetProperty("sandbox").GetProperty("isolationDenied").GetBoolean()); Assert.False(response.GetProperty("sandbox").GetProperty("escalated").GetBoolean());
        }
        else await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(operation, _ => Task.CompletedTask, default));
        Assert.False(File.Exists(Path.Combine(directory, "launches.txt")));
    }

    private async Task<(WindowsShellSandboxHost Host, WindowsExecutorBackend Backend, SqliteExecutorJournal Journal)> Host(Func<WindowsShellSandboxSettings> current, IExecutionOutputSink? sink = null)
    {
        var journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
        { Path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sqlite"), Mode = StorageOpenMode.Create, ApplicationScopeId = "app", EndUserId = "user", ExecutorId = "executor", ReadContext = () => Scope });
        journals.Add(journal);
        var background = new WindowsBackgroundHostOptions(directory, (request, location) =>
        {
            var command = request.GetProperty("command").GetString();
            var args = command is "large-unicode" or "binary" ? new[] { command } : ["environment-count", "PRIVATE_TOKEN"];
            var process = new WindowsProcessRequest(program.Executable, args!, () => location.AcquireProcessDirectory());
            process.Environment["PRIVATE_TOKEN"] = "fake-secret"; process.Environment["EXPLICIT_VALUE"] = "selected"; return process;
        })
        { CandidateRevision = WindowsBackgroundHost.CandidateRevision };
        var host = new WindowsShellSandboxHost(new(background, Interpreter, journal, current) { CandidateSchemaSha256 = WindowsShellSandboxHost.CandidateSchemaSha256 });
        hosts.Add(host);
        return (host, new WindowsExecutorBackend("executor", [new WindowsExecutorWorkspace("workspace", "1", workspace)], [host.CreateTool()], executionOutput: sink), journal);
    }
    private static async Task<JsonElement> Execute(WindowsExecutorBackend backend, SqliteExecutorJournal journal, JsonElement operation)
    {
        Assert.Equal(ExecutorJournalClaimStatus.Claimed, (await journal.ClaimAsync(operation)).Status);
        var result = await backend.ExecuteAsync(operation, _ => Task.CompletedTask, default);
        await journal.CompleteAsync(operation, Json(new
        {
            protocol = "sdk2-ext-v1",
            operationId = operation.GetProperty("operationId").GetString(),
            digest = operation.GetProperty("digest").GetString(),
            executorId = "executor",
            connectionId = "connection",
            status = "completed",
            result,
            errorCode = (string?)null
        }));
        return result;
    }
    private static JsonElement Parse(JsonElement result)
    {
        using var tool = JsonDocument.Parse(result.GetProperty("args").GetProperty("resultJson").GetString()!);
        using var response = JsonDocument.Parse(tool.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!); return response.RootElement.Clone();
    }
    private static JsonElement Request(string call, string? previous = null, string turn = "turn", string command = "command", int maximum = 4096)
    {
        var result = new Dictionary<string, object?>
        {
            ["contract"] = "terminal-shell-sandbox-v1",
            ["action"] = "exec",
            ["call"] = new { turnId = turn, toolCallId = call },
            ["command"] = command,
            ["cwd"] = "",
            ["timeoutMs"] = 10000,
            ["maxOutputBytes"] = maximum,
            ["interpreter"] = Interpreter
        };
        if (previous != null) result["escalation"] = new { previousDeniedOperationId = previous, approvedCall = new { turnId = turn, toolCallId = call, argsDigest = new string('a', 64) } };
        return Json(result);
    }
    private static JsonElement Operation(JsonElement request, string id, string binding = "binding")
    {
        var fields = new Dictionary<string, object?>
        {
            ["protocol"] = "sdk2-ext-v1",
            ["operationId"] = id,
            ["sessionId"] = "session",
            ["scope"] = Scope,
            ["binding"] = new { bindingId = binding, revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1", interpreter = Interpreter } },
            ["toolName"] = "Shell",
            ["request"] = new { operation = "tool.invoke", args = new { name = "TansrTerminalShellSandbox", definitionDigest = "058f31335cbb45a6ffe1b5ede8466013462c544c8108d687f41fa18447d51b96", argsJson = request.GetRawText() } },
            ["expiresAt"] = "2099-01-01T00:00:00.000Z"
        };
        fields["digest"] = WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(Json(fields), 1048576)); return Json(fields);
    }
    public void Dispose()
    { foreach (var host in hosts) host.CloseAsync().GetAwaiter().GetResult(); foreach (var journal in journals) journal.Dispose(); workspace.Dispose(); Directory.Delete(directory, true); }

    // 授权状态机夹具；实际 Windows provider 的 partial 能力在上面的真实子进程测试中单独核验。
    private sealed class TrustedPreExecutionDenial : IWindowsShellSandboxRuntime
    {
        public string Id => "fixture-preexecution-denial";
        public WindowsSandboxCapability Capability => WindowsSandboxCapability.Partial;
        public string CapabilityReason => "synthetic_denial_evidence";
        public WindowsProcessRequest Prepare(WindowsProcessRequest approvedProcess, string workingDirectory, WindowsSandboxPolicy policy)
            => throw new WindowsSandboxIsolationDeniedException();
    }
    private sealed class OutputCapture : IExecutionOutputSink, IExecutionOutputCapture
    {
        public List<(string Channel, byte[] Bytes)> Chunks = [];
        public JsonElement Operation; public bool Truncated, Sealed; public Action? BeforeOpen;
        public Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken ct)
        { Operation = operation.Clone(); BeforeOpen?.Invoke(); return Task.FromResult<IExecutionOutputCapture>(this); }
        public bool Append(string channel, string encoding, byte[] rawBytes)
        { Assert.Equal("utf-8", encoding); Chunks.Add((channel, (byte[])rawBytes.Clone())); Assert.True(Chunks.Sum(item => item.Bytes.Length) <= 65536); return true; }
        public Task SealAsync(bool captureTruncated, CancellationToken ct) { Truncated = captureTruncated; Sealed = true; return Task.CompletedTask; }
        public Task Completion => Task.CompletedTask;
        public void Dispose() { }
    }
}
