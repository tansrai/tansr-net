using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Hosting;

namespace Tansr.Sdk.Windows.Mcp;

public sealed class McpToolBinding
{
    /// <summary>固定经宿主批准的 tools/list 定义；每次执行前重读并核对，变更必须重新批准并重新绑定。</summary>
    public McpToolBinding(string name, string remoteName, string definitionDigest, JsonElement approvedRemoteDefinition)
        : this(name, remoteName, definitionDigest)
    {
        if (approvedRemoteDefinition.ValueKind != JsonValueKind.Object ||
            !approvedRemoteDefinition.TryGetProperty("name", out var remote) || remote.ValueKind != JsonValueKind.String || remote.GetString() != remoteName ||
            !approvedRemoteDefinition.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid approved MCP definition.", nameof(approvedRemoteDefinition));
        RemoteDefinitionDigest = Digest(approvedRemoteDefinition);
    }
    public McpToolBinding(string name, string remoteName, string definitionDigest)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || string.IsNullOrWhiteSpace(remoteName) || remoteName.Length > 128) throw new ArgumentException("Invalid MCP tool binding.");
        if (definitionDigest == null || definitionDigest.Length != 64 || definitionDigest.Any(x => (x < '0' || x > '9') && (x < 'a' || x > 'f'))) throw new ArgumentException("Invalid tool definition digest.");
        Name = name; RemoteName = remoteName; DefinitionDigest = definitionDigest;
    }
    public string Name { get; }
    public string RemoteName { get; }
    public string DefinitionDigest { get; }
    /// <summary>远端完整发现定义的本地批准摘要，与 Serve tool.invoke 定义摘要分开。</summary>
    public string? RemoteDefinitionDigest { get; }
    internal static string Digest(JsonElement definition)
    {
        // MCP JSON Schema permits fractional bounds. The SDK2 control codec intentionally
        // forbids them, so only this local trust snapshot uses sorted business JSON.
        _ = WireJson.Parse(Encoding.UTF8.GetBytes(definition.GetRawText()), 32768);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteSorted(writer, definition);
        return WireJson.Sha256(buffer.ToArray());
    }
    private static void WriteSorted(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteSorted(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteSorted(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}

/// <summary>复用 tool.invoke；固定连接/远端名/摘要，不允许参数选择连接、命令或环境。</summary>
public static class McpToolAdapter
{
    public static IReadOnlyList<WindowsBusinessTool> CreateTools(IMcpConnection connection, IEnumerable<McpToolBinding> bindings,
        Func<JsonElement, McpToolBinding, CancellationToken, Task<JsonElement>>? mapResult = null)
    {
        if (connection == null) throw new ArgumentNullException(nameof(connection));
        var snapshot = bindings?.Take(65).ToArray() ?? throw new ArgumentNullException(nameof(bindings));
        if (snapshot.Length > 64 || snapshot.Any(x => x == null) || snapshot.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Invalid MCP bindings.");
        return Array.AsReadOnly(snapshot.Select(binding => new WindowsBusinessTool(binding.Name, binding.DefinitionDigest, async (arguments, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.State != McpConnectionState.Ready) throw new McpException("not_ready");
            if (arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 32768) throw new McpException("invalid_arguments");
            if (binding.RemoteDefinitionDigest != null) await VerifyDefinitionAsync(connection, binding, token).ConfigureAwait(false);
            var parameters = McpJson.Object(writer =>
            { writer.WriteString("name", binding.RemoteName); writer.WritePropertyName("arguments"); arguments.WriteTo(writer); });
            var raw = await connection.RequestAsync("tools/call", parameters, TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
            JsonElement result = mapResult == null ? MapResult(raw) : await mapResult(raw, binding, token).ConfigureAwait(false);
            ValidateReceipt(result);
            return result.Clone();
        })).ToArray());
    }

    private static async Task VerifyDefinitionAsync(IMcpConnection connection, McpToolBinding binding, CancellationToken token)
    {
        string? cursor = null; var cursors = new HashSet<string>(StringComparer.Ordinal); var matched = false;
        for (int page = 0; page < 32; page++)
        {
            var response = await connection.RequestAsync("tools/list", cursor == null ? null : McpJson.Object(writer => writer.WriteString("cursor", cursor)), cancellationToken: token).ConfigureAwait(false);
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) throw new McpException("invalid_tools");
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) throw new McpException("invalid_tools");
                if (name.GetString() != binding.RemoteName) continue;
                if (matched || McpToolBinding.Digest(tool) != binding.RemoteDefinitionDigest) throw new McpException("definition_changed");
                matched = true;
            }
            if (!response.TryGetProperty("nextCursor", out var next))
            { if (!matched) throw new McpException("definition_changed"); return; }
            if (next.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cursor = next.GetString()) || cursor!.Length > 1024 || !cursors.Add(cursor)) throw new McpException("invalid_cursor");
        }
        throw new McpException("discovery_limit");
    }

    /// <summary>
    /// 沿用内核 MCP 桥的文本、图片、资源和音频元信息映射。原 ToolResultReceipt 只有文本/图片，
    /// 因此 structuredContent 作为明确标注的 JSON 文本保留；不会抓取资源链接或执行资源内容。
    /// 超过终端回执预算的单图使用元信息，原始完整结果仍可通过 CreateTools 的 mapResult 回调取得。
    /// </summary>
    public static JsonElement MapResult(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw new McpException("invalid_result");
        var hasContent = raw.TryGetProperty("content", out var content);
        if (hasContent && (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() > 64)) throw new McpException("unsupported_content");
        var hasStructured = raw.TryGetProperty("structuredContent", out var structured);
        if (hasStructured && structured.ValueKind != JsonValueKind.Object) throw new McpException("invalid_result");
        int count = hasContent ? content.GetArrayLength() : 0;
        if (hasStructured && count == 64) throw new McpException("response_limit");
        var result = McpJson.Object(writer =>
        {
            writer.WriteString("status", "ok");
            if (raw.TryGetProperty("isError", out var isError))
            {
                if (isError.ValueKind != JsonValueKind.True && isError.ValueKind != JsonValueKind.False) throw new McpException("invalid_result");
                writer.WriteBoolean("isError", isError.GetBoolean());
            }
            writer.WritePropertyName("content"); writer.WriteStartArray();
            if (hasContent) foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new McpException("invalid_result");
                switch (type.GetString())
                {
                    case "text":
                        if (!item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new McpException("invalid_result");
                        WriteText(writer, text.GetString()!); break;
                    case "image":
                        if (!item.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String ||
                            !item.TryGetProperty("data", out var bytes) || bytes.ValueKind != JsonValueKind.String) throw new McpException("unsupported_content");
                        WriteImage(writer, mime.GetString()!, bytes.GetString()!); break;
                    case "audio":
                        WriteText(writer, "[audio content (" + String(item, "mimeType", "unknown") + "), " + String(item, "data", "").Length + " base64 chars omitted]"); break;
                    case "resource_link":
                        if (!item.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String) throw new McpException("invalid_result");
                        string name = String(item, "name", ""), description = String(item, "description", "");
                        WriteText(writer, "[resource link]" + (name.Length == 0 ? "" : " " + name) + " " + uri.GetString() + (description.Length == 0 ? "" : " — " + description)); break;
                    case "resource":
                        if (!item.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object) throw new McpException("invalid_result");
                        string resourceUri = String(resource, "uri", "(unknown uri)");
                        if (resource.TryGetProperty("text", out var resourceText) && resourceText.ValueKind == JsonValueKind.String)
                            WriteText(writer, "[resource " + resourceUri + "]\n" + resourceText.GetString());
                        else if (resource.TryGetProperty("blob", out var blob) && blob.ValueKind == JsonValueKind.String)
                        {
                            string resourceMime = String(resource, "mimeType", "unknown media type");
                            if (resourceMime.StartsWith("image/", StringComparison.Ordinal)) WriteImage(writer, resourceMime, blob.GetString()!);
                            else WriteText(writer, "[binary resource " + resourceUri + " (" + resourceMime + "), base64 body omitted]");
                        }
                        else WriteText(writer, "[resource " + resourceUri + "]");
                        break;
                    default: WriteText(writer, "[unsupported MCP content type \"" + type.GetString() + "\"]"); break;
                }
            }
            if (hasStructured) WriteText(writer, "[MCP structuredContent]\n" + structured.GetRawText());
            else if (count == 0) WriteText(writer, "(empty result)");
            writer.WriteEndArray();
        });
        ValidateReceipt(result); return result;
    }

    private static string String(JsonElement value, string name, string fallback)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : fallback;
    private static void WriteText(Utf8JsonWriter writer, string value)
    { writer.WriteStartObject(); writer.WriteString("t", "text"); writer.WriteString("text", value); writer.WriteEndObject(); }
    private static void WriteImage(Utf8JsonWriter writer, string mime, string data)
    {
        if (!ImageMime(mime) || Encoding.UTF8.GetByteCount(data) > 32768)
        { WriteText(writer, "[image content (" + mime + "), " + data.Length + " base64 chars omitted: unsupported image type or terminal receipt budget exceeded]"); return; }
        writer.WriteStartObject(); writer.WriteString("t", "image"); writer.WriteString("mime", mime); writer.WriteString("data", data); writer.WriteEndObject();
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
