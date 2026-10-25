using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Tests.Client;

// EventSource 监听器作用于整个进程；本组和示例收集器测试均须避免
// 与其它会话调用并行，包括其它测试集合中的调用。
[CollectionDefinition("ConnectionTiming", DisableParallelization = true)]
public sealed class ConnectionTimingCollection { }

[Collection("ConnectionTiming")]
public sealed class ConnectionTimingTests
{
    private const string SourceName = "Tansr-Sdk-ConnectionTiming";
    private const string CapabilitiesPath = "/api/capabilities/sessions?protocol=sdk2-ext-v1";
    private const string SessionId = "synthetic-session-not-for-diagnostics";
    private const string Available = "{\"contracts\":[{\"availability\":\"source-required\",\"contract\":\"sdk2-offload-v1\"}],\"protocol\":\"sdk2-ext-v1\"}";
    private const string Created = "{\"sessionId\":\"synthetic-session-not-for-diagnostics\",\"resumed\":false,\"lastSeq\":0,\"contract\":\"sdk2-offload-v1\",\"availability\":\"source-required\"}";
    private const string Resumed = "{\"sessionId\":\"synthetic-session-not-for-diagnostics\",\"resumed\":true,\"lastSeq\":0,\"contract\":\"sdk2-offload-v1\",\"availability\":\"source-required\"}";
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DisabledSourceDoesNotCreateSamplesOrChangeRequests()
    {
        Assert.Null(ConnectionTiming.Start(false));
        Assert.Null(ConnectionTiming.Start(true));
        using var handler = new Handler((_, _) => Task.FromResult(Json(Created)));
        using var http = UnifiedStamp.Client(handler);
        using var client = new TansrClient(Options(sdk2: false), http);

        var session = await client.CreateSessionAsync(new());

        Assert.Equal(SessionId, session.Id);
        Assert.Equal(new[] { "POST /api/sessions" }, handler.Requests);
        Assert.False(TansrConnectionEventSource.Log.IsEnabled());
    }

    [Fact]
    public async Task CapabilityAndCreateHeadersAreObservedBeforeBodiesComplete()
    {
        using var listener = new TimingListener();
        using var capabilityBody = new GatedBodyStream(Available);
        using var createBody = new GatedBodyStream(Created);
        using var handler = new Handler((request, _) => Task.FromResult(Json(
            request.RequestUri!.PathAndQuery == CapabilitiesPath ? capabilityBody : createBody)));
        using var http = UnifiedStamp.Client(handler);
        using var client = new TansrClient(Options(), http);
        var before = Stopwatch.GetTimestamp();
        var create = client.CreateSessionAsync(new() { RequestId = "synthetic-request-not-for-diagnostics" });
        var bodyChecksCompleted = false;
        try
        {
            await capabilityBody.ReadStarted.Task.WaitAsync(TestDeadline);
            var capabilityWaiting = listener.ReadEvents();
            Assert.Contains(capabilityWaiting, item => item.Phase == "CapabilityHeaders");
            Assert.Contains(capabilityWaiting, item => item.Phase == "CapabilityHeadersValidated");
            Assert.DoesNotContain(capabilityWaiting, item => item.Phase is "CapabilityBody" or "CreateRequestReady");
            Assert.Equal(new[] { "GET " + CapabilitiesPath }, handler.Requests);
            Assert.False(create.IsCompleted);

            capabilityBody.Release();
            await createBody.ReadStarted.Task.WaitAsync(TestDeadline);
            var createWaiting = listener.ReadEvents();
            Assert.Contains(createWaiting, item => item.Phase == "CapabilityBody");
            Assert.Contains(createWaiting, item => item.Phase == "CreateHeaders");
            Assert.Contains(createWaiting, item => item.Phase == "CreateHeadersValidated");
            Assert.DoesNotContain(createWaiting, item => item.Phase is "CreateBody" or "Complete");
            Assert.False(create.IsCompleted);
            bodyChecksCompleted = true;
        }
        finally
        {
            capabilityBody.Release();
            createBody.Release();
            // 边界断言失败时也须在下一测试启用监听器前等本次调用结束，
            // 同时保留原断言异常，不让清理异常将其替换。
            try { await create.WaitAsync(TestDeadline); }
            catch when (!bodyChecksCompleted) { }
        }

        Assert.Equal(SessionId, (await create.WaitAsync(TestDeadline)).Id);
        var after = Stopwatch.GetTimestamp();
        var events = listener.ReadEvents();
        Assert.Equal(new[]
        {
            "CreateBegin", "RequestSerialized", "IdentityBeforeToken", "TokenReceived", "AccessValidated",
            "DiscoveryGateWait", "DiscoveryGateEntered", "CapabilityRequestReady", "CapabilityHeaders",
            "CapabilityHeadersValidated", "CapabilityBody", "CapabilityParsed", "DiscoveryGateReleased",
            "CreateRequestReady", "CreateHeaders", "CreateHeadersValidated", "CreateBody", "CreateParsed", "Complete"
        }, events.Select(item => item.Phase));
        Assert.Single(events.Select(item => item.Sample).Distinct(StringComparer.Ordinal));
        Assert.All(events, item => Assert.InRange(item.Timestamp, before, after));
        for (var index = 1; index < events.Length; index++)
            Assert.True(events[index].Timestamp >= events[index - 1].Timestamp);
        Assert.Equal(new[] { "GET " + CapabilitiesPath, "POST /api/sessions" }, handler.Requests);
    }

    [Fact]
    public async Task ReusedClientKeepsTokenDiscoveryCacheAndDistinctSamples()
    {
        using var listener = new TimingListener();
        var token = "synthetic-original-token-not-for-diagnostics";
        var options = Options();
        options.TokenProvider = _ => Task.FromResult(token);
        var creates = 0;
        using var handler = new Handler((request, _) => Task.FromResult(Json(
            request.RequestUri!.PathAndQuery == CapabilitiesPath ? Available : ++creates == 1 ? Created : Resumed)));
        using var http = UnifiedStamp.Client(handler);
        using var client = new TansrClient(options, http);

        await client.CreateSessionAsync(new() { RequestId = "first-request" });
        await client.CreateSessionAsync(new() { ResumeSessionId = SessionId });
        token = "synthetic-renewed-token-not-for-diagnostics";
        await client.CreateSessionAsync(new() { ResumeSessionId = SessionId });

        Assert.Equal(new[]
        {
            "GET " + CapabilitiesPath, "POST /api/sessions", "POST /api/sessions",
            "GET " + CapabilitiesPath, "POST /api/sessions"
        }, handler.Requests);
        var samples = listener.ReadEvents().GroupBy(item => item.Sample).Select(group => group.ToArray()).ToArray();
        Assert.Equal(3, samples.Length);
        Assert.Equal("CreateBegin", samples[0][0].Phase);
        Assert.Equal("ResumeBegin", samples[1][0].Phase);
        Assert.Equal("ResumeBegin", samples[2][0].Phase);
        Assert.DoesNotContain(samples[1], item => item.Phase.StartsWith("Capability", StringComparison.Ordinal));
        Assert.Single(samples[0], item => item.Phase == "CapabilityRequestReady");
        Assert.Single(samples[2], item => item.Phase == "CapabilityRequestReady");
        Assert.All(samples, sample => Assert.Equal("Complete", sample[^1].Phase));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ThrowingListenersCannotChangeSuccessFailureOrCancellation(string outcome)
    {
        using var listener = new TimingListener { ThrowOnStage = true };
        var original = new InvalidOperationException("synthetic original handler failure");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.PathAndQuery == CapabilitiesPath) return Json(Available);
            entered.TrySetResult();
            if (outcome == "failure") throw original;
            if (outcome == "cancel") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json(Created);
        });
        using var http = UnifiedStamp.Client(handler);
        using var client = new TansrClient(Options(), http);
        using var cancellation = new CancellationTokenSource();

        var create = client.CreateSessionAsync(new() { RequestId = "original-request" }, cancellation.Token);
        if (outcome == "cancel")
        {
            await entered.Task.WaitAsync(TestDeadline);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create.WaitAsync(TestDeadline));
        }
        else if (outcome == "failure")
            Assert.Same(original, await Record.ExceptionAsync(() => create.WaitAsync(TestDeadline)));
        else Assert.Equal(SessionId, (await create.WaitAsync(TestDeadline)).Id);

        Assert.True(listener.ThrownStages > 0);
        Assert.Equal(new[] { "GET " + CapabilitiesPath, "POST /api/sessions" }, handler.Requests);
    }

    [Fact]
    public async Task CanceledCreateBodyKeepsCancellationAndDoesNotReportCompletion()
    {
        using var listener = new TimingListener();
        using var body = new GatedBodyStream(Created);
        using var handler = new Handler((request, _) => Task.FromResult(
            request.RequestUri!.PathAndQuery == CapabilitiesPath ? Json(Available) : Json(body)));
        using var http = UnifiedStamp.Client(handler);
        using var client = new TansrClient(Options(), http);
        using var cancellation = new CancellationTokenSource();

        var create = client.CreateSessionAsync(new() { RequestId = "canceled-request" }, cancellation.Token);
        await body.ReadStarted.Task.WaitAsync(TestDeadline);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create.WaitAsync(TestDeadline));
        var events = listener.ReadEvents();
        Assert.Contains(events, item => item.Phase == "CreateHeaders");
        Assert.Equal("Canceled", events[^1].Phase);
        Assert.DoesNotContain(events, item => item.Phase is "CreateBody" or "CreateParsed" or "Complete" or "Failed");
        Assert.Equal(new[] { "GET " + CapabilitiesPath, "POST /api/sessions" }, handler.Requests);
    }

    [Fact]
    public async Task MissingCapabilityStillRejectsCreateAndReportsFailure()
    {
        using var listener = new TimingListener();
        using var handler = new Handler((_, _) => Task.FromResult(Json(
            "{\"contracts\":[{\"availability\":\"legacy-complete\",\"contract\":\"sdk1\"}],\"protocol\":\"sdk2-ext-v1\"}")));
        using var http = UnifiedStamp.Client(handler);
        using var client = new TansrClient(Options(), http);

        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => client.CreateSessionAsync(new() { RequestId = "rejected-request" }));

        Assert.Equal("unsupported_capability", error.Code);
        Assert.Equal(new[] { "GET " + CapabilitiesPath }, handler.Requests);
        var events = listener.ReadEvents();
        Assert.Equal("Failed", events[^1].Phase);
        Assert.Contains(events, item => item.Phase == "DiscoveryGateReleased");
        Assert.DoesNotContain(events, item => item.Phase is "CreateRequestReady" or "Complete");
    }

    private static TansrClientOptions Options(bool sdk2 = true) => new()
    {
        BaseUri = new Uri("https://synthetic-private-host.invalid/"),
        TokenProvider = _ => Task.FromResult("synthetic-private-token-not-for-diagnostics"),
        PrincipalProvider = () => "synthetic-private-app/user",
        SessionContract = sdk2 ? SessionContract.Sdk2OffloadV1 : SessionContract.Sdk1
    };

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json(Stream stream) => new(HttpStatusCode.OK)
    { Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method.Method + " " + request.RequestUri!.PathAndQuery);
            return respond(request, cancellationToken);
        }
    }

    private sealed record TimingEvent(string Sample, string Phase, long Timestamp);
    private sealed record RawTimingEvent(string[] Names, object?[] Payload);

    private sealed class TimingListener : EventListener
    {
        // EventListener 的基类构造期间也可能派发事件。
        private ConcurrentQueue<RawTimingEvent>? events;
        private ConcurrentQueue<RawTimingEvent> Events => LazyInitializer.EnsureInitialized(ref events);
        private int thrownStages;
        internal bool ThrowOnStage { get; init; }
        internal int ThrownStages => Volatile.Read(ref thrownStages);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == SourceName) EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != SourceName || eventData.EventId != 1) return;
            Events.Enqueue(new RawTimingEvent(eventData.PayloadNames?.ToArray() ?? [], eventData.Payload?.ToArray() ?? []));
            if (!ThrowOnStage) return;
            Interlocked.Increment(ref thrownStages);
            throw new InvalidOperationException("synthetic listener failure");
        }

        internal TimingEvent[] ReadEvents() => Events.ToArray().Select(item =>
        {
            Assert.Equal(new[] { "sample", "phase", "timestamp" }, item.Names);
            Assert.Equal(3, item.Payload.Length);
            var sample = Assert.IsType<string>(item.Payload[0]);
            var phase = Assert.IsType<string>(item.Payload[1]);
            var timestamp = Assert.IsType<long>(item.Payload[2]);
            Assert.True(Guid.TryParseExact(sample, "N", out _));
            Assert.Contains(phase, Enum.GetNames<ConnectionTimingPhase>());
            return new TimingEvent(sample, phase, timestamp);
        }).ToArray();
    }

    // 处理器已返回成功响应和响应头；测试显式放行之前，
    // 响应体读取保持等待，以区分响应头与正文完成两个时点。
    private sealed class GatedBodyStream(string text) : Stream
    {
        private readonly MemoryStream inner = new(Encoding.UTF8.GetBytes(text), writable: false);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => release.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { release.TrySetResult(); inner.Dispose(); }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
