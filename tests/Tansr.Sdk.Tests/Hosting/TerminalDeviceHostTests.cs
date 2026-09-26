using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Execution;
using Tansr.Sdk.Tests.Terminal;

namespace Tansr.Sdk.Tests.Hosting;

public sealed class TerminalDeviceHostTests
{
    [Fact]
    public async Task OneInitializationBindsBeforePollingAndRoutesOutputAndNotificationsWithDeviceCredentials()
    {
        using var rig = new Rig();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Output!.OpenAsync(rig.Adapter.ExistingOperation, default));
        await rig.Host.StartAsync(); await rig.Notifications.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeviceSessionState.Ready, rig.Host.State); Assert.NotNull(rig.Host.Binding);
        Assert.Equal(new[] { "initialize", "bind" }, rig.Controller.Actions); Assert.Equal(new[] { "register", "poll" }, rig.Device.Actions);
        Assert.Equal(1, rig.BindingPosts); Assert.Equal(1, rig.BackendCreations);
        using var capture = await rig.Output!.OpenAsync(rig.Adapter.ExistingOperation, default);
        Assert.True(capture.Append("stdout", "utf-8", Encoding.UTF8.GetBytes("中文 output")));
        await capture.SealAsync(false, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("中文 output", Encoding.UTF8.GetString(rig.Adapter.Received.SelectMany(item => Convert.FromBase64String(item.GetProperty("base64").GetString()!)).ToArray()));
        await rig.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeviceSessionState.Stopped, rig.Host.State); Assert.True(rig.Device.PollStopped); Assert.False(rig.Journal.Closed);
        Assert.Equal(0, rig.Backend.Executions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Host.StartAsync());
    }

    [Fact]
    public async Task LostTerminalBindingIsNotRepeatedAndCannotStartPolling()
    {
        using var rig = new Rig { FailBinding = true };
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => rig.Host.StartAsync()); Assert.Equal("network_error", error.Code);
        Assert.Equal(DeviceSessionState.Failed, rig.Host.State); Assert.Equal(1, rig.BindingPosts);
        Assert.Equal(new[] { "register" }, rig.Device.Actions); Assert.False(rig.Notifications.Task.IsCompleted);
        Assert.Equal(0, rig.Backend.Executions); Assert.Empty(rig.Adapter.Sent);
    }

    [Fact]
    public async Task OutputReconnectKeepsTheOriginalCursorAndUtf8DecoderWithoutRebindingOrReexecution()
    {
        using var rig = new Rig(); var blocks = Goldens.GetProperty("blocks");
        rig.OutputResponse = (request, _) => Task.FromResult(rig.OutputReads == 1
            ? Sse(Block(rig.Reference, blocks[0]), Block(rig.Reference, blocks[1]))
            : Sse(Block(rig.Reference, blocks[1]), Block(rig.Reference, blocks[2]), Complete(rig.Reference)));
        await rig.Host.StartAsync(); var text = new List<string>();
        var final = await rig.Host.ObserveOutputAsync(rig.Reference, (update, _) => { text.AddRange(update.Segments.Select(item => item.Text!)); return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(final.SealVerified); Assert.False(final.HasPresentationGap); Assert.Equal(2, final.LastSequence);
        Assert.Equal(new[] { "", "x", "😀" }, text); Assert.Equal(2, rig.OutputReads);
        Assert.DoesNotContain("afterSeq=", rig.OutputRequests[0], StringComparison.Ordinal); Assert.Contains("afterSeq=1", rig.OutputRequests[1], StringComparison.Ordinal);
        Assert.Equal(1, rig.BindingPosts); Assert.Equal(1, rig.Device.Actions.Count(item => item == "register")); Assert.Equal(0, rig.Backend.Executions);
        await rig.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EofInsideAnSseFrameResumesAfterTheLastCompleteBlockWithTheSameDecoder()
    {
        using var rig = new Rig(); var blocks = Goldens.GetProperty("blocks");
        rig.OutputResponse = async (_, _) =>
        {
            if (rig.OutputReads != 1) return Sse(Block(rig.Reference, blocks[1]), Block(rig.Reference, blocks[2]), Complete(rig.Reference));
            using var complete = Sse(Block(rig.Reference, blocks[0]));
            var content = await complete.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(content + "event: output.block\ndata: {", Encoding.UTF8, "text/event-stream") };
        };
        await rig.Host.StartAsync(); var text = new List<string>();
        var final = await rig.Host.ObserveOutputAsync(rig.Reference, (update, _) =>
        { text.AddRange(update.Segments.Select(item => item.Text!)); return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(final.SealVerified); Assert.False(final.HasPresentationGap);
        Assert.Equal(new[] { "", "x", "😀" }, text); Assert.Equal(2, rig.OutputReads);
        Assert.Contains("afterSeq=0", rig.OutputRequests[1], StringComparison.Ordinal);
        Assert.Equal(1, rig.BindingPosts); Assert.Equal(0, rig.Backend.Executions);
        await rig.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CallbackFailureIsReturnedOnceEvenIfItsExceptionLooksLikeANetworkFailure()
    {
        using var rig = new Rig(); rig.OutputResponse = (_, _) => Task.FromResult(Sse(Block(rig.Reference, Goldens.GetProperty("blocks")[0])));
        await rig.Host.StartAsync(); var calls = 0; var expected = new IOException("consumer failure");
        var error = await Assert.ThrowsAsync<IOException>(() => rig.Host.ObserveOutputAsync(rig.Reference, (_, _) => { calls++; throw expected; }));
        Assert.Same(expected, error); Assert.Equal(1, calls); Assert.Equal(1, rig.OutputReads);
        await rig.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopRetainsObservationUntilAnUncooperativeCallbackActuallyExits()
    {
        using var rig = new Rig(); rig.OutputResponse = (_, _) => Task.FromResult(Sse(Block(rig.Reference, Goldens.GetProperty("blocks")[0])));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.Host.StartAsync(); var observation = rig.Host.ObserveOutputAsync(rig.Reference, async (_, _) => { entered.TrySetResult(); await release.Task; });
        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("observation_already_active", (await Assert.ThrowsAsync<TansrProtocolException>(() => rig.Host.ObserveOutputAsync(rig.Reference, (_, _) => Task.CompletedTask))).Code);
            stopping = rig.Host.StopAsync(); Assert.False(stopping.IsCompleted); Assert.False(observation.IsCompleted);
            Assert.Equal(1, rig.OutputReads);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation.WaitAsync(TimeSpan.FromSeconds(5)));
        await stopping!.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(rig.Device.PollStopped); Assert.False(rig.Journal.Closed);
    }

    [Fact]
    public async Task ExhaustedReadOnlyReconnectsDoNotBecomeAnOutputSuccessOrANewExecution()
    {
        using var rig = new Rig(); rig.OutputResponse = (_, _) => Task.FromResult(Sse());
        await rig.Host.StartAsync();
        Assert.Equal("event_stream_disconnected", (await Assert.ThrowsAsync<TansrProtocolException>(() => rig.Host.ObserveOutputAsync(rig.Reference, (_, _) => Task.CompletedTask))).Code);
        Assert.Equal(3, rig.OutputReads); Assert.Equal(1, rig.BindingPosts); Assert.Equal(0, rig.Backend.Executions);
        await rig.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityAndMalformedOutputFailuresAreNeverReconnected(bool malformed)
    {
        using var rig = new Rig();
        rig.OutputResponse = (_, _) => Task.FromResult(malformed ? new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("event: output.block\ndata: {}\n\n", Encoding.UTF8, "text/event-stream") } :
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"forbidden\"}}", Encoding.UTF8, "application/json") });
        await rig.Host.StartAsync(); var error = await Record.ExceptionAsync(() => rig.Host.ObserveOutputAsync(rig.Reference, (_, _) => Task.CompletedTask));
        Assert.NotNull(error); Assert.Equal(1, rig.OutputReads); Assert.Equal(0, rig.Backend.Executions);
        await rig.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static JsonElement Goldens => TerminalCandidateContractTests.Goldens().GetProperty("semantic").GetProperty("interleavedUtf8");
    private static JsonElement Block(JsonElement reference, JsonElement block) => JsonSerializer.SerializeToElement(new
    { contract = "terminal-services-v1", type = "output.block", operation = reference, block });
    private static JsonElement Complete(JsonElement reference) => JsonSerializer.SerializeToElement(new
    {
        contract = "terminal-services-v1",
        type = "output.status",
        status = new
        {
            contract = "terminal-services-v1",
            operation = reference,
            state = "complete",
            acceptedThrough = "2",
            durableThrough = (string?)null,
            retainedFrom = "0",
            nextByteOffset = "5",
            seal = Goldens.GetProperty("seal")
        }
    });
    private static HttpResponseMessage Json(JsonElement value) => new(HttpStatusCode.OK)
    { Content = new StringContent(WireJson.CanonicalString(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(params JsonElement[] values) => new(HttpStatusCode.OK)
    { Content = new StringContent(string.Concat(values.Select(value => "event: " + value.GetProperty("type").GetString() + "\ndata: " + WireJson.CanonicalString(value) + "\n\n")), Encoding.UTF8, "text/event-stream") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct); }

    private sealed class Rig : IDisposable
    {
        public readonly TerminalTestAdapter Adapter = new();
        public readonly Client Controller = new(), Device = new();
        public readonly Journal Journal = new();
        public readonly Backend Backend = new();
        public readonly TaskCompletionSource Notifications = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> OutputRequests = [];
        private readonly HttpClient controllerHttp, deviceHttp;
        private readonly TerminalConnection controllerTerminal, deviceTerminal;
        public TerminalDeviceHost Host { get; }
        public IExecutionOutputSink? Output;
        public int BindingPosts, OutputReads, BackendCreations;
        public bool FailBinding;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OutputResponse;
        public JsonElement Reference => JsonSerializer.SerializeToElement(new { operationId = "operation-1", requestDigest = Adapter.ExistingOperation.GetProperty("digest").GetString() });
        public Rig()
        {
            controllerHttp = new(new Handler(async (request, ct) =>
            {
                Assert.Equal("controller-only", request.Headers.Authorization!.Parameter);
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/v3/terminal/capabilities") return Json(await Adapter.GetCapabilitiesAsync(ct));
                if (path == "/v3/terminal/bindings")
                {
                    BindingPosts++; Assert.DoesNotContain("poll", Device.Actions);
                    if (FailBinding) throw new HttpRequestException("lost original binding response");
                    return Json(await Adapter.BindAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(ct)), ct));
                }
                Assert.EndsWith("/tool-output", path, StringComparison.Ordinal); Assert.Equal(HttpMethod.Get, request.Method);
                OutputReads++; OutputRequests.Add(request.RequestUri.PathAndQuery);
                return await (OutputResponse ?? throw new InvalidOperationException("Unexpected output observation"))(request, ct);
            }));
            deviceHttp = new(new Handler(async (request, ct) =>
            {
                Assert.Equal("device-only", request.Headers.Authorization!.Parameter);
                if (request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                { Notifications.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("Unreachable"); }
                Assert.EndsWith("/output-batches", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
                Assert.Equal(HttpMethod.Post, request.Method); return Json(await Adapter.SendBatchAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(ct)), ct));
            }));
            controllerTerminal = new(Options("controller-only"), true, controllerHttp); deviceTerminal = new(Options("device-only"), true, deviceHttp);
            Host = new(Controller, Device, controllerTerminal, deviceTerminal, sink => { Output = sink; BackendCreations++; return Backend; }, Journal,
                new TerminalDeviceOptions
                {
                    SessionId = "session-1",
                    WorkspaceId = "workspace-1",
                    BindingRequestId = "original-binding-request",
                    SessionContract = SessionContract.Sdk2OffloadV1,
                    RequestedTools = ["Write"],
                    MaxOutputReconnectAttempts = 2,
                    OutputReconnectDelay = TimeSpan.Zero
                }, (_, _) => Task.CompletedTask);
        }
        private static TansrClientOptions Options(string token) => new()
        {
            BaseUri = new("http://127.0.0.1:34567/"),
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(token),
            PrincipalProvider = () => "trusted-principal",
            ExecutionScopeProvider = () => ExecutionFixture.Scope(),
            SessionContract = SessionContract.Sdk2OffloadV1
        };
        public void Dispose() { Host.Dispose(); controllerTerminal.Dispose(); deviceTerminal.Dispose(); controllerHttp.Dispose(); deviceHttp.Dispose(); }
    }
    private sealed class Backend : IExecutionBackend
    {
        public int Executions;
        public JsonElement Registration => ExecutionFixture.Registration();
        public Task<JsonElement> ExecuteAsync(JsonElement operation, Func<CancellationToken, Task> guard, CancellationToken ct)
        { Executions++; throw new InvalidOperationException("No device work was queued."); }
    }
    private sealed class Client : IDeviceExecutionClient
    {
        public readonly List<string> Actions = [];
        public bool PollStopped;
        public JsonElement ReadScope() => ExecutionFixture.Scope();
        public Task<JsonElement> InitializeAsync(JsonElement input, CancellationToken ct) { Actions.Add("initialize"); WireJson.ValidateNamed("SessionInitializeRequest", input); return Task.FromResult(Capabilities(null)); }
        public Task<JsonElement> RegisterAsync(JsonElement registration, CancellationToken ct) { Actions.Add("register"); return Task.FromResult(ExecutionFixture.Connection()); }
        public Task<JsonElement> HeartbeatAsync(JsonElement connection, CancellationToken ct) => Task.FromResult(connection);
        public Task<JsonElement> BindExecutionAsync(JsonElement request, JsonElement target, CancellationToken ct)
        {
            Actions.Add("bind"); WireJson.ValidateNamed("ExecutionBindingRequest", request); WireJson.ValidateNamed("ExecutionTarget", target);
            return Task.FromResult(Capabilities(JsonSerializer.SerializeToElement(new { bindingId = "binding-1", revision = "1", target })));
        }
        public async Task<JsonElement> PollAsync(JsonElement connection, CancellationToken ct)
        { Actions.Add("poll"); try { await Task.Delay(Timeout.Infinite, ct); } finally { PollStopped = true; } return ExecutionFixture.Batch(); }
        public Task<JsonElement> SubmitAsync(JsonElement receipt, CancellationToken ct) => throw new InvalidOperationException("No operation was queued");
        public Task<JsonElement> GetStatusAsync(string session, string operation, CancellationToken ct) => throw new InvalidOperationException("No operation was queued");
        private static JsonElement Capabilities(JsonElement? binding) => JsonSerializer.SerializeToElement(new
        { protocol = "sdk2-ext-v1", sessionId = "session-1", platform = ExecutionFixture.Registration().GetProperty("platform"), capabilityRevision = new string('a', 64), effectiveTools = Array.Empty<object>(), binding });
    }
    private sealed class Journal : IExecutorJournal
    {
        public bool Closed;
        public Task<ExecutorJournalClaim> ClaimAsync(JsonElement operation, CancellationToken ct = default) => throw new InvalidOperationException("No operation was queued");
        public Task CompleteAsync(JsonElement operation, JsonElement receipt, CancellationToken ct = default) => throw new InvalidOperationException("No operation was queued");
        public Task<JsonElement?> ReceiptAsync(JsonElement operation, CancellationToken ct = default) => throw new InvalidOperationException("No operation was queued");
        public Task<IReadOnlyList<JsonElement>> OperationsAsync(string? afterOperationId = null, CancellationToken ct = default) => throw new InvalidOperationException("No operation was queued");
        public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
    }
}
