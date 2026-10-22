using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    private static readonly string[] CustodySchema =
    {
        "CREATE TABLE custody_state (id INTEGER PRIMARY KEY CHECK(id=1), body BLOB NOT NULL) STRICT",
        "CREATE TABLE custody_operations (id TEXT PRIMARY KEY, body BLOB NOT NULL) STRICT",
        "CREATE TABLE custody_records (operation_id TEXT NOT NULL, ordinal INTEGER NOT NULL, digest TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(operation_id,ordinal)) STRICT",
        "CREATE TABLE custody_objects (operation_id TEXT NOT NULL, kind TEXT NOT NULL, hash TEXT NOT NULL, bytes INTEGER NOT NULL, source_key TEXT, PRIMARY KEY(operation_id,kind,hash)) STRICT",
        "CREATE TABLE custody_chunks (operation_id TEXT NOT NULL, kind TEXT NOT NULL, hash TEXT NOT NULL, offset INTEGER NOT NULL, bytes INTEGER NOT NULL, body BLOB NOT NULL, PRIMARY KEY(operation_id,kind,hash,offset)) STRICT",
    };
    private sealed class CustodyOperation
    {
        internal string Id = "", Intent = "", Direction = "", Phase = "prepared", Digest = "";
        internal JsonElement Proof;
        internal long Next, StoredBytes;
        internal long BudgetBytes = 1073741824, BudgetObjects = 1048576;
        internal long ReceivedBytes, InventoryBytes, InventoryObjects;
    }
    private ITerminalPersistenceCustodyAuthority CustodyAuthority => new TerminalPersistenceCustodyAuthorityGuard(_options.CustodyAuthority!, _options.ReadContext);
    private void InitializeCustody()
    {
        bool existing = Number("SELECT count(*) FROM sqlite_master WHERE name='custody_state'") != 0;
        if (!_options.EnableCustodyHandoff) { Require(!existing, "identity_mismatch"); return; }
        if (!existing)
        {
            foreach (string sql in CustodySchema) Exec(sql);
            SaveCustodyState(_options.Custody, null, false);
        }
        var state = LoadCustodyState(); Require(Equal(state.GetProperty("custody"), _options.Custody), "identity_mismatch");
    }
    private JsonElement LoadCustodyState()
    {
        var bytes = Scalar("SELECT body FROM custody_state WHERE id=1 AND length(body)<=32796") as byte[]; Require(bytes != null);
        var state = Decode("custody-state", bytes!); Fields(state, "custody", "operationId", "blocked");
        PersistenceCustodyContract.Validate("Custody", state.GetProperty("custody"));
        Require(state.GetProperty("blocked").ValueKind == JsonValueKind.True || state.GetProperty("blocked").ValueKind == JsonValueKind.False);
        return state;
    }
    private void SaveCustodyState(JsonElement custody, string? operation, bool blocked)
    {
        var value = Json(w => { Prop(w, "custody", custody); if (operation == null) w.WriteNull("operationId"); else w.WriteString("operationId", operation); w.WriteBoolean("blocked", blocked); });
        Exec("INSERT INTO custody_state VALUES(1,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body", ("$body", _cipher.Seal("custody-state", Bytes(value))));
    }
    private void AssertCustodyWritable()
    {
        if (!_options.EnableCustodyHandoff) return;
        var value = LoadCustodyState(); Need(!value.GetProperty("blocked").GetBoolean(), "revision_conflict");
        Require(Equal(value.GetProperty("custody"), _options.Custody), "identity_mismatch");
    }
    public async Task<JsonElement> ExecuteAsync(JsonElement request, string ownerCanonical, CancellationToken cancellationToken = default)
    {
        request = TerminalPersistenceContract.Copy("Request", request);
        if (!_options.EnableCustodyHandoff || S(request, "action") is "head" or "read" or "lookup" or "query")
            return await ExecuteOriginalAsync(request, ownerCanonical, cancellationToken).ConfigureAwait(false);
        var query = Json(w =>
        {
            w.WriteString("contract", PersistenceCustodyContract.Protocol); Prop(w, "identity", _options.Identity);
            w.WriteString("operationId", PersistenceCustodyContract.WriterOperationId(request)); w.WriteString("intentSha256", PersistenceCustodyContract.Digest(request));
            w.WriteString("action", "writer-query"); Prop(w, "custody", _options.Custody);
        });
        var observed = await CustodyAuthority.ExecuteAsync(query, cancellationToken).ConfigureAwait(false);
        var previous = observed.GetProperty("claim");
        if (previous.ValueKind != JsonValueKind.Null)
        {
            // PST retries expose the current original transfer, not a byte-identical historical response.
            // A settled fact never grants execution and an unknown claim never becomes a new writer.
            if (S(previous, "status") != "settled") throw new IOException("custody_writer_unresolved");
            Need(previous.GetProperty("responseDigest").ValueKind != JsonValueKind.Null, "request_conflict");
            return await ExecuteOriginalAsync(request, ownerCanonical, cancellationToken, mutationReplayOnly: true).ConfigureAwait(false);
        }
        var lease = await CustodyAuthority.AcquireWriterAsync(_options.Identity, _options.Custody, request, cancellationToken).ConfigureAwait(false);
        JsonElement result;
        try { result = await ExecuteOriginalAsync(request, ownerCanonical, cancellationToken, lease.AssertCurrent).ConfigureAwait(false); }
        catch (MemoryPublicationRejectedException)
        {
            // Only a confirmed pre-commit rejection/rollback releases a shared claim. IO and unknown COMMIT do not.
            await ConfirmCustodyAsync(() => lease.FinishAsync(Json(w => w.WriteString("state", "rolled-back")), cancellationToken)).ConfigureAwait(false); throw;
        }
        await ConfirmCustodyAsync(() => lease.FinishAsync(Json(w => { w.WriteString("state", "committed"); w.WriteString("responseDigest", PersistenceCustodyContract.Digest(result)); }), cancellationToken)).ConfigureAwait(false);
        return result;
    }
    private static async Task<JsonElement> ConfirmCustodyAsync(Func<Task<JsonElement>> confirm)
    {
        // Local work has already settled. A lost acknowledgement never proves that it rolled back.
        try { return await confirm().ConfigureAwait(false); }
        catch (Exception error) { throw new IOException("custody_outcome_unconfirmed", error); }
    }
    private CustodyOperation? ReadCustodyOperation(string id)
    {
        var bytes = Scalar("SELECT body FROM custody_operations WHERE id=$id AND length(body)<=262172", ("$id", id)) as byte[];
        if (bytes == null) return null;
        var value = Decode("custody-operation\0" + id, bytes);
        bool fixedBudget = value.TryGetProperty("migrationBudget", out var budget);
        Fields(value, new[] { "intent", "direction", "phase", "proof", "next", "digest", "storedBytes" }
            .Concat(fixedBudget ? new[] { "migrationBudget", "receivedBytes", "inventoryBytes", "inventoryObjects" } : Array.Empty<string>()).ToArray());
        if (fixedBudget) Fields(budget, "bytes", "objects");
        return new CustodyOperation
        {
            Id = id,
            Intent = S(value, "intent"),
            Direction = S(value, "direction"),
            Phase = S(value, "phase"),
            Proof = value.GetProperty("proof").Clone(),
            Next = value.GetProperty("next").GetInt64(),
            Digest = S(value, "digest"),
            StoredBytes = value.GetProperty("storedBytes").GetInt64(),
            // 旧受理没有显式预算，只继承旧默认；后来的配置不能替它扩大范围。
            BudgetBytes = fixedBudget ? budget.GetProperty("bytes").GetInt64() : 1073741824,
            BudgetObjects = fixedBudget ? budget.GetProperty("objects").GetInt64() : 1048576,
            ReceivedBytes = fixedBudget ? value.GetProperty("receivedBytes").GetInt64() : Number("SELECT coalesce(sum(bytes),0) FROM custody_chunks WHERE operation_id=$id", ("$id", id)),
            InventoryBytes = fixedBudget ? value.GetProperty("inventoryBytes").GetInt64() : Number("SELECT coalesce(sum(bytes),0) FROM custody_objects WHERE operation_id=$id", ("$id", id)),
            InventoryObjects = fixedBudget ? value.GetProperty("inventoryObjects").GetInt64() : Number("SELECT count(*) FROM custody_objects WHERE operation_id=$id", ("$id", id))
        };
    }
    private void SaveCustodyOperation(CustodyOperation operation)
    {
        var value = Json(w =>
        {
            w.WriteString("intent", operation.Intent); w.WriteString("direction", operation.Direction); w.WriteString("phase", operation.Phase); Prop(w, "proof", operation.Proof);
            w.WriteNumber("next", operation.Next); w.WriteString("digest", operation.Digest); w.WriteNumber("storedBytes", operation.StoredBytes);
            w.WriteStartObject("migrationBudget"); w.WriteNumber("bytes", operation.BudgetBytes); w.WriteNumber("objects", operation.BudgetObjects); w.WriteEndObject();
            w.WriteNumber("receivedBytes", operation.ReceivedBytes); w.WriteNumber("inventoryBytes", operation.InventoryBytes); w.WriteNumber("inventoryObjects", operation.InventoryObjects);
        });
        Exec("INSERT INTO custody_operations VALUES($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body", ("$id", operation.Id), ("$body", _cipher.Seal("custody-operation\0" + operation.Id, Bytes(value))));
    }
    private JsonElement CustodyResponse(JsonElement request, Action<Utf8JsonWriter> write)
    {
        var value = Json(w => { foreach (string field in new[] { "contract", "identity", "operationId", "intentSha256", "action" }) Prop(w, field, request.GetProperty(field)); write(w); });
        PersistenceCustodyContract.ValidateResponse(request, value); return value;
    }
    private async Task<JsonElement> AuthorityTicket(JsonElement request, CancellationToken ct)
    {
        var query = Json(w => { foreach (string field in new[] { "contract", "identity", "operationId", "intentSha256" }) Prop(w, field, request.GetProperty(field)); w.WriteString("action", "query"); });
        var response = await CustodyAuthority.ExecuteAsync(query, ct).ConfigureAwait(false);
        Need(response.GetProperty("ticket").ValueKind != JsonValueKind.Null, "revision_conflict"); return response.GetProperty("ticket").Clone();
    }

    public async Task<JsonElement> ExecuteCustodyAsync(JsonElement request, string ownerCanonical, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableCustodyHandoff) throw new MemoryPublicationRejectedException("invalid_request");
        request = PersistenceCustodyContract.Copy("Request", request); var owner = Owner(ownerCanonical);
        Need(Equal(request.GetProperty("identity"), _options.Identity) && Equal(owner.GetProperty("scope"), Scope(_options.ReadContext, _options.Identity)), "request_conflict");
        void CheckOwner() { cancellationToken.ThrowIfCancellationRequested(); Need(Equal(owner.GetProperty("scope"), Scope(_options.ReadContext, _options.Identity)), "request_conflict"); }
        string action = S(request, "action"), id = S(request, "operationId"), intent = S(request, "intentSha256");
        if (action == "prepare")
        {
            Need(Equal(request.GetProperty("source"), _options.Custody), "revision_conflict");
            var lease = await CustodyAuthority.BeginHandoffAsync(request, cancellationToken).ConfigureAwait(false);
            return await ConfirmCustodyAsync(async () =>
            {
                CheckOwner();
                var proof = Run(check =>
                {
                    JsonElement result = default;
                    Transaction(() =>
                    {
                        var state = LoadState(); Require(!state.ReadOnlyCopy, "read_only_copy"); Need(Equal(Optional(request, "expectedRoot") ?? Json(w => w.WriteNull("value")).GetProperty("value"),
                            state.Root?.GetProperty("commitRoot") ?? Json(w => w.WriteNull("value")).GetProperty("value")), "revision_conflict");
                        var current = LoadCustodyState(); string? active = current.GetProperty("operationId").GetString();
                        Need(active == null || active == id, "request_conflict");
                        var prior = ReadCustodyOperation(id);
                        result = prior == null ? BuildCustodyProof(id, check) : prior.Proof;
                        Need(prior == null || prior.Intent == intent && prior.Direction == "source" && Equal(prior.Proof, result), "request_conflict");
                        if (prior == null)
                        {
                            Need(Number("SELECT count(*) FROM custody_operations") < _options.MaxTransferFacts, "capacity_exceeded");
                            SaveCustodyOperation(new CustodyOperation
                            {
                                Id = id,
                                Intent = intent,
                                Direction = "source",
                                Proof = result,
                                Digest = S(result, "inventoryDigest"),
                                Next = result.GetProperty("records").GetInt64()
                            });
                        }
                        SaveCustodyState(_options.Custody, id, true);
                    }, check); return result;
                }, cancellationToken, assertCurrent: () => { lease.AssertCurrent(); CheckOwner(); });
                var ticket = await lease.RecordPreparedAsync(proof, cancellationToken).ConfigureAwait(false); CheckOwner();
                return CustodyResponse(request, w => Prop(w, "ticket", ticket));
            }).ConfigureAwait(false);
        }
        if (action == "query" || action == "abort" || action == "cutover")
        {
            var response = await CustodyAuthority.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            try { CheckOwner(); } catch (Exception error) { throw new IOException("custody_outcome_unconfirmed", error); }
            if (response.GetProperty("ticket").ValueKind != JsonValueKind.Null)
                ReconcileCustodyTicket(request, response.GetProperty("ticket"), cancellationToken);
            return response;
        }
        Need(action is "export-page" or "export-object" or "import-page" or "import-object" or "verify", "invalid_request");
        var confirmed = await AuthorityTicket(request, cancellationToken).ConfigureAwait(false);
        CheckOwner();
        var localResult = Run(check =>
        {
            Need(S(confirmed, "operationId") == id && S(confirmed, "intentSha256") == intent && Equal(confirmed.GetProperty("identity"), _options.Identity), "request_conflict");
            if (action == "export-page" || action == "export-object")
            {
                Need(Equal(confirmed.GetProperty("source"), _options.Custody), "revision_conflict");
                var operation = ReadCustodyOperation(id); Need(operation != null && operation.Intent == intent && operation.Direction == "source" && Equal(operation.Proof, confirmed.GetProperty("proof")), "request_conflict");
                return action == "export-page" ? ExportCustodyPage(request, operation!, check) : ExportCustodyObject(request, operation!, check);
            }
            JsonElement result = default;
            Transaction(() => { result = ImportCustody(request, confirmed, check); }, check); return result;
        }, cancellationToken, assertCurrent: CheckOwner);
        if (action != "verify") return localResult;
        return await ConfirmCustodyAsync(async () => { var value = await CustodyAuthority.ExecuteAsync(request, cancellationToken).ConfigureAwait(false); CheckOwner(); return value; }).ConfigureAwait(false);
    }
}
