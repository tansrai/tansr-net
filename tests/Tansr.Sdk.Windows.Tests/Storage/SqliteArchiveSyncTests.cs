using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteArchiveSyncTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheDurablyPreservesOriginalCheckpointAndReceiptWithoutCreatingAnAck(bool encrypted)
    {
        using var sourceFixture = new Fixture(encrypted); using var cacheFixture = new Fixture(encrypted);
        JsonElement page, firstReceipt;
        using (var source = await SqliteArchiveStore.OpenAsync(sourceFixture.Options()))
        {
            Assert.Null(await source.SyncPageAsync(null)); Assert.Null(await source.ReplicaOperationAsync(sourceFixture.Input.Request));
            var ack = await source.ReceiveAsync(sourceFixture.Input);
            var pendingOperation = (await source.ReplicaOperationAsync(sourceFixture.Input.Request))!.Value;
            Assert.Equal(JsonValueKind.Null, pendingOperation.GetProperty("receipt").ValueKind);
            Assert.Equal("pending_ack", (await Assert.ThrowsAsync<StorageException>(() => source.SyncPageAsync(null))).Code);
            firstReceipt = sourceFixture.Receipt(ack); await source.ConfirmAsync(firstReceipt); page = (await source.SyncPageAsync(null))!.Value;
            Assert.Equal(Text(ack), Text(page.GetProperty("ack"))); Assert.Equal(Text(firstReceipt), Text(page.GetProperty("receipt")));
            Assert.Null(await source.SyncPageAsync("1"));
            Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => source.SyncPageAsync("2"))).Code);
            Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => source.ReceiveSyncAsync(Input(page, sourceFixture.Input.Artifacts)))).Code);
        }
        var options = Cache(cacheFixture);
        using (var cache = await SqliteArchiveStore.OpenAsync(options))
        {
            Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => cache.ReceiveAsync(cacheFixture.Input))).Code);
            var input = Input(page, sourceFixture.Input.Artifacts); var receipt = await cache.ReceiveSyncAsync(input);
            Assert.Equal("archive-sync-receipt-v1", receipt.GetProperty("format").GetString());
            Assert.Equal(Text(receipt), Text(await cache.ReceiveSyncAsync(input)));
            Assert.Null(await cache.PendingAsync()); Assert.Equal(cacheFixture.Body, await cache.BodyAsync(cacheFixture.Reference));
            Assert.Equal(Text(page), Text((await cache.SyncPageAsync(null))!.Value));
            Assert.Equal(Text(firstReceipt), Text((await cache.ReplicaOperationAsync(cacheFixture.Input.Request))!.Value.GetProperty("receipt")));
            var identity = await cache.ReplicaIdentityAsync(); Assert.Equal("replica", identity.GetProperty("replica").GetProperty("role").GetString());
            Assert.Equal(Text(cacheFixture.Identity), Text(identity.GetProperty("receiver")));
            Assert.Equal(options.Limits.MaxBatchBytes, identity.GetProperty("limits").GetProperty("maxBatchBytes").GetInt32());
            Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => cache.ConfirmAsync(firstReceipt))).Code);
        }
        options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteArchiveStore.OpenAsync(options);
        Assert.Null(await reopened.PendingAsync()); Assert.Equal(Text(page), Text((await reopened.SyncPageAsync(null))!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheRequiresTrustedRetentionBeforeAcceptingTombstonesAndNeverRestoresDeletedBodies(bool encrypted)
    {
        using var sourceFixture = new Fixture(encrypted); using var cacheFixture = new Fixture(encrypted);
        using var source = await SqliteArchiveStore.OpenAsync(sourceFixture.Options()); var ack = await source.ReceiveAsync(sourceFixture.Input); await source.ConfirmAsync(sourceFixture.Receipt(ack));
        sourceFixture.RetentionRevision = "1"; await source.ApplyRetentionAsync(sourceFixture.Retention());
        var page = (await source.SyncPageAsync(null))!.Value; Assert.Single(page.GetProperty("tombstones").EnumerateArray());
        cacheFixture.RetentionRevision = "1"; var options = Cache(cacheFixture);
        using (var cache = await SqliteArchiveStore.OpenAsync(options))
        {
            Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => cache.ReceiveSyncAsync(Input(page)))).Code);
            await cache.ApplyRetentionAsync(cacheFixture.Retention());
            Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => cache.ReceiveSyncAsync(Input(page, cacheFixture.Input.Artifacts)))).Code);
            var receipt = await cache.ReceiveSyncAsync(Input(page)); Assert.Equal("1", receipt.GetProperty("retentionRevision").GetString());
            Assert.Null(await cache.PendingAsync()); Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => cache.BodyAsync(cacheFixture.Reference))).Code);
            Assert.Equal(Text(page), Text((await cache.SyncPageAsync(null))!.Value));
            var hiddenTombstone = Replace(page, "tombstones", Element(Array.Empty<object>()));
            Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => cache.ReceiveSyncAsync(Input(hiddenTombstone)))).Code);
        }
        options.Mode = StorageOpenMode.Reopen;
        using (var cache = await SqliteArchiveStore.OpenAsync(options)) Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => cache.BodyAsync(cacheFixture.Reference))).Code);
        using var connection = new SqliteConnection("Data Source=" + cacheFixture.Path + ";Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT length(body) FROM artifacts"; Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task DeletedRecordCannotOmitAnArtifactStillReferencedByALiveRecordInTheSameBatch()
    {
        using var sourceFixture = new Fixture(true); using var cacheFixture = new Fixture(true);
        var sourceOptions = sourceFixture.Options(); sourceOptions.AuthorizeRetention = _ => { };
        using var source = await SqliteArchiveStore.OpenAsync(sourceOptions); var batch = TwoRecordsSharingPayload(sourceFixture);
        var ack = await source.ReceiveAsync(batch); await source.ConfirmAsync(sourceFixture.Receipt(ack));
        sourceFixture.RetentionRevision = "1"; await source.ApplyRetentionAsync(sourceFixture.Retention());
        var page = (await source.SyncPageAsync(null))!.Value; cacheFixture.RetentionRevision = "1";
        using var cache = await SqliteArchiveStore.OpenAsync(Cache(cacheFixture)); await cache.ApplyRetentionAsync(cacheFixture.Retention());
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => cache.ReceiveSyncAsync(Input(page)))).Code);
        await cache.ReceiveSyncAsync(Input(page, sourceFixture.Input.Artifacts));
        Assert.Equal(cacheFixture.Body, await cache.BodyAsync(cacheFixture.Reference));
        Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => cache.ReadRecordsAsync(new ArchiveReadRequest { Identity = cacheFixture.Identity, Selection = Element(new { recordIds = new[] { "record" } }) }))).Code);
        Assert.Single((await cache.ReadRecordsAsync(new ArchiveReadRequest { Identity = cacheFixture.Identity, Selection = Element(new { recordIds = new[] { "record-2" } }) })).Records);
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("receipt")]
    [InlineData("checkpoint")]
    [InlineData("identity")]
    public async Task MismatchedOriginalOperationOrCheckpointIsRejectedWithoutChangingTheCache(string field)
    {
        using var sourceFixture = new Fixture(); using var cacheFixture = new Fixture(); using var source = await SqliteArchiveStore.OpenAsync(sourceFixture.Options());
        var ack = await source.ReceiveAsync(sourceFixture.Input); await source.ConfirmAsync(sourceFixture.Receipt(ack)); var page = (await source.SyncPageAsync(null))!.Value;
        using var cache = await SqliteArchiveStore.OpenAsync(Cache(cacheFixture)); await cache.ReceiveSyncAsync(Input(page, sourceFixture.Input.Artifacts));
        var changed = field switch
        {
            "ack" => ReplacePath(page, new[] { "ack", "expectedRevision" }, Element("1")),
            "receipt" => ReplacePath(page, new[] { "receipt", "semanticDigest" }, Element(new string('f', 64))),
            "checkpoint" => ReplacePath(page, new[] { "checkpoint", "binding", "revision" }, Element("8")),
            _ => ReplacePath(page, new[] { "identity", "sourceGeneration" }, Element("other-generation")),
        };
        Assert.Equal(field == "identity" ? "identity_mismatch" : "receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => cache.ReceiveSyncAsync(Input(changed, sourceFixture.Input.Artifacts)))).Code);
        Assert.Equal(Text(page), Text((await cache.SyncPageAsync(null))!.Value));
    }

    [Fact]
    public async Task CacheRoleAndReplicaIdentityArePersistentMetadataAndCannotBeReopenedDifferently()
    {
        using var fixture = new Fixture(); using (await SqliteArchiveStore.OpenAsync(Cache(fixture))) { }
        var options = Cache(fixture); options.Mode = StorageOpenMode.Reopen; options.SyncRole = "source";
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenAsync(options))).Code);
        options.SyncRole = "cache"; options.Replica = Replace(options.Replica, "role", Element("primary"));
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenAsync(options))).Code);
    }

    [Fact]
    public async Task ResponseBytesAreFrozenBeforeTheFirstContextCallbackAndCancellationBeforeCommitRollsBack()
    {
        using var sourceFixture = new Fixture(); using var cacheFixture = new Fixture(); using var source = await SqliteArchiveStore.OpenAsync(sourceFixture.Options());
        var ack = await source.ReceiveAsync(sourceFixture.Input); await source.ConfirmAsync(sourceFixture.Receipt(ack)); var page = (await source.SyncPageAsync(null))!.Value;
        byte[] body = (byte[])sourceFixture.Body.Clone(); bool armed = false, cancelBeforeCommit = true; int reads = 0; using var cancel = new CancellationTokenSource();
        var options = Cache(cacheFixture); options.ReadContext = () => { if (armed && ++reads == 1) body[0] ^= 1; if (armed && reads == 3 && cancelBeforeCommit) cancel.Cancel(); return cacheFixture.Scope; };
        using var cache = await SqliteArchiveStore.OpenAsync(options); armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ReceiveSyncAsync(Input(page, new[] { new ArchiveArtifact("body", body) }), cancel.Token));
        armed = false; Assert.Null(await cache.HeadAsync()); Assert.Null(await cache.ReplicaOperationAsync(sourceFixture.Input.Request));
        body = (byte[])sourceFixture.Body.Clone(); reads = 0; armed = true; cancelBeforeCommit = false;
        // 仅首回调改调用方数组：同步请求已冻结，保存的仍应是原正文。
        options.ReadContext = () => throw new InvalidOperationException("options are already frozen");
        await cache.ReceiveSyncAsync(Input(page, new[] { new ArchiveArtifact("body", body) })); armed = false;
        Assert.Equal(sourceFixture.Body, await cache.BodyAsync(sourceFixture.Reference)); Assert.NotEqual(sourceFixture.Body[0], body[0]);
    }

    [OriginalNodeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalNodeAndCSharpConsumeEachOthersCacheCheckpointsAndReceipts(bool encrypted)
    {
        using var sourceFixture = new Fixture(encrypted); using var csharpCache = new Fixture(encrypted); using var nodeCache = new Fixture(encrypted);
        JsonElement page;
        using (var source = await SqliteArchiveStore.OpenAsync(sourceFixture.Options()))
        { var ack = await source.ReceiveAsync(sourceFixture.Input); await source.ConfirmAsync(sourceFixture.Receipt(ack)); page = (await source.SyncPageAsync(null))!.Value; }
        using (var cache = await SqliteArchiveStore.OpenAsync(Cache(csharpCache))) await cache.ReceiveSyncAsync(Input(page, sourceFixture.Input.Artifacts));
        await OriginalNode(sourceFixture, csharpCache, nodeCache, encrypted, page);
        var options = Cache(nodeCache); options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteArchiveStore.OpenAsync(options);
        Assert.Equal(Text(page), Text((await reopened.SyncPageAsync(null))!.Value)); Assert.Null(await reopened.PendingAsync());
        Assert.Equal(sourceFixture.Body, await reopened.BodyAsync(sourceFixture.Reference));
    }

    private static SqliteArchiveStoreOptions Cache(Fixture fixture)
    { var options = fixture.Options(); options.SyncRole = "cache"; options.Replica = Replace(options.Replica, "role", Element("replica")); return options; }
    private static ArchiveSyncReceiveInput Input(JsonElement page, IReadOnlyList<ArchiveArtifact>? artifacts = null)
        => new() { Page = page, Artifacts = artifacts ?? Array.Empty<ArchiveArtifact>() };
    private static string Text(JsonElement value) => WireJson.CanonicalString(value, 1572864);
    private static JsonElement ReplacePath(JsonElement value, IReadOnlyList<string> path, JsonElement replacement, int index = 0)
        => Replace(value, path[index], index == path.Count - 1 ? replacement : ReplacePath(value.GetProperty(path[index]), path, replacement, index + 1));
    private static ArchiveReceiveInput TwoRecordsSharingPayload(Fixture fixture)
    {
        var first = fixture.Input.Page.GetProperty("records")[0];
        var values = first.EnumerateObject().Where(property => property.Name != "recordDigest").ToDictionary(property => property.Name, property => property.Value.Clone());
        values["recordId"] = Element("record-2"); values["sequence"] = Element("2"); values["turnId"] = Element("turn-2"); values["predecessorDigest"] = first.GetProperty("recordDigest");
        values["recordDigest"] = Element(WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(Element(values))));
        return new ArchiveReceiveInput
        {
            Binding = fixture.Input.Binding,
            Request = fixture.Input.Request,
            Artifacts = fixture.Input.Artifacts,
            Status = Replace(fixture.Input.Status, "publishedThroughSequence", Element("2")),
            Page = Replace(Replace(Replace(fixture.Input.Page, "records", Element(new[] { first, Element(values) })), "nextAfterSequence", Element("2")), "publishedThroughSequence", Element("2")),
        };
    }

    private static async Task OriginalNode(Fixture sourceFixture, Fixture csharpCache, Fixture nodeCache, bool encrypted, JsonElement expectedPage)
    {
        string cliRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("TANSR_TEST_CLI_ROOT")!); var options = Cache(nodeCache); var limits = options.Limits;
        var payload = JsonSerializer.Serialize(new { cliRoot, sourceRevision = WireContract.SourceRevision, encrypted, sourcePath = sourceFixture.Path, csharpPath = csharpCache.Path, nodePath = nodeCache.Path, identity = sourceFixture.Identity, replica = options.Replica, sourceReplica = sourceFixture.Options().Replica, scope = sourceFixture.Scope, limits = new { maxRecords = limits.MaxRecords, maxArtifacts = limits.MaxArtifacts, maxStoredBytes = limits.MaxStoredBytes, maxBatchBytes = limits.MaxBatchBytes }, maxPages = options.MaxPages, artifact = sourceFixture.Reference, expectedPage, bodyBase64 = Convert.ToBase64String(sourceFixture.Body) });
        const string script = """
            import assert from 'node:assert/strict';
            import { execFileSync } from 'node:child_process';
            import { join } from 'node:path';
            import { pathToFileURL } from 'node:url';
            let input=''; for await(const part of process.stdin) { input+=part; assert.ok(input.length<=2*1024*1024); }
            const d=JSON.parse(input);
            assert.equal(execFileSync('git',['-C',d.cliRoot,'rev-parse','HEAD'],{encoding:'utf8',windowsHide:true}).trim(),d.sourceRevision);
            assert.equal(execFileSync('git',['-C',d.cliRoot,'status','--porcelain','--','packages/api-client/src/sdk2'],{encoding:'utf8',windowsHide:true}).trim(),'');
            const module=await import(pathToFileURL(join(d.cliRoot,'packages/api-client/src/sdk2/receiver-sqlite.ts')).href);
            const open=d.encrypted?module.openSdk2EncryptedSqliteSyncArchiveStore:module.openSdk2SqliteSyncArchiveStore;
            const options=(path,mode,role)=>({path,mode,identity:d.identity,replica:role==='source'?d.sourceReplica:d.replica,syncRole:role,limits:d.limits,maxPages:d.maxPages,readContext:()=>d.scope,readRetentionRevision:()=> '0',authorizeRetention:()=>{throw Error('fixture does not authorize retention');},...(d.encrypted?{key:{id:'key',read:()=>Uint8Array.from({length:32},(_,i)=>i)}}:{})});
            const cache=await open(options(d.csharpPath,'reopen','cache'));
            try { assert.deepEqual(await cache.syncPage(null),d.expectedPage); assert.equal(await cache.pending(),null); assert.equal(Buffer.from(await cache.body(d.artifact)).toString('base64'),d.bodyBase64); }
            finally { await cache.close(); }
            const source=await open(options(d.sourcePath,'reopen','source')), target=await open(options(d.nodePath,'create','cache'));
            try { const page=await source.syncPage(null); assert.deepEqual(page,d.expectedPage); const receipt=await target.receiveSync({page,artifacts:[{artifactId:d.artifact.artifactId,body:await source.body(d.artifact)}]}); assert.equal(receipt.format,'archive-sync-receipt-v1'); assert.deepEqual(await target.syncPage(null),page); assert.equal(await target.pending(),null); }
            finally { await target.close(); await source.close(); }
            process.stdout.write('original-node-cache-ok');
            """;
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "node", WorkingDirectory = cliRoot, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("--disable-warning=ExperimentalWarning"); process.StartInfo.ArgumentList.Add("--import"); process.StartInfo.ArgumentList.Add(new Uri(Path.Combine(cliRoot, "node_modules", "tsx", "dist", "loader.mjs")).AbsoluteUri);
        process.StartInfo.ArgumentList.Add("--input-type=module"); process.StartInfo.ArgumentList.Add("--eval"); process.StartInfo.ArgumentList.Add(script);
        Assert.True(process.Start()); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(payload); process.StandardInput.Close(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error); Assert.Equal("original-node-cache-ok", await output);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
