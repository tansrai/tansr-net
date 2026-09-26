using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalExecutionOutputSinkTests
{
    [Fact]
    public async Task ProcessChunksTransmitBeforeSealAndSuccessfulDisposeReleasesCapture()
    {
        var adapter = new TerminalTestAdapter(); var bound = await adapter.BindAsync();
        using var sink = new TerminalExecutionOutputSink(bound.Client, bound.Binding);
        using var capture = await sink.OpenAsync(adapter.ExistingOperation, default);
        capture.Append("stdout", "utf-8", [0xe4]);
        await adapter.BatchObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(capture.Completion.IsCompleted);
        capture.Append("stdout", "utf-8", [0xb8, 0xad]);
        await capture.SealAsync(false, default);
        var retained = sink.FindCapture(TerminalJson.Text(adapter.ExistingOperation, "operationId"))!;
        Assert.Equal("complete", retained.LastStatus!.State); Assert.Null(retained.LastStatus.DurableThrough);
        capture.Dispose();
        Assert.Null(sink.FindCapture(TerminalJson.Text(adapter.ExistingOperation, "operationId")));
        Assert.Equal(2, adapter.Received.Count);
    }
    [Fact]
    public async Task LostSubmissionIsVisibleAndDisposeRetainsOriginalReconciliationSource()
    {
        var adapter = new TerminalTestAdapter { CommitThenThrow = true }; var bound = await adapter.BindAsync();
        using var sink = new TerminalExecutionOutputSink(bound.Client, bound.Binding);
        var capture = await sink.OpenAsync(adapter.ExistingOperation, default);
        capture.Append("stdout", "binary", [1]);
        await Assert.ThrowsAsync<IOException>(() => capture.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        capture.Append("stdout", "binary", [2]); // Native pipe keeps draining despite transport failure.
        capture.Dispose();
        var retained = sink.FindCapture(TerminalJson.Text(adapter.ExistingOperation, "operationId"))!;
        Assert.True(retained.RequiresReconciliation); Assert.Single(adapter.Sent);
        Assert.Equal(0, (await retained.ReconcileAsync()).AcceptedThrough);
        Assert.Single(adapter.Sent); Assert.Single(adapter.Received);
        Assert.Equal("commit_unknown", (await Assert.ThrowsAsync<WireProtocolException>(() => sink.OpenAsync(adapter.ExistingOperation, default))).Code);
    }
    [Fact]
    public async Task ProcessCancellationStillAllowsSeparatelyBoundedFinalSeal()
    {
        var adapter = new TerminalTestAdapter(); var bound = await adapter.BindAsync();
        using var sink = new TerminalExecutionOutputSink(bound.Client, bound.Binding);
        using var process = new CancellationTokenSource();
        using var capture = await sink.OpenAsync(adapter.ExistingOperation, process.Token);
        Assert.True(capture.Append("stdout", "binary", [1])); process.Cancel();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await capture.SealAsync(true, cleanup.Token);
        Assert.Equal("truncated", sink.FindCapture(TerminalJson.Text(adapter.ExistingOperation, "operationId"))!.LastStatus!.State);
    }
    [Fact]
    public async Task IdlePumpCancellationRequiresReconciliationAndRetainsOriginalKey()
    {
        var adapter = new TerminalTestAdapter(); var bound = await adapter.BindAsync();
        using var sink = new TerminalExecutionOutputSink(bound.Client, bound.Binding);
        var capture = await sink.OpenAsync(adapter.ExistingOperation, default); capture.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.Completion);
        Assert.True(sink.FindCapture(TerminalJson.Text(adapter.ExistingOperation, "operationId"))!.RequiresReconciliation);
        Assert.Empty(adapter.Sent);
    }
    [Fact]
    public async Task SuccessfulSequentialOperationsDoNotExhaustRetainedUnknownCapacity()
    {
        var adapter = new TerminalTestAdapter(); var bound = await adapter.BindAsync();
        using var sink = new TerminalExecutionOutputSink(bound.Client, bound.Binding);
        for (var index = 0; index < 10; index++)
        {
            // Empty output isolates source-slot lifecycle; every operation still has a real empty SHA seal.
            var operation = Tansr.Sdk.Tests.Execution.ExecutionFixture.Operation(id: "sequential-" + index);
            var capture = await sink.OpenAsync(operation, default); await capture.SealAsync(false, default); capture.Dispose();
            Assert.Null(sink.FindCapture(TerminalJson.Text(operation, "operationId")));
        }
    }
}
