using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using A = Tansr.Sdk.Archive.Replication.ArchiveReceiverValidation;

namespace Tansr.Sdk.Archive.Replication;

internal static class ArchiveSyncValidation
{
    internal const int MaximumMetadataBytes = 1572864;
    internal static JsonElement Copy(JsonElement value, int maximum = MaximumMetadataBytes) => A.Copy(value, maximum: maximum);
    internal static bool Equal(JsonElement a, JsonElement b) => WireJson.CanonicalString(a, MaximumMetadataBytes) == WireJson.CanonicalString(b, MaximumMetadataBytes);
    internal static void Fields(JsonElement value, params string[] fields) => A.Fields(value, fields);
    internal static JsonElement Limits(JsonElement input)
    {
        var value = Copy(input, 1024); Fields(value, "maxRecords", "maxArtifacts", "maxStoredBytes", "maxBatchBytes");
        foreach (string name in new[] { "maxRecords", "maxArtifacts" }) A.Need(value.GetProperty(name).TryGetInt32(out int count) && count >= 1 && count <= 1000000, "invalid_input");
        A.Need(value.GetProperty("maxStoredBytes").TryGetInt64(out long bytes) && bytes >= 1 && bytes <= 1073741824 && value.GetProperty("maxBatchBytes").TryGetInt64(out long batch) && batch >= 1 && batch <= 67108864, "invalid_input"); return value;
    }
    internal static JsonElement Replica(JsonElement input)
    {
        var value = Copy(input, 2048); Fields(value, "replicationId", "role"); WireJson.ValidateNamed("Id", value.GetProperty("replicationId"));
        A.Need(A.String(value, "role") == "primary" || A.String(value, "role") == "replica", "invalid_input"); return value;
    }
    internal static JsonElement ReplicaInfo(JsonElement input)
    {
        var value = Copy(input, 16384); Fields(value, "replica", "receiver", "limits"); Replica(value.GetProperty("replica")); A.Identity(value.GetProperty("receiver")); Limits(value.GetProperty("limits")); return value;
    }
    internal static JsonElement Checkpoint(JsonElement input)
    {
        var value = Copy(input); Fields(value, "binding", "status", "page", "request");
        WireJson.ValidateNamed("BindingView", value.GetProperty("binding")); WireJson.ValidateNamed("ArchiveStatus", value.GetProperty("status")); WireJson.ValidateNamed("ArchivePage", value.GetProperty("page")); WireJson.ValidateNamed("RequestIdentity", value.GetProperty("request")); return value;
    }
    internal static JsonElement Tombstones(JsonElement input)
    {
        var value = Copy(input, 65536); A.Need(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 128, "invalid_input");
        var ids = new HashSet<string>(StringComparer.Ordinal); var sequences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in value.EnumerateArray())
        {
            Fields(row, "recordId", "sequence", "recordDigest"); WireJson.ValidateNamed("Id", row.GetProperty("recordId")); WireJson.ValidateNamed("RecordSequence", row.GetProperty("sequence")); WireJson.ValidateNamed("Digest", row.GetProperty("recordDigest"));
            A.Need(ids.Add(A.String(row, "recordId")) && sequences.Add(A.String(row, "sequence")), "invalid_input");
        }
        return value;
    }
    internal static JsonElement Page(JsonElement input, JsonElement identity)
    {
        var value = Copy(input); Fields(value, "format", "identity", "retentionRevision", "checkpoint", "ack", "receipt", "tombstones");
        A.Need(A.String(value, "format") == "archive-sync-v1" && Equal(A.Identity(value.GetProperty("identity")), identity), "identity_mismatch");
        WireJson.ValidateNamed("Sequence", value.GetProperty("retentionRevision")); var checkpoint = Checkpoint(value.GetProperty("checkpoint"));
        WireJson.ValidateNamed("ArchiveAckRequest", value.GetProperty("ack")); WireJson.ValidateNamed("MutationReceipt", value.GetProperty("receipt"));
        var records = checkpoint.GetProperty("page").GetProperty("records").EnumerateArray().ToArray();
        foreach (var row in Tombstones(value.GetProperty("tombstones")).EnumerateArray())
            A.Need(records.Any(record => A.String(record, "recordId") == A.String(row, "recordId") && A.String(record, "recordDigest") == A.String(row, "recordDigest") && A.String(record, "sequence") == A.String(row, "sequence")), "integrity_mismatch");
        return value;
    }
    internal static JsonElement Retention(JsonElement input, JsonElement identity) => A.Retention(identity, input);
    internal static ArchiveReceiveInput Input(ArchiveReceiveInput input, JsonElement limits)
    {
        if (input == null || input.Artifacts == null) throw new StorageException("invalid_input");
        var checkpoint = Checkpoint(A.Object(w => { A.Property(w, "binding", input.Binding); A.Property(w, "status", input.Status); A.Property(w, "page", input.Page); A.Property(w, "request", input.Request); }));
        A.Need(input.Artifacts.Count <= limits.GetProperty("maxArtifacts").GetInt32(), "capacity_exceeded"); var artifacts = new List<ArchiveArtifact>(); long total = 0;
        foreach (var item in input.Artifacts)
        {
            if (item == null || item.Body == null) throw new StorageException("invalid_input");
            WireJson.ValidateNamed("Id", String(item.ArtifactId)); total += item.Body.LongLength; A.Need(total <= limits.GetProperty("maxBatchBytes").GetInt64(), "capacity_exceeded");
            artifacts.Add(new ArchiveArtifact(item.ArtifactId, (byte[])item.Body.Clone()));
        }
        return new ArchiveReceiveInput { Binding = checkpoint.GetProperty("binding"), Status = checkpoint.GetProperty("status"), Page = checkpoint.GetProperty("page"), Request = checkpoint.GetProperty("request"), Artifacts = artifacts.AsReadOnly() };
    }
    internal static JsonElement String(string? value)
    { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) writer.WriteStringValue(value); return WireJson.Parse(stream.ToArray()); }
    internal static JsonElement Null => WireJson.Parse(new byte[] { 110, 117, 108, 108 });
    internal static JsonElement? Head(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null; Fields(value, "sequence", "recordDigest"); WireJson.ValidateNamed("RecordSequence", value.GetProperty("sequence")); WireJson.ValidateNamed("Digest", value.GetProperty("recordDigest")); return Copy(value, 1024);
    }
}
