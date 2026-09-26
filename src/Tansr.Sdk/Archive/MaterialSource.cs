using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using J = Tansr.Sdk.Archive.ArchiveJson;

namespace Tansr.Sdk.Archive;

/// <summary>按 Serve 点名记录从终端档案取材。上传不是核心采纳，客户端不生成摘要或改变材料信任级别。</summary>
public sealed class MaterialSource
{
    private readonly IArchiveClient _client;
    private readonly IArchiveStore _store;
    private readonly JsonElement _identity;
    private readonly Func<JsonElement> _readContext;
    private readonly Func<JsonElement, JsonElement> _requestIdentity;
    private readonly Func<JsonElement, CancellationToken, Task> _persistResponse;
    private readonly IMaterialResponseOutbox? _outbox;
    private int _active, _poisoned;

    /// <param name="requestIdentity">调用方为原材料请求保存固定 RequestIdentity；重启后不得换键。</param>
    /// <param name="persistResponse">在 POST 前耐久保存完整原 MaterialResponseRequest；失败则不发送。恢复使用此原对象。</param>
    public MaterialSource(IArchiveClient client, IArchiveStore store, JsonElement identity, Func<JsonElement> readContext,
        Func<JsonElement, JsonElement> requestIdentity, Func<JsonElement, CancellationToken, Task> persistResponse)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client)); _store = store ?? throw new ArgumentNullException(nameof(store)); _identity = J.Identity(identity);
        _readContext = readContext ?? throw new ArgumentNullException(nameof(readContext)); _requestIdentity = requestIdentity ?? throw new ArgumentNullException(nameof(requestIdentity)); _persistResponse = persistResponse ?? throw new ArgumentNullException(nameof(persistResponse));
    }
    /// <summary>接入耐久响应箱；提交前保存，明确受理后精确清理，丢回包由 RecoverPendingAsync 查原操作。</summary>
    public MaterialSource(IArchiveClient client, IArchiveStore store, JsonElement identity, Func<JsonElement> readContext,
        Func<JsonElement, JsonElement> requestIdentity, IMaterialResponseOutbox outbox)
        : this(client, store, identity, readContext, requestIdentity,
            (outbox ?? throw new ArgumentNullException(nameof(outbox))).SaveIfEmptyAsync)
    { _outbox = outbox; }
    public Task<MaterialSourceResult> RespondAsync(JsonElement materialRequest, CancellationToken cancellationToken = default)
    {
        var request = J.Copy(materialRequest, "MaterialRequest"); Match(request); ArchiveClient.ValidateMaterialRequest(request);
        return Run(async (_, check, ct) =>
        {
            if (_outbox != null)
            {
                var pending = await _outbox.ReadAsync(ct).ConfigureAwait(false); check();
                J.Need(!pending.HasValue, "reconciliation_required");
            }
            var prepared = await Prepare(request, check, ct).ConfigureAwait(false); check();
            await _persistResponse(prepared.Response, ct).ConfigureAwait(false); check();
            var current = await ReadAll(prepared.Availability.RequestedRecordIds.ToArray(), ct).ConfigureAwait(false); check();
            J.Need(current.MissingRecordIds.Count == 0 && current.Records.Count == prepared.Availability.RequestedRecordIds.Count && prepared.Availability.SourceCoverage.HasValue && J.Equal(current.SourceCoverage, prepared.Availability.SourceCoverage.Value), "context_changed");
            foreach (var result in prepared.Response.GetProperty("results").EnumerateArray())
                J.Need(current.Records.Any(r => J.Text(r, "recordId") == J.Text(result, "recordId") && J.Text(r, "recordDigest") == J.Text(result, "digest")), "context_changed");
            var receipt = J.Copy(await _client.RespondMaterialsAsync(prepared.Response, ct).ConfigureAwait(false), "MaterialReceipt"); check(); VerifyReceipt(prepared.Response, receipt, true);
            if (_outbox != null) { await _outbox.ClearIfExactAsync(prepared.Response, ct).ConfigureAwait(false); check(); }
            return new MaterialSourceResult(prepared.Response, receipt, prepared.Availability);
        }, cancellationToken, request.GetProperty("remainingTtlMs").GetInt32());
    }
    /// <summary>只处理原 material.request 事件；调用方仍负责 SSE 游标的耐久保存与连接恢复。</summary>
    public Task<MaterialSourceResult?> RespondFrameAsync(JsonElement frame, CancellationToken cancellationToken = default)
    {
        var copy = J.Copy(frame); if (!copy.TryGetProperty("eventType", out var type) || type.ValueKind != JsonValueKind.String) throw new TansrProtocolException("invalid_input");
        J.Need(copy.TryGetProperty("payload", out _) && copy.EnumerateObject().All(p => new[] { "eventType", "payload", "protocol", "bindingId", "eventId", "cursor", "revision", "generations" }.Contains(p.Name)), "invalid_input");
        if (type.GetString() != "material.request") return Task.FromResult<MaterialSourceResult?>(null);
        return RespondFrameCore(copy.GetProperty("payload"), cancellationToken);
    }
    private async Task<MaterialSourceResult?> RespondFrameCore(JsonElement request, CancellationToken ct) => await RespondAsync(request, ct).ConfigureAwait(false);
    /// <summary>对已保存的原响应查账；retryOriginal=true 才补投原对象。材料状态相同不能代替原操作回执。</summary>
    public Task<MaterialSourceResult> RecoverAsync(JsonElement savedResponse, bool retryOriginal = false, CancellationToken cancellationToken = default)
    {
        var response = J.Copy(savedResponse, "MaterialResponseRequest"); Match(response);
        return Run(async (scope, check, ct) =>
        {
            if (_outbox != null)
            {
                var pending = await _outbox.ReadAsync(ct).ConfigureAwait(false); check(); J.Need(pending.HasValue && J.Equal(pending.Value, response), "context_changed");
            }
            return await Recover(response, retryOriginal, scope, check, ct).ConfigureAwait(false);
        }, cancellationToken);
    }
    /// <summary>恢复持久箱中的原响应。默认仅查账，未知或缺失回执保留原响应，不自动补投。</summary>
    public Task<MaterialSourceResult?> RecoverPendingAsync(bool retryOriginal = false, CancellationToken cancellationToken = default)
    {
        if (_outbox == null) throw new InvalidOperationException("A material response outbox is required.");
        return Run<MaterialSourceResult?>(async (scope, check, ct) =>
        {
            var saved = await _outbox.ReadAsync(ct).ConfigureAwait(false); check(); if (!saved.HasValue) return null;
            var response = J.Copy(saved.Value, "MaterialResponseRequest"); Match(response);
            return await Recover(response, retryOriginal, scope, check, ct).ConfigureAwait(false);
        }, cancellationToken);
    }
    private async Task<MaterialSourceResult> Recover(JsonElement response, bool retryOriginal, JsonElement scope, Action check, CancellationToken ct)
    {
        JsonElement receipt;
        if (retryOriginal)
        {
            // 已上传的旧引用不能绕过终端删除；显式重投也先确认这些记录仍由当前档案授权供材。
            string[] ids = response.GetProperty("results").EnumerateArray().Select(r => J.Text(r, "recordId")).ToArray();
            var current = await ReadAll(ids, ct).ConfigureAwait(false); check();
            J.Need(current.MissingRecordIds.Count == 0 && current.Records.Count == ids.Length, "context_changed");
            foreach (var result in response.GetProperty("results").EnumerateArray())
                J.Need(current.Records.Any(r => J.Text(r, "recordId") == J.Text(result, "recordId") && J.Text(r, "recordDigest") == J.Text(result, "digest")), "context_changed");
            receipt = await _client.RespondMaterialsAsync(response, ct).ConfigureAwait(false);
        }
        else
        {
            var operation = await _client.GetOperationAsync(J.Request(J.Text(_identity, "bindingId"), "material-response", response.GetProperty("request")), ct).ConfigureAwait(false); check(); J.VerifyOperation(response, operation, scope, "material-response");
            receipt = await _client.GetMaterialStatusAsync(StatusRequest(response), ct).ConfigureAwait(false);
        }
        check(); receipt = J.Copy(receipt, "MaterialReceipt"); VerifyReceipt(response, receipt, retryOriginal);
        if (_outbox != null) { await _outbox.ClearIfExactAsync(response, ct).ConfigureAwait(false); check(); }
        return new MaterialSourceResult(response, receipt, null);
    }
    private async Task<(JsonElement Response, MaterialAvailability Availability)> Prepare(JsonElement request, Action check, CancellationToken ct)
    {
        var identity = J.Copy(_requestIdentity(request), "RequestIdentity"); check(); var askedRecords = request.GetProperty("requestedRecords").EnumerateArray().ToArray(); J.Unique(request.GetProperty("requestedRecords"), "recordId");
        var references = new Dictionary<string, JsonElement>(StringComparer.Ordinal); long requestedBytes = 0;
        foreach (var item in askedRecords) foreach (var reference in J.References(item))
        {
            string id = J.Text(reference, "artifactId"); J.Need(J.Text(reference, "sourceId") == J.Text(_identity, "sourceId"));
            if (references.TryGetValue(id, out var prior)) J.Need(J.Equal(prior, reference));
            else { references.Add(id, reference); requestedBytes += reference.GetProperty("bytes").GetInt64(); }
            J.Need((reference.GetProperty("bytes").GetInt64() + request.GetProperty("chunkBytes").GetInt32() - 1) / request.GetProperty("chunkBytes").GetInt32() <= 16, "invalid_input");
        }
        J.Need(requestedBytes <= request.GetProperty("maxBytes").GetInt32(), "invalid_input");
        string[] requestedIds = askedRecords.Select(r => J.Text(r, "recordId")).ToArray(); var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal); var uploaded = new Dictionary<string, string>(StringComparer.Ordinal); var results = new List<JsonElement>(); JsonElement? coverage = null;
        foreach (var asked in askedRecords)
        {
            check(); var page = await ReadAll(requestedIds, ct).ConfigureAwait(false); check();
            if (page.MissingRecordIds.Count > 0) throw new MaterialSourceException("source_unavailable", page.MissingRecordIds, page.SourceCoverage);
            J.Need(page.Records.Count == requestedIds.Length && page.NextFromSequence == null && page.Records.Select(r => J.Text(r, "recordId")).Distinct(StringComparer.Ordinal).Count() == requestedIds.Length && page.Records.All(r => requestedIds.Contains(J.Text(r, "recordId"))));
            J.Need(J.Text(page.SourceCoverage, "sourceId") == J.Text(_identity, "sourceId") && J.Text(page.SourceCoverage, "sourceGeneration") == J.Text(_identity, "sourceGeneration"));
            if (coverage.HasValue) J.Need(J.Equal(coverage.Value, page.SourceCoverage)); else coverage = page.SourceCoverage.Clone();
            var record = J.Copy(page.Records.Single(r => J.Text(r, "recordId") == J.Text(asked, "recordId")), "ArchiveRecord"); J.Record(record);
            J.Need(J.Text(record, "recordDigest") == J.Text(asked, "digest") && J.TargetMatches(record.GetProperty("target"), request.GetProperty("target")) && J.Equal(record.GetProperty("payload"), asked.GetProperty("payload")) && J.Equal(record.GetProperty("attachments"), asked.GetProperty("attachments")));
            var resultRefs = new List<string>();
            foreach (var reference in J.References(record))
            {
                check(); string id = J.Text(reference, "artifactId");
                if (!bodies.TryGetValue(id, out var body))
                {
                    body = await ReadBody(reference, ct).ConfigureAwait(false); check(); J.Need(body.LongLength == reference.GetProperty("bytes").GetInt64() && WireJson.Sha256(body) == J.Text(reference, "sha256")); bodies.Add(id, body);
                }
                if (!uploaded.TryGetValue(id, out var uploadId))
                {
                    JsonElement? last = null; int width = request.GetProperty("chunkBytes").GetInt32();
                    for (int offset = 0; offset < body.Length; offset += width)
                    {
                        check(); int count = Math.Min(width, body.Length - offset); var chunk = new byte[count]; Buffer.BlockCopy(body, offset, chunk, 0, count);
                        var upload = J.Build(w => { w.WriteString("protocol", J.Protocol); w.WriteString("bindingId", J.Text(request, "bindingId")); w.WriteString("materialRequestId", J.Text(request, "materialRequestId")); J.Put(w, "target", request.GetProperty("target")); w.WriteString("sourceId", J.Text(request, "sourceId")); w.WriteString("sourceGeneration", J.Text(request, "sourceGeneration")); w.WriteString("artifactId", id); w.WriteNumber("offset", offset); w.WriteNumber("bytes", count); w.WriteString("chunkSha256", WireJson.Sha256(chunk)); w.WriteString("base64", Convert.ToBase64String(chunk)); });
                        last = J.Copy(await _client.UploadMaterialChunkAsync(upload, ct).ConfigureAwait(false), "MaterialUploadStatus"); check(); VerifyUpload(request, reference, last.Value, width);
                    }
                    if (!last.HasValue || J.Text(last.Value, "state") != "committed")
                    {
                        var status = J.Build(w => { w.WriteString("protocol", J.Protocol); w.WriteString("bindingId", J.Text(request, "bindingId")); w.WriteString("materialRequestId", J.Text(request, "materialRequestId")); w.WriteString("artifactId", id); });
                        last = J.Copy(await _client.GetMaterialUploadStatusAsync(status, ct).ConfigureAwait(false), "MaterialUploadStatus"); check(); VerifyUpload(request, reference, last.Value, width);
                    }
                    J.Need(J.Text(last.Value, "state") == "committed", "upload_incomplete"); uploadId = J.Text(last.Value, "uploadId"); uploaded.Add(id, uploadId);
                }
                resultRefs.Add(uploadId);
            }
            J.Need(WireJson.DomainDigest("tansr.sdk2.payload.v1", bodies[J.Text(record.GetProperty("payload"), "artifactId")]) == J.Text(record, "payloadDigest"));
            results.Add(J.Build(w => { w.WriteString("recordId", J.Text(record, "recordId")); w.WriteString("digest", J.Text(record, "recordDigest")); w.WriteStartObject("payload"); w.WriteString("uploadId", resultRefs[0]); w.WriteEndObject(); w.WriteStartArray("attachments"); foreach (string upload in resultRefs.Skip(1)) { w.WriteStartObject(); w.WriteString("uploadId", upload); w.WriteEndObject(); } w.WriteEndArray(); }));
        }
        // 上传期间删除或档案代际改变不能靠已读内存绕过；最后再次从可信存储核完整请求集合。
        var current = await ReadAll(requestedIds, ct).ConfigureAwait(false); check(); J.Need(current.MissingRecordIds.Count == 0 && current.Records.Count == requestedIds.Length && coverage.HasValue && J.Equal(coverage.Value, current.SourceCoverage), "context_changed");
        foreach (var asked in askedRecords) { var matching = current.Records.SingleOrDefault(r => J.Text(r, "recordId") == J.Text(asked, "recordId")); J.Need(matching.ValueKind == JsonValueKind.Object && J.Text(matching, "recordDigest") == J.Text(asked, "digest"), "context_changed"); }
        var response = J.Build(w => { w.WriteString("protocol", J.Protocol); J.Put(w, "request", identity); w.WriteString("bindingId", J.Text(request, "bindingId")); w.WriteString("materialRequestId", J.Text(request, "materialRequestId")); J.Put(w, "target", request.GetProperty("target")); w.WriteString("sourceId", J.Text(request, "sourceId")); w.WriteString("sourceGeneration", J.Text(request, "sourceGeneration")); w.WriteStartArray("results"); foreach (var result in results) result.WriteTo(w); w.WriteEndArray(); });
        WireJson.ValidateNamed("MaterialResponseRequest", response); return (response, new MaterialAvailability(requestedIds, requestedIds, Array.Empty<string>(), coverage));
    }
    private async Task<ArchiveRecordPage> ReadAll(string[] ids, CancellationToken ct)
    {
        try { return await _store.ReadRecordsAsync(new ArchiveReadRequest { Identity = _identity, Selection = J.Build(w => { w.WriteStartArray("recordIds"); foreach (string id in ids) w.WriteStringValue(id); w.WriteEndArray(); }), MaxRecords = ids.Length, MaxBytes = 1048576 }, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { ct.ThrowIfCancellationRequested(); throw new MaterialSourceException("source_unavailable", ids); }
    }
    private async Task<byte[]> ReadBody(JsonElement reference, CancellationToken ct)
    {
        try { return (byte[])(await _store.BodyAsync(reference, ct).ConfigureAwait(false)).Clone(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { ct.ThrowIfCancellationRequested(); throw new MaterialSourceException("source_unavailable"); }
    }
    private void Match(JsonElement value) => J.Need(J.Text(value, "bindingId") == J.Text(_identity, "bindingId") && J.Text(value, "sourceId") == J.Text(_identity, "sourceId") && J.Text(value, "sourceGeneration") == J.Text(_identity, "sourceGeneration") && J.TargetMatches(value.GetProperty("target"), _identity.GetProperty("target")), "context_changed");
    private static void VerifyUpload(JsonElement request, JsonElement reference, JsonElement status, int width)
    {
        J.Need(J.Text(status, "bindingId") == J.Text(request, "bindingId") && J.Text(status, "materialRequestId") == J.Text(request, "materialRequestId") && J.Equal(status.GetProperty("artifact"), reference) && status.GetProperty("chunkBytes").GetInt32() == width);
        int total = reference.GetProperty("bytes").GetInt32(), sum = 0, previous = -1;
        foreach (var item in status.GetProperty("receivedOffsets").EnumerateArray()) { int offset = item.GetInt32(); J.Need(offset > previous && offset % width == 0 && offset < total); previous = offset; sum += Math.Min(width, total - offset); }
        J.Need(sum == status.GetProperty("receivedBytes").GetInt32() && (J.Text(status, "state") == "committed") == (sum == total));
    }
    private static void VerifyReceipt(JsonElement response, JsonElement receipt, bool submitted)
    {
        J.Need(J.Text(receipt, "bindingId") == J.Text(response, "bindingId") && J.Text(receipt, "materialRequestId") == J.Text(response, "materialRequestId")); J.Unique(receipt.GetProperty("acceptedRecordIds"));
        string state = J.Text(receipt, "state"); J.Need(submitted ? state == "received" : state != "pending");
        if (state != "rejected") { var ids = response.GetProperty("results").EnumerateArray().Select(r => J.Text(r, "recordId")).ToArray(); J.Need(J.Text(receipt, "revision") != "0" && receipt.GetProperty("acceptedRecordIds").GetArrayLength() == ids.Length && receipt.GetProperty("acceptedRecordIds").EnumerateArray().All(x => ids.Contains(x.GetString()!))); }
    }
    private static JsonElement StatusRequest(JsonElement response) => J.Build(w => { w.WriteString("protocol", J.Protocol); w.WriteString("bindingId", J.Text(response, "bindingId")); w.WriteString("materialRequestId", J.Text(response, "materialRequestId")); });
    private async Task<T> Run<T>(Func<JsonElement, Action, CancellationToken, Task<T>> work, CancellationToken cancellationToken, int? lifetimeMilliseconds = null)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) { Interlocked.Exchange(ref _poisoned, 1); throw new TansrProtocolException("reentrant"); }
        Interlocked.Exchange(ref _poisoned, 0);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); if (lifetimeMilliseconds.HasValue) source.CancelAfter(lifetimeMilliseconds.Value);
        try
        {
            var scope = J.Scope(_readContext, _identity);
            void Check() { source.Token.ThrowIfCancellationRequested(); J.Need(Volatile.Read(ref _poisoned) == 0, "reentrant"); J.Need(J.Equal(scope, J.Scope(_readContext, _identity)) && J.Equal(scope, _client.ReadScope()), "context_changed"); J.Need(Volatile.Read(ref _poisoned) == 0, "reentrant"); }
            Check(); var result = await work(scope, Check, source.Token).ConfigureAwait(false); Check(); return result;
        }
        finally { Interlocked.Exchange(ref _active, 0); }
    }
}

public sealed class MaterialAvailability
{
    public IReadOnlyList<string> RequestedRecordIds { get; }
    public IReadOnlyList<string> AvailableRecordIds { get; }
    public IReadOnlyList<string> UnavailableRecordIds { get; }
    public JsonElement? SourceCoverage { get; }
    internal MaterialAvailability(IEnumerable<string> requested, IEnumerable<string> available, IEnumerable<string> unavailable, JsonElement? coverage)
    { RequestedRecordIds = requested.ToArray(); AvailableRecordIds = available.ToArray(); UnavailableRecordIds = unavailable.ToArray(); SourceCoverage = coverage?.Clone(); }
}
public sealed class MaterialSourceResult
{
    public JsonElement Response { get; }
    public JsonElement Receipt { get; }
    /// <summary>查账不会声称本次重新读取过本地档案，所以恢复结果不返回新的可用性证明。</summary>
    public MaterialAvailability? Availability { get; }
    internal MaterialSourceResult(JsonElement response, JsonElement receipt, MaterialAvailability? availability) { Response = response.Clone(); Receipt = receipt.Clone(); Availability = availability; }
}
public sealed class MaterialSourceException : TansrException
{
    public IReadOnlyList<string> UnavailableRecordIds { get; }
    public JsonElement? SourceCoverage { get; }
    public MaterialSourceException(string code, IEnumerable<string>? unavailableRecordIds = null, JsonElement? sourceCoverage = null) : base(code)
    { UnavailableRecordIds = (unavailableRecordIds ?? Array.Empty<string>()).ToArray(); SourceCoverage = sourceCoverage?.Clone(); }
}
