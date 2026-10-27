using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteExecutorJournalSourceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ExplicitRecoveryPageRejectsMissingPermanentMemberAndKeepsPageBodiesBounded()
    {
        foreach (string missing in new[] { "pending", "z" })
        {
            using var f = new Fixture();
            using (var initial = await SqliteExecutorJournal.OpenSourceAsync(f.Options()))
                foreach (string id in new[] { "a", "b", "pending", "z" })
                {
                    var operation = Operation(id); await initial.ClaimAsync(operation);
                    if (id != "pending") await initial.CompleteAsync(operation, Receipt(operation));
                }
            using var trace = new SqlTrace(f.Path);
            using (var intact = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen)))
            {
                Assert.Equal(0, trace.Take().BodyBytes);
                Assert.Equal(new[] { "pending", "z" }, (await intact.OperationsAsync("b")).Select(x => x.GetProperty("operationId").GetString()));
                var pageCost = trace.Take(); Assert.InRange(pageCost.BodyBytes, 1, 2000);
                output.WriteLine(JsonSerializer.Serialize(new { missing, explicitPageCost = pageCost }));
            }
            f.Sql("DELETE FROM operations WHERE id='" + missing + "'");
            using var damaged = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
            trace.Take();
            await Assert.ThrowsAsync<StorageException>(() => damaged.OperationsAsync("b"));
            var client = new NoRequests(f.Scope);
            await Assert.ThrowsAsync<StorageException>(() => new ExecutionRecovery(client, damaged).ReconcilePageAsync("b"));
            Assert.Equal(0, client.Requests);
            // Even an otherwise empty requested tail must not certify that a missing permanent member was reconciled.
            await Assert.ThrowsAsync<StorageException>(() => damaged.OperationsAsync("z"));
            Assert.Equal(0, trace.Take().BodyBytes); Assert.Empty(trace.Errors);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdSourceAndFirstOriginalClaimReadOnlySelectedRowsAsPermanentHistoryGrows(bool compact)
    {
        foreach (int size in new[] { 4, 128 })
        {
            using var f = new Fixture(); var options = f.Options(); options.CompactCompletedReceipts = compact;
            using (var source = await SqliteExecutorJournal.OpenSourceAsync(options))
                for (int i = 0; i < size; i++) { var op = Operation("saved-" + i.ToString("D4")); await source.ClaimAsync(op); await source.CompleteAsync(op, Receipt(op)); }
            options.Mode = StorageOpenMode.Reopen;
            using var trace = new SqlTrace(f.Path);
            using (var source = await SqliteExecutorJournal.OpenSourceAsync(options))
            {
                var cold = trace.Take(); Assert.Equal(0, cold.Rows); Assert.Equal(0, cold.BodyBytes); Assert.Equal(0, cold.Scans); Assert.Equal(1, cold.Nodes);
                var original = Operation("saved-0000"); Assert.Equal(ExecutorJournalClaimStatus.Completed, (await source.ClaimAsync(original)).Status);
                var query = trace.Take(); Assert.Equal(1, query.Rows); Assert.True(query.BodyBytes > 0); Assert.Equal(0, query.Scans);
                var first = Operation("first-new"); Assert.Equal(ExecutorJournalClaimStatus.Claimed, (await source.ClaimAsync(first)).Status);
                var claim = trace.Take(); Assert.Equal(0, claim.Rows); Assert.Equal(0, claim.BodyBytes); Assert.Equal(0, claim.Scans);
                Assert.Null(await source.ReceiptAsync(first)); var pending = trace.Take(); Assert.Equal(1, pending.Rows);
                await source.CompleteAsync(first, Receipt(first, "unknown")); var complete = trace.Take(); Assert.Equal(2, complete.Rows); Assert.Equal(0, complete.Scans);
                Assert.Equal(ExecutorJournalClaimStatus.Completed, (await source.ClaimAsync(first)).Status);
                Assert.Equal("unknown", (await source.ReceiptAsync(first))!.Value.GetProperty("status").GetString()); trace.Take();
                var page = await source.OperationsAsync(); var pageCost = trace.Take(); Assert.Equal(Math.Min(128, size + 1), page.Count);
                Assert.True(pageCost.BodyBytes < 2 * 1048576); Assert.Empty(trace.Errors);
                output.WriteLine(JsonSerializer.Serialize(new { size, compact, cold, query, claim, pending, complete, pageCost }));
            }
        }
    }

    [Fact]
    public async Task SelectedDeletionReplayOrCipherDamageCannotBecomeAbsentWhileUnrelatedDamageWaitsForAudit()
    {
        foreach (string change in new[] { "missing-row", "old-pending", "receipt", "operation", "missing-proof" })
        {
            using var f = new Fixture(); var good = Operation("good"); var bad = Operation("bad");
            using (var initial = await SqliteExecutorJournal.OpenSourceAsync(f.Options()))
                foreach (var op in new[] { good, bad }) { await initial.ClaimAsync(op); await initial.CompleteAsync(op, Receipt(op)); }
            f.Sql(change switch
            {
                "missing-row" => "DELETE FROM operations WHERE id='bad'",
                "old-pending" => "UPDATE operations SET receipt=NULL,reserve=zeroblob(349564) WHERE id='bad'",
                "receipt" => "UPDATE operations SET receipt='AAAA'||substr(receipt,5) WHERE id='bad'",
                "operation" => "UPDATE operations SET operation=(SELECT operation FROM operations WHERE id='good') WHERE id='bad'",
                _ => "DELETE FROM source_operations WHERE id='bad'",
            });
            using var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
            Assert.Equal(ExecutorJournalClaimStatus.Completed, (await source.ClaimAsync(good)).Status);
            await Assert.ThrowsAsync<StorageException>(() => source.ClaimAsync(bad));
            await Assert.ThrowsAsync<StorageException>(() => source.ReceiptAsync(bad));
            await Assert.ThrowsAsync<StorageException>(() => source.AuditAsync());
        }
    }

    [Fact]
    public async Task AuthenticatedHeaderRootIdentityKeyAndExplicitLayoutAreRequiredWithoutMigration()
    {
        foreach (string change in new[] { "key", "identity", "legacy", "state", "head", "root" })
        {
            using var f = new Fixture();
            using (var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options())) await source.ClaimAsync(Operation("pending"));
            var options = f.Options(StorageOpenMode.Reopen);
            if (change == "key") f.Key.Value[0] ^= 1;
            if (change == "identity") options.ExecutorId = "other";
            if (change == "legacy") options.Layout = ExecutorJournalSqliteLayout.LegacyAudited;
            if (change == "state") f.Sql("UPDATE state SET operation_count=0");
            if (change == "head") f.Sql("UPDATE source_authentication SET body=zeroblob(length(body))");
            if (change == "root") f.Sql("UPDATE source_authentication_nodes SET body=zeroblob(length(body))");
            var before = File.ReadAllBytes(f.Path);
            await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenAsync(options));
            Assert.Equal(before, File.ReadAllBytes(f.Path));
        }
        using var legacy = new Fixture(); var old = legacy.Options(); old.Layout = ExecutorJournalSqliteLayout.LegacyAudited;
        using (var source = await SqliteExecutorJournal.OpenAsync(old)) await source.ClaimAsync(Operation("old"));
        old.Mode = StorageOpenMode.Reopen;
        await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenSourceAsync(old));
        using var recovered = await SqliteExecutorJournal.OpenAsync(old);
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await recovered.ClaimAsync(Operation("old"))).Status);
    }

    [Fact]
    public async Task OriginalScopeConcurrencyAndPreCommitRevocationKeepAtomicPendingAndProof()
    {
        using var f = new Fixture(); var op = Operation("one");
        using (var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options()))
        {
            var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => source.ClaimAsync(op))));
            Assert.Single(claims, x => x.Status == ExecutorJournalClaimStatus.Claimed);
            Assert.Equal(3, claims.Count(x => x.Status == ExecutorJournalClaimStatus.Pending));
            await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen)));
            f.Revision = "2";
            await Assert.ThrowsAsync<StorageException>(() => source.ClaimAsync(op));
            await source.CompleteAsync(op, Receipt(op)); // Same app/user may settle its already executed old revision.
            Assert.NotNull(await source.ReceiptAsync(op)); f.Revision = "1";
        }
        using var trace = new SqlTrace(f.Path);
        using (var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen)))
        {
            var original = Operation("revoked");
            trace.BeforeHeadSave = () => { f.Revoked = true; trace.BeforeHeadSave = null; };
            Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => source.ClaimAsync(original))).Code);
            f.Revoked = false;
            Assert.Equal(ExecutorJournalClaimStatus.Claimed, (await source.ClaimAsync(original)).Status);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CompleteAsync(original, Receipt(original), canceled.Token));
            Assert.Null(await source.ReceiptAsync(original));
        }
        using var reopened = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await reopened.ClaimAsync(Operation("revoked"))).Status);
    }

    [Fact]
    public async Task ActualCommitFollowedByAuthorityLossIsUnknownAndColdOriginalKeyNeverRegrants()
    {
        using var f = new Fixture();
        using (var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options())) { }
        using var trace = new SqlTrace(f.Path); var op = Operation("committed-before-loss");
        using (var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen)))
        {
            trace.AfterCommit = () => { f.Revoked = true; trace.AfterCommit = null; };
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => source.ClaimAsync(op))).Code);
            f.Revoked = false;
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => source.ReceiptAsync(op))).Code);
        }
        using var reopened = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await reopened.ClaimAsync(op)).Status);
        await reopened.CompleteAsync(op, Receipt(op, "unknown"));
        Assert.Equal("unknown", (await reopened.ReceiptAsync(op))!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PhysicalPageLimitRejectsNewClaimButReservedPendingCompletionRemainsDurable()
    {
        using var f = new Fixture(); var first = Operation("reserved");
        using (var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options()))
        {
            await source.ClaimAsync(first);
            var db = (SqliteConnection)typeof(SqliteExecutorJournal).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA page_count";
            long pages = Convert.ToInt64(command.ExecuteScalar()); command.CommandText = "PRAGMA max_page_count=" + pages; Assert.Equal(pages, Convert.ToInt64(command.ExecuteScalar()));
            await Assert.ThrowsAsync<StorageException>(() => source.ClaimAsync(Operation("no-room")));
            Assert.Null(await source.ReceiptAsync(first));
            await source.CompleteAsync(first, Receipt(first));
            Assert.Equal(ExecutorJournalClaimStatus.Completed, (await source.ClaimAsync(first)).Status);
        }
        using var reopened = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen));
        Assert.Equal(ExecutorJournalClaimStatus.Completed, (await reopened.ClaimAsync(first)).Status);
        Assert.Single(await reopened.OperationsAsync());
        await reopened.AuditAsync();
    }

    [Fact]
    public async Task ExplicitFullCopyKeepsLayoutOriginalPendingUnknownAndEveryReceipt()
    {
        using var f = new Fixture(); var pending = Operation("pending"); var done = Operation("done"); var unknown = Operation("unknown");
        using (var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options()))
        {
            foreach (var op in new[] { pending, done, unknown }) await source.ClaimAsync(op);
            await source.CompleteAsync(done, Receipt(done)); await source.CompleteAsync(unknown, Receipt(unknown, "unknown"));
        }
        var original = File.ReadAllBytes(f.Path); string target = f.Path + ".copy", staging = f.Path + ".stage";
        var rotated = new Key(); rotated.Value[0] ^= 1;
        using (var source = await SqliteExecutorJournal.OpenSourceAsync(f.Options(StorageOpenMode.Reopen)))
        { await source.AuditAsync(); await source.CopyToEncryptedAsync(target, staging, rotated); }
        Assert.Equal(original, File.ReadAllBytes(f.Path)); Assert.False(File.Exists(staging));
        var options = f.Options(StorageOpenMode.Reopen); options.Path = target; options.KeyProvider = rotated;
        using var copy = await SqliteExecutorJournal.OpenAsync(options);
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await copy.ClaimAsync(pending)).Status);
        Assert.Equal(Text(Receipt(done)), Text((await copy.ReceiptAsync(done))!.Value));
        Assert.Equal("unknown", (await copy.ReceiptAsync(unknown))!.Value.GetProperty("status").GetString());
        Assert.Equal(3, (await copy.OperationsAsync()).Count); await copy.AuditAsync();
    }

    [Fact]
    public async Task NativeMemoryAndTerminalSelectionAndDeviceOptionCopyActuallyOpenTheSameIndexedJournal()
    {
        var assembly = Assembly.Load("ConsoleAssistant");
        var terminal = assembly.GetType("Tansr.Examples.NativeTerminalDeviceHost", true)!;
        var device = assembly.GetType("Tansr.Examples.NativeDeviceHost", true)!;
        ExecutorJournalSqliteLayout Select(string? profile, string? layout) => (ExecutorJournalSqliteLayout)terminal.GetMethod("JournalLayout", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object?[] { profile, layout })!;
        Assert.Equal(ExecutorJournalSqliteLayout.LegacyAudited, Select("terminal-persistence-v1", null));
        Assert.Equal(ExecutorJournalSqliteLayout.LegacyAudited, Select("terminal-services-v1", "authenticated-indexed-v2"));
        Assert.Equal(ExecutorJournalSqliteLayout.AuthenticatedIndexed, Select("terminal-persistence-v1", "authenticated-indexed-v2"));
        using var f = new Fixture(); var options = f.Options(); options.Layout = Select("terminal-persistence-v1", "authenticated-indexed-v2");
        SqliteExecutorJournalOptions Copy() => (SqliteExecutorJournalOptions)device.GetMethod("JournalOptions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { options, f.Scope(), (Func<JsonElement>)f.Scope })!;
        var copied = Copy(); Assert.Equal(options.Layout, copied.Layout); Assert.Same(options.KeyProvider, copied.KeyProvider);
        using (var source = await SqliteExecutorJournal.OpenAsync(copied)) await source.ClaimAsync(Operation("native-key"));
        options.Mode = StorageOpenMode.Reopen;
        using var trace = new SqlTrace(f.Path);
        using (var reopened = await SqliteExecutorJournal.OpenAsync(Copy()))
        {
            var cold = trace.Take(); Assert.Equal(0, cold.Rows); Assert.Equal(0, cold.BodyBytes); Assert.Empty(trace.Errors);
            Assert.Equal(ExecutorJournalClaimStatus.Pending, (await reopened.ClaimAsync(Operation("native-key"))).Status);
        }
    }

    private static string Text(JsonElement value) => WireJson.CanonicalString(value);
    private static JsonElement Operation(string id)
    {
        var unsigned = JsonSerializer.SerializeToElement(new
        {
            protocol = "sdk2-ext-v1",
            operationId = id,
            sessionId = "session",
            scope = new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" },
            binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } },
            toolName = "Read",
            request = new { operation = "fs.read", args = new { path = "synthetic.txt", offset = 0, length = 1024 } },
            expiresAt = "2099-01-01T00:00:00.000Z",
        });
        return WireJson.Parse(Encoding.UTF8.GetBytes(unsigned.GetRawText().TrimEnd('}') + ",\"digest\":\"" + WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(unsigned)) + "\"}"));
    }
    private static JsonElement Receipt(JsonElement op, string status = "completed") => JsonSerializer.SerializeToElement(new
    {
        protocol = "sdk2-ext-v1",
        executorId = "executor",
        connectionId = "connection",
        operationId = op.GetProperty("operationId").GetString(),
        digest = op.GetProperty("digest").GetString(),
        status,
        result = status == "completed" ? (object)new { operation = "fs.read", args = new { bytesBase64 = "c3ludGhldGlj" } } : null,
        errorCode = status == "completed" ? null : "effect_unknown",
    });
    private sealed class Key : IArchiveKeyProvider
    {
        public string KeyId => "synthetic-journal";
        internal byte[] Value = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        public byte[] ReadKey() => Value.ToArray();
    }
    private sealed class NoRequests(Func<JsonElement> scope) : IExecutionClient
    {
        internal int Requests;
        public JsonElement ReadScope() => scope();
        private Task<JsonElement> Fail() { Requests++; throw new InvalidOperationException("unexpected recovery request"); }
        public Task<JsonElement> RegisterAsync(JsonElement registration, CancellationToken cancellationToken) => Fail();
        public Task<JsonElement> HeartbeatAsync(JsonElement connection, CancellationToken cancellationToken) => Fail();
        public Task<JsonElement> PollAsync(JsonElement connection, CancellationToken cancellationToken) => Fail();
        public Task<JsonElement> SubmitAsync(JsonElement receipt, CancellationToken cancellationToken) => Fail();
        public Task<JsonElement> GetStatusAsync(string sessionId, string operationId, CancellationToken cancellationToken) => Fail();
    }
    private sealed class Fixture : IDisposable
    {
        internal string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "net-source-journal-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "journal.sqlite");
        internal Key Key = new(); internal string Revision = "1"; internal bool Revoked;
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal JsonElement Scope() => Revoked ? throw new InvalidOperationException("synthetic revoked authority") : JsonSerializer.SerializeToElement(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = Revision });
        internal SqliteExecutorJournalOptions Options(StorageOpenMode mode = StorageOpenMode.Create) => new()
        { Path = Path, Mode = mode, Layout = ExecutorJournalSqliteLayout.AuthenticatedIndexed, CompactCompletedReceipts = true, KeyProvider = Key, ApplicationScopeId = "app", EndUserId = "user", ExecutorId = "executor", ReadContext = Scope };
        internal void Sql(string sql)
        { using var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False;Foreign Keys=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed record Counts(long Rows, long BodyBytes, long Scans, long Nodes);
    private sealed class SqlTrace : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Extension(IntPtr database, IntPtr error, IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Trace(uint kind, IntPtr context, IntPtr statement, IntPtr data);
        private readonly Extension extension; private readonly Trace trace; private readonly string path;
        private long rows, bodyBytes, scans, nodes;
        internal Action? BeforeHeadSave, AfterCommit;
        internal List<string> Errors { get; } = new();
        internal SqlTrace(string path)
        { this.path = Path.GetFullPath(path); extension = Opened; trace = Observe; Assert.Equal(0, sqlite3_auto_extension(Marshal.GetFunctionPointerForDelegate(extension))); }
        private int Opened(IntPtr handle, IntPtr error, IntPtr api)
        {
            try
            {
                string? file = Marshal.PtrToStringUTF8(sqlite3_db_filename(handle, "main"));
                if (file != null && Path.GetFullPath(file).Equals(path, StringComparison.OrdinalIgnoreCase) && sqlite3_trace_v2(handle, 1 | 2 | 4, trace, IntPtr.Zero) != 0) Errors.Add("trace-registration");
            }
            catch (Exception errorValue) { Errors.Add(errorValue.GetType().Name); }
            return 0;
        }
        private int Observe(uint kind, IntPtr context, IntPtr statement, IntPtr data)
        {
            try
            {
                string sql = Marshal.PtrToStringUTF8(sqlite3_sql(statement)) ?? "";
                if (kind == 1 && sql.StartsWith("INSERT INTO source_authentication VALUES", StringComparison.Ordinal)) BeforeHeadSave?.Invoke();
                if (kind == 2 && sql == "COMMIT") AfterCommit?.Invoke();
                bool operation = sql.Contains(" FROM operations", StringComparison.OrdinalIgnoreCase);
                if (sql.Contains("quick_check", StringComparison.OrdinalIgnoreCase)) Errors.Add("implicit-full-check");
                if (kind == 2 && operation) scans += sqlite3_stmt_status(statement, 1, 1);
                if (kind == 4)
                {
                    if (sql.Contains(" FROM source_authentication_nodes", StringComparison.OrdinalIgnoreCase)) nodes++;
                    if (operation)
                    {
                        rows++;
                        for (int i = 0; i < sqlite3_column_count(statement); i++)
                        {
                            string name = Marshal.PtrToStringUTF8(sqlite3_column_name(statement, i)) ?? "";
                            if (sqlite3_column_type(statement, i) == 3 && (name.Contains("operation", StringComparison.OrdinalIgnoreCase) || name.Contains("receipt", StringComparison.OrdinalIgnoreCase))) bodyBytes += sqlite3_column_bytes(statement, i);
                        }
                    }
                }
            }
            catch (Exception errorValue) { Errors.Add(errorValue.GetType().Name); }
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
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    }
}
