using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tansr.Sdk.Protocol;

/// <summary>SDK2 规范控制字节。原 IR/附件必须保存原字节，不经此编码器重写。</summary>
public static class WireJson
{
    public const int MaximumControlBytes = 262144;
    public const int MaximumNodes = 100000;
    public const long MaximumSafeInteger = 9007199254740991;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>有界业务 JSON 读取；保留小数/负数，严格拒绝重复键与非法 Unicode。</summary>
    public static JsonElement Parse(byte[] utf8, int maximumBytes = MaximumControlBytes,
        int maximumDepth = 32, bool requireCanonical = false)
    {
        if (utf8 == null) { throw new ArgumentNullException(nameof(utf8)); }
        if (maximumBytes < 1 || maximumDepth < 1 || maximumDepth > 64)
        {
            throw new WireProtocolException("invalid_options");
        }

        if (utf8.Length > maximumBytes) { throw new WireProtocolException("payload_too_large"); }
        try
        {
            // JsonDocument 部分路径延迟解码文本，先核原始 UTF-8，拒绝 BOM。
            var text = Utf8.GetString(utf8);
            if (text.Length > 0 && text[0] == '\ufeff') { Fail(); }
            using (var document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth + 1
            }))
            {
                var nodes = 0;
                ValidateJson(document.RootElement, 0, maximumDepth, ref nodes);
                if (requireCanonical && CanonicalString(document.RootElement, maximumBytes) != text) { Fail(); }
                return document.RootElement.Clone();
            }
        }
        catch (WireProtocolException) { throw; }
        catch (JsonException) { throw new WireProtocolException("invalid_response"); }
        catch (DecoderFallbackException) { throw new WireProtocolException("invalid_response"); }
        catch (InvalidOperationException) { throw new WireProtocolException("invalid_response"); }
        catch (ArgumentException) { throw new WireProtocolException("invalid_response"); }
    }

    /// <summary>读取 sdk2-ext-v1 控制响应，要求与规范编码逐字节一致。</summary>
    public static JsonElement DecodeControl(byte[] utf8, int maximumBytes = MaximumControlBytes)
        => Parse(utf8, maximumBytes, 32, true);

    public static byte[] EncodeControl(JsonElement value, int maximumBytes = MaximumControlBytes)
        => Utf8.GetBytes(CanonicalString(value, maximumBytes));

    public static string CanonicalString(JsonElement value, int maximumBytes = MaximumControlBytes)
    {
        if (maximumBytes < 1) { throw new WireProtocolException("invalid_options"); }
        try
        {
            var writer = new CanonicalWriter(maximumBytes);
            writer.Write(value, 0);
            return writer.ToString();
        }
        catch (WireProtocolException) { throw; }
        catch (InvalidOperationException) { throw new WireProtocolException("invalid_request"); }
        catch (ArgumentException) { throw new WireProtocolException("invalid_request"); }
    }

    /// <summary>按锁定的具名 schema 校验；不等于授权、CAS或执行语义已验证。</summary>
    public static void ValidateNamed(string name, JsonElement value) => WireSchema.ValidateNamed(name, value);

    public static string Sha256(byte[] bytes)
    {
        if (bytes == null) { throw new ArgumentNullException(nameof(bytes)); }
        using (var sha = SHA256.Create())
        {
            return Hex(sha.ComputeHash(bytes));
        }
    }

    public static string DomainDigest(string domain, byte[] bytes)
    {
        if (domain == null) { throw new ArgumentNullException(nameof(domain)); }
        if (bytes == null) { throw new ArgumentNullException(nameof(bytes)); }
        ValidateUnicode(domain);
        if (domain.Length == 0 || domain.IndexOf('\0') >= 0) { throw new WireProtocolException("invalid_request"); }
        var prefix = Utf8.GetBytes(domain);
        using (var sha = SHA256.Create())
        {
            sha.TransformBlock(prefix, 0, prefix.Length, prefix, 0);
            var separator = new byte[1];
            sha.TransformBlock(separator, 0, 1, separator, 0);
            sha.TransformFinalBlock(bytes, 0, bytes.Length);
            return Hex(sha.Hash!);
        }
    }

    public static byte[] DecodeBase64(string value)
    {
        if (value == null) { throw new ArgumentNullException(nameof(value)); }
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (Convert.ToBase64String(bytes) != value) { throw new FormatException(); }
            return bytes;
        }
        catch (FormatException) { throw new WireProtocolException("integrity_mismatch"); }
    }

    internal static void ValidateUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) { Fail(); }
            }
            else if (char.IsLowSurrogate(value[i])) { Fail(); }
        }
    }

    internal static int ScalarLength(string value)
    {
        ValidateUnicode(value);
        var count = 0;
        for (var i = 0; i < value.Length; i++, count++)
        {
            if (char.IsHighSurrogate(value[i])) { i++; }
        }

        return count;
    }

    private static void ValidateJson(JsonElement value, int depth, int maximumDepth, ref int nodes)
    {
        if (depth > maximumDepth || ++nodes > MaximumNodes) { Fail(); }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    ValidateUnicode(property.Name);
                    if (!keys.Add(property.Name)) { Fail(); }
                    ValidateJson(property.Value, depth + 1, maximumDepth, ref nodes);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) { ValidateJson(item, depth + 1, maximumDepth, ref nodes); }
                break;
            case JsonValueKind.String:
                ValidateUnicode(value.GetString()!);
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number) ||
                    (Math.Truncate(number) == number && Math.Abs(number) > MaximumSafeInteger)) { Fail(); }
                break;
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                break;
            default: Fail(); break;
        }
    }

    private static string Hex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) { text.Append(b.ToString("x2", CultureInfo.InvariantCulture)); }
        return text.ToString();
    }

    private static void Fail() => throw new WireProtocolException("invalid_response");

    private sealed class CanonicalWriter
    {
        private readonly StringBuilder _text = new StringBuilder();
        private readonly int _maximum;
        private int _bytes;
        private int _nodes;

        public CanonicalWriter(int maximum) { _maximum = maximum; }
        public override string ToString() => _text.ToString();

        private void Add(string text)
        {
            var length = Utf8.GetByteCount(text);
            if (length > _maximum - _bytes) { throw new WireProtocolException("payload_too_large"); }
            _bytes += length;
            _text.Append(text);
        }

        private void Quoted(string value)
        {
            ValidateUnicode(value);
            if (value.Length > _maximum - _bytes) { throw new WireProtocolException("payload_too_large"); }
            Add("\"");
            var start = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c >= 0x20 && c != '"' && c != '\\') { continue; }
                if (i > start) { Add(value.Substring(start, i - start)); }
                switch (c)
                {
                    case '"': Add("\\\""); break;
                    case '\\': Add("\\\\"); break;
                    case '\b': Add("\\b"); break;
                    case '\t': Add("\\t"); break;
                    case '\n': Add("\\n"); break;
                    case '\f': Add("\\f"); break;
                    case '\r': Add("\\r"); break;
                    default: Add("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                }

                start = i + 1;
            }

            if (start < value.Length) { Add(value.Substring(start)); }
            Add("\"");
        }

        public void Write(JsonElement value, int depth)
        {
            if (depth > 32 || ++_nodes > MaximumNodes) { throw new WireProtocolException("invalid_request"); }
            switch (value.ValueKind)
            {
                case JsonValueKind.Null: Add("null"); return;
                case JsonValueKind.True: Add("true"); return;
                case JsonValueKind.False: Add("false"); return;
                case JsonValueKind.String: Quoted(value.GetString()!); return;
                case JsonValueKind.Number:
                    if (!value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number) ||
                        number < 0 || number > MaximumSafeInteger || Math.Truncate(number) != number ||
                        (number == 0 && value.GetRawText().StartsWith("-", StringComparison.Ordinal)))
                    {
                        throw new WireProtocolException("invalid_request");
                    }

                    Add(((long)number).ToString(CultureInfo.InvariantCulture));
                    return;
                case JsonValueKind.Array:
                    Add("[");
                    var firstItem = true;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (!firstItem) { Add(","); }
                        firstItem = false;
                        Write(item, depth + 1);
                    }

                    Add("]");
                    return;
                case JsonValueKind.Object:
                    var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (properties.Count >= MaximumNodes - _nodes || property.Name.Length == 0 || properties.ContainsKey(property.Name))
                        {
                            throw new WireProtocolException("invalid_request");
                        }
                        if (property.Name.Length > _maximum - _bytes) { throw new WireProtocolException("payload_too_large"); }
                        foreach (var c in property.Name)
                        {
                            if (c < 0x21 || c > 0x7e) { throw new WireProtocolException("invalid_request"); }
                        }

                        properties.Add(property.Name, property.Value);
                    }

                    Add("{");
                    var firstProperty = true;
                    foreach (var property in properties)
                    {
                        if (!firstProperty) { Add(","); }
                        firstProperty = false;
                        Quoted(property.Key);
                        Add(":");
                        Write(property.Value, depth + 1);
                    }

                    Add("}");
                    return;
                default: throw new WireProtocolException("invalid_request");
            }
        }
    }
}
