using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteExecutorJournalTests
{
    [Fact]
    public async Task ClaimIsDurableAndNeverGrantedAgainAfterReopen()
    {
        using var fixture = new Fixture(); var op = Operation();
        using (var journal = await SqliteExecutorJournal.OpenAsync(fixture.Options()))
        {
            Assert.Equal(ExecutorJournalClaimStatus.Claimed, (await journal.ClaimAsync(op)).Status);
            Assert.Equal(ExecutorJournalClaimStatus.Pending, (await journal.ClaimAsync(op)).Status);
        }
        using var reopened = await SqliteExecutorJournal.OpenAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await reopened.ClaimAsync(op)).Status);
        Assert.Null(await reopened.ReceiptAsync(op));
        Assert.Single(await reopened.OperationsAsync());
    }

    [Fact]
    public async Task CompletionSurvivesReopenAndIsImmutable()
    {
        using var fixture = new Fixture(); var op = Operation(); var receipt = Receipt(op);
        using (var journal = await SqliteExecutorJournal.OpenAsync(fixture.Options()))
        {
            await journal.ClaimAsync(op); await journal.CompleteAsync(op, receipt); await journal.CompleteAsync(op, receipt);
            var altered = Receipt(op, "unknown");
            Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => journal.CompleteAsync(op, altered))).Code);
        }
        using var reopened = await SqliteExecutorJournal.OpenAsync(fixture.Options(StorageOpenMode.Reopen));
        var prior = await reopened.ClaimAsync(op);
        Assert.Equal(ExecutorJournalClaimStatus.Completed, prior.Status);
        Assert.Equal(WireJson.CanonicalString(receipt), WireJson.CanonicalString(prior.Receipt!.Value));
    }

    [Fact]
    public async Task UnknownReceiptIsAlsoTerminalAndCannotGrantAnotherExecution()
    {
        using var fixture = new Fixture(); var op = Operation();
        using var journal = await SqliteExecutorJournal.OpenAsync(fixture.Options());
        await journal.ClaimAsync(op); await journal.CompleteAsync(op, Receipt(op, "unknown"));
        var result = await journal.ClaimAsync(op);
        Assert.Equal(ExecutorJournalClaimStatus.Completed, result.Status);
        Assert.Equal("unknown", result.Receipt!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DigestMutationAndWrongReceiptAreRejected()
    {
        using var fixture = new Fixture(); using var journal = await SqliteExecutorJournal.OpenAsync(fixture.Options());
        var op = Operation(); await journal.ClaimAsync(op);
        var different = Operation(content: "Yg==");
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => journal.ClaimAsync(different))).Code);
        Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => journal.CompleteAsync(op, Receipt(different)))).Code);
        Assert.Null(await journal.ReceiptAsync(op));
    }

    [Fact]
    public async Task SimultaneousClaimsHaveOneWinnerAndQuotaRetainsTerminalReserve()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.MaxOperations = 1;
        using var journal = await SqliteExecutorJournal.OpenAsync(options); var op = Operation();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => journal.ClaimAsync(op))));
        Assert.Single(results, x => x.Status == ExecutorJournalClaimStatus.Claimed);
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => journal.ClaimAsync(Operation("operation2")))).Code);
        await journal.CompleteAsync(op, Receipt(op));
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => journal.ClaimAsync(Operation("operation2")))).Code);
    }

    [Fact]
    public async Task ScopeChangesBlockNewExecutionButOriginalFactCanSettleAfterRevisionChange()
    {
        using var fixture = new Fixture(); var scope = Scope(); var options = fixture.Options(); options.ReadContext = () => scope;
        using var journal = await SqliteExecutorJournal.OpenAsync(options); var op = Operation(); await journal.ClaimAsync(op);
        scope = Scope(revision: "2");
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => journal.ClaimAsync(op))).Code);
        await journal.CompleteAsync(op, Receipt(op));
        scope = Scope(user: "other");
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => journal.ReceiptAsync(op))).Code);
    }

    [Fact]
    public async Task CreateNeverOverwritesAndCopiedDatabaseCannotImpersonateOriginalMedium()
    {
        using var fixture = new Fixture();
        using (var journal = await SqliteExecutorJournal.OpenAsync(fixture.Options())) await journal.ClaimAsync(Operation());
        await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenAsync(fixture.Options()));
        var copy = fixture.Options(StorageOpenMode.Reopen); copy.Path = System.IO.Path.Combine(fixture.Directory, "copy.sqlite");
        File.Copy(fixture.Path, copy.Path);
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenAsync(copy))).Code);
    }

    [Fact]
    public async Task CorruptClaimIsNotRepairedIntoFreshExecution()
    {
        using var fixture = new Fixture();
        using (var journal = await SqliteExecutorJournal.OpenAsync(fixture.Options())) await journal.ClaimAsync(Operation());
        using (var connection = new SqliteConnection("Data Source=" + fixture.Path + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE operations SET reserve=zeroblob(1)"; command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenAsync(fixture.Options(StorageOpenMode.Reopen)));
    }

    [Fact]
    public async Task CompactJournalCompletesMoreThan400SmallOperationsWithinOriginalDefaultQuota()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.CompactCompletedReceipts = true;
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            for (int index = 0; index < 420; index++)
            {
                var op = Operation("operation-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(ExecutorJournalClaimStatus.Claimed, (await journal.ClaimAsync(op)).Status);
                await journal.CompleteAsync(op, Receipt(op));
            }
        }
        var state = ReadState(fixture.Path);
        Assert.Equal(SqliteExecutorJournal.CompactFormat, state.Format);
        Assert.Equal(420, state.Count);
        Assert.Equal(state.ActualBytes, state.LogicalBytes);
        Assert.InRange(state.LogicalBytes, 1, options.MaxStoredBytes - 262144);
        Assert.Equal(0, state.PendingReserve); Assert.Equal(0, state.CompletedReserve);

        options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteExecutorJournal.OpenAsync(options);
        foreach (string id in new[] { "operation-000", "operation-419" })
        {
            var op = Operation(id); var prior = await reopened.ClaimAsync(op);
            Assert.Equal(ExecutorJournalClaimStatus.Completed, prior.Status);
            Assert.Equal(WireJson.CanonicalString(Receipt(op)), WireJson.CanonicalString(prior.Receipt!.Value));
            await reopened.CompleteAsync(op, Receipt(op));
            Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => reopened.CompleteAsync(op, Receipt(op, "unknown")))).Code);
        }
    }

    [Fact]
    public async Task CompactPendingKeepsFullReserveAndUnknownCompletionNeverRegrantsOrReleasesOperationCount()
    {
        using var fixture = new Fixture(); var options = fixture.Options();
        options.CompactCompletedReceipts = true; options.MaxStoredBytes = 400000; options.MaxOperations = 2;
        var first = Operation("first"); var second = Operation("second");
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            await journal.ClaimAsync(first);
            Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => journal.ClaimAsync(second))).Code);
        }
        var pending = ReadState(fixture.Path);
        Assert.Equal(1, pending.Count); Assert.Equal(262144, pending.PendingReserve);
        Assert.Equal(pending.ActualBytes, pending.LogicalBytes);
        options.Mode = StorageOpenMode.Reopen;
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            Assert.Equal(ExecutorJournalClaimStatus.Pending, (await journal.ClaimAsync(first)).Status);
            await journal.CompleteAsync(first, Receipt(first, "unknown"));
            Assert.Equal(ExecutorJournalClaimStatus.Claimed, (await journal.ClaimAsync(second)).Status);
            await journal.CompleteAsync(second, Receipt(second));
            Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => journal.ClaimAsync(Operation("third")))).Code);
        }
        var completed = ReadState(fixture.Path);
        Assert.Equal(2, completed.Count); Assert.Equal(0, completed.CompletedReserve); Assert.Equal(0, completed.PendingReserve);
        Assert.Equal(completed.ActualBytes, completed.LogicalBytes);
        using var reopened = await SqliteExecutorJournal.OpenAsync(options);
        var result = await reopened.ClaimAsync(first);
        Assert.Equal(ExecutorJournalClaimStatus.Completed, result.Status);
        Assert.Equal("unknown", result.Receipt!.Value.GetProperty("status").GetString());
        await reopened.CompleteAsync(first, Receipt(first, "unknown"));
        Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => reopened.CompleteAsync(first, Receipt(first)))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAndCompactFormatsRequireMatchingExplicitChoiceWithoutRewritingExistingFacts(bool compact)
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.CompactCompletedReceipts = compact;
        var op = Operation(); var expected = Receipt(op);
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        { await journal.ClaimAsync(op); await journal.CompleteAsync(op, expected); }
        var before = ReadState(fixture.Path);
        Assert.Equal(compact ? SqliteExecutorJournal.CompactFormat : SqliteExecutorJournal.Format, before.Format);
        Assert.Equal(compact ? 0 : 262144 - Encoding.UTF8.GetByteCount(WireJson.CanonicalString(expected)), before.CompletedReserve);
        Assert.Equal(before.ActualBytes, before.LogicalBytes);
        var originalBytes = File.ReadAllBytes(fixture.Path);
        options.Mode = StorageOpenMode.Reopen; options.CompactCompletedReceipts = !compact;
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenAsync(options))).Code);
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.Path));
        Assert.Equal(before, ReadState(fixture.Path));
        options.CompactCompletedReceipts = compact;
        using var reopened = await SqliteExecutorJournal.OpenAsync(options);
        var prior = await reopened.ClaimAsync(op);
        Assert.Equal(ExecutorJournalClaimStatus.Completed, prior.Status);
        Assert.Equal(WireJson.CanonicalString(expected), WireJson.CanonicalString(prior.Receipt!.Value));
    }

    [Fact]
    public async Task CanceledCompactCompletionKeepsDurablePendingAndItsEntireReservation()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.CompactCompletedReceipts = true;
        var op = Operation();
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            await journal.ClaimAsync(op);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.CompleteAsync(op, Receipt(op), canceled.Token));
            Assert.Null(await journal.ReceiptAsync(op));
        }
        var pending = ReadState(fixture.Path); Assert.Equal(262144, pending.PendingReserve);
        Assert.Equal(pending.ActualBytes, pending.LogicalBytes);
        options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteExecutorJournal.OpenAsync(options);
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await reopened.ClaimAsync(op)).Status);
        await reopened.CompleteAsync(op, Receipt(op, "unknown"));
        Assert.Equal(ExecutorJournalClaimStatus.Completed, (await reopened.ClaimAsync(op)).Status);
    }

    [Fact]
    public async Task CompactCompletionRollbackKeepsPendingAndRepeatedCommitDoesNotDeductTwice()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.CompactCompletedReceipts = true;
        int checksUntilFailure = 0;
        options.ReadContext = () =>
        {
            if (checksUntilFailure > 0 && --checksUntilFailure == 0) throw new InvalidOperationException("authority unavailable before commit");
            return Scope();
        };
        var op = Operation();
        using (var journal = await SqliteExecutorJournal.OpenAsync(options)) await journal.ClaimAsync(op);
        var pending = ReadState(fixture.Path);
        options.Mode = StorageOpenMode.Reopen;
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            // Run 初读、操作前校验通过；原事务 check 在 receipt/state 更新后、COMMIT 前失败。
            checksUntilFailure = 3;
            Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => journal.CompleteAsync(op, Receipt(op)))).Code);
            Assert.Null(await journal.ReceiptAsync(op));
        }
        Assert.Equal(pending, ReadState(fixture.Path));
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        {
            Assert.Equal(ExecutorJournalClaimStatus.Pending, (await journal.ClaimAsync(op)).Status);
            await journal.CompleteAsync(op, Receipt(op));
        }
        var committed = ReadState(fixture.Path);
        Assert.Equal(pending.LogicalBytes - 262144 + Encoding.UTF8.GetByteCount(WireJson.CanonicalString(Receipt(op))), committed.LogicalBytes);
        Assert.Equal(committed.ActualBytes, committed.LogicalBytes);
        using (var journal = await SqliteExecutorJournal.OpenAsync(options)) await journal.CompleteAsync(op, Receipt(op));
        Assert.Equal(committed, ReadState(fixture.Path));
    }

    [Theory]
    [InlineData("terminal-reserve")]
    [InlineData("pending-reserve")]
    [InlineData("logical-bytes")]
    public async Task CompactReopenRejectsTamperedReserveAndAccountingInsteadOfRegranting(string change)
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.CompactCompletedReceipts = true;
        var op = Operation();
        using (var journal = await SqliteExecutorJournal.OpenAsync(options))
        { await journal.ClaimAsync(op); await journal.CompleteAsync(op, Receipt(op)); await journal.ClaimAsync(Operation("pending")); }
        using (var connection = new SqliteConnection("Data Source=" + fixture.Path + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = change switch
            {
                "terminal-reserve" => "UPDATE operations SET reserve=zeroblob(1) WHERE receipt IS NOT NULL; UPDATE state SET logical_bytes=logical_bytes+1",
                "pending-reserve" => "UPDATE operations SET reserve=zeroblob(0) WHERE receipt IS NULL; UPDATE state SET logical_bytes=logical_bytes-262144",
                _ => "UPDATE state SET logical_bytes=logical_bytes+1",
            };
            command.ExecuteNonQuery();
        }
        options.Mode = StorageOpenMode.Reopen;
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteExecutorJournal.OpenAsync(options))).Code);
    }

    private static (int Count, long LogicalBytes, long ActualBytes, long PendingReserve, long CompletedReserve, string Format) ReadState(string path)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False;Mode=ReadOnly"); connection.Open();
        object Scalar(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()!; }
        long Number(string sql) => Convert.ToInt64(Scalar(sql), System.Globalization.CultureInfo.InvariantCulture);
        var metadata = (string)Scalar("SELECT json FROM metadata WHERE id=1");
        long actualBytes = Encoding.UTF8.GetByteCount(metadata) + 128 + Number("SELECT COALESCE(SUM(length(CAST(id AS BLOB))+length(CAST(operation AS BLOB))+COALESCE(length(CAST(receipt AS BLOB)),0)+length(reserve)),0) FROM operations");
        return (checked((int)Number("SELECT operation_count FROM state WHERE id=1")), Number("SELECT logical_bytes FROM state WHERE id=1"), actualBytes,
            Number("SELECT COALESCE(SUM(length(reserve)),0) FROM operations WHERE receipt IS NULL"),
            Number("SELECT COALESCE(SUM(length(reserve)),0) FROM operations WHERE receipt IS NOT NULL"), Parse(metadata).GetProperty("format").GetString()!);
    }

    private static JsonElement Scope(string user = "user", string revision = "1") => Parse("{\"applicationScopeId\":\"app\",\"endUserId\":\"" + user + "\",\"authorizationRevision\":\"" + revision + "\"}");
    private static JsonElement Parse(string text) => WireJson.Parse(Encoding.UTF8.GetBytes(text));

    private static JsonElement Operation(string id = "operation", string content = "YQ==")
    {
        var unsigned = Parse($$$"""{"protocol":"sdk2-ext-v1","operationId":"{{{id}}}","sessionId":"session","scope":{"applicationScopeId":"app","endUserId":"user","authorizationRevision":"1"},"binding":{"bindingId":"binding","revision":"1","target":{"executorId":"executor","connectionId":"connection","connectionRevision":"1","workspaceId":"workspace","workspaceRevision":"1"}},"toolName":"Write","request":{"operation":"fs.write","args":{"path":"file.txt","expectedHash":null,"bytesBase64":"{{{content}}}"}},"expiresAt":"2026-09-26T23:59:59.000Z"}""");
        var digest = WireJson.DomainDigest("tansr.sdk2.execution.v1", WireJson.EncodeControl(unsigned));
        return Parse(unsigned.GetRawText().TrimEnd('}') + ",\"digest\":\"" + digest + "\"}");
    }

    private static JsonElement Receipt(JsonElement op, string status = "completed")
    {
        string digest = op.GetProperty("digest").GetString()!, id = op.GetProperty("operationId").GetString()!;
        var result = status == "completed" ? "{\"operation\":\"fs.write\",\"args\":{\"hash\":\"" + WireJson.Sha256(WireJson.DecodeBase64(op.GetProperty("request").GetProperty("args").GetProperty("bytesBase64").GetString()!)) + "\"}}" : "null";
        return Parse($$$"""{"protocol":"sdk2-ext-v1","executorId":"executor","connectionId":"connection","operationId":"{{{id}}}","digest":"{{{digest}}}","status":"{{{status}}}","result":{{{result}}},"errorCode":{{{(status == "completed" ? "null" : "\"effect_unknown\"")}}}}""");
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr-net-journal-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "journal.sqlite");
        public Fixture() => System.IO.Directory.CreateDirectory(Directory);
        public SqliteExecutorJournalOptions Options(StorageOpenMode mode = StorageOpenMode.Create) => new SqliteExecutorJournalOptions
        {
            Path = Path,
            Mode = mode,
            ApplicationScopeId = "app",
            EndUserId = "user",
            ExecutorId = "executor",
            ReadContext = () => Scope(),
        };
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
