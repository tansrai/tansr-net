using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;
using A = Tansr.Sdk.Windows.Storage.ArchiveValidation;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteArchiveStore
{
    private static readonly string[] SourceSchema =
    {
        "CREATE TABLE source_authentication (id INTEGER PRIMARY KEY CHECK(id=1), body BLOB NOT NULL, reserve BLOB NOT NULL) STRICT",
        "CREATE TABLE source_authentication_nodes (hash TEXT PRIMARY KEY, body BLOB NOT NULL) STRICT",
        "CREATE TABLE source_artifacts (id TEXT PRIMARY KEY REFERENCES artifacts(id), active_records INTEGER NOT NULL CHECK(active_records>=0)) STRICT",
    };
    private readonly bool _indexed;
    private readonly MemoryPublicationBodyCipher? _sourceCipher;
    private string? _sourceRoot;
    private bool _sourceLoaded, _sourceTransaction, _sourceMaintenance;
    private long? _sourceDataVersion;
    private int _sourceNodeReads;
    private TerminalPersistenceAuthenticationTree SourceTree => new(ReadSourceNode, WriteSourceNode);

    private void CheckSourceVersion()
    {
        long version = Number("PRAGMA data_version");
        if (!_sourceDataVersion.HasValue) _sourceDataVersion = version;
        A.Need(_sourceDataVersion.Value == version, "identity_mismatch");
    }

    private byte[] ReadSourceNode(string hash)
    {
        _sourceNodeReads++;
        var bytes = Scalar("SELECT body FROM source_authentication_nodes WHERE hash=$hash AND length(body)<=512", ("$hash", hash)) as byte[];
        A.Need(bytes != null && WireJson.Sha256(bytes!) == hash); return bytes!;
    }

    private void WriteSourceNode(string hash, byte[] bytes)
    {
        A.Need(_sourceTransaction && bytes.Length <= 512 && WireJson.Sha256(bytes) == hash);
        var existing = Scalar("SELECT body FROM source_authentication_nodes WHERE hash=$hash AND length(body)<=512", ("$hash", hash)) as byte[];
        if (existing != null) { A.Need(existing.SequenceEqual(bytes)); return; }
        Exec("INSERT INTO source_authentication_nodes VALUES($hash,$body)", ("$hash", hash), ("$body", bytes));
    }

    private static string SourceKey(string domain, string key) => WireJson.Sha256(JsonSerializer.SerializeToUtf8Bytes(new[] { IndexedFormat, domain, key }));
    private static string SourceDigest(params object?[] values) => WireJson.Sha256(JsonSerializer.SerializeToUtf8Bytes(values));

    private void SourceCheck(string domain, string key, string? digest)
    {
        if (!_indexed) return;
        A.Need(_sourceLoaded && SourceTree.Get(_sourceRoot, SourceKey(domain, key)) == digest);
    }

    private void SourcePut(string domain, string key, string digest)
    {
        if (!_indexed) return;
        A.Need(_sourceLoaded && _sourceTransaction);
        _sourceRoot = SourceTree.Put(_sourceRoot, SourceKey(domain, key), digest);
    }

    private string SourceStateDigest()
    {
        var state = State();
        return SourceDigest(state.Head, state.Pending, state.Bytes, state.Records, state.Artifacts, state.Operations, LocalRevision());
    }

    private void LoadSourceAuthentication()
    {
        using var command = Command("SELECT body,length(reserve) FROM source_authentication WHERE id=1 AND length(body)<=4096");
        using var reader = command.ExecuteReader(); A.Need(reader.Read());
        byte[] plain = _sourceCipher!.Open("archive-source-head-v2", (byte[])reader.GetValue(0));
        try
        {
            var head = WireJson.Parse(plain); A.Fields(head, "format", "root", "stateDigest", "reserveBytes");
            A.Need(A.String(head, "format") == IndexedFormat && A.String(head, "stateDigest") == SourceStateDigest());
            string? root = head.GetProperty("root").ValueKind == JsonValueKind.Null ? null : A.String(head, "root");
            if (root != null) WireJson.ValidateNamed("Digest", head.GetProperty("root"));
            long reserve = head.GetProperty("reserveBytes").GetInt64();
            A.Need(reserve >= 0 && reserve <= 2162688 && reader.GetInt64(1) == reserve);
            A.Need((State().Pending == null) == (reserve == 0));
            _sourceRoot = root; _sourceLoaded = true;
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    private void SaveSourceAuthentication()
    {
        A.Need(_sourceLoaded && _sourceTransaction);
        var state = State(); int reserve = 0;
        if (state.Pending != null)
        {
            // 只有一个当前 ACK。为确认原键时更新认证路径预留物理页，旧 ACK 的 4 KiB 回执预算保持。
            _sourceNodeReads = 0;
            A.Need(SourceTree.Get(_sourceRoot, SourceKey("operation", state.Pending)) != null);
            reserve = checked((_sourceNodeReads * 2 + 14) * 4096);
        }
        byte[] plain = WireJson.EncodeControl(A.Object(writer =>
        {
            writer.WriteString("format", IndexedFormat); writer.WriteString("root", _sourceRoot);
            writer.WriteString("stateDigest", SourceStateDigest()); writer.WriteNumber("reserveBytes", reserve);
        }));
        byte[] encoded;
        try { encoded = _sourceCipher!.Seal("archive-source-head-v2", plain); }
        finally { Array.Clear(plain, 0, plain.Length); }
        Exec("INSERT INTO source_authentication VALUES(1,$body,zeroblob($reserve)) ON CONFLICT(id) DO UPDATE SET body=excluded.body,reserve=excluded.reserve", ("$body", encoded), ("$reserve", reserve));
    }

    private void CheckSourceState()
    {
        var state = State();
        A.Need(Sequence.Parse(LocalRevision()).ToInt64() <= Sequence.Parse(_retention()).ToInt64(), "context_changed");
        if (state.Head == null) A.Need(state.Records == 0 && state.Artifacts == 0 && state.Operations == 0 && state.Pending == null);
        else
        {
            var head = A.Parse(state.Head); A.Fields(head, "sequence", "recordDigest");
            WireJson.ValidateNamed("RecordSequence", head.GetProperty("sequence")); WireJson.ValidateNamed("Digest", head.GetProperty("recordDigest"));
            A.Need(A.SequenceOf(head, "sequence") == state.Records && state.Operations > 0 && state.Operations <= state.Records);
        }
        if (_sourceRoot != null) ReadSourceNode(_sourceRoot);
        if (state.Pending != null)
        {
            var operation = Operation(state.Pending); A.Need(operation.Receipt == null);
            A.Need(state.Head != null && A.SequenceOf(operation.Ack.GetProperty("coverage"), "throughSequence") == state.Records &&
                A.String(operation.Ack.GetProperty("coverage"), "headDigest") == A.String(A.Parse(state.Head!), "recordDigest"));
        }
        _known = state;
    }

    private void SourceCost(long bytes, long records = 0, long artifacts = 0, long operations = 0)
    {
        if (!_indexed) return;
        var before = State();
        Exec("UPDATE state SET logical_bytes=$bytes,record_count=$records,artifact_count=$artifacts,operation_count=$operations WHERE id=1",
            ("$bytes", checked(before.Bytes + bytes)), ("$records", checked(before.Records + records)),
            ("$artifacts", checked(before.Artifacts + artifacts)), ("$operations", checked(before.Operations + operations)));
        State();
    }

    private void SourceRecord(JsonElement record, bool write = false)
    {
        string id = A.String(record, "recordId"), sequence = A.String(record, "sequence");
        string digest = SourceDigest(sequence, id, A.Text(record));
        if (write) { SourcePut("record-id", id, digest); SourcePut("record-sequence", sequence, digest); }
        else { SourceCheck("record-id", id, digest); SourceCheck("record-sequence", sequence, digest); }
    }

    private bool SourceOperationExists(string request)
    {
        bool exists = Number("SELECT count(*) FROM operations WHERE request=$request", ("$request", request)) != 0;
        if (!exists) SourceCheck("operation", request, null);
        return exists;
    }

    private void SourceStoreOperation(string request)
    {
        if (!_indexed) return;
        using var command = Command("SELECT ack,receipt,length(reserve) FROM operations WHERE request=$request", ("$request", request));
        using var reader = command.ExecuteReader(); A.Need(reader.Read());
        SourcePut("operation", request, SourceDigest(request, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2)));
    }

    private string SourceCheckpoint(string request, bool write = false)
    {
        using var command = Command("SELECT from_sequence,through_sequence,json FROM checkpoints WHERE request=$request AND length(CAST(json AS BLOB))<=1572864", ("$request", request));
        using var reader = command.ExecuteReader(); A.Need(reader.Read());
        string from = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture), text = reader.GetString(2);
        string digest = SourceDigest(request, reader.GetInt64(0), reader.GetInt64(1), text);
        if (write) { SourcePut("checkpoint-request", request, digest); SourcePut("checkpoint-from", from, digest); }
        else { SourceCheck("checkpoint-request", request, digest); SourceCheck("checkpoint-from", from, digest); }
        return text;
    }

    private long SourceArtifactCount(string id)
    {
        object? value = Scalar("SELECT active_records FROM source_artifacts WHERE id=$id", ("$id", id)); A.Need(value != null);
        long count = Convert.ToInt64(value, CultureInfo.InvariantCulture); A.Need(count >= 0 && count <= _limits.MaxRecords);
        SourceCheck("artifact-links", id, SourceDigest(id, count)); return count;
    }

    private void SourceAddArtifactReference(string id)
    {
        if (!_indexed) return;
        long count = SourceArtifactCount(id);
        Exec("UPDATE source_artifacts SET active_records=$count WHERE id=$id", ("$count", count + 1), ("$id", id));
        SourcePut("artifact-links", id, SourceDigest(id, count + 1));
    }

    private void SourceNewArtifact(string id, string json, long length)
    {
        if (!_indexed) return;
        SourcePut("artifact", id, SourceDigest(id, json, length));
        Exec("INSERT INTO source_artifacts VALUES($id,0)", ("$id", id));
        SourcePut("artifact-links", id, SourceDigest(id, 0L));
    }

    private bool SourceDeleted(string id)
    {
        using var command = Command("SELECT sequence,digest FROM tombstones WHERE record_id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { SourceCheck("tombstone-id", id, null); return false; }
        string sequence = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
        string digest = SourceDigest(id, reader.GetInt64(0), reader.GetString(1));
        SourceCheck("tombstone-id", id, digest); SourceCheck("tombstone-sequence", sequence, digest); return true;
    }

    private string? SourceRetention(long revision)
    {
        using var command = Command("SELECT request_id,json FROM retention WHERE revision=$revision AND length(CAST(json AS BLOB))<=131072", ("$revision", revision));
        using var reader = command.ExecuteReader(); string key = revision.ToString(CultureInfo.InvariantCulture);
        if (!reader.Read()) { SourceCheck("retention-revision", key, null); return null; }
        string request = reader.GetString(0), text = reader.GetString(1); string digest = SourceDigest(revision, request, text);
        SourceCheck("retention-revision", key, digest); SourceCheck("retention-request", request, digest); return text;
    }

    /// <summary>显式完整介质审计；普通认证索引 source 开库只读取恢复点及当前原键。</summary>
    public Task AuditAsync(CancellationToken cancellationToken = default)
    {
        Run<object?>(check =>
        {
            A.Need((string?)Scalar("PRAGMA quick_check") == "ok"); check(); Audit(check);
            if (_indexed) SourceIndex(false, check);
            check(); return null;
        }, cancellationToken, true); return Task.CompletedTask;
    }

    private void SourceIndex(bool write, Action check)
    {
        if (!_indexed) return;
        if (write) _sourceRoot = null;
        long leaves = 0;
        void Item(string domain, string key, string digest)
        { check(); if (write) SourcePut(domain, key, digest); else SourceCheck(domain, key, digest); leaves++; }
        using (var command = Command("SELECT sequence,record_id,json FROM records ORDER BY sequence")) using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            string sequence = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture), id = reader.GetString(1);
            string digest = SourceDigest(sequence, id, reader.GetString(2)); Item("record-id", id, digest); Item("record-sequence", sequence, digest);
        }
        using (var command = Command("SELECT id,json,length(body) FROM artifacts ORDER BY id")) using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            string id = reader.GetString(0); Item("artifact", id, SourceDigest(id, reader.GetString(1), reader.GetInt64(2)));
            long count = Number("SELECT count(*) FROM record_artifacts ra LEFT JOIN tombstones t ON t.record_id=ra.record_id WHERE ra.artifact_id=$id AND t.record_id IS NULL", ("$id", id));
            if (write) Exec("UPDATE source_artifacts SET active_records=$count WHERE id=$id", ("$count", count), ("$id", id));
            A.Need(Number("SELECT active_records FROM source_artifacts WHERE id=$id", ("$id", id)) == count);
            Item("artifact-links", id, SourceDigest(id, count));
        }
        A.Need(Number("SELECT count(*) FROM source_artifacts") == State().Artifacts);
        using (var command = Command("SELECT request,ack,receipt,length(reserve) FROM operations ORDER BY request")) using (var reader = command.ExecuteReader()) while (reader.Read())
            Item("operation", reader.GetString(0), SourceDigest(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3)));
        using (var command = Command("SELECT request,from_sequence,through_sequence,json FROM checkpoints ORDER BY request")) using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            string digest = SourceDigest(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
            Item("checkpoint-request", reader.GetString(0), digest); Item("checkpoint-from", reader.GetInt64(1).ToString(CultureInfo.InvariantCulture), digest);
        }
        using (var command = Command("SELECT revision,request_id,json FROM retention ORDER BY revision")) using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            string digest = SourceDigest(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
            Item("retention-revision", reader.GetInt64(0).ToString(CultureInfo.InvariantCulture), digest); Item("retention-request", reader.GetString(1), digest);
        }
        using (var command = Command("SELECT record_id,sequence,digest FROM tombstones ORDER BY record_id")) using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            string digest = SourceDigest(reader.GetString(0), reader.GetInt64(1), reader.GetString(2));
            Item("tombstone-id", reader.GetString(0), digest); Item("tombstone-sequence", reader.GetInt64(1).ToString(CultureInfo.InvariantCulture), digest);
        }
        long found = 0; foreach (var _ in SourceTree.Entries(_sourceRoot)) { check(); found++; }
        A.Need(found == leaves);
    }
}
