using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

// Independent, explicitly negotiated sidecar. The original six-action contract is unchanged.
internal static class TerminalPersistenceContract
{
    internal const string Protocol = "terminal-persistence-v1";
    internal const string Revision = "2026-10-10.v1";
    internal const string ToolName = "TansrTerminalPersistenceV1";
    internal const string DefinitionDigest = "33029a264edf81f3fda2a13fc382403d0cd7ffefa1f38088bb9366f387a13587";
    internal const string SchemaSha256 = "47387fb03308d00244e876a3bb429e24b81ac8d94d5f611aeda43e03b7c8b16c";
    private static readonly EmbeddedWireContract Contract = new("Tansr.Sdk.Terminal.terminal-persistence-v1.schema.json", SchemaSha256,
        new[] { "Id", "LegacyId", "Sequence", "Digest", "Scope", "ExecutionBinding", "ExecutionTarget", "ExecutionInterpreter" });

    internal static JsonElement Copy(string name, JsonElement value)
    {
        var copy = WireJson.DecodeControl(WireJson.EncodeControl(value, 32768), 32768);
        Validate(name, copy); return copy;
    }

    internal static void ValidateStructure(string name, JsonElement value) => Contract.Validate(name, value);

    internal static void Validate(string name, JsonElement value)
    {
        Contract.Validate(name, value);
        try
        {
            if (name == "BodyPlan") Body(value);
            if (name == "IndexPlan") Index(value);
            if (name == "Root") Body(value.GetProperty("body"));
            if (name == "Request" && Text(value, "action") == "begin" || name == "BeginRequest")
            {
                Body(value.GetProperty("body")); Index(value.GetProperty("index"));
                var original = Json(writer => { foreach (var property in value.EnumerateObject()) if (property.Name != "intentSha256") property.WriteTo(writer); });
                Need(Digest(original) == Text(value, "intentSha256"));
            }
            if (name == "Request" && Text(value, "action") == "put" || name == "PutRequest") Payload(value, "sha256");
            if (name == "IndexPage")
            {
                string? last = null; var secondary = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in value.GetProperty("entries").EnumerateArray())
                {
                    string current = Text(entry, "primaryKey");
                    Need((last == null || string.CompareOrdinal(last, current) < 0) && secondary.Add(Text(entry, "secondaryKey"))); last = current;
                }
            }
            if (name == "Response" && Text(value, "action") == "read" || name == "ReadBodyResponse" || name == "ReadPageResponse") Payload(value, "payloadDigest");
        }
        catch (WireProtocolException) { throw; }
        catch (Exception error) when (error is InvalidOperationException || error is FormatException || error is OverflowException || error is ArgumentException)
        { throw new WireProtocolException("invalid_request"); }
    }

    private static void Body(JsonElement value)
    {
        int length = value.GetProperty("byteLength").GetInt32(), blocks = value.GetProperty("blockCount").GetInt32();
        Need(blocks == (length + 12287) / 12288 && value.GetProperty("pageHashes").GetArrayLength() == (blocks + 63) / 64);
        if (length == 0) Need(Text(value, "sha256") == TerminalCandidateContract.EmptyDigest);
    }

    private static void Index(JsonElement value)
    {
        int count = value.GetProperty("entryCount").GetInt32();
        Need(value.GetProperty("addedCount").GetInt32() <= count && value.GetProperty("pageHashes").GetArrayLength() == (count + 31) / 32);
    }

    internal static byte[] Payload(JsonElement value, string digest)
    {
        var bytes = WireJson.DecodeBase64(Text(value, "base64"));
        Need(bytes.Length == value.GetProperty("byteLength").GetInt32() && WireJson.Sha256(bytes) == Text(value, digest)); return bytes;
    }

    internal static string Digest(JsonElement value) => WireJson.Sha256(WireJson.EncodeControl(value, 262144));
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    internal static JsonElement Json(Action<Utf8JsonWriter> content)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory)) { writer.WriteStartObject(); content(writer); writer.WriteEndObject(); }
        return WireJson.Parse(memory.ToArray(), 262144);
    }
    private static void Need(bool condition) { if (!condition) throw new WireProtocolException("integrity_mismatch"); }
}
