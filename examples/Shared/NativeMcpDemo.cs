using System.Text.Json;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Mcp;
#endif

namespace Tansr.Examples;

internal static class NativeMcpDemo
{
    // 受信宿主路径和工作区来自操作员配置，不接受模型给出的可执行程序或参数。
    internal static async Task<JsonElement> RunAsync(CancellationToken ct)
    {
#if WINDOWS || NETFRAMEWORK
        var executable = Environment.GetEnvironmentVariable("TANSR_MCP_EXE") ?? throw new InvalidOperationException("TANSR_MCP_EXE_required");
        var directory = Environment.GetEnvironmentVariable("TANSR_MCP_WORKSPACE") ?? throw new InvalidOperationException("TANSR_MCP_WORKSPACE_required");
        var assembly = Environment.GetEnvironmentVariable("TANSR_MCP_DLL");
        using var workspace = new WindowsWorkspace(directory);
        var options = new WindowsDuplexProcessOptions(executable, string.IsNullOrEmpty(assembly) ? new[] { "--mcp" } : new[] { assembly!, "--mcp" }, () => workspace.AcquireProcessDirectory(""));
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT"); if (dotnetRoot != null) options.Environment.Add("DOTNET_ROOT", dotnetRoot);
        var client = await McpClient.ConnectStdioAsync(options, cancellationToken: ct);
        try
        {
            await client.ListToolsAsync(new[] { "application_info", "echo" }, ct);
            using var parameters = JsonDocument.Parse("{\"name\":\"echo\",\"arguments\":{\"text\":\"C# 原生 MCP 往返\"}}");
            return await client.RequestAsync("tools/call", parameters.RootElement, cancellationToken: ct);
        }
        finally { await client.CloseAsync(); client.Dispose(); }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("stdio MCP 客户端进程宿主需要 net10.0-windows；--mcp 服务本身是跨平台纯 C#。");
#endif
    }
}
