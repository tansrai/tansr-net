using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Sessions;

public enum SessionMessageAcceptance { NotSent, Unconfirmed, Accepted, Rejected }

public sealed class SessionRunOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
    public int MaximumMessages { get; set; } = 256;
    public int MaximumTextCharacters { get; set; } = 65536;
}

/// <summary>HTTP 接纳和具有同一 turnId 的轮终局分别记录；终局 reason 原值保留，aborted 不伪装为成功。</summary>
public sealed class SessionRunResult
{
    internal SessionRunResult(JsonElement acceptance, string turnId, AgentEvent terminal, SessionViewSnapshot snapshot)
    { Acceptance = acceptance.Clone(); TurnId = turnId; TerminalEvent = terminal; Snapshot = snapshot; Reason = SessionJson.String(terminal.Data, "reason"); }
    public JsonElement Acceptance { get; }
    public string TurnId { get; }
    public string Reason { get; }
    public AgentEvent TerminalEvent { get; }
    public SessionViewSnapshot Snapshot { get; }
    public bool WasAborted => TerminalEvent.Name == "turn.aborted";
}

/// <summary>单轮观察句柄。取消/Dispose 只停止本地等待；要中断服务端执行，显式调用 AgentSession.CancelAsync。</summary>
/// <remarks>
/// 宿主必须独占该会话的消息写入。冻结 messages ACK 不包含 turnId，无法证明跨客户端竞争下的发送归属。
/// 本助手拒绝已运行会话、同实例并发、监听缺口及身份不明的终局，且任何失败都不会自动重发消息。
/// </remarks>
public sealed class SessionRun : IDisposable
{
    private readonly CancellationTokenSource lifetime;
    private readonly object lifetimeGate = new object();
    private readonly TaskCompletionSource<JsonElement> acceptance = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SessionRunResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int acceptanceState;
    private int disposed;
    private bool finished;

    internal SessionRun(AgentSession session, Func<CancellationToken, Task<JsonElement>> send, SessionRunOptions options,
        Func<AgentEvent, CancellationToken, Task>? observer, CancellationToken cancellationToken, Action release)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromHours(24) ||
            options.MaximumMessages < 1 || options.MaximumMessages > 4096 || options.MaximumTextCharacters < 1 || options.MaximumTextCharacters > 1048576)
            throw new ArgumentException("Invalid run limits.", nameof(options));
        var fixedOptions = new SessionRunOptions { Timeout = options.Timeout, MaximumMessages = options.MaximumMessages, MaximumTextCharacters = options.MaximumTextCharacters };
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(fixedOptions.Timeout);
        // 两个任务可分别消费；只等待 Completion 的宿主也不会留下无人观察的 Acceptance 异常。
        _ = acceptance.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = ExecuteAsync(session, send, fixedOptions, observer, release);
    }

    public Task<JsonElement> Acceptance => acceptance.Task;
    public Task<SessionRunResult> Completion => completion.Task;
    public SessionMessageAcceptance AcceptanceState => (SessionMessageAcceptance)Volatile.Read(ref acceptanceState);
    public void CancelObservation() { lock (lifetimeGate) if (!finished) lifetime.Cancel(); }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) CancelObservation(); }

    private async Task ExecuteAsync(AgentSession session, Func<CancellationToken, Task<JsonElement>> send, SessionRunOptions options,
        Func<AgentEvent, CancellationToken, Task>? observer, Action release)
    {
        Task? pump = null; SessionView? view = null; SessionRunResult? result = null; Exception? failure = null;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? turnId = null; int sending = 0;
        try
        {
            var before = await session.ReadMetadataAsync(lifetime.Token).ConfigureAwait(false);
            RequireIdle(before);
            view = new SessionView(new SessionViewOptions
            {
                InitialSequence = before.LastSequence,
                MaximumMessages = options.MaximumMessages,
                MaximumTextCharacters = options.MaximumTextCharacters
            });
            var streamOptions = new EventStreamOptions
            {
                LastEventId = before.LastSequence < 0 ? null : before.LastSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StopOnGap = true,
                Reconnect = true
            };
            pump = session.ObserveReadyAsync(async (item, ct) =>
            {
                if (terminal.Task.IsCompleted) return;
                if (item.Name == "server.replay.gap") throw new TansrProtocolException("event_replay_gap");
                if (item.Name == "session.ended") throw new TansrProtocolException("run_ended_without_terminal");
                if (item.Name == "turn.started")
                {
                    if (Volatile.Read(ref sending) == 0) throw new TansrProtocolException("session_busy");
                    var started = SessionJson.String(item.Data, "turnId");
                    if (turnId is not null && turnId != started) throw new TansrProtocolException("run_identity_ambiguous");
                    turnId = started;
                }
                var isTerminal = item.Name == "turn.completed" || item.Name == "turn.aborted";
                if (isTerminal && (turnId is null || SessionJson.String(item.Data, "turnId") != turnId))
                    throw new TansrProtocolException("run_identity_ambiguous");
                view.Apply(item);
                if (view.Snapshot.HasEventGap) throw new TansrProtocolException("event_projection_gap");
                if (observer is not null) await observer(item, ct).ConfigureAwait(false);
                if (isTerminal) terminal.TrySetResult(item);
            }, streamOptions, lifetime.Token, () => ready.TrySetResult(true));
            await WaitForSignalAsync(ready.Task, pump).ConfigureAwait(false);
            // 启流期间会话可能已被其他入口写入；发送前再核对空闲和水位，不把旧终局当成自己的轮。
            var now = await session.ReadMetadataAsync(lifetime.Token).ConfigureAwait(false);
            RequireIdle(now);
            if (now.LastSequence != before.LastSequence) throw new TansrProtocolException("session_changed");
            if (pump.IsCompleted) { await pump.ConfigureAwait(false); throw new TansrProtocolException("event_stream_disconnected"); }
            lifetime.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref sending, 1);
            Volatile.Write(ref acceptanceState, (int)SessionMessageAcceptance.Unconfirmed);
            JsonElement accepted;
            try { accepted = await send(lifetime.Token).ConfigureAwait(false); }
            catch (TansrHttpException error) when (error.FamilyStatus >= 400 && error.FamilyStatus < 500 && error.FamilyStatus != 408)
            { Volatile.Write(ref acceptanceState, (int)SessionMessageAcceptance.Rejected); throw; }
            if (SessionJson.String(accepted, "sessionId") != session.Id || !accepted.TryGetProperty("accepted", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
                throw new TansrProtocolException("invalid_response");
            Volatile.Write(ref acceptanceState, (int)SessionMessageAcceptance.Accepted);
            acceptance.TrySetResult(accepted);
            await WaitForSignalAsync(terminal.Task, pump).ConfigureAwait(false);
            var end = await terminal.Task.ConfigureAwait(false);
            if (view.Snapshot.HasEventGap) throw new TansrProtocolException("event_projection_gap");
            result = new SessionRunResult(accepted, turnId!, end, view.Snapshot);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            lifetime.Cancel();
            if (pump is not null) { try { await pump.ConfigureAwait(false); } catch { /* 主路径已记录失败；停止观察的取消不能覆盖终局。 */ } }
            view?.Dispose(); release();
            lock (lifetimeGate) { finished = true; lifetime.Dispose(); }
        }
        if (failure is OperationCanceledException)
        { acceptance.TrySetCanceled(); completion.TrySetCanceled(); }
        else if (failure is not null)
        { acceptance.TrySetException(failure); completion.TrySetException(failure); }
        else completion.TrySetResult(result!);
    }

    private static void RequireIdle(SessionMetadata metadata)
    {
        if (!metadata.IsLive) throw new TansrProtocolException("session_not_live");
        if (metadata.Status != SessionStatus.Idle) throw new TansrProtocolException("session_busy");
    }

    private static async Task WaitForSignalAsync(Task signal, Task pump)
    {
        var first = await Task.WhenAny(signal, pump).ConfigureAwait(false);
        if (first == pump && !signal.IsCompleted)
        { await pump.ConfigureAwait(false); throw new TansrProtocolException("event_stream_disconnected"); }
        await signal.ConfigureAwait(false);
    }
}
