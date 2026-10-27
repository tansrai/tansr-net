using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Views;

public sealed partial class SessionView
{
    /// <summary>
    /// Replaces message presentation from one authoritative history response and its own lastSeq.
    /// Does not restore permission tickets, token accounting, or infer the current running turn.
    /// The installed history clears the presentation gap, not missing runtime facts;
    /// local accepted inputs must be tracked separately by the host.
    /// An already observed terminal at the same log watermark is retained. History
    /// ahead of or behind that watermark, or any replay gap, cannot prove current state.
    /// </summary>
    public long RestoreHistory(JsonElement history, string expectedSessionId)
    {
        if (history.ValueKind != JsonValueKind.Object || SessionJson.String(history, "sessionId") != expectedSessionId ||
            !history.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            throw new TansrProtocolException("invalid_response");
        var sequence = SessionJson.LastSequence(history);
        // A page must prove complete coverage. Never silently promote a partial page to a full snapshot.
        if (history.TryGetProperty("offset", out var offset) && (offset.ValueKind != JsonValueKind.Number || !offset.TryGetInt64(out var start) || start != 0) ||
            history.TryGetProperty("total", out var total) && (total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out var count) || count != messages.GetArrayLength()))
            throw new TansrProtocolException("incomplete_history_snapshot");
        foreach (var message in messages.EnumerateArray())
        {
            var role = String(message, "role");
            if (role != "user" && role != "assistant" || Property(message, "blocks")?.ValueKind != JsonValueKind.Array)
                throw new TansrProtocolException("invalid_response");
            foreach (var block in message.GetProperty("blocks").EnumerateArray())
                if (block.ValueKind != JsonValueKind.Object || String(block, "t") == null)
                    throw new TansrProtocolException("invalid_response");
        }
        return InstallHistoryPresentation(history, expectedSessionId, sequence);
    }

    /// <summary>
    /// Installs a server-selected settled window at a new observation origin. Omitted older
    /// messages remain browse-only. No approvals, usage, or terminal events are reconstructed.
    /// expectedVersion rejects a response if this view changed while the HTTP read was pending.
    /// </summary>
    public long RestoreRecoveryProjection(SessionRecoveryProjection projection, long expectedVersion)
    {
        if (projection == null) throw new ArgumentNullException(nameof(projection));
        return InstallHistoryPresentation(projection.Data, projection.SessionId, projection.HistorySequence, expectedVersion,
            projection.FirstSequence, projection.HistoryGeneration);
    }

    internal void MarkRecoveryPending()
    {
        Update(() => { _gap = true; _runtimeUnknown = true; return true; });
    }

    private long? _recoveryFirstSequence, _recoveryHistoryGeneration;

    private long InstallHistoryPresentation(JsonElement history, string expectedSessionId, long sequence,
        long? expectedVersion = null, long? firstSequence = null, long? historyGeneration = null)
    {
        var messages = history.GetProperty("messages");
        Update(() =>
        {
            if (expectedVersion.HasValue && _version != expectedVersion.Value)
                throw new TansrProtocolException("recovery_projection_stale");
            if (_sessionId != null && _sessionId != expectedSessionId) throw new InvalidOperationException("history_session_mismatch");
            if (firstSequence.HasValue && !_epochLost && _recoveryFirstSequence == firstSequence &&
                (_lastSequence > sequence || _recoveryHistoryGeneration > historyGeneration))
                throw new TansrProtocolException("recovery_projection_stale");
            // A settlement read can replace messages after the terminal was delivered.
            // Keep that original event's projection only at the same known log position;
            // never infer a terminal from messages or carry one into another epoch.
            var keepTerminal = !_epochLost && !_runtimeUnknown && !_running &&
                (firstSequence.HasValue
                    ? _observedTerminalSequence.HasValue && _observedTerminalSequence >= firstSequence &&
                        _observedTerminalSequence <= sequence && _lastSequence <= sequence &&
                        history.GetProperty("terminal").ValueKind == JsonValueKind.Object &&
                        history.GetProperty("terminal").GetProperty("seq").GetInt64() == _observedTerminalSequence &&
                        (!_recoveryFirstSequence.HasValue || _recoveryFirstSequence == firstSequence)
                    : !_gap && _observedTerminalSequence == sequence && _lastSequence == sequence);
            _recoveryFirstSequence = firstSequence; _recoveryHistoryGeneration = historyGeneration;
            _sessionId = expectedSessionId; _lastSequence = sequence; _epochLost = false; _gap = false;
            _messages.Clear(); _tools.Clear(); _blocks.Clear(); _requests.Clear(); _agents.Clear(); _todos.Clear();
            _current = null; _structuredBytes = 0; _running = false; _runtimeUnknown = !keepTerminal; _compacting = false;
            _sealed = false;
            if (!keepTerminal) { _failed = false; _error = null; _observedTerminalSequence = null; }
            var calls = new Dictionary<string, Tool>(StringComparer.Ordinal);
            foreach (var source in messages.EnumerateArray().Skip(Math.Max(0, messages.GetArrayLength() - _options.MaximumMessages)))
            {
                var message = AddMessage(String(source, "role")!);
                foreach (var block in source.GetProperty("blocks").EnumerateArray().Take(_options.MaximumPartsPerMessage))
                {
                    var kind = String(block, "t");
                    if (kind == "text" || kind == "thinking")
                    {
                        if (kind == "thinking" && _options.ThinkingDelivery == ThinkingDeliveryMode.Off) continue;
                        var text = String(block, "text") ?? "";
                        AddPart(message, new Part(kind) { Text = Prefix(text), Truncated = text.Length > _options.MaximumTextCharacters });
                    }
                    else if (kind == "tool_call")
                    {
                        var id = String(block, "id");
                        if (id == null) continue;
                        if (_tools.Count >= 1024) { _truncated = true; continue; }
                        var tool = new Tool(id, "tool-" + (++_nextTool).ToString(System.Globalization.CultureInfo.InvariantCulture), String(block, "name") ?? id)
                        { Status = "historical" };
                        _tools[tool.InstanceId] = tool; calls[id] = tool;
                        AddPart(message, new Part("toolCall") { ToolId = id, ToolInstanceId = tool.InstanceId });
                    }
                    else if (kind == "tool_result")
                    {
                        var id = String(block, "callId");
                        var content = Property(block, "content");
                        var text = content?.ValueKind == JsonValueKind.Array
                            ? string.Join("\n", content.Value.EnumerateArray().Where(x => String(x, "t") == "text").Select(x => String(x, "text") ?? "")) : "";
                        if (id != null && calls.TryGetValue(id, out var tool))
                        { tool.Status = Boolean(block, "isError") ? "failed" : "completed"; tool.ResultText = Prefix(text); }
                        else AddPart(message, new Part("text") { Text = "[historical tool result] " + Prefix(text) });
                    }
                    else if (kind == "image") AddPart(message, new Part("text") { Text = "[historical image]" });
                }
                _truncated |= source.GetProperty("blocks").GetArrayLength() > _options.MaximumPartsPerMessage;
            }
            _truncated |= messages.GetArrayLength() > _options.MaximumMessages;
            if (firstSequence.HasValue)
            {
                _truncated |= history.GetProperty("offset").GetInt64() > 0;
                Notice("recovery_projection_restored", "有界呈现已恢复；更早历史按需浏览，不恢复用量、审批或终态事件。");
            }
            else Notice("history_restored", "权威历史已恢复；历史不恢复事件用量、审批票据或当前运行状态。");
            return true;
        });
        return sequence;
    }
}
