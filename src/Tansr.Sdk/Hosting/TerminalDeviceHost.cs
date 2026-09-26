using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Hosting;

public sealed class TerminalDeviceOptions
{
    public string SessionId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    /// <summary>Retain this identity with the original binding intent. An uncertain bind is never automatically repeated.</summary>
    public string BindingRequestId { get; set; } = string.Empty;
    public SessionContract SessionContract { get; set; }
    public IReadOnlyList<string>? RequestedTools { get; set; }
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxOutputReconnectAttempts { get; set; } = 3;
    public TimeSpan OutputReconnectDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>A bounded output projection. A verified output seal is not a process success receipt.</summary>
public sealed class TerminalOutputUpdate
{
    internal TerminalOutputUpdate(TerminalOutputView view, IReadOnlyList<TerminalOutputSegment> segments)
    { Segments = segments; LastSequence = view.LastSequence; HasPresentationGap = view.HasPresentationGap; SealVerified = view.SealVerified; Status = view.LastStatus; }
    public IReadOnlyList<TerminalOutputSegment> Segments { get; }
    public long? LastSequence { get; }
    public bool HasPresentationGap { get; }
    public bool SealVerified { get; }
    public JsonElement? Status { get; }
}

/// <summary>Assembles the original device host, negotiated output capture and executor notifications.
/// Connections, backend resources and journal remain caller-owned. StopAsync drains this host;
/// it does not close the remote conversation or delete its history. No control write is retried.</summary>
public sealed class TerminalDeviceHost : IDisposable
{
    private readonly object gate = new();
    private readonly DeviceSessionHost device;
    private readonly TerminalConnection controllerTerminal;
    private readonly DeferredSink output = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, Task> observations = new(StringComparer.Ordinal);
    private readonly int retries;
    private readonly TimeSpan retryDelay;
    private TerminalBinding? binding;
    private TerminalOutputSink? boundSink;
    private Task? completion;
    private bool disposed;

    public TerminalDeviceHost(IDeviceExecutionClient controller, IExecutionClient executor,
        TerminalConnection controllerTerminal, TerminalConnection deviceTerminal,
        Func<IExecutionOutputSink, IExecutionBackend> createBackend, IExecutorJournal journal,
        TerminalDeviceOptions options, Func<JsonElement, CancellationToken, Task> authorize)
    {
        this.controllerTerminal = controllerTerminal ?? throw new ArgumentNullException(nameof(controllerTerminal));
        if (deviceTerminal is null) throw new ArgumentNullException(nameof(deviceTerminal));
        if (createBackend is null) throw new ArgumentNullException(nameof(createBackend));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.BindingRequestId) || options.BindingRequestId.Length > 256 || options.BindingRequestId.Any(char.IsControl))
            throw new ArgumentException("A retained binding request identity is required.", nameof(options));
        if (options.SessionContract != SessionContract.Sdk1 && options.SessionContract != SessionContract.Sdk2OffloadV1)
            throw new ArgumentOutOfRangeException(nameof(options));
        TerminalCandidateContract.Validate("Id", ExecutionJson.Object(writer => writer.WriteString("id", options.BindingRequestId)).GetProperty("id"));
        if (options.MaxOutputReconnectAttempts < 0 || options.MaxOutputReconnectAttempts > 16 ||
            options.OutputReconnectDelay < TimeSpan.Zero || options.OutputReconnectDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options));
        retries = options.MaxOutputReconnectAttempts; retryDelay = options.OutputReconnectDelay;
        var sessionId = options.SessionId;
        var requestId = options.BindingRequestId;
        var contract = options.SessionContract == SessionContract.Sdk1 ? "sdk1" : "sdk2-offload-v1";
        device = new DeviceSessionHost(controller, executor,
            createBackend(output) ?? throw new ArgumentException("A device backend is required.", nameof(createBackend)), journal,
            new DeviceSessionOptions
            {
                SessionId = sessionId,
                WorkspaceId = options.WorkspaceId,
                RequestedTools = options.RequestedTools,
                ConnectionTimeout = options.ConnectionTimeout,
                AfterBindingAsync = async (bound, ct) =>
                {
                    var request = ExecutionJson.Object(writer =>
                    {
                        writer.WriteString("contract", "terminal-services-v1"); writer.WriteString("requestId", requestId);
                        writer.WriteStartObject("session"); writer.WriteString("sessionContract", contract); writer.WriteString("sessionId", sessionId); writer.WriteEndObject();
                        writer.WritePropertyName("executionBinding"); bound.GetProperty("binding").WriteTo(writer);
                        writer.WriteStartArray("required"); writer.WriteStringValue("execution-stream-v1"); writer.WriteEndArray();
                        writer.WriteStartArray("optional"); writer.WriteEndArray();
                    });
                    var authenticated = await controllerTerminal.BindAsync(request, ct).ConfigureAwait(false);
                    var attached = deviceTerminal.AttachBinding(authenticated);
                    var sink = deviceTerminal.CreateOutputSink(attached);
                    lock (gate) { binding = authenticated; boundSink = sink; }
                    output.Bind(sink);
                    notificationSource = new TerminalExecutionNotifications(deviceTerminal, attached);
                },
                ExecutionNotifications = (_, _) => Task.FromResult(notificationSource ?? throw new InvalidOperationException("Terminal binding is not ready."))
            }, authorize);
    }

    private IExecutionNotificationSource? notificationSource;
    public DeviceSessionState State => device.State;
    public JsonElement? Capabilities => device.Capabilities;
    public JsonElement? Connection => device.Connection;
    public string? LastNotificationErrorCode => device.LastNotificationErrorCode;
    public TerminalBinding? Binding { get { lock (gate) return binding; } }
    public Task Completion { get { lock (gate) return completion ?? throw new InvalidOperationException("The host has not started."); } }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TerminalDeviceHost));
            if (completion != null) throw new InvalidOperationException("The host can start only once.");
            completion = RunAsync(cancellationToken);
        }
        try { await ready.Task.ConfigureAwait(false); }
        catch { try { await Completion.ConfigureAwait(false); } catch { } throw; }
    }

    private async Task RunAsync(CancellationToken caller)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, stop.Token);
        try
        {
            await device.StartAsync(lifetime.Token).ConfigureAwait(false);
            ready.TrySetResult(true);
            await device.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { ready.TrySetCanceled(); }
        catch (Exception error) { ready.TrySetException(error); throw; }
        finally
        {
            stop.Cancel();
            Task[] pending; TerminalOutputSink? sink;
            lock (gate) { pending = observations.Values.ToArray(); sink = boundSink; }
            // Callbacks keep their slots until they actually exit; drain never means merely requesting cancellation.
            await Task.WhenAll(pending).ConfigureAwait(false);
            if (sink != null) await sink.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Observe original output with one decoder and cursor across bounded, read-only reconnects.
    /// Callback failures, authority failures and malformed events are never retried. EOF without a seal is not success.</summary>
    public async Task<TerminalOutputUpdate> ObserveOutputAsync(JsonElement operationReference,
        Func<TerminalOutputUpdate, CancellationToken, Task> observer, CancellationToken cancellationToken = default)
    {
        if (observer is null) throw new ArgumentNullException(nameof(observer));
        using var view = new TerminalOutputView(operationReference);
        var key = Protocol.WireJson.CanonicalString(operationReference);
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TerminalBinding authenticated;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TerminalDeviceHost));
            if (stop.IsCancellationRequested || State != DeviceSessionState.Ready || binding == null) throw new InvalidOperationException("The device is not ready.");
            if (observations.ContainsKey(key)) throw new TansrProtocolException("observation_already_active");
            if (observations.Count >= 8) throw new TansrProtocolException("capacity_exceeded");
            observations.Add(key, drained.Task); authenticated = binding;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var callbackFailed = false;
                var sealedOutput = false;
                using var stream = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                try
                {
                    await controllerTerminal.ObserveOutputAsync(authenticated, operationReference, view.LastSequence, async (value, ct) =>
                    {
                        var update = new TerminalOutputUpdate(view, view.ApplyEvent(value));
                        try { await observer(update, ct).ConfigureAwait(false); }
                        catch { callbackFailed = true; throw; }
                        if (view.SealVerified) { sealedOutput = true; stream.Cancel(); }
                    }, stream.Token).ConfigureAwait(false);
                    if (view.SealVerified) return new TerminalOutputUpdate(view, Array.Empty<TerminalOutputSegment>());
                    throw new EndOfStreamException("Output observation ended without a verified seal.");
                }
                catch (OperationCanceledException) when (sealedOutput && !callbackFailed && !lifetime.IsCancellationRequested)
                { return new TerminalOutputUpdate(view, Array.Empty<TerminalOutputSegment>()); }
                catch (Exception error) when (!callbackFailed && !lifetime.IsCancellationRequested && attempt < retries && IsReadReconnect(error)) { }
                await Task.Delay(retryDelay, lifetime.Token).ConfigureAwait(false);
            }
        }
        finally { lock (gate) observations.Remove(key); drained.TrySetResult(true); }
    }

    private static bool IsReadReconnect(Exception error) => error is HttpRequestException ||
        error is IOException && error is not InvalidDataException ||
        error is TansrProtocolException protocol && (protocol.Code == "event_stream_disconnected" ||
            protocol.Code == "network_error" || protocol.Code == "stream_idle_timeout" || protocol.Code == "sse_incomplete_frame");

    public async Task StopAsync()
    {
        Task? running;
        lock (gate) { stop.Cancel(); running = completion; }
        if (running != null) await running.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; stop.Cancel();
            if (completion != null) _ = completion.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private sealed class DeferredSink : IExecutionOutputSink
    {
        private IExecutionOutputSink? sink;
        internal void Bind(IExecutionOutputSink value)
        { if (Interlocked.CompareExchange(ref sink, value, null) != null) throw new InvalidOperationException("Output was already bound."); }
        public Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken cancellationToken) =>
            (Volatile.Read(ref sink) ?? throw new InvalidOperationException("Output is not bound.")).OpenAsync(operation, cancellationToken);
    }
}
