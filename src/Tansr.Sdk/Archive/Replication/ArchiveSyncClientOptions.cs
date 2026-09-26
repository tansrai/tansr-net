using System.Text.Json;

namespace Tansr.Sdk.Archive.Replication;

public sealed class ArchiveSyncClientOptions
{
    public Uri BaseUri { get; set; } = null!;
    public bool AllowInsecureLoopback { get; set; }
    /// <summary>开发者档案服务专用短票；每次读取当前票，调用中变更即拒绝，不自动重试写入。</summary>
    public Func<string> ReadToken { get; set; } = null!;
    public JsonElement Identity { get; set; }
    /// <summary>原 receiver 四项 limits：maxRecords/maxArtifacts/maxStoredBytes/maxBatchBytes。</summary>
    public JsonElement Limits { get; set; }
    public Func<JsonElement> ReadContext { get; set; } = null!;
    /// <summary>可选可信宿主只读授权：原 {actor,owner,grantRevision}，服务器仍须独立核当前成员权。</summary>
    public Func<JsonElement>? SharedReadAccess { get; set; }
}
