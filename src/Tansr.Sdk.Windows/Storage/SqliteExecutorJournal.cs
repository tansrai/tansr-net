using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>默认兼容原 sdk2-execution-sqlite-v1，可显式选择独立紧凑介质。首次耐久占用才授予执行，重开、超时和断线均不重新授予。</summary>
public sealed class SqliteExecutorJournal : IExecutorJournal, IDisposable
{
    public const string Format = "sdk2-execution-sqlite-v1";
    public const string CompactFormat = "sdk2-execution-sqlite-compact-v1";
    private const int MaximumOperationBytes = 1048576;
    private const int ReceiptReserve = 262144;
    private static readonly string[] Schema =
    {
        "CREATE TABLE metadata (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL) STRICT",
        "CREATE TABLE operations (id TEXT PRIMARY KEY, operation TEXT NOT NULL, receipt TEXT, reserve BLOB NOT NULL) STRICT",
        "CREATE TABLE state (id INTEGER PRIMARY KEY CHECK(id=1), logical_bytes INTEGER NOT NULL, operation_count INTEGER NOT NULL) STRICT",
    };
    private readonly object _gate = new object();
    private readonly SqliteConnection _connection;
    private readonly StorageFileIdentity _parent;
    private readonly StorageFileIdentity _file;
    private readonly string _application, _user, _executor, _metadata;
    private readonly int _maximumOperations;
    private readonly long _maximumBytes;
    private readonly bool _compactCompletedReceipts;
    private readonly Func<JsonElement> _readContext;
    private bool _busy, _poisoned, _uncertain, _closed;
    private (long Bytes, int Count)? _known;

    private SqliteExecutorJournal(SqliteConnection connection, StorageFileIdentity parent, StorageFileIdentity file,
        SqliteExecutorJournalOptions options, string metadata)
    {
        _connection = connection; _parent = parent; _file = file; _metadata = metadata;
        _application = options.ApplicationScopeId; _user = options.EndUserId; _executor = options.ExecutorId;
        _maximumOperations = options.MaxOperations; _maximumBytes = options.MaxStoredBytes; _readContext = options.ReadContext;
        _compactCompletedReceipts = options.CompactCompletedReceipts;
    }

    public static Task<SqliteExecutorJournal> OpenAsync(SqliteExecutorJournalOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options == null || options.ReadContext == null || options.MaxOperations < 1 || options.MaxOperations > 1000000 ||
            options.MaxStoredBytes < 1 || options.MaxStoredBytes > 1073741824 || options.MaxPages < 8 || options.MaxPages > 262144 ||
            (options.Mode != StorageOpenMode.Create && options.Mode != StorageOpenMode.Reopen)) throw new StorageException("invalid_input");
        var fixedOptions = new SqliteExecutorJournalOptions
        {
            Path = StorageFileIdentity.FullPath(options.Path),
            Mode = options.Mode,
            CompactCompletedReceipts = options.CompactCompletedReceipts,
            ApplicationScopeId = options.ApplicationScopeId,
            EndUserId = options.EndUserId,
            ExecutorId = options.ExecutorId,
            MaxOperations = options.MaxOperations,
            MaxStoredBytes = options.MaxStoredBytes,
            MaxPages = options.MaxPages,
            ReadContext = options.ReadContext,
        };
        WireJson.ValidateNamed("Id", StringValue(fixedOptions.ApplicationScopeId));
        WireJson.ValidateNamed("LegacyId", StringValue(fixedOptions.EndUserId));
        WireJson.ValidateNamed("Id", StringValue(fixedOptions.ExecutorId));
        StorageFileIdentity? parent = null, file = null;
        SqliteConnection? connection = null;
        try
        {
            var originalScope = ReadScope(fixedOptions.ReadContext, fixedOptions.ApplicationScopeId, fixedOptions.EndUserId);
            parent = StorageFileIdentity.Open(System.IO.Path.GetDirectoryName(fixedOptions.Path)!, true);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(fixedOptions.Path + suffix))
                {
                    if (fixedOptions.Mode == StorageOpenMode.Create) throw new StorageException("identity_mismatch");
                    using var guard = StorageFileIdentity.Open(fixedOptions.Path + suffix, false);
                }
            file = StorageFileIdentity.Open(fixedOptions.Path, false, fixedOptions.Mode == StorageOpenMode.Create);
            var metadata = BuildMetadata(fixedOptions, parent, file);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fixedOptions.Path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            var journal = new SqliteExecutorJournal(connection, parent, file, fixedOptions, metadata);
            journal.Exec("PRAGMA busy_timeout=0; PRAGMA locking_mode=EXCLUSIVE; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL");
            if (fixedOptions.Mode == StorageOpenMode.Create)
            {
                journal.Exec("PRAGMA page_size=4096; PRAGMA journal_mode=WAL");
                if (Bytes(metadata) + 128 > options.MaxStoredBytes) throw new StorageException("capacity_exceeded");
                journal.Transaction(() =>
                {
                    foreach (var sql in Schema) journal.Exec(sql);
                    journal.Exec("INSERT INTO metadata VALUES (1,$value)", ("$value", metadata));
                    journal.Exec("INSERT INTO state VALUES (1,$bytes,0)", ("$bytes", Bytes(metadata) + 128));
                }, () => Require(Same(originalScope, ReadScope(fixedOptions.ReadContext, fixedOptions.ApplicationScopeId, fixedOptions.EndUserId)), "context_changed"), false);
            }
            else
            {
                Require(Convert.ToInt64(journal.Scalar("PRAGMA page_size"), CultureInfo.InvariantCulture) == 4096);
                Require((string?)journal.Scalar("PRAGMA quick_check") == "ok");
                Require((string?)journal.Scalar("PRAGMA journal_mode") == "wal");
                journal.Exec("BEGIN EXCLUSIVE");
                try { journal.Initialize(); journal.Exec("COMMIT"); }
                catch { try { journal.Exec("ROLLBACK"); } catch { } throw; }
            }
            Require(Convert.ToInt64(journal.Scalar("PRAGMA max_page_count=" + fixedOptions.MaxPages.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture) == fixedOptions.MaxPages, "capacity_exceeded");
            journal.Initialize();
            Require(Same(originalScope, ReadScope(fixedOptions.ReadContext, fixedOptions.ApplicationScopeId, fixedOptions.EndUserId)), "context_changed");
            return Task.FromResult(journal);
        }
        catch (StorageException) { connection?.Dispose(); file?.Dispose(); parent?.Dispose(); throw; }
        catch (Exception error) when (error is SqliteException || error is IOException || error is UnauthorizedAccessException)
        { connection?.Dispose(); file?.Dispose(); parent?.Dispose(); throw new StorageException("storage_error"); }
        catch { connection?.Dispose(); file?.Dispose(); parent?.Dispose(); throw; }
    }

    public Task<ExecutorJournalClaim> ClaimAsync(JsonElement operation, CancellationToken cancellationToken = default) => Task.FromResult(Run((scope, check) =>
    {
        var op = ValidateOperation(operation);
        Require(Same(op.GetProperty("scope"), scope), "context_changed");
        var text = Canonical(op); var id = op.GetProperty("operationId").GetString()!;
        var previous = Row(id);
        if (previous != null)
        {
            Require(previous.Value.Operation == text);
            return previous.Value.Receipt == null ? Pending(previous.Value) : ExecutorJournalClaim.Completed(ValidateStoredReceipt(previous.Value, op));
        }
        Transaction(() =>
        {
            var state = State(); long added = Bytes(id) + Bytes(text) + ReceiptReserve;
            Require(state.Count < _maximumOperations && state.Bytes + added <= _maximumBytes, "capacity_exceeded");
            Exec("INSERT INTO operations VALUES ($id,$operation,NULL,zeroblob($reserve))", ("$id", id), ("$operation", text), ("$reserve", ReceiptReserve));
            Exec("UPDATE state SET logical_bytes=$bytes,operation_count=$count WHERE id=1", ("$bytes", state.Bytes + added), ("$count", state.Count + 1));
        }, check);
        return ExecutorJournalClaim.Claimed();
    }, cancellationToken));

    public Task CompleteAsync(JsonElement operation, JsonElement receipt, CancellationToken cancellationToken = default)
    {
        Run<object?>((_scope, check) =>
        {
            // 已发生的事实允许在同一 app/user 下结算；旧授权不能继续执行，但其结果不可因授权更新丢失。
            var op = ValidateOperation(operation); var result = ValidateReceipt(receipt, op); var text = Canonical(result);
            var id = op.GetProperty("operationId").GetString()!; var row = Row(id);
            Require(row != null && row.Value.Operation == Canonical(op), "receipt_mismatch");
            if (row!.Value.Receipt != null)
            {
                Require(row.Value.Receipt == text && ValidTerminalReserve(text, row.Value.Reserved), "receipt_mismatch");
                return null;
            }
            Require(row.Value.Reserved == ReceiptReserve);
            Transaction(() =>
            {
                int unused = ReceiptReserve - Bytes(text);
                Exec("UPDATE operations SET receipt=$receipt,reserve=zeroblob($reserve) WHERE id=$id",
                    ("$receipt", text), ("$reserve", _compactCompletedReceipts ? 0 : unused), ("$id", id));
                if (_compactCompletedReceipts)
                {
                    // 与唯一终态同事务释放未用预留；不删除操作锚、不降低条数、也不重新授予未知结果。
                    var state = State(); Require(state.Bytes >= unused);
                    Exec("UPDATE state SET logical_bytes=$bytes WHERE id=1", ("$bytes", state.Bytes - unused));
                }
            }, check);
            return null;
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task<JsonElement?> ReceiptAsync(JsonElement operation, CancellationToken cancellationToken = default) => Task.FromResult(Run<JsonElement?>((_scope, _check) =>
    {
        var op = ValidateOperation(operation); var row = Row(op.GetProperty("operationId").GetString()!);
        Require(row != null && row.Value.Operation == Canonical(op), "receipt_mismatch");
        if (row!.Value.Receipt == null) { Require(row.Value.Reserved == ReceiptReserve); return null; }
        return ValidateStoredReceipt(row.Value, op);
    }, cancellationToken));

    public Task<IReadOnlyList<JsonElement>> OperationsAsync(string? afterOperationId = null, CancellationToken cancellationToken = default) => Task.FromResult(Run<IReadOnlyList<JsonElement>>((_scope, _check) =>
    {
        if (afterOperationId != null) WireJson.ValidateNamed("Id", StringValue(afterOperationId));
        var output = new List<JsonElement>(); int bytes = 0;
        using var command = Command("SELECT id,CASE WHEN length(CAST(operation AS BLOB))<=1048576 THEN operation ELSE NULL END FROM operations WHERE id>$after ORDER BY id LIMIT 128", ("$after", afterOperationId ?? ""));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Require(!reader.IsDBNull(1)); var text = reader.GetString(1);
            if (bytes + Bytes(text) > 2 * MaximumOperationBytes) break;
            var op = ValidateOperation(Stored(text, "ExecutionOperation", MaximumOperationBytes));
            Require(reader.GetString(0) == op.GetProperty("operationId").GetString());
            output.Add(op); bytes += Bytes(text);
        }
        return output.AsReadOnly();
    }, cancellationToken));

    public Task CloseAsync() { Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
            if (_closed) return;
            // 关闭不再请求授权或密钥；撤销后仍必须释放持有的句柄。
            _connection.Dispose(); _file.Dispose(); _parent.Dispose(); _closed = true;
        }
    }

    private T Run<T>(Func<JsonElement, Action, T> action, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
            if (_closed) throw new StorageException("closed");
            if (_uncertain) throw new StorageException("reconciliation_required");
            _busy = true; _poisoned = false;
            try
            {
                var scope = ReadScope(_readContext, _application, _user);
                void Check()
                {
                    Require(!_poisoned, "reentrant");
                    Require(Same(scope, ReadScope(_readContext, _application, _user)), "context_changed");
                    Require(!_poisoned, "reentrant"); CheckFixed();
                }
                Check(); if (_known.HasValue) Require(State() == _known.Value);
                var result = action(scope, Check); Check(); return result;
            }
            catch (SqliteException) { throw new StorageException("storage_error"); }
            finally { _busy = false; }
        }
    }

    private void Transaction(Action work, Action check, bool refreshState = true)
    {
        bool begun = false, committing = false;
        try
        {
            Exec("BEGIN IMMEDIATE"); begun = true; work(); check(); committing = true; Exec("COMMIT"); begun = false;
            if (refreshState) _known = State();
            committing = false;
        }
        catch
        {
            bool unknown = committing;
            if (begun) { try { if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle!) == 0) Exec("ROLLBACK"); } catch { unknown = true; } }
            if (unknown) { _uncertain = true; throw new StorageException("reconciliation_required"); }
            throw;
        }
    }

    private void Initialize()
    {
        CheckFixed(); var state = State();
        Require(Convert.ToInt64(Scalar("SELECT count(*) FROM metadata"), CultureInfo.InvariantCulture) == 1 &&
            Convert.ToInt64(Scalar("SELECT count(*) FROM state"), CultureInfo.InvariantCulture) == 1);
        Require(Convert.ToInt64(Scalar("SELECT count(*) FROM operations WHERE length(CAST(id AS BLOB))>128 OR length(CAST(operation AS BLOB))>1048576 OR length(CAST(receipt AS BLOB))>262144 OR length(reserve)>262144"), CultureInfo.InvariantCulture) == 0);
        using var command = Command("SELECT id,operation,receipt,length(reserve) FROM operations");
        using var reader = command.ExecuteReader(); int count = 0; long account = Bytes(_metadata) + 128;
        while (reader.Read())
        {
            var id = reader.GetString(0); var text = reader.GetString(1); var receipt = reader.IsDBNull(2) ? null : reader.GetString(2); int reserved = reader.GetInt32(3);
            Require(Bytes(id) <= 128 && Bytes(text) <= MaximumOperationBytes && reserved >= 0 && reserved <= ReceiptReserve);
            var op = ValidateOperation(Stored(text, "ExecutionOperation", MaximumOperationBytes));
            Require(id == op.GetProperty("operationId").GetString());
            if (receipt == null) Require(reserved == ReceiptReserve);
            else ValidateStoredReceipt((text, receipt, reserved), op);
            account += Bytes(id) + Bytes(text) + (receipt == null ? 0 : Bytes(receipt)) + reserved;
            Require(++count <= _maximumOperations && account <= _maximumBytes);
        }
        Require(count == state.Count && account == state.Bytes); _known = state;
    }

    private void CheckFixed()
    {
        _parent.Check(); _file.Check();
        using var command = Command("SELECT sql FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' ORDER BY name LIMIT 4");
        using (var reader = command.ExecuteReader())
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) { Require(!reader.IsDBNull(0)); var sql = reader.GetString(0); Require(Schema.Contains(sql)); found.Add(sql); }
            Require(found.Count == Schema.Length);
        }
        Require((string?)Scalar("SELECT json FROM metadata WHERE id=1 AND length(CAST(json AS BLOB))<=1048576") == _metadata, "identity_mismatch");
    }

    private (long Bytes, int Count) State()
    {
        using var command = Command("SELECT logical_bytes,operation_count FROM state WHERE id=1"); using var reader = command.ExecuteReader();
        Require(reader.Read()); long bytes = reader.GetInt64(0); int count = reader.GetInt32(1);
        Require(bytes >= 0 && bytes <= _maximumBytes && count >= 0 && count <= _maximumOperations); return (bytes, count);
    }

    private (string Operation, string? Receipt, int Reserved)? Row(string id)
    {
        using var command = Command("SELECT CASE WHEN length(CAST(operation AS BLOB))<=1048576 THEN operation END, CASE WHEN length(CAST(receipt AS BLOB))<=262144 THEN receipt END, length(reserve), receipt IS NOT NULL FROM operations WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        Require(!reader.IsDBNull(0) && (!reader.GetBoolean(3) || !reader.IsDBNull(1)));
        var reserved = reader.GetInt32(2); Require(reserved >= 0 && reserved <= ReceiptReserve);
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reserved);
    }

    private JsonElement ValidateOperation(JsonElement input)
    {
        var op = Snapshot(input, "ExecutionOperation", MaximumOperationBytes);
        var unsigned = Object(writer => { foreach (var property in op.EnumerateObject()) if (property.Name != "digest") property.WriteTo(writer); });
        Require(WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(unsigned, MaximumOperationBytes)) == op.GetProperty("digest").GetString());
        var scope = op.GetProperty("scope"); var target = op.GetProperty("binding").GetProperty("target");
        Require(scope.GetProperty("applicationScopeId").GetString() == _application && scope.GetProperty("endUserId").GetString() == _user &&
            target.GetProperty("executorId").GetString() == _executor, "identity_mismatch");
        if (op.GetProperty("request").GetProperty("operation").GetString() == "tool.invoke")
        {
            var args = op.GetProperty("request").GetProperty("args");
            Require(Tansr.Sdk.Terminal.TerminalCandidateContract.ValidToolInvocation(op));
            Require(WireJson.Parse(Encoding.UTF8.GetBytes(args.GetProperty("argsJson").GetString()!)).ValueKind == JsonValueKind.Object);
        }
        return op;
    }

    private JsonElement ValidateReceipt(JsonElement input, JsonElement op)
    {
        var value = Snapshot(input, "ExecutionReceiptRequest", ReceiptReserve); var target = op.GetProperty("binding").GetProperty("target");
        Require(value.GetProperty("operationId").GetString() == op.GetProperty("operationId").GetString() &&
            value.GetProperty("digest").GetString() == op.GetProperty("digest").GetString() &&
            value.GetProperty("executorId").GetString() == target.GetProperty("executorId").GetString() &&
            value.GetProperty("connectionId").GetString() == target.GetProperty("connectionId").GetString(), "receipt_mismatch");
        var result = value.GetProperty("result"); var status = value.GetProperty("status").GetString(); var error = value.GetProperty("errorCode");
        Require(status == "completed" ? result.ValueKind == JsonValueKind.Object && error.ValueKind == JsonValueKind.Null :
            result.ValueKind == JsonValueKind.Null && error.ValueKind == JsonValueKind.String, "receipt_mismatch");
        if (result.ValueKind != JsonValueKind.Null)
        {
            var request = op.GetProperty("request"); var kind = request.GetProperty("operation").GetString();
            Require(result.GetProperty("operation").GetString() == kind, "receipt_mismatch");
            var actual = result.GetProperty("args"); var expected = request.GetProperty("args");
            if (kind == "fs.read") Require(WireJson.DecodeBase64(actual.GetProperty("bytesBase64").GetString()!).Length <= expected.GetProperty("length").GetInt32(), "receipt_mismatch");
            if (kind == "fs.write") Require(actual.GetProperty("hash").GetString() == WireJson.Sha256(WireJson.DecodeBase64(expected.GetProperty("bytesBase64").GetString()!)), "receipt_mismatch");
            if (kind == "fs.list")
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in actual.GetProperty("entries").EnumerateArray())
                {
                    string name = entry.GetProperty("name").GetString()!;
                    Require(names.Add(name) && name != "." && name != ".." && name.IndexOfAny(new[] { '/', '\\', '\0' }) < 0, "receipt_mismatch");
                }
            }
            if (kind == "process.exec")
            {
                Require(Bytes(actual.GetProperty("stdout").GetString()!) + Bytes(actual.GetProperty("stderr").GetString()!) <= expected.GetProperty("maxOutputBytes").GetInt32(), "receipt_mismatch");
                var code = actual.GetProperty("exitCode");
                Require(code.ValueKind == JsonValueKind.Null || int.TryParse(code.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _), "receipt_mismatch");
            }
            if (kind == "tool.invoke") ValidateToolReceipt(actual.GetProperty("resultJson").GetString()!);
        }
        return value;
    }

    private static void ValidateToolReceipt(string text)
    {
        Require(Bytes(text) <= 32768, "receipt_mismatch");
        var value = WireJson.Parse(Encoding.UTF8.GetBytes(text));
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("status", out _), "receipt_mismatch");
        if (value.GetProperty("status").GetString() == "error")
        {
            Require(value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && message.GetString()!.Length >= 1 && message.GetString()!.Length <= 4096, "receipt_mismatch"); return;
        }
        Require(value.GetProperty("status").GetString() == "ok" && value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() >= 1 && content.GetArrayLength() <= 64, "receipt_mismatch");
        Require(!value.TryGetProperty("isError", out var error) || error.ValueKind == JsonValueKind.True || error.ValueKind == JsonValueKind.False, "receipt_mismatch");
        foreach (var item in value.GetProperty("content").EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("t", out _), "receipt_mismatch");
            if (item.GetProperty("t").GetString() == "text") Require(item.TryGetProperty("text", out var body) && body.ValueKind == JsonValueKind.String, "receipt_mismatch");
            else Require(item.GetProperty("t").GetString() == "image" && item.TryGetProperty("mime", out var mime) && new[] { "image/png", "image/jpeg", "image/webp", "image/gif" }.Contains(mime.GetString()) && item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String, "receipt_mismatch");
        }
    }

    private JsonElement ValidateStoredReceipt((string Operation, string? Receipt, int Reserved) row, JsonElement op)
    {
        Require(row.Receipt != null && ValidTerminalReserve(row.Receipt, row.Reserved));
        return ValidateReceipt(Stored(row.Receipt!, "ExecutionReceiptRequest", ReceiptReserve), op);
    }

    private bool ValidTerminalReserve(string receipt, int reserved) =>
        _compactCompletedReceipts ? reserved == 0 : Bytes(receipt) + reserved == ReceiptReserve;

    private static ExecutorJournalClaim Pending((string Operation, string? Receipt, int Reserved) row)
    { Require(row.Reserved == ReceiptReserve); return ExecutorJournalClaim.Pending(); }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value); return command;
    }
    private void Exec(string sql, params (string Name, object Value)[] parameters) { using var command = Command(sql, parameters); command.ExecuteNonQuery(); }
    private object? Scalar(string sql) { using var command = Command(sql); return command.ExecuteScalar(); }
    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
    private static string Canonical(JsonElement value) => WireJson.CanonicalString(value, MaximumOperationBytes);
    private static bool Same(JsonElement a, JsonElement b) => Canonical(a) == Canonical(b);
    private static void Require(bool condition, string code = "integrity_mismatch") { if (!condition) throw new StorageException(code); }
    private static JsonElement Snapshot(JsonElement value, string name, int maximum)
    { var fixedValue = WireJson.DecodeControl(WireJson.EncodeControl(value, maximum), maximum); WireJson.ValidateNamed(name, fixedValue); return fixedValue; }
    private static JsonElement Stored(string value, string name, int maximum)
    { Require(Bytes(value) <= maximum); var parsed = WireJson.DecodeControl(Encoding.UTF8.GetBytes(value), maximum); WireJson.ValidateNamed(name, parsed); return parsed; }
    private static JsonElement StringValue(string value)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) writer.WriteStringValue(value);
        return WireJson.Parse(stream.ToArray());
    }
    private static JsonElement Object(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return WireJson.Parse(stream.ToArray(), MaximumOperationBytes);
    }
    private static JsonElement ReadScope(Func<JsonElement> read, string application, string user)
    {
        JsonElement scope;
        try { scope = Snapshot(read(), "Scope", 65536); }
        catch { throw new StorageException("context_changed"); }
        Require(scope.GetProperty("applicationScopeId").GetString() == application && scope.GetProperty("endUserId").GetString() == user, "identity_mismatch");
        return scope;
    }
    private static string BuildMetadata(SqliteExecutorJournalOptions options, StorageFileIdentity parent, StorageFileIdentity file) => Canonical(Object(writer =>
    {
        writer.WriteString("format", options.CompactCompletedReceipts ? CompactFormat : Format);
        writer.WriteStartObject("identity"); writer.WriteStartObject("scope"); writer.WriteString("applicationScopeId", options.ApplicationScopeId); writer.WriteString("endUserId", options.EndUserId); writer.WriteEndObject(); writer.WriteString("executorId", options.ExecutorId); writer.WriteEndObject();
        writer.WriteStartObject("limits"); writer.WriteNumber("maxOperations", options.MaxOperations); writer.WriteNumber("maxStoredBytes", options.MaxStoredBytes); writer.WriteEndObject();
        writer.WriteNumber("maxPages", options.MaxPages); writer.WriteNumber("pageSize", 4096);
        writer.WriteStartObject("physical");
        writer.WriteStartObject("directory"); writer.WriteString("dev", parent.Device); writer.WriteString("ino", parent.Inode); writer.WriteEndObject();
        writer.WriteStartObject("file"); writer.WriteString("dev", file.Device); writer.WriteString("ino", file.Inode); writer.WriteEndObject();
        writer.WriteEndObject();
    }));
}
