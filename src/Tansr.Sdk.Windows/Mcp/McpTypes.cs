using System.Text.Json;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Mcp;

public enum McpConnectionState { Connecting, Ready, Closed }

/// <summary>终端 MCP 连接，不授予工具调用权限；应用须通过原 ExecutionHost 执行授权。</summary>
public interface IMcpConnection
{
    McpConnectionState State { get; }
    Task<JsonElement> RequestAsync(string method, JsonElement? parameters = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

public sealed class McpClientOptions
{
    public string ClientName { get; set; } = "tansr-dotnet";
    public string ClientVersion { get; set; } = "0.1.0";
    public string ProtocolVersion { get; set; } = "2025-11-25";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public int MaximumResponseBytes { get; set; } = 1024 * 1024;
    public int MaximumPendingRequests { get; set; } = 16;
    /// <summary>每次请求前复核当前宿主授权；发现工具及服务端 instructions 均不是授权。</summary>
    public Func<string, CancellationToken, Task>? Authorize { get; set; }
}

/// <summary>
/// 固定可信终端端点。内建传输禁止重定向、代理、Cookie 和系统凭据。
/// 此终端适配不替代 Serve 的 DNS 固定出站沙箱；URL/headers 必须由可信宿主配置。
/// </summary>
public sealed class McpHttpOptions
{
    public McpHttpOptions(Uri endpoint, IEnumerable<Uri> allowedEndpoints)
    { Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint)); AllowedEndpoints = allowedEndpoints?.ToArray() ?? throw new ArgumentNullException(nameof(allowedEndpoints)); }
    public Uri Endpoint { get; }
    public IReadOnlyList<Uri> AllowedEndpoints { get; }
    public bool AllowLoopbackHttp { get; set; }
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>每次 POST/DELETE 的授权复核，包括初始化、取消和连接释放。</summary>
    public Func<Uri, string, CancellationToken, Task>? Authorize { get; set; }
}

/// <summary>只报告稳定错误码，不把命令、响应、端点凭据写入异常消息。</summary>
public sealed class McpException : IOException
{
    public McpException(string code) : base("MCP operation failed: " + code) { Code = code; }
    public string Code { get; }
}

internal interface IMcpTransport
{
    bool IsClosed { get; }
    string? ProtocolVersion { get; set; }
    Task<JsonElement> RequestAsync(JsonElement message, CancellationToken cancellationToken);
    Task SendAsync(JsonElement message, CancellationToken cancellationToken);
    Task CloseAsync();
}
