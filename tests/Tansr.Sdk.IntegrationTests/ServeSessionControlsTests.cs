using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>Actual Serve controls with a deterministic synthetic upstream, not mocked HTTP
/// receipts or a replacement query loop. These cases contribute to the original A05/A07.</summary>
public sealed class ServeSessionControlsTests
{
    private const string Inserted = "NET_INSERT_ORIGINAL · 中文 café 👩🏽‍💻";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jMZkAAAAASUVORK5CYII=";
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required; use scripts/serve-integration.mjs.");
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static TansrClientOptions Options(Uri? origin = null, bool other = false) => new()
    {
        BaseUri = origin ?? new Uri(Required("TANSR_SERVE_CONTROLS_URL")),
        AllowInsecureLoopback = true,
        TokenProvider = _ => Task.FromResult(Required(other ? "TANSR_SERVE_TEST_OTHER_TOKEN" : "TANSR_SERVE_TEST_TOKEN")),
        PrincipalProvider = () => "net-integration-app/" + (other ? "net-integration-other" : "net-integration-user"),
        ExecutionScopeProvider = () => Json(new { applicationScopeId = "net-integration-app", endUserId = other ? "net-integration-other" : "net-integration-user", authorizationRevision = "1" }),
        RequestTimeout = TimeSpan.FromSeconds(10),
        StreamIdleTimeout = TimeSpan.FromSeconds(15),
        MaxReconnectAttempts = 0
    };

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task AcceptedInputResponseLossAndObservationReconnectRetainTheOriginalMultimodalTurn()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50)); var ct = deadline.Token;
        using var loss = new AcceptedInputLossHandler(); using var http = new HttpClient(loss);
        using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new CreateSessionOptions { Tools = [] }, ct);
        var seen = new ConcurrentQueue<AgentEvent>();
        Exception? primaryFailure = null;
        try
        {
            using var run = session.StartRun([MessageBlock.Text("NET-CONTROLS/input-original/hold"), MessageBlock.Image("image/png", Png)],
                new SessionRunOptions { Timeout = TimeSpan.FromSeconds(30) }, (item, _) => { seen.Enqueue(item); return Task.CompletedTask; }, ct);
            await run.Acceptance; await CommandAsync(new { action = "wait-held", scenario = "input-original" }, ct);
            var capabilities = await session.GetInputCapabilitiesAsync(ct);
            Assert.True(capabilities.GetProperty("memoryAck").GetBoolean()); Assert.False(capabilities.GetProperty("durableAck").GetBoolean());
            Assert.False(capabilities.GetProperty("image").GetBoolean()); var target = Target(capabilities);
            var originalDraft = Inserted;
            var durable = await Assert.ThrowsAsync<TansrHttpException>(() => session.SubmitInputAsync("input-durable", target, originalDraft, true, ct));
            Assert.Equal(422, durable.StatusCode); Assert.Equal("durable_unsupported", durable.Code); Assert.Equal(Inserted, originalDraft);
            var lost = await Assert.ThrowsAsync<TansrProtocolException>(() => session.SubmitInputAsync("input-original", target, originalDraft, cancellationToken: ct));
            Assert.Equal("network_error", lost.Code); Assert.Equal(1, loss.LostCount); Assert.NotNull(loss.Accepted);
            Assert.Equal("accepted", loss.Accepted.Value.GetProperty("outcome").GetString());
            while (!seen.Any(item => item.Name == "turn.started") || !seen.Any(item => item.Name == "msg.text.delta")) await Task.Delay(10, ct);
            var beforeDisconnect = session.LastSequence;
            run.CancelObservation(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion);
            Assert.Equal(SessionStatus.Running, (await session.ReadMetadataAsync(ct)).Status);

            using var connectedClient = new TansrClient(Options());
            var reconnected = await connectedClient.GetSessionAsync(session.Id, ct); Assert.Equal(session.Id, reconnected.Id);
            using var observe = new Observation(reconnected, beforeDisconnect, seen, ct);
            var status = await reconnected.GetInputStatusAsync("input-original", target, ct);
            Assert.Equal("accepted", status.GetProperty("receipt").GetProperty("state").GetString());
            var replay = await reconnected.SubmitInputAsync("input-original", target, originalDraft, cancellationToken: ct);
            Assert.Equal(WireJson.CanonicalString(loss.Accepted.Value), WireJson.CanonicalString(replay));
            var conflict = await Assert.ThrowsAsync<TansrHttpException>(() => reconnected.SubmitInputAsync("input-original", target, "NET_WRONG_REPLACEMENT", cancellationToken: ct));
            Assert.Equal("input_conflict", conflict.Code); Assert.Equal(409, conflict.StatusCode);
            foreach (var (bad, code) in new[] { (new SessionInputTarget("different-epoch", target.TurnId), "epoch_mismatch"), (new SessionInputTarget(target.HistoryEpoch, "different-turn"), "turn_mismatch") })
            {
                var rejected = await Assert.ThrowsAsync<TansrHttpException>(() => reconnected.SubmitInputAsync("input-stale-" + code, bad, "NET_REJECTED_DRAFT", cancellationToken: ct));
                Assert.Equal(409, rejected.StatusCode); Assert.Equal(code, rejected.Code);
            }
            using (var other = new TansrClient(Options(other: true)))
            {
                var denied = await Assert.ThrowsAsync<TansrHttpException>(() => other.GetSessionAsync(session.Id, ct)); Assert.Equal(403, denied.StatusCode);
            }
            await CommandAsync(new { action = "release", scenario = "input-original" }, ct);
            var terminal = await observe.Terminal.WaitAsync(ct); Assert.Equal("turn.completed", terminal.Name);
            Assert.Equal(target.TurnId, terminal.Data.GetProperty("turnId").GetString());
            await CommandAsync(new { action = "settle", sessionId = session.Id }, ct);
            status = await reconnected.GetInputStatusAsync("input-original", target, ct);
            Assert.Equal("consumed", status.GetProperty("receipt").GetProperty("state").GetString());
            Assert.Equal(loss.Accepted.Value.GetProperty("receipt").GetProperty("ordinal").GetInt64(), status.GetProperty("receipt").GetProperty("ordinal").GetInt64());
            var lateReplay = await reconnected.SubmitInputAsync("input-original", target, originalDraft, cancellationToken: ct);
            Assert.Equal("consumed", lateReplay.GetProperty("receipt").GetProperty("state").GetString());
            var late = await Assert.ThrowsAsync<TansrHttpException>(() => reconnected.SubmitInputAsync("input-late", target, "NET_LATE_DRAFT", cancellationToken: ct));
            Assert.Equal("turn_closed", late.Code); Assert.Equal(409, late.StatusCode);
            var evidence = await CommandAsync(new { action = "inspect", scenario = "input-original" }, ct);
            var requests = evidence.GetProperty("exchanges").EnumerateArray().Select(item => item.GetProperty("request")).ToArray(); Assert.Equal(2, requests.Length);
            var image = Assert.Single(requests[0].GetProperty("thread").EnumerateArray().SelectMany(item => item.GetProperty("blocks").EnumerateArray()), block => block.GetProperty("t").GetString() == "image");
            Assert.Equal("image/png", image.GetProperty("mime").GetString()); Assert.Equal(Png, image.GetProperty("v").GetString());
            Assert.Single(UserTexts(requests[1]), text => text == Inserted);
            var history = (await session.GetHistoryAsync(ct)).GetRawText(); Assert.Contains(Inserted, DecodeJsonStrings(history));
            Assert.DoesNotContain("NET_WRONG_REPLACEMENT", history); Assert.DoesNotContain("NET_REJECTED_DRAFT", history); Assert.DoesNotContain("NET_LATE_DRAFT", history);
            Assert.Single(seen, item => item.Name == "turn.started"); Assert.Single(seen, item => item.Name == "turn.completed");
            Assert.DoesNotContain(seen, item => item.Name is "turn.aborted" or "turn.error");
            await observe.StopAsync();

            // Explicit interruption is separate from the observation disconnect above.
            var cancelledEvents = new ConcurrentQueue<AgentEvent>();
            using var cancelledRun = reconnected.StartRun("NET-CONTROLS/input-cancel/hold", observer: (item, _) => { cancelledEvents.Enqueue(item); return Task.CompletedTask; }, cancellationToken: ct);
            await cancelledRun.Acceptance; await CommandAsync(new { action = "wait-held", scenario = "input-cancel" }, ct);
            var cancelledTarget = Target(await reconnected.GetInputCapabilitiesAsync(ct));
            await reconnected.SubmitInputBlocksAsync("input-pending-cancel", cancelledTarget, ["NET_NEVER_CONSUMED"], cancellationToken: ct);
            await reconnected.CancelAsync(ct); Assert.True((await cancelledRun.Completion).WasAborted);
            await CommandAsync(new { action = "settle", sessionId = session.Id }, ct);
            var closed = await reconnected.GetInputStatusAsync("input-pending-cancel", cancelledTarget, ct);
            Assert.Equal("closed", closed.GetProperty("receipt").GetProperty("state").GetString());
            Assert.Single(cancelledEvents, item => item.Name == "turn.aborted");
            Assert.True((await CommandAsync(new { action = "inspect", scenario = "input-cancel" }, ct)).GetProperty("aborted").GetBoolean());
            var resumed = await reconnected.SendAndObserveAsync("NET-CONTROLS/input-recovery/normal", cancellationToken: ct);
            Assert.False(resumed.WasAborted); Assert.Equal(session.Id, reconnected.Id);
            Assert.DoesNotContain("NET_NEVER_CONSUMED", (await reconnected.GetHistoryAsync(ct)).GetRawText());
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally { await CloseAsync(session, primaryFailure); }
    }

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task RealConfigurationCasSurvivesResponseLossAndRejectsBusyOrUnsafeWindowTransitions()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        using var loss = new LostResponseHandler("/configuration"); using var http = new HttpClient(loss);
        using var client = new TansrClient(Options(), http); var control = new TerminalSessionControl(client, enablePreview: true);
        var session = await client.CreateSessionAsync(new CreateSessionOptions { Model = "controls-main", Tools = [] }, ct);
        Exception? primaryFailure = null;
        try
        {
            await session.SendAndObserveAsync("NET-CONTROLS/config-baseline/normal", cancellationToken: ct);
            await CommandAsync(new { action = "settle", sessionId = session.Id }, ct);
            var history = await session.GetHistoryAsync(ct); var original = await control.ReadConfigurationAsync(session.Id, ct);
            Assert.Equal(0, original.GetProperty("configuration").GetProperty("revision").GetInt64());
            var change = control.CreateConfigurationOperation(session.Id, "config-original", 0, Json(new { model = "controls-large", thinking = new { budget = 64 } }));
            var lost = await Assert.ThrowsAsync<TansrProtocolException>(() => control.ApplyConfigurationAsync(change, ct)); Assert.Equal("network_error", lost.Code);
            Assert.Equal(1, loss.LostResponseCount);
            using var resumedClient = new TansrClient(Options()); var recoveredControl = new TerminalSessionControl(resumedClient, enablePreview: true);
            var restored = recoveredControl.RestoreConfigurationOperation(change.Request, change.Scope);
            var replay = await recoveredControl.ReplayConfigurationAsync(restored, ct);
            Assert.Equal("replayed", replay.GetProperty("status").GetString());
            Assert.Equal(WireJson.CanonicalString(loss.LostReceipt!.Value.GetProperty("configuration")), WireJson.CanonicalString(replay.GetProperty("configuration")));
            var afterConfiguration = await session.GetHistoryAsync(ct);
            Assert.Equal(history.GetProperty("sessionId").GetString(), afterConfiguration.GetProperty("sessionId").GetString());
            Assert.Equal(WireJson.CanonicalString(history.GetProperty("messages")), WireJson.CanonicalString(afterConfiguration.GetProperty("messages")));
            Assert.True(afterConfiguration.GetProperty("lastSeq").GetInt64() >= history.GetProperty("lastSeq").GetInt64());
            var current = replay.GetProperty("configuration"); Assert.Equal(1, current.GetProperty("revision").GetInt64());
            Assert.Equal(65536, (await session.ReadMetadataAsync(ct)).Context!.Value.GetProperty("selected").GetProperty("contextWindowTokens").GetInt32());
            foreach (var (id, revision, changes, code) in new[]
            {
                ("config-stale", 0L, Json(new { thinking = (object?)null }), "revision_conflict"),
                ("config-original", 0L, Json(new { model = "controls-main" }), "request_conflict"),
                ("config-tiny", 1L, Json(new { model = "controls-tiny", thinking = (object?)null }), "context_transition_required")
            })
            {
                var error = await Assert.ThrowsAsync<TansrHttpException>(() => recoveredControl.ApplyConfigurationAsync(recoveredControl.CreateConfigurationOperation(session.Id, id, revision, changes), ct));
                Assert.Equal(409, error.StatusCode); Assert.Equal(code, error.Code);
                Assert.Equal(WireJson.CanonicalString(current), WireJson.CanonicalString((await recoveredControl.ReadConfigurationAsync(session.Id, ct)).GetProperty("configuration")));
            }
            using var run = session.StartRun("NET-CONTROLS/config-busy/hold", cancellationToken: ct);
            await run.Acceptance; await CommandAsync(new { action = "wait-held", scenario = "config-busy" }, ct);
            var busy = await Assert.ThrowsAsync<TansrHttpException>(() => recoveredControl.ApplyConfigurationAsync(recoveredControl.CreateConfigurationOperation(session.Id, "config-busy", 1, Json(new { model = "controls-main", thinking = (object?)null })), ct));
            Assert.Equal(409, busy.StatusCode); Assert.Equal("busy", busy.Code); Assert.Equal("backoff", busy.RetryAction);
            await CommandAsync(new { action = "release", scenario = "config-busy" }, ct); Assert.False((await run.Completion).WasAborted);
            await CommandAsync(new { action = "settle", sessionId = session.Id }, ct);
            var request = (await CommandAsync(new { action = "inspect", scenario = "config-busy" }, ct)).GetProperty("exchanges")[0].GetProperty("request");
            Assert.Equal("controls-large", request.GetProperty("model").GetString()); Assert.Equal("low", request.GetProperty("params").GetProperty("reasoning").GetString());
            Assert.Contains("NET-CONTROLS/config-baseline/normal", UserTexts(request));
            Assert.Equal(WireJson.CanonicalString(current), WireJson.CanonicalString((await recoveredControl.ReadConfigurationAsync(session.Id, ct)).GetProperty("configuration")));
            var off = await recoveredControl.ApplyConfigurationAsync(recoveredControl.CreateConfigurationOperation(session.Id, "config-off", 1, Json(new { model = "controls-main", thinking = (object?)null })), ct);
            Assert.Equal(2, off.GetProperty("configuration").GetProperty("revision").GetInt64());
            await session.SendAndObserveAsync("NET-CONTROLS/config-final/normal", cancellationToken: ct);
            var final = (await CommandAsync(new { action = "inspect", scenario = "config-final" }, ct)).GetProperty("exchanges")[0].GetProperty("request");
            Assert.Equal("controls-main", final.GetProperty("model").GetString()); Assert.False(final.GetProperty("params").TryGetProperty("reasoning", out _));
            Assert.Equal(32768, (await session.ReadMetadataAsync(ct)).Context!.Value.GetProperty("selected").GetProperty("contextWindowTokens").GetInt32());
            Assert.Equal(session.Id, (await resumedClient.GetSessionAsync(session.Id, ct)).Id);
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally { await CloseAsync(session, primaryFailure); }
    }

    [Theory]
    [InlineData("fallback", "absent")]
    [InlineData("fallback", "explicit")]
    [InlineData("fallback", "empty")]
    [InlineData("prepend", "absent")]
    [InlineData("prepend", "explicit")]
    [InlineData("prepend", "empty")]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task PlatformPromptRefreshPreservesTheCurrentTurnSnapshotAndHostPrecedence(string policy, string profile)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        var origins = await CommandAsync(new { action = "origins" }, ct);
        var scenario = "prompt-" + policy + "-" + profile;
        await CommandAsync(new { action = "platform", profile, policy, prompt = "NET_PLATFORM_P1" }, ct);
        using var client = new TansrClient(Options(new Uri(origins.GetProperty(profile).GetString()!)));
        var session = await client.CreateSessionAsync(new CreateSessionOptions { Tools = [] }, ct);
        Exception? primaryFailure = null;
        try
        {
            using var run = session.StartRun("NET-CONTROLS/" + scenario + "/hold", cancellationToken: ct);
            await run.Acceptance; await CommandAsync(new { action = "wait-held", scenario }, ct);
            var target = Target(await session.GetInputCapabilitiesAsync(ct));
            await CommandAsync(new { action = "platform", profile, policy, prompt = "NET_PLATFORM_P2" }, ct);
            await session.SubmitInputAsync("prompt-original", target, "NET_PROMPT_SAME_TURN_INSERT", cancellationToken: ct);
            await CommandAsync(new { action = "release", scenario }, ct); Assert.False((await run.Completion).WasAborted);
            var source = await session.ReadApplicationPromptAsync(ct); Assert.True(source.IsKnown);
            Assert.Equal(policy == "fallback" ? ApplicationPromptPolicy.Fallback : ApplicationPromptPolicy.Prepend, source.Policy);
            Assert.Equal(profile == "absent" || policy == "prepend" && profile == "empty" ? ApplicationPromptSource.Platform : policy == "prepend" ? ApplicationPromptSource.PlatformAndSdk : ApplicationPromptSource.Sdk, source.Source);
            Assert.False((await session.GetMetadataAsync(ct)).TryGetProperty("applicationPrompt", out _));
            await CommandAsync(new { action = "settle", sessionId = session.Id }, ct);
            await session.SendAndObserveAsync("NET-CONTROLS/" + scenario + "/next", cancellationToken: ct);
            var requests = (await CommandAsync(new { action = "inspect", scenario }, ct)).GetProperty("exchanges").EnumerateArray().Select(item => item.GetProperty("request")).ToArray();
            Assert.Equal(3, requests.Length);
            string[] Expected(string platform) => [.. (profile == "absent" || policy == "prepend" ? new[] { platform } : []), .. (profile == "explicit" ? new[] { "NET_HOST_S" } : []), "NET_APPEND_A"];
            Assert.Equal(Expected("NET_PLATFORM_P1"), SystemTexts(requests[0]));
            Assert.Equal(Expected("NET_PLATFORM_P1"), SystemTexts(requests[1]));
            Assert.Equal(Expected("NET_PLATFORM_P2"), SystemTexts(requests[2]));
            Assert.Single(UserTexts(requests[1]), text => text == "NET_PROMPT_SAME_TURN_INSERT");
            Assert.All(requests, request => Assert.False(request.TryGetProperty("tools", out var tools) && tools.GetArrayLength() != 0));
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally { await CloseAsync(session, primaryFailure); }
    }

    private static SessionInputTarget Target(JsonElement capabilities) => new(capabilities.GetProperty("target").GetProperty("historyEpoch").GetString()!, capabilities.GetProperty("target").GetProperty("turnId").GetString()!);
    private static string[] UserTexts(JsonElement request) => Texts(request, "user");
    private static string[] SystemTexts(JsonElement request) => Texts(request, "system");
    private static string[] Texts(JsonElement request, string role) => request.GetProperty("thread").EnumerateArray().Where(item => item.GetProperty("role").GetString() == role)
        .SelectMany(item => item.GetProperty("blocks").EnumerateArray()).Where(block => block.GetProperty("t").GetString() == "text").Select(block => block.GetProperty("v").GetString()!).ToArray();
    private static string DecodeJsonStrings(string json) { using var doc = JsonDocument.Parse(json); return string.Join("\n", Flatten(doc.RootElement)); }
    private static IEnumerable<string> Flatten(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Flatten),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(item => Flatten(item.Value)),
        _ => []
    };
    private static async Task CloseAsync(AgentSession session, Exception? primaryFailure)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await session.CancelAsync(cleanup.Token); await session.CloseAsync(cleanup.Token); }
        catch (Exception error)
        {
            if (primaryFailure is null) throw;
            throw new AggregateException("The scenario and its independent cleanup both failed.", primaryFailure, error);
        }
    }
    private static async Task<JsonElement> CommandAsync(object command, CancellationToken ct)
    {
        var root = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "controls-host");
        var id = "command-" + Guid.NewGuid().ToString("N");
        var value = Json(command); var fields = value.EnumerateObject().ToDictionary(item => item.Name, item => (object)item.Value.Clone()); fields["id"] = id;
        var temporary = Path.Combine(root, "host-commands", id + ".tmp"); var ready = Path.Combine(root, "host-commands", id + ".json");
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(fields), ct);
        File.Move(temporary, ready);
        var response = Path.Combine(root, "host-responses", id + ".json"); var failed = Path.Combine(root, "host-failure.json");
        while (!File.Exists(response))
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(failed)) throw new InvalidOperationException("Real Serve controls fixture failed: " + await File.ReadAllTextAsync(failed, ct));
            await Task.Delay(20, ct);
        }
        using var body = JsonDocument.Parse(await File.ReadAllBytesAsync(response, ct)); Assert.Equal(id, body.RootElement.GetProperty("id").GetString());
        return body.RootElement.GetProperty("value").Clone();
    }

    private sealed class Observation : IDisposable
    {
        private readonly CancellationTokenSource stop;
        private readonly Task pump;
        private readonly TaskCompletionSource<AgentEvent> terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Observation(AgentSession session, long lastSequence, ConcurrentQueue<AgentEvent> events, CancellationToken ct)
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pump = Observe();
            async Task Observe()
            {
                try
                {
                    await session.ObserveAsync((item, _) =>
                    {
                        events.Enqueue(item);
                        if (item.Name is "turn.completed" or "turn.aborted" or "turn.error") terminal.TrySetResult(item);
                        return Task.CompletedTask;
                    }, new EventStreamOptions { LastEventId = lastSequence.ToString(CultureInfo.InvariantCulture), Reconnect = false }, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { terminal.TrySetException(error); throw; }
            }
        }
        public Task<AgentEvent> Terminal => terminal.Task;
        public async Task StopAsync() { await stop.CancelAsync(); await pump; }
        public void Dispose() { stop.Cancel(); _ = pump.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
    }

    private sealed class AcceptedInputLossHandler : DelegatingHandler
    {
        public AcceptedInputLossHandler() : base(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None }) { }
        private int lost;
        public int LostCount => Volatile.Read(ref lost);
        public JsonElement? Accepted { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/inputs", StringComparison.Ordinal) && response.StatusCode == HttpStatusCode.Accepted && Interlocked.CompareExchange(ref lost, 1, 0) == 0)
            {
                using (response)
                {
                    var body = await response.Content.ReadAsByteArrayAsync(ct); Assert.InRange(body.Length, 1, 65536); Accepted = WireJson.Parse(body);
                }
                throw new HttpRequestException("Synthetic loss after the original input was accepted.");
            }
            return response;
        }
    }
}
