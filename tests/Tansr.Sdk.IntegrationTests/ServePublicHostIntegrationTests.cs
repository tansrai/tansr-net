using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>Public .NET composition against a public Serve archive host with real kernel/SQLite.
/// Only the upstream platform/model is controlled; no paid sampling or fabricated wire receipts.</summary>
public sealed class ServePublicHostIntegrationTests(ITestOutputHelper output)
{
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required; use scripts/serve-integration.mjs.");
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Scope => Json(new { applicationScopeId = "net-integration-app", endUserId = "net-integration-user", authorizationRevision = "1" });

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task PublicDeviceHostExecutesRealReadAndArchivesOriginalTurnToEncryptedSqlite()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        var origin = new Uri(Required("TANSR_SERVE_PUBLIC_URL")); Assert.Equal("127.0.0.1", origin.Host);
        var directory = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "dotnet-device"); Directory.CreateDirectory(directory);
        var work = Path.Combine(directory, "workspace"); Directory.CreateDirectory(work);
        const string expected = "NET_DEVICE_NOTE_7319 · 中文 café 👩🏽‍💻";
        await File.WriteAllTextAsync(Path.Combine(work, "note.txt"), expected, new UTF8Encoding(false), ct);
        using var client = new TansrClient(new TansrClientOptions
        {
            BaseUri = origin,
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(Required("TANSR_SERVE_TEST_TOKEN")),
            PrincipalProvider = () => "net-integration-app/net-integration-user",
            ExecutionScopeProvider = () => Scope,
            RequestTimeout = TimeSpan.FromSeconds(10),
            StreamIdleTimeout = TimeSpan.FromSeconds(15),
            MaxReconnectAttempts = 0
        });
        var session = await client.CreateSessionAsync(new CreateSessionOptions { Tools = ["Read"] }, ct);
        using var workspace = new WindowsWorkspace(work);
        using var journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
        {
            Path = Path.Combine(directory, "execution.sqlite"),
            Mode = StorageOpenMode.Create,
            ExecutorId = "net-pc",
            ApplicationScopeId = "net-integration-app",
            EndUserId = "net-integration-user",
            ReadContext = () => Scope
        }, ct);
        var backend = new WindowsExecutorBackend("net-pc", [new WindowsExecutorWorkspace("work", "1", workspace)]);
        var operations = new ConcurrentQueue<JsonElement>();
        using var host = new DeviceSessionHost(new ExecutionClient(client), backend, journal,
            new DeviceSessionOptions { SessionId = session.Id, WorkspaceId = "work", RequestedTools = ["Read"] }, (operation, _) =>
            {
                Assert.Equal("Read", operation.GetProperty("toolName").GetString());
                Assert.Contains(operation.GetProperty("request").GetProperty("operation").GetString(), new[] { "fs.inspect", "fs.read" });
                operations.Enqueue(operation.Clone()); return Task.CompletedTask;
            });
        try
        {
            await host.StartAsync(ct); Assert.Equal(DeviceSessionState.Ready, host.State);
            int permissions = 0; var businessEvents = new ConcurrentQueue<AgentEvent>();
            var result = await session.SendAndObserveAsync("Read the original device note once.", new SessionRunOptions { Timeout = TimeSpan.FromSeconds(30) }, async (item, token) =>
            {
                businessEvents.Enqueue(item);
                if (item.Name == "server.permission.request")
                {
                    Interlocked.Increment(ref permissions);
                    var request = item.Data.GetProperty("payload");
                    Assert.Contains("work", request.GetProperty("summary").GetString());
                    await session.PermissionAsync(request.GetProperty("requestId").GetString()!, request.GetProperty("digest").GetString()!, true, token);
                }
            }, ct);
            Assert.False(result.WasAborted); Assert.Equal("turn.completed", result.TerminalEvent.Name);
            Assert.Equal(1, permissions);
            Assert.DoesNotContain(businessEvents, item => item.Name is "tool.failed" or "turn.error" or "turn.aborted");
            Assert.Contains(businessEvents, item => item.Name == "msg.text.delta" && item.Data.GetProperty("text").GetString() == expected);
            Assert.Contains(operations, operation => operation.GetProperty("request").GetProperty("operation").GetString() == "fs.read");
            foreach (var operation in operations)
            {
                var saved = await journal.ClaimAsync(operation, ct);
                Assert.NotNull(saved.Receipt); Assert.Equal(ExecutorJournalClaimStatus.Completed, saved.Status);
            }
            var archive = new ArchiveClient(client); await archive.GetCapabilitiesAsync(ct);
            var target = await archive.GetBindingTargetAsync(Json(new { protocol = "sdk2-ext-v1", sessionId = session.Id }), ct);
            var bindingId = target.GetProperty("bindingId").GetString()!;
            var binding = await archive.GetBindingAsync(bindingId, ct);
            var status = await archive.GetArchiveStatusAsync(bindingId, ct);
            for (var retry = 0; status.GetProperty("publishedThroughSequence").ValueKind == JsonValueKind.Null && retry < 100; retry++)
            { await Task.Delay(30, ct); status = await archive.GetArchiveStatusAsync(bindingId, ct); }
            Assert.Equal("1", status.GetProperty("publishedThroughSequence").GetString());
            var identity = Json(new
            {
                scope = new { applicationScopeId = "net-integration-app", endUserId = "net-integration-user" },
                bindingId,
                sourceId = status.GetProperty("sourceId").GetString(),
                sourceGeneration = status.GetProperty("sourceGeneration").GetString(),
                target = new { sessionId = session.Id, generations = status.GetProperty("generations") }
            });
            var key = CurrentUserDpapiArchiveKeyProvider.Create(Path.Combine(directory, "archive-key.json"), "integration-key");
            var settings = new SqliteArchiveStoreOptions
            {
                Path = Path.Combine(directory, "archive.sqlite"),
                Mode = StorageOpenMode.Create,
                Identity = identity,
                Replica = Json(new { replicationId = "net-primary", role = "primary" }),
                ReadContext = () => Scope,
                ReadRetentionRevision = () => "0",
                AuthorizeRetention = _ => throw new InvalidOperationException("No deletion is authorized by this fixture."),
                KeyProvider = key
            };
            using (var store = await SqliteArchiveStore.OpenAsync(settings, ct))
            {
                var transfer = new ArchiveTransferSession(archive, store, identity, () => Scope,
                    current => Json(new { operationEpoch = current.GetProperty("operationEpoch").GetProperty("id").GetString(), requestId = "net-original-archive-ack" }));
                var received = await transfer.PullAsync(2, ct);
                Assert.Equal(1, received.ArchivedRecords); Assert.True(received.Complete); Assert.Single(received.Receipts);
                Assert.Null(await store.PendingAsync(ct));
                var page = await store.ReadRecordsAsync(new ArchiveReadRequest { Identity = identity, Selection = Json(new { fromSequence = "1", throughSequence = "1" }) }, ct);
                var record = Assert.Single(page.Records);
                var body = await store.BodyAsync(record.GetProperty("payload"), ct);
                Assert.Contains("NET_DEVICE_NOTE_7319", new UTF8Encoding(false, true).GetString(body));
                Assert.Equal(record.GetProperty("payload").GetProperty("sha256").GetString(), WireJson.Sha256(body));
            }
            settings.Mode = StorageOpenMode.Reopen;
            settings.KeyProvider = CurrentUserDpapiArchiveKeyProvider.Open(Path.Combine(directory, "archive-key.json"), "integration-key");
            using (var reopened = await SqliteArchiveStore.OpenAsync(settings, ct))
            {
                Assert.Null(await reopened.PendingAsync(ct)); Assert.Equal("1", (await reopened.HeadAsync(ct))!.Value.GetProperty("sequence").GetString());
                // Trusted host explicitly requests a retained original record. The test IPC only
                // controls host lifecycle; .NET receives material.request through the real SSE.
                var page = await reopened.ReadRecordsAsync(new ArchiveReadRequest { Identity = identity, Selection = Json(new { fromSequence = "1", throughSequence = "1" }) }, ct);
                var original = Assert.Single(page.Records);
                using var outbox = await SqliteMaterialResponseOutbox.OpenAsync(new SqliteMaterialResponseOutboxOptions
                { Path = Path.Combine(directory, "material-outbox.sqlite"), Mode = StorageOpenMode.Create, Identity = identity, ReadContext = () => Scope }, ct);
                binding = await archive.GetBindingAsync(bindingId, ct);
                var epoch = binding.GetProperty("operationEpoch").GetProperty("id").GetString();
                var materials = new MaterialSource(archive, reopened, identity, () => Scope,
                    _ => Json(new { operationEpoch = epoch, requestId = "net-original-material-response" }), outbox);
                var delivered = new TaskCompletionSource<MaterialSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var observe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var events = archive.ConsumeEventsAsync(bindingId, status.GetProperty("generations"), async (frame, token) =>
                {
                    try
                    {
                        if (frame.GetProperty("eventType").GetString() != "material.request") return;
                        var received = await materials.RespondFrameAsync(frame, token);
                        Assert.NotNull(received);
                        // A host-owned cursor is durably committed only after the original response
                        // has been accepted, so reconnect cannot skip an unprocessed material request.
                        using var cursorFile = new FileStream(Path.Combine(directory, "archive-event-cursor.txt"), FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                        var bytes = Encoding.UTF8.GetBytes(frame.GetProperty("cursor").GetString()!); cursorFile.Write(bytes); cursorFile.Flush(true);
                        delivered.TrySetResult(received);
                    }
                    catch (Exception error) { delivered.TrySetException(error); throw; }
                }, cancellationToken: observe.Token);
                _ = events.ContinueWith(task => { if (task.IsFaulted) delivered.TrySetException(task.Exception!.GetBaseException()); }, TaskScheduler.Default);
                try
                {
                    var issued = await HostCommandAsync(new
                    {
                        id = "net-material-request",
                        action = "materials",
                        sessionId = session.Id,
                        recordIds = new[] { original.GetProperty("recordId").GetString() }
                    }, ct);
                    Assert.Equal(original.GetProperty("recordId").GetString(), issued.GetProperty("requestedRecords")[0].GetProperty("recordId").GetString());
                    var returned = await delivered.Task.WaitAsync(ct);
                    Assert.Equal("received", returned.Receipt.GetProperty("state").GetString());
                    Assert.Null(await outbox.ReadAsync(ct)); Assert.Single(returned.Availability!.AvailableRecordIds);
                }
                finally { observe.Cancel(); try { await events; } catch (OperationCanceledException) when (observe.IsCancellationRequested) { } }
                await HostCommandAsync(new { id = "net-material-enqueue", action = "enqueue", sessionId = session.Id, materialRequestId = "net-material-request" }, ct);
                var next = await session.SendAndObserveAsync("Use the original returned terminal material.", cancellationToken: ct);
                Assert.False(next.WasAborted);
                var consumed = await archive.GetMaterialStatusAsync(Json(new { protocol = "sdk2-ext-v1", bindingId, materialRequestId = "net-material-request" }), ct);
                Assert.Equal("core-consumed", consumed.GetProperty("state").GetString());
                var second = new ArchiveTransferSession(archive, reopened, identity, () => Scope,
                    current => Json(new { operationEpoch = current.GetProperty("operationEpoch").GetProperty("id").GetString(), requestId = "net-second-archive-ack" }));
                var secondPage = await second.PullAsync(2, ct); Assert.Equal(1, secondPage.ArchivedRecords); Assert.True(secondPage.Complete);
            }
            status = await archive.GetArchiveStatusAsync(bindingId, ct);
            Assert.Equal(0, status.GetProperty("pendingRecords").GetInt32());
            Assert.Equal("2", status.GetProperty("acknowledgedCoverage").GetProperty("throughSequence").GetString());
            output.WriteLine("Source {0}; public DeviceSessionHost -> real Read -> original kernel turn -> encrypted SQLite -> original ACK -> .NET archive SSE/material outbox -> original core consumption -> second archive ACK; {1} device authorization checks.", Required("TANSR_SERVE_SOURCE_SHA"), operations.Count);
        }
        finally { await host.StopAsync(); }
        await session.CloseAsync(ct);
    }

    private static async Task<JsonElement> HostCommandAsync(object command, CancellationToken cancellationToken)
    {
        var directory = Required("TANSR_SERVE_TEST_DIRECTORY"); var value = Json(command); var id = value.GetProperty("id").GetString();
        var temporary = Path.Combine(directory, "host-command.tmp");
        await File.WriteAllBytesAsync(temporary, WireJson.EncodeControl(value), cancellationToken);
        File.Move(temporary, Path.Combine(directory, "host-command.json"), true);
        for (; ; )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "host-response.json");
            if (File.Exists(path))
            {
                using var response = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
                if (response.RootElement.GetProperty("id").GetString() == id) return response.RootElement.GetProperty("value").Clone();
            }
            await Task.Delay(30, cancellationToken);
        }
    }
}
