using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using J = Tansr.Sdk.Archive.ArchiveJson;

namespace Tansr.Sdk.Archive;

/// <summary>复用 TansrClient 认证和取消的原档案 HTTP 客户端，不自动重试写入。</summary>
public sealed class ArchiveClient : IArchiveRecoveryClient, IArchiveConnectionSource
{
    private readonly TansrClient _client;
    private readonly object _gate = new object();
    private readonly JsonElement _local;
    private JsonElement? _discovered;
    private string? _scope;
    private readonly Dictionary<string, JsonElement> _bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public ArchiveClient(TansrClient client, JsonElement? limits = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client)); _local = J.Copy(limits ?? MaximumLimits(), "Limits"); ValidateLimits(_local);
    }
    public JsonElement ReadScope() => _client.ReadExecutionScope();
    public JsonElement GetEffectiveLimits(string bindingId)
    {
        string scope = J.Canonical(ReadScope()); lock (_gate)
        {
            if (_scope != scope) { _scope = scope; _discovered = null; _bindings.Clear(); }
            return Intersect(_local, _discovered, _bindings.TryGetValue(bindingId, out var value) ? value : (JsonElement?)null);
        }
    }
    public async Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => await Send(HttpMethod.Get, ApiRoutes.ArchiveCapabilities.Path(), null, null, "CapabilitiesResponse", cancellationToken).ConfigureAwait(false);
    /// <summary>消费一次原绑定事件连接。回调必须先耐久处理 frame.cursor；只有回调成功才推进返回游标，不自动重连或制造恢复票。</summary>
    public Task<string?> ConsumeEventsAsync(string bindingId, JsonElement generations, Func<JsonElement, CancellationToken, Task> onFrame,
        string? lastEventId = null, CancellationToken cancellationToken = default)
        => ConsumeConnectedEventsAsync(bindingId, generations, onFrame, _ => Task.CompletedTask, lastEventId, cancellationToken);

    public Task<string?> ConsumeConnectedEventsAsync(string bindingId, JsonElement generations, Func<JsonElement, CancellationToken, Task> onFrame,
        Func<CancellationToken, Task> onConnected, string? lastEventId = null, CancellationToken cancellationToken = default)
    {
        if (onFrame == null) throw new ArgumentNullException(nameof(onFrame)); var limits = GetEffectiveLimits(bindingId); var fixedGenerations = J.Copy(generations, "Generations");
        return _client.ConsumeArchiveEventsAsync(bindingId, fixedGenerations, async (frame, ct) =>
        {
            var scope = J.Copy(ReadScope(), "Scope"); var payload = frame.GetProperty("payload"); string eventType = J.Text(frame, "eventType");
            var input = J.Build(w => w.WriteString("bindingId", bindingId));
            if (eventType == "archive.status") ValidateResponse(payload, input, "ArchiveStatus", scope, limits);
            else if (eventType == "material.status") ValidateResponse(payload, input, "MaterialReceipt", scope, limits);
            else if (eventType == "binding.status") { ValidateResponse(payload, input, "BindingView", scope, limits); J.Need(WireJson.EncodeControl(payload).Length <= payload.GetProperty("limits").GetProperty("controlBytes").GetInt32(), "frame_too_large"); }
            else if (eventType == "material.request") ValidateMaterialRequest(payload);
            await onFrame(frame, ct).ConfigureAwait(false); J.Need(J.Equal(scope, ReadScope()), "context_changed");
        }, lastEventId, cancellationToken, limits.GetProperty("controlBytes").GetInt32(), onConnected);
    }
    internal static void ValidateMaterialRequest(JsonElement value)
    {
        WireJson.ValidateNamed("MaterialRequest", value); J.Unique(value.GetProperty("requestedRecords"), "recordId");
        var refs = new Dictionary<string, JsonElement>(StringComparer.Ordinal); long total = 0; int width = value.GetProperty("chunkBytes").GetInt32();
        foreach (var record in value.GetProperty("requestedRecords").EnumerateArray())
        {
            var local = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in J.References(record))
            { string id = J.Text(reference, "artifactId"); J.Need(local.Add(id) && J.Text(reference, "sourceId") == J.Text(value, "sourceId") && (reference.GetProperty("bytes").GetInt64() + width - 1) / width <= 16); if (refs.TryGetValue(id, out var prior)) J.Need(J.Equal(prior, reference)); else { refs.Add(id, reference); total += reference.GetProperty("bytes").GetInt64(); } }
        }
        J.Need(total <= value.GetProperty("maxBytes").GetInt64());
    }
    public Task<JsonElement> GetBindingTargetAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        var input = J.Copy(request, "BindingTargetRequest"); return Send(HttpMethod.Get, ApiRoutes.ArchiveBindingTarget.Path(id: J.Text(input, "sessionId")) + ApiRoutes.ArchiveBindingTarget.Query(("protocol", J.Protocol)), input, "BindingTargetRequest", "BindingTargetView", cancellationToken);
    }
    public Task<JsonElement> CreateBindingAsync(JsonElement request, CancellationToken cancellationToken = default) => Send(HttpMethod.Post, ApiRoutes.ArchiveBindingCreate.Path(), request, "BindingCreateRequest", "BindingView", cancellationToken, 201);
    public Task<JsonElement> GetBindingAsync(string bindingId, CancellationToken cancellationToken = default) => ReadBindingPath(bindingId, ApiRoutes.ArchiveBindingGet, "BindingView", cancellationToken);
    public Task<JsonElement> GetArchiveStatusAsync(string bindingId, CancellationToken cancellationToken = default) => ReadBindingPath(bindingId, ApiRoutes.ArchiveStatus, "ArchiveStatus", cancellationToken);
    private Task<JsonElement> ReadBindingPath(string bindingId, ApiOperation operation, string schema, CancellationToken ct)
    {
        var input = J.Build(w => w.WriteString("bindingId", bindingId)); WireJson.ValidateNamed("Id", input.GetProperty("bindingId")); return Send(HttpMethod.Get, operation.Path(id: bindingId) + operation.Query(("protocol", J.Protocol)), input, null, schema, ct);
    }
    public Task<JsonElement> ReadRecordsAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        var input = J.Copy(request, "ArchiveReadRequest"); var query = Generations(input); if (input.GetProperty("afterSequence").ValueKind != JsonValueKind.Null) query.Add(("afterSequence", J.Text(input, "afterSequence")));
        query.Add(("limit", input.GetProperty("limit").GetInt32())); query.Add(("maxBytes", input.GetProperty("maxBytes").GetInt32()));
        return Send(HttpMethod.Get, ApiRoutes.ArchiveRecordsRead.Path(id: J.Text(input, "bindingId")) + J.Query(ApiRoutes.ArchiveRecordsRead, query.ToArray()), input, "ArchiveReadRequest", "ArchivePage", cancellationToken, responseMaximum: input.GetProperty("maxBytes").GetInt32());
    }
    public Task<JsonElement> ReadArtifactAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        var input = J.Copy(request, "ArtifactReadRequest"); var query = Generations(input); query.Add(("offset", input.GetProperty("offset").GetInt32())); query.Add(("maxBytes", input.GetProperty("maxBytes").GetInt32()));
        return Send(HttpMethod.Get, ApiRoutes.ArchiveArtifactRead.Path(id: J.Text(input, "bindingId"), targetId: J.Text(input, "artifactId")) + J.Query(ApiRoutes.ArchiveArtifactRead, query.ToArray()), input, "ArtifactReadRequest", "ArtifactChunk", cancellationToken, responseMaximum: 357720);
    }
    public Task<JsonElement> AcknowledgeAsync(JsonElement request, CancellationToken cancellationToken = default) => Send(HttpMethod.Post, ApiRoutes.ArchiveAckCommit.Path(id: J.Text(request, "bindingId")), request, "ArchiveAckRequest", "MutationReceipt", cancellationToken);
    /// <summary>独立恢复合同；输入必须来自接收器 PrepareAckRebaseAsync 的耐久结果，不自动生成恢复键。</summary>
    public async Task<JsonElement> RebaseAckAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var input = ArchiveRecoveryContract.Request(request); string binding = J.Text(input, "bindingId");
        int controlBytes = GetEffectiveLimits(binding).GetProperty("controlBytes").GetInt32(); input = ArchiveRecoveryContract.Request(input, controlBytes); var scope = J.Copy(ReadScope(), "Scope");
        var response = await _client.SendArchiveRecoveryAsync(input, controlBytes + ArchiveRecoveryContract.RequestEnvelopeBytes, 2 * controlBytes + ArchiveRecoveryContract.ResponseEnvelopeBytes, cancellationToken).ConfigureAwait(false);
        J.Need(J.Equal(scope, ReadScope()), "context_changed"); return ArchiveRecoveryContract.Receipt(response, input, scope, controlBytes);
    }
    public Task<JsonElement> UploadMaterialChunkAsync(JsonElement request, CancellationToken cancellationToken = default) => Send(HttpMethod.Post, Material(ApiRoutes.MaterialUploadChunk, request, J.Text(request, "artifactId")), request, "MaterialUploadChunkRequest", "MaterialUploadStatus", cancellationToken);
    public Task<JsonElement> GetMaterialUploadStatusAsync(JsonElement request, CancellationToken cancellationToken = default) => Send(HttpMethod.Get, Material(ApiRoutes.MaterialUploadStatus, request, J.Text(request, "artifactId")) + ApiRoutes.MaterialUploadStatus.Query(("protocol", J.Protocol)), request, "MaterialUploadStatusRequest", "MaterialUploadStatus", cancellationToken);
    public Task<JsonElement> RespondMaterialsAsync(JsonElement request, CancellationToken cancellationToken = default) => Send(HttpMethod.Post, ApiRoutes.MaterialResponseSubmit.Path(id: J.Text(request, "bindingId")), request, "MaterialResponseRequest", "MaterialReceipt", cancellationToken, 202);
    public Task<JsonElement> GetMaterialStatusAsync(JsonElement request, CancellationToken cancellationToken = default) => Send(HttpMethod.Get, Material(ApiRoutes.MaterialStatus, request) + ApiRoutes.MaterialStatus.Query(("protocol", J.Protocol)), request, "MaterialStatusRequest", "MaterialReceipt", cancellationToken);
    public Task<JsonElement> CloseBindingAsync(JsonElement request, CancellationToken cancellationToken = default) => Send(HttpMethod.Post, ApiRoutes.ArchiveBindingClose.Path(id: J.Text(request, "bindingId")), request, "BindingCloseRequest", "MutationReceipt", cancellationToken);
    public Task<JsonElement> GetOperationAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        var input = J.Copy(request, "OperationStatusRequest"); string operation = J.Text(input, "operation"), subject = operation == "binding-create" ? "sessionId" : "bindingId"; var id = input.GetProperty("request");
        return Send(HttpMethod.Get, ApiRoutes.ArchiveOperationQuery.Path() + J.Query(ApiRoutes.ArchiveOperationQuery, ("protocol", J.Protocol), ("operation", operation), (subject, J.Text(input, subject)), ("operationEpoch", J.Text(id, "operationEpoch")), ("requestId", J.Text(id, "requestId"))), input, "OperationStatusRequest", "MutationReceipt", cancellationToken);
    }
    private async Task<JsonElement> Send(HttpMethod method, string path, JsonElement? supplied, string? requestSchema, string responseSchema, CancellationToken ct, int expectedStatus = 200, int responseMaximum = 262144)
    {
        ct.ThrowIfCancellationRequested(); var input = supplied.HasValue ? J.Copy(supplied.Value, requestSchema) : J.Build(_ => { });
        string binding = input.TryGetProperty("bindingId", out var field) ? field.GetString()! : ""; var limits = GetEffectiveLimits(binding); var scope = J.Copy(ReadScope(), "Scope");
        ValidateRequest(input, requestSchema, limits); J.Need(WireJson.EncodeControl(input).Length <= limits.GetProperty("controlBytes").GetInt32(), "payload_too_large");
        J.Need(path.Length <= 8192, "invalid_input");
        int maximum = responseSchema == "CapabilitiesResponse" || responseSchema == "BindingView" ? _local.GetProperty("controlBytes").GetInt32() : responseSchema == "ArchivePage" ? Math.Min(responseMaximum, limits.GetProperty("pageBytes").GetInt32()) : responseSchema == "ArtifactChunk" ? responseMaximum : Math.Min(responseMaximum, limits.GetProperty("controlBytes").GetInt32());
        var value = await _client.SendControlAsync(method, path, method == HttpMethod.Post ? input : (JsonElement?)null, ct, maximum, expectedStatus).ConfigureAwait(false);
        WireJson.ValidateNamed(responseSchema, value); J.Need(J.Equal(scope, ReadScope()), "context_changed");
        ValidateResponse(value, input, responseSchema, scope, limits);
        lock (_gate)
        {
            if (_scope != J.Canonical(scope)) { _bindings.Clear(); _discovered = null; _scope = J.Canonical(scope); }
            if (responseSchema == "CapabilitiesResponse") _discovered = value.GetProperty("limits").Clone();
            if (responseSchema == "BindingView") _bindings[J.Text(value, "bindingId")] = value.GetProperty("limits").Clone();
        }
        return value;
    }
    private static void ValidateRequest(JsonElement value, string? schema, JsonElement limits)
    {
        if (schema == "BindingCreateRequest") { J.Unique(value.GetProperty("requiredCapabilities")); J.Unique(value.GetProperty("optionalCapabilities")); J.Need(!value.GetProperty("requiredCapabilities").EnumerateArray().Select(x => x.GetString()).Intersect(value.GetProperty("optionalCapabilities").EnumerateArray().Select(x => x.GetString())).Any()); }
        if (schema == "ArchiveReadRequest") J.Need(value.GetProperty("limit").GetInt32() <= limits.GetProperty("pageRecords").GetInt32() && value.GetProperty("maxBytes").GetInt32() <= limits.GetProperty("pageBytes").GetInt32());
        if (schema == "ArtifactReadRequest") J.Need(value.GetProperty("maxBytes").GetInt32() <= limits.GetProperty("chunkBytes").GetInt32());
        if (schema == "ArchiveAckRequest")
        {
            var coverage = value.GetProperty("coverage"); J.Coverage(coverage); J.Need(J.Seq(coverage, "throughSequence") - J.Seq(coverage, "fromSequence") < 128); J.Unique(value.GetProperty("payloads"), "artifactId"); J.Unique(value.GetProperty("attachments"), "artifactId");
            var hashes = value.GetProperty("payloads").EnumerateArray().ToDictionary(x => J.Text(x, "artifactId"), x => J.Text(x, "sha256")); foreach (var item in value.GetProperty("attachments").EnumerateArray()) if (hashes.TryGetValue(J.Text(item, "artifactId"), out var hash)) J.Need(hash == J.Text(item, "sha256"));
        }
        if (schema == "MaterialUploadChunkRequest") { var bytes = WireJson.DecodeBase64(J.Text(value, "base64")); J.Need(bytes.Length == value.GetProperty("bytes").GetInt32() && bytes.Length <= limits.GetProperty("materialChunkBytes").GetInt32() && WireJson.Sha256(bytes) == J.Text(value, "chunkSha256")); }
        if (schema == "MaterialResponseRequest") { J.Unique(value.GetProperty("results"), "recordId"); J.Need(value.GetProperty("results").GetArrayLength() <= limits.GetProperty("materialCandidates").GetInt32()); }
    }
    private void ValidateResponse(JsonElement value, JsonElement input, string schema, JsonElement scope, JsonElement limits)
    {
        foreach (string key in new[] { "bindingId", "materialRequestId", "generations" }) if (input.TryGetProperty(key, out var expected) && value.TryGetProperty(key, out var actual)) J.Need(J.Equal(expected, actual));
        if (schema == "BindingTargetView") { J.Need(J.Text(value.GetProperty("target"), "sessionId") == J.Text(input, "sessionId")); Epoch(value.GetProperty("operationEpoch"), null); if (value.GetProperty("bindingId").ValueKind == JsonValueKind.Null) J.Need(J.Text(value, "revision") == "0" && value.GetProperty("operationEpoch").ValueKind == JsonValueKind.Null); }
        if (schema == "CapabilitiesResponse" || schema == "BindingView")
        {
            var negotiated = value.GetProperty("limits"); ValidateLimits(negotiated); Epoch(value.GetProperty("operationEpoch"), negotiated.GetProperty("epochLifetimeMs").GetInt64());
            JsonElement ceiling; lock (_gate) ceiling = schema == "CapabilitiesResponse" ? _local : Intersect(_local, _discovered, null);
            foreach (var item in negotiated.EnumerateObject()) J.Need(item.Value.GetInt64() <= ceiling.GetProperty(item.Name).GetInt64());
            if (schema == "CapabilitiesResponse") J.Need(value.GetProperty("archiveAckFormats").GetArrayLength() == (value.GetProperty("capabilities").EnumerateArray().Any(x => x.GetString() == "archive-transfer-v1") ? 1 : 0));
            if (schema == "BindingView")
            {
                J.Need(J.Equal(J.Without(value.GetProperty("scope"), "authorizationRevision"), J.Without(scope, "authorizationRevision"))); J.Unique(value.GetProperty("acceptedCapabilities")); J.Unique(value.GetProperty("rejectedCapabilities"), "capability");
                var accepted = value.GetProperty("acceptedCapabilities").EnumerateArray().Select(x => x.GetString()!).ToArray(); var rejected = value.GetProperty("rejectedCapabilities").EnumerateArray().Select(x => J.Text(x, "capability")).ToArray(); J.Need(!accepted.Intersect(rejected).Any());
                J.Need(value.GetProperty("archiveAckFormat").GetString() == (accepted.Contains("archive-transfer-v1") ? "split-receipts-v1" : null));
                if (input.TryGetProperty("target", out var target)) { J.Need(J.Equal(target, value.GetProperty("target")) && J.Text(input.GetProperty("archive"), "sourceId") == J.Text(value, "sourceId")); var required = input.GetProperty("requiredCapabilities").EnumerateArray().Select(x => x.GetString()!).ToArray(); var asked = required.Concat(input.GetProperty("optionalCapabilities").EnumerateArray().Select(x => x.GetString()!)).ToArray(); J.Need(required.All(accepted.Contains) && accepted.Length + rejected.Length == asked.Length && accepted.Concat(rejected).All(asked.Contains)); }
            }
        }
        if (schema == "ArchiveStatus")
        {
            var published = value.GetProperty("publishedThroughSequence"); var released = value.GetProperty("releasableThroughSequence"); var coverage = value.GetProperty("acknowledgedCoverage");
            if (published.ValueKind != JsonValueKind.Null) Sequence.Parse(published.GetString()!, false);
            if (released.ValueKind != JsonValueKind.Null) Sequence.Parse(released.GetString()!, false);
            if (coverage.ValueKind == JsonValueKind.Null) J.Need(released.ValueKind == JsonValueKind.Null);
            else { J.Coverage(coverage); J.Need(published.ValueKind != JsonValueKind.Null && J.Seq(coverage, "throughSequence") <= Sequence.Parse(published.GetString()!).ToInt64()); if (released.ValueKind != JsonValueKind.Null) J.Need(Sequence.Parse(released.GetString()!).ToInt64() <= J.Seq(coverage, "throughSequence")); }
        }
        if (schema == "ArchivePage")
        {
            var records = value.GetProperty("records"); J.Need(records.GetArrayLength() <= input.GetProperty("limit").GetInt32()); long previous = input.GetProperty("afterSequence").ValueKind == JsonValueKind.Null ? 0 : J.Seq(input, "afterSequence"); string? predecessor = previous == 0 ? new string('0', 64) : null;
            J.Unique(records, "recordId"); var refs = new Dictionary<string, string>();
            foreach (var record in records.EnumerateArray())
            {
                J.Record(record); J.Need(previous < long.MaxValue && J.Seq(record, "sequence") == ++previous && J.Equal(record.GetProperty("target").GetProperty("generations"), input.GetProperty("generations")) && (predecessor == null || predecessor == J.Text(record, "predecessorDigest"))); predecessor = J.Text(record, "recordDigest");
                J.Need(WireJson.EncodeControl(record).Length <= limits.GetProperty("recordBytes").GetInt32());
                foreach (var reference in J.References(record)) { J.Need(reference.GetProperty("bytes").GetInt64() <= limits.GetProperty("attachmentBytes").GetInt64()); var id = J.Text(reference, "artifactId"); var json = J.Canonical(reference); if (refs.TryGetValue(id, out var prior)) J.Need(prior == json); else refs.Add(id, json); }
            }
            J.Need(value.GetProperty("nextAfterSequence").GetString() == (previous == 0 ? null : previous.ToString(CultureInfo.InvariantCulture))); var published = value.GetProperty("publishedThroughSequence");
            if (published.ValueKind == JsonValueKind.Null) J.Need(records.GetArrayLength() == 0 && previous == 0 && value.GetProperty("complete").GetBoolean());
            else { long last = Sequence.Parse(published.GetString()!, false).ToInt64(); J.Need(previous <= last && value.GetProperty("complete").GetBoolean() == (previous == last) && (previous == last || records.GetArrayLength() > 0)); }
        }
        if (schema == "ArtifactChunk")
        {
            var bytes = WireJson.DecodeBase64(J.Text(value, "base64")); J.Need(J.Text(value, "artifactId") == J.Text(input, "artifactId") && value.GetProperty("offset").GetInt32() == input.GetProperty("offset").GetInt32() && bytes.Length == value.GetProperty("bytes").GetInt32() && bytes.Length <= input.GetProperty("maxBytes").GetInt32() && bytes.Length <= limits.GetProperty("chunkBytes").GetInt32() && value.GetProperty("offset").GetInt64() + bytes.Length <= value.GetProperty("totalBytes").GetInt64() && value.GetProperty("totalBytes").GetInt64() <= limits.GetProperty("attachmentBytes").GetInt64() && WireJson.Sha256(bytes) == J.Text(value, "chunkSha256"));
            if (value.GetProperty("offset").GetInt32() == 0 && bytes.Length == value.GetProperty("totalBytes").GetInt32()) J.Need(J.Text(value, "sha256") == J.Text(value, "chunkSha256"));
        }
        if (schema == "MutationReceipt")
        {
            J.Need(J.Equal(value.GetProperty("request"), input.GetProperty("request")));
            if (input.TryGetProperty("operation", out var operation)) J.Need(J.Text(value, "operation") == operation.GetString()); else J.VerifyOperation(input, value, scope, input.TryGetProperty("coverage", out _) ? "archive-ack" : "binding-close");
        }
        if (schema == "MaterialUploadStatus") VerifyUpload(value, input, limits);
        if (schema == "MaterialReceipt")
        {
            J.Unique(value.GetProperty("acceptedRecordIds")); string state = J.Text(value, "state");
            if (state == "pending") J.Need(J.Text(value, "revision") == "0" && value.GetProperty("acceptedRecordIds").GetArrayLength() == 0);
            if (input.TryGetProperty("results", out var results)) { var ids = results.EnumerateArray().Select(x => J.Text(x, "recordId")).ToArray(); J.Need(state == "received" && J.Text(value, "revision") != "0" && value.GetProperty("acceptedRecordIds").GetArrayLength() == ids.Length && value.GetProperty("acceptedRecordIds").EnumerateArray().All(x => ids.Contains(x.GetString()!))); }
        }
    }
    internal static void VerifyUpload(JsonElement value, JsonElement request, JsonElement limits)
    {
        var artifact = value.GetProperty("artifact"); int width = value.GetProperty("chunkBytes").GetInt32(), total = artifact.GetProperty("bytes").GetInt32();
        J.Need(J.Text(artifact, "artifactId") == J.Text(request, "artifactId") && total <= limits.GetProperty("materialBytes").GetInt32() && width <= limits.GetProperty("materialChunkBytes").GetInt32() && (total + width - 1) / width <= 16);
        if (request.TryGetProperty("sourceId", out var source)) J.Need(J.Text(artifact, "sourceId") == source.GetString()); int previous = -1, count = 0;
        foreach (var item in value.GetProperty("receivedOffsets").EnumerateArray()) { int offset = item.GetInt32(); J.Need(offset > previous && offset % width == 0 && offset < total); previous = offset; count += Math.Min(width, total - offset); }
        J.Need(count == value.GetProperty("receivedBytes").GetInt32() && (J.Text(value, "state") == "committed") == (count == total));
        if (request.TryGetProperty("offset", out var asked))
        {
            J.Need(asked.GetInt32() % width == 0 && value.GetProperty("receivedOffsets").EnumerateArray().Any(x => x.GetInt32() == asked.GetInt32()) && request.GetProperty("bytes").GetInt32() == Math.Min(width, total - asked.GetInt32()));
            if (asked.GetInt32() == 0 && request.GetProperty("bytes").GetInt32() == total) J.Need(J.Text(artifact, "sha256") == J.Text(request, "chunkSha256"));
        }
    }
    private static void ValidateLimits(JsonElement value) { WireJson.ValidateNamed("Limits", value); J.Need(value.GetProperty("inflightReserveBytes").GetInt64() <= value.GetProperty("pendingBytes").GetInt64()); }
    private static void Epoch(JsonElement value, long? maximum) { if (value.ValueKind == JsonValueKind.Null) return; double duration = (DateTimeOffset.Parse(J.Text(value, "expiresAt"), CultureInfo.InvariantCulture) - DateTimeOffset.Parse(J.Text(value, "issuedAt"), CultureInfo.InvariantCulture)).TotalMilliseconds; J.Need(duration > 0 && (!maximum.HasValue || duration <= maximum.Value)); }
    private static JsonElement Intersect(JsonElement local, JsonElement? discovered, JsonElement? binding) => J.Build(w => { foreach (var item in local.EnumerateObject()) { long value = item.Value.GetInt64(); if (discovered.HasValue) value = Math.Min(value, discovered.Value.GetProperty(item.Name).GetInt64()); if (binding.HasValue) value = Math.Min(value, binding.Value.GetProperty(item.Name).GetInt64()); w.WriteNumber(item.Name, value); } });
    private static string Material(ApiOperation operation, JsonElement value, string? uploadId = null) => operation.Path(id: J.Text(value, "bindingId"), targetId: J.Text(value, "materialRequestId"), uploadId: uploadId);
    private static List<(string Name, object Value)> Generations(JsonElement value)
    { var generation = value.GetProperty("generations"); return new List<(string, object)> { ("protocol", J.Protocol), ("historyEpoch", J.Text(generation, "historyEpoch")), ("deletionGeneration", J.Text(generation, "deletionGeneration")), ("projectionRevision", J.Text(generation, "projectionRevision")) }; }
    internal static JsonElement MaximumLimits() => J.Build(w => { w.WriteNumber("controlBytes", 262144); w.WriteNumber("recordBytes", 262144); w.WriteNumber("pageRecords", 128); w.WriteNumber("pageBytes", 1048576); w.WriteNumber("attachmentBytes", 33554432); w.WriteNumber("chunkBytes", 262144); w.WriteNumber("materialConcurrent", 2); w.WriteNumber("materialQueue", 16); w.WriteNumber("materialCandidates", 32); w.WriteNumber("materialBytes", 1048576); w.WriteNumber("materialDeadlineMs", 30000); w.WriteNumber("pendingRecords", 4096); w.WriteNumber("pendingBytes", 67108864); w.WriteNumber("inflightReserveBytes", 16777216); w.WriteNumber("offlineMs", 86400000); w.WriteNumber("eventRetentionMs", 600000); w.WriteNumber("eventRetentionFrames", 4096); w.WriteNumber("eventRetentionBytes", 8388608); w.WriteNumber("terminalReceiptRetentionMs", 604800000); w.WriteNumber("epochLifetimeMs", 86400000); w.WriteNumber("materialChunkBytes", 65536); });
}
