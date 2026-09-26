using System.Text.Json;

namespace Tansr.Sdk.Archive;

/// <summary>原 sdk2-ext-v1 档案与材料端点；只读查账不推断发送成功。</summary>
public interface IArchiveClient
{
    JsonElement ReadScope();
    JsonElement GetEffectiveLimits(string bindingId);
    Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetBindingTargetAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> CreateBindingAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> GetBindingAsync(string bindingId, CancellationToken cancellationToken = default);
    Task<JsonElement> GetArchiveStatusAsync(string bindingId, CancellationToken cancellationToken = default);
    Task<JsonElement> ReadRecordsAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> ReadArtifactAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> AcknowledgeAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> GetOperationAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> UploadMaterialChunkAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> GetMaterialUploadStatusAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> RespondMaterialsAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> GetMaterialStatusAsync(JsonElement request, CancellationToken cancellationToken = default);
    Task<JsonElement> CloseBindingAsync(JsonElement request, CancellationToken cancellationToken = default);
}
