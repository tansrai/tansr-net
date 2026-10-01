using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Tests.Client;

public sealed class ArchiveEventTests
{
    private const string Generations = "{\"historyEpoch\":\"历史\",\"deletionGeneration\":\"0\",\"projectionRevision\":\"1\"}";
    private const string Scope = "{\"applicationScopeId\":\"app\",\"endUserId\":\"user\",\"authorizationRevision\":\"1\"}";
    private static string Cursor => "e1.k1." + new string('A', 43) + ".0000000000000001.0000000000000001." + new string('A', 43);
    private static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static string FrameJson(string binding = "binding", string generations = Generations)
        => Encoding.UTF8.GetString(WireJson.EncodeControl(Element("{\"protocol\":\"sdk2-ext-v1\",\"bindingId\":\"" + binding + "\",\"eventId\":\"e1." + new string('A', 43) + ".0000000000000001.0000000000000001\",\"cursor\":\"" + Cursor + "\",\"revision\":\"1\",\"generations\":" + generations + ",\"eventType\":\"archive.records-available\",\"payload\":{\"publishedThroughSequence\":\"1\"}}")));
    private static string Frame(string json) => "id: " + Cursor + "\nevent: archive.records-available\ndata: " + json + "\n\n";
    private static ArchiveEventDecoder Decoder() => new("binding", Element(Generations), Element(Scope));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Task.FromResult(respond(request)); }
    }
    private static TansrClient Client(HttpClient http, Func<JsonElement>? scope = null) => new(new TansrClientOptions
    {
        BaseUri = new Uri("https://serve.test/"),
        PrincipalProvider = () => "app/user",
        TokenProvider = _ => Task.FromResult("token"),
        ExecutionScopeProvider = scope ?? (() => Element(Scope))
    }, http);
    private static HttpResponseMessage Stream(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

    [Fact]
    public async Task StrictDecoderHandlesEveryByteSplitIncludingUtf8AndCrLf()
    {
        var decoder = Decoder(); var bytes = Encoding.UTF8.GetBytes(": heartbeat\r\n\r\n" + Frame(FrameJson()).Replace("\n", "\r\n"));
        var frames = new List<JsonElement>();
        foreach (var b in bytes) await decoder.FeedAsync([b], 1, (frame, _) => { frames.Add(frame); return Task.CompletedTask; }, default);
        decoder.Complete(); Assert.Equal(Cursor, Assert.Single(frames).GetProperty("cursor").GetString());
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("multiline")]
    [InlineData("bare-cr")]
    [InlineData("bom")]
    [InlineData("retry")]
    [InlineData("mixed-comment")]
    [InlineData("wrong-event")]
    [InlineData("wrong-cursor")]
    [InlineData("noncanonical")]
    [InlineData("binding")]
    [InlineData("generations")]
    public async Task StrictArchiveFramesDoNotInheritPermissiveSessionSse(string fault)
    {
        var source = Frame(FrameJson());
        if (fault == "duplicate-id") source = source.Replace("event:", "id: " + Cursor + "\nevent:");
        if (fault == "multiline") source = source.Replace("\n\n", "\ndata: {}\n\n");
        if (fault == "bare-cr") source = source.Replace("\n", "\r");
        if (fault == "bom") source = "\uFEFF" + source;
        if (fault == "retry") source = "retry: 1\n" + source;
        if (fault == "mixed-comment") source = ": x\n" + source;
        if (fault == "wrong-event") source = source.Replace("event: archive.records-available", "event: material.request");
        if (fault == "wrong-cursor") source = source.Replace("id: e1.k1.", "id: ev1.k1.");
        if (fault == "noncanonical") source = Frame(" " + FrameJson());
        if (fault == "binding") source = Frame(FrameJson("other"));
        if (fault == "generations") source = Frame(FrameJson(generations: Generations.Replace("\"0\"", "\"1\"")));
        var bytes = Encoding.UTF8.GetBytes(source); int count = 0;
        await Assert.ThrowsAsync<TansrProtocolException>(() => Decoder().FeedAsync(bytes, bytes.Length, (_, _) => { count++; return Task.CompletedTask; }, default));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task UnterminatedFrameNeverDeliversAndOversizeFailsWithoutAccumulatingQueue()
    {
        var decoder = Decoder(); var bytes = Encoding.UTF8.GetBytes(Frame(FrameJson()).TrimEnd('\n') + "\n"); int delivered = 0;
        await decoder.FeedAsync(bytes, bytes.Length, (_, _) => { delivered++; return Task.CompletedTask; }, default);
        Assert.Throws<TansrProtocolException>(decoder.Complete); Assert.Equal(0, delivered);
        var oversized = new byte[ArchiveEventDecoder.MaximumFrameBytes + 1]; Array.Fill(oversized, (byte)'x');
        await Assert.ThrowsAsync<TansrProtocolException>(() => Decoder().FeedAsync(oversized, oversized.Length, (_, _) => Task.CompletedTask, default));
    }

    [Fact]
    public async Task ConnectionUsesOnlyArchiveRouteAndReturnsDurablyConsumedCursor()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/api/archive/bindings/binding/events?protocol=sdk2-ext-v1", request.RequestUri!.PathAndQuery);
            Assert.Equal(Cursor, Assert.Single(request.Headers.GetValues("Last-Event-ID")));
            Assert.Equal("token", request.Headers.Authorization!.Parameter); return Stream(Frame(FrameJson()));
        });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = Client(http);
        var persisted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consume = client.ConsumeArchiveEventsAsync("binding", Element(Generations), async (_, _) => { entered.SetResult(true); await persisted.Task; }, Cursor, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(consume.IsCompleted);
        persisted.SetResult(true); Assert.Equal(Cursor, await consume); Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task FailedConsumerStopsBeforeSecondFrameAndDoesNotReconnect()
    {
        using var handler = new Handler(_ => Stream(Frame(FrameJson()) + Frame(FrameJson()))); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = Client(http); int calls = 0;
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => client.ConsumeArchiveEventsAsync("binding", Element(Generations), (_, _) => { calls++; throw new InvalidOperationException("secret material"); }, null, default));
        Assert.Equal("consumer_failed", error.Code); Assert.DoesNotContain("secret", error.ToString()); Assert.Equal(1, calls); Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task CancellationDoesNotWaitForUncooperativeConsumerOrAdvanceCursor()
    {
        using var handler = new Handler(_ => Stream(Frame(FrameJson()))); using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = Client(http); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consume = client.ConsumeArchiveEventsAsync("binding", Element(Generations), (_, _) => { entered.SetResult(true); return finish.Task; }, null, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consume.WaitAsync(TimeSpan.FromSeconds(5))); finish.SetResult(true);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RepeatedReconnectCannotAccumulateUnsettledConsumersAndCapacityReturnsOnlyAfterCompletion()
    {
        using var handler = new Handler(request => Stream(Frame(FrameJson(request.RequestUri!.Segments[4].TrimEnd('/')))));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = Client(http);
        var pending = new List<TaskCompletionSource<bool>>();
        try
        {
            for (int i = 0; i < TansrClient.MaximumArchiveConsumers; i++)
            {
                var finish = new TaskCompletionSource<bool>(); pending.Add(finish);
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancel = new CancellationTokenSource();
                var task = client.ConsumeArchiveEventsAsync("binding" + i, Element(Generations), (_, _) => { entered.SetResult(true); return finish.Task; }, null, cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            for (int i = 0; i < 32; i++)
            {
                var same = await Assert.ThrowsAsync<TansrProtocolException>(() => client.ConsumeArchiveEventsAsync("binding0", Element(Generations), (_, _) => Task.CompletedTask, null, default));
                Assert.Equal("consumer_pending", same.Code);
            }
            var full = await Assert.ThrowsAsync<TansrProtocolException>(() => client.ConsumeArchiveEventsAsync("new-binding", Element(Generations), (_, _) => Task.CompletedTask, null, default));
            Assert.Equal("consumer_capacity_exceeded", full.Code); Assert.Equal(TansrClient.MaximumArchiveConsumers, handler.Count);
            pending[0].SetResult(true);
            Assert.Equal(Cursor, await client.ConsumeArchiveEventsAsync("binding0", Element(Generations), (_, _) => Task.CompletedTask, null, default));
            Assert.Equal(TansrClient.MaximumArchiveConsumers + 1, handler.Count);
        }
        finally { foreach (var finish in pending) finish.TrySetResult(true); }
    }

    [Fact]
    public async Task ChangedAuthorityAndMissingNoStoreAreRejected()
    {
        string scope = Scope;
        using var handler = new Handler(_ => { scope = Scope.Replace("\"1\"", "\"2\""); return Stream(Frame(FrameJson())); });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = Client(http, () => Element(scope));
        await Assert.ThrowsAsync<TansrProtocolException>(() => client.ConsumeArchiveEventsAsync("binding", Element(Generations), (_, _) => Task.CompletedTask, null, default));
        using var missing = new Handler(_ => { var result = Stream(Frame(FrameJson())); result.Headers.CacheControl = null; return result; });
        using var missingHttp = UnifiedStamp.Client(missing); using var missingClient = Client(missingHttp);
        await Assert.ThrowsAsync<TansrProtocolException>(() => missingClient.ConsumeArchiveEventsAsync("binding", Element(Generations), (_, _) => Task.CompletedTask, null, default));
    }
}
