using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    private sealed class State
    {
        internal JsonElement? Root;
        internal bool ReadOnlyCopy;
        internal readonly Dictionary<string, long> Used = CounterNames.ToDictionary(x => x, _ => 0L, StringComparer.Ordinal);
    }
    private sealed class Ticket
    {
        internal string Id = "", Owner = "", Status = "staging";
        internal JsonElement Request;
        internal JsonElement? Base, Result, Rejection;
        internal long RawRemaining, MetadataRemaining = MetadataReserve, ObjectsRemaining, EntriesRemaining, AccountedBytes;
        internal readonly List<ObjectRef> Accepted = new();
    }
    private sealed class ObjectRef
    {
        internal ObjectRef(string kind, string hash, int bytes, bool received) { Kind = kind; Hash = hash; Bytes = bytes; Received = received; }
        internal string Kind, Hash;
        internal int Bytes;
        internal bool Received;
        internal string Key => Kind + ":" + Hash;
    }
    private sealed class EntryRow
    {
        internal EntryRow(long ordinal, JsonElement value) { Ordinal = ordinal; Value = value; }
        internal long Ordinal;
        internal JsonElement Value;
    }
    private sealed class OpenOwner
    { internal bool Opening = true; internal bool Poisoned, Maintaining; internal string Signature = ""; internal Action<Action>? Cleanup; }
    private static readonly string[] CounterNames = { "activeTransfers", "stagingBytes", "receiptEntries", "transferFacts", "objects", "retainedBytes", "reservedBytes", "reservedObjects", "reservedReceiptEntries" };
    private static JsonElement Json(Action<Utf8JsonWriter> write) => TerminalPersistenceContract.Json(write);
    private static void Prop(Utf8JsonWriter writer, string name, JsonElement value) { writer.WritePropertyName(name); value.WriteTo(writer); }
    private static void Nullable(Utf8JsonWriter writer, string name, JsonElement? value) { writer.WritePropertyName(name); if (value.HasValue) value.Value.WriteTo(writer); else writer.WriteNullValue(); }
    private static string S(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static int N(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static string Canon(JsonElement value) => WireJson.CanonicalString(value, MaximumMetadataBytes);
    private static byte[] Bytes(JsonElement value) => WireJson.EncodeControl(value, MaximumMetadataBytes);
    private static bool Equal(JsonElement left, JsonElement right) => Canon(left) == Canon(right);
    private static string Hash(JsonElement value) => WireJson.Sha256(Bytes(value));
    private static JsonElement? Optional(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).Clone();
    private static void Need(bool condition, string code = "integrity_mismatch") { if (!condition) throw new MemoryPublicationRejectedException(code); }
    private static void Require(bool condition, string code = "integrity_mismatch") { if (!condition) throw new StorageException(code); }
    private static void Fields(JsonElement value, params string[] fields)
        => Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(fields.OrderBy(x => x, StringComparer.Ordinal)));
    private static JsonElement Scope(Func<JsonElement> read, JsonElement identity)
    {
        try
        {
            var value = WireJson.DecodeControl(WireJson.EncodeControl(read(), 32768), 32768); WireJson.ValidateNamed("Scope", value);
            Require(S(value, "applicationScopeId") == S(identity, "applicationScopeId") && S(value, "endUserId") == S(identity, "endUserId"), "context_changed"); return value;
        }
        catch { throw new StorageException("context_changed"); }
    }
    private static JsonElement Owner(string canonical)
    {
        Require(Encoding.UTF8.GetByteCount(canonical) <= 8192, "invalid_input");
        var value = WireJson.DecodeControl(Encoding.UTF8.GetBytes(canonical), 8192); Fields(value, "scope", "sessionId", "binding");
        WireJson.ValidateNamed("Scope", value.GetProperty("scope")); WireJson.ValidateNamed("LegacyId", value.GetProperty("sessionId")); WireJson.ValidateNamed("ExecutionBinding", value.GetProperty("binding"));
        Require(Canon(value) == canonical); return value;
    }
    private static void Release(string path, OpenOwner owner) { lock (OwnersGate) if (Owners.TryGetValue(path, out var current) && ReferenceEquals(current, owner)) Owners.Remove(path); }
    private SqliteCommand Command(string sql, params (string Name, object Value)[] args) { var command = _connection.CreateCommand(); command.CommandText = sql; foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value); return command; }
    private object? Scalar(string sql, params (string Name, object Value)[] args) { using var command = Command(sql, args); return command.ExecuteScalar(); }
    private long Number(string sql, params (string Name, object Value)[] args) => Convert.ToInt64(Scalar(sql, args), CultureInfo.InvariantCulture);
    private void Exec(string sql, params (string Name, object Value)[] args) { using var command = Command(sql, args); command.ExecuteNonQuery(); }
    private static JsonElement Limits(SqliteTerminalPersistenceOptions options) => Json(w =>
    {
        w.WriteNumber("activeTransfers", options.MaxActiveTransfers); w.WriteNumber("stagingBytes", options.MaxStagingBytes);
        w.WriteNumber("receiptEntries", options.MaxReceiptEntries); w.WriteNumber("transferFacts", options.MaxTransferFacts);
        w.WriteNumber("objects", options.MaxObjects); w.WriteNumber("retainedBytes", options.MaxRetainedBytes);
    });
    private static string Signature(SqliteTerminalPersistenceOptions options, string keyId) => Canon(Json(w =>
    { w.WriteString("format", Format); w.WriteString("keyId", keyId); Prop(w, "identity", options.Identity); Prop(w, "limits", Limits(options)); w.WriteNumber("maxPages", options.MaxPages); }));
    private static string Metadata(SqliteTerminalPersistenceOptions options, StorageFileIdentity parent, StorageFileIdentity file, string keyId) => Canon(Json(w =>
    {
        w.WriteString("format", Format); w.WriteString("keyId", keyId); Prop(w, "identity", options.Identity); Prop(w, "limits", Limits(options));
        w.WriteNumber("maxPages", options.MaxPages); w.WriteNumber("pageSize", 4096);
        w.WriteStartObject("physical"); w.WriteStartObject("directory"); w.WriteString("dev", parent.Device); w.WriteString("ino", parent.Inode); w.WriteEndObject();
        w.WriteStartObject("file"); w.WriteString("dev", file.Device); w.WriteString("ino", file.Inode); w.WriteEndObject(); w.WriteEndObject();
    }));
    private void CheckFixed()
    {
        _parent.Check(); _file.Check();
        long version = Number("PRAGMA data_version"); _dataVersion ??= version; Require(_dataVersion == version, "identity_mismatch");
        Require(Number("SELECT count(*) FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_'") == Schema.Length);
        using (var command = Command("SELECT sql FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' AND length(sql)<=4096 LIMIT 6")) using (var reader = command.ExecuteReader())
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) { Require(!reader.IsDBNull(0) && Schema.Contains(reader.GetString(0))); found.Add(reader.GetString(0)); }
            Require(found.Count == Schema.Length);
        }
        Require(Number("SELECT count(*) FROM metadata") == 1 && (string?)Scalar("SELECT json FROM metadata WHERE id=1 AND length(CAST(json AS BLOB))<=262144") == _metadata);
        Require(Number("SELECT count(*) FROM encryption") == 1);
        var check = Scalar("SELECT key_check FROM encryption WHERE id=1 AND length(key_check)=28") as byte[]; Require(check != null); _cipher.Open("key-check", check!);
    }
    private JsonElement Decode(string context, byte[] body)
    {
        var plain = _cipher.Open(context, body); var value = WireJson.DecodeControl(plain, MaximumMetadataBytes); Require(Bytes(value).SequenceEqual(plain)); return value;
    }
    private static JsonElement Used(State state) => Json(w => { foreach (var name in CounterNames) w.WriteNumber(name, state.Used[name]); });
    private State LoadState()
    {
        Require(Number("SELECT count(*) FROM state") == 1);
        var encoded = Scalar("SELECT body FROM state WHERE id=1 AND length(body)<=262172") as byte[]; Require(encoded != null);
        var value = Decode("state", encoded!);
        bool hasCopyMarker = value.TryGetProperty("readOnlyCopy", out var copyMarker);
        if (hasCopyMarker) Fields(value, "root", "used", "readOnlyCopy"); else Fields(value, "root", "used");
        Require(!hasCopyMarker || copyMarker.ValueKind == JsonValueKind.True || copyMarker.ValueKind == JsonValueKind.False);
        var result = new State { Root = Optional(value, "root"), ReadOnlyCopy = hasCopyMarker && copyMarker.GetBoolean() }; var used = value.GetProperty("used"); Fields(used, CounterNames);
        foreach (var name in CounterNames) result.Used[name] = used.GetProperty(name).GetInt64();
        CheckCapacity(result); if (result.Root.HasValue) ValidateRoot(result.Root.Value); return result;
    }
    private void SaveState(State state)
    {
        CheckCapacity(state);
        var value = Json(w => { Nullable(w, "root", state.Root); Prop(w, "used", Used(state)); w.WriteBoolean("readOnlyCopy", state.ReadOnlyCopy); });
        Exec("INSERT INTO state VALUES(1,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body", ("$body", _cipher.Seal("state", Bytes(value))));
    }
    private void CheckCapacity(State state)
    {
        foreach (long value in state.Used.Values) Require(value >= 0);
        var limits = Limits(_options);
        foreach (string field in new[] { "activeTransfers", "stagingBytes", "transferFacts" }) Require(state.Used[field] <= limits.GetProperty(field).GetInt64());
        Require(state.Used["receiptEntries"] + state.Used["reservedReceiptEntries"] <= _options.MaxReceiptEntries);
        Require(state.Used["objects"] + state.Used["reservedObjects"] <= _options.MaxObjects);
        Require(state.Used["retainedBytes"] + state.Used["reservedBytes"] <= _options.MaxRetainedBytes);
    }
    private static string ObjectContext(string kind, string hash, int length) => "object\0" + kind + "\0" + hash + "\0" + length.ToString(CultureInfo.InvariantCulture);
    private byte[] ReadObject(ObjectRef reference)
    {
        using var command = Command("SELECT bytes,body FROM objects WHERE kind=$kind AND hash=$hash AND length(body)<=12316", ("$kind", reference.Kind), ("$hash", reference.Hash));
        using var reader = command.ExecuteReader(); Require(reader.Read() && reader.GetInt32(0) == reference.Bytes);
        byte[] plain = _cipher.Open(ObjectContext(reference.Kind, reference.Hash, reference.Bytes), (byte[])reader.GetValue(1));
        Require(plain.Length == reference.Bytes && WireJson.Sha256(plain) == reference.Hash); return plain;
    }
    private bool AddObject(ObjectRef reference, byte[] plain)
    {
        if (Number("SELECT count(*) FROM objects WHERE kind=$kind AND hash=$hash", ("$kind", reference.Kind), ("$hash", reference.Hash)) != 0)
        { Require(ReadObject(reference).SequenceEqual(plain)); return false; }
        Exec("INSERT INTO objects VALUES($kind,$hash,$bytes,$body)", ("$kind", reference.Kind), ("$hash", reference.Hash), ("$bytes", reference.Bytes), ("$body", _cipher.Seal(ObjectContext(reference.Kind, reference.Hash, reference.Bytes), plain))); return true;
    }
    private static JsonElement RefJson(ObjectRef value) => Json(w =>
    { w.WriteString("kind", value.Kind); w.WriteString("sha256", value.Hash); w.WriteNumber("byteLength", value.Bytes); w.WriteBoolean("received", value.Received); });
    private static JsonElement TicketJson(Ticket row) => Json(w =>
    {
        w.WriteString("owner", row.Owner); Prop(w, "request", row.Request); w.WriteString("status", row.Status);
        Nullable(w, "base", row.Base); Nullable(w, "result", row.Result); Nullable(w, "rejection", row.Rejection);
        w.WriteNumber("rawRemaining", row.RawRemaining); w.WriteNumber("metadataRemaining", row.MetadataRemaining);
        w.WriteNumber("objectsRemaining", row.ObjectsRemaining); w.WriteNumber("entriesRemaining", row.EntriesRemaining); w.WriteNumber("accountedBytes", row.AccountedBytes);
        w.WriteStartArray("accepted"); foreach (var reference in row.Accepted) RefJson(reference).WriteTo(w); w.WriteEndArray();
    });
    private void SaveTicket(Ticket row)
    {
        var bytes = Bytes(TicketJson(row)); Require(bytes.Length <= MetadataReserve);
        Exec("INSERT INTO transfers VALUES($id,$status,$body) ON CONFLICT(id) DO UPDATE SET status=excluded.status,body=excluded.body",
            ("$id", row.Id), ("$status", row.Status), ("$body", _cipher.Seal("transfer\0" + row.Id + "\0" + row.Status, bytes)));
    }
    private Ticket? LoadTicket(string id)
    {
        using var command = Command("SELECT status,CASE WHEN length(body)<=262172 THEN body ELSE NULL END FROM transfers WHERE id=$id", ("$id", id)); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string status = reader.GetString(0); Require(status == "staging" || status == "committed" || status == "rejected");
        Require(!reader.IsDBNull(1)); var encoded = (byte[])reader.GetValue(1); Require(encoded.Length <= MetadataReserve + 28);
        var value = Decode("transfer\0" + id + "\0" + status, encoded);
        Fields(value, "owner", "request", "status", "base", "result", "rejection", "rawRemaining", "metadataRemaining", "objectsRemaining", "entriesRemaining", "accountedBytes", "accepted");
        var row = new Ticket
        {
            Id = id,
            Owner = S(value, "owner"),
            Request = value.GetProperty("request").Clone(),
            Status = S(value, "status"),
            Base = Optional(value, "base"),
            Result = Optional(value, "result"),
            Rejection = Optional(value, "rejection"),
            RawRemaining = value.GetProperty("rawRemaining").GetInt64(),
            MetadataRemaining = value.GetProperty("metadataRemaining").GetInt64(),
            ObjectsRemaining = value.GetProperty("objectsRemaining").GetInt64(),
            EntriesRemaining = value.GetProperty("entriesRemaining").GetInt64(),
            AccountedBytes = value.GetProperty("accountedBytes").GetInt64()
        };
        TerminalPersistenceContract.Validate("Request", row.Request); Require(S(row.Request, "action") == "begin" && S(row.Request, "transferId") == id && row.Status == status);
        var owner = Owner(row.Owner); Require(S(owner.GetProperty("scope"), "applicationScopeId") == S(_options.Identity, "applicationScopeId") && S(owner.GetProperty("scope"), "endUserId") == S(_options.Identity, "endUserId"));
        Require(new[] { "sourceId", "sourceGeneration", "domainKey" }.All(name => S(row.Request, name) == S(_options.Identity, name)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.GetProperty("accepted").EnumerateArray())
        {
            Fields(item, "kind", "sha256", "byteLength", "received"); var reference = new ObjectRef(S(item, "kind"), S(item, "sha256"), N(item, "byteLength"), item.GetProperty("received").GetBoolean());
            Require(seen.Add(reference.Key) && row.Accepted.Count < 612 && reference.Bytes >= 1 && reference.Bytes <= 12288); row.Accepted.Add(reference);
        }
        Require(row.RawRemaining >= 0 && row.MetadataRemaining >= 0 && row.ObjectsRemaining >= 0 && row.EntriesRemaining >= 0 && row.AccountedBytes >= 0);
        if (row.Base.HasValue) ValidateRoot(row.Base.Value);
        if (row.Result.HasValue) ValidateRoot(row.Result.Value);
        Require(status == "staging" ? !row.Result.HasValue && !row.Rejection.HasValue : status == "committed" ? row.Result.HasValue && !row.Rejection.HasValue : !row.Result.HasValue && row.Rejection.HasValue);
        return row;
    }
    private EntryRow? FindEntry(string key, bool secondary = false)
    {
        using var command = Command("SELECT primary_key,secondary_key,ordinal,CASE WHEN length(body)<=1024 THEN body ELSE NULL END FROM entries WHERE " + (secondary ? "secondary_key" : "primary_key") + "=$key", ("$key", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        string primary = reader.GetString(0), second = reader.GetString(1); long ordinal = reader.GetInt64(2);
        Require(!reader.IsDBNull(3)); var bytes = (byte[])reader.GetValue(3); Require(bytes.Length <= 1024);
        var value = Decode("entry\0" + primary + "\0" + second + "\0" + ordinal.ToString(CultureInfo.InvariantCulture), bytes);
        TerminalPersistenceContract.Validate("Entry", value); Require(S(value, "primaryKey") == primary && S(value, "secondaryKey") == second && ordinal >= 1);
        return new EntryRow(ordinal, value);
    }
    private void InsertEntry(long ordinal, JsonElement entry)
    {
        string primary = S(entry, "primaryKey"), secondary = S(entry, "secondaryKey");
        Exec("INSERT INTO entries VALUES($primary,$secondary,$ordinal,$body)", ("$primary", primary), ("$secondary", secondary), ("$ordinal", ordinal),
            ("$body", _cipher.Seal("entry\0" + primary + "\0" + secondary + "\0" + ordinal.ToString(CultureInfo.InvariantCulture), Bytes(entry))));
    }
    private string Chain(string tag, params JsonElement[] elements)
    {
        using var memory = new MemoryStream(); using (var writer = new Utf8JsonWriter(memory))
        { writer.WriteStartArray(); writer.WriteStringValue(tag); foreach (var element in elements) element.WriteTo(writer); writer.WriteEndArray(); }
        return Hash(WireJson.Parse(memory.ToArray(), MaximumMetadataBytes));
    }
    private static JsonElement Literal(string value) => Json(w => w.WriteString("value", value)).GetProperty("value").Clone();
    private string EmptyIndex => Chain("TPV1-INDEX", _options.Identity);
    private void ValidateRoot(JsonElement root)
    {
        TerminalPersistenceContract.Validate("Root", root);
        var content = Json(w => { foreach (string name in new[] { "generation", "body", "index" }) Prop(w, name, root.GetProperty(name)); });
        Require(S(root, "commitRoot") == Chain("TPV1-ROOT", _options.Identity, content));
    }
}
