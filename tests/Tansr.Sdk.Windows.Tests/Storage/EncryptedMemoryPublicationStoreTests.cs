using System.Text;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;
using Fixture = Tansr.Sdk.Windows.Tests.Storage.SqliteMemoryPublicationStoreTests.Fixture;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class EncryptedMemoryPublicationStoreTests
{
    [Fact]
    public async Task EncryptedStagingResumesOriginalCursorAndCommitsOriginalReceiptsWithoutPlaintextFiles()
    {
        using var fixture = new Fixture(); var key = new Key();
        byte[] body = Encoding.UTF8.GetBytes("private-memory-publication-秘密-" + new string('x', 13000));
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key)))
        {
            Assert.True(store.EncryptedBody); Assert.True(store.AtomicDurablePublication);
            await fixture.Run(store, "begin", fixture.Begin("original", body));
            await fixture.Run(store, "chunk", fixture.Chunk("original", 0, body.Take(100).ToArray()));
            Assert.Equal(body.Length, (await store.GetCapacityAsync()).StagingBytes);
            ProbeFiles(fixture, "private-memory-publication-秘密-");
        }
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen)))
        {
            var original = (await fixture.Run(store, "query", new { transferId = "original" })).GetProperty("transfer");
            Assert.Equal("staging", original.GetProperty("status").GetString()); Assert.Equal(100, original.GetProperty("receivedBytes").GetInt32());
            await fixture.Run(store, "chunk", fixture.Chunk("original", 0, body.Take(100).ToArray()));
            for (int offset = 100; offset < body.Length; offset += SqliteMemoryPublicationStore.MaximumChunkBytes)
                await fixture.Run(store, "chunk", fixture.Chunk("original", offset, body.Skip(offset).Take(SqliteMemoryPublicationStore.MaximumChunkBytes).ToArray()));
            await fixture.Stage(store, "loser", body);
            var committed = await fixture.Run(store, "commit", new { transferId = "original" });
            Assert.Equal("committed", committed.GetProperty("transfer").GetProperty("status").GetString());
            Assert.Equal(committed.GetRawText(), (await fixture.Run(store, "commit", new { transferId = "original" })).GetRawText());
            Assert.Equal("conflict", (await fixture.Run(store, "commit", new { transferId = "loser" })).GetProperty("transfer").GetProperty("status").GetString());
            Assert.Equal(body, await fixture.ReadAll(store)); Assert.Equal(body, await store.ReadPublicationAsync());
            Assert.Equal(0, (await store.GetCapacityAsync()).StagingBytes);
            ProbeFiles(fixture, "private-memory-publication-秘密-");
        }
        using (var raw = Raw(fixture.Path))
        {
            Assert.Equal(body.Length + 28L, Scalar(raw, "SELECT length(body) FROM publication"));
            Assert.Equal(2L, Scalar(raw, "SELECT count(*) FROM transfers WHERE length(body)=28"));
            Assert.Contains(SqliteMemoryPublicationStore.EncryptedFormat, (string)Scalar(raw, "SELECT json FROM metadata")!);
        }
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen));
        Assert.Equal(body, await reopened.ReadPublicationAsync());
        Assert.Equal("unknown", (await fixture.Run(reopened, "query", new { transferId = "absent" })).GetProperty("transfer").GetProperty("status").GetString());
        Assert.All(key.Copies, copy => Assert.All(copy, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task ActivePublicationWalAndTemporaryInventoryNeverContainsBodyOrRawKey()
    {
        using var fixture = new Fixture(); var key = new Key();
        const string plaintext = "PST-B8-live-publication-private-body";
        byte[] body = Encoding.UTF8.GetBytes(plaintext);
        var seen = new HashSet<string>(StringComparer.Ordinal); int scans = 0;
        void Probe()
        {
            scans++;
            foreach (string path in Directory.GetFiles(fixture.Directory))
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var contents = new MemoryStream(); file.CopyTo(contents); byte[] bytes = contents.ToArray();
                seen.Add(path); string text = Encoding.UTF8.GetString(bytes);
                Assert.DoesNotContain(plaintext, text); Assert.DoesNotContain(Convert.ToBase64String(body), text);
                Assert.False(bytes.AsSpan().IndexOf(key.Value) >= 0, "raw encryption key reached media");
                Assert.DoesNotContain(Convert.ToBase64String(key.Value), text);
                Assert.DoesNotContain(Convert.ToHexString(key.Value), text, StringComparison.OrdinalIgnoreCase);
            }
        }
        key.OnRead = Probe;
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key)))
        {
            await fixture.Stage(store, "active-inventory", body); Probe();
            await fixture.Run(store, "commit", new { transferId = "active-inventory" }); Probe();
            Assert.Equal(body, await store.ReadPublicationAsync());
            Assert.Contains(seen, path => path.EndsWith("-wal", StringComparison.Ordinal));
            // The original exclusive SQLite connection keeps its WAL index in memory.
            Assert.DoesNotContain(seen, path => path.EndsWith("-shm", StringComparison.Ordinal));
            Assert.True(scans > 5);
        }
        Probe();
        Assert.DoesNotContain(Directory.GetFiles(fixture.Directory), path => path.EndsWith("-wal", StringComparison.Ordinal) || path.EndsWith("-shm", StringComparison.Ordinal));
        Assert.All(key.Copies, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-id")]
    [InlineData("no-key")]
    [InlineData("unavailable")]
    public async Task MissingOrWrongKeysNeverRewriteEncryptedMedia(string failure)
    {
        using var fixture = new Fixture(); var key = new Key();
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key)))
        { await fixture.Stage(store, "one", Encoding.UTF8.GetBytes("preserve this private body")); await fixture.Run(store, "commit", new { transferId = "one" }); }
        var before = File.ReadAllBytes(fixture.Path); var badKey = new Key();
        if (failure == "wrong-key") badKey.Value[0] ^= 1;
        if (failure == "wrong-id") badKey.KeyId = "another-key";
        if (failure == "unavailable") badKey.Unavailable = true;
        await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(Options(fixture, failure == "no-key" ? null : badKey, StorageOpenMode.Reopen)));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen));
        Assert.Equal(Encoding.UTF8.GetBytes("preserve this private body"), await reopened.ReadPublicationAsync());
    }

    [Theory]
    [InlineData("publication-bit")]
    [InlineData("publication-truncated")]
    [InlineData("staging-bit")]
    [InlineData("staging-truncated")]
    [InlineData("staging-owner")]
    [InlineData("staging-received")]
    [InlineData("staging-request")]
    [InlineData("receipt-owner")]
    [InlineData("receipt-status")]
    [InlineData("cross-row")]
    [InlineData("format")]
    [InlineData("key-check")]
    public async Task TamperingRejectsBeforeReturningAnyPlaintextAndPreservesCorruptSource(string failure)
    {
        using var fixture = new Fixture(); var key = new Key(); byte[] bytes = Encoding.UTF8.GetBytes("authenticated-memory-body");
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key)))
        {
            await fixture.Stage(store, "published", bytes); await fixture.Run(store, "commit", new { transferId = "published" });
            await fixture.Stage(store, "staged", bytes);
        }
        using (var raw = Raw(fixture.Path)) Scalar(raw, failure switch
        {
            "publication-bit" => "UPDATE publication SET body=zeroblob(length(body))",
            "publication-truncated" => "UPDATE publication SET body=substr(body,1,length(body)-1)",
            "staging-bit" => "UPDATE transfers SET body=zeroblob(length(body)) WHERE id='staged'",
            "staging-truncated" => "UPDATE transfers SET body=substr(body,1,length(body)-1) WHERE id='staged'",
            "staging-owner" => "UPDATE transfers SET owner=replace(owner,'session/','another/') WHERE id='staged'",
            "staging-received" => "UPDATE transfers SET received=received-1 WHERE id='staged'",
            "staging-request" => "UPDATE transfers SET request=replace(request,'\"expectedEtag\":null','\"expectedEtag\":\"old\"') WHERE id='staged'",
            "receipt-owner" => "UPDATE transfers SET owner=replace(owner,'session/','another/') WHERE id='published'",
            "receipt-status" => "UPDATE transfers SET status='conflict',etag=NULL WHERE id='published'",
            "cross-row" => "UPDATE transfers SET body=(SELECT body FROM publication) WHERE id='staged'",
            "format" => "UPDATE metadata SET json=replace(json,'encrypted-net-sqlite-v1','encrypted-net-sqlite-v9')",
            _ => "UPDATE encryption SET key_check=zeroblob(28)",
        });
        var before = File.ReadAllBytes(fixture.Path);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen)));
        Assert.True(error is StorageException or SqliteMemoryPublicationException);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public async Task PlaintextLegacyStoreIsExplicitAndIsNeverSilentlyMigratedByProvidingAKey()
    {
        using var fixture = new Fixture(); byte[] bytes = Encoding.UTF8.GetBytes("legacy body");
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(fixture.Options()))
        { Assert.False(store.EncryptedBody); await fixture.Stage(store, "original", bytes); await fixture.Run(store, "commit", new { transferId = "original" }); }
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(Options(fixture, new Key(), StorageOpenMode.Reopen)))).Code);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.False(reopened.EncryptedBody); Assert.Equal(bytes, await reopened.ReadPublicationAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RotationOrRevocationWhileOpenFailsBeforeMutationAndRetainsOriginalCursor(bool revoke)
    {
        using var fixture = new Fixture(); var key = new Key(); byte[] bytes = Encoding.UTF8.GetBytes("original body");
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key)))
        {
            await fixture.Run(store, "begin", fixture.Begin("original", bytes));
            if (revoke) key.Unavailable = true; else key.Value[0] ^= 1;
            Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => fixture.Run(store, "chunk", fixture.Chunk("original", 0, bytes)))).Code);
            if (revoke) key.Unavailable = false; else key.Value[0] ^= 1;
            Assert.Equal(0, (await fixture.Run(store, "query", new { transferId = "original" })).GetProperty("transfer").GetProperty("receivedBytes").GetInt32());
        }
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen));
        await fixture.Run(reopened, "chunk", fixture.Chunk("original", 0, bytes));
        await fixture.Run(reopened, "commit", new { transferId = "original" }); Assert.Equal(bytes, await reopened.ReadPublicationAsync());
    }

    [Fact]
    public async Task EncryptionOverheadDoesNotConsumeLogicalStagingQuotaOrInvalidateMaximumBodies()
    {
        using var fixture = new Fixture(); var options = Options(fixture, new Key()); options.MaxStagingBytes = SqliteMemoryPublicationStore.MaximumBodyBytes;
        using var store = await SqliteMemoryPublicationStore.OpenAsync(options);
        await fixture.Run(store, "begin", fixture.Begin("maximum", new byte[SqliteMemoryPublicationStore.MaximumBodyBytes]));
        Assert.Equal(SqliteMemoryPublicationStore.MaximumBodyBytes, (await store.GetCapacityAsync()).StagingBytes);
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => fixture.Run(store, "begin", fixture.Begin("overflow", new byte[1])))).Code);
        Assert.Equal(1, (await store.GetCapacityAsync()).StoredTransfers);
    }

    [Fact]
    public async Task CurrentUserDpapiProviderCanReopenEncryptedPublicationWithTheOriginalKeyFile()
    {
        using var fixture = new Fixture(); string keyPath = Path.Combine(fixture.Directory, "memory.key");
        var key = CurrentUserDpapiArchiveKeyProvider.Create(keyPath, "publication-key");
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key)))
        { await fixture.Stage(store, "original", Encoding.UTF8.GetBytes("dpapi private body")); await fixture.Run(store, "commit", new { transferId = "original" }); }
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, CurrentUserDpapiArchiveKeyProvider.Open(keyPath, "publication-key"), StorageOpenMode.Reopen));
        Assert.Equal(Encoding.UTF8.GetBytes("dpapi private body"), await reopened.ReadPublicationAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalKeyCallbackCannotReleasePlaintextAfterRevocationOrSwallowedReentry(bool reenter)
    {
        using var fixture = new Fixture(); var key = new Key();
        using var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key));
        await fixture.Stage(store, "original", Encoding.UTF8.GetBytes("private result")); await fixture.Run(store, "commit", new { transferId = "original" });
        int reads = 0;
        key.OnRead = () =>
        {
            if (++reads != 3) return;
            if (reenter) { try { store.GetCapacityAsync().GetAwaiter().GetResult(); } catch (StorageException) { } }
            else fixture.AuthorizationRevision = "2";
        };
        Assert.Equal(reenter ? "reentrant" : "context_changed", (await Assert.ThrowsAsync<StorageException>(() => store.ReadPublicationAsync())).Code);
        key.OnRead = null; fixture.AuthorizationRevision = "1";
        Assert.Equal(Encoding.UTF8.GetBytes("private result"), await store.ReadPublicationAsync());
    }

    [Fact]
    public async Task KeyCallbackRevocationImmediatelyBeforeCommitRollsBackTheOriginalChunk()
    {
        using var fixture = new Fixture(); var key = new Key(); byte[] body = Encoding.UTF8.GetBytes("pending original");
        using var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key));
        await fixture.Run(store, "begin", fixture.Begin("original", body));
        int reads = 0; key.OnRead = () => { if (++reads == 5) fixture.AuthorizationRevision = "2"; };
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => fixture.Run(store, "chunk", fixture.Chunk("original", 0, body)))).Code);
        key.OnRead = null; fixture.AuthorizationRevision = "1";
        Assert.Equal(0, (await fixture.Run(store, "query", new { transferId = "original" })).GetProperty("transfer").GetProperty("receivedBytes").GetInt32());
        await fixture.Run(store, "chunk", fixture.Chunk("original", 0, body));
        await fixture.Run(store, "commit", new { transferId = "original" }); Assert.Equal(body, await store.ReadPublicationAsync());
    }

    [Fact]
    public async Task KeyFailureAfterDurableCommitRemainsUnknownUntilReopenAndQueryOfTheOriginalTransfer()
    {
        using var fixture = new Fixture(); var key = new Key(); byte[] body = Encoding.UTF8.GetBytes("committed before lost response");
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key)))
        {
            await fixture.Stage(store, "original", body);
            int reads = 0; key.OnRead = () => { if (++reads == 7) key.Unavailable = true; };
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => fixture.Run(store, "commit", new { transferId = "original" }))).Code);
            key.OnRead = null; key.Unavailable = false;
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => store.GetCapacityAsync())).Code);
        }
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(Options(fixture, key, StorageOpenMode.Reopen));
        var original = (await fixture.Run(reopened, "query", new { transferId = "original" })).GetProperty("transfer");
        Assert.Equal("committed", original.GetProperty("status").GetString()); Assert.Equal(body.Length, original.GetProperty("receivedBytes").GetInt32());
        Assert.Equal(body, await reopened.ReadPublicationAsync()); Assert.Equal(1, (await reopened.GetCapacityAsync()).StoredTransfers);
    }

    private static SqliteMemoryPublicationOptions Options(Fixture fixture, IArchiveKeyProvider? key, StorageOpenMode mode = StorageOpenMode.Create)
    { var options = fixture.Options(mode); options.KeyProvider = key; return options; }
    private static SqliteConnection Raw(string path) { var raw = new SqliteConnection("Data Source=" + path + ";Pooling=False"); raw.Open(); return raw; }
    private static object? Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static void ProbeFiles(Fixture fixture, string plaintext)
    {
        foreach (string path in Directory.GetFiles(fixture.Directory))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var contents = new MemoryStream(); file.CopyTo(contents);
            Assert.DoesNotContain(plaintext, Encoding.UTF8.GetString(contents.ToArray()));
        }
    }
    private sealed class Key : IArchiveKeyProvider
    {
        public string KeyId { get; set; } = "publication-key";
        internal byte[] Value { get; } = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        internal bool Unavailable { get; set; }
        internal List<byte[]> Copies { get; } = new();
        internal Action? OnRead { get; set; }
        public byte[] ReadKey()
        { OnRead?.Invoke(); if (Unavailable) throw new IOException("synthetic missing key"); var copy = (byte[])Value.Clone(); Copies.Add(copy); return copy; }
    }
}
