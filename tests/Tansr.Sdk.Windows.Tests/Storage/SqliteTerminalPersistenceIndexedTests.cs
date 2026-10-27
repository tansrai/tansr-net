using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteTerminalPersistenceIndexedTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ColdOriginalKeyAndFirstWriteReadOnlyFixedWorksetAsPermanentHistoryGrows()
    {
        var small = await Sample(4); var large = await Sample(64);
        Assert.Equal(small.Hash, large.Hash);
        Assert.Equal(small.Open with { Nodes = 0 }, large.Open with { Nodes = 0 });
        Assert.Equal(small.Query with { Nodes = 0 }, large.Query with { Nodes = 0 });
        Assert.Equal(small.Read with { Nodes = 0 }, large.Read with { Nodes = 0 });
        Assert.Equal(small.Write with { Nodes = 0 }, large.Write with { Nodes = 0 });
        Assert.Equal(0, large.Open.BodyBytes); Assert.Equal(0, large.Open.Rows);
        Assert.Equal(0, large.Query.BodyBytes); Assert.Equal(0, large.Read.Scans); Assert.Equal(0, large.Write.Scans);
        Assert.InRange(large.Open.Nodes, 1, 257); Assert.InRange(large.Read.Nodes, 1, 514);
        output.WriteLine("A05_TERMINAL_SOURCE_SQL " + JsonSerializer.Serialize(new { small, large }, new JsonSerializerOptions { IncludeFields = true }));
    }
    private async Task<(int History, string Hash, Counts Open, Counts Query, Counts Read, Counts Write)> Sample(int count)
    {
        using var f = new Fixture(); TransferPlan selected; JsonElement root, committed;
        using (var store = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options()))
        {
            JsonElement? current = null;
            for (int i = 0; i < count; i++)
            {
                var plan = f.Plan("history-" + i, Enumerable.Repeat((byte)i, 1024).ToArray(), new byte[] { 11 }, current, i == 0 ? 1 : 0);
                await f.Stage(store, plan); await f.Call(store, plan.Action("commit")); current = (await f.Call(store, f.Request("head"))).GetProperty("root").Clone();
            }
            selected = f.Plan("selected", Enumerable.Repeat((byte)0xfe, 1024).ToArray(), new byte[] { 11 }, current, 0);
            await f.Stage(store, selected); committed = await f.Call(store, selected.Action("commit"));
            root = (await f.Call(store, f.Request("head"))).GetProperty("root").Clone(); await store.AuditAsync();
        }
        using var trace = new SqlTrace(f.Path);
        using var reopened = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options(StorageOpenMode.Reopen)); var open = trace.Take();
        Assert.Equal(Text(committed.GetProperty("transfer")), Text((await f.Call(reopened, selected.Action("query"))).GetProperty("transfer"))); var query = trace.Take();
        var read = await f.Call(reopened, f.Request("read", new { commitRoot = root.GetProperty("commitRoot").GetString(), part = "body", offset = 0, length = 1024 })); var readCounts = trace.Take();
        var next = f.Plan("next", Enumerable.Repeat((byte)0xfe, 1024).ToArray(), new byte[] { 11 }, root, 0);
        Assert.Equal("staging", (await f.Call(reopened, next.Begin)).GetProperty("transfer").GetProperty("status").GetString()); var write = trace.Take();
        Assert.Empty(trace.Errors); return (count, WireJson.Sha256(Convert.FromBase64String(read.GetProperty("base64").GetString()!)), open, query, readCounts, write);
    }
    [Fact]
    public async Task SelectedCorruptionFailsWhileUnselectedCorruptionWaitsForExplicitAudit()
    {
        using var f = new Fixture(); var first = f.Plan("old", new byte[] { 1, 1 }, new byte[] { 9 }); JsonElement root;
        using (var store = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options()))
        {
            await f.Stage(store, first); await f.Call(store, first.Action("commit")); var before = (await f.Call(store, f.Request("head"))).GetProperty("root");
            var next = f.Plan("current", new byte[] { 2, 2 }, new byte[] { 9 }, before, 0); await f.Stage(store, next); await f.Call(store, next.Action("commit"));
            root = (await f.Call(store, f.Request("head"))).GetProperty("root").Clone();
        }
        Raw(f.Path, "UPDATE objects SET body=zeroblob(length(body)) WHERE kind='body-block' AND hash=$hash", ("$hash", WireJson.Sha256(new byte[] { 1, 1 })));
        using (var store = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options(StorageOpenMode.Reopen)))
        {
            var read = await f.Call(store, f.Request("read", new { commitRoot = root.GetProperty("commitRoot").GetString(), part = "body", offset = 0, length = 2 }));
            Assert.Equal(new byte[] { 2, 2 }, Convert.FromBase64String(read.GetProperty("base64").GetString()!));
            await Assert.ThrowsAsync<StorageException>(() => store.AuditAsync());
        }
        Raw(f.Path, "DELETE FROM objects WHERE kind='body-block' AND hash=$hash", ("$hash", WireJson.Sha256(new byte[] { 2, 2 })));
        using var selected = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
        await Assert.ThrowsAsync<StorageException>(() => f.Call(selected, f.Request("read", new { commitRoot = root.GetProperty("commitRoot").GetString(), part = "body", offset = 0, length = 2 })));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalTransferDeletionOrOldAuthenticatedCiphertextCannotBecomeUnknownOrStaging(bool replay)
    {
        using var f = new Fixture(); var plan = f.Plan("original", new byte[] { 3 }, new byte[] { 4 }); byte[] earlier;
        using (var store = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options())) await f.Call(store, plan.Begin);
        earlier = (byte[])Raw(f.Path, "SELECT body FROM transfers WHERE id='original'")!;
        using (var store = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options(StorageOpenMode.Reopen))) { await f.Stage(store, plan); await f.Call(store, plan.Action("commit")); }
        if (replay) Raw(f.Path, "UPDATE transfers SET status='staging',body=$body WHERE id='original'", ("$body", earlier));
        else Raw(f.Path, "DELETE FROM transfers WHERE id='original'");
        using var reopened = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
        await Assert.ThrowsAsync<StorageException>(() => f.Call(reopened, plan.Action("query")));
        await Assert.ThrowsAsync<StorageException>(() => f.Call(reopened, plan.Begin));
        Assert.Equal("unknown", (await f.Call(reopened, f.Plan("never", new byte[] { 3 }, new byte[] { 4 }).Action("query"))).GetProperty("transfer").GetProperty("status").GetString());
    }
    [Fact]
    public async Task LayoutIdentityAndOwnerBoundariesSurviveReopenAndConcurrentQueries()
    {
        using var f = new Fixture(); var plan = f.Plan("original", new byte[] { 5 }, new byte[] { 6 }); JsonElement original;
        using (var store = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options())) { await f.Stage(store, plan); original = await f.Call(store, plan.Action("commit")); }
        var legacy = f.Options(StorageOpenMode.Reopen); legacy.Layout = TerminalPersistenceSqliteLayout.LegacyAudited;
        await Assert.ThrowsAsync<StorageException>(() => SqliteTerminalPersistenceStore.OpenAsync(legacy));
        var wrong = f.Options(StorageOpenMode.Reopen); wrong.Identity = Json(new { applicationScopeId = "app", endUserId = "user", sourceId = "other", sourceGeneration = "1", domainKey = "domain" });
        await Assert.ThrowsAsync<StorageException>(() => SqliteTerminalPersistenceStore.OpenSourceAsync(wrong));
        using var reopened = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
        await Assert.ThrowsAsync<StorageException>(() => SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options(StorageOpenMode.Reopen)));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => f.Call(reopened, plan.Action("query")))));
        foreach (var result in results) Assert.Equal(Text(original.GetProperty("transfer")), Text(result.GetProperty("transfer")));
        Assert.Equal(Text(original), Text(await f.Call(reopened, plan.Action("commit"))));
        string root = (await f.Call(reopened, f.Request("head"))).GetProperty("root").GetProperty("commitRoot").GetString()!;
        JsonElement Lookup(string kind) => f.Request("lookup", new { commitRoot = root, key = new { kind, digest = new string(kind == "primary" ? 'a' : 'b', 64) } });
        foreach (string kind in new[] { "primary", "secondary" })
            Assert.Equal(new byte[] { 6 }, Convert.FromBase64String((await f.Call(reopened, Lookup(kind))).GetProperty("entry").GetProperty("base64").GetString()!));
        reopened.Dispose(); Raw(f.Path, "DELETE FROM entries");
        using var missingReceipt = await SqliteTerminalPersistenceStore.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
        foreach (string kind in new[] { "primary", "secondary" })
            await Assert.ThrowsAsync<StorageException>(() => f.Call(missingReceipt, Lookup(kind)));
    }
    private static object? Raw(string path, string sql, params (string Name, object Value)[] args)
    {
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value); return command.ExecuteScalar();
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
        internal string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pst-net-indexed-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "profile.sqlite"); internal Key Key = new(); internal string Revision = "1";
        internal JsonElement Identity => Json(new { applicationScopeId = "app", endUserId = "user", sourceId = "source", sourceGeneration = "1", domainKey = "domain" });
        internal JsonElement Scope => Json(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = Revision });
        internal string Owner => Text(Json(new { scope = Scope, sessionId = "session", binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } } }));
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal SqliteTerminalPersistenceOptions Options(StorageOpenMode mode = StorageOpenMode.Create) => new() { EnableProfile = true, Layout = TerminalPersistenceSqliteLayout.AuthenticatedIndexed, Path = Path, Mode = mode, Identity = Identity, ReadContext = () => Scope, KeyProvider = Key };
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
    private sealed record Counts(long Rows, long BodyBytes, long Scans, long Nodes);

    private sealed class SqlTrace : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Extension(IntPtr database, IntPtr error, IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Trace(uint kind, IntPtr context, IntPtr statement, IntPtr data);
        private readonly Extension extension;
        private readonly Trace trace;
        private readonly string path;
        private long rows, bodyBytes, scans, nodes;
        internal List<string> Errors { get; } = new();
        internal SqlTrace(string path)
        {
            this.path = Path.GetFullPath(path); extension = Opened; trace = Observe;
            Assert.Equal(0, sqlite3_auto_extension(Marshal.GetFunctionPointerForDelegate(extension)));
        }
        private int Opened(IntPtr handle, IntPtr error, IntPtr api)
        {
            try
            {
                string? file = Marshal.PtrToStringUTF8(sqlite3_db_filename(handle, "main"));
                if (file != null && Path.GetFullPath(file).Equals(path, StringComparison.OrdinalIgnoreCase) && sqlite3_trace_v2(handle, 2 | 4, trace, IntPtr.Zero) != 0) Errors.Add("trace-registration");
            }
            catch (Exception failure) { Errors.Add(failure.GetType().Name); }
            return 0;
        }
        private int Observe(uint kind, IntPtr context, IntPtr statement, IntPtr data)
        {
            try
            {
                string sql = Marshal.PtrToStringUTF8(sqlite3_sql(statement)) ?? "";
                bool archive = new[] { "objects", "entries", "transfers" }.Any(table => sql.Contains(" FROM " + table, StringComparison.OrdinalIgnoreCase));
                if (sql.Contains("quick_check", StringComparison.OrdinalIgnoreCase)) Errors.Add("implicit-full-check");
                if (kind == 2 && archive) scans += sqlite3_stmt_status(statement, 1, 1);
                if (kind == 4)
                {
                    if (archive) rows++;
                    if (sql.Contains(" FROM authentication_nodes", StringComparison.OrdinalIgnoreCase)) nodes++;
                    if (sql.Contains(" FROM objects", StringComparison.OrdinalIgnoreCase))
                        for (int i = 0; i < sqlite3_column_count(statement); i++)
                        {
                            string name = Marshal.PtrToStringUTF8(sqlite3_column_name(statement, i)) ?? "";
                            if (name == "body" || name.StartsWith("substr(body", StringComparison.OrdinalIgnoreCase)) bodyBytes += sqlite3_column_bytes(statement, i);
                        }
                }
            }
            catch (Exception failure) { Errors.Add(failure.GetType().Name); }
            return 0;
        }
        internal Counts Take() { var result = new Counts(rows, bodyBytes, scans, nodes); rows = bodyBytes = scans = nodes = 0; return result; }
        public void Dispose() { Assert.Equal(1, sqlite3_cancel_auto_extension(Marshal.GetFunctionPointerForDelegate(extension))); GC.KeepAlive(trace); GC.KeepAlive(extension); }
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_auto_extension(IntPtr callback);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_cancel_auto_extension(IntPtr callback);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_db_filename(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_trace_v2(IntPtr database, uint mask, Trace callback, IntPtr context);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_sql(IntPtr statement);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_stmt_status(IntPtr statement, int operation, int reset);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    }
}
