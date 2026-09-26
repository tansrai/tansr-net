using System.IO;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Mcp;
#endif

namespace Tansr.Examples;

/// <summary>显式宿主白名单的原生 stdio/HTTP MCP 桥；发现元数据不生成授权或可执行程序。</summary>
internal sealed class NativeMcpToolConnection
{
    private static readonly JsonElement Declaration = Parse("{\"name\":\"mcp_echo\",\"description\":\"调用宿主明确配置的原生 MCP 示例 echo，原样返回不超过4096字符的文字；不访问文件或网络。\",\"parameters\":{\"text\":{\"type\":\"string\",\"description\":\"待回显文字，最多4096字符\"}},\"readOnly\":true,\"timeoutMs\":10000}");
    private readonly object _gate = new();
    private Task? _closing;
    internal IReadOnlyList<NativeToolBinding> Bindings { get; }
#if WINDOWS || NETFRAMEWORK
    private readonly McpClient _client;
    private readonly WindowsWorkspace? _workspace;
    private NativeMcpToolConnection(McpClient client, WindowsWorkspace? workspace, IReadOnlyList<NativeToolBinding> bindings)
    { _client = client; _workspace = workspace; Bindings = bindings; }
#else
    private NativeMcpToolConnection() { Bindings = Array.Empty<NativeToolBinding>(); }
#endif
    internal static async Task<NativeMcpToolConnection?> OpenConfiguredAsync(CancellationToken ct)
    {
        var executable = Environment.GetEnvironmentVariable("TANSR_MCP_EXE");
        var endpoint = Environment.GetEnvironmentVariable("TANSR_MCP_HTTP_URL");
        if (string.IsNullOrWhiteSpace(executable) && string.IsNullOrWhiteSpace(endpoint)) return null;
        if (!string.IsNullOrWhiteSpace(executable) && !string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("native_mcp_choose_one_transport");
#if WINDOWS || NETFRAMEWORK
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TANSR_MCP_DLL"))) throw new InvalidOperationException("native_mcp_agent_bridge_requires_approved_single_file_candidate");
        WindowsWorkspace? workspace = null;
        McpClient? client = null;
        try
        {
            var clientOptions = new McpClientOptions { MaximumResponseBytes = 64 * 1024, RequestTimeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                var uri = new Uri(endpoint!, UriKind.Absolute);
                var options = new McpHttpOptions(uri, new[] { uri }) { AllowLoopbackHttp = Environment.GetEnvironmentVariable("TANSR_MCP_HTTP_ALLOW_LOOPBACK") == "1" };
                var bearer = Environment.GetEnvironmentVariable("TANSR_MCP_HTTP_BEARER");
                if (!string.IsNullOrWhiteSpace(bearer)) options.Headers.Add("Authorization", "Bearer " + bearer);
                client = await McpClient.ConnectHttpAsync(options, clientOptions, ct);
            }
            else
            {
                var digest = Environment.GetEnvironmentVariable("TANSR_MCP_SHA256") ?? throw new InvalidOperationException("TANSR_MCP_SHA256_required");
                var directory = Environment.GetEnvironmentVariable("TANSR_MCP_WORKSPACE") ?? throw new InvalidOperationException("TANSR_MCP_WORKSPACE_required");
                workspace = new WindowsWorkspace(directory);
                var options = new WindowsDuplexProcessOptions(executable!, new[] { "--mcp" }, () => workspace.AcquireProcessDirectory(""))
                { ExpectedExecutableSha256 = digest, MaxLineBytes = 64 * 1024 };
                client = await McpClient.ConnectStdioAsync(options, clientOptions, ct);
            }
            var configured = Environment.GetEnvironmentVariable("TANSR_MCP_TOOLS");
            var declarations = new List<(string Remote, JsonElement Declaration, bool Echo)>();
            if (string.IsNullOrWhiteSpace(configured)) declarations.Add(("echo", Declaration, true));
            else
            {
                var mappings = WireJson.Parse(Encoding.UTF8.GetBytes(configured!), 16384);
                if (mappings.ValueKind != JsonValueKind.Array || mappings.GetArrayLength() < 1 || mappings.GetArrayLength() > 12) throw new InvalidOperationException("native_mcp_invalid_allowlist");
                foreach (var mapping in mappings.EnumerateArray())
                {
                    var local = Text(mapping, "name"); var remote = Text(mapping, "remoteName");
                    if (local == null || !System.Text.RegularExpressions.Regex.IsMatch(local, "\\A[A-Za-z][A-Za-z0-9_]{0,63}\\z") || remote == null ||
                        !System.Text.RegularExpressions.Regex.IsMatch(remote, "\\A[A-Za-z0-9_.-]{1,128}\\z")) throw new InvalidOperationException("native_mcp_invalid_allowlist");
                    declarations.Add((remote, BuildDeclaration(local, remote), false));
                }
                if (declarations.Select(item => Text(item.Declaration, "name")).Distinct(StringComparer.Ordinal).Count() != declarations.Count) throw new InvalidOperationException("native_mcp_duplicate_binding");
            }
            var tools = await client.ListToolsAsync(declarations.Select(item => item.Remote), ct);
            var bindings = new List<NativeToolBinding>();
            foreach (var item in declarations)
            {
                var definition = tools.SingleOrDefault(tool => Text(tool, "name") == item.Remote);
                if (definition.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("native_mcp_allowed_tool_unavailable");
                var declaration = item.Echo ? item.Declaration : BuildDeclaration(Text(item.Declaration, "name")!, item.Remote, definition.GetProperty("inputSchema"));
                var digest = WireJson.DomainDigest("tansr.sdk2.client-tool.v1", WireJson.EncodeControl(declaration));
                var adapter = McpToolAdapter.CreateTools(client, new[] { new McpToolBinding(Text(declaration, "name")!, item.Remote, digest, definition) })[0];
                bindings.Add(new NativeToolBinding(declaration, async (arguments, token) =>
                {
                    if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1) throw new InvalidOperationException("native_mcp_invalid_arguments");
                    JsonElement input = arguments;
                    if (item.Echo)
                    { if (Text(arguments, "text") is not { Length: <= 4096 }) throw new InvalidOperationException("native_mcp_invalid_arguments"); }
                    else
                    {
                        var text = Text(arguments, "argumentsJson") ?? throw new InvalidOperationException("native_mcp_invalid_arguments");
                        input = WireJson.Parse(Encoding.UTF8.GetBytes(text), 32768);
                        if (input.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("native_mcp_invalid_arguments");
                    }
                    try { return await adapter.Invoke(input, token); }
                    catch (McpException error) { throw new InvalidOperationException("native_mcp_" + error.Code); }
                }));
            }
            return new NativeMcpToolConnection(client, workspace, bindings.AsReadOnly());
        }
        catch
        {
            try { if (client != null) await client.CloseAsync(); }
            finally { workspace?.Dispose(); }
            throw;
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("原生MCP智能体工具需要Windows目标；未启用该配置时跨平台会话仍可使用。");
#endif
    }
#if WINDOWS || NETFRAMEWORK
    private static JsonElement BuildDeclaration(string local, string remote, JsonElement? schema = null)
    {
        var schemaText = schema?.GetRawText() ?? "{}";
        if (schemaText.Length > 1800) throw new InvalidOperationException("native_mcp_schema_too_large_for_example");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("name", local);
            writer.WriteString("description", "调用宿主显式允许的 MCP 工具 " + remote + "。argumentsJson 必须是符合此远端 JSON Schema 的对象字符串；发现信息不是执行授权：" + schemaText);
            writer.WriteBoolean("readOnly", false); writer.WriteNumber("timeoutMs", 10000);
            writer.WriteStartObject("parameters"); writer.WriteStartObject("argumentsJson"); writer.WriteString("type", "string");
            writer.WriteString("description", "远端工具参数的 JSON 对象字符串"); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
#endif
    internal Task CloseAsync() { lock (_gate) return _closing ??= CloseCoreAsync(); }
    internal Task RevokeAsync() => CloseAsync();
    private async Task CloseCoreAsync()
    {
#if WINDOWS || NETFRAMEWORK
        try { await _client.CloseAsync(); }
        finally { _workspace?.Dispose(); }
#else
        await Task.CompletedTask;
#endif
    }
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
