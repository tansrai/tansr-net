using System.Text.Json;

namespace Tansr.Sdk.Archive;

/// <summary>原 archive-history-view-v1 只读结果；不是当前材料来源或 ACK。</summary>
public sealed class ArchiveHistoryPage
{
    public string Format => "archive-history-view-v1";
    public JsonElement? VersionHead { get; }
    public JsonElement? CurrentHead { get; }
    public string RetentionRevision { get; }
    public IReadOnlyList<JsonElement> Records { get; }
    public int Bytes { get; }
    public IReadOnlyList<string> MissingRecordIds { get; }
    public string? NextFromSequence { get; }

    internal ArchiveHistoryPage(JsonElement? versionHead, JsonElement? currentHead, string retentionRevision,
        IReadOnlyList<JsonElement> records, int bytes, IReadOnlyList<string> missingRecordIds, string? nextFromSequence)
    {
        VersionHead = versionHead?.Clone(); CurrentHead = currentHead?.Clone(); RetentionRevision = retentionRevision;
        Records = Array.AsReadOnly(records.Select(record => record.Clone()).ToArray()); Bytes = bytes;
        MissingRecordIds = Array.AsReadOnly(missingRecordIds.ToArray()); NextFromSequence = nextFromSequence;
    }
}
