using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Storage;

public sealed class WindowsSessionSnapshotStoreOptions
{
    public string Path { get; set; } = "";
    public SessionSnapshotScope Scope { get; set; } = null!;
    public Func<SessionSnapshotScope> ReadScope { get; set; } = null!;
    /// <summary>首次读取现有镜像/写入时取得原密钥提供者；构造不会创建密钥或数据库。</summary>
    public Func<IArchiveKeyProvider> KeyProvider { get; set; } = null!;
    public int MaximumSnapshotBytes { get; set; } = 32 * 1024 * 1024;
}

/// <summary>Windows 受护文件身份 + 原 ArchiveBodyCipher 的加密上下文镜像；独立 .NET 本地格式，不是 SDK2 档案介质或新 wire。</summary>
public sealed class WindowsSessionSnapshotStore : ISessionSnapshotStore, IDisposable
{
    public const string Format = "tansr-dotnet-sdk1-context-mirror-v1";
    private readonly object _gate = new();
    private readonly string _path, _scopeDigest;
    private readonly Func<SessionSnapshotScope> _readScope;
    private readonly Func<IArchiveKeyProvider> _keys;
    private readonly int _maximum;
    private SqliteConnection? _connection;
    private StorageFileIdentity? _parent, _file;
    private ArchiveBodyCipher? _cipher;
    private byte[]? _check;
    private bool _closed, _poisoned;

    public WindowsSessionSnapshotStore(WindowsSessionSnapshotStoreOptions options)
    {
        if (options == null || options.Scope == null || options.ReadScope == null || options.KeyProvider == null || options.MaximumSnapshotBytes < 1 || options.MaximumSnapshotBytes > 32 * 1024 * 1024)
            throw new ArgumentException("snapshot_invalid_options", nameof(options));
        _path = StorageFileIdentity.FullPath(options.Path); Scope = options.Scope; _readScope = options.ReadScope; _keys = options.KeyProvider; _maximum = options.MaximumSnapshotBytes;
        _scopeDigest = Digest(Encoding.UTF8.GetBytes(ScopeText(Scope)));
    }
    public SessionSnapshotScope Scope { get; }

    public Task<SessionSnapshotStoreState> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Run(() =>
        {
            if (!Open(false, cancellationToken)) return new SessionSnapshotStoreState(0, null);
            return ReadState();
        }, cancellationToken));

    public Task<long> WriteAsync(long expectedRevision, SessionSnapshotCopy snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var bytes = snapshot.Bytes;
        try
        {
            if (bytes.Length > _maximum) throw new StorageException("capacity_exceeded");
            return Task.FromResult(Run(() =>
            {
                Open(true, cancellationToken);
                var reference = Reference(snapshot.CheckpointId, bytes);
                var encrypted = _cipher!.Seal(reference, bytes);
                try
                {
                    return Mutate(expectedRevision, cancellationToken, transaction =>
                    {
                        using var update = _connection!.CreateCommand(); update.Transaction = transaction;
                        update.CommandText = "UPDATE mirror SET checkpoint=$checkpoint, reference=$reference, body=$body WHERE id=1";
                        update.Parameters.AddWithValue("$checkpoint", snapshot.CheckpointId); update.Parameters.AddWithValue("$reference", WireJson.CanonicalString(reference)); update.Parameters.AddWithValue("$body", encrypted); update.ExecuteNonQuery();
                    });
                }
                finally { ArchiveSecurityJson.Clear(encrypted); }
            }, cancellationToken));
        }
        finally { ArchiveSecurityJson.Clear(bytes); }
    }

    public Task<long> DeleteAsync(long expectedRevision, CancellationToken cancellationToken = default)
        => Task.FromResult(Run(() =>
        {
            Open(true, cancellationToken);
            return Mutate(expectedRevision, cancellationToken, transaction =>
            {
                using var command = _connection!.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE mirror SET checkpoint=NULL, reference=NULL, body=NULL WHERE id=1"; command.ExecuteNonQuery();
            });
        }, cancellationToken));

    private bool Open(bool create, CancellationToken token)
    {
        if (_connection != null) return true;
        if (!File.Exists(_path) && !create) return false;
        bool exists = File.Exists(_path);
        try
        {
            _parent = StorageFileIdentity.Open(Path.GetDirectoryName(_path)!, true);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) if (File.Exists(_path + suffix))
            { using var sidecar = StorageFileIdentity.Open(_path + suffix, false, metadataOnly: true); }
            _file = StorageFileIdentity.Open(_path, false, create: !exists);
            _cipher = new ArchiveBodyCipher(_keys(), CipherIdentity());
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
            _connection.Open();
            Execute("PRAGMA busy_timeout=0; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; PRAGMA trusted_schema=OFF;");
            if (!exists)
            {
                _check = _cipher.CreateCheck();
                using var transaction = _connection.BeginTransaction();
                using var command = _connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "CREATE TABLE metadata(id INTEGER PRIMARY KEY CHECK(id=1),format TEXT NOT NULL,scope TEXT NOT NULL,key_id TEXT NOT NULL,key_check BLOB NOT NULL) STRICT; " +
                    "CREATE TABLE mirror(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL CHECK(revision>=0),checkpoint TEXT,reference TEXT,body BLOB) STRICT; " +
                    "INSERT INTO metadata VALUES(1,$format,$scope,$key,$check); INSERT INTO mirror VALUES(1,0,NULL,NULL,NULL);";
                command.Parameters.AddWithValue("$format", Format); command.Parameters.AddWithValue("$scope", _scopeDigest); command.Parameters.AddWithValue("$key", _cipher.KeyId); command.Parameters.AddWithValue("$check", _check);
                command.ExecuteNonQuery(); Check(token); transaction.Commit();
            }
            else
            {
                using var command = _connection.CreateCommand(); command.CommandText = "SELECT format,scope,key_id,key_check FROM metadata WHERE id=1";
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.GetString(0) != Format || reader.GetString(1) != _scopeDigest || reader.GetString(2) != _cipher.KeyId) throw new StorageException("identity_mismatch");
                _check = (byte[])reader.GetValue(3); _cipher.VerifyCheck(_check);
                if (reader.Read()) throw new StorageException("integrity_mismatch");
            }
            using (var limit = _connection.CreateCommand())
            {
                var pages = (_maximum * 3L + 1048576) / 4096;
                limit.CommandText = "PRAGMA max_page_count=" + pages.ToString(CultureInfo.InvariantCulture);
                if (Convert.ToInt64(limit.ExecuteScalar(), CultureInfo.InvariantCulture) != pages) throw new StorageException("capacity_exceeded");
            }
            Check(token); return true;
        }
        catch
        {
            _connection?.Dispose(); _connection = null; _file?.Dispose(); _file = null; _parent?.Dispose(); _parent = null;
            _cipher = null; _check = null; throw;
        }
    }

    private SessionSnapshotStoreState ReadState()
    {
        using var command = _connection!.CreateCommand(); command.CommandText = "SELECT revision,checkpoint,reference,length(body),body,length(reference),length(checkpoint) FROM mirror WHERE id=1";
        long revision; string? checkpoint = null, referenceText = null; byte[]? encrypted = null;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new StorageException("integrity_mismatch");
            revision = reader.GetInt64(0);
            if (!reader.IsDBNull(1))
            {
                var length = reader.GetInt64(3);
                if (length < 1 || length > ArchiveBodyCipher.EncryptedBytes(_maximum)) throw new StorageException("capacity_exceeded");
                if (reader.GetInt64(5) > 4096 || reader.GetInt64(6) > 512) throw new StorageException("integrity_mismatch");
                checkpoint = reader.GetString(1); referenceText = reader.GetString(2); encrypted = (byte[])reader.GetValue(4);
                if (encrypted.LongLength != length) throw new StorageException("integrity_mismatch");
            }
            else if (!reader.IsDBNull(2) || !reader.IsDBNull(3)) throw new StorageException("integrity_mismatch");
        }
        if (checkpoint == null) return new SessionSnapshotStoreState(revision, null);
        var reference = WireJson.DecodeControl(Encoding.UTF8.GetBytes(referenceText!));
        if (reference.GetProperty("artifactId").GetString() != Digest(Encoding.UTF8.GetBytes(checkpoint)) || reference.GetProperty("bytes").GetInt64() > _maximum)
            throw new StorageException("integrity_mismatch");
        byte[]? bytes = null;
        try
        {
            bytes = _cipher!.Open(reference, encrypted!);
            if (Digest(bytes) != reference.GetProperty("sha256").GetString()) throw new StorageException("integrity_mismatch");
            return new SessionSnapshotStoreState(revision, new SessionSnapshotCopy(checkpoint, bytes));
        }
        finally { ArchiveSecurityJson.Clear(bytes); ArchiveSecurityJson.Clear(encrypted); }
    }

    private long Mutate(long expected, CancellationToken token, Action<SqliteTransaction> write)
    {
        if (expected < 0 || expected == long.MaxValue) throw new StorageException("invalid_input");
        using var transaction = _connection!.BeginTransaction();
        using (var revision = _connection.CreateCommand())
        {
            revision.Transaction = transaction; revision.CommandText = "SELECT revision FROM mirror WHERE id=1";
            if ((long?)revision.ExecuteScalar() != expected) throw new StorageException("revision_conflict");
        }
        write(transaction);
        using (var advance = _connection.CreateCommand()) { advance.Transaction = transaction; advance.CommandText = "UPDATE mirror SET revision=revision+1 WHERE id=1"; advance.ExecuteNonQuery(); }
        Check(token);
        try { transaction.Commit(); }
        catch { _poisoned = true; throw; }
        return expected + 1;
    }
    private T Run<T>(Func<T> action, CancellationToken token)
    {
        lock (_gate)
        {
            Check(token);
            try { var result = action(); Check(token); return result; }
            catch (SqliteException error) { throw new StorageException(error.SqliteErrorCode is 5 or 6 ? "storage_busy" : error.SqliteErrorCode == 13 ? "capacity_exceeded" : "storage_error"); }
            catch (WireProtocolException) { throw new StorageException("integrity_mismatch"); }
            catch (IOException) { throw new StorageException("storage_error"); }
            catch (UnauthorizedAccessException) { throw new StorageException("storage_error"); }
        }
    }
    private void Check(CancellationToken token)
    {
        if (_closed) throw new ObjectDisposedException(nameof(WindowsSessionSnapshotStore));
        if (_poisoned) throw new StorageException("commit_unknown_reopen_required");
        token.ThrowIfCancellationRequested();
        if (!Scope.Equals(_readScope())) throw new StorageException("context_changed");
        _parent?.Check(); _file?.Check(); if (_check != null) _cipher!.VerifyCheck(_check);
    }
    private JsonElement CipherIdentity() => ArchiveSecurityJson.Build(writer =>
    {
        writer.WriteStartObject(); writer.WriteStartObject("scope"); writer.WriteString("applicationScopeId", _scopeDigest); writer.WriteString("endUserId", "sdk1-context-mirror"); writer.WriteEndObject();
        writer.WriteString("bindingId", "sdk1-context-mirror"); writer.WriteString("sourceId", "sdk1-context-mirror"); writer.WriteString("sourceGeneration", "1");
        writer.WriteStartObject("target"); writer.WriteString("sessionId", _scopeDigest); writer.WriteStartObject("generations");
        writer.WriteString("historyEpoch", "local-mirror"); writer.WriteString("projectionRevision", "0"); writer.WriteString("deletionGeneration", "0");
        writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
    });
    private static JsonElement Reference(string checkpoint, byte[] bytes) => ArchiveSecurityJson.Build(writer =>
    {
        writer.WriteStartObject(); writer.WriteString("artifactId", Digest(Encoding.UTF8.GetBytes(checkpoint))); writer.WriteString("sourceId", "sdk1-context-mirror");
        writer.WriteString("mediaType", "application/octet-stream"); writer.WriteNumber("bytes", bytes.Length); writer.WriteString("sha256", Digest(bytes)); writer.WriteEndObject();
    });
    private static string ScopeText(SessionSnapshotScope scope) => ArchiveSecurityJson.Build(writer =>
    { writer.WriteStartArray(); writer.WriteStringValue(scope.ServeAuthority); writer.WriteStringValue(scope.ApplicationScopeId); writer.WriteStringValue(scope.EndUserId); writer.WriteStringValue(scope.SessionId); writer.WriteEndArray(); }).GetRawText();
    private static string Digest(byte[] bytes) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private void Execute(string sql) { using var command = _connection!.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose()
    {
        lock (_gate) { if (_closed) return; _closed = true; _connection?.Dispose(); _file?.Dispose(); _parent?.Dispose(); ArchiveSecurityJson.Clear(_check); }
    }
}
