using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Tests.Examples;

public sealed class ExampleEventRecoveryTests
{
    [Fact]
    public async Task IdleRecoveryReadsHistoryAfterMetadataAndUsesOnlyHistoryWatermark()
    {
        var reads = new Queue<JsonElement>(new[] { History(5), History(10) });
        var installed = new List<long>();
        var recovery = Recovery(_ => Task.FromResult(reads.Dequeue()), Metadata("idle", 99), installed);

        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.Equal(new long[] { 5, 10 }, installed);
        Assert.Equal(10, recovery.HistorySequence);
        Assert.False(recovery.Required);
    }

    [Fact]
    public async Task RunningRecoveryIgnoresOldTerminalThenWaitsForCommittedBody()
    {
        var reads = new Queue<JsonElement>(new[] { History(5), History(89), History(90) });
        var installed = new List<long>();
        var recovery = Recovery(_ => Task.FromResult(reads.Dequeue()), Metadata("running", 90), installed);
        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.False(await recovery.ObserveTerminalAsync(Terminal(80), CancellationToken.None));
        Assert.Equal(2, reads.Count);
        Assert.True(recovery.Required);
        // A terminal may equal metadata's watermark while Serve is asynchronously
        // finalizing that very turn. It is not an older terminal.
        Assert.True(await recovery.ObserveTerminalAsync(Terminal(90), CancellationToken.None));
        Assert.Equal(new long[] { 5, 90 }, installed);
        Assert.False(recovery.Required);
    }

    [Fact]
    public async Task IncompleteHistoryHasFiniteReadsAndNeverUnlocksInput()
    {
        var count = 0;
        var installed = new List<long>();
        var recovery = Recovery(_ => { count++; return Task.FromResult(History(5)); }, Metadata("running", 90), installed);
        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.False(await recovery.ObserveTerminalAsync(Terminal(91), CancellationToken.None));
        Assert.Equal(7, count); // Initial history plus at most six settlement reads.
        Assert.True(recovery.Required);
        Assert.Equal(new long[] { 5 }, installed);
    }

    [Fact]
    public async Task ClosingSessionDoesNotPretendArchiveIsSettled()
    {
        var installed = new List<long>();
        var recovery = Recovery(_ => Task.FromResult(History(5)), Metadata("ended", 9), installed);
        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.True(recovery.Required);
        Assert.False(await recovery.ObserveTerminalAsync(Event("session.ended", 9), CancellationToken.None));
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task ReplacedConnectionRejectsLateHistoryWithoutInstallation()
    {
        var current = true;
        var installed = new List<long>();
        var recovery = Recovery(_ => { current = false; return Task.FromResult(History(5)); }, Metadata("idle", 5), installed,
            isCurrent: () => current);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecoverGapAsync(CancellationToken.None));
        Assert.Empty(installed);
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task ReplacedConnectionRejectsLateTerminalHistory()
    {
        var current = true;
        var installed = new List<long>();
        var reads = 0;
        var recovery = Recovery(_ =>
        {
            if (++reads == 1) return Task.FromResult(History(5));
            current = false;
            return Task.FromResult(History(91));
        }, Metadata("running", 90), installed, isCurrent: () => current);
        await recovery.RecoverGapAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.ObserveTerminalAsync(Terminal(91), CancellationToken.None));
        Assert.Equal(new long[] { 5 }, installed);
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task SettlementDeadlinePreservesIncompleteState()
    {
        var reads = 0;
        var installed = new List<long>();
        var recovery = Recovery(async token =>
        {
            if (++reads == 1) return History(5);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return History(91);
        }, Metadata("running", 90), installed, deadline: TimeSpan.FromMilliseconds(20));
        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.False(await recovery.ObserveTerminalAsync(Terminal(91), CancellationToken.None));
        Assert.True(recovery.Required);
        Assert.Equal(new long[] { 5 }, installed);
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowedAsSettlementTimeout()
    {
        var reads = 0;
        var installed = new List<long>();
        using var cancelled = new CancellationTokenSource();
        var recovery = Recovery(async token =>
        {
            if (++reads == 1) return History(5);
            cancelled.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return History(91);
        }, Metadata("running", 90), installed);
        await recovery.RecoverGapAsync(cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.ObserveTerminalAsync(Terminal(91), cancelled.Token));
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task ForeignHistoryCannotChangePresentation()
    {
        var installed = new List<long>();
        var recovery = Recovery(_ => Task.FromResult(Json("{\"sessionId\":\"other\",\"lastSeq\":5,\"messages\":[]}")), Metadata("idle", 5), installed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverGapAsync(CancellationToken.None));
        Assert.Empty(installed);
    }

    [Fact]
    public async Task FailedGapReadCannotLetAnUnboundOldTerminalUnlockInput()
    {
        var reads = 0;
        var recovery = Recovery(_ =>
        {
            reads++;
            throw new IOException("fixture_read_failed");
        }, Metadata("running", 90), new List<long>());
        await Assert.ThrowsAsync<IOException>(() => recovery.RecoverGapAsync(CancellationToken.None));

        Assert.False(await recovery.ObserveTerminalAsync(Terminal(80), CancellationToken.None));
        Assert.Equal(1, reads);
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task FailedRecoveryIsNotRetriedByEveryRetainedEvent()
    {
        var reads = 0;
        var recovery = Recovery(_ =>
        {
            reads++;
            throw new IOException("fixture_read_failed");
        }, Metadata("idle", 50), new List<long>());
        await Assert.ThrowsAsync<IOException>(() => recovery.RecoverGapAsync(CancellationToken.None));

        await recovery.RecoverGapAsync(CancellationToken.None);
        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.Equal(1, reads);
        Assert.True(recovery.Required);
        Assert.False(await recovery.ObserveTerminalAsync(Terminal(50), CancellationToken.None));
    }

    [Fact]
    public async Task RunningRecoveryStartsOnceUntilCoveringTerminalThenCanHandleANewGap()
    {
        var reads = new Queue<JsonElement>(new[] { History(5), History(91), History(91) });
        var installed = new List<long>();
        var recovery = Recovery(_ => Task.FromResult(reads.Dequeue()), Metadata("running", 90), installed);
        await recovery.RecoverGapAsync(CancellationToken.None);
        await recovery.RecoverGapAsync(CancellationToken.None);
        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.Equal(new long[] { 5 }, installed);
        Assert.True(recovery.Required);
        Assert.True(await recovery.ObserveTerminalAsync(Terminal(91), CancellationToken.None));
        Assert.False(recovery.Required);
        await recovery.RecoverGapAsync(CancellationToken.None);
        Assert.Equal(new long[] { 5, 91, 91 }, installed);
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task NewAuthoritativeEpochGapReplacesPendingOldTerminalFloor()
    {
        using var view = new SessionView();
        var histories = new Queue<JsonElement>(new[] { History(5), History(1), History(3) });
        var metadata = new Queue<JsonElement>(new[] { Metadata("running", 90), Metadata("running", 2) });
        var recovery = new ExampleEventRecovery("s", _ => Task.FromResult(histories.Dequeue()),
            _ => Task.FromResult(metadata.Dequeue()), history =>
            {
                view.RestoreHistory(history, "s");
                return Task.CompletedTask;
            }, () => true);
        await recovery.RecoverGapAsync(CancellationToken.None);
        Assert.True(recovery.Required);
        view.Apply(new AgentEvent("server.replay.gap", null,
            Json("""{"sessionId":"s","type":"server.replay.gap","reason":"ahead_of_log"}""")));

        await recovery.RecoverGapAsync(CancellationToken.None, newReplayGap: true);
        Assert.Equal(1, recovery.HistorySequence);
        Assert.True(recovery.Required);
        view.Apply(Event("turn.started", 2));
        view.Apply(Terminal(3));
        Assert.True(await recovery.ObserveTerminalAsync(Terminal(3), CancellationToken.None));

        Assert.False(recovery.Required);
        Assert.Equal(3, recovery.HistorySequence);
        Assert.False(view.Snapshot.HasEventGap);
        Assert.Equal("idle", view.Snapshot.Status);
        Assert.Empty(histories);
        Assert.Empty(metadata);
    }

    [Fact]
    public async Task FailedNewEpochRecoveryCannotReuseThePreviousTerminalFloor()
    {
        var reads = 0;
        var recovery = Recovery(_ => ++reads == 1 ? Task.FromResult(History(5)) : throw new IOException("new_epoch_history_failed"),
            Metadata("running", 90), new List<long>());
        await recovery.RecoverGapAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => recovery.RecoverGapAsync(CancellationToken.None, newReplayGap: true));
        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.False(await recovery.ObserveTerminalAsync(Terminal(91), CancellationToken.None));
        Assert.Equal(2, reads);
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task IncompleteProjectionHistoryLeavesRecoveryLockedWithoutRepeatedReads()
    {
        using var view = new SessionView();
        view.Apply(Event("session.events_dropped", 0));
        var reads = 0;
        var recovery = new ExampleEventRecovery("s", _ =>
        {
            reads++;
            return Task.FromResult(Json("""{"sessionId":"s","lastSeq":5,"offset":0,"total":1,"messages":[]}"""));
        }, _ => Task.FromResult(Metadata("idle", 5)), history =>
        {
            view.RestoreHistory(history, "s");
            return Task.CompletedTask;
        }, () => true);

        var error = await Assert.ThrowsAsync<Tansr.Sdk.Client.TansrProtocolException>(() => recovery.RecoverGapAsync(CancellationToken.None));
        await recovery.RecoverGapAsync(CancellationToken.None);

        Assert.Equal("incomplete_history_snapshot", error.Code);
        Assert.Equal(1, reads);
        Assert.True(recovery.Required);
        Assert.True(view.Snapshot.HasEventGap);
    }

    [Fact]
    public async Task TerminalSettlementKeepsObservedErrorAndRecoversNarratorInSameInstall()
    {
        using var view = new SessionView();
        var lines = new List<string>();
        using var narrator = new SessionNarrator(lines.Add);
        var gap = new AgentEvent("server.replay.gap", null,
            Json("""{"sessionId":"s","type":"server.replay.gap","reason":"ahead_of_log"}"""));
        view.Apply(gap); narrator.Apply(gap);
        var reads = new Queue<JsonElement>(new[] { History(5), History(8) });
        var recovery = new ExampleEventRecovery("s", _ => Task.FromResult(reads.Dequeue()),
            _ => Task.FromResult(Metadata("running", 6)), history =>
            {
                view.RestoreHistory(history, "s");
                narrator.RestoreHistory(history, "s");
                return Task.CompletedTask;
            }, () => true);
        await recovery.RecoverGapAsync(CancellationToken.None);
        var started = Event("turn.started", 6);
        var error = new AgentEvent("turn.error", "7",
            Json("""{"sessionId":"s","type":"turn.error","seq":7,"scope":"kernel","recoverable":false,"message":"reconciliation_required"}"""));
        var aborted = new AgentEvent("turn.aborted", "8",
            Json("""{"sessionId":"s","type":"turn.aborted","seq":8,"reason":"internal_error"}"""));
        foreach (var item in new[] { started, error, aborted }) { view.Apply(item); narrator.Apply(item); }

        Assert.True(await recovery.ObserveTerminalAsync(aborted, CancellationToken.None));

        Assert.False(recovery.Required);
        Assert.Equal("error", view.Snapshot.Status);
        Assert.Equal("reconciliation_required", view.Snapshot.LastError?.Message);
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, line => line.Contains("turn error [kernel]"));
        Assert.Contains(lines, line => line.Contains("turn aborted (internal_error)"));
        Assert.Empty(view.Snapshot.PendingRequests);
        Assert.Equal(0, view.Snapshot.UsageRequests);
    }

    private static ExampleEventRecovery Recovery(Func<CancellationToken, Task<JsonElement>> history, JsonElement metadata,
        List<long> installed, Func<bool>? isCurrent = null, TimeSpan? deadline = null) =>
        new("s", history, _ => Task.FromResult(metadata), value =>
        {
            installed.Add(value.GetProperty("lastSeq").GetInt64());
            return Task.CompletedTask;
        }, isCurrent ?? (() => true), (_, _) => Task.CompletedTask, deadline);

    private static JsonElement History(long sequence) => Json("{\"sessionId\":\"s\",\"lastSeq\":" + sequence + ",\"messages\":[]}");
    private static JsonElement Metadata(string status, long sequence) => Json("{\"sessionId\":\"s\",\"status\":\"" + status + "\",\"lastSeq\":" + sequence + "}");
    private static AgentEvent Terminal(long sequence) => Event("turn.completed", sequence);
    private static AgentEvent Event(string type, long sequence) => new(type, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Json("{\"type\":\"" + type + "\",\"sessionId\":\"s\",\"seq\":" + sequence + "}"));
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
