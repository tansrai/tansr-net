using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using A = Tansr.Sdk.Windows.Storage.ArchiveValidation;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>单份原 MaterialResponseRequest 的耐久发件箱。独立 .NET 私有介质，不是原 archive SQLite 文件族或新 wire 协议。</summary>
public sealed class SqliteMaterialResponseOutbox : IMaterialResponseOutbox, IDisposable
{
    public const string Format = "tansr-dotnet-material-response-outbox-sqlite-v1";
    public const int MaximumResponseBytes = 262144;
    private const int MaximumMetadataBytes = 16384;
    private static readonly string[] Schema =
    {
        "CREATE TABLE metadata (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL) STRICT",
        "CREATE TABLE pending (id INTEGER PRIMARY KEY CHECK(id=1), response BLOB NOT NULL CHECK(length(response)>=1 AND length(response)<=262144)) STRICT",
    };
    private readonly object _gate = new object();
    private readonly SqliteConnection _connection;
    private readonly StorageFileIdentity _parent, _file;
    private readonly JsonElement _identity;
    private readonly Func<JsonElement> _readContext;
    private readonly string _metadata;
    private string? _knownDigest;
    private bool _busy, _poisoned, _uncertain, _closed;

    private SqliteMaterialResponseOutbox(SqliteConnection connection, StorageFileIdentity parent, StorageFileIdentity file,
        JsonElement identity, Func<JsonElement> readContext, string metadata)
    { _connection = connection; _parent = parent; _file = file; _identity = identity; _readContext = readContext; _metadata = metadata; }

    public static Task<SqliteMaterialResponseOutbox> OpenAsync(SqliteMaterialResponseOutboxOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options == null || options.ReadContext == null || options.MaxPages < 8 || options.MaxPages > 1024 ||
            (options.Mode != StorageOpenMode.Create && options.Mode != StorageOpenMode.Reopen)) throw new StorageException("invalid_input");
        string path = StorageFileIdentity.FullPath(options.Path); var identity = A.Identity(options.Identity);
        var readContext = options.ReadContext; var mode = options.Mode; int maxPages = options.MaxPages;
        StorageFileIdentity? parent = null, file = null; SqliteConnection? connection = null;
        try
        {
            var scope = Scope(readContext, identity);
            parent = StorageFileIdentity.Open(Path.GetDirectoryName(path)!, true);
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) if (File.Exists(path + suffix))
            { A.Need(mode != StorageOpenMode.Create, "identity_mismatch"); using var sidecar = StorageFileIdentity.Open(path + suffix, false); }
            file = StorageFileIdentity.Open(path, false, mode == StorageOpenMode.Create);
            string metadata = Metadata(identity, maxPages, parent, file);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 0 }.ToString()); connection.Open();
            var outbox = new SqliteMaterialResponseOutbox(connection, parent, file, identity, readContext, metadata);
            outbox.Exec("PRAGMA busy_timeout=0; PRAGMA locking_mode=EXCLUSIVE; PRAGMA synchronous=FULL");
            void CheckOpening()
            {
                cancellationToken.ThrowIfCancellationRequested(); parent.Check(); file.Check();
                A.Need(A.Equal(scope, Scope(readContext, identity)), "context_changed"); cancellationToken.ThrowIfCancellationRequested();
            }
            if (mode == StorageOpenMode.Create)
            {
                outbox.Exec("PRAGMA page_size=4096; PRAGMA journal_mode=WAL");
                outbox.Transaction(() =>
                {
                    foreach (string sql in Schema) outbox.Exec(sql);
                    outbox.Exec("INSERT INTO metadata VALUES(1,$json)", ("$json", metadata));
                }, CheckOpening, false);
            }
            else A.Need(outbox.Number("PRAGMA page_size") == 4096 && (string?)outbox.Scalar("PRAGMA journal_mode") == "wal" && (string?)outbox.Scalar("PRAGMA quick_check") == "ok");
            A.Need(outbox.Number("PRAGMA max_page_count=" + maxPages.ToString(CultureInfo.InvariantCulture)) == maxPages, "capacity_exceeded");
            outbox.CheckFixed(); outbox._knownDigest = Digest(outbox.Row()); CheckOpening();
            return Task.FromResult(outbox);
        }
        catch (Exception error)
        {
            connection?.Dispose(); file?.Dispose(); parent?.Dispose();
            if (error is SqliteException || error is IOException || error is UnauthorizedAccessException) throw new StorageException("storage_error");
            throw;
        }
    }

    public Task<JsonElement?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run<JsonElement?>(_ =>
    { var bytes = Row(); return bytes == null ? null : WireJson.DecodeControl(bytes, MaximumResponseBytes); }, cancellationToken));

    public Task SaveIfEmptyAsync(JsonElement response, CancellationToken cancellationToken = default)
    {
        Run<object?>(check =>
        {
            var bytes = Response(response); var pending = Row();
            if (pending != null) { A.Need(pending.SequenceEqual(bytes), "pending_conflict"); return null; }
            Transaction(() => Exec("INSERT INTO pending VALUES(1,$response)", ("$response", bytes)), check); return null;
        }, cancellationToken); return Task.CompletedTask;
    }

    public Task ClearIfExactAsync(JsonElement response, CancellationToken cancellationToken = default)
    {
        Run<object?>(check =>
        {
            var bytes = Response(response); var pending = Row(); A.Need(pending != null && pending.SequenceEqual(bytes), "receipt_mismatch");
            Transaction(() => Exec("DELETE FROM pending WHERE id=1"), check); return null;
        }, cancellationToken); return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
            if (_closed) return;
            _connection.Dispose(); _file.Dispose(); _parent.Dispose(); _closed = true;
        }
    }

    private T Run<T>(Func<Action, T> work, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
            cancellationToken.ThrowIfCancellationRequested(); A.Need(!_closed, "closed"); A.Need(!_uncertain, "reconciliation_required");
            _busy = true; _poisoned = false;
            try
            {
                var scope = Scope(_readContext, _identity);
                void Check()
                {
                    A.Need(!_poisoned, "reentrant"); cancellationToken.ThrowIfCancellationRequested(); CheckFixed();
                    A.Need(A.Equal(scope, Scope(_readContext, _identity)), "context_changed");
                    A.Need(!_poisoned, "reentrant"); cancellationToken.ThrowIfCancellationRequested();
                }
                Check(); A.Need(_knownDigest == Digest(Row())); var result = work(Check); Check(); return result;
            }
            catch (SqliteException) { throw new StorageException("storage_error"); }
            finally { _busy = false; }
        }
    }

    private void Transaction(Action work, Action check, bool refresh = true)
    {
        bool begun = false, committing = false;
        try
        {
            Exec("BEGIN IMMEDIATE"); begun = true; work(); check(); committing = true; Exec("COMMIT"); begun = false;
            if (refresh) _knownDigest = Digest(Row()); committing = false;
        }
        catch
        {
            bool unknown = committing;
            if (begun) { try { Exec("ROLLBACK"); } catch { unknown = true; } }
            if (unknown) { _uncertain = true; throw new StorageException("reconciliation_required"); }
            throw;
        }
    }

    private void CheckFixed()
    {
        _parent.Check(); _file.Check();
        A.Need(Number("SELECT count(*) FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_'") == Schema.Length);
        A.Need(Number("SELECT count(*) FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' AND (length(CAST(sql AS BLOB))>4096 OR length(CAST(name AS BLOB))>128)") == 0);
        using (var command = Command("SELECT sql FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' ORDER BY name LIMIT 3"))
        using (var reader = command.ExecuteReader())
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) { A.Need(!reader.IsDBNull(0) && Schema.Contains(reader.GetString(0))); found.Add(reader.GetString(0)); }
            A.Need(found.Count == Schema.Length);
        }
        A.Need(Number("SELECT count(*) FROM metadata") == 1);
        A.Need((string?)Scalar("SELECT json FROM metadata WHERE id=1 AND length(CAST(json AS BLOB))<=16384") == _metadata, "identity_mismatch");
        A.Need(Number("SELECT count(*) FROM pending") <= 1);
        A.Need(Number("SELECT count(*) FROM pending WHERE id<>1 OR typeof(response)<>'blob'") == 0);
    }

    private byte[]? Row()
    {
        // 长度先在 SQL 中读取和限制，再装载单份响应；坏大行不能当空 outbox。
        var lengthValue = Scalar("SELECT length(response) FROM pending WHERE id=1"); if (lengthValue == null) return null;
        long length = Convert.ToInt64(lengthValue, CultureInfo.InvariantCulture); A.Need(length >= 1 && length <= MaximumResponseBytes);
        var bytes = Scalar("SELECT response FROM pending WHERE id=1 AND length(response)=$length", ("$length", length)) as byte[]; A.Need(bytes != null && bytes.LongLength == length);
        try
        {
            var value = WireJson.DecodeControl(bytes!, MaximumResponseBytes); var verified = Response(value); A.Need(bytes!.SequenceEqual(verified)); return bytes;
        }
        catch (WireProtocolException) { throw new StorageException("integrity_mismatch"); }
    }

    private byte[] Response(JsonElement response)
    {
        var value = A.Copy(response, "MaterialResponseRequest", MaximumResponseBytes);
        foreach (string name in new[] { "bindingId", "sourceId", "sourceGeneration" }) A.Need(A.String(value, name) == A.String(_identity, name), "identity_mismatch");
        var target = value.GetProperty("target"); var expected = _identity.GetProperty("target");
        A.Need(A.String(target, "sessionId") == A.String(expected, "sessionId") && A.Equal(target.GetProperty("generations"), expected.GetProperty("generations")), "identity_mismatch");
        var records = value.GetProperty("results").EnumerateArray().Select(record => A.String(record, "recordId")).ToArray();
        A.Need(records.Distinct(StringComparer.Ordinal).Count() == records.Length, "invalid_input"); return WireJson.EncodeControl(value, MaximumResponseBytes);
    }

    private static JsonElement Scope(Func<JsonElement> read, JsonElement identity)
    {
        JsonElement scope;
        try { scope = A.Copy(read(), "Scope", MaximumMetadataBytes); }
        catch (StorageException) { throw; }
        catch { throw new StorageException("context_changed"); }
        A.Need(A.Equal(A.Without(scope, "authorizationRevision"), identity.GetProperty("scope")), "identity_mismatch"); return scope;
    }

    private static string Metadata(JsonElement identity, int maxPages, StorageFileIdentity parent, StorageFileIdentity file)
        => A.Text(A.Object(writer =>
        {
            writer.WriteString("format", Format); A.Property(writer, "identity", identity);
            writer.WriteNumber("maximumResponseBytes", MaximumResponseBytes); writer.WriteNumber("maxPages", maxPages); writer.WriteNumber("pageSize", 4096);
            writer.WriteStartObject("physical"); writer.WriteStartObject("directory"); writer.WriteString("dev", parent.Device); writer.WriteString("ino", parent.Inode); writer.WriteEndObject();
            writer.WriteStartObject("file"); writer.WriteString("dev", file.Device); writer.WriteString("ino", file.Inode); writer.WriteEndObject(); writer.WriteEndObject();
        }), MaximumMetadataBytes);

    private static string? Digest(byte[]? bytes) => bytes == null ? null : WireJson.Sha256(bytes);
    private SqliteCommand Command(string sql, params (string Name, object Value)[] args)
    { var command = _connection.CreateCommand(); command.CommandText = sql; foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value); return command; }
    private void Exec(string sql, params (string Name, object Value)[] args) { using var command = Command(sql, args); command.ExecuteNonQuery(); }
    private object? Scalar(string sql, params (string Name, object Value)[] args) { using var command = Command(sql, args); return command.ExecuteScalar(); }
    private long Number(string sql, params (string Name, object Value)[] args) => Convert.ToInt64(Scalar(sql, args), CultureInfo.InvariantCulture);
}
