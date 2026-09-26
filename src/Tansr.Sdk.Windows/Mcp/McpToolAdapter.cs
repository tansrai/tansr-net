using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Hosting;

namespace Tansr.Sdk.Windows.Mcp;

public sealed class McpToolBinding
{
    public McpToolBinding(string name, string remoteName, string definitionDigest)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || string.IsNullOrWhiteSpace(remoteName) || remoteName.Length > 128) throw new ArgumentException("Invalid MCP tool binding.");
        if (definitionDigest == null || definitionDigest.Length != 64 || definitionDigest.Any(x => (x < '0' || x > '9') && (x < 'a' || x > 'f'))) throw new ArgumentException("Invalid tool definition digest.");
        Name = name; RemoteName = remoteName; DefinitionDigest = definitionDigest;
    }
    public string Name { get; }
    public string RemoteName { get; }
    public string DefinitionDigest { get; }
}

/// <summary>复用 tool.invoke；固定连接/远端名/摘要，不允许参数选择连接、命令或环境。</summary>
public static class McpToolAdapter
{
    public static IReadOnlyList<WindowsBusinessTool> CreateTools(IMcpConnection connection, IEnumerable<McpToolBinding> bindings,
        Func<JsonElement, McpToolBinding, CancellationToken, Task<JsonElement>>? mapResult = null)
    {
        if (connection == null) throw new ArgumentNullException(nameof(connection));
        var snapshot = bindings?.ToArray() ?? throw new ArgumentNullException(nameof(bindings));
        if (snapshot.Length > 64 || snapshot.Any(x => x == null) || snapshot.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Invalid MCP bindings.");
        return Array.AsReadOnly(snapshot.Select(binding => new WindowsBusinessTool(binding.Name, binding.DefinitionDigest, async (arguments, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.State != McpConnectionState.Ready) throw new McpException("not_ready");
            if (arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 32768) throw new McpException("invalid_arguments");
            var parameters = McpJson.Object(writer =>
            { writer.WriteString("name", binding.RemoteName); writer.WritePropertyName("arguments"); arguments.WriteTo(writer); });
            var raw = await connection.RequestAsync("tools/call", parameters, TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
            JsonElement result = mapResult == null ? MapResult(raw) : await mapResult(raw, binding, token).ConfigureAwait(false);
            ValidateReceipt(result);
            return result.Clone();
        })).ToArray());
    }

    /// <summary>原 ToolResultReceipt 仅接受文本/图片；其他 MCP 内容须由宿主显式映射，不能悄悄丢弃。</summary>
    public static JsonElement MapResult(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() < 1 || content.GetArrayLength() > 64)
            throw new McpException("unsupported_content");
        var result = McpJson.Object(writer =>
        {
            writer.WriteString("status", "ok");
            if (raw.TryGetProperty("isError", out var isError))
            {
                if (isError.ValueKind != JsonValueKind.True && isError.ValueKind != JsonValueKind.False) throw new McpException("invalid_result");
                writer.WriteBoolean("isError", isError.GetBoolean());
            }
            writer.WritePropertyName("content"); writer.WriteStartArray();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new McpException("invalid_result");
                writer.WriteStartObject();
                switch (type.GetString())
                {
                    case "text":
                        if (!item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new McpException("invalid_result");
                        writer.WriteString("t", "text"); writer.WriteString("text", text.GetString()); break;
                    case "image":
                        if (!item.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String || !ImageMime(mime.GetString()) ||
                            !item.TryGetProperty("data", out var bytes) || bytes.ValueKind != JsonValueKind.String) throw new McpException("unsupported_content");
                        writer.WriteString("t", "image"); writer.WriteString("mime", mime.GetString()); writer.WriteString("data", bytes.GetString()); break;
                    default: throw new McpException("unsupported_content");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        ValidateReceipt(result); return result;
    }

    private static void ValidateReceipt(JsonElement result)
    {
        _ = WireJson.Parse(Encoding.UTF8.GetBytes(result.GetRawText()), 32768);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String) throw new McpException("invalid_result");
        if (status.GetString() == "error")
        {
            if (!result.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || message.GetString()!.Length < 1 || message.GetString()!.Length > 4096) throw new McpException("invalid_result");
            return;
        }
        if (status.GetString() != "ok" || !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() < 1 || content.GetArrayLength() > 64 ||
            (result.TryGetProperty("isError", out var error) && error.ValueKind != JsonValueKind.True && error.ValueKind != JsonValueKind.False)) throw new McpException("invalid_result");
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("t", out var type) || type.ValueKind != JsonValueKind.String) throw new McpException("invalid_result");
            if (type.GetString() == "text")
            { if (!item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new McpException("invalid_result"); }
            else if (type.GetString() != "image" || !item.TryGetProperty("mime", out var mime) || mime.ValueKind != JsonValueKind.String || !ImageMime(mime.GetString()) ||
                !item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) throw new McpException("invalid_result");
        }
    }
    private static bool ImageMime(string? mime) => mime == "image/png" || mime == "image/jpeg" || mime == "image/webp" || mime == "image/gif";
}
