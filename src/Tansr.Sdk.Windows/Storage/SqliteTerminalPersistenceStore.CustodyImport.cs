using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    private JsonElement ImportCustody(JsonElement request, JsonElement ticket, Action check)
    {
        string id = S(request, "operationId"), intent = S(request, "intentSha256"), action = S(request, "action");
        var destination = ticket.GetProperty("destination");
        Need(S(destination, "custodyId") == S(_options.Custody, "custodyId") && S(destination, "mode") == S(_options.Custody, "mode") &&
            long.Parse(S(_options.Custody, "custodyRevision"), CultureInfo.InvariantCulture) == long.Parse(S(ticket.GetProperty("source"), "custodyRevision"), CultureInfo.InvariantCulture) + 1, "revision_conflict");
        Need(S(ticket, "phase") is "prepared" or "staged" or "verified", "revision_conflict");
        var proof = ticket.GetProperty("proof"); Need(S(proof, "originFormat") == "terminal-persistence-v1", "invalid_request");
        var operation = ReadCustodyOperation(id);
        if (operation == null)
        {
            Need(action == "import-page" && S(request.GetProperty("page"), "index") == "0", "invalid_request");
            var current = LoadCustodyState(); Need(current.GetProperty("operationId").ValueKind == JsonValueKind.Null, "request_conflict");
            var state = LoadState(); Need(!state.Root.HasValue && state.Used.Values.All(x => x == 0), "request_conflict");
            Need(Number("SELECT count(*) FROM custody_operations") < _options.MaxTransferFacts, "capacity_exceeded");
            operation = new CustodyOperation
            {
                Id = id,
                Intent = intent,
                Direction = "target",
                Proof = proof.Clone(),
                BudgetBytes = _options.MigrationBudget!.Bytes,
                BudgetObjects = _options.MigrationBudget.Objects,
                Digest = PersistenceCustodyContract.InventorySeed(_options.Identity, S(proof, "originFormat"), S(proof, "fixedRoot"))
            };
            CheckCustodyMigrationBudget(operation);
            state.ReadOnlyCopy = true; SaveState(state); SaveCustodyState(_options.Custody, id, true); SaveCustodyOperation(operation);
        }
        Need(operation.Intent == intent && operation.Direction == "target" && Equal(operation.Proof, proof), "request_conflict");
        CheckCustodyMigrationBudget(operation);
        if (action == "import-page")
        {
            Need(Equal(request.GetProperty("proof"), proof), "request_conflict"); var page = request.GetProperty("page");
            long first = long.Parse(S(page, "index"), CultureInfo.InvariantCulture), next = first;
            string digest = first == 0 ? PersistenceCustodyContract.InventorySeed(_options.Identity, S(proof, "originFormat"), S(proof, "fixedRoot")) :
                (string?)Scalar("SELECT digest FROM custody_records WHERE operation_id=$id AND ordinal=$ordinal", ("$id", id), ("$ordinal", first - 1)) ?? "";
            Need(first <= operation.Next && S(page, "previousDigest") == digest, "request_conflict");
            foreach (var record in page.GetProperty("records").EnumerateArray())
            {
                check(); digest = PersistenceCustodyContract.RecordDigest(next.ToString(CultureInfo.InvariantCulture), digest, record);
                if (next < operation.Next) Need(Equal(ReadCustodyRecord(id, next), record), "request_conflict");
                else
                {
                    Need(operation.Phase == "prepared", "revision_conflict"); ValidateCustodyRecordOrder(id, next, record);
                    if (next == 0) Need(PersistenceCustodyContract.SnapshotDigest(_options.Identity, record) == S(proof, "fixedRoot"), "integrity_mismatch");
                    operation.StoredBytes = checked(operation.StoredBytes + Bytes(record).Length);
                    WriteCustodyRecord(id, next, digest, record); operation.Next++; operation.Digest = digest;
                    if (S(record, "kind") == "object")
                    {
                        var reference = record.GetProperty("object"); Need(N(reference, "byteLength") > 0, "invalid_request");
                        operation.InventoryBytes = checked(operation.InventoryBytes + N(reference, "byteLength"));
                        operation.InventoryObjects++; CheckCustodyMigrationBudget(operation);
                        Exec("INSERT INTO custody_objects VALUES($id,$kind,$hash,$bytes,NULL)", ("$id", id), ("$kind", S(reference, "kind")), ("$hash", S(reference, "sha256")), ("$bytes", N(reference, "byteLength")));
                    }
                }
                next++;
            }
            Need(next <= proof.GetProperty("records").GetInt64() && S(page, "sha256") == digest &&
                page.GetProperty("complete").GetBoolean() == (next == proof.GetProperty("records").GetInt64()), "integrity_mismatch");
            if (page.GetProperty("complete").GetBoolean()) Need(digest == S(proof, "inventoryDigest"), "integrity_mismatch");
            SaveCustodyOperation(operation);
            return CustodyResponse(request, w => { Prop(w, "ticket", ticket); w.WriteString("pageDigest", digest); });
        }
        if (action == "import-object")
        {
            var chunk = request.GetProperty("chunk"); var reference = chunk.GetProperty("object");
            string kind = S(reference, "kind"), hash = S(reference, "sha256"); int length = N(reference, "byteLength"), offset = N(chunk, "offset");
            Need(Number("SELECT count(*) FROM custody_objects WHERE operation_id=$id AND kind=$kind AND hash=$hash AND bytes=$bytes", ("$id", id), ("$kind", kind), ("$hash", hash), ("$bytes", length)) == 1, "request_conflict");
            byte[] bytes = WireJson.DecodeBase64(S(chunk, "base64")); Need(bytes.Length > 0 && bytes.Length <= 12288 && offset >= 0 && bytes.Length <= length - offset, "invalid_request");
            byte[]? existing = Scalar("SELECT body FROM custody_chunks WHERE operation_id=$id AND kind=$kind AND hash=$hash AND offset=$offset", ("$id", id), ("$kind", kind), ("$hash", hash), ("$offset", offset)) as byte[];
            string context = CustodyChunkContext(id, kind, hash, offset);
            if (existing != null) Need(_cipher.Open(context, existing).SequenceEqual(bytes), "request_conflict");
            else
            {
                Need(operation.Phase == "prepared", "revision_conflict");
                long received = Number("SELECT coalesce(sum(bytes),0) FROM custody_chunks WHERE operation_id=$id AND kind=$kind AND hash=$hash", ("$id", id), ("$kind", kind), ("$hash", hash));
                Need(received == offset, "request_conflict"); operation.StoredBytes = checked(operation.StoredBytes + bytes.Length);
                operation.ReceivedBytes = checked(operation.ReceivedBytes + bytes.Length); CheckCustodyMigrationBudget(operation);
                Exec("INSERT INTO custody_chunks VALUES($id,$kind,$hash,$offset,$bytes,$body)", ("$id", id), ("$kind", kind), ("$hash", hash), ("$offset", offset), ("$bytes", bytes.Length), ("$body", _cipher.Seal(context, bytes)));
                SaveCustodyOperation(operation);
            }
            return CustodyResponse(request, w => { Prop(w, "ticket", ticket); Prop(w, "object", reference); w.WriteNumber("nextOffset", offset + bytes.Length); });
        }
        Need(action == "verify" && Equal(request.GetProperty("proof"), proof), "request_conflict");
        if (operation.Phase == "prepared") { InstallCustody(operation, check); operation.Phase = "verified"; SaveCustodyOperation(operation); }
        Need(operation.Phase == "verified", "revision_conflict"); return CustodyResponse(request, w => Prop(w, "ticket", ticket));
    }
    private static void CheckCustodyMigrationBudget(CustodyOperation operation)
    {
        Require(operation.BudgetBytes >= 0 && operation.BudgetBytes <= 1102569742 && operation.BudgetObjects >= 0 && operation.BudgetObjects <= 1310721);
        long proofBytes = operation.Proof.GetProperty("bytes").GetInt64(), proofObjects = operation.Proof.GetProperty("objects").GetInt64();
        Need(proofBytes <= operation.BudgetBytes && proofObjects <= operation.BudgetObjects, "capacity_exceeded");
        Require(operation.ReceivedBytes >= 0 && operation.InventoryBytes >= 0 && operation.InventoryObjects >= 0);
        Need(operation.InventoryBytes <= proofBytes && operation.InventoryObjects <= proofObjects && operation.ReceivedBytes <= operation.InventoryBytes, "capacity_exceeded");
    }
    private static string CustodyChunkContext(string id, string kind, string hash, int offset) => "custody-chunk\0" + id + "\0" + kind + "\0" + hash + "\0" + offset.ToString(CultureInfo.InvariantCulture);
    private void ValidateCustodyRecordOrder(string id, long ordinal, JsonElement record)
    {
        string kind = S(record, "kind"); string[] stages = { "root", "receipt-index", "transfer", "object" }; int stage = Array.IndexOf(stages, kind);
        Need(stage >= 0 && (ordinal == 0 ? stage == 0 : stage > 0), "invalid_request");
        if (ordinal == 0) return;
        var prior = ReadCustodyRecord(id, ordinal - 1); int previous = Array.IndexOf(stages, S(prior, "kind")); Need(previous <= stage, "invalid_request");
        if (kind == "receipt-index") Need(long.Parse(S(record, "ordinal"), CultureInfo.InvariantCulture) == (previous == stage ? long.Parse(S(prior, "ordinal"), CultureInfo.InvariantCulture) + 1 : 1), "invalid_request");
        if (previous == stage && kind == "transfer") Need(StringComparer.Ordinal.Compare(S(prior, "transferId"), S(record, "transferId")) < 0, "invalid_request");
        if (previous == stage && kind == "object")
        {
            var a = prior.GetProperty("object"); var b = record.GetProperty("object");
            Need(StringComparer.Ordinal.Compare(S(a, "kind") + ":" + S(a, "sha256"), S(b, "kind") + ":" + S(b, "sha256")) < 0, "invalid_request");
        }
    }
    private byte[] ReadCustodyObject(string id, JsonElement reference, Action check)
    {
        string kind = S(reference, "kind"), hash = S(reference, "sha256"); int total = N(reference, "byteLength");
        int maximum = kind == "custody-transfer" ? 524288 : kind == "custody-control" ? 32768 : 12288;
        Need(total >= 1 && total <= maximum, "invalid_request");
        var result = new byte[total]; int offset = 0;
        using var command = Command("SELECT offset,bytes,body FROM custody_chunks WHERE operation_id=$id AND kind=$kind AND hash=$hash ORDER BY offset", ("$id", id), ("$kind", kind), ("$hash", hash));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            check(); Require(reader.GetInt32(0) == offset); byte[] bytes = _cipher.Open(CustodyChunkContext(id, kind, hash, offset), (byte[])reader.GetValue(2));
            Require(reader.GetInt32(1) == bytes.Length && bytes.Length > 0 && bytes.Length <= Math.Min(12288, total - offset));
            Buffer.BlockCopy(bytes, 0, result, offset, bytes.Length); offset += bytes.Length;
        }
        Need(offset == total && WireJson.Sha256(result) == hash, "integrity_mismatch"); return result;
    }
    private void InstallCustody(CustodyOperation operation, Action check)
    {
        CheckCustodyMigrationBudget(operation);
        Need(Number("SELECT coalesce(sum(bytes),0) FROM custody_chunks WHERE operation_id=$id", ("$id", operation.Id)) == operation.ReceivedBytes, "integrity_mismatch");
        Need(operation.InventoryBytes == operation.Proof.GetProperty("bytes").GetInt64() && operation.InventoryObjects == operation.Proof.GetProperty("objects").GetInt64() && operation.ReceivedBytes == operation.InventoryBytes, "integrity_mismatch");
        Need(operation.Next == operation.Proof.GetProperty("records").GetInt64() && operation.Digest == S(operation.Proof, "inventoryDigest"), "integrity_mismatch");
        var rootRecord = ReadCustodyRecord(operation.Id, 0); var originalRoot = Optional(rootRecord, "root");
        var controlBytes = ReadCustodyObject(operation.Id, rootRecord.GetProperty("controlRef"), check);
        var control = WireJson.DecodeControl(controlBytes, 32768); PersistenceCustodyContract.Validate("ControlState", control);
        Need(Bytes(control).SequenceEqual(controlBytes), "integrity_mismatch");
        var state = new State { Root = originalRoot, ReadOnlyCopy = true };
        foreach (string name in CounterNames) state.Used[name] = control.GetProperty("capacity").GetProperty(name).GetInt64();
        CheckCapacity(state); if (originalRoot.HasValue) ValidateRoot(originalRoot.Value);
        Need(originalRoot.HasValue ? S(originalRoot.Value, "commitRoot") == S(operation.Proof, "originalCommitRoot") && S(originalRoot.Value.GetProperty("index"), "root") == S(operation.Proof, "indexRoot") &&
            S(originalRoot.Value.GetProperty("index"), "count") == S(operation.Proof, "indexCount") : operation.Proof.GetProperty("originalCommitRoot").ValueKind == JsonValueKind.Null &&
            operation.Proof.GetProperty("indexRoot").ValueKind == JsonValueKind.Null && S(operation.Proof, "indexCount") == "0", "integrity_mismatch");
        long objects = 0, total = 0;
        using (var command = Command("SELECT kind,hash,bytes FROM custody_objects WHERE operation_id=$id ORDER BY kind,hash", ("$id", operation.Id))) using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                check(); string kind = reader.GetString(0), hash = reader.GetString(1); int length = reader.GetInt32(2);
                var reference = Json(w => { w.WriteString("kind", kind); w.WriteString("sha256", hash); w.WriteNumber("byteLength", length); });
                byte[] bytes = ReadCustodyObject(operation.Id, reference, check); objects++; total = checked(total + bytes.Length);
                if (kind != "custody-control" && kind != "custody-transfer") AddObject(new ObjectRef(kind, hash, length, false), bytes);
            }
        Need(objects == operation.Proof.GetProperty("objects").GetInt64() && total == operation.Proof.GetProperty("bytes").GetInt64(), "integrity_mismatch");
        for (long ordinal = 1; ordinal < operation.Next; ordinal++)
        {
            check(); var record = ReadCustodyRecord(operation.Id, ordinal); string kind = S(record, "kind");
            if (kind == "receipt-index") InsertEntry(long.Parse(S(record, "ordinal"), CultureInfo.InvariantCulture), record.GetProperty("entry"));
            if (kind == "transfer")
            {
                var bytes = ReadCustodyObject(operation.Id, record.GetProperty("metadataRef"), check); var metadata = WireJson.DecodeControl(bytes, 524288);
                PersistenceCustodyContract.Validate("TransferMetadata", metadata, 524288); Need(WireJson.EncodeControl(metadata, 524288).SequenceEqual(bytes), "integrity_mismatch");
                var row = ImportCustodyTransfer(metadata); Need(row.Id == S(record, "transferId") && S(row.Request, "intentSha256") == S(record, "intentSha256") && row.Status == S(record, "status"), "integrity_mismatch");
                SaveTicket(row);
                Need(Equal(Transfer(row.Request, row), metadata.GetProperty("transfer")), "integrity_mismatch");
            }
        }
        SaveState(state); Audit(); check();
    }
    private void ReconcileCustodyTicket(JsonElement request, JsonElement ticket, CancellationToken ct)
    {
        Run(check =>
        {
            var operation = ReadCustodyOperation(S(request, "operationId")); if (operation == null) return false;
            Need(operation.Intent == S(request, "intentSha256") && Equal(operation.Proof, ticket.GetProperty("proof")), "request_conflict");
            string phase = S(ticket, "phase"); if (phase != "published" && phase != "aborted") return false;
            string? currentOperation = LoadCustodyState().GetProperty("operationId").GetString();
            if (currentOperation != null && currentOperation != operation.Id) return false; // Historical query must not unlock a later handoff.
            Transaction(() =>
            {
                if (phase == "published" && operation.Direction == "target")
                {
                    Need((operation.Phase is "verified" or "published") && Equal(ticket.GetProperty("selectedCustody"), _options.Custody), "revision_conflict");
                    var state = LoadState(); state.ReadOnlyCopy = false; SaveState(state); SaveCustodyState(_options.Custody, null, false);
                }
                else if (phase == "aborted" && operation.Direction == "source")
                { Need(Equal(ticket.GetProperty("selectedCustody"), _options.Custody), "revision_conflict"); SaveCustodyState(_options.Custody, null, false); }
                else SaveCustodyState(_options.Custody, operation.Id, true);
                operation.Phase = phase; SaveCustodyOperation(operation);
            }, check); return true;
        }, ct);
    }
}
