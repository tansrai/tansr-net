using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Sessions;

public enum SessionInitializationStage
{
    Connecting, PreparingPersistence, SessionConfirmed, PreparingRequiredServices, InitializingCapabilities,
    PreparingObservation, Ready, Canceled, Failed
}

/// <summary>客户端接入阶段；不代表 Serve 内部百分比、运行空闲或历史投影完整。</summary>
public sealed class SessionInitializationProgress
{
    internal SessionInitializationProgress(SessionInitializationStage stage, TimeSpan elapsed)
    { Stage = stage; Elapsed = elapsed; }
    public SessionInitializationStage Stage { get; }
    public TimeSpan Elapsed { get; }
    public bool CanCancel => Stage is not (SessionInitializationStage.Ready or SessionInitializationStage.Canceled or SessionInitializationStage.Failed);
}

/// <summary>显式必需的端侧供材适配；不得在此选择上下文或默认开启可选工具。</summary>
public interface ISessionInitializationServices
{
    /// <summary>启动本次专属供材通道。token 在成功移交后仍有效，直至连接停止。</summary>
    Task StartAsync(AgentSession session, CancellationToken lifetimeToken);
    /// <summary>只停止本次资源，不删除会话或撤销其他连接。失败保留原回执，不能重放。</summary>
    Task StopAsync();
}

/// <summary>本次创建前获得的持久化供材资源；只返回本次有权关闭的资源，常驻借用返回 null。</summary>
public interface ISessionPersistenceLease
{
    Task StopAsync();
}

public sealed class SessionObservationOptions
{
    public SessionObservationOptions(Func<AgentEvent, CancellationToken, Task> observer)
    { Observer = observer ?? throw new ArgumentNullException(nameof(observer)); }
    public Func<AgentEvent, CancellationToken, Task> Observer { get; }
    public EventStreamOptions Events { get; set; } = new();
    /// <summary>Optional shared bounded presentation recovery. Pending recovery does not prevent observation or cancellation.</summary>
    public SessionRecovery? Recovery { get; set; }
}

public sealed class SessionInitializationOptions
{
    /// <summary>本次资源收尾等待上限；超时仅说明清理未确认，不宣称底层工作已停止。</summary>
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>SDK2 平台声明；省略时使用本 .NET 进程的基础平台描述。</summary>
    public JsonElement? Platform { get; set; }
    /// <summary>null 保持原收窄；空数组显式关闭全部工具。不会自动由本机工具生成。</summary>
    public IReadOnlyList<string>? RequestedTools { get; set; }
    /// <summary>后台等待显式的独立持久化来源就绪后才建会；需要 UI 时由宿主派发。不替代 Serve 校验，null 表示借用常驻来源。</summary>
    public Func<CancellationToken, Task<ISessionPersistenceLease?>>? PreparePersistenceAsync { get; set; }
    /// <summary>已确认 ID 后、后继步骤前保存原会话归属。失败会阻止后继步骤，Accepted 仍保留。</summary>
    public Func<AgentSession, CancellationToken, Task>? SessionConfirmedAsync { get; set; }
    public ISessionInitializationServices? RequiredServices { get; set; }
    /// <summary>准备宿主呈现／原生桥；返回 null 不启流。不能重建上下文、发送业务或默默清除缺口。</summary>
    public Func<AgentSession, CancellationToken, Task<SessionObservationOptions?>>? PrepareObservationAsync { get; set; }
    /// <summary>有界阶段通知，在后台按序调用；异常被隔离，不参与授权或完成判定。</summary>
    public Action<SessionInitializationProgress>? Progress { get; set; }
}

/// <summary>会话接入操作。取消仅停止本次准备；已确认 ID 不回滚，失回沿原请求键对账。</summary>
public sealed class SessionInitialization : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SessionLifetimeCancellation lifetimeCancellation;
    private readonly CancellationToken lifetimeToken;
    private readonly TaskCompletionSource<AgentSession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SessionConnection> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Action<SessionInitializationProgress>? observer;
    private readonly Queue<SessionInitializationProgress> pendingProgress = new();
    private CancellationTokenRegistration cancellation;
    private SessionInitializationProgress progress = new(SessionInitializationStage.Connecting, TimeSpan.Zero);
    private bool handedOff, finished, notifying, cancellationRequested;

    internal SessionInitialization(TansrClient client, CreateSessionOptions request, SessionInitializationOptions options, CancellationToken token)
    {
        lifetimeToken = lifetime.Token;
        lifetimeCancellation = new SessionLifetimeCancellation(lifetime);
        observer = options.Progress;
        ObserveFailure(accepted.Task); ObserveFailure(ready.Task);
        cancellation = token.Register(Cancel);
        _ = RunAsync(client, request, options);
    }

    public Task<AgentSession> Accepted => accepted.Task;
    public Task<SessionConnection> Ready => ready.Task;
    public SessionInitializationProgress Progress { get { lock (gate) return new SessionInitializationProgress(progress.Stage, clock.Elapsed); } }
    /// <summary>原失败不被收尾失败覆盖；宿主可独立报告未确认的资源收尾。</summary>
    public Exception? CleanupFailure { get; private set; }

    public void Cancel()
    {
        lock (gate)
        {
            if (finished || handedOff) return;
            cancellationRequested = true;
            // 不持 gate 调用外部取消回调，避免与宿主清理形成锁反转。
        }
        _ = lifetimeCancellation.Request();
    }
    public void Dispose() => Cancel();

    private async Task RunAsync(TansrClient client, CreateSessionOptions request, SessionInitializationOptions options)
    {
        bool servicesStarted = false;
        bool cleanupOwnsLifetime = false;
        Task? pendingPreparation = null;
        Task<ISessionPersistenceLease?>? persistencePreparation = null;
        ISessionPersistenceLease? persistence = null;
        SessionConnection? connection = null;
        try
        {
            Publish(SessionInitializationStage.Connecting);
            lifetimeToken.ThrowIfCancellationRequested();
            if (options.PreparePersistenceAsync != null) client.BindInitializationIdentity();
            var scope = client.CaptureInitializationScope();
            if (options.PreparePersistenceAsync != null)
            {
                client.AssertInitializationScope(scope);
                Publish(SessionInitializationStage.PreparingPersistence);
                // 仅显式前置供材异步派发，避免同步开库前半段阻塞 loading 返回；旧默认路径不变。
                persistencePreparation = Task.Run(async () =>
                {
                    lifetimeToken.ThrowIfCancellationRequested();
                    client.AssertInitializationScope(scope);
                    return await options.PreparePersistenceAsync(lifetimeToken).ConfigureAwait(false);
                });
                pendingPreparation = persistencePreparation;
                await AwaitPreparationAsync(persistencePreparation, lifetimeToken).ConfigureAwait(false);
                persistence = await persistencePreparation.ConfigureAwait(false);
                pendingPreparation = null;
                client.AssertInitializationScope(scope);
                Publish(SessionInitializationStage.Connecting);
            }
            var session = await client.CreateSessionAsync(request, lifetimeToken).ConfigureAwait(false);
            accepted.TrySetResult(session);
            Publish(SessionInitializationStage.SessionConfirmed);
            if (options.SessionConfirmedAsync != null)
            {
                pendingPreparation = options.SessionConfirmedAsync(session, lifetimeToken);
                await AwaitPreparationAsync(pendingPreparation, lifetimeToken).ConfigureAwait(false);
                pendingPreparation = null;
            }
            lifetimeToken.ThrowIfCancellationRequested();
            client.AssertInitializationScope(scope);
            if (options.RequiredServices != null)
            {
                Publish(SessionInitializationStage.PreparingRequiredServices);
                servicesStarted = true;
                pendingPreparation = options.RequiredServices.StartAsync(session, lifetimeToken);
                await AwaitPreparationAsync(pendingPreparation, lifetimeToken).ConfigureAwait(false);
                pendingPreparation = null;
            }
            lifetimeToken.ThrowIfCancellationRequested();
            client.AssertInitializationScope(scope);
            JsonElement? capabilities = null;
            if (client.InitializationContract == SessionContract.Sdk2OffloadV1)
            {
                Publish(SessionInitializationStage.InitializingCapabilities);
                capabilities = await new ExecutionClient(client).InitializeAsync(PlatformRequest(session.Id, options), lifetimeToken).ConfigureAwait(false);
            }
            lifetimeToken.ThrowIfCancellationRequested();
            client.AssertInitializationScope(scope);
            SessionObservationOptions? observation = null;
            if (options.PrepareObservationAsync != null)
            {
                Publish(SessionInitializationStage.PreparingObservation);
                var preparation = options.PrepareObservationAsync(session, lifetimeToken);
                pendingPreparation = preparation;
                await AwaitPreparationAsync(preparation, lifetimeToken).ConfigureAwait(false);
                observation = await preparation.ConfigureAwait(false);
                pendingPreparation = null;
            }
            lifetimeToken.ThrowIfCancellationRequested();
            client.AssertInitializationScope(scope);
            if (observation?.Recovery != null)
            {
                if (!ReferenceEquals(observation.Recovery.Session, session)) throw new TansrProtocolException("recovery_session_mismatch");
                // Establish one bounded presentation origin before the event stream opens.
                // A pending source keeps input blocked but must not prevent receiving live approvals/cancellation.
                pendingPreparation = observation.Recovery.RecoverAsync(lifetimeToken);
                await AwaitPreparationAsync(pendingPreparation, lifetimeToken).ConfigureAwait(false);
                pendingPreparation = null;
                client.AssertInitializationScope(scope);
            }
            connection = new SessionConnection(session, capabilities, lifetime, lifetimeCancellation, servicesStarted ? options.RequiredServices : null, persistence, observation, options.CleanupTimeout);
            await connection.ObservationReady.ConfigureAwait(false);
            if (connection.Completion.IsCompleted)
            {
                await connection.Completion.ConfigureAwait(false);
                throw new TansrProtocolException("event_stream_disconnected");
            }
            client.AssertInitializationScope(scope);
            lock (gate)
            {
                if (cancellationRequested) throw new OperationCanceledException(lifetimeToken);
                lifetimeToken.ThrowIfCancellationRequested();
                handedOff = true;
            }
            cancellation.Dispose();
            Publish(SessionInitializationStage.Ready);
            ready.TrySetResult(connection);
        }
        catch (Exception error)
        {
            try
            {
                if (connection != null) await connection.StopAsync().ConfigureAwait(false);
                else
                {
                    CleanupFailure = await lifetimeCancellation.Request().ConfigureAwait(false);
                    // 宿主忽略取消时只结束本次等待。晚到供材仍由同一清理任务收尾，不提前宣布已停止。
                    cleanupOwnsLifetime = true;
                    var cleanup = CleanupPreparationAsync(pendingPreparation, servicesStarted ? options.RequiredServices : null, persistencePreparation, lifetime);
                    await WaitForCleanupAsync(cleanup, options.CleanupTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception cleanup) { CleanupFailure = MergeFailures(CleanupFailure, cleanup); }
            Publish(error is OperationCanceledException ? SessionInitializationStage.Canceled : SessionInitializationStage.Failed);
            // 保留异常对象与已收到的创建事实；不包装成新的创建失败，也不自动发 DELETE。
            if (error is OperationCanceledException) { accepted.TrySetCanceled(); ready.TrySetCanceled(); }
            else { accepted.TrySetException(error); ready.TrySetException(error); }
        }
        finally
        {
            clock.Stop();
            lock (gate) finished = true;
            cancellation.Dispose();
            if (!handedOff && connection == null && !cleanupOwnsLifetime) lifetime.Dispose();
        }
    }

    private static async Task AwaitPreparationAsync(Task preparation, CancellationToken token)
    {
        ObserveFailure(preparation);
        if (!preparation.IsCompleted)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => canceled.TrySetResult(true));
            if (await Task.WhenAny(preparation, canceled.Task).ConfigureAwait(false) != preparation)
                throw new OperationCanceledException(token);
        }
        await preparation.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    internal static async Task CleanupPreparationAsync(Task? preparation, ISessionInitializationServices? services,
        Task<ISessionPersistenceLease?>? persistencePreparation, CancellationTokenSource lifetime)
    {
        try
        {
            // 取消后不再消费回调的成功值，也不丢弃其后继异常；只有实际结束后才停止已启动供材。
            if (preparation != null) { try { await preparation.ConfigureAwait(false); } catch { } }
            Exception? failure = null;
            try { if (services != null) await services.StopAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            ISessionPersistenceLease? persistence = null;
            if (persistencePreparation != null)
            {
                try { persistence = await persistencePreparation.ConfigureAwait(false); }
                catch { /* 来源未交还 owned 资源，不能臆测对方的清理句柄。 */ }
            }
            try { if (persistence != null) await persistence.StopAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { lifetime.Dispose(); }
    }

    private void Publish(SessionInitializationStage stage)
    {
        lock (gate)
        {
            progress = new SessionInitializationProgress(stage, clock.Elapsed);
            if (observer == null) return;
            // 每个操作最多八个固定阶段，无轮询、正文或无界进度缓存。
            pendingProgress.Enqueue(progress);
            if (notifying) return;
            notifying = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (; ; )
            {
                SessionInitializationProgress next;
                lock (gate)
                {
                    if (pendingProgress.Count == 0) { notifying = false; return; }
                    next = pendingProgress.Dequeue();
                }
                try { observer!(next); } catch { /* 进度是观察，不参与接入结果。 */ }
            }
        });
    }

    private static JsonElement PlatformRequest(string sessionId, SessionInitializationOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("protocol", "sdk2-ext-v1"); writer.WriteString("sessionId", sessionId);
            writer.WritePropertyName("platform");
            if (options.Platform.HasValue) options.Platform.Value.WriteTo(writer);
            else
            {
                writer.WriteStartObject();
                writer.WriteString("platform", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "linux");
                writer.WriteString("arch", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
                writer.WriteString("language", "csharp"); writer.WriteString("runtimeVersion", Environment.Version.ToString());
                writer.WriteString("adapterVersion", "dotnet-v1"); writer.WriteEndObject();
            }
            if (options.RequestedTools != null)
            {
                writer.WriteStartArray("requestedTools");
                foreach (var tool in options.RequestedTools) writer.WriteStringValue(tool);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    internal static void ObserveFailure(Task task) => _ = task.ContinueWith(failed => { _ = failed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal static Exception? MergeFailures(Exception? original, Exception? next) =>
        next == null || ReferenceEquals(original, next) ? original : original == null ? next : new AggregateException(original, next);

    internal static async Task WaitForCleanupAsync(Task cleanup, TimeSpan timeout, Action? onTimeout = null)
    {
        ObserveFailure(cleanup);
        if (!cleanup.IsCompleted)
        {
            if (timeout <= TimeSpan.Zero) { onTimeout?.Invoke(); throw new TimeoutException("session_cleanup_unconfirmed"); }
            using var timer = new CancellationTokenSource();
            try
            {
                if (await Task.WhenAny(cleanup, Task.Delay(timeout, timer.Token)).ConfigureAwait(false) != cleanup)
                { onTimeout?.Invoke(); throw new TimeoutException("session_cleanup_unconfirmed"); }
            }
            finally { timer.Cancel(); }
        }
        await cleanup.ConfigureAwait(false);
    }
}

// 初始化与已交接连接共享一次取消通知；通知异常不跳过资源清理，并发 Stop 等待同一真实通知结果。
internal sealed class SessionLifetimeCancellation(CancellationTokenSource lifetime)
{
    private readonly TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int requested;

    internal Task<Exception?> Request()
    {
        if (Interlocked.Exchange(ref requested, 1) == 0)
        {
            try { lifetime.Cancel(); completion.TrySetResult(null); }
            catch (ObjectDisposedException) { completion.TrySetResult(null); }
            catch (Exception error) { completion.TrySetResult(error); }
        }
        return completion.Task;
    }
}

/// <summary>接入成功后的本机资源。Stop 不取消业务轮、不删除远端会话；无观察时也可作为连接寿命句柄。</summary>
public sealed class SessionConnection : IDisposable
{
    private readonly CancellationTokenSource lifetime;
    private readonly SessionLifetimeCancellation lifetimeCancellation;
    private readonly ISessionInitializationServices? services;
    private readonly ISessionPersistenceLease? persistence;
    private readonly TimeSpan cleanupTimeout;
    private readonly TaskCompletionSource<bool> stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> observationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private bool finished;

    internal SessionConnection(AgentSession session, JsonElement? capabilities, CancellationTokenSource lifetime, SessionLifetimeCancellation lifetimeCancellation,
        ISessionInitializationServices? services, ISessionPersistenceLease? persistence, SessionObservationOptions? observation, TimeSpan cleanupTimeout)
    {
        Session = session; Capabilities = capabilities; this.lifetime = lifetime; this.lifetimeCancellation = lifetimeCancellation; this.services = services; this.persistence = persistence; this.cleanupTimeout = cleanupTimeout;
        var events = observation?.Events ?? new EventStreamOptions();
        Recovery = observation?.Recovery;
        var fixedEvents = new EventStreamOptions { LastEventId = Recovery?.ObservationSequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? events.LastEventId,
            StopOnGap = Recovery == null && events.StopOnGap, Reconnect = events.Reconnect, Exclude = events.Exclude?.ToArray() };
        var pump = observation == null ? null : Recovery == null
            ? session.ObserveReadyAsync(observation.Observer, fixedEvents, lifetime.Token, () => observationReady.TrySetResult(true))
            : session.ObserveReadyAsync(async (item, token) =>
        {
            if (Recovery != null && !Recovery.Includes(item)) return;
            await observation.Observer(item, token).ConfigureAwait(false);
            if (Recovery != null) await Recovery.AfterEventAsync(item, token).ConfigureAwait(false);
        }, fixedEvents, lifetime.Token, () => observationReady.TrySetResult(true));
        if (pump == null) observationReady.TrySetResult(true);
        Completion = CompleteAsync(pump);
        SessionInitialization.ObserveFailure(Completion); SessionInitialization.ObserveFailure(observationReady.Task);
    }
    public AgentSession Session { get; }
    public JsonElement? Capabilities { get; }
    public SessionRecovery? Recovery { get; }
    public Task Completion { get; }
    internal Task ObservationReady => observationReady.Task;

    public async Task StopAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }
    public void Dispose()
    {
        lock (gate) { if (finished) return; }
        stop.TrySetResult(true);
        _ = lifetimeCancellation.Request();
    }
    private async Task CompleteAsync(Task? pump)
    {
        Exception? failure = null;
        Stopwatch? cleanupClock = null;
        var pumpCleanupTimedOut = false;
        try
        {
            if (pump == null) await stop.Task.ConfigureAwait(false);
            else if (await Task.WhenAny(pump, stop.Task).ConfigureAwait(false) == pump)
            {
                // 先观察原事件泵结果，不能先取消 lifetime 再误吞 observer 自己的取消／失败。
                await pump.ConfigureAwait(false);
            }
            else
            {
                cleanupClock = Stopwatch.StartNew();
                await lifetimeCancellation.Request().ConfigureAwait(false);
                try { await SessionInitialization.WaitForCleanupAsync(pump, cleanupTimeout, () => pumpCleanupTimedOut = true).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            failure = SessionInitialization.MergeFailures(failure, await lifetimeCancellation.Request().ConfigureAwait(false));
            observationReady.TrySetException(failure ?? new TansrProtocolException("event_stream_disconnected"));
            try
            {
                // 回调尚未退出时保留其供材资源；超时只结束等待，原任务晚到后仍完成一次逆序收尾。
                var cleanup = SessionInitialization.CleanupPreparationAsync(pump, services, Task.FromResult(persistence), lifetime);
                if (pumpCleanupTimedOut && !cleanup.IsCompleted)
                {
                    // The same cleanup budget already expired while awaiting this pump.
                    // Keep its original timeout; the late pump still owns resource cleanup.
                    SessionInitialization.ObserveFailure(cleanup);
                }
                else await SessionInitialization.WaitForCleanupAsync(cleanup, cleanupTimeout - (cleanupClock?.Elapsed ?? TimeSpan.Zero)).ConfigureAwait(false);
            }
            catch (Exception cleanup) { failure = SessionInitialization.MergeFailures(failure, cleanup); }
            lock (gate) finished = true;
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
