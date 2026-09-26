using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Tests.Execution;

public sealed class ExecutionRecoveryTests
{
    private static readonly JsonElement Original = ExecutionFixture.Operation();
    [Fact]
    public async Task PendingIsReportedUnknownWithoutReexecutingOrSubmitting()
    {
        var client = new Client(); var journal = new Journal();
        var result = await new ExecutionRecovery(client, journal).ReconcilePageAsync();
        Assert.Equal(ExecutionRecoveryDisposition.OutcomeUnknown, Assert.Single(result.Items).Disposition);
        Assert.Equal("operation-1", result.AfterOperationId);
        Assert.Equal(0, client.Submits); Assert.Null(journal.Local);
        Assert.Null((await new ExecutionRecovery(client, journal).ReconcilePageAsync(result.AfterOperationId)).AfterOperationId);
    }
    [Fact]
    public async Task CompletedLocalReceiptIsSubmittedOnceWithOriginalKey()
    {
        var client = new Client(); var journal = new Journal { Local = ExecutionFixture.Receipt(Original) };
        var result = await new ExecutionRecovery(client, journal).ReconcilePageAsync();
        Assert.Equal(ExecutionRecoveryDisposition.Submitted, Assert.Single(result.Items).Disposition);
        Assert.Equal(1, client.Submits);
        Assert.True(ExecutionFixture.Same(journal.Local.Value, client.Remote!.Value));
    }
    [Fact]
    public async Task LostSubmitResponseQueriesSameOperationInsteadOfSubmittingAgain()
    {
        var client = new Client { LoseResponse = true }; var journal = new Journal { Local = ExecutionFixture.Receipt(Original) };
        var result = await new ExecutionRecovery(client, journal).ReconcilePageAsync();
        Assert.Equal(ExecutionRecoveryDisposition.Confirmed, Assert.Single(result.Items).Disposition);
        Assert.Equal(1, client.Submits); Assert.Equal(2, client.Queries);
    }
    [Fact]
    public async Task RemoteTerminalIsArchivedButConflictingLocalFactIsNeverOverwritten()
    {
        var client = new Client { Remote = ExecutionFixture.Receipt(Original) }; var journal = new Journal();
        var recovery = new ExecutionRecovery(client, journal);
        Assert.Equal(ExecutionRecoveryDisposition.Confirmed, Assert.Single((await recovery.ReconcilePageAsync()).Items).Disposition);
        Assert.True(ExecutionFixture.Same(journal.Local!.Value, client.Remote!.Value));
        client.Remote = ExecutionFixture.Receipt(Original, "unknown");
        Assert.Equal(ExecutionRecoveryDisposition.Conflict, Assert.Single((await recovery.ReconcilePageAsync()).Items).Disposition);
        Assert.Equal("completed", journal.Local.Value.GetProperty("status").GetString());
        Assert.Equal(0, client.Submits);
    }
    [Fact]
    public async Task DifferentSubjectAndIdentityChangesAbortRecoveryBeforeWriting()
    {
        var client = new Client { User = "other" }; var journal = new Journal();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionRecovery(client, journal).ReconcilePageAsync());
        Assert.Equal(0, client.Queries);
        client.User = "user"; client.ChangeAfterQuery = true;
        client.Remote = ExecutionFixture.Receipt(Original);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionRecovery(client, journal).ReconcilePageAsync());
        Assert.Null(journal.Local);
    }
    private sealed class Journal : IExecutorJournal
    {
        public JsonElement? Local;
        public Task<IReadOnlyList<JsonElement>> OperationsAsync(string? afterOperationId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JsonElement>>(afterOperationId == null ? [Original] : []);
        public Task<JsonElement?> ReceiptAsync(JsonElement op, CancellationToken ct = default) => Task.FromResult(Local);
        public Task CompleteAsync(JsonElement op, JsonElement receipt, CancellationToken ct = default) { Local = receipt; return Task.CompletedTask; }
        public Task<ExecutorJournalClaim> ClaimAsync(JsonElement op, CancellationToken ct = default) => throw new InvalidOperationException("Recovery must never acquire execution permission.");
        public Task CloseAsync() => Task.CompletedTask;
    }
    private sealed class Client : IExecutionClient
    {
        public int Submits, Queries; public bool LoseResponse, ChangeAfterQuery;
        public string User = "user"; public JsonElement? Remote;
        public JsonElement ReadScope() => ExecutionFixture.Scope(User, "2"); // historical revision 1 is legitimate
        public Task<JsonElement> RegisterAsync(JsonElement input, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> HeartbeatAsync(JsonElement input, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> PollAsync(JsonElement input, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> SubmitAsync(JsonElement receipt, CancellationToken ct)
        {
            Submits++; Remote = receipt;
            if (LoseResponse) throw new IOException("synthetic response loss");
            return Task.FromResult(ExecutionFixture.Status(Original, Remote));
        }
        public Task<JsonElement> GetStatusAsync(string session, string op, CancellationToken ct)
        {
            Assert.Equal("session-1", session); Assert.Equal("operation-1", op); Queries++;
            if (ChangeAfterQuery) User = "other";
            return Task.FromResult(ExecutionFixture.Status(Original, Remote));
        }
    }
}
