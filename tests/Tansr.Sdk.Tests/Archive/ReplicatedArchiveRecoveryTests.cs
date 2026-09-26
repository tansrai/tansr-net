using Tansr.Sdk.Client;
using Tansr.Sdk.Storage;
using static Tansr.Sdk.Tests.Archive.ArchiveRecoveryFixture;

namespace Tansr.Sdk.Tests.Archive;

public sealed class ReplicatedArchiveRecoveryTests
{
    [Fact]
    public async Task FirstPrepareVerifiesBothDurableBodiesBeforeCreatingAnyIntent()
    {
        var f = new ArchiveRecoveryFixture(); var a = new RecoveryStore(f); var b = new RecoveryStore(f, "replica") { CorruptBody = true }; var group = f.Group(a, b);
        try
        {
            await Assert.ThrowsAsync<StorageException>(() => group.PrepareAckRebaseAsync(f.Request));
            Assert.Equal(0, a.Prepares); Assert.Equal(0, b.Prepares); Assert.Null(a.Intent); Assert.Null(b.Intent);
        }
        finally { await group.CloseAsync(); }
    }

    [Fact]
    public async Task PartialPrepareKeepsTheOnlyRecoveryKeyAndMustFinishBothBeforeReturning()
    {
        var f = new ArchiveRecoveryFixture(); var a = new RecoveryStore(f); var b = new RecoveryStore(f, "replica") { FailPrepare = true }; var group = f.Group(a, b);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => group.PrepareAckRebaseAsync(f.Request)); Assert.NotNull(a.Intent); Assert.Null(b.Intent);
            Assert.Equal(Text(a.Intent!.Value), Text((await group.PendingAckRebaseAsync())!.Value)); b.FailPrepare = false;
            Assert.Equal(Text(a.Intent.Value), Text(await group.PrepareAckRebaseAsync(f.Request))); Assert.Equal(Text(a.Intent.Value), Text(b.Intent!.Value));
        }
        finally { await group.CloseAsync(); }
    }

    [Fact]
    public async Task OneConfirmedReplicaRetainsSameIntentUntilOtherSideCommits()
    {
        var f = new ArchiveRecoveryFixture(); var a = new RecoveryStore(f); var b = new RecoveryStore(f, "replica") { FailConfirm = true }; var group = f.Group(a, b);
        try
        {
            var intent = await group.PrepareAckRebaseAsync(f.Request); var proof = f.Result(intent);
            await Assert.ThrowsAsync<IOException>(() => group.ConfirmAckRebaseAsync(proof)); Assert.True(a.Confirmed); Assert.False(b.Confirmed);
            Assert.Equal(Text(intent), Text((await group.PendingAckRebaseAsync())!.Value)); await Assert.ThrowsAsync<StorageException>(() => group.PendingAsync());
            b.FailConfirm = false; Assert.Equal(Text(intent), Text(await group.PrepareAckRebaseAsync(f.Request))); await group.ConfirmAckRebaseAsync(proof);
            Assert.Null(await group.PendingAckRebaseAsync()); Assert.Null(await group.PendingAsync()); await group.ConfirmAckRebaseAsync(proof); Assert.Equal(Text(a.Ack), Text(b.Ack));
        }
        finally { await group.CloseAsync(); }
    }

    [Fact]
    public async Task ConfirmCannotInventAnIntentOrPrepareTheOtherSideAfterHttp()
    {
        var f = new ArchiveRecoveryFixture(); var a = new RecoveryStore(f); var b = new RecoveryStore(f, "replica"); var group = f.Group(a, b);
        try
        {
            await Assert.ThrowsAsync<StorageException>(() => group.ConfirmAckRebaseAsync(f.Result(f.Intent(f.Request)))); Assert.Equal(0, a.Prepares); Assert.Equal(0, b.Prepares);
            await a.PrepareAckRebaseAsync(f.Request); await Assert.ThrowsAsync<StorageException>(() => group.ConfirmAckRebaseAsync(f.Result(a.Intent!.Value)));
            Assert.Equal(0, a.Confirms); Assert.Equal(0, b.Prepares);
        }
        finally { await group.CloseAsync(); }
    }

    [Fact]
    public async Task ChangedReplicaBodyPreventsBothCommits()
    {
        var f = new ArchiveRecoveryFixture(); var a = new RecoveryStore(f); var b = new RecoveryStore(f, "replica"); var group = f.Group(a, b);
        try
        {
            var intent = await group.PrepareAckRebaseAsync(f.Request); b.CorruptBody = true;
            await Assert.ThrowsAsync<StorageException>(() => group.ConfirmAckRebaseAsync(f.Result(intent))); Assert.Equal(0, a.Confirms); Assert.Equal(0, b.Confirms);
        }
        finally { await group.CloseAsync(); }
    }
}
