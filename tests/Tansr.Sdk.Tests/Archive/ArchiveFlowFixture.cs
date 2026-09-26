using System.Text;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Tests.Archive;

internal sealed class ArchiveFlowFixture
{
    internal static JsonElement Element(object value) => WireJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value), 1048576);
    internal static JsonElement Set(JsonElement value, string name, object? replacement)
    { var copy = value.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()); copy[name] = replacement; return Element(copy); }
    internal string User { get; set; } = "user";
    internal JsonElement Scope => Element(new { applicationScopeId = "app", endUserId = User, authorizationRevision = "1" });
    internal byte[] Body { get; } = Encoding.UTF8.GetBytes("Original terminal archive 中文 😀");
    internal JsonElement Identity { get; }
    internal JsonElement Target { get; }
    internal JsonElement Reference { get; }
    internal JsonElement Record { get; }
    internal JsonElement Limits { get; }
    internal JsonElement Binding { get; }
    internal JsonElement Status { get; }
    internal JsonElement Page { get; }
    internal JsonElement MaterialRequest { get; }
    internal JsonElement RequestIdentity => Element(new { operationEpoch = "epoch", requestId = "fixed-request" });
    internal ArchiveFlowFixture()
    {
        var generations = new { historyEpoch = "history", deletionGeneration = "0", projectionRevision = "1" };
        Target = Element(new { sessionId = "session/中文", generations, sourceSnapshotDigest = new string('a', 64) });
        Identity = Element(new { scope = new { applicationScopeId = "app", endUserId = "user" }, bindingId = "binding", target = new { sessionId = "session/中文", generations }, sourceId = "source", sourceGeneration = "source-generation" });
        Reference = Element(new { artifactId = "artifact", sourceId = "source", bytes = Body.Length, sha256 = WireJson.Sha256(Body), mediaType = "text/plain" });
        var record = Element(new { recordId = "record", sequence = "1", target = Target, turnId = "turn", recordKind = "turn", turnState = "completed", predecessorDigest = new string('0', 64), payload = Reference, attachments = Array.Empty<object>(), payloadDigest = WireJson.DomainDigest("tansr.sdk2.payload.v1", Body) });
        Record = Set(record, "recordDigest", WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(record)));
        Limits = Element(new { controlBytes = 262144, recordBytes = 262144, pageRecords = 128, pageBytes = 1048576, attachmentBytes = 33554432, chunkBytes = 262144, materialConcurrent = 2, materialQueue = 16, materialCandidates = 32, materialBytes = 1048576, materialDeadlineMs = 30000, pendingRecords = 4096, pendingBytes = 67108864, inflightReserveBytes = 16777216, offlineMs = 86400000, eventRetentionMs = 600000, eventRetentionFrames = 4096, eventRetentionBytes = 8388608, terminalReceiptRetentionMs = 604800000, epochLifetimeMs = 86400000, materialChunkBytes = 65536 });
        Binding = Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", scope = Scope, target = Target, revision = "2", state = "active", sourceId = "source", acceptedCapabilities = new[] { "archive-transfer-v1", "context-materials-v1" }, rejectedCapabilities = Array.Empty<object>(), availability = "legacy-complete", operationEpoch = new { id = "epoch", issuedAt = "2026-09-26T00:00:00Z", expiresAt = "2026-09-26T00:01:00Z", state = "active" }, limits = Limits, archiveAckFormat = "split-receipts-v1" });
        Status = Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", revision = "2", generations, sourceId = "source", sourceGeneration = "source-generation", publishedThroughSequence = "1", acknowledgedCoverage = (object?)null, releasableThroughSequence = (object?)null, pendingBytes = Body.Length, pendingRecords = 1, sessionPersistence = "unchanged", state = "active" });
        Page = Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", generations, records = new[] { Record }, nextAfterSequence = "1", complete = true, publishedThroughSequence = "1" });
        MaterialRequest = Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", materialRequestId = "material", target = Target, sourceId = "source", sourceGeneration = "source-generation", requestedRecords = new[] { new { recordId = "record", digest = Record.GetProperty("recordDigest").GetString(), payload = Reference, attachments = Array.Empty<object>() } }, purpose = "context-recall", maxBytes = 1024, remainingTtlMs = 30000, chunkBytes = 8 });
    }
    internal JsonElement Ack(JsonElement request) => Element(new { protocol = "sdk2-ext-v1", ackFormat = "split-receipts-v1", request, bindingId = "binding", expectedRevision = "2", generations = Target.GetProperty("generations"), sourceId = "source", sourceGeneration = "source-generation", coverage = new { fromSequence = "1", throughSequence = "1", headDigest = Record.GetProperty("recordDigest").GetString() }, payloads = new[] { new { artifactId = "artifact", sha256 = Reference.GetProperty("sha256").GetString(), state = "durably-stored" } }, attachments = Array.Empty<object>() });
    internal JsonElement Receipt(JsonElement request, string operation)
    {
        var semantic = Element(request.EnumerateObject().Where(p => p.Name != "request").ToDictionary(p => p.Name, p => p.Value.Clone()));
        return Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", request = request.GetProperty("request"), operation, semanticDigest = WireJson.DomainDigest("tansr.sdk2.operation.v1", WireJson.EncodeControl(Element(new { scope = new[] { "app", "user" }, operation, semantic }), 1048576)), state = operation == "archive-ack" ? "completed" : "accepted", revision = operation == "archive-ack" ? (Sequence.Parse(request.GetProperty("expectedRevision").GetString()!).ToInt64() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "3", outcomeRef = operation == "archive-ack" ? "binding" : "material" });
    }
    internal JsonElement MaterialReceipt(string state = "received") => Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", materialRequestId = "material", state, revision = "1", acceptedRecordIds = new[] { "record" } });
    internal JsonElement Chunk(JsonElement request)
    {
        int offset = request.GetProperty("offset").GetInt32(), count = Math.Min(Body.Length - offset, request.GetProperty("maxBytes").GetInt32()); byte[] bytes = Body.Skip(offset).Take(count).ToArray();
        return Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", artifactId = "artifact", sourceId = "source", generations = Target.GetProperty("generations"), offset, bytes = count, totalBytes = Body.Length, sha256 = Reference.GetProperty("sha256").GetString(), chunkSha256 = WireJson.Sha256(bytes), base64 = Convert.ToBase64String(bytes) });
    }
    internal sealed class Store : IArchiveStore
    {
        private readonly ArchiveFlowFixture _data;
        internal bool HasRecord { get; set; }
        internal JsonElement? Pending { get; set; }
        internal Action? OnReceive { get; set; }
        internal Action? OnBody { get; set; }
        internal bool FailReceive { get; set; }
        internal int Receives { get; private set; }
        internal Store(ArchiveFlowFixture data, bool hasRecord = false) { _data = data; HasRecord = hasRecord; }
        public Task<JsonElement> ReceiveAsync(ArchiveReceiveInput input, CancellationToken cancellationToken = default)
        {
            if (FailReceive) throw new StorageException("storage_error"); Receives++; Assert.Equal(_data.Body, Assert.Single(input.Artifacts).Body); Pending = Set(_data.Ack(input.Request), "expectedRevision", input.Binding.GetProperty("revision")); HasRecord = true; OnReceive?.Invoke(); return Task.FromResult(Pending.Value);
        }
        public Task<JsonElement?> PendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(Pending);
        public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(HasRecord ? Element(new { sequence = "1", recordDigest = _data.Record.GetProperty("recordDigest").GetString() }) : null);
        public Task<byte[]> BodyAsync(JsonElement artifactReference, CancellationToken cancellationToken = default) { OnBody?.Invoke(); return Task.FromResult((byte[])_data.Body.Clone()); }
        public Task ConfirmAsync(JsonElement receipt, CancellationToken cancellationToken = default) { Assert.NotNull(Pending); Pending = null; return Task.CompletedTask; }
        public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ArchiveRecordPage(HasRecord ? new[] { _data.Record } : Array.Empty<JsonElement>(), HasRecord ? WireJson.EncodeControl(_data.Record).Length : 0, HasRecord ? Array.Empty<string>() : new[] { "record" }, null, Coverage()));
        public Task<JsonElement> CoverageAsync(CancellationToken cancellationToken = default) => Task.FromResult(Coverage());
        private JsonElement Coverage() => Element(new { sourceId = "source", sourceGeneration = "source-generation", fromSequence = HasRecord ? "1" : null, throughSequence = HasRecord ? "1" : null, headDigest = HasRecord ? _data.Record.GetProperty("recordDigest").GetString() : null, complete = HasRecord });
        public Task CloseAsync() => Task.CompletedTask;
    }
    internal sealed class Client : IArchiveClient
    {
        private readonly ArchiveFlowFixture _data;
        internal readonly List<string> Calls = new();
        internal JsonElement? LastAck, LastMaterialResponse;
        internal Func<JsonElement, JsonElement>? AlterChunk;
        internal Func<JsonElement, JsonElement>? AlterOperation;
        internal Func<JsonElement, JsonElement>? AlterBinding, AlterStatus;
        internal Action? OnAck;
        internal bool LoseAck, LoseMaterial;
        internal Action? OnRespond;
        private readonly HashSet<int> _uploaded = new();
        internal Client(ArchiveFlowFixture data) => _data = data;
        public JsonElement ReadScope() => _data.Scope;
        public JsonElement GetEffectiveLimits(string bindingId) => _data.Limits;
        public Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GetBindingTargetAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> CreateBindingAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GetBindingAsync(string bindingId, CancellationToken cancellationToken = default) { Calls.Add("binding"); return Task.FromResult(AlterBinding?.Invoke(_data.Binding) ?? _data.Binding); }
        public Task<JsonElement> GetArchiveStatusAsync(string bindingId, CancellationToken cancellationToken = default) { Calls.Add("status"); return Task.FromResult(AlterStatus?.Invoke(_data.Status) ?? _data.Status); }
        public Task<JsonElement> ReadRecordsAsync(JsonElement request, CancellationToken cancellationToken = default) { Calls.Add("records"); return Task.FromResult(_data.Page); }
        public Task<JsonElement> ReadArtifactAsync(JsonElement request, CancellationToken cancellationToken = default) { Calls.Add("artifact"); var chunk = _data.Chunk(request); return Task.FromResult(AlterChunk?.Invoke(chunk) ?? chunk); }
        public Task<JsonElement> AcknowledgeAsync(JsonElement request, CancellationToken cancellationToken = default)
        { Calls.Add("ack"); LastAck = request.Clone(); OnAck?.Invoke(); if (LoseAck) throw new IOException("synthetic lost response"); return Task.FromResult(_data.Receipt(request, "archive-ack")); }
        public Task<JsonElement> GetOperationAsync(JsonElement request, CancellationToken cancellationToken = default)
        { Calls.Add("operation"); string operation = request.GetProperty("operation").GetString()!; var receipt = _data.Receipt(operation == "archive-ack" ? LastAck!.Value : LastMaterialResponse!.Value, operation); return Task.FromResult(AlterOperation?.Invoke(receipt) ?? receipt); }
        public Task<JsonElement> UploadMaterialChunkAsync(JsonElement request, CancellationToken cancellationToken = default)
        {
            Calls.Add("upload"); int offset = request.GetProperty("offset").GetInt32(); _uploaded.Add(offset); int bytes = _uploaded.Sum(n => Math.Min(8, _data.Body.Length - n));
            return Task.FromResult(Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", materialRequestId = "material", uploadId = "upload", artifact = _data.Reference, state = bytes == _data.Body.Length ? "committed" : "receiving", chunkBytes = 8, receivedOffsets = _uploaded.OrderBy(n => n).ToArray(), receivedBytes = bytes, remainingTtlMs = 20000 }));
        }
        public Task<JsonElement> GetMaterialUploadStatusAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> RespondMaterialsAsync(JsonElement request, CancellationToken cancellationToken = default)
        { Calls.Add("respond"); LastMaterialResponse = request.Clone(); OnRespond?.Invoke(); if (LoseMaterial) throw new IOException("synthetic lost response"); return Task.FromResult(_data.MaterialReceipt()); }
        public Task<JsonElement> GetMaterialStatusAsync(JsonElement request, CancellationToken cancellationToken = default) { Calls.Add("material-status"); return Task.FromResult(_data.MaterialReceipt("core-consumed")); }
        public Task<JsonElement> CloseBindingAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
