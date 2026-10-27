using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Tests.Examples;

public sealed class ExampleEventRecoveryTests
{
    [Fact]
    public async Task SettledProjectionReplacesHistoryMetadataHistoryWithOneBoundedRead()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        Assert.True(await recovery.RecoverAsync());
        Assert.False(recovery.Required);
        Assert.Equal(10, recovery.ObservationSequence);
        Assert.Equal("unknown", view.Snapshot.Status); // no invented terminal event
        Assert.False(view.Snapshot.HasEventGap);
        Assert.Contains("tail", SessionViewTextFormatter.Format(view.Snapshot));
        var request = Assert.Single(fixture.Handler.Paths);
        Assert.Contains("projection=recovery", request);
        Assert.Contains("maxMessages=128", request);
        Assert.Contains("maxBytes=262144", request);
    }

    [Theory]
    [InlineData("recovery_projection_pending")]
    [InlineData("recovery_projection_unsupported")]
    [InlineData("recovery_projection_stale")]
    [InlineData("recovery_projection_limit")]
    public async Task UnavailableProjectionDoesNotDownloadHistoryOrPoll(string code)
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) => throw new TansrHttpException(409, code);
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        Assert.False(await recovery.RecoverAsync());
        for (var i = 0; i < 8; i++) await recovery.AfterEventAsync(Event("tool.progress", i + 20), CancellationToken.None);
        Assert.Single(fixture.Handler.Paths);
        Assert.True(recovery.Required);
        Assert.Equal(code, recovery.BlockedReason);
        Assert.Equal("unknown", view.Snapshot.Status);
    }

    [Fact]
    public async Task SameConnectionLateHttpCannotOverwriteNewSseOrReopenApproval()
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = (_, _) => pending.Task;
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        var read = recovery.RecoverAsync();
        await fixture.Handler.Entered.Task;
        view.Apply(Event("turn.started", 11));
        view.Apply(Event("server.permission.request", 12, "\"requestId\":\"new\",\"digest\":\"new-digest\""));
        view.Apply(Event("server.permission.closed", 13, "\"requestId\":\"new\""));
        pending.SetResult(Response(Projection(-1)));
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => read);
        Assert.Equal("recovery_projection_stale", error.Code);
        Assert.Equal(13, view.Snapshot.LastSequence);
        Assert.Empty(view.Snapshot.PendingRequests);
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task PendingRecoveryKeepsActiveBlocksToolsAndNewApprovalUsable()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) => throw new TansrHttpException(409, "recovery_projection_pending");
        using var view = new SessionView();
        view.Apply(Event("turn.started", 1));
        view.Apply(Event("msg.block.start", 2, "\"index\":0,\"blockType\":\"text\""));
        view.Apply(Event("msg.text.delta", 3, "\"index\":0,\"text\":\"before\""));
        view.Apply(Event("tool.started", 4, "\"toolCallId\":\"call\",\"name\":\"Read\""));
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        Assert.False(await recovery.RecoverAsync());
        view.Apply(Event("server.permission.request", 5, "\"requestId\":\"fresh\",\"digest\":\"d\""));
        view.Apply(Event("msg.text.delta", 6, "\"index\":0,\"text\":\"-after\""));
        view.Apply(Event("msg.block.end", 7, "\"index\":0"));
        Assert.Contains("before-after", SessionViewTextFormatter.Format(view.Snapshot));
        Assert.DoesNotContain(view.Snapshot.Notices, n => n.Code == "orphan_delta");
        Assert.Single(view.Snapshot.PendingRequests);
        Assert.True(recovery.Required);
    }

    [Fact]
    public void ExplicitDroppedEventsInvalidateOldApprovalButPreserveNewLiveRequests()
    {
        using var view = new SessionView();
        view.Apply(Event("turn.started", 1));
        view.Apply(Event("server.permission.request", 2, "\"requestId\":\"old\",\"digest\":\"d\""));
        view.Apply(Event("session.events_dropped", 3));
        Assert.Empty(view.Snapshot.PendingRequests);
        Assert.Equal("unknown", view.Snapshot.Status);
        view.Apply(Event("server.permission.request", 4, "\"requestId\":\"fresh\",\"digest\":\"d2\""));
        Assert.Equal("fresh", Assert.Single(view.Snapshot.PendingRequests).Id);
    }

    [Fact]
    public async Task ProjectionNeverRestoresHistoricalPermissionsOrUsage()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        view.Apply(Event("cost.usage.updated", 0, "\"usage\":{\"inputTokens\":7,\"outputTokens\":3}"));
        view.Apply(Event("server.permission.request", 1, "\"requestId\":\"old\",\"digest\":\"d\""));
        view.Apply(Gap("evicted"));
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        await recovery.RecoverAsync();
        Assert.Empty(view.Snapshot.PendingRequests);
        Assert.Equal(10, view.Snapshot.SessionTokens);
        Assert.Equal(1, view.Snapshot.UsageRequests);
        view.Apply(Event("cost.usage.updated", 11, "\"usage\":{\"inputTokens\":2}"));
        Assert.Equal(12, view.Snapshot.SessionTokens);
        Assert.Equal(2, view.Snapshot.UsageRequests);
    }

    [Fact]
    public async Task TerminalReadBindsKnownEpochAndCoversSettledBodyWithoutPolling()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        await recovery.RecoverAsync();
        view.Apply(Event("turn.started", 11));
        view.Apply(Event("session.events_dropped", 12));
        fixture.Handler.Respond = (_, _) => throw new TansrHttpException(409, "recovery_projection_pending");
        await recovery.AfterEventAsync(Event("session.events_dropped", 12), CancellationToken.None);
        var terminal = Event("turn.aborted", 13, "\"reason\":\"internal_error\"");
        view.Apply(terminal);
        fixture.Handler.Respond = (request, _) => Task.FromResult(Response(Projection(13, first: 1, history: 13, expected: 1, minimum: 13, terminal: 13)));
        await recovery.AfterEventAsync(terminal, CancellationToken.None);
        Assert.Contains("expectedFirstSeq=1&minTerminalSeq=13", fixture.Handler.Paths.Last());
        Assert.False(recovery.Required);
        Assert.Equal("error", view.Snapshot.Status);
        Assert.Equal(3, fixture.Handler.Paths.Count);
    }

    [Fact]
    public async Task NewEpochGapNeverReusesPreviousTerminalFence()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        await recovery.RecoverAsync();
        view.Apply(Gap("ahead_of_log"));
        fixture.Handler.Respond = (_, _) => Task.FromResult(Response(Projection(-1, first: 2, history: 3)));
        await recovery.AfterEventAsync(Gap("ahead_of_log"), CancellationToken.None);
        Assert.DoesNotContain("expectedFirstSeq", fixture.Handler.Paths.Last());
        Assert.DoesNotContain("minTerminalSeq", fixture.Handler.Paths.Last());
        Assert.Equal(3, view.Snapshot.LastSequence);
        Assert.False(view.Snapshot.HasEventGap);
        view.Apply(Event("turn.started", 4));
        Assert.Equal("thinking", view.Snapshot.Status);
    }

    [Fact]
    public async Task NewEpochPendingAcceptsFreshApprovalWithoutJoiningOldRuntimeOrClearingGap()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        view.Apply(Event("turn.started", 100));
        view.Apply(Event("msg.block.start", 101, "\"index\":0,\"blockType\":\"text\""));
        view.Apply(Event("msg.text.delta", 102, "\"index\":0,\"text\":\"old-body\""));
        view.Apply(Event("tool.started", 103, "\"toolCallId\":\"old-call\",\"name\":\"Read\""));
        view.Apply(Event("cost.usage.updated", 104, "\"usage\":{\"inputTokens\":7}"));
        view.Apply(Event("server.permission.request", 105, "\"requestId\":\"old\",\"digest\":\"old-digest\""));
        fixture.Handler.Respond = (_, _) => throw new TansrHttpException(409, "recovery_projection_pending");
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        for (var epoch = 0; epoch < 2; epoch++)
        {
            var gap = Gap("ahead_of_log"); view.Apply(gap);
            await recovery.AfterEventAsync(gap, CancellationToken.None);
            Assert.Empty(view.Snapshot.PendingRequests);
            Assert.Empty(view.Snapshot.Tools);
            Assert.True(view.Snapshot.HasEventGap);
            Assert.True(recovery.Required);
            view.Apply(Event("server.permission.request", 1, "\"requestId\":\"fresh-" + epoch + "\",\"digest\":\"fresh\""));
            Assert.Equal("fresh-" + epoch, Assert.Single(view.Snapshot.PendingRequests).Id);
            view.Apply(Event("msg.text.delta", 2, "\"index\":0,\"text\":\"must-not-join\""));
            Assert.DoesNotContain("old-bodymust-not-join", SessionViewTextFormatter.Format(view.Snapshot));
            Assert.Equal(7, view.Snapshot.SessionTokens);
        }
        view.Apply(Event("server.permission.closed", 3, "\"requestId\":\"fresh-1\""));
        Assert.Empty(view.Snapshot.PendingRequests);
        var terminal = Event("turn.aborted", 4, "\"reason\":\"user_canceled\""); view.Apply(terminal);
        fixture.Handler.Respond = (_, _) => Task.FromResult(Response(Projection(4, first: 1, history: 4, terminal: 4)));
        await recovery.AfterEventAsync(terminal, CancellationToken.None);
        Assert.False(recovery.Required);
        Assert.False(view.Snapshot.HasEventGap);
        Assert.DoesNotContain("minTerminalSeq", fixture.Handler.Paths.Last());
        Assert.Empty(view.Snapshot.PendingRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalSettlementDoesNotSwallowCallbackCancellation(bool afterInstall)
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        var fail = false;
        var failure = new OperationCanceledException("synthetic observer cancellation");
        var recovery = new SessionRecovery(fixture.Session, view,
            installed: (_, _) => afterInstall && fail ? Task.FromException(failure) : Task.CompletedTask,
            changed: (_, _) => !afterInstall && fail ? Task.FromException(failure) : Task.CompletedTask);
        fixture.Handler.Respond = (_, _) => throw new TansrHttpException(409, "recovery_projection_pending");
        await recovery.RecoverAsync();
        var terminal = Event("turn.completed", 1); view.Apply(terminal);
        fixture.Handler.Respond = (_, _) => Task.FromResult(Response(Projection(1)));
        fail = true;
        Assert.Same(failure, await Record.ExceptionAsync(() => recovery.AfterEventAsync(terminal, CancellationToken.None)));
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task TerminalBeforeArchiveSettlementUsesOnlyBoundedProjectionReads()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        await recovery.RecoverAsync();
        view.Apply(Event("session.events_dropped", 11));
        fixture.Handler.Respond = (_, _) => throw new TansrHttpException(409, "recovery_projection_pending");
        await recovery.AfterEventAsync(Event("session.events_dropped", 11), CancellationToken.None);
        var terminal = Event("turn.completed", 12);
        view.Apply(terminal);
        var reads = 0;
        fixture.Handler.Respond = (_, _) => ++reads == 1 ? throw new TansrHttpException(409, "recovery_projection_pending") :
            Task.FromResult(Response(Projection(12, first: 1, history: 12, expected: 1, minimum: 12)));
        await recovery.AfterEventAsync(terminal, CancellationToken.None);
        Assert.Equal(2, reads);
        Assert.False(recovery.Required);
        Assert.All(fixture.Handler.Paths, path => Assert.Contains("projection=recovery", path));
    }

    [Fact]
    public async Task SettlementBudgetExhaustionKeepsRequiredWithoutFullHistoryFallback()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        fixture.Handler.Respond = (_, _) => throw new TansrHttpException(409, "recovery_projection_pending");
        await recovery.RecoverAsync();
        var terminal = Event("turn.completed", 12); view.Apply(terminal);
        await recovery.AfterEventAsync(terminal, CancellationToken.None);
        Assert.Equal(7, fixture.Handler.Paths.Count); // initial read plus six terminal-settlement reads
        Assert.True(recovery.Required);
        Assert.Equal("recovery_projection_pending", recovery.BlockedReason);
    }

    [Fact]
    public async Task CallerCancellationCannotInstallLateSuccessfulResponse()
    {
        using var fixture = new Fixture();
        using var canceled = new CancellationTokenSource();
        fixture.Handler.Respond = (_, _) => { canceled.Cancel(); return Task.FromResult(Response(Projection(-1))); };
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecoverAsync(canceled.Token));
        Assert.True(recovery.Required);
        Assert.Empty(view.Snapshot.Messages);
    }

    [Theory]
    [InlineData("sessionId", "\"foreign\"")]
    [InlineData("requestedAfterSeq", "99")]
    [InlineData("requestedFirstSeq", "1")]
    [InlineData("firstSeq", "11")]
    [InlineData("historyLastSeq", "12")]
    [InlineData("historyGeneration", "\"1\"")]
    [InlineData("windowComplete", "false")]
    [InlineData("historyComplete", "true")]
    [InlineData("status", "\"running\"")]
    [InlineData("nextOffset", "99")]
    [InlineData("terminal", "{\"seq\":99,\"type\":\"turn.completed\"}")]
    public async Task InvalidProofNeverPartiallyInstalls(string field, string replacement)
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) => { var json = Projection(-1); json[field] = JsonNode.Parse(replacement); return Task.FromResult(Response(json)); };
        using var view = new SessionView();
        view.AppendUserMessage("preserve");
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        await Assert.ThrowsAsync<TansrProtocolException>(() => recovery.RecoverAsync());
        Assert.Contains("preserve", SessionViewTextFormatter.Format(view.Snapshot));
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task ResponseBodyLimitIsEnforcedBeforeProjectionInstallation()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) => { var value = Projection(-1); value["padding"] = new string('x', 300000); return Task.FromResult(Response(value)); };
        using var view = new SessionView();
        var recovery = ExampleEventRecovery.Create(fixture.Session, view);
        await Assert.ThrowsAnyAsync<TansrException>(() => recovery.RecoverAsync());
        Assert.Empty(view.Snapshot.Messages);
        Assert.True(recovery.Required);
    }

    [Fact]
    public async Task PartialRecoveryWindowDoesNotRelaxLegacyFullHistoryContract()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        var projection = await fixture.Session.GetRecoveryProjectionAsync();
        Assert.Throws<TansrProtocolException>(() => view.RestoreHistory(projection.Data, "s"));
        view.RestoreRecoveryProjection(projection, view.Snapshot.Version);
        Assert.True(view.Snapshot.PresentationTruncated);
    }

    [Fact]
    public async Task NarratorUsesSameObservationOriginWithoutInventingTerminal()
    {
        using var fixture = new Fixture();
        using var view = new SessionView();
        var lines = new List<string>();
        using var narrator = new SessionNarrator(lines.Add);
        narrator.Apply(Gap("ahead_of_log"));
        var recovery = ExampleEventRecovery.Create(fixture.Session, view, narrator);
        await recovery.RecoverAsync();
        Assert.DoesNotContain(lines, line => line.Contains("completed") || line.Contains("aborted"));
        narrator.Apply(Event("turn.started", 11));
        Assert.Contains(lines, line => line.Contains("turn started"));
    }

    internal static JsonObject Projection(long after, long first = 1, long history = 10, long? expected = null, long? minimum = null, long? terminal = null) => new()
    {
        ["kind"] = "tansr-session-recovery-projection-v1", ["sessionId"] = "s", ["requestedAfterSeq"] = after,
        ["requestedFirstSeq"] = expected, ["requestedMinTerminalSeq"] = minimum,
        ["firstSeq"] = first, ["lastSeq"] = history, ["historyGeneration"] = 1, ["historyLastSeq"] = history,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "assistant", ["blocks"] = new JsonArray(new JsonObject { ["t"] = "text", ["text"] = "tail" }) }),
        ["total"] = 100, ["offset"] = 99, ["nextOffset"] = 100, ["windowComplete"] = true, ["historyComplete"] = false,
        ["terminal"] = terminal.HasValue ? new JsonObject { ["seq"] = terminal.Value, ["type"] = "turn.aborted" } : null,
        ["status"] = "idle"
    };
    private static HttpResponseMessage Response(JsonNode value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    private static AgentEvent Gap(string reason) => new("server.replay.gap", null, Json("{\"sessionId\":\"s\",\"type\":\"server.replay.gap\",\"reason\":\"" + reason + "\"}"));
    private static AgentEvent Event(string name, long sequence, string? fields = null) => new(name, sequence.ToString(),
        Json("{\"sessionId\":\"s\",\"type\":\"" + name + "\",\"seq\":" + sequence + (fields == null ? "" : "," + fields) + "}"));
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private sealed class Fixture : IDisposable
    {
        internal readonly Handler Handler = new();
        private readonly HttpClient http;
        internal readonly TansrClient Client;
        internal readonly AgentSession Session;
        internal Fixture()
        {
            http = UnifiedStamp.Client(Handler);
            Client = new TansrClient(new TansrClientOptions { BaseUri = new Uri("https://synthetic.invalid"), TokenProvider = _ => Task.FromResult("synthetic"), PrincipalProvider = () => "app/user" }, http);
            Session = new AgentSession(Client, "s", -1, true);
        }
        public void Dispose() { Client.Dispose(); http.Dispose(); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal readonly List<string> Paths = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery); Entered.TrySetResult();
            if (Respond != null) return Respond(request, cancellationToken);
            var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
            Assert.Equal("recovery", query["projection"]);
            return Task.FromResult(Response(Projection(long.Parse(query["afterSeq"]))));
        }
    }
}
