using System.Text.Json;

namespace Tansr.Sdk.Storage;

/// <summary>原接收器的耐久副本身份与操作查账；角色由介质固定，不能由组合器临时自报。</summary>
public interface IReplicaArchiveStore : IArchiveRetentionStore
{
    Task<JsonElement> ReplicaIdentityAsync(CancellationToken cancellationToken = default);
    Task<JsonElement?> ReplicaOperationAsync(JsonElement requestIdentity, CancellationToken cancellationToken = default);
}

/// <summary>原 archive-sync-v1 档案同步。缓存接收只返回同步事实，绝不生成推进来源覆盖的 ACK。</summary>
public interface ISyncArchiveStore : IReplicaArchiveStore
{
    Task<JsonElement?> SyncPageAsync(string? afterSequence, CancellationToken cancellationToken = default);
    Task<JsonElement> ReceiveSyncAsync(ArchiveSyncReceiveInput input, CancellationToken cancellationToken = default);
}

public sealed class ArchiveSyncReceiveInput
{
    public JsonElement Page { get; set; }
    public IReadOnlyList<ArchiveArtifact> Artifacts { get; set; } = Array.Empty<ArchiveArtifact>();
}
