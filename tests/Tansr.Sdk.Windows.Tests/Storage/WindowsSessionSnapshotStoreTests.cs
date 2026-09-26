using System.Text;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class WindowsSessionSnapshotStoreTests
{
    [Fact]
    public async Task DefaultConstructionAndEmptyReadDoNotCreateDatabaseOrReadKeys()
    {
        using var f = new Fixture(); var keys = 0;
        using var store = f.Open(() => { keys++; return f.Key; });
        Assert.Empty(Directory.GetFiles(f.DirectoryPath));
        var empty = await store.ReadAsync(); Assert.Null(empty.Snapshot); Assert.Equal(0, empty.Revision);
        Assert.Equal(0, keys); Assert.Empty(Directory.GetFiles(f.DirectoryPath));
    }

    [Fact]
    public async Task ExactSnapshotIsEncryptedDurableScopedAndDeletionFencesOldWriters()
    {
        using var f = new Fixture(); var copy = new SessionSnapshotCopy("checkpoint-中文", Encoding.UTF8.GetBytes("snapshot-secret-中文-完整图片-and-core-fields"));
        using (var store = f.Open())
        {
            Assert.Equal(1, await store.WriteAsync(0, copy));
            Assert.Equal(copy.Bytes, (await store.ReadAsync()).Snapshot!.Bytes);
        }
        Assert.DoesNotContain("snapshot-secret", Encoding.UTF8.GetString(File.ReadAllBytes(f.Path)), StringComparison.Ordinal);
        using (var reopened = f.Open())
        {
            Assert.Equal(copy.Bytes, (await reopened.ReadAsync()).Snapshot!.Bytes);
            Assert.Equal(2, await reopened.DeleteAsync(1)); Assert.Null((await reopened.ReadAsync()).Snapshot);
            Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<StorageException>(() => reopened.WriteAsync(1, copy))).Code);
        }
        using var final = f.Open(); Assert.Null((await final.ReadAsync()).Snapshot); Assert.Equal(2, (await final.ReadAsync()).Revision);
    }

    [Fact]
    public async Task WrongApplicationUserEndpointSessionAndKeyCannotOpenPriorMirror()
    {
        using var f = new Fixture(); using (var first = f.Open()) await first.WriteAsync(0, new("checkpoint", new byte[] { 1, 2, 3 }));
        var scopes = new[] {
            new SessionSnapshotScope("https://other.test", "app", "user", "session"),
            new SessionSnapshotScope("https://serve.test", "other-app", "user", "session"),
            new SessionSnapshotScope("https://serve.test", "app", "other-user", "session"),
            new SessionSnapshotScope("https://serve.test", "app", "user", "other-session"),
        };
        foreach (var scope in scopes)
        {
            using var foreign = new WindowsSessionSnapshotStore(new() { Path = f.Path, Scope = scope, ReadScope = () => scope, KeyProvider = () => f.Key });
            Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => foreign.ReadAsync())).Code);
        }
        using var wrong = f.Open(() => new Key(19));
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => wrong.ReadAsync())).Code);
    }

    [Fact]
    public async Task ChangedTrustedAuthorityAndCanceledWritePreservePriorCopy()
    {
        using var f = new Fixture(); using var store = f.Open(); var first = new SessionSnapshotCopy("original", new byte[] { 1, 2, 3 });
        await store.WriteAsync(0, first);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(1, new("late", new byte[] { 4 }), canceled.Token));
        Assert.Equal(first.Bytes, (await store.ReadAsync()).Snapshot!.Bytes);
        f.Current = new("https://serve.test", "app", "changed", "session");
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => store.DeleteAsync(1))).Code);
        f.Current = f.Identity; Assert.Equal(first.Bytes, (await store.ReadAsync()).Snapshot!.Bytes);
    }

    [Fact]
    public async Task LimitsAndDamagedCiphertextFailWithoutReplacingTheExistingCopy()
    {
        using var f = new Fixture(); using (var store = f.Open(maximum: 32))
        {
            await store.WriteAsync(0, new("checkpoint", new byte[] { 1, 2, 3 }));
            Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => store.WriteAsync(1, new("large", new byte[33])))).Code);
            Assert.Equal(new byte[] { 1, 2, 3 }, (await store.ReadAsync()).Snapshot!.Bytes);
        }
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE mirror SET body=zeroblob(length(body))"; command.ExecuteNonQuery();
        }
        using var reopened = f.Open();
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => reopened.ReadAsync())).Code);
    }

    [Fact]
    public async Task RealCurrentUserDpapiKeyReopensTheEncryptedMirror()
    {
        using var f = new Fixture(); var keyPath = System.IO.Path.Combine(f.DirectoryPath, "mirror.key");
        var provider = CurrentUserDpapiArchiveKeyProvider.Create(keyPath, "snapshot-key");
        using (var store = f.Open(() => provider)) await store.WriteAsync(0, new("checkpoint", Encoding.UTF8.GetBytes("current-user-only")));
        using var reopened = f.Open(() => CurrentUserDpapiArchiveKeyProvider.Open(keyPath, "snapshot-key"));
        Assert.Equal("current-user-only", Encoding.UTF8.GetString((await reopened.ReadAsync()).Snapshot!.Bytes));
    }

    private sealed class Key(byte marker = 7) : IArchiveKeyProvider
    {
        public string KeyId => "synthetic-snapshot-key";
        public byte[] ReadKey() => Enumerable.Repeat(marker, 32).ToArray();
    }
    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr snapshot 中文 " + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(DirectoryPath, "context.sqlite");
        internal SessionSnapshotScope Identity = new("https://serve.test", "app", "user", "session"), Current = new("https://serve.test", "app", "user", "session");
        internal IArchiveKeyProvider Key = new Key();
        internal Fixture() { Directory.CreateDirectory(DirectoryPath); }
        internal WindowsSessionSnapshotStore Open(Func<IArchiveKeyProvider>? keys = null, int maximum = 32 * 1024 * 1024)
            => new(new() { Path = Path, Scope = Identity, ReadScope = () => Current, KeyProvider = keys ?? (() => Key), MaximumSnapshotBytes = maximum });
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
