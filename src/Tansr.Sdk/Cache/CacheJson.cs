using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Cache;

internal static class CacheJson
{
    internal const string Protocol = "sdk2-cache-v1";
    internal const string Feature = "private-logical-cache-v1";
    internal const int MaximumBytes = 65536;

    internal static JsonElement Read(byte[] bytes)
    {
        try
        {
            var value = WireJson.Parse(bytes, MaximumBytes);
            LexicalNumbers(value);
            // 同原 decoder：允许空白及非排序输入，但不允许非 ASCII key/负数/指数/小数。
            WireJson.EncodeControl(value, MaximumBytes);
            return value;
        }
        catch (WireProtocolException) { throw new TansrProtocolException("invalid_response"); }
    }

    private static void LexicalNumbers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            var raw = value.GetRawText();
            if (raw.Length == 0 || raw.Length > 1 && raw[0] == '0') throw new WireProtocolException("invalid_response");
            foreach (var c in raw) if (c < '0' || c > '9') throw new WireProtocolException("invalid_response");
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) LexicalNumbers(property.Value);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) LexicalNumbers(item);
    }

    internal static byte[] Encode(JsonElement value)
    {
        try { return WireJson.EncodeControl(value, MaximumBytes); }
        catch (WireProtocolException) { throw new TansrProtocolException("invalid_request"); }
    }

    internal static JsonElement Write(Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) write(writer);
        return Read(output.ToArray());
    }

    internal static void Validate(string name, JsonElement value)
    {
        try { CacheSchema.ValidateNamed(name, value); }
        catch (WireProtocolException) { throw new TansrProtocolException("invalid_response"); }
    }

    internal static void Text(string name, string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        Validate(name, Write(writer => writer.WriteStringValue(value)));
        if (name == "Ticket")
        {
            try
            {
                var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
                if (bytes.Length != 32 || Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != value)
                    throw new FormatException();
            }
            catch (FormatException) { throw new TansrProtocolException("invalid_response"); }
        }
    }

    internal static string String(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    internal static long Sequence(JsonElement value, string name) => long.Parse(String(value, name), CultureInfo.InvariantCulture);
    internal static string Canonical(JsonElement value) => Encoding.UTF8.GetString(Encode(value));
    internal static void Binding(JsonElement value)
    {
        Validate("BindingView", value);
        if (String(value, "audience") != "serve-cache") throw new TansrProtocolException("invalid_response");
    }

    internal static void Error(JsonElement value, int status)
    {
        try { CacheSchema.ValidateRuntimeError(value); }
        catch (WireProtocolException) { throw new TansrProtocolException("invalid_response"); }
        if (value.GetProperty("status").GetInt32() != status) throw new TansrProtocolException("invalid_response");
    }
}
