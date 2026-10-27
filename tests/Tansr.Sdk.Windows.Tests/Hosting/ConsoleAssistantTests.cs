using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class ConsoleAssistantTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly Type ConsoleProgram = Assembly.Load("ConsoleAssistant").GetType("ConsoleAssistant.Program", true)!;

    [Fact]
    public async Task CancelledInteractiveInputReturnsBeforeTheSingleSynchronousReadFinishes()
    {
        using var input = new BlockingInput(); using var stop = new CancellationTokenSource();
        var command = ReadCommand(input, stop.Token);
        try
        {
            await input.Entered.Task.WaitAsync(Deadline);
            Assert.False(command.IsCompleted); Assert.Equal(1, input.Reads);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.WaitAsync(Deadline));
            Assert.False(input.Finished.Task.IsCompleted); Assert.Equal(1, input.Reads);
            Assert.False(input.WasDisposed);
        }
        finally
        {
            input.Release.Set();
            await input.Finished.Task.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task InteractiveInputPreservesCommandsAndEofWithoutConcurrentReads()
    {
        using var input = new StringReader("/device-status\n/quit\n");
        Assert.Equal("/device-status", await ReadCommand(input, CancellationToken.None).WaitAsync(Deadline));
        Assert.Equal("/quit", await ReadCommand(input, CancellationToken.None).WaitAsync(Deadline));
        Assert.Null(await ReadCommand(input, CancellationToken.None).WaitAsync(Deadline));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadCommand(input, stop.Token).WaitAsync(Deadline));
    }

    [Fact]
    public async Task ResumedOnceSkipsOldCompletedReplayAndWaitsForItsOwnTurn()
    {
        using var f = new Fixture(); var session = await f.ResumeAsync();
        var observed = new ConcurrentQueue<AgentEvent>(); var currentSeen = Signal();
        f.Handler.Events.Push(Frame(40, "turn.started", "old") + Frame(41, "turn.completed", "old", "completed"));
        var once = RunOnce(session, (item, _) => { observed.Enqueue(item); if (item.Name == "turn.started") currentSeen.TrySetResult(true); return Task.CompletedTask; });
        await f.Handler.Sent.Task.WaitAsync(Deadline); await currentSeen.Task.WaitAsync(Deadline);
        Assert.True(session.Resumed); Assert.Equal("41", f.Handler.Cursor); Assert.Equal(2, f.Handler.MetadataReads);
        Assert.False(once.IsCompleted); Assert.Single(observed); Assert.Equal("current", observed.Single().Data.GetProperty("turnId").GetString());
        f.Handler.Events.Push(Frame(43, "turn.completed", "current", "completed"));
        Assert.Equal(0, await once.WaitAsync(Deadline)); Assert.Equal(1, f.Handler.Messages);
        Assert.Equal(0, f.Handler.Interrupts); Assert.Equal(0, f.Handler.Closes);
    }

    [Fact]
    public async Task EarlyCurrentTerminalDoesNotReportSuccessBeforeMessageAcceptance()
    {
        using var f = new Fixture(); var session = await f.ResumeAsync(); var releaseAck = Signal(); var terminalSeen = Signal();
        f.Handler.BeforeAcceptance = async ct => { f.Handler.Events.Push(Frame(43, "turn.completed", "current", "completed")); await releaseAck.Task.WaitAsync(ct); };
        var once = RunOnce(session, (item, _) => { if (item.Name == "turn.completed") terminalSeen.TrySetResult(true); return Task.CompletedTask; });
        await terminalSeen.Task.WaitAsync(Deadline); Assert.False(once.IsCompleted);
        releaseAck.SetResult(true); Assert.Equal(0, await once.WaitAsync(Deadline)); Assert.Equal(1, f.Handler.Messages);
    }

    [Theory]
    [InlineData("turn.completed", "completed", 0)]
    [InlineData("turn.completed", "structured_output", 0)]
    [InlineData("turn.completed", "error", 2)]
    [InlineData("turn.aborted", "cancelled", 2)]
    public async Task OnceUsesOnlyTheMatchingTerminalReasonForItsExitCode(string type, string reason, int expected)
    {
        using var f = new Fixture(); var session = await f.ResumeAsync(); var once = RunOnce(session);
        await f.Handler.Sent.Task.WaitAsync(Deadline); f.Handler.Events.Push(Frame(43, type, "current", reason));
        Assert.Equal(expected, await once.WaitAsync(Deadline)); Assert.Equal(1, f.Handler.Messages);
    }

    [Fact]
    public async Task OldTurnTerminalAboveTheCursorIsRejectedRatherThanClosingNewWorkAsSuccessful()
    {
        using var f = new Fixture(); var session = await f.ResumeAsync(); var once = RunOnce(session);
        await f.Handler.Sent.Task.WaitAsync(Deadline); f.Handler.Events.Push(Frame(43, "turn.completed", "old", "completed"));
        Assert.Equal("run_identity_ambiguous", (await Assert.ThrowsAsync<TansrProtocolException>(() => once.WaitAsync(Deadline))).Code);
        Assert.Equal(1, f.Handler.Messages); Assert.Equal(0, f.Handler.Interrupts); Assert.Equal(0, f.Handler.Closes);
    }

    [Fact]
    public async Task LostAcceptanceDoesNotResendOrReturnTheEarlyTerminalAsSuccess()
    {
        using var f = new Fixture(); var session = await f.ResumeAsync();
        f.Handler.BeforeAcceptance = _ => { f.Handler.Events.Push(Frame(43, "turn.completed", "current", "completed")); throw new HttpRequestException("controlled lost acceptance"); };
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => RunOnce(session).WaitAsync(Deadline))).Code); Assert.Equal(1, f.Handler.Messages);
    }

    [Fact]
    public async Task CancelledOnceReleasesThePublicRunWithoutImplicitInterruptOrRetry()
    {
        using var f = new Fixture(); var session = await f.ResumeAsync(); using var stop = new CancellationTokenSource();
        var once = RunOnce(session, token: stop.Token); await f.Handler.Sent.Task.WaitAsync(Deadline); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => once.WaitAsync(Deadline));
        Assert.Equal(1, f.Handler.Messages); Assert.Equal(0, f.Handler.Interrupts); Assert.Equal(0, f.Handler.Closes);
        await session.SendAsync("explicit next prompt"); Assert.Equal(2, f.Handler.Messages);
    }

    [Fact]
    public async Task InteractiveObserverKeepsMessageSendingAndSameTurnInsertionAvailable()
    {
        using var f = new Fixture(); using var stop = new CancellationTokenSource(); using var view = new SessionView();
        f.Handler.EmitStart = false;
        IDisposable? tools = null;
        var lines = new ConcurrentQueue<string>(); var projected = Signal();
        using var narrator = new SessionNarrator(lines.Enqueue);
        using var subscription = view.Subscribe(snapshot => { if (snapshot.LastSequence == 43) projected.TrySetResult(true); });
        using var initialization = f.StartInitialization(new SessionInitializationOptions
        {
            PrepareObservationAsync = async (session, ct) =>
            {
                tools = CreateTools(session, ct);
                await ToolsReady(tools);
                return new SessionObservationOptions((item, eventToken) =>
                {
                    narrator.Apply(item);
                    return (Task)ConsoleProgram.GetMethod("ObserveEventAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                        [session, view, tools, false, item, eventToken])!;
                });
            }
        }, stop.Token);
        SessionConnection? connection = null;
        try
        {
            connection = await initialization.Ready.WaitAsync(Deadline);
            var session = connection.Session;
            var metadataAtReady = f.Handler.MetadataReads; Assert.Equal(1, metadataAtReady);
            Assert.Equal(0, f.Handler.Messages); Assert.Equal(1, f.Handler.Creations);
            var observation = (Task)ConsoleProgram.GetMethod("ObserveCompletionAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [connection])!;
            await f.Handler.Listening.Task.WaitAsync(Deadline);
            await session.SendAsync("first interactive prompt");
            await session.SubmitInputAsync("input-original", new SessionInputTarget("epoch", "current"), "same turn text");
            await session.SendAsync("second interactive prompt");
            f.Handler.Events.Push(Frame(42, "turn.started", "current") + Frame(43, "turn.completed", "current", "completed"));
            await projected.Task.WaitAsync(Deadline);
            Assert.Equal(1, f.Handler.EventSubscriptions);
            Assert.Equal(2, lines.Count); Assert.Contains(lines, line => line.Contains("turn started", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("turn completed", StringComparison.Ordinal));
            Assert.Equal("idle", view.Snapshot.Status);
            Assert.Equal(2, f.Handler.Messages); Assert.Equal(1, f.Handler.Inputs); Assert.Equal(metadataAtReady, f.Handler.MetadataReads);
            stop.Cancel(); Assert.False(connection.Completion.IsCompleted);
            await connection.StopAsync().WaitAsync(Deadline); await observation.WaitAsync(Deadline);
            Assert.Equal(0, f.Handler.Interrupts); Assert.Equal(0, f.Handler.Closes);
        }
        finally
        {
            if (connection != null) await connection.StopAsync().WaitAsync(Deadline);
            tools?.Dispose();
        }
    }

    [Fact]
    public async Task StartupCancellationInterruptsNativeResumeMetadataWithoutSendingOrClosingTheSession()
    {
        using var f = new Fixture(); using var startup = new CancellationTokenSource();
        f.Handler.BeforeMetadata = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        IDisposable? tools = null;
        using var initialization = f.StartInitialization(new SessionInitializationOptions
        {
            PrepareObservationAsync = async (session, ct) =>
            {
                tools = CreateTools(session, ct);
                await ToolsReady(tools);
                return null;
            }
        }, startup.Token);
        try
        {
            await f.Handler.MetadataEntered.Task.WaitAsync(Deadline);
            Assert.Equal("resumed", (await initialization.Accepted.WaitAsync(Deadline)).Id);
            startup.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.Ready.WaitAsync(Deadline));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolsReady(tools!).WaitAsync(Deadline));
            await f.Handler.MetadataCancelled.Task.WaitAsync(Deadline);
            Assert.Equal(1, f.Handler.Creations); Assert.Equal(1, f.Handler.MetadataReads);
            Assert.Equal(0, f.Handler.EventSubscriptions); Assert.Equal(0, f.Handler.Messages);
            Assert.Equal(0, f.Handler.Interrupts); Assert.Equal(0, f.Handler.Closes);
        }
        finally { startup.Cancel(); tools?.Dispose(); }
    }

    [Fact]
    public async Task CancellingStartupAfterReadyDoesNotCancelTheNativeHostLifetime()
    {
        using var f = new Fixture(); using var startup = new CancellationTokenSource();
        var session = await f.ResumeAsync();
        using var host = CreateTools(session, startup.Token);
        await ToolsReady(host).WaitAsync(Deadline);
        startup.Cancel();
        Assert.True(ToolsReady(host).IsCompletedSuccessfully);
        var tools = (IReadOnlyList<Tansr.Sdk.Windows.Hosting.WindowsBusinessTool>)host.GetType()
            .GetMethod("SelectDeviceExecution", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null)!;
        using var arguments = JsonDocument.Parse("{}");
        var result = await Assert.Single(tools, item => item.Name == "application_info").Invoke(arguments.RootElement, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal(1, f.Handler.Creations); Assert.Equal(1, f.Handler.MetadataReads);
        Assert.Equal(0, f.Handler.EventSubscriptions); Assert.Equal(0, f.Handler.Messages);
        Assert.Equal(0, f.Handler.Interrupts); Assert.Equal(0, f.Handler.Closes);
    }

    private static Task<int> RunOnce(AgentSession session, Func<AgentEvent, CancellationToken, Task>? observer = null, CancellationToken token = default)
        => (Task<int>)ConsoleProgram.GetMethod("RunOnceAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [session, "new prompt", observer ?? ((_, _) => Task.CompletedTask), token])!;

    private static Task<string?> ReadCommand(TextReader input, CancellationToken token)
        => (Task<string?>)ConsoleProgram.GetMethod("ReadCommandAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [input, token])!;

    private static Task ToolsReady(IDisposable tools)
        => (Task)tools.GetType().GetProperty("Ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tools)!;

    private static IDisposable CreateTools(AgentSession session, CancellationToken initializationCancellation)
    {
        var type = ConsoleProgram.Assembly.GetType("Tansr.Examples.NativeToolHost", true)!;
        return (IDisposable)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single(constructor => constructor.GetParameters().Length == 7).Invoke(
            [session, "console-test", (Func<string, CancellationToken, Task>)((_, _) => Task.CompletedTask), (Action<string>)(_ => { }), null, true, initializationCancellation]);
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Frame(int sequence, string type, string turn, string? reason = null)
        => "id: " + sequence + "\ndata: " + JsonSerializer.Serialize(new { sessionId = "resumed", seq = sequence, type, turnId = turn, reason }) + "\n\n";

    private sealed class BlockingInput : TextReader
    {
        internal readonly TaskCompletionSource<bool> Entered = Signal(), Finished = Signal();
        internal readonly ManualResetEventSlim Release = new();
        internal int Reads;
        internal bool WasDisposed;
        public override string? ReadLine()
        {
            Interlocked.Increment(ref Reads); Entered.TrySetResult(true);
            try { Release.Wait(); return "unconsumed input"; }
            finally { Finished.TrySetResult(true); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { WasDisposed = true; Release.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Handler Handler = new();
        private readonly HttpClient http;
        private readonly TansrClient client;
        internal Fixture()
        {
            http = new HttpClient(UnifiedStamp.Stamp(Handler));
            client = new TansrClient(new TansrClientOptions { BaseUri = new Uri("https://serve.test/"), TokenProvider = _ => Task.FromResult("synthetic-token"), MaxReconnectAttempts = 0 }, http);
        }
        internal Task<AgentSession> ResumeAsync() => client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = "resumed" });
        internal SessionInitialization StartInitialization(SessionInitializationOptions options, CancellationToken token)
            => client.StartSessionInitialization(new CreateSessionOptions { ResumeSessionId = "resumed" }, options, token);
        public void Dispose() { client.Dispose(); http.Dispose(); Handler.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal readonly Feed Events = new();
        internal readonly TaskCompletionSource<bool> Sent = Signal(), Listening = Signal(), MetadataEntered = Signal(), MetadataCancelled = Signal();
        internal int Messages, MetadataReads, Inputs, Interrupts, Closes, EventSubscriptions, Creations;
        internal string? Cursor;
        internal bool EmitStart = true;
        internal Func<CancellationToken, Task>? BeforeAcceptance;
        internal Func<CancellationToken, Task>? BeforeMetadata;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/sessions")
            {
                Interlocked.Increment(ref Creations);
                using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Assert.Equal("resumed", data.RootElement.GetProperty("resume").GetProperty("sessionId").GetString());
                return Json(new { sessionId = "resumed", resumed = true, lastSeq = 41 });
            }
            if (request.Method == HttpMethod.Get && path == "/api/sessions/resumed")
            {
                Interlocked.Increment(ref MetadataReads);
                MetadataEntered.TrySetResult(true);
                if (BeforeMetadata != null)
                {
                    try { await BeforeMetadata(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { MetadataCancelled.TrySetResult(true); throw; }
                }
                return Json(new { sessionId = "resumed", endUserId = "user", status = "idle", live = true, lastSeq = 41, createdAt = "2026-09-27T00:00:00Z", lastActivityAt = "2026-09-27T00:00:00Z" });
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/events", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref EventSubscriptions);
                Cursor = request.Headers.TryGetValues("Last-Event-ID", out var values) ? values.Single() : null; Listening.TrySetResult(true);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Events) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Messages); if (EmitStart) Events.Push(Frame(42, "turn.started", "current")); Sent.TrySetResult(true);
                if (BeforeAcceptance != null) await BeforeAcceptance(ct);
                return Json(new { sessionId = "resumed", accepted = true }, HttpStatusCode.Accepted);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/inputs", StringComparison.Ordinal))
            {
                using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("input-original", data.RootElement.GetProperty("inputId").GetString()); Assert.Equal("current", data.RootElement.GetProperty("target").GetProperty("turnId").GetString());
                Assert.Equal("same turn text", data.RootElement.GetProperty("content").GetProperty("text").GetString()); Interlocked.Increment(ref Inputs);
                return Json(new { accepted = true });
            }
            if (path.EndsWith("/interrupt", StringComparison.Ordinal)) { Interlocked.Increment(ref Interrupts); return Json(new { interrupted = true }); }
            if (request.Method == HttpMethod.Delete) { Interlocked.Increment(ref Closes); return Json(new { closed = true }); }
            throw new InvalidOperationException("unexpected_console_once_fixture_route");
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class Feed : Stream
    {
        private readonly Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>();
        private byte[]? current; private int offset;
        internal void Push(string frames) => Assert.True(queue.Writer.TryWrite(Encoding.UTF8.GetBytes(frames)));
        public override async Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken ct)
        {
            while (current == null || offset == current.Length)
            {
                if (!await queue.Reader.WaitToReadAsync(ct)) return 0;
                if (!queue.Reader.TryRead(out current)) continue; offset = 0;
            }
            int length = Math.Min(count, current.Length - offset); Array.Copy(current, offset, buffer, start, length); offset += length; return length;
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
}
