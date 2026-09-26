using System.Text.Json;
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
        Assert.True(view.Snapshot.HasEventGap);
        view.Apply(Event("{\"type\":\"tool.completed\",\"seq\":5,\"toolCallId\":\"t\",\"isError\":true,\"content\":\"hello\"}"));
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
