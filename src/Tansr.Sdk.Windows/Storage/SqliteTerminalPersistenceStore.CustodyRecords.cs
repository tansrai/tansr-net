using System.Text.Json;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    private static long TicketStagingBytes(Ticket ticket) => ticket.Status == "staging"
        ? N(ticket.Request.GetProperty("declared"), "bytes") -
            (ticket.ReleasedReuseReservations ? ticket.Accepted.Where(x => !x.Received).Sum(x => (long)x.Bytes) : 0)
        : 0;

    // A portable ticket carries the original public begin/owner/base/transfer, never a C# row or
    // another runtime's private SQLite JSON. Exact source reservations remain visible in the catalog.
    private JsonElement ExportCustodyTransfer(Ticket ticket) => Json(w =>
    {
        Prop(w, "begin", ticket.Request); Prop(w, "owner", Owner(ticket.Owner)); Nullable(w, "baseRoot", ticket.Base);
        Prop(w, "transfer", Transfer(ticket.Request, ticket));
        foreach (bool received in new[] { true, false })
        {
            w.WriteStartArray(received ? "received" : "reused");
            foreach (var item in ticket.Accepted.Where(x => x.Received == received).OrderBy(x => x.Key, StringComparer.Ordinal))
            { w.WriteStartObject(); w.WriteString("kind", item.Kind); w.WriteString("sha256", item.Hash); w.WriteNumber("byteLength", item.Bytes); w.WriteEndObject(); }
            w.WriteEndArray();
        }
        w.WriteNumber("reservedBytes", ticket.RawRemaining + ticket.MetadataRemaining);
        w.WriteNumber("reservedObjects", ticket.ObjectsRemaining); w.WriteNumber("reservedEntries", ticket.EntriesRemaining);
        w.WriteNumber("stagingBytes", TicketStagingBytes(ticket));
    });

    private Ticket ImportCustodyTransfer(JsonElement value)
    {
        Fields(value, "begin", "owner", "baseRoot", "transfer", "received", "reused", "reservedBytes", "reservedObjects", "reservedEntries", "stagingBytes");
        var begin = value.GetProperty("begin"); var transfer = value.GetProperty("transfer");
        TerminalPersistenceContract.Validate("Request", begin); TerminalPersistenceContract.Validate("Transfer", transfer);
        Require(S(begin, "action") == "begin" && S(transfer, "status") != "unknown" && S(begin, "transferId") == S(transfer, "transferId") &&
            S(begin, "intentSha256") == S(transfer, "intentSha256"));
        Require(new[] { "sourceId", "sourceGeneration", "domainKey" }.All(name => S(begin, name) == S(_options.Identity, name)));
        string owner = Canon(value.GetProperty("owner")); var checkedOwner = Owner(owner);
        Require(S(checkedOwner.GetProperty("scope"), "applicationScopeId") == S(_options.Identity, "applicationScopeId") &&
            S(checkedOwner.GetProperty("scope"), "endUserId") == S(_options.Identity, "endUserId"));
        var row = new Ticket
        {
            Id = S(begin, "transferId"),
            Request = begin.Clone(),
            Owner = owner,
            Base = Optional(value, "baseRoot"),
            Status = S(transfer, "status"),
            Result = Optional(transfer, "result"),
            Rejection = Optional(transfer, "rejection"),
            ObjectsRemaining = value.GetProperty("reservedObjects").GetInt64(),
            EntriesRemaining = value.GetProperty("reservedEntries").GetInt64()
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (bool received in new[] { true, false })
        {
            var map = value.GetProperty(received ? "received" : "reused"); Require(map.ValueKind == JsonValueKind.Array);
            string? previous = null;
            foreach (var item in map.EnumerateArray())
            {
                Fields(item, "kind", "sha256", "byteLength"); string kind = S(item, "kind"), hash = S(item, "sha256"), key = kind + ":" + hash;
                int bytes = N(item, "byteLength"); TerminalPersistenceContract.Validate("Digest", Json(w => w.WriteString("value", hash)).GetProperty("value"));
                Require(new[] { "body-block", "body-page", "index-page", "receipt-value" }.Contains(kind) && bytes >= 1 && bytes <= 12288 && seen.Add(key) && seen.Count <= 612 &&
                    (previous == null || StringComparer.Ordinal.Compare(previous, key) < 0)); previous = key;
                row.Accepted.Add(new ObjectRef(kind, hash, bytes, received));
            }
        }
        long reservedBytes = value.GetProperty("reservedBytes").GetInt64(), stagingBytes = value.GetProperty("stagingBytes").GetInt64();
        if (row.Status == "staging")
        {
            long receivedBytes = row.Accepted.Where(x => x.Received).Sum(x => (long)x.Bytes), reusedBytes = row.Accepted.Where(x => !x.Received).Sum(x => (long)x.Bytes);
            long declaredBytes = N(begin.GetProperty("declared"), "bytes"), declaredObjects = N(begin.GetProperty("declared"), "objects");
            bool released = row.ObjectsRemaining == declaredObjects - row.Accepted.Count && stagingBytes == declaredBytes - reusedBytes;
            bool legacy = row.ObjectsRemaining == declaredObjects - row.Accepted.Count(x => x.Received) && stagingBytes == declaredBytes;
            Require(released || legacy); row.ReleasedReuseReservations = released;
            row.RawRemaining = declaredBytes - receivedBytes - (released ? reusedBytes : 0);
            row.MetadataRemaining = reservedBytes - row.RawRemaining;
            Require(row.RawRemaining >= 0 && row.MetadataRemaining >= 0 && row.MetadataRemaining <= MetadataReserve && row.ObjectsRemaining >= 0 && row.EntriesRemaining >= 0);
            Require(!row.Result.HasValue && !row.Rejection.HasValue);
        }
        else
        {
            Require(reservedBytes == 0 && row.ObjectsRemaining == 0 && row.EntriesRemaining == 0 && stagingBytes == 0 && row.Accepted.Count == 0 && !row.Base.HasValue);
            row.RawRemaining = row.MetadataRemaining = 0; row.ReleasedReuseReservations = true;
            Require(row.Status == "committed" ? row.Result.HasValue && !row.Rejection.HasValue : row.Status == "rejected" && !row.Result.HasValue && row.Rejection.HasValue);
        }
        if (row.Base.HasValue) ValidateRoot(row.Base.Value); if (row.Result.HasValue) ValidateRoot(row.Result.Value);
        row.AccountedBytes = Bytes(Json(w => { Prop(w, "begin", begin); Prop(w, "owner", checkedOwner); Nullable(w, "baseRoot", row.Base); Prop(w, "transfer", transfer); })).Length;
        return row;
    }
}
