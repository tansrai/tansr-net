using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    public Task<JsonElement> ExecuteAsync(JsonElement request, string ownerCanonical, CancellationToken cancellationToken = default)
    {
        JsonElement fixedRequest = default, owner = default;
        return Task.FromResult(Run(check =>
        {
            Need(Equal(owner.GetProperty("scope"), Scope(_options.ReadContext, _options.Identity)), "request_conflict"); check();
            string action = S(fixedRequest, "action"); var state = LoadState();
            // An authenticated copy cannot become a writer merely by being reopened with the same owner.
            Require(!state.ReadOnlyCopy || action == "head" || action == "read" || action == "lookup" || action == "query", "read_only_copy");
            if (action == "head") return Response(fixedRequest, w =>
            { Nullable(w, "root", state.Root); w.WriteStartObject("capacity"); Prop(w, "limits", Limits(_options)); Prop(w, "used", Used(state)); w.WriteEndObject(); });
            if (action == "read" || action == "lookup")
            {
                Need(state.Root.HasValue && S(state.Root.Value, "commitRoot") == S(fixedRequest, "commitRoot"), "revision_conflict"); var root = state.Root!.Value;
                if (action == "lookup")
                {
                    var key = fixedRequest.GetProperty("key"); var entry = FindEntry(S(key, "digest"), S(key, "kind") == "secondary");
                    return Response(fixedRequest, w =>
                    {
                        w.WriteString("commitRoot", S(root, "commitRoot")); w.WriteString("indexRoot", S(root.GetProperty("index"), "root")); w.WriteString("indexCount", S(root.GetProperty("index"), "count"));
                        w.WritePropertyName("entry");
                        if (entry == null) w.WriteNullValue();
                        else
                        {
                            Require(entry.Ordinal <= state.Used["receiptEntries"]); var value = entry.Value.GetProperty("value"); var raw = ReadObject(new ObjectRef("receipt-value", S(value, "sha256"), N(value, "byteLength"), false));
                            w.WriteStartObject(); foreach (var property in entry.Value.EnumerateObject()) property.WriteTo(w); w.WriteString("base64", Convert.ToBase64String(raw)); w.WriteEndObject();
                        }
                    });
                }
                string part = S(fixedRequest, "part"); var body = root.GetProperty("body");
                if (part == "body-page")
                {
                    int p = N(fixedRequest, "pageIndex"); var hashes = body.GetProperty("pageHashes"); Need(p < hashes.GetArrayLength(), "invalid_request"); string hash = hashes[p].GetString()!;
                    int length = checked((int)Number("SELECT bytes FROM objects WHERE kind='body-page' AND hash=$hash", ("$hash", hash))); Require(length >= 1 && length <= 12288);
                    var raw = ReadObject(new ObjectRef("body-page", hash, length, false));
                    return Response(fixedRequest, w => { w.WriteString("part", part); w.WriteString("commitRoot", S(root, "commitRoot")); w.WriteNumber("pageIndex", p); WritePayload(w, raw); });
                }
                int offset = N(fixedRequest, "offset"), requested = N(fixedRequest, "length"), total = N(body, "byteLength"); Need(offset <= total, "invalid_request");
                var bytes = new byte[Math.Min(requested, total - offset)]; var references = BodyReferences(root); int copied = 0;
                while (copied < bytes.Length)
                {
                    int block = (offset + copied) / 12288, local = (offset + copied) % 12288; var raw = ReadObject(references[block]); int length = Math.Min(bytes.Length - copied, raw.Length - local);
                    Require(length > 0); Buffer.BlockCopy(raw, local, bytes, copied, length); copied += length;
                }
                return Response(fixedRequest, w =>
                {
                    w.WriteString("part", part); w.WriteString("commitRoot", S(root, "commitRoot")); w.WriteString("bodyEtag", S(body, "sha256")); w.WriteNumber("offset", offset);
                    WritePayload(w, bytes); w.WriteNumber("nextOffset", offset + bytes.Length); w.WriteBoolean("complete", offset + bytes.Length == total);
                });
            }
            string id = S(fixedRequest, "transferId"); var ticket = LoadTicket(id);
            if (ticket != null)
            {
                Need(S(ticket.Request, "intentSha256") == S(fixedRequest, "intentSha256"), "request_conflict");
                if (action == "begin") Need(Equal(ticket.Request, fixedRequest), "request_conflict");
                if (ticket.Owner != ownerCanonical)
                {
                    Need(action == "query" && _options.AuthorizeRecovery != null, "request_conflict");
                    bool authorized = _options.AuthorizeRecovery!(new TerminalPersistenceRecoveryContext(_options.Identity, id, Owner(ticket.Owner), owner)); check(); Need(authorized, "request_conflict");
                }
            }
            if (action != "query" && (action == "begin" && ticket == null || ticket?.Status == "staging"))
                Transaction(() =>
                {
                    // Same exclusive connection; re-read the authenticated root/counters at the mutation boundary.
                    state = LoadState();
                    if (action == "begin") { if (ticket == null) Begin(state, fixedRequest, ownerCanonical); }
                    else if (action == "put") Put(state, ticket!, fixedRequest);
                    else Commit(state, ticket!);
                }, check);
            return Response(fixedRequest, w => Prop(w, "transfer", Transfer(fixedRequest, LoadTicket(id))));
        }, cancellationToken, () =>
        {
            try { fixedRequest = TerminalPersistenceContract.Copy("Request", request); owner = Owner(ownerCanonical); }
            catch (WireProtocolException) { throw new MemoryPublicationRejectedException("invalid_request"); }
            Need(new[] { "sourceId", "sourceGeneration", "domainKey" }.All(name => S(fixedRequest, name) == S(_options.Identity, name)), "stale_generation");
            Need(S(owner.GetProperty("scope"), "applicationScopeId") == S(_options.Identity, "applicationScopeId") && S(owner.GetProperty("scope"), "endUserId") == S(_options.Identity, "endUserId"), "request_conflict");
        }));
    }
    private static void WritePayload(Utf8JsonWriter w, byte[] bytes)
    { w.WriteNumber("byteLength", bytes.Length); w.WriteString("base64", Convert.ToBase64String(bytes)); w.WriteString("payloadDigest", WireJson.Sha256(bytes)); }

    // Bounded-memory startup audit. Permanent rows are streamed by their real indexes; this is not an O(1) startup claim.
    private void Audit()
    {
        var state = LoadState(); var counts = new State(); string index = EmptyIndex; long ordinal = 0;
        Require(Number("SELECT count(*) FROM transfers") == state.Used["transferFacts"] && Number("SELECT count(*) FROM entries") == state.Used["receiptEntries"] && Number("SELECT count(*) FROM objects") == state.Used["objects"]);
        using (var command = Command("SELECT primary_key,ordinal FROM entries ORDER BY ordinal")) using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var entry = FindEntry(reader.GetString(0)); Require(entry != null && entry.Ordinal == ++ordinal && reader.GetInt64(1) == ordinal);
                index = Chain("TPV1-ENTRY", Literal(index), Literal(ordinal.ToString(CultureInfo.InvariantCulture)), entry!.Value);
                var value = entry.Value.GetProperty("value"); ReadObject(new ObjectRef("receipt-value", S(value, "sha256"), N(value, "byteLength"), false));
                counts.Used["receiptEntries"]++; counts.Used["retainedBytes"] += EntryCost(ordinal, entry.Value);
            }
        }
        Require(state.Root.HasValue ? S(state.Root.Value.GetProperty("index"), "count") == ordinal.ToString(CultureInfo.InvariantCulture) && S(state.Root.Value.GetProperty("index"), "root") == index : ordinal == 0);
        using (var command = Command("SELECT kind,hash,bytes FROM objects ORDER BY kind,hash")) using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string kind = reader.GetString(0); int length = reader.GetInt32(2);
                Require(new[] { "body-page", "index-page", "body-block", "receipt-value" }.Contains(kind) && length >= 1 && length <= 12288);
                ReadObject(new ObjectRef(kind, reader.GetString(1), length, false)); counts.Used["objects"]++; counts.Used["retainedBytes"] += length + ObjectMetadataCost(new ObjectRef(kind, reader.GetString(1), length, false));
            }
        }
        using (var command = Command("SELECT id FROM transfers ORDER BY id")) using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var ticket = LoadTicket(reader.GetString(0))!; counts.Used["transferFacts"]++;
                long ticketCost = TicketCost(ticket); Require(ticketCost == ticket.AccountedBytes); counts.Used["retainedBytes"] += ticketCost;
                if (ticket.Status == "staging")
                {
                    var plan = Material(ticket); foreach (var reference in ticket.Accepted) ReadObject(reference);
                    Require(ticket.RawRemaining + ticket.Accepted.Where(x => x.Received).Sum(x => (long)x.Bytes) == N(ticket.Request.GetProperty("declared"), "bytes"));
                    Require(ticket.ObjectsRemaining + ticket.Accepted.Count(x => x.Received) == N(ticket.Request.GetProperty("declared"), "objects"));
                    counts.Used["activeTransfers"]++; counts.Used["stagingBytes"] += N(ticket.Request.GetProperty("declared"), "bytes");
                    counts.Used["reservedBytes"] += ticket.RawRemaining + ticket.MetadataRemaining;
                    counts.Used["reservedObjects"] += ticket.ObjectsRemaining; counts.Used["reservedReceiptEntries"] += ticket.EntriesRemaining;
                }
                else
                {
                    Require(ticket.RawRemaining == 0 && ticket.MetadataRemaining == 0 && ticket.ObjectsRemaining == 0 && ticket.EntriesRemaining == 0 && ticket.Accepted.Count == 0 && !ticket.Base.HasValue);
                }
            }
        }
        if (state.Root.HasValue) counts.Used["retainedBytes"] += Bytes(state.Root.Value).Length;
        foreach (var name in CounterNames) Require(counts.Used[name] == state.Used[name]);
        if (state.Root.HasValue)
        {
            using var sha = SHA256.Create(); long length = 0;
            foreach (var reference in BodyReferences(state.Root.Value)) { var bytes = ReadObject(reference); sha.TransformBlock(bytes, 0, bytes.Length, null, 0); length += bytes.Length; }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); var body = state.Root.Value.GetProperty("body"); Require(N(body, "byteLength") == length && S(body, "sha256") == Hex(sha.Hash!));
        }
    }
}
