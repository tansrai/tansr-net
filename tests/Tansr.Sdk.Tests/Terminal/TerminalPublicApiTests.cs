using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalPublicApiTests
{
    private static JsonElement Session => JsonSerializer.SerializeToElement(new { sessionContract = "sdk2-offload-v1", sessionId = "session-1" });
    private static JsonElement Reference(JsonElement operation) => JsonSerializer.SerializeToElement(new
    { operationId = operation.GetProperty("operationId").GetString(), requestDigest = operation.GetProperty("digest").GetString() });
    private static JsonElement Request(TerminalTestAdapter adapter) => TerminalJson.Object(w =>
    {
        w.WriteString("contract", TerminalCandidateContract.Protocol); w.WriteString("requestId", "public-binding");
        TerminalJson.Field(w, "session", Session); TerminalJson.Field(w, "executionBinding", adapter.ExistingOperation.GetProperty("binding"));
        w.WriteStartArray("required"); w.WriteStringValue("execution-stream-v1"); w.WriteEndArray(); w.WriteStartArray("optional"); w.WriteEndArray();
    });
    private static TansrClientOptions Options(string token = "controller-only", Func<JsonElement>? scope = null, int port = 34567) => new()
    {
        BaseUri = new Uri("http://127.0.0.1:" + port + "/"),
        AllowInsecureLoopback = true,
        SessionContract = SessionContract.Sdk2OffloadV1,
        TokenProvider = _ => Task.FromResult(token),
        PrincipalProvider = () => "trusted-synthetic-principal",
        ExecutionScopeProvider = scope ?? (() => ExecutionFixture.Scope())
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
    private static HttpResponseMessage Json(JsonElement value) => new(HttpStatusCode.OK)
    { Content = new StringContent(WireJson.CanonicalString(value), Encoding.UTF8, "application/json") };
    private static JsonElement StatusEvent(TerminalTestAdapter adapter, JsonElement reference) => TerminalJson.Object(w =>
    { w.WriteString("contract", TerminalCandidateContract.Protocol); w.WriteString("type", "output.status"); TerminalJson.Field(w, "status", adapter.Status(reference)); });
    private static HttpResponseMessage Sse(JsonElement value) => new(HttpStatusCode.OK)
    { Content = new StringContent("event: " + TerminalJson.Text(value, "type") + "\ndata: " + WireJson.CanonicalString(value) + "\n\n", Encoding.UTF8, "text/event-stream") };
    private static HttpClient ControllerHttp(TerminalTestAdapter adapter) => new(new Handler(async (request, token) =>
    {
        Assert.Equal("controller-only", request.Headers.Authorization!.Parameter);
        if (request.RequestUri!.AbsolutePath == "/v3/terminal/capabilities") return Json(await adapter.GetCapabilitiesAsync(token));
        Assert.Equal("/v3/terminal/bindings", request.RequestUri.AbsolutePath);
        return Json(await adapter.BindAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(token)), token));
    }));

    [Fact]
    public void PublicNetworkEntryPointsRequireExplicitPreview()
    {
        using var client = new TansrClient(Options());
        Assert.Equal("unsupported_capability", Assert.Throws<TansrProtocolException>(() => new TerminalSessionControl(client)).Code);
        Assert.Equal("unsupported_capability", Assert.Throws<TansrProtocolException>(() => new TerminalConnection(Options())).Code);
        Assert.Equal(TerminalCandidateContract.Revision, TerminalSessionControl.SchemaRevision);
        Assert.Equal(TerminalCandidateContract.SchemaSha256, TerminalSessionControl.SchemaSha256);
    }

    [Fact]
    public async Task ExecutorNotificationsUseDeviceCredentialsOriginalBindingAndExactCursor()
    {
        var adapter = new TerminalTestAdapter(); using var controllerHttp = ControllerHttp(adapter);
        using var controller = new TerminalConnection(Options(), true, controllerHttp);
        var authenticated = await controller.BindAsync(Request(adapter)); var calls = 0;
        var notification = JsonSerializer.SerializeToElement(new
        { contract = "terminal-services-v1", eventId = "9007199254740993", type = "operations-available", executorId = "executor-1", connectionId = "connection-1", operation = (object?)null });
        using var deviceHttp = new HttpClient(new Handler((request, _) =>
        {
            calls++; Assert.Equal("device-only", request.Headers.Authorization!.Parameter);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v3/terminal/executors/executor-1/events?contract=terminal-services-v1&sessionContract=sdk2-offload-v1&connectionId=connection-1", request.RequestUri!.PathAndQuery);
            Assert.Equal("9007199254740992", request.Headers.GetValues("Last-Event-ID").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("retry: 1000\ndata:\n\nid: 9007199254740993\nevent: operations-available\ndata: " + WireJson.CanonicalString(notification) + "\n\n", Encoding.UTF8, "text/event-stream") });
        }));
        using var device = new TerminalConnection(Options("device-only"), true, deviceHttp);
        await Assert.ThrowsAsync<ArgumentException>(() => device.ObserveExecutorAsync(authenticated, null, (_, _) => Task.CompletedTask));
        var binding = device.AttachBinding(authenticated); var observed = 0;
        Assert.Equal("event_stream_disconnected", (await Assert.ThrowsAsync<TansrProtocolException>(() =>
            device.ObserveExecutorAsync(binding, 9007199254740992L, (value, _) =>
            { observed++; Assert.Equal(WireJson.CanonicalString(notification), WireJson.CanonicalString(value)); return Task.CompletedTask; }))).Code);
        Assert.Equal(1, observed); Assert.Equal(1, calls);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ObserveExecutorAsync(binding, -1, (_, _) => Task.CompletedTask));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecutorObservationRetainsItsSlotUntilCancelledCallbackActuallyEnds()
    {
        var adapter = new TerminalTestAdapter(); using var controllerHttp = ControllerHttp(adapter);
        using var controller = new TerminalConnection(Options(), true, controllerHttp);
        var authenticated = await controller.BindAsync(Request(adapter)); var calls = 0;
        var notification = JsonSerializer.SerializeToElement(new
        { contract = "terminal-services-v1", eventId = "0", type = "reconcile-required", executorId = "executor-1", connectionId = "connection-1", operation = (object?)null });
        using var deviceHttp = new HttpClient(new Handler((_, _) =>
        {
            calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("id: 0\nevent: reconcile-required\ndata: " + WireJson.CanonicalString(notification) + "\n\n", Encoding.UTF8, "text/event-stream") });
        }));
        using var device = new TerminalConnection(Options("device-only"), true, deviceHttp);
        var binding = device.AttachBinding(authenticated);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var observing = device.ObserveExecutorAsync(binding, null, async (_, _) => { entered.TrySetResult(); await release.Task; }, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            Assert.False(observing.IsCompleted);
            Assert.Equal("observation_already_active", (await Assert.ThrowsAsync<TansrProtocolException>(() =>
                device.ObserveExecutorAsync(binding, null, (_, _) => Task.CompletedTask))).Code);
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("event_stream_disconnected", (await Assert.ThrowsAsync<TansrProtocolException>(() =>
            device.ObserveExecutorAsync(binding, null, (_, _) => Task.CompletedTask))).Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ControllerBindingCanBeAttachedWithSeparateDeviceCredentialsAndRetainsUnknownCapture()
    {
        var adapter = new TerminalTestAdapter { CommitThenThrow = true };
        using var controllerHttp = ControllerHttp(adapter);
        using var controller = new TerminalConnection(Options(), true, controllerHttp);
        var authenticated = await controller.BindAsync(Request(adapter));
        var deviceRequests = new List<string>();
        using var deviceHttp = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("device-only", request.Headers.Authorization!.Parameter); deviceRequests.Add(request.Method.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post) return Json(await adapter.SendBatchAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(ct)), ct));
            return Json(await adapter.GetOutputStatusAsync(Session, Reference(adapter.ExistingOperation), ct));
        }));
        using var device = new TerminalConnection(Options("device-only"), true, deviceHttp);
        Assert.Throws<ArgumentException>(() => device.CreateOutputSink(authenticated));
        var bound = device.AttachBinding(authenticated);
        using var sink = device.CreateOutputSink(bound);
        var capture = await sink.OpenAsync(adapter.ExistingOperation, default);
        Assert.True(capture.Append("stdout", "utf-8", Encoding.UTF8.GetBytes("合成")));
        await Assert.ThrowsAsync<IOException>(() => capture.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        capture.Dispose();
        var status = await sink.ReconcileAsync("operation-1");
        Assert.Equal("receiving", status.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("durableThrough").ValueKind);
        Assert.Equal(new[] { "POST /v3/terminal/executors/executor-1/output-batches", "GET /v3/terminal/sessions/session-1/tool-output-status" }, deviceRequests);
        Assert.Single(adapter.Sent); Assert.Single(adapter.Received);
        Assert.Equal("commit_unknown", (await Assert.ThrowsAsync<WireProtocolException>(() => sink.OpenAsync(adapter.ExistingOperation, default))).Code);
        sink.ReleaseCapture("operation-1");
        Assert.Equal("source_unavailable", (await Assert.ThrowsAsync<TansrProtocolException>(() => sink.ReconcileAsync("operation-1"))).Code);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("application")]
    [InlineData("user")]
    [InlineData("revision")]
    public async Task TypedAttachRejectsWrongEndpointOrAnyFullScopeComponent(string difference)
    {
        var adapter = new TerminalTestAdapter(); using var http = ControllerHttp(adapter);
        using var controller = new TerminalConnection(Options(), true, http);
        var binding = await controller.BindAsync(Request(adapter));
        var scope = ExecutionFixture.Scope(user: difference == "user" ? "other" : "user", revision: difference == "revision" ? "2" : "1", application: difference == "application" ? "other" : "app");
        using var device = new TerminalConnection(Options("device-only", () => scope, difference == "endpoint" ? 34568 : 34567), true);
        var error = Record.Exception(() => device.AttachBinding(binding));
        Assert.True(error is TansrProtocolException or WireProtocolException);
    }

    [Fact]
    public void PublicOutputViewPreservesInterleavedUtf8AndReturnsDefensiveRawBytes()
    {
        var reference = JsonSerializer.SerializeToElement(new { operationId = "operation-1", requestDigest = new string('b', 64) });
        var golden = TerminalCandidateContractTests.Goldens().GetProperty("semantic").GetProperty("interleavedUtf8");
        JsonElement Block(JsonElement block) => TerminalJson.Object(w =>
        { w.WriteString("contract", TerminalCandidateContract.Protocol); w.WriteString("type", "output.block"); TerminalJson.Field(w, "operation", reference); TerminalJson.Field(w, "block", block); });
        using var view = new TerminalOutputView(reference);
        var blocks = golden.GetProperty("blocks");
        var first = Assert.Single(view.ApplyEvent(Block(blocks[0]))); Assert.Equal("", first.Text);
        var before = first.Bytes; before[0] = 0; Assert.NotEqual(0, first.Bytes[0]);
        Assert.Equal("x", Assert.Single(view.ApplyEvent(Block(blocks[1]))).Text);
        Assert.Equal("😀", Assert.Single(view.ApplyEvent(Block(blocks[2]))).Text);
        Assert.Empty(view.ApplyEvent(Block(blocks[2])));
        var status = TerminalJson.Object(w =>
        {
            w.WriteString("contract", TerminalCandidateContract.Protocol); TerminalJson.Field(w, "operation", reference); w.WriteString("state", "complete");
            w.WriteString("acceptedThrough", "2"); w.WriteNull("durableThrough"); w.WriteString("retainedFrom", "0");
            w.WriteString("nextByteOffset", "5"); TerminalJson.Field(w, "seal", golden.GetProperty("seal"));
        });
        view.ApplyEvent(TerminalJson.Object(w =>
        { w.WriteString("contract", TerminalCandidateContract.Protocol); w.WriteString("type", "output.status"); TerminalJson.Field(w, "status", status); }));
        Assert.True(view.SealVerified); Assert.False(view.HasPresentationGap); Assert.Equal(2, view.LastSequence);
        Assert.Equal(JsonValueKind.Null, view.LastStatus!.Value.GetProperty("durableThrough").ValueKind);
        view.Dispose(); Assert.Throws<ObjectDisposedException>(() => view.ApplyEvent(Block(blocks[2])));
    }

    [Fact]
    public async Task CancelledUncooperativeObserverRetainsBindingSlotUntilCallbackReallyEnds()
    {
        var adapter = new TerminalTestAdapter(); using var controllerHttp = ControllerHttp(adapter);
        using var controller = new TerminalConnection(Options(), true, controllerHttp);
        var authenticated = await controller.BindAsync(Request(adapter));
        var reference = Reference(adapter.ExistingOperation); var calls = 0;
        using var deviceHttp = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Sse(StatusEvent(adapter, reference))); }));
        using var device = new TerminalConnection(Options("device-only"), true, deviceHttp);
        var binding = device.AttachBinding(authenticated);
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observing = device.ObserveOutputAsync(binding, reference, null, async (_, _) => { entered.SetResult(); await release.Task; }, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            Assert.False(observing.IsCompleted);
            for (var attempt = 0; attempt < 5; attempt++)
                Assert.Equal("observation_already_active", (await Assert.ThrowsAsync<TansrProtocolException>(() => device.ObserveOutputAsync(binding, reference, null, (_, _) => Task.CompletedTask))).Code);
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("event_stream_disconnected", (await Assert.ThrowsAsync<TansrProtocolException>(() => device.ObserveOutputAsync(binding, reference, null, (_, _) => Task.CompletedTask))).Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DisposedSinksRetainBoundedConnectionCapacityUntilIgnoredCancellationHttpSettles()
    {
        var adapter = new TerminalTestAdapter(); using var controllerHttp = ControllerHttp(adapter);
        using var controller = new TerminalConnection(Options(), true, controllerHttp);
        var authenticated = await controller.BindAsync(Request(adapter));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var entries = 0;
        using var deviceHttp = new HttpClient(new Handler(async (_, _) =>
        {
            if (Interlocked.Increment(ref entries) == 8) allEntered.TrySetResult();
            await release.Task; return Json(adapter.Status(Reference(adapter.ExistingOperation)));
        }));
        using var device = new TerminalConnection(Options("device-only"), true, deviceHttp);
        var binding = device.AttachBinding(authenticated); var sinks = new List<TerminalOutputSink>();
        var captures = new List<Tansr.Sdk.Execution.IExecutionOutputCapture>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var sink = device.CreateOutputSink(binding); sinks.Add(sink);
                var capture = await sink.OpenAsync(ExecutionFixture.Operation("blocked-" + i), default); captures.Add(capture);
                Assert.True(capture.Append("stdout", "binary", [1]));
            }
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var sink in sinks) sink.Dispose();
            for (var attempt = 0; attempt < 5; attempt++)
                Assert.Equal("capacity_exceeded", Assert.Throws<TansrProtocolException>(() => device.CreateOutputSink(binding)).Code);
            Assert.Equal(8, entries);
        }
        finally { release.TrySetResult(); foreach (var capture in captures) capture.Dispose(); foreach (var sink in sinks) sink.Dispose(); }
        foreach (var capture in captures)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        foreach (var sink in sinks)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        using var next = device.CreateOutputSink(binding);
    }

    [Fact]
    public async Task PreCancelledReconciliationDoesNotStopOriginalCaptureOrItsNextAppend()
    {
        var adapter = new TerminalTestAdapter(); using var controllerHttp = ControllerHttp(adapter);
        using var controller = new TerminalConnection(Options(), true, controllerHttp);
        var authenticated = await controller.BindAsync(Request(adapter));
        var reads = 0;
        using var deviceHttp = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) { reads++; return Json(adapter.Status(Reference(adapter.ExistingOperation))); }
            return Json(await adapter.SendBatchAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(token)), token));
        }));
        using var device = new TerminalConnection(Options("device-only"), true, deviceHttp);
        using var sink = device.CreateOutputSink(device.AttachBinding(authenticated));
        using var capture = await sink.OpenAsync(adapter.ExistingOperation, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.ReconcileAsync("operation-1", new CancellationToken(true)));
        Assert.False(capture.Completion.IsCompleted);
        Assert.True(capture.Append("stdout", "utf-8", Encoding.UTF8.GetBytes("still captured")));
        await capture.SealAsync(false, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("still captured", Encoding.UTF8.GetString(adapter.Received.SelectMany(x => Convert.FromBase64String(x.GetProperty("base64").GetString()!)).ToArray()));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task CancellingReconciliationReturnsWhileOriginalBlockedPumpsRetainAllEightSlots()
    {
        var adapter = new TerminalTestAdapter(); using var controllerHttp = ControllerHttp(adapter);
        using var controller = new TerminalConnection(Options(), true, controllerHttp);
        var authenticated = await controller.BindAsync(Request(adapter));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var entries = 0; var reads = 0;
        using var deviceHttp = new HttpClient(new Handler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref reads);
            if (Interlocked.Increment(ref entries) == 8) allEntered.TrySetResult();
            await release.Task; return Json(adapter.Status(Reference(adapter.ExistingOperation)));
        }));
        using var device = new TerminalConnection(Options("device-only"), true, deviceHttp);
        var binding = device.AttachBinding(authenticated); var sinks = new List<TerminalOutputSink>();
        var captures = new List<Tansr.Sdk.Execution.IExecutionOutputCapture>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var sink = device.CreateOutputSink(binding); sinks.Add(sink);
                var capture = await sink.OpenAsync(ExecutionFixture.Operation("reconcile-" + i), default); captures.Add(capture);
                Assert.True(capture.Append("stdout", "binary", [1]));
            }
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var reconciling = sinks[0].ReconcileAsync("reconcile-0", cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciling.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(captures[0].Completion.IsCompleted); Assert.Equal(0, reads);
            foreach (var sink in sinks) sink.Dispose();
            Assert.Equal("capacity_exceeded", Assert.Throws<TansrProtocolException>(() => device.CreateOutputSink(binding)).Code);
            Assert.Equal(8, entries);
        }
        finally { release.TrySetResult(); foreach (var capture in captures) capture.Dispose(); foreach (var sink in sinks) sink.Dispose(); }
        foreach (var capture in captures)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        foreach (var sink in sinks)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        using var next = device.CreateOutputSink(binding);
    }

    [Fact]
    public void PublicOperationWrappersPreserveProtectedJournalValuesAndRejectUnknownControls()
    {
        using var client = new TansrClient(Options()); var control = new TerminalSessionControl(client, enablePreview: true);
        SessionConfigurationOperation operation;
        using (var document = JsonDocument.Parse("{\"model\":\"trusted-alias\",\"thinking\":null}"))
            operation = control.CreateConfigurationOperation("session-1", "stable-request", 4, document.RootElement);
        Assert.False(operation.Attempted); Assert.Equal("stable-request", operation.Request.GetProperty("requestId").GetString());
        var recovered = control.RestoreConfigurationOperation(operation.Request, operation.Scope);
        Assert.True(recovered.Attempted); Assert.Equal(WireJson.CanonicalString(operation.Request), WireJson.CanonicalString(recovered.Request));
        Assert.Throws<WireProtocolException>(() => control.CreateConfigurationOperation("session-1", "stable-request", 4,
            JsonSerializer.SerializeToElement(new { systemPrompt = "unsupported field" })));
    }
}
