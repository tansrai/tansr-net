using System.Text.Json;

namespace Tansr.Sdk.Storage;

/// <summary>原 SDK2 操作的耐久占用。只有首次 Claimed 可以开始副作用，Pending 永不自动重新授予。</summary>
public interface IExecutorJournal
{
    Task<ExecutorJournalClaim> ClaimAsync(JsonElement operation, CancellationToken cancellationToken = default);
    Task CompleteAsync(JsonElement operation, JsonElement receipt, CancellationToken cancellationToken = default);
    Task<JsonElement?> ReceiptAsync(JsonElement operation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JsonElement>> OperationsAsync(string? afterOperationId = null, CancellationToken cancellationToken = default);
    Task CloseAsync();
}

/// <summary>可选介质保证；全 operation/receipt（包括工具参数及结果）在持久化前已加密。</summary>
public interface IEncryptedExecutorJournal
{
    bool EncryptedAtRest { get; }
}

public enum ExecutorJournalClaimStatus { Claimed, Pending, Completed }

public sealed class ExecutorJournalClaim
{
    public ExecutorJournalClaimStatus Status { get; }
    public JsonElement? Receipt { get; }

    private ExecutorJournalClaim(ExecutorJournalClaimStatus status, JsonElement? receipt)
    {
        Status = status;
        Receipt = receipt?.Clone();
    }

    public static ExecutorJournalClaim Claimed() => new ExecutorJournalClaim(ExecutorJournalClaimStatus.Claimed, null);
    public static ExecutorJournalClaim Pending() => new ExecutorJournalClaim(ExecutorJournalClaimStatus.Pending, null);
    public static ExecutorJournalClaim Completed(JsonElement receipt) => new ExecutorJournalClaim(ExecutorJournalClaimStatus.Completed, receipt);
}
