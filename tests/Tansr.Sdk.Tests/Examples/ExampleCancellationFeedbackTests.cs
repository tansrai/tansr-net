using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Tests.Examples;

public sealed class ExampleCancellationFeedbackTests
{
    [Theory]
    [InlineData("aborted_streaming")]
    [InlineData("aborted_tools")]
    public void AcknowledgementWaitsForMatchingAbortedTurn(string reason)
    {
        var feedback = Running();
        var attempt = feedback.Begin();
        feedback.Acknowledge(attempt);
        Assert.Contains("等待原轮运行终态", feedback.Text);
        Assert.True(feedback.Observe(Event("turn.aborted", 2, reason: reason)));
        Assert.Equal("原轮已取消（服务端已确认中止）。", feedback.Text);
    }

    [Theory]
    [InlineData("turn.aborted", "aborted_streaming", "原轮已取消")]
    [InlineData("turn.completed", "completed", "原轮已完成")]
    public void TerminalBeforeHttpAcknowledgementIsNotOverwritten(string type, string reason, string expected)
    {
        var feedback = Running();
        var attempt = feedback.Begin();
        Assert.True(feedback.Observe(Event(type, 2, reason: reason)));
        var terminal = feedback.Text;
        feedback.Acknowledge(attempt);
        Assert.Equal(terminal, feedback.Text);
        Assert.StartsWith(expected, feedback.Text);
        Assert.DoesNotContain("等待", feedback.Text);
    }

    [Theory]
    [InlineData("turn.aborted", "model_error")]
    [InlineData("turn.aborted", "timeout")]
    [InlineData("turn.completed", "max_turns")]
    public void OtherTerminalReasonsAreNotCalledSuccessfulCancellation(string type, string reason)
    {
        var feedback = Running();
        feedback.Acknowledge(feedback.Begin());
        feedback.Observe(Event(type, 2, reason: reason));
        Assert.Equal("原轮已结束；未确认取消成功。", feedback.Text);
    }

    [Fact]
    public void HistoricalForeignAndUnidentifiedTerminalCannotSettleTheRequest()
    {
        var feedback = Running();
        feedback.Acknowledge(feedback.Begin());
        Assert.False(feedback.Observe(Event("turn.aborted", 1, reason: "aborted_tools")));
        Assert.False(feedback.Observe(Event("turn.aborted", 2, session: "other", reason: "aborted_tools")));
        Assert.False(feedback.Observe(Event("turn.aborted", 2, turn: "old", reason: "aborted_tools")));
        Assert.False(feedback.Observe(Event("turn.aborted", 3, turn: null, reason: "aborted_tools")));
        Assert.Contains("等待原轮运行终态", feedback.Text);
        Assert.True(feedback.Observe(Event("turn.completed", 4, reason: "completed")));
        Assert.StartsWith("原轮已完成", feedback.Text);
    }

    [Fact]
    public void UnknownTurnAndLaterTurnAreNotGuessedAsCancellationTarget()
    {
        var feedback = new ExampleCancellationFeedback("session");
        feedback.Acknowledge(feedback.Begin());
        Assert.Contains("轮次尚未确认", feedback.Text);
        feedback.Observe(Event("turn.started", 1));
        feedback.Observe(Event("turn.aborted", 2, reason: "aborted_streaming"));
        Assert.DoesNotContain("原轮已取消", feedback.Text);
        Assert.Contains("结果未知", feedback.Text);
    }

    [Fact]
    public void FailedRequestKeepsBothErrorAndIndependentTerminalFact()
    {
        var feedback = Running();
        var attempt = feedback.Begin();
        feedback.Fail(attempt, "conflict | reason=busy | traceId=synthetic");
        Assert.Contains("结果未知", feedback.Text);
        feedback.Observe(Event("turn.completed", 2, reason: "completed"));
        Assert.StartsWith("原轮已完成", feedback.Text);
        Assert.Contains("取消请求未确认：conflict | reason=busy | traceId=synthetic", feedback.Text);
    }

    [Fact]
    public void ObservationFailureBeforeOrDuringRequestRemainsUnknownAfterAck()
    {
        var feedback = Running();
        Assert.False(feedback.ObservationFailed("event_source"));
        feedback.Acknowledge(feedback.Begin());
        Assert.Contains("结果未知", feedback.Text);
        Assert.Contains("event_source", feedback.Text);
        Assert.True(feedback.ObservationFailed("read_timeout"));
        Assert.Contains("read_timeout", feedback.Text);
    }

    [Fact]
    public void DismissOrDetachPreventsLateAckAndTerminalFromChangingNextPresentation()
    {
        var feedback = Running();
        var attempt = feedback.Begin();
        feedback.Dismiss();
        feedback.Acknowledge(attempt);
        feedback.Fail(attempt, "timeout");
        Assert.False(feedback.Observe(Event("turn.aborted", 2, reason: "aborted_tools")));
        Assert.Null(feedback.Text);
        feedback.Observe(Event("turn.started", 3, turn: "next"));
        attempt = feedback.Begin();
        feedback.Detach();
        feedback.Acknowledge(attempt);
        Assert.False(feedback.Observe(Event("turn.aborted", 4, turn: "next", reason: "aborted_tools")));
        Assert.Null(feedback.Text);
    }

    [Fact]
    public void LaterAttemptCannotBeChangedByPriorHttpReply()
    {
        var feedback = Running();
        var first = feedback.Begin();
        var second = feedback.Begin();
        feedback.Acknowledge(first);
        feedback.Fail(first, "timeout");
        Assert.StartsWith("正在请求", feedback.Text);
        feedback.Acknowledge(second);
        Assert.Contains("等待原轮运行终态", feedback.Text);
        Assert.DoesNotContain("timeout", feedback.Text);
    }

    [Fact]
    public void EndOfObservationIsNotProofOfCancellation()
    {
        var feedback = Running();
        feedback.Acknowledge(feedback.Begin());
        feedback.Observe(Event("session.ended", 2, turn: null));
        Assert.True(feedback.ObservationEnded());
        Assert.Contains("结果未知", feedback.Text);
        Assert.DoesNotContain("原轮已取消", feedback.Text);
    }

    [Theory]
    [InlineData("session.ended")]
    [InlineData("session.created")]
    public void LifecycleChangeMakesPendingOutcomeUnknownButKeepsKnownTerminal(string type)
    {
        var pending = Running();
        pending.Acknowledge(pending.Begin());
        Assert.True(pending.Observe(Event(type, 2, turn: null)));
        Assert.Contains("生命周期已变化", pending.Text);
        Assert.Contains("结果未知", pending.Text);
        Assert.DoesNotContain("等待原轮运行终态", pending.Text);

        var completed = Running();
        var attempt = completed.Begin();
        completed.Observe(Event("turn.completed", 2, reason: "completed"));
        Assert.False(completed.Observe(Event(type, 3, turn: null)));
        completed.Acknowledge(attempt);
        Assert.Equal("原轮已完成；取消请求不代表取消成功。", completed.Text);
    }

    private static ExampleCancellationFeedback Running()
    {
        var feedback = new ExampleCancellationFeedback("session");
        feedback.Observe(Event("turn.started", 1));
        return feedback;
    }

    private static AgentEvent Event(string type, long seq, string? turn = "turn", string session = "session", string? reason = null) =>
        new(type, seq.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonSerializer.SerializeToElement(new { type, seq, turnId = turn, sessionId = session, reason }));
}
