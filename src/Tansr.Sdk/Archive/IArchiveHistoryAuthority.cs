using System.Text.Json;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Archive;

/// <summary>可信宿主固定的当前档案来源；不得由待查看旧备份或远端请求自行提供。</summary>
public interface IArchiveHistoryAuthority
{
    Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default);
    Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default);
    Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default);
}

public static class ArchiveHistoryAuthority
{
    /// <summary>仅借用当前来源的只读授权面，历史视图关闭时不关闭当前来源。</summary>
    public static IArchiveHistoryAuthority FromStore(IArchiveRetentionStore store)
        => new StoreAuthority(store ?? throw new ArgumentNullException(nameof(store)));

    private sealed class StoreAuthority : IArchiveHistoryAuthority
    {
        private readonly IArchiveRetentionStore _store;
        internal StoreAuthority(IArchiveRetentionStore store) => _store = store;
        public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) => _store.HeadAsync(cancellationToken);
        public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default) => _store.ReadRecordsAsync(request, cancellationToken);
        public Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default) => _store.RetentionRevisionAsync(cancellationToken);
    }
}
