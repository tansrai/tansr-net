using System.Globalization;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteExecutorJournal
{
    public const string IndexedFormat = "sdk2-execution-encrypted-indexed-net-sqlite-v2";
    public const string IndexedCompactFormat = "sdk2-execution-encrypted-indexed-compact-net-sqlite-v2";
    private static readonly string[] SourceSchema =
    {
        "CREATE TABLE source_authentication (id INTEGER PRIMARY KEY CHECK(id=1), body BLOB NOT NULL) STRICT",
        "CREATE TABLE source_authentication_nodes (hash TEXT PRIMARY KEY, body BLOB NOT NULL) STRICT",
        "CREATE TABLE source_operations (id TEXT PRIMARY KEY REFERENCES operations(id), operation_digest TEXT NOT NULL, receipt_digest TEXT, receipt_reserve INTEGER NOT NULL, reserve BLOB NOT NULL) STRICT",
    };
    // 任意随后新增原键都可能延长此 pending 键的路径。按最大 257 节点预留，而非只按 claim 当时深度。
    // 两页/节点加固定余量覆盖 SQLite 分裂与新头；只受原物理 maxPages，业务 quota 不扩大。
    private const int SourceCompletionReserve = (257 * 2 + 14) * 4096;
    private bool Indexed => _options.Layout == ExecutorJournalSqliteLayout.AuthenticatedIndexed;
    private string? _sourceRoot;
    private bool _sourceLoaded, _sourceTransaction;
    private long? _sourceDataVersion;
    private TerminalPersistenceAuthenticationTree SourceTree => new(ReadSourceNode, WriteSourceNode);

    private void CheckSourceVersion()
    {
        long version = Convert.ToInt64(Scalar("PRAGMA data_version"), CultureInfo.InvariantCulture);
        if (!_sourceDataVersion.HasValue) _sourceDataVersion = version;
        Require(version == _sourceDataVersion.Value, "identity_mismatch");
    }

    private byte[] ReadSourceNode(string hash)
    {
        var bytes = Scalar("SELECT body FROM source_authentication_nodes WHERE hash=$hash AND length(body)<=512", ("$hash", hash)) as byte[];
        Require(bytes != null && WireJson.Sha256(bytes!) == hash); return bytes!;
    }

    private void WriteSourceNode(string hash, byte[] bytes)
    {
        Require(_sourceTransaction && bytes.Length <= 512 && WireJson.Sha256(bytes) == hash);
        using var command = Command("SELECT CASE WHEN length(body)<=512 THEN body END FROM source_authentication_nodes WHERE hash=$hash", ("$hash", hash));
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read()) { Require(!reader.IsDBNull(0) && ((byte[])reader.GetValue(0)).SequenceEqual(bytes)); return; }
        }
        Exec("INSERT INTO source_authentication_nodes VALUES($hash,$body)", ("$hash", hash), ("$body", bytes));
    }

    private static string SourceKey(string id) => WireJson.Sha256(JsonSerializer.SerializeToUtf8Bytes(new[] { IndexedFormat, "operation", id }));
    private static string SourceDigest(params object?[] values) => WireJson.Sha256(JsonSerializer.SerializeToUtf8Bytes(values));
    private static string EncodedDigest(string text) => WireJson.Sha256(Encoding.UTF8.GetBytes(text));

    private void LoadSourceAuthentication()
    {
        var bytes = Scalar("SELECT body FROM source_authentication WHERE id=1 AND length(body)<=4096") as byte[];
        Require(bytes != null);
        byte[] plain = _cipher!.Open("execution-source-head-v2", bytes!);
        try
        {
            var head = WireJson.DecodeControl(plain, 4096);
            Require(head.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
                .SequenceEqual(new[] { "bytes", "count", "format", "root" }));
            var state = State();
            Require(head.GetProperty("format").GetString() == IndexedFormat && head.GetProperty("bytes").GetInt64() == state.Bytes && head.GetProperty("count").GetInt32() == state.Count);
            var root = head.GetProperty("root");
            if (root.ValueKind != JsonValueKind.Null) WireJson.ValidateNamed("Digest", root);
            _sourceRoot = root.ValueKind == JsonValueKind.Null ? null : root.GetString();
            Require((state.Count == 0) == (_sourceRoot == null)); _sourceLoaded = true;
        }
        finally { ArchiveSecurityJson.Clear(plain); }
    }

    private void SaveSourceAuthentication()
    {
        Require(_sourceLoaded && _sourceTransaction);
        var state = State();
        byte[] plain = WireJson.EncodeControl(Object(writer =>
        {
            writer.WriteString("format", IndexedFormat); writer.WriteString("root", _sourceRoot);
            writer.WriteNumber("bytes", state.Bytes); writer.WriteNumber("count", state.Count);
        }), 4096);
        byte[] encoded;
        try { encoded = _cipher!.Seal("execution-source-head-v2", plain); }
        finally { ArchiveSecurityJson.Clear(plain); }
        Exec("INSERT INTO source_authentication VALUES(1,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body", ("$body", encoded));
    }

    private void InitializeSource()
    {
        CheckFixed(); Require(_sourceLoaded);
        if (_sourceRoot != null) ReadSourceNode(_sourceRoot);
        _known = State();
    }

    private (string Operation, string? Receipt, int ReceiptReserve, int ProofReserve)? SourceDescriptor(string id)
    {
        Require(_sourceLoaded);
        using var command = Command("SELECT operation_digest,receipt_digest,receipt_reserve,length(reserve) FROM source_operations WHERE id=$id AND length(operation_digest)=64 AND (receipt_digest IS NULL OR length(receipt_digest)=64)", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            Require(SourceTree.Get(_sourceRoot, SourceKey(id)) == null);
            Require(Scalar("SELECT 1 FROM source_operations WHERE id=$id", ("$id", id)) == null);
            return null;
        }
        string operation = reader.GetString(0); string? receipt = reader.IsDBNull(1) ? null : reader.GetString(1);
        int reserve = reader.GetInt32(2), proof = reader.GetInt32(3);
        WireJson.ValidateNamed("Digest", StringValue(operation));
        if (receipt != null) WireJson.ValidateNamed("Digest", StringValue(receipt));
        Require(reserve >= 0 && reserve <= StoredReceiptReserve && (receipt == null ? proof == SourceCompletionReserve && reserve == StoredReceiptReserve : proof == 0));
        Require(SourceTree.Get(_sourceRoot, SourceKey(id)) == SourceDigest(id, operation, receipt, reserve, proof));
        return (operation, receipt, reserve, proof);
    }

    private void SourceCheckAbsent(string id)
    {
        if (Indexed) Require(SourceDescriptor(id) == null);
    }

    private void SourceCheckOperation(string id, string operation)
    {
        if (!Indexed) return;
        var descriptor = SourceDescriptor(id);
        Require(descriptor.HasValue && descriptor.Value.Operation == EncodedDigest(operation));
    }

    private void SourceCheckRow(string id, string operation, string? receipt, int reserve)
    {
        if (!Indexed) return;
        var descriptor = SourceDescriptor(id);
        Require(descriptor.HasValue && descriptor.Value.Operation == EncodedDigest(operation) && descriptor.Value.Receipt == (receipt == null ? null : EncodedDigest(receipt)) && descriptor.Value.ReceiptReserve == reserve);
    }

    private string StoredSourceOperation(string id)
    {
        if (!Indexed) return "";
        var text = Scalar("SELECT operation FROM operations WHERE id=$id AND length(CAST(operation AS BLOB))<=$maximum", ("$id", id), ("$maximum", StoredOperationMaximum)) as string;
        Require(text != null); return text!;
    }

    private void ReleaseSourceReserve(string id)
    {
        if (Indexed) Exec("UPDATE source_operations SET reserve=zeroblob(0) WHERE id=$id", ("$id", id));
    }

    private void SourceStoreRow(string id, string operation, string? receipt, int reserve, bool update = false)
    {
        if (!Indexed) return;
        Require(_sourceLoaded && _sourceTransaction);
        string opDigest = EncodedDigest(operation); string? receiptDigest = receipt == null ? null : EncodedDigest(receipt);
        int proof = receipt == null ? SourceCompletionReserve : 0;
        if (update)
        {
            Require((string?)Scalar("SELECT operation_digest FROM source_operations WHERE id=$id", ("$id", id)) == opDigest);
            Exec("UPDATE source_operations SET receipt_digest=$receipt,receipt_reserve=$reserve,reserve=zeroblob($proof) WHERE id=$id",
                ("$id", id), ("$receipt", (object?)receiptDigest ?? DBNull.Value), ("$reserve", reserve), ("$proof", proof));
        }
        else
            Exec("INSERT INTO source_operations VALUES($id,$operation,$receipt,$reserve,zeroblob($proof))",
                ("$id", id), ("$operation", opDigest), ("$receipt", (object?)receiptDigest ?? DBNull.Value), ("$reserve", reserve), ("$proof", proof));
        _sourceRoot = SourceTree.Put(_sourceRoot, SourceKey(id), SourceDigest(id, opDigest, receiptDigest, reserve, proof));
    }

    private void CheckSourceMembership(Action check)
    {
        if (!Indexed) return;
        // 此树按键摘要排序，不提供原 operationId 的 SQL 范围证明。显式恢复/授权证据分页必须
        // 核完整成员目录，不能只验证 SQL 返回行后把漏键的页称为完整。只读 ID/认证描述，不读全正文。
        int expected = State().Count, count = 0;
        using (var command = Command("SELECT CASE WHEN length(CAST(id AS BLOB))<=128 THEN id END FROM operations ORDER BY id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                Require(!reader.IsDBNull(0) && ++count <= expected);
                Require(SourceDescriptor(reader.GetString(0)).HasValue);
                if ((count & 127) == 0) check();
            }
        Require(count == expected); AuditSourceIndex(count, check); check();
    }

    private void AuditSourceIndex(int count, Action? check = null)
    {
        if (!Indexed) return;
        int leaves = 0;
        foreach (var _ in SourceTree.Entries(_sourceRoot))
        {
            Require(++leaves <= count);
            if ((leaves & 127) == 0) check?.Invoke();
        }
        Require(leaves == count && Convert.ToInt64(Scalar("SELECT count(*) FROM source_operations"), CultureInfo.InvariantCulture) == count);
    }

    /// <summary>显式全库物理、原件、回执、计数和认证索引审计。普通新来源冷开不执行此全历史遍历。</summary>
    public Task AuditAsync(CancellationToken cancellationToken = default)
    {
        Run((_scope, check) =>
        {
            Require((string?)Scalar("PRAGMA quick_check") == "ok");
            Initialize(); check(); return true;
        }, cancellationToken);
        return Task.CompletedTask;
    }
}
