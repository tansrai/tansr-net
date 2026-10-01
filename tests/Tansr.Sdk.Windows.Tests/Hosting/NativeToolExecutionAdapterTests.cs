using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class NativeToolExecutionAdapterTests
{
    [Fact]
    public async Task DeviceRegistrationUsesOriginalDeclarationsAndDelegatesWithoutLegacyDoubleExecution()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = Client(http);
        var calls = 0; var titles = new List<string>(); var revoked = false;
        var binding = new NativeToolBinding(Json("{\"name\":\"native_echo\",\"description\":\"bounded synthetic echo\",\"readOnly\":true,\"timeoutMs\":10000}"),
            (_, token) => { token.ThrowIfCancellationRequested(); if (revoked) throw new InvalidOperationException("trust_revoked"); calls++; return Task.FromResult(Json("{\"status\":\"ok\",\"content\":[{\"t\":\"text\",\"text\":\"original delegate\"}]}")); });
        var declarations = NativeToolHost.GetDeclarations(new[] { binding });
        var session = await client.CreateSessionAsync(new CreateSessionOptions { ClientTools = declarations });
        using var host = new NativeToolHost(session, "adapter-test", (title, token) => { token.ThrowIfCancellationRequested(); titles.Add(title); return Task.CompletedTask; }, _ => { }, new[] { binding });
        var tools = host.SelectDeviceExecution();
        var directory = Path.Combine(Path.GetTempPath(), "tansr-native-adapter-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var workspace = new WindowsWorkspace(directory);
            var backend = new WindowsExecutorBackend("native", new[] { new WindowsExecutorWorkspace("work", "1", workspace) }, tools);
            Assert.Contains(backend.Registration.GetProperty("operations").EnumerateArray(), item => item.GetString() == "tool.invoke");
            Assert.Equal(declarations.GetArrayLength(), backend.Registration.GetProperty("tools").GetArrayLength());
            foreach (var declaration in declarations.EnumerateArray())
            {
                var tool = Assert.Single(tools, item => item.Name == declaration.GetProperty("name").GetString());
                Assert.Equal(WireJson.DomainDigest("tansr.sdk2.client-tool.v1", WireJson.EncodeControl(declaration)), tool.DefinitionDigest);
            }
            host.HandleEvent(Request("native_echo"));
            var echo = Assert.Single(tools, item => item.Name == "native_echo");
            Assert.Equal("ok", (await echo.Invoke(Json("{}"), CancellationToken.None)).GetProperty("status").GetString());
            Assert.Equal(1, calls); Assert.Equal(0, handler.Receipts);
            await Assert.Single(tools, item => item.Name == "set_window_title").Invoke(Json("{\"title\":\"same original window\"}"), CancellationToken.None);
            Assert.Equal(new[] { "same original window" }, titles);
            revoked = true;
            Assert.Equal("trust_revoked", (await echo.Invoke(Json("{}"), CancellationToken.None)).GetProperty("message").GetString());
            Assert.Equal(1, calls);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => echo.Invoke(Json("{}"), cancelled.Token));
            await host.DrainAsync(CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => echo.Invoke(Json("{}"), CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PendingLegacySideEffectPreventsSwitchingExecutionPipelines()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = Client(http);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new NativeToolBinding(Json("{\"name\":\"native_echo\"}"), async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); });
        var session = await client.CreateSessionAsync(new CreateSessionOptions());
        using var host = new NativeToolHost(session, "adapter-test", (_, _) => Task.CompletedTask, _ => { }, new[] { binding });
        host.HandleEvent(Request("native_echo")); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("native_tool_prior_outcome_unconfirmed", Assert.Throws<InvalidOperationException>(() => host.SelectDeviceExecution()).Message);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.DrainAsync(timeout.Token);
        Assert.Equal(1, handler.Receipts);
    }

    private static AgentEvent Request(string name)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new AgentEvent("server.tool.request", "42", Json("{\"type\":\"server.tool.request\",\"ts\":" + now + ",\"payload\":{\"callId\":\"call\",\"name\":\"" + name + "\",\"args\":{},\"deadlineAt\":" + (now + 10000) + ",\"ttlMs\":10000}}"));
    }
    private static JsonElement Json(string value) => WireJson.Parse(Encoding.UTF8.GetBytes(value));
    private static TansrClient Client(HttpClient http) => new(new TansrClientOptions { BaseUri = new Uri("https://serve.test"), TokenProvider = _ => Task.FromResult("fixture") }, http);
    private sealed class Handler : HttpMessageHandler
    {
        internal int Receipts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var receipt = request.RequestUri!.AbsolutePath.Contains("/tool-results/", StringComparison.Ordinal);
            if (receipt) Interlocked.Increment(ref Receipts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(receipt ? "{\"accepted\":true}" : "{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":-1}", Encoding.UTF8, "application/json") });
        }
    }
}
