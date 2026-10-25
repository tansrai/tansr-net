using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace WpfAssistant;

// 示例宿主的一次性测量，不接管传输；只在结束后写盘，避免把诊断IO计入首字路径。
internal sealed class FirstTokenDiagnostics : IDisposable
{
    internal enum Stage
    {
        Click, Saved, Sent, SendFailed, TurnStartedReceived, ThinkingReceived, TextReceived,
        TurnStartedHandled, ThinkingHandled, TextHandled, TextRenderStart, TextRenderEnd,
        ThinkingRenderStart, ThinkingRenderEnd, TerminalReceived, TerminalHandled,
        Complete, Expired, Closed, EventGap, AmbiguousTurn, LimitReached,
        RecoveredIdleBaseline, BaselineGap, BaselineUnconfirmed
    }
    private const int MaximumRecords = 64, MaximumBytes = 16384;
    private readonly object gate = new();
    private readonly string path;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<long> clock;
    private readonly long expiresAt;
    private readonly List<byte[]> records = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Timer? timer;
    private object? session;
    private object? recoveredIdleSession;
    private long? recoveredIdleSequence;
    private string? sessionId, turnId, sample;
    private HashSet<string> oldMessages = new();
    private long baselineSequence, startedAt, lastSequence, startSequence = -1, textSequence = -1, thinkingSequence = -1, terminalSequence = -1;
    private int bytes, renderStarted, renderEnded;
    private bool used, finished, saved, sent, completeRequested;
    private readonly HashSet<long> handled = new();

    private FirstTokenDiagnostics(string path, TimeSpan ttl, Func<DateTimeOffset> utcNow, Func<long> clock)
    {
        this.path = path; this.utcNow = utcNow; this.clock = clock;
        expiresAt = clock() + checked((long)(ttl.TotalSeconds * Stopwatch.Frequency));
        timer = new Timer(_ => Stop(Stage.Expired), null, ttl, Timeout.InfiniteTimeSpan);
    }
    internal Task Completion => completion.Task;

    internal static FirstTokenDiagnostics? FromEnvironment()
    {
        try
        {
            var seconds = Environment.GetEnvironmentVariable("TANSR_WPF_TIMING_TTL_SECONDS");
            if (seconds != null && !int.TryParse(seconds, out _)) return null;
            return Create(Environment.GetEnvironmentVariable("TANSR_WPF_TIMING_FILE"),
                TimeSpan.FromSeconds(seconds == null ? 1800 : int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch { return null; }
    }

    internal static FirstTokenDiagnostics? Create(string? absolutePath, TimeSpan ttl,
        Func<DateTimeOffset>? utcNow = null, Func<long>? clock = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath) ||
                absolutePath.StartsWith(@"\\", StringComparison.Ordinal) || ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(30)) return null;
            var path = Path.GetFullPath(absolutePath);
            if (Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || File.Exists(path) ||
                Directory.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path))) return null;
            return new FirstTokenDiagnostics(path, ttl, utcNow ?? (() => DateTimeOffset.UtcNow), clock ?? Stopwatch.GetTimestamp);
        }
        catch { return null; }
    }

    // messages ACK没有turnId；本采样沿用单写者手测前提，不冒充跨客户端因果证明。
    // 仅由宿主在metadata确认idle且随后完整历史安装成功后调用；不改SessionView的unknown事实。
    internal void ConfirmRecoveredIdle(object currentSession, long historySequence)
    {
        try
        {
            lock (gate)
            {
                if (!Open() || used) return;
                recoveredIdleSession = currentSession; recoveredIdleSequence = historySequence;
            }
        }
        catch { Stop(Stage.Closed); }
    }

    internal bool BeginSend(object currentSession, string id, SessionViewSnapshot baseline)
    {
        try
        {
            lock (gate)
            {
                if (!Open() || used) return false;
                used = true; session = currentSession; sessionId = id; sample = Guid.NewGuid().ToString("N");
                baselineSequence = lastSequence = baseline.LastSequence ?? -1; startedAt = clock();
                Add(Stage.Click, baseline.LastSequence, baseline.Version);
                if (baseline.HasEventGap) { Finish(Stage.BaselineGap); return false; }
                var recoveredIdle = baseline.Status == "unknown" && ReferenceEquals(currentSession, recoveredIdleSession) &&
                    baseline.LastSequence.HasValue && baseline.LastSequence == recoveredIdleSequence;
                if (baseline.Status != "idle" && !recoveredIdle) { Finish(Stage.BaselineUnconfirmed); return false; }
                if (recoveredIdle) Add(Stage.RecoveredIdleBaseline, baseline.LastSequence, baseline.Version);
                oldMessages = baseline.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
                return true;
            }
        }
        catch { Stop(Stage.Closed); return false; }
    }
    internal void Saved() => Safely(() => { if (!saved) { saved = true; Add(Stage.Saved); } });
    internal void Sent() => Safely(() =>
    {
        if (!sent) { sent = true; Add(Stage.Sent); }
        if (completeRequested) Finish(Stage.Complete);
    });
    internal void SendFailed() => Stop(Stage.SendFailed);

    internal void Received(object currentSession, AgentEvent item)
    {
        Safely(() =>
        {
            if (!ReferenceEquals(session, currentSession) || terminalSequence >= 0) return;
            if (item.Name == "server.replay.gap") { Finish(Stage.EventGap); return; }
            if (!Identity(item, out var sequence, out var currentTurn) || sequence <= lastSequence) return;
            lastSequence = sequence;
            if (item.Name == "turn.started")
            {
                if (!saved || currentTurn == null) { Finish(Stage.AmbiguousTurn); return; }
                if (turnId != null && turnId != currentTurn) { Finish(Stage.AmbiguousTurn); return; }
                if (turnId == null) { turnId = currentTurn; startSequence = sequence; Add(Stage.TurnStartedReceived, sequence); }
                return;
            }
            if (turnId == null || (currentTurn != null && currentTurn != turnId)) return;
            if (item.Name == "msg.text.delta" && textSequence < 0 && HasText(item)) { textSequence = sequence; Add(Stage.TextReceived, sequence); }
            if (item.Name == "msg.thinking.delta" && thinkingSequence < 0 && HasText(item)) { thinkingSequence = sequence; Add(Stage.ThinkingReceived, sequence); }
            if ((item.Name is "turn.completed" or "turn.aborted") && currentTurn == turnId) { terminalSequence = sequence; Add(Stage.TerminalReceived, sequence); }
        });
    }

    internal bool Handled(object currentSession, AgentEvent item)
    {
        var terminal = false;
        Safely(() =>
        {
            if (!ReferenceEquals(session, currentSession) || !Identity(item, out var sequence, out var currentTurn) ||
                (currentTurn != null && currentTurn != turnId) ||
                (sequence != startSequence && sequence != thinkingSequence && sequence != textSequence && sequence != terminalSequence) || !handled.Add(sequence)) return;
            if (sequence == startSequence) Add(Stage.TurnStartedHandled, sequence);
            if (sequence == thinkingSequence) Add(Stage.ThinkingHandled, sequence);
            if (sequence == textSequence) Add(Stage.TextHandled, sequence);
            if (sequence == terminalSequence) { Add(Stage.TerminalHandled, sequence); terminal = true; }
        });
        return terminal;
    }

    internal int BeginRender(object currentSession, SessionViewSnapshot snapshot)
    {
        var kinds = 0;
        Safely(() =>
        {
            if (!ReferenceEquals(session, currentSession) || turnId == null || snapshot.HasEventGap ||
                snapshot.LastSequence is not long seq || seq <= baselineSequence || (terminalSequence >= 0 && seq > terminalSequence)) return;
            var parts = snapshot.Messages.Where(message => message.Role == "assistant" && !oldMessages.Contains(message.Id)).SelectMany(message => message.Parts);
            if ((renderStarted & 1) == 0 && textSequence >= 0 && seq >= textSequence && parts.Any(part => part.Kind == "text" && part.Text.Length > 0))
            { kinds |= 1; renderStarted |= 1; Add(Stage.TextRenderStart, seq, snapshot.Version); }
            if ((renderStarted & 2) == 0 && thinkingSequence >= 0 && seq >= thinkingSequence && parts.Any(part => part.Kind == "thinking" && part.Text.Length > 0))
            { kinds |= 2; renderStarted |= 2; Add(Stage.ThinkingRenderStart, seq, snapshot.Version); }
        });
        return kinds;
    }
    internal void EndRender(int kinds, SessionViewSnapshot snapshot) => Safely(() =>
    {
        if ((kinds & 1 & renderStarted) != 0 && (renderEnded & 1) == 0) { renderEnded |= 1; Add(Stage.TextRenderEnd, snapshot.LastSequence, snapshot.Version); }
        if ((kinds & 2 & renderStarted) != 0 && (renderEnded & 2) == 0) { renderEnded |= 2; Add(Stage.ThinkingRenderEnd, snapshot.LastSequence, snapshot.Version); }
    });
    internal void Complete() => Safely(() =>
    {
        // 极快的轮终态可能先于POST响应；停止事件采样仍须保留原发送返回时点。
        completeRequested = true;
        if (terminalSequence < 0 || sent) Finish(Stage.Complete);
    });
    public void Dispose() => Stop(Stage.Closed);

    private bool Identity(AgentEvent item, out long sequence, out string? currentTurn)
    {
        sequence = -1; currentTurn = null;
        if (!item.Data.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != sessionId ||
            !item.Data.TryGetProperty("seq", out var seq) || !seq.TryGetInt64(out sequence) || sequence <= baselineSequence) return false;
        if (item.Data.TryGetProperty("turnId", out var turn))
        {
            if (turn.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(currentTurn = turn.GetString())) return false;
        }
        return true;
    }
    private static bool HasText(AgentEvent item) => item.Data.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString()?.Length > 0;
    private bool Open()
    {
        if (finished) return false;
        if (clock() >= expiresAt) { Finish(Stage.Expired); return false; }
        return true;
    }
    private void Safely(Action action)
    {
        try { lock (gate) { if (Open() && used) action(); } }
        catch { Stop(Stage.Closed); }
    }
    private void Add(Stage stage, long? seq = null, long? version = null)
    {
        var record = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sample,
            stage = stage.ToString(),
            utc = utcNow().ToString("O"),
            elapsedMs = Stopwatch.GetElapsedTime(startedAt, clock()).TotalMilliseconds,
            seq,
            version
        });
        if (records.Count >= MaximumRecords || bytes + record.Length + 1 > MaximumBytes) { Finish(Stage.LimitReached, recordEnd: false); return; }
        records.Add(record); bytes += record.Length + 1;
    }
    private void Stop(Stage stage)
    {
        try { lock (gate) Finish(stage); }
        catch { completion.TrySetResult(); }
    }
    private void Finish(Stage stage, bool recordEnd = true)
    {
        if (finished) return;
        finished = true; timer?.Dispose(); timer = null;
        if (used && recordEnd && records.Count < MaximumRecords && bytes < MaximumBytes - 512) Add(stage);
        var output = records.ToArray(); records.Clear(); oldMessages.Clear(); session = recoveredIdleSession = null;
        recoveredIdleSequence = null; sessionId = turnId = null;
        _ = Task.Run(async () =>
        {
            try
            {
                if (output.Length > 0)
                {
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                    foreach (var line in output) { await file.WriteAsync(line); await file.WriteAsync(new byte[] { 10 }); }
                }
            }
            catch { /* 可选诊断失败不影响业务，也不重试或覆盖旧证据。 */ }
            finally { completion.TrySetResult(); }
        });
    }
}
