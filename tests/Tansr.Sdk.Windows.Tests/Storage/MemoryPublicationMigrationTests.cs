using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using Fixture = Tansr.Sdk.Windows.Tests.Storage.SqliteMemoryPublicationStoreTests.Fixture;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class MemoryPublicationMigrationTests
{
    private const string PrivateMarker = "migration-private-memory-秘密-";
    private static readonly string[] TransferIds = { "original", "winner", "conflict", "pending" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyPreservesPublicationOriginalReceiptsAndPartialCursorWithoutChangingSource(bool encryptedSource)
    {
        using var fixture = new Fixture { MaxTransfers = 9 };
        var oldKey = encryptedSource ? new Key("old-key", 7) : null;
        var newKey = new Key("new-key", 19);
        string destination = Path.Combine(fixture.Directory, "migrated.sqlite"), staging = Path.Combine(fixture.Directory, "migration-stage.sqlite");
        var seeded = await SeedAsync(fixture, oldKey);
        string sourceHash = Hash(fixture.Path);
        Dictionary<string, string> receipts;
        SqliteMemoryPublicationCapacity capacity;
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, oldKey, StorageOpenMode.Reopen)))
        {
            receipts = await QueriesAsync(fixture, source);
            capacity = await source.GetCapacityAsync();
            await source.CopyToEncryptedAsync(destination, staging, newKey);
            Assert.Equal(encryptedSource, source.EncryptedBody);
            Assert.Equal(seeded.Published, await fixture.ReadAll(source));
            Assert.Equal(seeded.Published, await source.ReadPublicationAsync());
            Assert.Equal(receipts, await QueriesAsync(fixture, source));
        }
        Assert.Equal(sourceHash, Hash(fixture.Path));
        Assert.False(File.Exists(staging));
        Assert.True(File.Exists(destination));
        ProbeEncryptedFiles(destination);

        using (var migrated = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, newKey, StorageOpenMode.Reopen, destination)))
        {
            Assert.True(migrated.EncryptedBody); Assert.True(migrated.AtomicDurablePublication);
            Assert.Equal(WireJson.CanonicalString(fixture.Identity), WireJson.CanonicalString(migrated.Identity));
            Assert.Equal(receipts, await QueriesAsync(fixture, migrated));
            var migratedCapacity = await migrated.GetCapacityAsync();
            Assert.Equal(capacity.MaxTransfers, migratedCapacity.MaxTransfers);
            Assert.Equal(capacity.MaxStagingBytes, migratedCapacity.MaxStagingBytes);
            Assert.Equal(capacity.MaxPages, migratedCapacity.MaxPages);
            Assert.Equal(capacity.StoredTransfers, migratedCapacity.StoredTransfers);
            Assert.Equal(capacity.StagingBytes, migratedCapacity.StagingBytes);
            Assert.Equal(seeded.Published, await fixture.ReadAll(migrated));
            foreach (string id in new[] { "original", "winner", "conflict" })
            {
                var query = WireJson.Parse(Encoding.UTF8.GetBytes(receipts[id])).GetProperty("transfer");
                var replay = (await fixture.Run(migrated, "commit", new { transferId = id })).GetProperty("transfer");
                Assert.Equal(WireJson.CanonicalString(query), WireJson.CanonicalString(replay));
            }
            Assert.Equal(seeded.Published, await migrated.ReadPublicationAsync());
            AssertTransfer(await fixture.Run(migrated, "query", new { transferId = "pending" }), "staging", seeded.Received, null);
            // The original begin and already accepted chunk remain idempotent after migration.
            await fixture.Run(migrated, "begin", fixture.Begin("pending", seeded.Pending, WireJson.Sha256(seeded.Published)));
            await fixture.Run(migrated, "chunk", fixture.Chunk("pending", 0, seeded.Pending.Take(seeded.Received).ToArray()));
            for (int offset = seeded.Received; offset < seeded.Pending.Length; offset += SqliteMemoryPublicationStore.MaximumChunkBytes)
                await fixture.Run(migrated, "chunk", fixture.Chunk("pending", offset, seeded.Pending.Skip(offset).Take(SqliteMemoryPublicationStore.MaximumChunkBytes).ToArray()));
            AssertTransfer(await fixture.Run(migrated, "commit", new { transferId = "pending" }), "committed", seeded.Pending.Length, WireJson.Sha256(seeded.Pending));
            Assert.Equal(seeded.Pending, await fixture.ReadAll(migrated));
            Assert.Equal(4, (await migrated.GetCapacityAsync()).StoredTransfers);
            Assert.Equal(0, (await migrated.GetCapacityAsync()).StagingBytes);
            ProbeEncryptedFiles(destination);
        }
        using (var migrated = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, newKey, StorageOpenMode.Reopen, destination)))
        {
            Assert.Equal(seeded.Pending, await migrated.ReadPublicationAsync());
            AssertTransfer(await fixture.Run(migrated, "query", new { transferId = "pending" }), "committed", seeded.Pending.Length, WireJson.Sha256(seeded.Pending));
            Assert.Equal("unknown", (await fixture.Run(migrated, "query", new { transferId = "never-created" })).GetProperty("transfer").GetProperty("status").GetString());
        }
        string destinationHash = Hash(destination);
        await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(Options(fixture, oldKey, StorageOpenMode.Reopen, destination)));
        Assert.Equal(destinationHash, Hash(destination));
        Assert.Equal(sourceHash, Hash(fixture.Path));
        Assert.All(newKey.Copies, copy => Assert.All(copy, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-id")]
    [InlineData("unavailable")]
    public async Task WrongOldKeyRefusesMigrationWithoutChangingSourceOrExistingDestination(string failure)
    {
        using var fixture = new Fixture(); var oldKey = new Key("old-key", 7);
        await SeedAsync(fixture, oldKey);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "existing.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        byte[] existing = Encoding.UTF8.GetBytes("existing destination must survive"); File.WriteAllBytes(destination, existing);
        var wrongKey = new Key(failure == "wrong-id" ? "other-id" : "old-key", failure == "wrong-key" ? (byte)8 : (byte)7);
        if (failure == "unavailable") wrongKey.OnRead = () => throw new IOException("synthetic missing old key");
        await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(Options(fixture, wrongKey, StorageOpenMode.Reopen)));
        Assert.Equal(sourceHash, Hash(fixture.Path)); Assert.Equal(existing, File.ReadAllBytes(destination)); Assert.False(File.Exists(staging));
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, oldKey, StorageOpenMode.Reopen));
        AssertTransfer(await fixture.Run(reopened, "query", new { transferId = "pending" }), "staging", 113, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedSourceIsRejectedAndNeverBecomesAnEmptyMigratedStore(bool encryptedSource)
    {
        using var fixture = new Fixture(); var key = encryptedSource ? new Key("old-key", 7) : null;
        await SeedAsync(fixture, key);
        using (var damaged = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.None)) damaged.SetLength(100);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        await Assert.ThrowsAsync<StorageException>(async () =>
        {
            using var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen));
            await source.CopyToEncryptedAsync(destination, staging, new Key("new-key", 19));
        });
        Assert.Equal(sourceHash, Hash(fixture.Path)); Assert.False(File.Exists(destination)); Assert.False(File.Exists(staging));
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("destination-wal")]
    [InlineData("staging")]
    [InlineData("staging-shm")]
    public async Task ExistingDestinationOrStagingMediaIsNeverOverwritten(string existingMedia)
    {
        using var fixture = new Fixture(); await SeedAsync(fixture, null);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        string preserved = existingMedia switch
        {
            "destination" => destination,
            "destination-wal" => destination + "-wal",
            "staging" => staging,
            _ => staging + "-shm",
        };
        byte[] sentinel = Encoding.UTF8.GetBytes("unowned media must survive"); File.WriteAllBytes(preserved, sentinel);
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, null, StorageOpenMode.Reopen)))
            await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, new Key("new-key", 19)));
        Assert.Equal(sentinel, File.ReadAllBytes(preserved)); Assert.Equal(sourceHash, Hash(fixture.Path));
        if (preserved != destination) Assert.False(File.Exists(destination));
        if (preserved != staging) Assert.False(File.Exists(staging));
    }

    [Fact]
    public async Task DestinationCreatedDuringCopyIsPreservedAndCompletedStagingRemainsRecoverable()
    {
        using var fixture = new Fixture(); var seeded = await SeedAsync(fixture, null);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        byte[] sentinel = Encoding.UTF8.GetBytes("destination won by another writer"); var key = new Key("new-key", 19);
        key.OnRead = () => { if (!File.Exists(destination)) File.WriteAllBytes(destination, sentinel); };
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, null, StorageOpenMode.Reopen)))
            await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, key));
        Assert.Equal(sourceHash, Hash(fixture.Path)); Assert.Equal(sentinel, File.ReadAllBytes(destination)); Assert.True(File.Exists(staging));
        key.OnRead = null;
        using var retained = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen, staging));
        Assert.Equal(seeded.Published, await retained.ReadPublicationAsync());
        AssertTransfer(await fixture.Run(retained, "query", new { transferId = "pending" }), "staging", seeded.Received, null);
        Assert.Equal(4, (await retained.GetCapacityAsync()).StoredTransfers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewKeyFailureRetainsNamedStagingAndRetryRequiresANewStagingPath(bool invalidLength)
    {
        using var fixture = new Fixture(); var seeded = await SeedAsync(fixture, null);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "failed-stage.sqlite"), retryStaging = Path.Combine(fixture.Directory, "retry-stage.sqlite");
        var failingKey = new Key("new-key", 19) { InvalidLength = invalidLength };
        if (!invalidLength) failingKey.OnRead = () => throw new IOException("synthetic destination provider failure");
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, null, StorageOpenMode.Reopen)))
            await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, failingKey));
        Assert.Equal(sourceHash, Hash(fixture.Path)); Assert.False(File.Exists(destination)); Assert.True(File.Exists(staging));
        string retainedHash = Hash(staging); var key = new Key("new-key", 19);
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, null, StorageOpenMode.Reopen)))
        {
            await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, key));
            Assert.Equal(retainedHash, Hash(staging)); Assert.False(File.Exists(destination));
            await source.CopyToEncryptedAsync(destination, retryStaging, key);
        }
        Assert.Equal(sourceHash, Hash(fixture.Path)); Assert.Equal(retainedHash, Hash(staging)); Assert.False(File.Exists(retryStaging));
        using var migrated = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen, destination));
        Assert.Equal(seeded.Published, await migrated.ReadPublicationAsync());
        AssertTransfer(await fixture.Run(migrated, "query", new { transferId = "pending" }), "staging", seeded.Received, null);
    }

    [Theory]
    [InlineData("destination-is-source")]
    [InlineData("staging-is-source")]
    [InlineData("staging-is-destination")]
    [InlineData("different-directory")]
    public async Task InvalidMigrationPathsCannotAlterTheSource(string failure)
    {
        using var fixture = new Fixture(); await SeedAsync(fixture, null);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        if (failure == "destination-is-source") destination = fixture.Path;
        else if (failure == "staging-is-source") staging = fixture.Path;
        else if (failure == "staging-is-destination") staging = destination;
        else { string subdirectory = Path.Combine(fixture.Directory, "staging"); Directory.CreateDirectory(subdirectory); staging = Path.Combine(subdirectory, "stage.sqlite"); }
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, null, StorageOpenMode.Reopen)))
            await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, new Key("new-key", 19)));
        Assert.Equal(sourceHash, Hash(fixture.Path));
        if (destination != fixture.Path) Assert.False(File.Exists(destination));
        if (staging != fixture.Path) Assert.False(File.Exists(staging));
    }

    [Fact]
    public async Task DestinationKeyRevokedAfterStagingCloseCannotPublishAUsableSuccess()
    {
        using var fixture = new Fixture(); var seeded = await SeedAsync(fixture, null);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        var key = new Key("new-key", 19); bool active = false, revoked = false;
        var options = Options(fixture, null, StorageOpenMode.Reopen);
        options.ReadContext = () =>
        {
            if (active && key.Copies.Count > 4 && File.Exists(staging) && !File.Exists(staging + "-wal"))
            { revoked = true; key.InvalidLength = true; }
            return fixture.Scope;
        };
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            active = true;
            await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, key));
            active = false; key.InvalidLength = false;
        }
        Assert.True(revoked); Assert.False(File.Exists(destination)); Assert.Equal(sourceHash, Hash(fixture.Path));
        using var retained = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen, staging));
        Assert.Equal(seeded.Published, await retained.ReadPublicationAsync());
    }

    [Fact]
    public async Task FinalReadonlyAuditPinsTheStagingBytesAgainstSameFileWritesUntilRename()
    {
        using var fixture = new Fixture(); var seeded = await SeedAsync(fixture, null);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        var key = new Key("new-key", 19); bool active = false, blocked = false;
        var options = Options(fixture, null, StorageOpenMode.Reopen);
        options.ReadContext = () =>
        {
            if (active && key.Copies.Count > 4 && File.Exists(staging) && !File.Exists(staging + "-wal"))
            {
                try { using var writer = new FileStream(staging, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); writer.WriteByte(0); }
                catch (IOException) { blocked = true; }
            }
            return fixture.Scope;
        };
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(options)) { active = true; await source.CopyToEncryptedAsync(destination, staging, key); active = false; }
        Assert.True(blocked); Assert.Equal(sourceHash, Hash(fixture.Path));
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" }) Assert.False(File.Exists(staging + suffix));
        using var migrated = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen, destination));
        Assert.Equal(seeded.Published, await migrated.ReadPublicationAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrDuringCopyKeepsSourceAndDoesNotPublish(bool duringCopy)
    {
        using var fixture = new Fixture(); await SeedAsync(fixture, null);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        var key = new Key("new-key", 19); using var cancellation = new CancellationTokenSource();
        if (duringCopy) key.OnRead = () => cancellation.Cancel(); else cancellation.Cancel();
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, null, StorageOpenMode.Reopen)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CopyToEncryptedAsync(destination, staging, key, cancellation.Token));
        Assert.Equal(sourceHash, Hash(fixture.Path)); Assert.False(File.Exists(destination)); Assert.Equal(duringCopy, File.Exists(staging));
    }

    [Fact]
    public async Task EncryptionCapacityFailureDoesNotExpandTheOriginalPageLimitOrPublishPartialDestination()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.MaxPages = 8;
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(options))
            await fixture.Run(source, "begin", fixture.Begin("pending", new byte[12300]));
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        options.Mode = StorageOpenMode.Reopen;
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(options))
            Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, new Key("new-key", 19)))).Code);
        Assert.Equal(sourceHash, Hash(fixture.Path)); Assert.False(File.Exists(destination)); Assert.True(File.Exists(staging));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterDestinationPublicationRequiresReconciliationWithTheKnownNewPath(bool cancel)
    {
        using var fixture = new Fixture(); var seeded = await SeedAsync(fixture, null);
        string sourceHash = Hash(fixture.Path), destination = Path.Combine(fixture.Directory, "destination.sqlite"), staging = Path.Combine(fixture.Directory, "stage.sqlite");
        var key = new Key("new-key", 19); using var cancellation = new CancellationTokenSource(); bool active = false;
        var options = Options(fixture, null, StorageOpenMode.Reopen);
        options.ReadContext = () =>
        {
            if (active && File.Exists(destination)) { if (cancel) cancellation.Cancel(); else key.InvalidLength = true; }
            return fixture.Scope;
        };
        using (var source = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            active = true;
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, key, cancellation.Token))).Code);
            active = false; key.InvalidLength = false;
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => source.GetCapacityAsync())).Code);
        }
        Assert.Equal(sourceHash, Hash(fixture.Path)); Assert.True(File.Exists(destination)); Assert.False(File.Exists(staging));
        using var migrated = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen, destination));
        Assert.Equal(seeded.Published, await migrated.ReadPublicationAsync());
        AssertTransfer(await fixture.Run(migrated, "query", new { transferId = "pending" }), "staging", seeded.Received, null);
    }

    private static async Task<Seeded> SeedAsync(Fixture fixture, IArchiveKeyProvider? key)
    {
        byte[] original = Encoding.UTF8.GetBytes(PrivateMarker + "original"), published = Encoding.UTF8.GetBytes(PrivateMarker + "winner"), conflict = Encoding.UTF8.GetBytes(PrivateMarker + "conflict"), pending = Encoding.UTF8.GetBytes(PrivateMarker + new string('p', 25000));
        using var source = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key));
        await fixture.Stage(source, "original", original); await fixture.Run(source, "commit", new { transferId = "original" });
        await fixture.Stage(source, "conflict", conflict, WireJson.Sha256(original));
        await fixture.Stage(source, "winner", published, WireJson.Sha256(original)); await fixture.Run(source, "commit", new { transferId = "winner" });
        AssertTransfer(await fixture.Run(source, "commit", new { transferId = "conflict" }), "conflict", conflict.Length, null);
        await fixture.Run(source, "begin", fixture.Begin("pending", pending, WireJson.Sha256(published)));
        await fixture.Run(source, "chunk", fixture.Chunk("pending", 0, pending.Take(113).ToArray()));
        return new Seeded(published, pending, 113);
    }

    private static async Task<Dictionary<string, string>> QueriesAsync(Fixture fixture, SqliteMemoryPublicationStore store)
    {
        var result = new Dictionary<string, string>();
        foreach (string id in TransferIds) result[id] = WireJson.CanonicalString(await fixture.Run(store, "query", new { transferId = id }));
        return result;
    }

    private static SqliteMemoryPublicationOptions Options(Fixture fixture, IArchiveKeyProvider? key, StorageOpenMode mode = StorageOpenMode.Create, string? path = null)
    {
        var options = fixture.Options(mode); options.KeyProvider = key; options.Path = path ?? fixture.Path;
        options.MaxStagingBytes = SqliteMemoryPublicationStore.MaximumBodyBytes + 8192; options.MaxPages = 128;
        return options;
    }

    private static void AssertTransfer(JsonElement response, string status, int received, string? etag)
    {
        var transfer = response.GetProperty("transfer"); Assert.Equal(status, transfer.GetProperty("status").GetString());
        Assert.Equal(received, transfer.GetProperty("receivedBytes").GetInt32()); Assert.Equal(etag, transfer.GetProperty("etag").GetString());
    }

    private static string Hash(string path) => WireJson.Sha256(File.ReadAllBytes(path));

    private static void ProbeEncryptedFiles(string path)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (!File.Exists(path + suffix)) continue;
            using var file = new FileStream(path + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var contents = new MemoryStream(); file.CopyTo(contents);
            Assert.DoesNotContain(PrivateMarker, Encoding.UTF8.GetString(contents.ToArray()));
        }
    }

    private sealed record Seeded(byte[] Published, byte[] Pending, int Received);

    private sealed class Key(string keyId, byte marker) : IArchiveKeyProvider
    {
        public string KeyId => keyId;
        internal Action? OnRead { get; set; }
        internal bool InvalidLength { get; set; }
        internal List<byte[]> Copies { get; } = new();
        public byte[] ReadKey()
        {
            OnRead?.Invoke(); var copy = Enumerable.Repeat(marker, InvalidLength ? 31 : 32).ToArray(); Copies.Add(copy); return copy;
        }
    }
}

