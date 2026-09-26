using System.Net;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>The complete original checkpoint/context/audio route surface against actual
/// Serve storage and kernel. HTTP acceptance is deliberately not called resource drain.</summary>
public sealed partial class ServeSessionApiTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jMZkAAAAASUVORK5CYII=";
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required; use scripts/serve-integration.mjs.");
    private static TansrClientOptions Options(bool other = false) => new()
    {
        BaseUri = new(Required("TANSR_SERVE_SESSION_API_URL")),
        AllowInsecureLoopback = true,
        TokenProvider = _ => Task.FromResult(Required(other ? "TANSR_SERVE_TEST_OTHER_TOKEN" : "TANSR_SERVE_TEST_TOKEN")),
        PrincipalProvider = () => "net-integration-app/" + (other ? "net-integration-other" : "net-integration-user"),
        ExecutionScopeProvider = () => JsonSerializer.SerializeToElement(new { applicationScopeId = "net-integration-app", endUserId = other ? "net-integration-other" : "net-integration-user", authorizationRevision = "1" }),
        RequestTimeout = TimeSpan.FromSeconds(10),
        StreamIdleTimeout = TimeSpan.FromSeconds(15),
        MaxReconnectAttempts = 0
    };

    [Theory]
    [InlineData("delayed")]
    [InlineData("failed")]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task PublicResourceObservationNeverEquatesCloseAcceptanceWithHostCleanup(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = deadline.Token;
        using var client = new TansrClient(Options()); using var observation = new TerminalObservationClient(Options(), true);
        var session = await client.CreateSessionAsync(new() { Tools = [], Labels = new Dictionary<string, string> { ["resource"] = mode } }, ct); Exception? primary = null;
        try
        {
            var original = await observation.ReadResourcesAsync(session.Id, cancellationToken: ct); Assert.Equal(TerminalResourceState.Active, original.State); Assert.False(original.CloseRequested);
            Assert.True((await session.CloseAsync(ct)).GetProperty("accepted").GetBoolean());
            if (mode == "delayed")
            {
                await Assert.ThrowsAsync<TimeoutException>(() => observation.WaitForResourcesAsync(session.Id, TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromMilliseconds(10), cancellationToken: ct));
                // Close acceptance precedes the registry's real event publication/owner IO.
                // Wait for that phase boundary, rather than treating a fixed 100ms as evidence.
                using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); phaseDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var pending = await observation.ReadResourcesAsync(session.Id, cancellationToken: phaseDeadline.Token);
                while (pending.State == TerminalResourceState.Accepted)
                {
                    await Task.Delay(10, phaseDeadline.Token);
                    pending = await observation.ReadResourcesAsync(session.Id, cancellationToken: phaseDeadline.Token);
                }
                Assert.Equal(TerminalResourceState.Draining, pending.State); Assert.False(pending.Completed); Assert.True(pending.ExecutionEnded);
                await CommandAsync(new { action = "release-resource", sessionId = session.Id }, ct);
                var completed = await observation.WaitForResourcesAsync(session.Id, TimeSpan.FromSeconds(5), cancellationToken: ct);
                Assert.True(completed.Completed); Assert.Equal(original.EpochStartSequence, completed.EpochStartSequence);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<TerminalResourceSettlementException>(() => observation.WaitForResourcesAsync(session.Id, TimeSpan.FromSeconds(5), cancellationToken: ct));
                Assert.Equal(TerminalResourceState.Failed, failure.Observation.State); Assert.Equal("settlement_failed", failure.Observation.ErrorCode); Assert.False(failure.Observation.Completed);
                Assert.DoesNotContain("NET_API_RESOURCE_FAILURE", failure.ToString());
            }
            using var other = new TerminalObservationClient(Options(true), true);
            Assert.Equal("not_found", (await Assert.ThrowsAsync<TansrHttpException>(() => other.ReadResourcesAsync(session.Id, cancellationToken: ct))).Code);
            Assert.Equal("unsupported_capability", (await Assert.ThrowsAsync<TansrHttpException>(() => observation.ReadOutputCorrelationsAsync(session.Id, cancellationToken: ct))).Code);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await CleanupAsync([session], primary); }
    }

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task CheckpointsCompactRestoreExportImportForkAndDeletePreserveTheOriginalSessionAndImages()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = deadline.Token;
        using var capturedExports = new ExportCaptureHandler(); using var sessionHttp = new HttpClient(capturedExports);
        using var client = new TansrClient(Options(), sessionHttp);
        using var restartedClient = new TansrClient(Options());
        var session = await client.CreateSessionAsync(new() { Tools = [], MaxTokens = 100000 }, ct);
        var forked = new List<AgentSession>(); Exception? primary = null;
        try
        {
            var empty = await session.CompactAsync(new() { Checkpoint = false }, ct);
            Assert.Equal("rejected", empty.GetProperty("status").GetString()); Assert.Equal("empty_history", empty.GetProperty("reason").GetString());
            var filler = string.Concat(Enumerable.Repeat("NET synthetic archived detail 中文 ", 100));
            AssertUsage(await session.SendAndObserveAsync([MessageBlock.Text("NET-API image " + filler), MessageBlock.Image("image/png", Png)], cancellationToken: ct));
            for (var i = 0; i < 3; i++) AssertUsage(await session.SendAndObserveAsync("NET-API round " + i + filler, cancellationToken: ct));
            await CommandAsync(new { action = "settle", sessionId = session.Id }, ct);
            Assert.Equal(620, (await CommandAsync(new { action = "usage", sessionId = session.Id }, ct)).GetProperty("totals").GetProperty("totalTokens").GetInt64());
            var before = await session.GetHistoryAsync(ct); var beforeMessages = before.GetProperty("messages");
            var checkpoint = await session.CheckpointAsync("original 中文", ct); var checkpointId = checkpoint.GetProperty("checkpointId").GetString()!;
            Assert.Equal(session.Id, checkpoint.GetProperty("sessionId").GetString()); Assert.Equal(beforeMessages.GetArrayLength(), checkpoint.GetProperty("messageCount").GetInt32());
            Assert.Contains((await session.ListCheckpointsAsync(ct)).GetProperty("checkpoints").EnumerateArray(), item => item.GetProperty("checkpointId").GetString() == checkpointId);
            var exported = await session.ExportCheckpointAsync(checkpointId, ct);
            Assert.Contains(Png, System.Text.Encoding.UTF8.GetString(exported));
            await VerifyLocalSnapshotToggleAsync(session, capturedExports, ct);
            var compacted = await session.CompactAsync(new() { Instructions = "Keep NET synthetic decisions and identifiers.", CheckpointLabel = "before compact" }, ct);
            Assert.Equal("compacted", compacted.GetProperty("status").GetString()); Assert.NotEmpty(compacted.GetProperty("compactionId").GetString()!);
            var compactedUsage = await CommandAsync(new { action = "usage", sessionId = session.Id }, ct);
            Assert.Equal(JsonValueKind.Object, compactedUsage.GetProperty("totals").ValueKind);
            Assert.Equal(645, compactedUsage.GetProperty("totals").GetProperty("totalTokens").GetInt64());
            Assert.True((await session.GetHistoryAsync(ct)).GetProperty("messages").GetArrayLength() < beforeMessages.GetArrayLength());
            var restore = await session.RestoreCheckpointAsync(checkpointId, false, ct);
            Assert.Equal("restored", restore.GetProperty("status").GetString()); Assert.Equal(checkpointId, restore.GetProperty("checkpointId").GetString());
            Assert.Equal(WireJson.CanonicalString(beforeMessages), WireJson.CanonicalString((await session.GetHistoryAsync(ct)).GetProperty("messages")));
            Assert.Equal(WireJson.CanonicalString(compactedUsage), WireJson.CanonicalString(await CommandAsync(new { action = "usage", sessionId = session.Id }, ct)));
            var imported = await session.ImportAsync(exported, "imported 中文", ct); var importedId = imported.GetProperty("checkpointId").GetString()!;
            Assert.NotEqual(checkpointId, importedId); Assert.Equal(session.Id, imported.GetProperty("sessionId").GetString());
            var fork = await session.ForkAsync(importedId, new() { Tools = [], MaxTokens = 100000 }, ct); forked.Add(fork); Assert.NotEqual(session.Id, fork.Id);
            Assert.Equal(WireJson.CanonicalString(beforeMessages), WireJson.CanonicalString((await fork.GetHistoryAsync(ct)).GetProperty("messages")));
            // The original contract forks messages into a new identity; only resume carries the
            // source usage snapshot. A branch's new spend must never alter its parent ledger.
            Assert.Equal(JsonValueKind.Null, (await CommandAsync(new { action = "usage", sessionId = fork.Id }, ct)).GetProperty("snapshot").ValueKind);
            var originalMetadata = await session.GetMetadataAsync(ct);
            AssertUsage(await fork.SendAndObserveAsync("NET-API branch-only usage", cancellationToken: ct));
            Assert.Equal(155, (await CommandAsync(new { action = "usage", sessionId = fork.Id }, ct)).GetProperty("totals").GetProperty("totalTokens").GetInt64());
            Assert.Equal(WireJson.CanonicalString(compactedUsage), WireJson.CanonicalString(await CommandAsync(new { action = "usage", sessionId = session.Id }, ct)));
            Assert.Equal(originalMetadata.GetProperty("lastSeq").GetInt64(), (await session.GetMetadataAsync(ct)).GetProperty("lastSeq").GetInt64());
            var listings = await client.GetSessionsAsync(cancellationToken: ct);
            Assert.Contains(listings.Sessions, item => item.Id == session.Id); Assert.Contains(listings.Sessions, item => item.Id == fork.Id);
            using var other = new TansrClient(Options(true));
            Assert.Equal(403, (await Assert.ThrowsAsync<TansrHttpException>(() => other.GetSessionAsync(session.Id, ct))).StatusCode);
            Assert.DoesNotContain((await other.GetSessionsAsync(cancellationToken: ct)).Sessions, item => item.Id == session.Id || item.Id == fork.Id);
            Assert.Equal(JsonValueKind.Null, (await session.DeleteCheckpointAsync(importedId, ct)).ValueKind);
            Assert.Equal("checkpoint_not_found", (await Assert.ThrowsAsync<TansrHttpException>(() => session.DeleteCheckpointAsync(importedId, ct))).Code);
            Assert.Equal("checkpoint_not_found", (await Assert.ThrowsAsync<TansrHttpException>(() => session.RestoreCheckpointAsync(importedId, false, ct))).Code);
            Assert.Equal("checkpoint_import_invalid", (await Assert.ThrowsAsync<TansrHttpException>(() => session.ImportAsync([1, 2, 3], cancellationToken: ct))).Code);
            Assert.Equal(WireJson.CanonicalString(beforeMessages), WireJson.CanonicalString((await session.GetHistoryAsync(ct)).GetProperty("messages")));
            var paths = await CommandAsync(new { action = "inspect" }, ct);
            var changed = await session.SetCwdAsync(paths.GetProperty("alternate").GetString()!, ct);
            Assert.Equal(paths.GetProperty("cwd").GetString(), changed.GetProperty("from").GetString());
            Assert.Equal(paths.GetProperty("alternate").GetString(), changed.GetProperty("cwd").GetString());
            var same = await session.SetCwdAsync(paths.GetProperty("alternate").GetString()!, ct); Assert.Equal(same.GetProperty("cwd").GetString(), same.GetProperty("from").GetString());
            var invalidCwd = await Assert.ThrowsAsync<TansrHttpException>(() => session.SetCwdAsync("relative-not-allowed", ct)); Assert.Equal(400, invalidCwd.StatusCode);
            var close = await session.CloseAsync(ct); Assert.True(close.GetProperty("accepted").GetBoolean()); Assert.Equal(session.Id, close.GetProperty("sessionId").GetString());
            // This is trusted-host evidence, not a fabricated client-side drain success.
            Assert.True((await CommandAsync(new { action = "settle", sessionId = session.Id }, ct)).GetProperty("settled").GetBoolean());
            Assert.True((await session.CloseAsync(ct)).GetProperty("accepted").GetBoolean());
            // A fresh .NET client and a fresh session driver from the original persistent
            // factory. This is not claimed as a process restart of the Serve host.
            var limited = await restartedClient.ResumeSessionAsync(session.Id, new() { Tools = [], MaxTokens = 645 }, ct); forked.Add(limited); Assert.True(limited.Resumed);
            Assert.Equal(WireJson.CanonicalString(beforeMessages), WireJson.CanonicalString((await limited.GetHistoryAsync(ct)).GetProperty("messages")));
            var callsBeforeBudget = (await CommandAsync(new { action = "inspect" }, ct)).GetProperty("exchanges").GetInt32();
            var rejected = await limited.SendAndObserveAsync("NET-API persisted paid usage must prevent another model call", cancellationToken: ct);
            Assert.True(rejected.WasAborted); Assert.Equal("budget_exceeded", rejected.Reason);
            await CommandAsync(new { action = "settle", sessionId = limited.Id }, ct);
            var budgetEvidence = await CommandAsync(new { action = "inspect" }, ct);
            Assert.Equal(callsBeforeBudget, budgetEvidence.GetProperty("exchanges").GetInt32());
            Assert.Equal(2, budgetEvidence.GetProperty("creations").EnumerateArray().Count(item => item.GetProperty("sessionId").GetString() == session.Id));
            Assert.Equal(645, (await CommandAsync(new { action = "usage", sessionId = limited.Id }, ct)).GetProperty("totals").GetProperty("totalTokens").GetInt64());
            var afterRejected = (await limited.GetHistoryAsync(ct)).GetProperty("messages");
            await limited.CloseAsync(ct); await CommandAsync(new { action = "settle", sessionId = limited.Id }, ct);
            var resumed = await restartedClient.ResumeSessionAsync(session.Id, new() { Tools = [], MaxTokens = 100000 }, ct); forked.Add(resumed); Assert.True(resumed.Resumed);
            Assert.Equal(WireJson.CanonicalString(afterRejected), WireJson.CanonicalString((await resumed.GetHistoryAsync(ct)).GetProperty("messages")));
            AssertUsage(await resumed.SendAndObserveAsync("NET-API resume retains the original spend", cancellationToken: ct));
            Assert.Equal(800,
                (await CommandAsync(new { action = "usage", sessionId = resumed.Id }, ct)).GetProperty("totals").GetProperty("totalTokens").GetInt64());
            Assert.Equal(155, (await CommandAsync(new { action = "usage", sessionId = fork.Id }, ct)).GetProperty("totals").GetProperty("totalTokens").GetInt64());
        }
        catch (Exception error) { primary = error; throw; }
        finally { await CleanupAsync([session, .. forked], primary); }
    }

    private static void AssertUsage(SessionRunResult result)
    { Assert.False(result.WasAborted); Assert.Equal(1, result.Snapshot.UsageRequests); Assert.Equal(155, result.Snapshot.TurnTokens); Assert.Equal(155, result.Snapshot.SessionTokens); }

    private static async Task VerifyLocalSnapshotToggleAsync(AgentSession session, ExportCaptureHandler captures, CancellationToken ct)
    {
        var directory = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "dotnet-session-snapshot"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "context.sqlite"); var keyPath = Path.Combine(directory, "context.key");
        var scope = new SessionSnapshotScope(Required("TANSR_SERVE_SESSION_API_URL"), "net-integration-app", "net-integration-user", session.Id);
        CurrentUserDpapiArchiveKeyProvider? key = null;
        using var store = new WindowsSessionSnapshotStore(new()
        {
            Path = path,
            Scope = scope,
            ReadScope = () => scope,
            KeyProvider = () => key ??= CurrentUserDpapiArchiveKeyProvider.Create(keyPath, "a08-snapshot-key")
        });
        using var mirror = await SessionSnapshotPersistence.CreateAsync(session, store, () => scope, ct);
        var history = await session.GetHistoryAsync(ct); var metadata = await session.ReadMetadataAsync(ct);
        var usage = await CommandAsync(new { action = "usage", sessionId = session.Id }, ct);
        var checkpoints = await session.ListCheckpointsAsync(ct); var originalId = session.Id; var exports = captures.Exports;
        Assert.False(mirror.IsEnabled); Assert.False(File.Exists(path)); Assert.False(File.Exists(keyPath));
        await mirror.FlushAsync(ct); Assert.Equal(exports, captures.Exports); Assert.False(File.Exists(path));

        async Task AssertUnchangedAsync()
        {
            Assert.Same(session, mirror.Session); Assert.Equal(originalId, mirror.Session.Id);
            Assert.Equal(metadata.LastSequence, (await session.ReadMetadataAsync(ct)).LastSequence);
            Assert.Equal(WireJson.CanonicalString(history), WireJson.CanonicalString(await session.GetHistoryAsync(ct)));
            Assert.Equal(WireJson.CanonicalString(usage), WireJson.CanonicalString(await CommandAsync(new { action = "usage", sessionId = session.Id }, ct)));
            Assert.Equal(WireJson.CanonicalString(checkpoints), WireJson.CanonicalString(await session.ListCheckpointsAsync(ct)));
            Assert.Null(mirror.PendingCheckpointId); Assert.Null(mirror.LastCheckpointCleanupErrorCode);
        }
        async Task AssertExactCopyAsync()
        {
            var copy = await mirror.ReadAsync(ct); Assert.NotNull(copy);
            Assert.Equal(captures.LastExport, copy.Bytes); Assert.Equal(copy.Bytes, (await store.ReadAsync(ct)).Snapshot!.Bytes);
            Assert.Contains(Png, System.Text.Encoding.UTF8.GetString(copy.Bytes));
            Assert.DoesNotContain(Png, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct)));
            await AssertUnchangedAsync();
        }

        await mirror.EnableAsync(ct); Assert.True(mirror.IsEnabled); Assert.Equal(++exports, captures.Exports); await AssertExactCopyAsync();
        var enabledRevision = (await store.ReadAsync(ct)).Revision;
        await mirror.FlushAsync(ct); Assert.Equal(++exports, captures.Exports); Assert.True((await store.ReadAsync(ct)).Revision > enabledRevision); await AssertExactCopyAsync();
        await mirror.DisableAsync(ct); Assert.False(mirror.IsEnabled); Assert.Null(await mirror.ReadAsync(ct)); await AssertUnchangedAsync();
        var disabledRevision = (await store.ReadAsync(ct)).Revision;
        await mirror.FlushAsync(ct); Assert.Equal(exports, captures.Exports); Assert.Equal(disabledRevision, (await store.ReadAsync(ct)).Revision);
        await mirror.EnableAsync(ct); Assert.True(mirror.IsEnabled); Assert.Equal(++exports, captures.Exports); await AssertExactCopyAsync();
        // Reopen the same encrypted file/key independently; this is a durable real SQLite
        // copy of the original Serve export, never synthetic SessionView text or a new session.
        store.Dispose();
        using var reopened = new WindowsSessionSnapshotStore(new()
        {
            Path = path,
            Scope = scope,
            ReadScope = () => scope,
            KeyProvider = () => CurrentUserDpapiArchiveKeyProvider.Open(keyPath, "a08-snapshot-key")
        });
        Assert.Equal(captures.LastExport, (await reopened.ReadAsync(ct)).Snapshot!.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task AudioAndLostCheckpointResponsesNeverMutateHistoryOrSilentlyRetryASideEffect(bool timeout)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        var options = Options(); if (timeout) options.RequestTimeout = TimeSpan.FromSeconds(1);
        using var handler = new LostCheckpointHandler(timeout); using var http = new HttpClient(handler); using var client = new TansrClient(options, http);
        var session = await client.CreateSessionAsync(new() { Tools = [] }, ct); Exception? primary = null;
        try
        {
            var before = await session.GetHistoryAsync(ct);
            var transcript = await session.TranscribeAsync(new() { Audio = "data:audio/wav;base64,UklGRg==", Language = "zh", Diarize = false }, ct);
            Assert.Equal("NET transcribed draft 中文", transcript.GetProperty("text").GetString());
            var speech = await session.SpeakAsync(new() { Input = "NET 中文", Format = "wav", Speed = 1 }, ct);
            Assert.Equal("UklGRg==", speech.GetProperty("audio").GetProperty("b64").GetString());
            Assert.Equal("tts_quota_exceeded", (await Assert.ThrowsAsync<TansrHttpException>(() => session.SpeakAsync(new() { Input = "NET-AUDIO-QUOTA" }, ct))).Code);
            Assert.Equal(WireJson.CanonicalString(before), WireJson.CanonicalString(await session.GetHistoryAsync(ct)));
            if (timeout) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CheckpointAsync("lost-original", ct));
            else Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => session.CheckpointAsync("lost-original", ct))).Code);
            Assert.Equal(1, handler.Lost); Assert.False(ct.IsCancellationRequested);
            var listed = (await session.ListCheckpointsAsync(ct)).GetProperty("checkpoints").EnumerateArray().ToArray();
            var created = Assert.Single(listed, item => item.GetProperty("label").GetString() == "lost-original");
            Assert.Equal(session.Id, created.GetProperty("sessionId").GetString());
            Assert.Equal(1, handler.Posts); Assert.Equal(1, handler.Lost);
            Assert.True((await session.CloseAsync(ct)).GetProperty("accepted").GetBoolean());
            await CommandAsync(new { action = "settle", sessionId = session.Id }, ct);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await CleanupAsync([session], primary); }
    }

    private static async Task CleanupAsync(IEnumerable<AgentSession> sessions, Exception? primary)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var errors = new List<Exception>();
        foreach (var session in sessions) try { await session.CloseAsync(deadline.Token); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) { if (primary is not null) errors.Insert(0, primary); throw new AggregateException("Session scenario cleanup failed.", errors); }
    }
    private static async Task<JsonElement> CommandAsync(object command, CancellationToken ct)
    {
        var root = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "session-api"); var id = "command-" + Guid.NewGuid().ToString("N");
        var fields = JsonSerializer.SerializeToElement(command).EnumerateObject().ToDictionary(item => item.Name, item => (object)item.Value.Clone()); fields["id"] = id;
        var temporary = Path.Combine(root, "host-commands", id + ".tmp");
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(fields), ct); File.Move(temporary, Path.ChangeExtension(temporary, ".json"));
        var response = Path.Combine(root, "host-responses", id + ".json"); var failure = Path.Combine(root, "host-failure.json");
        while (!File.Exists(response)) { ct.ThrowIfCancellationRequested(); if (File.Exists(failure)) throw new InvalidOperationException(await File.ReadAllTextAsync(failure, ct)); await Task.Delay(20, ct); }
        using var body = JsonDocument.Parse(await File.ReadAllBytesAsync(response, ct)); Assert.Equal(id, body.RootElement.GetProperty("id").GetString()); return body.RootElement.GetProperty("value").Clone();
    }
    private sealed class ExportCaptureHandler : DelegatingHandler
    {
        internal ExportCaptureHandler() : base(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
        internal int Exports { get; private set; }
        internal byte[]? LastExport { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/export", StringComparison.Ordinal) && response.StatusCode == HttpStatusCode.OK)
            {
                LastExport = await response.Content.ReadAsByteArrayAsync(ct); Exports++;
            }
            return response;
        }
    }
    private sealed class LostCheckpointHandler : DelegatingHandler
    {
        private readonly bool timeout;
        public LostCheckpointHandler(bool timeout) : base(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { this.timeout = timeout; }
        public int Posts { get; private set; }
        public int Lost { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var checkpoint = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/checkpoints", StringComparison.Ordinal); if (checkpoint) Posts++;
            var response = await base.SendAsync(request, ct);
            if (checkpoint && response.StatusCode == HttpStatusCode.Created && Lost == 0)
            {
                Lost++; response.Dispose();
                if (timeout) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new HttpRequestException("Synthetic receipt loss after persisted checkpoint.");
            }
            return response;
        }
    }
}
