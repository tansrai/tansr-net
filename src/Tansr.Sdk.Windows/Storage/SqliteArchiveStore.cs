using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;
using A = Tansr.Sdk.Windows.Storage.ArchiveValidation;
using S = Tansr.Sdk.Archive.Replication.ArchiveSyncValidation;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>原 SDK2 source/cache 同步文件族的本地档案介质。只有 source 耐久接收才生成 ACK；密钥、权限和删除修订不从旧库自证。</summary>
public sealed class SqliteArchiveStore : ISyncArchiveStore, IDisposable
{
    public const string Format = "sdk2-archive-sync-sqlite-v1";
    private const int Reserve = 4096;
    private static readonly string[] Schema =
    {
        "CREATE TABLE metadata (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL) STRICT",
        "CREATE TABLE records (sequence INTEGER PRIMARY KEY, record_id TEXT NOT NULL UNIQUE, json TEXT NOT NULL) STRICT",
        "CREATE TABLE artifacts (id TEXT PRIMARY KEY, json TEXT NOT NULL, body BLOB NOT NULL) STRICT",
        "CREATE TABLE operations (request TEXT PRIMARY KEY, ack TEXT NOT NULL, receipt TEXT, reserve BLOB NOT NULL) STRICT",
        "CREATE TABLE state (id INTEGER PRIMARY KEY CHECK(id=1), head TEXT, pending TEXT REFERENCES operations(request), logical_bytes INTEGER NOT NULL, record_count INTEGER NOT NULL, artifact_count INTEGER NOT NULL, operation_count INTEGER NOT NULL) STRICT",
        "CREATE TABLE checkpoints (request TEXT PRIMARY KEY REFERENCES operations(request), from_sequence INTEGER NOT NULL UNIQUE, through_sequence INTEGER NOT NULL, json TEXT NOT NULL) STRICT",
        "CREATE TABLE retention (revision INTEGER PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, json TEXT NOT NULL) STRICT",
        "CREATE TABLE tombstones (record_id TEXT PRIMARY KEY, sequence INTEGER NOT NULL UNIQUE, digest TEXT NOT NULL) STRICT",
        "CREATE TABLE record_artifacts (artifact_id TEXT NOT NULL, record_id TEXT NOT NULL, PRIMARY KEY(artifact_id,record_id)) STRICT",
        "CREATE INDEX record_artifacts_record ON record_artifacts(record_id)",
        "CREATE TABLE sync_state (id INTEGER PRIMARY KEY CHECK(id=1), revision TEXT NOT NULL) STRICT",
    };
    private const string EncryptionSchema = "CREATE TABLE encryption (id INTEGER PRIMARY KEY CHECK(id=1), key_check BLOB NOT NULL) STRICT";
    private readonly object _gate = new object();
    private readonly SqliteConnection _connection;
    private readonly StorageFileIdentity _parent, _file;
    private readonly JsonElement _identity;
    private readonly JsonElement _replica;
    private readonly string _syncRole;
    private readonly ArchiveStoreLimits _limits;
    private readonly Func<JsonElement> _context;
    private readonly Func<string> _retention;
    private readonly Action<JsonElement> _authorizeRetention;
    private readonly ArchiveBodyCipher? _cipher;
    private readonly string _metadata;
    private readonly bool _historical;
    private bool _closed, _busy, _poisoned, _uncertain;
    private StoreState? _known;

    private SqliteArchiveStore(SqliteConnection connection, StorageFileIdentity parent, StorageFileIdentity file,
        SqliteArchiveStoreOptions options, JsonElement identity, string metadata, ArchiveBodyCipher? cipher, bool historical)
    {
        _connection = connection; _parent = parent; _file = file; _identity = identity; _metadata = metadata; _cipher = cipher;
        _replica = options.Replica.Clone(); _syncRole = options.SyncRole;
        _limits = new ArchiveStoreLimits { MaxRecords = options.Limits.MaxRecords, MaxArtifacts = options.Limits.MaxArtifacts, MaxStoredBytes = options.Limits.MaxStoredBytes, MaxBatchBytes = options.Limits.MaxBatchBytes };
        _context = options.ReadContext; _retention = options.ReadRetentionRevision; _authorizeRetention = options.AuthorizeRetention;
        _historical = historical;
    }

    public static Task<SqliteArchiveStore> OpenAsync(SqliteArchiveStoreOptions options, CancellationToken cancellationToken = default)
        => OpenCore(options, false, cancellationToken);

    internal static Task<SqliteArchiveStore> OpenHistoryAsync(SqliteArchiveStoreOptions options, CancellationToken cancellationToken)
        => OpenCore(options, true, cancellationToken);

    internal ArchiveHistoryView CreateHistoryView(IArchiveHistoryAuthority current)
    {
        A.Need(_historical, "invalid_input"); return new ArchiveHistoryView(this, current, _identity, _context);
    }

    private static Task<SqliteArchiveStore> OpenCore(SqliteArchiveStoreOptions options, bool historical, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options == null || options.ReadContext == null || options.ReadRetentionRevision == null || options.AuthorizeRetention == null || options.Limits == null) throw new StorageException("invalid_input");
        var value = options; var limits = value.Limits;
        A.Need(limits.MaxRecords >= 1 && limits.MaxRecords <= 1000000 && limits.MaxArtifacts >= 1 && limits.MaxArtifacts <= 1000000 && limits.MaxStoredBytes >= 1 && limits.MaxStoredBytes <= 1073741824 && limits.MaxBatchBytes >= 1 && limits.MaxBatchBytes <= 67108864 && value.MaxPages >= 8 && value.MaxPages <= 262144 && (value.Mode == StorageOpenMode.Create || value.Mode == StorageOpenMode.Reopen), "invalid_input");
        string path = StorageFileIdentity.FullPath(value.Path); var identity = A.Identity(value.Identity); var replica = A.Copy(value.Replica);
        A.Fields(replica, "replicationId", "role"); WireJson.ValidateNamed("Id", replica.GetProperty("replicationId")); A.Need(new[] { "primary", "replica" }.Contains(A.String(replica, "role")), "invalid_input");
        A.Need(value.SyncRole == "source" || value.SyncRole == "cache", "invalid_input");
        // 固定调用方配置后才访问文件，外部对象之后的修改不能改变库身份或配额。
        var fixedOptions = new SqliteArchiveStoreOptions
        {
            Path = path,
            Mode = historical ? StorageOpenMode.Reopen : value.Mode,
            Identity = identity,
            Replica = replica,
            SyncRole = value.SyncRole,
            MaxPages = value.MaxPages,
            Limits = new ArchiveStoreLimits { MaxRecords = limits.MaxRecords, MaxArtifacts = limits.MaxArtifacts, MaxStoredBytes = limits.MaxStoredBytes, MaxBatchBytes = limits.MaxBatchBytes },
            ReadContext = value.ReadContext,
            ReadRetentionRevision = value.ReadRetentionRevision,
            AuthorizeRetention = value.AuthorizeRetention,
            KeyProvider = value.KeyProvider,
        };
        StorageFileIdentity? parent = null, file = null; SqliteConnection? connection = null;
        try
        {
            var scope = Scope(fixedOptions.ReadContext, identity); var cipher = fixedOptions.KeyProvider == null ? null : new ArchiveBodyCipher(fixedOptions.KeyProvider, identity);
            parent = StorageFileIdentity.Open(Path.GetDirectoryName(path)!, true);
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) if (File.Exists(path + suffix))
            { A.Need(fixedOptions.Mode != StorageOpenMode.Create, "identity_mismatch"); using var sidecar = StorageFileIdentity.Open(path + suffix, false, metadataOnly: historical); }
            file = StorageFileIdentity.Open(path, false, fixedOptions.Mode == StorageOpenMode.Create, metadataOnly: historical);
            string metadata = Metadata(fixedOptions, parent, file, cipher);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = historical ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 0 }.ToString()); connection.Open();
            var store = new SqliteArchiveStore(connection, parent, file, fixedOptions, identity, metadata, cipher, historical);
            // 历史只读连接不改 journal_mode、page_size、max_page_count 或原 metadata。
            store.Exec(historical ? "PRAGMA busy_timeout=0; PRAGMA query_only=ON; PRAGMA foreign_keys=ON" : "PRAGMA busy_timeout=0; PRAGMA locking_mode=EXCLUSIVE; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL");
            if (fixedOptions.Mode == StorageOpenMode.Create)
            {
                store.Exec("PRAGMA page_size=4096; PRAGMA journal_mode=WAL");
                store.Transaction(() =>
                {
                    foreach (string sql in Schema) store.Exec(sql);
                    store.Exec("INSERT INTO metadata VALUES(1,$json)", ("$json", metadata));
                    store.Exec("INSERT INTO sync_state VALUES(1,'0')");
                    if (cipher != null) { store.Exec(EncryptionSchema); store.Exec("INSERT INTO encryption VALUES(1,$check)", ("$check", cipher.CreateCheck())); }
                    store.Exec("INSERT INTO state VALUES(1,NULL,NULL,0,0,0,0)"); store.Reaccount();
                }, () => { A.Need(A.Equal(scope, Scope(fixedOptions.ReadContext, identity)), "context_changed"); parent.Check(); file.Check(); }, false);
            }
            else
            {
                A.Need(store.Number("PRAGMA page_size") == 4096 && (string?)store.Scalar("PRAGMA journal_mode") == "wal" && (string?)store.Scalar("PRAGMA quick_check") == "ok");
            }
            if (historical) A.Need(store.Number("PRAGMA page_count") <= fixedOptions.MaxPages, "capacity_exceeded");
            else A.Need(store.Number("PRAGMA max_page_count=" + fixedOptions.MaxPages.ToString(CultureInfo.InvariantCulture)) == fixedOptions.MaxPages, "capacity_exceeded");
            store.CheckFixed(); store.Audit(); A.Need(A.Equal(scope, Scope(fixedOptions.ReadContext, identity)), "context_changed");
            return Task.FromResult(store);
        }
        catch (Exception error)
        {
            connection?.Dispose(); file?.Dispose(); parent?.Dispose();
            if (error is SqliteException || error is IOException || error is UnauthorizedAccessException) throw new StorageException("storage_error");
            throw;
        }
    }

    public Task<JsonElement> ReceiveAsync(ArchiveReceiveInput input, CancellationToken cancellationToken = default)
    {
        ArchiveReceiveInput? fixedInput = null;
        return Task.FromResult(Run(check =>
        {
            A.Need(_syncRole == "source", "invalid_input");
            var state = State(); A.Need(state.Pending == null, "pending_ack");
            var batch = A.Prepare(_identity, _limits, state.Head == null ? null : A.Parse(state.Head), fixedInput!);
            CommitBatch(batch.Records, batch.References, batch.Bodies, batch.Head, batch.Ack, batch.Checkpoint, null, new HashSet<string>(StringComparer.Ordinal), check);
            return batch.Ack;
        }, cancellationToken, prepare: () => fixedInput = Snapshot(input)));
    }

    private void CommitBatch(JsonElement[] records, Dictionary<string, JsonElement> references, Dictionary<string, byte[]> bodies,
        JsonElement head, JsonElement ack, JsonElement checkpoint, JsonElement? receipt, ISet<string> deleted, Action check)
    {
        string request = A.Text(ack.GetProperty("request"));
        Transaction(() =>
        {
            var state = State(); A.Need(state.Pending == null, "pending_ack");
            long previous = state.Head == null ? 0 : A.SequenceOf(A.Parse(state.Head), "sequence");
            A.Need(previous != long.MaxValue && A.SequenceOf(records[0], "sequence") == previous + 1);
            A.Need(Number("SELECT count(*) FROM operations WHERE request=$request", ("$request", request)) == 0, "receipt_mismatch");
            foreach (var pair in references)
            {
                var existing = Artifact(pair.Value, false);
                byte[] supplied = bodies.TryGetValue(pair.Key, out var suppliedBody) ? suppliedBody : Array.Empty<byte>();
                if (existing != null) { if (supplied.Length != 0) A.Need(ReadBody(pair.Value).SequenceEqual(supplied)); continue; }
                byte[] body = _cipher == null || supplied.Length == 0 ? supplied : _cipher.Seal(pair.Value, supplied);
                Exec("INSERT INTO artifacts VALUES($id,$json,$body)", ("$id", pair.Key), ("$json", A.Text(pair.Value)), ("$body", body));
            }
            foreach (var record in records)
            {
                string id = A.String(record, "recordId");
                if (deleted.Contains(id)) RequireTombstone(record);
                else A.Need(Number("SELECT count(*) FROM tombstones WHERE record_id=$id OR sequence=$sequence", ("$id", id), ("$sequence", A.SequenceOf(record, "sequence"))) == 0, "deleted");
                Exec("INSERT INTO records VALUES($sequence,$id,$json)", ("$sequence", A.SequenceOf(record, "sequence")), ("$id", id), ("$json", A.Text(record)));
                foreach (var reference in A.References(record)) Exec("INSERT OR IGNORE INTO record_artifacts VALUES($artifact,$record)", ("$artifact", A.String(reference, "artifactId")), ("$record", id));
            }
            string? receiptText = receipt == null ? null : A.Text(receipt.Value); A.Need(receiptText == null || A.Bytes(receiptText) <= Reserve, "receipt_mismatch");
            Exec("INSERT INTO operations VALUES($request,$ack,$receipt,zeroblob($reserve))", ("$request", request), ("$ack", A.Text(ack)), ("$receipt", (object?)receiptText ?? DBNull.Value), ("$reserve", Reserve - (receiptText == null ? 0 : A.Bytes(receiptText))));
            Exec("INSERT INTO checkpoints VALUES($request,$from,$through,$json)", ("$request", request), ("$from", A.SequenceOf(ack.GetProperty("coverage"), "fromSequence")), ("$through", A.SequenceOf(ack.GetProperty("coverage"), "throughSequence")), ("$json", A.Text(checkpoint, 1572864)));
            Exec("UPDATE state SET head=$head,pending=$pending WHERE id=1", ("$head", A.Text(head)), ("$pending", receipt == null ? (object)request : DBNull.Value)); Reaccount();
        }, check);
    }

    public Task<JsonElement?> PendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run<JsonElement?>(_ =>
    { var state = State(); return state.Pending == null ? null : Operation(state.Pending).Ack; }, cancellationToken));
    public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run<JsonElement?>(_ =>
    { string? head = State().Head; return head == null ? null : A.Parse(head); }, cancellationToken));
    public Task<byte[]> BodyAsync(JsonElement artifactReference, CancellationToken cancellationToken = default) => Task.FromResult(Run(_ => ReadBody(ValidateReference(artifactReference)), cancellationToken));
    public Task<byte[]> BodyChunkAsync(JsonElement artifactReference, long offset, CancellationToken cancellationToken = default) => Task.FromResult(Run(_ =>
    {
        var reference = ValidateReference(artifactReference); Artifact(reference, true); long length = reference.GetProperty("bytes").GetInt64();
        A.Need(offset >= 0 && offset < length, "invalid_input"); int count = checked((int)Math.Min(262144, length - offset));
        long readOffset = offset; int readCount = count;
        if (_cipher != null) { var range = _cipher.Range(reference, offset, count); readOffset = range.Start; readCount = checked((int)range.Bytes); }
        var bytes = (byte[])Scalar("SELECT substr(body,$offset,$count) FROM artifacts WHERE id=$id", ("$offset", readOffset + 1), ("$count", readCount), ("$id", A.String(reference, "artifactId")))!;
        A.Need(bytes.Length == readCount); return _cipher == null ? bytes : _cipher.OpenRange(reference, bytes, offset, count);
    }, cancellationToken));

    public Task<JsonElement> ReplicaIdentityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run(_ => A.Object(writer =>
    {
        A.Property(writer, "replica", _replica); A.Property(writer, "receiver", _identity);
        A.Property(writer, "limits", LimitsJson());
    }), cancellationToken, true));

    public Task<JsonElement?> ReplicaOperationAsync(JsonElement requestIdentity, CancellationToken cancellationToken = default)
    {
        string? request = null;
        return Task.FromResult(Run<JsonElement?>(_ =>
        {
            if (Number("SELECT count(*) FROM operations WHERE request=$request", ("$request", request!)) == 0) return null;
            var operation = Operation(request!);
            return A.Object(writer =>
            {
                A.Property(writer, "ack", operation.Ack); writer.WritePropertyName("receipt");
                if (operation.Receipt == null) writer.WriteNullValue(); else A.Parse(operation.Receipt, "MutationReceipt", Reserve).WriteTo(writer);
            });
        }, cancellationToken, prepare: () => request = A.Text(A.Copy(requestIdentity, "RequestIdentity", 4096))));
    }

    public Task<JsonElement?> SyncPageAsync(string? afterSequence, CancellationToken cancellationToken = default) => Task.FromResult(Run<JsonElement?>(_ =>
    {
        long after = afterSequence == null ? 0 : Sequence.Parse(afterSequence, false).ToInt64();
        string? head = State().Head; long end = head == null ? 0 : A.SequenceOf(A.Parse(head), "sequence");
        if (after == end) return null;
        A.Need(after < end && after != long.MaxValue, "invalid_input");
        using var command = Command("SELECT request,json FROM checkpoints WHERE from_sequence=$from AND length(CAST(request AS BLOB))<=4096 AND length(CAST(json AS BLOB))<=1572864", ("$from", after + 1));
        using var reader = command.ExecuteReader(); A.Need(reader.Read(), "invalid_input");
        var operation = Operation(reader.GetString(0)); A.Need(operation.Receipt != null, "pending_ack");
        var checkpoint = A.Parse(reader.GetString(1), maximum: 1572864);
        var tombstones = checkpoint.GetProperty("page").GetProperty("records").EnumerateArray().Where(record => Deleted(A.String(record, "recordId"))).Select(record =>
        {
            RequireTombstone(record);
            return A.Object(writer => { writer.WriteString("recordId", A.String(record, "recordId")); writer.WriteString("sequence", A.String(record, "sequence")); writer.WriteString("recordDigest", A.String(record, "recordDigest")); });
        }).ToArray();
        return S.Page(A.Object(writer =>
        {
            writer.WriteString("format", "archive-sync-v1"); A.Property(writer, "identity", _identity); writer.WriteString("retentionRevision", LocalRevision());
            A.Property(writer, "checkpoint", checkpoint); A.Property(writer, "ack", operation.Ack); A.Property(writer, "receipt", A.Parse(operation.Receipt!, "MutationReceipt", Reserve));
            A.Array(writer, "tombstones", tombstones);
        }), _identity);
    }, cancellationToken));

    public Task<JsonElement> ReceiveSyncAsync(ArchiveSyncReceiveInput input, CancellationToken cancellationToken = default)
    {
        JsonElement page = default; ArchiveReceiveInput? fixedInput = null;
        return Task.FromResult(Run(check =>
        {
            A.Need(_syncRole == "cache", "invalid_input");
            var tombstones = page.GetProperty("tombstones").EnumerateArray().ToArray();
            var deleted = new HashSet<string>(tombstones.Select(row => A.String(row, "recordId")), StringComparer.Ordinal);
            foreach (var record in fixedInput!.Page.GetProperty("records").EnumerateArray())
                if (Deleted(A.String(record, "recordId"))) { A.Need(deleted.Contains(A.String(record, "recordId"))); RequireTombstone(record); }
            string revision = A.String(page, "retentionRevision");
            A.Need(revision == Sequence.Parse(_retention()).Value && revision == LocalRevision(), "context_changed");
            // 同步页不能授权删除：先由可信宿主 ApplyRetention 写入精确墓碑，再允许本页省略已删正文。
            foreach (var tombstone in tombstones) RequireTombstone(tombstone);
            var before = State(); A.Need(before.Pending == null, "pending_ack");
            var ack = page.GetProperty("ack"); var receipt = page.GetProperty("receipt"); string request = A.Text(ack.GetProperty("request"));
            if (Number("SELECT count(*) FROM operations WHERE request=$request", ("$request", request)) != 0)
            {
                var old = Operation(request);
                A.Need(A.Equal(old.Ack, ack) && old.Receipt == A.Text(receipt), "receipt_mismatch");
                A.Need((string?)Scalar("SELECT json FROM checkpoints WHERE request=$request AND length(CAST(json AS BLOB))<=1572864", ("$request", request)) == A.Text(page.GetProperty("checkpoint"), 1572864), "receipt_mismatch");
                return SyncReceipt(A.Object(writer => { writer.WriteString("sequence", A.String(ack.GetProperty("coverage"), "throughSequence")); writer.WriteString("recordDigest", A.String(ack.GetProperty("coverage"), "headDigest")); }), revision);
            }
            var batch = A.Prepare(_identity, _limits, before.Head == null ? null : A.Parse(before.Head), fixedInput, deletedRecordIds: deleted);
            A.Need(A.Equal(batch.Ack, ack), "receipt_mismatch"); var verifiedReceipt = A.Receipt(_identity, batch.Ack, receipt);
            CommitBatch(batch.Records, batch.References, batch.Bodies, batch.Head, batch.Ack, batch.Checkpoint, verifiedReceipt, deleted, check);
            return SyncReceipt(batch.Head, revision);
        }, cancellationToken, true, () =>
        {
            A.Need(input != null, "invalid_input"); page = S.Page(input!.Page, _identity); var checkpoint = page.GetProperty("checkpoint");
            fixedInput = Snapshot(new ArchiveReceiveInput { Binding = checkpoint.GetProperty("binding"), Status = checkpoint.GetProperty("status"), Page = checkpoint.GetProperty("page"), Request = checkpoint.GetProperty("request"), Artifacts = input.Artifacts });
        }));
    }

    private static JsonElement SyncReceipt(JsonElement head, string revision) => A.Object(writer =>
    { writer.WriteString("format", "archive-sync-receipt-v1"); A.Property(writer, "head", head); writer.WriteString("retentionRevision", revision); });

    private void RequireTombstone(JsonElement record)
    {
        A.Need(Number("SELECT count(*) FROM tombstones WHERE record_id=$id AND sequence=$sequence AND digest=$digest", ("$id", A.String(record, "recordId")), ("$sequence", A.SequenceOf(record, "sequence")), ("$digest", A.String(record, "recordDigest"))) == 1, "context_changed");
    }

    private ArchiveReceiveInput Snapshot(ArchiveReceiveInput input) => S.Input(input, LimitsJson());
    private JsonElement LimitsJson() => A.Object(writer =>
    {
        writer.WriteNumber("maxRecords", _limits.MaxRecords); writer.WriteNumber("maxArtifacts", _limits.MaxArtifacts);
        writer.WriteNumber("maxStoredBytes", _limits.MaxStoredBytes); writer.WriteNumber("maxBatchBytes", _limits.MaxBatchBytes);
    });

    public Task ConfirmAsync(JsonElement receipt, CancellationToken cancellationToken = default)
    {
        Run<object?>(check =>
        {
            A.Need(_syncRole == "source", "invalid_input");
            var fixedReceipt = A.Copy(receipt, "MutationReceipt", Reserve); string request = A.Text(fixedReceipt.GetProperty("request")); var operation = Operation(request);
            var verified = A.Receipt(_identity, operation.Ack, fixedReceipt); string text = A.Text(verified);
            if (operation.Receipt != null) { A.Need(operation.Receipt == text, "receipt_mismatch"); return null; }
            A.Need(State().Pending == request && operation.Reserved == Reserve, "receipt_mismatch");
            Transaction(() =>
            {
                Exec("UPDATE operations SET receipt=$receipt,reserve=zeroblob($reserve) WHERE request=$request", ("$receipt", text), ("$reserve", Reserve - A.Bytes(text)), ("$request", request));
                Exec("UPDATE state SET pending=NULL WHERE id=1"); Reaccount();
            }, check); return null;
        }, cancellationToken); return Task.CompletedTask;
    }

    public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Run(_ =>
    {
        A.Need(request != null && request.MaxRecords >= 1 && request.MaxRecords <= 128 && request.MaxBytes >= 1 && request.MaxBytes <= 1048576, "invalid_input");
        A.Need(A.Equal(A.Identity(request!.Identity), _identity), "identity_mismatch"); var selection = A.Copy(request.Selection, maximum: 65536);
        int maxRecords = request.MaxRecords, maxBytes = request.MaxBytes, bytes = 0; var records = new List<JsonElement>(); var missing = new List<string>(); string? next = null; bool complete = true;
        if (selection.TryGetProperty("recordIds", out var ids))
        {
            A.Fields(selection, "recordIds"); A.Need(ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() >= 1 && ids.GetArrayLength() <= maxRecords, "invalid_input");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in ids.EnumerateArray())
            {
                WireJson.ValidateNamed("Id", value); string id = value.GetString()!; A.Need(seen.Add(id), "invalid_input");
                A.Need(Number("SELECT count(*) FROM tombstones WHERE record_id=$id", ("$id", id)) == 0, "deleted");
                string? text = (string?)Scalar("SELECT json FROM records WHERE record_id=$id AND length(CAST(json AS BLOB))<=1048576", ("$id", id));
                if (text == null) { missing.Add(id); continue; }
                A.Need(bytes + A.Bytes(text) <= maxBytes, "capacity_exceeded"); var record = A.Parse(text, "ArchiveRecord"); A.VerifyRecord(record, _identity); records.Add(record); bytes += A.Bytes(text);
            }
            complete = missing.Count == 0;
        }
        else
        {
            A.Fields(selection, "fromSequence", "throughSequence"); long from = Sequence.Parse(A.String(selection, "fromSequence"), false).ToInt64(), through = Sequence.Parse(A.String(selection, "throughSequence"), false).ToInt64(); A.Need(through >= from, "invalid_input");
            string? head = State().Head; A.Need(head != null && through <= A.SequenceOf(A.Parse(head), "sequence"), "invalid_input");
            long expectedRows = Math.Min(through - from + 1, maxRecords);
            A.Need(Number("SELECT count(*) FROM (SELECT sequence FROM records WHERE sequence>=$from AND sequence<=$through ORDER BY sequence LIMIT $limit)", ("$from", from), ("$through", through), ("$limit", maxRecords)) == expectedRows);
            using var command = Command("SELECT sequence,json FROM records WHERE sequence>=$from AND sequence<=$through ORDER BY sequence LIMIT $limit", ("$from", from), ("$through", through), ("$limit", maxRecords)); using var reader = command.ExecuteReader(); long expected = from;
            while (reader.Read())
            {
                A.Need(reader.GetInt64(0) == expected); string text = reader.GetString(1);
                if (bytes + A.Bytes(text) > maxBytes) { A.Need(records.Count > 0, "capacity_exceeded"); break; }
                var record = A.Parse(text, "ArchiveRecord"); A.VerifyRecord(record, _identity); A.Need(Number("SELECT count(*) FROM tombstones WHERE record_id=$id", ("$id", A.String(record, "recordId"))) == 0, "deleted"); records.Add(record); bytes += A.Bytes(text); if (expected == long.MaxValue) break; expected++;
            }
            complete = records.Count > 0 && A.SequenceOf(records[records.Count - 1], "sequence") == through;
            if (!complete) next = expected.ToString(CultureInfo.InvariantCulture);
        }
        return new ArchiveRecordPage(records.AsReadOnly(), bytes, missing.AsReadOnly(), next, Coverage(records, complete));
    }, cancellationToken));

    public Task<JsonElement> CoverageAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run(_ =>
    {
        string? headText = State().Head; var head = headText == null ? (JsonElement?)null : A.Parse(headText);
        return A.Object(w => { w.WriteString("sourceId", A.String(_identity, "sourceId")); w.WriteString("sourceGeneration", A.String(_identity, "sourceGeneration")); w.WriteString("fromSequence", head == null ? null : "1"); w.WriteString("throughSequence", head == null ? null : A.String(head.Value, "sequence")); w.WriteString("headDigest", head == null ? null : A.String(head.Value, "recordDigest")); w.WriteBoolean("complete", true); });
    }, cancellationToken));

    public Task ApplyRetentionAsync(JsonElement retention, CancellationToken cancellationToken = default)
    {
        Run<object?>(check =>
        {
            var fixedValue = A.Retention(_identity, retention); string revision = A.String(fixedValue, "revision"), request = A.String(fixedValue, "requestId"); string text = A.Text(fixedValue);
            A.Need(Sequence.Parse(revision).ToInt64() <= Sequence.Parse(_retention()).ToInt64(), "context_changed");
            _authorizeRetention(fixedValue);
            string? existing = (string?)Scalar("SELECT json FROM retention WHERE revision=$revision OR request_id=$request", ("$revision", Sequence.Parse(revision).ToInt64()), ("$request", request));
            if (existing != null) { A.Need(existing == text, "receipt_mismatch"); return null; }
            A.Need(LocalRevision() == A.String(fixedValue, "previousRevision") && State().Pending == null, "context_changed");
            Transaction(() =>
            {
                foreach (var tombstone in fixedValue.GetProperty("records").EnumerateArray())
                {
                    string id = A.String(tombstone, "recordId"); long sequence = A.SequenceOf(tombstone, "sequence"); string digest = A.String(tombstone, "recordDigest");
                    string? recordText = (string?)Scalar("SELECT json FROM records WHERE record_id=$id OR sequence=$sequence", ("$id", id), ("$sequence", sequence));
                    if (recordText != null) { var record = A.Parse(recordText, "ArchiveRecord"); A.Need(A.String(record, "recordId") == id && A.SequenceOf(record, "sequence") == sequence && A.String(record, "recordDigest") == digest); }
                    using (var command = Command("SELECT record_id,sequence,digest FROM tombstones WHERE record_id=$id OR sequence=$sequence", ("$id", id), ("$sequence", sequence)))
                    using (var reader = command.ExecuteReader()) if (reader.Read()) A.Need(reader.GetString(0) == id && reader.GetInt64(1) == sequence && reader.GetString(2) == digest);
                    Exec("INSERT OR IGNORE INTO tombstones VALUES($id,$sequence,$digest)", ("$id", id), ("$sequence", sequence), ("$digest", digest));
                }
                Exec("UPDATE artifacts SET body=zeroblob(0) WHERE EXISTS(SELECT 1 FROM record_artifacts ra WHERE ra.artifact_id=artifacts.id) AND NOT EXISTS(SELECT 1 FROM record_artifacts ra LEFT JOIN tombstones t ON t.record_id=ra.record_id WHERE ra.artifact_id=artifacts.id AND t.record_id IS NULL)");
                Exec("INSERT INTO retention VALUES($revision,$request,$json)", ("$revision", Sequence.Parse(revision).ToInt64()), ("$request", request), ("$json", text));
                Exec("UPDATE sync_state SET revision=$revision WHERE id=1", ("$revision", revision)); Reaccount();
            }, () => { _authorizeRetention(fixedValue); check(); }); return null;
        }, cancellationToken, true); return Task.CompletedTask;
    }

    public Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run(_ => LocalRevision(), cancellationToken, true));
    public Task<JsonElement?> RetentionPageAsync(string afterRevision, CancellationToken cancellationToken = default) => Task.FromResult(Run<JsonElement?>(_ =>
    {
        long after = Sequence.Parse(afterRevision).ToInt64(); A.Need(after <= Sequence.Parse(LocalRevision()).ToInt64(), "invalid_input");
        if (after == long.MaxValue) return null; string? text = (string?)Scalar("SELECT json FROM retention WHERE revision=$revision", ("$revision", after + 1)); return text == null ? null : A.Retention(_identity, A.Parse(text));
    }, cancellationToken, true));
    public Task CloseAsync() { Dispose(); return Task.CompletedTask; }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
            if (_closed) return; _connection.Dispose(); _file.Dispose(); _parent.Dispose(); _closed = true;
        }
    }

    private T Run<T>(Func<Action, T> work, CancellationToken cancellationToken, bool allowStale = false, Action? prepare = null)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
            A.Need(!_closed, "closed"); A.Need(!_uncertain, "reconciliation_required"); _busy = true; _poisoned = false;
            try
            {
                prepare?.Invoke();
                var scope = Scope(_context, _identity); string trusted = Sequence.Parse(_retention()).Value;
                void Check()
                {
                    A.Need(!_poisoned, "reentrant"); cancellationToken.ThrowIfCancellationRequested(); CheckFixed(); A.Need(!_poisoned, "reentrant");
                    // VerifyCheck 会调用宿主供钥函数，之后必须重新核授权；不能在最后一次回调后交出旧主体正文或提交。
                    A.Need(A.Equal(scope, Scope(_context, _identity)) && trusted == Sequence.Parse(_retention()).Value, "context_changed");
                    A.Need(!_poisoned, "reentrant"); string local = LocalRevision(); A.Need(allowStale || _historical ? Sequence.Parse(local).ToInt64() <= Sequence.Parse(trusted).ToInt64() : local == trusted, "context_changed");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                Check(); if (_known != null) A.Need(_known.Equals(State())); var result = work(Check); Check(); return result;
            }
            catch (SqliteException) { throw new StorageException("storage_error"); }
            finally { _busy = false; }
        }
    }

    private void Transaction(Action work, Action check, bool refresh = true)
    {
        A.Need(!_historical, "invalid_input");
        bool begun = false, committing = false;
        try { Exec("BEGIN IMMEDIATE"); begun = true; work(); check(); committing = true; Exec("COMMIT"); begun = false; if (refresh) _known = State(); committing = false; }
        catch
        {
            bool unknown = committing; if (begun) { try { Exec("ROLLBACK"); } catch { unknown = true; } }
            if (unknown) { _uncertain = true; throw new StorageException("reconciliation_required"); }
            throw;
        }
    }

    private void CheckFixed()
    {
        _parent.Check(); _file.Check(); var expected = _cipher == null ? Schema : Schema.Concat(new[] { EncryptionSchema }).ToArray();
        A.Need(Number("SELECT count(*) FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_'") == expected.Length && Number("SELECT count(*) FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' AND (length(CAST(sql AS BLOB))>4096 OR length(CAST(name AS BLOB))>128)") == 0);
        using (var command = Command("SELECT sql FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' ORDER BY name LIMIT 14"))
        using (var reader = command.ExecuteReader()) { var found = new HashSet<string>(StringComparer.Ordinal); while (reader.Read()) { A.Need(!reader.IsDBNull(0) && expected.Contains(reader.GetString(0))); found.Add(reader.GetString(0)); } A.Need(found.Count == expected.Length); }
        A.Need((string?)Scalar("SELECT json FROM metadata WHERE id=1 AND length(CAST(json AS BLOB))<=1048576") == _metadata, "identity_mismatch");
        if (_cipher != null) { var check = Scalar("SELECT key_check FROM encryption WHERE id=1 AND length(key_check)<=4096") as byte[]; A.Need(check != null); _cipher.VerifyCheck(check!); }
    }

    private void Audit()
    {
        foreach (string table in new[] { "metadata", "state", "sync_state" }) A.Need(Number("SELECT count(*) FROM " + table) == 1);
        A.Need(Number("SELECT count(*) FROM records WHERE length(CAST(json AS BLOB))>1048576 OR length(CAST(record_id AS BLOB))>128") == 0);
        A.Need(Number("SELECT count(*) FROM artifacts WHERE length(CAST(json AS BLOB))>1048576 OR length(body)>67117056 OR length(CAST(id AS BLOB))>128") == 0);
        A.Need(Number("SELECT count(*) FROM operations WHERE length(CAST(request AS BLOB))>4096 OR length(CAST(ack AS BLOB))>1048576 OR length(CAST(receipt AS BLOB))>4096 OR length(reserve)>4096") == 0);
        A.Need(Number("SELECT count(*) FROM checkpoints WHERE length(CAST(json AS BLOB))>1572864") == 0);
        A.Need(Number("SELECT count(*) FROM retention WHERE length(CAST(json AS BLOB))>131072") == 0);
        var state = State(); A.Need(state.Bytes == Account() && state.Records == Number("SELECT count(*) FROM records") && state.Artifacts == Number("SELECT count(*) FROM artifacts") && state.Operations == Number("SELECT count(*) FROM operations"));
        var references = new Dictionary<string, JsonElement>(StringComparer.Ordinal); long sequence = 0; string digest = A.ZeroDigest;
        using (var command = Command("SELECT sequence,record_id,json FROM records ORDER BY sequence")) using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var record = A.Parse(reader.GetString(2), "ArchiveRecord"); A.VerifyRecord(record, _identity);
            A.Need(reader.GetInt64(0) == ++sequence && A.SequenceOf(record, "sequence") == sequence && reader.GetString(1) == A.String(record, "recordId") && A.String(record, "predecessorDigest") == digest); digest = A.String(record, "recordDigest");
            foreach (var reference in A.References(record))
            {
                string id = A.String(reference, "artifactId"); if (references.TryGetValue(id, out var prior)) A.Need(A.Equal(prior, reference)); else references.Add(id, reference);
                A.Need(Number("SELECT count(*) FROM record_artifacts WHERE artifact_id=$artifact AND record_id=$record", ("$artifact", id), ("$record", A.String(record, "recordId"))) == 1);
            }
            A.Need(Number("SELECT count(*) FROM record_artifacts WHERE record_id=$record", ("$record", A.String(record, "recordId"))) == A.References(record).Select(r => A.String(r, "artifactId")).Distinct(StringComparer.Ordinal).Count());
            if (!Deleted(A.String(record, "recordId"))) A.Need(WireJson.DomainDigest("tansr.sdk2.payload.v1", ReadBody(record.GetProperty("payload"))) == A.String(record, "payloadDigest"));
        }
        A.Need(sequence == state.Records && (sequence == 0 ? state.Head == null : state.Head != null && A.SequenceOf(A.Parse(state.Head), "sequence") == sequence && A.String(A.Parse(state.Head), "recordDigest") == digest));
        A.Need(references.Count == state.Artifacts);
        foreach (var reference in references.Values)
        {
            var artifact = Artifact(reference, false); A.Need(artifact != null);
            if (Allowed(A.String(reference, "artifactId"))) ReadBody(reference); else A.Need(artifact!.Value.Length == 0);
        }
        A.Need(Number("SELECT count(*) FROM record_artifacts ra LEFT JOIN records r ON ra.record_id=r.record_id LEFT JOIN artifacts a ON ra.artifact_id=a.id WHERE r.record_id IS NULL OR a.id IS NULL") == 0);
        long through = 0; int pending = 0;
        using (var command = Command("SELECT request,from_sequence,through_sequence,json FROM checkpoints ORDER BY from_sequence")) using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            string key = reader.GetString(0); var operation = Operation(key); var coverage = operation.Ack.GetProperty("coverage");
            A.Need(reader.GetInt64(1) == through + 1 && A.SequenceOf(coverage, "fromSequence") == reader.GetInt64(1) && A.SequenceOf(coverage, "throughSequence") == reader.GetInt64(2)); through = reader.GetInt64(2);
            var checkpoint = A.Parse(reader.GetString(3), maximum: 1572864); A.Fields(checkpoint, "binding", "status", "page", "request"); A.Need(A.Text(checkpoint.GetProperty("request")) == key);
            WireJson.ValidateNamed("BindingView", checkpoint.GetProperty("binding")); WireJson.ValidateNamed("ArchiveStatus", checkpoint.GetProperty("status")); WireJson.ValidateNamed("ArchivePage", checkpoint.GetProperty("page"));
            var records = checkpoint.GetProperty("page").GetProperty("records").EnumerateArray().ToArray(); A.Need(records.Length > 0 && A.SequenceOf(records[0], "sequence") == reader.GetInt64(1) && A.SequenceOf(records[records.Length - 1], "sequence") == through);
            foreach (var record in records) A.Need((string?)Scalar("SELECT json FROM records WHERE sequence=$sequence", ("$sequence", A.SequenceOf(record, "sequence"))) == A.Text(record));
            A.Need(records.Length <= 128 && records.Length == through - reader.GetInt64(1) + 1 && A.String(coverage, "headDigest") == A.String(records[records.Length - 1], "recordDigest"));
            var payloads = new List<JsonElement>(); var attachments = new List<JsonElement>(); var payloadIds = new HashSet<string>(StringComparer.Ordinal); var attachmentIds = new HashSet<string>(StringComparer.Ordinal);
            void Add(JsonElement reference, HashSet<string> ids, List<JsonElement> results)
            {
                if (ids.Add(A.String(reference, "artifactId"))) results.Add(A.Object(w => { w.WriteString("artifactId", A.String(reference, "artifactId")); w.WriteString("sha256", A.String(reference, "sha256")); w.WriteString("state", "durably-stored"); }));
            }
            foreach (var record in records) { Add(record.GetProperty("payload"), payloadIds, payloads); foreach (var reference in record.GetProperty("attachments").EnumerateArray()) Add(reference, attachmentIds, attachments); }
            A.Need(A.Equal(operation.Ack.GetProperty("payloads"), JsonSerializer.SerializeToElement(payloads)) && A.Equal(operation.Ack.GetProperty("attachments"), JsonSerializer.SerializeToElement(attachments)));
            if (operation.Receipt == null) { pending++; A.Need(state.Pending == key && through == sequence); }
        }
        A.Need(through == sequence && Number("SELECT count(*) FROM checkpoints") == state.Operations && pending == (state.Pending == null ? 0 : 1));
        long revision = 0; var tombstones = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using (var command = Command("SELECT revision,request_id,json FROM retention ORDER BY revision")) using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var retention = A.Retention(_identity, A.Parse(reader.GetString(2))); A.Need(reader.GetInt64(0) == ++revision && A.SequenceOf(retention, "revision") == revision && reader.GetString(1) == A.String(retention, "requestId"));
            foreach (var tombstone in retention.GetProperty("records").EnumerateArray())
            {
                string id = A.String(tombstone, "recordId");
                if (tombstones.TryGetValue(id, out var prior)) A.Need(A.Equal(prior, tombstone)); else tombstones.Add(id, tombstone);
                A.Need(Number("SELECT count(*) FROM tombstones WHERE record_id=$id AND sequence=$sequence AND digest=$digest", ("$id", id), ("$sequence", A.SequenceOf(tombstone, "sequence")), ("$digest", A.String(tombstone, "recordDigest"))) == 1);
            }
        }
        A.Need(Sequence.Parse(LocalRevision()).ToInt64() == revision);
        A.Need(Number("SELECT count(*) FROM tombstones") == tombstones.Count);
        A.Need(Number("SELECT count(*) FROM tombstones t JOIN records r ON t.record_id=r.record_id OR t.sequence=r.sequence WHERE t.record_id<>r.record_id OR t.sequence<>r.sequence OR json_extract(r.json,'$.recordDigest')<>t.digest") == 0);
        _known = state;
    }

    private JsonElement ValidateReference(JsonElement value)
    { var reference = A.Copy(value, "ArtifactRef"); A.Need(A.String(reference, "sourceId") == A.String(_identity, "sourceId"), "identity_mismatch"); return reference; }
    private (JsonElement Reference, long Length)? Artifact(JsonElement reference, bool required)
    {
        using var command = Command("SELECT json,length(body) FROM artifacts WHERE id=$id AND length(CAST(json AS BLOB))<=1048576", ("$id", A.String(reference, "artifactId"))); using var reader = command.ExecuteReader();
        if (!reader.Read()) { A.Need(!required, "missing_artifact"); return null; }
        var stored = A.Parse(reader.GetString(0), "ArtifactRef"); A.Need(A.Equal(stored, reference));
        if (required) A.Need(Allowed(A.String(reference, "artifactId")), "deleted");
        long expected = reference.GetProperty("bytes").GetInt64(); if (_cipher != null) expected = ArchiveBodyCipher.EncryptedBytes(expected);
        long length = reader.GetInt64(1); A.Need(length == expected || !required && length == 0 && !Allowed(A.String(reference, "artifactId"))); return (stored, length);
    }
    private byte[] ReadBody(JsonElement reference)
    {
        var artifact = Artifact(reference, true); A.Need(artifact!.Value.Length <= _limits.MaxBatchBytes + 8192, "capacity_exceeded");
        byte[] raw = (byte[])Scalar("SELECT body FROM artifacts WHERE id=$id", ("$id", A.String(reference, "artifactId")))!; byte[] body = _cipher == null ? raw : _cipher.Open(reference, raw);
        A.Need(body.LongLength == reference.GetProperty("bytes").GetInt64() && WireJson.Sha256(body) == A.String(reference, "sha256")); return body;
    }
    private bool Allowed(string id) => Number("SELECT count(*) FROM record_artifacts ra LEFT JOIN tombstones t ON t.record_id=ra.record_id WHERE ra.artifact_id=$id AND t.record_id IS NULL", ("$id", id)) > 0;
    private bool Deleted(string id) => Number("SELECT count(*) FROM tombstones WHERE record_id=$id", ("$id", id)) > 0;
    private (JsonElement Ack, string? Receipt, int Reserved) Operation(string request)
    {
        using var command = Command("SELECT ack,receipt,length(reserve) FROM operations WHERE request=$request AND length(CAST(ack AS BLOB))<=1048576 AND (receipt IS NULL OR length(CAST(receipt AS BLOB))<=4096)", ("$request", request)); using var reader = command.ExecuteReader(); A.Need(reader.Read(), "receipt_mismatch");
        var ack = A.Parse(reader.GetString(0), "ArchiveAckRequest"); A.Need(A.Text(ack.GetProperty("request")) == request && A.String(ack, "bindingId") == A.String(_identity, "bindingId") && A.String(ack, "sourceId") == A.String(_identity, "sourceId") && A.String(ack, "sourceGeneration") == A.String(_identity, "sourceGeneration") && A.Equal(ack.GetProperty("generations"), _identity.GetProperty("target").GetProperty("generations")));
        string? receipt = reader.IsDBNull(1) ? null : reader.GetString(1); int reserve = reader.GetInt32(2); A.Need(reserve >= 0 && reserve + (receipt == null ? 0 : A.Bytes(receipt)) == Reserve);
        if (receipt != null) A.Receipt(_identity, ack, A.Parse(receipt)); return (ack, receipt, reserve);
    }
    private JsonElement Coverage(IReadOnlyList<JsonElement> records, bool complete)
    {
        var ordered = records.OrderBy(r => A.SequenceOf(r, "sequence")).ToArray(); var head = State().Head;
        return A.Object(w => { w.WriteString("sourceId", A.String(_identity, "sourceId")); w.WriteString("sourceGeneration", A.String(_identity, "sourceGeneration")); w.WriteString("fromSequence", ordered.Length == 0 ? null : A.String(ordered[0], "sequence")); w.WriteString("throughSequence", ordered.Length == 0 ? null : A.String(ordered[ordered.Length - 1], "sequence")); w.WriteString("headDigest", head == null ? null : A.String(A.Parse(head), "recordDigest")); w.WriteBoolean("complete", complete); });
    }
    private string LocalRevision() { string? value = (string?)Scalar("SELECT revision FROM sync_state WHERE id=1 AND length(revision)<=19"); A.Need(value != null); return Sequence.Parse(value!).Value; }
    private void Reaccount()
    {
        long bytes = Account(), records = Number("SELECT count(*) FROM records"), artifacts = Number("SELECT count(*) FROM artifacts"), operations = Number("SELECT count(*) FROM operations");
        A.Need(bytes <= _limits.MaxStoredBytes && records <= _limits.MaxRecords && artifacts <= _limits.MaxArtifacts && operations <= _limits.MaxRecords, "capacity_exceeded");
        Exec("UPDATE state SET logical_bytes=$bytes,record_count=$records,artifact_count=$artifacts,operation_count=$operations WHERE id=1", ("$bytes", bytes), ("$records", records), ("$artifacts", artifacts), ("$operations", operations));
    }
    private long Account()
    {
        foreach (string table in new[] { "checkpoints", "retention", "tombstones" }) A.Need(Number("SELECT count(*) FROM " + table) <= _limits.MaxRecords, "capacity_exceeded");
        A.Need(Number("SELECT count(*) FROM record_artifacts") <= (long)_limits.MaxRecords * 129 && Number("SELECT count(*) FROM sync_state") == 1, "capacity_exceeded");
        if (_cipher != null) A.Need(Number("SELECT count(*) FROM encryption") == 1 && Number("SELECT COALESCE(SUM(length(key_check)),0) FROM encryption") <= 128);
        long Sum(string table, string expression) => Number("SELECT COALESCE(SUM(" + expression + "),0) FROM " + table);
        return Sum("metadata", "length(CAST(json AS BLOB))") + 128 + Sum("state", "COALESCE(length(CAST(head AS BLOB)),0)+COALESCE(length(CAST(pending AS BLOB)),0)") +
            Sum("records", "8+length(CAST(record_id AS BLOB))+length(CAST(json AS BLOB))") + Sum("artifacts", "length(CAST(id AS BLOB))+length(CAST(json AS BLOB))+length(body)") +
            Sum("operations", "length(CAST(request AS BLOB))+length(CAST(ack AS BLOB))+COALESCE(length(CAST(receipt AS BLOB)),0)+length(reserve)") +
            Sum("checkpoints", "16+length(CAST(request AS BLOB))+length(CAST(json AS BLOB))") + Sum("retention", "8+length(CAST(request_id AS BLOB))+length(CAST(json AS BLOB))") +
            Sum("tombstones", "8+length(CAST(record_id AS BLOB))+length(CAST(digest AS BLOB))") + Sum("record_artifacts", "length(CAST(artifact_id AS BLOB))+length(CAST(record_id AS BLOB))") +
            Sum("sync_state", "length(CAST(revision AS BLOB))") + (_cipher == null ? 0 : Sum("encryption", "length(key_check)"));
    }
    private StoreState State()
    {
        using var command = Command("SELECT head,pending,logical_bytes,record_count,artifact_count,operation_count FROM state WHERE id=1 AND (head IS NULL OR length(CAST(head AS BLOB))<=4096) AND (pending IS NULL OR length(CAST(pending AS BLOB))<=4096)"); using var reader = command.ExecuteReader(); A.Need(reader.Read());
        var state = new StoreState(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        A.Need(state.Bytes >= 0 && state.Bytes <= _limits.MaxStoredBytes && state.Records >= 0 && state.Records <= _limits.MaxRecords && state.Artifacts >= 0 && state.Artifacts <= _limits.MaxArtifacts && state.Operations >= 0 && state.Operations <= _limits.MaxRecords); return state;
    }
    private SqliteCommand Command(string sql, params (string Name, object Value)[] args) { var command = _connection.CreateCommand(); command.CommandText = sql; foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value); return command; }
    private void Exec(string sql, params (string Name, object Value)[] args) { using var command = Command(sql, args); command.ExecuteNonQuery(); }
    private object? Scalar(string sql, params (string Name, object Value)[] args) { using var command = Command(sql, args); return command.ExecuteScalar(); }
    private long Number(string sql, params (string Name, object Value)[] args) => Convert.ToInt64(Scalar(sql, args), CultureInfo.InvariantCulture);
    private static JsonElement Scope(Func<JsonElement> read, JsonElement identity)
    {
        JsonElement scope; try { scope = A.Copy(read(), "Scope", 65536); } catch { throw new StorageException("context_changed"); }
        A.Need(A.Equal(A.Without(scope, "authorizationRevision"), identity.GetProperty("scope")), "identity_mismatch"); return scope;
    }
    private static string Metadata(SqliteArchiveStoreOptions options, StorageFileIdentity parent, StorageFileIdentity file, ArchiveBodyCipher? cipher) => A.Text(A.Object(w =>
    {
        w.WriteString("format", cipher == null ? Format : ArchiveBodyCipher.Format); w.WriteStartObject("identity"); foreach (var property in options.Identity.EnumerateObject()) property.WriteTo(w);
        A.Property(w, "replica", options.Replica); w.WriteString("syncRole", options.SyncRole); if (cipher != null) w.WriteString("keyId", cipher.KeyId); w.WriteEndObject();
        w.WriteStartObject("limits"); w.WriteNumber("maxRecords", options.Limits.MaxRecords); w.WriteNumber("maxArtifacts", options.Limits.MaxArtifacts); w.WriteNumber("maxStoredBytes", options.Limits.MaxStoredBytes); w.WriteNumber("maxBatchBytes", options.Limits.MaxBatchBytes); w.WriteEndObject();
        w.WriteNumber("maxPages", options.MaxPages); w.WriteNumber("pageSize", 4096); w.WriteStartObject("physical"); w.WriteStartObject("directory"); w.WriteString("dev", parent.Device); w.WriteString("ino", parent.Inode); w.WriteEndObject(); w.WriteStartObject("file"); w.WriteString("dev", file.Device); w.WriteString("ino", file.Inode); w.WriteEndObject(); w.WriteEndObject();
    }));
    private sealed class StoreState : IEquatable<StoreState>
    {
        internal string? Head { get; }
        internal string? Pending { get; }
        internal long Bytes { get; }
        internal long Records { get; }
        internal long Artifacts { get; }
        internal long Operations { get; }
        internal StoreState(string? head, string? pending, long bytes, long records, long artifacts, long operations) { Head = head; Pending = pending; Bytes = bytes; Records = records; Artifacts = artifacts; Operations = operations; }
        public bool Equals(StoreState? value) => value != null && Head == value.Head && Pending == value.Pending && Bytes == value.Bytes && Records == value.Records && Artifacts == value.Artifacts && Operations == value.Operations;
    }
}
