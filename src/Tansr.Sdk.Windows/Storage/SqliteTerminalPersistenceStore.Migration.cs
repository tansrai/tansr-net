using Microsoft.Data.Sqlite;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    /// <summary>
    /// 显式完整复制至新加密库或换钥；保留原身份、限额和全部 transfer 事实。stagingPath 与 destinationPath
    /// 必须是同目录内不同的新路径。验证并关闭暂存库后才发布目标；失败保留原源及具名暂存文件供恢复，不覆盖或清理。
    /// 此入口只复制同一 v1 格式（包含永久双键、原票据及未决材料），不导入旧六动作格式。
    /// 调用前停止宿主写入；成功仅表示 copy/validation 完成，切换仍 pending。现有控制机制须先确认旧 writer 已停止/撤权、未知结果已按原键结算。
    /// 目标的只读标记在认证加密状态内持久保存，重开仍仅可 head/read/lookup/query；无公共开关能将其改为 writer。
    /// 本方法不改变当前实例或 Serve 绑定，不宣称整库独立防回滚，也不会删除源库或钥。
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
            var options = new SqliteTerminalPersistenceOptions
            {
                EnableProfile = true,
                Path = staging,
                Mode = StorageOpenMode.Create,
                Identity = _options.Identity,
                ReadContext = _options.ReadContext,
                AuthorizeRecovery = _options.AuthorizeRecovery,
                MaxActiveTransfers = _options.MaxActiveTransfers,
                MaxReceiptEntries = _options.MaxReceiptEntries,
                MaxTransferFacts = _options.MaxTransferFacts,
                MaxObjects = _options.MaxObjects,
                MaxRetainedBytes = _options.MaxRetainedBytes,
                MaxStagingBytes = _options.MaxStagingBytes,
                MaxPages = _options.MaxPages,
                KeyProvider = keyProvider!,
            };
            string device, inode, metadata;
            MemoryPublicationBodyCipher cipher;
            byte[] keyCheck;
            var originalScope = Scope(_options.ReadContext, _options.Identity);
            using (var copy = OpenCoreAsync(options, cancellationToken, readOnlyCopy: true).GetAwaiter().GetResult())
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
                        using (var command = Command("SELECT kind,hash,bytes FROM objects ORDER BY kind,hash"))
                        using (var reader = command.ExecuteReader())
                            while (reader.Read())
                            {
                                CheckBoth(); var reference = new ObjectRef(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), false);
                                copy.AddObject(reference, ReadObject(reference));
                            }
                        using (var command = Command("SELECT primary_key FROM entries ORDER BY ordinal"))
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) { CheckBoth(); var row = FindEntry(reader.GetString(0))!; copy.InsertEntry(row.Ordinal, row.Value); }
                        using (var command = Command("SELECT id FROM transfers ORDER BY id"))
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) { CheckBoth(); copy.SaveTicket(LoadTicket(reader.GetString(0))!); }
                        var copiedState = LoadState(); copiedState.ReadOnlyCopy = true;
                        copy.SaveState(copiedState); copy.Audit(); VerifyCopiedContents(copy, CheckBoth);
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
                var audit = new SqliteTerminalPersistenceStore(connection, parent, guard, options, new OpenOwner(), metadata, cipher);
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

    private void VerifyCopiedContents(SqliteTerminalPersistenceStore copy, Action check)
    {
        var original = LoadState(); var next = copy.LoadState();
        Require(next.ReadOnlyCopy, "integrity_mismatch");
        Require(original.Root.HasValue == next.Root.HasValue && (!original.Root.HasValue || Equal(original.Root.Value, next.Root!.Value)));
        foreach (var name in CounterNames) Require(original.Used[name] == next.Used[name]);
        using (var command = Command("SELECT kind,hash,bytes FROM objects ORDER BY kind,hash"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            { check(); var reference = new ObjectRef(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), false); Require(ReadObject(reference).SequenceEqual(copy.ReadObject(reference))); }
        using (var command = Command("SELECT primary_key FROM entries ORDER BY ordinal"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            { check(); var row = FindEntry(reader.GetString(0))!; var copied = copy.FindEntry(reader.GetString(0)); Require(copied != null && row.Ordinal == copied.Ordinal && Equal(row.Value, copied.Value)); }
        using (var command = Command("SELECT id FROM transfers ORDER BY id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            { check(); var row = LoadTicket(reader.GetString(0))!; var copied = copy.LoadTicket(reader.GetString(0)); Require(copied != null && Equal(TicketJson(row), TicketJson(copied))); }
    }

    private static void RequireNewMedia(string path)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            Require(!File.Exists(path + suffix) && !Directory.Exists(path + suffix), "storage_error");
    }
}
