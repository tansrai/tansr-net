using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

internal static class PersistenceCustodyContract
{
    internal const string Protocol = "persistence-custody-handoff-v1";
    internal const string Revision = "2026-10-21.v1";
    internal const string ToolName = "TansrPersistenceCustodyHandoffV1";
    internal const string DefinitionDigest = "7f3b94cb801bf29be071674cdab0450855e70c55ab44bea27508c30dfbebb5e7";
    internal const string SchemaSha256 = "8c84ad5ba1461207dd0d9f11871989aeb8b971f6557c39ca4e06a690e191f9d9";
    private static readonly EmbeddedWireContract Contract = new("Tansr.Sdk.Terminal.persistence-custody-handoff-v1.schema.json", SchemaSha256,
        new[] { "Id", "LegacyId", "Sequence", "Digest", "Scope", "ExecutionBinding", "ExecutionTarget", "ExecutionInterpreter" });

    internal static JsonElement Copy(string name, JsonElement value, int maximumBytes = 32768)
    { var copy = WireJson.DecodeControl(WireJson.EncodeControl(value, maximumBytes), maximumBytes); Validate(name, copy, maximumBytes); return copy; }

    internal static void Validate(string name, JsonElement value, int maximumBytes = 32768)
    {
        Contract.Validate(name, value, maximumBytes);
        if (name == "ObjectChunk")
        {
            byte[] bytes = WireJson.DecodeBase64(Text(value, "base64"));
            Check(bytes.Length == value.GetProperty("byteLength").GetInt32() && WireJson.Sha256(bytes) == Text(value, "payloadDigest"));
            Check((long)value.GetProperty("offset").GetInt32() + bytes.Length <= value.GetProperty("object").GetProperty("byteLength").GetInt32());
        }
        if (name == "Page")
        {
            string digest = Text(value, "previousDigest"); long index = long.Parse(Text(value, "index"), System.Globalization.CultureInfo.InvariantCulture);
            foreach (var record in value.GetProperty("records").EnumerateArray()) digest = RecordDigest((index++).ToString(System.Globalization.CultureInfo.InvariantCulture), digest, record);
            Check(digest == Text(value, "sha256"));
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("chunk", out var chunk)) Validate("ObjectChunk", chunk);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("page", out var page)) Validate("Page", page);
        if (name == "TransferMetadata")
        {
            TerminalPersistenceContract.Validate("Request", value.GetProperty("begin"));
            var begin = value.GetProperty("begin"); var transfer = value.GetProperty("transfer");
            Check(Text(begin, "transferId") == Text(transfer, "transferId") && Text(begin, "intentSha256") == Text(transfer, "intentSha256"));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string field in new[] { "received", "reused" })
            {
                string? previous = null;
                foreach (var item in value.GetProperty(field).EnumerateArray())
                {
                    string key = Text(item, "kind") + ":" + Text(item, "sha256");
                    Check(seen.Add(key) && (previous == null || StringComparer.Ordinal.Compare(previous, key) < 0)); previous = key;
                }
            }
        }
    }

    internal static void ValidateResponse(JsonElement request, JsonElement response)
    {
        Validate("Response", response);
        foreach (string name in new[] { "contract", "identity", "operationId", "intentSha256", "action" }) Check(Equal(request.GetProperty(name), response.GetProperty(name)));
        foreach (string name in new[] { "ticket", "claim" })
        {
            if (!response.TryGetProperty(name, out var fact) || fact.ValueKind == JsonValueKind.Null) continue;
            Check(Text(fact, "operationId") == Text(request, "operationId") && Text(fact, "intentSha256") == Text(request, "intentSha256"));
            if (name == "ticket") Check(Equal(fact.GetProperty("identity"), request.GetProperty("identity")));
        }
        string action = Text(request, "action");
        if (action == "query") Check(!response.GetProperty("preparing").GetBoolean() || response.GetProperty("ticket").ValueKind == JsonValueKind.Null);
        if (action == "export-object")
        {
            var chunk = response.GetProperty("chunk"); Check(Equal(request.GetProperty("object"), chunk.GetProperty("object")) &&
                request.GetProperty("offset").GetInt32() == chunk.GetProperty("offset").GetInt32() && chunk.GetProperty("byteLength").GetInt32() <= request.GetProperty("length").GetInt32());
        }
        if (action == "import-object")
        {
            var chunk = request.GetProperty("chunk"); Check(Equal(chunk.GetProperty("object"), response.GetProperty("object")) &&
                response.GetProperty("nextOffset").GetInt32() == chunk.GetProperty("offset").GetInt32() + chunk.GetProperty("byteLength").GetInt32());
        }
        if (action == "import-page") Check(Text(request.GetProperty("page"), "sha256") == Text(response, "pageDigest"));
    }

    internal static bool Equal(JsonElement left, JsonElement right) => WireJson.CanonicalString(left, 524288) == WireJson.CanonicalString(right, 524288);
    internal static string Digest(JsonElement value) => WireJson.Sha256(WireJson.EncodeControl(value, 524288));
    internal static string SnapshotDigest(JsonElement identity, JsonElement rootRecord) => Chain("PST-CUSTODY-SNAPSHOT", identity, rootRecord);
    internal static string InventorySeed(JsonElement identity, string originFormat, string fixedRoot) => Chain("PST-CUSTODY-DIRECTORY", identity, Literal(originFormat), Literal(fixedRoot));
    internal static string RecordDigest(string index, string previousDigest, JsonElement record) => Chain("PST-CUSTODY-RECORD", Literal(index), Literal(previousDigest), record);
    internal static string WriterOperationId(JsonElement request) => Chain("PST-CUSTODY", request);
    internal static string PrepareIntentDigest(JsonElement request) => Digest(TerminalPersistenceContract.Json(w =>
    { foreach (string name in new[] { "identity", "source", "destination", "expectedWriterFence", "expectedRoot" }) { w.WritePropertyName(name); request.GetProperty(name).WriteTo(w); } }));
    private static JsonElement Literal(string value) => TerminalPersistenceContract.Json(w => w.WriteString("value", value)).GetProperty("value").Clone();
    private static string Chain(string tag, params JsonElement[] elements)
    {
        using var memory = new MemoryStream(); using (var writer = new Utf8JsonWriter(memory))
        { writer.WriteStartArray(); writer.WriteStringValue(tag); foreach (var element in elements) element.WriteTo(writer); writer.WriteEndArray(); }
        return WireJson.Sha256(WireJson.EncodeControl(WireJson.Parse(memory.ToArray(), 32768), 32768));
    }
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static void Check(bool condition) { if (!condition) throw new WireProtocolException("invalid_response"); }
}
