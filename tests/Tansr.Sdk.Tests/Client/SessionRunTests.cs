using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Client;

public sealed class SessionRunTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private sealed class Feed : Stream
    {
        private readonly Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int offset;
        public void Push(string text) => Assert.True(queue.Writer.TryWrite(Encoding.UTF8.GetBytes(text)));
        public void End() => queue.Writer.TryComplete();
        public override async Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken ct)
        {
            while (current is null || offset == current.Length)
            {
                if (!await queue.Reader.WaitToReadAsync(ct)) return 0;
                if (!queue.Reader.TryRead(out current)) continue;
                offset = 0;
            }
            var length = Math.Min(count, current.Length - offset); Array.Copy(current, offset, buffer, start, length); offset += length; return length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Feed Events { get; } = new();
        public Feed? ReconnectedEvents { get; set; }
        public TaskCompletionSource<bool> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task<HttpResponseMessage>>? OnSend { get; set; }
        public string Status { get; set; } = "idle";
        public bool ChangeAfterListen { get; set; }
        public int Sends, Reads, Connections;
        public List<string> Paths { get; } = [];
        public List<string?> Cursors { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Paths) Paths.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Post && path == "/api/sessions") return Json("{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":-1}");
            if (path == "/api/sessions/s/events")
            {
                Connections++;
                Cursors.Add(request.Headers.TryGetValues("Last-Event-ID", out var ids) ? ids.Single() : null);
                return new(HttpStatusCode.OK)
                {
                    Content = new StreamContent(Connections > 1 ? ReconnectedEvents! : Events)
                    { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } }
                };
            }
            if (request.Method == HttpMethod.Get && path == "/api/sessions/s")
            {
                Reads++;
                return Json("{\"sessionId\":\"s\",\"endUserId\":\"user\",\"status\":\"" + Status + "\",\"live\":true,\"lastSeq\":" +
                    (ChangeAfterListen && Reads > 1 ? "0" : "-1") + ",\"createdAt\":\"2026-09-26T00:00:00Z\",\"lastActivityAt\":\"2026-09-26T00:00:00Z\"}");
            }
            if (request.Method == HttpMethod.Post && path == "/api/sessions/s/messages")
            {
                Assert.True(Connections > 0); Sends++; Sent.TrySetResult(true);
                return OnSend is null ? Accepted() : await OnSend(ct);
            }
            throw new InvalidOperationException("Unexpected fixture route.");
        }
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted) { Content = new StringContent("{\"sessionId\":\"s\",\"accepted\":true}", Encoding.UTF8, "application/json") };
    private static string Frame(int seq, string type, string turn = "turn-1", string extra = "")
        => "id: " + seq + "\ndata: {\"sessionId\":\"s\",\"seq\":" + seq + ",\"type\":\"" + type + "\",\"turnId\":\"" + turn + "\"" + extra + "}\n\n";
    private static TansrClient CreateClient(HttpClient http) => new(new TansrClientOptions
    {
        BaseUri = new("https://serve.test/"),
        TokenProvider = _ => Task.FromResult("fixture-token"),
        ReconnectDelay = TimeSpan.Zero,
        MaxReconnectAttempts = 1
    }, http);
    private static string Completed(int seq = 1, string turn = "turn-1") => Frame(seq, "turn.completed", turn, ",\"reason\":\"completed\"");

    [Fact]
    public async Task TerminalBeforeHttpAckCannotCompleteBeforeAcceptanceIsKnown()
    {
        using var handler = new Handler(); var releaseAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.OnSend = async ct => { handler.Events.Push(Frame(0, "turn.started") + Completed()); await releaseAck.Task.WaitAsync(ct); return Accepted(); };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http); var session = await client.CreateSessionAsync(new());
        using var run = session.StartRun("once", observer: (item, _) => { if (item.Name == "turn.completed") terminalSeen.TrySetResult(true); return Task.CompletedTask; });
        await terminalSeen.Task.WaitAsync(Deadline);
        Assert.False(run.Acceptance.IsCompleted); Assert.False(run.Completion.IsCompleted); Assert.Equal(SessionMessageAcceptance.Unconfirmed, run.AcceptanceState);
        releaseAck.SetResult(true);
        var result = await run.Completion.WaitAsync(Deadline);
        Assert.Equal("turn-1", result.TurnId); Assert.False(result.WasAborted); Assert.Equal("completed", result.Reason);
        Assert.Equal(SessionMessageAcceptance.Accepted, run.AcceptanceState); Assert.Equal(1, handler.Sends);
        Assert.Equal(new[] { "POST /api/sessions", "GET /api/sessions/s", "GET /api/sessions/s/events", "GET /api/sessions/s", "POST /api/sessions/s/messages" }, handler.Paths);
    }

    [Fact]
    public async Task AcceptanceAloneIsNotTurnCompletionAndExplicitAbortKeepsItsReason()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http);
        var session = await client.CreateSessionAsync(new()); using var run = session.StartRun("work");
        await run.Acceptance.WaitAsync(Deadline); Assert.False(run.Completion.IsCompleted);
        handler.Events.Push(Frame(0, "turn.started") + Frame(1, "turn.aborted", extra: ",\"reason\":\"budget_exceeded\""));
        var result = await run.Completion.WaitAsync(Deadline);
        Assert.True(result.WasAborted); Assert.Equal("budget_exceeded", result.Reason);
    }

    [Fact]
    public async Task CancellationAfterAcceptanceNeverPostsInterruptOrRepeatsInput()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http);
        var session = await client.CreateSessionAsync(new()); using var run = session.StartRun("work");
        await run.Acceptance.WaitAsync(Deadline); run.CancelObservation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion.WaitAsync(Deadline));
        Assert.Equal(SessionMessageAcceptance.Accepted, run.AcceptanceState); Assert.Equal(1, handler.Sends);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("interrupt", StringComparison.Ordinal) || path.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LostPostResponseIsUnconfirmedAndNeverAutomaticallyRetried()
    {
        using var handler = new Handler { OnSend = _ => throw new HttpRequestException("lost fixture response") };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http); var session = await client.CreateSessionAsync(new());
        using var run = session.StartRun("side effect");
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => run.Completion.WaitAsync(Deadline))).Code);
        Assert.Equal(SessionMessageAcceptance.Unconfirmed, run.AcceptanceState); Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("running", false, "session_busy")]
    [InlineData("idle", true, "session_changed")]
    public async Task ExistingOrInterveningWorkPreventsSending(string status, bool changed, string code)
    {
        using var handler = new Handler { Status = status, ChangeAfterListen = changed };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http); var session = await client.CreateSessionAsync(new());
        using var run = session.StartRun("must not send");
        Assert.Equal(code, (await Assert.ThrowsAsync<TansrProtocolException>(() => run.Completion.WaitAsync(Deadline))).Code);
        Assert.Equal(SessionMessageAcceptance.NotSent, run.AcceptanceState); Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public async Task SameClientAliasesCannotStartCompetingRunsOrOrdinaryMessages()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http);
        var session = await client.CreateSessionAsync(new()); var alias = await client.GetSessionAsync("s"); using var run = session.StartRun("first");
        await run.Acceptance.WaitAsync(Deadline);
        Assert.Equal("run_already_active", Assert.Throws<TansrProtocolException>(() => alias.StartRun("second")).Code);
        Assert.Equal("run_already_active", (await Assert.ThrowsAsync<TansrProtocolException>(() => alias.SendAsync("third"))).Code);
        run.CancelObservation(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion.WaitAsync(Deadline)); Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("wrong-turn")]
    [InlineData("missing-start")]
    [InlineData("session-ended")]
    [InlineData("gap")]
    public async Task AmbiguousOrMissingTerminalEvidenceNeverReportsCompletion(string scenario)
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http); var session = await client.CreateSessionAsync(new());
        using var run = session.StartRun("work"); await run.Acceptance.WaitAsync(Deadline);
        handler.Events.Push(scenario switch
        {
            "wrong-turn" => Frame(0, "turn.started") + Completed(turn: "foreign"),
            "missing-start" => Completed(0),
            "session-ended" => Frame(0, "session.ended"),
            _ => "event: server.replay.gap\ndata: {\"type\":\"server.replay.gap\",\"sessionId\":\"s\",\"reason\":\"evicted\",\"droppedEvents\":1,\"requestedAfterSeq\":-1}\n\n"
        });
        await Assert.ThrowsAsync<TansrProtocolException>(() => run.Completion.WaitAsync(Deadline)); Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task ReconnectContinuesTheOriginalTurnWithoutResendingAndSkipsDuplicateEvents()
    {
        using var handler = new Handler { ReconnectedEvents = new Feed() };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http); var session = await client.CreateSessionAsync(new()); var events = new List<string>();
        using var run = session.StartRun("work", observer: (item, _) => { events.Add(item.Name); return Task.CompletedTask; });
        await run.Acceptance.WaitAsync(Deadline); handler.Events.Push(Frame(0, "turn.started")); handler.Events.End();
        handler.ReconnectedEvents.Push(Frame(0, "turn.started") + Completed());
        await run.Completion.WaitAsync(Deadline);
        Assert.Equal(new string?[] { null, "0" }, handler.Cursors); Assert.Equal(new[] { "turn.started", "turn.completed" }, events); Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task CompletionReusesProjectionRetractionInsteadOfReturningWithdrawnText()
    {
        using var handler = new Handler(); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http); var session = await client.CreateSessionAsync(new());
        using var run = session.StartRun("work"); await run.Acceptance.WaitAsync(Deadline);
        handler.Events.Push(Frame(0, "turn.started") + Frame(1, "msg.block.start", extra: ",\"index\":0,\"blockType\":\"text\"") +
            Frame(2, "msg.text.delta", extra: ",\"index\":0,\"text\":\"withdrawn\"") + Frame(3, "msg.retracted", extra: ",\"index\":0") + Completed(4));
        var result = await run.Completion.WaitAsync(Deadline);
        Assert.DoesNotContain(result.Snapshot.Messages.SelectMany(m => m.Parts), p => p.Text.Contains("withdrawn", StringComparison.Ordinal));
    }
}
