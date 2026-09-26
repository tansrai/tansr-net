using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

/// <summary>One operation's candidate observation state. An output seal never means process success.
/// Keep this instance across SSE reconnects to preserve each channel's incomplete UTF-8 scalar.</summary>
internal sealed class TerminalOutputObserver : IDisposable
{
    private sealed class Channel
    {
        internal Channel(string encoding) { Encoding = encoding; }
        internal string Encoding { get; }
        internal Decoder Decoder { get; } = new UTF8Encoding(false, false).GetDecoder();
    }
    private sealed class Remembered
    {
        internal Remembered(long sequence, string fingerprint, int bytes) { Sequence = sequence; Fingerprint = fingerprint; Bytes = bytes; }
        internal long Sequence { get; }
        internal string Fingerprint { get; }
        internal int Bytes { get; }
    }
    private readonly object gate = new();
    private readonly JsonElement operation;
    private readonly Dictionary<string, Channel> channels = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Remembered> fingerprints = new();
    private readonly Queue<Remembered> retained = new();
    private readonly SHA256 hash = SHA256.Create();
    private long? lastSequence;
    private long nextOffset;
    private int retainedBytes;
    private bool hasGap, fullPrefix = true, sealVerified, flushed, disposed;
    private string? completedDigest;
    private JsonElement? announcedSeal;
    private TerminalOutputStatus? status;

    internal TerminalOutputObserver(JsonElement operation)
    { TerminalCandidateContract.Validate("OperationReference", operation); this.operation = operation.Clone(); }

    internal long? LastSequence { get { lock (gate) return lastSequence; } }
    internal bool HasPresentationGap { get { lock (gate) return hasGap; } }
    internal bool SealVerified { get { lock (gate) return sealVerified; } }
    internal TerminalOutputStatus? LastStatus { get { lock (gate) return status; } }

    internal IReadOnlyList<TerminalOutputPiece> ApplyEvent(JsonElement value)
    {
        TerminalCandidateContract.Validate("OutputEvent", value);
        if (TerminalJson.Text(value, "type") == "output.status") return ApplyStatus(value.GetProperty("status"));
        TerminalJson.Check(TerminalJson.Equal(operation, value.GetProperty("operation")), "binding_conflict");
        return ApplyBlock(value.GetProperty("block"));
    }

    internal IReadOnlyList<TerminalOutputPiece> ApplyBlock(JsonElement block)
    {
        TerminalCandidateContract.Validate("OutputBlock", block);
        var sequence = TerminalJson.Sequence(block, "seq");
        var offset = TerminalJson.Sequence(block, "byteOffset");
        var channelName = TerminalJson.Text(block, "channel");
        var encoding = TerminalJson.Text(block, "encoding");
        var bytes = WireJson.DecodeBase64(TerminalJson.Text(block, "base64"));
        var fingerprint = WireJson.Sha256(WireJson.EncodeControl(block));
        lock (gate)
        {
            EnsureOpen();
            if (lastSequence.HasValue && sequence <= lastSequence)
            {
                if (!fingerprints.TryGetValue(sequence, out var old)) { MarkGap(); throw new WireProtocolException("output_gap"); }
                TerminalJson.Check(old.Fingerprint == fingerprint, "revision_conflict");
                return Array.Empty<TerminalOutputPiece>();
            }
            if (announcedSeal.HasValue)
            {
                var last = TerminalJson.OptionalSequence(announcedSeal.Value, "lastSeq");
                TerminalJson.Check(last.HasValue && sequence <= last && !flushed, "revision_conflict");
            }
            if (channels.TryGetValue(channelName, out var channel)) TerminalJson.Check(channel.Encoding == encoding, "revision_conflict");
            else { channel = new Channel(encoding); channels.Add(channelName, channel); }
            var adjacent = lastSequence.HasValue ? lastSequence != long.MaxValue && sequence == lastSequence.Value + 1 : sequence == 0;
            if (!adjacent)
            {
                TerminalJson.Check(offset >= nextOffset, "integrity_mismatch");
                MarkGap();
            }
            else TerminalJson.Check(offset == nextOffset, "integrity_mismatch");
            hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            lastSequence = sequence; nextOffset = checked(offset + bytes.Length);
            var memory = new Remembered(sequence, fingerprint, bytes.Length);
            fingerprints.Add(sequence, memory); retained.Enqueue(memory); retainedBytes += bytes.Length;
            while (retained.Count > 4096 || retainedBytes > 8388608)
            { var removed = retained.Dequeue(); fingerprints.Remove(removed.Sequence); retainedBytes -= removed.Bytes; }
            var text = encoding == "utf-8" ? Decode(channel, bytes, false) : null;
            var result = new List<TerminalOutputPiece> { new TerminalOutputPiece(sequence, channelName, encoding, bytes, text, hasGap) };
            FinishSeal(result);
            return result;
        }
    }

    internal IReadOnlyList<TerminalOutputPiece> ApplyStatus(JsonElement value)
    {
        var incoming = new TerminalOutputStatus(value);
        TerminalJson.Check(TerminalJson.Equal(operation, value.GetProperty("operation")), "binding_conflict");
        lock (gate)
        {
            EnsureOpen();
            if (incoming.State == "unavailable" || incoming.AcceptedThrough.HasValue && lastSequence.HasValue && incoming.AcceptedThrough < lastSequence) MarkGap();
            if (incoming.RetainedFrom.HasValue && (!lastSequence.HasValue ? incoming.RetainedFrom > 0 :
                lastSequence != long.MaxValue && incoming.RetainedFrom > lastSequence.Value + 1)) MarkGap();
            if (incoming.Seal.HasValue)
            {
                if (announcedSeal.HasValue) TerminalJson.Check(TerminalJson.Equal(announcedSeal.Value, incoming.Seal.Value), "revision_conflict");
                var last = TerminalJson.OptionalSequence(incoming.Seal.Value, "lastSeq");
                TerminalJson.Check(!lastSequence.HasValue || last.HasValue && last >= lastSequence, "integrity_mismatch");
                announcedSeal = incoming.Seal;
                if (last != lastSequence) hasGap = true; // Terminal status alone cannot manufacture missing output.
            }
            status = incoming;
            var result = new List<TerminalOutputPiece>(); FinishSeal(result); return result;
        }
    }

    /// <summary>For a transport gap or lost decoding state. Raw continuation bytes remain visible,
    /// incomplete scalars become replacements, and the surviving window is never called complete text.</summary>
    internal void NoticeGap()
    { lock (gate) { EnsureOpen(); MarkGap(); } }

    private void MarkGap()
    {
        hasGap = true; fullPrefix = false; sealVerified = false;
        foreach (var channel in channels.Values) channel.Decoder.Reset();
    }
    private void FinishSeal(List<TerminalOutputPiece> result)
    {
        if (!announcedSeal.HasValue || flushed || TerminalJson.OptionalSequence(announcedSeal.Value, "lastSeq") != lastSequence) return;
        var seal = announcedSeal.Value;
        TerminalJson.Check(TerminalJson.Sequence(seal, "totalBytes") == nextOffset, "integrity_mismatch");
        if (fullPrefix)
        {
            if (completedDigest == null)
            {
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                completedDigest = string.Concat(hash.Hash!.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
            }
            TerminalJson.Check(completedDigest == TerminalJson.Text(seal, "payloadDigest"), "integrity_mismatch");
            sealVerified = true;
        }
        foreach (var pair in channels)
        {
            if (pair.Value.Encoding != "utf-8") continue;
            var text = Decode(pair.Value, Array.Empty<byte>(), true);
            if (text.Length != 0) result.Add(new TerminalOutputPiece(lastSequence, pair.Key, "utf-8", Array.Empty<byte>(), text, hasGap));
        }
        flushed = true;
    }
    private static string Decode(Channel channel, byte[] bytes, bool flush)
    {
        var chars = new char[bytes.Length + 4];
        var count = channel.Decoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush);
        return new string(chars, 0, count);
    }
    private void EnsureOpen() { if (disposed) throw new ObjectDisposedException(nameof(TerminalOutputObserver)); }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; hash.Dispose(); fingerprints.Clear(); retained.Clear(); channels.Clear(); } }
}

/// <summary>Notification cursors are never output durability cursors or execution permissions.</summary>
internal sealed class TerminalExecutorEventCursor
{
    private readonly string executorId, connectionId;
    private long? last;
    private string? fingerprint;
    internal TerminalExecutorEventCursor(string executorId, string connectionId)
    { this.executorId = executorId; this.connectionId = connectionId; }
    internal bool RequiresReconciliation { get; private set; }
    internal long? LastEventId => last;
    internal bool Apply(JsonElement value)
    {
        TerminalCandidateContract.Validate("ExecutorEvent", value);
        TerminalJson.Check(TerminalJson.Text(value, "executorId") == executorId && TerminalJson.Text(value, "connectionId") == connectionId, "binding_conflict");
        var next = TerminalJson.Sequence(value, "eventId");
        var digest = WireJson.Sha256(WireJson.EncodeControl(value));
        if (last.HasValue && next <= last)
        {
            if (next == last) TerminalJson.Check(digest == fingerprint, "revision_conflict");
            else RequiresReconciliation = true;
            return false;
        }
        if ((!last.HasValue ? next != 0 : last == long.MaxValue || next != last.Value + 1) || TerminalJson.Text(value, "type") == "reconcile-required")
            RequiresReconciliation = true;
        last = next; fingerprint = digest; return true;
    }
    internal void NoticeGap() => RequiresReconciliation = true;
}
