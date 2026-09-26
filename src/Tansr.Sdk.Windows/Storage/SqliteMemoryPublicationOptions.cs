using System.Text.Json;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>显式选择 Serve 候选设备记忆介质。身份和路径必须来自可信宿主配置。</summary>
public sealed class SqliteMemoryPublicationOptions
{
    public bool EnablePreview { get; set; }
    public string Path { get; set; } = "";
    public StorageOpenMode Mode { get; set; }
    /// <summary>原 Node identity：scope（applicationScopeId/endUserId）、sourceId、sourceGeneration、domainKey。</summary>
    public JsonElement Identity { get; set; }
    public Func<JsonElement> ReadContext { get; set; } = null!;
    /// <summary>必须显式规划；终态回执永久保留，不能通过重开提高上限或复用 transferId。</summary>
    public int MaxTransfers { get; set; }
    public int MaxStagingBytes { get; set; } = 8388608;
    public int MaxPages { get; set; } = 8192;
    /// <summary>可信恢复见证，只允许新 owner 查询旧 transfer；不转移写权限。</summary>
    public Func<SqliteMemoryPublicationRecoveryContext, bool>? AuthorizeRecovery { get; set; }
}

public sealed class SqliteMemoryPublicationRecoveryContext
{
    internal SqliteMemoryPublicationRecoveryContext(JsonElement identity, string transferId, JsonElement originalOwner, JsonElement currentOwner)
    { Identity = identity.Clone(); TransferId = transferId; OriginalOwner = originalOwner.Clone(); CurrentOwner = currentOwner.Clone(); }
    public JsonElement Identity { get; }
    public string TransferId { get; }
    public JsonElement OriginalOwner { get; }
    public JsonElement CurrentOwner { get; }
}

public sealed class SqliteMemoryPublicationCapacity
{
    internal SqliteMemoryPublicationCapacity(int maxTransfers, long storedTransfers, int maxStagingBytes, long stagingBytes, int maxPages, long allocatedPages, long reusablePages)
    { MaxTransfers = maxTransfers; StoredTransfers = storedTransfers; MaxStagingBytes = maxStagingBytes; StagingBytes = stagingBytes; MaxPages = maxPages; AllocatedPages = allocatedPages; ReusablePages = reusablePages; }
    public int MaxTransfers { get; }
    public long StoredTransfers { get; }
    public long RemainingTransfers => MaxTransfers - StoredTransfers;
    public int MaxStagingBytes { get; }
    public long StagingBytes { get; }
    public long RemainingStagingBytes => MaxStagingBytes - StagingBytes;
    public int MaxPages { get; }
    public long AllocatedPages { get; }
    public long ReusablePages { get; }
}

/// <summary>确定的 publication 业务拒绝；不代表 COMMIT 未知或执行回执已经耐久。</summary>
public sealed class SqliteMemoryPublicationException : Exception
{
    public SqliteMemoryPublicationException(string code) : base("Tansr memory publication: " + code) { Code = code; }
    public string Code { get; }
}
