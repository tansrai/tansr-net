using System.Text.Json;

namespace Tansr.Sdk.Windows.Storage;

public sealed class SqliteMaterialResponseOutboxOptions
{
    /// <summary>可信宿主指定的独立绝对路径；父目录必须存在，不得与档案数据库共用。</summary>
    public string Path { get; set; } = "";
    /// <summary>Create 不覆盖，Reopen 不重建未知或损坏介质。</summary>
    public StorageOpenMode Mode { get; set; }
    /// <summary>原 receiver identity，固定应用、用户、绑定、逻辑会话和来源代际。</summary>
    public JsonElement Identity { get; set; }
    public Func<JsonElement> ReadContext { get; set; } = null!;
    /// <summary>4096 字节页，允许 8–1024 页；单份响应另有固定的 262144 字节上限。</summary>
    public int MaxPages { get; set; } = 128;
}
