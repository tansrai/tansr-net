using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteArchiveSourceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ColdSourceAndFirstWriteReadOnlySelectedRowsAsUnrelatedArchiveGrows()
    {
        var small = await Sample(4); var large = await Sample(256);
        Assert.Equal(small.Open, large.Open); Assert.Equal(small.BodyHash, large.BodyHash);
        Assert.Equal(0, small.Open.Rows); Assert.Equal(0, small.Open.BodyBytes);
        foreach (var sample in new[] { small, large })
        {
            Assert.Equal(0, sample.Open.Scans); Assert.Equal(0, sample.Page.Scans); Assert.Equal(0, sample.Body.Scans);
            Assert.Equal(0, sample.Receive.Scans); Assert.Equal(0, sample.Confirm.Scans);
            Assert.Equal(0, sample.Page.BodyBytes); Assert.Equal(4096 + 28, sample.Body.BodyBytes);
            Assert.Equal(0, sample.Receive.BodyBytes); Assert.Equal(0, sample.Confirm.BodyBytes);
            Assert.InRange(sample.Page.Rows, 1, 16); Assert.InRange(sample.Receive.Rows, 1, 32); Assert.InRange(sample.Confirm.Rows, 1, 4);
            Assert.InRange(sample.Page.Nodes, 1, 257 * 8); Assert.InRange(sample.Receive.Nodes, 1, 257 * 32);
        }
        output.WriteLine("A05_ARCHIVE_SOURCE_SQL " + JsonSerializer.Serialize(new { small, large }));
    }

    private async Task<SampleResult> Sample(int count)
    {
        using var fixture = new Fixture(true, Encoding.UTF8.GetBytes(new string('w', 4096)));
        var last = await Populate(fixture, count);
        using var trace = new SqlTrace(fixture.Path);
        using var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen));
        var opened = trace.Take();
        var page = await store.ReadRecordsAsync(new ArchiveReadRequest { Identity = fixture.Identity, Selection = Element(new { recordIds = new[] { "record" } }), MaxRecords = 1 });
        Assert.Single(page.Records); var paged = trace.Take();
        var body = await store.BodyAsync(fixture.Reference); Assert.Equal(fixture.Body, body); var expanded = trace.Take();
        var input = Next(fixture, last.Record, last.Ack, count + 1);
        var ack = await store.ReceiveAsync(input); var received = trace.Take();
        await store.ConfirmAsync(Receipt(fixture, ack)); var confirmed = trace.Take();
        Assert.Empty(trace.Errors);
        return new SampleResult(count, WireJson.Sha256(body), opened, paged, expanded, received, confirmed);
    }

    [Fact]
    public async Task PendingOriginalKeySurvivesColdReopenAndSelectedDeletionCannotBecomeAbsence()
    {
        using var fixture = new Fixture(true); JsonElement ack;
        using (var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options())) ack = await store.ReceiveAsync(fixture.Input);
        using (var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen)))
        {
            Assert.Equal(Text(ack), Text((await store.PendingAsync())!.Value));
            await store.ConfirmAsync(fixture.Receipt(ack)); await store.ConfirmAsync(fixture.Receipt(ack));
            Assert.Null(await store.ReplicaOperationAsync(Element(new { operationEpoch = "epoch", requestId = "unknown" })));
        }
        Raw(fixture.Path, "DELETE FROM operations");
        using var reopened = await SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => reopened.ReplicaOperationAsync(ack.GetProperty("request")))).Code);
    }

    [Fact]
    public async Task OldValidReceiptRowCannotBeReplayedUnderCurrentAuthenticatedState()
    {
        using var fixture = new Fixture(true); JsonElement ack;
        using (var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options())) ack = await store.ReceiveAsync(fixture.Input);
        using (var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen))) await store.ConfirmAsync(fixture.Receipt(ack));
        Raw(fixture.Path, "UPDATE operations SET receipt=NULL,reserve=zeroblob(4096)");
        using var reopened = await SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.Null(await reopened.PendingAsync());
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => reopened.ConfirmAsync(fixture.Receipt(ack)))).Code);
    }

    [Fact]
    public async Task MissingSelectedRecordIsCorruptionWhileUnknownRecordIsMissing()
    {
        using var fixture = new Fixture(true); await Populate(fixture, 2);
        Raw(fixture.Path, "DELETE FROM records WHERE record_id='record'");
        using var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen));
        var unknown = await store.ReadRecordsAsync(new ArchiveReadRequest { Identity = fixture.Identity, Selection = Element(new { recordIds = new[] { "unknown" } }) });
        Assert.Equal(new[] { "unknown" }, unknown.MissingRecordIds);
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => store.ReadRecordsAsync(new ArchiveReadRequest { Identity = fixture.Identity, Selection = Element(new { recordIds = new[] { "record" } }) }))).Code);
    }

    [Fact]
    public async Task UnselectedCorruptBodyDoesNotEnterOpenButSelectionAndExplicitAuditRejectIt()
    {
        using var fixture = new Fixture(true); var last = await Populate(fixture, 3);
        Raw(fixture.Path, "UPDATE artifacts SET body=zeroblob(length(body)) WHERE id='source-body-3'");
        using var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.Equal(fixture.Body, await store.BodyAsync(fixture.Reference));
        await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(last.Record.GetProperty("payload")));
        await Assert.ThrowsAsync<StorageException>(() => store.AuditAsync());
    }

    [Fact]
    public async Task LayoutKeyIdentityAndConcurrentOwnersRemainBound()
    {
        using var fixture = new Fixture(true);
        using (var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options()))
        {
            await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen)));
            async Task<bool> Receive()
            {
                try { await store.ReceiveAsync(fixture.Input); return true; }
                catch (StorageException error) when (error.Code == "pending_ack") { return false; }
            }
            var receipts = await Task.WhenAll(Task.Run(Receive), Task.Run(Receive));
            Assert.Single(receipts, value => value);
        }
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen)));
        var wrong = fixture.Options(StorageOpenMode.Reopen); wrong.Identity = Replace(wrong.Identity, "sourceId", Element("other-source"));
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenSourceAsync(wrong));
        wrong = fixture.Options(StorageOpenMode.Reopen); wrong.KeyProvider = new WrongKey();
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenSourceAsync(wrong));
        using var reopened = await SqliteArchiveStore.OpenSourceAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.NotNull(await reopened.PendingAsync()); await reopened.ConfirmAsync(fixture.Receipt((await reopened.PendingAsync())!.Value));
        await reopened.AuditAsync();
    }

    [Fact]
    public async Task OriginalCapacityAndAuthorizedRetentionArePreserved()
    {
        using var fixture = new Fixture(true); var options = fixture.Options(); options.Limits.MaxRecords = 1;
        using var store = await SqliteArchiveStore.OpenSourceAsync(options);
        var ack = await store.ReceiveAsync(fixture.Input); await store.ConfirmAsync(fixture.Receipt(ack));
        await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(Next(fixture, fixture.Input.Page.GetProperty("records")[0], ack, 2)));
        Assert.Equal("1", (await store.HeadAsync())!.Value.GetProperty("sequence").GetString());
        fixture.RetentionRevision = "1"; await store.ApplyRetentionAsync(fixture.Retention());
        Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(fixture.Reference))).Code);
        await store.AuditAsync();
    }

    [Fact]
    public async Task CancellationAndProviderReentryCannotCommitOrReturnSelectedData()
    {
        using var fixture = new Fixture(true); using var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options());
        using var cancellation = new CancellationTokenSource();
        fixture.Keys!.OnRead = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReceiveAsync(fixture.Input, cancellation.Token));
        fixture.Keys.OnRead = null; Assert.Null(await store.HeadAsync());
        int reads = 0;
        fixture.Keys.OnRead = () =>
        {
            if (++reads != 3) return;
            try { store.HeadAsync().GetAwaiter().GetResult(); } catch (StorageException) { }
        };
        Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => store.ReceiveAsync(fixture.Input))).Code);
        fixture.Keys.OnRead = null; Assert.Null(await store.HeadAsync());
        var ack = await store.ReceiveAsync(fixture.Input); await store.ConfirmAsync(fixture.Receipt(ack));
        fixture.User = "revoked"; await Assert.ThrowsAsync<StorageException>(() => store.BodyAsync(fixture.Reference));
    }

    [Fact]
    public async Task PlainRecoveryAndLegacyFilesNeverImplicitlyEnterNewLayout()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenSourceAsync(fixture.Options()));
        using (var old = await SqliteArchiveStore.OpenRecoverableAsync(fixture.Options())) await old.ReceiveAsync(fixture.Input);
        var options = fixture.Options(StorageOpenMode.Reopen); options.Layout = ArchiveSqliteLayout.AuthenticatedIndexed;
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenRecoverableAsync(options));
        using var legacy = await SqliteArchiveStore.OpenRecoverableAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.NotNull(await legacy.PendingAsync());
    }

    private static string Text(JsonElement value) => WireJson.CanonicalString(value, 1572864);
    private static JsonElement Receipt(Fixture fixture, JsonElement ack) => Replace(fixture.Receipt(ack), "revision", Element((long.Parse(ack.GetProperty("expectedRevision").GetString()!, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)));

    private static async Task<(JsonElement Record, JsonElement Ack)> Populate(Fixture fixture, int count)
    {
        using var store = await SqliteArchiveStore.OpenSourceAsync(fixture.Options());
        var record = fixture.Input.Page.GetProperty("records")[0]; var ack = await store.ReceiveAsync(fixture.Input); await store.ConfirmAsync(fixture.Receipt(ack));
        for (int sequence = 2; sequence <= count; sequence++)
        {
            var input = Next(fixture, record, ack, sequence); record = input.Page.GetProperty("records")[0];
            ack = await store.ReceiveAsync(input); await store.ConfirmAsync(Receipt(fixture, ack));
        }
        await store.AuditAsync(); return (record, ack);
    }

    private static ArchiveReceiveInput Next(Fixture fixture, JsonElement previous, JsonElement ack, int sequence)
    {
        string next = sequence.ToString(CultureInfo.InvariantCulture), revision = (sequence + 1).ToString(CultureInfo.InvariantCulture);
        var reference = Replace(fixture.Reference, "artifactId", Element("source-body-" + next));
        var values = fixture.Input.Page.GetProperty("records")[0].EnumerateObject().Where(item => item.Name != "recordDigest").ToDictionary(item => item.Name, item => item.Value.Clone());
        values["recordId"] = Element("source-record-" + next); values["sequence"] = Element(next); values["turnId"] = Element("source-turn-" + next);
        values["predecessorDigest"] = previous.GetProperty("recordDigest"); values["payload"] = reference;
        values["recordDigest"] = Element(WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(Element(values))));
        return new ArchiveReceiveInput
        {
            Binding = Replace(fixture.Input.Binding, "revision", Element(revision)),
            Status = Replace(Replace(Replace(fixture.Input.Status, "revision", Element(revision)), "acknowledgedCoverage", ack.GetProperty("coverage")), "publishedThroughSequence", Element(next)),
            Page = Replace(Replace(Replace(fixture.Input.Page, "records", Element(new[] { Element(values) })), "nextAfterSequence", Element(next)), "publishedThroughSequence", Element(next)),
            Request = Replace(fixture.Input.Request, "requestId", Element("source-ack-" + next)), Artifacts = new[] { new ArchiveArtifact("source-body-" + next, fixture.Body) },
        };
    }

    private static void Raw(string path, string sql)
    {
        // 故意损坏本例已关闭的合成库；不让外键先拦截缺行注入而掩盖源端认证检查。
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private sealed class WrongKey : IArchiveKeyProvider
    {
        public string KeyId => "key";
        public byte[] ReadKey() => Enumerable.Repeat((byte)0xee, 32).ToArray();
    }
    private sealed record Counts(long Rows, long BodyBytes, long Scans, long Nodes);
    private sealed record SampleResult(int Records, string BodyHash, Counts Open, Counts Page, Counts Body, Counts Receive, Counts Confirm);

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
                bool archive = new[] { "records", "artifacts", "operations", "checkpoints", "retention", "tombstones", "record_artifacts", "source_artifacts" }.Any(table => sql.Contains(" FROM " + table, StringComparison.OrdinalIgnoreCase));
                if (sql.Contains("quick_check", StringComparison.OrdinalIgnoreCase)) Errors.Add("implicit-full-check");
                if (kind == 2 && archive) scans += sqlite3_stmt_status(statement, 1, 1);
                if (kind == 4)
                {
                    if (archive) rows++;
                    if (sql.Contains(" FROM source_authentication_nodes", StringComparison.OrdinalIgnoreCase)) nodes++;
                    if (sql.Contains(" FROM artifacts", StringComparison.OrdinalIgnoreCase))
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
