using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Tests.Views;

public sealed class SessionNarratorTests
{
    // Frozen original SDK packages/sdk/test/view/narrator.test.ts TYPICAL vector and
    // expected English lines. Event timestamps and UTF-16 thinking lengths are identical.
    // Original test SHA256: 9040923ff430d607276e895d59f96ad4f3b2a6753ba81efd846ed32a3d895fdd.
    // Original narrator SHA256: fa142628bf5d03c76f48ab09068ee2998f55458e4a93410ff91a2727974d0484.
    private const string Typical = """
        [
          {"type":"session.created","cwd":"/app"},
          {"type":"turn.started"},
          {"type":"msg.block.start","index":0,"blockType":"thinking"},
          {"type":"msg.thinking.delta","index":0,"text":"user wants a file listing"},
          {"type":"msg.block.end","index":0},
          {"type":"msg.block.start","index":1,"blockType":"text"},
          {"type":"msg.text.delta","index":1,"text":"Let me check the files."},
          {"type":"msg.block.end","index":1},
          {"type":"msg.block.start","index":2,"blockType":"tool_call"},
          {"type":"msg.block.end","index":2},
          {"type":"tool.proposed","toolCallId":"t1","name":"Shell","args":{"cmd":"ls -la"}},
          {"type":"tool.permission.requested","toolCallId":"t1","name":"Shell"},
          {"type":"tool.permission.decided","toolCallId":"t1","decision":"allow","decisionSource":"user"},
          {"type":"tool.started","toolCallId":"t1"},
          {"type":"tool.completed","toolCallId":"t1","durationMs":1200},
          {"type":"cost.usage.updated","model":"mock-model","usage":{"inputTokens":1500,"outputTokens":342}},
          {"type":"msg.block.start","index":0,"blockType":"text"},
          {"type":"msg.text.delta","index":0,"text":"There are 2 files."},
          {"type":"msg.block.end","index":0},
          {"type":"turn.completed","reason":"completed"},
          {"type":"session.ended"}
        ]
        """;

    [Theory]
    [InlineData(NarratorVerbosity.Quiet)]
    [InlineData(NarratorVerbosity.Normal)]
    [InlineData(NarratorVerbosity.Verbose)]
    public void SameOriginalSdkVectorProducesOriginalThreeVerbosityLines(NarratorVerbosity verbosity)
    {
        var lines = new List<string>();
        using var narrator = new SessionNarrator(lines.Add, new SessionNarratorOptions { Verbosity = verbosity });
        using var source = JsonDocument.Parse(Typical);
        var sequence = 0;
        foreach (var body in source.RootElement.EnumerateArray()) narrator.Apply(Event(body.GetRawText(), sequence++));
        var all = new (NarratorVerbosity Level, string Line)[]
        {
            (NarratorVerbosity.Quiet, "[12:03:01] turn started"),
            (NarratorVerbosity.Verbose, "[12:03:04] thinking finished (25 chars)"),
            (NarratorVerbosity.Verbose, "[12:03:07] assistant: \"Let me check the files.\""),
            (NarratorVerbosity.Verbose, "[12:03:10] tool Shell proposed: {\"cmd\":\"ls -la\"}"),
            (NarratorVerbosity.Normal, "[12:03:11] tool Shell awaiting permission"),
            (NarratorVerbosity.Verbose, "[12:03:12] permission allow for Shell (user)"),
            (NarratorVerbosity.Normal, "[12:03:13] tool Shell started: {\"cmd\":\"ls -la\"}"),
            (NarratorVerbosity.Normal, "[12:03:14] tool Shell completed (1.2s)"),
            (NarratorVerbosity.Verbose, "[12:03:15] usage +1,842 tokens (mock-model)"),
            (NarratorVerbosity.Verbose, "[12:03:18] assistant: \"There are 2 files.\""),
            (NarratorVerbosity.Quiet, "[12:03:19] turn completed · 1,842 tokens"),
            (NarratorVerbosity.Quiet, "[12:03:20] session ended"),
        };
        Assert.Equal(all.Where(x => x.Level <= verbosity).Select(x => x.Line), lines);
        Assert.DoesNotContain(lines, line => line.Contains("user wants"));
    }

    [Fact]
    public void OriginalDenialFailureRecoveryAndCompactionVectorKeepsMeaning()
    {
        var lines = Narrate("""
            [
              {"type":"turn.started"},
              {"type":"session.compacted","removedRange":[1,5],"removedIndices":[1,2,4]},
              {"type":"tool.proposed","toolCallId":"t1","name":"Write","args":{"path":"/etc/passwd"}},
              {"type":"tool.permission.requested","toolCallId":"t1","name":"Write"},
              {"type":"tool.permission.decided","toolCallId":"t1","decision":"deny","decisionSource":"rule","matchedRule":"Write(/etc/*)"},
              {"type":"tool.proposed","toolCallId":"t2","name":"Http","args":{}},
              {"type":"tool.started","toolCallId":"t2"},
              {"type":"tool.failed","toolCallId":"t2","errorType":"timeout","message":"no response"},
              {"type":"turn.error","scope":"model","message":"upstream 500","recoverable":true},
              {"type":"turn.error","scope":"model","message":"upstream exploded","recoverable":false},
              {"type":"turn.aborted","reason":"aborted_streaming"}
            ]
            """, NarratorVerbosity.Normal);
        Assert.Equal(new[]
        {
            "[12:03:00] turn started", "[12:03:01] history compacted (3 messages folded)",
            "[12:03:03] tool Write awaiting permission", "[12:03:04] tool Write denied (rule, rule: Write(/etc/*))",
            "[12:03:06] tool Http started: {}", "[12:03:07] tool Http failed (timeout): no response",
            "[12:03:08] turn notice [model]: upstream 500 — recoverable, session continues",
            "[12:03:09] turn error [model]: upstream exploded — this turn cannot be retried",
            "[12:03:10] turn aborted (aborted_streaming)",
        }, lines);
    }

    [Fact]
    public void ReplayRetractBlockReuseAndLongHiddenThinkingUseOriginalProjection()
    {
        var lines = new List<string>();
        using var narrator = new SessionNarrator(lines.Add, new SessionNarratorOptions { Verbosity = NarratorVerbosity.Verbose });
        narrator.Apply(Event("""{"type":"turn.started"}""", 0));
        narrator.Apply(Event("""{"type":"msg.block.start","index":0,"blockType":"thinking"}""", 1));
        var thinking = Event(JsonSerializer.Serialize(new { type = "msg.thinking.delta", index = 0, text = new string('秘', 5000) + "🙂" }), 2);
        narrator.Apply(thinking); narrator.Apply(thinking);
        narrator.Apply(Event("""{"type":"msg.block.end","index":0}""", 3));
        narrator.Apply(Event("""{"type":"msg.block.end","index":0}""", 4));
        narrator.Apply(Event("""{"type":"msg.block.start","index":0,"blockType":"text"}""", 5));
        narrator.Apply(Event("""{"type":"msg.text.delta","index":0,"text":"RETRACT_SECRET"}""", 6));
        narrator.Apply(Event("""{"type":"msg.retracted","index":0}""", 7));
        narrator.Apply(Event("""{"type":"msg.block.end","index":0}""", 8));
        narrator.Apply(Event("""{"type":"turn.started"}""", 9));
        narrator.Apply(Event("""{"type":"msg.block.end","index":0}""", 10));
        Assert.Single(lines, line => line.Contains("thinking finished (5,002 chars)"));
        Assert.DoesNotContain(lines, line => line.Contains('秘') || line.Contains("RETRACT_SECRET") || line.Contains("assistant:"));
    }

    [Fact]
    public void GapAdjudicatorUsageAndChildFactsAreNarratedWithoutRetryPromises()
    {
        var lines = Narrate("""
            [
              {"type":"tool.proposed","toolCallId":"t","name":"Read","args":{}},
              {"type":"tool.permission.decided","toolCallId":"t","decision":"allow","decisionSource":"classifier"},
              {"type":"tool.progress","toolCallId":"t","message":"working"},
              {"type":"tool.completed","toolCallId":"t","durationMs":500,"isError":true},
              {"type":"cost.usage.updated","model":"model","agentId":"a","usage":{"inputTokens":10,"outputTokens":20,"cacheReadInputTokens":30,"cacheCreationInputTokens":40}},
              {"type":"agent.spawned","agentId":"a"},
              {"type":"agent.completed","agentId":"a"},
              {"type":"session.microcompacted","evictedBlocks":3,"recoveredTokens":1200},
              {"type":"session.events_dropped","droppedCount":15,"reason":"count_limit","scope":"sdk.events"},
              {"type":"server.replay.gap","reason":"ahead_of_log"},
              {"type":"turn.started"}
            ]
            """, NarratorVerbosity.Verbose);
        Assert.Contains("[12:03:01] tool Read allowed by adjudicator", lines);
        Assert.Contains("[12:03:03] tool Read completed with error (0.5s)", lines);
        Assert.Contains("[12:03:04] usage +100 tokens (model, subagent)", lines);
        Assert.Contains("[12:03:05] subagent a spawned", lines);
        Assert.Contains("[12:03:06] subagent a completed", lines);
        Assert.Contains("[12:03:07] history microcompacted (3 blocks, 1,200 tokens reclaimed)", lines);
        Assert.Contains("[12:03:08] events dropped [sdk.events]: 15 event(s) dropped (count_limit)", lines);
        Assert.Equal("[12:03:09] event replay gap (ahead_of_log) — authoritative history required", lines.Last());
        Assert.DoesNotContain(lines, line => line.Contains("retry"));
    }

    [Fact]
    public void SinkFailureDisposeBoundsAndInvalidOptionsDoNotInventEvents()
    {
        var calls = 0; var lines = new List<string>();
        var narrator = new SessionNarrator(line => { calls++; if (calls == 1) throw new InvalidOperationException(); lines.Add(line); },
            new SessionNarratorOptions { MaximumLineCharacters = 80 });
        narrator.Apply(Event("""{"type":"turn.started"}""", 0));
        narrator.Apply(Event(JsonSerializer.Serialize(new { type = "turn.error", message = new string('x', 10000), scope = "sdk.notice", recoverable = true }), 1));
        Assert.Equal(2, calls); Assert.InRange(Assert.Single(lines).Length, 1, 80);
        narrator.Dispose(); narrator.Dispose(); narrator.Apply(Event("""{"type":"turn.started"}""", 2));
        Assert.Equal(2, calls);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionNarrator(_ => { }, new SessionNarratorOptions { Verbosity = (NarratorVerbosity)100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionNarrator(_ => { }, new SessionNarratorOptions { MaximumLineCharacters = 1 }));
    }

    [Fact]
    public void DeliveryChangesAffectOnlyNewBlocksWithoutRebuildingHistory()
    {
        using var view = new SessionView(new SessionViewOptions { ThinkingDelivery = ThinkingDeliveryMode.Off });
        view.Apply(Event("""{"type":"turn.started"}""", 0));
        view.Apply(Event("""{"type":"msg.block.start","index":0,"blockType":"text"}""", 1));
        view.Apply(Event("""{"type":"msg.block.start","index":1,"blockType":"thinking"}""", 2));
        view.SetDelivery(TextDeliveryMode.Final, ThinkingDeliveryMode.Stream);
        view.Apply(Event("""{"type":"msg.text.delta","index":0,"text":"existing stream"}""", 3));
        view.Apply(Event("""{"type":"msg.thinking.delta","index":1,"text":"HIDDEN_SECRET"}""", 4));
        Assert.Equal("existing stream", Assert.Single(view.Snapshot.Messages[0].Parts).Text);
        view.Apply(Event("""{"type":"msg.block.start","index":2,"blockType":"text"}""", 5));
        view.Apply(Event("""{"type":"msg.text.delta","index":2,"text":"new final"}""", 6));
        Assert.Single(view.Snapshot.Messages[0].Parts);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.SetDelivery(TextDeliveryMode.Stream, (ThinkingDeliveryMode)100));
        view.Apply(Event("""{"type":"msg.block.end","index":2}""", 7));
        Assert.Equal("new final", view.Snapshot.Messages[0].Parts[1].Text);
        view.Apply(Event("""{"type":"msg.block.start","index":3,"blockType":"thinking"}""", 8));
        view.Apply(Event("""{"type":"msg.thinking.delta","index":3,"text":"new visible"}""", 9));
        Assert.Equal("new visible", view.Snapshot.Messages[0].Parts[2].Text);
        Assert.DoesNotContain(view.Snapshot.Messages[0].Parts, part => part.Text.Contains("HIDDEN_SECRET"));
        view.Apply(Event("""{"type":"msg.block.start","index":4,"blockType":"text"}""", 10));
        view.Apply(Event("""{"type":"msg.text.delta","index":4,"text":"still final"}""", 11));
        Assert.Equal(3, view.Snapshot.Messages[0].Parts.Count);
    }

    [Fact]
    public void ThinkingOffRemovesVisibleAndBufferedPartsOnceAndCannotReviveThem()
    {
        using var view = new SessionView();
        view.Apply(Event("""{"type":"turn.started"}""", 0));
        view.Apply(Event("""{"type":"msg.block.start","index":0,"blockType":"thinking"}""", 1));
        var original = Event("""{"type":"msg.thinking.delta","index":0,"text":"visible chain"}""", 2);
        view.Apply(original);
        var oldSnapshot = view.Snapshot;
        view.SetDelivery(TextDeliveryMode.Stream, ThinkingDeliveryMode.Final);
        view.Apply(Event("""{"type":"msg.block.start","index":1,"blockType":"thinking"}""", 3));
        view.Apply(Event("""{"type":"msg.thinking.delta","index":1,"text":"buffered chain"}""", 4));
        var notifications = 0;
        using var subscription = view.Subscribe(_ => notifications++);
        var before = notifications;
        view.SetDelivery(TextDeliveryMode.Stream, ThinkingDeliveryMode.Off);
        Assert.Equal(before + 1, notifications);
        Assert.Empty(view.Snapshot.Messages.SelectMany(message => message.Parts));
        view.SetDelivery(TextDeliveryMode.Stream, ThinkingDeliveryMode.Off);
        Assert.Equal(before + 1, notifications);
        view.SetDelivery(TextDeliveryMode.Stream, ThinkingDeliveryMode.Stream);
        view.Apply(Event("""{"type":"msg.thinking.delta","index":0,"text":"must stay hidden"}""", 5));
        view.Apply(Event("""{"type":"msg.block.end","index":0}""", 6));
        view.Apply(Event("""{"type":"msg.block.end","index":1}""", 7));
        Assert.Empty(view.Snapshot.Messages.SelectMany(message => message.Parts));
        Assert.Equal("visible chain", oldSnapshot.Messages[0].Parts[0].Text);
        Assert.Equal("visible chain", original.Data.GetProperty("text").GetString());
        view.Apply(Event("""{"type":"msg.block.start","index":2,"blockType":"thinking"}""", 8));
        view.Apply(Event("""{"type":"msg.thinking.delta","index":2,"text":"new chain"}""", 9));
        Assert.Equal("new chain", Assert.Single(view.Snapshot.Messages.SelectMany(message => message.Parts)).Text);
    }

    private static List<string> Narrate(string json, NarratorVerbosity verbosity)
    {
        var result = new List<string>();
        using var narrator = new SessionNarrator(result.Add, new SessionNarratorOptions { Verbosity = verbosity });
        using var source = JsonDocument.Parse(json); var sequence = 0;
        foreach (var body in source.RootElement.EnumerateArray()) narrator.Apply(Event(body.GetRawText(), sequence++));
        return result;
    }

    private static AgentEvent Event(string json, int sequence)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node["seq"] = sequence; node["sessionId"] = "sess-narr";
        node["ts"] = new DateTimeOffset(2026, 8, 28, 12, 3, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() + sequence * 1000L;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return new AgentEvent("event", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), document.RootElement);
    }
}
