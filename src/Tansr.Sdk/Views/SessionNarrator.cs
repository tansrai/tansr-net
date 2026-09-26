using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Views;

public enum NarratorVerbosity { Quiet, Normal, Verbose }

public sealed class SessionNarratorOptions
{
    public NarratorVerbosity Verbosity { get; set; } = NarratorVerbosity.Normal;
    public int MaximumLineCharacters { get; set; } = 4096;
    public long? InitialSequence { get; set; }
}

/// <summary>
/// Incremental English narration of the original session events. Feed Apply from the host's
/// single ObserveAsync pump; this class never opens another subscription or performs I/O.
/// Thinking is counted, never retained or printed. Line sink failures do not interrupt the pump.
/// </summary>
public sealed class SessionNarrator : IDisposable
{
    private readonly object _gate = new();
    private SessionView? _view;
    private readonly NarratorVerbosity _verbosity;
    private readonly int _maximumLineCharacters;
    private Action<string>? _onLine;

    public SessionNarrator(Action<string> onLine, SessionNarratorOptions? options = null)
    {
        _onLine = onLine ?? throw new ArgumentNullException(nameof(onLine));
        options ??= new SessionNarratorOptions();
        if (!Enum.IsDefined(typeof(NarratorVerbosity), options.Verbosity) || options.MaximumLineCharacters < 80 || options.MaximumLineCharacters > 65536)
            throw new ArgumentOutOfRangeException(nameof(options));
        _verbosity = options.Verbosity; _maximumLineCharacters = options.MaximumLineCharacters;
        _view = new SessionView(new SessionViewOptions
        {
            MaximumMessages = 8,
            MaximumPartsPerMessage = 256,
            MaximumTextCharacters = 4096,
            MaximumOutputCharacters = 1,
            ThinkingDelivery = ThinkingDeliveryMode.Off,
            InitialSequence = options.InitialSequence,
        });
    }

    /// <summary>Replay, sequence and session boundaries are handled by the existing SessionView projection.</summary>
    public void Apply(AgentEvent item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        lock (_gate)
        {
            if (_onLine == null) return;
            var view = _view!;
            var data = item.Data;
            var type = Text(data, "type") ?? item.Name;
            var payload = Property(data, "payload");
            if (type.StartsWith("server.", StringComparison.Ordinal) && payload?.ValueKind == JsonValueKind.Object) data = payload.Value;
            var before = view.Snapshot;
            var index = Number(data, "index");
            var block = index >= 0 && index <= int.MaxValue ? view.GetNarrationBlock((int)index) : null;
            var id = Text(data, "toolCallId") ?? "";
            var arguments = view.GetNarrationArguments(id);
            view.Apply(item);
            if (view.Snapshot.Version == before.Version) return;
            var line = Narrate(type, data, before, block, id, arguments);
            if (line == null) return;
            line = "[" + Clock(item.Data) + "] " + line;
            if (line.Length > _maximumLineCharacters) line = Limit(line, _maximumLineCharacters - 1);
            try { _onLine(line); }
            catch { /* Narration is observational; a broken sink cannot fail the session. */ }
        }
    }

    private string? Narrate(string type, JsonElement data, SessionViewSnapshot before, NarrationBlock? block, string id, string? arguments)
    {
        var name = before.Tools.LastOrDefault(tool => tool.Id == id && tool.IsActive)?.Name ??
            before.Tools.LastOrDefault(tool => tool.Id == id)?.Name ?? id;
        var normal = _verbosity >= NarratorVerbosity.Normal;
        var verbose = _verbosity == NarratorVerbosity.Verbose;
        var reason = Text(data, "reason");
        switch (type)
        {
            case "turn.started": return "turn started";
            case "turn.completed": return "turn completed" + (reason == "completed" ? "" : " (" + reason + ")") + " · " + Count(before.TurnTokens) + " tokens";
            case "turn.aborted": return "turn aborted (" + reason + ")";
            case "turn.error":
                return True(data, "recoverable")
                ? "turn notice [" + Text(data, "scope") + "]: " + Text(data, "message") + " — recoverable, session continues"
                : "turn error [" + Text(data, "scope") + "]: " + Text(data, "message") + " — this turn cannot be retried";
            case "session.events_dropped": return "events dropped [" + Text(data, "scope") + "]: " + Number(data, "droppedCount") + " event(s) dropped (" + reason + ")";
            case "server.replay.gap": return "event replay gap (" + reason + ") — authoritative history required";
            case "session.compacted":
                var indices = Property(data, "removedIndices"); var range = Property(data, "removedRange");
                var folded = indices?.ValueKind == JsonValueKind.Array ? indices.Value.GetArrayLength() :
                    range?.ValueKind == JsonValueKind.Array && range.Value.GetArrayLength() == 2 ? range.Value[1].GetInt64() - range.Value[0].GetInt64() + 1 : 0;
                return "history compacted (" + folded.ToString(CultureInfo.InvariantCulture) + " messages folded)";
            case "session.ended": return "session ended";
            case "tool.started": return normal ? "tool " + name + " started: " + (arguments ?? "null") : null;
            case "tool.completed": return normal ? "tool " + name + (True(data, "isError") ? " completed with error (" : " completed (") + Seconds(data) + ")" : null;
            case "tool.failed": return normal ? "tool " + name + " failed (" + Text(data, "errorType") + "): " + Text(data, "message") : null;
            case "tool.permission.requested": return normal ? "tool " + Text(data, "name") + " awaiting permission" : null;
            case "tool.permission.decided":
                var decision = Text(data, "decision"); var source = Text(data, "decisionSource"); var rule = Text(data, "matchedRule");
                if (decision == "deny") return normal ? "tool " + name + " denied (" + source + (rule == null ? "" : ", rule: " + rule) + ")" : null;
                if (source == "classifier") return normal ? "tool " + name + " allowed by adjudicator" + (rule == null ? "" : " (rule: " + rule + ")") : null;
                return verbose ? "permission " + decision + " for " + name + " (" + source + (rule == null ? "" : ", rule: " + rule) + ")" : null;
            case "tool.proposed": return verbose ? "tool " + Text(data, "name") + " proposed: " + Summarize(Property(data, "args")) : null;
            case "tool.progress": return verbose ? "tool " + name + " progress: " + Text(data, "message") : null;
            case "msg.block.end":
                if (!verbose || block == null) return null;
                if (block.Kind == "thinking" && block.Characters > 0) return "thinking finished (" + Count(block.Characters) + " chars)";
                return block.Kind == "text" && block.Text.Length > 0 ? "assistant: " + Limit("\"" + JsonEncodedText.Encode(block.Text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"", 60) : null;
            case "msg.retracted": return verbose ? "assistant output retracted (block " + Number(data, "index") + ")" : null;
            case "session.microcompacted": return verbose ? "history microcompacted (" + Number(data, "evictedBlocks") + " blocks, " + Count(Number(data, "recoveredTokens")) + " tokens reclaimed)" : null;
            case "cost.usage.updated":
                if (!verbose) return null;
                var usage = Property(data, "usage"); long tokens = 0;
                if (usage.HasValue)
                    foreach (var key in new[] { "inputTokens", "outputTokens", "cacheReadInputTokens", "cacheCreationInputTokens" })
                    { var added = Math.Max(0, Number(usage.Value, key)); tokens = tokens > long.MaxValue - added ? long.MaxValue : tokens + added; }
                var purpose = Text(data, "purpose") ?? (Text(data, "agentId") == null ? null : "subagent");
                return "usage +" + Count(tokens) + " tokens (" + Text(data, "model") + (purpose == null ? "" : ", " + purpose) + ")";
            case "agent.spawned": return verbose ? "subagent " + Text(data, "agentId") + " spawned" : null;
            case "agent.completed": return verbose ? "subagent " + Text(data, "agentId") + " completed" : null;
            default: return null;
        }
    }

    internal static string Summarize(JsonElement? value)
    {
        if (!value.HasValue) return "null";
        using var output = new JsonPrefixStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            value.Value.WriteTo(writer);
        return Limit(output.Text, 80);
    }
    private static string Limit(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        var length = maximum;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text.Substring(0, length) + "…";
    }
    private static string Clock(JsonElement data)
    {
        var timestamp = Property(data, "ts");
        if (timestamp?.ValueKind != JsonValueKind.Number || !timestamp.Value.TryGetInt64(out var milliseconds)) return "--:--:--";
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return "--:--:--"; }
    }
    private static string Seconds(JsonElement data) => (Number(data, "durationMs") / 1000d).ToString("F1", CultureInfo.InvariantCulture) + "s";
    private static string Count(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    private static JsonElement? Property(JsonElement data, string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) ? value : null;
    private static string? Text(JsonElement data, string key) { var value = Property(data, key); return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null; }
    private static long Number(JsonElement data, string key) { var value = Property(data, key); return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var number) ? number : 0; }
    private static bool True(JsonElement data, string key) => Property(data, key)?.ValueKind == JsonValueKind.True;

    public void Dispose()
    {
        lock (_gate) { if (_onLine == null) return; _onLine = null; _view!.Dispose(); _view = null; }
    }

    // Keep only enough UTF-8 bytes for the 80-character summary; discard the rest.
    private sealed class JsonPrefixStream : Stream
    {
        private readonly byte[] _prefix = new byte[512];
        private int _length;
        internal string Text => Encoding.UTF8.GetString(_prefix, 0, _length);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count)
        {
            var copied = Math.Min(count, _prefix.Length - _length);
            Buffer.BlockCopy(buffer, offset, _prefix, _length, copied); _length += copied;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class NarrationBlock(string kind, string text, long characters)
{
    internal readonly string Kind = kind, Text = text;
    internal readonly long Characters = characters;
}
