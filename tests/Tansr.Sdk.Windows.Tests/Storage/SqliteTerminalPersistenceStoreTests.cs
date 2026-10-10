using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteTerminalPersistenceStoreTests
{
    [Fact]
    public async Task AtomicRootIndexOriginalTerminalAndEncryptedReopen()
    {
        using var f = new Fixture(); var bytes = Encoding.UTF8.GetBytes("opaque synthetic body 中文"); var value = Encoding.UTF8.GetBytes("original permanent receipt");
        var plan = f.Plan("one", bytes, value);
        JsonElement committed, root;
        using (var store = await SqliteTerminalPersistenceStore.OpenAsync(f.Options()))
        {
            await f.Stage(store, plan); committed = await f.Call(store, plan.Action("commit"));
            Assert.Equal("committed", committed.GetProperty("transfer").GetProperty("status").GetString());
            root = (await f.Call(store, f.Request("head"))).GetProperty("root").Clone();
            Assert.Equal("1", root.GetProperty("index").GetProperty("count").GetString());
            Assert.Equal(Text(committed), Text(await f.Call(store, plan.Action("commit"))));
        }
        byte[] media = File.ReadAllBytes(f.Path); Assert.False(Contains(media, bytes)); Assert.False(Contains(media, value)); Assert.False(Contains(media, f.Key.Bytes));
        using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(f.Options(StorageOpenMode.Reopen));
        Assert.Equal(Text(committed.GetProperty("transfer")), Text((await f.Call(reopened, plan.Action("query"))).GetProperty("transfer")));
        var read = await f.Call(reopened, f.Request("read", new { commitRoot = root.GetProperty("commitRoot").GetString(), part = "body", offset = 0, length = 12288 }));
        Assert.Equal(bytes, Convert.FromBase64String(read.GetProperty("base64").GetString()!));
        foreach (string kind in new[] { "primary", "secondary" })
        {
            var lookup = await f.Call(reopened, f.Request("lookup", new { commitRoot = root.GetProperty("commitRoot").GetString(), key = new { kind, digest = new string(kind == "primary" ? 'a' : 'b', 64) } }));
            Assert.Equal(value, Convert.FromBase64String(lookup.GetProperty("entry").GetProperty("base64").GetString()!));
        }
    }

    [Fact]
    public async Task ActiveCapOriginalQueryAndQueryOnlyRecoveryDoNotGrantWrites()
    {
        using var f = new Fixture(); var options = f.Options(); options.MaxActiveTransfers = 1; options.AuthorizeRecovery = _ => true;
        var plan = f.Plan("one", new byte[] { 1 }, new byte[] { 2 });
        using (var store = await SqliteTerminalPersistenceStore.OpenAsync(options))
        {
            await f.Call(store, plan.Begin); var other = f.Plan("two", new byte[] { 1 }, new byte[] { 2 });
            Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<MemoryPublicationRejectedException>(() => f.Call(store, other.Begin))).Code);
            Assert.Equal("unknown", (await f.Call(store, other.Action("query"))).GetProperty("transfer").GetProperty("status").GetString());
        }
        f.Revision = "2"; options.Mode = StorageOpenMode.Reopen;
        using var reopen = await SqliteTerminalPersistenceStore.OpenAsync(options);
        Assert.Equal("staging", (await f.Call(reopen, plan.Action("query"))).GetProperty("transfer").GetProperty("status").GetString());
        Assert.Equal("request_conflict", (await Assert.ThrowsAsync<MemoryPublicationRejectedException>(() => f.Call(reopen, plan.Action("commit")))).Code);
        Assert.Equal(JsonValueKind.Null, (await f.Call(reopen, f.Request("head"))).GetProperty("root").ValueKind);
    }

    [Fact]
    public async Task DeltaReusesOnlyProtectedBaseAndPermanentDualKeyReceipt()
    {
        using var f = new Fixture(); using var store = await SqliteTerminalPersistenceStore.OpenAsync(f.Options());
        var body = Encoding.UTF8.GetBytes("unchanged original body"); var receipt = Encoding.UTF8.GetBytes("same receipt");
        var first = f.Plan("one", body, receipt); await f.Stage(store, first); await f.Call(store, first.Action("commit"));
        var root = (await f.Call(store, f.Request("head"))).GetProperty("root");
        var next = f.Plan("two", body, receipt, root, added: 0);
        await f.Call(store, next.Begin);
        // Unchanged base body page/block and exact permanent receipt are reused; only this plan's index page is sent.
        foreach (var item in next.Objects.Where(x => x.Kind == "index-page")) await f.Call(store, next.Put(item));
        var result = await f.Call(store, next.Action("commit")); Assert.Equal("committed", result.GetProperty("transfer").GetProperty("status").GetString());
        var after = (await f.Call(store, f.Request("head"))).GetProperty("root"); Assert.Equal("2", after.GetProperty("generation").GetString());
        Assert.Equal("1", after.GetProperty("index").GetProperty("count").GetString());
        Assert.Equal(Text(root.GetProperty("index")), Text(after.GetProperty("index")));
    }

    [Fact]
    public async Task CapacityCountsCanonicalObjectsEntriesTicketsAndRootExactly()
    {
        using var f = new Fixture(); var plan = f.Plan("capacity", Encoding.UTF8.GetBytes("body"), Encoding.UTF8.GetBytes("receipt"));
        using (var store = await SqliteTerminalPersistenceStore.OpenAsync(f.Options()))
        {
            var begun = await f.Call(store, plan.Begin);
            long pending = Canon(Json(new { begin = plan.Begin, owner = WireJson.Parse(Encoding.UTF8.GetBytes(f.Owner)), baseRoot = (object?)null, transfer = begun.GetProperty("transfer") })).Length;
            var head = await f.Call(store, f.Request("head")); var used = head.GetProperty("capacity").GetProperty("used");
            Assert.Equal(pending, used.GetProperty("retainedBytes").GetInt64());
            Assert.Equal(plan.Begin.GetProperty("declared").GetProperty("bytes").GetInt64() + 262144 - pending, used.GetProperty("reservedBytes").GetInt64());
            await f.Stage(store, plan); var committed = await f.Call(store, plan.Action("commit"));
            head = await f.Call(store, f.Request("head")); var root = head.GetProperty("root");
            long objects = plan.Objects.Sum(x => x.Bytes.Length + (long)Canon(Json(new { kind = x.Kind, sha256 = WireJson.Sha256(x.Bytes), byteLength = x.Bytes.Length })).Length);
            var entry = WireJson.Parse(plan.Objects.Single(x => x.Kind == "index-page").Bytes).GetProperty("entries")[0];
            long expected = objects + Canon(Json(new { ordinal = "1", entry })).Length + Canon(root).Length +
                Canon(Json(new { begin = plan.Begin, owner = WireJson.Parse(Encoding.UTF8.GetBytes(f.Owner)), baseRoot = (object?)null, transfer = committed.GetProperty("transfer") })).Length;
            used = head.GetProperty("capacity").GetProperty("used");
            Assert.Equal(expected, used.GetProperty("retainedBytes").GetInt64()); Assert.Equal(0, used.GetProperty("reservedBytes").GetInt64());
        }
        using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(f.Options(StorageOpenMode.Reopen));
        Assert.Equal("committed", (await f.Call(reopened, plan.Action("query"))).GetProperty("transfer").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ExplicitRekeyCopiesOriginalCompletedAndPendingFactsWithoutCutover()
    {
        using var f = new Fixture(); var original = f.Plan("completed", new byte[] { 1, 2 }, new byte[] { 3 });
        var destination = System.IO.Path.Combine(f.Directory, "rekey.sqlite"); var staging = System.IO.Path.Combine(f.Directory, "rekey-staging.sqlite");
        var key = new Key { Bytes = Enumerable.Range(32, 32).Select(x => (byte)x).ToArray() };
        JsonElement expected;
        using (var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options()))
        {
            await f.Stage(source, original); await f.Call(source, original.Action("commit"));
            var root = (await f.Call(source, f.Request("head"))).GetProperty("root");
            var pending = f.Plan("pending", new byte[] { 1, 2 }, new byte[] { 3 }, root, 0);
            await f.Call(source, pending.Begin); expected = await f.Call(source, f.Request("head"));
            await source.CopyToEncryptedAsync(destination, staging, key);
            Assert.True(File.Exists(f.Path)); Assert.True(File.Exists(destination)); Assert.False(File.Exists(staging));
            Assert.Equal(Text(expected), Text(await f.Call(source, f.Request("head"))));
            var options = f.Options(StorageOpenMode.Reopen); options.Path = destination; options.KeyProvider = key;
            using var copied = await SqliteTerminalPersistenceStore.OpenAsync(options);
            Assert.Equal(Text(expected), Text(await f.Call(copied, f.Request("head"))));
            Assert.Equal(Text(await f.Call(source, original.Action("query"))), Text(await f.Call(copied, original.Action("query"))));
            Assert.Equal(Text(await f.Call(source, pending.Action("query"))), Text(await f.Call(copied, pending.Action("query"))));
            await Assert.ThrowsAsync<StorageException>(() => f.Call(copied, pending.Begin));
            await Assert.ThrowsAsync<StorageException>(() => f.Call(copied, pending.Put(pending.Objects[0])));
            await Assert.ThrowsAsync<StorageException>(() => f.Call(copied, original.Action("commit")));
            Assert.Equal(Text(expected), Text(await f.Call(copied, f.Request("head"))));
            await Assert.ThrowsAsync<StorageException>(() => source.CopyToEncryptedAsync(destination, staging, key));
        }
        var wrong = f.Options(StorageOpenMode.Reopen); wrong.Path = destination;
        await Assert.ThrowsAsync<StorageException>(() => SqliteTerminalPersistenceStore.OpenAsync(wrong));
        using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(f.Options(StorageOpenMode.Reopen));
        Assert.Equal(Text(expected), Text(await f.Call(reopened, f.Request("head"))));
    }

    [Fact]
    public async Task PhysicalAdmissionRejectsNewTicketAndKeepsAcceptedPlanAcrossReopen()
    {
        using (var small = new Fixture())
        {
            var options = small.Options(); options.MaxPages = 64;
            using var store = await SqliteTerminalPersistenceStore.OpenAsync(options);
            var plan = small.Plan("no-physical-budget", new byte[] { 1, 2 }, new byte[] { 3 });
            var error = await Assert.ThrowsAsync<MemoryPublicationRejectedException>(() => small.Call(store, plan.Begin));
            Assert.Equal("capacity_exceeded", error.Code);
            Assert.Equal("unknown", (await small.Call(store, plan.Action("query"))).GetProperty("transfer").GetProperty("status").GetString());
        }
        using var f = new Fixture(); var first = f.Plan("first", new byte[] { 4, 5 }, new byte[] { 6 });
        JsonElement accepted;
        using (var store = await SqliteTerminalPersistenceStore.OpenAsync(Options(StorageOpenMode.Create)))
        {
            await f.Call(store, first.Begin); accepted = await f.Call(store, first.Action("query"));
            var competing = f.Plan("second", new byte[] { 7, 8 }, new byte[] { 9 });
            var error = await Assert.ThrowsAsync<MemoryPublicationRejectedException>(() => f.Call(store, competing.Begin));
            Assert.Equal("capacity_exceeded", error.Code);
            Assert.Equal(Text(accepted), Text(await f.Call(store, first.Action("query"))));
            Assert.Equal("unknown", (await f.Call(store, competing.Action("query"))).GetProperty("transfer").GetProperty("status").GetString());
        }
        using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(Options(StorageOpenMode.Reopen));
        Assert.Equal(Text(accepted), Text(await f.Call(reopened, first.Action("query"))));
        foreach (var item in first.Objects) await f.Call(reopened, first.Put(item));
        Assert.Equal("committed", (await f.Call(reopened, first.Action("commit"))).GetProperty("transfer").GetProperty("status").GetString());
        SqliteTerminalPersistenceOptions Options(StorageOpenMode mode) { var result = f.Options(mode); result.MaxPages = 480; return result; }
    }

    private static bool Contains(byte[] bytes, byte[] needle) => Enumerable.Range(0, Math.Max(0, bytes.Length - needle.Length + 1)).Any(i => bytes.Skip(i).Take(needle.Length).SequenceEqual(needle));
    private static JsonElement Json(object? value) => WireJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value), 262144);
    private static string Text(JsonElement value) => WireJson.CanonicalString(value, 262144);
    private static byte[] Canon(JsonElement value) => WireJson.EncodeControl(value, 262144);
    private sealed class Key : IArchiveKeyProvider { public string KeyId => "synthetic-tpv1"; public byte[] Bytes = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray(); public byte[] ReadKey() => Bytes.ToArray(); }
    private sealed class ObjectData { internal string Kind; internal byte[] Bytes; internal ObjectData(string kind, byte[] bytes) { Kind = kind; Bytes = bytes; } }
    private sealed class TransferPlan
    {
        internal Fixture F = null!; internal JsonElement Begin; internal List<ObjectData> Objects = new();
        internal JsonElement Action(string action) => F.Request(action, new { transferId = Begin.GetProperty("transferId").GetString(), intentSha256 = Begin.GetProperty("intentSha256").GetString() });
        internal JsonElement Put(ObjectData item) => F.Request("put", new { transferId = Begin.GetProperty("transferId").GetString(), intentSha256 = Begin.GetProperty("intentSha256").GetString(), kind = item.Kind, sha256 = WireJson.Sha256(item.Bytes), byteLength = item.Bytes.Length, base64 = Convert.ToBase64String(item.Bytes) });
    }
    private sealed class Fixture : IDisposable
    {
        internal string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pst-net-v1-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "profile.sqlite"); internal Key Key = new(); internal string Revision = "1";
        internal JsonElement Identity => Json(new { applicationScopeId = "app", endUserId = "user", sourceId = "source", sourceGeneration = "1", domainKey = "domain" });
        internal JsonElement Scope => Json(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = Revision });
        internal string Owner => Text(Json(new { scope = Scope, sessionId = "session", binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } } }));
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal SqliteTerminalPersistenceOptions Options(StorageOpenMode mode = StorageOpenMode.Create) => new() { EnableProfile = true, Path = Path, Mode = mode, Identity = Identity, ReadContext = () => Scope, KeyProvider = Key };
        internal JsonElement Request(string action, object? extra = null)
        { var fields = new Dictionary<string, JsonElement> { ["contract"] = Json("terminal-persistence-v1"), ["action"] = Json(action) }; foreach (string field in new[] { "sourceId", "sourceGeneration", "domainKey" }) fields[field] = Identity.GetProperty(field); if (extra != null) foreach (var p in Json(extra).EnumerateObject()) fields[p.Name] = p.Value.Clone(); return Json(fields); }
        internal Task<JsonElement> Call(SqliteTerminalPersistenceStore store, JsonElement request) => store.ExecuteAsync(request, Owner);
        internal async Task Stage(SqliteTerminalPersistenceStore store, TransferPlan plan) { await Call(store, plan.Begin); foreach (var item in plan.Objects) await Call(store, plan.Put(item)); }
        internal TransferPlan Plan(string id, byte[] body, byte[] receipt, JsonElement? root = null, int added = 1)
        {
            var result = new TransferPlan { F = this }; var refs = new List<object>();
            for (int offset = 0; offset < body.Length; offset += 12288) { var b = body.Skip(offset).Take(12288).ToArray(); refs.Add(new { sha256 = WireJson.Sha256(b), byteLength = b.Length }); result.Objects.Add(new("body-block", b)); }
            var pages = new List<ObjectData>();
            for (int start = 0; start < refs.Count; start += 64) pages.Add(new("body-page", Canon(Json(new { version = 1, kind = "body-page", index = start / 64, refs = refs.Skip(start).Take(64) }))));
            var entry = new { primaryKey = new string('a', 64), secondaryKey = new string('b', 64), value = new { sha256 = WireJson.Sha256(receipt), byteLength = receipt.Length } };
            var indexPage = new ObjectData("index-page", Canon(Json(new { version = 1, kind = "index-page", index = 0, entries = new[] { entry } })));
            result.Objects.InsertRange(0, pages); result.Objects.Insert(pages.Count, indexPage); result.Objects.Add(new("receipt-value", receipt));
            result.Objects = result.Objects.GroupBy(x => x.Kind + ":" + WireJson.Sha256(x.Bytes)).Select(x => x.First()).ToList();
            object? expected = root.HasValue ? new { commitRoot = root.Value.GetProperty("commitRoot").GetString(), generation = root.Value.GetProperty("generation").GetString(), bodyEtag = root.Value.GetProperty("body").GetProperty("sha256").GetString(), indexRoot = root.Value.GetProperty("index").GetProperty("root").GetString(), indexCount = root.Value.GetProperty("index").GetProperty("count").GetString() } : null;
            var begin = Request("begin", new { transferId = id, expected, body = new { byteLength = body.Length, sha256 = WireJson.Sha256(body), blockCount = refs.Count, pageHashes = pages.Select(x => WireJson.Sha256(x.Bytes)) }, index = new { entryCount = 1, addedCount = added, pageHashes = new[] { WireJson.Sha256(indexPage.Bytes) } }, declared = new { objects = result.Objects.Count, bytes = result.Objects.Sum(x => x.Bytes.Length) } });
            var fields = begin.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()); fields["intentSha256"] = Json(WireJson.Sha256(Canon(begin))); result.Begin = Json(fields); return result;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
