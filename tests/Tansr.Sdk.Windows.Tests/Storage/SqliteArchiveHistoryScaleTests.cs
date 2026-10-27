using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteArchiveHistoryScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FixedWorksetUsesIndexedRowsAndNoUnrelatedBodyDecryptAsArchiveGrows()
    {
        // 两库都用原公开接收/确认生成；没有虚构大正文、改 state 或降低原单库容量。
        var small = await Sample(4);
        var large = await Sample(2048);
        Assert.Equal(small.WorksetHash, large.WorksetHash);
        Assert.Equal(small.Open, large.Open);
        Assert.Equal(small.Page, large.Page);
        Assert.Equal(small.Body, large.Body);
        Assert.Equal(small.OpenKeyReads, large.OpenKeyReads);
        Assert.Equal(small.PageKeyReads, large.PageKeyReads);
        Assert.Equal(small.BodyKeyReads, large.BodyKeyReads);
        output.WriteLine("A05_SQLITE_SCALE " + JsonSerializer.Serialize(new { small, large }));
    }

    private async Task<SampleResult> Sample(int unrelatedRecords)
    {
        using var fixture = new Fixture(true, Encoding.UTF8.GetBytes(new string('w', 4096)));
        var authority = await Populate(fixture, unrelatedRecords);
        // 在计时/计量前完成构造与完整原写者审计，避免把造数内存当恢复工作集。
        using (var audit = await SqliteArchiveStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen)))
            Assert.Equal((unrelatedRecords + 1).ToString(CultureInfo.InvariantCulture), (await audit.HeadAsync())!.Value.GetProperty("sequence").GetString());
        long physicalBytes = new FileInfo(fixture.Path).Length;
        long managedBefore = GC.GetTotalMemory(true), rssBefore = Rss();
        using var trace = new SqlTrace(fixture.Path);
        int keys = 0;
        fixture.Keys!.OnRead = () => keys++;
        var clock = Stopwatch.StartNew();
        var history = await SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority);
        long openMs = clock.ElapsedMilliseconds;
        try
        {
            var opened = trace.Take(); int openKeys = keys; keys = 0;
            Assert.Equal(0, opened.ArchiveRows); Assert.Equal(0, opened.BodyBytes);
            Assert.Equal(opened.KeyChecks, openKeys); Assert.Equal(1, openKeys);
            clock.Restart();
            var page = await history.ReadPageAsync(new ArchiveReadRequest
            {
                Identity = fixture.Identity,
                Selection = Element(new { fromSequence = "1", throughSequence = (unrelatedRecords + 1).ToString(CultureInfo.InvariantCulture) }),
                MaxRecords = 1
            });
            long pageMs = clock.ElapsedMilliseconds;
            var paged = trace.Take(); int pageKeys = keys; keys = 0;
            Assert.Single(page.Records); Assert.Equal("2", page.NextFromSequence);
            Assert.Equal(0, paged.BodyBytes); Assert.Equal(0, paged.ArchiveFullScanSteps);
            Assert.Equal(paged.KeyChecks, pageKeys);
            clock.Restart();
            var body = await history.ReadArtifactAsync("record", fixture.Reference);
            long bodyMs = clock.ElapsedMilliseconds;
            var expanded = trace.Take(); int bodyKeys = keys;
            Assert.Equal(fixture.Body, body); Assert.Equal(4096 + 28, expanded.BodyBytes);
            Assert.Equal(1, expanded.BodyRows); Assert.Equal(0, expanded.ArchiveFullScanSteps);
            Assert.Equal(expanded.KeyChecks + 1, bodyKeys);
            Assert.Empty(trace.Errors);
            long cacheBytes = trace.CacheBytes();
            // SQLite 自身页缓存有界；RSS/GC 数值另作描述，不用进程噪声定义性能通过。
            Assert.InRange(cacheBytes, 1, 2 * 1024 * 1024);
            return new SampleResult(unrelatedRecords, (long)(unrelatedRecords + 1) * 4096,
                physicalBytes, WireJson.Sha256(body), opened, paged, expanded,
                openKeys, pageKeys, bodyKeys, cacheBytes, managedBefore, GC.GetTotalMemory(true), rssBefore, Rss(), openMs, pageMs, bodyMs);
        }
        finally { fixture.Keys.OnRead = null; await history.CloseAsync(); }
    }

    private static long Rss() { using var process = Process.GetCurrentProcess(); process.Refresh(); return process.WorkingSet64; }

    private static async Task<Authority> Populate(Fixture fixture, int unrelatedRecords)
    {
        using var store = await SqliteArchiveStore.OpenAsync(fixture.Options());
        var first = fixture.Input.Page.GetProperty("records")[0]; var last = first;
        var ack = await store.ReceiveAsync(fixture.Input); await store.ConfirmAsync(fixture.Receipt(ack));
        int next = 2, batchNumber = 1;
        while (next <= unrelatedRecords + 1)
        {
            int count = Math.Min(128, unrelatedRecords + 2 - next);
            var records = new List<JsonElement>(); var artifacts = new List<ArchiveArtifact>();
            for (int i = 0; i < count; i++, next++)
            {
                var reference = Replace(fixture.Reference, "artifactId", Element("archive-body-" + next));
                var values = first.EnumerateObject().Where(item => item.Name != "recordDigest").ToDictionary(item => item.Name, item => item.Value.Clone());
                values["recordId"] = Element("archive-record-" + next); values["sequence"] = Element(next.ToString(CultureInfo.InvariantCulture));
                values["turnId"] = Element("archive-turn-" + next); values["predecessorDigest"] = last.GetProperty("recordDigest"); values["payload"] = reference;
                values["recordDigest"] = Element(WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(Element(values))));
                last = Element(values); records.Add(last); artifacts.Add(new ArchiveArtifact(reference.GetProperty("artifactId").GetString()!, fixture.Body));
            }
            string through = (next - 1).ToString(CultureInfo.InvariantCulture), revision = (2 + batchNumber).ToString(CultureInfo.InvariantCulture);
            var input = new ArchiveReceiveInput
            {
                Binding = Replace(fixture.Input.Binding, "revision", Element(revision)),
                Status = Replace(Replace(Replace(Replace(Replace(fixture.Input.Status, "revision", Element(revision)),
                    "acknowledgedCoverage", ack.GetProperty("coverage")), "publishedThroughSequence", Element(through)), "pendingRecords", Element(count)), "pendingBytes", Element(count * fixture.Body.Length)),
                Page = Replace(Replace(Replace(fixture.Input.Page, "records", Element(records)), "nextAfterSequence", Element(through)), "publishedThroughSequence", Element(through)),
                Request = Replace(fixture.Input.Request, "requestId", Element("archive-ack-" + batchNumber)),
                Artifacts = artifacts
            };
            ack = await store.ReceiveAsync(input);
            await store.ConfirmAsync(Replace(fixture.Receipt(ack), "revision", Element((3 + batchNumber++).ToString(CultureInfo.InvariantCulture))));
        }
        return new Authority(first, last);
    }

    private sealed class Authority(JsonElement selected, JsonElement last) : IArchiveHistoryAuthority
    {
        public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<JsonElement?>(Element(new { sequence = last.GetProperty("sequence"), recordDigest = last.GetProperty("recordDigest") }));
        public Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult("0");
        public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(new[] { "record" }, request.Selection.GetProperty("recordIds").EnumerateArray().Select(id => id.GetString()).ToArray());
            return Task.FromResult(new ArchiveRecordPage(new[] { selected }, WireJson.EncodeControl(selected).Length, Array.Empty<string>(), null, Element(new { })));
        }
    }

    private sealed record Counts(long ArchiveRows, long BodyRows, long BodyBytes, long ArchiveFullScanSteps, long KeyChecks);
    private sealed record SampleResult(int UnrelatedRecords, long ActualPlaintextBytes, long ActualDatabaseBytes, string WorksetHash,
        Counts Open, Counts Page, Counts Body, int OpenKeyReads, int PageKeyReads, int BodyKeyReads, long SqliteCacheBytes,
        long ManagedBefore, long ManagedAfter, long RssBefore, long RssAfter, long OpenMs, long PageMs, long BodyMs);

    // 仅本例新建库的只读连接安装原生观察器，不改 SQL／产品或已存在的用户文件。
    private sealed class SqlTrace : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Extension(IntPtr database, IntPtr error, IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Trace(uint kind, IntPtr context, IntPtr statement, IntPtr data);
        private readonly Extension extension;
        private readonly Trace trace;
        private readonly string path;
        private IntPtr database;
        private long archiveRows, bodyRows, bodyBytes, fullScanSteps, keyChecks;
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
                if (file != null && Path.GetFullPath(file).Equals(path, StringComparison.OrdinalIgnoreCase))
                { database = handle; if (sqlite3_trace_v2(handle, 2 | 4 | 8, trace, IntPtr.Zero) != 0) Errors.Add("trace-registration"); }
            }
            catch (Exception failure) { Errors.Add(failure.GetType().Name); }
            return 0;
        }
        private int Observe(uint kind, IntPtr context, IntPtr statement, IntPtr data)
        {
            try
            {
                if (kind == 8) { database = IntPtr.Zero; return 0; }
                var sql = Marshal.PtrToStringUTF8(sqlite3_sql(statement)) ?? "";
                bool archive = sql.Contains(" FROM records", StringComparison.OrdinalIgnoreCase) || sql.Contains(" FROM artifacts", StringComparison.OrdinalIgnoreCase) || sql.Contains(" FROM record_artifacts", StringComparison.OrdinalIgnoreCase);
                if (kind == 2 && archive) fullScanSteps += sqlite3_stmt_status(statement, 1, 1);
                if (kind == 4)
                {
                    if (archive) archiveRows++;
                    if (sql.Contains("SELECT key_check FROM encryption", StringComparison.OrdinalIgnoreCase)) keyChecks++;
                    for (int i = 0; i < sqlite3_column_count(statement); i++)
                    {
                        var name = Marshal.PtrToStringUTF8(sqlite3_column_name(statement, i)) ?? "";
                        if (name.Equals("body", StringComparison.OrdinalIgnoreCase) || name.StartsWith("substr(body", StringComparison.OrdinalIgnoreCase))
                        { bodyRows++; bodyBytes += sqlite3_column_bytes(statement, i); }
                    }
                }
            }
            catch (Exception failure) { Errors.Add(failure.GetType().Name); }
            return 0;
        }
        internal Counts Take()
        {
            var value = new Counts(archiveRows, bodyRows, bodyBytes, fullScanSteps, keyChecks);
            archiveRows = bodyRows = bodyBytes = fullScanSteps = keyChecks = 0; return value;
        }
        internal int CacheBytes()
        {
            Assert.NotEqual(IntPtr.Zero, database);
            Assert.Equal(0, sqlite3_db_status(database, 1, out var current, out _, 0)); return current;
        }
        public void Dispose()
        {
            Assert.Equal(1, sqlite3_cancel_auto_extension(Marshal.GetFunctionPointerForDelegate(extension)));
            GC.KeepAlive(trace); GC.KeepAlive(extension);
        }
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_auto_extension(IntPtr callback);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_cancel_auto_extension(IntPtr callback);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_db_filename(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_trace_v2(IntPtr database, uint mask, Trace callback, IntPtr context);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_sql(IntPtr statement);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_stmt_status(IntPtr statement, int operation, int reset);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_db_status(IntPtr database, int operation, out int current, out int highwater, int reset);
    }
}
