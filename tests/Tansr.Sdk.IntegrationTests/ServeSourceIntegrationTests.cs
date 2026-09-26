using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Xunit.Abstractions;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>
/// Real Serve source HTTP/SSE routes with the source FakeAgentFactory.
/// This verifies transport consumption, not real kernel/model/tool parity or paid sampling.
/// Run through scripts/serve-integration.mjs; missing environment is a hard failure.
/// </summary>
public sealed class ServeSourceIntegrationTests
{
    private readonly Uri origin;
    private readonly string token;
    private readonly string otherToken;

    public ServeSourceIntegrationTests(ITestOutputHelper output)
    {
        var source = Required("TANSR_SERVE_SOURCE");
        Assert.True(Directory.Exists(source), "TANSR_SERVE_SOURCE must name the Serve source checkout.");
        origin = new Uri(Required("TANSR_SERVE_TEST_URL"));
        Assert.Equal("http", origin.Scheme);
        Assert.Equal("127.0.0.1", origin.Host);
        token = Required("TANSR_SERVE_TEST_TOKEN");
        otherToken = Required("TANSR_SERVE_TEST_OTHER_TOKEN");
        var revision = Required("TANSR_SERVE_SOURCE_SHA");
        Assert.Matches("^[a-f0-9]{40}$", revision);
        output.WriteLine("Serve source revision: {0}; real HTTP/SSE routes; controlled FakeAgentFactory; zero model calls.", revision);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(name + " is required. Run scripts/serve-integration.mjs; this gate does not skip.");

    private TansrClient CreateClient(HttpClient? http = null, string? accessToken = null)
    {
        var options = new TansrClientOptions
        {
            BaseUri = origin,
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(accessToken ?? token),
            RequestTimeout = TimeSpan.FromSeconds(10),
            StreamIdleTimeout = TimeSpan.FromSeconds(10),
            MaxReconnectAttempts = 0
        };
        Assert.Equal(SessionContract.Sdk1, options.SessionContract);
        return new TansrClient(options, http);
    }

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task Sdk1DefaultCreatesSendsLiveUnicodeAndReconnectsWithoutResending()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        using var handler = new ObservedHttpHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = CreateClient(http);
        var session = await client.CreateSessionAsync(new CreateSessionOptions(), ct);
        Assert.False(session.Resumed);
        Assert.Equal(-1, session.LastSequence);
        var metadata = await session.GetMetadataAsync(ct);
        Assert.False(metadata.TryGetProperty("contract", out _));
        Assert.Equal(session.Id, metadata.GetProperty("sessionId").GetString());

        const string firstPrompt = "第一轮 café é 😀\n下一行";
        var firstSeen = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var observation = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var streaming = session.ObserveAsync((item, _) =>
            {
                if (item.Name == "msg.text.delta") firstSeen.TrySetResult(item);
                return Task.CompletedTask;
            }, new EventStreamOptions { Reconnect = false }, observation.Token);
            await handler.StreamOpened.Task.WaitAsync(ct);
            var sent = await session.SendAsync(firstPrompt, ct);
            Assert.True(sent.GetProperty("accepted").GetBoolean());
            var first = await firstSeen.Task.WaitAsync(ct);
            Assert.Equal("0", first.Id);
            Assert.Equal("echo[1]: " + firstPrompt + " · 汉字😀é", first.Data.GetProperty("text").GetString());
            observation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streaming);
        }

        // Sending while the observer is disconnected must remain visible through the real replay log.
        const string secondPrompt = "重连后不重复发送 🦊";
        await session.SendAsync(secondPrompt, ct);
        var resumed = await client.GetSessionAsync(session.Id, ct);
        var replayed = new ConcurrentQueue<AgentEvent>();
        var replaySeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var observation = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var replay = resumed.ObserveAsync((item, _) =>
            {
                replayed.Enqueue(item); replaySeen.TrySetResult(true); return Task.CompletedTask;
            }, new EventStreamOptions { LastEventId = "0", Reconnect = false }, observation.Token);
            await replaySeen.Task.WaitAsync(ct);
            observation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);
        }
        var restored = Assert.Single(replayed);
        Assert.Equal("1", restored.Id);
        Assert.Equal("echo[2]: " + secondPrompt + " · 汉字😀é", restored.Data.GetProperty("text").GetString());
        var attached = await client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = session.Id }, ct);
        Assert.Equal(session.Id, attached.Id);
        var history = await attached.GetHistoryAsync(ct);
        Assert.Equal(1, history.GetProperty("lastSeq").GetInt64());
        Assert.Equal(4, history.GetProperty("messages").GetArrayLength());
        Assert.Equal(firstPrompt, Text(history.GetProperty("messages")[0]));
        Assert.Equal(secondPrompt, Text(history.GetProperty("messages")[2]));
        var page = await attached.GetHistoryAsync(2, 2, ct);
        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("offset").GetInt32());
        Assert.Equal(4, page.GetProperty("nextOffset").GetInt32());
        Assert.Equal(2, page.GetProperty("messages").GetArrayLength());
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/interrupt", StringComparison.Ordinal));
        Assert.All(handler.Paths, path => Assert.StartsWith("/v2/", path));
        await session.CloseAsync(ct);
    }

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task RealReplayGapStopsProjectionAndHistoryRemainsAvailable()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var client = CreateClient();
        var ct = deadline.Token;
        var session = await client.CreateSessionAsync(new CreateSessionOptions(), ct);
        for (var i = 0; i < 7; i++) await session.SendAsync("窗口测试 " + i + " 😀", ct);
        var seen = new ConcurrentQueue<AgentEvent>();
        var failure = await Assert.ThrowsAsync<TansrProtocolException>(() => session.ObserveAsync((item, _) =>
        {
            seen.Enqueue(item); return Task.CompletedTask;
        }, new EventStreamOptions { LastEventId = "0", Reconnect = false }, ct));
        Assert.Equal("event_replay_gap", failure.Code);
        var gap = Assert.Single(seen);
        Assert.Equal("server.replay.gap", gap.Name);
        Assert.Null(gap.Id);
        Assert.Equal(session.Id, gap.Data.GetProperty("sessionId").GetString());
        var history = await session.GetHistoryAsync(ct);
        Assert.Equal(14, history.GetProperty("messages").GetArrayLength());
        Assert.Equal(6, history.GetProperty("lastSeq").GetInt64());
        Assert.Equal("窗口测试 6 😀", Text(history.GetProperty("messages")[12]));
        await session.CloseAsync(ct);
    }

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task RealAuthenticationAndSessionOwnershipRejectForeignConsumers()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        using var owner = CreateClient();
        var session = await owner.CreateSessionAsync(new CreateSessionOptions(), ct);
        using var invalid = CreateClient(accessToken: "synthetic-invalid-token");
        var unauthorized = await Assert.ThrowsAsync<TansrHttpException>(() => invalid.GetSessionAsync(session.Id, ct));
        Assert.Equal(401, unauthorized.StatusCode);
        using var other = CreateClient(accessToken: otherToken);
        var forbidden = await Assert.ThrowsAsync<TansrHttpException>(() => other.GetSessionAsync(session.Id, ct));
        Assert.Equal(403, forbidden.StatusCode);
        Assert.Equal(session.Id, (await owner.GetSessionAsync(session.Id, ct)).Id);
        await session.CloseAsync(ct);
    }

    private static string? Text(JsonElement message) => message.GetProperty("blocks")[0].GetProperty("text").GetString();

    private sealed class ObservedHttpHandler : DelegatingHandler
    {
        internal ConcurrentQueue<string> Paths { get; } = new ConcurrentQueue<string>();
        internal TaskCompletionSource<bool> StreamOpened { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ObservedHttpHandler() : base(new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
        { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") StreamOpened.TrySetResult(true);
            return response;
        }
    }
}
