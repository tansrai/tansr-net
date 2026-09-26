using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Hosting;

public enum DeviceSessionState { Created, Initializing, Connecting, Binding, Ready, Stopped, Failed }

public sealed class DeviceSessionOptions
{
    public string SessionId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public IReadOnlyList<string>? RequestedTools { get; set; }
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>可信宿主在设备绑定后、允许发送前装配可选协议绑定。失败保持未就绪，不自动重试控制写入。</summary>
    /// <remarks>此回调结束后才开始设备 polling；只做能力协商/绑定。需要设备执行的记忆读取或命令
    /// 必须在 StartAsync 成功之后调用，否则服务等待设备而回调等待服务会形成循环。</remarks>
    public Func<JsonElement, CancellationToken, Task>? AfterBindingAsync { get; set; }
    /// <summary>Opt-in executor notifications, created after AfterBindingAsync. The factory
    /// uses the original authenticated terminal binding; it never grants new execution rights.
    /// The caller owns its terminal connection and releases it after StopAsync completes.</summary>
    public Func<JsonElement, CancellationToken, Task<IExecutionNotificationSource>>? ExecutionNotifications { get; set; }
}

/// <summary>
/// 一次装配负责声明平台、核对应用能力、登记设备、绑定工作区、续租、执行和耐久回执。
/// 停止仅分离本设备，等待正在运行的设备操作收尾；不关闭远端会话、不删除档案。
/// 后端、账本、客户端由调用者所有，须在 StopAsync 后释放。断线不自动换连接或重做副作用。
/// </summary>
public sealed class DeviceSessionHost : IDisposable
{
    private readonly IDeviceExecutionClient _client;
    private readonly IExecutionClient _deviceClient;
    private readonly Func<JsonElement, CancellationToken, Task>? _afterBinding;
    private readonly Func<JsonElement, CancellationToken, Task<IExecutionNotificationSource>>? _notificationFactory;
    private IExecutionNotificationSource? _notificationSource;
    private readonly ExecutionHost _execution;
    private readonly JsonElement _registration, _workspace;
    private readonly string _sessionId;
    private readonly string[]? _tools;
    private readonly TimeSpan _connectionTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _completion;
    private DeviceSessionState _state;
    private JsonElement? _capabilities;
    private JsonElement? _confirmedScope, _confirmedBinding;
    private bool _disposed;

    public DeviceSessionHost(IDeviceExecutionClient client, IExecutionBackend backend, IExecutorJournal journal,
        DeviceSessionOptions options, Func<JsonElement, CancellationToken, Task> authorize)
        : this(client, client, backend, journal, options, authorize) { }

    /// <summary>远端部署可使用分别持controller/executor权限的客户端；不把设备票据提升为控制票据。</summary>
    public DeviceSessionHost(IDeviceExecutionClient controllerClient, IExecutionClient deviceClient, IExecutionBackend backend,
        IExecutorJournal journal, DeviceSessionOptions options, Func<JsonElement, CancellationToken, Task> authorize)
    {
        _client = controllerClient ?? throw new ArgumentNullException(nameof(controllerClient));
        _deviceClient = deviceClient ?? throw new ArgumentNullException(nameof(deviceClient));
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (backend == null) throw new ArgumentNullException(nameof(backend));
        _registration = backend.Registration.Clone();
        WireJson.ValidateNamed("ExecutorRegistrationRequest", _registration);
        _workspace = _registration.GetProperty("workspaces").EnumerateArray()
            .SingleOrDefault(x => ExecutionJson.Text(x, "workspaceId") == options.WorkspaceId);
        if (_workspace.ValueKind != JsonValueKind.Object) throw new ArgumentException("工作区必须来自已登记的终端后端。", nameof(options));
        _sessionId = options.SessionId;
        _tools = options.RequestedTools?.ToArray();
        if (_tools != null && _tools.Distinct(StringComparer.Ordinal).Count() != _tools.Length)
            throw new ArgumentException("工具声明不得重复。", nameof(options));
        _connectionTimeout = options.ConnectionTimeout;
        _afterBinding = options.AfterBindingAsync;
        _notificationFactory = options.ExecutionNotifications;
        if (_connectionTimeout <= TimeSpan.Zero || _connectionTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(options));
        WireJson.ValidateNamed("SessionInitializeRequest", Initialization());
        if (authorize == null) throw new ArgumentNullException(nameof(authorize));
        _execution = new ExecutionHost(deviceClient, controllerClient, backend, journal, async (operation, ct) =>
        {
            GuardBoundOperation(operation);
            await authorize(operation, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            GuardBoundOperation(operation);
        }, new ExecutionHostOptions
        {
            NotificationSourceFactory = _notificationFactory == null ? null : (_, _) =>
                Task.FromResult(_notificationSource ?? throw new InvalidOperationException("执行通知尚未装配。"))
        });
    }

    public DeviceSessionState State { get { lock (_gate) return _state; } }
    public JsonElement? Capabilities { get { lock (_gate) return _capabilities?.Clone(); } }
    public JsonElement? Connection => _execution.Connection;
    /// <summary>Last recoverable notification transport error; contains a code only, never
    /// credentials or remote diagnostics. A fatal error is observed through Completion.</summary>
    public string? LastNotificationErrorCode => _execution.LastNotificationErrorCode;
    /// <summary>设备生命周期任务；异常必须由宿主观察。Ready 只证明绑定完成，不证明模型终局。</summary>
    public Task Completion { get { lock (_gate) return _completion ?? throw new InvalidOperationException("设备宿主尚未启动。"); } }

    /// <summary>只返回已绑定可消费的设备；token 控制本次设备生命周期。启动失败不自动重试注册或绑定。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DeviceSessionHost));
            if (_completion != null) throw new InvalidOperationException("设备宿主只能启动一次。");
            _completion = RunAsync(cancellationToken);
        }
        try { await _ready.Task.ConfigureAwait(false); }
        catch
        {
            // 出错返回前等待所有已启动的续租/执行任务收尾，并观察同一失败。
            try { await Completion.ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task StopAsync()
    {
        Task? completion;
        lock (_gate) { _stop.Cancel(); completion = _completion; }
        if (completion != null) await completion.ConfigureAwait(false);
    }

    /// <summary>Drain optional executor notifications before remote session close, keeping the original
    /// device polling, heartbeat and memory/tool settlement alive. Previously recorded failures remain visible.</summary>
    public Task QuiesceNotificationsAsync(CancellationToken cancellationToken = default) =>
        _execution.QuiesceNotificationsAsync(cancellationToken);

    private JsonElement Initialization() => ExecutionJson.Object(writer =>
    {
        writer.WriteString("protocol", "sdk2-ext-v1"); writer.WriteString("sessionId", _sessionId);
        writer.WritePropertyName("platform"); _registration.GetProperty("platform").WriteTo(writer);
        if (_tools != null)
        {
            writer.WritePropertyName("requestedTools"); writer.WriteStartArray();
            foreach (var tool in _tools) writer.WriteStringValue(tool);
            writer.WriteEndArray();
        }
    });

    private void SetState(DeviceSessionState state) { lock (_gate) _state = state; }

    private async Task RunAsync(CancellationToken outer)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(outer, _stop.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(_connectionTimeout);
        // 启动超时取消整个连接任务；绑定成功后停止该计时，不限制合法会话寿命。
        using var abort = deadline.Token.Register(() => lifetime.Cancel());
        var token = lifetime.Token;
        try
        {
            SetState(DeviceSessionState.Initializing);
            var scope = _client.ReadScope();
            WireJson.ValidateNamed("Scope", scope);
            var capabilities = await _client.InitializeAsync(Initialization(), token).ConfigureAwait(false);
            VerifyCapabilities(capabilities, scope);
            SetState(DeviceSessionState.Connecting);
            await _execution.RunAsync(async (connection, ct) =>
            {
                VerifyScope(scope);
                SetState(DeviceSessionState.Binding);
                var target = ExecutionJson.Object(writer =>
                {
                    foreach (var name in new[] { "executorId", "connectionId", "connectionRevision" })
                        writer.WriteString(name, ExecutionJson.Text(connection, name));
                    writer.WriteString("workspaceId", ExecutionJson.Text(_workspace, "workspaceId"));
                    writer.WriteString("workspaceRevision", ExecutionJson.Text(_workspace, "revision"));
                    if (_registration.TryGetProperty("interpreter", out var interpreter))
                    { writer.WritePropertyName("interpreter"); interpreter.WriteTo(writer); }
                });
                var request = ExecutionJson.Object(writer =>
                {
                    writer.WriteString("protocol", "sdk2-ext-v1"); writer.WriteString("sessionId", _sessionId);
                    writer.WriteString("executorId", ExecutionJson.Text(connection, "executorId"));
                    writer.WriteString("connectionId", ExecutionJson.Text(connection, "connectionId"));
                    writer.WriteString("workspaceId", ExecutionJson.Text(_workspace, "workspaceId"));
                    writer.WriteString("expectedCapabilityRevision", ExecutionJson.Text(capabilities, "capabilityRevision"));
                });
                var bound = await _client.BindExecutionAsync(request, target, ct).ConfigureAwait(false);
                VerifyCapabilities(bound, scope);
                var binding = bound.GetProperty("binding");
                ExecutionJson.Check(binding.ValueKind == JsonValueKind.Object && ExecutionJson.Equal(binding.GetProperty("target"), target));
                if (_afterBinding != null) await _afterBinding(bound.Clone(), ct).ConfigureAwait(false);
                if (_notificationFactory != null)
                {
                    var source = await _notificationFactory(connection.Clone(), ct).ConfigureAwait(false)
                        ?? throw new InvalidDataException("执行通知来源缺失。");
                    var notificationScope = source.Scope;
                    WireJson.ValidateNamed("Scope", notificationScope);
                    ExecutionJson.Check(ExecutionJson.Equal(scope, notificationScope));
                    _notificationSource = source;
                }
                VerifyScope(scope);
                ct.ThrowIfCancellationRequested();
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                lock (_gate)
                {
                    _capabilities = bound.Clone(); _confirmedScope = scope.Clone();
                    _confirmedBinding = binding.Clone(); _state = DeviceSessionState.Ready;
                }
                _ready.TrySetResult(true);
            }, token).ConfigureAwait(false);
            SetState(DeviceSessionState.Stopped);
            if (!_ready.Task.IsCompleted) _ready.TrySetCanceled();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { SetState(DeviceSessionState.Stopped); _ready.TrySetCanceled(); }
        catch (Exception error)
        { SetState(DeviceSessionState.Failed); _ready.TrySetException(error); throw; }
        finally { lifetime.Cancel(); }
    }

    private void VerifyScope(JsonElement original)
    {
        WireJson.ValidateNamed("Scope", _client.ReadScope());
        ExecutionJson.Check(ExecutionJson.Equal(original, _client.ReadScope()));
        ExecutionJson.Check(ExecutionJson.Equal(original, _deviceClient.ReadScope()));
    }

    private void GuardBoundOperation(JsonElement operation)
    {
        JsonElement? scope, binding;
        lock (_gate) { scope = _confirmedScope; binding = _confirmedBinding; }
        // 一次装配固定原主体、完整绑定和工作区代际。登录/撤权/重绑须显式停止后再装配。
        if (!scope.HasValue || !binding.HasValue || ExecutionJson.Text(operation, "sessionId") != _sessionId ||
            !ExecutionJson.Equal(scope.Value, _client.ReadScope()) || !ExecutionJson.Equal(scope.Value, _deviceClient.ReadScope()) ||
            !ExecutionJson.Equal(scope.Value, operation.GetProperty("scope")) ||
            !ExecutionJson.Equal(binding.Value, operation.GetProperty("binding"))) throw new ExecutionRejectedException("EACCES");
    }

    private void VerifyCapabilities(JsonElement value, JsonElement scope)
    {
        WireJson.ValidateNamed("SessionExecutionCapabilities", value);
        VerifyScope(scope);
        ExecutionJson.Check(ExecutionJson.Text(value, "sessionId") == _sessionId &&
            ExecutionJson.Equal(value.GetProperty("platform"), _registration.GetProperty("platform")));
    }

    /// <summary>仅发出取消；异步清理完成必须等待 StopAsync/Completion。</summary>
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _stop.Cancel(); }
    }
}
