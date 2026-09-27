using System.Text.Json;

namespace Tansr.Sdk.Windows.Storage;

public sealed class SqliteExecutorJournalOptions
{
    /// <summary>可信宿主的绝对路径；父目录必须已存在。不得接受模型或 wire 路径。</summary>
    public string Path { get; set; } = "";
    /// <summary>Create 不覆盖已有文件；Reopen 不修复或重建未知介质。</summary>
    public StorageOpenMode Mode { get; set; }
    /// <summary>显式选择独立紧凑介质；终态释放未用预留但永久保留原键和完整回执。重开时须保持原选择；默认兼容原 v1，不迁移旧库。</summary>
    public bool CompactCompletedReceipts { get; set; }
    public string ApplicationScopeId { get; set; } = "";
    public string EndUserId { get; set; } = "";
    public string ExecutorId { get; set; } = "";
    public int MaxOperations { get; set; } = 4096;
    public long MaxStoredBytes { get; set; } = 67108864;
    public int MaxPages { get; set; } = 32768;
    /// <summary>每次操作读取当前原 Scope；撤销时抛错。回调不能取得额外服务端权限。</summary>
    public Func<JsonElement> ReadContext { get; set; } = null!;
}

public enum StorageOpenMode { Create, Reopen, MigrateV1 }
