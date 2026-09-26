using System.Collections.Concurrent;
using System.Diagnostics;
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
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.IntegrationTests;

public sealed class ServeFileOperationsTests(ITestOutputHelper output)
{
    private static readonly string[] Names = ["Read", "Write", "Edit", "List", "Glob", "Grep", "BusinessLookup"];
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Scope => Json(new { applicationScopeId = "net-files-app", endUserId = "net-integration-user", authorizationRevision = "1" });
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required; use the Serve runner.");
    private static string Root => Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "file-operations");

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task OriginalFileToolsExecuteOnWindowsAndRejectChangedAuthorityWithoutHostFallback()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Root, "fixture.json"), ct));
        var declaration = fixture.RootElement.GetProperty("declaration").Clone();
        var digest = fixture.RootElement.GetProperty("definitionDigest").GetString()!;
        var privatePath = fixture.RootElement.GetProperty("privatePath").GetString()!;
        var directory = Path.Combine(Root, "dotnet"); var work = Path.Combine(directory, "workspace"); Directory.CreateDirectory(work);
        MakePrivate(work);
        using var workspace = new WindowsWorkspace(work, new WindowsWorkspaceOptions { AllWritersCooperate = true });
        using var controllerHttp = Http("controller"); using var deviceHttp = Http("executor");
        using var controller = new TansrClient(Options(), controllerHttp); using var device = new TansrClient(Options(), deviceHttp);
        var execution = new ExecutionClient(device); var sessions = new List<AgentSession>(); var hosts = new List<DeviceSessionHost>();
        var journals = new List<SqliteExecutorJournal>(); var operations = new ConcurrentDictionary<string, JsonElement>();
        var events = new ConcurrentQueue<AgentEvent>(); var businessCalls = 0; var turn = 0;
        Exception? primary = null; var cleanup = new List<Exception>(); string? ownedJunction = null;
        async Task<(AgentSession Session, DeviceSessionHost Host, SqliteExecutorJournal Journal)> OpenAsync(string name, string definition)
        {
            var session = await controller.CreateSessionAsync(new CreateSessionOptions { Tools = Names, ClientTools = Json(new[] { declaration }) }, ct); sessions.Add(session);
            var backend = new WindowsExecutorBackend("net-files-pc", [new WindowsExecutorWorkspace("work", "1", workspace)],
                [new WindowsBusinessTool("BusinessLookup", definition, (args, token) =>
                {
                    token.ThrowIfCancellationRequested(); Assert.Equal("synthetic", args.GetProperty("id").GetString());
                    Interlocked.Increment(ref businessCalls);
                    return Task.FromResult(Json(new { status = "ok", content = new[] { new { t = "text", text = "NATIVE_ORDER_中文_42" } } }));
                })]);
            var journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
            { Path = Path.Combine(directory, name + ".sqlite"), Mode = StorageOpenMode.Create, ExecutorId = "net-files-pc", ApplicationScopeId = "net-files-app", EndUserId = "net-integration-user", ReadContext = () => Scope }, ct);
            journals.Add(journal);
            var host = new DeviceSessionHost(new ExecutionClient(controller), execution, backend, journal,
                new DeviceSessionOptions { SessionId = session.Id, WorkspaceId = "work", RequestedTools = Names }, (operation, token) =>
                {
                    token.ThrowIfCancellationRequested(); Assert.Equal(session.Id, operation.GetProperty("sessionId").GetString());
                    Assert.Equal("net-files-pc", operation.GetProperty("binding").GetProperty("target").GetProperty("executorId").GetString());
                    Assert.Equal("work", operation.GetProperty("binding").GetProperty("target").GetProperty("workspaceId").GetString());
                    operations.TryAdd(operation.GetProperty("operationId").GetString()!, operation.Clone()); return Task.CompletedTask;
                });
            hosts.Add(host); await host.StartAsync(ct); Assert.Equal(DeviceSessionState.Ready, host.State);
            return (session, host, journal);
        }
        async Task<JsonElement> ToolAsync(AgentSession session, string name, object args, bool succeeds)
        {
            var id = "file-" + ++turn;
            using var run = session.StartRun("NET_FILES:" + Json(new { id, name, args }).GetRawText(),
                new SessionRunOptions { Timeout = TimeSpan.FromSeconds(15) }, async (item, token) =>
                {
                    events.Enqueue(item);
                    if (item.Name == "server.permission.request")
                    {
                        var permission = item.Data.GetProperty("payload");
                        await session.PermissionAsync(permission.GetProperty("requestId").GetString()!, permission.GetProperty("digest").GetString()!, true, token);
                    }
                }, ct);
            var result = await run.Completion; Assert.False(result.WasAborted); Assert.Equal("completed", result.Reason);
            var inspected = await CommandAsync(new { action = "inspect" }, ct);
            var original = Assert.Single(inspected.GetProperty("results").EnumerateArray(), item => item.GetProperty("id").GetString() == id).GetProperty("result");
            var error = original.TryGetProperty("isError", out var failed) && failed.GetBoolean();
            Assert.True(error == !succeeds, $"{name} expected success={succeeds}; actual original result: {original.GetRawText()}");
            Assert.True(inspected.GetProperty("hostSentinelUnchanged").GetBoolean());
            return original.Clone();
        }
        try
        {
            var good = await OpenAsync("original", digest);
            const string originalText = "NATIVE FILE alpha\nneedle 中文 😀\n";
            Assert.False(File.Exists(Path.Combine(work, "note.txt")));
            await ToolAsync(good.Session, "Write", new { file_path = "/workspace/work/note.txt", contents = originalText }, true);
            Assert.Equal(originalText, Encoding.UTF8.GetString(workspace.Read("note.txt")));
            var creationLedger = await good.Journal.OperationsAsync(cancellationToken: ct);
            var missingTarget = creationLedger.First(op =>
                op.GetProperty("request").GetProperty("operation").GetString() == "fs.inspect" &&
                op.GetProperty("request").GetProperty("args").GetProperty("path").GetString() == "note.txt" &&
                !op.GetProperty("request").GetProperty("args").GetProperty("followLinks").GetBoolean());
            var observedMissing = await good.Journal.ClaimAsync(missingTarget, ct);
            Assert.Equal(ExecutorJournalClaimStatus.Completed, observedMissing.Status); Assert.NotNull(observedMissing.Receipt);
            Assert.Equal("failed", observedMissing.Receipt!.Value.GetProperty("status").GetString());
            Assert.Equal("ENOENT", observedMissing.Receipt.Value.GetProperty("errorCode").GetString());
            Assert.Single(creationLedger, op => op.GetProperty("request").GetProperty("operation").GetString() == "fs.write");
            Assert.Contains("needle", (await ToolAsync(good.Session, "Read", new { file_path = "/workspace/work/note.txt" }, true)).GetRawText());
            await ToolAsync(good.Session, "Edit", new { file_path = "/workspace/work/note.txt", old_string = "alpha", new_string = "updated" }, true);
            Assert.Equal(originalText.Replace("alpha", "updated", StringComparison.Ordinal), Encoding.UTF8.GetString(workspace.Read("note.txt")));
            Assert.Contains("note.txt", (await ToolAsync(good.Session, "List", new { path = "/workspace/work" }, true)).GetRawText());
            Assert.Contains("note.txt", (await ToolAsync(good.Session, "Glob", new { path = "/workspace/work", pattern = "*.txt" }, true)).GetRawText());
            Assert.Contains("needle", (await ToolAsync(good.Session, "Grep", new { path = "/workspace/work", pattern = "needle" }, true)).GetRawText());
            Assert.Contains("NATIVE_ORDER", (await ToolAsync(good.Session, "BusinessLookup", new { id = "synthetic" }, true)).GetRawText());
            Assert.Equal(1, businessCalls);
            var ledger = await good.Journal.OperationsAsync(cancellationToken: ct);
            Assert.Contains(ledger, op => op.GetProperty("request").GetProperty("operation").GetString() == "fs.write");
            Assert.Contains(ledger, op => op.GetProperty("request").GetProperty("operation").GetString() == "fs.list");
            var business = Assert.Single(ledger, op => op.GetProperty("request").GetProperty("operation").GetString() == "tool.invoke");
            var saved = await good.Journal.ClaimAsync(business, ct); Assert.Equal(ExecutorJournalClaimStatus.Completed, saved.Status); Assert.NotNull(saved.Receipt);
            var replayed = await execution.SubmitAsync(saved.Receipt!.Value, ct);
            Assert.Equal("completed", replayed.GetProperty("status").GetString()); Assert.Equal(1, businessCalls);
            Assert.Equal(ledger.Count, (await good.Journal.OperationsAsync(cancellationToken: ct)).Count);
            foreach (var path in new[] { privatePath, "/workspace/work/../../serve-private/sentinel.txt", @"\\localhost\C$\not-a-device-file", "/workspace/work/note.txt:secret" })
                await ToolAsync(good.Session, "Read", new { file_path = path }, false);
            await ToolAsync(good.Session, "Write", new { file_path = privatePath, contents = "must-not-replace-host" }, false);
            var junction = Path.Combine(work, "escape");
            CreateJunction(junction, fixture.RootElement.GetProperty("privateDirectory").GetString()!);
            ownedJunction = junction;
            await ToolAsync(good.Session, "Read", new { file_path = "/workspace/work/escape/sentinel.txt" }, false);
            Assert.True((await CommandAsync(new { action = "inspect" }, ct)).GetProperty("hostSentinelUnchanged").GetBoolean());
            // Stop the first device before intentionally creating another connection of the same executor.
            await good.Session.CloseAsync(ct); sessions.Remove(good.Session);
            await good.Host.StopAsync(); hosts.Remove(good.Host); good.Host.Dispose();
            var changed = await OpenAsync("changed-definition", new string('b', 64));
            var beforeChanged = operations.Count;
            var capabilities = await new ExecutionClient(controller).GetExecutionCapabilitiesAsync(changed.Session.Id, ct);
            // The original capability directory retains unavailable tools for explanation;
            // a changed definition must remove invocation authority, not the directory entry.
            var unavailable = Assert.Single(capabilities.GetProperty("effectiveTools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "BusinessLookup");
            Assert.False(unavailable.GetProperty("available").GetBoolean());
            Assert.Equal("bound-device", unavailable.GetProperty("executionKind").GetString());
            Assert.Equal("capability_unconfirmed", unavailable.GetProperty("unavailableReason").GetString());
            await ToolAsync(changed.Session, "BusinessLookup", new { id = "synthetic" }, false);
            Assert.Equal(beforeChanged, operations.Count); Assert.Equal(1, businessCalls);
            Assert.Empty(await changed.Journal.OperationsAsync(cancellationToken: ct));
            await CommandAsync(new { action = "revoke" }, ct);
            var beforeRevoked = operations.Count;
            var failure = await Record.ExceptionAsync(() => changed.Session.SendAndObserveAsync("NET_FILES:" + Json(new { id = "revoked-read", name = "Read", args = new { file_path = "/workspace/work/note.txt" } }).GetRawText(), new SessionRunOptions { Timeout = TimeSpan.FromSeconds(10) }, cancellationToken: ct));
            if (failure != null) Assert.IsAssignableFrom<TansrException>(failure);
            Assert.Equal(beforeRevoked, operations.Count); Assert.Equal(1, businessCalls);
            var final = await CommandAsync(new { action = "inspect" }, ct); Assert.True(final.GetProperty("hostSentinelUnchanged").GetBoolean());
            await File.WriteAllTextAsync(Path.Combine(directory, "file-evidence.json"), final.GetRawText(), ct);
            output.WriteLine("Real Serve -> Windows Write/Read/Edit/List/Glob/Grep and native business delegate; original receipt replay did not execute again; host-path, ADS, UNC, traversal, junction and changed-definition/revocation attempts rejected. All actual execution targets are the bound Windows workspace.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            foreach (var session in sessions) try { await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(8)); } catch (Exception error) { cleanup.Add(error); }
            foreach (var host in hosts) { try { await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(8)); } catch (Exception error) { cleanup.Add(error); } finally { host.Dispose(); } }
            foreach (var journal in journals) { try { await journal.CloseAsync(); } catch (Exception error) { cleanup.Add(error); } finally { journal.Dispose(); } }
            if (ownedJunction != null)
            {
                try
                {
                    var full = Path.GetFullPath(ownedJunction);
                    Assert.StartsWith(Path.GetFullPath(work) + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
                    Assert.True(new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint));
                    Directory.Delete(full); // Remove only the owned junction, never recurse into its target.
                }
                catch (Exception error) { cleanup.Add(error); }
            }
            await File.WriteAllLinesAsync(Path.Combine(directory, "events.jsonl"), events.Select(item => Json(new { item.Name, item.Data }).GetRawText()));
        }
        if (primary != null) { if (cleanup.Count > 0) throw new AggregateException(new[] { primary }.Concat(cleanup)); ExceptionDispatchInfo.Capture(primary).Throw(); }
        if (cleanup.Count > 0) throw new AggregateException(cleanup);
    }

    private static TansrClientOptions Options() => new()
    {
        BaseUri = new Uri(Required("TANSR_SERVE_FILES_URL")),
        AllowInsecureLoopback = true,
        TokenProvider = _ => Task.FromResult(Required("TANSR_SERVE_TEST_TOKEN")),
        PrincipalProvider = () => "net-files-app/net-integration-user",
        ExecutionScopeProvider = () => Scope,
        RequestTimeout = TimeSpan.FromSeconds(10),
        StreamIdleTimeout = TimeSpan.FromSeconds(20),
        MaxReconnectAttempts = 0
    };
    private static HttpClient Http(string role)
    { var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }); http.DefaultRequestHeaders.Add("x-net-role", role); return http; }
    private static void MakePrivate(string path)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        using var user = WindowsIdentity.GetCurrent(); security.SetOwner(user.User!);
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static void CreateJunction(string link, string target)
    {
        Assert.False(Directory.Exists(link));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000)) { process.Kill(true); throw new TimeoutException("junction fixture timeout"); }
        Assert.True(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
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
            if (File.Exists(Path.Combine(Root, "host-failure.json"))) throw new InvalidOperationException(await File.ReadAllTextAsync(Path.Combine(Root, "host-failure.json"), ct));
            await Task.Delay(10, ct);
        }
        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(response, ct)); return result.RootElement.GetProperty("value").Clone();
    }
}
