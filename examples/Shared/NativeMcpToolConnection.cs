using System.IO;
using System.Text.Json;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Mcp;
#endif

namespace Tansr.Examples;

/// <summary>固定受信原生MCP echo的SDK1桥；发现元数据不生成权限、声明或可执行程序。</summary>
internal sealed class NativeMcpToolConnection
{
    private static readonly JsonElement Declaration = Parse("{\"name\":\"mcp_echo\",\"description\":\"调用宿主明确配置的原生 MCP 示例 echo，原样返回不超过4096字符的文字；不访问文件或网络。\",\"parameters\":{\"text\":{\"type\":\"string\",\"description\":\"待回显文字，最多4096字符\"}},\"readOnly\":true,\"timeoutMs\":10000}");
    private readonly object _gate = new();
    private Task? _closing;
    internal IReadOnlyList<NativeToolBinding> Bindings { get; }
#if WINDOWS || NETFRAMEWORK
    private readonly McpClient _client;
    private readonly WindowsWorkspace _workspace;
    private NativeMcpToolConnection(McpClient client, WindowsWorkspace workspace)
    { _client = client; _workspace = workspace; Bindings = new[] { new NativeToolBinding(Declaration, InvokeAsync) }; }
#else
    private NativeMcpToolConnection() { Bindings = Array.Empty<NativeToolBinding>(); }
#endif
    internal static async Task<NativeMcpToolConnection?> OpenConfiguredAsync(CancellationToken ct)
    {
        var executable = Environment.GetEnvironmentVariable("TANSR_MCP_EXE");
        if (string.IsNullOrWhiteSpace(executable)) return null;
#if WINDOWS || NETFRAMEWORK
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TANSR_MCP_DLL"))) throw new InvalidOperationException("native_mcp_agent_bridge_requires_approved_single_file_candidate");
        var digest = Environment.GetEnvironmentVariable("TANSR_MCP_SHA256") ?? throw new InvalidOperationException("TANSR_MCP_SHA256_required");
        var directory = Environment.GetEnvironmentVariable("TANSR_MCP_WORKSPACE") ?? throw new InvalidOperationException("TANSR_MCP_WORKSPACE_required");
        var workspace = new WindowsWorkspace(directory);
        McpClient? client = null;
        try
        {
            var options = new WindowsDuplexProcessOptions(executable!, new[] { "--mcp" }, () => workspace.AcquireProcessDirectory(""))
            { ExpectedExecutableSha256 = digest, MaxLineBytes = 64 * 1024 };
            client = await McpClient.ConnectStdioAsync(options, new McpClientOptions { MaximumResponseBytes = 64 * 1024, RequestTimeout = TimeSpan.FromSeconds(10) }, ct);
            // 只确认固定工具存在；不接受发现结果中的路径、额外工具、参数模式或readOnly声明。
            var tools = await client.ListToolsAsync(new[] { "echo" }, ct);
            if (!tools.Any(x => Text(x, "name") == "echo")) throw new InvalidOperationException("native_mcp_echo_unavailable");
            return new NativeMcpToolConnection(client, workspace);
        }
        catch
        {
            try { if (client != null) await client.CloseAsync(); }
            finally { workspace.Dispose(); }
            throw;
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("原生MCP智能体工具需要Windows目标；未启用该配置时跨平台会话仍可使用。");
#endif
    }
#if WINDOWS || NETFRAMEWORK
    private async Task<JsonElement> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1 ||
            Text(arguments, "text") is not { Length: <= 4096 } text) throw new InvalidOperationException("native_mcp_invalid_arguments");
        ct.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("name", "echo"); writer.WriteStartObject("arguments");
            writer.WriteString("text", text); writer.WriteEndObject(); writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        try
        {
            var response = await _client.RequestAsync("tools/call", document.RootElement, TimeSpan.FromSeconds(10), ct);
            return McpToolAdapter.MapResult(response);
        }
        catch (McpException error) { throw new InvalidOperationException("native_mcp_" + error.Code); }
    }
#endif
    internal Task CloseAsync() { lock (_gate) return _closing ??= CloseCoreAsync(); }
    private async Task CloseCoreAsync()
    {
#if WINDOWS || NETFRAMEWORK
        try { await _client.CloseAsync(); }
        finally { _workspace.Dispose(); }
#else
        await Task.CompletedTask;
#endif
    }
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
