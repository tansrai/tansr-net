using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Tansr.Sdk.Protocol;
namespace Tansr.Sdk.Cache;

// 独立候选 schema 校验器；复用本仓 WireSchema 的有界词表，不修改已冻结 sdk2-ext-v1。
// 不提供通用远端 schema 装载、不使用反射 DTO，不将结构验证视作授权。
internal static class CacheSchema
{
    private static readonly JsonElement Definitions = Load();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // Serve 源头已修正候选 code/status 条件；完整采用新候选，不再剥离 allOf。
    internal static void ValidateRuntimeError(JsonElement value)
        => ValidateNamed("ErrorResponse", value);

    internal static void ValidateNamed(string name, JsonElement value)
    {
        try
        {
            var budget = 1000000;
            if (name == null || !Definitions.TryGetProperty(name, out var schema) ||
                !Matches(value, schema, 0, ref budget) || !SequenceBound(name, value))
            {
                throw new WireProtocolException("invalid_response");
            }
        }
        catch (WireProtocolException) { throw; }
        catch (InvalidOperationException) { throw new WireProtocolException("invalid_response"); }
        catch (FormatException) { throw new WireProtocolException("invalid_response"); }
        catch (RegexMatchTimeoutException) { throw new WireProtocolException("invalid_response"); }
    }

    private static JsonElement Load()
    {
        using (var stream = typeof(CacheSchema).Assembly.GetManifestResourceStream("Tansr.Sdk.Cache.sdk2-cache-v1.schema.json"))
        {
            if (stream == null) { throw new InvalidOperationException("The pinned SDK2 schema resource is missing."); }
            using var buffer = new System.IO.MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (WireJson.Sha256(bytes) != "88e7941861fab1a6380b9226e3a42adb8c872efdf720dc36e92ffa42da43d818")
                throw new InvalidOperationException("The pinned cache candidate schema fingerprint changed.");
            using (var document = JsonDocument.Parse(bytes))
            {
                return document.RootElement.GetProperty("definitions").Clone();
            }
        }
    }

    private static bool SequenceBound(string name, JsonElement value)
        => (name != "Sequence" && name != "RecordSequence") ||
           (value.ValueKind == JsonValueKind.String && Sequence.TryParse(value.GetString(), out _));

    private static bool Matches(JsonElement value, JsonElement schema, int depth, ref int budget)
    {
        if (depth > 64 || --budget < 0) { return false; }
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var text = reference.GetString()!;
            const string prefix = "#/definitions/";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) { return false; }
            var name = text.Substring(prefix.Length);
            if (!Definitions.TryGetProperty(name, out var target) || !Matches(value, target, depth + 1, ref budget) ||
                !SequenceBound(name, value)) { return false; }
        }

        if (schema.TryGetProperty("const", out var constant) && !Equal(value, constant)) { return false; }
        if (schema.TryGetProperty("enum", out var enumeration))
        {
            var found = false;
            foreach (var candidate in enumeration.EnumerateArray()) { if (Equal(value, candidate)) { found = true; break; } }
            if (!found) { return false; }
        }

        if (schema.TryGetProperty("allOf", out var all))
        {
            foreach (var child in all.EnumerateArray()) { if (!Matches(value, child, depth + 1, ref budget)) { return false; } }
        }

        if (schema.TryGetProperty("anyOf", out var any))
        {
            var found = false;
            foreach (var child in any.EnumerateArray()) { if (Matches(value, child, depth + 1, ref budget)) { found = true; break; } }
            if (!found) { return false; }
        }

        if (schema.TryGetProperty("oneOf", out var one))
        {
            var count = 0;
            foreach (var child in one.EnumerateArray()) { if (Matches(value, child, depth + 1, ref budget)) { count++; } }
            if (count != 1) { return false; }
        }

        if (schema.TryGetProperty("not", out var not) && Matches(value, not, depth + 1, ref budget)) { return false; }
        if (schema.TryGetProperty("if", out var condition))
        {
            var branch = Matches(value, condition, depth + 1, ref budget) ? "then" : "else";
            if (schema.TryGetProperty(branch, out var child) && !Matches(value, child, depth + 1, ref budget)) { return false; }
        }

        if (schema.TryGetProperty("type", out var type) && !TypeMatches(value, type.GetString()!)) { return false; }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var length = WireJson.ScalarLength(text);
            if (!Within(schema, "minLength", "maxLength", length)) { return false; }
            if (schema.TryGetProperty("pattern", out var pattern) &&
                !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant | RegexOptions.ECMAScript, RegexTimeout)) { return false; }
            if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time" && !DateTimeMatches(text)) { return false; }
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number) ||
                !Within(schema, "minimum", "maximum", number)) { return false; }
            if (schema.TryGetProperty("multipleOf", out var multiple) && number % multiple.GetDouble() != 0) { return false; }
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!Within(schema, "minItems", "maxItems", value.GetArrayLength())) { return false; }
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                foreach (var item in value.EnumerateArray()) { if (!Matches(item, itemSchema, depth + 1, ref budget)) { return false; } }
            }

            if (schema.TryGetProperty("contains", out var contains))
            {
                var found = false;
                foreach (var item in value.EnumerateArray()) { if (Matches(item, contains, depth + 1, ref budget)) { found = true; break; } }
                if (!found) { return false; }
            }

            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in value.EnumerateArray()) { if (!seen.Add(WireJson.CanonicalString(item, 1048576))) { return false; } }
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
            foreach (var property in value.EnumerateObject())
            {
                WireJson.ValidateUnicode(property.Name);
                if (!seen.Add(property.Name)) { return false; }
                if (hasProperties && properties.TryGetProperty(property.Name, out var child))
                {
                    if (!Matches(property.Value, child, depth + 1, ref budget)) { return false; }
                }
                else if (closed) { return false; }
            }

            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var key in required.EnumerateArray()) { if (!seen.Contains(key.GetString()!)) { return false; } }
            }
        }

        return true;
    }

    private static bool Within(JsonElement schema, string minimum, string maximum, double value)
        => (!schema.TryGetProperty(minimum, out var min) || value >= min.GetDouble()) &&
           (!schema.TryGetProperty(maximum, out var max) || value <= max.GetDouble());

    private static bool TypeMatches(JsonElement value, string type)
    {
        switch (type)
        {
            case "null": return value.ValueKind == JsonValueKind.Null;
            case "boolean": return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
            case "string": return value.ValueKind == JsonValueKind.String;
            case "array": return value.ValueKind == JsonValueKind.Array;
            case "object": return value.ValueKind == JsonValueKind.Object;
            case "integer":
                return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
                       !double.IsNaN(number) && !double.IsInfinity(number) && Math.Abs(number) <= WireJson.MaximumSafeInteger &&
                       Math.Truncate(number) == number && !(number == 0 && value.GetRawText().StartsWith("-", StringComparison.Ordinal));
            case "number": return value.ValueKind == JsonValueKind.Number;
            default: return false;
        }
    }

    private static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) { return false; }
        switch (left.ValueKind)
        {
            case JsonValueKind.String: return left.GetString() == right.GetString();
            case JsonValueKind.Number: return left.TryGetDouble(out var a) && right.TryGetDouble(out var b) && a == b;
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False: return true;
            default: return WireJson.CanonicalString(left, 1048576) == WireJson.CanonicalString(right, 1048576);
        }
    }

    private static bool DateTimeMatches(string value)
    {
        // 同 wireDateTime：公历校验、大小写 T/Z、闰秒及最大23:59偏移。
        var match = Regex.Match(value,
            "^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt]([0-9]{2}):([0-9]{2}):([0-9]{2})(\\.[0-9]+)?([Zz]|[+-][0-9]{2}:[0-9]{2})$",
            RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success) { return false; }
        var year = Number(match.Groups[1].Value);
        var month = Number(match.Groups[2].Value);
        var day = Number(match.Groups[3].Value);
        var days = new[] { 31, year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
        var zone = match.Groups[8].Value;
        return month >= 1 && month <= 12 && day >= 1 && day <= days[month - 1] &&
               Number(match.Groups[4].Value) <= 23 && Number(match.Groups[5].Value) <= 59 && Number(match.Groups[6].Value) <= 60 &&
               (zone.Length == 1 || (Number(zone.Substring(1, 2)) <= 23 && Number(zone.Substring(4, 2)) <= 59));
    }

    private static int Number(string value) => int.Parse(value, CultureInfo.InvariantCulture);
}
