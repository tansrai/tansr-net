using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Mcp;
using Xunit;

namespace Tansr.Sdk.Windows.Tests.Mcp;

public sealed class McpClientTests
{
    [Fact]
    public async Task HttpHandshakeSessionHeadersWhitelistAndCloseUseActualSocket()
    {
        using var server = new Server(); var authorized = new List<string>();
        var options = server.Options(); options.Headers.Add("Authorization", "Bearer synthetic-test-only");
        options.Authorize = (_, method, _) => { authorized.Add(method); return Task.CompletedTask; };
        var client = await McpClient.ConnectHttpAsync(options);
        Assert.Equal(McpConnectionState.Ready, client.State); Assert.Equal("2025-11-25", client.ProtocolVersion);
        var tools = await client.ListToolsAsync(new[] { "allowed" }); Assert.Single(tools);
        Assert.Equal("allowed", tools[0].GetProperty("name").GetString());
        await client.CloseAsync(); Assert.Equal(McpConnectionState.Closed, client.State);
        var requests = server.Requests.ToArray();
        Assert.Equal(4, requests.Length); Assert.Equal("DELETE", requests[3].Method);
        Assert.False(requests[0].Headers.ContainsKey("Mcp-Session-Id"));
        Assert.All(requests.Skip(1), x => Assert.Equal("synthetic-session", x.Headers["Mcp-Session-Id"]));
        Assert.All(requests.Skip(1), x => Assert.Equal("2025-11-25", x.Headers["MCP-Protocol-Version"]));
        Assert.All(requests, x => Assert.Equal("Bearer synthetic-test-only", x.Headers["Authorization"]));
        Assert.Equal(new[] { "POST", "POST", "POST", "DELETE" }, authorized);
    }

    [Fact]
    public async Task SseProcessesPingButRefusesUndeclaredReverseCapability()
    {
        using var server = new Server { Sse = true };
        using var client = await McpClient.ConnectHttpAsync(server.Options());
        var reply = await client.RequestAsync("tools/call", Json("{\"name\":\"allowed\",\"arguments\":{}}"));
        Assert.Equal("hello", reply.GetProperty("content")[0].GetProperty("text").GetString());
        var reverse = server.Requests.Where(x => x.Body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number).ToArray();
        Assert.Equal(2, reverse.Length);
        Assert.True(reverse[0].Body.TryGetProperty("result", out _));
        Assert.Equal(-32601, reverse[1].Body.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task RedirectIsRejectedWithoutCredentialForwarding()
    {
        using var server = new Server { Redirect = true };
        var error = await Assert.ThrowsAsync<McpException>(() => McpClient.ConnectHttpAsync(server.Options()));
        Assert.Equal("redirect_rejected", error.Code); Assert.Single(server.Requests);
    }

    [Fact]
    public async Task DeadlineSendsCancellationAndDoesNotRepeatSideEffect()
    {
        using var server = new Server { StallTool = true };
        using var client = await McpClient.ConnectHttpAsync(server.Options());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync("tools/call", Json("{\"name\":\"allowed\",\"arguments\":{}}"), TimeSpan.FromMilliseconds(180)));
        Assert.Single(server.Requests, x => Method(x.Body) == "tools/call");
        Assert.Single(server.Requests, x => Method(x.Body) == "notifications/cancelled");
    }

    [Fact]
    public async Task ResponseByteLimitAndRequestAuthorizationPreventUnboundedReadsAndCalls()
    {
        using var server = new Server { LargeToolResult = true };
        using var client = await McpClient.ConnectHttpAsync(server.Options(), new McpClientOptions
        {
            MaximumResponseBytes = 1024,
            Authorize = (method, _) => method == "forbidden" ? Task.FromException(new McpException("revoked")) : Task.CompletedTask
        });
        Assert.Equal("response_limit", (await Assert.ThrowsAsync<McpException>(() => client.RequestAsync("tools/call", Json("{}")))).Code);
        Assert.Equal("revoked", (await Assert.ThrowsAsync<McpException>(() => client.RequestAsync("forbidden"))).Code);
        Assert.DoesNotContain(server.Requests, x => Method(x.Body) == "forbidden");
    }

    [Fact]
    public async Task SessionExpiryIsVisibleAndNeverAutoInitializesOrReplaysTool()
    {
        using var server = new Server { ExpireTool = true };
        var client = await McpClient.ConnectHttpAsync(server.Options());
        Assert.Equal("session_expired", (await Assert.ThrowsAsync<McpException>(() => client.RequestAsync("tools/call", Json("{}")))).Code);
        Assert.Equal(McpConnectionState.Closed, client.State);
        Assert.Single(server.Requests, x => Method(x.Body) == "initialize");
        Assert.Single(server.Requests, x => Method(x.Body) == "tools/call");
        await client.CloseAsync();
    }

    [Fact]
    public async Task ToolBindingUsesFixedRemoteNameAndRejectsUnsupportedContent()
    {
        using var server = new Server(); using var client = await McpClient.ConnectHttpAsync(server.Options());
        var binding = new McpToolBinding("local", "allowed", new string('a', 64));
        var tool = Assert.Single(McpToolAdapter.CreateTools(client, new[] { binding }));
        var result = await tool.Invoke(Json("{\"name\":\"injected-other-tool\"}"), CancellationToken.None);
        Assert.Equal("ok", result.GetProperty("status").GetString());
        var request = Assert.Single(server.Requests, x => Method(x.Body) == "tools/call");
        Assert.Equal("allowed", request.Body.GetProperty("params").GetProperty("name").GetString());
        Assert.Equal(binding.DefinitionDigest, tool.DefinitionDigest);
        Assert.Equal("unsupported_content", Assert.Throws<McpException>(() => McpToolAdapter.MapResult(Json("{\"content\":[{\"type\":\"audio\",\"data\":\"not-audio\"}]}"))).Code);
        await client.CloseAsync();
        await Assert.ThrowsAsync<McpException>(() => tool.Invoke(Json("{}"), CancellationToken.None));
    }

    [Fact]
    public async Task EndpointPolicyRejectsBeforeNetwork()
    {
        var uri = new Uri("http://example.invalid/mcp");
        await Assert.ThrowsAsync<ArgumentException>(() => McpClient.ConnectHttpAsync(new McpHttpOptions(uri, new[] { uri }) { AllowLoopbackHttp = true }));
        uri = new Uri("https://example.invalid/mcp");
        await Assert.ThrowsAsync<ArgumentException>(() => McpClient.ConnectHttpAsync(new McpHttpOptions(uri, Array.Empty<Uri>())));
        var headers = new McpHttpOptions(uri, new[] { uri }); headers.Headers.Add("Host", "other.invalid");
        await Assert.ThrowsAsync<ArgumentException>(() => McpClient.ConnectHttpAsync(headers));
    }

    private static JsonElement Json(string text) => WireJson.Parse(Encoding.UTF8.GetBytes(text));
    private static string? Method(JsonElement body) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("method", out var value) ? value.GetString() : null;

    private sealed class Request
    {
        internal string Method = "";
        internal readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        internal JsonElement Body;
    }

    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly System.Collections.Concurrent.ConcurrentBag<Task> _clients = new();
        internal readonly System.Collections.Concurrent.ConcurrentQueue<Request> Requests = new();
        internal bool Sse, Redirect, StallTool, LargeToolResult, ExpireTool;
        internal Server() { _listener.Start(); _loop = AcceptAsync(); }
        internal McpHttpOptions Options()
        {
            var uri = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/mcp");
            return new McpHttpOptions(uri, new[] { uri }) { AllowLoopbackHttp = true };
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                { var socket = await _listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(HandleAsync(socket)); }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }
        private async Task HandleAsync(TcpClient socket)
        {
            using (socket)
            {
                try
                {
                    var stream = socket.GetStream(); var header = new List<byte>(); var one = new byte[1];
                    while (header.Count < 32768)
                    {
                        if (await stream.ReadAsync(one, 0, 1, _stop.Token) == 0) return;
                        header.Add(one[0]);
                        if (header.Count >= 4 && Encoding.ASCII.GetString(header.Skip(header.Count - 4).ToArray()) == "\r\n\r\n") break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var request = new Request { Method = lines[0].Split(' ')[0] };
                    foreach (string line in lines.Skip(1)) { int at = line.IndexOf(':'); if (at > 0) request.Headers[line.Substring(0, at)] = line.Substring(at + 1).Trim(); }
                    int length = request.Headers.TryGetValue("Content-Length", out string? value) ? int.Parse(value) : 0;
                    var bytes = new byte[length]; for (int read = 0; read < length;) { int count = await stream.ReadAsync(bytes, read, length - read, _stop.Token); if (count == 0) return; read += count; }
                    request.Body = length == 0 ? Json("{}") : WireJson.Parse(bytes); Requests.Enqueue(request);
                    if (Redirect) { await Respond(stream, "302 Found", "", "Location: http://127.0.0.1:1/not-allowed\r\n"); return; }
                    string? method = McpClientTests.Method(request.Body);
                    if (request.Method == "DELETE") { await Respond(stream, "204 No Content", ""); return; }
                    if (method == null || method.StartsWith("notifications/", StringComparison.Ordinal)) { await Respond(stream, "202 Accepted", ""); return; }
                    string id = request.Body.GetProperty("id").GetRawText();
                    if (method == "initialize")
                    { await Respond(stream, "200 OK", "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"}}}", "Mcp-Session-Id: synthetic-session\r\n"); return; }
                    if (method == "tools/list")
                    { await Respond(stream, "200 OK", "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[{\"name\":\"forbidden\",\"inputSchema\":{}},{\"name\":\"allowed\",\"inputSchema\":{}}]}}"); return; }
                    if (StallTool) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                    if (ExpireTool) { await Respond(stream, "404 Not Found", ""); return; }
                    string result = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"" + (LargeToolResult ? new string('x', 2048) : "hello") + "\"}]}}";
                    if (Sse)
                    {
                        string content = "data: {\"jsonrpc\":\"2.0\",\"id\":71,\"method\":\"ping\"}\n\ndata: {\"jsonrpc\":\"2.0\",\"id\":72,\"method\":\"sampling/createMessage\"}\n\ndata: " + result + "\n\n";
                        await Respond(stream, "200 OK", content, contentType: "text/event-stream");
                    }
                    else await Respond(stream, "200 OK", result);
                }
                catch (Exception) when (_stop.IsCancellationRequested) { }
            }
        }
        private async Task Respond(NetworkStream stream, string status, string body, string extra = "", string contentType = "application/json")
        {
            byte[] content = Encoding.UTF8.GetBytes(body);
            byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Type: " + contentType + "\r\nContent-Length: " + content.Length + "\r\n" + extra + "\r\n");
            await stream.WriteAsync(headers, 0, headers.Length, _stop.Token); await stream.WriteAsync(content, 0, content.Length, _stop.Token);
        }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop(); _loop.GetAwaiter().GetResult();
            Task.WhenAll(_clients.ToArray()).GetAwaiter().GetResult(); _stop.Dispose();
        }
    }
}
