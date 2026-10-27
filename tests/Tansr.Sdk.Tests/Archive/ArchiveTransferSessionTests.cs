using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Tests.Archive;

public sealed class ArchiveTransferSessionTests
{
    [Theory]
    [InlineData("terminal", "terminal-durable-ack")]
    [InlineData("cloud", "cloud-immutable-publish")]
    public async Task SourceManagedArchiveKeepsDurableReceiveAndOriginalAcknowledgement(string mode, string handoff)
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data);
        var persistence = ArchiveFlowFixture.Element(new { contract = "serve-persistence-v1", mode, handoff, sourceId = "source", sourceGeneration = "source-generation", localDisk = false, trustedControl = true });
        client.AlterBinding = value => ArchiveFlowFixture.Set(ArchiveFlowFixture.Set(value, "availability", "source-managed"), "persistence", persistence);
        client.AlterStatus = value => ArchiveFlowFixture.Set(ArchiveFlowFixture.Set(value, "sessionPersistence", "source-managed"), "persistence", persistence);
        store.OnReceive = () => Assert.DoesNotContain("ack", client.Calls);
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity);
        var result = await session.PullAsync(); Assert.Equal(1, result.ArchivedRecords); Assert.True(result.Complete); Assert.Single(result.Receipts); Assert.Null(store.Pending);
        Assert.Equal(WireJson.CanonicalString(data.Ack(data.RequestIdentity)), WireJson.CanonicalString(client.LastAck!.Value));
        // 与 Windows SQLite、云副本接收器相同的真实准备器；不能只验证 IArchiveStore 测试替身。
        var input = new ArchiveReceiveInput
        {
            Binding = client.AlterBinding(data.Binding),
            Status = client.AlterStatus(data.Status),
            Page = data.Page,
            Request = data.RequestIdentity,
            Artifacts = new[] { new ArchiveArtifact("artifact", data.Body) }
        };
        var batch = Tansr.Sdk.Archive.Replication.ArchiveReceiverValidation.Prepare(data.Identity, new ArchiveRecoveryFixture().Limits, null, input);
        Assert.Equal(WireJson.CanonicalString(data.Ack(data.RequestIdentity)), WireJson.CanonicalString(batch.Ack));
        Assert.Equal(data.Body, batch.Bodies["artifact"]);
        input.Status = data.Status;
        Assert.Equal("identity_mismatch", Assert.Throws<StorageException>(() => Tansr.Sdk.Archive.Replication.ArchiveReceiverValidation.Prepare(data.Identity, new ArchiveRecoveryFixture().Limits, null, input)).Code);
    }
    [Fact]
    public async Task MixedLegacyAndSourceManagedStatusCannotReachDurableReceiver()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data);
        var persistence = ArchiveFlowFixture.Element(new { contract = "serve-persistence-v1", mode = "cloud", handoff = "cloud-immutable-publish", sourceId = "source", sourceGeneration = "source-generation", localDisk = false, trustedControl = true });
        client.AlterBinding = value => ArchiveFlowFixture.Set(ArchiveFlowFixture.Set(value, "availability", "source-managed"), "persistence", persistence);
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<TansrProtocolException>(() => session.PullAsync())).Code);
        Assert.Equal(0, store.Receives); Assert.DoesNotContain("ack", client.Calls);
    }
    [Fact]
    public async Task RevisionAdvancedDuringDownloadIsRefreshedBeforeOriginalDurableWrite()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data); string revision = "2"; string? chosen = null;
        client.AlterChunk = chunk => { revision = "3"; return chunk; };
        client.AlterBinding = value => ArchiveFlowFixture.Set(value, "revision", revision); client.AlterStatus = value => ArchiveFlowFixture.Set(value, "revision", revision);
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, binding => { chosen = binding.GetProperty("revision").GetString(); return data.RequestIdentity; });
        var result = await session.PullAsync(); Assert.Equal(1, result.ArchivedRecords); Assert.Equal("3", chosen); Assert.Equal("3", client.LastAck!.Value.GetProperty("expectedRevision").GetString()); Assert.Equal(1, store.Receives); Assert.Null(store.Pending);
    }
    [Fact]
    public async Task DifferentPublishedSnapshotDuringRefreshDoesNotPersistOrMintRequest()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data); bool downloaded = false; int identities = 0;
        client.AlterChunk = chunk => { downloaded = true; return chunk; }; client.AlterStatus = status => downloaded ? ArchiveFlowFixture.Set(status, "publishedThroughSequence", "2") : status;
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => { identities++; return data.RequestIdentity; });
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => session.PullAsync()); Assert.Equal("context_changed", error.Code); Assert.Equal(0, identities); Assert.Equal(0, store.Receives); Assert.DoesNotContain("ack", client.Calls);
    }
    [Fact]
    public async Task RevisionConflictAfterDurableWriteRetainsExactAckAndOnlyQueriesOnNextPull()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data); int identities = 0;
        client.OnAck = () => throw new TansrHttpException(409, "binding_conflict", retryAction: "none");
        client.AlterOperation = _ => throw new TansrHttpException(503, "source_unavailable", retryAction: "query-status");
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => { identities++; return data.RequestIdentity; });
        var conflict = await Assert.ThrowsAsync<ArchiveAcknowledgementConflictException>(() => session.PullAsync());
        Assert.Equal("binding_conflict", conflict.FailureCode); Assert.Equal(409, conflict.HttpStatusCode); Assert.Equal(WireJson.CanonicalString(store.Pending!.Value), WireJson.CanonicalString(conflict.PendingAcknowledgement)); string original = WireJson.CanonicalString(store.Pending.Value);
        Assert.Equal("fixed-request", conflict.PendingAcknowledgement.GetProperty("request").GetProperty("requestId").GetString()); Assert.Equal("epoch", conflict.PendingAcknowledgement.GetProperty("request").GetProperty("operationEpoch").GetString()); Assert.Equal("2", conflict.PendingAcknowledgement.GetProperty("expectedRevision").GetString());
        await Assert.ThrowsAsync<TansrHttpException>(() => session.PullAsync()); Assert.Equal(original, WireJson.CanonicalString(store.Pending.Value)); Assert.Equal(1, identities); Assert.Equal(1, store.Receives); Assert.Single(client.Calls, value => value == "ack"); Assert.Single(client.Calls, value => value == "operation");
        await Assert.ThrowsAsync<ArchiveAcknowledgementConflictException>(() => session.RecoverPendingAsync(retryOriginal: true)); Assert.Equal(original, WireJson.CanonicalString(store.Pending.Value)); Assert.Equal(1, identities);
    }
    [Fact]
    public async Task DownloadsDurablyStoresThenAcknowledgesOriginalOperation()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data);
        store.OnReceive = () => Assert.DoesNotContain("ack", client.Calls);
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity);
        var result = await session.PullAsync(); Assert.Equal(1, result.ArchivedRecords); Assert.True(result.Complete); Assert.Single(result.Receipts); Assert.Null(store.Pending); Assert.Equal(1, store.Receives);
    }
    [Fact]
    public async Task LostAckOnlyQueriesAndConfirmsTheOriginalDurableOperation()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data) { LoseAck = true };
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity);
        await Assert.ThrowsAsync<IOException>(() => session.PullAsync()); Assert.NotNull(store.Pending); client.LoseAck = false;
        Assert.NotNull(await session.RecoverPendingAsync()); Assert.Null(store.Pending); Assert.Single(client.Calls, c => c == "ack"); Assert.Single(client.Calls, c => c == "operation"); Assert.Equal(1, store.Receives);
    }
    [Fact]
    public async Task StatusWithWrongSemanticDigestDoesNotClearPending()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data) { Pending = data.Ack(data.RequestIdentity) }; var client = new ArchiveFlowFixture.Client(data) { LastAck = store.Pending, AlterOperation = r => ArchiveFlowFixture.Set(r, "semanticDigest", new string('f', 64)) };
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity);
        await Assert.ThrowsAsync<TansrProtocolException>(() => session.RecoverPendingAsync()); Assert.NotNull(store.Pending); Assert.DoesNotContain("ack", client.Calls);
    }
    [Fact]
    public async Task CorruptArtifactNeverReachesDurableReceiverOrAck()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data) { AlterChunk = r => ArchiveFlowFixture.Set(r, "chunkSha256", new string('f', 64)) };
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity);
        await Assert.ThrowsAsync<TansrProtocolException>(() => session.PullAsync()); Assert.Equal(0, store.Receives); Assert.DoesNotContain("ack", client.Calls);
    }
    [Fact]
    public async Task FailedDurableWriteNeverAcknowledgesUpstream()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data) { FailReceive = true }; var client = new ArchiveFlowFixture.Client(data);
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity);
        await Assert.ThrowsAsync<StorageException>(() => session.PullAsync()); Assert.DoesNotContain("ack", client.Calls);
    }
    [Fact]
    public async Task SubjectChangedByStoreKeepsPendingButDoesNotPostAck()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data) { OnReceive = () => data.User = "other" }; var client = new ArchiveFlowFixture.Client(data);
        var session = new ArchiveTransferSession(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity);
        await Assert.ThrowsAsync<TansrProtocolException>(() => session.PullAsync()); Assert.NotNull(store.Pending); Assert.DoesNotContain("ack", client.Calls);
    }
}
