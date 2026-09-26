using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Tests.Archive;

public sealed class MaterialSourceTests
{
    [Fact]
    public async Task UploadsOnlyRequestedObjectsThenPersistsResponseBeforeSubmission()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data); JsonElement? saved = null;
        client.OnRespond = () => Assert.NotNull(saved);
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, (response, _) => { saved = response.Clone(); return Task.CompletedTask; });
        var result = await source.RespondAsync(data.MaterialRequest); Assert.Equal("received", result.Receipt.GetProperty("state").GetString()); Assert.NotNull(result.Availability); Assert.Equal(new[] { "record" }, result.Availability.AvailableRecordIds); Assert.Single(client.Calls, c => c == "respond"); Assert.Equal((data.Body.Length + 7) / 8, client.Calls.Count(c => c == "upload"));
    }
    [Fact]
    public async Task MissingLocalRecordDoesNotBecomeAnEmptySuccessfulResponse()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data); var client = new ArchiveFlowFixture.Client(data);
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, (_, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<MaterialSourceException>(() => source.RespondAsync(data.MaterialRequest)); Assert.Equal("source_unavailable", error.Code); Assert.Equal(new[] { "record" }, error.UnavailableRecordIds); Assert.Empty(client.Calls);
    }
    [Fact]
    public async Task DeletionDuringHostPersistencePreventsSubmission()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data);
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, (_, _) => { store.HasRecord = false; return Task.CompletedTask; });
        await Assert.ThrowsAsync<TansrProtocolException>(() => source.RespondAsync(data.MaterialRequest)); Assert.DoesNotContain("respond", client.Calls);
    }
    [Fact]
    public async Task PersistFailurePreventsMaterialResponsePost()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data);
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, (_, _) => throw new IOException("synthetic disk full"));
        await Assert.ThrowsAsync<IOException>(() => source.RespondAsync(data.MaterialRequest)); Assert.DoesNotContain("respond", client.Calls);
    }
    [Fact]
    public async Task LostResponseRecoversOnlyWithMatchingOriginalOperation()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data) { LoseMaterial = true }; JsonElement? saved = null;
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, (response, _) => { saved = response.Clone(); return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => source.RespondAsync(data.MaterialRequest)); Assert.NotNull(saved);
        var recovered = await source.RecoverAsync(saved.Value); Assert.Equal("core-consumed", recovered.Receipt.GetProperty("state").GetString()); Assert.Null(recovered.Availability); Assert.Single(client.Calls, c => c == "respond");
        client.AlterOperation = r => ArchiveFlowFixture.Set(r, "semanticDigest", new string('f', 64)); int statuses = client.Calls.Count(c => c == "material-status");
        await Assert.ThrowsAsync<TansrProtocolException>(() => source.RecoverAsync(saved.Value)); Assert.Equal(statuses, client.Calls.Count(c => c == "material-status"));
    }
    [Fact]
    public async Task RevocationDuringBodyReadPreventsAnyUpload()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true) { OnBody = () => data.User = "other" }; var client = new ArchiveFlowFixture.Client(data);
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<TansrProtocolException>(() => source.RespondAsync(data.MaterialRequest)); Assert.Empty(client.Calls);
    }
    [Fact]
    public async Task CancellationBeforeStartDoesNotReadOrSend()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data); var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, (_, _) => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.RespondAsync(data.MaterialRequest, cancellation.Token)); Assert.Empty(client.Calls);
    }
    [Fact]
    public async Task DefaultOutboxSavesBeforePostAndClearsOnlyAfterAcceptedReceipt()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data); var outbox = new Outbox();
        client.OnRespond = () => Assert.NotNull(outbox.Pending);
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, outbox);
        await source.RespondAsync(data.MaterialRequest); Assert.Null(outbox.Pending); Assert.Equal(1, outbox.Saves); Assert.Equal(1, outbox.Clears); Assert.Null(await source.RecoverPendingAsync());
    }
    [Fact]
    public async Task PendingResponseBlocksNewUploadsAndSurvivesUntilOriginalOperationIsProven()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data) { LoseMaterial = true }; var outbox = new Outbox();
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, outbox);
        await Assert.ThrowsAsync<IOException>(() => source.RespondAsync(data.MaterialRequest)); var original = outbox.Pending!.Value;
        int uploadCount = client.Calls.Count(c => c == "upload");
        var blocked = await Assert.ThrowsAsync<TansrProtocolException>(() => source.RespondAsync(data.MaterialRequest)); Assert.Equal("reconciliation_required", blocked.Code); Assert.Equal(uploadCount, client.Calls.Count(c => c == "upload"));
        client.AlterOperation = r => ArchiveFlowFixture.Set(r, "semanticDigest", new string('f', 64));
        await Assert.ThrowsAsync<TansrProtocolException>(() => source.RecoverPendingAsync()); Assert.Equal(original.GetRawText(), outbox.Pending.Value.GetRawText()); Assert.Equal(0, outbox.Clears);
        client.AlterOperation = null;
        var restarted = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => throw new InvalidOperationException("Recovery must not mint a request key"), outbox);
        var result = await restarted.RecoverPendingAsync(); Assert.NotNull(result); Assert.Equal("core-consumed", result.Receipt.GetProperty("state").GetString()); Assert.Null(outbox.Pending); Assert.Single(client.Calls, c => c == "respond");
    }
    [Fact]
    public async Task ExplicitReplayUsesExactResponseAndDoesNotReuploadBodies()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data) { LoseMaterial = true }; var outbox = new Outbox();
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, outbox);
        await Assert.ThrowsAsync<IOException>(() => source.RespondAsync(data.MaterialRequest)); var original = outbox.Pending!.Value; int uploads = client.Calls.Count(c => c == "upload"); client.LoseMaterial = false;
        var result = await source.RecoverPendingAsync(retryOriginal: true); Assert.NotNull(result); Assert.Equal(original.GetRawText(), result.Response.GetRawText()); Assert.Null(outbox.Pending); Assert.Equal(uploads, client.Calls.Count(c => c == "upload")); Assert.Equal(2, client.Calls.Count(c => c == "respond"));
    }
    [Fact]
    public async Task DeletedRecordCannotBeReplayedFromAnOtherwiseValidPersistedResponse()
    {
        var data = new ArchiveFlowFixture(); var store = new ArchiveFlowFixture.Store(data, true); var client = new ArchiveFlowFixture.Client(data) { LoseMaterial = true }; var outbox = new Outbox();
        var source = new MaterialSource(client, store, data.Identity, () => data.Scope, _ => data.RequestIdentity, outbox);
        await Assert.ThrowsAsync<IOException>(() => source.RespondAsync(data.MaterialRequest)); store.HasRecord = false; client.LoseMaterial = false;
        await Assert.ThrowsAsync<TansrProtocolException>(() => source.RecoverPendingAsync(retryOriginal: true)); Assert.NotNull(outbox.Pending); Assert.Single(client.Calls, c => c == "respond");
        Assert.NotNull(await source.RecoverPendingAsync()); Assert.Null(outbox.Pending);
    }
    private sealed class Outbox : IMaterialResponseOutbox
    {
        internal JsonElement? Pending;
        internal int Saves, Clears;
        public Task<JsonElement?> ReadAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Pending?.Clone()); }
        public Task SaveIfEmptyAsync(JsonElement response, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.Null(Pending); Pending = WireJson.DecodeControl(WireJson.EncodeControl(response)); Saves++; return Task.CompletedTask;
        }
        public Task ClearIfExactAsync(JsonElement response, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.NotNull(Pending); Assert.Equal(WireJson.CanonicalString(Pending.Value), WireJson.CanonicalString(response)); Pending = null; Clears++; return Task.CompletedTask;
        }
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
