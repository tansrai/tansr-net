using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

// 应用层编辑器只消费 inputs 合同；不创建、打断或重建任何轮次。
internal sealed class TurnInputEditor
{
    private readonly AgentSession session;
    private readonly Action<TurnInputRecord> persist;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal TurnInputEditor(AgentSession session, TurnInputRecord? saved, Action<TurnInputRecord> persist)
    {
        this.session = session;
        this.persist = persist;
        if (saved != null && saved.SessionId == session.Id) Current = saved;
    }

    internal TurnInputRecord? Current { get; private set; }

    internal async Task<string> InsertAsync(string text, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current != null && !Current.CanStartAnother) throw new InvalidOperationException("input_unconfirmed_query_original_first");
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("input_text_required");
            if (text.Length > 262144) throw new InvalidOperationException("input_text_too_large_no_truncation");
            var caps = await session.GetInputCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (!caps.TryGetProperty("text", out var enabled) || enabled.ValueKind != JsonValueKind.True ||
                !caps.TryGetProperty("memoryAck", out var ack) || ack.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("same_turn_text_not_supported");
            if (!caps.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("no_active_turn_for_insertion");
            var record = new TurnInputRecord(session.Id, Guid.NewGuid().ToString("N"),
                target.GetProperty("historyEpoch").GetString()!, target.GetProperty("turnId").GetString()!, text, "prepared", null);
            // 原键和完整正文先落盘，失败则绝不发送。
            Save(record);
            return await SubmitOriginalAsync(record, cancellationToken, firstAttempt: true).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<string> QueryAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = Current ?? throw new InvalidOperationException("no_saved_input");
            try
            {
                var result = await session.GetInputStatusAsync(record.InputId, record.Target(), cancellationToken).ConfigureAwait(false);
                ValidateReceipt(result, record, false);
                Save(record.With("accepted", result.GetRawText()));
                return Describe();
            }
            catch (TansrHttpException error) when (error.DomainCode == "input_not_found")
            {
                // current-and-last-turn 保留窗外的 404 不能证明旧输入从未接纳。
                Save(record.With("not_found", null));
                return Describe();
            }
        }
        finally { gate.Release(); }
    }

    internal async Task<string> RetryOriginalAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = Current ?? throw new InvalidOperationException("no_saved_input");
            if (record.Outcome == "accepted") throw new InvalidOperationException("input_already_accepted_query_consumption");
            return await SubmitOriginalAsync(record, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal string Describe()
    {
        var record = Current;
        if (record == null) return "尚无同轮插入。";
        return "input=" + record.InputId + " target=" + record.HistoryEpoch + "/" + record.TurnId + " state=" + record.Outcome +
            (record.Outcome == "accepted" ? "；仅确认接纳，实际消费见原回执。" : record.Outcome == "not_found" ? "；原回执不存在或已过保留窗，不能推断未执行。" : "；原正文已保留，不自动改键或换轮重投。") +
            (record.ReceiptJson == null ? "" : "\n" + record.ReceiptJson);
    }

    private async Task<string> SubmitOriginalAsync(TurnInputRecord record, CancellationToken cancellationToken, bool firstAttempt = false)
    {
        try
        {
            var result = await session.SubmitInputAsync(record.InputId, record.Target(), record.Text, false, cancellationToken).ConfigureAwait(false);
            ValidateReceipt(result, record, true);
            Save(record.With("accepted", result.GetRawText()));
        }
        catch (TansrHttpException error) when (error.InputOutcome == "closed" || error.InputOutcome == "rejected")
        {
            // 原来未知的 POST 可能已经接纳；较晚的 closed/rejected 不能否定它。
            Save(record.With(firstAttempt ? error.InputOutcome : "unconfirmed", error.Code));
            throw;
        }
        catch
        {
            Save(record.With("unconfirmed", null));
            throw;
        }
        return Describe();
    }

    private void Save(TurnInputRecord record) { persist(record); Current = record; }
    private static void ValidateReceipt(JsonElement result, TurnInputRecord record, bool submission)
    {
        bool Text(JsonElement item, string name, string expected) => item.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == expected;
        if (result.ValueKind != JsonValueKind.Object || submission && !Text(result, "outcome", "accepted") ||
            !result.TryGetProperty("receipt", out var receipt) || receipt.ValueKind != JsonValueKind.Object ||
            !Text(receipt, "inputId", record.InputId) || !Text(receipt, "sessionId", record.SessionId) ||
            !Text(receipt, "historyEpoch", record.HistoryEpoch) || !Text(receipt, "turnId", record.TurnId) || !Text(receipt, "durability", "memory") ||
            !receipt.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
            !new[] { "reserved", "accepted", "consumed", "closed", "cancelled" }.Contains(state.GetString(), StringComparer.Ordinal) ||
            !receipt.TryGetProperty("ordinal", out var ordinal) || !ordinal.TryGetInt64(out var order) || order < 0 || order > 9007199254740991 ||
            !receipt.TryGetProperty("revision", out var revision) || !revision.TryGetInt64(out var version) || version < 0 || version > 9007199254740991 ||
            !(Text(receipt, "source", "strict") || Text(receipt, "source", "legacy")))
            throw new TansrProtocolException("input_receipt_identity_mismatch");
    }
}

internal sealed class TurnInputRecord
{
    internal TurnInputRecord(string sessionId, string inputId, string historyEpoch, string turnId, string text, string outcome, string? receiptJson)
    { SessionId = sessionId; InputId = inputId; HistoryEpoch = historyEpoch; TurnId = turnId; Text = text; Outcome = outcome; ReceiptJson = receiptJson; }
    internal string SessionId { get; }
    internal string InputId { get; }
    internal string HistoryEpoch { get; }
    internal string TurnId { get; }
    internal string Text { get; }
    internal string Outcome { get; }
    internal string? ReceiptJson { get; }
    internal bool CanStartAnother => Outcome == "accepted" || Outcome == "closed" || Outcome == "rejected";
    internal SessionInputTarget Target() => new(HistoryEpoch, TurnId);
    internal TurnInputRecord With(string outcome, string? receipt) => new(SessionId, InputId, HistoryEpoch, TurnId, Text, outcome, receipt);
}
