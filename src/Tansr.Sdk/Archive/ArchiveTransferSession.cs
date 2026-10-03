using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using J = Tansr.Sdk.Archive.ArchiveJson;

namespace Tansr.Sdk.Archive;

/// <summary>绑定到一个可信档案域的下载协调器；只有自有存储耐久返回的原 ACK 才能发送给 Serve。</summary>
public sealed class ArchiveTransferSession
{
    private readonly IArchiveClient _client;
    private readonly IArchiveStore _store;
    private readonly JsonElement _identity;
    private readonly Func<JsonElement> _readContext;
    private readonly Func<JsonElement, JsonElement> _requestIdentity;
    private int _active, _poisoned;
    public ArchiveTransferSession(IArchiveClient client, IArchiveStore store, JsonElement identity, Func<JsonElement> readContext, Func<JsonElement, JsonElement> requestIdentity)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client)); _store = store ?? throw new ArgumentNullException(nameof(store));
        _identity = J.Identity(identity); _readContext = readContext ?? throw new ArgumentNullException(nameof(readContext)); _requestIdentity = requestIdentity ?? throw new ArgumentNullException(nameof(requestIdentity));
    }
    /// <summary>有界拉取；若原 ACK 未决，先查询其原操作。网络失败保留本地 ACK，调用方明确恢复，不生成新请求键。</summary>
    public Task<ArchiveTransferResult> PullAsync(int maximumPages = 1, CancellationToken cancellationToken = default)
    {
        if (maximumPages < 1 || maximumPages > 128) throw new ArgumentOutOfRangeException(nameof(maximumPages));
        return Run(async (scope, check) =>
        {
            var receipts = new List<JsonElement>(); var recovered = await RecoverCore(scope, check, false, cancellationToken).ConfigureAwait(false); if (recovered.HasValue) receipts.Add(recovered.Value);
            int count = 0; bool complete = false;
            for (int index = 0; index < maximumPages; index++)
            {
                check(); string bindingId = J.Text(_identity, "bindingId"); var binding = J.Copy(await _client.GetBindingAsync(bindingId, cancellationToken).ConfigureAwait(false), "BindingView"); check(); MatchBinding(binding);
                var status = J.Copy(await _client.GetArchiveStatusAsync(bindingId, cancellationToken).ConfigureAwait(false), "ArchiveStatus"); check(); MatchSource(status); J.Need(J.Text(status, "revision") == J.Text(binding, "revision") && J.Text(status, "state") == J.Text(binding, "state"));
                var head = await _store.HeadAsync(cancellationToken).ConfigureAwait(false); check(); var limits = J.Copy(_client.GetEffectiveLimits(bindingId), "Limits");
                var request = J.Build(w => { w.WriteString("protocol", J.Protocol); w.WriteString("bindingId", bindingId); J.Put(w, "generations", _identity.GetProperty("target").GetProperty("generations")); w.WriteString("afterSequence", head.HasValue ? J.Text(head.Value, "sequence") : null); w.WriteNumber("limit", limits.GetProperty("pageRecords").GetInt32()); w.WriteNumber("maxBytes", limits.GetProperty("pageBytes").GetInt32()); });
                var page = J.Copy(await _client.ReadRecordsAsync(request, cancellationToken).ConfigureAwait(false), "ArchivePage"); check();
                J.Need(J.Text(page, "bindingId") == bindingId && J.Equal(page.GetProperty("generations"), _identity.GetProperty("target").GetProperty("generations")) && J.Equal(page.GetProperty("publishedThroughSequence"), status.GetProperty("publishedThroughSequence")));
                if (page.GetProperty("records").GetArrayLength() == 0) { J.Need(page.GetProperty("complete").GetBoolean()); complete = true; break; }
                var artifacts = new List<ArchiveArtifact>(); var references = new Dictionary<string, JsonElement>(StringComparer.Ordinal); long total = 0;
                foreach (var record in page.GetProperty("records").EnumerateArray())
                {
                    J.Record(record); J.Need(J.TargetMatches(record.GetProperty("target"), _identity.GetProperty("target")));
                    foreach (var reference in J.References(record))
                    {
                        string id = J.Text(reference, "artifactId"); J.Need(J.Text(reference, "sourceId") == J.Text(_identity, "sourceId"));
                        if (references.TryGetValue(id, out var previous)) J.Need(J.Equal(previous, reference));
                        else { references.Add(id, reference); total += reference.GetProperty("bytes").GetInt64(); }
                    }
                }
                J.Need(total <= 67108864, "capacity_exceeded");
                foreach (var reference in references.Values) artifacts.Add(new ArchiveArtifact(J.Text(reference, "artifactId"), await Download(reference, limits, check, cancellationToken).ConfigureAwait(false)));
                // A completed turn can publish its archive before finish-run advances the binding revision.
                // Refresh before the first durable write; this is not a rebase of a previously issued ACK.
                var currentBinding = J.Copy(await _client.GetBindingAsync(bindingId, cancellationToken).ConfigureAwait(false), "BindingView"); check(); MatchBinding(currentBinding);
                var currentStatus = J.Copy(await _client.GetArchiveStatusAsync(bindingId, cancellationToken).ConfigureAwait(false), "ArchiveStatus"); check(); MatchSource(currentStatus);
                J.Need(J.Seq(currentBinding, "revision") >= J.Seq(binding, "revision") && J.Text(currentStatus, "revision") == J.Text(currentBinding, "revision") && J.Text(currentStatus, "state") == J.Text(currentBinding, "state") && J.Equal(currentStatus.GetProperty("publishedThroughSequence"), page.GetProperty("publishedThroughSequence")), "context_changed");
                binding = currentBinding; status = currentStatus;
                var identity = J.Copy(_requestIdentity(binding), "RequestIdentity"); check(); J.Need(J.Text(identity, "operationEpoch") == J.Text(binding.GetProperty("operationEpoch"), "id"));
                var ack = J.Copy(await _store.ReceiveAsync(new ArchiveReceiveInput { Binding = binding, Status = status, Page = page, Request = identity, Artifacts = artifacts.AsReadOnly() }, cancellationToken).ConfigureAwait(false), "ArchiveAckRequest"); check();
                MatchAck(ack); J.Need(J.Equal(ack.GetProperty("request"), identity) && J.Text(ack, "expectedRevision") == J.Text(binding, "revision"));
                var rows = page.GetProperty("records"); var coverage = ack.GetProperty("coverage"); J.Need(J.Text(coverage, "fromSequence") == J.Text(rows[0], "sequence") && J.Text(coverage, "throughSequence") == J.Text(rows[rows.GetArrayLength() - 1], "sequence") && J.Text(coverage, "headDigest") == J.Text(rows[rows.GetArrayLength() - 1], "recordDigest"));
                var receipt = J.Copy(await PostAck(ack, check, cancellationToken).ConfigureAwait(false), "MutationReceipt"); check(); J.VerifyOperation(ack, receipt, scope, "archive-ack");
                await _store.ConfirmAsync(receipt, cancellationToken).ConfigureAwait(false); check(); receipts.Add(receipt); count += rows.GetArrayLength(); complete = page.GetProperty("complete").GetBoolean(); if (complete) break;
            }
            return new ArchiveTransferResult(count, complete, receipts.AsReadOnly());
        }, cancellationToken);
    }
    /// <summary>默认只查询原操作；retryOriginal=true 明确要求补投同一耐久 ACK，绝不换键或根据覆盖相同推断成功。</summary>
    public Task<JsonElement?> RecoverPendingAsync(bool retryOriginal = false, CancellationToken cancellationToken = default) => Run((scope, check) => RecoverCore(scope, check, retryOriginal, cancellationToken), cancellationToken);
    private async Task<JsonElement?> RecoverCore(JsonElement scope, Action check, bool retryOriginal, CancellationToken ct)
    {
        var pending = await _store.PendingAsync(ct).ConfigureAwait(false); check(); if (!pending.HasValue) return null;
        var ack = J.Copy(pending.Value, "ArchiveAckRequest"); MatchAck(ack);
        var receipt = retryOriginal ? await PostAck(ack, check, ct).ConfigureAwait(false) : await _client.GetOperationAsync(J.Request(J.Text(_identity, "bindingId"), "archive-ack", ack.GetProperty("request")), ct).ConfigureAwait(false);
        check(); J.VerifyOperation(ack, receipt, scope, "archive-ack"); await _store.ConfirmAsync(receipt, ct).ConfigureAwait(false); check(); return receipt.Clone();
    }
    private async Task<JsonElement> PostAck(JsonElement ack, Action check, CancellationToken ct)
    {
        try { return await _client.AcknowledgeAsync(ack, ct).ConfigureAwait(false); }
        catch (TansrHttpException error) when (error.FamilyStatus == 409 && error.FamilyCode == "binding_conflict")
        { check(); throw new ArchiveAcknowledgementConflictException(ack); }
    }
    private async Task<byte[]> Download(JsonElement reference, JsonElement limits, Action check, CancellationToken ct)
    {
        int total = reference.GetProperty("bytes").GetInt32(); using var body = new MemoryStream(total);
        while (body.Length < total)
        {
            check(); int offset = checked((int)body.Length); var request = J.Build(w => { w.WriteString("protocol", J.Protocol); w.WriteString("bindingId", J.Text(_identity, "bindingId")); J.Put(w, "generations", _identity.GetProperty("target").GetProperty("generations")); w.WriteString("artifactId", J.Text(reference, "artifactId")); w.WriteNumber("offset", offset); w.WriteNumber("maxBytes", Math.Min(total - offset, limits.GetProperty("chunkBytes").GetInt32())); });
            var chunk = J.Copy(await _client.ReadArtifactAsync(request, ct).ConfigureAwait(false), "ArtifactChunk"); check(); byte[] bytes = WireJson.DecodeBase64(J.Text(chunk, "base64"));
            J.Need(J.Text(chunk, "bindingId") == J.Text(_identity, "bindingId") && J.Text(chunk, "artifactId") == J.Text(reference, "artifactId") && J.Text(chunk, "sourceId") == J.Text(_identity, "sourceId") && J.Equal(chunk.GetProperty("generations"), _identity.GetProperty("target").GetProperty("generations")) && chunk.GetProperty("offset").GetInt32() == offset && bytes.Length == chunk.GetProperty("bytes").GetInt32() && bytes.Length >= 1 && bytes.Length <= request.GetProperty("maxBytes").GetInt32() && offset + bytes.Length <= total && chunk.GetProperty("totalBytes").GetInt32() == total && J.Text(chunk, "sha256") == J.Text(reference, "sha256") && WireJson.Sha256(bytes) == J.Text(chunk, "chunkSha256"));
            body.Write(bytes, 0, bytes.Length);
        }
        byte[] value = body.ToArray(); J.Need(WireJson.Sha256(value) == J.Text(reference, "sha256")); return value;
    }
    private void MatchBinding(JsonElement binding)
    {
        J.Need(J.Text(binding, "bindingId") == J.Text(_identity, "bindingId") && J.Text(binding, "sourceId") == J.Text(_identity, "sourceId") && J.Equal(J.Without(binding.GetProperty("scope"), "authorizationRevision"), _identity.GetProperty("scope")) && J.TargetMatches(binding.GetProperty("target"), _identity.GetProperty("target")) && J.Text(binding, "state") != "closed" && binding.GetProperty("acceptedCapabilities").EnumerateArray().Any(v => v.GetString() == "archive-transfer-v1") && binding.GetProperty("operationEpoch").ValueKind == JsonValueKind.Object, "context_changed");
    }
    private void MatchSource(JsonElement value) => J.Need(J.Text(value, "bindingId") == J.Text(_identity, "bindingId") && J.Text(value, "sourceId") == J.Text(_identity, "sourceId") && J.Text(value, "sourceGeneration") == J.Text(_identity, "sourceGeneration") && J.Equal(value.GetProperty("generations"), _identity.GetProperty("target").GetProperty("generations")), "context_changed");
    private void MatchAck(JsonElement ack) { MatchSource(ack); J.Coverage(ack.GetProperty("coverage")); }
    private async Task<T> Run<T>(Func<JsonElement, Action, Task<T>> operation, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) { Interlocked.Exchange(ref _poisoned, 1); throw new StorageException("reentrant"); }
        Interlocked.Exchange(ref _poisoned, 0);
        try
        {
            ct.ThrowIfCancellationRequested(); var scope = J.Scope(_readContext, _identity);
            void Check() { ct.ThrowIfCancellationRequested(); J.Need(Volatile.Read(ref _poisoned) == 0, "reentrant"); J.Need(J.Equal(scope, J.Scope(_readContext, _identity)) && J.Equal(scope, _client.ReadScope()), "context_changed"); J.Need(Volatile.Read(ref _poisoned) == 0, "reentrant"); }
            Check(); var result = await operation(scope, Check).ConfigureAwait(false); Check(); return result;
        }
        finally { Interlocked.Exchange(ref _active, 0); }
    }
}

public sealed class ArchiveTransferResult
{
    public int ArchivedRecords { get; }
    public bool Complete { get; }
    public IReadOnlyList<JsonElement> Receipts { get; }
    internal ArchiveTransferResult(int archivedRecords, bool complete, IReadOnlyList<JsonElement> receipts) { ArchivedRecords = archivedRecords; Complete = complete; Receipts = receipts; }
}
