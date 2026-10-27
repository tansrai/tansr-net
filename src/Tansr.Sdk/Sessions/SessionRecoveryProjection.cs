using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Sessions;

/// <summary>A bounded, settled presentation window. It contains no permission authority or usage history.</summary>
public sealed class SessionRecoveryProjection
{
    private SessionRecoveryProjection(JsonElement value)
    {
        Data = value.Clone(); SessionId = Text(value, "sessionId"); FirstSequence = Number(value, "firstSeq");
        LastSequence = Number(value, "lastSeq"); HistorySequence = Number(value, "historyLastSeq");
        HistoryGeneration = Number(value, "historyGeneration"); Status = Text(value, "status");
    }
    public JsonElement Data { get; }
    public string SessionId { get; }
    public long FirstSequence { get; }
    public long LastSequence { get; }
    public long HistorySequence { get; }
    public long HistoryGeneration { get; }
    /// <summary>Settled state at the snapshot point, not a synthesized terminal event.</summary>
    public string Status { get; }

    internal static SessionRecoveryProjection Parse(JsonElement value, string sessionId, SessionRecoveryRequest request)
    {
        if (value.ValueKind != JsonValueKind.Object || Text(value, "kind") != "tansr-session-recovery-projection-v1" ||
            Text(value, "sessionId") != sessionId || Number(value, "requestedAfterSeq", -1) != request.AfterSequence ||
            NullableNumber(value, "requestedFirstSeq") != request.ExpectedFirstSequence ||
            NullableNumber(value, "requestedMinTerminalSeq") != request.MinimumTerminalSequence ||
            !value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array ||
            messages.GetArrayLength() > request.MaximumMessages || Encoding.UTF8.GetByteCount(value.GetRawText()) > request.MaximumBytes ||
            !value.TryGetProperty("windowComplete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw Invalid();
        var first = Number(value, "firstSeq"); var history = Number(value, "historyLastSeq"); var last = Number(value, "lastSeq");
        var offset = Number(value, "offset"); var total = Number(value, "total"); var next = Number(value, "nextOffset");
        _ = Number(value, "historyGeneration");
        if (first > history || history > last || request.ExpectedFirstSequence.HasValue && request.ExpectedFirstSequence != first ||
            request.MinimumTerminalSequence.HasValue && (request.MinimumTerminalSequence < first || request.MinimumTerminalSequence > history) ||
            offset > next || next != total || messages.GetArrayLength() > next - offset ||
            !value.TryGetProperty("historyComplete", out var full) || (full.ValueKind != JsonValueKind.True && full.ValueKind != JsonValueKind.False) ||
            full.GetBoolean() != (offset == 0) || Text(value, "status") is not ("idle" or "ended")) throw Invalid();
        if (!value.TryGetProperty("terminal", out var terminal)) throw Invalid();
        if (terminal.ValueKind != JsonValueKind.Null && (terminal.ValueKind != JsonValueKind.Object ||
            Text(terminal, "type") is not ("turn.completed" or "turn.aborted") ||
            Number(terminal, "seq") < first || Number(terminal, "seq") > history)) throw Invalid();
        foreach (var message in messages.EnumerateArray())
        {
            if (Text(message, "role") is not ("user" or "assistant") ||
                !message.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array) throw Invalid();
            foreach (var block in blocks.EnumerateArray()) if (block.ValueKind != JsonValueKind.Object || Text(block, "t").Length == 0) throw Invalid();
        }
        return new SessionRecoveryProjection(value);
    }

    private static string Text(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Invalid();
    private static long Number(JsonElement value, string field, long minimum = 0) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) && number >= minimum && number <= 9007199254740991L ? number : throw Invalid();
    private static long? NullableNumber(JsonElement value, string field) => value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.Null ? null : Number(value, field);
    private static TansrProtocolException Invalid() => new("invalid_recovery_projection");
}
