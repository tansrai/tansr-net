using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteArchiveStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableOriginalAckSurvivesRestartAndConfirmIsImmutable(bool encrypted)
    {
        using var fixture = new Fixture(encrypted); JsonElement ack;
        using (var store = await SqliteArchiveStore.OpenAsync(fixture.Options()))
        {
            Assert.Null(await store.HeadAsync()); ack = await store.ReceiveAsync(fixture.Input);
            Assert.Equal(fixture.Body, await store.BodyAsync(fixture.Reference));
            Assert.Equal("pending_ack", (await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(fixture.Input))).Code);
        }
        using (var store = await SqliteArchiveStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen)))
        {
            Assert.Equal(WireJson.CanonicalString(ack), WireJson.CanonicalString((await store.PendingAsync())!.Value));
            var receipt = fixture.Receipt(ack); await store.ConfirmAsync(receipt); await store.ConfirmAsync(receipt);
            Assert.Null(await store.PendingAsync());
            Assert.Equal(fixture.Body, await store.BodyChunkAsync(fixture.Reference, 0));
            var page = await store.ReadRecordsAsync(new ArchiveReadRequest { Identity = fixture.Identity, Selection = Element(new { fromSequence = "1", throughSequence = "1" }) });
            Assert.Single(page.Records); Assert.True(page.SourceCoverage.GetProperty("complete").GetBoolean());
            Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.ConfirmAsync(Replace(receipt, "outcomeRef", Element("other"))))).Code);
        }
        using (var store = await SqliteArchiveStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen)))
        { Assert.Null(await store.PendingAsync()); Assert.Equal("1", (await store.CoverageAsync()).GetProperty("throughSequence").GetString()); }
    }

    [Fact]
    public async Task RangeCannotClaimUnpublishedCoverage()
    {
        using var fixture = new Fixture(); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options());
        var request = new ArchiveReadRequest { Identity = fixture.Identity, Selection = Element(new { fromSequence = "1", throughSequence = "1" }) };
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => store.ReadRecordsAsync(request))).Code);
        await store.ReceiveAsync(fixture.Input); request.Selection = Element(new { fromSequence = "1", throughSequence = "2" });
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => store.ReadRecordsAsync(request))).Code);
    }

    [Fact]
    public async Task TamperedBodyNeverCreatesAckOrPartialChain()
    {
        using var fixture = new Fixture(); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options());
        fixture.Input.Artifacts[0].Body[0] ^= 1;
        await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(fixture.Input)); Assert.Null(await store.PendingAsync()); Assert.Null(await store.HeadAsync());
    }

    [Fact]
    public async Task QuotaFailureRollsBackEntireBatch()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.Limits.MaxStoredBytes = 2000;
        using var store = await SqliteArchiveStore.OpenAsync(options);
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(fixture.Input))).Code);
        Assert.Null(await store.PendingAsync()); Assert.Null(await store.HeadAsync());
    }

    [Fact]
    public async Task TombstoneKeepsChainAndReceiptsButRemovesBodyAndOldBackupAuthority()
    {
        using var fixture = new Fixture(true); JsonElement ack;
        using (var store = await SqliteArchiveStore.OpenAsync(fixture.Options()))
        {
            ack = await store.ReceiveAsync(fixture.Input); await store.ConfirmAsync(fixture.Receipt(ack));
            fixture.RetentionRevision = "1";
            Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(fixture.Reference))).Code);
            var retention = fixture.Retention(); await store.ApplyRetentionAsync(retention); await store.ApplyRetentionAsync(retention);
            Assert.Equal("1", await store.RetentionRevisionAsync()); Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(fixture.Reference))).Code);
            Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => store.ReadRecordsAsync(new ArchiveReadRequest { Identity = fixture.Identity, Selection = Element(new { recordIds = new[] { "record" } }) }))).Code);
            Assert.Equal("1", (await store.HeadAsync())!.Value.GetProperty("sequence").GetString()); Assert.NotNull(await store.RetentionPageAsync("0"));
        }
        using (var store = await SqliteArchiveStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen)))
        { Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(fixture.Reference))).Code); await store.ConfirmAsync(fixture.Receipt(ack)); }
        using var connection = new SqliteConnection("Data Source=" + fixture.Path + ";Pooling=False"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT length(body) FROM artifacts"; Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ScopeAndKeyRevocationFailClosedAndStillAllowClose()
    {
        using var fixture = new Fixture(true); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options()); await store.ReceiveAsync(fixture.Input);
        fixture.User = "other"; Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(fixture.Reference))).Code);
        fixture.User = "user"; fixture.Keys!.Available = false; await Assert.ThrowsAsync<StorageException>(() => store.PendingAsync()); await store.CloseAsync();
    }

    [Fact]
    public async Task CorruptionCannotBeReopenedAsFreshStore()
    {
        using var fixture = new Fixture(); using (var store = await SqliteArchiveStore.OpenAsync(fixture.Options())) await store.ReceiveAsync(fixture.Input);
        using (var connection = new SqliteConnection("Data Source=" + fixture.Path + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE artifacts SET body=zeroblob(length(body))"; command.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen)));
    }

    [Fact]
    public async Task AuthorizationChangesDuringCommitRollBackWithoutAck()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); int calls = 0; bool change = false;
        options.ReadContext = () => { if (change && ++calls == 3) fixture.User = "other"; return fixture.Scope; };
        using var store = await SqliteArchiveStore.OpenAsync(options); change = true;
        await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(fixture.Input)); change = false; fixture.User = "user";
        Assert.Null(await store.HeadAsync()); Assert.Null(await store.PendingAsync());
    }

    [Fact]
    public async Task EncryptedChunkCrossesSegmentsAndOriginalPlaintextIsNotStored()
    {
        var body = Encoding.UTF8.GetBytes(new string('q', 524313)); using var fixture = new Fixture(true, body); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options()); await store.ReceiveAsync(fixture.Input);
        Assert.Equal(body.Skip(262139).Take(262144).ToArray(), await store.BodyChunkAsync(fixture.Reference, 262139));
        Assert.Equal(body.Skip(524300).ToArray(), await store.BodyChunkAsync(fixture.Reference, 524300)); Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => store.BodyChunkAsync(fixture.Reference, body.Length))).Code);
    }

    [Fact]
    public async Task RetentionAndTombstonesShareTheOriginalRecordCountCap()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.Limits.MaxRecords = 1; options.AuthorizeRetention = _ => { };
        using var store = await SqliteArchiveStore.OpenAsync(options); var ack = await store.ReceiveAsync(fixture.Input); await store.ConfirmAsync(fixture.Receipt(ack));
        fixture.RetentionRevision = "1"; await store.ApplyRetentionAsync(fixture.Retention()); fixture.RetentionRevision = "2";
        var second = Replace(Replace(Replace(fixture.Retention(), "revision", Element("2")), "previousRevision", Element("1")), "requestId", Element("delete-again"));
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => store.ApplyRetentionAsync(second))).Code);
        Assert.Equal("1", await store.RetentionRevisionAsync());
    }

    [Fact]
    public async Task KeyProviderCannotChangeSubjectAtTheLastReadAndReceivePlaintext()
    {
        using var fixture = new Fixture(true); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options()); await store.ReceiveAsync(fixture.Input); int calls = 0;
        fixture.Keys!.OnRead = () => { if (++calls == 3) fixture.User = "other"; };
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(fixture.Reference))).Code);
    }

    [Fact]
    public async Task SwallowedKeyProviderReentryCannotCommitAnAck()
    {
        using var fixture = new Fixture(true); using var store = await SqliteArchiveStore.OpenAsync(fixture.Options()); int calls = 0;
        fixture.Keys!.OnRead = () =>
        {
            if (++calls != 3) return;
            try { store.HeadAsync().GetAwaiter().GetResult(); } catch (StorageException) { }
        };
        Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(fixture.Input))).Code); fixture.Keys.OnRead = null;
        Assert.Null(await store.PendingAsync()); Assert.Null(await store.HeadAsync());
    }

    [Theory]
    [InlineData("C:archive.sqlite")]
    [InlineData("\\archive.sqlite")]
    public async Task RelativeDrivePathsAreRejectedBeforeOpening(string path)
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.Path = path;
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenAsync(options))).Code);
    }

    internal static JsonElement Element(object value) => WireJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value), 1572864);
    internal static JsonElement Replace(JsonElement value, string name, JsonElement replacement)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); foreach (var item in value.EnumerateObject()) { if (item.Name == name) { writer.WritePropertyName(name); replacement.WriteTo(writer); } else item.WriteTo(writer); } writer.WriteEndObject(); }
        return WireJson.Parse(stream.ToArray(), 1572864);
    }
    private static JsonElement AddDigest(JsonElement unsigned, string domain)
    {
        var items = unsigned.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()); items.Add("recordDigest", Element(WireJson.DomainDigest(domain, WireJson.EncodeControl(unsigned)))); return Element(items);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr-net-archive-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "archive.sqlite");
        internal string User { get; set; } = "user";
        internal string RetentionRevision { get; set; } = "0";
        internal JsonElement Scope => Element(new { applicationScopeId = "app", endUserId = User, authorizationRevision = "7" });
        internal byte[] Body { get; }
        internal JsonElement Identity { get; }
        internal JsonElement Reference { get; }
        internal ArchiveReceiveInput Input { get; }
        internal SyntheticKeys? Keys { get; }
        internal Fixture(bool encrypted = false, byte[]? body = null)
        {
            System.IO.Directory.CreateDirectory(Directory); Keys = encrypted ? new SyntheticKeys() : null; Body = body ?? Encoding.UTF8.GetBytes("Synthetic 原档案 😀");
            var generations = new { historyEpoch = "history", deletionGeneration = "0", projectionRevision = "0" }; var target = new { sessionId = "session/中文", sourceSnapshotDigest = new string('a', 64), generations };
            Identity = Element(new { scope = new { applicationScopeId = "app", endUserId = "user" }, bindingId = "binding", target = new { target.sessionId, generations }, sourceId = "source", sourceGeneration = "source-generation" });
            Reference = Element(new { artifactId = "body", sourceId = "source", bytes = Body.Length, sha256 = WireJson.Sha256(Body), mediaType = "application/json" });
            var record = AddDigest(Element(new { recordId = "record", sequence = "1", target, turnId = "turn", recordKind = "turn", turnState = "completed", predecessorDigest = new string('0', 64), payload = Reference, attachments = Array.Empty<object>(), payloadDigest = WireJson.DomainDigest("tansr.sdk2.payload.v1", Body) }), "tansr.sdk2.record.v1");
            var limits = new { controlBytes = 262144, recordBytes = 262144, pageRecords = 128, pageBytes = 1048576, attachmentBytes = 33554432, chunkBytes = 262144, materialConcurrent = 2, materialQueue = 16, materialCandidates = 32, materialBytes = 1048576, materialDeadlineMs = 30000, pendingRecords = 4096, pendingBytes = 67108864, inflightReserveBytes = 16777216, offlineMs = 86400000, eventRetentionMs = 600000, eventRetentionFrames = 4096, eventRetentionBytes = 8388608, terminalReceiptRetentionMs = 604800000, epochLifetimeMs = 86400000, materialChunkBytes = 65536 };
            Input = new ArchiveReceiveInput
            {
                Binding = Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", scope = Scope, target, revision = "2", state = "active", sourceId = "source", acceptedCapabilities = new[] { "archive-transfer-v1", "context-materials-v1" }, rejectedCapabilities = Array.Empty<object>(), availability = "legacy-complete", operationEpoch = new { id = "epoch", issuedAt = "2026-09-20T00:00:00Z", expiresAt = "2026-09-20T00:01:00Z", state = "active" }, limits, archiveAckFormat = "split-receipts-v1" }),
                Status = Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", revision = "2", generations, sourceId = "source", sourceGeneration = "source-generation", publishedThroughSequence = "1", acknowledgedCoverage = (object?)null, releasableThroughSequence = (object?)null, pendingBytes = Body.Length, pendingRecords = 1, sessionPersistence = "unchanged", state = "active" }),
                Page = Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", generations, records = new[] { record }, nextAfterSequence = "1", complete = true, publishedThroughSequence = "1" }),
                Request = Element(new { operationEpoch = "epoch", requestId = "ack" }),
                Artifacts = new[] { new ArchiveArtifact("body", (byte[])Body.Clone()) },
            };
        }
        internal SqliteArchiveStoreOptions Options(StorageOpenMode mode = StorageOpenMode.Create) => new SqliteArchiveStoreOptions
        {
            Path = Path,
            Mode = mode,
            Identity = Identity,
            Replica = Element(new { replicationId = "group", role = "primary" }),
            ReadContext = () => Scope,
            ReadRetentionRevision = () => RetentionRevision,
            AuthorizeRetention = value => Assert.Equal(WireJson.CanonicalString(Retention()), WireJson.CanonicalString(value)),
            KeyProvider = Keys,
        };
        internal JsonElement Receipt(JsonElement ack)
        {
            var semantic = Element(ack.EnumerateObject().Where(x => x.Name != "request").ToDictionary(x => x.Name, x => x.Value.Clone()));
            return Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", request = ack.GetProperty("request"), operation = "archive-ack", semanticDigest = WireJson.DomainDigest("tansr.sdk2.operation.v1", WireJson.EncodeControl(Element(new { scope = new[] { "app", "user" }, operation = "archive-ack", semantic }))), state = "completed", revision = "3", outcomeRef = "binding" });
        }
        internal JsonElement Retention() => Element(new { format = "archive-retention-v1", identity = Identity, revision = "1", previousRevision = "0", requestId = "delete", records = new[] { new { recordId = "record", sequence = "1", recordDigest = Input.Page.GetProperty("records")[0].GetProperty("recordDigest").GetString() } } });
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    internal sealed class SyntheticKeys : IArchiveKeyProvider
    {
        public string KeyId => "key";
        internal bool Available { get; set; } = true;
        internal Action? OnRead { get; set; }
        public byte[] ReadKey() { OnRead?.Invoke(); if (!Available) throw new StorageException("context_changed"); return Enumerable.Range(0, 32).Select(x => (byte)x).ToArray(); }
    }
}
