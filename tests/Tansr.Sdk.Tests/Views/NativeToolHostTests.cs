using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
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
    private static AgentEvent Request(string name = "mcp_echo", string text = "hello", string? sequence = "42", string id = "call")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new AgentEvent("server.tool.request", sequence, Json("{\"type\":\"server.tool.request\",\"ts\":" + now + ",\"payload\":{\"callId\":\"" + id + "\",\"name\":\"" + name + "\",\"args\":{\"text\":\"" + text + "\",\"title\":\"" + text + "\"},\"deadlineAt\":" + (now + 10000) + ",\"ttlMs\":10000}}"));
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

    [Theory]
    [InlineData("40")]
    [InlineData("41")]
    [InlineData(null)]
    [InlineData("042")]
    [InlineData("9007199254740992")]
    public async Task ResumedSessionDoesNotReexecuteUnknownPriorMcpWork(string? sequence)
    {
        using var handler = new Handler(true); using var http = new HttpClient(handler); using var client = Client(http); var executions = 0;
        var binding = new NativeToolBinding(Declaration, (_, _) => { executions++; return Task.FromResult(Receipt); });
        var session = await client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = "s" });
        using var host = new NativeToolHost(session, "test", (_, _) => Task.CompletedTask, _ => { }, new[] { binding });
        await host.Ready; host.HandleEvent(Request(sequence: sequence)); var receipt = await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token);
        Assert.Equal(0, executions); Assert.Equal("native_tool_prior_outcome_unknown", receipt.GetProperty("message").GetString()); Assert.Equal(1, handler.MetadataReads);
    }

    [Theory]
    [InlineData("application_info")]
    [InlineData("set_window_title")]
    [InlineData("mcp_echo")]
    [InlineData("skill_lookup")]
    public async Task ResumedSessionAcceptsNewNativeBusinessMcpAndSkillRequestsAfterTheOriginalWatermark(string tool)
    {
        using var handler = new Handler(true); using var http = new HttpClient(handler); using var client = Client(http);
        var executions = 0; var titles = 0;
        var bindings = new[] { new NativeToolBinding(Declaration, (_, _) => { executions++; return Task.FromResult(Receipt); }),
            new NativeToolBinding(Json("{\"name\":\"skill_lookup\",\"readOnly\":true}"), (_, _) => { executions++; return Task.FromResult(Receipt); }) };
        var session = await client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = "s" });
        using var host = new NativeToolHost(session, "test", (title, ct) => { ct.ThrowIfCancellationRequested(); Assert.Equal("hello", title); titles++; return Task.CompletedTask; }, _ => { }, bindings);
        await host.Ready; var request = Request(tool); host.HandleEvent(request); host.HandleEvent(request);
        var receipt = await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token);
        Assert.Equal("ok", receipt.GetProperty("status").GetString()); Assert.Equal(tool is "mcp_echo" or "skill_lookup" ? 1 : 0, executions);
        Assert.Equal(tool == "set_window_title" ? 1 : 0, titles); Assert.Equal(1, handler.MetadataReads);
    }

    [Fact]
    public async Task ResumedRequestsWaitForTheAuthoritativeMetadataAndUseItsLaterWatermark()
    {
        var metadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(true) { MetadataGate = metadata.Task, LastSequence = 50 }; using var http = new HttpClient(handler); using var client = Client(http); var executions = 0;
        var binding = new NativeToolBinding(Declaration, (_, _) => { executions++; return Task.FromResult(Receipt); });
        var session = await client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = "s" });
        using var host = new NativeToolHost(session, "test", (_, _) => Task.CompletedTask, _ => { }, new[] { binding });
        host.HandleEvent(Request(sequence: "49")); Assert.False(host.Ready.IsCompleted); Assert.Equal(0, executions); Assert.Empty(handler.Receipts);
        metadata.SetResult(); await host.Ready;
        var receipt = await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token);
        Assert.Equal(0, executions); Assert.Equal("native_tool_prior_outcome_unknown", receipt.GetProperty("message").GetString()); Assert.Equal(1, handler.MetadataReads);
    }

    [Fact]
    public async Task FailureToReadTheOriginalWatermarkNeverExecutesOrSilentlyRecreatesTheSession()
    {
        using var handler = new Handler(true) { FailMetadata = true }; using var http = new HttpClient(handler); using var client = Client(http); var executions = 0;
        var binding = new NativeToolBinding(Declaration, (_, _) => { executions++; return Task.FromResult(Receipt); });
        var session = await client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = "s" });
        using var host = new NativeToolHost(session, "test", (_, _) => Task.CompletedTask, _ => { }, new[] { binding });
        await Assert.ThrowsAsync<TansrHttpException>(() => host.Ready); host.HandleEvent(Request());
        var receipt = await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("error", receipt.GetProperty("status").GetString()); Assert.Equal(0, executions); Assert.Equal(1, handler.MetadataReads); Assert.Equal(1, handler.Creates);
    }

    [Fact]
    public async Task CompletedConfirmedCallsRetireWithoutLimitingTheConversationOrReexecutingEvictedRequests()
    {
        using var handler = new Handler(false); using var http = new HttpClient(handler); using var client = Client(http); var executions = 0;
        var reports = new ConcurrentQueue<string>(); var binding = new NativeToolBinding(Declaration, (_, _) => { executions++; return Task.FromResult(Receipt); });
        var session = await client.CreateSessionAsync(new()); using var host = new NativeToolHost(session, "test", (_, _) => Task.CompletedTask, reports.Enqueue, new[] { binding });
        for (var i = 1; i <= 270; i++)
        {
            var id = "call-" + i; host.HandleEvent(Request(sequence: i.ToString(System.Globalization.CultureInfo.InvariantCulture), id: id)); await CallCompletion(host, id);
        }
        Assert.Equal(270, executions); Assert.Equal(270, handler.Receipts.Count); Assert.Empty(reports);
        host.HandleEvent(Request(sequence: "1", id: "call-1")); await CallCompletion(host, "call-1");
        Assert.Equal(270, executions); Assert.Equal("native_tool_prior_outcome_unknown", handler.Receipts.Last().GetProperty("message").GetString());
        host.HandleEvent(Request(sequence: "271", id: "call-271")); await CallCompletion(host, "call-271"); Assert.Equal(271, executions);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token); Assert.Equal(1, handler.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedReceiptsAndPendingWorkStayBoundedUntilOriginalConfirmationWithoutRepeatingBusinessWork(bool firstPending)
    {
        using var handler = new Handler(false) { LoseReceipts = true }; using var http = new HttpClient(handler); using var client = Client(http); var executions = 0;
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentQueue<string>(); var binding = new NativeToolBinding(Declaration, async (args, ct) =>
        { Interlocked.Increment(ref executions); if (args.GetProperty("text").GetString() != "pending") return Receipt; entered.TrySetResult(); return await pending.Task.WaitAsync(ct); });
        var session = await client.CreateSessionAsync(new()); using var host = new NativeToolHost(session, "test", (_, _) => Task.CompletedTask, reports.Enqueue, new[] { binding });
        for (var i = 1; i <= 256; i++)
        {
            var id = "call-" + i; host.HandleEvent(Request(text: firstPending && i == 1 ? "pending" : "hello", sequence: i.ToString(System.Globalization.CultureInfo.InvariantCulture), id: id));
            if (firstPending && i == 1) await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); else await CallCompletion(host, id);
        }
        host.HandleEvent(Request(sequence: "257", id: "call-257")); Assert.Equal(256, executions);
        Assert.Contains("native_tool_host_capacity: pending_or_unconfirmed", reports);
        handler.LoseReceipts = false;
        if (firstPending) pending.SetResult(Receipt); else host.HandleEvent(Request(sequence: "1", id: "call-1"));
        await CallCompletion(host, "call-1"); Assert.Equal(256, executions);
        host.HandleEvent(Request(sequence: "257", id: "call-257")); await CallCompletion(host, "call-257"); Assert.Equal(257, executions);
        host.HandleEvent(Request(sequence: "1", id: "call-1")); await CallCompletion(host, "call-1"); Assert.Equal(257, executions);
        Assert.Equal("native_tool_prior_outcome_unknown", handler.Receipts.Last().GetProperty("message").GetString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token);
    }

    private static async Task CallCompletion(NativeToolHost host, string id)
    {
        // HandleEvent deliberately cannot await a tool: cancellation must keep flowing. The
        // test joins that original private work item, rather than sleeping for an arbitrary delay.
        Task task;
        var gate = typeof(NativeToolHost).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        lock (gate)
        {
            var calls = (System.Collections.IDictionary)typeof(NativeToolHost).GetField("_calls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var call = calls[id]; if (call == null) return;
            task = (Task)call.GetType().GetField("Task", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(call)!;
        }
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Handler(bool resumed) : HttpMessageHandler
    {
        internal JsonElement? CreateBody;
        internal Task? MetadataGate;
        internal bool FailMetadata;
        internal bool LoseReceipts;
        internal long LastSequence = 41;
        internal int MetadataReads, Creates;
        internal readonly ConcurrentQueue<JsonElement> Receipts = new();
        internal readonly TaskCompletionSource<JsonElement> Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? default : Json(await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri!.AbsolutePath.Contains("/tool-results/", StringComparison.Ordinal))
            { Receipts.Enqueue(body); Submitted.TrySetResult(body); if (LoseReceipts) throw new HttpRequestException("Synthetic unknown receipt."); return Response("{\"accepted\":true}"); }
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/v2/sessions/s")
            {
                MetadataReads++; if (MetadataGate != null) await MetadataGate.WaitAsync(cancellationToken);
                if (FailMetadata) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"forbidden\",\"message\":\"fixture\"}}", Encoding.UTF8, "application/json") };
                return Response("{\"sessionId\":\"s\",\"endUserId\":\"user\",\"live\":true,\"status\":\"idle\",\"lastSeq\":" + LastSequence + ",\"createdAt\":\"2026-09-27T00:00:00Z\",\"lastActivityAt\":\"2026-09-27T00:00:00Z\"}");
            }
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/v2/sessions", request.RequestUri.AbsolutePath); Creates++; CreateBody = body;
            return Response("{\"sessionId\":\"s\",\"resumed\":" + (resumed ? "true" : "false") + ",\"lastSeq\":" + (resumed ? 40 : -1) + "}");
        }
        private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
