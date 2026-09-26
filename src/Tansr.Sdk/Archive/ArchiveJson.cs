using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Archive;

internal static class ArchiveJson
{
    internal const string Protocol = "sdk2-ext-v1";
    internal static void Need(bool condition, string code = "integrity_mismatch") { if (!condition) throw new TansrProtocolException(code); }
    internal static JsonElement Copy(JsonElement input, string? schema = null, int maximum = 1048576)
    {
        var value = WireJson.DecodeControl(WireJson.EncodeControl(input, maximum), maximum);
        if (schema != null) WireJson.ValidateNamed(schema, value); return value;
    }
    internal static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return WireJson.Parse(stream.ToArray(), 1048576);
    }
    internal static void Put(Utf8JsonWriter writer, string name, JsonElement value) { writer.WritePropertyName(name); value.WriteTo(writer); }
    internal static JsonElement Without(JsonElement value, string property) => Build(w => { foreach (var item in value.EnumerateObject()) if (item.Name != property) item.WriteTo(w); });
    internal static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    internal static string Canonical(JsonElement value) => WireJson.CanonicalString(value, 1048576);
    internal static bool Equal(JsonElement left, JsonElement right) => Canonical(left) == Canonical(right);
    internal static long Seq(JsonElement value, string property) => Sequence.Parse(Text(value, property)).ToInt64();
    internal static JsonElement Identity(JsonElement value)
    {
        var fixedValue = Copy(value); var scope = fixedValue.GetProperty("scope"); var target = fixedValue.GetProperty("target");
        Need(fixedValue.EnumerateObject().Count() == 5 && scope.EnumerateObject().Count() == 2 && target.EnumerateObject().Count() == 2, "invalid_input");
        WireJson.ValidateNamed("Scope", Build(w => { foreach (var item in scope.EnumerateObject()) item.WriteTo(w); w.WriteString("authorizationRevision", "0"); }));
        WireJson.ValidateNamed("Target", Build(w => { foreach (var item in target.EnumerateObject()) item.WriteTo(w); w.WriteString("sourceSnapshotDigest", new string('0', 64)); }));
        foreach (string key in new[] { "bindingId", "sourceId", "sourceGeneration" }) WireJson.ValidateNamed("Id", fixedValue.GetProperty(key)); return fixedValue;
    }
    internal static JsonElement Scope(Func<JsonElement> read, JsonElement identity)
    {
        var scope = Copy(read(), "Scope"); Need(Equal(Without(scope, "authorizationRevision"), identity.GetProperty("scope")), "context_changed"); return scope;
    }
    internal static bool TargetMatches(JsonElement actual, JsonElement expected) => Text(actual, "sessionId") == Text(expected, "sessionId") && Equal(actual.GetProperty("generations"), expected.GetProperty("generations"));
    internal static IEnumerable<JsonElement> References(JsonElement record)
    { yield return record.GetProperty("payload"); foreach (var item in record.GetProperty("attachments").EnumerateArray()) yield return item; }
    internal static void Record(JsonElement record)
    {
        WireJson.ValidateNamed("ArchiveRecord", record);
        Need(Text(record, "recordDigest") == WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(Without(record, "recordDigest"))));
        if (record.TryGetProperty("sourceEventRange", out var range)) Need(range.GetProperty("firstSeq").GetInt64() <= range.GetProperty("lastSeq").GetInt64());
        if (record.TryGetProperty("projection", out var projection)) Coverage(projection.GetProperty("coverage"));
    }
    internal static void Coverage(JsonElement value) => Need(Seq(value, "fromSequence") >= 1 && Seq(value, "fromSequence") <= Seq(value, "throughSequence"));
    internal static void Unique(JsonElement values, string? property = null)
    { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var item in values.EnumerateArray()) Need(seen.Add(property == null ? item.GetString()! : Text(item, property))); }
    internal static void VerifyOperation(JsonElement response, JsonElement receipt, JsonElement scope, string operation)
    {
        WireJson.ValidateNamed("MutationReceipt", receipt);
        Need(Text(receipt, "operation") == operation && Text(receipt, "bindingId") == Text(response, "bindingId") && Equal(receipt.GetProperty("request"), response.GetProperty("request")));
        var semantic = Build(w => { w.WriteStartArray("scope"); w.WriteStringValue(Text(scope, "applicationScopeId")); w.WriteStringValue(Text(scope, "endUserId")); w.WriteEndArray(); w.WriteString("operation", operation); Put(w, "semantic", Without(response, "request")); });
        Need(Text(receipt, "semanticDigest") == WireJson.DomainDigest("tansr.sdk2.operation.v1", WireJson.EncodeControl(semantic, 1048576)));
        if (operation == "archive-ack") Need(Text(receipt, "state") == "completed" && Seq(receipt, "revision") > Seq(response, "expectedRevision"));
        if (operation == "material-response") Need(Text(receipt, "state") == "accepted" && Text(receipt, "outcomeRef") == Text(response, "materialRequestId"));
    }
    internal static string Segment(string value) => Uri.EscapeDataString(value);
    internal static string Query(params (string Name, object Value)[] entries) => "?" + string.Join("&", entries.Select(e => e.Name + "=" + Segment(Convert.ToString(e.Value, CultureInfo.InvariantCulture)!)));
    internal static JsonElement Request(string binding, string operation, JsonElement request) => Build(w => { w.WriteString("protocol", Protocol); w.WriteString("bindingId", binding); w.WriteString("operation", operation); Put(w, "request", request); });
}
