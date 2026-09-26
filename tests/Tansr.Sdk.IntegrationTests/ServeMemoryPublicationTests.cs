using System.Collections.Concurrent;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>Original Serve memory lifecycle and execution wire with a real Windows publication store.
/// The synthetic model writes a specific fact through the real extraction tools; no test callback
/// populates the memory files or manufactures publication/control receipts.</summary>
public sealed class ServeMemoryPublicationTests(ITestOutputHelper output)
{
    private const string Fact = "NET_MEMORY_FACT_8271: the synthetic user prefers violet report headings.";
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required; use the Serve integration runner.");
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Scope => Json(new { applicationScopeId = "net-integration-app", endUserId = "net-integration-user", authorizationRevision = "1" });

    [Fact]
    [Trait("Category", "ServeMemoryPublication")]
    public async Task RealExtractionPublishesToWindowsAndRecallForgetRevocationAndOriginalReceiptRecoveryStayScoped()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(110)); var ct = deadline.Token;
        var origin = new Uri(Required("TANSR_SERVE_MEMORY_URL")); Assert.Equal("127.0.0.1", origin.Host);
        var directory = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "memory-publication");
        var deviceDirectory = Path.Combine(directory, "dotnet-device"); Directory.CreateDirectory(deviceDirectory);
        var diagnosticsGate = new object(); var started = System.Diagnostics.Stopwatch.StartNew(); var diagnosticCount = 0;
        void Diagnostic(string stage, object? details = null)
        {
            lock (diagnosticsGate)
            {
                Assert.True(++diagnosticCount <= 4096, "Synthetic diagnostic record limit exceeded.");
                File.AppendAllText(Path.Combine(directory, "dotnet-stages.jsonl"), JsonSerializer.Serialize(new
                { at = DateTimeOffset.UtcNow, elapsedMs = started.ElapsedMilliseconds, stage, details }) + "\n");
            }
        }
        Diagnostic("fixture.start");
        var work = Path.Combine(deviceDirectory, "workspace"); Directory.CreateDirectory(work);
        var identity = await CommandAsync(new { id = "memory-identity", action = "identity" }, ct);
        var path = Path.Combine(deviceDirectory, "memory.sqlite");
        SqliteMemoryPublicationOptions StoreOptions(StorageOpenMode mode) => new()
        {
            EnablePreview = true,
            Path = path,
            Mode = mode,
            Identity = identity,
            ReadContext = () => Scope,
            MaxTransfers = 1024,
            MaxPages = 32768
        };
        using var publication = await SqliteMemoryPublicationStore.OpenAsync(StoreOptions(StorageOpenMode.Create), ct);
        using var lostCommand = new LostResponseHandler("/memory/commands");
        using var http = new HttpClient(lostCommand);
        using var client = new TansrClient(new TansrClientOptions
        {
            BaseUri = origin,
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(Required("TANSR_SERVE_TEST_TOKEN")),
            PrincipalProvider = () => "net-integration-app/net-integration-user",
            ExecutionScopeProvider = () => Scope,
            RequestTimeout = TimeSpan.FromSeconds(45),
            StreamIdleTimeout = TimeSpan.FromSeconds(30),
            MaxReconnectAttempts = 0
        }, http);
        var control = new TerminalSessionControl(client, enablePreview: true);
        var session = await client.CreateSessionAsync(new CreateSessionOptions { Tools = ["SearchMemory"] }, ct);
        Diagnostic("session.created", new { sessionId = session.Id });
        var approvals = new HashSet<string>(StringComparer.Ordinal);
        async Task ObserveAsync(AgentEvent item, ConcurrentQueue<AgentEvent>? events, CancellationToken token)
        {
            events?.Enqueue(item); Diagnostic("session.event", new { item.Name, item.Id });
            if (item.Name != "server.permission.request") return;
            var request = item.Data.GetProperty("payload");
            Assert.Equal(session.Id, item.Data.GetProperty("sessionId").GetString());
            Assert.Equal("SearchMemory", request.GetProperty("name").GetString());
            Assert.Equal("ask", request.GetProperty("attribution").GetProperty("decision").GetString());
            Assert.True(request.GetProperty("expiresAt").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var requestId = request.GetProperty("requestId").GetString()!; var digest = request.GetProperty("digest").GetString()!;
            Assert.False(string.IsNullOrEmpty(requestId)); Assert.False(string.IsNullOrEmpty(digest)); Assert.True(approvals.Add(requestId));
            Diagnostic("permission.allow", new { name = "SearchMemory", requestId, digest });
            await session.PermissionAsync(requestId, digest, true, token);
            Diagnostic("permission.accepted", new { requestId });
        }
        using var workspace = new WindowsWorkspace(work);
        using var journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
        {
            // This is full-lifecycle acceptance, not the separate capacity-refusal case. The original
            // journal permanently reserves 262144 bytes per receipt; explicitly plan the whole chain.
            Path = Path.Combine(deviceDirectory, "execution.sqlite"),
            Mode = StorageOpenMode.Create,
            ExecutorId = "net-memory-pc",
            ApplicationScopeId = "net-integration-app",
            EndUserId = "net-integration-user",
            ReadContext = () => Scope,
            MaxOperations = 1024,
            MaxStoredBytes = 512L * 1024 * 1024,
            MaxPages = 131072
        }, ct);
        Diagnostic("execution.journal.capacity", new
        {
            maxOperations = 1024,
            maxStoredBytes = 512L * 1024 * 1024,
            maxPages = 131072,
            reason = "Fixed full-lifecycle receipt reservation; product defaults and capacity rejection tests unchanged."
        });
        var memoryHost = new WindowsMemoryPublicationHost(publication, enablePreview: true);
        var backend = new WindowsExecutorBackend("net-memory-pc", [new WindowsExecutorWorkspace("work", "1", workspace)], [memoryHost.CreateTool()]);
        var operations = new ConcurrentQueue<JsonElement>();
        using var host = new DeviceSessionHost(new ExecutionClient(client), backend, journal,
            new DeviceSessionOptions { SessionId = session.Id, WorkspaceId = "work", RequestedTools = ["SearchMemory"] }, (operation, _) =>
            {
                Assert.Equal("MemoryPublication", operation.GetProperty("toolName").GetString());
                Assert.Equal("tool.invoke", operation.GetProperty("request").GetProperty("operation").GetString());
                Assert.Equal("TansrTerminalMemoryPublication", operation.GetProperty("request").GetProperty("args").GetProperty("name").GetString());
                Diagnostic("execution.authorized", new
                {
                    operationId = operation.GetProperty("operationId").GetString(),
                    action = PublicationRequest(operation).GetProperty("action").GetString()
                });
                operations.Enqueue(operation.Clone()); return Task.CompletedTask;
            });
        JsonElement? savedCommit = null; string? savedOwner = null; JsonElement? pinReceipt = null;
        var sessionClosed = false; Exception? primaryError = null;
        try
        {
            Assert.Empty(operations); await host.StartAsync(ct); Assert.Empty(operations);
            Diagnostic("device.ready");
            var initial = await control.ReadMemoryAsync(session.Id, ct);
            Diagnostic("memory.initial-read");
            Assert.Equal("client-managed", initial.GetProperty("memory").GetProperty("identity").GetProperty("kind").GetString());
            await CommandAsync(new { id = "initial-idle", action = "idle" }, ct);
            var first = await session.SendAndObserveAsync("NET_MEMORY_SEED. This is a durable synthetic user preference: " + Fact,
                new SessionRunOptions { Timeout = TimeSpan.FromSeconds(40) }, (item, token) => ObserveAsync(item, null, token), ct);
            Assert.False(first.WasAborted);
            Diagnostic("seed.completed");
            var afterExtraction = await CommandAsync(new { id = "after-extraction", action = "inspect" }, ct);
            Assert.True(afterExtraction.GetProperty("initialExtractionDone").GetBoolean());
            Assert.Contains(Fact, afterExtraction.GetProperty("files").GetProperty("net-preference.md").GetString());
            Assert.Contains("net-preference.md", afterExtraction.GetProperty("files").GetProperty("MEMORY.md").GetString());
            Assert.Contains(operations, operation => PublicationRequest(operation).GetProperty("action").GetString() == "commit");
            Diagnostic("extraction.verified");

            var recallEvents = new ConcurrentQueue<AgentEvent>();
            var recalled = await session.SendAndObserveAsync("NET_MEMORY_RECALL. Search the stored report preference and apply it.",
                new SessionRunOptions { Timeout = TimeSpan.FromSeconds(40) }, (item, token) => ObserveAsync(item, recallEvents, token), ct);
            Assert.False(recalled.WasAborted);
            Assert.Contains(recallEvents, item => item.Data.GetRawText().Contains("NET_MEMORY_ADOPTED: violet report headings.", StringComparison.Ordinal));
            var afterRecall = await CommandAsync(new { id = "after-recall", action = "inspect" }, ct);
            Assert.True(afterRecall.GetProperty("recallAdopted").GetBoolean());
            Diagnostic("recall.verified");

            var state = await control.ReadMemoryAsync(session.Id, ct);
            var pin = control.CreateMemoryOperation(session.Id, state, "pin-request", "pin-operation", Json(new { kind = "pin", text = "NET_PIN_449: review synthetic preferences." }));
            var lost = await Assert.ThrowsAsync<TansrProtocolException>(() => control.SubmitMemoryAsync(pin, ct));
            Assert.Equal("network_error", lost.Code); Assert.Equal(1, lostCommand.LostResponseCount);
            pinReceipt = (await control.QueryMemoryAsync(pin, ct)).GetProperty("receipt").Clone();
            Assert.Equal("committed", pinReceipt.Value.GetProperty("status").GetString());
            Assert.True(pinReceipt.Value.GetProperty("durable").GetBoolean()); Assert.False(pinReceipt.Value.GetProperty("consumed").GetBoolean());
            Assert.Equal(WireJson.CanonicalString(lostCommand.LostReceipt!.Value.GetProperty("receipt")), WireJson.CanonicalString(pinReceipt.Value));
            Assert.Single(lostCommand.Requests, request => request.Method == "POST" && request.Path.EndsWith("/memory/commands", StringComparison.Ordinal));
            var afterPin = await CommandAsync(new { id = "after-pin", action = "inspect" }, ct);
            Assert.Contains("NET_PIN_449", afterPin.GetProperty("files").GetProperty("pending-anchors.md").GetString());

            state = await control.ReadMemoryAsync(session.Id, ct);
            var forget = control.CreateMemoryOperation(session.Id, state, "forget-request", "forget-operation", Json(new { kind = "forget", topic = "net-preference.md" }));
            var forgotten = await control.SubmitMemoryAsync(forget, ct);
            Assert.Equal("committed", forgotten.GetProperty("receipt").GetProperty("status").GetString());
            var queried = await control.QueryMemoryAsync(forget, ct);
            Assert.Equal(WireJson.CanonicalString(forgotten), WireJson.CanonicalString(queried));
            var afterForget = await CommandAsync(new { id = "after-forget", action = "inspect" }, ct);
            Assert.Equal(JsonValueKind.Null, afterForget.GetProperty("files").GetProperty("net-preference.md").ValueKind);
            Assert.Equal(JsonValueKind.Null, afterForget.GetProperty("files").GetProperty("MEMORY.md").ValueKind);
            Assert.Equal("1", afterForget.GetProperty("memory").GetProperty("deletionGeneration").GetString());
            await CommandAsync(new { id = "confirm-deletion-floor", action = "deletion-floor", generation = "1" }, ct);
            var absentEvents = new ConcurrentQueue<AgentEvent>();
            await session.SendAndObserveAsync("NET_MEMORY_AFTER_DELETE. Search for the deleted report preference without reconstructing it from the conversation.",
                new SessionRunOptions { Timeout = TimeSpan.FromSeconds(40) }, (item, token) => ObserveAsync(item, absentEvents, token), ct);
            Assert.Contains(absentEvents, item => item.Data.GetRawText().Contains("NET_MEMORY_ABSENT_CONFIRMED", StringComparison.Ordinal));
            var absence = await CommandAsync(new { id = "after-delete-search", action = "inspect" }, ct);
            Assert.True(absence.GetProperty("forgottenAbsent").GetBoolean());
            Assert.Equal(JsonValueKind.Null, absence.GetProperty("files").GetProperty("net-preference.md").ValueKind);
            Assert.Equal(2, approvals.Count); Diagnostic("forget.verified");

            var beforeRevoke = operations.Count;
            await CommandAsync(new { id = "revoke-memory", action = "enabled", enabled = false }, ct);
            await Assert.ThrowsAsync<TansrHttpException>(() => control.ReadMemoryAsync(session.Id, ct));
            Assert.Equal(beforeRevoke, operations.Count);
            await CommandAsync(new { id = "restore-memory", action = "enabled", enabled = true }, ct);

            await CommandAsync(new { id = "close-memory-session", action = "close-session" }, ct);
            sessionClosed = true;
            var committed = operations.Last(operation => PublicationRequest(operation).GetProperty("action").GetString() == "commit");
            savedCommit = PublicationRequest(committed); savedOwner = Owner(committed);
        }
        catch (Exception error) { primaryError = error; Diagnostic("test.failed", new { type = error.GetType().Name, code = (error as TansrProtocolException)?.Code }); throw; }
        finally
        {
            Exception? cleanupError = null;
            if (!sessionClosed)
            {
                Diagnostic("cleanup.close.begin");
                // The original execution poll remains active while Serve cancels and drains its owned resources.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await CommandAsync(new { id = "close-memory-after-failure", action = "close-session" }, cleanup.Token, allowOriginalFailure: true); }
                catch (Exception error) { cleanupError = error; Diagnostic("cleanup.close.failed", new { type = error.GetType().Name }); output.WriteLine("Original Serve cleanup failed: " + error.GetType().Name); }
            }
            try { await host.StopAsync(); }
            catch (Exception error) { cleanupError ??= error; output.WriteLine("Device host cleanup failed: " + error.GetType().Name); }
            Diagnostic("execution.journal.consumption", new
            {
                authorizedOperations = operations.Select(item => item.GetProperty("operationId").GetString()).Distinct(StringComparer.Ordinal).Count(),
                physicalBytes = new[] { "execution.sqlite", "execution.sqlite-wal", "execution.sqlite-shm" }
                    .Select(name => new FileInfo(Path.Combine(deviceDirectory, name))).Where(file => file.Exists).Sum(file => file.Length)
            });
            if (primaryError is null && cleanupError is not null) throw cleanupError;
            Diagnostic("cleanup.device.stopped");
        }

        await publication.CloseAsync(ct);
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(StoreOptions(StorageOpenMode.Reopen), ct);
        Assert.NotNull(savedCommit); Assert.NotNull(savedOwner); Assert.NotNull(pinReceipt);
        var head = await reopened.ExecuteAsync(PublicationControl(identity, "head"), savedOwner!, ct);
        Assert.Equal(JsonValueKind.Object, head.GetProperty("publication").ValueKind);
        var query = await reopened.ExecuteAsync(PublicationControl(identity, "query", savedCommit!.Value.GetProperty("transferId").GetString()), savedOwner!, ct);
        Assert.Equal("committed", query.GetProperty("transfer").GetProperty("status").GetString());
        Assert.Equal(savedCommit.Value.GetProperty("transferId").GetString(), query.GetProperty("transfer").GetProperty("transferId").GetString());
        Assert.Empty(Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories));
        output.WriteLine("Real Serve extraction -> original MemoryPublication execution profile -> Windows SQLite -> later SearchMemory model adoption; original pin HTTP response loss queried without replay; forget removes topic/index and increments deletion generation; revocation dispatches nothing; original committed transfer survives same-owner store reopen. No paid model calls or local Shell.");
    }

    private static JsonElement PublicationRequest(JsonElement operation)
    {
        using var document = JsonDocument.Parse(operation.GetProperty("request").GetProperty("args").GetProperty("argsJson").GetString()!);
        return document.RootElement.Clone();
    }
    private static string Owner(JsonElement operation) => WireJson.CanonicalString(Json(new
    { scope = operation.GetProperty("scope"), sessionId = operation.GetProperty("sessionId"), binding = operation.GetProperty("binding") }));
    private static JsonElement PublicationControl(JsonElement identity, string action, string? transferId = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("contract", "terminal-services-v1"); writer.WriteString("action", action);
            foreach (var name in new[] { "sourceId", "sourceGeneration", "domainKey" }) { writer.WritePropertyName(name); identity.GetProperty(name).WriteTo(writer); }
            if (transferId is not null) writer.WriteString("transferId", transferId);
            writer.WriteEndObject(); writer.Flush();
        }
        return WireJson.Parse(buffer.ToArray());
    }
    private static async Task<JsonElement> CommandAsync(object command, CancellationToken cancellationToken, bool allowOriginalFailure = false)
    {
        var directory = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "memory-publication"); var value = Json(command); var id = value.GetProperty("id").GetString();
        Assert.Matches("^[a-z0-9-]{1,64}$", id!);
        if (allowOriginalFailure) Assert.Equal("close-session", value.GetProperty("action").GetString());
        var temporary = Path.Combine(directory, "host-commands", id + ".tmp");
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            await file.WriteAsync(WireJson.EncodeControl(value), cancellationToken);
        File.Move(temporary, Path.Combine(directory, "host-commands", id + ".json"));
        for (; ; )
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = Path.Combine(directory, "host-responses", id + ".json");
            var failurePath = Path.Combine(directory, "host-failure.json");
            if (!allowOriginalFailure && File.Exists(failurePath))
            {
                Assert.InRange(new FileInfo(failurePath).Length, 1, 16384);
                using var failed = JsonDocument.Parse(await File.ReadAllBytesAsync(failurePath, cancellationToken));
                throw FixtureFailure(failed.RootElement);
            }
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
                Assert.Equal(id, document.RootElement.GetProperty("id").GetString());
                if (document.RootElement.TryGetProperty("failure", out var failed)) throw FixtureFailure(failed);
                return document.RootElement.GetProperty("value").Clone();
            }
            await Task.Delay(20, cancellationToken);
        }
    }

    private static InvalidOperationException FixtureFailure(JsonElement failure) => new(
        "Synthetic Serve fixture failed at " + failure.GetProperty("stage").GetString() + " [" +
        failure.GetProperty("type").GetString() + "]: " + failure.GetProperty("message").GetString());
}
