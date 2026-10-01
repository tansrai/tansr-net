using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Client;

public sealed class SessionEventTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string?> EventCursors { get; } = [];
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                lock (EventCursors) EventCursors.Add(request.Headers.TryGetValues("Last-Event-ID", out var values) ? values.Single() : null);
            return Task.FromResult(respond(request));
        }
    }
    private sealed class FragmentedStream(byte[] bytes, int fragment = 1) : MemoryStream(bytes)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => base.ReadAsync(buffer, offset, Math.Min(count, fragment), ct);
    }
    private sealed class WaitingStream : Stream
    {
        public TaskCompletionSource<bool> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { Reading.TrySetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class DisposeReleasedStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource<int> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Assert.True(bytes.Length <= count); bytes.CopyTo(buffer, offset); Reading.TrySetResult(true);
            // An injected stream may ignore the token and finish normally when Dispose unblocks it.
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
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Events(Stream stream) => new(HttpStatusCode.OK)
    { Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
    private static HttpResponseMessage Events(string text) => Events(new FragmentedStream(Encoding.UTF8.GetBytes(text)));
    private static string Frame(int seq, string type, string extra = "") => "id: " + seq + "\ndata: {\"sessionId\":\"s\",\"seq\":" + seq + ",\"type\":\"" + type + "\"" + extra + "}\n\n";
    private static HttpResponseMessage Created() => Json("{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":0}");
    private static TansrClientOptions Options() => new()
    {
        BaseUri = new("https://serve.test/"),
        TokenProvider = _ => Task.FromResult("token"),
        ReconnectDelay = TimeSpan.Zero,
        MaxReconnectAttempts = 1
    };

    [Fact]
    public async Task EmptyLogSentinelDoesNotSuppressTheFirstEvent()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post
            ? Json("{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":-1}") : Events(Frame(0, "session.ended")));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<AgentEvent>();
        Assert.Equal(-1, session.LastSequence);
        await session.ObserveAsync((item, _) => { seen.Add(item); return Task.CompletedTask; });
        Assert.Equal("0", Assert.Single(seen).Id);
        Assert.Equal(0, session.LastSequence);
        Assert.Null(Assert.Single(handler.EventCursors));
    }

    [Fact]
    public async Task Utf8CrlfAndMultilineDataSurviveEveryByteBoundary()
    {
        var text = ": heartbeat\r\n\r\nid: 1\r\ndata: {\"sessionId\":\"s\",\"seq\":1,\r\ndata: \"type\":\"msg.text.delta\",\"text\":\"你😀\"}\r\n\r\n" + Frame(2, "session.ended");
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(text));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<AgentEvent>();
        await session.ObserveAsync((item, _) => { seen.Add(item); return Task.CompletedTask; });
        Assert.Equal(2, seen.Count); Assert.Equal("你😀", seen[0].Data.GetProperty("text").GetString());
        Assert.Equal("msg.text.delta", seen[0].Name); Assert.Equal(2, session.LastSequence);
    }

    [Fact]
    public async Task ReconnectUsesLastDeliveredIdAndDeduplicatesReplay()
    {
        int connections = 0;
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() :
            Events(Interlocked.Increment(ref connections) == 1 ? Frame(1, "msg.text.delta", ",\"text\":\"one\"")
                : Frame(1, "msg.text.delta", ",\"text\":\"one\"") + Frame(2, "session.ended")));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<string?>();
        await session.ObserveAsync((item, _) => { seen.Add(item.Id); return Task.CompletedTask; });
        Assert.Equal(new string?[] { null, "1" }, handler.EventCursors);
        Assert.Equal(new[] { "1", "2" }, seen);
        Assert.DoesNotContain(handler.Paths, p => p.Contains("interrupt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultipleObserversReceiveIndependentCompleteStreams()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(Frame(1, "msg.text.delta") + Frame(2, "session.ended")));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new()); int one = 0, two = 0;
        await Task.WhenAll(session.ObserveAsync((_, _) => { one++; return Task.CompletedTask; }), session.ObserveAsync((_, _) => { two++; return Task.CompletedTask; }));
        Assert.Equal(2, one); Assert.Equal(2, two); Assert.Equal(2, handler.EventCursors.Count);
        Assert.All(handler.EventCursors, Assert.Null);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("control")]
    [InlineData("gap")]
    [InlineData("terminal")]
    public async Task PrincipalChangeDuringObserverStopsBufferedFramesAndDoesNotAdvanceCursor(string firstKind)
    {
        var first = firstKind switch
        {
            "control" => "event: server.tool.request\nid: 1\ndata: {\"ts\":123,\"callId\":\"c\",\"name\":\"local\",\"args\":{}}\n\n",
            "gap" => "event: server.replay.gap\ndata: {\"type\":\"server.replay.gap\",\"sessionId\":\"s\",\"reason\":\"evicted\"}\n\n",
            "terminal" => Frame(1, "session.ended"),
            _ => Frame(1, "msg.text.delta", ",\"text\":\"user-a-first\"")
        };
        var bytes = Encoding.UTF8.GetBytes(first + Frame(2, "session.ended"));
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(new FragmentedStream(bytes, bytes.Length)));
        var principal = "app/user-a";
        var options = Options(); options.PrincipalProvider = () => principal;
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(options, http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<AgentEvent>();

        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((item, _) =>
        {
            seen.Add(item); principal = "app/user-b"; return Task.CompletedTask;
        }, new EventStreamOptions { StopOnGap = false }));

        Assert.Equal("context_changed", error.Code);
        Assert.Single(seen); Assert.Single(handler.EventCursors);
        Assert.Equal(0, session.LastSequence);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("interrupt", StringComparison.Ordinal) || path.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TicketRefreshForTheSamePrincipalPreservesBufferedEvents()
    {
        var bytes = Encoding.UTF8.GetBytes(Frame(1, "msg.text.delta", ",\"text\":\"one\"") + Frame(2, "session.ended"));
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(new FragmentedStream(bytes, bytes.Length)));
        var token = "ticket-original";
        var options = Options(); options.PrincipalProvider = () => "app/user-a"; options.TokenProvider = _ => Task.FromResult(token);
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(options, http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<string?>();

        await session.ObserveAsync((item, _) => { seen.Add(item.Id); token = "ticket-renewed"; return Task.CompletedTask; });

        Assert.Equal(new[] { "1", "2" }, seen); Assert.Equal(2, session.LastSequence);
        Assert.Single(handler.EventCursors);
    }

    [Fact]
    public async Task NamedControlPayloadIsPreservedAndGapIsNotInventedAsASequence()
    {
        var control = "event: server.tool.request\nid: 1\ndata: {\"ts\":123,\"callId\":\"c\",\"name\":\"local\",\"args\":{},\"ttlMs\":1000,\"deadlineAt\":1123}\n\n";
        var gap = "event: server.replay.gap\ndata: {\"type\":\"server.replay.gap\",\"sessionId\":\"s\",\"reason\":\"evicted\"}\n\n";
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(control + gap));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<AgentEvent>();
        var failure = await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((item, _) => { seen.Add(item); return Task.CompletedTask; }));
        Assert.Equal("event_replay_gap", failure.Code); Assert.Equal("c", seen[0].Data.GetProperty("payload").GetProperty("callId").GetString());
        Assert.Null(seen[1].Id); Assert.False(seen[1].Data.TryGetProperty("payload", out _)); Assert.Single(handler.EventCursors);
    }

    [Theory]
    [InlineData("id: 01\ndata: {\"sessionId\":\"s\",\"seq\":1,\"type\":\"x\"}\n\n", "invalid_event_id")]
    [InlineData("id: -1\ndata: {\"sessionId\":\"s\",\"seq\":-1,\"type\":\"x\"}\n\n", "invalid_event_id")]
    [InlineData("id: 0\ndata: {\"sessionId\":\"s\",\"seq\":-1,\"type\":\"x\"}\n\n", "invalid_response")]
    [InlineData("id: 1\ndata: {\"sessionId\":\"other\",\"seq\":1,\"type\":\"x\"}\n\n", "invalid_response")]
    [InlineData("id: 1\ndata: {\"sessionId\":\"s\",\"seq\":1,\"type\":\"x\"}\n", "sse_incomplete_frame")]
    public async Task CorruptFramesFailWithoutRetry(string frame, string code)
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(frame));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((_, _) => Task.CompletedTask));
        Assert.Equal(code, error.Code); Assert.Single(handler.EventCursors);
    }

    [Fact]
    public async Task InvalidUtf8IsNotSilentlyReplaced()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(new FragmentedStream([100, 97, 116, 97, 58, 32, 0xc3, 0x28, 10, 10])));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        Assert.Equal("invalid_utf8", (await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((_, _) => Task.CompletedTask))).Code);
    }

    [Fact]
    public async Task OversizedFrameIsBoundedEvenWithoutANewline()
    {
        var options = Options(); options.MaxEventBytes = 64;
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events("data: " + new string('x', 1000)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(options, http); var session = await client.CreateSessionAsync(new());
        Assert.Equal("sse_frame_too_large", (await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((_, _) => Task.CompletedTask))).Code);
    }

    [Fact]
    public async Task CancelingObservationDisposesOnlyTheStream()
    {
        using var waiting = new WaitingStream();
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(waiting));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        using var cancel = new CancellationTokenSource();
        var observe = session.ObserveAsync((_, _) => Task.CompletedTask, cancel.Token);
        await waiting.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observe);
        Assert.True(waiting.Disposed); Assert.DoesNotContain(handler.Paths, p => p.Contains("interrupt", StringComparison.Ordinal) || p.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationOrIdleExpiryCannotBecomeNormalDisposedStreamCompletion(bool callerCancels, bool returnsBytes)
    {
        using var stream = new DisposeReleasedStream(returnsBytes ? Encoding.UTF8.GetBytes(Frame(1, "session.ended")) : []);
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(stream));
        var options = Options(); options.MaxReconnectAttempts = 0;
        options.StreamIdleTimeout = callerCancels ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(30);
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(options, http);
        var session = await client.CreateSessionAsync(new()); var seen = new List<AgentEvent>();
        using var cancellation = new CancellationTokenSource();
        var observe = session.ObserveAsync((item, _) => { seen.Add(item); return Task.CompletedTask; }, cancellation.Token);
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (callerCancels)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observe.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            var error = await Assert.ThrowsAsync<TansrProtocolException>(() => observe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("stream_idle_timeout", error.Code);
        }

        Assert.Empty(seen); Assert.Equal(0, session.LastSequence); Assert.True(stream.Disposed);
        Assert.Single(handler.EventCursors);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("interrupt", StringComparison.Ordinal) || path.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObserverFailureIsNotMistakenForTransientNetworkFailure()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(Frame(1, "msg.text.delta")));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        var expected = new TansrProtocolException("network_error");
        var actual = await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((_, _) => throw expected));
        Assert.Same(expected, actual); Assert.Single(handler.EventCursors);
    }

    [Fact]
    public async Task CleanUnexpectedEofHasABoundedReconnectBudget()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : Events(": heartbeat\n\n"));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        Assert.Equal("event_stream_disconnected", (await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((_, _) => Task.CompletedTask))).Code);
        Assert.Equal(2, handler.EventCursors.Count);
    }
}
