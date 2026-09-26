using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Execution;

public enum ExecutionRecoveryDisposition { Confirmed, Submitted, OutcomeUnknown, Conflict }

public sealed class ExecutionRecoveryItem
{
    internal ExecutionRecoveryItem(string sessionId, string operationId, ExecutionRecoveryDisposition disposition)
    { SessionId = sessionId; OperationId = operationId; Disposition = disposition; }
    public string SessionId { get; }
    public string OperationId { get; }
    public ExecutionRecoveryDisposition Disposition { get; }
}

public sealed class ExecutionRecoveryPage
{
    internal ExecutionRecoveryPage(IReadOnlyList<ExecutionRecoveryItem> items, string? afterOperationId)
    { Items = items; AfterOperationId = afterOperationId; }
    public IReadOnlyList<ExecutionRecoveryItem> Items { get; }
    /// <summary>继续读取原账本的游标；null 表示已读到空页。非空不承诺还有记录。</summary>
    public string? AfterOperationId { get; }
}

/// <summary>
/// 重启后逐页核对原操作和已耐久终态；不登记新设备、不领取、不执行，不把 Pending 当作可重试。
/// 只有原主体相同的历史事实可以补投，旧授权修订不能用于新派工。
/// </summary>
public sealed class ExecutionRecovery
{
    private readonly IExecutionClient _client;
    private readonly IExecutorJournal _journal;
    public ExecutionRecovery(IExecutionClient client, IExecutorJournal journal)
    { _client = client ?? throw new ArgumentNullException(nameof(client)); _journal = journal ?? throw new ArgumentNullException(nameof(journal)); }

    public async Task<ExecutionRecoveryPage> ReconcilePageAsync(string? afterOperationId = null, CancellationToken cancellationToken = default)
    {
        var scope = _client.ReadScope();
        WireJson.ValidateNamed("Scope", scope);
        var operations = await _journal.OperationsAsync(afterOperationId, cancellationToken).ConfigureAwait(false);
        Current(scope, cancellationToken);
        if (operations.Count > 128) throw new InvalidDataException("执行账本页超过128项。");
        var output = new List<ExecutionRecoveryItem>();
        string? cursor = afterOperationId;
        foreach (var operation in operations)
        {
            ExecutionJson.Operation(operation);
            var id = ExecutionJson.Text(operation, "operationId");
            var session = ExecutionJson.Text(operation, "sessionId");
            if (cursor != null && string.CompareOrdinal(id, cursor) <= 0) throw new InvalidDataException("执行账本游标没有前进。");
            SameSubject(scope, operation.GetProperty("scope"));
            var local = await _journal.ReceiptAsync(operation, cancellationToken).ConfigureAwait(false);
            Current(scope, cancellationToken);
            if (local.HasValue) ExecutionJson.Receipt(operation, local.Value);
            var status = await _client.GetStatusAsync(session, id, cancellationToken).ConfigureAwait(false);
            Current(scope, cancellationToken);
            VerifyStatus(operation, status);
            var remote = status.GetProperty("receipt");
            ExecutionRecoveryDisposition disposition;
            if (remote.ValueKind != JsonValueKind.Null)
            {
                if (local.HasValue && !ExecutionJson.Equal(local.Value, remote)) disposition = ExecutionRecoveryDisposition.Conflict;
                else
                {
                    if (!local.HasValue)
                    {
                        // 仅归档服务端已确认的原终态；不从模型文本或HTTP接纳推断结果。
                        await _journal.CompleteAsync(operation, remote, cancellationToken).ConfigureAwait(false);
                        Current(scope, cancellationToken);
                    }
                    disposition = ExecutionRecoveryDisposition.Confirmed;
                }
            }
            else if (!local.HasValue) disposition = ExecutionRecoveryDisposition.OutcomeUnknown;
            else
            {
                try
                {
                    var submitted = await _client.SubmitAsync(local.Value, cancellationToken).ConfigureAwait(false);
                    Current(scope, cancellationToken);
                    VerifyStatus(operation, submitted);
                    ExecutionJson.Check(ExecutionJson.Equal(submitted.GetProperty("receipt"), local.Value));
                    disposition = ExecutionRecoveryDisposition.Submitted;
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    Current(scope, cancellationToken);
                    var reconciled = await _client.GetStatusAsync(session, id, cancellationToken).ConfigureAwait(false);
                    Current(scope, cancellationToken);
                    VerifyStatus(operation, reconciled);
                    var receipt = reconciled.GetProperty("receipt");
                    if (receipt.ValueKind == JsonValueKind.Null) throw; // 原提交结果仍未知，保持原事实，不再POST。
                    disposition = ExecutionJson.Equal(receipt, local.Value)
                        ? ExecutionRecoveryDisposition.Confirmed : ExecutionRecoveryDisposition.Conflict;
                }
            }
            output.Add(new ExecutionRecoveryItem(session, id, disposition));
            cursor = id;
        }
        return new ExecutionRecoveryPage(output.AsReadOnly(), operations.Count == 0 ? null : cursor);
    }

    private void Current(JsonElement original, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ExecutionJson.Check(ExecutionJson.Equal(original, _client.ReadScope()));
    }
    private static void SameSubject(JsonElement current, JsonElement previous) => ExecutionJson.Check(
        ExecutionJson.Text(current, "applicationScopeId") == ExecutionJson.Text(previous, "applicationScopeId") &&
        ExecutionJson.Text(current, "endUserId") == ExecutionJson.Text(previous, "endUserId"));
    private static void VerifyStatus(JsonElement operation, JsonElement status)
    {
        WireJson.ValidateNamed("ExecutionStatus", status);
        ExecutionJson.Check(ExecutionJson.Equal(operation, status.GetProperty("operation")));
        var receipt = status.GetProperty("receipt");
        if (receipt.ValueKind != JsonValueKind.Null)
        {
            ExecutionJson.Receipt(operation, receipt);
            ExecutionJson.Check(ExecutionJson.Text(status, "status") == ExecutionJson.Text(receipt, "status"));
        }
        else ExecutionJson.Check(ExecutionJson.Text(status, "status") is "pending" or "unknown");
    }
}
