using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>Consumes the sealed public Serve fixture over its original HTTP APIs. The fixture's
/// control channel supplies trusted synthetic host state; it never manufactures execution receipts.</summary>
public sealed class ServeEncryptedMemoryPublicationTests(ITestOutputHelper output)
{
    private const string Secret = "NET_PST_DEVICE_58371_private_body";
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(name + " is required; run the packed memory integration gate.");
    private static JsonElement Request(JsonElement operation)
    { using var document = JsonDocument.Parse(Text(operation.GetProperty("request").GetProperty("args"), "argsJson")); return document.RootElement.Clone(); }
    private static string Owner(JsonElement operation) => WireJson.CanonicalString(Json(new
    { scope = operation.GetProperty("scope"), sessionId = operation.GetProperty("sessionId"), binding = operation.GetProperty("binding") }));

    [Fact]
    [Trait("Category", "PstMemoryPublication")]
    public async Task EncryptedDemoKeepsOriginalReceiptThroughResponseLossAndProcessRestart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100)); var ct = deadline.Token;
        using var f = new Fixture();
        using var loss = new LostResponseHandler("/memory/commands"); using var http = new HttpClient(loss);
        using var client = new TansrClient(f.ClientOptions(), http);
        var session = await client.CreateSessionAsync(new CreateSessionOptions { Tools = ["SearchMemory"] }, ct);
        var control = new TerminalSessionControl(client, enablePreview: true);
        f.WriteDemoConfiguration(session.Id, "create");
        await using (var demo = new DemoProcess(f))
        {
            await demo.ReadyAsync(ct);
            var state = await control.ReadMemoryAsync(session.Id, ct);
            var pin = control.CreateMemoryOperation(session.Id, state, "net-pst-pin-request", "net-pst-pin-operation", Json(new { kind = "pin", text = Secret }));
            // The original command body and scope are the only restart anchor. No replacement IDs.
            var originalRequest = pin.Request; var originalScope = pin.Scope;
            var submitError = await Record.ExceptionAsync(() => SubmitOriginalAsync(control, pin, ct));
            if (submitError is UnifiedApiException unified) output.WriteLine("Original pin refusal: " + unified.Code + " " + unified.Detail.Raw?.GetRawText());
            Assert.Equal("network_error", Assert.IsType<TansrProtocolException>(submitError).Code);
            Assert.Equal(1, loss.LostResponseCount);
            var restored = control.RestoreMemoryOperation(originalRequest, originalScope);
            var recovered = await control.QueryMemoryAsync(restored, ct);
            Assert.Equal(WireJson.CanonicalString(loss.LostReceipt!.Value), WireJson.CanonicalString(recovered));
            Assert.Equal("committed", Text(recovered.GetProperty("receipt"), "status"));
            Assert.True(recovered.GetProperty("receipt").GetProperty("durable").GetBoolean());
            Assert.False(recovered.GetProperty("receipt").GetProperty("consumed").GetBoolean());
            var posts = loss.Requests.Where(request => request.Method == "POST" && request.Path.EndsWith("/memory/commands", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(posts);
            Assert.All(posts, request => Assert.Equal(WireJson.CanonicalString(originalRequest), WireJson.CanonicalString(request.Body!.Value)));
            var otherOptions = f.ClientOptions(); otherOptions.TokenProvider = _ => Task.FromResult(f.OtherToken);
            using var other = new TansrClient(otherOptions);
            await Assert.ThrowsAnyAsync<TansrHttpException>(() => new TerminalSessionControl(other, enablePreview: true).ReadMemoryAsync(session.Id, ct));
            await demo.StopAsync(ct);
        }
        var key = CurrentUserDpapiArchiveKeyProvider.Open(f.KeyPath, "net-pst-key");
        JsonElement commit; string receipt; int operationCount;
        using (var journal = await SqliteExecutorJournal.OpenAsync(f.JournalOptions(StorageOpenMode.Reopen, key), ct))
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(f.StoreOptions(StorageOpenMode.Reopen, key), ct))
        {
            Assert.True(journal.EncryptedAtRest); Assert.True(store.EncryptedBody);
            var operations = await journal.OperationsAsync(cancellationToken: ct); operationCount = operations.Count;
            Assert.NotEmpty(operations);
            Assert.Contains(operations, operation => Text(Request(operation), "action") == "chunk");
            commit = operations.Last(operation => Text(Request(operation), "action") == "commit");
            receipt = WireJson.CanonicalString((await journal.ReceiptAsync(commit, ct))!.Value);
            Assert.Equal("completed", Text((await journal.ReceiptAsync(commit, ct))!.Value, "status"));
            Assert.Equal("committed", Text((await store.ExecuteAsync(f.Control("query", new { transferId = Text(Request(commit), "transferId") }), Owner(commit), ct)).GetProperty("transfer"), "status"));
            Assert.Contains(Secret, Encoding.UTF8.GetString(await f.ReadAllAsync(store, Owner(commit), ct)));
            f.Probe(operations);
        }
        f.AssertClosed();
        // A second real process reopens both original encrypted files. A new connection is not
        // a grant to resume old transfers; this process only binds and cleanly stops.
        f.WriteDemoConfiguration(session.Id, "reopen");
        await using (var restarted = new DemoProcess(f)) { await restarted.ReadyAsync(ct); await restarted.StopAsync(ct); }
        using (var journal = await SqliteExecutorJournal.OpenAsync(f.JournalOptions(StorageOpenMode.Reopen, key), ct))
        {
            Assert.Equal(operationCount, (await journal.OperationsAsync(cancellationToken: ct)).Count);
            Assert.Equal(receipt, WireJson.CanonicalString((await journal.ReceiptAsync(commit, ct))!.Value));
        }
        f.AssertClosed();
        await using (var revoked = new DemoProcess(f))
        {
            await revoked.ReadyAsync(ct);
            await f.ControlHostAsync("set-business", false, ct);
            await f.ControlHostAsync("set-auth", false, ct);
            try
            {
                await Assert.ThrowsAnyAsync<TansrHttpException>(() => control.ReadMemoryAsync(session.Id, ct));
                Assert.Equal(1, await revoked.ExitAsync(ct));
            }
            finally { await f.ControlHostAsync("set-auth", true, ct); await f.ControlHostAsync("set-business", true, ct); }
        }
        f.AssertClosed();
        using (var journal = await SqliteExecutorJournal.OpenAsync(f.JournalOptions(StorageOpenMode.Reopen, key), ct))
            Assert.Equal(receipt, WireJson.CanonicalString((await journal.ReceiptAsync(commit, ct))!.Value));
        var originalBytes = f.DatabaseHashes();
        f.CurrentScope = Replace(f.Scope, "endUserId", Json("different-user")); f.WriteScope();
        await using (var denied = new DemoProcess(f)) { Assert.Equal(1, await denied.ExitAsync(ct)); }
        Assert.Equal(originalBytes, f.DatabaseHashes()); f.AssertClosed();
        output.WriteLine("Real Console process: DPAPI publication + actual encrypted execution journal; original command response lost then queried without another POST; two process lifetimes preserve permanent receipt; wrong scope fails without rewriting or retained handles.");
    }

    [Fact]
    [Trait("Category", "PstMemoryPublication")]
    public Task OriginalChunkCommitLossKeepsUnknownWithoutReexecution() => ChunkUnknownAsync(false);

    [Fact]
    [Trait("Category", "PstMemoryPublication")]
    public Task InFlightStopKeepsOriginalUnknownWithoutReexecution() => ChunkUnknownAsync(true);

    private async Task ChunkUnknownAsync(bool stopInFlight)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        using var f = new Fixture();
        var key = CurrentUserDpapiArchiveKeyProvider.Create(f.KeyPath, "net-pst-key");
        using var controller = new TansrClient(f.ClientOptions()); using var device = new TansrClient(f.ClientOptions());
        var session = await controller.CreateSessionAsync(new CreateSessionOptions { Tools = ["SearchMemory"] }, ct);
        var control = new TerminalSessionControl(controller, enablePreview: true);
        JsonElement original; JsonElement unknown; string owner; int count;
        using (var journal = await SqliteExecutorJournal.OpenAsync(f.JournalOptions(StorageOpenMode.Create, key), ct))
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(f.StoreOptions(StorageOpenMode.Create, key), ct))
        using (var workspace = new WindowsWorkspace(f.Workspace))
        {
            var fault = new LostChunkStore(store, stopInFlight);
            var tool = new WindowsMemoryPublicationHost(fault, enablePreview: true, requireEncryption: true).CreateTool();
            var backend = new WindowsExecutorBackend(f.ExecutorId, [new WindowsExecutorWorkspace("work", "1", workspace)], [tool]);
            var authorized = new ConcurrentQueue<JsonElement>();
            using var host = new DeviceSessionHost(new ExecutionClient(controller), new ExecutionClient(device), backend, journal,
                new DeviceSessionOptions { SessionId = session.Id, WorkspaceId = "work", RequestedTools = ["SearchMemory"] }, (operation, token) =>
                { token.ThrowIfCancellationRequested(); Assert.Equal("MemoryPublication", Text(operation, "toolName")); authorized.Enqueue(operation.Clone()); return Task.CompletedTask; });
            await host.StartAsync(ct);
            try
            {
                var state = await control.ReadMemoryAsync(session.Id, ct);
                var command = control.CreateMemoryOperation(session.Id, state, "net-pst-unknown-request", "net-pst-unknown-operation", Json(new { kind = "pin", text = Secret }));
                fault.Armed = true;
                using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var submission = SubmitOriginalAsync(control, command, requestLifetime.Token);
                if (await Task.WhenAny(fault.Committed.Task, submission).WaitAsync(ct) == submission)
                { await submission; Assert.Fail("The original command completed without reaching the armed real chunk."); }
                await fault.Committed.Task.WaitAsync(ct);
                if (stopInFlight) { await host.StopAsync().WaitAsync(ct); requestLifetime.Cancel(); }
                try { await submission; } catch (Exception error) when (error is TansrProtocolException or TansrHttpException or OperationCanceledException) { }
                await host.StopAsync().WaitAsync(ct);
                original = authorized.Last(operation => Text(Request(operation), "action") == "chunk"); owner = Owner(original);
                unknown = (await journal.ReceiptAsync(original, ct))!.Value;
                Assert.Equal("unknown", Text(unknown, "status"));
                Assert.Equal(1, fault.FaultCount);
                count = (await journal.OperationsAsync(cancellationToken: ct)).Count;
                var transfer = (await store.ExecuteAsync(f.Control("query", new { transferId = Text(Request(original), "transferId") }), owner, ct)).GetProperty("transfer");
                Assert.Equal("staging", Text(transfer, "status")); Assert.True(transfer.GetProperty("receivedBytes").GetInt32() > 0);
                // Submission after reconnect uses the durable original receipt and the frozen API,
                // never the backend. It also covers the receipt not sent during in-flight stop.
                var status = await new ExecutionClient(device).SubmitAsync(unknown, ct);
                Assert.Equal(WireJson.CanonicalString(unknown), WireJson.CanonicalString(status.GetProperty("receipt")));
                var sameKey = await new ExecutionClient(controller).GetStatusAsync(session.Id, Text(original, "operationId"), ct);
                Assert.Equal("unknown", Text(sameKey, "status"));
                Assert.Equal(WireJson.CanonicalString(original), WireJson.CanonicalString(sameKey.GetProperty("operation")));
                f.Probe(await journal.OperationsAsync(cancellationToken: ct));
            }
            finally { await host.StopAsync().WaitAsync(ct); }
        }
        f.AssertClosed();
        using (var journal = await SqliteExecutorJournal.OpenAsync(f.JournalOptions(StorageOpenMode.Reopen, key), ct))
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(f.StoreOptions(StorageOpenMode.Reopen, key), ct))
        {
            Assert.Equal(count, (await journal.OperationsAsync(cancellationToken: ct)).Count);
            var replay = await journal.ClaimAsync(original, ct);
            Assert.Equal(ExecutorJournalClaimStatus.Completed, replay.Status);
            Assert.Equal(WireJson.CanonicalString(unknown), WireJson.CanonicalString(replay.Receipt!.Value));
            var newOwner = Replace(WireJson.Parse(Encoding.UTF8.GetBytes(owner)), "sessionId", Json("other-session"));
            Assert.Equal("request_conflict", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => store.ExecuteAsync(Request(original), WireJson.CanonicalString(newOwner), ct))).Code);
            var changed = Replace(Request(original), "base64", Json(Convert.ToBase64String(Encoding.UTF8.GetBytes("changed"))));
            changed = Replace(changed, "byteLength", Json(7)); changed = Replace(changed, "payloadDigest", Json(WireJson.Sha256(Encoding.UTF8.GetBytes("changed"))));
            Assert.Equal("request_conflict", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => store.ExecuteAsync(changed, owner, ct))).Code);
            Assert.Equal("staging", Text((await store.ExecuteAsync(f.Control("query", new { transferId = Text(Request(original), "transferId") }), owner, ct)).GetProperty("transfer"), "status"));
        }
        f.AssertClosed();
        output.WriteLine("Actual Serve-issued chunk committed to encrypted SQLite, then " + (stopInFlight ? "cancelled in flight" : "lost its result") + "; original unknown receipt was durably kept, submitted/queried by original key, and preserved on reopen; changed body and new-owner write refused.");
    }

    private async Task<JsonElement> SubmitOriginalAsync(TerminalSessionControl control, MemoryOperation operation, CancellationToken ct)
    {
        var started = Stopwatch.StartNew(); var attempts = 0;
        while (true)
        {
            try
            {
                attempts++;
                return attempts == 1 ? await control.SubmitMemoryAsync(operation, ct) : await control.ReplayMemoryAsync(operation, ct);
            }
            catch (UnifiedApiException error) when (error.Detail.DomainCode == "busy" && error.Detail.DomainRetryAction == "backoff")
            {
                // Only this explicit pre-admission refusal allows a same-key attempt. A missing
                // query alone, any unknown outcome or any changed request never grants replay.
                var observed = await control.QueryMemoryAsync(operation, ct);
                Assert.Equal(JsonValueKind.Null, observed.GetProperty("receipt").ValueKind);
                if (started.Elapsed > TimeSpan.FromSeconds(15))
                { output.WriteLine("Original busy remained unadmitted after " + attempts + " attempts / " + started.ElapsedMilliseconds + "ms."); throw; }
                await Task.Delay(200, ct);
            }
        }
    }

    private static JsonElement Replace(JsonElement value, string name, JsonElement field)
    { var node = JsonNode.Parse(value.GetRawText())!.AsObject(); node[name] = JsonNode.Parse(field.GetRawText()); return JsonSerializer.SerializeToElement(node); }

    private sealed class LostChunkStore(SqliteMemoryPublicationStore store, bool waitForStop) : IMemoryPublicationStore, IEncryptedMemoryPublicationStore
    {
        internal bool Armed; internal int FaultCount;
        internal TaskCompletionSource<bool> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AtomicDurablePublication => store.AtomicDurablePublication;
        public bool EncryptedBody => store.EncryptedBody;
        public JsonElement Identity => store.Identity;
        public Task CloseAsync(CancellationToken cancellationToken = default) => store.CloseAsync(cancellationToken);
        public async Task<JsonElement> ExecuteAsync(JsonElement request, string ownerCanonical, CancellationToken cancellationToken = default)
        {
            var result = await store.ExecuteAsync(request, ownerCanonical, cancellationToken);
            if (Armed && Text(request, "action") == "chunk")
            {
                Armed = false; FaultCount++; Committed.TrySetResult(true);
                if (waitForStop) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new IOException("synthetic_result_lost_after_real_chunk_commit");
            }
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly JsonElement config;
        internal readonly string DirectoryPath, Workspace, KeyPath, DemoConfiguration, ScopeFile;
        internal readonly JsonElement Scope, Identity;
        internal JsonElement CurrentScope;
        internal string ExecutorId => Text(config, "executorId");
        internal string Token => Text(config, "token");
        internal string OtherToken => Text(config, "otherToken");
        internal async Task ControlHostAsync(string command, bool allowed, CancellationToken ct)
        {
            using var http = new HttpClient(); using var request = new HttpRequestMessage(HttpMethod.Post, Text(config, "controlURL"));
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Text(config, "controlKey"));
            request.Content = new StringContent(Json(new { command, allowed }).GetRawText(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        }
        internal Fixture()
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Required("TANSR_PST_HOST_CONFIG"))); config = document.RootElement.Clone();
            Scope = config.TryGetProperty("scope", out var scope) ? scope.Clone() : Json(new
            { applicationScopeId = Text(config, "applicationScopeId"), endUserId = Text(config, "endUserId"), authorizationRevision = Text(config, "authorizationRevision") });
            CurrentScope = Scope;
            var publication = config.GetProperty("publicationIdentity");
            Identity = publication.TryGetProperty("scope", out _) ? publication.Clone() : Json(new
            { scope = new { applicationScopeId = Text(Scope, "applicationScopeId"), endUserId = Text(Scope, "endUserId") }, sourceId = Text(publication, "sourceId"), sourceGeneration = Text(publication, "sourceGeneration"), domainKey = Text(publication, "domainKey") });
            Assert.Equal("127.0.0.1", new Uri(Text(config, "baseURL")).Host);
            var parent = Path.GetFullPath(Required("TANSR_PST_TEST_DIRECTORY"));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), parent, StringComparison.OrdinalIgnoreCase);
            DirectoryPath = Path.Combine(parent, "net-device-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(DirectoryPath);
            Workspace = Path.Combine(DirectoryPath, "workspace"); Directory.CreateDirectory(Workspace);
            KeyPath = Path.Combine(DirectoryPath, "device.key"); ScopeFile = Path.Combine(DirectoryPath, "scope.json");
            DemoConfiguration = Path.Combine(DirectoryPath, "device.json"); WriteScope();
        }
        internal TansrClientOptions ClientOptions() => new()
        {
            BaseUri = new Uri(Text(config, "baseURL")),
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(Token),
            PrincipalProvider = () => Text(CurrentScope, "applicationScopeId") + "/" + Text(CurrentScope, "endUserId"),
            ExecutionScopeProvider = () => CurrentScope.Clone(),
            RequestTimeout = TimeSpan.FromSeconds(25),
            MaxReconnectAttempts = 0,
        };
        internal void WriteScope() => File.WriteAllText(ScopeFile, Json(new { principal = Text(CurrentScope, "applicationScopeId") + "/" + Text(CurrentScope, "endUserId"), scope = CurrentScope }).GetRawText());
        internal void WriteDemoConfiguration(string sessionId, string mode) => File.WriteAllText(DemoConfiguration, Json(new
        {
            format = "tansr-example-device-memory-v1",
            enablePreview = true,
            serveUrl = Text(config, "baseURL"),
            allowInsecureLoopback = true,
            sessionId,
            executorId = ExecutorId,
            trustedScopeFile = ScopeFile,
            controllerTokenEnvironment = "NET_PST_CONTROLLER",
            deviceTokenEnvironment = "NET_PST_DEVICE",
            workspace = new { path = Workspace, id = "work", revision = "1" },
            encryption = new { provider = "dpapi-current-user", path = KeyPath, keyId = "net-pst-key", mode },
            journal = new { path = Path.Combine(DirectoryPath, "execution.sqlite"), mode, maxOperations = 256, maxStoredBytes = 67108864, maxPages = 32768 },
            publication = new { path = Path.Combine(DirectoryPath, "memory.sqlite"), mode, maxTransfers = 32, maxStagingBytes = 8388608, maxPages = 8192, identity = Identity },
        }).GetRawText());
        internal SqliteExecutorJournalOptions JournalOptions(StorageOpenMode mode, IArchiveKeyProvider key) => new()
        {
            Path = Path.Combine(DirectoryPath, "execution.sqlite"),
            Mode = mode,
            ExecutorId = ExecutorId,
            ApplicationScopeId = Text(Scope, "applicationScopeId"),
            EndUserId = Text(Scope, "endUserId"),
            ReadContext = () => CurrentScope.Clone(),
            KeyProvider = key,
            CompactCompletedReceipts = true,
            MaxOperations = 256,
            MaxStoredBytes = 67108864,
            MaxPages = 32768,
        };
        internal SqliteMemoryPublicationOptions StoreOptions(StorageOpenMode mode, IArchiveKeyProvider key) => new()
        {
            EnablePreview = true,
            Path = Path.Combine(DirectoryPath, "memory.sqlite"),
            Mode = mode,
            Identity = Identity,
            ReadContext = () => CurrentScope.Clone(),
            KeyProvider = key,
            MaxTransfers = 32,
            MaxStagingBytes = 8388608,
            MaxPages = 8192,
        };
        internal JsonElement Control(string action, object? fields = null)
        {
            var value = new JsonObject { ["contract"] = "terminal-services-v1", ["action"] = action };
            foreach (var name in new[] { "sourceId", "sourceGeneration", "domainKey" }) value[name] = Text(Identity, name);
            if (fields != null) foreach (var pair in JsonSerializer.SerializeToElement(fields).EnumerateObject()) value[pair.Name] = JsonNode.Parse(pair.Value.GetRawText());
            return JsonSerializer.SerializeToElement(value);
        }
        internal async Task<byte[]> ReadAllAsync(SqliteMemoryPublicationStore store, string owner, CancellationToken ct)
        {
            var head = (await store.ExecuteAsync(Control("head"), owner, ct)).GetProperty("publication"); using var body = new MemoryStream();
            while (body.Length < head.GetProperty("byteLength").GetInt32())
            {
                var block = await store.ExecuteAsync(Control("read", new { etag = Text(head, "etag"), offset = (int)body.Length, length = 12288 }), owner, ct);
                body.Write(Convert.FromBase64String(Text(block, "base64")));
            }
            return body.ToArray();
        }
        internal void Probe(IReadOnlyList<JsonElement> operations)
        {
            var forbidden = new List<byte[]> { Encoding.UTF8.GetBytes(Secret) };
            forbidden.AddRange(operations.Select(Request).Where(value => Text(value, "action") == "chunk").Select(value => Encoding.ASCII.GetBytes(Text(value, "base64"))));
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.sqlite*"))
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream(); input.CopyTo(copy); var bytes = copy.ToArray();
                foreach (var needle in forbidden.Where(value => value.Length >= 16)) Assert.True(bytes.AsSpan().IndexOf(needle) < 0, "Sensitive plaintext/base64 found in " + Path.GetFileName(path));
            }
        }
        internal string[] DatabaseHashes() => new[] { "memory.sqlite", "execution.sqlite" }.Select(name => WireJson.Sha256(File.ReadAllBytes(Path.Combine(DirectoryPath, name)))).ToArray();
        internal void AssertClosed()
        {
            foreach (var name in new[] { "memory.sqlite", "execution.sqlite", "device.key" })
                using (new FileStream(Path.Combine(DirectoryPath, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    private sealed class DemoProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task outputPump, errorPump;
        private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder transcript = new();
        internal DemoProcess(Fixture fixture)
        {
            var start = new ProcessStartInfo(Required("TANSR_PST_DEMO_EXE"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--device-memory"); start.ArgumentList.Add(fixture.DemoConfiguration);
            start.Environment["NET_PST_CONTROLLER"] = fixture.Token; start.Environment["NET_PST_DEVICE"] = fixture.Token;
            process = Process.Start(start) ?? throw new InvalidOperationException("Example process did not start.");
            outputPump = PumpAsync(process.StandardOutput); errorPump = PumpAsync(process.StandardError);
        }
        private async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (transcript) { if (transcript.Length > 65536) throw new IOException("Bounded demo output exceeded."); transcript.AppendLine(line); }
                if (line.Contains("device=Ready", StringComparison.Ordinal)) ready.TrySetResult(true);
            }
        }
        internal async Task ReadyAsync(CancellationToken ct)
        {
            var exited = process.WaitForExitAsync(ct);
            if (await Task.WhenAny(ready.Task, exited) == exited) { await exited; Assert.Fail("Demo exited before Ready: " + transcript); }
            await ready.Task.WaitAsync(ct);
        }
        internal async Task<int> ExitAsync(CancellationToken ct) { await process.WaitForExitAsync(ct); await Task.WhenAll(outputPump, errorPump); return process.ExitCode; }
        internal async Task StopAsync(CancellationToken ct)
        {
            Assert.False(process.HasExited, "Demo stopped before explicit stop: " + transcript);
            await process.StandardInput.WriteLineAsync("/stop"); await process.StandardInput.FlushAsync(ct);
            Assert.True(await ExitAsync(ct) == 0, "Demo stop failed: " + transcript);
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(outputPump, errorPump); process.Dispose();
        }
    }
}
