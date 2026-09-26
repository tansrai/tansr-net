using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>One current Serve, original mobile clients and Electron runtime; no paid model.</summary>
public sealed class ServeSharedClientsTests(ITestOutputHelper output)
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " required by shared-serve-integration.mjs");

    [Fact]
    [Trait("Category", "SharedServeIntegration")]
    public async Task FiveClientsRemainIsolatedWhileNativeExecutionFailsAndLosesAuthority()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(7)); var ct = deadline.Token;
        var directory = Required("TANSR_SHARED_DIRECTORY");
        using var control = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18890"), Timeout = TimeSpan.FromSeconds(10) };
        control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-control");
        async Task<JsonElement> State() { using var response = await control.GetAsync("/state", ct); response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct)).RootElement.Clone(); }
        async Task Post(string route, object value) { using var body = new StringContent(Json(value).GetRawText(), System.Text.Encoding.UTF8, "application/json"); using var response = await control.PostAsync(route, body, ct); response.EnsureSuccessStatusCode(); }
        async Task Until(Func<JsonElement, bool> predicate) { while (!predicate(await State())) await Task.Delay(100, ct); }
        TansrClient Client(string role, string user) => new(new TansrClientOptions
        {
            BaseUri = new Uri(Required("TANSR_SHARED_URL")),
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult("demo1.unified." + role),
            PrincipalProvider = () => "unified-app/" + user,
            ExecutionScopeProvider = () => Json(new { applicationScopeId = "unified-app", endUserId = user, authorizationRevision = "1" }),
            RequestTimeout = TimeSpan.FromSeconds(10),
            StreamIdleTimeout = TimeSpan.FromSeconds(40),
            MaxReconnectAttempts = 0
        });
        using var mobile = Client("ui", "u-unified"); using var mobileDevice = Client("device", "u-unified");
        using var native = Client("csharp", "u-csharp"); using var nativeDevice = Client("csharp-device", "u-csharp");
        var resources = new List<IDisposable>(); var hosts = new List<DeviceSessionHost>(); var revoked = false;
        async Task<(AgentSession Session, SqliteExecutorJournal Journal, string Work)> Open(TansrClient owner, TansrClient executor, string user, string id)
        {
            var work = Path.Combine(directory, id); Directory.CreateDirectory(work);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); using var identity = WindowsIdentity.GetCurrent();
            security.SetOwner(identity.User!); security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); new DirectoryInfo(work).SetAccessControl(security);
            var workspace = new WindowsWorkspace(work, new WindowsWorkspaceOptions { AllWritersCooperate = true }); resources.Add(workspace);
            var session = await owner.CreateSessionAsync(new CreateSessionOptions { Tools = ["Read", "Write"], MaxTokens = 10000 }, ct);
            var journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
            {
                Path = Path.Combine(directory, id + ".sqlite"),
                Mode = StorageOpenMode.Create,
                ExecutorId = id,
                ApplicationScopeId = "unified-app",
                EndUserId = user,
                ReadContext = () => Json(new { applicationScopeId = "unified-app", endUserId = user, authorizationRevision = "1" })
            }, ct); resources.Add(journal);
            var backend = new WindowsExecutorBackend(id, [new WindowsExecutorWorkspace("work", "1", workspace)]);
            var host = new DeviceSessionHost(new ExecutionClient(owner), new ExecutionClient(executor), backend, journal,
                new DeviceSessionOptions { SessionId = session.Id, WorkspaceId = "work", RequestedTools = ["Read", "Write"] },
                (operation, token) => { token.ThrowIfCancellationRequested(); Assert.Equal(user, operation.GetProperty("scope").GetProperty("endUserId").GetString()); return Task.CompletedTask; });
            hosts.Add(host); await host.StartAsync(ct); Assert.Equal(DeviceSessionState.Ready, host.State);
            return (session, journal, work);
        }
        async Task Denied(string role, string id, string suffix)
        {
            using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo1.unified." + role);
            using var response = await http.GetAsync(Required("TANSR_SHARED_URL") + "/v2/sessions/" + Uri.EscapeDataString(id) + suffix, ct);
            Assert.Contains((int)response.StatusCode, new[] { 401, 403, 404 });
        }
        try
        {
            var shared = await Open(mobile, mobileDevice, "u-unified", "shared-mobile-pc");
            var own = await Open(native, nativeDevice, "u-csharp", "shared-csharp-pc");
            await Post("/sessions", new { windows = shared.Session.Id, csharp = own.Session.Id });
            await Post("/timeline", new { platform = "csharp", stage = "ready", sessionId = own.Session.Id });
            await Until(state => state.GetProperty("barrier").GetBoolean());
            await Post("/timeline", new { platform = "csharp", stage = "active" });
            // Metadata includes context/usage projections; never count a nonexistent route's 404 as isolation.
            foreach (var suffix in new[] { "", "/history" }) await Denied("csharp", shared.Session.Id, suffix);
            async Task<JsonElement> Run(string id, string name, object args, bool success)
            {
                using var run = own.Session.StartRun("NET_SHARED:" + Json(new { id, name, args }).GetRawText(), new SessionRunOptions { Timeout = TimeSpan.FromSeconds(25) }, async (item, token) =>
                {
                    if (item.Name == "server.permission.request") { var p = item.Data.GetProperty("payload"); await own.Session.PermissionAsync(p.GetProperty("requestId").GetString()!, p.GetProperty("digest").GetString()!, true, token); }
                }, ct);
                var done = await run.Completion; Assert.False(done.WasAborted); Assert.Equal("completed", done.Reason);
                var state = await State(); var original = Assert.Single(state.GetProperty("results").EnumerateArray(), item => item.GetProperty("id").GetString() == id).GetProperty("result");
                Assert.Equal(!success, original.TryGetProperty("isError", out var error) && error.GetBoolean()); return original.Clone();
            }
            await Run("csharp-write", "Write", new { file_path = "/workspace/work/own.txt", contents = "CSHARP_PRIVATE_WORKSPACE" }, true);
            await Run("csharp-fail", "Read", new { file_path = "/workspace/work/missing.txt" }, false);
            var completed = await own.Journal.OperationsAsync(cancellationToken: ct); Assert.NotEmpty(completed);
            var originalOperation = completed[0];
            await Denied("electron", own.Session.Id, "/executions/" + originalOperation.GetProperty("operationId").GetString() + "?protocol=sdk2-ext-v1");
            // Disconnect only the local observation. The original remote session and prior receipt survive.
            using (var observer = new CancellationTokenSource())
            {
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var watching = own.Session.ObserveAsync((_, _) => { observed.TrySetResult(); return Task.CompletedTask; }, new EventStreamOptions { LastEventId = "0", Reconnect = false }, observer.Token);
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); observer.Cancel();
                try { await watching; } catch (OperationCanceledException) { }
            }
            var restored = await native.GetSessionAsync(own.Session.Id, ct); Assert.Equal(own.Session.Id, restored.Id);
            var oldStatus = await new ExecutionClient(native).GetStatusAsync(own.Session.Id, originalOperation.GetProperty("operationId").GetString()!, ct);
            Assert.Equal("completed", oldStatus.GetProperty("status").GetString());
            Assert.Equal(completed.Count, (await own.Journal.OperationsAsync(cancellationToken: ct)).Count);
            await Post("/pressure", new { }); var count = (await own.Journal.OperationsAsync(cancellationToken: ct)).Count;
            var pressure = await Run("csharp-pressure", "Write", new { file_path = "/workspace/work/blocked.txt", contents = "NEVER_WRITTEN" }, false);
            Assert.Matches("(?i)capacity|budget|backpressure", pressure.GetRawText());
            Assert.Equal(count, (await own.Journal.OperationsAsync(cancellationToken: ct)).Count); Assert.False(File.Exists(Path.Combine(own.Work, "blocked.txt")));
            using (var slowHttp = new HttpClient())
            {
                slowHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo1.unified.csharp");
                // Deliberately do not consume the body. The real server queue must apply backpressure.
                using var slow = await slowHttp.GetAsync(Required("TANSR_SHARED_URL") + "/v2/sessions/" + own.Session.Id + "/events", HttpCompletionOption.ResponseHeadersRead, ct);
                slow.EnsureSuccessStatusCode();
                using var flood = own.Session.StartRun("NET_SHARED:" + Json(new { id = "csharp-slow-stream", stream = true }).GetRawText(),
                    new SessionRunOptions { Timeout = TimeSpan.FromSeconds(45), MaximumTextCharacters = 4096 }, cancellationToken: ct);
                Assert.Equal("completed", (await flood.Completion).Reason);
                var backpressure = (await State()).GetProperty("slowStream");
                Assert.True(backpressure.GetProperty("disconnected").GetBoolean()); Assert.True(backpressure.GetProperty("sawPaused").GetBoolean());
            }
            await Post("/revoke", new { }); revoked = true; await Denied("csharp", own.Session.Id, "");
            await Post("/sessions", new { csharpFaultsComplete = true });
            await Until(state => state.GetProperty("sessions").GetProperty("mobileStage").GetString() == "done");
            var final = await State(); Assert.True(final.GetProperty("hostSentinelUnchanged").GetBoolean());
            foreach (var platform in new[] { "android", "harmony", "ios" })
            {
                Assert.Equal("native-mobile-" + platform, await File.ReadAllTextAsync(Path.Combine(shared.Work, "mobile-" + platform + ".txt"), ct));
                Assert.False(File.Exists(Path.Combine(own.Work, "mobile-" + platform + ".txt")));
            }
            Assert.Equal("CSHARP_PRIVATE_WORKSPACE", await File.ReadAllTextAsync(Path.Combine(own.Work, "own.txt"), ct));
            Assert.False(File.Exists(Path.Combine(shared.Work, "own.txt")));
            var ledger = await shared.Journal.OperationsAsync(cancellationToken: ct); Assert.Equal(3, ledger.Count);
            foreach (var operation in ledger) await Denied("electron", shared.Session.Id, "/executions/" + operation.GetProperty("operationId").GetString() + "?protocol=sdk2-ext-v1");
            await Post("/timeline", new { platform = "csharp", stage = "finish", localOperations = count, mobileOperations = ledger.Count });
            await File.WriteAllTextAsync(Path.Combine(directory, "csharp-evidence.json"), Json(new
            {
                passed = true,
                sharedSession = shared.Session.Id,
                nativeSession = own.Session.Id,
                disconnectedWithoutResend = true,
                backpressure = pressure,
                revoked = true,
                mobileOperations = ledger.Count,
                ownOperations = count
            }).GetRawText(), ct);
            output.WriteLine("Five-client shared Serve: C# real Windows tools, original mobile session, independent Electron scope; native faults did not leak workspaces or revoke other subjects.");
        }
        finally
        {
            // The revoked principal cannot close its session; the owned fixture closes it centrally.
            var cleanup = new List<Exception>();
            foreach (var host in hosts)
            {
                try { await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TansrHttpException fault) when (revoked && ReferenceEquals(host, hosts.Last()) && fault.StatusCode == 401)
                { Assert.Equal(DeviceSessionState.Failed, host.State); output.WriteLine("Revoked executor polling ended with the actual unauthorized response."); }
                catch (Exception fault) { cleanup.Add(fault); }
                finally { host.Dispose(); }
            }
            foreach (var resource in resources.AsEnumerable().Reverse()) try { resource.Dispose(); } catch (Exception fault) { cleanup.Add(fault); }
            if (cleanup.Count > 0) throw new AggregateException("Same-Serve owned device cleanup failed.", cleanup);
        }
    }
}
