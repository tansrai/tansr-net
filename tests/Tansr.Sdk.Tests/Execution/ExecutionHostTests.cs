using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Tests.Execution;

public sealed class ExecutionHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateEmptyPollBetweenSerialOperationsKeepsReceiptsAndCancelsIdleWait(bool stopAtIdleCap)
    {
        var batches = new Queue<JsonElement>();
        batches.Enqueue(ExecutionFixture.Batch());
        batches.Enqueue(ExecutionFixture.Batch(ExecutionFixture.Operation("serial-1")));
        batches.Enqueue(ExecutionFixture.Batch());
        batches.Enqueue(ExecutionFixture.Batch(ExecutionFixture.Operation("serial-2")));
        for (var i = 0; i < 6; i++) batches.Enqueue(ExecutionFixture.Batch());
        if (!stopAtIdleCap)
        {
            batches.Enqueue(ExecutionFixture.Batch(ExecutionFixture.Operation("serial-3")));
            batches.Enqueue(ExecutionFixture.Batch());
        }
        var client = new FakeClient { Poll = _ => Task.FromResult(batches.Dequeue()) };
        var journal = new FakeJournal(); var backend = new FakeBackend();
        var delays = new List<int>(); var waiting = Signal();
        async Task Delay(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); delays.Add(milliseconds);
            if (batches.Count != 0) return;
            waiting.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        using var host = new ExecutionHost(client, client, backend, journal, Allow, Delay);
        var run = host.RunAsync();
        try
        {
            await waiting.Task.WaitAsync(Deadline);
            var expected = stopAtIdleCap ? new[] { "serial-1", "serial-2" } : new[] { "serial-1", "serial-2", "serial-3" };
            Assert.Equal(expected, journal.Completions.Select(receipt => receipt.GetProperty("operationId").GetString()));
            Assert.All(journal.Completions, receipt => Assert.Equal("completed", receipt.GetProperty("status").GetString()));
            Assert.Equal(expected.Length, backend.SideEffects); Assert.Equal(expected.Length, client.SubmitCalls);
            var expectedDelays = new[] { 250, 25, 25, 50, 100, 200, 250, 250 };
            Assert.Equal(stopAtIdleCap ? expectedDelays : expectedDelays.Append(25), delays);
            var pollsAtStop = client.PollCalls;
            await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline);
            Assert.Equal(pollsAtStop, client.PollCalls); Assert.Empty(batches);
        }
        finally { await host.StopAsync().WaitAsync(Deadline); }
    }

    [Fact]
    public async Task ReadyBatchesDoNotPayIdleBackoffBetweenEveryDurableReceipt()
    {
        const int count = 32;
        var client = new FakeClient(); var journal = new FakeJournal(); var backend = new FakeBackend();
        for (var i = 0; i < count; i++) client.Enqueue(ExecutionFixture.Operation(id: "chunk-" + i));
        using var host = new ExecutionHost(client, backend, journal, Allow);
        using var collection = new CancellationTokenSource();
        Task? received = null;
        var run = host.RunAsync();
        try
        {
            async Task ReceiveAllAsync()
            {
                for (var i = 0; i < count; i++)
                {
                    var receipt = await client.Submissions.Reader.ReadAsync(collection.Token);
                    Assert.Equal("chunk-" + i, receipt.GetProperty("operationId").GetString());
                    Assert.Equal("completed", receipt.GetProperty("status").GetString());
                }
            }
            // In-memory ready work needs no idle wait. The old unconditional 250 ms per batch
            // imposed at least 7.75 seconds here and roughly 86 seconds on a 4 MiB publication.
            received = ReceiveAllAsync();
            await received.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(count, backend.SideEffects); Assert.Equal(count, journal.Completions.Count);
            Assert.Equal(0, client.StatusReads);
        }
        finally
        {
            collection.Cancel();
            if (received != null) { try { await received; } catch (OperationCanceledException) when (collection.IsCancellationRequested) { } }
            await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task RepeatedDeliverySubmitsTheSameReceiptWithoutRepeatingSideEffects()
    {
        var operation = ExecutionFixture.Operation();
        var client = new FakeClient();
        var journal = new FakeJournal();
        var backend = new FakeBackend();
        client.Enqueue(operation); client.Enqueue(operation);
        using var host = new ExecutionHost(client, backend, journal, Allow);
        var run = host.RunAsync();
        try
        {
            var first = await client.Submissions.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            var second = await client.Submissions.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.True(ExecutionFixture.Same(first, second));
            Assert.Equal("completed", first.GetProperty("status").GetString());
            Assert.Equal(1, backend.SideEffects);
            Assert.Single(journal.Completions);
            Assert.Equal(0, client.StatusReads);
        }
        finally { await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task PendingClaimAfterRestartBecomesUnknownAndIsNeverExecuted()
    {
        var operation = ExecutionFixture.Operation();
        var journal = new FakeJournal();
        Assert.Equal(ExecutorJournalClaimStatus.Claimed, (await journal.ClaimAsync(operation)).Status);
        var client = new FakeClient(); client.Enqueue(operation);
        var backend = new FakeBackend();
        using var host = new ExecutionHost(client, backend, journal, Allow);
        var run = host.RunAsync();
        try
        {
            var receipt = await client.Submissions.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.Equal("unknown", receipt.GetProperty("status").GetString());
            Assert.Equal("execution_outcome_unknown", receipt.GetProperty("errorCode").GetString());
            Assert.Equal(0, backend.SideEffects);
            Assert.Equal("unknown", (await journal.ReceiptAsync(operation))!.Value.GetProperty("status").GetString());
            Assert.False(journal.CompletionTokens.Single().CanBeCanceled);
        }
        finally { await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task LostSubmitResponseOnlyQueriesTheOriginalOperation()
    {
        var operation = ExecutionFixture.Operation();
        var client = new FakeClient { LoseSubmitResponse = true }; client.Enqueue(operation);
        var backend = new FakeBackend();
        var journal = new FakeJournal();
        client.Status = (session, id) =>
        {
            Assert.Equal("session-1", session); Assert.Equal("operation-1", id);
            return ExecutionFixture.Status(operation, journal.Completions.Single());
        };
        using var host = new ExecutionHost(client, backend, journal, Allow);
        var run = host.RunAsync();
        try
        {
            await client.StatusObserved.Task.WaitAsync(Deadline);
            Assert.Equal(1, client.SubmitCalls);
            Assert.Equal(1, client.StatusReads);
            Assert.Equal(1, backend.SideEffects);
            Assert.Single(journal.Completions);
        }
        finally { await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task UnconfirmedLostSubmitDoesNotClearTheDurableResultOrRerun()
    {
        var operation = ExecutionFixture.Operation();
        var client = new FakeClient { LoseSubmitResponse = true, Status = (_, _) => ExecutionFixture.Status(operation, null) };
        client.Enqueue(operation);
        var backend = new FakeBackend();
        var journal = new FakeJournal();
        using (var host = new ExecutionHost(client, backend, journal, Allow))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => host.RunAsync().WaitAsync(Deadline));
        }
        Assert.Equal(1, backend.SideEffects);
        Assert.Equal(1, client.SubmitCalls);
        Assert.Equal(1, client.StatusReads);
        var retryClient = new FakeClient(); retryClient.Enqueue(operation);
        using var resumed = new ExecutionHost(retryClient, backend, journal, Allow);
        var run = resumed.RunAsync();
        try
        {
            var receipt = await retryClient.Submissions.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.Equal("completed", receipt.GetProperty("status").GetString());
            Assert.Equal(1, backend.SideEffects);
            Assert.Single(journal.Completions);
        }
        finally { await resumed.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData("user")]
    [InlineData("application")]
    [InlineData("authorization")]
    [InlineData("connection")]
    [InlineData("workspace")]
    [InlineData("workspaceRevision")]
    public async Task ForeignOrStaleOperationNeverClaimsOrExecutes(string field)
    {
        var operation = ExecutionFixture.Operation(user: field == "user" ? "foreign" : "user",
            application: field == "application" ? "foreign" : "app", authorization: field == "authorization" ? "2" : "1",
            connectionRevision: field == "connection" ? "2" : "1", workspace: field == "workspace" ? "foreign" : "workspace-1",
            workspaceRevision: field == "workspaceRevision" ? "2" : "1");
        var client = new FakeClient(); client.Enqueue(operation);
        var journal = new FakeJournal(); var backend = new FakeBackend();
        using var host = new ExecutionHost(client, backend, journal, Allow);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.RunAsync().WaitAsync(Deadline));
        Assert.Equal(0, journal.ClaimCalls);
        Assert.Equal(0, backend.SideEffects);
        Assert.Equal(0, client.SubmitCalls);
    }

    [Fact]
    public async Task ScopeChangedWhileAwaitingApprovalDoesNotCreateAClaim()
    {
        var client = new FakeClient(); client.Enqueue(ExecutionFixture.Operation());
        var journal = new FakeJournal(); var backend = new FakeBackend();
        using var host = new ExecutionHost(client, backend, journal, (_, _) =>
        {
            client.Scope = ExecutionFixture.Scope(revision: "2");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => host.RunAsync().WaitAsync(Deadline));
        Assert.Equal(0, journal.ClaimCalls);
        Assert.Equal(0, backend.SideEffects);
    }

    [Fact]
    public async Task StopWaitsForBackendCleanupBeforeCompletingTheJournal()
    {
        var operation = ExecutionFixture.Operation();
        var entered = Signal(); var cancelled = Signal(); var release = Signal();
        var cleanupFinished = false;
        var backend = new FakeBackend
        {
            Execute = async (_, guard, cancellation) =>
            {
                await guard(cancellation);
                entered.TrySetResult(true);
                using var observer = cancellation.Register(() => cancelled.TrySetResult(true));
                await release.Task;
                cleanupFinished = true;
                throw new OperationCanceledException(cancellation);
            }
        };
        var client = new FakeClient(); client.Enqueue(operation);
        var journal = new FakeJournal { BeforeComplete = () => Assert.True(cleanupFinished) };
        using var host = new ExecutionHost(client, backend, journal, Allow);
        var run = host.RunAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            var stopping = host.StopAsync();
            await cancelled.Task.WaitAsync(Deadline);
            Assert.False(stopping.IsCompleted);
            Assert.Empty(journal.Completions);
            release.TrySetResult(true);
            await stopping.WaitAsync(Deadline);
            Assert.True(cleanupFinished);
            Assert.Equal("unknown", journal.Completions.Single().GetProperty("status").GetString());
            Assert.False(journal.CompletionTokens.Single().CanBeCanceled);
            Assert.Equal(0, client.SubmitCalls);
        }
        finally { release.TrySetResult(true); await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task ServerStatusBecomesUnknownWhileBackendRunsCancelsAndWaitsForCleanup()
    {
        var operation = ExecutionFixture.Operation();
        var entered = Signal(); var cancelled = Signal(); var release = Signal();
        var cleanupFinished = false;
        var executions = 0;
        var backend = new FakeBackend
        {
            Execute = async (_, guard, cancellation) =>
            {
                await guard(cancellation);
                Interlocked.Increment(ref executions);
                entered.TrySetResult(true);
                using var observer = cancellation.Register(() => cancelled.TrySetResult(true));
                await release.Task;
                cleanupFinished = true;
                throw new OperationCanceledException(cancellation);
            }
        };
        var client = new FakeClient
        {
            Status = (_, _) => ExecutionFixture.Status(operation, ExecutionFixture.Receipt(operation, "unknown"))
        };
        client.Enqueue(operation);
        var journal = new FakeJournal { BeforeComplete = () => Assert.True(cleanupFinished) };
        using var host = new ExecutionHost(client, backend, journal, Allow);
        var run = host.RunAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await cancelled.Task.WaitAsync(Deadline);
            Assert.False(cleanupFinished);
            Assert.Empty(journal.Completions);
            Assert.Equal(0, client.SubmitCalls);
            release.TrySetResult(true);
            var receipt = await client.Submissions.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.True(cleanupFinished);
            Assert.Equal("unknown", receipt.GetProperty("status").GetString());
            Assert.Equal(1, executions);
            Assert.Single(journal.Completions);
            Assert.False(journal.CompletionTokens.Single().CanBeCanceled);
            Assert.Equal(1, client.StatusReads);
        }
        finally { release.TrySetResult(true); await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task FailedJournalCompletionPreservesPendingAndNoRemoteReceiptIsSent()
    {
        var client = new FakeClient(); var operation = ExecutionFixture.Operation(); client.Enqueue(operation);
        var backend = new FakeBackend();
        var journal = new FakeJournal { BeforeComplete = () => throw new StorageException("storage_error") };
        using var host = new ExecutionHost(client, backend, journal, Allow);
        var error = await Assert.ThrowsAsync<StorageException>(() => host.RunAsync().WaitAsync(Deadline));
        Assert.Equal("storage_error", error.Code);
        Assert.Equal(1, backend.SideEffects);
        Assert.Equal(0, client.SubmitCalls);
        Assert.Equal(ExecutorJournalClaimStatus.Pending, (await journal.ClaimAsync(operation)).Status);
    }

    [Fact]
    public async Task PrincipalChangedDuringJournalCommitKeepsReceiptLocal()
    {
        var enteringCommit = Signal(); var releaseCommit = Signal();
        var client = new FakeClient { RejectForeignSubmit = true };
        var operation = ExecutionFixture.Operation(); client.Enqueue(operation);
        var backend = new FakeBackend();
        var journal = new FakeJournal
        {
            WaitBeforeComplete = async () => { enteringCommit.TrySetResult(true); await releaseCommit.Task; }
        };
        using var host = new ExecutionHost(client, backend, journal, Allow);
        var run = host.RunAsync();
        try
        {
            await enteringCommit.Task.WaitAsync(Deadline);
            client.Scope = ExecutionFixture.Scope(user: "other-user");
            releaseCommit.TrySetResult(true);
            await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(Deadline));
            Assert.Equal(1, backend.SideEffects);
            Assert.Equal("completed", journal.Completions.Single().GetProperty("status").GetString());
            Assert.Equal(0, client.SubmitCalls);
            Assert.Equal(0, client.StatusReads);
        }
        finally { releaseCommit.TrySetResult(true); host.Dispose(); }
    }

    private static Task Allow(JsonElement _, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    [Fact]
    public async Task SeparateControllerReconcilesLostDeviceReceiptWithoutElevatingDeviceCredential()
    {
        var operation = ExecutionFixture.Operation(); var journal = new FakeJournal();
        var device = new FakeClient { LoseSubmitResponse = true }; device.Enqueue(operation);
        var controller = new FakeClient { Status = (_, _) => ExecutionFixture.Status(operation, journal.Completions.Single()) };
        var backend = new FakeBackend();
        using var host = new ExecutionHost(device, controller, backend, journal, Allow);
        var run = host.RunAsync();
        try
        {
            await controller.StatusObserved.Task.WaitAsync(Deadline);
            Assert.Equal(1, device.SubmitCalls); Assert.Equal(0, device.StatusReads);
            Assert.Equal(1, controller.StatusReads); Assert.Equal(0, controller.SubmitCalls);
            Assert.Equal(1, backend.SideEffects); Assert.Single(journal.Completions);
        }
        finally { await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task ForeignObservationPrincipalCannotReadLostReceipt()
    {
        var operation = ExecutionFixture.Operation(); var journal = new FakeJournal();
        var device = new FakeClient { LoseSubmitResponse = true }; device.Enqueue(operation);
        var controller = new FakeClient { Scope = ExecutionFixture.Scope(user: "foreign") };
        var backend = new FakeBackend();
        using var host = new ExecutionHost(device, controller, backend, journal, Allow);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.RunAsync().WaitAsync(Deadline));
        Assert.Equal(0, controller.StatusReads); Assert.Equal(0, device.StatusReads);
        Assert.Equal(1, backend.SideEffects);
        Assert.Equal("completed", journal.Completions.Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task ActiveOperationMonitoringUsesControllerWithoutSendingItDeviceReceipts()
    {
        var operation = ExecutionFixture.Operation(); var journal = new FakeJournal();
        var device = new FakeClient(); device.Enqueue(operation);
        var controller = new FakeClient { Status = (_, _) => ExecutionFixture.Status(operation, null) };
        var backend = new FakeBackend
        {
            Execute = async (_, _, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ExecutionFixture.Result(); }
        };
        using var host = new ExecutionHost(device, controller, backend, journal, Allow);
        var run = host.RunAsync();
        try
        {
            await controller.StatusObserved.Task.WaitAsync(Deadline);
            Assert.Equal(0, device.StatusReads); Assert.Equal(0, controller.SubmitCalls);
        }
        finally { await host.StopAsync().WaitAsync(Deadline); await run.WaitAsync(Deadline); }
        Assert.Equal("unknown", journal.Completions.Single().GetProperty("status").GetString());
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeBackend : IExecutionBackend
    {
        public JsonElement Registration => ExecutionFixture.Registration();
        public int SideEffects;
        public Func<JsonElement, Func<CancellationToken, Task>, CancellationToken, Task<JsonElement>>? Execute;
        public async Task<JsonElement> ExecuteAsync(JsonElement operation, Func<CancellationToken, Task> guard, CancellationToken cancellationToken)
        {
            if (Execute != null) return await Execute(operation, guard, cancellationToken);
            await guard(cancellationToken);
            Interlocked.Increment(ref SideEffects);
            return ExecutionFixture.Result();
        }
    }

    private sealed class FakeClient : IExecutionClient
    {
        private readonly Channel<JsonElement> _batches = Channel.CreateUnbounded<JsonElement>();
        public Channel<JsonElement> Submissions { get; } = Channel.CreateUnbounded<JsonElement>();
        public TaskCompletionSource<bool> StatusObserved { get; } = Signal();
        public JsonElement Scope = ExecutionFixture.Scope();
        public Func<string, string, JsonElement>? Status;
        public Func<CancellationToken, Task<JsonElement>>? Poll;
        public bool LoseSubmitResponse;
        public bool RejectForeignSubmit;
        public int SubmitCalls, StatusReads, PollCalls;
        public void Enqueue(JsonElement operation) => _batches.Writer.TryWrite(ExecutionFixture.Batch(operation));
        public JsonElement ReadScope() => Scope.Clone();
        public Task<JsonElement> RegisterAsync(JsonElement registration, CancellationToken cancellationToken) => Task.FromResult(ExecutionFixture.Connection());
        public Task<JsonElement> HeartbeatAsync(JsonElement connection, CancellationToken cancellationToken) => Task.FromResult(ExecutionFixture.Connection());
        public async Task<JsonElement> PollAsync(JsonElement connection, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PollCalls);
            return Poll == null ? await _batches.Reader.ReadAsync(cancellationToken) : await Poll(cancellationToken);
        }
        public Task<JsonElement> SubmitAsync(JsonElement receipt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SubmitCalls);
            if (RejectForeignSubmit && Scope.GetProperty("endUserId").GetString() != "user")
                throw new InvalidOperationException("old receipt was sent with another principal");
            Submissions.Writer.TryWrite(receipt.Clone());
            if (LoseSubmitResponse) throw new IOException("synthetic response loss after server acceptance");
            return Task.FromResult(default(JsonElement));
        }
        public Task<JsonElement> GetStatusAsync(string sessionId, string operationId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StatusReads);
            var result = Status?.Invoke(sessionId, operationId) ?? throw new InvalidOperationException("unexpected status request");
            StatusObserved.TrySetResult(true);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeJournal : IExecutorJournal
    {
        private readonly Dictionary<string, (JsonElement Operation, JsonElement? Receipt)> _records = new(StringComparer.Ordinal);
        public ConcurrentQueue<JsonElement> Completions { get; } = new();
        public ConcurrentQueue<CancellationToken> CompletionTokens { get; } = new();
        public Action? BeforeComplete;
        public Func<Task>? WaitBeforeComplete;
        public int ClaimCalls;
        public Task<ExecutorJournalClaim> ClaimAsync(JsonElement operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref ClaimCalls);
            lock (_records)
            {
                var id = operation.GetProperty("operationId").GetString()!;
                if (_records.TryGetValue(id, out var existing))
                {
                    if (!ExecutionFixture.Same(existing.Operation, operation)) throw new StorageException("integrity_mismatch");
                    return Task.FromResult(existing.Receipt.HasValue ? ExecutorJournalClaim.Completed(existing.Receipt.Value) : ExecutorJournalClaim.Pending());
                }
                _records.Add(id, (operation.Clone(), null));
                return Task.FromResult(ExecutorJournalClaim.Claimed());
            }
        }
        public async Task CompleteAsync(JsonElement operation, JsonElement receipt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); BeforeComplete?.Invoke();
            if (WaitBeforeComplete != null) await WaitBeforeComplete();
            lock (_records)
            {
                var id = operation.GetProperty("operationId").GetString()!;
                var original = _records[id];
                if (original.Receipt.HasValue && !ExecutionFixture.Same(original.Receipt.Value, receipt)) throw new StorageException("receipt_mismatch");
                _records[id] = (original.Operation, receipt.Clone());
                CompletionTokens.Enqueue(cancellationToken); Completions.Enqueue(receipt.Clone());
            }
        }
        public Task<JsonElement?> ReceiptAsync(JsonElement operation, CancellationToken cancellationToken = default)
        {
            lock (_records) return Task.FromResult(_records[operation.GetProperty("operationId").GetString()!].Receipt?.Clone());
        }
        public Task<IReadOnlyList<JsonElement>> OperationsAsync(string? afterOperationId = null, CancellationToken cancellationToken = default)
        {
            lock (_records) return Task.FromResult<IReadOnlyList<JsonElement>>(_records.Values.Select(x => x.Operation.Clone()).ToArray());
        }
        public Task CloseAsync() => Task.CompletedTask;
    }
}
