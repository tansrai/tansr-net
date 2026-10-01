using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalCandidateHttpTransportTests
{
    private static TansrClientOptions Options(Func<Task<string>>? token = null, Func<JsonElement>? scope = null) => new()
    {
        BaseUri = new Uri("http://127.0.0.1:34567/"),
        AllowInsecureLoopback = true,
        TokenProvider = _ => token?.Invoke() ?? Task.FromResult("synthetic-short-token"),
        PrincipalProvider = () => "fixture-only",
        ExecutionScopeProvider = scope ?? (() => ExecutionFixture.Scope())
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }
    private static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(WireJson.CanonicalString(value), Encoding.UTF8, "application/json") };
    private static JsonElement Reference => TerminalJson.Object(w => { w.WriteString("operationId", "operation-1"); w.WriteString("requestDigest", new string('a', 64)); });
    private static JsonElement Session => TerminalJson.Object(w => { w.WriteString("sessionContract", "sdk2-offload-v1"); w.WriteString("sessionId", "session-1"); });

    [Fact]
    public async Task RealCandidateRouteGrammarReauthenticatesEachControlWithoutFallback()
    {
        var adapter = new TerminalTestAdapter(); var paths = new List<string>(); var tokens = 0;
        using var http = UnifiedStamp.Client(new Handler(async request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            Assert.Equal("token-" + paths.Count, request.Headers.Authorization!.Parameter);
            Assert.False(request.Headers.Contains("Last-Event-ID"));
            if (request.Method == HttpMethod.Get && paths.Count == 1) return Json(await adapter.GetCapabilitiesAsync(default));
            if (request.Method == HttpMethod.Post)
            {
                var body = WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync());
                return Json(await adapter.BindAsync(body, default));
            }
            return Json(adapter.Status(Reference));
        }));
        using var transport = new TerminalCandidateHttpTransport(Options(() => Task.FromResult("token-" + ++tokens)), http);
        var client = new TerminalCandidateClient(transport, () => adapter.Scope);
        var original = await adapter.BindAsync();
        var request = TerminalJson.Object(w =>
        {
            w.WriteString("contract", "terminal-services-v1"); w.WriteString("requestId", "bind-request");
            TerminalJson.Field(w, "session", Session); TerminalJson.Field(w, "executionBinding", adapter.ExistingOperation.GetProperty("binding"));
            w.WriteStartArray("required"); w.WriteStringValue("execution-stream-v1"); w.WriteEndArray(); w.WriteStartArray("optional"); w.WriteEndArray();
        });
        await client.BindAsync(request);
        await transport.GetOutputStatusAsync(Session, Reference, default);
        Assert.Equal("/api/capabilities/terminal?contract=terminal-services-v1", paths[0]);
        Assert.Equal("/api/terminal/bindings", paths[1]);
        Assert.Equal("/api/terminal/sessions/session-1/tool-output-status?contract=terminal-services-v1&sessionContract=sdk2-offload-v1&operationId=operation-1&requestDigest=" + new string('a', 64), paths[2]);
        Assert.Equal(3, tokens);
    }
    private static string OutputFrame(JsonElement value, string? id = null) => "event: " + TerminalJson.Text(value, "type") + "\n" +
        (id == null ? "" : "id: " + id + "\n") + "data: " + WireJson.CanonicalString(value) + "\n\n";
    private static JsonElement StatusEvent() => TerminalJson.Object(w =>
    { w.WriteString("contract", "terminal-services-v1"); w.WriteString("type", "output.status"); TerminalJson.Field(w, "status", new TerminalTestAdapter().Status(Reference)); });
    private static HttpResponseMessage Sse(string data) => new(HttpStatusCode.OK)
    { Content = new StreamContent(new BytewiseStream(Encoding.UTF8.GetBytes(data))) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
    private sealed class BytewiseStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
    }
    private sealed class DisposeReleasedStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource<int> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool started;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (started) return Task.FromResult(0);
            started = true; Assert.True(bytes.Length <= count); bytes.CopyTo(buffer, offset);
            // A stream may unblock a pending read normally when Dispose closes the connection.
            // The next read is EOF so the unfixed product cannot spin or hang the regression.
            return released.Task;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { Disposed = true; released.TrySetResult(bytes.Length); base.Dispose(disposing); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IdleExpiryThatDisposesToNormalReadNeverEmitsTerminalEvents(bool executor, bool returnsBytes)
    {
        var value = executor ? JsonSerializer.SerializeToElement(new
        { contract = "terminal-services-v1", eventId = "1", type = "operations-available", executorId = "executor-1", connectionId = "connection-1", operation = (object?)null }) : StatusEvent();
        var bytes = returnsBytes ? Encoding.UTF8.GetBytes(OutputFrame(value, executor ? "1" : null)) : Array.Empty<byte>();
        using var stream = new DisposeReleasedStream(bytes);
        var requests = 0; var observed = 0;
        using var http = UnifiedStamp.Client(new Handler(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } });
        }));
        var options = Options(); options.StreamIdleTimeout = TimeSpan.FromMilliseconds(30);
        using var transport = new TerminalCandidateHttpTransport(options, http);
        Task Observe(JsonElement received, CancellationToken token) { observed++; return Task.CompletedTask; }

        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => (executor
            ? transport.ObserveExecutorAsync("sdk2-offload-v1", "executor-1", "connection-1", null, Observe, default)
            : transport.ObserveOutputAsync(Session, Reference, null, Observe, default)).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("stream_idle_timeout", error.Code);
        Assert.Equal(0, observed); Assert.Equal(1, requests); Assert.True(stream.Disposed);
    }
    [Fact]
    public async Task OutputUsesFull64BitAfterSequenceAndEofNeverBecomesCompletion()
    {
        var value = StatusEvent(); var observed = 0;
        using var http = UnifiedStamp.Client(new Handler(request =>
        {
            Assert.EndsWith("&afterSeq=9223372036854775807", request.RequestUri!.Query);
            Assert.False(request.Headers.Contains("Last-Event-ID")); return Task.FromResult(Sse(OutputFrame(value)));
        }));
        using var transport = new TerminalCandidateHttpTransport(Options(), http);
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => transport.ObserveOutputAsync(Session, Reference, long.MaxValue,
            (received, _) => { observed++; Assert.Equal(WireJson.CanonicalString(value), WireJson.CanonicalString(received)); return Task.CompletedTask; }, default));
        Assert.Equal("event_stream_disconnected", error.Code); Assert.Equal(1, observed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyServeRetryPreambleIsIgnoredForBothTerminalStreams(bool executor)
    {
        var value = executor ? JsonSerializer.SerializeToElement(new
        { contract = "terminal-services-v1", eventId = "9007199254740993", type = "operations-available", executorId = "executor-1", connectionId = "connection-1", operation = (object?)null }) : StatusEvent();
        var observed = 0;
        using var http = UnifiedStamp.Client(new Handler(request =>
        {
            if (executor) Assert.Equal("9007199254740992", request.Headers.GetValues("Last-Event-ID").Single());
            return Task.FromResult(Sse("retry: 1000\ndata:\n\n" + OutputFrame(value, executor ? "9007199254740993" : null)));
        }));
        using var transport = new TerminalCandidateHttpTransport(Options(), http);
        Task Observe(JsonElement received, CancellationToken token)
        { observed++; Assert.Equal(WireJson.CanonicalString(value), WireJson.CanonicalString(received)); return Task.CompletedTask; }
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => executor
            ? transport.ObserveExecutorAsync("sdk2-offload-v1", "executor-1", "connection-1", 9007199254740992L, Observe, default)
            : transport.ObserveOutputAsync(Session, Reference, null, Observe, default));
        Assert.Equal("event_stream_disconnected", error.Code); Assert.Equal(1, observed);
    }
    [Theory]
    [InlineData("event: operations-available\ndata:\n\n")]
    [InlineData("id: 0\ndata:\n\n")]
    [InlineData("data: not-json\n\n")]
    public async Task NamedIdentifiedOrNonemptyMalformedFramesAreNeverSwallowed(string data)
    {
        var observed = 0;
        using var http = UnifiedStamp.Client(new Handler(_ => Task.FromResult(Sse(data))));
        using var transport = new TerminalCandidateHttpTransport(Options(), http);
        await Assert.ThrowsAsync<WireProtocolException>(() => transport.ObserveExecutorAsync("sdk2-offload-v1", "executor-1", "connection-1", null,
            (_, _) => { observed++; return Task.CompletedTask; }, default));
        Assert.Equal(0, observed);
    }
    [Fact]
    public async Task OutputRejectsNotificationCursorAndBindingErrorsNeverRetry()
    {
        var requests = 0;
        using var http = UnifiedStamp.Client(new Handler(_ => { requests++; return Task.FromResult(Sse(OutputFrame(StatusEvent(), "0"))); }));
        using var transport = new TerminalCandidateHttpTransport(Options(), http);
        Assert.Equal("invalid_event_id", (await Assert.ThrowsAsync<WireProtocolException>(() => transport.ObserveOutputAsync(Session, Reference, null,
            (_, _) => throw new InvalidOperationException("Invalid frame must not reach consumer"), default))).Code);
        Assert.Equal(1, requests);
    }
    [Fact]
    public async Task TypedCandidateErrorPreservesReconciliationAndRedactsServiceData()
    {
        var requests = 0;
        var error = TerminalJson.Object(w => { w.WriteString("contract", "terminal-services-v1"); w.WriteString("requestId", "error-1"); w.WriteString("code", "commit_unknown"); w.WriteNumber("status", 409); w.WriteString("retryAction", "reconcile"); });
        using var http = UnifiedStamp.Client(new Handler(_ => { requests++; return Task.FromResult(Json(error, HttpStatusCode.Conflict)); }));
        using var transport = new TerminalCandidateHttpTransport(Options(), http);
        var actual = await Assert.ThrowsAsync<TansrHttpException>(() => transport.GetOutputStatusAsync(Session, Reference, default));
        Assert.Equal("commit_unknown", actual.Code); Assert.Equal("reconcile", actual.RetryAction); Assert.Equal(1, requests);
        Assert.DoesNotContain("synthetic", actual.ToString());
    }
    [Fact]
    public async Task CandidateExecutorStateUsesOnlyTheNarrowOriginalOperationRoute()
    {
        var operation = ExecutionFixture.Operation();
        var reference = JsonSerializer.SerializeToElement(new { operationId = operation.GetProperty("operationId").GetString(), requestDigest = operation.GetProperty("digest").GetString() });
        var state = JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", session = Session, execution = ExecutionFixture.Status(operation, ExecutionFixture.Receipt(operation)) });
        using var http = UnifiedStamp.Client(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/terminal/executors/executor-1/operations/operation-1?contract=terminal-services-v1&sessionContract=sdk2-offload-v1&sessionId=session-1&requestDigest=" + operation.GetProperty("digest").GetString() + "&connectionId=connection-1", request.RequestUri!.PathAndQuery);
            Assert.False(request.Headers.Contains("Last-Event-ID")); return Task.FromResult(Json(state));
        }));
        using var transport = new TerminalCandidateHttpTransport(Options(), http);
        var result = await transport.GetExecutionStateAsync(Session, reference, "executor-1", "connection-1", default);
        Assert.Equal(WireJson.CanonicalString(state), WireJson.CanonicalString(result));
    }
}
