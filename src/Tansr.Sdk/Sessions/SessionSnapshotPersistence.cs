using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Sessions;

/// <summary>可信宿主提供的快照镜像分域；不是从 JWT 文本推断的授权，也不替代 Serve 鉴权。</summary>
public sealed class SessionSnapshotScope : IEquatable<SessionSnapshotScope>
{
    public SessionSnapshotScope(string serveAuthority, string applicationScopeId, string endUserId, string sessionId)
    {
        if (serveAuthority == null || serveAuthority.Length > 2048 || !Uri.TryCreate(serveAuthority, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("snapshot_invalid_authority", nameof(serveAuthority));
        ServeAuthority = uri.AbsoluteUri.TrimEnd('/');
        ApplicationScopeId = Validate(applicationScopeId); EndUserId = Validate(endUserId); SessionId = Validate(sessionId);
    }
    public string ServeAuthority { get; }
    public string ApplicationScopeId { get; }
    public string EndUserId { get; }
    public string SessionId { get; }
    public bool Equals(SessionSnapshotScope? other) => other != null && ServeAuthority == other.ServeAuthority && ApplicationScopeId == other.ApplicationScopeId && EndUserId == other.EndUserId && SessionId == other.SessionId;
    public override bool Equals(object? obj) => obj is SessionSnapshotScope other && Equals(other);
    public override int GetHashCode() => ServeAuthority.GetHashCode() ^ ApplicationScopeId.GetHashCode() ^ EndUserId.GetHashCode() ^ SessionId.GetHashCode();
    private static string Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) throw new ArgumentException("snapshot_invalid_scope");
        try { _ = new UTF8Encoding(false, true).GetBytes(value); } catch (EncoderFallbackException) { throw new ArgumentException("snapshot_invalid_scope"); }
        return value;
    }
}

/// <summary>Serve 原 export 的完整上下文快照字节，含其全部字段/图片；不表示完整长档案或记忆。</summary>
public sealed class SessionSnapshotCopy
{
    private readonly byte[] _bytes;
    public SessionSnapshotCopy(string checkpointId, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(checkpointId) || checkpointId.Length > 512 || checkpointId.Any(char.IsControl)) throw new ArgumentException("snapshot_invalid_checkpoint", nameof(checkpointId));
        if (bytes == null || bytes.Length == 0 || bytes.Length > 32 * 1024 * 1024) throw new ArgumentException("snapshot_invalid_bytes", nameof(bytes));
        CheckpointId = checkpointId; _bytes = (byte[])bytes.Clone();
    }
    public string CheckpointId { get; }
    public byte[] Bytes => (byte[])_bytes.Clone();
}

public sealed class SessionSnapshotStoreState
{
    public SessionSnapshotStoreState(long revision, SessionSnapshotCopy? snapshot)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision; Snapshot = snapshot;
    }
    public long Revision { get; }
    public SessionSnapshotCopy? Snapshot { get; }
}

/// <summary>宿主可注入第三方镜像介质。写/删除必须原子 CAS；删除也递增 revision，使旧写入失效。</summary>
public interface ISessionSnapshotStore
{
    SessionSnapshotScope Scope { get; }
    Task<SessionSnapshotStoreState> ReadAsync(CancellationToken cancellationToken = default);
    Task<long> WriteAsync(long expectedRevision, SessionSnapshotCopy snapshot, CancellationToken cancellationToken = default);
    Task<long> DeleteAsync(long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>SDK1 上下文快照本地镜像开关；默认关闭，不改变会话、Serve 留存或 SDK2 长档案策略。</summary>
/// <remarks>宿主在原轮完成后调用 FlushAsync。关闭会阻止晚到 export 写回，并等待原写入结束后删除镜像。
/// 不自动导入本地副本，不触发会话重建；存储由宿主拥有。Dispose 仅停止本实例写入，不替代显式 DisableAsync 删除。</remarks>
public sealed class SessionSnapshotPersistence : IDisposable
{
    private readonly ISessionSnapshotStore _store;
    private readonly Func<SessionSnapshotScope> _readScope;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _write;
    private long _generation, _revision;
    private bool _enabled, _disposed;
    private string? _pendingCheckpointId, _checkpointCleanupError;
    private SessionSnapshotPersistence(AgentSession session, ISessionSnapshotStore store, Func<SessionSnapshotScope> readScope)
    { Session = session; _store = store; _readScope = readScope; }
    public AgentSession Session { get; }
    public bool IsEnabled { get { lock (_gate) return _enabled; } }
    /// <summary>Only a checkpoint created by this mirror. A failed cleanup blocks new captures until an explicit retry succeeds.</summary>
    public string? PendingCheckpointId { get { lock (_gate) return _pendingCheckpointId; } }
    public string? LastCheckpointCleanupErrorCode { get { lock (_gate) return _checkpointCleanupError; } }

    public static async Task<SessionSnapshotPersistence> CreateAsync(AgentSession session, ISessionSnapshotStore store,
        Func<SessionSnapshotScope> readScope, CancellationToken cancellationToken = default)
    {
        if (session == null || store == null || readScope == null) throw new ArgumentNullException(session == null ? nameof(session) : store == null ? nameof(store) : nameof(readScope));
        if (store.Scope.SessionId != session.Id || !store.Scope.Equals(readScope())) throw new StorageException("identity_mismatch");
        var meta = await session.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (meta.TryGetProperty("availability", out var availability) && availability.GetString() == "source-required" ||
            meta.TryGetProperty("contract", out var contract) && contract.GetString() == "sdk2-offload-v1")
            throw new NotSupportedException("snapshot_mirror_cannot_replace_required_source");
        if (!store.Scope.Equals(readScope())) throw new StorageException("context_changed");
        return new SessionSnapshotPersistence(session, store, readScope);
    }

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        long generation = -1;
        try
        {
            CheckContext();
            lock (_gate) { if (_enabled) return; generation = ++_generation; _enabled = true; }
            var current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false); _revision = current.Revision;
            await CaptureAsync(generation, cancellationToken).ConfigureAwait(false);
        }
        catch { lock (_gate) { if (_generation == generation) _enabled = false; } throw; }
        finally { _serial.Release(); }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(SessionSnapshotPersistence)); if (!_enabled) return; generation = _generation; }
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CaptureAsync(generation, cancellationToken).ConfigureAwait(false); }
        finally { _serial.Release(); }
    }

    private async Task CaptureAsync(long generation, CancellationToken cancellationToken)
    {
        CheckContext();
        CancellationTokenSource request;
        lock (_gate)
        {
            if (!_enabled || generation != _generation) return;
            if (_pendingCheckpointId != null) throw new TansrProtocolException("snapshot_checkpoint_cleanup_pending");
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token); _write = request;
        }
        using (request)
        {
            try
            {
                var exported = await Session.ExportAsync("SDK1 local context mirror", request.Token).ConfigureAwait(false);
                lock (_gate) _pendingCheckpointId = exported.CheckpointId;
                CheckContext();
                lock (_gate) { if (!_enabled || generation != _generation) return; }
                request.Token.ThrowIfCancellationRequested();
                _revision = await _store.WriteAsync(_revision, new SessionSnapshotCopy(exported.CheckpointId, exported.Bytes), request.Token).ConfigureAwait(false);
                CheckContext();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !IsGenerationActive(generation)) { }
            finally
            {
                // The original export is complete. Its private temporary checkpoint no longer needs
                // server retention, even if cancellation prevents saving the local copy. Cleanup has
                // its own bounded token and never hides the original export/storage exception.
                await CleanupCheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_gate) { if (ReferenceEquals(_write, request)) _write = null; }
            }
        }
    }

    /// <summary>Explicit retry of one retained cleanup identity. No checkpoint is created or exported.</summary>
    public async Task RetryCheckpointCleanupAsync(CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CheckContext(); await CleanupCheckpointAsync(cancellationToken).ConfigureAwait(false); }
        finally { _serial.Release(); }
    }

    private async Task CleanupCheckpointAsync(CancellationToken cancellationToken)
    {
        string? checkpoint;
        lock (_gate) checkpoint = _pendingCheckpointId;
        if (checkpoint == null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            CheckContext();
            await Session.DeleteCheckpointAsync(checkpoint, deadline.Token).ConfigureAwait(false);
            CheckContext();
            lock (_gate) { _pendingCheckpointId = null; _checkpointCleanupError = null; }
        }
        catch (Exception error)
        {
            lock (_gate) _checkpointCleanupError = error is TansrException tansr ? tansr.Code :
                error is StorageException storage ? storage.Code :
                error is OperationCanceledException ? "checkpoint_cleanup_cancelled" : "checkpoint_cleanup_failed";
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SessionSnapshotPersistence));
            _enabled = false; generation = ++_generation; _write?.Cancel();
        }
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckContext();
            lock (_gate) { if (generation != _generation) return; }
            var current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            _revision = await _store.DeleteAsync(current.Revision, cancellationToken).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    public async Task<SessionSnapshotCopy?> ReadAsync(CancellationToken cancellationToken = default)
    {
        CheckContext(); var state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false); CheckContext(); return state.Snapshot;
    }
    private bool IsGenerationActive(long generation) { lock (_gate) return _enabled && !_disposed && _generation == generation; }
    private void CheckContext()
    {
        lock (_gate) if (_disposed) throw new ObjectDisposedException(nameof(SessionSnapshotPersistence));
        if (!_store.Scope.Equals(_readScope())) throw new StorageException("context_changed");
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _enabled = false; ++_generation; _lifetime.Cancel(); _write?.Cancel(); }
    }
}
