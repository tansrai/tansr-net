using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    private static JsonElement CustodyRef(string kind, byte[] bytes) => Json(w =>
    { w.WriteString("kind", kind); w.WriteString("sha256", WireJson.Sha256(bytes)); w.WriteNumber("byteLength", bytes.Length); });
    private JsonElement CustodyControl() => Json(w =>
    { w.WriteString("format", "terminal-persistence-control-v1"); Prop(w, "capacity", Used(LoadState())); Prop(w, "limits", Limits(_options)); });
    private JsonElement RegisterCustodyMetadata(string id, string kind, JsonElement value, string sourceKey)
    {
        var bytes = Bytes(value); var reference = CustodyRef(kind, bytes);
        Exec("INSERT INTO custody_objects VALUES($id,$kind,$hash,$bytes,$source)", ("$id", id), ("$kind", kind), ("$hash", S(reference, "sha256")), ("$bytes", bytes.Length), ("$source", sourceKey)); return reference;
    }
    private void WriteCustodyRecord(string id, long ordinal, string digest, JsonElement record)
    {
        var raw = WireJson.EncodeControl(record, 32768);
        Exec("INSERT INTO custody_records VALUES($id,$ordinal,$digest,$body)", ("$id", id), ("$ordinal", ordinal), ("$digest", digest),
            ("$body", _cipher.Seal("custody-record\0" + id + "\0" + ordinal.ToString(CultureInfo.InvariantCulture), raw)));
    }
    private JsonElement ReadCustodyRecord(string id, long ordinal)
    {
        var bytes = Scalar("SELECT body FROM custody_records WHERE operation_id=$id AND ordinal=$ordinal AND length(body)<=32796", ("$id", id), ("$ordinal", ordinal)) as byte[];
        Require(bytes != null); return Decode("custody-record\0" + id + "\0" + ordinal.ToString(CultureInfo.InvariantCulture), bytes!);
    }
    private JsonElement BuildCustodyProof(string id, Action check)
    {
        var state = LoadState(); var control = RegisterCustodyMetadata(id, "custody-control", CustodyControl(), "control");
        var rootRecord = Json(w => { w.WriteString("kind", "root"); Nullable(w, "root", state.Root); Prop(w, "controlRef", control); });
        string fixedRoot = PersistenceCustodyContract.SnapshotDigest(_options.Identity, rootRecord);
        string digest = PersistenceCustodyContract.InventorySeed(_options.Identity, "terminal-persistence-v1", fixedRoot);
        long ordinal = 0;
        void Add(JsonElement record)
        {
            check(); PersistenceCustodyContract.Validate("Record", record);
            Need(ordinal < 4194304, "capacity_exceeded");
            digest = PersistenceCustodyContract.RecordDigest(ordinal.ToString(CultureInfo.InvariantCulture), digest, record);
            WriteCustodyRecord(id, ordinal++, digest, record);
        }
        Add(rootRecord);
        using (var command = Command("SELECT primary_key FROM entries ORDER BY ordinal")) using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var row = FindEntry(reader.GetString(0))!;
                Add(Json(w => { w.WriteString("kind", "receipt-index"); w.WriteString("ordinal", row.Ordinal.ToString(CultureInfo.InvariantCulture)); Prop(w, "entry", row.Value); }));
            }
        using (var command = Command("SELECT id FROM transfers ORDER BY id")) using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                check(); var row = LoadTicket(reader.GetString(0))!; var metadata = ExportCustodyTransfer(row);
                PersistenceCustodyContract.Validate("TransferMetadata", metadata, 524288);
                var reference = RegisterCustodyMetadata(id, "custody-transfer", metadata, row.Id);
                Add(Json(w =>
                {
                    w.WriteString("kind", "transfer"); w.WriteString("transferId", row.Id); w.WriteString("intentSha256", S(row.Request, "intentSha256"));
                    w.WriteString("status", row.Status); Prop(w, "metadataRef", reference);
                }));
            }
        long objects = 0, total = 0;
        using (var command = Command("SELECT kind,hash,bytes FROM objects UNION ALL SELECT kind,hash,bytes FROM custody_objects WHERE operation_id=$id ORDER BY kind,hash", ("$id", id)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                string kind = reader.GetString(0), hash = reader.GetString(1); int length = reader.GetInt32(2);
                objects++; total = checked(total + length); Need(objects <= 1310721 && total <= 1102569742, "capacity_exceeded");
                Add(Json(w => { w.WriteString("kind", "object"); w.WriteStartObject("object"); w.WriteString("kind", kind); w.WriteString("sha256", hash); w.WriteNumber("byteLength", length); w.WriteEndObject(); }));
            }
        var proof = Json(w =>
        {
            w.WriteString("originFormat", "terminal-persistence-v1"); w.WriteString("fixedRoot", fixedRoot);
            if (state.Root.HasValue) { w.WriteString("originalCommitRoot", S(state.Root.Value, "commitRoot")); w.WriteString("indexRoot", S(state.Root.Value.GetProperty("index"), "root")); }
            else { w.WriteNull("originalCommitRoot"); w.WriteNull("indexRoot"); }
            w.WriteString("indexCount", state.Root.HasValue ? S(state.Root.Value.GetProperty("index"), "count") : "0");
            w.WriteString("inventoryDigest", digest); w.WriteNumber("records", ordinal); w.WriteNumber("objects", objects); w.WriteNumber("bytes", total);
        });
        PersistenceCustodyContract.Validate("RootProof", proof); return proof;
    }
    private JsonElement ExportCustodyPage(JsonElement request, CustodyOperation operation, Action check)
    {
        var state = LoadCustodyState(); Need(state.GetProperty("blocked").GetBoolean() && state.GetProperty("operationId").GetString() == operation.Id, "revision_conflict");
        var cursor = Optional(request, "cursor"); long first = cursor.HasValue ? long.Parse(S(cursor.Value, "index"), CultureInfo.InvariantCulture) : 0;
        string previous = first == 0 ? PersistenceCustodyContract.InventorySeed(_options.Identity, S(operation.Proof, "originFormat"), S(operation.Proof, "fixedRoot")) :
            (string?)Scalar("SELECT digest FROM custody_records WHERE operation_id=$id AND ordinal=$ordinal", ("$id", operation.Id), ("$ordinal", first - 1)) ?? "";
        Need(first >= 0 && first < operation.Next, "invalid_request");
        if (cursor.HasValue)
            Need(S(cursor.Value, "operationId") == operation.Id && S(cursor.Value, "intentSha256") == operation.Intent && S(cursor.Value, "fixedRoot") == S(operation.Proof, "fixedRoot") &&
                S(cursor.Value, "inventoryDigest") == S(operation.Proof, "inventoryDigest") && S(cursor.Value, "previousDigest") == previous, "request_conflict");
        var records = new List<JsonElement>(); long next = first; string digest = previous; JsonElement response = default;
        while (records.Count < N(request, "limit") && next < operation.Next)
        {
            check(); var record = ReadCustodyRecord(operation.Id, next); string candidateDigest = PersistenceCustodyContract.RecordDigest(next.ToString(CultureInfo.InvariantCulture), digest, record);
            records.Add(record); next++;
            var page = Json(w =>
            {
                w.WriteString("index", first.ToString(CultureInfo.InvariantCulture)); w.WriteString("previousDigest", previous); w.WriteString("sha256", candidateDigest);
                w.WriteStartArray("records"); foreach (var item in records) item.WriteTo(w); w.WriteEndArray(); w.WriteBoolean("complete", next == operation.Next);
            });
            try
            {
                response = CustodyResponse(request, w =>
                {
                    Prop(w, "proof", operation.Proof); Prop(w, "page", page);
                    w.WritePropertyName("nextCursor"); if (next == operation.Next) w.WriteNullValue();
                    else
                    {
                        w.WriteStartObject(); w.WriteString("operationId", operation.Id); w.WriteString("intentSha256", operation.Intent);
                        w.WriteString("fixedRoot", S(operation.Proof, "fixedRoot")); w.WriteString("inventoryDigest", S(operation.Proof, "inventoryDigest")); w.WriteString("index", next.ToString(CultureInfo.InvariantCulture)); w.WriteString("previousDigest", candidateDigest); w.WriteEndObject();
                    }
                });
                digest = candidateDigest;
            }
            catch (WireProtocolException) when (records.Count > 1) { break; }
        }
        Require(response.ValueKind == JsonValueKind.Object); return response;
    }
    private JsonElement ExportCustodyObject(JsonElement request, CustodyOperation operation, Action check)
    {
        var state = LoadCustodyState(); Need(state.GetProperty("blocked").GetBoolean() && state.GetProperty("operationId").GetString() == operation.Id, "revision_conflict");
        var reference = request.GetProperty("object"); string kind = S(reference, "kind"), hash = S(reference, "sha256"); int length = N(reference, "byteLength"); byte[] bytes;
        if (kind == "custody-control" || kind == "custody-transfer")
        {
            using var command = Command("SELECT bytes,source_key FROM custody_objects WHERE operation_id=$id AND kind=$kind AND hash=$hash", ("$id", operation.Id), ("$kind", kind), ("$hash", hash));
            using var reader = command.ExecuteReader(); Need(reader.Read() && reader.GetInt32(0) == length, "request_conflict");
            bytes = Bytes(kind == "custody-control" ? CustodyControl() : ExportCustodyTransfer(LoadTicket(reader.GetString(1))!));
        }
        else bytes = ReadObject(new ObjectRef(kind, hash, length, false));
        Need(bytes.Length == length && WireJson.Sha256(bytes) == hash, "integrity_mismatch"); check();
        int offset = N(request, "offset"); Need(offset <= bytes.Length, "invalid_request"); byte[] chunk = bytes.Skip(offset).Take(N(request, "length")).ToArray();
        return CustodyResponse(request, w => { w.WriteStartObject("chunk"); Prop(w, "object", reference); w.WriteNumber("offset", offset); WritePayload(w, chunk); w.WriteEndObject(); });
    }
}
