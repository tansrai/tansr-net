using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

// Internal consumer of the Serve-owned candidate. Never included in the stable public contract.
internal static class TerminalCandidateContract
{
    internal const string Protocol = "terminal-services-v1";
    internal const string Revision = "2026-09-26.candidate-4";
    internal const string SchemaSha256 = "5973fde3f029f9c92794cde385e4041a150de0ace476bb2f6ff2c63c2a166dd0";
    internal const string EmptyDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly JsonElement Definitions = Load();
    private static readonly HashSet<string> Shared = new(StringComparer.Ordinal)
        { "Id", "LegacyId", "Sequence", "Digest", "Scope", "ExecutionBinding", "ExecutionTarget", "ExecutionInterpreter",
          "ExecutionStatus", "ExecutionOperation", "ResourceRequest", "ExecutionReceiptRequest", "ResourceResult" };

    internal static JsonElement Decode(string name, byte[] bytes)
    {
        var value = WireJson.DecodeControl(bytes);
        Validate(name, value);
        return value;
    }

    internal static void Validate(string name, JsonElement value)
    {
        try
        {
            ValidateStructure(name, value);
            if (name == "OutputBlock") ValidateBlock(value);
            if (name == "OutputSeal") ValidateSeal(value);
            if (name == "OutputStatus") ValidateStatus(value);
            if (name == "OutputBatchRequest")
            {
                var length = 0;
                foreach (var block in value.GetProperty("blocks").EnumerateArray())
                { ValidateBlock(block); length += block.GetProperty("byteLength").GetInt32(); }
                if (length > 65536) Fail("payload_too_large");
                if (value.GetProperty("seal").ValueKind != JsonValueKind.Null) ValidateSeal(value.GetProperty("seal"));
            }
            if (name == "OutputEvent")
            {
                if (TerminalJson.Text(value, "type") == "output.block") ValidateBlock(value.GetProperty("block"));
                else ValidateStatus(value.GetProperty("status"));
            }
            if (name == "Limits") ValidateLimits(value);
            if (name == "CapabilitiesResponse")
            {
                ValidateLimits(value.GetProperty("limits"));
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var feature in value.GetProperty("features").EnumerateArray())
                {
                    if (!names.Add(TerminalJson.Text(feature, "feature"))) Fail();
                    if (feature.GetProperty("installed").GetBoolean() && !feature.GetProperty("supported").GetBoolean()) Fail();
                }
                if (names.Count != 4) Fail();
            }
            if (name == "BindingRequest")
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var list in new[] { "required", "optional" })
                    foreach (var feature in value.GetProperty(list).EnumerateArray()) if (!names.Add(feature.GetString()!)) Fail();
                if (names.Count == 0) Fail();
            }
            if (name == "BindingResponse") ValidateLimits(value.GetProperty("limits"));
        }
        catch (WireProtocolException) { throw; }
        catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is FormatException || error is OverflowException || error is RegexMatchTimeoutException)
        { throw new WireProtocolException("invalid_response"); }
    }

    internal static void ValidateStructure(string name, JsonElement value)
    {
        try
        {
            // Structural goldens deliberately include representable scalars that cannot be added without overflow.
            // Keep their structural acceptance distinct from consumer semantics and state transitions.
            WireJson.EncodeControl(value);
            if (!Definitions.TryGetProperty(name, out var definition) || !Matches(value, definition, 0)) Fail();
            if (Shared.Contains(name)) WireJson.ValidateNamed(name, value);
        }
        catch (WireProtocolException) { throw; }
        catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is FormatException || error is OverflowException || error is RegexMatchTimeoutException)
        { throw new WireProtocolException("invalid_response"); }
    }

    internal static void ValidateStatus(JsonElement value)
    {
        var accepted = TerminalJson.OptionalSequence(value, "acceptedThrough");
        var durable = TerminalJson.OptionalSequence(value, "durableThrough");
        var retained = TerminalJson.OptionalSequence(value, "retainedFrom");
        var offset = TerminalJson.OptionalSequence(value, "nextByteOffset");
        var state = TerminalJson.Text(value, "state");
        var seal = value.GetProperty("seal");
        if (durable.HasValue && (!accepted.HasValue || durable > accepted) || retained.HasValue && (!accepted.HasValue || retained > accepted)) Fail();
        if (!offset.HasValue)
        {
            if (state != "unavailable" || accepted.HasValue || durable.HasValue || retained.HasValue || seal.ValueKind != JsonValueKind.Null) Fail();
        }
        else if (!accepted.HasValue && offset != 0) Fail();
        // Each block has at least one byte. Subtraction avoids an Int64.MaxValue + 1 sentinel.
        if (accepted.HasValue && (!offset.HasValue || offset <= accepted)) Fail();
        if (seal.ValueKind != JsonValueKind.Null)
        {
            ValidateSeal(seal);
            if (TerminalJson.OptionalSequence(seal, "lastSeq") != accepted || TerminalJson.Sequence(seal, "totalBytes") != offset) Fail();
        }
        if (state == "complete" && (seal.ValueKind == JsonValueKind.Null || seal.GetProperty("truncated").GetBoolean())) Fail();
        if (state == "truncated" && (seal.ValueKind == JsonValueKind.Null || !seal.GetProperty("truncated").GetBoolean())) Fail();
        if (state == "available" && (accepted.HasValue || seal.ValueKind != JsonValueKind.Null)) Fail();
        if (state == "receiving" && (!accepted.HasValue || seal.ValueKind != JsonValueKind.Null)) Fail();
        if (state == "gap" && !accepted.HasValue) Fail();
    }

    private static void ValidateBlock(JsonElement value)
    {
        var bytes = WireJson.DecodeBase64(TerminalJson.Text(value, "base64"));
        if (bytes.Length != value.GetProperty("byteLength").GetInt32() || WireJson.Sha256(bytes) != TerminalJson.Text(value, "payloadDigest")) Fail("integrity_mismatch");
        if (TerminalJson.Sequence(value, "byteOffset") > long.MaxValue - bytes.Length) Fail("capacity_exceeded");
    }

    private static void ValidateSeal(JsonElement value)
    {
        var last = TerminalJson.OptionalSequence(value, "lastSeq");
        var total = TerminalJson.Sequence(value, "totalBytes");
        if (!last.HasValue && (total != 0 || TerminalJson.Text(value, "payloadDigest") != EmptyDigest) || last.HasValue && total <= last) Fail("integrity_mismatch");
    }

    private static void ValidateLimits(JsonElement value)
    {
        var block = value.GetProperty("maxBlockBytes").GetInt32();
        var batch = value.GetProperty("maxBatchBytes").GetInt32();
        if (block > batch || batch > value.GetProperty("maxPendingBytes").GetInt32() || batch > value.GetProperty("maxRetainedBytes").GetInt32()) Fail();
    }

    private static JsonElement Load()
    {
        using var stream = typeof(TerminalCandidateContract).Assembly.GetManifestResourceStream("Tansr.Sdk.Terminal.terminal-services-v1.schema.json")
            ?? throw new InvalidOperationException("The pinned terminal candidate schema is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (WireJson.Sha256(bytes) != SchemaSha256) throw new InvalidOperationException("The terminal candidate schema fingerprint changed.");
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("definitions").Clone();
    }

    // Implements only the vocabulary present in the pinned Serve candidate; no external schemas or reflection.
    private static bool Matches(JsonElement value, JsonElement schema, int depth)
    {
        if (depth > 64) return false;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            const string prefix = "#/definitions/";
            var name = reference.GetString()!;
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
            name = name.Substring(prefix.Length);
            if (Shared.Contains(name))
            {
                try { WireJson.ValidateNamed(name, value); return true; }
                catch (WireProtocolException) { return false; }
            }
            return Definitions.TryGetProperty(name, out var target) && Matches(value, target, depth + 1);
        }
        if (schema.TryGetProperty("const", out var constant) && !Equal(value, constant)) return false;
        if (schema.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(x => Equal(value, x))) return false;
        foreach (var key in new[] { "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(key, out var optionsList)) continue;
            var count = 0;
            foreach (var option in optionsList.EnumerateArray()) if (Matches(value, option, depth + 1)) count++;
            if (key == "oneOf" ? count != 1 : count == 0) return false;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var name = type.GetString();
            if (name == "object" && value.ValueKind != JsonValueKind.Object || name == "array" && value.ValueKind != JsonValueKind.Array ||
                name == "string" && value.ValueKind != JsonValueKind.String || name == "null" && value.ValueKind != JsonValueKind.Null ||
                name == "boolean" && value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False ||
                name == "integer" && (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var integer) || Math.Truncate(integer) != integer)) return false;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var length = WireJson.ScalarLength(text);
            if (!Within(schema, "minLength", "maxLength", length)) return false;
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.ECMAScript | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
        }
        if (value.ValueKind == JsonValueKind.Number && !Within(schema, "minimum", "maximum", value.GetDouble())) return false;
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!Within(schema, "minItems", "maxItems", value.GetArrayLength())) return false;
            if (schema.TryGetProperty("items", out var items)) foreach (var item in value.EnumerateArray()) if (!Matches(item, items, depth + 1)) return false;
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
            {
                var values = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in value.EnumerateArray()) if (!values.Add(WireJson.CanonicalString(item))) return false;
            }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) return false;
                if (hasProperties && properties.TryGetProperty(property.Name, out var child))
                { if (!Matches(property.Value, child, depth + 1)) return false; }
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False) return false;
            }
            if (schema.TryGetProperty("required", out var required)) foreach (var key in required.EnumerateArray()) if (!keys.Contains(key.GetString()!)) return false;
        }
        return true;
    }

    private static bool Within(JsonElement schema, string minimum, string maximum, double value)
        => (!schema.TryGetProperty(minimum, out var min) || value >= min.GetDouble()) && (!schema.TryGetProperty(maximum, out var max) || value <= max.GetDouble());
    private static bool Equal(JsonElement left, JsonElement right) => left.ValueKind == right.ValueKind &&
        (left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText());
    private static void Fail(string code = "invalid_response") => throw new WireProtocolException(code);
}
