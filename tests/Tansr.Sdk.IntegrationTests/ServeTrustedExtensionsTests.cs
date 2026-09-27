using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Mcp;
using Tansr.Sdk.Windows.Storage;
using Tansr.Sdk.Windows.Tests.Mcp;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>Original HTTP/SSE -> kernel -> trusted hooks/skills/child lifecycle.
/// Only model answers and explicitly trusted host policy are synthetic.</summary>
public sealed class ServeTrustedExtensionsTests(McpNativeFixture nativeMcp) : IClassFixture<McpNativeFixture>
{
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required.");
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Scope => Json(new { applicationScopeId = "net-trusted-app", endUserId = "net-integration-user", authorizationRevision = "1" });
    private static string Root => Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "trusted-extensions");

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task OriginalTrustedHooksDeviceSkillsAndChildrenRemainWithinTheirApprovedBoundaries()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        var events = new ConcurrentQueue<AgentEvent>();
        await InContextAsync(await Context.OpenAsync("hooks", ["TrustedOrder"], ct), async context =>
        {
            await PlanAsync("TrustedOrder", new { id = "synthetic" }, ct: ct);
            await TurnAsync(context.Session, "NET_TRUSTED_ALLOW", events, ct);
            Assert.Equal(1, (await InspectAsync(ct)).GetProperty("executeCount").GetInt32());
            await PlanAsync("TrustedOrder", new { id = "synthetic" }, "deny", ct: ct);
            await TurnAsync(context.Session, "NET_TRUSTED_DENY", events, ct);
            Assert.Equal(1, (await InspectAsync(ct)).GetProperty("executeCount").GetInt32());
            await PlanAsync("TrustedOrder", new { id = "synthetic" }, "throw", ct: ct);
            await TurnAsync(context.Session, "NET_TRUSTED_THROW", events, ct);
            var before = await InspectAsync(ct); Assert.Equal(1, before.GetProperty("executeCount").GetInt32());
            await PlanAsync(null, null, promptBlocked: true, ct: ct);
            await TurnAsync(context.Session, "NET_TRUSTED_PROMPT_DENIED", events, ct);
            Assert.Equal(before.GetProperty("requests").GetArrayLength(), (await InspectAsync(ct)).GetProperty("requests").GetArrayLength());
            Assert.True(before.GetProperty("hookCalls").GetInt32() >= 3);
        });
        await InContextAsync(await Context.OpenAsync("skills", ["Skill"], ct), async context =>
        {
            context.Workspace.WriteAtomic("SKILL.md", Encoding.UTF8.GetBytes("---\nname: device-guide\ndescription: local\n---\nDEVICE_GUIDE_BODY_中文🙂"));
            await PlanAsync("Skill", new { name = "device-guide" }, ct: ct);
            await TurnAsync(context.Session, "NET_DEVICE_SKILL", events, ct);
            await PlanAsync("Skill", new { name = "inline-guide" }, ct: ct);
            await TurnAsync(context.Session, "NET_INLINE_SKILL", events, ct);
            var evidence = await InspectAsync(ct);
            var requests = evidence.GetProperty("requests").EnumerateArray().Select(item => item.GetProperty("thread").GetRawText()).ToArray();
            Assert.Contains(requests, value => value.Contains("DEVICE_GUIDE_BODY_", StringComparison.Ordinal));
            Assert.Contains(requests, value => value.Contains("INLINE_GUIDE_BODY", StringComparison.Ordinal));
            Assert.Contains(evidence.GetProperty("events").EnumerateArray(), item => item.TryGetProperty("operation", out var operation) && operation.GetString() == "fs.read");
            Assert.True(context.Executions > 0, "The skill body must be read by the actual C# device backend.");
        });
        var mcpDirectory = Path.Combine(Root, "native-mcp-workspace"); Directory.CreateDirectory(mcpDirectory);
        using (var mcpWorkspace = new WindowsWorkspace(mcpDirectory))
        {
            var options = new WindowsDuplexProcessOptions(nativeMcp.Executable, Array.Empty<string>(), () => mcpWorkspace.AcquireProcessDirectory());
            options.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            using var stdio = await McpClient.ConnectStdioAsync(options, cancellationToken: ct);
            await NativeMcpAsync("stdio", stdio, "NATIVE_STDIO_MCP_中文🙂", events, ct);
            await stdio.CloseAsync(); Assert.Equal(McpConnectionState.Closed, stdio.State);
        }
        var origin = new Uri((await CommandAsync(new { action = "mcp-origin" }, ct)).GetProperty("url").GetString()!);
        using (var http = await McpClient.ConnectHttpAsync(new McpHttpOptions(origin, [origin]) { AllowLoopbackHttp = true }, cancellationToken: ct))
        {
            await NativeMcpAsync("http", http, "NATIVE_HTTP_MCP_中文🙂", events, ct);
            await http.CloseAsync(); Assert.Equal(McpConnectionState.Closed, http.State);
            var evidence = await InspectAsync(ct); Assert.Single(evidence.GetProperty("mcpCalls").EnumerateArray(), item => item.GetString() == "tools/call");
            Assert.Contains(evidence.GetProperty("requests").EnumerateArray(), item => item.GetProperty("thread").GetRawText().Contains("MCP structuredContent", StringComparison.Ordinal));
        }
        string governedSessionId = string.Empty;
        await InContextAsync(await Context.OpenAsync("governance", ["Task", "Write"], ct, maxTokens: 60), async context =>
        {
            governedSessionId = context.Session.Id;
            await CommandAsync(new { action = "governance", sessionId = governedSessionId }, ct);
            var result = await TurnAsync(context.Session, "NET_REAL_BUDGET_ADJUDICATION", events, ct, denyWrites: true);
            Assert.True(result.WasAborted); Assert.Equal("budget_exceeded", result.Reason);
            Assert.Equal(0, context.Executions);
            Assert.False(File.Exists(Path.Combine(Root, "dotnet-governance", "workspace", "denied-0.txt")));
            var governed = await InspectAsync(ct); var facts = governed.GetProperty("governance");
            Assert.Equal(1, facts.GetProperty("mainCalls").GetInt32()); Assert.Equal(1, facts.GetProperty("childCalls").GetInt32());
            Assert.Equal(1, facts.GetProperty("adjudicationCalls").GetInt32()); Assert.Equal(70, facts.GetProperty("spend").GetProperty("totalTokens").GetInt32());
            Assert.Contains(governed.GetProperty("decisions").EnumerateArray(), record => record.GetProperty("toolName").GetString() == "Task" && record.GetProperty("decision").GetString() == "allow");
            var denied = Assert.Single(governed.GetProperty("decisions").EnumerateArray(), record => record.GetProperty("callId").GetString() == "net-governance-write-0");
            Assert.Equal("deny", denied.GetProperty("decision").GetString());
            Assert.Contains(denied.GetProperty("adjudicationChain").EnumerateArray(), entry => entry.GetProperty("verdict").GetString() == "reject");
            Assert.Contains(events, item => item.Name == "tool.permission.decided");
        });
        await InContextAsync(await Context.OpenAsync("governance-resume", ["Task", "Write"], ct, maxTokens: 60, resumeSessionId: governedSessionId), async context =>
        {
            await CommandAsync(new { action = "governance", sessionId = context.Session.Id }, ct);
            var result = await TurnAsync(context.Session, "The existing persisted budget must not reset.", events, ct, denyWrites: true);
            Assert.True(result.WasAborted); Assert.Equal("budget_exceeded", result.Reason); Assert.Equal(0, context.Executions);
            var facts = (await InspectAsync(ct)).GetProperty("governance");
            Assert.Equal(1, facts.GetProperty("mainCalls").GetInt32()); Assert.Equal(1, facts.GetProperty("childCalls").GetInt32()); Assert.Equal(1, facts.GetProperty("adjudicationCalls").GetInt32());
            Assert.Equal(70, facts.GetProperty("spend").GetProperty("totalTokens").GetInt32());
        });
        await InContextAsync(await Context.OpenAsync("children", ["Task", "SpawnAgent", "AgentFollowup", "Read"], ct), async context =>
        {
            context.Workspace.WriteAtomic("child-sentinel.txt", Encoding.UTF8.GetBytes("CHILD_DEVICE_SENTINEL"));
            await PlanAsync("Task", new { description = "bounded foreground", prompt = "Synthetic foreground task", tools = new[] { "Read" } }, ct: ct);
            await TurnAsync(context.Session, "NET_FOREGROUND_CHILD", events, ct);
            await PlanAsync("SpawnAgent", new { label = "bounded resident", prompt = "Synthetic resident task", tools = new[] { "Read" } }, ct: ct);
            await TurnAsync(context.Session, "NET_RESIDENT_CHILD", events, ct);
            await WaitNoticesAsync(context.Session, events, 1, ct);
            var before = await InspectAsync(ct); var originalId = before.GetProperty("childId").GetString();
            Assert.False(string.IsNullOrEmpty(originalId));
            await PlanAsync("AgentFollowup", new { agent_id = "original-from-trusted-host", message = "Continue the synthetic task" }, ct: ct);
            await TurnAsync(context.Session, "NET_CHILD_FOLLOWUP", events, ct);
            await WaitNoticesAsync(context.Session, events, 2, ct);
            var followed = await InspectAsync(ct); Assert.Equal(originalId, followed.GetProperty("childId").GetString());
            Assert.True(followed.GetProperty("childCalls").GetInt32() > before.GetProperty("childCalls").GetInt32());
            var executionsBeforeRevoke = context.Executions; Assert.True(executionsBeforeRevoke > 0);
            await CommandAsync(new { action = "revoke" }, ct);
            await PlanAsync("AgentFollowup", new { agent_id = originalId, message = "Must not run after authority changes" }, ct: ct);
            await TurnAsync(context.Session, "NET_REVOKED_CHILD", events, ct);
            await WaitNoticesAsync(context.Session, events, 3, ct);
            var revoked = await InspectAsync(ct);
            Assert.Equal(followed.GetProperty("childCalls").GetInt32(), revoked.GetProperty("childCalls").GetInt32());
            Assert.Equal(executionsBeforeRevoke, context.Executions);
            Assert.Contains(revoked.GetProperty("notices").EnumerateArray(), item => item.TryGetProperty("outcome", out var value) && value.GetString() == "internal_error");
            Assert.Contains(events, item => item.Name == "agent.progress" && item.Data.GetRawText().Contains("stale_generation", StringComparison.Ordinal));
            Assert.All(revoked.GetProperty("subjects").EnumerateArray(), subject =>
            { Assert.Equal("net-trusted-app", subject.GetProperty("applicationScopeId").GetString()); Assert.Equal("net-integration-user", subject.GetProperty("endUserId").GetString()); });
        });
        var final = await InspectAsync(ct); Assert.Equal(7, final.GetProperty("closes").GetInt32());
        Assert.DoesNotContain(events, item => item.Name == "turn.error" && item.Data.GetRawText().Contains("/server/private", StringComparison.Ordinal));
    }

    private static async Task NativeMcpAsync(string name, McpClient connection, string text, ConcurrentQueue<AgentEvent> events, CancellationToken ct)
    {
        var discovered = Assert.Single(await connection.ListToolsAsync(["echo"], ct));
        var declaration = Json(new { name = "NativeMcp", description = "Call the one explicitly approved synthetic native MCP echo", parameters = new { text = new { type = "string" } }, readOnly = false, timeoutMs = 10000 });
        var digest = WireJson.DomainDigest("tansr.sdk2.client-tool.v1", WireJson.EncodeControl(declaration));
        var bridge = Assert.Single(McpToolAdapter.CreateTools(connection, [new McpToolBinding("NativeMcp", "echo", digest, discovered)]));
        var delegateCalls = 0;
        WindowsBusinessTool[] tools = [new(bridge.Name, bridge.DefinitionDigest, async (arguments, token) =>
        {
            Interlocked.Increment(ref delegateCalls);
            return await bridge.Invoke(arguments, token);
        })];
        await InContextAsync(await Context.OpenAsync("mcp-" + name, ["NativeMcp"], ct, tools, Json(new[] { declaration })), async context =>
        {
            await PlanAsync("NativeMcp", new { text }, ct: ct);
            await TurnAsync(context.Session, "NET_NATIVE_MCP_" + name, events, ct);
            Assert.Equal(1, context.Executions);
            Assert.Equal(1, delegateCalls);
            Assert.True(context.AuthorizationChecks >= 3, "Original guards must still revalidate before claim, execution and native delegation.");
            var history = await context.Session.GetHistoryAsync(ct);
            Assert.Contains("NATIVE_" + name.ToUpperInvariant() + "_MCP_", history.GetRawText());
        });
    }

    private static async Task InContextAsync(Context context, Func<Context, Task> action)
    {
        Exception? primary = null;
        try { await action(context); } catch (Exception error) { primary = error; }
        try { await context.DisposeAsync(); }
        catch (Exception cleanup)
        {
            if (primary is null) throw;
            throw new AggregateException("Trusted extension scenario failed; the first exception is the original failure.", primary, cleanup);
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static async Task<SessionRunResult> TurnAsync(AgentSession session, string prompt, ConcurrentQueue<AgentEvent> events, CancellationToken ct, bool denyWrites = false)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = session.SendAndObserveAsync(prompt, observer: (item, token) => ObserveAsync(session, events, item, token, denyWrites), cancellationToken: turn.Token);
        while (!running.IsCompleted)
        {
            var failed = Path.Combine(Root, "host-failure.json");
            if (File.Exists(failed))
            {
                var diagnostic = await File.ReadAllTextAsync(failed, ct);
                turn.Cancel(); try { await running; } catch { }
                throw new InvalidOperationException(diagnostic);
            }
            await Task.WhenAny(running, Task.Delay(20, ct));
            ct.ThrowIfCancellationRequested();
        }
        var result = await running;
        await CommandAsync(new { action = "settle", sessionId = session.Id }, ct);
        return result;
    }
    private static Task<JsonElement> PlanAsync(string? tool, object? args, string hookMode = "allow", bool promptBlocked = false, CancellationToken ct = default)
        => CommandAsync(tool == null ? new { action = "plan", hookMode, promptBlocked } : (object)new { action = "plan", tool, args, hookMode, promptBlocked }, ct);
    private static Task<JsonElement> InspectAsync(CancellationToken ct) => CommandAsync(new { action = "inspect" }, ct);
    private static async Task ObserveAsync(AgentSession session, ConcurrentQueue<AgentEvent> events, AgentEvent item, CancellationToken ct, bool denyWrites = false)
    {
        if (item.Name == "server.permission.request")
        {
            var permission = item.Data.GetProperty("payload");
            await session.PermissionAsync(permission.GetProperty("requestId").GetString()!, permission.GetProperty("digest").GetString()!, !denyWrites || permission.GetProperty("name").GetString() != "Write", ct);
        }
        events.Enqueue(item);
    }
    private static bool BelongsTo(AgentEvent item, AgentSession session) => item.Data.TryGetProperty("sessionId", out var id) && id.GetString() == session.Id;
    private static async Task WaitNoticesAsync(AgentSession session, ConcurrentQueue<AgentEvent> events, int expected, CancellationToken ct)
    {
        // SendAndObserve has completed and released its sole pump. A resident child
        // can still need original device permission after the parent turn ends.
        // Resume at the callback-consumed cursor: SessionRun may observe later frames
        // while tearing down without handing them to this application callback.
        var last = events.LastOrDefault(item => item.Id != null && BelongsTo(item, session)); Assert.NotNull(last);
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = session.ObserveAsync((item, token) => ObserveAsync(session, events, item, token),
            new EventStreamOptions { LastEventId = last.Id, StopOnGap = true, Reconnect = false }, observation.Token);
        Exception? primary = null;
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (pump.IsCompleted) { await pump; throw new InvalidOperationException("Resident child observation ended before settlement."); }
                var facts = await InspectAsync(ct);
                if (facts.GetProperty("notices").EnumerateArray().Count(item => item.GetProperty("channel").GetString() == "settled") >= expected &&
                    events.Count(item => item.Name == "agent.settled" && BelongsTo(item, session)) >= expected) break;
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(10)) throw new TimeoutException("Original child did not settle within 10 seconds: " + facts.GetRawText());
                await Task.WhenAny(pump, Task.Delay(500, ct)); ct.ThrowIfCancellationRequested();
            }
        }
        catch (Exception error) { primary = error; }
        finally
        {
            observation.Cancel();
            try { await pump; }
            catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
            catch (Exception error) { primary = primary == null ? error : new AggregateException("Child observation and cleanup failed.", primary, error); }
        }
        if (primary != null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private static async Task<JsonElement> CommandAsync(object request, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        { writer.WriteStartObject(); writer.WriteString("id", id); foreach (var property in Json(request).EnumerateObject()) property.WriteTo(writer); writer.WriteEndObject(); }
        var path = Path.Combine(Root, "host-commands", id);
        await File.WriteAllBytesAsync(path + ".tmp", buffer.ToArray(), ct); File.Move(path + ".tmp", path + ".json");
        var response = Path.Combine(Root, "host-responses", id + ".json");
        while (!File.Exists(response))
        {
            var failed = Path.Combine(Root, "host-failure.json");
            if (File.Exists(failed)) throw new InvalidOperationException(await File.ReadAllTextAsync(failed, ct));
            await Task.Delay(10, ct);
        }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(response, ct));
        Assert.Equal(id, document.RootElement.GetProperty("id").GetString()); return document.RootElement.GetProperty("value").Clone();
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TansrClient _client; private readonly DeviceSessionHost _host; private readonly SqliteExecutorJournal _journal;
        internal AgentSession Session { get; }
        internal WindowsWorkspace Workspace { get; }
        internal int Executions, AuthorizationChecks;
        private Context(TansrClient client, AgentSession session, WindowsWorkspace workspace, SqliteExecutorJournal journal, DeviceSessionHost host)
        { _client = client; Session = session; Workspace = workspace; _journal = journal; _host = host; }
        internal static async Task<Context> OpenAsync(string name, string[] tools, CancellationToken ct,
            IReadOnlyList<WindowsBusinessTool>? businessTools = null, JsonElement? clientTools = null, long? maxTokens = null, string? resumeSessionId = null)
        {
            var directory = Path.Combine(Root, "dotnet-" + name); Directory.CreateDirectory(directory);
            var work = Path.Combine(directory, "workspace"); Directory.CreateDirectory(work);
            var client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri(Required("TANSR_SERVE_TRUSTED_URL")),
                AllowInsecureLoopback = true,
                TokenProvider = _ => Task.FromResult(Required("TANSR_SERVE_TEST_TOKEN")),
                PrincipalProvider = () => "net-trusted-app/net-integration-user",
                ExecutionScopeProvider = () => Scope,
                RequestTimeout = TimeSpan.FromSeconds(10),
                StreamIdleTimeout = TimeSpan.FromSeconds(20),
                MaxReconnectAttempts = 0
            });
            var session = await client.CreateSessionAsync(new CreateSessionOptions { Tools = tools, ClientTools = clientTools, MaxTokens = maxTokens, ResumeSessionId = resumeSessionId }, ct);
            var writable = tools.Contains("Write", StringComparer.Ordinal);
            if (writable)
            {
                // This isolated fixture owns every writer. The real backend must advertise Write
                // before the real child whitelist and adjudicator can reject its proposed writes.
                using var identity = WindowsIdentity.GetCurrent();
                var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
                security.SetOwner(identity.User!);
                security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(work).SetAccessControl(security);
            }
            var workspace = new WindowsWorkspace(work, new WindowsWorkspaceOptions { AllWritersCooperate = writable });
            var journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
            { Path = Path.Combine(directory, "journal.sqlite"), Mode = StorageOpenMode.Create, ExecutorId = "net-trusted-pc", ApplicationScopeId = "net-trusted-app", EndUserId = "net-integration-user", ReadContext = () => Scope }, ct);
            Context? context = null;
            var backend = new WindowsExecutorBackend("net-trusted-pc", [new WindowsExecutorWorkspace("work", "1", workspace)], tools: businessTools);
            var observedBackend = new ObservedBackend(backend, () => { if (context != null) Interlocked.Increment(ref context.Executions); });
            var host = new DeviceSessionHost(new ExecutionClient(client), observedBackend, journal,
                new DeviceSessionOptions { SessionId = session.Id, WorkspaceId = "work", RequestedTools = tools },
                (_, _) => { if (context != null) Interlocked.Increment(ref context.AuthorizationChecks); return Task.CompletedTask; });
            context = new Context(client, session, workspace, journal, host);
            await host.StartAsync(ct); Assert.Equal(DeviceSessionState.Ready, host.State);
            await File.WriteAllTextAsync(Path.Combine(directory, "bound-capabilities.json"), host.Capabilities!.Value.GetRawText(), ct);
            if (writable)
                foreach (var expected in new[] { "Task", "Write" })
                    Assert.Contains(host.Capabilities.Value.GetProperty("effectiveTools").EnumerateArray(),
                        tool => tool.GetProperty("name").GetString() == expected && tool.GetProperty("available").GetBoolean());
            return context;
        }
        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            async Task Run(Func<Task> action) { try { await action(); } catch (Exception error) { failures.Add(error); } }
            await Run(async () => { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await Session.CloseAsync(ct.Token); await CommandAsync(new { action = "settle", sessionId = Session.Id }, ct.Token); });
            await Run(_host.StopAsync); await Run(_journal.CloseAsync); _host.Dispose(); Workspace.Dispose(); _client.Dispose();
            if (failures.Count != 0) throw new AggregateException("Trusted extension cleanup failed.", failures);
        }
    }

    private sealed class ObservedBackend(IExecutionBackend inner, Action onExecute) : IExecutionBackend
    {
        public JsonElement Registration => inner.Registration;
        public Task<JsonElement> ExecuteAsync(JsonElement operation, Func<CancellationToken, Task> guard, CancellationToken cancellationToken)
        {
            onExecute();
            return inner.ExecuteAsync(operation, guard, cancellationToken);
        }
    }
}
