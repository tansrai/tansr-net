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
