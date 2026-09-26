using System.Globalization;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>沿原 receiver-validation/http-semantics 验证不可变记录和 ACK，不赋予服务端权限。</summary>
internal static class ArchiveValidation
{
    internal const string ZeroDigest = "0000000000000000000000000000000000000000000000000000000000000000";
    internal static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);
    internal static string Text(JsonElement value, int maximum = 1048576) => WireJson.CanonicalString(value, maximum);
    internal static JsonElement Copy(JsonElement value, string? schema = null, int maximum = 1048576)
    {
        var fixedValue = WireJson.DecodeControl(WireJson.EncodeControl(value, maximum), maximum);
        if (schema != null) WireJson.ValidateNamed(schema, fixedValue); return fixedValue;
    }
    internal static JsonElement Parse(string text, string? schema = null, int maximum = 1048576)
    { var value = WireJson.DecodeControl(Encoding.UTF8.GetBytes(text), maximum); if (schema != null) WireJson.ValidateNamed(schema, value); return value; }
    internal static bool Equal(JsonElement a, JsonElement b) => Text(a) == Text(b);
    internal static string String(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    internal static long SequenceOf(JsonElement value, string property) => Sequence.Parse(String(value, property)).ToInt64();
    internal static void Need(bool value, string code = "integrity_mismatch") { if (!value) throw new StorageException(code); }
    internal static JsonElement Object(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return WireJson.Parse(stream.ToArray(), 1572864);
    }
    internal static JsonElement Without(JsonElement value, string property) => Object(w => { foreach (var item in value.EnumerateObject()) if (item.Name != property) item.WriteTo(w); });
    internal static void Fields(JsonElement value, params string[] fields)
    { Need(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(fields.OrderBy(x => x, StringComparer.Ordinal)), "invalid_input"); }
    internal static JsonElement Identity(JsonElement input)
    {
        var value = Copy(input); Fields(value, "scope", "bindingId", "target", "sourceId", "sourceGeneration");
        Fields(value.GetProperty("scope"), "applicationScopeId", "endUserId"); Fields(value.GetProperty("target"), "sessionId", "generations");
        WireJson.ValidateNamed("Scope", Object(w => { foreach (var item in value.GetProperty("scope").EnumerateObject()) item.WriteTo(w); w.WriteString("authorizationRevision", "0"); }));
        WireJson.ValidateNamed("Target", Object(w => { foreach (var item in value.GetProperty("target").EnumerateObject()) item.WriteTo(w); w.WriteString("sourceSnapshotDigest", ZeroDigest); }));
        foreach (var key in new[] { "bindingId", "sourceId", "sourceGeneration" }) WireJson.ValidateNamed("Id", value.GetProperty(key));
        return value;
    }

    internal static void VerifyRecord(JsonElement record, JsonElement identity)
    {
        WireJson.ValidateNamed("ArchiveRecord", record);
        Need(String(record.GetProperty("target"), "sessionId") == String(identity.GetProperty("target"), "sessionId") && Equal(record.GetProperty("target").GetProperty("generations"), identity.GetProperty("target").GetProperty("generations")), "identity_mismatch");
        Need(String(record, "recordDigest") == WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(Without(record, "recordDigest"))));
        if (record.TryGetProperty("sourceEventRange", out var range)) Need(range.GetProperty("firstSeq").GetInt64() <= range.GetProperty("lastSeq").GetInt64());
        if (record.TryGetProperty("projection", out var projection)) Need(SequenceOf(projection.GetProperty("coverage"), "fromSequence") <= SequenceOf(projection.GetProperty("coverage"), "throughSequence"));
        foreach (var reference in References(record)) Need(String(reference, "sourceId") == String(identity, "sourceId"), "identity_mismatch");
    }
    internal static IEnumerable<JsonElement> References(JsonElement record)
    { yield return record.GetProperty("payload"); foreach (var item in record.GetProperty("attachments").EnumerateArray()) yield return item; }

    internal static ArchiveBatch Prepare(JsonElement identity, ArchiveStoreLimits limits, JsonElement? head, ArchiveReceiveInput input, ISet<string>? deletedRecordIds = null)
    {
        var fixedLimits = Object(w => { w.WriteNumber("maxRecords", limits.MaxRecords); w.WriteNumber("maxArtifacts", limits.MaxArtifacts); w.WriteNumber("maxStoredBytes", limits.MaxStoredBytes); w.WriteNumber("maxBatchBytes", limits.MaxBatchBytes); });
        var value = Tansr.Sdk.Archive.Replication.ArchiveReceiverValidation.Prepare(identity, fixedLimits, head, input, deletedRecordIds);
        return new ArchiveBatch(value.Records, value.References, value.Bodies, value.Head, value.Ack, value.Checkpoint);
    }
    internal static JsonElement Receipt(JsonElement identity, JsonElement ack, JsonElement input)
    {
        var value = Copy(input, "MutationReceipt", 4096);
        Need(String(value, "operation") == "archive-ack" && String(value, "state") == "completed" && String(value, "bindingId") == String(identity, "bindingId") && Equal(value.GetProperty("request"), ack.GetProperty("request")) && SequenceOf(value, "revision") > SequenceOf(ack, "expectedRevision"), "receipt_mismatch");
        var semantic = Object(w =>
        {
            w.WriteStartArray("scope"); w.WriteStringValue(String(identity.GetProperty("scope"), "applicationScopeId")); w.WriteStringValue(String(identity.GetProperty("scope"), "endUserId")); w.WriteEndArray();
            w.WriteString("operation", "archive-ack"); Property(w, "semantic", Without(ack, "request"));
        });
        Need(String(value, "semanticDigest") == WireJson.DomainDigest("tansr.sdk2.operation.v1", WireJson.EncodeControl(semantic, 1048576)), "receipt_mismatch"); return value;
    }
    internal static JsonElement Retention(JsonElement identity, JsonElement input)
    {
        var value = Copy(input, maximum: 131072); Fields(value, "format", "identity", "revision", "previousRevision", "requestId", "records");
        Need(String(value, "format") == "archive-retention-v1" && Equal(Identity(value.GetProperty("identity")), identity), "identity_mismatch");
        WireJson.ValidateNamed("Id", value.GetProperty("requestId")); long previous = SequenceOf(value, "previousRevision");
        Need(previous != long.MaxValue && SequenceOf(value, "revision") == previous + 1, "invalid_input");
        var ids = new HashSet<string>(StringComparer.Ordinal); var sequences = new HashSet<string>(StringComparer.Ordinal); var records = value.GetProperty("records");
        Need(records.ValueKind == JsonValueKind.Array && records.GetArrayLength() >= 1 && records.GetArrayLength() <= 128, "invalid_input");
        foreach (var record in records.EnumerateArray())
        {
            Fields(record, "recordId", "sequence", "recordDigest"); WireJson.ValidateNamed("Id", record.GetProperty("recordId")); WireJson.ValidateNamed("RecordSequence", record.GetProperty("sequence")); WireJson.ValidateNamed("Digest", record.GetProperty("recordDigest"));
            Need(ids.Add(String(record, "recordId")) && sequences.Add(String(record, "sequence")), "invalid_input");
        }
        return value;
    }
    internal static void Property(Utf8JsonWriter writer, string name, JsonElement value) { writer.WritePropertyName(name); value.WriteTo(writer); }
    internal static void Array(Utf8JsonWriter writer, string name, IEnumerable<JsonElement> values) { writer.WriteStartArray(name); foreach (var value in values) value.WriteTo(writer); writer.WriteEndArray(); }
    private static void AddReceipt(JsonElement reference, HashSet<string> ids, List<JsonElement> values)
    {
        if (ids.Add(String(reference, "artifactId"))) values.Add(Object(w => { w.WriteString("artifactId", String(reference, "artifactId")); w.WriteString("sha256", String(reference, "sha256")); w.WriteString("state", "durably-stored"); }));
    }
}

internal sealed class ArchiveBatch
{
    internal JsonElement[] Records { get; }
    internal Dictionary<string, JsonElement> References { get; }
    internal Dictionary<string, byte[]> Bodies { get; }
    internal JsonElement Head { get; }
    internal JsonElement Ack { get; }
    internal JsonElement Checkpoint { get; }
    internal ArchiveBatch(JsonElement[] records, Dictionary<string, JsonElement> references, Dictionary<string, byte[]> bodies, JsonElement head, JsonElement ack, JsonElement checkpoint)
    { Records = records; References = references; Bodies = bodies; Head = head; Ack = ack; Checkpoint = checkpoint; }
}
