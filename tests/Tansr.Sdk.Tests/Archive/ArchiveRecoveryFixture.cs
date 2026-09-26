using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Archive.Replication;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using static Tansr.Sdk.Tests.Archive.ArchiveFlowFixture;

namespace Tansr.Sdk.Tests.Archive;

internal sealed class ArchiveRecoveryFixture
{
    internal ArchiveFlowFixture Data { get; } = new();
    internal List<string> Events { get; } = new();
    internal JsonElement Request => Element(new { operationEpoch = "epoch", requestId = "recovery" });
    internal JsonElement Limits => Element(new { maxRecords = 100, maxArtifacts = 100, maxStoredBytes = 1048576, maxBatchBytes = 65536 });
    internal JsonElement Intent(JsonElement request) => Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", previous = Data.Ack(Data.RequestIdentity), request });
    internal JsonElement Result(JsonElement intent)
    {
        var next = Set(Set(intent.GetProperty("previous"), "request", intent.GetProperty("request")), "expectedRevision", "6");
        return Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", previous = intent.GetProperty("previous"), request = intent.GetProperty("request"), next, receipt = Data.Receipt(next, "archive-ack") });
    }
    internal ArchiveRecoverySession Session(RecoveryStore store, RecoveryClient client) => new(client, store, Data.Identity, () => Data.Scope);
    internal ReplicatedArchiveStore Group(RecoveryStore primary, RecoveryStore replica) => new(new ReplicatedArchiveStoreOptions { Primary = primary, Replica = replica, ReplicationId = "group", Identity = Data.Identity, Limits = Limits, ReadContext = () => Data.Scope });
    internal static string Text(JsonElement value) => WireJson.CanonicalString(value, 1048576);

    internal sealed class RecoveryStore : IRecoverableArchiveStore, IReplicaArchiveStore
    {
        private readonly ArchiveRecoveryFixture _f;
        private readonly ArchiveFlowFixture.Store _records;
        internal string Role { get; }
        internal JsonElement Ack, SavedReceipt;
        internal JsonElement? Intent, Completed;
        internal bool Confirmed, FailPrepare, FailConfirm, CorruptBody;
        internal Action? OnPrepare;
        internal int Prepares, Confirms, OriginalConfirms;
        internal RecoveryStore(ArchiveRecoveryFixture f, string role = "primary") { _f = f; Role = role; Ack = f.Data.Ack(f.Data.RequestIdentity); _records = new(f.Data, true); }
        public Task<JsonElement> PrepareAckRebaseAsync(JsonElement request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); _f.Events.Add(Role + ":prepare"); Prepares++;
            if (FailPrepare) throw new IOException("synthetic durable prepare failure");
            if (Intent.HasValue) { if (Text(Intent.Value.GetProperty("request")) != Text(request)) throw new StorageException("pending_ack"); }
            else { Assert.False(Confirmed); Intent = _f.Intent(request); }
            OnPrepare?.Invoke(); return Task.FromResult(Intent.Value);
        }
        public Task<JsonElement?> PendingAckRebaseAsync(CancellationToken cancellationToken = default) => Task.FromResult(Confirmed ? null : Intent);
        public Task ConfirmAckRebaseAsync(JsonElement receipt, CancellationToken cancellationToken = default)
        {
            _f.Events.Add(Role + ":confirm"); Confirms++; Assert.NotNull(Intent);
            ArchiveRecoveryContract.Receipt(receipt, Intent!.Value, _f.Data.Scope);
            if (FailConfirm) throw new IOException("synthetic confirm failure");
            Ack = receipt.GetProperty("next").Clone(); SavedReceipt = receipt.GetProperty("receipt").Clone(); Completed = receipt; Confirmed = true; return Task.CompletedTask;
        }
        public Task<JsonElement?> ReplicaOperationAsync(JsonElement requestIdentity, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(Text(Ack.GetProperty("request")) != Text(requestIdentity) ? null : Element(new { ack = Ack, receipt = Confirmed ? (JsonElement?)SavedReceipt : null }));
        public Task<JsonElement> ReplicaIdentityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Element(new { replica = new { replicationId = "group", role = Role }, receiver = _f.Data.Identity, limits = _f.Limits }));
        public Task ConfirmAsync(JsonElement receipt, CancellationToken cancellationToken = default) { OriginalConfirms++; _f.Events.Add(Role + ":original-confirm"); SavedReceipt = receipt; Confirmed = true; return Task.CompletedTask; }
        public Task<JsonElement?> PendingAsync(CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(Confirmed ? null : Ack);
        public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) => _records.HeadAsync(cancellationToken);
        public Task<byte[]> BodyAsync(JsonElement artifactReference, CancellationToken cancellationToken = default) => CorruptBody ? Task.FromResult(new byte[] { 1 }) : _records.BodyAsync(artifactReference, cancellationToken);
        public Task<JsonElement> ReceiveAsync(ArchiveReceiveInput input, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Recovery must not receive again.");
        public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default) => _records.ReadRecordsAsync(request, cancellationToken);
        public Task<JsonElement> CoverageAsync(CancellationToken cancellationToken = default) => _records.CoverageAsync(cancellationToken);
        public Task CloseAsync() => Task.CompletedTask;
        public Task ApplyRetentionAsync(JsonElement retention, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult("0");
        public Task<JsonElement?> RetentionPageAsync(string afterRevision, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
        public Task<byte[]> BodyChunkAsync(JsonElement artifactReference, long offset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class RecoveryClient(ArchiveRecoveryFixture f) : IArchiveRecoveryClient
    {
        internal Func<JsonElement, JsonElement>? OnRebase;
        internal Func<JsonElement>? OnQuery;
        internal readonly List<JsonElement> Requests = new();
        public Task<JsonElement> RebaseAckAsync(JsonElement request, CancellationToken cancellationToken = default)
        { f.Events.Add("http:rebase"); Requests.Add(request.Clone()); return Task.FromResult(OnRebase?.Invoke(request) ?? f.Result(request)); }
        public Task<JsonElement> GetOperationAsync(JsonElement request, CancellationToken cancellationToken = default)
        { f.Events.Add("http:query"); Assert.Equal(Text(f.Data.RequestIdentity), Text(request.GetProperty("request"))); return Task.FromResult(OnQuery?.Invoke() ?? throw new TansrHttpException(410, "receipt_expired")); }
        public JsonElement ReadScope() => f.Data.Scope;
        public JsonElement GetEffectiveLimits(string bindingId) => f.Data.Limits;
        public Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GetBindingTargetAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> CreateBindingAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GetBindingAsync(string bindingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GetArchiveStatusAsync(string bindingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> ReadRecordsAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> ReadArtifactAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> AcknowledgeAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> UploadMaterialChunkAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GetMaterialUploadStatusAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> RespondMaterialsAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GetMaterialStatusAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> CloseBindingAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
