using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

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

    internal ExecutionHost(IExecutionClient client, IExecutionClient observationClient, IExecutionBackend backend,
        IExecutorJournal journal, Func<JsonElement, CancellationToken, Task> authorize, Func<int, CancellationToken, Task> idleDelay)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _observationClient = observationClient ?? throw new ArgumentNullException(nameof(observationClient));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        _idleDelay = idleDelay ?? throw new ArgumentNullException(nameof(idleDelay));
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

    private JsonElement Current()
    {
        lock (_gate) return _connection ?? throw new InvalidOperationException("执行器尚未连接。");
    }

    private async Task RunCoreAsync(Func<JsonElement, CancellationToken, Task>? onConnected, CancellationToken outer)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(outer, _stop.Token);
        Exception? heartbeatFailure = null;
        Task? heartbeat = null;
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
                    await _idleDelay(idleDelayMs, token).ConfigureAwait(false);
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
        }
        if (heartbeatFailure != null) throw new IOException("执行器续租失败。", heartbeatFailure);
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
                    ExecutionJson.Text(renewed, "connectionId") == ExecutionJson.Text(connection, "connectionId"));
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
        var result = await _observationClient.GetStatusAsync(ExecutionJson.Text(operation, "sessionId"),
            ExecutionJson.Text(operation, "operationId"), cancellationToken).ConfigureAwait(false);
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
