using Microsoft.Data.Sqlite;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteExecutorJournal
{
    /// <summary>
    /// 停止执行后，显式完整复制到新加密路径或换钥。保持身份、限额、compact 选择、原 operation 和永久 receipt，
    /// Pending/unknown 不重新授予执行。暂存与目标须同目录且均不存在；失败保留源与具名暂存，不覆盖或自动切换。
    /// 加密编码计入原字节限额；空间不足时拒绝迁移，不提高限额或丢事实。
    /// </summary>
    public Task CopyToEncryptedAsync(string destinationPath, string stagingPath, IArchiveKeyProvider keyProvider, CancellationToken cancellationToken = default)
    {
        Run((_scope, check) =>
        {
            Require(keyProvider != null, "invalid_input");
            string destination = StorageFileIdentity.FullPath(destinationPath), staging = StorageFileIdentity.FullPath(stagingPath);
            string parentPath = Path.GetDirectoryName(destination)!;
            Require(!string.Equals(destination, _options.Path, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(staging, _options.Path, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(destination, staging, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parentPath, Path.GetDirectoryName(staging), StringComparison.OrdinalIgnoreCase), "invalid_input");
            using var parent = StorageFileIdentity.Open(parentPath, true);
            RequireNewMedia(destination); RequireNewMedia(staging);
            Initialize(); check(); parent.Check();
            var options = new SqliteExecutorJournalOptions
            {
                Path = staging,
                Mode = StorageOpenMode.Create,
                KeyProvider = keyProvider,
                CompactCompletedReceipts = _compactCompletedReceipts,
                ApplicationScopeId = _application,
                EndUserId = _user,
                ExecutorId = _executor,
                MaxOperations = _maximumOperations,
                MaxStoredBytes = _maximumBytes,
                MaxPages = _options.MaxPages,
                ReadContext = _readContext,
            };
            string device, inode, metadata;
            MemoryPublicationBodyCipher cipher;
            byte[] keyCheck;
            using (var copy = OpenAsync(options, cancellationToken).GetAwaiter().GetResult())
            {
                device = copy._file.Device; inode = copy._file.Inode; metadata = copy._metadata; cipher = copy._cipher!;
                keyCheck = (byte[])copy.Scalar("SELECT key_check FROM encryption WHERE id=1")!;
                copy.Run((_copyScope, copyCheck) =>
                {
                    void CheckBoth() { check(); copyCheck(); check(); parent.Check(); }
                    copy.Transaction(() =>
                    {
                        long bytes = Bytes(metadata) + 128; int count = 0;
                        using var command = Command("SELECT id FROM operations ORDER BY id");
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            CheckBoth(); string id = reader.GetString(0); var row = Row(id)!.Value;
                            var operation = copy.Encode("operation", id, row.Operation);
                            var receipt = row.Receipt == null ? null : copy.Encode("receipt", id, row.Receipt);
                            int reserve = receipt == null ? copy.StoredReceiptReserve : _compactCompletedReceipts ? 0 : copy.StoredReceiptReserve - Bytes(receipt);
                            bytes += Bytes(id) + Bytes(operation) + (receipt == null ? 0 : Bytes(receipt)) + reserve;
                            Require(++count <= _maximumOperations && bytes <= _maximumBytes, "capacity_exceeded");
                            copy.Exec("INSERT INTO operations VALUES($id,$operation,$receipt,zeroblob($reserve))",
                                ("$id", id), ("$operation", operation), ("$receipt", (object?)receipt ?? DBNull.Value), ("$reserve", reserve));
                        }
                        copy.Exec("UPDATE state SET logical_bytes=$bytes,operation_count=$count WHERE id=1", ("$bytes", bytes), ("$count", count));
                        copy.Initialize(); VerifyCopiedContents(copy, CheckBoth);
                    }, CheckBoth);
                    return true;
                }, cancellationToken);
            }
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) Require(!File.Exists(staging + suffix), "reconciliation_required");
            using var guard = StorageFileIdentity.OpenReadOnly(staging);
            Require(guard.Device == device && guard.Inode == inode, "identity_mismatch");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = new Uri(staging).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                connection.Open();
                var audit = new SqliteExecutorJournal(connection, parent, guard, options, metadata, cipher);
                audit.Initialize(); VerifyCopiedContents(audit, check);
            }
            byte[] digest = guard.Digest();
            check(); cipher.Open("key-check", keyCheck); check(); parent.Check(); guard.Check(); RequireNewMedia(destination);
            StorageFileIdentity.MoveNew(staging, destination, device, inode, digest);
            _committed = true;
            cipher.Open("key-check", keyCheck); check();
            return true;
        }, cancellationToken);
        return Task.CompletedTask;
    }

    private void VerifyCopiedContents(SqliteExecutorJournal copy, Action check)
    {
        Require(State().Count == copy.State().Count);
        using var command = Command("SELECT id FROM operations ORDER BY id");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            check(); string id = reader.GetString(0); var original = Row(id)!.Value; var copied = copy.Row(id);
            Require(copied.HasValue && original.Operation == copied.Value.Operation && original.Receipt == copied.Value.Receipt);
        }
    }

    private static void RequireNewMedia(string path)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            Require(!File.Exists(path + suffix) && !Directory.Exists(path + suffix), "storage_error");
    }
}
