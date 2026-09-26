using System.Text.Json;

namespace Tansr.Sdk.Storage;

/// <summary>原恢复接收器的加法能力。旧 IArchiveStore 无需实现；恢复需显式新版介质。</summary>
public interface IRecoverableArchiveStore : IArchiveStore
{
    /// <summary>先耐久固定恢复请求键并预留完整回执空间；原 pending ACK 保持不变。</summary>
    Task<JsonElement> PrepareAckRebaseAsync(JsonElement request, CancellationToken cancellationToken = default);
    /// <summary>仅返回本地仍未完成的原恢复意图，不联网、不生成新键。</summary>
    Task<JsonElement?> PendingAckRebaseAsync(CancellationToken cancellationToken = default);
    /// <summary>验证真实 next ACK 回执，原子保存旧映射、替换当前批次身份并确认。</summary>
    Task ConfirmAckRebaseAsync(JsonElement receipt, CancellationToken cancellationToken = default);
}
