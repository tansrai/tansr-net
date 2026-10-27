using System.Text.Json;
using Tansr.Examples;

namespace Tansr.Sdk.Tests.Examples;

public sealed class ExampleHistoryPagerTests
{
    [Fact]
    public async Task LargeHistoryReadsOnlyTheRequestedPageWithoutPrefetch()
    {
        var requests = new List<(int Offset, int Limit)>();
        var pager = new ExampleHistoryPager("synthetic", (offset, limit, _) =>
        {
            requests.Add((offset, limit));
            return Task.FromResult(Page(offset, offset + 50, 1_000_000));
        }, () => true);
        Assert.Empty(requests);
        await pager.FirstAsync(CancellationToken.None);
        Assert.Equal(new[] { (0, 50) }, requests);
        Assert.Equal(1_000_000, pager.Total);
        Assert.True(pager.HasNext);
        await pager.NextAsync(CancellationToken.None);
        Assert.Equal(new[] { (0, 50), (50, 50) }, requests);
        Assert.Equal(50, pager.Offset);
        Assert.Equal(1, pager.Page!.Value.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task UsesRawNextOffsetWhenProjectionIsEmptyAndPairingExpandsThePage()
    {
        var offsets = new List<int>();
        var pager = new ExampleHistoryPager("synthetic", (offset, _, _) =>
        {
            offsets.Add(offset);
            return Task.FromResult(offset == 0 ? Page(0, 61, 100, count: 0) : Page(60, 100, 100));
        }, () => true);
        await pager.FirstAsync(CancellationToken.None);
        await pager.NextAsync(CancellationToken.None);
        Assert.Equal(new[] { 0, 61 }, offsets);
        Assert.Equal(60, pager.Offset);
        Assert.Equal(100, pager.NextOffset);
        Assert.False(pager.HasNext);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pager.NextAsync(CancellationToken.None));
        Assert.Equal(2, offsets.Count);
    }

    [Fact]
    public async Task ToolPairingMayReturnMoreThanTheRequestedMessageCount()
    {
        var pager = new ExampleHistoryPager("synthetic", (_, _, _) => Task.FromResult(Page(0, 60, 60, count: 60)), () => true);
        await pager.FirstAsync(CancellationToken.None);
        Assert.Equal(60, pager.Page!.Value.GetProperty("messages").GetArrayLength());
        Assert.False(pager.HasNext);
    }

    [Theory]
    [InlineData(11, 100)]
    [InlineData(10, 101)]
    public async Task ChangedVersionKeepsThePreviousPageAndRequiresExplicitRestart(long sequence, int total)
    {
        var calls = 0;
        var pager = new ExampleHistoryPager("synthetic", (offset, _, _) => Task.FromResult(++calls switch
        {
            1 => Page(0, 50, 100),
            2 => Page(offset, 100, total, sequence),
            _ => Page(0, 50, total, sequence)
        }), () => true);
        await pager.FirstAsync(CancellationToken.None);
        var original = pager.Page!.Value.GetRawText();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pager.NextAsync(CancellationToken.None));
        Assert.Equal("history_page_changed", error.Message);
        Assert.Equal(original, pager.Page!.Value.GetRawText());
        Assert.True(pager.Invalidated);
        Assert.False(pager.HasNext);
        Assert.Equal(2, calls);
        await pager.FirstAsync(CancellationToken.None);
        Assert.False(pager.Invalidated);
        Assert.Equal(0, pager.Offset);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationOrReplacedSessionCannotInstallALatePage(bool cancel)
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var current = true;
        var calls = 0;
        var pager = new ExampleHistoryPager("synthetic", (_, _, _) => { calls++; return response.Task; }, () => current);
        var pending = pager.FirstAsync(cancellation.Token);
        if (cancel) cancellation.Cancel(); else current = false;
        response.SetResult(Page(0, 1, 1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(pager.Page);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentClicksDoNotStartASecondRequest()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var pager = new ExampleHistoryPager("synthetic", (_, _, _) => { calls++; return response.Task; }, () => true);
        var first = pager.FirstAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pager.FirstAsync(CancellationToken.None));
        Assert.Equal("history_page_read_in_progress", error.Message);
        response.SetResult(Page(0, 0, 0, count: 0));
        await first;
        Assert.False(pager.HasNext);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RequestFailurePreservesOriginalExceptionAndPageWithoutRetry()
    {
        var failure = new IOException("synthetic failure");
        int calls = 0;
        var pager = new ExampleHistoryPager("synthetic", (_, _, _) => ++calls == 1
            ? Task.FromResult(Page(0, 50, 100)) : Task.FromException<JsonElement>(failure), () => true);
        await pager.FirstAsync(CancellationToken.None);
        var original = pager.Page!.Value.GetRawText();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pager.NextAsync(CancellationToken.None)));
        Assert.Equal(original, pager.Page!.Value.GetRawText());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CurrentPresentationChecksKeepTheHostsSynchronizationContext()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new RecordingContext();
        var observed = new List<SynchronizationContext?>();
        var pager = new ExampleHistoryPager("synthetic", (_, _, _) => response.Task,
            () => { observed.Add(SynchronizationContext.Current); return true; });
        var original = SynchronizationContext.Current;
        Task pending;
        try { SynchronizationContext.SetSynchronizationContext(context); pending = pager.FirstAsync(CancellationToken.None); }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        response.SetResult(Page(0, 1, 1));
        var resume = await context.Callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { SynchronizationContext.SetSynchronizationContext(context); resume(); }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await pending;
        Assert.Equal(2, observed.Count);
        Assert.All(observed, value => Assert.Same(context, value));
    }

    [Theory]
    [InlineData("{\"sessionId\":\"synthetic\",\"messages\":[]}")]
    [InlineData("{\"sessionId\":\"other\",\"messages\":[],\"offset\":0,\"nextOffset\":1,\"total\":2,\"lastSeq\":10}")]
    [InlineData("{\"sessionId\":\"synthetic\",\"messages\":[],\"offset\":1,\"nextOffset\":2,\"total\":2,\"lastSeq\":10}")]
    [InlineData("{\"sessionId\":\"synthetic\",\"messages\":[],\"offset\":0,\"nextOffset\":0,\"total\":2,\"lastSeq\":10}")]
    [InlineData("{\"sessionId\":\"synthetic\",\"messages\":[],\"offset\":0,\"nextOffset\":3,\"total\":2,\"lastSeq\":10}")]
    [InlineData("{\"sessionId\":\"synthetic\",\"messages\":[],\"offset\":0,\"nextOffset\":1,\"total\":2,\"lastSeq\":\"10\"}")]
    public async Task InvalidOrUnpagedResponseNeverFallsBackToReadingEverything(string json)
    {
        int calls = 0;
        var pager = new ExampleHistoryPager("synthetic", (_, _, _) => { calls++; return Task.FromResult(Json(json)); }, () => true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pager.FirstAsync(CancellationToken.None));
        Assert.Equal("invalid_history_page", error.Message);
        Assert.Null(pager.Page);
        Assert.Equal(1, calls);
    }

    private static JsonElement Page(int offset, int next, int total, long sequence = 10, int count = 1) => Json(JsonSerializer.Serialize(new
    {
        sessionId = "synthetic",
        offset,
        nextOffset = next,
        total,
        lastSeq = sequence,
        messages = Enumerable.Range(0, count).Select(_ => new { role = "user", blocks = Array.Empty<object>() }).ToArray()
    }));
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private sealed class RecordingContext : SynchronizationContext
    {
        internal TaskCompletionSource<Action> Callback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback action, object? state) => Callback.TrySetResult(() => action(state));
    }
}
