using System.Text.Json;

namespace Tansr.Sdk.Archive;

/// <summary>独立 sdk2-archive-recovery-v1 加法端点；调用前必须先耐久保存原恢复意图。</summary>
public interface IArchiveRecoveryClient : IArchiveClient
{
    /// <summary>只提交已经耐久固定的恢复请求。失回重发完全相同意图，不重跑模型。</summary>
    Task<JsonElement> RebaseAckAsync(JsonElement request, CancellationToken cancellationToken = default);
}
