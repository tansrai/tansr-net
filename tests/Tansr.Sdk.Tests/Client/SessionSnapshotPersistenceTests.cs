using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Tests.Client;

public sealed class SessionSnapshotPersistenceTests
{
    [Fact]
    public async Task DefaultsOffAndTogglesKeepOriginalSessionAndExactSnapshotBytes()
    {
        using var f = await Fixture.CreateAsync(); using var mirror = await f.MirrorAsync();
        Assert.False(mirror.IsEnabled); Assert.Equal(0, f.Store.Reads); Assert.Equal(0, f.Exports);
        await mirror.FlushAsync(); Assert.Equal(0, f.Exports);
        await mirror.EnableAsync(); Assert.True(mirror.IsEnabled); Assert.Same(f.Session, mirror.Session);
        Assert.Equal(f.Body, (await mirror.ReadAsync())!.Bytes); Assert.Equal(1, f.Exports);
        f.Body = Encoding.UTF8.GetBytes("complete export after compaction 中文 image=unchanged");
        await mirror.FlushAsync(); Assert.Equal(f.Body, (await mirror.ReadAsync())!.Bytes);
        await mirror.DisableAsync(); Assert.False(mirror.IsEnabled); Assert.Null(await mirror.ReadAsync());
        await mirror.FlushAsync(); Assert.Equal(2, f.Exports); Assert.Equal(1, f.SessionCreates); Assert.Equal(0, f.Imports);
        Assert.Equal(2, f.CheckpointDeletes); Assert.Null(mirror.PendingCheckpointId);
    }

    [Fact]
    public async Task DisableDuringLateExportPreventsResurrectionWithoutRebuildingSession()
    {
        using var f = await Fixture.CreateAsync(); using var mirror = await f.MirrorAsync(); await mirror.EnableAsync();
        f.HoldExport = true; var flush = mirror.FlushAsync(); await f.ExportEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disable = mirror.DisableAsync(); Assert.False(mirror.IsEnabled); f.ExportRelease.SetResult(true);
        await Task.WhenAll(flush, disable); Assert.Null(await mirror.ReadAsync());
        Assert.Equal(1, f.Store.Writes); Assert.Equal(1, f.SessionCreates);
    }

    [Fact]
    public async Task LateStoreIgnoringCancellationIsRemovedBeforeDisableCompletes()
    {
        using var f = await Fixture.CreateAsync(); using var mirror = await f.MirrorAsync(); await mirror.EnableAsync();
        f.Store.HoldWrite = true; var flush = mirror.FlushAsync(); await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabled = mirror.DisableAsync(); Assert.False(disabled.IsCompleted);
        f.Store.Release.SetResult(true); await Task.WhenAll(flush, disabled);
        Assert.Null(await mirror.ReadAsync()); Assert.Equal(2, f.Store.Writes);
        Assert.Equal(2, f.CheckpointDeletes); Assert.Null(mirror.PendingCheckpointId);
    }

    [Fact]
    public async Task DisableFailureStaysDisabledAndFlushDoesNotRewriteDisk()
    {
        using var f = await Fixture.CreateAsync(); using var mirror = await f.MirrorAsync(); await mirror.EnableAsync();
        f.Store.FailDelete = true;
        await Assert.ThrowsAsync<StorageException>(() => mirror.DisableAsync()); Assert.False(mirror.IsEnabled);
        await mirror.FlushAsync(); Assert.Equal(1, f.Exports); Assert.NotNull(await mirror.ReadAsync());
    }

    [Fact]
    public async Task ExportFailureLeavesExistingCopyAndFailedEnableIsOff()
    {
        using var f = await Fixture.CreateAsync(); using var mirror = await f.MirrorAsync(); f.FailExport = true;
        await Assert.ThrowsAsync<TansrHttpException>(() => mirror.EnableAsync());
        Assert.False(mirror.IsEnabled); Assert.Equal(0, f.Store.Writes); Assert.Equal(0, f.Imports);
    }

    [Fact]
    public async Task ChangedTrustedScopeAndSourceRequiredAreRejectedBeforeExportOrStorage()
    {
        using var f = await Fixture.CreateAsync(); f.SourceRequired = true;
        await Assert.ThrowsAsync<NotSupportedException>(() => f.MirrorAsync()); Assert.Equal(0, f.Store.Reads); Assert.Equal(0, f.Exports);
        f.SourceRequired = false; using var mirror = await f.MirrorAsync(); f.Current = new("https://serve.test", "app", "other-user", "s");
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => mirror.EnableAsync())).Code);
        Assert.Equal(0, f.Store.Reads); Assert.Equal(0, f.Exports);
    }

    [Fact]
    public async Task UnknownCleanupPreservesDurableCopyAndBlocksMoreCheckpointsUntilExplicitRetry()
    {
        using var f = await Fixture.CreateAsync(); using var mirror = await f.MirrorAsync(); f.FailCheckpointDelete = true;
        await mirror.EnableAsync(); Assert.True(mirror.IsEnabled); Assert.Equal(f.Body, (await mirror.ReadAsync())!.Bytes);
        Assert.Equal("checkpoint", mirror.PendingCheckpointId); Assert.Equal("network_error", mirror.LastCheckpointCleanupErrorCode);
        Assert.Equal("snapshot_checkpoint_cleanup_pending", (await Assert.ThrowsAsync<TansrProtocolException>(() => mirror.FlushAsync())).Code);
        Assert.Equal(1, f.Exports); Assert.Equal(1, f.CheckpointDeletes);
        f.FailCheckpointDelete = false; await mirror.RetryCheckpointCleanupAsync(); Assert.Null(mirror.PendingCheckpointId); Assert.Null(mirror.LastCheckpointCleanupErrorCode);
        Assert.Equal(2, f.CheckpointDeletes); Assert.Equal(1, f.Exports);
        await mirror.FlushAsync(); Assert.Equal(2, f.Exports); Assert.Equal(3, f.CheckpointDeletes);
    }

    [Fact]
    public async Task CleanupFailureDoesNotMaskTheOriginalLocalWriteFailureOrClaimDurability()
    {
        using var f = await Fixture.CreateAsync(); using var mirror = await f.MirrorAsync(); f.Store.FailWrite = true; f.FailCheckpointDelete = true;
        Assert.Equal("storage_full", (await Assert.ThrowsAsync<StorageException>(() => mirror.EnableAsync())).Code);
        Assert.False(mirror.IsEnabled); Assert.Null(await mirror.ReadAsync()); Assert.Equal("checkpoint", mirror.PendingCheckpointId);
        Assert.Equal("network_error", mirror.LastCheckpointCleanupErrorCode); Assert.Equal(1, f.CheckpointDeletes);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private HttpClient _http = null!; private TansrClient _client = null!;
        internal AgentSession Session = null!;
        internal SessionSnapshotScope Current = new("https://serve.test", "app", "user", "s");
        internal MemoryStore Store = null!;
        internal int SessionCreates, Exports, Imports, CheckpointDeletes;
        internal bool HoldExport, FailExport, SourceRequired, FailCheckpointDelete;
        internal byte[] Body = Encoding.UTF8.GetBytes("{\"snapshot\":\"whole Unicode 中文\",\"image\":\"AQID\",\"usage\":123,\"additional\":null}");
        internal TaskCompletionSource<bool> ExportEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ExportRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); f._http = new(f, false);
            f._client = new(new TansrClientOptions { BaseUri = new("https://serve.test"), TokenProvider = _ => Task.FromResult("synthetic") }, f._http);
            f.Session = await f._client.CreateSessionAsync(new()); f.Store = new(f.Current); return f;
        }
        internal Task<SessionSnapshotPersistence> MirrorAsync() => SessionSnapshotPersistence.CreateAsync(Session, Store, () => Current);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (path == "/v2/sessions") { SessionCreates++; return Json("{\"sessionId\":\"s\",\"lastSeq\":0,\"resumed\":false}"); }
            if (path == "/v2/sessions/s") return Json("{\"sessionId\":\"s\",\"lastSeq\":0" + (SourceRequired ? ",\"availability\":\"source-required\",\"contract\":\"sdk2-offload-v1\"" : "") + "}");
            if (request.Method == HttpMethod.Delete && path == "/v2/sessions/s/checkpoints/checkpoint")
            {
                CheckpointDeletes++; if (FailCheckpointDelete) throw new HttpRequestException("synthetic lost cleanup response");
                return new(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/checkpoints", StringComparison.Ordinal)) return Json("{\"sessionId\":\"s\",\"checkpointId\":\"checkpoint\"}");
            if (path.EndsWith("/export", StringComparison.Ordinal))
            {
                Exports++; if (HoldExport) { ExportEntered.SetResult(true); await ExportRelease.Task; }
                if (FailExport) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"code\":\"storage_error\",\"message\":\"synthetic\"}}", Encoding.UTF8, "application/json") };
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) }; response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); return response;
            }
            Imports++; throw new InvalidOperationException("Unexpected snapshot mutation");
        }
        protected override void Dispose(bool disposing) { if (disposing) { _client?.Dispose(); _http?.Dispose(); } base.Dispose(disposing); }
    }
    private sealed class MemoryStore(SessionSnapshotScope scope) : ISessionSnapshotStore
    {
        private SessionSnapshotCopy? _copy; private long _revision;
        public SessionSnapshotScope Scope => scope;
        internal int Reads, Writes; internal bool HoldWrite, FailDelete, FailWrite;
        internal TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SessionSnapshotStoreState> ReadAsync(CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(new SessionSnapshotStoreState(_revision, _copy)); }
        public async Task<long> WriteAsync(long expectedRevision, SessionSnapshotCopy snapshot, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new StorageException("storage_full");
            if (HoldWrite) { Entered.SetResult(true); await Release.Task; }
            if (_revision != expectedRevision) throw new StorageException("revision_conflict");
            _copy = snapshot; Writes++; return ++_revision;
        }
        public Task<long> DeleteAsync(long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (FailDelete) throw new StorageException("storage_error");
            if (_revision != expectedRevision) throw new StorageException("revision_conflict");
            _copy = null; return Task.FromResult(++_revision);
        }
    }
}
