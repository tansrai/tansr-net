using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;
using WpfAssistant;

namespace Tansr.Sdk.Tests.Examples;

public sealed class FirstTokenDiagnosticsTests
{
    [Fact]
    public async Task ReceivedHandledAndRenderedAreIndependentTimesAndContainNoBusinessContent()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = 10 });
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        clock.Advance(); diagnostic.Saved();
        clock.Advance(); diagnostic.Sent();
        clock.Advance();
        ReceiveAndApply(diagnostic, session, view, Event("turn.started", 11));
        clock.Advance();
        Assert.False(diagnostic.Handled(session, Event("turn.started", 11)));
        Observe(diagnostic, session, view, Event("msg.block.start", 12, blockType: "text"));
        clock.Advance();
        var delta = Event("msg.text.delta", 13, text: "SECRET-BODY bearer credential C:\\private\\file.txt");
        ReceiveAndApply(diagnostic, session, view, delta);
        clock.Advance(); Assert.False(diagnostic.Handled(session, delta));
        clock.Advance();
        Assert.Equal(0, diagnostic.BeginRender(new object(), view.Snapshot));
        var kinds = diagnostic.BeginRender(session, view.Snapshot);
        Assert.Equal(1, kinds);
        clock.Advance(); diagnostic.EndRender(kinds, view.Snapshot);
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        Assert.False(File.Exists(files.Output));
        Assert.True(Observe(diagnostic, session, view, Event("turn.completed", 14)));
        diagnostic.Complete();
        var rows = await ReadAsync(diagnostic, files.Output);

        Assert.Equal(1000d, Elapsed(rows, "TextHandled") - Elapsed(rows, "TextReceived"));
        Assert.Equal(1000d, Elapsed(rows, "TextRenderEnd") - Elapsed(rows, "TextRenderStart"));
        Assert.True(Elapsed(rows, "TextRenderStart") > Elapsed(rows, "TextHandled"));
        Assert.True(Elapsed(rows, "TurnStartedHandled") > Elapsed(rows, "TurnStartedReceived"));
        Assert.Single(rows, row => Stage(row) == "TextReceived");
        Assert.Single(rows, row => Stage(row) == "TextHandled");
        Assert.Single(rows, row => Stage(row) == "Complete");
        var expectedFields = new[] { "elapsedMs", "sample", "seq", "stage", "utc", "version" };
        Assert.All(rows, row => Assert.Equal(expectedFields, row.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)));
        var sample = Assert.Single(rows.Select(row => row.GetProperty("sample").GetString()).Distinct());
        Assert.True(Guid.TryParseExact(sample, "N", out _));
        var output = File.ReadAllText(files.Output);
        Assert.DoesNotContain("SECRET-BODY", output);
        Assert.DoesNotContain("private-session-credential", output);
        Assert.DoesNotContain("bearer", output);
        Assert.DoesNotContain("private", output);
        Assert.DoesNotContain(files.Output, output);
    }

    [Fact]
    public async Task ForeignSessionHistoricalSequenceAndWrongTurnCannotClaimFirstTextOrTerminal()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = 10 });
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        diagnostic.Saved();
        diagnostic.Sent();
        diagnostic.Received(new object(), Event("turn.started", 11));
        diagnostic.Received(session, Event("turn.started", 11, sessionId: "other"));
        diagnostic.Received(session, Event("turn.started", 10));
        Assert.False(diagnostic.Handled(session, Event("turn.completed", 10)));
        Assert.False(diagnostic.Handled(session, Event("turn.completed", 11, sessionId: "other")));
        clock.Advance();
        Observe(diagnostic, session, view, Event("turn.started", 11));
        Observe(diagnostic, session, view, Event("msg.block.start", 12, blockType: "text"));
        diagnostic.Received(session, Event("msg.text.delta", 13, turnId: "old-turn", text: "FOREIGN-TEXT"));
        Assert.False(diagnostic.Handled(session, Event("turn.completed", 14, turnId: "old-turn")));
        clock.Advance();
        var current = Event("msg.text.delta", 15, text: "current");
        diagnostic.Received(session, current); diagnostic.Handled(session, current);
        var terminal = Event("turn.completed", 16);
        diagnostic.Received(session, terminal);
        Assert.True(diagnostic.Handled(session, terminal));
        diagnostic.Complete();
        var rows = await ReadAsync(diagnostic, files.Output);

        Assert.Equal(11, Single(rows, "TurnStartedReceived").GetProperty("seq").GetInt64());
        Assert.Equal(15, Single(rows, "TextReceived").GetProperty("seq").GetInt64());
        Assert.Equal(2000d, Elapsed(rows, "TextReceived"));
        Assert.Equal(16, Single(rows, "TerminalHandled").GetProperty("seq").GetInt64());
        Assert.DoesNotContain(rows, row => Stage(row) == "AmbiguousTurn");
    }

    [Fact]
    public async Task HistoricalAssistantAndNewUserSnapshotsDoNotCountAsCurrentResponse()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        view.Apply(Event("turn.started", 0, turnId: "old-turn"));
        view.Apply(Event("msg.block.start", 1, blockType: "text"));
        view.Apply(Event("msg.text.delta", 2, text: "historical assistant"));
        view.Apply(Event("turn.completed", 3, turnId: "old-turn"));
        var baseline = view.Snapshot;
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", baseline));
        diagnostic.Saved();
        Assert.Equal(0, diagnostic.BeginRender(session, baseline));
        view.AppendUserMessage("new user prompt");
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        Observe(diagnostic, session, view, Event("turn.started", 4));
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        Observe(diagnostic, session, view, Event("msg.block.start", 5, blockType: "text"));
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        Observe(diagnostic, session, view, Event("msg.text.delta", 6, text: "new answer"));
        Assert.Equal(0, diagnostic.BeginRender(session, baseline));
        Assert.Equal(1, diagnostic.BeginRender(session, view.Snapshot));
        diagnostic.Complete();
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Equal(6, Single(rows, "TextRenderStart").GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task TerminalAllowsAlreadyQueuedResponseRenderButRejectsLaterEventsAndSnapshots()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        diagnostic.Saved();
        Observe(diagnostic, session, view, Event("turn.started", 0));
        Observe(diagnostic, session, view, Event("msg.block.start", 1, blockType: "text"));
        Observe(diagnostic, session, view, Event("msg.text.delta", 2, text: "queued response"));
        Assert.True(Observe(diagnostic, session, view, Event("turn.completed", 3)));
        var queuedTerminal = view.Snapshot;
        diagnostic.Received(session, Event("msg.thinking.delta", 4, text: "late"));
        Assert.False(diagnostic.Handled(session, Event("turn.completed", 5, turnId: "later")));
        view.Apply(Event("turn.started", 4, turnId: "later"));
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        clock.Advance();
        var kinds = diagnostic.BeginRender(session, queuedTerminal);
        Assert.Equal(1, kinds);
        clock.Advance(); diagnostic.EndRender(kinds, queuedTerminal);
        Assert.False(File.Exists(files.Output));
        diagnostic.Complete();
        Assert.False(diagnostic.Completion.IsCompleted);
        Assert.False(File.Exists(files.Output));
        clock.Advance(); diagnostic.Sent();
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Equal(0, Single(rows, "TurnStartedReceived").GetProperty("seq").GetInt64());
        Assert.Equal(3, Single(rows, "TextRenderStart").GetProperty("seq").GetInt64());
        Assert.True(Elapsed(rows, "TextRenderStart") > Elapsed(rows, "TerminalHandled"));
        Assert.True(Elapsed(rows, "Sent") > Elapsed(rows, "TextRenderEnd"));
        Assert.DoesNotContain(rows, row => Stage(row) == "ThinkingReceived");
    }

    [Fact]
    public async Task FinalTextAndHiddenThinkingSeparateReceiptFromVisibleContent()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions
        { InitialSequence = -1, TextDelivery = TextDeliveryMode.Final, ThinkingDelivery = ThinkingDeliveryMode.Off });
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        diagnostic.Saved();
        Observe(diagnostic, session, view, Event("turn.started", 0));
        Observe(diagnostic, session, view, Event("msg.block.start", 1, blockType: "thinking"));
        Observe(diagnostic, session, view, Event("msg.thinking.delta", 2, text: "hidden thinking"));
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        Observe(diagnostic, session, view, Event("msg.block.end", 3));
        Observe(diagnostic, session, view, Event("msg.block.start", 4, index: 1, blockType: "text"));
        Observe(diagnostic, session, view, Event("msg.text.delta", 5, index: 1, text: ""));
        clock.Advance();
        Observe(diagnostic, session, view, Event("msg.text.delta", 6, index: 1, text: "final answer"));
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        clock.Advance();
        Observe(diagnostic, session, view, Event("msg.block.end", 7, index: 1));
        var kinds = diagnostic.BeginRender(session, view.Snapshot);
        Assert.Equal(1, kinds);
        diagnostic.EndRender(kinds, view.Snapshot);
        diagnostic.Complete();
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Equal(6, Single(rows, "TextReceived").GetProperty("seq").GetInt64());
        Assert.Equal(7, Single(rows, "TextRenderStart").GetProperty("seq").GetInt64());
        Assert.Single(rows, row => Stage(row) == "ThinkingReceived");
        Assert.DoesNotContain(rows, row => Stage(row).StartsWith("ThinkingRender", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CombinedSnapshotRecordsBothKindsOnceAndMonotonicTimeSurvivesUtcRollback()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        diagnostic.Saved();
        Observe(diagnostic, session, view, Event("turn.started", 0));
        Observe(diagnostic, session, view, Event("msg.block.start", 1, blockType: "thinking"));
        Observe(diagnostic, session, view, Event("msg.thinking.delta", 2, text: "thinking"));
        Observe(diagnostic, session, view, Event("msg.block.end", 3));
        Observe(diagnostic, session, view, Event("msg.block.start", 4, index: 1, blockType: "text"));
        Observe(diagnostic, session, view, Event("msg.text.delta", 5, index: 1, text: "answer"));
        clock.Advance(); clock.UtcNow = clock.UtcNow.AddHours(-1);
        var kinds = diagnostic.BeginRender(session, view.Snapshot);
        Assert.Equal(3, kinds);
        clock.Advance(); diagnostic.EndRender(kinds, view.Snapshot);
        diagnostic.EndRender(kinds, view.Snapshot);
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        diagnostic.Complete();
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Equal(1000d, Elapsed(rows, "TextRenderStart"));
        Assert.Equal(2000d, Elapsed(rows, "ThinkingRenderEnd"));
        Assert.Single(rows, row => Stage(row) == "TextRenderEnd");
        Assert.Single(rows, row => Stage(row) == "ThinkingRenderEnd");
        Assert.All(rows, row => Assert.True(row.GetProperty("elapsedMs").GetDouble() >= 0));
    }

    [Fact]
    public async Task OneShotWindowExpiresAndCannotBeRearmed()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = Create(files.Output, clock, TimeSpan.FromMinutes(1));
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        Assert.False(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        clock.Advance(TimeSpan.FromMinutes(2));
        diagnostic.Received(session, Event("turn.started", 0));
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Single(rows, row => Stage(row) == "Expired");
        Assert.DoesNotContain(rows, row => Stage(row) == "TurnStartedReceived");
        Assert.False(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        var before = File.ReadAllBytes(files.Output);
        diagnostic.Saved(); diagnostic.Sent(); diagnostic.Complete(); diagnostic.Dispose();
        await diagnostic.Completion;
        Assert.Equal(before, File.ReadAllBytes(files.Output));
    }

    [Fact]
    public async Task TimerEndsTheWindowWithoutAnotherBusinessCallback()
    {
        using var files = new DiagnosticFiles();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = FirstTokenDiagnostics.Create(files.Output, TimeSpan.FromSeconds(1));
        Assert.NotNull(diagnostic);
        Assert.True(diagnostic.BeginSend(new object(), "private-session-credential", view.Snapshot));
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Single(rows, row => Stage(row) == "Expired");
        Assert.DoesNotContain(rows, row => Stage(row) == "Complete");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedBeforeSaveOrASecondTurnStopsRatherThanAssigningAnotherTurnsText(bool secondTurn)
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        if (secondTurn)
        {
            diagnostic.Saved();
            Observe(diagnostic, session, view, Event("turn.started", 0));
            diagnostic.Received(session, Event("turn.started", 1, turnId: "other-turn"));
        }
        else diagnostic.Received(session, Event("turn.started", 0));
        diagnostic.Received(session, Event("msg.text.delta", 2, text: "unattributed"));
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Single(rows, row => Stage(row) == "AmbiguousTurn");
        Assert.DoesNotContain(rows, row => Stage(row) == "TextReceived");
    }

    [Fact]
    public async Task ReplayGapEndsTheMeasurementBeforeHistoryCanBePresentedAsLiveText()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        diagnostic.Saved();
        Observe(diagnostic, session, view, Event("turn.started", 0));
        var gap = new AgentEvent("server.replay.gap", null,
            JsonSerializer.SerializeToElement(new { sessionId = "private-session-credential", reason = "evicted" }));
        diagnostic.Received(session, gap);
        view.Apply(gap);
        using var history = JsonDocument.Parse("""{"sessionId":"private-session-credential","lastSeq":5,"messages":[{"role":"assistant","blocks":[{"t":"text","text":"recovered historical text"}]}]}""");
        view.RestoreHistory(history.RootElement, "private-session-credential");
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Single(rows, row => Stage(row) == "EventGap");
        Assert.DoesNotContain(rows, row => Stage(row).StartsWith("Text", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "BaselineUnconfirmed")]
    [InlineData(true, "BaselineGap")]
    public async Task BusyOrGappedBaselineRecordsReasonAndCannotRearm(bool gapped, string reason)
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        view.Apply(gapped ? Event("turn.completed", 2) : Event("turn.started", 0));
        Assert.Equal(gapped, view.Snapshot.HasEventGap);
        Assert.False(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        using var idle = new SessionView();
        Assert.False(diagnostic.BeginSend(session, "private-session-credential", idle.Snapshot));
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Equal(new[] { "Click", reason }, rows.Select(Stage));
    }

    [Fact]
    public async Task RecoveredIdleMetadataAllowsExactUnknownBaselineWithoutInventingRuntimeState()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var diagnostic = Create(files.Output, clock);
        using var view = new SessionView();
        var session = new object();
        var historyReads = 0;
        var metadataReads = 0;
        var history = JsonSerializer.SerializeToElement(new
        {
            sessionId = "private-session-credential",
            lastSeq = 10,
            messages = new[] { new { role = "assistant", blocks = new[] { new { t = "text", text = "OLD-CONTENT" } } } }
        });
        var recovery = new ExampleEventRecovery("private-session-credential",
            _ => { historyReads++; return Task.FromResult(history); },
            _ => { metadataReads++; return Task.FromResult(JsonSerializer.SerializeToElement(new { sessionId = "private-session-credential", lastSeq = 10, status = "idle" })); },
            value => { view.RestoreHistory(value, "private-session-credential"); return Task.CompletedTask; }, () => true);
        await recovery.RecoverGapAsync(CancellationToken.None);
        Assert.False(recovery.Required);
        Assert.Equal(2, historyReads); Assert.Equal(1, metadataReads);
        Assert.Equal("unknown", view.Snapshot.Status); Assert.False(view.Snapshot.HasEventGap);
        diagnostic.ConfirmRecoveredIdle(session, recovery.HistorySequence);
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        Assert.Equal("unknown", view.Snapshot.Status);
        Assert.Equal(0, diagnostic.BeginRender(session, view.Snapshot));
        diagnostic.Saved(); diagnostic.Sent();
        Observe(diagnostic, session, view, Event("turn.started", 11));
        Observe(diagnostic, session, view, Event("msg.block.start", 12, blockType: "text"));
        Observe(diagnostic, session, view, Event("msg.text.delta", 13, text: "NEW-CONTENT"));
        var render = diagnostic.BeginRender(session, view.Snapshot); Assert.Equal(1, render);
        diagnostic.EndRender(render, view.Snapshot);
        Assert.True(Observe(diagnostic, session, view, Event("turn.completed", 14)));
        diagnostic.Complete();
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Single(rows, row => Stage(row) == "RecoveredIdleBaseline");
        Assert.Single(rows, row => Stage(row) == "TextRenderEnd");
        Assert.Single(rows, row => Stage(row) == "Complete");
        Assert.DoesNotContain("CONTENT", File.ReadAllText(files.Output));
    }

    [Fact]
    public async Task NonIdleRecoveryCannotAuthorizeUnknownBaseline()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var diagnostic = Create(files.Output, clock);
        using var view = new SessionView();
        var session = new object();
        var recovery = new ExampleEventRecovery("private-session-credential",
            _ => Task.FromResult(JsonSerializer.SerializeToElement(new { sessionId = "private-session-credential", lastSeq = 10, messages = Array.Empty<object>() })),
            _ => Task.FromResult(JsonSerializer.SerializeToElement(new { sessionId = "private-session-credential", lastSeq = 10, status = "running" })),
            value => { view.RestoreHistory(value, "private-session-credential"); return Task.CompletedTask; }, () => true);
        await recovery.RecoverGapAsync(CancellationToken.None);
        Assert.True(recovery.Required);
        if (!recovery.Required) diagnostic.ConfirmRecoveredIdle(session, recovery.HistorySequence);
        Assert.False(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        Assert.Equal("unknown", view.Snapshot.Status);
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Equal(new[] { "Click", "BaselineUnconfirmed" }, rows.Select(Stage));
    }

    [Theory]
    [InlineData("other-session", "BaselineUnconfirmed")]
    [InlineData("new-watermark", "BaselineUnconfirmed")]
    [InlineData("new-gap", "BaselineGap")]
    [InlineData("running", "BaselineUnconfirmed")]
    public async Task RecoveryProofCannotAuthorizeDifferentSessionWatermarkGapOrRunning(string change, string reason)
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var diagnostic = Create(files.Output, clock);
        using var view = new SessionView();
        var session = new object();
        diagnostic.ConfirmRecoveredIdle(session, 10);
        view.RestoreHistory(JsonSerializer.SerializeToElement(new
        {
            sessionId = "private-session-credential",
            lastSeq = change == "new-watermark" ? 11 : 10,
            messages = Array.Empty<object>()
        }), "private-session-credential");
        if (change == "new-gap") view.Apply(new AgentEvent("server.replay.gap", null,
            JsonSerializer.SerializeToElement(new { type = "server.replay.gap", sessionId = "private-session-credential", reason = "evicted" })));
        if (change == "running") view.Apply(Event("turn.started", 11));
        Assert.False(diagnostic.BeginSend(change == "other-session" ? new object() : session, "private-session-credential", view.Snapshot));
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.Equal(new[] { "Click", reason }, rows.Select(Stage));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.jsonl")]
    [InlineData("..\\relative.jsonl")]
    [InlineData("\\\\server\\share\\sample.jsonl")]
    public void InvalidOutputLocationsAreDisabled(string? path)
    {
        Assert.Null(FirstTokenDiagnostics.Create(path, TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1801)]
    public void InvalidTimeWindowsAreDisabled(int seconds)
    {
        using var files = new DiagnosticFiles();
        Assert.Null(FirstTokenDiagnostics.Create(files.Output, TimeSpan.FromSeconds(seconds)));
        Assert.False(File.Exists(files.Output));
    }

    [Fact]
    public async Task ExistingOutputAndCreateNewRaceNeverOverwriteTheOtherWriter()
    {
        using var files = new DiagnosticFiles();
        const string sentinel = "existing-user-evidence";
        File.WriteAllText(files.Output, sentinel);
        Assert.Null(FirstTokenDiagnostics.Create(files.Output, TimeSpan.FromMinutes(1)));
        Assert.Equal(sentinel, File.ReadAllText(files.Output));
        File.Delete(files.Output);
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = Create(files.Output, new TestClock());
        Assert.True(diagnostic.BeginSend(new object(), "private-session-credential", view.Snapshot));
        File.WriteAllText(files.Output, sentinel);
        diagnostic.Complete();
        await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sentinel, File.ReadAllText(files.Output));
    }

    [Fact]
    public async Task FailedFlushAndConcurrentClosureDoNotEscapeIntoTheCaller()
    {
        using var files = new DiagnosticFiles();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = Create(files.Output, new TestClock());
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        Directory.CreateDirectory(files.Output);
        await Task.WhenAll(Task.Run(diagnostic.Complete), Task.Run(diagnostic.Dispose), Task.Run(diagnostic.SendFailed));
        await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Directory.Exists(files.Output));
        Assert.False(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
    }

    [Fact]
    public async Task RepeatedAndUnknownEventsCannotExceedTheFixedOutputBudget()
    {
        using var files = new DiagnosticFiles();
        var clock = new TestClock();
        using var view = new SessionView(new SessionViewOptions { InitialSequence = -1 });
        using var diagnostic = Create(files.Output, clock);
        var session = new object();
        Assert.True(diagnostic.BeginSend(session, "private-session-credential", view.Snapshot));
        diagnostic.Saved();
        Observe(diagnostic, session, view, Event("turn.started", 0));
        for (var sequence = 1; sequence <= 1000; sequence++)
        {
            diagnostic.Saved(); diagnostic.Sent();
            var item = Event("custom." + new string('x', 200), sequence, text: new string('z', 2000));
            diagnostic.Received(session, item); diagnostic.Handled(session, item);
        }
        await Task.WhenAll(Task.Run(diagnostic.Complete), Task.Run(diagnostic.Dispose), Task.Run(diagnostic.Complete));
        var rows = await ReadAsync(diagnostic, files.Output);
        Assert.InRange(rows.Length, 1, 64);
        Assert.InRange(new FileInfo(files.Output).Length, 1L, 16L * 1024);
        Assert.All(rows, row => Assert.DoesNotContain("custom.", Stage(row)));
        Assert.Single(rows, row => Stage(row) is "Complete" or "Closed" or "LimitReached");
    }

    private static FirstTokenDiagnostics Create(string path, TestClock clock, TimeSpan? ttl = null)
    {
        var diagnostic = FirstTokenDiagnostics.Create(path, ttl ?? TimeSpan.FromMinutes(10), () => clock.UtcNow, () => clock.Ticks);
        Assert.NotNull(diagnostic);
        return diagnostic;
    }

    private static bool Observe(FirstTokenDiagnostics diagnostic, object session, SessionView view, AgentEvent item)
    {
        ReceiveAndApply(diagnostic, session, view, item);
        return diagnostic.Handled(session, item);
    }

    private static void ReceiveAndApply(FirstTokenDiagnostics diagnostic, object session, SessionView view, AgentEvent item)
    {
        diagnostic.Received(session, item);
        view.Apply(item);
    }

    private static AgentEvent Event(string type, long seq, string? turnId = null, string sessionId = "private-session-credential",
        string? text = null, int index = 0, string? blockType = null)
    {
        var fields = new Dictionary<string, object?> { ["type"] = type, ["seq"] = seq, ["sessionId"] = sessionId };
        if (type.StartsWith("turn.", StringComparison.Ordinal) || turnId != null) fields["turnId"] = turnId ?? "current-turn";
        if (text != null) fields["text"] = text;
        if (type.StartsWith("msg.", StringComparison.Ordinal)) fields["index"] = index;
        if (blockType != null) fields["blockType"] = blockType;
        return new AgentEvent(type, seq.ToString(CultureInfo.InvariantCulture), JsonSerializer.SerializeToElement(fields));
    }

    private static async Task<JsonElement[]> ReadAsync(FirstTokenDiagnostics diagnostic, string path)
    {
        await diagnostic.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        return File.ReadAllLines(path, Encoding.UTF8).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }

    private static string Stage(JsonElement row) => row.GetProperty("stage").GetString()!;
    private static JsonElement Single(IEnumerable<JsonElement> rows, string stage) => Assert.Single(rows, row => Stage(row) == stage);
    private static double Elapsed(IEnumerable<JsonElement> rows, string stage) => Single(rows, stage).GetProperty("elapsedMs").GetDouble();

    private sealed class TestClock
    {
        internal DateTimeOffset UtcNow { get; set; } = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        internal long Ticks { get; private set; } = 100 * Stopwatch.Frequency;
        internal void Advance(TimeSpan? duration = null)
        {
            var elapsed = duration ?? TimeSpan.FromSeconds(1);
            UtcNow += elapsed;
            Ticks += (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
        }
    }

    private sealed class DiagnosticFiles : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tansr-first-token-" + Guid.NewGuid().ToString("N")));
        internal DiagnosticFiles() { Directory.CreateDirectory(root); }
        internal string Output => Path.Combine(root, "sample.jsonl");
        public void Dispose()
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test directory.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
