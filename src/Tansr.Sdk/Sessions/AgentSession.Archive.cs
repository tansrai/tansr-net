using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Sessions;

public sealed partial class AgentSession
{
    public async Task<SessionMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default)
        => new SessionMetadata(await GetMetadataAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>创建并导出上下文快照。导出失败时保留已经创建的快照，不自动删除或重新创建。</summary>
    /// <remarks>这是现有 checkpoint 合同的组合；不宣称导出完整长历史或记忆。</remarks>
    public async Task<SessionExport> ExportAsync(string? label = null, CancellationToken cancellationToken = default)
    {
        var checkpoint = await CheckpointAsync(label, cancellationToken).ConfigureAwait(false);
        var checkpointId = SessionJson.String(checkpoint, "checkpointId");
        if (SessionJson.String(checkpoint, "sessionId") != Id) throw new TansrProtocolException("invalid_response");
        return new SessionExport(checkpointId, await ExportCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>仅导入上下文快照；显式调用 RestoreCheckpointAsync 才替换当前上下文。</summary>
    public async Task<JsonElement> ImportAsync(byte[] bytes, string? label = null, CancellationToken cancellationToken = default)
    {
        var checkpoint = await ImportCheckpointAsync(bytes, label, cancellationToken).ConfigureAwait(false);
        if (SessionJson.String(checkpoint, "sessionId") != Id) throw new TansrProtocolException("invalid_response");
        SessionJson.String(checkpoint, "checkpointId");
        return checkpoint;
    }

    /// <summary>使用冻结 create.fork 新建独立会话。源会话不修改；SDK2-offload 不支持时明确拒绝。</summary>
    public Task<AgentSession> ForkAsync(string checkpointId, CreateSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copy = SessionRequestWriter.Copy(options);
        if (copy.ResumeSessionId is not null || copy.ForkSessionId is not null || copy.ForkCheckpointId is not null)
            throw new ArgumentException("Fork options cannot already specify resume or fork.", nameof(options));
        copy.ForkSessionId = Id; copy.ForkCheckpointId = checkpointId;
        return client.CreateSessionAsync(copy, cancellationToken);
    }
}
