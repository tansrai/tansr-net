using System.Diagnostics;
using System.Text;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Mcp;
using Xunit;

namespace Tansr.Sdk.Windows.Tests.Mcp;

public sealed class McpStdioTests : IDisposable, IClassFixture<McpNativeFixture>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tansr-native-mcp-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsWorkspace _workspace;
    private readonly McpNativeFixture _fixture;
    public McpStdioTests(McpNativeFixture fixture) { Directory.CreateDirectory(_directory); _workspace = new WindowsWorkspace(_directory); _fixture = fixture; }

    [Fact]
    public async Task PureFrameworkServerHandlesHandshakeMultipleRequestsAndUnicodeWithoutNode()
    {
        using var client = await McpClient.ConnectStdioAsync(Options());
        Assert.Equal(McpConnectionState.Ready, client.State);
        Assert.Equal("2025-03-26", client.ProtocolVersion);
        var tools = await client.ListToolsAsync(new[] { "echo" }); Assert.Single(tools);
        var binding = Assert.Single(McpToolAdapter.CreateTools(client, new[] { new McpToolBinding("echo-local", "echo", new string('a', 64)) }));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(number => binding.Invoke(WireJson.Parse(Encoding.UTF8.GetBytes("{\"text\":\"中文🙂" + number + "\"}")), CancellationToken.None)));
        for (int i = 0; i < results.Length; i++) Assert.Equal("中文🙂" + i, results[i].GetProperty("content")[0].GetProperty("text").GetString());
        await client.CloseAsync(); Assert.Equal(McpConnectionState.Closed, client.State);
    }

    [Fact]
    public async Task MalformedPeerOutputClosesConnectionAndRejectsPendingCalls()
    {
        using var client = await McpClient.ConnectStdioAsync(Options());
        var error = await Assert.ThrowsAsync<McpException>(() => client.RequestAsync("malformed"));
        Assert.Equal("invalid_message", error.Code);
        Assert.Equal(McpConnectionState.Closed, client.State);
        await client.CloseAsync();
    }

    [Fact]
    public async Task TimedOutRequestCanReceiveLateResultWithoutSatisfyingNextRequest()
    {
        using var client = await McpClient.ConnectStdioAsync(Options());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync("delay", timeout: TimeSpan.FromMilliseconds(30)));
        var result = await client.RequestAsync("ping");
        Assert.Equal("ping", result.GetProperty("method").GetString());
    }

    private WindowsDuplexProcessOptions Options()
    {
        var options = new WindowsDuplexProcessOptions(_fixture.Executable, Array.Empty<string>(), () => _workspace.AcquireProcessDirectory());
        options.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows); return options;
    }
    public void Dispose() { _workspace.Dispose(); Directory.Delete(_directory, true); }
}
