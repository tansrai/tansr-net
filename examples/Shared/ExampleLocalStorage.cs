using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

// 仅为三种宿主组合公开存储入口；协议、ACK、上下文与材料选择仍由 SDK/Serve 负责。
internal sealed class ExampleLocalStorage
{
    private readonly TansrClient client;
    private readonly AgentSession session;
    private readonly Uri endpoint;
    private readonly SemaphoreSlim gate = new(1, 1);
    private NativeSnapshotHost? snapshot;
    private NativeArchiveHost? archive;
    private NativeCacheContinuityHost? cache;
    private bool closed;
    internal ExampleLocalStorage(TansrClient client, AgentSession session, Uri endpoint)
    { this.client = client; this.session = session; this.endpoint = endpoint; }
    internal string PendingCleanupStatus => snapshot?.PendingCleanupStatus ?? "";

    internal async Task<string> ExecuteAsync(int action, string argument, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (closed) throw new ObjectDisposedException(nameof(ExampleLocalStorage));
            switch (action)
            {
                case 11:
                    if (snapshot != null) throw new InvalidOperationException("snapshot_configuration_already_open");
                    snapshot = await NativeSnapshotHost.OpenAsync(session, endpoint, Config(argument, "TANSR_SNAPSHOT_CONFIGURATION"), ct).ConfigureAwait(false);
                    return "SDK1 上下文镜像配置已打开，默认关闭。启用才落盘；SDK2 source-required 应使用档案入口。";
                case 12: return await Snapshot().EnableAsync(ct).ConfigureAwait(false);
                case 13: return await Snapshot().DisableAsync(ct).ConfigureAwait(false);
                case 14: return await Snapshot().ReadAsync(ct).ConfigureAwait(false);
                case 15:
                    if (archive != null) throw new InvalidOperationException("archive_host_already_connected");
                    archive = await NativeArchiveHost.StartAsync(Config(argument, "TANSR_ARCHIVE_CONFIGURATION"), session.Id, endpoint, ct, client).ConfigureAwait(false);
                    return await archive.ReadStatusAsync(ct).ConfigureAwait(false);
                case 16: return await Archive().SynchronizeAsync(ct).ConfigureAwait(false);
                case 17: return await Archive().ReadRecordsAsync(ct).ConfigureAwait(false);
                case 18:
                    if (archive != null) { await archive.StopAsync().ConfigureAwait(false); archive = null; }
                    return "已停止本机档案事件通道；未删除档案，未关闭远端会话。";
                case 19:
                    if (archive == null) return "未连接本机档案。";
                    if (archive.Completion.IsCompleted) await archive.Completion.ConfigureAwait(false);
                    return await archive.ReadStatusAsync(ct).ConfigureAwait(false);
                case 20: return await Snapshot().RetryCheckpointCleanupAsync(ct).ConfigureAwait(false);
                case 21:
                    if (cache != null) throw new InvalidOperationException("cache_continuity_already_configured");
                    cache = await NativeCacheContinuityHost.StartAsync(Config(argument, "TANSR_CACHE_CONFIGURATION"), session.Id, endpoint, client, ct).ConfigureAwait(false);
                    return await cache.ReadStatusAsync(ct).ConfigureAwait(false);
                case 22: return await Cache().NewAsync(ct).ConfigureAwait(false);
                case 23: return await Cache().ResumeAsync(ct).ConfigureAwait(false);
                case 24: return await Cache().QueryAsync(ct).ConfigureAwait(false);
                case 25: return await Cache().ReadDiagnosticsAsync(ct).ConfigureAwait(false);
                case 26: return await Cache().CloseAsync(ct).ConfigureAwait(false);
                case 27: return await Cache().ReadStatusAsync(ct).ConfigureAwait(false);
                default: throw new InvalidOperationException("select_storage_action");
            }
        }
        finally { gate.Release(); }
    }
    internal async Task ObserveAsync(AgentEvent item, CancellationToken ct)
    {
        var type = item.Data.TryGetProperty("type", out var value) ? value.GetString() : item.Name;
        if (type == "turn.completed" || type == "turn.aborted") await FlushAsync(ct).ConfigureAwait(false);
    }
    internal async Task FlushAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!closed && snapshot?.IsEnabled == true) await snapshot.FlushAsync(ct).ConfigureAwait(false);
            if (!closed && PendingCleanupStatus.Length > 0) throw new InvalidOperationException(PendingCleanupStatus);
        }
        finally { gate.Release(); }
    }
    internal async Task CloseAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) return;
            closed = true;
            // 保留镜像；只有用户明确选择 Disable 才删除镜像内容。
            try { if (archive != null) await archive.StopAsync().ConfigureAwait(false); }
            finally
            {
                try { if (cache != null) await cache.StopAsync().ConfigureAwait(false); }
                finally { snapshot?.Dispose(); snapshot = null; archive = null; cache = null; }
            }
        }
        finally { gate.Release(); }
    }
    private NativeSnapshotHost Snapshot() => snapshot ?? throw new InvalidOperationException("open_snapshot_configuration_first");
    private NativeArchiveHost Archive() => archive ?? throw new InvalidOperationException("connect_archive_first");
    private NativeCacheContinuityHost Cache() => cache ?? throw new InvalidOperationException("configure_cache_continuity_first");
    private static string Config(string argument, string environment) => !string.IsNullOrWhiteSpace(argument) ? argument.Trim() :
        Environment.GetEnvironmentVariable(environment) is { Length: > 0 } path ? path : throw new InvalidOperationException("missing_" + environment);
}
