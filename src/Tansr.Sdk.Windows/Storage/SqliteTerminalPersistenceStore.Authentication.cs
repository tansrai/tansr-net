using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    private static readonly string[] AuthenticationSchema =
    {
        "CREATE TABLE authentication (id INTEGER PRIMARY KEY CHECK(id=1), body BLOB NOT NULL) STRICT",
        "CREATE TABLE authentication_nodes (hash TEXT PRIMARY KEY, body BLOB NOT NULL) STRICT",
    };
    private bool Indexed => _options.Layout == TerminalPersistenceSqliteLayout.AuthenticatedIndexed;
    private string? _authenticationRoot;
    private readonly HashSet<string> _authenticationTransfers = new(StringComparer.Ordinal);
    private bool _authenticationLoaded, _authenticationTransaction;
    private TerminalPersistenceAuthenticationTree AuthenticationTree => new(ReadAuthenticationNode, WriteAuthenticationNode);

    private byte[] ReadAuthenticationNode(string hash)
    {
        var raw = Scalar("SELECT body FROM authentication_nodes WHERE hash=$hash AND length(body)<=$limit", ("$hash", hash), ("$limit", TerminalPersistenceAuthenticationTree.MaximumNodeBytes + 28)) as byte[];
        Require(raw != null); var plain = _cipher.Open("authentication-node\0" + hash, raw!);
        Require(WireJson.Sha256(plain) == hash); return plain;
    }
    private void WriteAuthenticationNode(string hash, byte[] bytes)
    {
        Require(_authenticationTransaction && bytes.Length <= TerminalPersistenceAuthenticationTree.MaximumNodeBytes);
        var old = Scalar("SELECT body FROM authentication_nodes WHERE hash=$hash AND length(body)<=$limit", ("$hash", hash), ("$limit", TerminalPersistenceAuthenticationTree.MaximumNodeBytes + 28)) as byte[];
        if (old != null) { Require(_cipher.Open("authentication-node\0" + hash, old).SequenceEqual(bytes)); return; }
        Exec("INSERT INTO authentication_nodes VALUES($hash,$body)", ("$hash", hash), ("$body", _cipher.Seal("authentication-node\0" + hash, bytes)));
    }
    private static string AuthenticationKey(string domain, params string[] keys)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        { writer.WriteStartArray(); writer.WriteStringValue("terminal-persistence-index-v2"); writer.WriteStringValue(domain); foreach (string key in keys) writer.WriteStringValue(key); writer.WriteEndArray(); }
        return WireJson.Sha256(WireJson.EncodeControl(WireJson.Parse(memory.ToArray(), MaximumMetadataBytes), MaximumMetadataBytes));
    }
    private void Authenticate(string domain, string[] keys, string? rowDigest)
    {
        if (!Indexed) return;
        Require(_authenticationLoaded);
        Require(AuthenticationTree.Get(_authenticationRoot, AuthenticationKey(domain, keys)) == rowDigest);
    }
    private void AuthenticateWrite(string domain, string[] keys, string rowDigest)
    {
        if (!Indexed) return;
        Require(_authenticationTransaction && _authenticationLoaded);
        _authenticationRoot = AuthenticationTree.Put(_authenticationRoot, AuthenticationKey(domain, keys), rowDigest);
    }
    private static string ObjectRowDigest(string kind, string hash, int bytes, byte[] encoded) => Hash(Json(w =>
    { w.WriteString("kind", kind); w.WriteString("hash", hash); w.WriteNumber("bytes", bytes); w.WriteString("body", WireJson.Sha256(encoded)); }));
    private static string TicketRowDigest(string id, string status, byte[] encoded) => Hash(Json(w =>
    { w.WriteString("id", id); w.WriteString("status", status); w.WriteString("body", WireJson.Sha256(encoded)); }));
    private static string EntryRowDigest(string primary, string secondary, long ordinal, byte[] encoded) => Hash(Json(w =>
    { w.WriteString("primary", primary); w.WriteString("secondary", secondary); w.WriteString("ordinal", ordinal.ToString(CultureInfo.InvariantCulture)); w.WriteString("body", WireJson.Sha256(encoded)); }));
    private string CurrentStateDigest()
    {
        var raw = Scalar("SELECT body FROM state WHERE id=1 AND length(body)<=262172") as byte[]; Require(raw != null);
        return WireJson.Sha256(raw!);
    }
    private string? CurrentCustodyDigest()
    {
        if (!_options.EnableCustodyHandoff || Number("SELECT count(*) FROM sqlite_master WHERE name='custody_state'") == 0) return null;
        var raw = Scalar("SELECT body FROM custody_state WHERE id=1 AND length(body)<=32796") as byte[]; Require(raw != null);
        return WireJson.Sha256(raw!);
    }
    private void CreateAuthentication()
    { _authenticationRoot = null; _authenticationTransfers.Clear(); _authenticationLoaded = true; SaveAuthentication(); }
    private void SaveAuthentication()
    {
        Require(_authenticationTransaction && _authenticationLoaded && _authenticationTransfers.Count <= _options.MaxActiveTransfers);
        var state = LoadState(); Require(state.Used["activeTransfers"] == _authenticationTransfers.Count);
        var value = Json(w =>
        {
            w.WriteString("format", "terminal-persistence-authentication-v2");
            if (_authenticationRoot == null) w.WriteNull("root"); else w.WriteString("root", _authenticationRoot);
            w.WriteString("stateDigest", CurrentStateDigest());
            string? custody = CurrentCustodyDigest(); if (custody == null) w.WriteNull("custodyDigest"); else w.WriteString("custodyDigest", custody);
            w.WriteStartArray("activeTransfers"); foreach (string id in _authenticationTransfers.OrderBy(x => x, StringComparer.Ordinal)) w.WriteStringValue(id); w.WriteEndArray();
        });
        Exec("INSERT INTO authentication VALUES(1,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body", ("$body", _cipher.Seal("authentication", Bytes(value))));
    }
    private void LoadAuthentication()
    {
        var raw = Scalar("SELECT body FROM authentication WHERE id=1 AND length(body)<=32796") as byte[]; Require(raw != null);
        var value = Decode("authentication", raw!); Fields(value, "format", "root", "stateDigest", "custodyDigest", "activeTransfers");
        Require(S(value, "format") == "terminal-persistence-authentication-v2" && S(value, "stateDigest") == CurrentStateDigest());
        Require(value.GetProperty("custodyDigest").GetString() == CurrentCustodyDigest());
        var root = value.GetProperty("root"); Require(root.ValueKind == JsonValueKind.Null || root.ValueKind == JsonValueKind.String);
        if (root.ValueKind == JsonValueKind.String) Tansr.Sdk.Terminal.TerminalPersistenceContract.Validate("Digest", root);
        var active = value.GetProperty("activeTransfers"); Require(active.ValueKind == JsonValueKind.Array && active.GetArrayLength() <= _options.MaxActiveTransfers);
        var next = new HashSet<string>(StringComparer.Ordinal); string? previous = null;
        foreach (var item in active.EnumerateArray())
        {
            Tansr.Sdk.Terminal.TerminalPersistenceContract.Validate("Id", item); string id = item.GetString()!;
            Require(next.Add(id) && (previous == null || StringComparer.Ordinal.Compare(previous, id) < 0)); previous = id;
        }
        _authenticationRoot = root.GetString(); _authenticationTransfers.Clear(); _authenticationTransfers.UnionWith(next); _authenticationLoaded = true;
        Require(LoadState().Used["activeTransfers"] == next.Count);
    }
    private void CheckSourceState()
    {
        LoadState();
        // 当前未决责任是必要工作集；终态历史和未选中正文留在点读/显式审计。
        if (_authenticationRoot != null) ReadAuthenticationNode(_authenticationRoot);
        foreach (string id in _authenticationTransfers) Require(LoadTicket(id)?.Status == "staging");
    }
    /// <summary>显式全库审计，包括SQLite结构、全部永久原件、计数与根链；不是普通源端初始化。</summary>
    public Task AuditAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run(check =>
    {
        Require((string?)Scalar("PRAGMA quick_check") == "ok"); Audit(check);
        if (Indexed)
        {
            long expected = Number("SELECT count(*) FROM objects") + 2 * Number("SELECT count(*) FROM entries") + Number("SELECT count(*) FROM transfers");
            if (_options.EnableCustodyHandoff)
            {
                using var command = Command("SELECT id FROM custody_operations ORDER BY id"); using var reader = command.ExecuteReader();
                while (reader.Read()) { check(); Require(ReadCustodyOperation(reader.GetString(0)) != null); expected++; }
            }
            long count = 0; foreach (var _ in AuthenticationTree.Entries(_authenticationRoot)) { check(); count++; }
            Require(count == expected);
        }
        check(); return true;
    }, cancellationToken));
}
