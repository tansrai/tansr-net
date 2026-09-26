using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Hosting;

public sealed class WindowsBackgroundHostOptions
{
    public WindowsBackgroundHostOptions(string artifactDirectory, Func<JsonElement, WindowsWorkspace, WindowsProcessRequest> processFactory)
    { ArtifactDirectory = artifactDirectory; ProcessFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory)); }
    /// <summary>明确接受尚未稳定发行的 Serve 候选合同；与服务端和本包固定候选一致。</summary>
    public string CandidateRevision { get; set; } = "";
    /// <summary>可信宿主提供的实际本地目录，不接收模型路径。仅删除本实例持有的工件句柄。</summary>
    public string ArtifactDirectory { get; }
    /// <summary>仅将已授权 command 解析为批准的可执行文件、参数与环境；cwd 仍由绑定工作区解析。</summary>
    public Func<JsonElement, WindowsWorkspace, WindowsProcessRequest> ProcessFactory { get; }
    public int MaximumTasks { get; set; } = 16;
    public int MaximumArtifactBytes { get; set; } = 8 * 1024 * 1024;
    public TimeSpan ArtifactTimeToLive { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan MaximumRuntime { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// 原 tool.invoke 的受控 Windows 后台执行适配，不是第二套执行账本。
/// 必须接 ExecutionHost 及耐久 journal；启动回执由原 journal 落盘，宿主重开不接管旧 PID、不重跑旧操作。
/// </summary>
public sealed class WindowsBackgroundHost : IDisposable
{
    public static string CandidateRevision => TerminalCandidateContract.Revision;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> tasks = new(StringComparer.Ordinal);
    private readonly List<SafeFileHandle> pins = new();
    private readonly WindowsProcessExecutor executor;
    private readonly Func<JsonElement, WindowsWorkspace, WindowsProcessRequest> processFactory;
    private readonly int maximumTasks, maximumBytes;
    private readonly TimeSpan ttl, maximumRuntime;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenRegistration parentLifetime;
    private bool closed;
    private Task? closing;

    public WindowsBackgroundHost(WindowsBackgroundHostOptions options, CancellationToken lifetimeCancellation = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.CandidateRevision != CandidateRevision || options.MaximumTasks < 1 || options.MaximumTasks > 64 ||
            options.MaximumArtifactBytes < 1 || options.MaximumArtifactBytes > 8388608 || options.ArtifactTimeToLive <= TimeSpan.Zero ||
            options.ArtifactTimeToLive > TimeSpan.FromDays(1) || options.MaximumRuntime <= TimeSpan.Zero || options.MaximumRuntime > TimeSpan.FromDays(1))
            throw new ArgumentException("后台候选合同或资源限额无效。", nameof(options));
        lifetimeCancellation.ThrowIfCancellationRequested();
        WindowsDuplexProcessNative.ValidatePath(options.ArtifactDirectory);
        maximumTasks = options.MaximumTasks; maximumBytes = options.MaximumArtifactBytes; ttl = options.ArtifactTimeToLive;
        maximumRuntime = options.MaximumRuntime; processFactory = options.ProcessFactory;
        executor = new WindowsProcessExecutor(maximumTasks);
        RuntimeInstanceId = Guid.NewGuid().ToString("D");
        try
        {
            pins.Add(NativeWorkspace.OpenDrive(options.ArtifactDirectory.Substring(0, 3)));
            foreach (string part in options.ArtifactDirectory.Substring(3).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                pins.Add(NativeWorkspace.OpenDirectory(pins[pins.Count - 1], part));
            if (!string.Equals(NativeWorkspace.FinalPath(pins[pins.Count - 1]).TrimEnd('\\'), @"\\?\" + options.ArtifactDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new WindowsWorkspaceException("path_identity_unavailable");
            // SDK 不擅自更改 ACL；输出目录应由开发者配置为本用户私有。
            NativeWorkspace.ValidatePrivatePermissions(pins[pins.Count - 1]);
            var registration = lifetimeCancellation.Register(() => { _ = CloseAsync(); });
            lock (gate) { if (!closed) parentLifetime = registration; else registration.Dispose(); }
        }
        catch { foreach (var pin in pins) pin.Dispose(); lifetime.Dispose(); throw; }
    }

    public string RuntimeInstanceId { get; }
    public WindowsBusinessTool CreateTool() => new(TerminalCandidateContract.BackgroundToolName,
        TerminalCandidateContract.BackgroundToolDefinitionSha256, InvokeAsync);

    private async Task<JsonElement> InvokeAsync(JsonElement request, WindowsBusinessToolContext context, CancellationToken ct)
        => await InvokeCoreAsync(request, context, true, ct).ConfigureAwait(false);

    internal Task<JsonElement> InvokeSandboxAsync(JsonElement request, WindowsBusinessToolContext context, CancellationToken ct)
        => InvokeCoreAsync(request, context, false, ct);

    private async Task<JsonElement> InvokeCoreAsync(JsonElement request, WindowsBusinessToolContext context, bool ownProfile, CancellationToken ct)
    {
        Check();
        if (ownProfile) ValidateProfile(context.Operation, request);
        else TerminalCandidateContract.Validate("BackgroundRequest", request);
        await context.GuardAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        await ExpireAsync().ConfigureAwait(false);
        string action = Text(request, "action"); JsonElement response;
        if (action == "launch")
        {
            var entry = await LaunchAsync(request, context, ct).ConfigureAwait(false);
            response = Response(action, writer => { writer.WritePropertyName("task"); WriteState(writer, entry); });
        }
        else
        {
            var identity = request.GetProperty("identity"); Entry? entry;
            lock (gate)
            {
                Check(); tasks.TryGetValue(Text(identity, "taskId"), out entry);
                if (entry != null && (!Same(entry.Identity, identity) || entry.ScopeKey != ScopeKey(context.Operation))) throw Rejected("EACCES");
            }
            if (entry == null || Text(identity, "runtimeInstanceId") != RuntimeInstanceId)
            {
                if (action != "query") throw Rejected("ESTALE");
                response = Response(action, writer => { writer.WritePropertyName("task"); WriteUnknown(writer, identity); });
            }
            else if (action == "query" || action == "cancel")
            {
                if (action == "cancel") await CancelAsync(entry).ConfigureAwait(false);
                response = Response(action, writer => { writer.WritePropertyName("task"); WriteState(writer, entry); });
            }
            else
            {
                if (!Same(entry.Artifact, request.GetProperty("artifact"))) throw Rejected("EACCES");
                if (action == "outputRead")
                {
                    using var memory = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(memory))
                    {
                        writer.WriteStartObject(); WriteHeader(writer, action); writer.WritePropertyName("identity"); entry.Identity.WriteTo(writer);
                        writer.WritePropertyName("artifact"); entry.Artifact.WriteTo(writer);
                        await entry.Output.WriteReadAsync(writer, long.Parse(Text(request, "offset"), CultureInfo.InvariantCulture), Math.Min(request.GetProperty("length").GetInt32(), 16384)).ConfigureAwait(false);
                        writer.WriteEndObject();
                    }
                    response = WireJson.Parse(memory.ToArray(), 32768);
                }
                else
                {
                    lock (gate) if (entry.State == "running") throw Rejected("EACCES");
                    bool deleted = entry.Output.Deleted; await entry.Output.DeleteAsync().ConfigureAwait(false);
                    response = Response(action, writer =>
                    {
                        writer.WritePropertyName("identity"); entry.Identity.WriteTo(writer); writer.WritePropertyName("artifact"); entry.Artifact.WriteTo(writer);
                        writer.WriteString("status", deleted ? "already_deleted" : "deleted");
                    });
                }
            }
        }
        TerminalCandidateContract.Validate("BackgroundResponse", response);
        return Object(writer =>
        {
            writer.WriteString("status", "ok"); writer.WritePropertyName("content"); writer.WriteStartArray(); writer.WriteStartObject();
            writer.WriteString("t", "text"); writer.WriteString("text", response.GetRawText()); writer.WriteEndObject(); writer.WriteEndArray();
        });
    }

    private async Task<Entry> LaunchAsync(JsonElement request, WindowsBusinessToolContext context, CancellationToken ct)
    {
        int timeout = request.GetProperty("timeoutMs").GetInt32();
        if (timeout > maximumRuntime.TotalMilliseconds || Text(request, "mode") == "background" && request.TryGetProperty("foregroundTimeoutMs", out _) ||
            request.TryGetProperty("foregroundTimeoutMs", out var foreground) && foreground.GetInt32() > timeout) throw Rejected("EACCES");
        var selected = processFactory(request.Clone(), context.Workspace) ?? throw Rejected("ENOTSUP");
        var process = new WindowsProcessRequest(selected.TrustedExecutablePath, selected.Arguments,
            () => context.Workspace.AcquireProcessDirectory(Text(request, "cwd")))
        {
            ExpectedExecutableSha256 = selected.ExpectedExecutableSha256,
            ValidateBeforeStart = selected.ValidateBeforeStart,
            Timeout = TimeSpan.FromMilliseconds(timeout),
            MaxOutputBytes = maximumBytes,
            MaxPendingChunks = selected.MaxPendingChunks,
            ChunkBytes = selected.ChunkBytes,
            OutputCallbackTimeout = selected.OutputCallbackTimeout,
            CleanupTimeout = selected.CleanupTimeout,
            DrainAfterOutputLimit = true,
        };
        foreach (var variable in selected.Environment) process.Environment.Add(variable.Key, variable.Value);
        await context.GuardAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        Entry entry; bool created = false;
        lock (gate)
        {
            Check();
            var original = tasks.Values.FirstOrDefault(value => Text(value.Identity, "operationId") == Text(context.Operation, "operationId"));
            if (original != null)
            {
                if (!Same(original.Operation, context.Operation)) throw Rejected("EACCES");
                entry = original;
            }
            else
            {
                if (tasks.Count >= maximumTasks) throw Rejected("EFBIG");
                string taskId = Guid.NewGuid().ToString("D");
                var identity = Object(writer =>
                {
                    writer.WriteString("taskId", taskId); writer.WriteString("operationId", Text(context.Operation, "operationId"));
                    writer.WriteString("requestDigest", Text(context.Operation, "digest")); writer.WriteString("runtimeInstanceId", RuntimeInstanceId);
                    writer.WriteString("processInstanceId", Guid.NewGuid().ToString("D"));
                });
                var artifact = ArtifactFor(identity);
                var output = new WindowsBackgroundArtifact(pins[pins.Count - 1], ".tansr-sdk-background-" + RuntimeInstanceId + "-" + taskId, maximumBytes);
                entry = new Entry(identity, artifact, context.Operation.Clone(), ScopeKey(context.Operation), output, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
                tasks.Add(taskId, entry); created = true;
            }
        }
        using var launchCancellation = ct.Register(() => RequestCancel(entry));
        if (created)
        {
            process.Started = pid => { lock (gate) { entry.ProcessId = pid; entry.Startup.TrySetResult(true); } };
            process.OutputTruncated = entry.Output.Truncate;
            _ = RunAsync(entry, process);
        }
        bool witnessed = await entry.Startup.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!witnessed) throw Rejected("resource_start_failed");
        Check(); return entry;
    }

    private async Task RunAsync(Entry entry, WindowsProcessRequest request)
    {
        try
        {
            var result = await executor.ExecuteAsync(request, (chunk, _) => { entry.Output.Capture(chunk); return Task.CompletedTask; }, entry.Stop.Token).ConfigureAwait(false);
            if (!result.OutputComplete) entry.Output.Truncate();
            await entry.Output.FinishAsync().ConfigureAwait(false);
            lock (gate)
            {
                entry.ExitCode = result.ExitCode;
                entry.State = !result.CleanupConfirmed || !result.OutputDeliverySettled ? "unknown" : !result.Started ? "failed" :
                    result.Termination == WindowsProcessTermination.Canceled || result.Termination == WindowsProcessTermination.TimedOut ? "cancelled" :
                    result.Termination == WindowsProcessTermination.Exited && result.ExitCode == 0 ? "completed" : "failed";
            }
        }
        catch
        {
            entry.Output.Truncate();
            lock (gate) entry.State = entry.ProcessId.HasValue ? "unknown" : "failed";
            await entry.Output.FinishAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (gate) { entry.EndedAt = DateTimeOffset.UtcNow; entry.Startup.TrySetResult(entry.ProcessId.HasValue); entry.Stop.Dispose(); }
            entry.Done.TrySetResult(true);
        }
    }

    private void RequestCancel(Entry entry) { lock (gate) if (!entry.EndedAt.HasValue) entry.Stop.Cancel(); }
    private async Task CancelAsync(Entry entry) { RequestCancel(entry); await entry.Done.Task.ConfigureAwait(false); }

    private async Task ExpireAsync()
    {
        Entry[] expired;
        lock (gate) expired = tasks.Values.Where(entry => entry.EndedAt.HasValue && DateTimeOffset.UtcNow - entry.EndedAt.Value >= ttl).ToArray();
        foreach (var entry in expired)
        {
            await entry.Output.DeleteAsync().ConfigureAwait(false);
            lock (gate) tasks.Remove(Text(entry.Identity, "taskId"));
        }
    }

    public Task CloseAsync()
    {
        lock (gate)
        {
            if (closing != null) return closing;
            closed = true; lifetime.Cancel();
            var entries = tasks.Values.ToArray();
            return closing = Task.Run(() => CloseCoreAsync(entries));
        }
    }
    private async Task CloseCoreAsync(Entry[] entries)
    {
        var failures = new List<Exception>();
        try
        {
            await Task.WhenAll(entries.Select(entry => entry.Done.Task)).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                try { await entry.Output.DeleteAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
                lock (gate) if (entry.State == "unknown") failures.Add(new IOException("后台进程资源收尾尚未确认。"));
            }
        }
        finally { parentLifetime.Dispose(); foreach (var pin in pins) pin.Dispose(); lifetime.Dispose(); }
        if (failures.Count != 0) throw new AggregateException("background_cleanup_unavailable", failures);
    }
    /// <summary>请求停止；应用关闭须 await CloseAsync 才能确认 Job 与工件已回收。</summary>
    public void Dispose() { _ = CloseAsync(); }

    private void Check() { lock (gate) if (closed) throw Rejected("ESTALE"); }
    private static void ValidateProfile(JsonElement operation, JsonElement request)
    {
        ExecutionJson.Operation(operation);
        var args = operation.GetProperty("request").GetProperty("args");
        if (Text(operation, "toolName") != "Shell" || Text(operation.GetProperty("request"), "operation") != "tool.invoke" ||
            Text(args, "name") != TerminalCandidateContract.BackgroundToolName || Text(args, "definitionDigest") != TerminalCandidateContract.BackgroundToolDefinitionSha256 ||
            Encoding.UTF8.GetByteCount(Text(args, "argsJson")) > 32768 || !Same(WireJson.Parse(Encoding.UTF8.GetBytes(Text(args, "argsJson")), 32768), request))
            throw Rejected("EACCES");
        TerminalCandidateContract.Validate("BackgroundRequest", request);
    }
    private void WriteState(Utf8JsonWriter writer, Entry entry)
    {
        lock (gate)
        {
            writer.WriteStartObject(); writer.WritePropertyName("identity"); entry.Identity.WriteTo(writer);
            writer.WritePropertyName("artifact"); entry.Artifact.WriteTo(writer); writer.WriteString("state", entry.State);
            if (entry.ExitCode.HasValue) writer.WriteNumber("exitCode", entry.ExitCode.Value); else writer.WriteNull("exitCode");
            writer.WriteNull("signalName"); writer.WriteString("totalBytes", entry.Output.Bytes.ToString(CultureInfo.InvariantCulture));
            writer.WriteBoolean("truncated", entry.Output.Truncated); writer.WriteEndObject();
        }
    }
    private static void WriteUnknown(Utf8JsonWriter writer, JsonElement identity)
    {
        writer.WriteStartObject(); writer.WritePropertyName("identity"); identity.WriteTo(writer); writer.WritePropertyName("artifact"); ArtifactFor(identity).WriteTo(writer);
        writer.WriteString("state", "unknown"); writer.WriteNull("exitCode"); writer.WriteNull("signalName"); writer.WriteNull("totalBytes"); writer.WriteBoolean("truncated", true); writer.WriteEndObject();
    }
    private static JsonElement ArtifactFor(JsonElement identity) => Object(writer =>
    { writer.WriteString("artifactId", "output." + Text(identity, "taskId")); writer.WriteString("operationId", Text(identity, "operationId")); writer.WriteString("requestDigest", Text(identity, "requestDigest")); });
    private static string ScopeKey(JsonElement operation) => WireJson.CanonicalString(Object(writer =>
    { foreach (string key in new[] { "scope", "sessionId", "binding" }) { writer.WritePropertyName(key); operation.GetProperty(key).WriteTo(writer); } }));
    private static bool Same(JsonElement left, JsonElement right) => WireJson.CanonicalString(left) == WireJson.CanonicalString(right);
    private static string Text(JsonElement element, string key) => element.GetProperty(key).GetString()!;
    private static ExecutionRejectedException Rejected(string code) => new(code);
    private static void WriteHeader(Utf8JsonWriter writer, string action) { writer.WriteString("contract", "terminal-services-v1"); writer.WriteString("action", action); }
    private static JsonElement Response(string action, Action<Utf8JsonWriter> write) => Object(writer => { WriteHeader(writer, action); write(writer); });
    private static JsonElement Object(Action<Utf8JsonWriter> write)
    { using var memory = new MemoryStream(); using (var writer = new Utf8JsonWriter(memory)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); } return WireJson.Parse(memory.ToArray(), 32768); }
    private sealed class Entry
    {
        internal Entry(JsonElement identity, JsonElement artifact, JsonElement operation, string scopeKey, WindowsBackgroundArtifact output, CancellationTokenSource stop)
        { Identity = identity; Artifact = artifact; Operation = operation; ScopeKey = scopeKey; Output = output; Stop = stop; }
        internal readonly JsonElement Identity, Artifact, Operation;
        internal readonly string ScopeKey;
        internal readonly WindowsBackgroundArtifact Output;
        internal readonly CancellationTokenSource Stop;
        internal readonly TaskCompletionSource<bool> Startup = new(TaskCreationOptions.RunContinuationsAsynchronously), Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string State = "running";
        internal int? ProcessId, ExitCode;
        internal DateTimeOffset? EndedAt;
    }
}
