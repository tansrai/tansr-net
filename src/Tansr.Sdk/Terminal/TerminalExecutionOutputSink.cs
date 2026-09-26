using System.Text.Json;
using Tansr.Sdk.Execution;

namespace Tansr.Sdk.Terminal;

/// <summary>Candidate-only bridge from an already authorized native execution to raw output.
/// A transport failure is observable through Completion; it never schedules a new execution.</summary>
internal sealed class TerminalExecutionOutputSink : IExecutionOutputSink, IDisposable
{
    private readonly TerminalCandidateClient client;
    private readonly TerminalCandidateBinding binding;
    private readonly object gate = new();
    private readonly Dictionary<string, Capture> captures = new(StringComparer.Ordinal);
    private bool disposed;
    internal TerminalExecutionOutputSink(TerminalCandidateClient client, TerminalCandidateBinding binding)
    { this.client = client; this.binding = binding; client.AssertBinding(binding); }
    public Task<IExecutionOutputCapture> OpenAsync(JsonElement operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TerminalExecutionOutputSink));
            var id = TerminalJson.Text(operation, "operationId");
            TerminalJson.Check(!captures.ContainsKey(id), "commit_unknown");
            TerminalJson.Check(captures.Count < 8, "capacity_exceeded");
            var capture = new Capture(client.CreateOutputProducer(binding, operation), () => ReleaseCapture(id));
            captures.Add(id, capture); return Task.FromResult<IExecutionOutputCapture>(capture);
        }
    }
    internal Capture? FindCapture(string operationId)
    { lock (gate) return captures.TryGetValue(operationId, out var capture) ? capture : null; }
    // Explicit release discards this memory-only source; a caller must not claim later replayability.
    internal void ReleaseCapture(string operationId)
    { lock (gate) if (captures.TryGetValue(operationId, out var capture)) { captures.Remove(operationId); capture.Release(); } }
    public void Dispose()
    {
        Capture[] retained;
        lock (gate)
        { if (disposed) return; disposed = true; retained = captures.Values.ToArray(); captures.Clear(); }
        foreach (var capture in retained) capture.Release();
    }
    internal sealed class Capture : IExecutionOutputCapture
    {
        private readonly object gate = new();
        private readonly TerminalOutputProducer producer;
        private readonly CancellationTokenSource stop = new();
        private readonly Action releaseSuccessful;
        private bool sealRequested, captureTruncated, disposed, released;
        internal Capture(TerminalOutputProducer producer, Action releaseSuccessful)
        {
            this.producer = producer; this.releaseSuccessful = releaseSuccessful;
            // Process cancellation ends pipe capture, but the backend gives Seal a separate bounded
            // cleanup token. Linking this lifetime to the process token would cancel that final seal.
            Completion = PumpAsync();
        }
        public Task Completion { get; }
        internal TerminalOutputStatus? LastStatus => producer.LastStatus;
        internal bool RequiresReconciliation => producer.RequiresReconciliation;
        internal long DiscardedBytes => producer.DiscardedBytes;
        internal async Task<TerminalOutputStatus> ReconcileAsync(CancellationToken cancellationToken = default)
        {
            // Completion may fault, but the immutable original blocks survive for a read-only
            // reconciliation. This method does not restart either the pump or the native process.
            Dispose();
            try { await Completion.ConfigureAwait(false); } catch { }
            lock (gate) if (released) throw new ObjectDisposedException(nameof(Capture));
            return await producer.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        public bool Append(string channel, string encoding, byte[] raw)
        {
            lock (gate)
            {
                if (disposed || Completion.IsCompleted || sealRequested) return false;
                // Reaching the cap fixes truncation permanently, while the native pipe continues
                // draining. Producer never renumbers later data over a discarded hole.
                var accepted = producer.TryAppend(channel, encoding, raw);
                if (!accepted) captureTruncated = true;
                return accepted;
            }
        }
        public async Task SealAsync(bool truncated, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(Capture));
                if (sealRequested && truncated && !captureTruncated) throw new InvalidOperationException("Candidate seal cannot change after completion starts.");
                captureTruncated |= truncated; sealRequested = true;
            }
            using var cancel = cancellationToken.Register(stop.Cancel);
            await Completion.ConfigureAwait(false);
        }
        private async Task PumpAsync()
        {
            // Exactly one pump serializes POSTs. No automatic recovery/retry follows unknown
            // commitment; host observes the failure and reconciles the original operation.
            try
            {
                for (; ; )
                {
                    stop.Token.ThrowIfCancellationRequested();
                    bool finish, truncated;
                    lock (gate) { finish = sealRequested; truncated = captureTruncated; }
                    if (finish) { await producer.CompleteAsync(truncated, stop.Token).ConfigureAwait(false); return; }
                    await producer.FlushAsync(stop.Token).ConfigureAwait(false);
                    await Task.Delay(30, stop.Token).ConfigureAwait(false);
                }
            }
            catch { producer.RequireReconciliation(); throw; }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            stop.Cancel();
            // Successful sealed output has no undecided submission to preserve. Unknown/error
            // captures remain keyed for explicit reconciliation and consume the bounded capacity.
            _ = Completion.ContinueWith(task =>
            {
                _ = task.Exception;
                if (task.Status == TaskStatus.RanToCompletion && producer.LastStatus?.Seal != null && !producer.RequiresReconciliation)
                    releaseSuccessful();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        internal void Release()
        {
            lock (gate) { if (released) return; released = true; }
            Dispose();
            // Pipe owner Dispose only cancels transport. The sink owns retained original bytes,
            // and releases them only after the old pump can no longer append or hash anything.
            _ = Completion.ContinueWith(task => { _ = task.Exception; producer.Dispose(); stop.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
