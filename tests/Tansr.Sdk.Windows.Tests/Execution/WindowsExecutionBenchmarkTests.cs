using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
using Xunit.Abstractions;

namespace Tansr.Sdk.Windows.Tests.Execution;

/// <summary>原 Electron SDK/IPC 和真实 Serve/C# 输出链的同机统一 QPC 采样；缺环境明确跳过，不计入性能过门。</summary>
public sealed class WindowsExecutionBenchmarkTests(ITestOutputHelper output, WindowsProcessTestProgram program) : IClassFixture<WindowsProcessTestProgram>
{
    private static readonly Regex Sample = new(@"QPC_SAMPLE\|(\d+)\|(\d+)\|(\d+)\|(\d+)\|中文🙂", RegexOptions.CultureInvariant);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " must be set by the source integration runner.");
    private static JsonElement Scope => Json(new { applicationScopeId = "net-execution-app", endUserId = "net-integration-user", authorizationRevision = "1" });

    [ExecutionBenchmarkFact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task OriginalElectronAndServeShareSameFlushedCommandAndQpcCollector()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4)); var ct = deadline.Token;
        var directory = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "execution-benchmark"); Directory.CreateDirectory(directory);
        var results = new List<JsonElement>();
        try
        {
            results.Add(await ElectronAsync(Path.Combine(directory, "electron"), ct));
            results.Add(await ServeAsync(Path.Combine(directory, "serve-loopback"), 0, ct));
            results.Add(await ServeAsync(Path.Combine(directory, "serve-rtt20"), 10, ct));
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "comparison.json"), Json(new
            {
                command = new
                {
                    executable = program.Executable,
                    sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(program.Executable, CancellationToken.None))).ToLowerInvariant(),
                    arguments = new[] { "benchmark", "<per-run-release-event>" },
                    samples = 100,
                    flushIntervalMs = 100,
                    alternatingStdoutStderr = true
                },
                clock = "Source and common collector both use Windows QueryPerformanceCounter via Stopwatch.GetTimestamp; no Node clock subtraction.",
                measurement = "Both consumers report complete observed lines through the same named-pipe C# collector. Electron additionally includes DOM console IPC. These are conservative source-to-consumption upper bounds; no subtraction of estimated overhead.",
                scope = "Electron original renderer DOM consumption versus C# original TerminalOutputView consumer; this does not substitute for desktop UIA rendering or real WAN measurements.",
                originalBudgetMs = new { loopbackP95 = 150, controlled20MsRttP95 = 250 },
                measuredLoopbackP95IncrementOverElectronMs = results.Count >= 2 ? results[1].GetProperty("p95Ms").GetDouble() - results[0].GetProperty("p95Ms").GetDouble() : (double?)null,
                measuredControlledRttP95IncrementOverLoopbackMs = results.Count >= 3 ? results[2].GetProperty("p95Ms").GetDouble() - results[1].GetProperty("p95Ms").GetDouble() : (double?)null,
                results
            }).GetRawText(), CancellationToken.None);
        }
        Assert.Equal(3, results.Count);
        Assert.True(results[1].GetProperty("p95Ms").GetDouble() <= 150, results[1].GetRawText());
        Assert.True(results[2].GetProperty("p95Ms").GetDouble() <= 250, results[2].GetRawText());
        output.WriteLine("Electron P95 {0:F3} ms; Serve loopback {1:F3} ms; injected 20ms RTT {2:F3} ms. Raw samples and all timing boundaries retained.",
            results[0].GetProperty("p95Ms").GetDouble(), results[1].GetProperty("p95Ms").GetDouble(), results[2].GetProperty("p95Ms").GetDouble());
    }

    private async Task<JsonElement> ElectronAsync(string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        await using var collector = new QpcCollector(directory);
        var releaseName = @"Local\tansr-electron-benchmark-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        var config = Path.Combine(directory, "benchmark-config.json");
        await File.WriteAllTextAsync(config, Json(new { directory, executable = program.Executable, releaseName, pipe = @"\\.\pipe\" + collector.Name }).GetRawText(), ct);
        var start = new ProcessStartInfo(Required("TANSR_ELECTRON_RUNTIME"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Required("TANSR_ELECTRON_BENCHMARK_ENTRY")); start.ArgumentList.Add("--benchmark-config"); start.ArgumentList.Add(config);
        // The synthetic Electron app never needs inherited real credentials or Electron's Node-only mode.
        foreach (var key in start.Environment.Keys.Where(key => Regex.IsMatch(key, "TOKEN|SECRET|PASSWORD|API.?KEY|APP.?KEY|CREDENTIAL|ELECTRON_RUN_AS_NODE", RegexOptions.IgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Electron benchmark failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await collector.AllSamplesAsync(ct);
            collector.AssertProducerAlive(); Assert.False(process.HasExited);
            release.Set(); await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(35), ct);
            Assert.Equal(0, process.ExitCode); await collector.Finished.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Contains(collector.Notices, item => item.GetProperty("stage").GetString() == "electron.complete");
            return collector.Summary("electron-original-sdk-ipc", new
            {
                runtime = start.FileName,
                runtimeSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(start.FileName, ct))).ToLowerInvariant(),
                entry = Required("TANSR_ELECTRON_BENCHMARK_ENTRY"),
                renderer = "original DOM mutation",
                wrapper = "original kernel PowerShell executor"
            });
        }
        finally
        {
            release.Set();
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), await stdout, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), await stderr, CancellationToken.None);
        }
    }

    private async Task<JsonElement> ServeAsync(string directory, int delayEachDirectionMs, CancellationToken ct)
    {
        Directory.CreateDirectory(directory); var work = Path.Combine(directory, "workspace"); Directory.CreateDirectory(work);
        var actual = new Uri(Required("TANSR_SERVE_EXECUTION_URL"));
        await using var proxy = new DelayedProxy(actual, delayEachDirectionMs);
        var origin = delayEachDirectionMs == 0 ? actual : proxy.Origin;
        await using var collector = new QpcCollector(directory);
        using var sender = new NamedPipeClientStream(".", collector.Name, PipeDirection.Out, PipeOptions.Asynchronous);
        await sender.ConnectAsync(ct);
        await using var pipe = new StreamWriter(sender, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var stages = new ConcurrentQueue<JsonElement>();
        void Record(string stage, object facts) => stages.Enqueue(Json(new { stage, qpc = Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture), frequency = Stopwatch.Frequency, utcTicks = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), facts }));
        using var controlHttp = new HttpClient(new TimingHandler(Record)); controlHttp.DefaultRequestHeaders.Add("x-net-role", "controller");
        using var deviceHttp = new HttpClient(new TimingHandler(Record)); deviceHttp.DefaultRequestHeaders.Add("x-net-role", "executor");
        TansrClientOptions Options() => new()
        {
            BaseUri = origin,
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(Required("TANSR_SERVE_TEST_TOKEN")),
            PrincipalProvider = () => "net-execution-app/net-integration-user",
            ExecutionScopeProvider = () => Scope,
            RequestTimeout = TimeSpan.FromSeconds(15),
            StreamIdleTimeout = TimeSpan.FromSeconds(35),
            MaxReconnectAttempts = 0
        };
        using var controller = new TansrClient(Options(), controlHttp); using var device = new TansrClient(Options(), deviceHttp);
        using var controlTerminal = new TerminalConnection(Options(), true, controlHttp); using var terminal = new TerminalConnection(Options(), true, deviceHttp);
        var session = await controller.CreateSessionAsync(new CreateSessionOptions { Tools = ["Shell"] }, ct);
        var rtts = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var start = Stopwatch.GetTimestamp(); using var response = await controlHttp.GetAsync(new Uri(origin, "/__net_benchmark_rtt_probe"), ct);
            await response.Content.ReadAsByteArrayAsync(ct); rtts.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        using var workspace = new WindowsWorkspace(work);
        using var journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
        { Path = Path.Combine(directory, "journal.sqlite"), Mode = StorageOpenMode.Create, ExecutorId = "net-native-pc", ApplicationScopeId = "net-execution-app", EndUserId = "net-integration-user", ReadContext = () => Scope }, ct);
        var releaseName = @"Local\tansr-serve-benchmark-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        var sink = new BoundSink(); TerminalBinding? binding = null, deviceBinding = null;
        var original = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = "native-benchmark-" + delayEachDirectionMs; var launches = 0;
        var backend = new WindowsExecutorBackend("net-native-pc", [new WindowsExecutorWorkspace("work", "1", workspace)],
            interpreter: Json(new { id = "net-native-fixture", revision = "1", hostShell = "powershell" }), processFactory: (args, location) =>
            {
                Assert.Equal(command, args.GetProperty("command").GetString()); Interlocked.Increment(ref launches);
                var request = new WindowsProcessRequest(program.Executable, ["benchmark", releaseName], () => location.AcquireProcessDirectory()) { ChunkBytes = 1024, MaxPendingChunks = 256 };
                request.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows); return request;
            }, output: (chunk, _) => { Record("native.read", new { stream = chunk.Stream.ToString(), text = chunk.Text }); return Task.CompletedTask; }, executionOutput: sink);
        using var host = new DeviceSessionHost(new ExecutionClient(controller), new ExecutionClient(device), backend, journal,
            new DeviceSessionOptions
            {
                SessionId = session.Id,
                WorkspaceId = "work",
                RequestedTools = ["Shell"],
                AfterBindingAsync = async (bound, token) =>
                {
                    binding = await controlTerminal.BindAsync(Json(new
                    {
                        contract = "terminal-services-v1",
                        requestId = command,
                        session = new { sessionContract = "sdk1", sessionId = session.Id },
                        executionBinding = bound.GetProperty("binding"),
                        required = new[] { "execution-stream-v1" },
                        optional = Array.Empty<string>()
                    }), token);
                    deviceBinding = terminal.AttachBinding(binding); sink.Inner = terminal.CreateOutputSink(deviceBinding);
                },
                ExecutionNotifications = (_, _) => Task.FromResult<IExecutionNotificationSource>(new TerminalExecutionNotifications(terminal, deviceBinding!))
            }, (operation, _) => { original.TrySetResult(operation.Clone()); return Task.CompletedTask; });
        using var observing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? stream = null; SessionRun? run = null; TerminalOutputView? view = null; string? operationId = null;
        var streams = new Dictionary<string, StringBuilder> { ["stdout"] = new(), ["stderr"] = new() }; var seen = new HashSet<int>();
        try
        {
            await host.StartAsync(ct);
            var action = Json(new { id = command, name = "Shell", args = new { command, timeout_ms = 60000 } });
            run = session.StartRun("NET_BACKGROUND:" + action.GetRawText(), new SessionRunOptions { Timeout = TimeSpan.FromSeconds(75) }, async (item, token) =>
            {
                if (item.Name == "server.permission.request")
                { var request = item.Data.GetProperty("payload"); await session.PermissionAsync(request.GetProperty("requestId").GetString()!, request.GetProperty("digest").GetString()!, true, token); }
            }, ct);
            var operation = await original.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            Assert.Equal("process.exec", operation.GetProperty("request").GetProperty("operation").GetString());
            operationId = operation.GetProperty("operationId").GetString()!;
            var reference = Json(new { operationId, requestDigest = operation.GetProperty("digest").GetString() }); view = new TerminalOutputView(reference);
            stream = ObserveAsync();
            async Task ObserveAsync()
            {
                try
                {
                    await controlTerminal.ObserveOutputAsync(binding!, reference, null, async (item, token) =>
                    {
                        foreach (var segment in view.ApplyEvent(item))
                        {
                            Record("view.consume", new { segment.Channel, segment.Sequence, bytes = segment.Bytes.Length });
                            if (!streams.TryGetValue(segment.Channel, out var text)) continue;
                            text.Append(segment.Text);
                            foreach (Match match in Sample.Matches(text.ToString()))
                            {
                                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                                if (seen.Add(index)) await pipe.WriteLineAsync(Json(new { stage = "serve.view", line = match.Value }).GetRawText().AsMemory(), token);
                            }
                        }
                    }, observing.Token);
                }
                catch (OperationCanceledException) when (observing.IsCancellationRequested) { }
                catch (TansrProtocolException error) when (error.Code == "event_stream_disconnected" && view.SealVerified) { }
            }
            await collector.AllSamplesAsync(ct); collector.AssertProducerAlive(); Assert.False(run.Completion.IsCompleted); release.Set();
            var final = await run.Completion.WaitAsync(TimeSpan.FromSeconds(25), ct); Assert.False(final.WasAborted);
            var sealDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!view.SealVerified) { if (stream.IsFaulted) await stream; Assert.True(DateTime.UtcNow < sealDeadline, "Original output seal missing."); await Task.Delay(10, ct); }
            Assert.False(view.HasPresentationGap); Assert.Null(backend.LastOutputFailure); Assert.Equal(1, launches);
            Assert.Single(await File.ReadAllLinesAsync(Path.Combine(work, "launches.txt"), ct));
            var accepted = (await File.ReadAllLinesAsync(Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "execution-pipeline", "serve-pipeline.jsonl"), ct))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).Where(item => item.GetProperty("stage").GetString() == "serve.output.accepted" && item.GetProperty("operationId").GetString() == operationId).ToArray();
            Assert.True(accepted.Length >= 100, "All flushed blocks must traverse the original Serve accepted-output boundary.");
            await File.WriteAllLinesAsync(Path.Combine(directory, "serve-accepted.jsonl"), accepted.Select(item => item.GetRawText()), ct);
            return collector.Summary(delayEachDirectionMs == 0 ? "serve-loopback" : "serve-controlled-rtt20", new
            {
                operationId,
                acceptedBlocks = accepted.Length,
                launches,
                injectedDelayPerTcpDirectionMs = delayEachDirectionMs,
                measuredHttpRttMs = rtts,
                tcpProxy = delayEachDirectionMs != 0,
                budgetMs = delayEachDirectionMs == 0 ? 150 : 250,
                viewBoundary = "Original TerminalOutputView.ApplyEvent consumption; not an actual desktop window paint.",
                serveClock = "Serve native UTC/hrtime records are retained without cross-runtime clock subtraction."
            });
        }
        finally
        {
            release.Set(); observing.Cancel();
            if (stream != null) await stream.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            run?.Dispose();
            await host.QuiesceNotificationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await File.WriteAllLinesAsync(Path.Combine(directory, "dotnet-stages.jsonl"), stages.Select(item => item.GetRawText()), CancellationToken.None);
        }
    }

    private sealed class BoundSink : IExecutionOutputSink
    {
        internal IExecutionOutputSink? Inner;
        public Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken ct) => (Inner ?? throw new InvalidOperationException("Terminal binding missing.")).OpenAsync(operation, ct);
    }

    private sealed class TimingHandler(Action<string, object> record) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/output-batches", StringComparison.Ordinal)) record("client.output.send", new { path = request.RequestUri.AbsolutePath });
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class QpcCollector : IAsyncDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentDictionary<int, JsonElement> samples = new();
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string directory;
        internal ConcurrentQueue<JsonElement> Notices { get; } = new();
        internal string Name { get; } = "tansr-qpc-" + Guid.NewGuid().ToString("N");
        internal Task Finished { get; }
        internal QpcCollector(string directory)
        {
            // Node's duplex pipe client closes its write half when an inbound-only server
            // reports EOF. Keep the unused outbound half open for both original consumers.
            this.directory = directory; pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Finished = ReadAsync();
        }
        private async Task ReadAsync()
        {
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token); using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), leaveOpen: true);
                while (await reader.ReadLineAsync(stop.Token) is { } line)
                {
                    var qpc = Stopwatch.GetTimestamp(); Assert.True(line.Length <= 8192);
                    var item = JsonDocument.Parse(line).RootElement.Clone(); Notices.Enqueue(item);
                    Assert.True(Notices.Count <= 104);
                    if (!item.TryGetProperty("line", out var text)) { Assert.DoesNotContain("failed", item.GetProperty("stage").GetString()); continue; }
                    var match = Sample.Match(text.GetString()!); Assert.True(match.Success); var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var produced = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture); var frequency = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    Assert.Equal(Stopwatch.Frequency, frequency); Assert.InRange(index, 0, 99); Assert.True(qpc >= produced);
                    Assert.True(samples.TryAdd(index, Json(new
                    {
                        index,
                        sourceQpc = produced.ToString(CultureInfo.InvariantCulture),
                        collectorQpc = qpc.ToString(CultureInfo.InvariantCulture),
                        frequency,
                        processId = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                        milliseconds = (qpc - produced) * 1000d / frequency,
                        channel = index % 2 == 0 ? "stdout" : "stderr",
                        stage = item.GetProperty("stage").GetString(),
                        originalLine = text.GetString()
                    })));
                    if (samples.Count == 1) AssertProducerAlive();
                    if (samples.Count == 100) { AssertProducerAlive(); all.TrySetResult(); }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { all.TrySetException(error); throw; }
        }
        internal async Task AllSamplesAsync(CancellationToken ct)
        {
            var first = await Task.WhenAny(all.Task, Finished).WaitAsync(TimeSpan.FromSeconds(70), ct);
            await first; Assert.Equal(100, samples.Count);
        }
        internal void AssertProducerAlive()
        {
            var pid = samples.Values.First().GetProperty("processId").GetInt32(); using var process = Process.GetProcessById(pid); Assert.False(process.HasExited);
        }
        internal JsonElement Summary(string name, object facts)
        {
            Assert.Equal(100, samples.Count); var sorted = samples.Values.Select(item => item.GetProperty("milliseconds").GetDouble()).Order().ToArray();
            return Json(new { name, count = sorted.Length, p50Ms = sorted[49], p95Ms = sorted[94], maximumMs = sorted[^1], processLiveAtFirstAndLast = true, facts });
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); pipe.Dispose();
            try { await Finished; }
            finally
            {
                await File.WriteAllLinesAsync(Path.Combine(directory, "qpc-samples.jsonl"), samples.OrderBy(item => item.Key).Select(item => item.Value.GetRawText()), CancellationToken.None);
                await File.WriteAllLinesAsync(Path.Combine(directory, "consumer-notices.jsonl"), Notices.Select(item => item.GetRawText()), CancellationToken.None); stop.Dispose();
            }
        }
    }

    /// <summary>Bounded real TCP forwarding; each direction delays each available read by 10ms, including SSE and request bodies.</summary>
    private sealed class DelayedProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> connections = new();
        private readonly Task accepting;
        private readonly Uri target;
        private readonly int milliseconds;
        internal Uri Origin { get; }
        internal DelayedProxy(Uri target, int milliseconds)
        {
            Assert.Equal("http", target.Scheme); Assert.Equal("127.0.0.1", target.Host); this.target = target; this.milliseconds = milliseconds;
            listener.Start(); Origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port); accepting = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                { var client = await listener.AcceptTcpClientAsync(stop.Token); Assert.True(connections.Count < 128); connections.Add(ForwardAsync(client)); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        private async Task ForwardAsync(TcpClient client)
        {
            using (client)
            using (var upstream = new TcpClient())
            {
                try
                {
                    await upstream.ConnectAsync(target.Host, target.Port, stop.Token); client.NoDelay = true; upstream.NoDelay = true;
                    using var end = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    var up = PumpAsync(client.GetStream(), upstream.GetStream(), end.Token); var down = PumpAsync(upstream.GetStream(), client.GetStream(), end.Token);
                    await Task.WhenAny(up, down); end.Cancel();
                    try { await Task.WhenAll(up, down); } catch (OperationCanceledException) when (end.IsCancellationRequested) { }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) { /* The original HTTP client may close a cancelled SSE. */ }
                catch (SocketException) when (stop.IsCancellationRequested) { }
            }
        }
        private async Task PumpAsync(Stream source, Stream destination, CancellationToken ct)
        {
            var buffer = new byte[8192]; int size;
            while ((size = await source.ReadAsync(buffer, ct)) != 0)
            { if (milliseconds != 0) await Task.Delay(milliseconds, ct); await destination.WriteAsync(buffer.AsMemory(0, size), ct); }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await accepting; await Task.WhenAll(connections); } finally { stop.Dispose(); }
        }
    }
}

public sealed class ExecutionBenchmarkFactAttribute : FactAttribute
{
    public ExecutionBenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TANSR_ELECTRON_BENCHMARK_ENTRY") is null)
            Skip = "需冻结原 Electron 完整 SDK/IPC 候选、真实 Serve fixture 和同机 QPC 程序；普通单测不得冒充性能基准。";
    }
}
