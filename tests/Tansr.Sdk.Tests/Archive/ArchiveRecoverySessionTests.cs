using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Storage;
using static Tansr.Sdk.Tests.Archive.ArchiveFlowFixture;
using static Tansr.Sdk.Tests.Archive.ArchiveRecoveryFixture;

namespace Tansr.Sdk.Tests.Archive;

public sealed class ArchiveRecoverySessionTests
{
    [Fact]
    public async Task LostResponseResumesTheDurableIntentWithExactlyTheSameRecoveryKey()
    {
        var f = new ArchiveRecoveryFixture(); var store = new RecoveryStore(f); var client = new RecoveryClient(f) { OnRebase = _ => throw new IOException("lost HTTP response") };
        await Assert.ThrowsAsync<IOException>(() => f.Session(store, client).RecoverAsync(f.Request));
        Assert.Equal(new[] { "http:query", "primary:prepare", "http:rebase" }, f.Events); Assert.Equal(Text(f.Data.Ack(f.Data.RequestIdentity)), Text((await store.PendingAsync())!.Value));
        client.OnRebase = null; var result = await f.Session(store, client).ResumeAsync();
        Assert.Equal(ArchiveAcknowledgementRecoveryOutcome.Rebased, result!.Outcome); Assert.Equal(2, client.Requests.Count);
        Assert.Equal(Text(client.Requests[0]), Text(client.Requests[1])); Assert.Equal(1, store.Confirms); Assert.Null(await store.PendingAckRebaseAsync()); Assert.Null(await f.Session(store, client).ResumeAsync());
    }

    [Fact]
    public async Task AlreadyAcceptedOriginalUsesItsReceiptWithoutPreparingOrRebasing()
    {
        var f = new ArchiveRecoveryFixture(); var store = new RecoveryStore(f); var receipt = f.Data.Receipt(store.Ack, "archive-ack"); var client = new RecoveryClient(f) { OnQuery = () => receipt };
        var result = await f.Session(store, client).RecoverAsync(f.Request);
        Assert.Equal(ArchiveAcknowledgementRecoveryOutcome.OriginalConfirmed, result.Outcome); Assert.Null(result.RecoveryReceipt); Assert.Equal(0, store.Prepares); Assert.Empty(client.Requests); Assert.Equal(1, store.OriginalConfirms);
    }

    [Theory]
    [InlineData("binding_conflict")]
    [InlineData("request_id_conflict")]
    public async Task OriginalMayCompleteBetweenQueryAndRebaseAndIsReconciled(string code)
    {
        var f = new ArchiveRecoveryFixture(); var store = new RecoveryStore(f); bool completed = false;
        var client = new RecoveryClient(f) { OnQuery = () => completed ? f.Data.Receipt(store.Ack, "archive-ack") : throw new TansrHttpException(410, "receipt_expired"), OnRebase = _ => { completed = true; throw new TansrHttpException(409, code); } };
        var result = await f.Session(store, client).RecoverAsync(f.Request);
        Assert.Equal(ArchiveAcknowledgementRecoveryOutcome.OriginalConfirmed, result.Outcome); Assert.Equal(1, store.OriginalConfirms); Assert.Equal(0, store.Confirms); Assert.Null(await store.PendingAckRebaseAsync());
    }

    [Fact]
    public async Task UnknownOriginalQueryDoesNotTreatNetworkFailureAsProofOfAbsence()
    {
        var f = new ArchiveRecoveryFixture(); var store = new RecoveryStore(f); var client = new RecoveryClient(f) { OnQuery = () => throw new TansrHttpException(503, "source_unavailable") };
        await Assert.ThrowsAsync<TansrHttpException>(() => f.Session(store, client).RecoverAsync(f.Request)); Assert.Equal(0, store.Prepares); Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PreparedRecoveryCannotBeReplacedWithAnotherKey()
    {
        var f = new ArchiveRecoveryFixture(); var store = new RecoveryStore(f); await store.PrepareAckRebaseAsync(f.Request); var client = new RecoveryClient(f);
        Assert.Equal("pending_ack", (await Assert.ThrowsAsync<TansrProtocolException>(() => f.Session(store, client).RecoverAsync(Set(f.Request, "requestId", "other")))).Code); Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("next")]
    [InlineData("receipt")]
    [InlineData("request")]
    public async Task ChangedRecoveryEvidenceNeverConfirmsTheOriginalBatch(string field)
    {
        var f = new ArchiveRecoveryFixture(); var store = new RecoveryStore(f); var client = new RecoveryClient(f)
        {
            OnRebase = intent => { var result = f.Result(intent); return field switch { "next" => Set(result, "next", Set(result.GetProperty("next"), "sourceId", "other")), "receipt" => Set(result, "receipt", Set(result.GetProperty("receipt"), "semanticDigest", new string('f', 64))), _ => Set(result, "request", Set(f.Request, "requestId", "other")) }; }
        };
        await Assert.ThrowsAsync<TansrProtocolException>(() => f.Session(store, client).RecoverAsync(f.Request)); Assert.Equal(0, store.Confirms); Assert.NotNull(await store.PendingAckRebaseAsync());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("context")]
    [InlineData("reentrant")]
    public async Task FinalUserCallbackCannotBypassCancelContextOrReentrancyBeforeHttp(string mode)
    {
        var f = new ArchiveRecoveryFixture(); var store = new RecoveryStore(f); var client = new RecoveryClient(f); var session = f.Session(store, client); using var cancellation = new CancellationTokenSource();
        store.OnPrepare = () => { if (mode == "cancel") cancellation.Cancel(); else if (mode == "context") f.Data.User = "other"; else { try { session.ResumeAsync().GetAwaiter().GetResult(); } catch (StorageException) { } } };
        var error = await Record.ExceptionAsync(() => session.RecoverAsync(f.Request, cancellation.Token)); Assert.NotNull(error);
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error); else if (mode == "context") Assert.Equal("context_changed", Assert.IsType<TansrProtocolException>(error).Code); else Assert.Equal("reentrant", Assert.IsType<TansrProtocolException>(error).Code);
        Assert.Empty(client.Requests); Assert.Equal(0, store.Confirms); Assert.NotNull(store.Intent);
    }
}
