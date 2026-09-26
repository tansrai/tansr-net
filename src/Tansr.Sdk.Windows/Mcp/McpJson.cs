using System.Globalization;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Windows.Mcp;

internal static class McpJson
{
    internal static JsonElement Object(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return WireJson.Parse(stream.ToArray(), 16 * 1024 * 1024);
    }

    internal static JsonElement Message(string? id, string method, JsonElement? parameters) => Object(writer =>
    {
        writer.WriteString("jsonrpc", "2.0");
        if (id != null) writer.WriteString("id", id);
        writer.WriteString("method", method);
        if (parameters.HasValue) { writer.WritePropertyName("params"); parameters.Value.WriteTo(writer); }
    });

    internal static JsonElement Parse(string text, int maximumBytes)
    {
        try { return WireJson.Parse(new UTF8Encoding(false, true).GetBytes(text), maximumBytes, 48); }
        catch (Exception error) when (error is WireProtocolException || error is EncoderFallbackException) { throw new McpException("invalid_message"); }
    }

    internal static string Id(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id)) throw new McpException("missing_id");
        if (id.ValueKind == JsonValueKind.String && id.GetString()!.Length <= 128) return "s:" + id.GetString();
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value)) return "n:" + value.ToString(CultureInfo.InvariantCulture);
        throw new McpException("invalid_id");
    }

    internal static void Validate(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0")
            throw new McpException("invalid_message");
        bool method = message.TryGetProperty("method", out var methodValue);
        if (method)
        {
            if (methodValue.ValueKind != JsonValueKind.String || methodValue.GetString()!.Length == 0 || methodValue.GetString()!.Length > 256 ||
                message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)) throw new McpException("invalid_message");
            if (message.TryGetProperty("id", out _)) _ = Id(message);
        }
        else
        {
            _ = Id(message);
            if (message.TryGetProperty("result", out _) == message.TryGetProperty("error", out _)) throw new McpException("invalid_message");
        }
    }

    internal static JsonElement Result(JsonElement message)
    {
        Validate(message);
        if (message.TryGetProperty("error", out var error))
        {
            if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var code) || !code.TryGetInt32(out _)) throw new McpException("invalid_message");
            throw new McpException("remote_error");
        }
        return message.GetProperty("result").Clone();
    }

    internal static JsonElement ServerReply(JsonElement request) => Object(writer =>
    {
        writer.WriteString("jsonrpc", "2.0"); writer.WritePropertyName("id"); request.GetProperty("id").WriteTo(writer);
        if (request.GetProperty("method").GetString() == "ping") { writer.WritePropertyName("result"); writer.WriteStartObject(); writer.WriteEndObject(); }
        else
        {
            // 未声明 sampling/roots/elicitation；服务端不得借反向请求绕过 Serve 的治理。
            writer.WritePropertyName("error"); writer.WriteStartObject(); writer.WriteNumber("code", -32601);
            writer.WriteString("message", "Client capability is not available."); writer.WriteEndObject();
        }
    });

    internal static IEnumerable<JsonElement> Frames(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Array) { Validate(message); yield return message; yield break; }
        if (message.GetArrayLength() < 1 || message.GetArrayLength() > 64) throw new McpException("invalid_batch");
        foreach (var item in message.EnumerateArray()) { Validate(item); yield return item; }
    }
}
