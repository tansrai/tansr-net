using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Execution;

/// <summary>设备连接、续租、领取与耐久回执。停止观察/传输不会被误报为未知副作用已回滚。</summary>
public sealed class ExecutionHost : IDisposable
{
    private readonly IExecutionClient _client;
    private readonly IExecutionClient _observationClient;
    private readonly IExecutionBackend _backend;
    private readonly IExecutorJournal _journal;
    private readonly Func<JsonElement, CancellationToken, Task> _authorize;
    private readonly Func<int, CancellationToken, Task> _idleDelay;
    private readonly Func<JsonElement, CancellationToken, Task<IExecutionNotificationSource>>? _notificationFactory;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private IExecutionNotificationSource? _notifications;
    private JsonElement _notificationScope;
    private JsonElement _notificationConnection;
    private ActiveExecution? _active;
    private string? _notificationError;
    private Exception? _notificationFailure;
    private readonly CancellationTokenSource _notificationStop = new();
    private readonly TaskCompletionSource<bool> _notificationsStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly JsonElement _registration;
    private JsonElement? _connection;
    private Task? _running;
    private bool _started;
    private bool _disposed;

    public ExecutionHost(IExecutionClient client, IExecutionBackend backend, IExecutorJournal journal,
        Func<JsonElement, CancellationToken, Task> authorize)
        : this(client, client, backend, journal, authorize) { }

    /// <summary>设备票据用于派工和回执；独立控制票据仅查询原执行状态，不提升设备票据权限。</summary>
    public ExecutionHost(IExecutionClient client, IExecutionClient observationClient, IExecutionBackend backend,
        IExecutorJournal journal, Func<JsonElement, CancellationToken, Task> authorize)
        : this(client, observationClient, backend, journal, authorize, Task.Delay) { }

    /// <summary>Enable explicitly negotiated executor notifications without changing original execution or polling semantics.</summary>
    public ExecutionHost(IExecutionClient client, IExecutionClient observationClient, IExecutionBackend backend,
        IExecutorJournal journal, Func<JsonElement, CancellationToken, Task> authorize, ExecutionHostOptions options)
        : this(client, observationClient, backend, journal, authorize, Task.Delay, options ?? throw new ArgumentNullException(nameof(options))) { }

    internal ExecutionHost(IExecutionClient client, IExecutionClient observationClient, IExecutionBackend backend,
        IExecutorJournal journal, Func<JsonElement, CancellationToken, Task> authorize, Func<int, CancellationToken, Task> idleDelay,
        ExecutionHostOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _observationClient = observationClient ?? throw new ArgumentNullException(nameof(observationClient));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        _idleDelay = idleDelay ?? throw new ArgumentNullException(nameof(idleDelay));
        _notificationFactory = options?.NotificationSourceFactory;
        _registration = backend.Registration.Clone();
        WireJson.ValidateNamed("ExecutorRegistrationRequest", _registration);
        var operations = _registration.GetProperty("operations").EnumerateArray().Select(x => x.GetString()).ToArray();
        ExecutionJson.Check(operations.Distinct(StringComparer.Ordinal).Count() == operations.Length);
        var workspaces = _registration.GetProperty("workspaces").EnumerateArray().Select(x => ExecutionJson.Text(x, "workspaceId")).ToArray();
        ExecutionJson.Check(workspaces.Distinct(StringComparer.Ordinal).Count() == workspaces.Length);
        var hasTools = _registration.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0;
        ExecutionJson.Check(operations.Contains("tool.invoke") == hasTools);
        if (hasTools)
        {
            var names = tools.EnumerateArray().Select(x => ExecutionJson.Text(x, "name")).ToArray();
            ExecutionJson.Check(names.Distinct(StringComparer.Ordinal).Count() == names.Length);
        }
    }

    public JsonElement? Connection { get { lock (_gate) return _connection?.Clone(); } }
    /// <summary>Last notification transport failure, without payloads or credentials. Polling remains the fallback.</summary>
    public string? LastNotificationErrorCode { get { lock (_gate) return _notificationError; } }

    /// <summary>登记后调用 onConnected 进行明确的会话绑定。宿主必须保留并等待此任务。</summary>
    public Task RunAsync(Func<JsonElement, CancellationToken, Task>? onConnected = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed || _stop.IsCancellationRequested) throw new ObjectDisposedException(nameof(ExecutionHost));
            if (_started) throw new InvalidOperationException("执行宿主只能运行一次。");
            _started = true;
            _running = RunCoreAsync(onConnected, cancellationToken);
            return _running;
        }
    }

    public async Task StopAsync()
    {
        Task? running;
        lock (_gate) { _stop.Cancel(); running = _running; }
        if (running != null) await running.ConfigureAwait(false);
    }

    /// <summary>Drain only optional notification observation before closing its remote session.
    /// Original polling, heartbeat, execution and durable settlement continue until StopAsync.
    /// This is irreversible for this host; an already recorded notification failure remains a failure.</summary>
    public async Task QuiesceNotificationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ExecutionHost));
            if (!_started) throw new InvalidOperationException("执行宿主尚未启动。");
            _notificationStop.Cancel();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Task.Delay(5000, deadline.Token);
        var completed = await Task.WhenAny(_notificationsStopped.Task, timeout).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed != _notificationsStopped.Task) throw new IOException("execution_notifications_stop_timeout");
        deadline.Cancel();
        await _notificationsStopped.Task.ConfigureAwait(false);
        Exception? failure;
        Task? running;
        lock (_gate) { failure = _notificationFailure; running = _running; }
        if (failure != null) throw new IOException("执行通知失效；设备执行已取消并等待收尾。", failure);
        if (running != null && running.IsCompleted) await running.ConfigureAwait(false);
    }

    private JsonElement Current()
    {
        lock (_gate) return _connection ?? throw new InvalidOperationException("执行器尚未连接。");
    }

    private async Task RunCoreAsync(Func<JsonElement, CancellationToken, Task>? onConnected, CancellationToken outer)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(outer, _stop.Token);
        using var notificationLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, _notificationStop.Token);
        Exception? heartbeatFailure = null;
        Task? heartbeat = null;
        Task? notifications = null;
        var token = lifetime.Token;
        try
        {
            var registered = await _client.RegisterAsync(_registration, token).ConfigureAwait(false);
            WireJson.ValidateNamed("ExecutorConnection", registered);
            ExecutionJson.Check(ExecutionJson.Text(registered, "executorId") == ExecutionJson.Text(_registration, "executorId"));
            token.ThrowIfCancellationRequested();
            lock (_gate) _connection = registered.Clone();
            heartbeat = RenewAsync(lifetime, error => heartbeatFailure = error);
            if (onConnected != null) await onConnected(registered.Clone(), token).ConfigureAwait(false);
            if (_notificationFactory != null)
            {
                try
                {
                    var scope = _client.ReadScope();
                    _notifications = await _notificationFactory(registered.Clone(), token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("执行通知来源缺失。");
                    _notificationScope = _notifications.Scope.Clone();
                    _notificationConnection = registered.Clone();
                    WireJson.ValidateNamed("Scope", _notificationScope);
                    ExecutionJson.Check(ExecutionJson.Equal(scope, _notificationScope));
                    CheckNotifications(registered, token);
                    notifications = ObserveNotificationsAsync(registered, lifetime, notificationLifetime.Token);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    // Publish initialization failure before finally signals notification drain.
                    lock (_gate) _notificationFailure ??= error;
                    throw;
                }
            }
            else _notificationsStopped.TrySetResult(true);
            var idleDelayMs = 250;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                EnsureLive(Current());
                var requested = Current();
                var batch = await _client.PollAsync(requested, token).ConfigureAwait(false);
                WireJson.ValidateNamed("ExecutionBatch", batch);
                ExecutionJson.Check(ExecutionJson.Text(batch, "executorId") == ExecutionJson.Text(requested, "executorId") &&
                    ExecutionJson.Text(batch, "connectionId") == ExecutionJson.Text(requested, "connectionId"));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var operation in batch.GetProperty("operations").EnumerateArray())
                {
                    ExecutionJson.Check(seen.Add(ExecutionJson.Text(operation, "operationId")));
                    var receipt = await ExecuteAsync(operation.Clone(), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var originalScope = operation.GetProperty("scope");
                    var settlementScope = _client.ReadScope();
                    ExecutionJson.Check(ExecutionJson.Text(originalScope, "applicationScopeId") == ExecutionJson.Text(settlementScope, "applicationScopeId") &&
                        ExecutionJson.Text(originalScope, "endUserId") == ExecutionJson.Text(settlementScope, "endUserId"));
                    try { await _client.SubmitAsync(receipt, token).ConfigureAwait(false); }
                    catch when (!token.IsCancellationRequested)
                    {
                        // 提交回包丢失只能查原键；已执行操作不可改ID或重做。
                        var state = await ReadStatusAsync(operation, token).ConfigureAwait(false);
                        ExecutionJson.Check(ExecutionJson.Equal(state.GetProperty("operation"), operation) &&
                            state.GetProperty("receipt").ValueKind != JsonValueKind.Null && ExecutionJson.Equal(state.GetProperty("receipt"), receipt));
                    }
                }
                // The next serial operation may still be preparing after its predecessor's receipt.
                // Stay briefly responsive after settled work; sustained idle keeps the original cap.
                if (batch.GetProperty("operations").GetArrayLength() == 0)
                {
                    await WaitForWorkAsync(idleDelayMs, token).ConfigureAwait(false);
                    idleDelayMs = Math.Min(250, idleDelayMs * 2);
                }
                else idleDelayMs = 25;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (heartbeatFailure != null) throw new IOException("执行器续租失败；设备执行已取消并等待收尾。", heartbeatFailure);
        }
        finally
        {
            lifetime.Cancel();
            if (heartbeat != null) await heartbeat.ConfigureAwait(false);
            if (notifications != null)
            {
                if (await Task.WhenAny(notifications, Task.Delay(5000)).ConfigureAwait(false) != notifications)
                {
                    _ = notifications.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw new IOException("execution_notifications_stop_timeout");
                }
                await notifications.ConfigureAwait(false);
            }
            else _notificationsStopped.TrySetResult(true);
        }
        if (heartbeatFailure != null) throw new IOException("执行器续租失败。", heartbeatFailure);
        Exception? notificationFailure;
        lock (_gate) notificationFailure = _notificationFailure;
        if (notificationFailure != null) throw new IOException("执行通知失效；设备执行已取消并等待收尾。", notificationFailure);
    }

    private async Task RenewAsync(CancellationTokenSource lifetime, Action<Exception> failed)
    {
        var token = lifetime.Token;
        try
        {
            while (true)
            {
                var connection = Current();
                var until = Expiry(connection) - DateTimeOffset.UtcNow;
                if (until <= TimeSpan.Zero) throw new IOException("执行连接已经过期。");
                var delay = Math.Min(connection.GetProperty("heartbeatAfterMs").GetInt32(), Math.Max(1, until.TotalMilliseconds / 2));
                await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
                var renewed = await _client.HeartbeatAsync(connection, token).ConfigureAwait(false);
                WireJson.ValidateNamed("ExecutorConnection", renewed);
                ExecutionJson.Check(ExecutionJson.Text(renewed, "executorId") == ExecutionJson.Text(connection, "executorId") &&
                    ExecutionJson.Text(renewed, "connectionId") == ExecutionJson.Text(connection, "connectionId") &&
                    ExecutionJson.Text(renewed, "connectionRevision") == ExecutionJson.Text(connection, "connectionRevision"));
                EnsureLive(renewed);
                lock (_gate) _connection = renewed.Clone();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { failed(error); lifetime.Cancel(); }
    }

    private static DateTimeOffset Expiry(JsonElement value) => DateTimeOffset.Parse(ExecutionJson.Text(value, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private static void EnsureLive(JsonElement value)
    {
        if (Expiry(value) <= DateTimeOffset.UtcNow) throw new ExecutionRejectedException("ESTALE");
    }

    private async Task GuardAsync(JsonElement operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var connection = Current();
        EnsureLive(connection);
        EnsureLive(operation);
        var scope = _client.ReadScope();
        var target = operation.GetProperty("binding").GetProperty("target");
        ExecutionJson.Check(ExecutionJson.Equal(scope, operation.GetProperty("scope")) &&
            ExecutionJson.Text(target, "executorId") == ExecutionJson.Text(connection, "executorId") &&
            ExecutionJson.Text(target, "connectionId") == ExecutionJson.Text(connection, "connectionId") &&
            ExecutionJson.Text(target, "connectionRevision") == ExecutionJson.Text(connection, "connectionRevision"));
        await _authorize(operation.Clone(), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        EnsureLive(Current());
        EnsureLive(operation);
        ExecutionJson.Check(ExecutionJson.Equal(scope, _client.ReadScope()) &&
            ExecutionJson.Text(Current(), "connectionRevision") == ExecutionJson.Text(connection, "connectionRevision"));
    }

    private async Task<JsonElement> ExecuteAsync(JsonElement operation, CancellationToken token)
    {
        ExecutionJson.Operation(operation);
        await GuardAsync(operation, token).ConfigureAwait(false);
        var request = operation.GetProperty("request");
        var target = operation.GetProperty("binding").GetProperty("target");
        var operationName = ExecutionJson.Text(request, "operation");
        ExecutionJson.Check(_registration.GetProperty("operations").EnumerateArray().Any(item => item.GetString() == operationName));
        ExecutionJson.Check(_registration.GetProperty("workspaces").EnumerateArray().Any(item =>
            ExecutionJson.Text(item, "workspaceId") == ExecutionJson.Text(target, "workspaceId") && ExecutionJson.Text(item, "revision") == ExecutionJson.Text(target, "workspaceRevision")));
        if (operationName == "tool.invoke")
        {
            var args = request.GetProperty("args");
            ExecutionJson.Check(_registration.GetProperty("tools").EnumerateArray().Any(item =>
                ExecutionJson.Text(item, "name") == ExecutionJson.Text(args, "name") && ExecutionJson.Text(item, "definitionDigest") == ExecutionJson.Text(args, "definitionDigest")));
        }
        if (operationName == "process.exec")
            ExecutionJson.Check(ExecutionJson.Equal(_registration.GetProperty("interpreter"), request.GetProperty("args").GetProperty("interpreter")));

        var claim = await _journal.ClaimAsync(operation, token).ConfigureAwait(false);
        if (claim.Status == ExecutorJournalClaimStatus.Completed)
        {
            var stored = claim.Receipt ?? throw new InvalidDataException("耐久回执缺失。");
            ExecutionJson.Receipt(operation, stored);
            return stored;
        }
        if (claim.Status == ExecutorJournalClaimStatus.Pending)
        {
            var unknown = ExecutionJson.ReceiptFor(operation, "unknown", null, "execution_outcome_unknown");
            await _journal.CompleteAsync(operation, unknown, CancellationToken.None).ConfigureAwait(false);
            return unknown;
        }

        JsonElement receipt;
        var invoked = false;
        using var operationLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(operationLifetime.Token);
        var active = new ActiveExecution(operation, operationLifetime);
        lock (_gate) _active = active;
        Task? monitor = null;
        var remaining = Expiry(operation) - DateTimeOffset.UtcNow;
        operationLifetime.CancelAfter(remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < TimeSpan.FromSeconds(125) ? remaining : TimeSpan.FromSeconds(125));
        try
        {
            await GuardAsync(operation, operationLifetime.Token).ConfigureAwait(false);
            monitor = MonitorOperationAsync(operation, operationLifetime, observation.Token);
            invoked = true;
            var result = await _backend.ExecuteAsync(operation, ct => GuardAsync(operation, ct), operationLifetime.Token).ConfigureAwait(false);
            WireJson.ValidateNamed("ResourceResult", result);
            receipt = ExecutionJson.ReceiptFor(operation, "completed", result, null);
            ExecutionJson.Receipt(operation, receipt);
        }
        catch (ExecutionRejectedException error) { receipt = ExecutionJson.ReceiptFor(operation, "failed", null, error.Code); }
        catch (Exception) { receipt = ExecutionJson.ReceiptFor(operation, invoked ? "unknown" : "failed", null, invoked ? "execution_outcome_unknown" : "ECANCELED"); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_active, active)) _active = null;
            observation.Cancel();
            if (monitor != null) await monitor.ConfigureAwait(false);
        }
        // 不拿已取消的运行票取消本地终局落盘；存储失败向调用者保留未决责任。
        await _journal.CompleteAsync(operation, receipt, CancellationToken.None).ConfigureAwait(false);
        return receipt;
    }

    private async Task<JsonElement> ReadStatusAsync(JsonElement operation, CancellationToken cancellationToken)
    {
        var current = _client.ReadScope();
        var original = operation.GetProperty("scope");
        ExecutionJson.Check(ExecutionJson.Equal(current, _observationClient.ReadScope()) &&
            ExecutionJson.Text(current, "applicationScopeId") == ExecutionJson.Text(original, "applicationScopeId") &&
            ExecutionJson.Text(current, "endUserId") == ExecutionJson.Text(original, "endUserId"));
        JsonElement result;
        if (_notifications == null)
            result = await _observationClient.GetStatusAsync(ExecutionJson.Text(operation, "sessionId"),
                ExecutionJson.Text(operation, "operationId"), cancellationToken).ConfigureAwait(false);
        else
        {
            CheckNotifications(Current(), cancellationToken);
            result = await _notifications.GetStatusAsync(operation.Clone(), cancellationToken).ConfigureAwait(false);
            CheckNotifications(Current(), cancellationToken);
            WireJson.ValidateNamed("ExecutionStatus", result);
            ExecutionJson.Check(ExecutionJson.Equal(result.GetProperty("operation"), operation));
            var receipt = result.GetProperty("receipt");
            if (receipt.ValueKind != JsonValueKind.Null)
            {
                ExecutionJson.Receipt(operation, receipt);
                ExecutionJson.Check(ExecutionJson.Text(result, "status") == ExecutionJson.Text(receipt, "status"));
            }
            else ExecutionJson.Check(ExecutionJson.Text(result, "status") == "pending" || ExecutionJson.Text(result, "status") == "unknown");
        }
        ExecutionJson.Check(ExecutionJson.Equal(current, _client.ReadScope()) && ExecutionJson.Equal(current, _observationClient.ReadScope()));
        return result;
    }

    private async Task MonitorOperationAsync(JsonElement operation, CancellationTokenSource execution, CancellationToken observation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(1000, observation).ConfigureAwait(false);
                var target = operation.GetProperty("binding").GetProperty("target");
                var connection = Current();
                EnsureLive(connection);
                EnsureLive(operation);
                ExecutionJson.Check(ExecutionJson.Equal(_client.ReadScope(), operation.GetProperty("scope")) &&
                    ExecutionJson.Text(connection, "connectionId") == ExecutionJson.Text(target, "connectionId") &&
                    ExecutionJson.Text(connection, "connectionRevision") == ExecutionJson.Text(target, "connectionRevision"));
                var state = await ReadStatusAsync(operation, observation).ConfigureAwait(false);
                ExecutionJson.Check(ExecutionJson.Equal(state.GetProperty("operation"), operation) &&
                    ExecutionJson.Text(state, "status") == "pending" && state.GetProperty("receipt").ValueKind == JsonValueKind.Null);
            }
        }
        catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
        catch (Exception) { execution.Cancel(); }
    }

    private async Task WaitForWorkAsync(int milliseconds, CancellationToken token)
    {
        if (_notifications == null) { await _idleDelay(milliseconds, token).ConfigureAwait(false); return; }
        if (_wake.Wait(0)) return;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delay = _idleDelay(milliseconds, waiting.Token);
        var wake = _wake.WaitAsync(waiting.Token);
        var completed = await Task.WhenAny(delay, wake).ConfigureAwait(false);
        waiting.Cancel();
        try { await completed.ConfigureAwait(false); }
        finally
        {
            try { await (ReferenceEquals(completed, delay) ? wake : delay).ConfigureAwait(false); }
            catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { } // At most one pending wake survives polling/execution.
    }

    private void CheckNotifications(JsonElement connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureLive(Current());
        ExecutionJson.Check(ExecutionJson.Equal(_notificationScope, _client.ReadScope()) &&
            ExecutionJson.Equal(_notificationScope, _notifications!.Scope));
        foreach (var field in new[] { "executorId", "connectionId", "connectionRevision" })
            ExecutionJson.Check(ExecutionJson.Text(Current(), field) == ExecutionJson.Text(connection, field) &&
                ExecutionJson.Text(connection, field) == ExecutionJson.Text(_notificationConnection, field));
    }

    private async Task ObserveNotificationsAsync(JsonElement connection, CancellationTokenSource lifetime, CancellationToken token)
    {
        var cursor = new TerminalExecutorEventCursor(ExecutionJson.Text(connection, "executorId"), ExecutionJson.Text(connection, "connectionId"));
        try
        {
            while (true)
            {
                CheckNotifications(connection, token);
                var reconnectDelayMs = 250;
                try
                {
                    await _notifications!.ObserveAsync(connection.Clone(), cursor.LastEventId, async (value, callbackToken) =>
                    {
                        callbackToken.ThrowIfCancellationRequested(); CheckNotifications(connection, token);
                        bool accepted = cursor.Apply(value);
                        if (accepted || cursor.RequiresReconciliation)
                        {
                            // Notifications never grant execution or cancellation authority. Read the exact
                            // active operation, including its digest, before stopping that operation.
                            if (await ReconcileActiveAsync(token).ConfigureAwait(false))
                            {
                                cursor.ClearReconciliation();
                                lock (_gate) _notificationError = null;
                            }
                        }
                        callbackToken.ThrowIfCancellationRequested(); CheckNotifications(connection, token);
                    }, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    ReportNotificationError(new TansrProtocolException("event_stream_disconnected"));
                }
                catch (Exception error) when (!token.IsCancellationRequested && CanReconnect(error))
                {
                    ReportNotificationError(error);
                    if (error is TansrHttpException http && http.RetryAfterMs.HasValue)
                        reconnectDelayMs = Math.Max(reconnectDelayMs, http.RetryAfterMs.Value);
                }
                token.ThrowIfCancellationRequested();
                cursor.NoticeGap();
                if (await ReconcileActiveAsync(token).ConfigureAwait(false)) cursor.ClearReconciliation();
                // Reopen the same negotiated source, never register/rebind or retry side effects.
                await Task.Delay(reconnectDelayMs, token).ConfigureAwait(false);
            }
        }
        // Closing an HTTP stream during shutdown may surface an I/O/protocol transport error
        // instead of OCE. A failure recorded before cancellation is still retained by RunCoreAsync.
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            ReportNotificationError(error);
            lock (_gate) _notificationFailure ??= error;
            lifetime.Cancel();
        }
        finally { _notificationsStopped.TrySetResult(true); }
    }

    private async Task<bool> ReconcileActiveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Wake();
        ActiveExecution? active;
        lock (_gate) active = _active;
        if (active == null) return true;
        try
        {
            using var query = CancellationTokenSource.CreateLinkedTokenSource(token);
            query.CancelAfter(5000);
            var state = await ReadStatusAsync(active.Operation, query.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ExecutionJson.Check(ExecutionJson.Equal(state.GetProperty("operation"), active.Operation));
            if (ExecutionJson.Text(state, "status") != "pending")
            {
                lock (_gate) if (!ReferenceEquals(_active, active)) return true;
                try { active.Lifetime.Cancel(); }
                catch (ObjectDisposedException) { } // This exact operation has already finished.
            }
            return true;
        }
        catch (Exception error) when (!token.IsCancellationRequested && CanReconnect(error))
        {
            ReportNotificationError(error);
            return false; // A failed read is not proof of cancellation; the original monitor remains active.
        }
    }

    private static bool CanReconnect(Exception error)
    {
        if (error is TansrHttpException http) return http.RetryAction == "backoff" &&
            (http.StatusCode == 429 && (http.Code == "capacity_exceeded" || http.Code == "request_limit") ||
             http.StatusCode == 503 && http.Code == "source_unavailable" || http.StatusCode == 409 && http.Code == "busy");
        if (error is TansrProtocolException protocol) return protocol.Code == "network_error" || protocol.Code == "stream_idle_timeout" ||
            protocol.Code == "event_stream_disconnected" || protocol.Code == "sse_incomplete_frame";
        if (error is InvalidDataException || error is WireProtocolException) return false;
        return error is HttpRequestException || error is IOException || error is OperationCanceledException;
    }

    private void ReportNotificationError(Exception error)
    {
        lock (_gate) _notificationError = error is TansrException tansr ? tansr.Code :
            error is OperationCanceledException ? "notification_timeout" : "execution_notification_failed";
    }

    private sealed class ActiveExecution
    {
        internal ActiveExecution(JsonElement operation, CancellationTokenSource lifetime) { Operation = operation.Clone(); Lifetime = lifetime; }
        internal JsonElement Operation { get; }
        internal CancellationTokenSource Lifetime { get; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
        }
        // 调用者须另行等待 StopAsync；Dispose 不谎称异步清理已经完成。
    }
}
