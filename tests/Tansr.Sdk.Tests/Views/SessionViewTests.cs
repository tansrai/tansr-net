using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Tests.Views;

public sealed class SessionViewTests
{
    private static AgentEvent Event(string json, string name = "event", string? id = null)
    { using var document = JsonDocument.Parse(json); return new AgentEvent(name, id, document.RootElement); }

    [Fact]
    public void EmptyLogSentinelAcceptsSequenceZeroWithoutDroppingOrGap()
    {
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        view.Apply(Event("{\"type\":\"turn.started\",\"seq\":0}"));
        Assert.Equal(0, view.Snapshot.LastSequence);
        Assert.Equal("thinking", view.Snapshot.Status);
        Assert.False(view.Snapshot.HasEventGap);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionView(new SessionViewOptions { InitialSequence = -2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionView(new SessionViewOptions { InitialSequence = 9007199254740992L }));
        Assert.Throws<ArgumentException>(() => view.Apply(Event("{\"type\":\"turn.started\",\"seq\":-1}")));
    }

    [Fact]
    public void StreamBlocksRetractAndReuseIndexAcrossModelCalls()
    {
        using var view = new SessionView();
        view.Apply(Event("{\"type\":\"turn.started\",\"seq\":0}"));
        view.Apply(Event("{\"type\":\"msg.block.start\",\"seq\":1,\"index\":0,\"blockType\":\"text\"}"));
        view.Apply(Event("{\"type\":\"msg.text.delta\",\"seq\":2,\"index\":0,\"text\":\"撤销\"}"));
        var previous = view.Snapshot;
        view.Apply(Event("{\"type\":\"msg.retracted\",\"seq\":3,\"index\":0}"));
        Assert.Empty(view.Snapshot.Messages[0].Parts);
        Assert.Equal("撤销", previous.Messages[0].Parts[0].Text);
        view.Apply(Event("{\"type\":\"msg.block.start\",\"seq\":4,\"index\":0,\"blockType\":\"text\"}"));
        view.Apply(Event("{\"type\":\"msg.text.delta\",\"seq\":5,\"index\":0,\"text\":\"正确\"}"));
        Assert.Equal(2, view.Snapshot.Messages.Count);
        Assert.Equal("正确", view.Snapshot.Messages[1].Parts[0].Text);
    }

    [Fact]
    public void FinalDeliveryHidesPartialAndThinkingOffNeverMaterializes()
    {
        using var view = new SessionView(new SessionViewOptions { TextDelivery = TextDeliveryMode.Final, ThinkingDelivery = ThinkingDeliveryMode.Off });
        view.Apply(Event("{\"type\":\"turn.started\",\"seq\":1}"));
        view.Apply(Event("{\"type\":\"msg.block.start\",\"seq\":2,\"index\":0,\"blockType\":\"thinking\"}"));
        view.Apply(Event("{\"type\":\"msg.thinking.delta\",\"seq\":3,\"index\":0,\"text\":\"不显示\"}"));
        Assert.Equal("thinking", view.Snapshot.Status);
        view.Apply(Event("{\"type\":\"msg.block.end\",\"seq\":4,\"index\":0}"));
        view.Apply(Event("{\"type\":\"msg.block.start\",\"seq\":5,\"index\":1,\"blockType\":\"text\"}"));
        view.Apply(Event("{\"type\":\"msg.text.delta\",\"seq\":6,\"index\":1,\"text\":\"最终正文\"}"));
        Assert.Empty(view.Snapshot.Messages[0].Parts);
        view.Apply(Event("{\"type\":\"msg.block.end\",\"seq\":7,\"index\":1}"));
        Assert.Equal("最终正文", Assert.Single(view.Snapshot.Messages[0].Parts).Text);
    }

    [Fact]
    public void ReplayDoesNotDuplicateUsageOrOutputAndGapRemainsVisible()
    {
        using var view = new SessionView();
        view.Apply(Event("{\"type\":\"tool.proposed\",\"seq\":1,\"toolCallId\":\"t\",\"name\":\"Shell\"}"));
        var output = Event("{\"type\":\"tool.output.delta\",\"seq\":2,\"toolCallId\":\"t\",\"chunk\":\"hello\"}");
        view.Apply(output); view.Apply(output);
        Assert.Equal("hello", Assert.Single(view.Snapshot.Tools).OutputTail);
        var usage = Event("{\"type\":\"cost.usage.updated\",\"seq\":4,\"usage\":{\"inputTokens\":10,\"outputTokens\":3,\"cacheReadInputTokens\":4,\"cacheCreationInputTokens\":2,\"reasoningOutputTokens\":2}}");
        view.Apply(usage); view.Apply(usage);
        Assert.Equal(19, view.Snapshot.SessionTokens);
        Assert.Equal(1, view.Snapshot.UsageRequests);
        Assert.False(view.Snapshot.HasEventGap);
        view.Apply(Event("{\"type\":\"tool.completed\",\"seq\":5,\"toolCallId\":\"t\",\"isError\":true,\"content\":\"hello\"}"));
        view.Apply(Event("{\"type\":\"session.events_dropped\",\"seq\":6,\"droppedCount\":1,\"reason\":\"count_limit\"}"));
        Assert.True(view.Snapshot.HasEventGap);
        Assert.Equal("failed", Assert.Single(view.Snapshot.Tools).Status);
        Assert.Equal(1, SessionViewTextFormatter.Format(view.Snapshot).Split("hello").Length - 1);
    }

    [Fact]
    public void ControlsUsePayloadAndRecoverableWarningsDoNotBecomeFailures()
    {
        using var view = new SessionView();
        view.Apply(Event("{\"type\":\"server.permission.request\",\"seq\":1,\"payload\":{\"requestId\":\"r\",\"digest\":\"d\",\"name\":\"Shell\"}}"));
        Assert.Equal("d", Assert.Single(view.Snapshot.PendingRequests).Data.GetProperty("digest").GetString());
        view.Apply(Event("{\"type\":\"turn.error\",\"seq\":2,\"scope\":\"sdk.notice\",\"recoverable\":true,\"message\":\"notice\"}"));
        Assert.Null(view.Snapshot.LastError);
        Assert.Single(view.Snapshot.Notices);
        view.Apply(Event("{\"type\":\"server.permission.closed\",\"seq\":3,\"payload\":{\"requestId\":\"r\"}}"));
        Assert.Empty(view.Snapshot.PendingRequests);
    }

    [Theory]
    [InlineData("session.created")]
    [InlineData("server.platform.warning")]
    public void MonotonicEpochOrFilteredSequenceJumpIsNotLossEvidence(string type)
    {
        using var view = new SessionView(new SessionViewOptions { InitialSequence = 102 });
        view.Apply(Event("{\"type\":\"" + type + "\",\"seq\":4305072}"));
        view.Apply(Event("{\"type\":\"turn.started\",\"seq\":4305073}"));
        view.Apply(Event("{\"type\":\"turn.completed\",\"seq\":4305081}"));
        view.Apply(Event("{\"type\":\"session.created\",\"seq\":7085025}"));

        Assert.False(view.Snapshot.HasEventGap);
        Assert.Equal(7085025, view.Snapshot.LastSequence);
        Assert.DoesNotContain(view.Snapshot.Notices, notice => notice.Code == "event_sequence_gap");
    }

    [Theory]
    [InlineData("server.replay.gap", "evicted")]
    [InlineData("server.replay.gap", "ahead_of_log")]
    [InlineData("session.events_dropped", "count_limit")]
    public void ExplicitLossSurvivesLaterEpochAndTerminal(string type, string reason)
    {
        using var view = new SessionView(new SessionViewOptions { InitialSequence = 2 });
        view.Apply(Event("{\"type\":\"" + type + "\",\"reason\":\"" + reason + "\",\"seq\":3}"));
        view.Apply(Event("{\"type\":\"session.created\",\"seq\":1000}"));
        view.Apply(Event("{\"type\":\"turn.completed\",\"seq\":1001}"));

        Assert.True(view.Snapshot.HasEventGap);
        if (reason == "ahead_of_log") Assert.Equal(2, view.Snapshot.LastSequence);
    }

    [Fact]
    public void AdjacentWarningCannotExtendAnEarlierTerminalToAnotherHistoryWatermark()
    {
        using var view = FailedTurn();
        view.Apply(Event("{\"type\":\"server.platform.warning\",\"seq\":4}"));
        using var history = JsonDocument.Parse("{\"sessionId\":\"s\",\"lastSeq\":4,\"messages\":[]}");

        view.RestoreHistory(history.RootElement, "s");

        Assert.Equal("unknown", view.Snapshot.Status);
        Assert.Null(view.Snapshot.LastError);
    }

    [Fact]
    public void EpochGapPreventsOldAndNewHistoryBeingSilentlyMerged()
    {
        using var view = new SessionView(new SessionViewOptions { InitialSequence = 100 });
        view.Apply(Event("{\"type\":\"server.replay.gap\",\"reason\":\"ahead_of_log\",\"requestedAfterSeq\":100,\"droppedEvents\":0}"));
        view.Apply(Event("{\"type\":\"turn.started\",\"seq\":1}"));
        Assert.True(view.Snapshot.HasEventGap);
        Assert.Equal(100, view.Snapshot.LastSequence);
    }

    [Fact]
    public void AuthoritativeHistoryRestoresGapWithoutRestoringRuntimeFacts()
    {
        using var view = new SessionView();
        view.Apply(Event("{\"type\":\"server.replay.gap\",\"reason\":\"evicted\",\"requestedAfterSeq\":1,\"droppedEvents\":2}"));
        using var history = JsonDocument.Parse("{\"sessionId\":\"s\",\"lastSeq\":42,\"messages\":[{\"role\":\"user\",\"blocks\":[{\"t\":\"text\",\"text\":\"old\"}]}]}");
        Assert.Equal(42, view.RestoreHistory(history.RootElement, "s"));
        Assert.False(view.Snapshot.HasEventGap);
        Assert.Equal(42, view.Snapshot.LastSequence);
        Assert.Equal("old", Assert.Single(Assert.Single(view.Snapshot.Messages).Parts).Text);
        Assert.Equal("unknown", view.Snapshot.Status);
        Assert.Empty(view.Snapshot.PendingRequests);
    }

    [Fact]
    public void PartialHistoryCannotClearAReplayGap()
    {
        using var view = new SessionView();
        view.Apply(Event("{\"type\":\"server.replay.gap\",\"reason\":\"evicted\"}"));
        using var history = JsonDocument.Parse("{\"sessionId\":\"s\",\"lastSeq\":4,\"offset\":0,\"total\":2,\"messages\":[{\"role\":\"user\",\"blocks\":[]}]}");
        Assert.Throws<TansrProtocolException>(() => view.RestoreHistory(history.RootElement, "s"));
        Assert.True(view.Snapshot.HasEventGap);
    }

    [Fact]
    public void RestoredHistoryFiltersCoveredTextButAcceptsNewPermissionAndTerminal()
    {
        using var view = new SessionView();
        using var history = JsonDocument.Parse("{\"sessionId\":\"s\",\"lastSeq\":42,\"messages\":[{\"role\":\"assistant\",\"blocks\":[{\"t\":\"text\",\"text\":\"answer\"}]}]}");
        view.RestoreHistory(history.RootElement, "s");
        view.Apply(Event("{\"type\":\"msg.block.start\",\"seq\":40,\"index\":0,\"blockType\":\"text\"}"));
        view.Apply(Event("{\"type\":\"msg.text.delta\",\"seq\":41,\"index\":0,\"text\":\"answer\"}"));
        view.Apply(Event("{\"type\":\"turn.completed\",\"seq\":42}"));
        Assert.Single(view.Snapshot.Messages);
        Assert.Equal("unknown", view.Snapshot.Status);
        view.Apply(Event("{\"type\":\"server.permission.request\",\"seq\":43,\"payload\":{\"requestId\":\"new\",\"digest\":\"d\",\"name\":\"Shell\"}}"));
        Assert.Equal("new", Assert.Single(view.Snapshot.PendingRequests).Id);
        view.Apply(Event("{\"type\":\"turn.aborted\",\"seq\":44}"));
        Assert.Empty(view.Snapshot.PendingRequests);
        Assert.Equal("idle", view.Snapshot.Status);
        Assert.Equal(0, view.Snapshot.SessionTokens);
    }

    [Fact]
    public void MalformedHistoryDoesNotPartiallyReplacePresentation()
    {
        using var view = new SessionView();
        view.AppendUserMessage("keep");
        view.Apply(Event("{\"type\":\"server.replay.gap\",\"reason\":\"evicted\"}"));
        using var history = JsonDocument.Parse("{\"sessionId\":\"s\",\"lastSeq\":42,\"messages\":[{\"role\":\"assistant\",\"blocks\":[null]}]}");
        Assert.Throws<TansrProtocolException>(() => view.RestoreHistory(history.RootElement, "s"));
        Assert.True(view.Snapshot.HasEventGap);
        Assert.Equal("keep", Assert.Single(Assert.Single(view.Snapshot.Messages).Parts).Text);
    }

    [Fact]
    public void SettlementHistoryAtObservedTerminalWatermarkKeepsErrorForQueuedUi()
    {
        using var view = new SessionView();
        var context = new QueuedContext();
        var rendered = new List<string>();
        using var ui = view.Subscribe(snapshot => rendered.Add(SessionViewTextFormatter.Format(snapshot)), context);
        view.Apply(Event("""{"sessionId":"s","type":"turn.started","seq":10}"""));
        view.Apply(Event("""{"sessionId":"s","type":"server.permission.request","seq":11,"payload":{"requestId":"old","name":"Shell"}}"""));
        view.Apply(Event("""{"sessionId":"s","type":"turn.error","seq":12,"errorKind":"internal_error","scope":"kernel","recoverable":false,"message":"Archive spool: reconciliation_required"}"""));
        view.Apply(Event("""{"sessionId":"s","type":"turn.aborted","seq":13,"reason":"internal_error"}"""));

        using var history = JsonDocument.Parse("""{"sessionId":"s","lastSeq":13,"messages":[]}""");
        view.RestoreHistory(history.RootElement, "s");
        context.Run();

        Assert.Equal("error", view.Snapshot.Status);
        Assert.Equal("Archive spool: reconciliation_required", view.Snapshot.LastError?.Message);
        Assert.Contains("reconciliation_required", Assert.Single(rendered));
        Assert.Empty(view.Snapshot.PendingRequests);
        Assert.Equal(13, view.Snapshot.LastSequence);
        Assert.Equal(0, view.Snapshot.UsageRequests);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void DifferentHistoryWatermarkCannotCarryObservedTerminal(int historySequence)
    {
        using var view = FailedTurn();
        using var history = JsonDocument.Parse(JsonSerializer.Serialize(new { sessionId = "s", lastSeq = historySequence, messages = Array.Empty<object>() }));
        view.RestoreHistory(history.RootElement, "s");
        Assert.Equal("unknown", view.Snapshot.Status);
        Assert.Null(view.Snapshot.LastError);
    }

    [Theory]
    [InlineData("{\"type\":\"server.replay.gap\",\"reason\":\"evicted\"}", 3)]
    [InlineData("{\"type\":\"server.replay.gap\",\"reason\":\"ahead_of_log\"}", 3)]
    [InlineData("{\"type\":\"session.events_dropped\",\"seq\":4}", 4)]
    [InlineData("{\"type\":\"server.platform.warning\",\"seq\":5}", 5)]
    [InlineData("{\"type\":\"turn.started\",\"seq\":4}", 4)]
    [InlineData("{\"type\":\"session.created\",\"seq\":4}", 4)]
    public void GapOrNewIncarnationCannotCarryObservedTerminal(string nextEvent, int historySequence)
    {
        using var view = FailedTurn();
        view.Apply(Event(nextEvent));
        using var history = JsonDocument.Parse(JsonSerializer.Serialize(new { sessionId = "s", lastSeq = historySequence, messages = Array.Empty<object>() }));
        view.RestoreHistory(history.RootElement, "s");
        Assert.Equal("unknown", view.Snapshot.Status);
        Assert.Null(view.Snapshot.LastError);
        Assert.Empty(view.Snapshot.PendingRequests);
    }

    [Fact]
    public void ObservedCancellationStaysSettledButHistoryAloneCannotInventIt()
    {
        using var view = new SessionView();
        view.Apply(Event("""{"sessionId":"s","type":"turn.started","seq":1}"""));
        view.Apply(Event("""{"sessionId":"s","type":"turn.aborted","seq":2,"reason":"aborted_streaming"}"""));
        using var history = JsonDocument.Parse("""{"sessionId":"s","lastSeq":2,"messages":[]}""");
        view.RestoreHistory(history.RootElement, "s");
        Assert.Equal("idle", view.Snapshot.Status);
        Assert.Null(view.Snapshot.LastError);
        using var fresh = new SessionView();
        fresh.RestoreHistory(history.RootElement, "s");
        Assert.Equal("unknown", fresh.Snapshot.Status);
    }

    private static SessionView FailedTurn()
    {
        var view = new SessionView();
        view.Apply(Event("""{"sessionId":"s","type":"turn.started","seq":1}"""));
        view.Apply(Event("""{"sessionId":"s","type":"turn.error","seq":2,"recoverable":false,"message":"failed"}"""));
        view.Apply(Event("""{"sessionId":"s","type":"turn.aborted","seq":3,"reason":"internal_error"}"""));
        return view;
    }

    [Fact]
    public void MultipleSubscribersAreIndependentAndSlowUiQueueIsBounded()
    {
        using var view = new SessionView();
        var context = new QueuedContext(); var rendered = new List<long>(); var errors = 0;
        using var broken = view.Subscribe(_ => throw new InvalidOperationException(), onObserverError: _ => errors++);
        using var ui = view.Subscribe(x => rendered.Add(x.Version), context);
        for (var index = 0; index < 20; index++) view.AppendUserMessage(index.ToString());
        Assert.Single(context.Work);
        context.Run();
        Assert.Equal(new[] { view.Snapshot.Version }, rendered);
        Assert.True(errors > 0);
        ui.Dispose(); view.AppendUserMessage("after dispose"); context.Run();
        Assert.Single(rendered);
    }

    [Fact]
    public async Task ConcurrentPublicationKeepsImmutableSnapshotsAndMonotonicVersions()
    {
        using var view = new SessionView();
        var versions = new List<long>();
        using var subscription = view.Subscribe(x => { lock (versions) versions.Add(x.Version); });
        await Task.WhenAll(Enumerable.Range(0, 50).Select(x => Task.Run(() => view.AppendUserMessage(x.ToString()))));
        Assert.Equal(50, view.Snapshot.Messages.Count);
        Assert.True(versions.Zip(versions.Skip(1), (a, b) => a < b).All(x => x));
    }

    [Fact]
    public void TruncatedTerminalResultsAndOutputAreExplicitInSnapshot()
    {
        using var view = new SessionView(new SessionViewOptions { MaximumTextCharacters = 4, MaximumOutputCharacters = 4 });
        view.Apply(Event("{\"type\":\"tool.proposed\",\"seq\":1,\"toolCallId\":\"t\",\"name\":\"Shell\"}"));
        view.Apply(Event("{\"type\":\"tool.output.delta\",\"seq\":2,\"toolCallId\":\"t\",\"chunk\":\"0123456789\"}"));
        Assert.True(view.Snapshot.PresentationTruncated);
        Assert.Equal("6789", Assert.Single(view.Snapshot.Tools).OutputTail);
        using var final = new SessionView(new SessionViewOptions { MaximumTextCharacters = 4 });
        final.Apply(Event("{\"type\":\"tool.proposed\",\"seq\":1,\"toolCallId\":\"t\",\"name\":\"Shell\"}"));
        final.Apply(Event("{\"type\":\"tool.completed\",\"seq\":2,\"toolCallId\":\"t\",\"content\":\"0123456789\"}"));
        Assert.True(final.Snapshot.PresentationTruncated);
        Assert.Equal("0123", Assert.Single(final.Snapshot.Tools).ResultText);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        internal List<Action> Work { get; } = new();
        public override void Post(SendOrPostCallback d, object? state) => Work.Add(() => d(state));
        internal void Run() { var pending = Work.ToArray(); Work.Clear(); foreach (var callback in pending) callback(); }
    }
}
