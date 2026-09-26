using System.IO;
using System.Text;
using System.Text.Json;

namespace Tansr.Examples;

/// <summary>纯 C# stdio MCP 示例；stdout 只输出一行一帧 JSON-RPC，不装 Node 或调用 shell。</summary>
internal sealed class NativeMcpServer
{
    private const int MaximumLineBytes = 64 * 1024;
    private bool _initializeAccepted, _initialized;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal async Task RunAsync(Stream input, Stream output, CancellationToken token)
    {
        var buffer = new byte[4096]; using var line = new MemoryStream();
        while (true)
        {
            var count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == 10)
                {
                    if (line.Length > 0)
                    {
                        var bytes = line.ToArray(); line.SetLength(0);
                        if (bytes[bytes.Length - 1] == 13) Array.Resize(ref bytes, bytes.Length - 1);
                        await DispatchAsync(bytes, output, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (line.Length == MaximumLineBytes) throw new InvalidOperationException("mcp_request_too_large");
                    line.WriteByte(buffer[index]);
                }
            }
        }
        if (line.Length != 0) throw new InvalidOperationException("mcp_truncated_message");
    }

    private async Task DispatchAsync(byte[] bytes, Stream output, CancellationToken token)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(Utf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (Exception error) when (error is JsonException || error is DecoderFallbackException)
        { await ErrorAsync(output, null, -32700, "parse_error", token).ConfigureAwait(false); return; }
        using (document)
        {
            var request = document.RootElement;
            JsonElement? id = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var requestId) ? requestId.Clone() : null;
            if (request.ValueKind != JsonValueKind.Object || Text(request, "jsonrpc") != "2.0" || Text(request, "method") is not { } method ||
                request.EnumerateObject().GroupBy(x => x.Name).Any(x => x.Count() > 1) ||
                id.HasValue && id.Value.ValueKind != JsonValueKind.String && id.Value.ValueKind != JsonValueKind.Number && id.Value.ValueKind != JsonValueKind.Null)
            { await ErrorAsync(output, id, -32600, "invalid_request", token).ConfigureAwait(false); return; }
            if (method == "notifications/initialized") { if (_initializeAccepted && !id.HasValue) _initialized = true; return; }
            if (method == "notifications/cancelled" || method == "notifications/progress") return;
            if (!id.HasValue) return;
            var parameters = request.TryGetProperty("params", out var p) ? p : default;
            if (method == "initialize")
            {
                if (_initializeAccepted || Text(parameters, "protocolVersion") is not { } version ||
                    !parameters.TryGetProperty("clientInfo", out var clientInfo) || clientInfo.ValueKind != JsonValueKind.Object)
                { await ErrorAsync(output, id, -32602, "invalid_initialize", token).ConfigureAwait(false); return; }
                var selected = version is "2024-11-05" or "2025-03-26" or "2025-06-18" ? version : "2025-03-26";
                await ResultAsync(output, id.Value, writer =>
                {
                    writer.WriteStartObject(); writer.WriteString("protocolVersion", selected);
                    writer.WriteStartObject("capabilities"); writer.WriteStartObject("tools"); writer.WriteBoolean("listChanged", false); writer.WriteEndObject(); writer.WriteEndObject();
                    writer.WriteStartObject("serverInfo"); writer.WriteString("name", "tansr-native-example"); writer.WriteString("version", "0.1.0"); writer.WriteEndObject(); writer.WriteEndObject();
                }, token).ConfigureAwait(false); _initializeAccepted = true; return;
            }
            if (!_initialized) { await ErrorAsync(output, id, -32002, "server_not_initialized", token).ConfigureAwait(false); return; }
            if (method == "ping") { await ResultAsync(output, id.Value, w => { w.WriteStartObject(); w.WriteEndObject(); }, token).ConfigureAwait(false); return; }
            if (method == "tools/list")
            {
                await ResultAsync(output, id.Value, writer =>
                {
                    writer.WriteStartObject(); writer.WriteStartArray("tools");
                    Definition(writer, "application_info", "读取当前原生 MCP 示例的应用与运行环境", false);
                    Definition(writer, "echo", "原样返回调用者提供的文本；不执行代码或访问文件", true);
                    writer.WriteEndArray(); writer.WriteEndObject();
                }, token).ConfigureAwait(false); return;
            }
            if (method == "tools/call")
            {
                var name = Text(parameters, "name"); var args = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var a) ? a : default;
                if (name is not ("application_info" or "echo")) { await ErrorAsync(output, id, -32602, "unknown_tool", token).ConfigureAwait(false); return; }
                if (name == "echo" && (args.ValueKind != JsonValueKind.Object || Text(args, "text") is not { Length: <= 4096 } || args.EnumerateObject().Any(x => x.Name != "text")) ||
                    name == "application_info" && args.ValueKind != JsonValueKind.Undefined && (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any()))
                { await ErrorAsync(output, id, -32602, "invalid_tool_arguments", token).ConfigureAwait(false); return; }
                var text = name == "echo" ? Text(args, "text")! : "Tansr native MCP; runtime=" + Environment.Version + "; os=" + Environment.OSVersion.Platform;
                await ResultAsync(output, id.Value, writer =>
                { writer.WriteStartObject(); writer.WriteStartArray("content"); writer.WriteStartObject(); writer.WriteString("type", "text"); writer.WriteString("text", text); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteBoolean("isError", false); writer.WriteEndObject(); }, token).ConfigureAwait(false);
                return;
            }
            await ErrorAsync(output, id, -32601, "method_not_found", token).ConfigureAwait(false);
        }
    }
    private static void Definition(Utf8JsonWriter writer, string name, string description, bool textArgument)
    {
        writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteString("description", description);
        writer.WriteStartObject("inputSchema"); writer.WriteString("type", "object"); writer.WriteBoolean("additionalProperties", false); writer.WriteStartObject("properties");
        if (textArgument) { writer.WriteStartObject("text"); writer.WriteString("type", "string"); writer.WriteNumber("maxLength", 4096); writer.WriteEndObject(); }
        writer.WriteEndObject(); if (textArgument) { writer.WriteStartArray("required"); writer.WriteStringValue("text"); writer.WriteEndArray(); }
        writer.WriteEndObject(); writer.WriteEndObject();
    }
    private static Task ResultAsync(Stream output, JsonElement id, Action<Utf8JsonWriter> write, CancellationToken token) => FrameAsync(output, writer =>
    { writer.WritePropertyName("id"); id.WriteTo(writer); writer.WritePropertyName("result"); write(writer); }, token);
    private static Task ErrorAsync(Stream output, JsonElement? id, int code, string message, CancellationToken token) => FrameAsync(output, writer =>
    { writer.WritePropertyName("id"); if (id.HasValue) id.Value.WriteTo(writer); else writer.WriteNullValue(); writer.WriteStartObject("error"); writer.WriteNumber("code", code); writer.WriteString("message", message); writer.WriteEndObject(); }, token);
    private static async Task FrameAsync(Stream output, Action<Utf8JsonWriter> write, CancellationToken token)
    {
        using var memory = new MemoryStream(); using (var writer = new Utf8JsonWriter(memory)) { writer.WriteStartObject(); writer.WriteString("jsonrpc", "2.0"); write(writer); writer.WriteEndObject(); }
        memory.WriteByte(10); var bytes = memory.ToArray(); await output.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
    }
    private static string? Text(JsonElement data, string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
