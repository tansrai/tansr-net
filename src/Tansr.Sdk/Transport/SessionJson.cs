using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Transport;

internal static class SessionJson
{
    internal const long SafeInteger = 9007199254740991L;
    internal static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) { write(writer); writer.Flush(); }
        return buffer.ToArray();
    }

    internal static byte[] Object(Action<Utf8JsonWriter>? write = null) => Write(w =>
    { w.WriteStartObject(); write?.Invoke(w); w.WriteEndObject(); });

    internal static JsonElement Parse(byte[] bytes)
    {
        try
        {
            var value = WireJson.Parse(bytes, 32 * 1024 * 1024, 64);
            if (value.ValueKind != JsonValueKind.Object) throw new TansrProtocolException("invalid_response");
            return value;
        }
        catch (WireProtocolException)
        {
            // 会话传输维持统一异常体系，不暴露底层解析异常或原始响应。
            throw new TansrProtocolException("invalid_response");
        }
    }

    internal static string String(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString()))
            throw new TansrProtocolException("invalid_response");
        return field.GetString()!;
    }

    internal static long Sequence(JsonElement value, string key)
        => ReadSequence(value, key, 0);

    // Serve 空事件日志返回 -1；仅元信息 lastSeq 使用该哨兵，事件序号不得为负数。
    internal static long LastSequence(JsonElement value)
        => ReadSequence(value, "lastSeq", -1);

    private static long ReadSequence(JsonElement value, string key, long minimum)
    {
        if (!value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.Number ||
            !field.TryGetInt64(out var number) || number < minimum || number > SafeInteger)
            throw new TansrProtocolException("invalid_response");
        return number;
    }

    internal static string Segment(string value)
    {
        Text(value, 512, "identifier");
        return Uri.EscapeDataString(value);
    }

    internal static void Text(string value, int limit, string name, bool allowEmpty = false)
    {
        if (value is null || !allowEmpty && value.Length == 0 || value.Length > limit)
            throw new ArgumentException("Invalid " + name + ".", name);
        foreach (char c in value) if (c < 32 || c == 127) throw new ArgumentException("Invalid " + name + ".", name);
        Unicode(value);
    }

    internal static void Unicode(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new ArgumentException("Invalid Unicode text."); }
            else if (char.IsLowSurrogate(value[i])) throw new ArgumentException("Invalid Unicode text.");
        }
    }

    internal static void Optional(Utf8JsonWriter writer, string name, string? value)
    { if (value is not null) { Unicode(value); writer.WriteString(name, value); } }

    internal static string? Code(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return null;
        var text = field.GetString();
        if (string.IsNullOrEmpty(text) || text!.Length > 128 || text[0] < 'a' || text[0] > 'z') return null;
        foreach (var c in text) if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-' || c == '.')) return null;
        return text;
    }
}
