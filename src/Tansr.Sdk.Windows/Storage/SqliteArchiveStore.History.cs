using System.Text.Json;
using Tansr.Sdk.Protocol;
using A = Tansr.Sdk.Windows.Storage.ArchiveValidation;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteArchiveStore
{
    private long? _historyDataVersion;
    // ArchiveHistoryView 每次读取正文都先选定一条记录；这里只保留该有界元数据，不缓存正文或授予当前来源权威。
    private JsonElement? _historyRecord;

    private void CheckHistoryVersion()
    {
        if (!_historical) return;
        long version = Number("PRAGMA data_version");
        if (!_historyDataVersion.HasValue) _historyDataVersion = version;
        A.Need(_historyDataVersion.Value == version, "identity_mismatch");
    }

    private void CheckHistoryMetadata()
    {
        // 仅查看固定小元数据。完整库存、链、回执和正文审计仍由普通/恢复开库执行。
        var state = State();
        if (state.Head == null) A.Need(state.Records == 0);
        else
        {
            var head = A.Parse(state.Head); A.Fields(head, "sequence", "recordDigest");
            WireJson.ValidateNamed("RecordSequence", head.GetProperty("sequence"));
            WireJson.ValidateNamed("Digest", head.GetProperty("recordDigest"));
            A.Need(A.SequenceOf(head, "sequence") == state.Records);
        }
        A.Need(Sequence.Parse(LocalRevision()).ToInt64() <= Sequence.Parse(_retention()).ToInt64(), "context_changed");
        _known = state;
    }

    private void CheckHistoryRecord(JsonElement record)
    {
        var references = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var reference in A.References(record))
        {
            string id = A.String(reference, "artifactId");
            if (references.TryGetValue(id, out var previous)) A.Need(A.Equal(previous, reference));
            else references.Add(id, reference);
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command("SELECT CASE WHEN length(CAST(artifact_id AS BLOB))<=128 THEN artifact_id END FROM record_artifacts WHERE record_id=$record LIMIT $limit",
            ("$record", A.String(record, "recordId")), ("$limit", references.Count + 1)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                A.Need(!reader.IsDBNull(0)); string id = reader.GetString(0);
                A.Need(references.ContainsKey(id) && found.Add(id));
            }
        }
        A.Need(found.Count == references.Count);
        foreach (var reference in references.Values) A.Need(Artifact(reference, false) != null);
    }

    private void CheckHistoryBodyReference(JsonElement reference)
    {
        // 数据版本固定后，正文只能来自刚经 HistoryView 当前权威逐项核验的那一条记录。
        A.Need(_historyRecord.HasValue && A.References(_historyRecord.Value).Any(item => A.Equal(item, reference)), "identity_mismatch");
    }
}
