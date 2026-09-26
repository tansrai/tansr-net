using Tansr.Sdk.Archive;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>已有 SDK2 文件的只读历史工厂。始终 reopen；旧库不成为当前材料来源、ACK 接收器或删除授权。</summary>
public static class SqliteArchiveHistory
{
    /// <summary>仅返回受当前来源复验的历史视图。options.Mode 不用于创建文件，当前来源由可信宿主固定。</summary>
    public static async Task<ArchiveHistoryView> OpenAsync(SqliteArchiveStoreOptions options,
        IArchiveHistoryAuthority current, CancellationToken cancellationToken = default)
    {
        if (options == null || current == null) throw new StorageException("invalid_input");
        var version = await SqliteArchiveStore.OpenHistoryAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); return version.CreateHistoryView(current);
        }
        catch { await version.CloseAsync().ConfigureAwait(false); throw; }
    }
}
