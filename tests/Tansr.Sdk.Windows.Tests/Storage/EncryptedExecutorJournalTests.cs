using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class EncryptedExecutorJournalTests
{
    private const string Secret = "private-execution-body-秘密";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntireOperationsAndReceiptsAreEncryptedBeforeSqliteAndReopenPreservesTerminalAndPending(bool compact)
    {
        using var f = new Fixture(); var options = f.Options(); options.CompactCompletedReceipts = compact;
        var write = Operation("write", true); var read = Operation("read"); var pending = Operation("pending"); var unknown = Operation("unknown");
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            Assert.True(journal.EncryptedAtRest);
            foreach (var op in new[] { write, read, unknown, pending }) await journal.ClaimAsync(op);
            await journal.CompleteAsync(write, Receipt(write)); await journal.CompleteAsync(read, Receipt(read));
            await journal.CompleteAsync(unknown, Receipt(unknown, "unknown"));
            f.Probe();
        }
        options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteExecutorJournal.OpenAsync(options);
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await reopened.ClaimAsync(pending)).Status);
        Assert.Null(await reopened.ReceiptAsync(pending));
        foreach (var op in new[] { write, read, unknown })
        {
            var result = await reopened.ClaimAsync(op); Assert.Equal(ExecutorJournalClaimStatus.Completed, result.Status);
            Assert.Equal(WireJson.CanonicalString(Receipt(op, op.GetProperty("operationId").GetString() == "unknown" ? "unknown" : "completed")), WireJson.CanonicalString(result.Receipt!.Value));
        }
        Assert.Equal(4, (await reopened.OperationsAsync()).Count);
        Assert.All(f.Key.Copies, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task ActiveWalAndTemporaryInventoryNeverContainsOperationReceiptOrRawKey()
    {
        using var fixture = new Fixture();
        fixture.Key.OnRead = fixture.Probe;
        var operation = Operation("active-inventory", true);
        using (var journal = await SqliteExecutorJournal.OpenAsync(fixture.Options()))
        {
            await journal.ClaimAsync(operation); fixture.Probe();
            await journal.CompleteAsync(operation, Receipt(operation)); fixture.Probe();
            Assert.Contains(fixture.Seen, path => path.EndsWith("-wal", StringComparison.Ordinal));
            // The original exclusive SQLite connection keeps its WAL index in memory.
            Assert.DoesNotContain(fixture.Seen, path => path.EndsWith("-shm", StringComparison.Ordinal));
            Assert.True(fixture.Scans > 5);
        }
        fixture.Probe();
        Assert.DoesNotContain(System.IO.Directory.GetFiles(fixture.Directory), path => path.EndsWith("-wal", StringComparison.Ordinal) || path.EndsWith("-shm", StringComparison.Ordinal));
        Assert.All(fixture.Key.Copies, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-id")]
    [InlineData("missing-key")]
    [InlineData("unavailable")]
    [InlineData("operation-truncated")]
    [InlineData("receipt-bit")]
    [InlineData("cross-row")]
    [InlineData("key-check")]
    [InlineData("unknown-format")]
    public async Task CorruptionAndWrongKeysRefuseWithoutRewritingTheOriginal(string change)
    {
        using var f = new Fixture(); var options = f.Options();
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            foreach (var op in new[] { Operation("first"), Operation("second") })
            { await journal.ClaimAsync(op); await journal.CompleteAsync(op, Receipt(op)); }
        }
        if (change == "wrong-key") f.Key.Value[0] ^= 1;
        if (change == "wrong-id") f.Key.KeyId = "other";
        if (change == "missing-key") options.KeyProvider = null;
        if (change == "unavailable") f.Key.Unavailable = true;
        string? sql = change switch
        {
            "operation-truncated" => "UPDATE operations SET operation=substr(operation,1,length(operation)-4) WHERE id='first'",
            "receipt-bit" => "UPDATE operations SET receipt='AAAA'||substr(receipt,5) WHERE id='first'",
            "cross-row" => "UPDATE operations SET operation=(SELECT operation FROM operations WHERE id='first') WHERE id='second'",
            "key-check" => "UPDATE encryption SET key_check=zeroblob(28)",
            "unknown-format" => "UPDATE metadata SET json=replace(json,'encrypted-net-sqlite-v1','encrypted-net-sqlite-v99')",
            _ => null,
        };
        if (sql != null) f.Sql(sql);
        var before = File.ReadAllBytes(options.Path); options.Mode = StorageOpenMode.Reopen;
        await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenAsync(options));
        Assert.Equal(before, File.ReadAllBytes(options.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMigrationAndRotationKeepOriginalBytesPendingUnknownAndEveryReceipt(bool compact)
    {
        using var f = new Fixture(); var plainOptions = f.Options(); plainOptions.KeyProvider = null; plainOptions.CompactCompletedReceipts = compact;
        var pending = Operation("pending"); var unknown = Operation("unknown"); var done = Operation("done");
        using (var original = await SqliteExecutorJournal.OpenAsync(plainOptions))
        {
            await original.ClaimAsync(pending); await original.ClaimAsync(unknown); await original.CompleteAsync(unknown, Receipt(unknown, "unknown"));
            await original.ClaimAsync(done); await original.CompleteAsync(done, Receipt(done));
        }
        var before = File.ReadAllBytes(f.Path); plainOptions.Mode = StorageOpenMode.Reopen;
        var implicitOptions = f.Options(); implicitOptions.Mode = StorageOpenMode.Reopen; implicitOptions.CompactCompletedReceipts = compact;
        await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenAsync(implicitOptions));
        Assert.Equal(before, File.ReadAllBytes(f.Path));
        string destination = f.Path + ".encrypted", staging = destination + ".staging";
        using (var original = await SqliteExecutorJournal.OpenAsync(plainOptions))
        {
            await original.CopyToEncryptedAsync(destination, staging, f.Key);
            Assert.False(File.Exists(staging));
            await Assert.ThrowsAsync<StorageException>(() => original.CopyToEncryptedAsync(destination, staging, f.Key));
        }
        Assert.Equal(before, File.ReadAllBytes(f.Path));
        var encryptedOptions = f.Options(); encryptedOptions.Path = destination; encryptedOptions.Mode = StorageOpenMode.Reopen; encryptedOptions.CompactCompletedReceipts = compact;
        var newKey = new Key { KeyId = "rotated" }; newKey.Value[0] ^= 1;
        using (var encrypted = await SqliteExecutorJournal.OpenAsync(encryptedOptions))
        {
            Assert.Equal(ExecutorJournalClaimStatus.Pending, (await encrypted.ClaimAsync(pending)).Status);
            Assert.Equal("unknown", (await encrypted.ReceiptAsync(unknown))!.Value.GetProperty("status").GetString());
            Assert.Equal(WireJson.CanonicalString(Receipt(done)), WireJson.CanonicalString((await encrypted.ReceiptAsync(done))!.Value));
            await encrypted.CopyToEncryptedAsync(destination + ".rotated", staging, newKey);
        }
        var encryptedBefore = File.ReadAllBytes(destination);
        encryptedOptions.Path += ".rotated"; encryptedOptions.KeyProvider = newKey;
        using var rotated = await SqliteExecutorJournal.OpenAsync(encryptedOptions);
        Assert.Equal(3, (await rotated.OperationsAsync()).Count);
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await rotated.ClaimAsync(pending)).Status);
        Assert.Equal("unknown", (await rotated.ReceiptAsync(unknown))!.Value.GetProperty("status").GetString());
        Assert.Equal(before, File.ReadAllBytes(f.Path)); Assert.Equal(encryptedBefore, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task MigrationCancellationAndInsufficientQuotaNeverPublishOrLoseOriginalFacts()
    {
        using var f = new Fixture(); var options = f.Options(); options.KeyProvider = null; options.MaxStoredBytes = 300000;
        using var original = await SqliteExecutorJournal.OpenAsync(options); var op = Operation("pending"); await original.ClaimAsync(op);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.CopyToEncryptedAsync(f.Path + ".target", f.Path + ".stage", f.Key, canceled.Token));
        Assert.False(File.Exists(f.Path + ".stage"));
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => original.CopyToEncryptedAsync(f.Path + ".target", f.Path + ".stage", f.Key))).Code);
        Assert.False(File.Exists(f.Path + ".target")); Assert.True(File.Exists(f.Path + ".stage"));
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await original.ClaimAsync(op)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyRevocationBeforeCommitRollsBackAndAfterCommitRequiresOriginalKeyReconciliation(bool afterCommit)
    {
        using var f = new Fixture(); var options = f.Options(); var op = Operation("original");
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            int reads = 0; f.Key.OnRead = () => { if (++reads == (afterCommit ? 4 : 3)) f.Key.Unavailable = true; };
            Assert.Equal(afterCommit ? "reconciliation_required" : "context_changed", (await Assert.ThrowsAsync<StorageException>(() => journal.ClaimAsync(op))).Code);
            f.Key.OnRead = null; f.Key.Unavailable = false;
            if (afterCommit) Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => journal.OperationsAsync())).Code);
            else Assert.Empty(await journal.OperationsAsync());
        }
        options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteExecutorJournal.OpenAsync(options);
        Assert.Equal(afterCommit ? ExecutorJournalClaimStatus.Pending : ExecutorJournalClaimStatus.Claimed, (await reopened.ClaimAsync(op)).Status);
    }

    internal sealed class Key : IArchiveKeyProvider
    {
        public string KeyId { get; set; } = "journal-key";
        internal byte[] Value { get; } = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        internal bool Unavailable { get; set; }
        internal List<byte[]> Copies { get; } = new();
        internal Action? OnRead { get; set; }
        public byte[] ReadKey()
        { OnRead?.Invoke(); if (Unavailable) throw new IOException("synthetic missing key"); var copy = (byte[])Value.Clone(); Copies.Add(copy); return copy; }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr-encrypted-journal-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "journal.sqlite");
        internal Key Key { get; } = new();
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal SqliteExecutorJournalOptions Options() => new()
        { Path = Path, Mode = StorageOpenMode.Create, ApplicationScopeId = "app", EndUserId = "user", ExecutorId = "executor", ReadContext = Scope, KeyProvider = Key };
        internal void Sql(string sql)
        { using var connection = new SqliteConnection("Data Source=" + Path + ";Pooling=False"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
        internal int Scans { get; private set; }
        internal void Probe()
        {
            Scans++;
            foreach (var path in System.IO.Directory.GetFiles(Directory))
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var bytes = new MemoryStream(); file.CopyTo(bytes); var text = Encoding.UTF8.GetString(bytes.ToArray());
                Seen.Add(path);
                Assert.DoesNotContain(Secret, text); Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)), text);
                Assert.False(bytes.ToArray().AsSpan().IndexOf(Key.Value) >= 0, "raw encryption key reached media");
                Assert.DoesNotContain(Convert.ToBase64String(Key.Value), text);
                Assert.DoesNotContain(Convert.ToHexString(Key.Value), text, StringComparison.OrdinalIgnoreCase);
            }
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    private static JsonElement Scope() => JsonSerializer.SerializeToElement(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" });
    private static JsonElement Operation(string id, bool write = false)
    {
        var args = write ? JsonSerializer.SerializeToElement(new { path = Secret, expectedHash = (string?)null, bytesBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)) }) : JsonSerializer.SerializeToElement(new { path = Secret, offset = 0, length = 1024 });
        var unsigned = JsonSerializer.SerializeToElement(new
        {
            protocol = "sdk2-ext-v1",
            operationId = id,
            sessionId = "session",
            scope = Scope(),
            binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } },
            toolName = write ? "Write" : "Read",
            request = new { operation = write ? "fs.write" : "fs.read", args },
            expiresAt = "2099-01-01T00:00:00.000Z",
        });
        return WireJson.Parse(Encoding.UTF8.GetBytes(unsigned.GetRawText().TrimEnd('}') + ",\"digest\":\"" + WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(unsigned)) + "\"}"));
    }
    private static JsonElement Receipt(JsonElement operation, string status = "completed")
    {
        string kind = operation.GetProperty("request").GetProperty("operation").GetString()!;
        var bytes = Encoding.UTF8.GetBytes(Secret);
        var args = kind == "fs.write" ? JsonSerializer.SerializeToElement(new { hash = WireJson.Sha256(bytes) }) : JsonSerializer.SerializeToElement(new { bytesBase64 = Convert.ToBase64String(bytes) });
        return JsonSerializer.SerializeToElement(new
        {
            protocol = "sdk2-ext-v1",
            executorId = "executor",
            connectionId = "connection",
            operationId = operation.GetProperty("operationId").GetString(),
            digest = operation.GetProperty("digest").GetString(),
            status,
            result = status == "completed" ? (object)new { operation = kind, args } : null,
            errorCode = status == "completed" ? null : "effect_unknown",
        });
    }
}
