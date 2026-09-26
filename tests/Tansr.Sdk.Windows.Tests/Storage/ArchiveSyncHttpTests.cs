using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Archive.Replication;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class ArchiveSyncHttpTests
{
    [Fact]
    public async Task RealHttpSyncCopiesBoundedChunksToEncryptedDurableCacheAndReopens()
    {
        var body = Encoding.UTF8.GetBytes(new string('中', 210000)); using var sourceFixture = new Fixture(true, body); using var cacheFixture = new Fixture(true, body);
        using var source = await SqliteArchiveStore.OpenAsync(sourceFixture.Options()); var ack = await source.ReceiveAsync(sourceFixture.Input); await source.ConfirmAsync(sourceFixture.Receipt(ack));
        await using var server = new Server(source, sourceFixture); using var client = Client(server, sourceFixture); var options = Cache(cacheFixture);
        using (var cache = await SqliteArchiveStore.OpenAsync(options))
        {
            var result = await client.SynchronizeAsync(cache); Assert.Equal("archive-sync-receipt-v1", result!.Value.GetProperty("format").GetString()); Assert.Equal(body, await cache.BodyAsync(cacheFixture.Reference));
            Assert.Equal(3, server.Calls.Count(c => c == "body-chunk")); Assert.Null(await cache.PendingAsync()); Assert.Null(await client.SynchronizeAsync(cache));
        }
        options.Mode = StorageOpenMode.Reopen; using var reopened = await SqliteArchiveStore.OpenAsync(options); Assert.Equal(body, await reopened.BodyAsync(cacheFixture.Reference)); Assert.NotNull(await reopened.ReplicaOperationAsync(cacheFixture.Input.Request));
    }
    [Fact]
    public async Task HttpSyncAppliesTrustedTombstoneBeforeAnyBodyReadAndDoesNotResurrectOnReopen()
    {
        using var sourceFixture = new Fixture(true); using var cacheFixture = new Fixture(true); using var source = await SqliteArchiveStore.OpenAsync(sourceFixture.Options());
        var ack = await source.ReceiveAsync(sourceFixture.Input); await source.ConfirmAsync(sourceFixture.Receipt(ack)); sourceFixture.RetentionRevision = "1"; await source.ApplyRetentionAsync(sourceFixture.Retention()); cacheFixture.RetentionRevision = "1";
        await using var server = new Server(source, sourceFixture); using var client = Client(server, sourceFixture); var options = Cache(cacheFixture);
        using (var cache = await SqliteArchiveStore.OpenAsync(options))
        { Assert.NotNull(await client.SynchronizeAsync(cache)); Assert.DoesNotContain("body-chunk", server.Calls); Assert.True(server.Calls.IndexOf("retention-page") < server.Calls.IndexOf("sync-page")); Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => cache.BodyAsync(cacheFixture.Reference))).Code); }
        options.Mode = StorageOpenMode.Reopen; using var reopened = await SqliteArchiveStore.OpenAsync(options); Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => reopened.BodyAsync(cacheFixture.Reference))).Code);
    }
    [Fact]
    public async Task TwoDurableStoresRecoverLostReplicaResponseWithoutReapplyingOrMintingAnAck()
    {
        using var primaryFixture = new Fixture(true); using var replicaFixture = new Fixture(true); var replicaOptions = replicaFixture.Options(); replicaOptions.Replica = Element(new { replicationId = "group", role = "replica" });
        using var primary = await SqliteArchiveStore.OpenAsync(primaryFixture.Options()); using var replica = await SqliteArchiveStore.OpenAsync(replicaOptions); await using var server = new Server(replica, replicaFixture) { DropReceiveResponse = true }; using var remote = Client(server, replicaFixture);
        var combined = new ReplicatedArchiveStore(new ReplicatedArchiveStoreOptions { Primary = primary, Replica = remote, ReplicationId = "group", Identity = primaryFixture.Identity, Limits = Limits(primaryFixture), ReadContext = () => primaryFixture.Scope });
        try
        {
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => combined.ReceiveAsync(primaryFixture.Input))).Code); Assert.NotNull(await primary.PendingAsync()); Assert.NotNull(await replica.PendingAsync());
            var ack = await combined.RecoverPendingAsync((_, _) => throw new InvalidOperationException("Both stores already received the original operation")); Assert.NotNull(ack); Assert.Single(server.Calls, c => c == "receive");
            await combined.ConfirmAsync(primaryFixture.Receipt(ack.Value)); Assert.Null(await combined.PendingAsync()); Assert.Null(await primary.PendingAsync()); Assert.Null(await replica.PendingAsync()); Assert.Equal(primaryFixture.Body, await combined.BodyAsync(primaryFixture.Reference));
        }
        finally { await combined.CloseAsync(); }
        using var reopened = await SqliteArchiveStore.OpenAsync(primaryFixture.Options(StorageOpenMode.Reopen)); Assert.Null(await reopened.PendingAsync()); Assert.NotNull(await reopened.ReplicaOperationAsync(primaryFixture.Input.Request));
    }
    [Fact]
    public async Task ShortTokenChangeDuringRequestRejectsObservedResponse()
    {
        using var fixture = new Fixture(); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options()); string token = "first-token"; await using var server = new Server(store, fixture) { AfterDispatch = _ => token = "second-token" }; using var client = Client(server, fixture, () => token);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => client.HeadAsync())).Code);
    }
    [Fact]
    public async Task TransportCredentialCallbackReentryIsRejectedBeforeAnyHttpRequest()
    {
        using var fixture = new Fixture(); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options()); await using var server = new Server(store, fixture);
        int reads = 0; StorageException? nestedFailure = null; ArchiveSyncClient? client = null;
        client = Client(server, fixture, () =>
        {
            if (++reads == 3)
            {
                try { client!.HeadAsync().GetAwaiter().GetResult(); }
                catch (StorageException error) { nestedFailure = error; }
            }
            return "same-synthetic-token";
        });
        using (client)
        {
            Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => client.HeadAsync())).Code);
            Assert.NotNull(nestedFailure); Assert.Equal("reentrant", nestedFailure.Code); Assert.Empty(server.Calls); Assert.Null(await store.HeadAsync());
        }
    }
    [Fact]
    public async Task PartialReplicaFailureLoadsOnlyOriginalInputAndCompletesMissingDurableSide()
    {
        using var primaryFixture = new Fixture(); using var replicaFixture = new Fixture(); var replicaOptions = replicaFixture.Options(); replicaOptions.Replica = Element(new { replicationId = "group", role = "replica" });
        using var primary = await SqliteArchiveStore.OpenAsync(primaryFixture.Options()); using var replica = await SqliteArchiveStore.OpenAsync(replicaOptions); await using var server = new Server(replica, replicaFixture) { FailReceiveOnce = true }; using var remote = Client(server, replicaFixture);
        var combined = new ReplicatedArchiveStore(new ReplicatedArchiveStoreOptions { Primary = primary, Replica = remote, ReplicationId = "group", Identity = primaryFixture.Identity, Limits = Limits(primaryFixture), ReadContext = () => primaryFixture.Scope });
        try
        {
            await Assert.ThrowsAsync<StorageException>(() => combined.ReceiveAsync(primaryFixture.Input)); Assert.NotNull(await primary.PendingAsync()); Assert.Null(await replica.HeadAsync());
            int loads = 0; var ack = await combined.RecoverPendingAsync((original, _) => { loads++; Assert.Equal(WireJson.CanonicalString(primaryFixture.Input.Request), WireJson.CanonicalString(original.GetProperty("request"))); return Task.FromResult(primaryFixture.Input); });
            Assert.NotNull(ack); Assert.Equal(1, loads); Assert.Equal(2, server.Calls.Count(c => c == "receive")); await combined.ConfirmAsync(primaryFixture.Receipt(ack.Value)); Assert.Null(await combined.PendingAsync());
        }
        finally { await combined.CloseAsync(); }
    }
    [Fact]
    public async Task SharedReadGrantCannotBeUsedForSourceMutation()
    {
        using var fixture = new Fixture(); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options()); await using var server = new Server(store, fixture);
        using var client = new ArchiveSyncClient(new ArchiveSyncClientOptions { BaseUri = server.Origin, AllowInsecureLoopback = true, Identity = fixture.Identity, Limits = Limits(fixture), ReadContext = () => fixture.Scope, ReadToken = () => "read-only-token", SharedReadAccess = () => Element(new { actor = fixture.Scope, owner = fixture.Identity, grantRevision = "1" }) });
        Assert.Null(await client.HeadAsync()); int before = server.Calls.Count;
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => client.ReceiveAsync(fixture.Input))).Code); Assert.Equal(before, server.Calls.Count); Assert.Null(await store.HeadAsync());
    }
    [Fact]
    public async Task ForeignReceiverEnvelopeIsRejectedWithoutWritingCache()
    {
        using var fixture = new Fixture(); using var cacheFixture = new Fixture(); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options()); await using var server = new Server(store, fixture) { AlterIdentity = true }; using var client = Client(server, fixture); using var cache = await SqliteArchiveStore.OpenAsync(Cache(cacheFixture));
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => client.SynchronizeAsync(cache))).Code); Assert.Null(await cache.HeadAsync());
    }
    private static SqliteArchiveStoreOptions Cache(Fixture fixture) { var value = fixture.Options(); value.SyncRole = "cache"; value.Replica = Element(new { replicationId = "group", role = "replica" }); return value; }
    private static JsonElement Limits(Fixture fixture) { var l = fixture.Options().Limits; return Element(new { maxRecords = l.MaxRecords, maxArtifacts = l.MaxArtifacts, maxStoredBytes = l.MaxStoredBytes, maxBatchBytes = l.MaxBatchBytes }); }
    private static ArchiveSyncClient Client(Server server, Fixture fixture, Func<string>? token = null) => new(new ArchiveSyncClientOptions { BaseUri = server.Origin, AllowInsecureLoopback = true, Identity = fixture.Identity, Limits = Limits(fixture), ReadContext = () => fixture.Scope, ReadToken = token ?? (() => "synthetic-storage-token") });
    private sealed class Server : IAsyncDisposable
    {
        private readonly HttpListener _listener = new(); private readonly SqliteArchiveStore _store; private readonly Fixture _fixture; private readonly Task _loop;
        internal Uri Origin { get; }
        internal List<string> Calls { get; } = new();
        internal bool DropReceiveResponse, AlterIdentity, FailReceiveOnce;
        internal Action<string>? AfterDispatch;
        internal Server(SqliteArchiveStore store, Fixture fixture)
        {
            _store = store; _fixture = fixture; var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Origin = new Uri("http://127.0.0.1:" + port + "/"); _listener.Prefixes.Add(Origin.AbsoluteUri); _listener.Start(); _loop = Loop();
        }
        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context; try { context = await _listener.GetContextAsync(); } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                try
                {
                    Assert.Equal("POST", context.Request.HttpMethod); Assert.Equal("/v3/sdk2/archive-sync/binding", context.Request.Url!.AbsolutePath); Assert.StartsWith("Bearer ", context.Request.Headers["Authorization"]);
                    using var body = new MemoryStream(); await context.Request.InputStream.CopyToAsync(body); var envelope = WireJson.DecodeControl(body.ToArray(), 67108864); Assert.Equal("archive-sync-v1", envelope.GetProperty("format").GetString()); Assert.Equal(WireJson.CanonicalString(_fixture.Identity), WireJson.CanonicalString(envelope.GetProperty("identity")));
                    string method = envelope.GetProperty("method").GetString()!; Calls.Add(method); object? value; int status = 200;
                    try { value = await Dispatch(method, envelope.GetProperty("value")); } catch (StorageException e) { status = 409; value = new { code = e.Code }; }
                    AfterDispatch?.Invoke(method);
                    if (method == "receive" && DropReceiveResponse) { context.Response.Abort(); continue; }
                    var output = new Dictionary<string, object?> { ["format"] = "archive-sync-v1", ["identity"] = AlterIdentity ? Replace(_fixture.Identity, "sourceGeneration", Element("foreign")) : _fixture.Identity, ["method"] = method, [status == 200 ? "value" : "error"] = value };
                    byte[] bytes = WireJson.EncodeControl(Element(output), 1638400); context.Response.StatusCode = status; context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
                catch { context.Response.Abort(); throw; }
            }
        }
        private async Task<object?> Dispatch(string method, JsonElement value)
        {
            switch (method)
            {
                case "identity": return await _store.ReplicaIdentityAsync();
                case "head": return await _store.HeadAsync();
                case "pending": return await _store.PendingAsync();
                case "coverage": return await _store.CoverageAsync();
                case "retention": return await _store.RetentionRevisionAsync();
                case "retention-page": return await _store.RetentionPageAsync(value.GetString()!);
                case "sync-page": return await _store.SyncPageAsync(value.GetString());
                case "body-chunk": { var reference = value.GetProperty("ref"); long offset = value.GetProperty("offset").GetInt64(); return new { @ref = reference, offset, base64 = Convert.ToBase64String(await _store.BodyChunkAsync(reference, offset)) }; }
                case "operation": return await _store.ReplicaOperationAsync(value);
                case "confirm": await _store.ConfirmAsync(value); return null;
                case "receive":
                    if (FailReceiveOnce) { FailReceiveOnce = false; throw new StorageException("storage_error"); }
                    Assert.Equal(WireJson.CanonicalString(_fixture.Input.Page), WireJson.CanonicalString(value.GetProperty("page")));
                    return await _store.ReceiveAsync(new ArchiveReceiveInput { Binding = value.GetProperty("binding"), Status = value.GetProperty("status"), Page = value.GetProperty("page"), Request = value.GetProperty("request"), Artifacts = value.GetProperty("artifacts").EnumerateArray().Select(a => new ArchiveArtifact(a.GetProperty("artifactId").GetString()!, WireJson.DecodeBase64(a.GetProperty("base64").GetString()!))).ToArray() });
                case "records":
                    var page = await _store.ReadRecordsAsync(new ArchiveReadRequest { Identity = value.GetProperty("identity"), Selection = value.GetProperty("selection"), MaxRecords = value.GetProperty("maxRecords").GetInt32(), MaxBytes = value.GetProperty("maxBytes").GetInt32() });
                    return new { records = page.Records, bytes = page.Bytes, missingRecordIds = page.MissingRecordIds, nextFromSequence = page.NextFromSequence, sourceCoverage = page.SourceCoverage };
                default: throw new InvalidOperationException(method);
            }
        }
        public async ValueTask DisposeAsync() { _listener.Close(); await _loop; }
    }
}
