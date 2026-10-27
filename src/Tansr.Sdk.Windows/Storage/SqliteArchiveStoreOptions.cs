using System.Text.Json;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Storage;

public sealed class ArchiveStoreLimits
{
    public int MaxRecords { get; set; } = 10000;
    public int MaxArtifacts { get; set; } = 20000;
    public long MaxStoredBytes { get; set; } = 268435456;
    public int MaxBatchBytes { get; set; } = 33554432;
}

/// <summary>显式选择本地文件布局。既有文件不自动迁移。</summary>
public enum ArchiveSqliteLayout
{
    LegacyAudited = 0,
    AuthenticatedIndexed = 1,
}

/// <summary>既有 SDK2 同步文件族的 source/cache 介质；不声明跨组织共享读取支持。</summary>
public sealed class SqliteArchiveStoreOptions
{
    public string Path { get; set; } = "";
    public StorageOpenMode Mode { get; set; }
    /// <summary>新认证索引布局只支持带原 KeyProvider 的 source；旧明文恢复文件族继续完整审计。</summary>
    public ArchiveSqliteLayout Layout { get; set; }
    public JsonElement Identity { get; set; }
    /// <summary>原 replica 对象，包含 replicationId 及 primary/replica role。</summary>
    public JsonElement Replica { get; set; }
    /// <summary>原持久 syncRole：source 可接收上游档案并确认 ACK，cache 仅接收原同步页。重开不能换角色。</summary>
    public string SyncRole { get; set; } = "source";
    public ArchiveStoreLimits Limits { get; set; } = new ArchiveStoreLimits();
    public int MaxPages { get; set; } = 65536;
    public Func<JsonElement> ReadContext { get; set; } = null!;
    /// <summary>从当前可信授权取得，不可从本地旧备份自证。</summary>
    public Func<string> ReadRetentionRevision { get; set; } = null!;
    /// <summary>核对核心已受理的原删除意图及完整范围；拒绝须抛错。</summary>
    public Action<JsonElement> AuthorizeRetention { get; set; } = null!;
    /// <summary>设置时选择原密文文件族；缺席时显式选择原明文同步文件族，不自动降级。</summary>
    public IArchiveKeyProvider? KeyProvider { get; set; }
}
