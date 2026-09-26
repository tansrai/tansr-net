using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Sessions;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;
#endif

namespace Tansr.Examples;

/// <summary>可信配置的 SDK1 上下文镜像；开关不重建会话，不修改 Serve 留存策略。</summary>
internal sealed class NativeSnapshotHost : IDisposable
{
#if WINDOWS || NETFRAMEWORK
    private readonly SessionSnapshotPersistence _persistence;
    private readonly WindowsSessionSnapshotStore _store;
    private readonly string _directory, _keyPath, _keyId, _mirrorPath;
    private NativeSnapshotHost(SessionSnapshotPersistence persistence, WindowsSessionSnapshotStore store, string directory, string keyPath, string keyId, string mirrorPath)
    { _persistence = persistence; _store = store; _directory = directory; _keyPath = keyPath; _keyId = keyId; _mirrorPath = mirrorPath; }
    internal bool IsEnabled => _persistence.IsEnabled;
    internal string PendingCleanupStatus => CleanupStatus();
#else
    private NativeSnapshotHost() { }
    internal bool IsEnabled => false;
    internal string PendingCleanupStatus => string.Empty;
#endif
    internal static async Task<NativeSnapshotHost> OpenAsync(AgentSession session, Uri serveUri, string configPath, CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var bytes = await BoundedFiles.ReadAsync(configPath, 16384);
        using var document = JsonDocument.Parse(bytes); var config = document.RootElement;
        var authority = TrustedExampleScope.FromPath(config.GetProperty("scopeFile").GetString()!);
        var directory = Path.GetFullPath(config.GetProperty("directory").GetString()!);
        var keyId = config.TryGetProperty("keyId", out var configuredKey) ? configuredKey.GetString()! : "sdk1-context-mirror";
        SessionSnapshotScope ReadScope()
        {
            var scope = authority.ReadScope();
            return new SessionSnapshotScope(serveUri.AbsoluteUri, scope.GetProperty("applicationScopeId").GetString()!, scope.GetProperty("endUserId").GetString()!, session.Id);
        }
        var identity = ReadScope();
        string name;
        using (var hash = SHA256.Create())
        {
            using var data = new MemoryStream();
            using (var writer = new Utf8JsonWriter(data)) { writer.WriteStartArray(); writer.WriteStringValue(identity.ServeAuthority); writer.WriteStringValue(identity.ApplicationScopeId); writer.WriteStringValue(identity.EndUserId); writer.WriteStringValue(identity.SessionId); writer.WriteEndArray(); }
            name = BitConverter.ToString(hash.ComputeHash(data.ToArray())).Replace("-", "").ToLowerInvariant();
        }
        var keyPath = Path.Combine(directory, name + ".key");
        var mirrorPath = Path.Combine(directory, name + ".sqlite");
        var store = new WindowsSessionSnapshotStore(new WindowsSessionSnapshotStoreOptions
        {
            Path = mirrorPath, Scope = identity, ReadScope = ReadScope,
            KeyProvider = () => CurrentUserDpapiArchiveKeyProvider.Open(keyPath, keyId),
        });
        try { return new NativeSnapshotHost(await SessionSnapshotPersistence.CreateAsync(session, store, ReadScope, ct), store, directory, keyPath, keyId, mirrorPath); }
        catch { store.Dispose(); throw; }
#else
        await Task.CompletedTask; throw new PlatformNotSupportedException("native_snapshot_mirror_requires_windows");
#endif
    }
    internal async Task<string> EnableAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(_directory);
        if (!File.Exists(_keyPath))
        {
            if (File.Exists(_mirrorPath)) throw new IOException("snapshot_key_missing_existing_mirror_preserved");
            _ = CurrentUserDpapiArchiveKeyProvider.Create(_keyPath, _keyId);
        }
        await _persistence.EnableAsync(ct); return "SDK1 上下文镜像已开启；原会话保持，原 export 全字段加密保存。" + CleanupStatus();
#else
        await Task.CompletedTask; throw new PlatformNotSupportedException();
#endif
    }
    internal async Task<string> DisableAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        if (!IsEnabled && !File.Exists(_mirrorPath)) return "本地上下文镜像保持关闭；没有创建过镜像。";
        await _persistence.DisableAsync(ct); return "本地上下文镜像已关闭并删除内容；Serve 留存和原会话不变。" + CleanupStatus();
#else
        await Task.CompletedTask; throw new PlatformNotSupportedException();
#endif
    }
    internal async Task<string> FlushAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        if (_persistence.PendingCheckpointId != null) return "未执行新的上下文捕获。" + CleanupStatus();
        await _persistence.FlushAsync(ct); return (IsEnabled ? "上下文镜像已耐久保存。" : "镜像关闭；未写入本地快照。") + CleanupStatus();
#else
        await Task.CompletedTask; throw new PlatformNotSupportedException();
#endif
    }
    internal async Task<string> ReadAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var copy = await _persistence.ReadAsync(ct);
        return (copy == null ? "没有本地上下文镜像。" : "本地只读上下文镜像：checkpoint=" + copy.CheckpointId + "，bytes=" + copy.Bytes.Length + "。未导入、未替换当前上下文。") + CleanupStatus();
#else
        await Task.CompletedTask; throw new PlatformNotSupportedException();
#endif
    }
    internal async Task<string> RetryCheckpointCleanupAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        await _persistence.RetryCheckpointCleanupAsync(ct);
        return _persistence.PendingCheckpointId == null ? "本镜像的临时 checkpoint 清理已完成；未创建新快照。" : CleanupStatus();
#else
        await Task.CompletedTask; throw new PlatformNotSupportedException();
#endif
    }
    internal async Task CloseAsync(CancellationToken ct = default)
    {
        try { if (IsEnabled) await FlushAsync(ct); } finally { Dispose(); }
    }
    public void Dispose()
    {
#if WINDOWS || NETFRAMEWORK
        _persistence.Dispose(); _store.Dispose();
#endif
    }
#if WINDOWS || NETFRAMEWORK
    private string CleanupStatus() => _persistence.PendingCheckpointId == null ? string.Empty :
        " 临时 checkpoint 清理待办：" + _persistence.PendingCheckpointId + " (" + _persistence.LastCheckpointCleanupErrorCode + ")；暂停新捕获，需显式重试清理。退出前请保留此编号，退出不会自动清理。";
#endif
}
