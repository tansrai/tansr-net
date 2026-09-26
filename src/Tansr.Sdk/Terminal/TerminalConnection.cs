using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

/// <summary>Opt-in, schema-pinned terminal output connection. Controller and device connections
/// may use different credentials. A validated binding can be handed to the device without
/// handing it controller credentials. This connection does not execute tools.</summary>
public sealed class TerminalConnection : IDisposable
{
    private readonly TerminalCandidateHttpTransport transport;
    private readonly TerminalCandidateClient client;
    private readonly Uri baseUri;
    private readonly object gate = new();
    private readonly HashSet<TerminalOutputSink> sinks = new();
    private readonly HashSet<string> observations = new(StringComparer.Ordinal);
    private bool disposed;

    public TerminalConnection(TansrClientOptions options, bool enablePreview = false, HttpClient? httpClient = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (!enablePreview) throw new TansrProtocolException("unsupported_capability");
        var scope = options.ExecutionScopeProvider ?? throw new ArgumentException("A trusted execution scope is required.", nameof(options));
        transport = new TerminalCandidateHttpTransport(options, httpClient);
        baseUri = options.BaseUri;
        client = new TerminalCandidateClient(transport, scope);
    }

    public Task<JsonElement> DiscoverAsync(CancellationToken cancellationToken = default)
    { EnsureOpen(); return client.DiscoverAsync(cancellationToken); }

    /// <summary>The controller binds once before the first send. It must durably retain its
    /// original request identity when the binding outcome is uncertain; no automatic retry occurs.</summary>
    public async Task<TerminalBinding> BindAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var binding = await client.BindAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureOpen(); return new TerminalBinding(this, binding);
    }

    /// <summary>Attach a binding already authenticated by a controller connection to this
    /// device connection. The endpoint and current full scope must match. Serve independently
    /// authenticates every subsequent output write; attachment is not execution authorization.</summary>
    public TerminalBinding AttachBinding(TerminalBinding authenticatedBinding)
    {
        if (authenticatedBinding is null) throw new ArgumentNullException(nameof(authenticatedBinding));
        EnsureOpen();
        if (baseUri != authenticatedBinding.Owner.baseUri) throw new TansrProtocolException("binding_conflict");
        var binding = new TerminalCandidateBinding(client, authenticatedBinding.Inner.Raw);
        client.AssertBinding(binding);
        return new TerminalBinding(this, binding);
    }

    private void Check(TerminalBinding binding)
    {
        EnsureOpen();
        if (binding is null || !ReferenceEquals(binding.Owner, this)) throw new ArgumentException("Binding belongs to another terminal connection.", nameof(binding));
        client.AssertBinding(binding.Inner);
    }

    /// <summary>Pass this sink to the native execution backend. It captures only already
    /// authorized operations and never starts or retries a native process.</summary>
    public TerminalOutputSink CreateOutputSink(TerminalBinding binding)
    {
        Check(binding);
        lock (gate)
        {
            EnsureOpen();
            if (sinks.Count >= 8) throw new TansrProtocolException("capacity_exceeded");
            var sink = new TerminalOutputSink(new TerminalExecutionOutputSink(client, binding.Inner), Release);
            sinks.Add(sink); return sink;
        }
    }

    private void Release(TerminalOutputSink sink) { lock (gate) sinks.Remove(sink); }

    public Task<JsonElement> GetExecutionStateAsync(TerminalBinding binding, JsonElement originalOperation, CancellationToken cancellationToken = default)
    { Check(binding); return client.GetExecutionStateAsync(binding.Inner, originalOperation, cancellationToken); }

    public async Task<JsonElement> GetOutputStatusAsync(TerminalBinding binding, JsonElement operationReference, CancellationToken cancellationToken = default)
    { Check(binding); return (await client.GetOutputStatusAsync(binding.Inner, operationReference, cancellationToken).ConfigureAwait(false)).Raw; }

    /// <summary>Observe one bounded SSE connection. Keep the same output view across reconnects
    /// and pass the last successfully applied raw sequence explicitly. EOF is not success.</summary>
    public async Task ObserveOutputAsync(TerminalBinding binding, JsonElement operationReference, long? afterSequence,
        Func<JsonElement, CancellationToken, Task> observer, CancellationToken cancellationToken = default)
    {
        Check(binding);
        if (observer is null) throw new ArgumentNullException(nameof(observer));
        TerminalCandidateContract.Validate("OperationReference", operationReference);
        var key = WireJson.CanonicalString(binding.Inner.Session) + ":" + WireJson.CanonicalString(operationReference);
        lock (gate)
        {
            EnsureOpen();
            if (observations.Contains(key)) throw new TansrProtocolException("observation_already_active");
            if (observations.Count >= 8) throw new TansrProtocolException("capacity_exceeded");
            observations.Add(key);
        }
        try
        {
            await transport.ObserveOutputAsync(binding.Inner.Session, operationReference, afterSequence, async (value, token) =>
            {
                Check(binding); token.ThrowIfCancellationRequested();
                await observer(value, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); Check(binding);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { lock (gate) observations.Remove(key); }
    }

    private void EnsureOpen() { lock (gate) if (disposed) throw new ObjectDisposedException(nameof(TerminalConnection)); }
    public void Dispose()
    {
        TerminalOutputSink[] owned;
        lock (gate) { if (disposed) return; disposed = true; owned = sinks.ToArray(); sinks.Clear(); }
        // A callback that ignores cancellation retains its observation slot until it actually exits.
        // Disposal never permits another connection on this instance or claims that callback stopped.
        try { transport.Dispose(); }
        finally { foreach (var sink in owned) sink.Dispose(); }
    }
}

/// <summary>A controller-authenticated binding. The immutable projection carries no token.</summary>
public sealed class TerminalBinding
{
    internal TerminalBinding(TerminalConnection owner, TerminalCandidateBinding inner) { Owner = owner; Inner = inner; }
    internal TerminalConnection Owner { get; }
    internal TerminalCandidateBinding Inner { get; }
    public JsonElement Value => Inner.Raw;
}

/// <summary>Bounded raw output bridge. An output seal is separate from a process receipt.</summary>
public sealed class TerminalOutputSink : IExecutionOutputSink, IDisposable
{
    private readonly TerminalExecutionOutputSink inner;
    private readonly Action<TerminalOutputSink> release;
    private readonly object gate = new();
    private Task? closing;
    internal TerminalOutputSink(TerminalExecutionOutputSink inner, Action<TerminalOutputSink> release)
    { this.inner = inner; this.release = release; }
    public Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken cancellationToken) => inner.OpenAsync(operation, cancellationToken);

    /// <summary>Read-only reconciliation of retained original output. It never re-executes a
    /// process or invents durable coverage. A missing capture must not be represented as success.</summary>
    public async Task<JsonElement> ReconcileAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var capture = inner.FindCapture(operationId) ?? throw new TansrProtocolException("source_unavailable");
        return (await capture.ReconcileAsync(cancellationToken).ConfigureAwait(false)).Raw;
    }

    /// <summary>Explicitly discard a retained output capture, relinquishing replay from it.</summary>
    public void ReleaseCapture(string operationId) => inner.ReleaseCapture(operationId);
    /// <summary>Cancel capture and wait for the original pumps to end. A transport that ignores
    /// cancellation retains its bounded connection slot until it actually returns.</summary>
    public Task CloseAsync()
    {
        lock (gate) return closing ??= FinishAsync(inner.CloseAsync());
    }
    private async Task FinishAsync(Task stopped)
    { try { await stopped.ConfigureAwait(false); } finally { release(this); } }
    public void Dispose()
    {
        _ = CloseAsync().ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
