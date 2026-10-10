using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteTerminalPersistenceStore
{
    private sealed class Plan
    {
        internal readonly List<ObjectRef?> Body = new();
        internal readonly List<JsonElement?> Entries = new();
        internal readonly Dictionary<string, ObjectRef> Objects = new(StringComparer.Ordinal);
        internal bool[] Pages = Array.Empty<bool>();
        internal bool CompletePages => Pages.All(x => x);
    }
    private List<ObjectRef> BodyReferences(JsonElement root)
    {
        var body = root.GetProperty("body"); var result = new List<ObjectRef>(); int pageIndex = 0;
        foreach (var hash in body.GetProperty("pageHashes").EnumerateArray())
        {
            string digest = hash.GetString()!; int bytes = checked((int)Number("SELECT bytes FROM objects WHERE kind='body-page' AND hash=$hash", ("$hash", digest)));
            Require(bytes >= 1 && bytes <= 12288); var raw = ReadObject(new ObjectRef("body-page", digest, bytes, false));
            var page = WireJson.DecodeControl(raw, 12288); TerminalPersistenceContract.Validate("BodyPage", page); Require(Bytes(page).SequenceEqual(raw) && N(page, "index") == pageIndex++);
            int expected = Math.Min(64, N(body, "blockCount") - result.Count); Require(page.GetProperty("refs").GetArrayLength() == expected);
            foreach (var item in page.GetProperty("refs").EnumerateArray())
            {
                int length = Math.Min(12288, N(body, "byteLength") - result.Count * 12288); Require(N(item, "byteLength") == length);
                result.Add(new ObjectRef("body-block", S(item, "sha256"), length, false));
            }
        }
        Require(result.Count == N(body, "blockCount")); return result;
    }
    private Plan Material(Ticket ticket)
    {
        var plan = new Plan(); var body = ticket.Request.GetProperty("body"); var index = ticket.Request.GetProperty("index");
        int bodyPages = body.GetProperty("pageHashes").GetArrayLength(), indexPages = index.GetProperty("pageHashes").GetArrayLength();
        plan.Pages = new bool[bodyPages + indexPages];
        plan.Body.AddRange(Enumerable.Repeat<ObjectRef?>(null, N(body, "blockCount")));
        plan.Entries.AddRange(Enumerable.Repeat<JsonElement?>(null, N(index, "entryCount")));
        void Add(ObjectRef reference)
        {
            if (plan.Objects.TryGetValue(reference.Key, out var prior)) Need(prior.Bytes == reference.Bytes, "invalid_request"); else plan.Objects.Add(reference.Key, reference);
        }
        foreach (string kind in new[] { "body-page", "index-page" })
        {
            bool isBody = kind == "body-page"; var hashes = (isBody ? body : index).GetProperty("pageHashes"); int position = 0;
            foreach (var hash in hashes.EnumerateArray())
            {
                string digest = hash.GetString()!; var reference = ticket.Accepted.FirstOrDefault(x => x.Kind == kind && x.Hash == digest);
                if (reference == null) { position++; continue; }
                var raw = ReadObject(reference); var page = WireJson.DecodeControl(raw, 12288);
                TerminalPersistenceContract.Validate(isBody ? "BodyPage" : "IndexPage", page);
                Need(Bytes(page).SequenceEqual(raw) && N(page, "index") == position, "invalid_request"); Add(reference); plan.Pages[(isBody ? 0 : bodyPages) + position] = true;
                var items = page.GetProperty(isBody ? "refs" : "entries"); int perPage = isBody ? 64 : 32, count = isBody ? plan.Body.Count : plan.Entries.Count;
                Need(items.GetArrayLength() == Math.Min(perPage, count - position * perPage), "invalid_request");
                int itemIndex = position * perPage;
                foreach (var item in items.EnumerateArray())
                {
                    if (isBody)
                    {
                        int length = Math.Min(12288, N(body, "byteLength") - itemIndex * 12288); Need(N(item, "byteLength") == length, "invalid_request");
                        var block = new ObjectRef("body-block", S(item, "sha256"), length, false); plan.Body[itemIndex] = block; Add(block);
                    }
                    else { plan.Entries[itemIndex] = item.Clone(); var value = item.GetProperty("value"); Add(new ObjectRef("receipt-value", S(value, "sha256"), N(value, "byteLength"), false)); }
                    itemIndex++;
                }
                position++;
            }
        }
        string? last = null; var secondaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in plan.Entries.Where(x => x.HasValue).Select(x => x!.Value))
        { string primary = S(entry, "primaryKey"); Need((last == null || string.CompareOrdinal(last, primary) < 0) && secondaries.Add(S(entry, "secondaryKey")), "request_conflict"); last = primary; }
        if (plan.CompletePages)
        {
            var declared = ticket.Request.GetProperty("declared");
            Need(plan.Objects.Count == N(declared, "objects") && plan.Objects.Values.Sum(x => (long)x.Bytes) == N(declared, "bytes"), "invalid_request");
        }
        return plan;
    }
    private void Reuse(Ticket ticket)
    {
        void Accept(ObjectRef reference)
        {
            var prior = ticket.Accepted.FirstOrDefault(x => x.Key == reference.Key);
            if (prior != null) { Need(prior.Bytes == reference.Bytes, "invalid_request"); return; }
            ReadObject(reference); ticket.Accepted.Add(reference);
        }
        List<ObjectRef> originals = new();
        if (ticket.Base.HasValue)
        {
            originals = BodyReferences(ticket.Base.Value);
            var originalPages = ticket.Base.Value.GetProperty("body").GetProperty("pageHashes");
            var wantedPages = ticket.Request.GetProperty("body").GetProperty("pageHashes");
            for (int p = 0; p < Math.Min(originalPages.GetArrayLength(), wantedPages.GetArrayLength()); p++)
                if (originalPages[p].GetString() == wantedPages[p].GetString())
                {
                    string hash = originalPages[p].GetString()!; int bytes = checked((int)Number("SELECT bytes FROM objects WHERE kind='body-page' AND hash=$hash", ("$hash", hash)));
                    Accept(new ObjectRef("body-page", hash, bytes, false));
                }
        }
        var plan = Material(ticket);
        foreach (var reference in plan.Body.Where(x => x != null).Select(x => x!))
            if (originals.Any(x => x.Hash == reference.Hash && x.Bytes == reference.Bytes)) Accept(reference);
        foreach (var entry in plan.Entries.Where(x => x.HasValue).Select(x => x!.Value))
        {
            var primary = FindEntry(S(entry, "primaryKey")); var secondary = FindEntry(S(entry, "secondaryKey"), true);
            if (primary == null && secondary == null) continue;
            Need(primary != null && secondary != null && primary.Ordinal == secondary.Ordinal && Equal(primary.Value, entry) && Equal(secondary.Value, entry), "request_conflict");
            var value = entry.GetProperty("value"); Accept(new ObjectRef("receipt-value", S(value, "sha256"), N(value, "byteLength"), false));
        }
    }
    private static byte[] Bits(IEnumerable<bool> values)
    {
        var list = values.ToArray(); var bytes = new byte[(list.Length + 7) / 8];
        for (int n = 0; n < list.Length; n++) if (list[n]) bytes[n / 8] |= (byte)(1 << (n % 8)); return bytes;
    }
    private JsonElement Transfer(JsonElement request, Ticket? ticket)
    {
        JsonElement? progress = null;
        if (ticket?.Status == "staging")
        {
            var plan = Material(ticket); var accepted = ticket.Accepted.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            progress = Json(w =>
            {
                w.WriteString("pagesReady", Convert.ToBase64String(Bits(plan.Pages)));
                w.WriteString("bodyReady", Convert.ToBase64String(Bits(plan.Body.Select(x => x != null && accepted.Contains(x.Key)))));
                w.WriteString("valuesReady", Convert.ToBase64String(Bits(plan.Entries.Select(x => x.HasValue && accepted.Contains("receipt-value:" + S(x.Value.GetProperty("value"), "sha256"))))));
                w.WriteNumber("receivedBytes", ticket.Accepted.Where(x => x.Received).Sum(x => (long)x.Bytes));
            });
        }
        return Json(w =>
        {
            w.WriteString("transferId", S(request, "transferId")); w.WriteString("intentSha256", S(request, "intentSha256"));
            w.WriteString("status", ticket?.Status ?? "unknown"); Nullable(w, "progress", progress); Nullable(w, "result", ticket?.Result); Nullable(w, "rejection", ticket?.Rejection);
        });
    }
    private JsonElement Response(JsonElement request, Action<Utf8JsonWriter> write)
    {
        var value = Json(w => { foreach (string name in new[] { "contract", "sourceId", "sourceGeneration", "domainKey", "action" }) Prop(w, name, request.GetProperty(name)); write(w); });
        TerminalPersistenceContract.Copy("Response", value); return value;
    }
    private static bool Expected(JsonElement? root, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.Null) return !root.HasValue;
        if (!root.HasValue) return false;
        var value = root.Value;
        return S(expected, "commitRoot") == S(value, "commitRoot") && S(expected, "generation") == S(value, "generation") && S(expected, "bodyEtag") == S(value.GetProperty("body"), "sha256") &&
            S(expected, "indexRoot") == S(value.GetProperty("index"), "root") && S(expected, "indexCount") == S(value.GetProperty("index"), "count");
    }
    private static long ObjectMetadataCost(ObjectRef reference) => Bytes(Json(w =>
    { w.WriteString("kind", reference.Kind); w.WriteString("sha256", reference.Hash); w.WriteNumber("byteLength", reference.Bytes); })).Length;
    private static long EntryCost(long ordinal, JsonElement entry) => Bytes(Json(w =>
    { w.WriteString("ordinal", ordinal.ToString(CultureInfo.InvariantCulture)); Prop(w, "entry", entry); })).Length;
    private long TicketCost(Ticket ticket) => Bytes(Json(w =>
    { Prop(w, "begin", ticket.Request); Prop(w, "owner", Owner(ticket.Owner)); Nullable(w, "baseRoot", ticket.Base); Prop(w, "transfer", Transfer(ticket.Request, ticket)); })).Length;
    private static void ChargeMetadata(State state, Ticket ticket, long delta)
    {
        Require(delta <= ticket.MetadataRemaining); ticket.MetadataRemaining -= delta;
        state.Used["reservedBytes"] -= delta; state.Used["retainedBytes"] += delta;
    }
    private void AccountTicket(State state, Ticket ticket)
    {
        long cost = TicketCost(ticket); ChargeMetadata(state, ticket, cost - ticket.AccountedBytes); ticket.AccountedBytes = cost;
    }
    private void SaveProgress(State state, Ticket ticket)
    { AccountTicket(state, ticket); SaveTicket(ticket); SaveState(state); }
    private void Finish(State state, Ticket ticket, JsonElement? result, string? rejection)
    {
        ticket.Status = rejection == null ? "committed" : "rejected"; ticket.Result = result;
        ticket.Rejection = rejection == null ? null : Json(w => { w.WriteString("code", rejection); Nullable(w, "observedRoot", state.Root); });
        ticket.Accepted.Clear(); ticket.Base = null; AccountTicket(state, ticket);
        state.Used["activeTransfers"]--; state.Used["stagingBytes"] -= N(ticket.Request.GetProperty("declared"), "bytes");
        state.Used["reservedBytes"] -= ticket.RawRemaining + ticket.MetadataRemaining;
        state.Used["reservedObjects"] -= ticket.ObjectsRemaining; state.Used["reservedReceiptEntries"] -= ticket.EntriesRemaining;
        ticket.RawRemaining = ticket.MetadataRemaining = ticket.ObjectsRemaining = ticket.EntriesRemaining = 0;
        ticket.Accepted.Clear(); ticket.Base = null; SaveTicket(ticket); SaveState(state);
    }
    private static long PhysicalBudget(JsonElement begin)
    {
        // Conservative max_page_count admission, not a promise of filesystem or WAL free space.
        var declared = begin.GetProperty("declared");
        return checked((N(declared, "bytes") * 2L + MetadataReserve * 4L + 4095) / 4096 +
            N(declared, "objects") * 16L + N(begin.GetProperty("index"), "addedCount") * 32L + 64);
    }
    private void ReservePhysical(State state, JsonElement request)
    {
        long allocated = Number("PRAGMA page_count"), reusable = Number("PRAGMA freelist_count");
        Require(Number("PRAGMA page_size") == 4096 && allocated >= reusable && reusable >= 0);
        Require(Number("PRAGMA max_page_count") == _options.MaxPages, "capacity_exceeded");
        long reserved = 0, active = 0;
        using (var command = Command("SELECT id FROM transfers WHERE status='staging' LIMIT 33"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                Require(++active <= 32); var ticket = LoadTicket(reader.GetString(0));
                Require(ticket != null && ticket.Status == "staging"); reserved = checked(reserved + PhysicalBudget(ticket!.Request));
            }
        Require(active == state.Used["activeTransfers"]);
        Need(allocated - reusable + reserved + PhysicalBudget(request) <= _options.MaxPages, "capacity_exceeded");
    }
    private void Begin(State state, JsonElement request, string owner)
    {
        var declared = request.GetProperty("declared"); long bytes = N(declared, "bytes"), objects = N(declared, "objects"), added = N(request.GetProperty("index"), "addedCount");
        Need(state.Used["activeTransfers"] < _options.MaxActiveTransfers && state.Used["transferFacts"] < _options.MaxTransferFacts &&
            state.Used["stagingBytes"] + bytes <= _options.MaxStagingBytes && state.Used["objects"] + state.Used["reservedObjects"] + objects <= _options.MaxObjects &&
            state.Used["receiptEntries"] + state.Used["reservedReceiptEntries"] + added <= _options.MaxReceiptEntries &&
            state.Used["retainedBytes"] + state.Used["reservedBytes"] + bytes + MetadataReserve <= _options.MaxRetainedBytes, "capacity_exceeded");
        ReservePhysical(state, request);
        var ticket = new Ticket { Id = S(request, "transferId"), Request = request, Owner = owner, Base = state.Root, RawRemaining = bytes, ObjectsRemaining = objects, EntriesRemaining = added };
        state.Used["activeTransfers"]++; state.Used["transferFacts"]++; state.Used["stagingBytes"] += bytes;
        state.Used["reservedBytes"] += bytes + MetadataReserve; state.Used["reservedObjects"] += objects; state.Used["reservedReceiptEntries"] += added;
        if (!Expected(state.Root, request.GetProperty("expected"))) { Finish(state, ticket, null, "revision_conflict"); return; }
        try { Reuse(ticket); }
        catch (MemoryPublicationRejectedException error) { Finish(state, ticket, null, error.Code); return; }
        catch (WireProtocolException) { Finish(state, ticket, null, "invalid_request"); return; }
        SaveProgress(state, ticket);
    }
    private void Put(State state, Ticket ticket, JsonElement request)
    {
        if (ticket.Status != "staging") return;
        string kind = S(request, "kind"), hash = S(request, "sha256"); var bytes = TerminalPersistenceContract.Payload(request, "sha256");
        var reference = new ObjectRef(kind, hash, bytes.Length, true);
        var prior = ticket.Accepted.FirstOrDefault(x => x.Key == reference.Key);
        if (prior != null) { Need(prior.Bytes == bytes.Length && ReadObject(prior).SequenceEqual(bytes), "request_conflict"); return; }
        var plan = Material(ticket);
        if (kind == "body-page" || kind == "index-page")
        {
            var hashes = ticket.Request.GetProperty(kind == "body-page" ? "body" : "index").GetProperty("pageHashes");
            Need(hashes.EnumerateArray().Any(x => x.GetString() == hash), "request_conflict");
        }
        else Need(plan.Objects.TryGetValue(reference.Key, out var declared) && declared.Bytes == reference.Bytes, "request_conflict");
        Need(ticket.RawRemaining >= bytes.Length && ticket.ObjectsRemaining > 0, "invalid_request");
        bool added = AddObject(reference, bytes); ticket.Accepted.Add(reference);
        ticket.RawRemaining -= bytes.Length; ticket.ObjectsRemaining--; state.Used["reservedBytes"] -= bytes.Length; state.Used["reservedObjects"]--;
        if (added)
        {
            ChargeMetadata(state, ticket, ObjectMetadataCost(reference));
            state.Used["objects"]++; state.Used["retainedBytes"] += bytes.Length;
        }
        try { Reuse(ticket); }
        catch (MemoryPublicationRejectedException error) { Finish(state, ticket, null, error.Code); return; }
        catch (WireProtocolException) { Finish(state, ticket, null, "invalid_request"); return; }
        SaveProgress(state, ticket);
    }
    private void Commit(State state, Ticket ticket)
    {
        if (ticket.Status != "staging") return;
        if (!Expected(state.Root, ticket.Request.GetProperty("expected"))) { Finish(state, ticket, null, "revision_conflict"); return; }
        var plan = Material(ticket); Need(plan.CompletePages, "invalid_request");
        var accepted = ticket.Accepted.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        Need(plan.Objects.Values.All(x => accepted.Contains(x.Key)), "invalid_request");
        using var sha = SHA256.Create(); long bodyLength = 0;
        foreach (var reference in plan.Body)
        { var bytes = ReadObject(reference!); sha.TransformBlock(bytes, 0, bytes.Length, null, 0); bodyLength += bytes.Length; }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var body = ticket.Request.GetProperty("body"); Need(bodyLength == N(body, "byteLength") && Hex(sha.Hash!) == S(body, "sha256"));
        var additions = new List<JsonElement>();
        foreach (var entry in plan.Entries.Select(x => x!.Value))
        {
            var a = FindEntry(S(entry, "primaryKey")); var b = FindEntry(S(entry, "secondaryKey"), true);
            if (a == null && b == null) additions.Add(entry);
            else if (a == null || b == null || a.Ordinal != b.Ordinal || !Equal(a.Value, entry) || !Equal(b.Value, entry)) { Finish(state, ticket, null, "request_conflict"); return; }
            var value = entry.GetProperty("value"); ReadObject(new ObjectRef("receipt-value", S(value, "sha256"), N(value, "byteLength"), false));
        }
        if (additions.Count != N(ticket.Request.GetProperty("index"), "addedCount")) { Finish(state, ticket, null, "request_conflict"); return; }
        long generation = state.Root.HasValue ? long.Parse(S(state.Root.Value, "generation"), CultureInfo.InvariantCulture) : 0;
        Need(generation < long.MaxValue, "capacity_exceeded");
        string indexRoot = state.Root.HasValue ? S(state.Root.Value.GetProperty("index"), "root") : EmptyIndex;
        long count = state.Used["receiptEntries"];
        foreach (var entry in additions)
        {
            count++; long charge = EntryCost(count, entry); Require(ticket.MetadataRemaining >= charge);
            InsertEntry(count, entry); indexRoot = Chain("TPV1-ENTRY", Literal(indexRoot), Literal(count.ToString(CultureInfo.InvariantCulture)), entry);
            ticket.MetadataRemaining -= charge; state.Used["reservedBytes"] -= charge; state.Used["retainedBytes"] += charge;
            ticket.EntriesRemaining--; state.Used["reservedReceiptEntries"]--; state.Used["receiptEntries"]++;
        }
        var content = Json(w => { w.WriteString("generation", (generation + 1).ToString(CultureInfo.InvariantCulture)); Prop(w, "body", body); w.WriteStartObject("index"); w.WriteString("root", indexRoot); w.WriteString("count", count.ToString(CultureInfo.InvariantCulture)); w.WriteEndObject(); });
        var root = Json(w => { w.WriteString("commitRoot", Chain("TPV1-ROOT", _options.Identity, content)); foreach (var property in content.EnumerateObject()) property.WriteTo(w); });
        ChargeMetadata(state, ticket, Bytes(root).Length - (state.Root.HasValue ? Bytes(state.Root.Value).Length : 0));
        state.Root = root; Finish(state, ticket, root, null);
    }
    private static string Hex(byte[] value) => string.Concat(value.Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
}
