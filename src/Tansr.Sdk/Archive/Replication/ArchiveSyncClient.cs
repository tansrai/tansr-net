using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Transport;
using A = Tansr.Sdk.Archive.Replication.ArchiveReceiverValidation;
using S = Tansr.Sdk.Archive.Replication.ArchiveSyncValidation;

namespace Tansr.Sdk.Archive.Replication;

/// <summary>开发者档案服务的原 archive-sync-v1 HTTP 消费者；不创建 Serve 会话、材料权限或资金账本。</summary>
public sealed class ArchiveSyncClient : IReplicaArchiveStore, IDisposable
{
    private static readonly HashSet<string> ReadMethods = new HashSet<string>(new[] { "head", "coverage", "identity", "records", "body-chunk", "sync-page", "retention", "retention-page" }, StringComparer.Ordinal);
    private readonly JsonElement _identity, _limits;
    private readonly Func<JsonElement> _readContext;
    private readonly Func<JsonElement>? _readGrant;
    private readonly Func<string> _readToken;
    private readonly SessionTransport _transport;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private int _closed, _readingAuthority;
    private long _poison;
    public ArchiveSyncClient(ArchiveSyncClientOptions options, HttpClient? httpClient = null)
    {
        if (options == null || options.ReadToken == null || options.ReadContext == null) throw new ArgumentNullException(nameof(options));
        _identity = A.Identity(options.Identity); _limits = S.Limits(options.Limits); _readContext = options.ReadContext; _readGrant = options.SharedReadAccess; _readToken = options.ReadToken;
        _transport = new SessionTransport(new TansrClientOptions
        {
            BaseUri = options.BaseUri,
            AllowInsecureLoopback = options.AllowInsecureLoopback,
            TokenProvider = _ => Task.FromResult(A.String(Authority(), "token")),
            PrincipalProvider = () =>
            {
                var context = Authority().GetProperty("context");
                return A.Text(A.Without(_readGrant == null ? context : context.GetProperty("actor"), "authorizationRevision"));
            },
        }, httpClient);
    }
    public async Task<JsonElement> ReceiveAsync(ArchiveReceiveInput input, CancellationToken cancellationToken = default)
    {
        var fixedInput = S.Input(input, _limits); int maximum = RequestMaximum;
        var wire = Build(w => { Checkpoint(w, fixedInput); w.WriteStartArray("artifacts"); foreach (var artifact in fixedInput.Artifacts) { w.WriteStartObject(); w.WriteString("artifactId", artifact.ArtifactId); w.WriteString("base64", Convert.ToBase64String(artifact.Body)); w.WriteEndObject(); } w.WriteEndArray(); }, maximum);
        var previous = fixedInput.Status.GetProperty("acknowledgedCoverage"); JsonElement? head = previous.ValueKind == JsonValueKind.Null ? null : A.Object(w => { w.WriteString("sequence", A.String(previous, "throughSequence")); w.WriteString("recordDigest", A.String(previous, "headDigest")); });
        var batch = A.Prepare(_identity, _limits, head, fixedInput);
        var ack = A.Copy(await Request("receive", wire, cancellationToken).ConfigureAwait(false), "ArchiveAckRequest"); A.Need(S.Equal(ack, batch.Ack)); return ack;
    }
    public async Task ConfirmAsync(JsonElement receipt, CancellationToken cancellationToken = default) => Null(await Request("confirm", A.Copy(receipt, "MutationReceipt"), cancellationToken).ConfigureAwait(false));
    public async Task ReconcileAsync(JsonElement ack, CancellationToken cancellationToken = default) => Null(await Request("reconcile", MatchAck(ack), cancellationToken).ConfigureAwait(false));
    public async Task<JsonElement?> PendingAsync(CancellationToken cancellationToken = default)
    { var value = await Request("pending", S.Null, cancellationToken).ConfigureAwait(false); return value.ValueKind == JsonValueKind.Null ? null : MatchAck(value); }
    public async Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) => S.Head(await Request("head", S.Null, cancellationToken).ConfigureAwait(false));
    public async Task<JsonElement> CoverageAsync(CancellationToken cancellationToken = default)
    { var value = await Request("coverage", S.Null, cancellationToken).ConfigureAwait(false); Coverage(value); return value; }
    public async Task<JsonElement> ReplicaIdentityAsync(CancellationToken cancellationToken = default)
    { var value = S.ReplicaInfo(await Request("identity", S.Null, cancellationToken).ConfigureAwait(false)); A.Need(S.Equal(value.GetProperty("receiver"), _identity) && S.Equal(value.GetProperty("limits"), _limits), "identity_mismatch"); return value; }
    public async Task<JsonElement?> ReplicaOperationAsync(JsonElement requestIdentity, CancellationToken cancellationToken = default)
    {
        var request = A.Copy(requestIdentity, "RequestIdentity"); var value = await Request("operation", request, cancellationToken).ConfigureAwait(false); if (value.ValueKind == JsonValueKind.Null) return null;
        S.Fields(value, "ack", "receipt"); var ack = MatchAck(value.GetProperty("ack")); A.Need(S.Equal(ack.GetProperty("request"), request));
        if (value.GetProperty("receipt").ValueKind != JsonValueKind.Null) A.Receipt(_identity, ack, value.GetProperty("receipt")); return value;
    }
    public async Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request)); var identity = A.Identity(request.Identity); A.Need(S.Equal(identity, _identity), "identity_mismatch");
        int maximum = request.MaxRecords, maxBytes = request.MaxBytes; var selection = A.Copy(request.Selection, maximum: 65536);
        A.Need(maximum >= 1 && maximum <= 128 && maxBytes >= 1 && maxBytes <= 1048576, "invalid_input");
        string[]? ids = null; long from = 0, through = 0;
        if (selection.TryGetProperty("recordIds", out var selected))
        { S.Fields(selection, "recordIds"); A.Need(selected.ValueKind == JsonValueKind.Array && selected.GetArrayLength() >= 1 && selected.GetArrayLength() <= maximum, "invalid_input"); ids = selected.EnumerateArray().Select(x => { WireJson.ValidateNamed("Id", x); return x.GetString()!; }).ToArray(); A.Need(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "invalid_input"); }
        else { S.Fields(selection, "fromSequence", "throughSequence"); from = Sequence.Parse(A.String(selection, "fromSequence"), false).ToInt64(); through = Sequence.Parse(A.String(selection, "throughSequence"), false).ToInt64(); A.Need(through >= from, "invalid_input"); }
        var input = A.Object(w => { A.Property(w, "identity", identity); A.Property(w, "selection", selection); w.WriteNumber("maxRecords", maximum); w.WriteNumber("maxBytes", maxBytes); });
        var value = await Request("records", input, cancellationToken).ConfigureAwait(false); S.Fields(value, "records", "bytes", "missingRecordIds", "nextFromSequence", "sourceCoverage"); Coverage(value.GetProperty("sourceCoverage"));
        var rows = value.GetProperty("records").EnumerateArray().Select(x => A.Copy(x, "ArchiveRecord")).ToArray(); var missing = value.GetProperty("missingRecordIds").EnumerateArray().Select(x => { WireJson.ValidateNamed("Id", x); return x.GetString()!; }).ToArray();
        A.Need(rows.Length <= maximum && rows.Select(r => A.String(r, "recordId")).Distinct(StringComparer.Ordinal).Count() == rows.Length && missing.Distinct(StringComparer.Ordinal).Count() == missing.Length);
        foreach (var row in rows) A.VerifyRecord(row, _identity);
        int bytes = rows.Sum(r => WireJson.EncodeControl(r).Length); A.Need(value.GetProperty("bytes").GetInt32() == bytes && bytes <= maxBytes);
        string? next = value.GetProperty("nextFromSequence").GetString(); if (next != null) Sequence.Parse(next, false);
        if (ids != null) { var returned = rows.Select(r => A.String(r, "recordId")).ToArray(); A.Need(next == null && returned.All(ids.Contains) && missing.All(ids.Contains) && !returned.Intersect(missing).Any() && rows.Length + missing.Length == ids.Length); }
        else { A.Need(rows.Length > 0 && missing.Length == 0); for (int i = 0; i < rows.Length; i++) A.Need(i <= through - from && A.SequenceOf(rows[i], "sequence") == from + i); long last = A.SequenceOf(rows[rows.Length - 1], "sequence"); A.Need(last == through ? next == null : next != null && Sequence.Parse(next).ToInt64() == last + 1); }
        return new ArchiveRecordPage(Array.AsReadOnly(rows), bytes, Array.AsReadOnly(missing), next, value.GetProperty("sourceCoverage"));
    }
    public async Task<byte[]> BodyChunkAsync(JsonElement artifactReference, long offset, CancellationToken cancellationToken = default)
    {
        var reference = A.Copy(artifactReference, "ArtifactRef"); int total = reference.GetProperty("bytes").GetInt32(); A.Need(offset >= 0 && offset < total, "invalid_input"); A.Need(A.String(reference, "sourceId") == A.String(_identity, "sourceId"), "identity_mismatch");
        var value = await Request("body-chunk", A.Object(w => { A.Property(w, "ref", reference); w.WriteNumber("offset", offset); }), cancellationToken).ConfigureAwait(false); S.Fields(value, "ref", "offset", "base64");
        A.Need(S.Equal(value.GetProperty("ref"), reference) && value.GetProperty("offset").GetInt64() == offset); var body = WireJson.DecodeBase64(A.String(value, "base64")); A.Need(body.Length == Math.Min(262144, total - offset)); return body;
    }
    public async Task<byte[]> BodyAsync(JsonElement artifactReference, CancellationToken cancellationToken = default)
    {
        var reference = A.Copy(artifactReference, "ArtifactRef"); int total = reference.GetProperty("bytes").GetInt32(); A.Need(total <= Math.Min(33554432, _limits.GetProperty("maxBatchBytes").GetInt64()), "capacity_exceeded"); var authority = Authority(); long poison = Interlocked.Read(ref _poison);
        var bytes = new byte[total]; for (int offset = 0; offset < total;) { var part = await BodyChunkAsync(reference, offset, cancellationToken).ConfigureAwait(false); Check(authority, poison, cancellationToken); Buffer.BlockCopy(part, 0, bytes, offset, part.Length); offset += part.Length; }
        A.Need(WireJson.Sha256(bytes) == A.String(reference, "sha256")); return bytes;
    }
    public async Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default)
    { var value = await Request("retention", S.Null, cancellationToken).ConfigureAwait(false); WireJson.ValidateNamed("Sequence", value); return value.GetString()!; }
    public async Task<JsonElement?> RetentionPageAsync(string afterRevision, CancellationToken cancellationToken = default)
    { Sequence.Parse(afterRevision); var value = await Request("retention-page", S.String(afterRevision), cancellationToken).ConfigureAwait(false); return value.ValueKind == JsonValueKind.Null ? null : S.Retention(value, _identity); }
    public async Task ApplyRetentionAsync(JsonElement retention, CancellationToken cancellationToken = default) => Null(await Request("apply-retention", S.Retention(retention, _identity), cancellationToken).ConfigureAwait(false));
    public async Task<JsonElement?> SyncPageAsync(string? afterSequence, CancellationToken cancellationToken = default)
    { if (afterSequence != null) Sequence.Parse(afterSequence, false); var value = await Request("sync-page", S.String(afterSequence), cancellationToken).ConfigureAwait(false); return value.ValueKind == JsonValueKind.Null ? null : S.Page(value, _identity); }
    /// <summary>每次只推进一份原批或一份删除修订；先落实墓碑，才允许请求未删除正文。</summary>
    public async Task<JsonElement?> SynchronizeAsync(ISyncArchiveStore cache, CancellationToken cancellationToken = default)
    {
        if (cache == null) throw new ArgumentNullException(nameof(cache)); var authority = Authority(); long poison = Interlocked.Read(ref _poison);
        void Verify() => Check(authority, poison, cancellationToken);
        var info = S.ReplicaInfo(await cache.ReplicaIdentityAsync(cancellationToken).ConfigureAwait(false)); Verify(); A.Need(S.Equal(info.GetProperty("receiver"), _identity), "identity_mismatch");
        string revision = await cache.RetentionRevisionAsync(cancellationToken).ConfigureAwait(false); Verify(); Sequence.Parse(revision); string current = await RetentionRevisionAsync(cancellationToken).ConfigureAwait(false); Verify();
        if (revision != current)
        {
            A.Need(Sequence.Parse(revision).CompareTo(Sequence.Parse(current)) < 0, "context_changed"); var retention = await RetentionPageAsync(revision, cancellationToken).ConfigureAwait(false); Verify(); A.Need(retention.HasValue && A.String(retention.Value, "previousRevision") == revision, "reconciliation_required");
            await cache.ApplyRetentionAsync(retention!.Value, cancellationToken).ConfigureAwait(false); Verify();
            if (A.String(retention.Value, "revision") != current) return A.Object(w => { w.WriteString("format", "archive-retention-receipt-v1"); w.WriteString("retentionRevision", A.String(retention.Value, "revision")); });
        }
        var head = await cache.HeadAsync(cancellationToken).ConfigureAwait(false); Verify(); var page = await SyncPageAsync(head.HasValue ? A.String(head.Value, "sequence") : null, cancellationToken).ConfigureAwait(false); Verify(); if (!page.HasValue) return null;
        var deleted = new HashSet<string>(page.Value.GetProperty("tombstones").EnumerateArray().Select(x => A.String(x, "recordId")), StringComparer.Ordinal); var refs = new Dictionary<string, JsonElement>(StringComparer.Ordinal); long bytes = 0;
        foreach (var record in page.Value.GetProperty("checkpoint").GetProperty("page").GetProperty("records").EnumerateArray()) if (!deleted.Contains(A.String(record, "recordId"))) foreach (var reference in A.References(record))
        { string id = A.String(reference, "artifactId"); if (refs.TryGetValue(id, out var prior)) A.Need(S.Equal(prior, reference)); else { bytes += reference.GetProperty("bytes").GetInt64(); A.Need(bytes <= _limits.GetProperty("maxBatchBytes").GetInt64() && refs.Count < _limits.GetProperty("maxArtifacts").GetInt32(), "capacity_exceeded"); refs.Add(id, reference); } }
        var artifacts = new List<ArchiveArtifact>(); foreach (var reference in refs.Values) { artifacts.Add(new ArchiveArtifact(A.String(reference, "artifactId"), await BodyAsync(reference, cancellationToken).ConfigureAwait(false))); Verify(); }
        var result = await cache.ReceiveSyncAsync(new ArchiveSyncReceiveInput { Page = page.Value, Artifacts = artifacts.AsReadOnly() }, cancellationToken).ConfigureAwait(false); Verify(); S.Fields(result, "format", "head", "retentionRevision");
        A.Need(A.String(result, "format") == "archive-sync-receipt-v1" && A.String(result, "retentionRevision") == A.String(page.Value, "retentionRevision")); var resultHead = S.Head(result.GetProperty("head")); var coverage = page.Value.GetProperty("ack").GetProperty("coverage"); A.Need(resultHead.HasValue && A.String(resultHead.Value, "sequence") == A.String(coverage, "throughSequence") && A.String(resultHead.Value, "recordDigest") == A.String(coverage, "headDigest")); return result;
    }
    public Task CloseAsync() { Dispose(); return Task.CompletedTask; }
    public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) != 0) return; _lifetime.Cancel(); _transport.Dispose(); }
    private int RequestMaximum => checked(2097152 + 4 * ((_limits.GetProperty("maxBatchBytes").GetInt32() + 2) / 3));
    private async Task<JsonElement> Request(string method, JsonElement input, CancellationToken cancellationToken)
    {
        A.Need(_readGrant == null || ReadMethods.Contains(method), "context_changed"); var authority = Authority(); long poison = Interlocked.Read(ref _poison); Check(authority, poison, cancellationToken);
        int maximum = method == "receive" ? RequestMaximum : S.MaximumMetadataBytes; var value = S.Copy(input, maximum); var body = Build(w => { w.WriteString("format", "archive-sync-v1"); A.Property(w, "identity", _identity); w.WriteString("method", method); A.Property(w, "value", value); }, maximum + 65536);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token); cancel.CancelAfter(30000);
        try
        {
            var access = await _transport.AccessAsync(cancel.Token).ConfigureAwait(false); Check(authority, poison, cancel.Token);
            using var response = await _transport.SendAsync(HttpMethod.Post, ApiRoutes.ArchiveSync.Path(id: A.String(_identity, "bindingId")), access, WireJson.EncodeControl(body, maximum + 65536), "application/json", null, null, cancel.Token).ConfigureAwait(false); Check(authority, poison, cancel.Token);
            SessionTransport.ExpectContent(response, "application/json"); var raw = await SessionTransport.ReadBodyAsync(response, 1638400, cancel.Token).ConfigureAwait(false);
            // archive-sync-v1 族错误经门面直通(RFC-UAPI-1 §2.4 不包装);门面自有错误(401/404/412/503)仍是统一信封。
            if (!response.IsSuccessStatusCode) SessionTransport.ThrowUnified(response, raw);
            var decoded = WireJson.DecodeControl(raw, 1638400); Check(authority, poison, cancel.Token);
            S.Fields(decoded, "format", "identity", "method", response.IsSuccessStatusCode ? "value" : "error"); A.Need(A.String(decoded, "format") == "archive-sync-v1" && A.String(decoded, "method") == method && S.Equal(decoded.GetProperty("identity"), _identity), "identity_mismatch");
            if (!response.IsSuccessStatusCode) { var error = decoded.GetProperty("error"); S.Fields(error, "code"); string code = A.String(error, "code"); var codes = new[] { "invalid_input", "identity_mismatch", "integrity_mismatch", "capacity_exceeded", "pending_ack", "receipt_mismatch", "context_changed", "reentrant", "closed", "storage_error", "reconciliation_required" }; throw new StorageException(codes.Contains(code) ? code : "storage_error"); }
            return decoded.GetProperty("value").Clone();
        }
        catch (StorageException) { throw; }
        catch (UnifiedApiException) { throw; }
        catch (ContractUnavailableException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { if (Interlocked.Read(ref _poison) != poison) throw new StorageException("reentrant"); throw new StorageException("reconciliation_required"); }
    }
    private JsonElement Authority()
    {
        A.Need(Volatile.Read(ref _closed) == 0, "closed"); long poison = Interlocked.Read(ref _poison); if (Interlocked.Exchange(ref _readingAuthority, 1) != 0) { Interlocked.Increment(ref _poison); throw new StorageException("reentrant"); }
        try
        {
            var scope = A.Copy(_readContext(), "Scope"); JsonElement context = scope;
            if (_readGrant == null) A.Need(S.Equal(A.Without(scope, "authorizationRevision"), _identity.GetProperty("scope")), "context_changed");
            else { var grant = S.Copy(_readGrant()); S.Fields(grant, "actor", "owner", "grantRevision"); WireJson.ValidateNamed("Scope", grant.GetProperty("actor")); WireJson.ValidateNamed("Sequence", grant.GetProperty("grantRevision")); A.Need(S.Equal(scope, grant.GetProperty("actor")) && S.Equal(A.Identity(grant.GetProperty("owner")), _identity) && A.String(scope, "applicationScopeId") == A.String(_identity.GetProperty("scope"), "applicationScopeId"), "context_changed"); context = grant; }
            string token = Token(); A.Need(S.Equal(scope, A.Copy(_readContext(), "Scope")), "context_changed"); A.Need(Interlocked.Read(ref _poison) == poison, "reentrant"); return A.Object(w => { A.Property(w, "context", context); w.WriteString("token", token); });
        }
        finally { Interlocked.Exchange(ref _readingAuthority, 0); }
    }
    private string Token() { string value = _readToken(); A.Need(!string.IsNullOrEmpty(value) && value.Length <= 16384 && value.All(c => c >= 33 && c <= 126), "context_changed"); return value; }
    private void Check(JsonElement original, long poison, CancellationToken ct) { ct.ThrowIfCancellationRequested(); A.Need(Interlocked.Read(ref _poison) == poison, "reentrant"); A.Need(S.Equal(original, Authority()), "context_changed"); A.Need(Interlocked.Read(ref _poison) == poison, "reentrant"); }
    private JsonElement MatchAck(JsonElement input)
    { var ack = A.Copy(input, "ArchiveAckRequest"); A.Need(A.String(ack, "bindingId") == A.String(_identity, "bindingId") && A.String(ack, "sourceId") == A.String(_identity, "sourceId") && A.String(ack, "sourceGeneration") == A.String(_identity, "sourceGeneration") && S.Equal(ack.GetProperty("generations"), _identity.GetProperty("target").GetProperty("generations")), "identity_mismatch"); return ack; }
    private void Coverage(JsonElement value)
    {
        S.Fields(value, "sourceId", "sourceGeneration", "fromSequence", "throughSequence", "headDigest", "complete"); A.Need(A.String(value, "sourceId") == A.String(_identity, "sourceId") && A.String(value, "sourceGeneration") == A.String(_identity, "sourceGeneration"), "identity_mismatch");
        A.Need(value.GetProperty("complete").ValueKind == JsonValueKind.True || value.GetProperty("complete").ValueKind == JsonValueKind.False);
        if (value.GetProperty("fromSequence").ValueKind == JsonValueKind.Null) A.Need(value.GetProperty("throughSequence").ValueKind == JsonValueKind.Null && value.GetProperty("headDigest").ValueKind == JsonValueKind.Null);
        else { long from = Sequence.Parse(A.String(value, "fromSequence"), false).ToInt64(); long through = Sequence.Parse(A.String(value, "throughSequence"), false).ToInt64(); A.Need(through >= from); WireJson.ValidateNamed("Digest", value.GetProperty("headDigest")); }
    }
    private static void Null(JsonElement value) => A.Need(value.ValueKind == JsonValueKind.Null);
    private static void Checkpoint(Utf8JsonWriter writer, ArchiveReceiveInput value) { A.Property(writer, "binding", value.Binding); A.Property(writer, "status", value.Status); A.Property(writer, "page", value.Page); A.Property(writer, "request", value.Request); }
    private static JsonElement Build(Action<Utf8JsonWriter> write, int maximum)
    { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); } return WireJson.Parse(stream.ToArray(), maximum); }
}
