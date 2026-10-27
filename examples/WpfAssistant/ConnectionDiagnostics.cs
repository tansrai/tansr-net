using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Text.Json;
using Tansr.Sdk.Sessions;

namespace WpfAssistant;

internal enum ConnectionInitializationPhase
{
    LocalStateReady, ClientReady, SdkInitializationBegin, SessionConfirmed, SessionConfirmedPersisted,
    PresentationBegin, NativeReadyBegin, NativeReady, ObservationPrepared, SseReady,
    InputBlockedByRecovery, InputReady, InteractionReady, Failed, Canceled
}

// 一次建会的本机元数据采样；沿用首字诊断的有界内存、结束后写盘和不覆盖旧证据约束。
internal sealed class ConnectionDiagnostics : EventListener
{
    private const string SourceName = "Tansr-Sdk-ConnectionTiming";
    private const int MaximumRecords = 64, MaximumBytes = 16384;
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "CreateBegin", "ResumeBegin", "RequestSerialized", "IdentityBeforeToken", "TokenReceived", "AccessValidated",
        "DiscoveryGateWait", "DiscoveryGateEntered", "CapabilityRequestReady", "CapabilityHeaders",
        "CapabilityHeadersValidated", "CapabilityBody", "CapabilityParsed", "DiscoveryGateReleased",
        "CreateRequestReady", "CreateHeaders", "CreateHeadersValidated", "CreateBody", "CreateParsed",
        "Complete", "Failed", "Canceled"
    };
    // EventListener 基类可能提前回调；惰性锁不能被随后字段初始化覆盖。
    private object? gate;
    private object Gate => LazyInitializer.EnsureInitialized(ref gate);
    private readonly List<byte[]> records = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string path;
    private readonly Func<long> clock;
    private readonly long expiresAt;
    private EventSource? source;
    private Timer? timer;
    private string? sample;
    private string? sdkSample;
    private bool initializationMode, sdkReadyObserved, interactionReadyObserved;
    private readonly HashSet<ConnectionInitializationPhase> initializationPhases = new();
    private long startedAt, lastTimestamp;
    private int bytes;
    private bool initialized, finished;

    private ConnectionDiagnostics(string path, TimeSpan ttl, Func<long> clock)
    {
        this.path = path; this.clock = clock;
        expiresAt = clock() + checked((long)(ttl.TotalSeconds * Stopwatch.Frequency));
        EventSource? initialSource;
        lock (Gate)
        {
            initialized = true;
            initialSource = source;
            timer = new Timer(_ => Stop("Expired"), null, ttl, Timeout.InfiniteTimeSpan);
        }
        if (initialSource != null) EnableIfOpen(initialSource);
    }
    internal Task Completion => completion.Task;
    // WPF 在进入任何本机读取/网络准备前显式开启全程计量。未调用时保留旧的 CreateSession 单段采样。
    internal string? BeginInitialization()
    {
        try
        {
            lock (Gate)
            {
                if (!initialized || finished || sample != null) return null;
                if (clock() >= expiresAt) { Finish("Expired"); return null; }
                initializationMode = true;
                sample = Guid.NewGuid().ToString("N"); startedAt = lastTimestamp = clock();
                Add("Initialization.Begin", startedAt);
                return sample;
            }
        }
        catch { Stop("Closed"); return null; }
    }
    internal void MarkInitialization(string? attempt, ConnectionInitializationPhase phase)
    {
        try
        {
            if (!Enum.IsDefined(phase)) return;
            lock (Gate)
            {
                if (!AcceptInitialization(attempt) || !initializationPhases.Add(phase)) return;
                Add("Initialization." + phase, clock());
                if (phase is ConnectionInitializationPhase.Failed or ConnectionInitializationPhase.Canceled) Finish(null);
                else if (phase == ConnectionInitializationPhase.InteractionReady)
                {
                    interactionReadyObserved = true;
                    if (sdkReadyObserved) Finish(null);
                }
            }
        }
        catch { Stop("Closed"); }
    }
    internal void MarkInitializationProgress(string? attempt, SessionInitializationProgress progress)
    {
        try
        {
            if (!Enum.IsDefined(progress.Stage) || progress.Elapsed < TimeSpan.Zero) return;
            lock (Gate)
            {
                if (!AcceptInitialization(attempt)) return;
                // elapsedMs 是宿主收到通知的时刻；sdkElapsedMs 保留核心阶段时间，避免把回调调度等待算进能力协商。
                Add("Initialization.Sdk." + progress.Stage, clock(), progress.Elapsed.TotalMilliseconds);
                if (progress.Stage == SessionInitializationStage.Ready)
                {
                    sdkReadyObserved = true;
                    // 通知按序异步派发。界面交互可能先启用，等最后一项通知到达再封口；新发送是否可用独立记载。
                    if (interactionReadyObserved) Finish(null);
                }
            }
        }
        catch { Stop("Closed"); }
    }
    private bool AcceptInitialization(string? attempt)
    {
        if (!initialized || finished || !initializationMode || attempt == null || sample != attempt) return false;
        if (clock() < expiresAt) return true;
        Finish("Expired"); return false;
    }
    internal static ConnectionDiagnostics? FromEnvironment()
    {
        try
        {
            var seconds = Environment.GetEnvironmentVariable("TANSR_WPF_CONNECTION_TIMING_TTL_SECONDS");
            if (seconds != null && !int.TryParse(seconds, out _)) return null;
            return Create(Environment.GetEnvironmentVariable("TANSR_WPF_CONNECTION_TIMING_FILE"),
                TimeSpan.FromSeconds(seconds == null ? 1800 : int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch { return null; }
    }
    internal static ConnectionDiagnostics? Create(string? absolutePath, TimeSpan ttl, Func<long>? clock = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath) ||
                absolutePath.StartsWith(@"\\", StringComparison.Ordinal) || ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(30)) return null;
            var path = Path.GetFullPath(absolutePath);
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            if (Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || File.Exists(path) ||
                Directory.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path))) return null;
            return new ConnectionDiagnostics(path, ttl, clock ?? Stopwatch.GetTimestamp);
        }
        catch { return null; }
    }
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name != SourceName) return;
        bool ready;
        lock (Gate)
        {
            if (finished) return;
            source = eventSource;
            ready = initialized;
        }
        if (ready) EnableIfOpen(eventSource);
    }
    private void EnableIfOpen(EventSource eventSource)
    {
        // EventSource 回调可能持有内部锁；不在本机锁内启停监听，避免锁序反转。
        try
        {
            if (Volatile.Read(ref finished)) return;
            EnableEvents(eventSource, EventLevel.Informational);
            // 到期可能与启用交错，封口之后必须立即撤销这次启用。
            if (Volatile.Read(ref finished)) DisableEvents(eventSource);
        }
        catch { /* 观察器故障不能影响原调用。 */ }
    }
    protected override void OnEventWritten(EventWrittenEventArgs item)
    {
        try
        {
            if (item.EventSource.Name != SourceName || item.EventId != 1 || item.Payload?.Count != 3 ||
                item.Payload[0] is not string current || !Guid.TryParseExact(current, "N", out _) ||
                item.Payload[1] is not string phase || !Phases.Contains(phase) || item.Payload[2] is not long timestamp) return;
            lock (Gate)
            {
                if (!initialized || finished) return;
                if (clock() >= expiresAt) { Finish("Expired"); return; }
                if (initializationMode)
                {
                    if (sdkSample == null)
                    {
                        if (phase is not ("CreateBegin" or "ResumeBegin")) return;
                        sdkSample = current;
                    }
                    if (sdkSample != current) return;
                    Add("CreateSession." + phase, timestamp);
                    // 创建返回仅是初始化中段，不能提前漏掉能力、原生宿主、SSE 及按钮可用耗时。
                    return;
                }
                if (sample == null)
                {
                    if (phase is not ("CreateBegin" or "ResumeBegin")) return;
                    sample = current; startedAt = lastTimestamp = timestamp;
                }
                if (sample != current) return;
                if (timestamp < lastTimestamp) { Finish("InvalidOrder"); return; }
                lastTimestamp = timestamp;
                Add(phase, timestamp);
                if (phase is "Complete" or "Failed" or "Canceled") Finish(null);
            }
        }
        catch { Stop("Closed"); }
    }
    private void Add(string phase, long timestamp, double? sdkElapsedMs = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["sample"] = sample, ["phase"] = phase, ["utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["timestamp"] = timestamp, ["elapsedMs"] = (timestamp - startedAt) * 1000d / Stopwatch.Frequency
        };
        if (sdkElapsedMs.HasValue) fields["sdkElapsedMs"] = sdkElapsedMs.Value;
        var record = JsonSerializer.SerializeToUtf8Bytes(fields);
        if (records.Count >= MaximumRecords || bytes + record.Length + 1 > MaximumBytes)
        { Finish("LimitReached", false); return; }
        records.Add(record); bytes += record.Length + 1;
    }
    private void Stop(string phase)
    {
        try { lock (Gate) Finish(phase); }
        catch { completion.TrySetResult(); }
    }
    private void Finish(string? phase, bool recordEnd = true)
    {
        if (finished) return;
        finished = true; timer?.Dispose(); timer = null;
        var observedSource = source;
        if (sample != null && recordEnd && phase != null && records.Count < MaximumRecords && bytes < MaximumBytes - 512)
            Add(phase, Math.Max(lastTimestamp, clock()));
        var output = records.ToArray(); records.Clear();
        _ = Task.Run(async () =>
        {
            try
            {
                // 先在本机锁外停听；Completion 同时覆盖停听和文件写入。
                if (observedSource != null) DisableEvents(observedSource);
                if (output.Length > 0)
                {
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                    foreach (var line in output) { await file.WriteAsync(line); await file.WriteAsync(new byte[] { 10 }); }
                }
            }
            catch { /* 诊断文件不可写时不重试、不覆盖，也不影响业务。 */ }
            finally { completion.TrySetResult(); }
        });
    }
    public override void Dispose()
    {
        Stop("Closed");
        base.Dispose();
    }
}
