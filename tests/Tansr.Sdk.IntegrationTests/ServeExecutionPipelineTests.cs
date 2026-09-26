using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Storage;
using Tansr.Sdk.Windows.Tests.Execution;
using Xunit.Abstractions;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>原生进程、真实 Serve HTTP/SSE 与公开设备宿主同链；合成模型不替代协议或副作用。</summary>
public sealed class ServeExecutionPipelineTests(ITestOutputHelper output, WindowsProcessTestProgram program) : IClassFixture<WindowsProcessTestProgram>
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Scope => Json(new { applicationScopeId = "net-execution-app", endUserId = "net-integration-user", authorizationRevision = "1" });
    private static JsonElement Interpreter => Json(new { id = "net-native-fixture", revision = "1", hostShell = "powershell" });
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required; use scripts/serve-integration.mjs.");
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(25);

    [Theory]
    [InlineData("STREAM")]
    [InlineData("CANCEL")]
    [InlineData("LOSS")]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task NativeProcessUsesOriginalOutputAndExecutionPipeline(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100)); var ct = deadline.Token;
        var origin = new Uri(Required("TANSR_SERVE_EXECUTION_URL")); Assert.Equal("127.0.0.1", origin.Host);
        var root = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "execution-pipeline");
        var directory = Path.Combine(root, "dotnet-" + scenario.ToLowerInvariant()); Directory.CreateDirectory(directory);
        var work = Path.Combine(directory, "workspace"); Directory.CreateDirectory(work);
        var records = new ConcurrentQueue<JsonElement>();
        void Record(string stage, object? facts = null) => records.Enqueue(Json(new
        { stage, utcTicks = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), monotonicTicks = Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture), frequency = Stopwatch.Frequency, facts }));
        using var controllerTiming = new TimingHandler(Record);
        using var controllerHttp = Http(controllerTiming, "controller");
        using var loss = new LostResponseHandler(request => scenario == "LOSS" && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/output-batches", StringComparison.Ordinal));
        using var deviceTiming = new TimingHandler(Record, loss);
        using var deviceHttp = Http(deviceTiming, "executor");
        TansrClientOptions Options() => new()
        {
            BaseUri = origin,
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(Required("TANSR_SERVE_TEST_TOKEN")),
            PrincipalProvider = () => "net-execution-app/net-integration-user",
            ExecutionScopeProvider = () => Scope,
            RequestTimeout = TimeSpan.FromSeconds(10),
            StreamIdleTimeout = TimeSpan.FromSeconds(35),
            MaxReconnectAttempts = 0
        };
        using var controller = new TansrClient(Options(), controllerHttp);
        using var device = new TansrClient(Options(), deviceHttp);
        using var controllerTerminal = new TerminalConnection(Options(), true, controllerHttp);
        using var deviceTerminal = new TerminalConnection(Options(), true, deviceHttp);
        var session = await controller.CreateSessionAsync(new CreateSessionOptions { Tools = ["Shell"] }, ct);
        using var workspace = new WindowsWorkspace(work);
        var journalOptions = new SqliteExecutorJournalOptions
        { Path = Path.Combine(directory, "journal.sqlite"), Mode = StorageOpenMode.Create, ExecutorId = "net-native-pc", ApplicationScopeId = "net-execution-app", EndUserId = "net-integration-user", ReadContext = () => Scope };
        var journal = await SqliteExecutorJournal.OpenAsync(journalOptions, ct);
        var releaseName = @"Local\tansr-native-pipeline-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        var native = new CapturedStreams(); var visible = new CapturedStreams();
        var sink = new BoundSink(); TerminalBinding? controlBinding = null, deviceBinding = null;
        var original = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new ConcurrentQueue<JsonElement>(); var events = new ConcurrentQueue<AgentEvent>();
        var launches = 0;
        var backend = new WindowsExecutorBackend("net-native-pc", [new WindowsExecutorWorkspace("work", "1", workspace)],
            interpreter: Interpreter, processFactory: (args, location) =>
            {
                Assert.Equal("net-pipeline-" + scenario.ToLowerInvariant(), args.GetProperty("command").GetString());
                Assert.Equal("", args.GetProperty("cwd").GetString()); Interlocked.Increment(ref launches);
                var request = new WindowsProcessRequest(program.Executable, scenario == "CANCEL" ? ["tree-pipeline"] : ["paced", releaseName], () => location.AcquireProcessDirectory(""))
                { ChunkBytes = 1, MaxPendingChunks = 256 };
                request.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return request;
            }, output: (chunk, _) =>
            {
                Record("native.chunk", new { channel = chunk.Stream.ToString(), bytes = chunk.RawBytes.Count });
                native.Append(chunk.Stream == WindowsProcessOutputStream.StandardOutput ? "stdout" : "stderr", chunk.RawBytes.ToArray(), chunk.Text);
                return Task.CompletedTask;
            }, executionOutput: sink);
        using var host = new DeviceSessionHost(new ExecutionClient(controller), new ExecutionClient(device), backend, journal,
            new DeviceSessionOptions
            {
                SessionId = session.Id,
                WorkspaceId = "work",
                RequestedTools = ["Shell"],
                AfterBindingAsync = async (bound, token) =>
                {
                    controlBinding = await controllerTerminal.BindAsync(Json(new
                    {
                        contract = "terminal-services-v1",
                        requestId = "native-" + scenario.ToLowerInvariant(),
                        session = new { sessionContract = "sdk1", sessionId = session.Id },
                        executionBinding = bound.GetProperty("binding"),
                        required = new[] { "execution-stream-v1" },
                        optional = Array.Empty<string>()
                    }), token);
                    Assert.Equal("tool-output", controlBinding.Value.GetProperty("outputAuthority").GetString());
                    deviceBinding = deviceTerminal.AttachBinding(controlBinding); sink.Set(deviceTerminal.CreateOutputSink(deviceBinding));
                },
                ExecutionNotifications = (_, _) => Task.FromResult<IExecutionNotificationSource>(new TerminalExecutionNotifications(deviceTerminal, deviceBinding!))
            }, (operation, _) =>
            {
                Assert.Equal("Shell", operation.GetProperty("toolName").GetString());
                if (operation.GetProperty("request").GetProperty("operation").GetString() == "process.exec") original.TrySetResult(operation.Clone());
                return Task.CompletedTask;
            });
        Task? observing = null; CancellationTokenSource? observation = null; TerminalOutputView? view = null;
        Process? nativeRoot = null, child = null; SessionRun? run = null; bool sessionClosed = false;
        Exception? primaryFailure = null; TansrProtocolException? expectedOutputFailure = null;
        var cleanupFailures = new List<Exception>();
        async Task CleanupAsync(string stage, Func<Task> action)
        {
            try { await action(); Record(stage + ".completed"); }
            catch (Exception error) { cleanupFailures.Add(error); Record(stage + ".failed", new { error = error.ToString() }); }
        }
        try
        {
            await host.StartAsync(ct); Assert.Equal(DeviceSessionState.Ready, host.State);
            run = session.StartRun("NET_PIPELINE_" + scenario, new SessionRunOptions { Timeout = TimeSpan.FromSeconds(80) }, async (item, token) =>
            {
                events.Enqueue(item);
                if (item.Name == "server.permission.request")
                {
                    var permission = item.Data.GetProperty("payload");
                    await session.PermissionAsync(permission.GetProperty("requestId").GetString()!, permission.GetProperty("digest").GetString()!, true, token);
                }
            }, ct);
            await run.Acceptance.WaitAsync(Step, ct);
            var operation = await original.Task.WaitAsync(Step, ct);
            var operationId = operation.GetProperty("operationId").GetString()!;
            var reference = Json(new { operationId, requestDigest = operation.GetProperty("digest").GetString() });
            view = new TerminalOutputView(reference);
            void StartObservation(long? cursor)
            {
                observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                observing = ObserveAsync(controllerTerminal, controlBinding!, reference, cursor, view, visible, applied, Record, observation.Token);
            }
            StartObservation(null);
            var nativeText = await native.UntilAsync(text => scenario == "CANCEL" ? text.Contains("PIPE_TREE|", StringComparison.Ordinal) && text.Contains('\n') : text.Contains("PIPE_OUT_中文🙂\n", StringComparison.Ordinal), ct);
            if (scenario == "CANCEL")
            {
                var ids = Regex.Match(nativeText, @"PIPE_TREE\|(\d+)\|(\d+)\|(\d+)"); Assert.True(ids.Success, nativeText);
                nativeRoot = Process.GetProcessById(int.Parse(ids.Groups[1].Value, CultureInfo.InvariantCulture));
                child = Process.GetProcessById(int.Parse(ids.Groups[2].Value, CultureInfo.InvariantCulture));
                Assert.False(nativeRoot.HasExited); Assert.False(child.HasExited);
                await visible.UntilAsync(text => text.Contains("PIPE_TREE|", StringComparison.Ordinal) && text.Contains('\n'), ct);
                Record("controller.interrupt.begin"); await session.CancelAsync(ct); Record("controller.interrupt.accepted");
                var result = await run.Completion.WaitAsync(Step, ct); Assert.True(result.WasAborted);
                var receipt = await WaitReceiptAsync(journal, operation, ct);
                Assert.Equal("completed", receipt.GetProperty("status").GetString());
                Assert.True(receipt.GetProperty("result").GetProperty("args").GetProperty("aborted").GetBoolean());
                Assert.True(nativeRoot.WaitForExit((int)Step.TotalMilliseconds)); Assert.True(child.WaitForExit((int)Step.TotalMilliseconds));
                Record("native.tree.exited", new { rootId = nativeRoot.Id, childId = child.Id });
            }
            else
            {
                var produced = Regex.Match(nativeText, @"PIPE_TIME\|(\d+)\|(\d+)\|(\d+)\|(\d+)"); Assert.True(produced.Success, nativeText);
                nativeRoot = Process.GetProcessById(int.Parse(produced.Groups[4].Value, CultureInfo.InvariantCulture));
                Record("native.produced", new { monotonicTicks = produced.Groups[1].Value, frequency = produced.Groups[2].Value, utcTicks = produced.Groups[3].Value, processId = nativeRoot.Id });
                Assert.False(nativeRoot.HasExited); Assert.False(run.Completion.IsCompleted);
                if (scenario == "STREAM")
                {
                    await visible.UntilAsync(text => text.Contains("PIPE_OUT_中文🙂\n", StringComparison.Ordinal) && visible.Error.Contains("PIPE_ERR_中文🙂\n", StringComparison.Ordinal), ct);
                    Assert.False(nativeRoot.HasExited); Assert.False(run.Completion.IsCompleted);
                    Record("view.complete-prefix-process-live");
                    // Reconnect from the same original key, replay one block, and retain one decoder/view.
                    observation!.Cancel(); await observing!.WaitAsync(Step, ct); observation.Dispose();
                    var retained = view.LastSequence; Assert.NotNull(retained);
                    StartObservation(retained > 0 ? retained - 1 : null);
                    Record("view.reconnected", new { retainedSequence = retained });
                    release.Set();
                }
                else
                {
                    await WaitUntilAsync(() => loss.LostResponseCount == 1, ct);
                    Assert.False(nativeRoot.HasExited); release.Set();
                }
                var result = await run.Completion.WaitAsync(Step, ct); Assert.False(result.WasAborted);
                Record("run.completed");
                Assert.Equal("turn.completed", result.TerminalEvent.Name);
                Assert.Single(events, item => item.Name == "turn.completed");
                Assert.Contains(events, item => item.Name == "msg.text.delta" && item.Data.GetProperty("text").GetString() == "NET_PIPELINE_" + scenario + "_DONE");
                var receipt = await WaitReceiptAsync(journal, operation, ct);
                Assert.Equal("completed", receipt.GetProperty("status").GetString());
                var args = receipt.GetProperty("result").GetProperty("args"); Assert.Equal("0", args.GetProperty("exitCode").GetString());
                Assert.Equal(native.Output, args.GetProperty("stdout").GetString()); Assert.Equal(native.Error, args.GetProperty("stderr").GetString());
                Record("execution.receipt.verified");
                Assert.True(nativeRoot.WaitForExit((int)Step.TotalMilliseconds));
                if (scenario == "STREAM")
                {
                    await WaitUntilAsync(() => view.SealVerified, ct);
                    Assert.False(view.HasPresentationGap); Assert.Equal(native.Output, visible.Output); Assert.Equal(native.Error, visible.Error);
                    Assert.Equal(native.Bytes("stdout"), visible.Bytes("stdout")); Assert.Equal(native.Bytes("stderr"), visible.Bytes("stderr"));
                    Assert.True(applied.Count(item => item.GetProperty("type").GetString() == "output.block") > 2);
                    Assert.Null(backend.LastOutputFailure);
                    Record("view.seal.verified");
                }
                else
                {
                    Assert.Equal("output_transfer_unconfirmed", backend.LastOutputFailure!.Code);
                    var before = loss.Requests.Count(request => request.Method == "POST" && request.Path.EndsWith("/output-batches", StringComparison.Ordinal));
                    Assert.Equal(1, before);
                    var status = await sink.Inner!.ReconcileAsync(operationId, ct);
                    Assert.Equal("receiving", status.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, status.GetProperty("durableThrough").ValueKind);
                    Assert.Equal(before, loss.Requests.Count(request => request.Method == "POST" && request.Path.EndsWith("/output-batches", StringComparison.Ordinal)));
                    Assert.Equal("commit_unknown", (await Assert.ThrowsAsync<WireProtocolException>(() => sink.Inner.OpenAsync(operation, ct))).Code);
                    Assert.NotNull(sink.Capture);
                    expectedOutputFailure = await Assert.ThrowsAsync<TansrProtocolException>(() => sink.Capture.Completion.WaitAsync(Step, ct));
                    Assert.Equal("network_error", expectedOutputFailure.Code);
                    Assert.Equal(1, loss.LostResponseCount);
                    Record("output.original-capture-failed", new { expectedOutputFailure.Code, error = expectedOutputFailure.ToString() });
                }
                // Execution settlement is independently durable and reconciles by the original operation.
                var remote = await new ExecutionClient(controller).GetStatusAsync(session.Id, operationId, ct);
                Assert.Equal(WireJson.CanonicalString(receipt), WireJson.CanonicalString(remote.GetProperty("receipt")));
                var recovery = await new ExecutionRecovery(new ExecutionClient(controller), journal).ReconcilePageAsync(cancellationToken: ct);
                Assert.All(recovery.Items, item => Assert.Equal(ExecutionRecoveryDisposition.Confirmed, item.Disposition));
            }
            Assert.Equal(1, launches); Assert.Single(await File.ReadAllLinesAsync(Path.Combine(work, "launches.txt"), ct));
            Assert.Contains(deviceTiming.Requests, request => request.Path.EndsWith("/events", StringComparison.Ordinal) && request.Path.StartsWith("/v3/terminal/executors/", StringComparison.Ordinal));
            Assert.DoesNotContain(deviceTiming.Requests, request => request.Path.StartsWith("/v2/sessions/", StringComparison.Ordinal));
            Assert.Contains(applied, item => item.GetProperty("type").GetString() == "output.block");
            observation!.Cancel(); await observing!.WaitAsync(Step, ct); observing = null;
            observation.Dispose(); observation = null;
            await session.CloseAsync(ct); sessionClosed = true;
            Record("session.closed");
            await host.StopAsync().WaitAsync(Step, ct);
            Record("host.stopped");
            var accepted = (await File.ReadAllLinesAsync(Path.Combine(root, "serve-pipeline.jsonl"), ct)).Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Where(item => item.GetProperty("stage").GetString() == "serve.output.accepted" && item.GetProperty("operationId").GetString() == operationId).ToArray();
            Assert.NotEmpty(accepted);
            Record("serve.accepted-evidence", new { count = accepted.Length, firstUtcMs = accepted[0].GetProperty("utcMs").GetInt64() });
            output.WriteLine("Native pipeline {0}: process executions={1}, accepted blocks={2}, replay key={3}; timing only, no Electron performance claim.", scenario, launches, accepted.Length, operationId);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            Record("primary.failed", new { error = error.ToString() });
        }
        finally
        {
            await CleanupAsync("cleanup.release", () => { release.Set(); observation?.Cancel(); return Task.CompletedTask; });
            if (observing != null) await CleanupAsync("cleanup.observation", () => observing.WaitAsync(Step));
            await CleanupAsync("cleanup.view", () => { observation?.Dispose(); view?.Dispose(); return Task.CompletedTask; });
            if (!sessionClosed) await CleanupAsync("cleanup.session", async () =>
            { using var cleanup = new CancellationTokenSource(Step); await session.CloseAsync(cleanup.Token); });
            await CleanupAsync("cleanup.host", () => host.StopAsync().WaitAsync(Step));
            if (sink.Inner != null) await CleanupAsync("cleanup.output", async () =>
            {
                if (expectedOutputFailure == null) { await sink.Inner.CloseAsync().WaitAsync(Step); return; }
                // Close must retain this capture's original failure, not turn an unknown transfer into success.
                var closeFailure = await Xunit.Record.ExceptionAsync(() => sink.Inner.CloseAsync().WaitAsync(Step));
                Assert.Same(expectedOutputFailure, closeFailure);
                Record("cleanup.output.original-failure-visible", new { expectedOutputFailure.Code });
            });
            await CleanupAsync("cleanup.run", () => { run?.Dispose(); return Task.CompletedTask; });
            await CleanupAsync("cleanup.processes", () => { nativeRoot?.Dispose(); child?.Dispose(); return Task.CompletedTask; });
            await CleanupAsync("cleanup.journal", () => { journal.Dispose(); return Task.CompletedTask; });
            try
            {
                await File.WriteAllLinesAsync(Path.Combine(directory, "dotnet-pipeline.jsonl"), records.Select(item => item.GetRawText()), new UTF8Encoding(false));
            }
            catch (Exception error) { cleanupFailures.Add(error); output.WriteLine("Pipeline timing could not be written: {0}", error); }
        }
        if (primaryFailure != null && cleanupFailures.Count == 0) ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (primaryFailure != null) cleanupFailures.Insert(0, primaryFailure);
        if (cleanupFailures.Count > 0) throw new AggregateException("Native pipeline failed; original failure precedes cleanup failures.", cleanupFailures);
    }

    private static HttpClient Http(HttpMessageHandler handler, string role)
    { var http = new HttpClient(handler); http.DefaultRequestHeaders.Add("x-net-role", role); return http; }
    private static async Task ObserveAsync(TerminalConnection terminal, TerminalBinding binding, JsonElement reference, long? cursor,
        TerminalOutputView view, CapturedStreams display, ConcurrentQueue<JsonElement> applied, Action<string, object?> record, CancellationToken ct)
    {
        try
        {
            await terminal.ObserveOutputAsync(binding, reference, cursor, (item, _) =>
            {
                applied.Enqueue(item.Clone());
                foreach (var part in view.ApplyEvent(item))
                { record("view.segment", new { part.Channel, part.Sequence, bytes = part.Bytes.Length }); display.Append(part.Channel, part.Bytes, part.Text ?? ""); }
                return Task.CompletedTask;
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (TansrProtocolException error) when (error.Code == "event_stream_disconnected" && view.SealVerified) { }
    }
    private static async Task<JsonElement> WaitReceiptAsync(SqliteExecutorJournal journal, JsonElement operation, CancellationToken ct)
    {
        var until = DateTime.UtcNow + Step;
        while (true)
        {
            var receipt = await journal.ReceiptAsync(operation, ct); if (receipt.HasValue) return receipt.Value;
            if (DateTime.UtcNow >= until) throw new TimeoutException("Original native operation receipt did not settle.");
            await Task.Delay(15, ct);
        }
    }
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    { var until = DateTime.UtcNow + Step; while (!condition()) { if (DateTime.UtcNow >= until) throw new TimeoutException("Native pipeline fact did not arrive."); await Task.Delay(15, ct); } }
    private sealed class BoundSink : IExecutionOutputSink
    {
        internal TerminalOutputSink? Inner;
        internal IExecutionOutputCapture? Capture;
        internal void Set(TerminalOutputSink value) { if (Interlocked.CompareExchange(ref Inner, value, null) != null) throw new InvalidOperationException("Already bound."); }
        public async Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken ct)
        {
            var capture = await (Volatile.Read(ref Inner) ?? throw new InvalidOperationException("Terminal output binding is not ready.")).OpenAsync(operation, ct);
            if (Interlocked.CompareExchange(ref Capture, capture, null) != null) throw new InvalidOperationException("Original process capture was opened more than once.");
            return capture;
        }
    }
    private sealed class CapturedStreams
    {
        private readonly object gate = new(); private readonly StringBuilder stdout = new(), stderr = new();
        private readonly MemoryStream outBytes = new(), errBytes = new();
        internal void Append(string channel, byte[] bytes, string text)
        { lock (gate) { (channel == "stdout" ? stdout : stderr).Append(text); (channel == "stdout" ? outBytes : errBytes).Write(bytes, 0, bytes.Length); } }
        internal string Output { get { lock (gate) return stdout.ToString(); } }
        internal string Error { get { lock (gate) return stderr.ToString(); } }
        internal byte[] Bytes(string channel) { lock (gate) return (channel == "stdout" ? outBytes : errBytes).ToArray(); }
        internal async Task<string> UntilAsync(Func<string, bool> condition, CancellationToken ct)
        { await WaitUntilAsync(() => condition(Output), ct); return Output; }
    }
    private sealed class TimingHandler(Action<string, object?> record, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
    {
        internal ConcurrentQueue<(string Method, string Path)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Enqueue((request.Method.Method, path));
            if (path.EndsWith("/output-batches", StringComparison.Ordinal)) record("client.output.send", new { path });
            return await base.SendAsync(request, ct);
        }
    }
}
