using Microsoft.Data.Sqlite;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteMemoryPublicationStore
{
    /// <summary>
    /// 显式完整复制至新加密库或换钥；保留原身份、限额和全部 transfer 事实。stagingPath 与 destinationPath
    /// 必须是同目录内不同的新路径。验证并关闭暂存库后才发布目标；失败保留原源及具名暂存文件供恢复，不覆盖或清理。
    /// 调用前停止宿主写入，成功后由宿主显式切换到新路径和新密钥；本方法不改变当前实例或 Serve 绑定。
    /// </summary>
    public Task CopyToEncryptedAsync(string destinationPath, string stagingPath, IArchiveKeyProvider keyProvider, CancellationToken cancellationToken = default)
    {
        Action? verifyDestinationKey = null;
        Run(check =>
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
            Audit(); check(); parent.Check();
            var options = new SqliteMemoryPublicationOptions
            {
                EnablePreview = true,
                Path = staging,
                Mode = StorageOpenMode.Create,
                Identity = _options.Identity,
                ReadContext = _options.ReadContext,
                AuthorizeRecovery = _options.AuthorizeRecovery,
                MaxTransfers = _options.MaxTransfers,
                MaxStagingBytes = _options.MaxStagingBytes,
                MaxPages = _options.MaxPages,
                KeyProvider = keyProvider,
            };
            string device, inode, metadata;
            MemoryPublicationBodyCipher cipher;
            byte[] keyCheck;
            var originalScope = Scope(_options.ReadContext, _options.Identity);
            using (var copy = OpenAsync(options, cancellationToken).GetAwaiter().GetResult())
            {
                device = copy._file.Device; inode = copy._file.Inode; metadata = copy._metadata; cipher = copy._cipher!;
                keyCheck = (byte[])copy.Scalar("SELECT key_check FROM encryption WHERE id=1")!;
                verifyDestinationKey = () =>
                {
                    cipher.Open("key-check", keyCheck);
                    Require(!_poisoned, "reentrant");
                    Require(Equal(originalScope, Scope(_options.ReadContext, _options.Identity)), "context_changed");
                    Require(!_poisoned, "reentrant"); cancellationToken.ThrowIfCancellationRequested();
                };
                copy.Run(copyCheck =>
                {
                    void CheckBoth() { check(); copyCheck(); check(); parent.Check(); }
                    copy.Transaction(() =>
                    {
                        var publication = Publication();
                        if (publication != null)
                        {
                            copy.Exec("INSERT INTO publication VALUES(1,$etag,$body)", ("$etag", publication.Etag), ("$body", copy.EncodePublication(publication.Etag, publication.Body)));
                            var copied = copy.Publication();
                            Require(copied != null && copied.Etag == publication.Etag && copied.Body.SequenceEqual(publication.Body));
                        }
                        using (var command = Command("SELECT id FROM transfers ORDER BY id"))
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                CheckBoth(); string id = reader.GetString(0); var row = Stage(id)!;
                                copy.Exec("INSERT INTO transfers VALUES($id,$owner,$request,$status,$received,$body,$etag)",
                                    ("$id", id), ("$owner", row.Owner), ("$request", Text(row.Request)), ("$status", row.Status), ("$received", row.Received),
                                    ("$body", copy.EncodeTransfer(row.Owner, row.Request, row.Status, row.Received, row.Body, row.Etag)), ("$etag", (object?)row.Etag ?? DBNull.Value));
                                var copied = copy.Stage(id);
                                Require(copied != null && copied.Owner == row.Owner && Equal(copied.Request, row.Request) && copied.Status == row.Status &&
                                    copied.Received == row.Received && copied.Etag == row.Etag &&
                                    (row.Body == null ? copied.Body == null : copied.Body != null && copied.Body.SequenceEqual(row.Body)));
                            }
                        }
                        copy.Audit();
                        var originalCapacity = Capacity(); var copiedCapacity = copy.Capacity();
                        Require(originalCapacity.StoredTransfers == copiedCapacity.StoredTransfers && originalCapacity.StagingBytes == copiedCapacity.StagingBytes);
                    }, CheckBoth);
                    return true;
                }, cancellationToken);
            }
            // SQLite close checkpoints its WAL; refuse to publish a file with unresolved sidecars.
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) Require(!File.Exists(staging + suffix), "reconciliation_required");
            // Deny all writes throughout the final read-only SQLite audit, digest and no-replace rename.
            using var guard = StorageFileIdentity.OpenReadOnly(staging);
            Require(guard.Device == device && guard.Inode == inode, "identity_mismatch");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = new Uri(staging).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                // The write-denying guard makes SQLite immutable mode truthful and avoids new WAL/SHM files.
                connection.Open();
                var audit = new SqliteMemoryPublicationStore(connection, parent, guard, options, new OpenOwner(), metadata, cipher);
                audit.CheckFixed(); audit.Audit(); VerifyCopiedContents(audit, check);
            }
            byte[] digest = guard.Digest();
            check(); verifyDestinationKey(); parent.Check(); guard.Check(); RequireNewMedia(destination);
            StorageFileIdentity.MoveNew(staging, destination, device, inode, digest);
            _committed = true; // A failure in the final authority check must report the destination outcome as unknown.
            return true;
        }, cancellationToken, verifyResult: () => verifyDestinationKey?.Invoke());
        return Task.CompletedTask;
    }

    private void VerifyCopiedContents(SqliteMemoryPublicationStore copy, Action check)
    {
        var original = Publication(); var copied = copy.Publication();
        Require(original == null ? copied == null : copied != null && copied.Etag == original.Etag && copied.Body.SequenceEqual(original.Body));
        using (var command = Command("SELECT id FROM transfers ORDER BY id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                check(); string id = reader.GetString(0); var row = Stage(id)!; var next = copy.Stage(id);
                Require(next != null && next.Owner == row.Owner && Equal(next.Request, row.Request) && next.Status == row.Status && next.Received == row.Received && next.Etag == row.Etag &&
                    (row.Body == null ? next.Body == null : next.Body != null && next.Body.SequenceEqual(row.Body)));
            }
        }
        var originalCapacity = Capacity(); var copiedCapacity = copy.Capacity();
        Require(originalCapacity.StoredTransfers == copiedCapacity.StoredTransfers && originalCapacity.StagingBytes == copiedCapacity.StagingBytes);
    }

    private static void RequireNewMedia(string path)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            Require(!File.Exists(path + suffix) && !Directory.Exists(path + suffix), "storage_error");
    }
}
