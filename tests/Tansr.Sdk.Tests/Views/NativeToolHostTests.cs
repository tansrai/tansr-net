using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Tests.Views;

public sealed class NativeToolHostTests
{
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static readonly JsonElement Declaration = Json("{\"name\":\"mcp_echo\",\"parameters\":{\"text\":{\"type\":\"string\"}},\"readOnly\":true,\"timeoutMs\":10000}");
    private static readonly JsonElement Receipt = Json("{\"status\":\"ok\",\"content\":[{\"t\":\"text\",\"text\":\"native echo\"}]}");
    private static AgentEvent Request(string name = "mcp_echo", string text = "hello")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new AgentEvent("server.tool.request", null, Json("{\"type\":\"server.tool.request\",\"ts\":" + now + ",\"payload\":{\"callId\":\"call\",\"name\":\"" + name + "\",\"args\":{\"text\":\"" + text + "\"},\"deadlineAt\":" + (now + 10000) + ",\"ttlMs\":10000}}"));
    }
    private static TansrClient Client(HttpClient http) => new(new TansrClientOptions { BaseUri = new Uri("https://serve.test"), TokenProvider = _ => Task.FromResult("fixture") }, http);

    [Fact]
    public void OptionalFixedDeclarationCannotReplaceBuiltInTools()
    {
        var binding = new NativeToolBinding(Declaration, (_, _) => Task.FromResult(Receipt));
        var declarations = NativeToolHost.GetDeclarations(new[] { binding });
        Assert.Equal(new[] { "application_info", "set_window_title", "mcp_echo" }, declarations.EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.True(declarations[2].GetProperty("readOnly").GetBoolean());
        Assert.Throws<ArgumentException>(() => NativeToolHost.GetDeclarations(new[] { binding, binding }));
        var shadow = new NativeToolBinding(Json("{\"name\":\"set_window_title\"}"), (_, _) => Task.FromResult(Receipt));
        Assert.Throws<ArgumentException>(() => NativeToolHost.GetDeclarations(new[] { shadow }));
    }

    [Fact]
    public async Task ServeRequestInvokesOptionalDelegateOnceAndSubmitsOriginalReceipt()
    {
        using var handler = new Handler(false); using var http = new HttpClient(handler); using var client = Client(http);
        var bindingEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); var executions = 0;
        var binding = new NativeToolBinding(Declaration, (args, _) =>
        { Assert.Equal("hello", args.GetProperty("text").GetString()); Interlocked.Increment(ref executions); bindingEntered.TrySetResult(true); return finish.Task; });
        var session = await client.CreateSessionAsync(new CreateSessionOptions { ClientTools = NativeToolHost.GetDeclarations(new[] { binding }) });
        using var host = new NativeToolHost(session, "test", (_, _) => Task.CompletedTask, _ => { }, new[] { binding });
        var request = Request(); host.HandleEvent(request); await bindingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.HandleEvent(request); finish.SetResult(Receipt);
        var submitted = await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token);
        Assert.Equal(1, executions); Assert.Equal(Receipt.GetRawText(), submitted.GetRawText()); Assert.Single(handler.Receipts);
        Assert.Equal(3, handler.CreateBody!.Value.GetProperty("clientTools").GetArrayLength());
    }

    [Fact]
    public async Task CancelFrameReachesRunningDelegateWithoutBlockingEventConsumption()
    {
        using var handler = new Handler(false); using var http = new HttpClient(handler); using var client = Client(http);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new NativeToolBinding(Declaration, async (_, ct) => { entered.SetResult(true); await Task.Delay(Timeout.Infinite, ct); return Receipt; });
        var session = await client.CreateSessionAsync(new CreateSessionOptions());
        using var host = new NativeToolHost(session, "test", (_, _) => Task.CompletedTask, _ => { }, new[] { binding });
        host.HandleEvent(Request()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.HandleEvent(new AgentEvent("server.tool.cancel", null, Json("{\"type\":\"server.tool.cancel\",\"payload\":{\"callId\":\"call\"}}")));
        var receipt = await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token);
        Assert.Equal("error", receipt.GetProperty("status").GetString()); Assert.Equal("native_tool_cancelled", receipt.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ResumedSessionDoesNotReexecuteUnknownPriorMcpWork()
    {
        using var handler = new Handler(true); using var http = new HttpClient(handler); using var client = Client(http); var executions = 0;
        var binding = new NativeToolBinding(Declaration, (_, _) => { executions++; return Task.FromResult(Receipt); });
        var session = await client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = "s" });
        using var host = new NativeToolHost(session, "test", (_, _) => Task.CompletedTask, _ => { }, new[] { binding });
        host.HandleEvent(Request()); var receipt = await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token);
        Assert.Equal(0, executions); Assert.Equal("native_tool_host_requires_new_session", receipt.GetProperty("message").GetString());
    }

    private sealed class Handler(bool resumed) : HttpMessageHandler
    {
        internal JsonElement? CreateBody;
        internal readonly ConcurrentQueue<JsonElement> Receipts = new();
        internal readonly TaskCompletionSource<JsonElement> Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? default : Json(await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri!.AbsolutePath.EndsWith("/tool-results/call", StringComparison.Ordinal))
            { Receipts.Enqueue(body); Submitted.TrySetResult(body); return Response("{\"ok\":true}"); }
            CreateBody = body;
            return Response("{\"sessionId\":\"s\",\"resumed\":" + (resumed ? "true" : "false") + ",\"lastSeq\":-1}");
        }
        private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
