using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteArchiveRecoveryTests
{
    internal static JsonElement Request => Element(new { operationEpoch = "epoch", requestId = "rebase-ack-one" });
    internal static string Text(JsonElement value) => WireJson.CanonicalString(value, 1572864);
    internal static JsonElement Result(Fixture fixture, JsonElement intent)
    {
        var next = Replace(Replace(intent.GetProperty("previous"), "request", intent.GetProperty("request")), "expectedRevision", Element("6"));
        return Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", previous = intent.GetProperty("previous"), request = intent.GetProperty("request"), next, receipt = Replace(fixture.Receipt(next), "revision", Element("7")) });
    }

    [Fact]
    public async Task RealKernelGoldenMatchesNodeDdlPreparedReserveCompletedLedgerAndSyncPage()
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "..", "..", "Tansr.Sdk.Tests", "Terminal", "Fixtures", "sdk2-archive-recovery-v1.golden.json"));
        byte[] bytes = File.ReadAllBytes(path); Assert.Equal("614adf2088585a6213d010acde29598d40757d0f2d7b4608f1670ccbde3a5553", WireJson.Sha256(bytes));
        var golden = WireJson.Parse(bytes); var limits = golden.GetProperty("receiverLimits"); var checkpoint = golden.GetProperty("receive").GetProperty("checkpoint");
        using var f = new Fixture(); var options = new SqliteArchiveStoreOptions
        {
            Path = f.Path,
            Identity = golden.GetProperty("identity"),
            Replica = Element(new { replicationId = "group", role = "primary" }),
            Limits = new ArchiveStoreLimits { MaxRecords = limits.GetProperty("maxRecords").GetInt32(), MaxArtifacts = limits.GetProperty("maxArtifacts").GetInt32(), MaxStoredBytes = limits.GetProperty("maxStoredBytes").GetInt64(), MaxBatchBytes = limits.GetProperty("maxBatchBytes").GetInt32() },
            ReadContext = () => golden.GetProperty("scope"),
            ReadRetentionRevision = () => "0",
            AuthorizeRetention = _ => throw new InvalidOperationException(),
        };
        var input = new ArchiveReceiveInput
        {
            Binding = checkpoint.GetProperty("binding"),
            Status = checkpoint.GetProperty("status"),
            Page = checkpoint.GetProperty("page"),
            Request = checkpoint.GetProperty("request"),
            Artifacts = golden.GetProperty("receive").GetProperty("artifacts").EnumerateArray().Select(item => new ArchiveArtifact(item.GetProperty("artifactId").GetString()!, Convert.FromBase64String(item.GetProperty("base64").GetString()!))).ToArray(),
        };
        using (var store = await SqliteArchiveStore.OpenAsync(options)) Assert.Equal(Text(golden.GetProperty("request").GetProperty("previous")), Text(await store.ReceiveAsync(input)));
        options.Mode = StorageOpenMode.MigrateV1;
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(options)) Assert.Equal(golden.GetProperty("wire").GetProperty("request").GetProperty("canonical").GetString(), Text(await store.PrepareAckRebaseAsync(golden.GetProperty("request").GetProperty("request"))));
        using (var db = Raw(f.Path))
        {
            foreach (var ddl in golden.GetProperty("sqlite").GetProperty("ddl").EnumerateArray())
            {
                using var query = db.CreateCommand(); query.CommandText = "SELECT sql FROM sqlite_master WHERE name=$name"; query.Parameters.AddWithValue("$name", ddl.GetProperty("name").GetString());
                Assert.Equal(ddl.GetProperty("sql").GetString(), query.ExecuteScalar());
            }
            AssertGoldenLedger(db, golden.GetProperty("sqlite").GetProperty("prepared"));
        }
        options.Mode = StorageOpenMode.Reopen;
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(options))
        {
            await store.ConfirmAckRebaseAsync(golden.GetProperty("response")); Assert.Equal(Text(golden.GetProperty("syncPage")), Text((await store.SyncPageAsync(null))!.Value));
        }
        using (var db = Raw(f.Path)) AssertGoldenLedger(db, golden.GetProperty("sqlite").GetProperty("rebased"));
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(options)) Assert.Null(await store.PendingAsync());
    }

    private static void AssertGoldenLedger(SqliteConnection db, JsonElement expected)
    {
        foreach (string column in new[] { "request", "previous_request", "intent", "result", "original_receipt" })
        {
            var value = expected.GetProperty(column); object desired = value.ValueKind == JsonValueKind.Null ? DBNull.Value : value.ValueKind == JsonValueKind.String ? value.GetString()! : Text(value);
            Assert.Equal(desired, Scalar(db, "SELECT " + column + " FROM ack_rebases"));
        }
        Assert.Equal(expected.GetProperty("reserveBytes").GetInt64(), Scalar(db, "SELECT length(reserve) FROM ack_rebases"));
    }

    [Fact]
    public async Task ExplicitMigrationKeepsTheOriginalBatchAndRebasesExactlyOneOperationAndCheckpoint()
    {
        using var f = new Fixture(); JsonElement previous, intent, result;
        using (var original = await SqliteArchiveStore.OpenAsync(f.Options())) previous = await original.ReceiveAsync(f.Input);
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.Reopen)));
        using (var migrated = await SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.MigrateV1)))
        {
            Assert.Equal(Text(previous), Text((await migrated.PendingAsync())!.Value)); Assert.Equal(f.Body, await migrated.BodyAsync(f.Reference));
            intent = await migrated.PrepareAckRebaseAsync(Request); result = Result(f, intent);
            Assert.Equal(Text(intent), Text(await migrated.PrepareAckRebaseAsync(Request)));
            Assert.Equal(Text(previous), Text((await migrated.PendingAsync())!.Value));
            Assert.Equal("pending_ack", (await Assert.ThrowsAsync<StorageException>(() => migrated.PrepareAckRebaseAsync(Replace(Request, "requestId", Element("other"))))).Code);
        }
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenAsync(f.Options(StorageOpenMode.Reopen)));
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.MigrateV1)));
        using (var reopened = await SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.Reopen)))
        {
            Assert.Equal(Text(intent), Text((await reopened.PendingAckRebaseAsync())!.Value));
            await reopened.ConfirmAckRebaseAsync(result); await reopened.ConfirmAckRebaseAsync(result);
            Assert.Null(await reopened.PendingAsync()); Assert.Null(await reopened.PendingAckRebaseAsync()); Assert.Equal(f.Body, await reopened.BodyAsync(f.Reference));
            Assert.Null(await reopened.ReplicaOperationAsync(previous.GetProperty("request")));
            var page = (await reopened.SyncPageAsync(null))!.Value;
            Assert.Equal(Text(result.GetProperty("next")), Text(page.GetProperty("ack"))); Assert.Equal(Text(result.GetProperty("receipt")), Text(page.GetProperty("receipt")));
            var checkpoint = page.GetProperty("checkpoint"); Assert.Equal(Text(Request), Text(checkpoint.GetProperty("request")));
            Assert.Equal("6", checkpoint.GetProperty("binding").GetProperty("revision").GetString()); Assert.Equal("6", checkpoint.GetProperty("status").GetProperty("revision").GetString());
            Assert.Equal(Text(f.Input.Page), Text(checkpoint.GetProperty("page")));
            using var cacheFixture = new Fixture(); var options = cacheFixture.Options(); options.SyncRole = "cache";
            using var cache = await SqliteArchiveStore.OpenAsync(options);
            await cache.ReceiveSyncAsync(new ArchiveSyncReceiveInput { Page = page, Artifacts = f.Input.Artifacts }); Assert.Equal(f.Body, await cache.BodyAsync(f.Reference));
        }
        using (var reopened = await SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.Reopen))) await reopened.ConfirmAckRebaseAsync(result);
        using var db = Raw(f.Path); Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM operations")); Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM checkpoints")); Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM records"));
        Assert.Null(Scalar(db, "PRAGMA foreign_key_check")); Assert.Equal(Text(intent), Scalar(db, "SELECT intent FROM ack_rebases")); Assert.Equal(Text(result), Scalar(db, "SELECT result FROM ack_rebases"));
    }

    [Fact]
    public async Task OriginalReceiptFinishesThePreparedIntentWithoutRewritingTheAck()
    {
        using var f = new Fixture(); JsonElement previous, intent;
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options()))
        {
            previous = await store.ReceiveAsync(f.Input); intent = await store.PrepareAckRebaseAsync(Request);
            await store.ConfirmAsync(f.Receipt(previous)); await store.ConfirmAsync(f.Receipt(previous));
            Assert.Null(await store.PendingAckRebaseAsync()); Assert.Null(await store.PendingAsync());
            Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.ConfirmAckRebaseAsync(Result(f, intent)))).Code);
        }
        using (var reopened = await SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.Reopen))) { Assert.Null(await reopened.PendingAckRebaseAsync()); Assert.Equal(f.Body, await reopened.BodyAsync(f.Reference)); }
        using var db = Raw(f.Path); Assert.Equal(DBNull.Value, Scalar(db, "SELECT result FROM ack_rebases"));
        Assert.Equal(Text(f.Receipt(previous)), Scalar(db, "SELECT original_receipt FROM ack_rebases")); Assert.Equal(Text(previous), Scalar(db, "SELECT ack FROM operations"));
    }

    [Fact]
    public async Task LongRecoveryKeyHasAllOperationAndCheckpointGrowthReservedBeforePosting()
    {
        using var f = new Fixture(); JsonElement intent;
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options())) { await store.ReceiveAsync(f.Input); intent = await store.PrepareAckRebaseAsync(Replace(Request, "requestId", Element(new string('r', 128)))); }
        long reserved; using (var db = Raw(f.Path)) reserved = (long)Scalar(db, "SELECT logical_bytes FROM state")!;
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.Reopen))) await store.ConfirmAckRebaseAsync(Result(f, intent));
        using (var db = Raw(f.Path)) Assert.Equal(reserved, Scalar(db, "SELECT logical_bytes FROM state"));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("coverage")]
    [InlineData("digest")]
    [InlineData("generation")]
    public async Task ConflictingRecoveryFactsCannotChangeTheDurableIntent(string field)
    {
        using var f = new Fixture(); using var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options());
        var previous = await store.ReceiveAsync(f.Input); var intent = await store.PrepareAckRebaseAsync(Request); var result = Result(f, intent);
        var next = result.GetProperty("next");
        var bad = field switch
        {
            "key" => Replace(result, "request", Replace(Request, "requestId", Element("unknown"))),
            "revision" => Replace(result, "next", Replace(next, "expectedRevision", previous.GetProperty("expectedRevision"))),
            "source" => Replace(result, "next", Replace(next, "sourceId", Element("other"))),
            "coverage" => Replace(result, "next", Replace(next, "coverage", Replace(next.GetProperty("coverage"), "headDigest", Element(new string('b', 64))))),
            "generation" => Replace(result, "next", Replace(next, "generations", Replace(next.GetProperty("generations"), "projectionRevision", Element("1")))),
            _ => Replace(result, "receipt", Replace(result.GetProperty("receipt"), "semanticDigest", Element(new string('b', 64)))),
        };
        await Rejected(() => store.ConfirmAckRebaseAsync(bad));
        Assert.Equal(Text(previous), Text((await store.PendingAsync())!.Value)); Assert.Equal(Text(intent), Text((await store.PendingAckRebaseAsync())!.Value)); Assert.Equal(f.Body, await store.BodyAsync(f.Reference));
        await store.ConfirmAckRebaseAsync(result);
        await Rejected(() => store.ConfirmAckRebaseAsync(Replace(result, "receipt", Replace(result.GetProperty("receipt"), "outcomeRef", Element("altered")))));
        Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.ConfirmAsync(f.Receipt(previous)))).Code);
    }

    [Fact]
    public async Task SameKeyDifferentEpochAndUnsupportedMediaFailBeforeCreatingIntentOrFile()
    {
        using var f = new Fixture(); using var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options()); var previous = await store.ReceiveAsync(f.Input);
        Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.PrepareAckRebaseAsync(previous.GetProperty("request")))).Code);
        Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.PrepareAckRebaseAsync(Replace(Request, "operationEpoch", Element("other"))))).Code); Assert.Null(await store.PendingAckRebaseAsync());
        using var encrypted = new Fixture(true); await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenRecoverableAsync(encrypted.Options())); Assert.False(File.Exists(encrypted.Path));
        using var cache = new Fixture(); var options = cache.Options(); options.SyncRole = "cache";
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenRecoverableAsync(options)); Assert.False(File.Exists(cache.Path));
    }

    [Fact]
    public async Task PrepareQuotaFailureLeavesTheOriginalPendingUsable()
    {
        long used; using (var measurement = new Fixture())
        {
            using (var store = await SqliteArchiveStore.OpenRecoverableAsync(measurement.Options())) await store.ReceiveAsync(measurement.Input);
            using var db = Raw(measurement.Path); used = (long)Scalar(db, "SELECT logical_bytes FROM state")!;
        }
        using var f = new Fixture(); var options = f.Options(); options.Limits.MaxStoredBytes = used + 100;
        using var bounded = await SqliteArchiveStore.OpenRecoverableAsync(options); var previous = await bounded.ReceiveAsync(f.Input);
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => bounded.PrepareAckRebaseAsync(Request))).Code);
        Assert.Null(await bounded.PendingAckRebaseAsync()); Assert.Equal(Text(previous), Text((await bounded.PendingAsync())!.Value)); await bounded.ConfirmAsync(f.Receipt(previous));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedRecoveryPermanentlyReservesBothRequestKeysForFutureBatches(bool oldKey)
    {
        using var f = new Fixture(); using var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options());
        var previous = await store.ReceiveAsync(f.Input); var intent = await store.PrepareAckRebaseAsync(Request); await store.ConfirmAckRebaseAsync(Result(f, intent));
        var input = NextInput(f, oldKey ? previous.GetProperty("request") : Request);
        Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(input))).Code); Assert.Null(await store.PendingAsync());
        Assert.Equal("1", (await store.HeadAsync())!.Value.GetProperty("sequence").GetString());
    }

    [Fact]
    public async Task TrustedRetentionCannotPassAnUnfinishedRecoveryAndDoesNotResurrectBodiesAfterConfirm()
    {
        using var f = new Fixture(); using var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options());
        await store.ReceiveAsync(f.Input); var intent = await store.PrepareAckRebaseAsync(Request); f.RetentionRevision = "1";
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => store.ApplyRetentionAsync(f.Retention()))).Code);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => store.ConfirmAckRebaseAsync(Result(f, intent)))).Code);
        f.RetentionRevision = "0"; await store.ConfirmAckRebaseAsync(Result(f, intent)); f.RetentionRevision = "1"; await store.ApplyRetentionAsync(f.Retention());
        Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(f.Reference))).Code);
        await store.ConfirmAckRebaseAsync(Result(f, intent)); Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(f.Reference))).Code);
    }

    [Theory]
    [InlineData("prepare", false)]
    [InlineData("prepare", true)]
    [InlineData("confirm", false)]
    [InlineData("confirm", true)]
    public async Task UnknownCommitRequiresReopenAndUsesOnlyTheOriginalRecoveryKey(string operation, bool afterCommit)
    {
        using var f = new Fixture(); var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options()); JsonElement intent;
        try
        {
            var previous = await store.ReceiveAsync(f.Input);
            intent = operation == "confirm" ? await store.PrepareAckRebaseAsync(Request) : Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", previous, request = Request });
            var connection = (SqliteConnection)typeof(SqliteArchiveStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            int commits = 0; WalHook hook = (_, _, _, _) => { commits++; return 10; };
            if (afterCommit) SqliteWalHook(connection.Handle!.DangerousGetHandle(), hook, IntPtr.Zero);
            else SQLitePCL.raw.sqlite3_commit_hook(connection.Handle!, _ => { commits++; return 1; }, null);
            try
            {
                var error = await Assert.ThrowsAsync<StorageException>(() => operation == "confirm" ? store.ConfirmAckRebaseAsync(Result(f, intent)) : store.PrepareAckRebaseAsync(Request));
                Assert.Equal("reconciliation_required", error.Code); Assert.Equal(1, commits);
            }
            finally { SqliteWalHook(connection.Handle!.DangerousGetHandle(), null, IntPtr.Zero); SQLitePCL.raw.sqlite3_commit_hook(connection.Handle!, null, null); GC.KeepAlive(hook); }
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => store.PendingAckRebaseAsync())).Code);
        }
        finally { store.Dispose(); }
        using var reopened = await SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.Reopen));
        if (operation == "prepare")
        {
            if (afterCommit) Assert.Equal(Text(intent), Text((await reopened.PendingAckRebaseAsync())!.Value)); else Assert.Null(await reopened.PendingAckRebaseAsync());
            Assert.NotNull(await reopened.PendingAsync()); Assert.Equal(Text(intent), Text(await reopened.PrepareAckRebaseAsync(Request)));
        }
        else
        {
            Assert.Equal(afterCommit, await reopened.PendingAsync() == null); Assert.Equal(afterCommit, await reopened.PendingAckRebaseAsync() == null);
            await reopened.ConfirmAckRebaseAsync(Result(f, intent)); Assert.Null(await reopened.PendingAsync());
        }
        Assert.Equal(f.Body, await reopened.BodyAsync(f.Reference));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("cancel")]
    [InlineData("reentry")]
    public async Task AFailedAuthorityCheckCannotPublishAnIntent(string failure)
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource(); var options = f.Options(); SqliteArchiveStore? store = null; bool active = false; int calls = 0;
        options.ReadContext = () =>
        {
            if (active && ++calls == 3)
            {
                if (failure == "scope") f.User = "other";
                else if (failure == "cancel") cancellation.Cancel();
                else { try { store!.PendingAsync().GetAwaiter().GetResult(); } catch (StorageException) { } }
            }
            return f.Scope;
        };
        using (store = await SqliteArchiveStore.OpenRecoverableAsync(options))
        {
            var previous = await store.ReceiveAsync(f.Input); active = true;
            if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PrepareAckRebaseAsync(Request, cancellation.Token));
            else Assert.Equal(failure == "scope" ? "identity_mismatch" : "reentrant", (await Assert.ThrowsAsync<StorageException>(() => store.PrepareAckRebaseAsync(Request, cancellation.Token))).Code);
            active = false; f.User = "user";
            Assert.Null(await store.PendingAckRebaseAsync()); Assert.Equal(Text(previous), Text((await store.PendingAsync())!.Value));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetentionCallbackCannotSwitchPrincipalAfterTheLastScopeCheckAndCommit(bool confirm)
    {
        using var f = new Fixture(); var options = f.Options(); bool active = false; int checks = 0;
        options.ReadRetentionRevision = () => { if (active && ++checks == 3) f.User = "other"; return f.RetentionRevision; };
        using var store = await SqliteArchiveStore.OpenRecoverableAsync(options); var previous = await store.ReceiveAsync(f.Input);
        JsonElement? intent = confirm ? await store.PrepareAckRebaseAsync(Request) : null; active = true;
        var error = await Assert.ThrowsAsync<StorageException>(() => confirm ? store.ConfirmAckRebaseAsync(Result(f, intent!.Value)) : store.PrepareAckRebaseAsync(Request));
        Assert.Equal("identity_mismatch", error.Code); active = false; f.User = "user";
        Assert.Equal(Text(previous), Text((await store.PendingAsync())!.Value));
        if (confirm) Assert.Equal(Text(intent!.Value), Text((await store.PendingAckRebaseAsync())!.Value)); else Assert.Null(await store.PendingAckRebaseAsync());
    }

    [Theory]
    [InlineData("receive")]
    [InlineData("confirm")]
    [InlineData("read")]
    public async Task OriginalStorePathsRejectPrincipalChangesInsideTheFinalRetentionCallback(string operation)
    {
        using var f = new Fixture(); var options = f.Options(); bool active = false; int checks = 0;
        options.ReadRetentionRevision = () => { if (active && ++checks == 3) f.User = "other"; return f.RetentionRevision; };
        using var store = await SqliteArchiveStore.OpenAsync(options); JsonElement? previous = operation == "receive" ? null : await store.ReceiveAsync(f.Input); active = true;
        var error = await Assert.ThrowsAsync<StorageException>(() => operation switch
        {
            "receive" => store.ReceiveAsync(f.Input),
            "confirm" => store.ConfirmAsync(f.Receipt(previous!.Value)),
            _ => store.BodyAsync(f.Reference),
        });
        Assert.Equal("identity_mismatch", error.Code); active = false; f.User = "user";
        if (operation == "receive") { Assert.Null(await store.HeadAsync()); Assert.Null(await store.PendingAsync()); }
        else { Assert.Equal(Text(previous!.Value), Text((await store.PendingAsync())!.Value)); Assert.Equal(f.Body, await store.BodyAsync(f.Reference)); }
    }

    [Fact]
    public async Task SameSizeLedgerTamperingFailsAuditAndCorruptOldStoresCannotBeMigrated()
    {
        using var f = new Fixture();
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(f.Options())) { await store.ReceiveAsync(f.Input); await store.PrepareAckRebaseAsync(Request); }
        using (var db = Raw(f.Path)) Scalar(db, "UPDATE ack_rebases SET intent=replace(intent,'rebase-ack-one','rebase-ack-two')");
        await Rejected(() => SqliteArchiveStore.OpenRecoverableAsync(f.Options(StorageOpenMode.Reopen)));
        using var old = new Fixture(); using (var store = await SqliteArchiveStore.OpenAsync(old.Options())) await store.ReceiveAsync(old.Input);
        using (var db = Raw(old.Path)) Scalar(db, "UPDATE artifacts SET body=zeroblob(length(body))");
        await Rejected(() => SqliteArchiveStore.OpenRecoverableAsync(old.Options(StorageOpenMode.MigrateV1)));
        using (var db = Raw(old.Path)) Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name='ack_rebases'"));
    }

    [Fact]
    public async Task RevocationAtMigrationCommitRollsBackDdlAndPreservesTheOldReader()
    {
        using var f = new Fixture(); JsonElement previous;
        using (var old = await SqliteArchiveStore.OpenAsync(f.Options())) previous = await old.ReceiveAsync(f.Input);
        var options = f.Options(StorageOpenMode.MigrateV1); int checks = 0;
        options.ReadContext = () => { if (++checks == 5) f.User = "other"; return f.Scope; };
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenRecoverableAsync(options))).Code); f.User = "user";
        using (var db = Raw(f.Path))
        {
            Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name='ack_rebases'"));
            using var metadata = JsonDocument.Parse((string)Scalar(db, "SELECT json FROM metadata")!); Assert.Equal(SqliteArchiveStore.Format, metadata.RootElement.GetProperty("format").GetString());
        }
        using var original = await SqliteArchiveStore.OpenAsync(f.Options(StorageOpenMode.Reopen));
        Assert.Equal(Text(previous), Text((await original.PendingAsync())!.Value)); Assert.Equal(f.Body, await original.BodyAsync(f.Reference));
    }

    private static SqliteConnection Raw(string path) { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
    private static string SourcePath([CallerFilePath] string path = "") => path;
    private static ArchiveReceiveInput NextInput(Fixture fixture, JsonElement request)
    {
        var first = fixture.Input.Page.GetProperty("records")[0];
        var unsigned = first.EnumerateObject().Where(item => item.Name != "recordDigest").ToDictionary(item => item.Name, item => item.Value.Clone());
        unsigned["recordId"] = Element("record-2"); unsigned["sequence"] = Element("2"); unsigned["predecessorDigest"] = first.GetProperty("recordDigest");
        var record = new Dictionary<string, JsonElement>(unsigned) { ["recordDigest"] = Element(WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(Element(unsigned)))) };
        var coverage = Element(new { fromSequence = "1", throughSequence = "1", headDigest = first.GetProperty("recordDigest").GetString() });
        return new ArchiveReceiveInput
        {
            Binding = fixture.Input.Binding,
            Request = request,
            Artifacts = fixture.Input.Artifacts,
            Status = Replace(Replace(fixture.Input.Status, "publishedThroughSequence", Element("2")), "acknowledgedCoverage", coverage),
            Page = Replace(Replace(Replace(fixture.Input.Page, "publishedThroughSequence", Element("2")), "nextAfterSequence", Element("2")), "records", Element(new[] { record })),
        };
    }
    private static object? Scalar(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static async Task Rejected(Func<Task> action)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        Assert.True(error is StorageException or TansrProtocolException or WireProtocolException, error.GetType().FullName);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WalHook(IntPtr argument, IntPtr database, IntPtr name, int pages);
    // SQLite invokes the WAL callback after committing; a nonzero return reports the error to COMMIT's caller.
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_wal_hook", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SqliteWalHook(IntPtr database, WalHook? callback, IntPtr argument);
}
