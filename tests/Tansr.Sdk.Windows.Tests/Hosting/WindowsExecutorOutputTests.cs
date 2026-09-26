using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Tests.Execution;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class WindowsExecutorOutputTests : IClassFixture<WindowsProcessTestProgram>, IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "tansr-output-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsWorkspace workspace;
    private readonly WindowsProcessTestProgram program;
    private static readonly JsonElement Interpreter = JsonSerializer.SerializeToElement(new { id = "synthetic", revision = "1", hostShell = "powershell" });
    public WindowsExecutorOutputTests(WindowsProcessTestProgram program)
    { this.program = program; Directory.CreateDirectory(path); workspace = new WindowsWorkspace(path); }

    [Fact]
    public async Task RawOutputCrossesSdkSinkWhileTheNativeProcessStillRuns()
    {
        var releaseName = @"Local\tansr-output-sink-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        var sink = new Sink(); var backend = Backend(sink, releaseName);
        var run = backend.ExecuteAsync(Operation(), _ => Task.CompletedTask, CancellationToken.None);
        JsonElement result;
        try
        {
            await sink.Capture.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
        }
        finally { release.Set(); result = await run; }
        Assert.Equal("中文🙂done", result.GetProperty("args").GetProperty("stdout").GetString());
        Assert.True(sink.Capture.Chunks.Count > 2);
        Assert.Equal("中文🙂done", System.Text.Encoding.UTF8.GetString(sink.Capture.Chunks.Where(x => x.Channel == "stdout").SelectMany(x => x.Bytes).ToArray()));
        Assert.True(sink.Capture.Sealed); Assert.True(sink.Capture.Disposed); Assert.Null(backend.LastOutputFailure);
    }

    [Fact]
    public async Task LostOutputSealDoesNotReplaceKnownProcessSuccessWithAnExecutionRetry()
    {
        var sink = new Sink(); sink.Capture.FailSeal = true;
        var backend = Backend(sink); var failures = new List<WindowsExecutionOutputFailure>(); backend.OutputFailed += failures.Add;
        var result = await backend.ExecuteAsync(Operation(), _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("0", result.GetProperty("args").GetProperty("exitCode").GetString());
        Assert.Equal("operation", Assert.Single(failures).OperationId);
        Assert.Equal("output_transfer_unconfirmed", backend.LastOutputFailure!.Code);
        Assert.True(sink.Capture.Disposed);
    }

    [Fact]
    public async Task RejectedChunkSealsATruncatedCaptureWithoutDiscardingKnownProcessSuccess()
    {
        var sink = new Sink(); sink.Capture.RejectAppend = true;
        var backend = Backend(sink);
        var result = await backend.ExecuteAsync(Operation(), _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("0", result.GetProperty("args").GetProperty("exitCode").GetString());
        Assert.True(sink.Capture.Truncated);
        Assert.True(sink.Capture.Sealed);
        Assert.Null(backend.LastOutputFailure);
    }

    [Fact]
    public async Task UnsettledSealsKeepTheirCapacityUntilTheyActuallyFinish()
    {
        var sink = new UnsettledSink();
        var backend = Backend(sink);
        var runs = Enumerable.Range(0, 8).Select(_ => backend.ExecuteAsync(Operation(), _ => Task.CompletedTask, CancellationToken.None)).ToArray();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(8, sink.Count);
        Assert.All(runs, run => Assert.Equal("0", run.Result.GetProperty("args").GetProperty("exitCode").GetString()));
        var error = await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(Operation(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("output_transport_capacity", error.Code);
        Assert.Equal(8, sink.Count);
        sink.Finish.TrySetResult(true);
        await sink.Finish.Task;
        // Continuations release existing leases; wait only for their completion, without launching retry executions.
        await Task.Delay(50);
        var next = await backend.ExecuteAsync(Operation(), _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("0", next.GetProperty("args").GetProperty("exitCode").GetString());
        Assert.Equal(9, sink.Count);
    }

    [Fact]
    public async Task OutputPreflightFailureDoesNotStartAProcess()
    {
        var sink = new Sink { FailOpen = true }; var backend = Backend(sink);
        var error = await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(Operation(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("output_transport_unavailable", error.Code);
        Assert.Empty(sink.Capture.Chunks);
    }

    private WindowsExecutorBackend Backend(IExecutionOutputSink sink, string? releaseName = null) => new("executor",
        new[] { new WindowsExecutorWorkspace("workspace", "1", workspace) }, interpreter: Interpreter,
        processFactory: (_, space) => new WindowsProcessRequest(program.Executable, releaseName == null ? ["unicode"] : ["unicode", releaseName], () => space.AcquireProcessDirectory("")) { ChunkBytes = 1, MaxPendingChunks = 128 }, executionOutput: sink);
    private static JsonElement Operation() => JsonSerializer.SerializeToElement(new
    {
        protocol = "sdk2-ext-v1",
        operationId = "operation",
        sessionId = "session",
        digest = new string('a', 64),
        scope = new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" },
        binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1", interpreter = Interpreter } },
        toolName = "Bash",
        request = new { operation = "process.exec", args = new { command = "synthetic fixture", cwd = "", timeoutMs = 10000, maxOutputBytes = 4096, interpreter = Interpreter } },
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
    });
    private sealed class Sink : IExecutionOutputSink
    {
        public Capture Capture = new(); public bool FailOpen;
        public Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken ct)
        { WireJson.ValidateNamed("ExecutionOperation", operation); if (FailOpen) throw new IOException("synthetic failure"); return Task.FromResult<IExecutionOutputCapture>(Capture); }
    }
    private sealed class Capture : IExecutionOutputCapture
    {
        public List<(string Channel, byte[] Bytes)> Chunks = [];
        public TaskCompletionSource<bool> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Sealed, Disposed, FailSeal, RejectAppend, Truncated;
        public Task Completion => Task.CompletedTask;
        public bool Append(string channel, string encoding, byte[] bytes)
        { Assert.Equal("utf-8", encoding); Chunks.Add((channel, (byte[])bytes.Clone())); First.TrySetResult(true); return !RejectAppend; }
        public Task SealAsync(bool truncated, CancellationToken ct)
        { Truncated = truncated; Sealed = true; if (FailSeal) throw new IOException("synthetic lost seal"); return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
    private sealed class UnsettledSink : IExecutionOutputSink
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public TaskCompletionSource<bool> Finish = new();
        public Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken ct)
        { Interlocked.Increment(ref count); return Task.FromResult<IExecutionOutputCapture>(new UnsettledCapture(Finish.Task)); }
    }
    private sealed class UnsettledCapture(Task completion) : IExecutionOutputCapture
    {
        public Task Completion => completion;
        public bool Append(string channel, string encoding, byte[] bytes) => true;
        public Task SealAsync(bool truncated, CancellationToken ct) => completion;
        public void Dispose() { }
    }
    public void Dispose() { workspace.Dispose(); Directory.Delete(path, true); }
}
