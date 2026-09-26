using System.Security.Cryptography;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

/// <summary>
/// Bounded candidate output source for one already authorized operation. Capture never waits for the
/// network. The host must keep draining pipes when TryAppend returns false. Retained bytes are memory
/// only; a Serve accepted watermark never becomes a local durability claim or permission to rerun.
/// </summary>
internal sealed class TerminalOutputProducer : IDisposable
{
    private sealed class Block
    {
        internal Block(JsonElement wire, byte[] raw, long end) { Wire = wire; Raw = raw; End = end; }
        internal JsonElement Wire { get; }
        internal byte[] Raw { get; }
        internal long End { get; }
    }
    private readonly TerminalCandidateClient client;
    private readonly TerminalCandidateBinding binding;
    private readonly JsonElement operation;
    private readonly object gate = new();
    private readonly SemaphoreSlim transmission = new(1, 1);
    private readonly List<Block> blocks = new();
    private readonly Dictionary<string, string> encodings = new(StringComparer.Ordinal);
    private readonly SHA256 hash = SHA256.Create();
    private long capturedBytes;
    private long discardedBytes;
    private long? acceptedThrough;
    private bool stoppedCapture, truncated, completedCapture, requiresReconciliation, disposed;
    private JsonElement? seal;
    private TerminalOutputStatus? status;

    internal TerminalOutputProducer(TerminalCandidateClient client, TerminalCandidateBinding binding, JsonElement operation)
    {
        this.client = client; this.binding = binding; this.operation = operation.Clone();
        client.AssertBinding(binding);
        TerminalCandidateContract.Validate("OperationReference", operation);
        // Detect a negotiated control limit that cannot even carry the identity envelope.
        WireJson.EncodeControl(Batch(Array.Empty<JsonElement>(), null), binding.Limits.MaxControlBytes);
    }

    internal long CapturedBytes { get { lock (gate) return capturedBytes; } }
    internal long DiscardedBytes { get { lock (gate) return discardedBytes; } }
    internal bool IsTruncated { get { lock (gate) return truncated; } }
    internal bool RequiresReconciliation { get { lock (gate) return requiresReconciliation; } }
    internal TerminalOutputStatus? LastStatus { get { lock (gate) return status; } }
    internal void RequireReconciliation() { lock (gate) requiresReconciliation = true; }

    /// <summary>All-or-none capture of this callback. A dropped chunk permanently ends capture;
    /// later callbacks are drained/discarded and never disguise a hole as consecutive output.</summary>
    internal bool TryAppend(string channel, string encoding, byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        TerminalJson.Check(channel == "stdout" || channel == "stderr", "invalid_request");
        TerminalJson.Check(encoding == "utf-8" || encoding == "binary", "invalid_request");
        client.AssertBinding(binding);
        lock (gate)
        {
            EnsureOpen();
            if (completedCapture) throw new InvalidOperationException("Candidate output capture is already sealed.");
            if (encodings.TryGetValue(channel, out var previous) && previous != encoding) throw new WireProtocolException("revision_conflict");
            if (bytes.Length == 0) return true;
            var chunkCount = ((long)bytes.Length + binding.Limits.MaxBlockBytes - 1) / binding.Limits.MaxBlockBytes;
            var pendingBytes = capturedBytes - AcceptedBytes();
            if (stoppedCapture || bytes.Length > binding.Limits.MaxPendingBytes - pendingBytes ||
                bytes.Length > binding.Limits.MaxRetainedBytes - capturedBytes || blocks.Count + chunkCount > 4096 ||
                capturedBytes > long.MaxValue - bytes.Length)
            {
                stoppedCapture = truncated = true;
                discardedBytes = discardedBytes > long.MaxValue - bytes.Length ? long.MaxValue : discardedBytes + bytes.Length;
                return false;
            }
            encodings[channel] = encoding;
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(binding.Limits.MaxBlockBytes, bytes.Length - offset);
                var raw = new byte[count]; Buffer.BlockCopy(bytes, offset, raw, 0, count);
                var sequence = blocks.Count;
                var start = capturedBytes;
                var wire = TerminalJson.Object(w =>
                {
                    w.WriteString("seq", TerminalJson.Decimal(sequence)); w.WriteString("byteOffset", TerminalJson.Decimal(start));
                    w.WriteString("channel", channel); w.WriteString("encoding", encoding); w.WriteNumber("byteLength", count);
                    w.WriteString("payloadDigest", WireJson.Sha256(raw)); w.WriteString("base64", Convert.ToBase64String(raw));
                });
                hash.TransformBlock(raw, 0, raw.Length, raw, 0);
                capturedBytes += count; blocks.Add(new Block(wire, raw, capturedBytes)); offset += count;
            }
            return true;
        }
    }

    internal async Task<TerminalOutputStatus?> FlushAsync(CancellationToken cancellationToken = default)
    {
        await transmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await FlushCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { transmission.Release(); }
    }

    internal async Task<TerminalOutputStatus> CompleteAsync(bool captureTruncated, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureOpen();
            if (seal.HasValue)
            { TerminalJson.Check(!captureTruncated || truncated, "revision_conflict"); }
            else
            {
                completedCapture = true; truncated |= captureTruncated;
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var digest = string.Concat(hash.Hash!.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
                seal = TerminalJson.Object(w =>
                {
                    TerminalJson.Decimal(w, "lastSeq", blocks.Count == 0 ? null : (long?)blocks.Count - 1);
                    w.WriteString("totalBytes", TerminalJson.Decimal(capturedBytes)); w.WriteString("payloadDigest", digest); w.WriteBoolean("truncated", truncated);
                });
            }
        }
        await transmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            lock (gate) if (status?.Seal != null) return status;
            JsonElement request;
            lock (gate) { EnsureCanTransmit(); request = Batch(Array.Empty<JsonElement>(), seal); }
            var result = await SendAsync(request, blocks.Count == 0 ? null : (long?)blocks.Count - 1, cancellationToken).ConfigureAwait(false);
            if (!result.Seal.HasValue)
            { lock (gate) requiresReconciliation = true; throw new WireProtocolException("invalid_response"); }
            return result;
        }
        finally { transmission.Release(); }
    }

    /// <summary>Only reads the original operation. It never executes a command or silently repeats a POST.</summary>
    internal async Task<TerminalOutputStatus> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await transmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate) EnsureOpen();
            var result = await client.GetOutputStatusAsync(binding, operation, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                EnsureOpen();
                if (result.State == "unavailable") { status = result; requiresReconciliation = true; return result; }
                ValidateAcknowledgement(result);
                // A known accepted prefix cannot disappear and then be treated as a brand-new operation.
                if (acceptedThrough.HasValue && (!result.AcceptedThrough.HasValue || result.AcceptedThrough < acceptedThrough))
                { status = result; requiresReconciliation = true; throw new WireProtocolException("output_gap"); }
                acceptedThrough = result.AcceptedThrough; status = result; requiresReconciliation = false;
            }
            return result;
        }
        catch { lock (gate) requiresReconciliation = true; throw; }
        finally { transmission.Release(); }
    }

    private async Task<TerminalOutputStatus?> FlushCoreAsync(CancellationToken cancellationToken)
    {
        for (; ; )
        {
            JsonElement request;
            long through;
            lock (gate)
            {
                EnsureCanTransmit();
                var first = acceptedThrough.HasValue ? checked((int)acceptedThrough.Value + 1) : 0;
                if (first == blocks.Count) return status;
                var selected = new List<JsonElement>(); var rawLength = 0;
                for (var index = first; index < blocks.Count && selected.Count < 32; index++)
                {
                    if (rawLength + blocks[index].Raw.Length > binding.Limits.MaxBatchBytes) break;
                    selected.Add(blocks[index].Wire);
                    var candidate = Batch(selected, null);
                    try { WireJson.EncodeControl(candidate, binding.Limits.MaxControlBytes); }
                    catch (WireProtocolException error) when (error.Code == "payload_too_large") { selected.RemoveAt(selected.Count - 1); break; }
                    rawLength += blocks[index].Raw.Length;
                }
                TerminalJson.Check(selected.Count > 0, "payload_too_large");
                through = first + selected.Count - 1; request = Batch(selected, null);
            }
            await SendAsync(request, through, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TerminalOutputStatus> SendAsync(JsonElement request, long? minimumAccepted, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.SendBatchAsync(binding, request, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                EnsureOpen(); status = result;
                TerminalJson.Check(result.State != "unavailable", "source_unavailable");
                ValidateAcknowledgement(result);
                TerminalJson.Check(!minimumAccepted.HasValue || result.AcceptedThrough.HasValue && result.AcceptedThrough >= minimumAccepted, "invalid_response");
                TerminalJson.Check(!acceptedThrough.HasValue || result.AcceptedThrough >= acceptedThrough, "output_gap");
                acceptedThrough = result.AcceptedThrough;
            }
            return result;
        }
        catch { lock (gate) requiresReconciliation = true; throw; }
    }

    private void ValidateAcknowledgement(TerminalOutputStatus value)
    {
        var accepted = value.AcceptedThrough;
        TerminalJson.Check(!accepted.HasValue || accepted >= 0 && accepted < blocks.Count, "integrity_mismatch");
        var expectedBytes = accepted.HasValue ? blocks[(int)accepted.Value].End : 0;
        TerminalJson.Check(value.NextByteOffset == expectedBytes, "integrity_mismatch");
        if (value.Seal.HasValue) TerminalJson.Check(seal.HasValue && TerminalJson.Equal(seal.Value, value.Seal.Value), "integrity_mismatch");
    }
    private long AcceptedBytes() => acceptedThrough.HasValue ? blocks[(int)acceptedThrough.Value].End : 0;
    private JsonElement Batch(IEnumerable<JsonElement> selected, JsonElement? finalSeal)
    {
        var target = binding.ExecutionBinding.GetProperty("target");
        return TerminalJson.Object(w =>
        {
            w.WriteString("contract", TerminalCandidateContract.Protocol); TerminalJson.Field(w, "session", binding.Session);
            TerminalJson.Field(w, "operation", operation); w.WriteString("executorId", TerminalJson.Text(target, "executorId"));
            w.WriteString("connectionId", TerminalJson.Text(target, "connectionId")); w.WriteStartArray("blocks");
            foreach (var block in selected) block.WriteTo(w);
            w.WriteEndArray(); if (finalSeal.HasValue) TerminalJson.Field(w, "seal", finalSeal.Value); else w.WriteNull("seal");
        });
    }
    private void EnsureOpen() { if (disposed) throw new ObjectDisposedException(nameof(TerminalOutputProducer)); }
    private void EnsureCanTransmit()
    {
        EnsureOpen();
        if (requiresReconciliation) throw new WireProtocolException(status?.State == "unavailable" ? "source_unavailable" : "commit_unknown");
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; hash.Dispose(); blocks.Clear();
            // Keep the semaphore alive until any already in-flight response has observed disposal.
        }
    }
}
