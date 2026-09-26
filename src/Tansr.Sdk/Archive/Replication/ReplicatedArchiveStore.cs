using System.Runtime.CompilerServices;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using A = Tansr.Sdk.Archive.Replication.ArchiveReceiverValidation;
using S = Tansr.Sdk.Archive.Replication.ArchiveSyncValidation;

namespace Tansr.Sdk.Archive.Replication;

/// <summary>两份同源耐久介质都核实后才返回原 ACK。部分提交只用原输入恢复，不降级为单份成功。</summary>
public sealed partial class ReplicatedArchiveStore : IRecoverableArchiveStore
{
    private static readonly ConditionalWeakTable<object, object> ActiveStores = new ConditionalWeakTable<object, object>();
    private static readonly object ActiveGate = new object();
    private readonly IReplicaArchiveStore[] _stores;
    private readonly JsonElement _identity, _limits;
    private readonly string _replicationId;
    private readonly Func<JsonElement> _readContext;
    private readonly bool[] _closed = new bool[2];
    private int _busy, _poisoned, _closing;
    public ReplicatedArchiveStore(ReplicatedArchiveStoreOptions options)
    {
        if (options == null || options.Primary == null || options.Replica == null || options.ReadContext == null) throw new ArgumentNullException(nameof(options));
        _identity = A.Identity(options.Identity); _limits = S.Limits(options.Limits); _replicationId = options.ReplicationId; WireJson.ValidateNamed("Id", S.String(_replicationId)); _readContext = options.ReadContext;
        _stores = new[] { options.Primary, options.Replica }; A.Need(!ReferenceEquals(_stores[0], _stores[1]), "invalid_input");
        lock (ActiveGate) { A.Need(!ActiveStores.TryGetValue(_stores[0], out _) && !ActiveStores.TryGetValue(_stores[1], out _), "invalid_input"); foreach (var store in _stores) ActiveStores.Add(store, new object()); }
    }
    public Task<JsonElement> ReceiveAsync(ArchiveReceiveInput input, CancellationToken cancellationToken = default)
    { Preflight(); var fixedInput = S.Input(input, _limits); return Run(check => Accept(fixedInput, check, cancellationToken), cancellationToken); }
    /// <summary>原输入须在首次接收前由宿主耐久保存。两侧均已持有原操作时不会调用加载器。</summary>
    public Task<JsonElement?> RecoverPendingAsync(Func<JsonElement, CancellationToken, Task<ArchiveReceiveInput>> loadOriginal, CancellationToken cancellationToken = default)
    {
        if (loadOriginal == null) throw new ArgumentNullException(nameof(loadOriginal));
        return Run<JsonElement?>(async check =>
        {
            var a = await Facts(_stores[0], check, cancellationToken).ConfigureAwait(false); var b = await Facts(_stores[1], check, cancellationToken).ConfigureAwait(false); var pending = a.Pending ?? b.Pending;
            if (!pending.HasValue) { A.Need(Same(a.Head, b.Head), "reconciliation_required"); return null; }
            A.Need(!a.Pending.HasValue || !b.Pending.HasValue || S.Equal(a.Pending.Value, b.Pending.Value), "reconciliation_required");
            var originalA = await _stores[0].ReplicaOperationAsync(pending.Value.GetProperty("request"), cancellationToken).ConfigureAwait(false); check(); var originalB = await _stores[1].ReplicaOperationAsync(pending.Value.GetProperty("request"), cancellationToken).ConfigureAwait(false); check();
            if (originalA.HasValue && originalB.HasValue) return (await Aligned(check, true, cancellationToken).ConfigureAwait(false)).Pending;
            await Operation(originalA.HasValue ? _stores[0] : _stores[1], pending.Value, check, cancellationToken).ConfigureAwait(false);
            var input = await loadOriginal(pending.Value, cancellationToken).ConfigureAwait(false); check(); var ack = await Accept(S.Input(input, _limits), check, cancellationToken).ConfigureAwait(false); A.Need(S.Equal(ack, pending.Value), "integrity_mismatch"); return ack;
        }, cancellationToken);
    }
    public Task<JsonElement?> PendingAsync(CancellationToken cancellationToken = default) => Run(async check => (await Aligned(check, true, cancellationToken).ConfigureAwait(false)).Pending, cancellationToken);
    public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) => Run(async check => (await Aligned(check, false, cancellationToken).ConfigureAwait(false)).Head, cancellationToken);
    public Task<JsonElement> CoverageAsync(CancellationToken cancellationToken = default) => Run(async check =>
    { await Aligned(check, false, cancellationToken).ConfigureAwait(false); var a = await _stores[0].CoverageAsync(cancellationToken).ConfigureAwait(false); check(); var b = await _stores[1].CoverageAsync(cancellationToken).ConfigureAwait(false); check(); A.Need(S.Equal(a, b), "reconciliation_required"); return S.Copy(a); }, cancellationToken);
    public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default)
    {
        Preflight(); if (request == null) throw new ArgumentNullException(nameof(request)); var fixedRequest = new ArchiveReadRequest { Identity = A.Identity(request.Identity), Selection = S.Copy(request.Selection, 65536), MaxRecords = request.MaxRecords, MaxBytes = request.MaxBytes };
        return Run(async check =>
        {
            A.Need(S.Equal(fixedRequest.Identity, _identity), "identity_mismatch"); await Aligned(check, false, cancellationToken).ConfigureAwait(false);
            var a = await _stores[0].ReadRecordsAsync(fixedRequest, cancellationToken).ConfigureAwait(false); check(); var b = await _stores[1].ReadRecordsAsync(fixedRequest, cancellationToken).ConfigureAwait(false); check();
            A.Need(EqualPage(a, b), "integrity_mismatch"); return ClonePage(a);
        }, cancellationToken);
    }
    public Task<byte[]> BodyAsync(JsonElement artifactReference, CancellationToken cancellationToken = default)
    {
        Preflight(); var reference = A.Copy(artifactReference, "ArtifactRef"); return Run(async check =>
        {
            A.Need(reference.GetProperty("bytes").GetInt64() <= _limits.GetProperty("maxBatchBytes").GetInt64(), "capacity_exceeded"); await Aligned(check, false, cancellationToken).ConfigureAwait(false);
            var a = await _stores[0].BodyAsync(reference, cancellationToken).ConfigureAwait(false); check(); var body = ValidateBody(reference, a);
            var b = await _stores[1].BodyAsync(reference, cancellationToken).ConfigureAwait(false); check(); ValidateBody(reference, b); return body;
        }, cancellationToken);
    }
    public Task ConfirmAsync(JsonElement receipt, CancellationToken cancellationToken = default)
    {
        Preflight(); var fixedReceipt = A.Copy(receipt, "MutationReceipt"); return Run<object?>(async check =>
        {
            var saved = await _stores[0].ReplicaOperationAsync(fixedReceipt.GetProperty("request"), cancellationToken).ConfigureAwait(false); check(); A.Need(saved.HasValue, "receipt_mismatch"); var ack = A.Copy(saved!.Value.GetProperty("ack"), "ArchiveAckRequest"); A.Receipt(_identity, ack, fixedReceipt);
            foreach (var store in _stores) { var original = await Operation(store, ack, check, cancellationToken).ConfigureAwait(false); A.Need(original.GetProperty("receipt").ValueKind == JsonValueKind.Null || S.Equal(original.GetProperty("receipt"), fixedReceipt), "receipt_mismatch"); await VerifyStored(store, ack, check, cancellationToken).ConfigureAwait(false); }
            foreach (var store in _stores) { await store.ConfirmAsync(fixedReceipt, cancellationToken).ConfigureAwait(false); check(); }
            return null;
        }, cancellationToken);
    }
    public async Task CloseAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Exchange(ref _poisoned, 1); throw new StorageException("reentrant"); }
        Interlocked.Exchange(ref _closing, 1); var errors = new List<Exception>();
        try { for (int i = 0; i < _stores.Length; i++) if (!_closed[i]) { try { await _stores[i].CloseAsync().ConfigureAwait(false); _closed[i] = true; } catch (Exception e) { errors.Add(e); } } if (_closed.All(v => v)) lock (ActiveGate) foreach (var store in _stores) ActiveStores.Remove(store); if (errors.Count > 0) throw new AggregateException("Archive connections require close retry.", errors); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
    private async Task<JsonElement> Accept(ArchiveReceiveInput input, Action check, CancellationToken ct)
    {
        var coverage = input.Status.GetProperty("acknowledgedCoverage"); JsonElement? previous = coverage.ValueKind == JsonValueKind.Null ? null : A.Object(w => { w.WriteString("sequence", A.String(coverage, "throughSequence")); w.WriteString("recordDigest", A.String(coverage, "headDigest")); });
        var batch = A.Prepare(_identity, _limits, previous, input); var states = new bool[2];
        for (int i = 0; i < _stores.Length; i++)
        {
            var store = _stores[i]; var state = await Facts(store, check, ct).ConfigureAwait(false); A.Need(!state.Pending.HasValue || S.Equal(state.Pending.Value, batch.Ack), "pending_ack");
            var saved = await store.ReplicaOperationAsync(input.Request, ct).ConfigureAwait(false); check(); states[i] = saved.HasValue;
            if (saved.HasValue) { await Operation(store, batch.Ack, check, ct).ConfigureAwait(false); await VerifyStored(store, batch.Ack, check, ct, batch.Records).ConfigureAwait(false); A.Need(state.Head.HasValue && S.Equal(state.Head.Value, batch.Head), "reconciliation_required"); }
            else A.Need(!state.Pending.HasValue && Same(state.Head, previous), "reconciliation_required");
        }
        for (int i = 0; i < _stores.Length; i++) if (!states[i]) { var ack = await _stores[i].ReceiveAsync(input, ct).ConfigureAwait(false); check(); A.Need(S.Equal(ack, batch.Ack), "integrity_mismatch"); }
        foreach (var store in _stores) { await Operation(store, batch.Ack, check, ct).ConfigureAwait(false); await VerifyStored(store, batch.Ack, check, ct, batch.Records).ConfigureAwait(false); }
        return batch.Ack;
    }
    private async Task<(JsonElement? Head, JsonElement? Pending)> Aligned(Action check, bool bodies, CancellationToken ct)
    {
        var a = await Facts(_stores[0], check, ct).ConfigureAwait(false); var b = await Facts(_stores[1], check, ct).ConfigureAwait(false); A.Need(Same(a.Head, b.Head), "reconciliation_required"); var pending = a.Pending ?? b.Pending;
        if (pending.HasValue) for (int i = 0; i < _stores.Length; i++) { var current = i == 0 ? a : b; A.Need(!current.Pending.HasValue || S.Equal(current.Pending.Value, pending.Value), "reconciliation_required"); var saved = await Operation(_stores[i], pending.Value, check, ct).ConfigureAwait(false); A.Need(current.Pending.HasValue ? saved.GetProperty("receipt").ValueKind == JsonValueKind.Null : saved.GetProperty("receipt").ValueKind != JsonValueKind.Null, "reconciliation_required"); if (bodies) await VerifyStored(_stores[i], pending.Value, check, ct).ConfigureAwait(false); }
        return (a.Head, pending);
    }
    private static async Task<(JsonElement? Head, JsonElement? Pending)> Facts(IReplicaArchiveStore store, Action check, CancellationToken ct)
    { var head = await store.HeadAsync(ct).ConfigureAwait(false); check(); var fixedHead = head.HasValue ? S.Head(head.Value) : null; var pending = await store.PendingAsync(ct).ConfigureAwait(false); check(); return (fixedHead, pending.HasValue ? A.Copy(pending.Value, "ArchiveAckRequest") : null); }
    private async Task<JsonElement> Operation(IReplicaArchiveStore store, JsonElement ack, Action check, CancellationToken ct)
    {
        A.Need(A.String(ack, "bindingId") == A.String(_identity, "bindingId") && A.String(ack, "sourceId") == A.String(_identity, "sourceId") && A.String(ack, "sourceGeneration") == A.String(_identity, "sourceGeneration") && S.Equal(ack.GetProperty("generations"), _identity.GetProperty("target").GetProperty("generations")), "identity_mismatch");
        var raw = await store.ReplicaOperationAsync(ack.GetProperty("request"), ct).ConfigureAwait(false); check(); A.Need(raw.HasValue, "reconciliation_required"); var saved = S.Copy(raw!.Value, 524288); S.Fields(saved, "ack", "receipt"); A.Need(S.Equal(saved.GetProperty("ack"), ack), "reconciliation_required"); if (saved.GetProperty("receipt").ValueKind != JsonValueKind.Null) A.Receipt(_identity, ack, saved.GetProperty("receipt")); return saved;
    }
    private async Task VerifyStored(IReplicaArchiveStore store, JsonElement ack, Action check, CancellationToken ct, IReadOnlyList<JsonElement>? expected = null)
    {
        var coverage = ack.GetProperty("coverage"); long from = A.SequenceOf(coverage, "fromSequence"), through = A.SequenceOf(coverage, "throughSequence"); A.Need(from >= 1 && through >= from && through - from < 128, "reconciliation_required");
        var page = await store.ReadRecordsAsync(new ArchiveReadRequest { Identity = _identity, Selection = A.Object(w => { w.WriteString("fromSequence", A.String(coverage, "fromSequence")); w.WriteString("throughSequence", A.String(coverage, "throughSequence")); }), MaxRecords = 128, MaxBytes = 1048576 }, ct).ConfigureAwait(false); check();
        A.Need(page.NextFromSequence == null && page.MissingRecordIds.Count == 0 && page.Records.Count == through - from + 1 && page.SourceCoverage.GetProperty("complete").GetBoolean() && A.String(page.SourceCoverage, "sourceId") == A.String(_identity, "sourceId") && A.String(page.SourceCoverage, "sourceGeneration") == A.String(_identity, "sourceGeneration"), "reconciliation_required");
        if (expected != null) A.Need(page.Records.Count == expected.Count && page.Records.Select((r, i) => S.Equal(r, expected[i])).All(x => x));
        var refs = new Dictionary<string, JsonElement>(StringComparer.Ordinal); var payloads = new Dictionary<string, JsonElement>(StringComparer.Ordinal); var attachments = new Dictionary<string, JsonElement>(StringComparer.Ordinal); long bytes = 0;
        for (int i = 0; i < page.Records.Count; i++)
        {
            var record = page.Records[i]; A.VerifyRecord(record, _identity); A.Need(A.SequenceOf(record, "sequence") == from + i && (i == 0 || A.String(record, "predecessorDigest") == A.String(page.Records[i - 1], "recordDigest")));
            foreach (var reference in A.References(record)) { string id = A.String(reference, "artifactId"); if (refs.TryGetValue(id, out var prior)) A.Need(S.Equal(prior, reference)); else { refs.Add(id, reference); bytes += reference.GetProperty("bytes").GetInt64(); } }
            Add(record.GetProperty("payload"), payloads); foreach (var reference in record.GetProperty("attachments").EnumerateArray()) Add(reference, attachments);
        }
        A.Need(A.String(page.Records[page.Records.Count - 1], "recordDigest") == A.String(coverage, "headDigest") && ArrayEquals(payloads.Values, ack.GetProperty("payloads")) && ArrayEquals(attachments.Values, ack.GetProperty("attachments")));
        A.Need(bytes <= _limits.GetProperty("maxBatchBytes").GetInt64() && refs.Count <= _limits.GetProperty("maxArtifacts").GetInt32(), "capacity_exceeded");
        foreach (var reference in refs.Values) { var body = await store.BodyAsync(reference, ct).ConfigureAwait(false); check(); ValidateBody(reference, body); }
    }
    private byte[] ValidateBody(JsonElement reference, byte[] bytes)
    { A.Need(A.String(reference, "sourceId") == A.String(_identity, "sourceId") && bytes != null && bytes.LongLength == reference.GetProperty("bytes").GetInt64()); var body = (byte[])bytes!.Clone(); A.Need(WireJson.Sha256(body) == A.String(reference, "sha256")); return body; }
    private async Task<T> Run<T>(Func<Action, Task<T>> work, CancellationToken ct)
    {
        Preflight(); if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Exchange(ref _poisoned, 1); throw new StorageException("reentrant"); }
        Interlocked.Exchange(ref _poisoned, 0);
        try
        {
            ct.ThrowIfCancellationRequested(); var scope = Context(); void Check() { ct.ThrowIfCancellationRequested(); A.Need(Volatile.Read(ref _poisoned) == 0, "reentrant"); A.Need(S.Equal(scope, Context()), "context_changed"); A.Need(Volatile.Read(ref _poisoned) == 0, "reentrant"); }
            Check();
            for (int i = 0; i < _stores.Length; i++) { var info = S.ReplicaInfo(await _stores[i].ReplicaIdentityAsync(ct).ConfigureAwait(false)); Check(); A.Need(S.Equal(info.GetProperty("receiver"), _identity) && S.Equal(info.GetProperty("limits"), _limits) && A.String(info.GetProperty("replica"), "replicationId") == _replicationId && A.String(info.GetProperty("replica"), "role") == (i == 0 ? "primary" : "replica"), "identity_mismatch"); var coverage = await _stores[i].CoverageAsync(ct).ConfigureAwait(false); Check(); A.Need(A.String(coverage, "sourceId") == A.String(_identity, "sourceId") && A.String(coverage, "sourceGeneration") == A.String(_identity, "sourceGeneration"), "identity_mismatch"); }
            var result = await work(Check).ConfigureAwait(false); Check(); return result;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
    private JsonElement Context() { var scope = A.Copy(_readContext(), "Scope"); A.Need(S.Equal(A.Without(scope, "authorizationRevision"), _identity.GetProperty("scope")), "context_changed"); return scope; }
    private void Preflight() { if (Volatile.Read(ref _busy) != 0) { Interlocked.Exchange(ref _poisoned, 1); throw new StorageException("reentrant"); } A.Need(Volatile.Read(ref _closing) == 0, "closed"); }
    private static bool Same(JsonElement? a, JsonElement? b) => a.HasValue == b.HasValue && (!a.HasValue || S.Equal(a.Value, b!.Value));
    private static void Add(JsonElement reference, Dictionary<string, JsonElement> into) { string id = A.String(reference, "artifactId"); if (!into.ContainsKey(id)) into.Add(id, A.Object(w => { w.WriteString("artifactId", id); w.WriteString("sha256", A.String(reference, "sha256")); w.WriteString("state", "durably-stored"); })); }
    private static bool ArrayEquals(IEnumerable<JsonElement> values, JsonElement array) { var expected = array.EnumerateArray().ToArray(); var actual = values.ToArray(); return actual.Length == expected.Length && actual.Select((v, i) => S.Equal(v, expected[i])).All(v => v); }
    private static bool EqualPage(ArchiveRecordPage a, ArchiveRecordPage b) => a.Bytes == b.Bytes && a.NextFromSequence == b.NextFromSequence && a.MissingRecordIds.SequenceEqual(b.MissingRecordIds) && S.Equal(a.SourceCoverage, b.SourceCoverage) && a.Records.Count == b.Records.Count && a.Records.Select((r, i) => S.Equal(r, b.Records[i])).All(v => v);
    private static ArchiveRecordPage ClonePage(ArchiveRecordPage page) => new ArchiveRecordPage(Array.AsReadOnly(page.Records.Select(r => A.Copy(r, "ArchiveRecord")).ToArray()), page.Bytes, Array.AsReadOnly(page.MissingRecordIds.ToArray()), page.NextFromSequence, S.Copy(page.SourceCoverage));
}
