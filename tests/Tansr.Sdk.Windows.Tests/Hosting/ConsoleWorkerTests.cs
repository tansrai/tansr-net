using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ConsoleAssistant;
using Tansr.Sdk.Client;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class ConsoleWorkerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void JsonlPreservesLongUnicodePromptAndRejectsWholeInvalidQueue()
    {
        var prompt = "前缀😀\n" + new string('长', 40000) + "\n尾部";
        var jobs = ConsoleWorker.ParseJobs(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = "long", prompt }) + "\n"));
        Assert.Equal(prompt, Assert.Single(jobs).Prompt);
        Assert.ThrowsAny<Exception>(() => ConsoleWorker.ParseJobs(Encoding.UTF8.GetBytes("{\"id\":\"same\",\"prompt\":\"a\"}\n{\"id\":\"same\",\"prompt\":\"b\"}")));
        Assert.ThrowsAny<Exception>(() => ConsoleWorker.ParseJobs(Encoding.UTF8.GetBytes("{\"id\":\"a\",\"prompt\":\"a\",\"prompt\":\"b\"}")));
        Assert.ThrowsAny<Exception>(() => ConsoleWorker.ParseJobs(Encoding.UTF8.GetBytes("{\"id\":\"a\",\"prompt\":\"a\",\"resume\":\"old\"}")));
        Assert.ThrowsAny<Exception>(() => ConsoleWorker.ParseJobs([0x7b, 0xff, 0x7d]));
        Assert.ThrowsAny<Exception>(() => ConsoleWorker.ParseJobs(Encoding.UTF8.GetBytes("{\"id\":\"a\",\"prompt\":\"\\uD800\"}")));
    }

    [Fact]
    public async Task BoundedIndependentSessionsUseFullPromptsAndUnattendedPolicies()
    {
        using var handler = new Handler { CompleteAfter = 2 };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http);
        var logs = new ConcurrentQueue<string>();
        var prompt = "全量😀\n" + new string('文', 40000);
        var jobs = new[] { new ConsoleWorker.Job("first", prompt, null), new ConsoleWorker.Job("second", "second", "actual-model"), new ConsoleWorker.Job("third", "third", null) };
        var work = ConsoleWorker.RunJobsAsync(jobs, 2, client, null, CancellationToken.None, logs.Enqueue);
        await handler.FirstTwoSent.Task.WaitAsync(Deadline);
        Assert.Equal(2, handler.Created); Assert.False(work.IsCompleted);
        Assert.Equal(prompt, handler.Sessions["s1"].Prompt);
        handler.Finish("s1", questions: true); handler.Finish("s2", questions: true);
        Assert.Equal(0, await work.WaitAsync(Deadline));
        Assert.Equal(3, handler.Created); Assert.Equal(3, handler.Sends); Assert.Equal(3, handler.Closes);
        Assert.Equal(0, handler.Interrupts); Assert.Equal(2, handler.Permissions); Assert.Equal(2, handler.Questions);
        Assert.Equal(2, handler.MaximumOpen);
        var events = ReadLogs(logs);
        foreach (var job in jobs)
        {
            Assert.Single(events, x => Has(x, "jobId", job.Id) && Has(x, "phase", "acceptance") && Has(x, "state", "accepted"));
            Assert.Single(events, x => Has(x, "jobId", job.Id) && Has(x, "phase", "terminal") && Has(x, "state", "succeeded"));
            Assert.Single(events, x => Has(x, "jobId", job.Id) && Has(x, "phase", "cleanup") && Has(x, "state", "completed"));
        }
        Assert.DoesNotContain(handler.Requests, x => x.Contains("resume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopDoesNotStartQueuedJobAndStillClosesEverySessionWhenInterruptFails()
    {
        using var handler = new Handler { FailFirstInterrupt = true };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http); using var stop = new CancellationTokenSource();
        var logs = new ConcurrentQueue<string>();
        var jobs = Enumerable.Range(1, 3).Select(i => new ConsoleWorker.Job("job-" + i, "run " + i, null)).ToArray();
        var work = ConsoleWorker.RunJobsAsync(jobs, 2, client, null, stop.Token, logs.Enqueue);
        await handler.FirstTwoSent.Task.WaitAsync(Deadline);
        stop.Cancel();
        Assert.Equal(130, await work.WaitAsync(Deadline));
        Assert.Equal(2, handler.Created); Assert.Equal(2, handler.Sends); Assert.Equal(2, handler.Interrupts); Assert.Equal(2, handler.Closes);
        Assert.Equal(0, handler.Open);
        var events = ReadLogs(logs);
        Assert.Contains(events, x => Has(x, "jobId", "job-3") && Has(x, "phase", "acceptance") && Has(x, "state", "not_sent"));
        Assert.Contains(events, x => Has(x, "jobId", "job-3") && Has(x, "phase", "terminal") && Has(x, "state", "not_started"));
        Assert.Contains(events, x => Has(x, "resource", "interrupt") && Has(x, "state", "unconfirmed"));
        Assert.Equal(2, events.Count(x => Has(x, "resource", "close") && Has(x, "state", "completed")));
        Assert.DoesNotContain(events, x => Has(x, "phase", "terminal") && Has(x, "state", "succeeded"));
    }

    [Fact]
    public async Task LostAcceptanceResponseKeepsUnknownAndNeverRepeatsTheJob()
    {
        using var handler = new Handler { LoseMessageResponse = true };
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = CreateClient(http);
        var logs = new ConcurrentQueue<string>();
        Assert.Equal(2, await ConsoleWorker.RunJobsAsync([new("unknown", "effect", null)], 1, client, null, CancellationToken.None, logs.Enqueue).WaitAsync(Deadline));
        Assert.Equal(1, handler.Created); Assert.Equal(1, handler.Sends); Assert.Equal(1, handler.Interrupts); Assert.Equal(1, handler.Closes);
        var events = ReadLogs(logs);
        Assert.Contains(events, x => Has(x, "phase", "acceptance") && Has(x, "state", "unconfirmed"));
        Assert.Contains(events, x => Has(x, "phase", "terminal") && Has(x, "state", "unconfirmed"));
        Assert.DoesNotContain(events, x => Has(x, "state", "succeeded"));
    }

    private static TansrClient CreateClient(HttpClient http) => new(new TansrClientOptions
    { BaseUri = new("https://serve.test/"), TokenProvider = _ => Task.FromResult("fixture-token"), MaxReconnectAttempts = 0 }, http);
    private static JsonElement[] ReadLogs(ConcurrentQueue<string> logs) => logs.Select(x => { using var json = JsonDocument.Parse(x); return json.RootElement.Clone(); }).ToArray();
    private static bool Has(JsonElement item, string name, string value) => item.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == value;

    private sealed class Handler : HttpMessageHandler
    {
        internal readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.Ordinal);
        internal readonly ConcurrentQueue<string> Requests = new();
        internal readonly TaskCompletionSource<bool> FirstTwoSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Created, Sends, Closes, Interrupts, Permissions, Questions, Open, MaximumOpen;
        internal int CompleteAfter = int.MaxValue;
        internal bool FailFirstInterrupt, LoseMessageResponse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Enqueue(request.Method + " " + path);
            if (request.Method == HttpMethod.Post && path == "/api/sessions")
            {
                var created = Interlocked.Increment(ref Created); var id = "s" + created;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.False(body.RootElement.TryGetProperty("resume", out _));
                Assert.True(body.RootElement.GetProperty("labels").TryGetProperty("worker.job", out _));
                Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("clientTools").ValueKind);
                Assert.True(Sessions.TryAdd(id, new Session()));
                var open = Interlocked.Increment(ref Open);
                int previous;
                do { previous = Volatile.Read(ref MaximumOpen); if (previous >= open) break; } while (Interlocked.CompareExchange(ref MaximumOpen, open, previous) != previous);
                return Json(new { sessionId = id, resumed = false, lastSeq = -1 });
            }
            var parts = path.Split('/'); var sessionId = parts[3]; var session = Sessions[sessionId];
            if (request.Method == HttpMethod.Get && path.EndsWith("/events", StringComparison.Ordinal))
            {
                session.Listening = true;
                return new(HttpStatusCode.OK) { Content = new StreamContent(session.Events) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
            }
            if (request.Method == HttpMethod.Get && parts.Length == 4)
                return Json(new { sessionId, endUserId = "fixture", status = "idle", live = true, lastSeq = -1, createdAt = "2026-09-26T00:00:00Z", lastActivityAt = "2026-09-26T00:00:00Z" });
            if (request.Method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                Assert.True(session.Listening);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); session.Prompt = body.RootElement.GetProperty("prompt").GetString();
                var sent = Interlocked.Increment(ref Sends);
                Push(sessionId, "turn.started");
                if (sent >= 2) FirstTwoSent.TrySetResult(true);
                if (LoseMessageResponse) throw new HttpRequestException("lost controlled ACK");
                if (sent > CompleteAfter) Finish(sessionId);
                return Json(new { sessionId, accepted = true }, HttpStatusCode.Accepted);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/interrupt", StringComparison.Ordinal))
            {
                Assert.False(ct.IsCancellationRequested);
                if (Interlocked.Increment(ref Interrupts) == 1 && FailFirstInterrupt) throw new HttpRequestException("controlled interrupt failure");
                return Json(new { sessionId, interrupted = true });
            }
            if (request.Method == HttpMethod.Delete && parts.Length == 4)
            {
                Assert.False(ct.IsCancellationRequested);
                Interlocked.Increment(ref Closes); Interlocked.Decrement(ref Open);
                return Json(new { sessionId, closed = true });
            }
            if (request.Method == HttpMethod.Post && path.Contains("/permission/", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("deny", body.RootElement.GetProperty("verdict").GetString()); Interlocked.Increment(ref Permissions);
                return Json(new { accepted = true });
            }
            if (request.Method == HttpMethod.Post && path.Contains("/questions/", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var answer = Assert.Single(body.RootElement.GetProperty("answers").EnumerateArray());
                Assert.Empty(answer.GetProperty("selectedOptionIds").EnumerateArray()); Assert.Contains("无人值守", answer.GetProperty("freeText").GetString());
                Interlocked.Increment(ref Questions); return Json(new { accepted = true });
            }
            throw new InvalidOperationException("unexpected_worker_fixture_route");
        }

        internal void Finish(string id, bool questions = false)
        {
            if (questions)
            {
                Push(id, "server.permission.request", new { requestId = "permission-" + id, digest = "digest", toolName = "shell" });
                Push(id, "server.question.request", new { requestId = "question-" + id, questions = new[] { new { id = "q", question = "Choose", options = Array.Empty<object>() } } });
            }
            Push(id, "turn.completed", reason: "completed");
        }
        private void Push(string id, string type, object? payload = null, string? reason = null)
        {
            var seq = Interlocked.Increment(ref Sessions[id].Sequence);
            var text = JsonSerializer.Serialize(new { sessionId = id, seq, type, turnId = "turn-" + id, payload, reason });
            Sessions[id].Events.Push("id: " + seq + "\ndata: " + text + "\n\n");
        }
        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
    private sealed class Session
    {
        internal readonly Feed Events = new();
        internal int Sequence = -1;
        internal bool Listening;
        internal string? Prompt;
    }
    private sealed class Feed : Stream
    {
        private readonly Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>();
        private byte[]? current; private int offset;
        internal void Push(string frame) => Assert.True(queue.Writer.TryWrite(Encoding.UTF8.GetBytes(frame)));
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
}
