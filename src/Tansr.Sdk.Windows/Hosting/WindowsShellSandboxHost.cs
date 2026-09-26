using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Hosting;

public sealed class WindowsShellSandboxHostOptions
{
    public WindowsShellSandboxHostOptions(WindowsBackgroundHostOptions background, JsonElement interpreter, IExecutorJournal journal,
        Func<WindowsShellSandboxSettings> current)
    { Background = background; Interpreter = interpreter.Clone(); Journal = journal; Current = current; }
    public string CandidateSchemaSha256 { get; set; } = "";
    public WindowsBackgroundHostOptions Background { get; }
    public JsonElement Interpreter { get; }
    public IExecutorJournal Journal { get; }
    public Func<WindowsShellSandboxSettings> Current { get; }
    public int MaximumEvidenceOperations { get; set; } = 4096;
}

/// <summary>独立 Shell sandbox profile。复用原 journal、进程捕获、后台 registry 与本地授权；不是 UAC 提权。</summary>
public sealed class WindowsShellSandboxHost : IDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    public static string CandidateSchemaSha256 => TerminalShellSandboxContract.SchemaSha256;
    private readonly object gate = new();
    private readonly WindowsShellSandboxHostOptions options;
    private readonly WindowsBackgroundHost background;
    private readonly AsyncLocal<Invocation?> active = new();
    private readonly HashSet<Task> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? closing;
    private bool closed;
    private int cleanupUnconfirmed;

    public WindowsShellSandboxHost(WindowsShellSandboxHostOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.CandidateSchemaSha256 != CandidateSchemaSha256 || options.Background == null || options.Journal == null || options.Current == null ||
            options.MaximumEvidenceOperations < 1 || options.MaximumEvidenceOperations > 1000000) throw new ArgumentException("Invalid sandbox host options.", nameof(options));
        WireJson.ValidateNamed("ExecutionInterpreter", options.Interpreter); ReadSettings();
        background = new WindowsBackgroundHost(new WindowsBackgroundHostOptions(options.Background.ArtifactDirectory,
            (request, workspace) => Prepare(request, workspace))
        {
            CandidateRevision = options.Background.CandidateRevision,
            MaximumTasks = options.Background.MaximumTasks,
            MaximumArtifactBytes = options.Background.MaximumArtifactBytes,
            ArtifactTimeToLive = options.Background.ArtifactTimeToLive,
            MaximumRuntime = options.Background.MaximumRuntime
        });
    }

    public WindowsBusinessTool CreateTool() => new(TerminalShellSandboxContract.ToolName, TerminalShellSandboxContract.DefinitionDigest, InvokeAsync);
    public string RuntimeInstanceId => background.RuntimeInstanceId;

    private Task<JsonElement> InvokeAsync(JsonElement request, WindowsBusinessToolContext context, CancellationToken ct)
    {
        lock (gate)
        {
            if (closed) throw Reject("ESTALE");
            var work = ExecuteAsync(request, context, ct); pending.Add(work);
            _ = work.ContinueWith(done => { lock (gate) pending.Remove(done); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return work;
        }
    }

    private async Task<JsonElement> ExecuteAsync(JsonElement request, WindowsBusinessToolContext context, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token); var ct = stop.Token;
        ExecutionJson.Operation(context.Operation); TerminalShellSandboxContract.ValidateRequest(request);
        var original = ParseOperation(context.Operation);
        if (!original.HasValue || !Equal(original.Value, request)) throw Reject("EACCES");
        var launch = Text(request, "action") == "exec" || Text(request.GetProperty("request"), "action") == "launch";
        if (launch && (!context.Operation.GetProperty("binding").GetProperty("target").TryGetProperty("interpreter", out var interpreter) ||
            !Equal(interpreter, options.Interpreter) || Text(request, "action") == "exec" && !Equal(request.GetProperty("interpreter"), options.Interpreter))) throw Reject("ENOTSUP");
        await context.GuardAsync(ct).ConfigureAwait(false); await VerifyEscalationAsync(context.Operation, request, ct).ConfigureAwait(false);
        var settings = ReadSettings();
        var invocation = new Invocation(settings, request, context); var previous = active.Value; active.Value = invocation;
        JsonElement? result = null, output = null;
        try
        {
            if (Text(request, "action") == "exec")
            {
                if (request.GetProperty("maxOutputBytes").GetInt32() > 4096 && !context.HasExecutionOutput) throw Reject("ENOTSUP");
                var selected = Prepare(request, context.Workspace);
                var process = new WindowsProcessRequest(selected.TrustedExecutablePath, selected.Arguments, () => context.Workspace.AcquireProcessDirectory(Text(request, "cwd")))
                {
                    ExpectedExecutableSha256 = selected.ExpectedExecutableSha256,
                    Timeout = TimeSpan.FromMilliseconds(request.GetProperty("timeoutMs").GetInt32()),
                    ValidateBeforeStart = selected.ValidateBeforeStart,
                    MaxOutputBytes = request.GetProperty("maxOutputBytes").GetInt32(),
                    ChunkBytes = selected.ChunkBytes,
                    MaxPendingChunks = selected.MaxPendingChunks,
                    CleanupTimeout = selected.CleanupTimeout,
                    OutputCallbackTimeout = selected.OutputCallbackTimeout
                };
                foreach (var item in selected.Environment) process.Environment.Add(item.Key, item.Value);
                await context.GuardAsync(ct).ConfigureAwait(false); RequireCurrent(invocation);
                var elapsed = Stopwatch.StartNew(); var executed = await context.ExecuteProcessAsync(process, ct).ConfigureAwait(false);
                if (!executed.CleanupConfirmed) Interlocked.Exchange(ref cleanupUnconfirmed, 1);
                if (!executed.Started || !executed.CleanupConfirmed || !executed.OutputDeliverySettled ||
                    !executed.OutputComplete && executed.Termination != WindowsProcessTermination.Canceled && executed.Termination != WindowsProcessTermination.TimedOut)
                    throw new IOException("sandbox_execution_unconfirmed");
                // 原捕获的字节仍是输出窗权威。只对新 profile 使用严格 UTF8，不能把非法字节换字后伪造摘要。
                _ = StrictUtf8.GetString(executed.StandardOutputBytes); _ = StrictUtf8.GetString(executed.StandardErrorBytes);
                var outPreview = Preview(executed.StandardOutputBytes, 4096);
                var errPreview = Preview(executed.StandardErrorBytes, 4096 - StrictUtf8.GetByteCount(outPreview));
                output = Json(writer =>
                {
                    writer.WriteBoolean("previewTruncated", StrictUtf8.GetByteCount(outPreview) + StrictUtf8.GetByteCount(errPreview) < executed.StandardOutputBytes.Length + executed.StandardErrorBytes.Length);
                    void Channel(string name, byte[] bytes)
                    {
                        writer.WritePropertyName(name); writer.WriteStartObject(); writer.WriteNumber("totalBytes", bytes.Length);
                        writer.WriteString("payloadDigest", WireJson.Sha256(bytes)); writer.WriteEndObject();
                    }
                    Channel("stdout", executed.StandardOutputBytes); Channel("stderr", executed.StandardErrorBytes);
                });
                result = Json(writer =>
                {
                    writer.WriteString("stdout", outPreview); writer.WriteString("stderr", errPreview);
                    writer.WriteString("exitCode", executed.ExitCode?.ToString(CultureInfo.InvariantCulture)); writer.WriteNull("signalName");
                    writer.WriteNumber("durationMs", elapsed.ElapsedMilliseconds); writer.WriteBoolean("timedOut", executed.Termination == WindowsProcessTermination.TimedOut);
                    writer.WriteBoolean("aborted", executed.Termination == WindowsProcessTermination.Canceled);
                });
            }
            else
            {
                var tool = await background.InvokeSandboxAsync(request.GetProperty("request"), context, ct).ConfigureAwait(false);
                result = WireJson.Parse(Encoding.UTF8.GetBytes(tool.GetProperty("content")[0].GetProperty("text").GetString()!), 32768);
            }
        }
        catch (WindowsSandboxIsolationDeniedException) when (launch && !invocation.Escalated && settings.Mode != WindowsSandboxMode.Off && settings.Capability != WindowsSandboxCapability.None)
        { invocation.IsolationDenied = true; }
        finally { active.Value = previous; }
        var response = Json(writer =>
        {
            writer.WriteString("contract", TerminalShellSandboxContract.Protocol); writer.WriteString("action", Text(request, "action"));
            writer.WritePropertyName("result"); if (result.HasValue) result.Value.WriteTo(writer); else writer.WriteNullValue();
            if (Text(request, "action") == "exec") { writer.WritePropertyName("output"); if (output.HasValue) output.Value.WriteTo(writer); else writer.WriteNullValue(); }
            writer.WritePropertyName("sandbox"); writer.WriteStartObject(); writer.WriteString("mode", settings.Mode.ToString().ToLowerInvariant());
            writer.WriteString("capability", settings.Capability.ToString().ToLowerInvariant()); writer.WriteBoolean("isolationDenied", invocation.IsolationDenied);
            writer.WriteBoolean("escalated", invocation.Escalated); writer.WriteEndObject();
        });
        TerminalShellSandboxContract.ValidateResponse(response, request);
        return Json(writer =>
        {
            writer.WriteString("status", "ok"); writer.WritePropertyName("content"); writer.WriteStartArray(); writer.WriteStartObject();
            writer.WriteString("t", "text"); writer.WriteString("text", response.GetRawText()); writer.WriteEndObject(); writer.WriteEndArray();
        });
    }

    private WindowsProcessRequest Prepare(JsonElement request, WindowsWorkspace workspace)
    {
        var invocation = active.Value ?? throw Reject("EACCES"); RequireCurrent(invocation);
        if (invocation.Settings.Mode == WindowsSandboxMode.Required && invocation.Settings.Capability == WindowsSandboxCapability.None) throw Reject("ENOTSUP");
        var selected = options.Background.ProcessFactory(request.Clone(), workspace) ?? throw Reject("ENOTSUP");
        RequireCurrent(invocation);
        invocation.Escalated = invocation.Request.TryGetProperty("escalation", out _) && invocation.Settings.Mode == WindowsSandboxMode.On && invocation.Settings.Capability != WindowsSandboxCapability.None;
        if (invocation.Settings.Mode == WindowsSandboxMode.Off || invocation.Settings.Capability == WindowsSandboxCapability.None || invocation.Escalated) return WithFinalCheck(selected, invocation);
        using var location = workspace.AcquireProcessDirectory(Text(request, "cwd"));
        var wrapped = invocation.Settings.Runtime!.Prepare(selected, location.DirectoryPath, invocation.Settings.Policy) ?? throw Reject("ENOTSUP");
        RequireCurrent(invocation); return WithFinalCheck(wrapped, invocation);
    }

    private WindowsProcessRequest WithFinalCheck(WindowsProcessRequest selected, Invocation invocation)
    {
        var request = new WindowsProcessRequest(selected.TrustedExecutablePath, selected.Arguments, selected.AcquireWorkingDirectory)
        {
            ExpectedExecutableSha256 = selected.ExpectedExecutableSha256,
            Timeout = selected.Timeout,
            MaxOutputBytes = selected.MaxOutputBytes,
            ChunkBytes = selected.ChunkBytes,
            MaxPendingChunks = selected.MaxPendingChunks,
            CleanupTimeout = selected.CleanupTimeout,
            OutputCallbackTimeout = selected.OutputCallbackTimeout,
            ValidateBeforeStart = () => { selected.ValidateBeforeStart?.Invoke(); RequireCurrent(invocation); }
        };
        foreach (var item in selected.Environment) request.Environment.Add(item.Key, item.Value);
        return request;
    }

    private Settings ReadSettings()
    {
        var current = options.Current() ?? throw Reject("EACCES"); var capability = current.Runtime?.Capability ?? WindowsSandboxCapability.None;
        if (!Enum.IsDefined(typeof(WindowsSandboxMode), current.Mode) || !Enum.IsDefined(typeof(WindowsSandboxCapability), capability)) throw Reject("EACCES");
        foreach (var path in current.Policy.AllowedWriteRoots) WindowsDuplexProcessNative.ValidatePath(path);
        return new Settings(current.Mode, current.Runtime, capability, new WindowsSandboxPolicy(current.Policy.AllowedWriteRoots, current.Policy.AllowNetwork));
    }

    private void RequireCurrent(Invocation invocation)
    {
        var current = ReadSettings(); var previous = invocation.Settings;
        if (lifetime.IsCancellationRequested || current.Mode != previous.Mode || !ReferenceEquals(current.Runtime, previous.Runtime) || current.Capability != previous.Capability ||
            current.Policy.AllowNetwork != previous.Policy.AllowNetwork || !current.Policy.AllowedWriteRoots.SequenceEqual(previous.Policy.AllowedWriteRoots, StringComparer.Ordinal)) throw Reject("ESTALE");
    }

    private async Task VerifyEscalationAsync(JsonElement operation, JsonElement request, CancellationToken ct)
    {
        if (!request.TryGetProperty("escalation", out var escalation)) return;
        JsonElement? previous = null; string? after = null; var count = 0;
        while (true)
        {
            var page = await options.Journal.OperationsAsync(after, ct).ConfigureAwait(false); if (page.Count == 0) break;
            foreach (var value in page)
            {
                var id = Text(value, "operationId");
                if (++count > options.MaximumEvidenceOperations || after != null && string.CompareOrdinal(id, after) <= 0) throw Reject("EFBIG");
                after = id; if (id == Text(escalation, "previousDeniedOperationId")) previous = value;
                var old = ParseOperation(value);
                if (id != Text(operation, "operationId") && old.HasValue && old.Value.TryGetProperty("escalation", out var oldEscalation) &&
                    Text(oldEscalation, "previousDeniedOperationId") == Text(escalation, "previousDeniedOperationId") &&
                    Text(value, "sessionId") == Text(operation, "sessionId") && Equal(value.GetProperty("scope"), operation.GetProperty("scope"))) throw Reject("EACCES");
            }
        }
        if (!previous.HasValue || Text(previous.Value, "operationId") == Text(operation, "operationId") || Text(previous.Value, "sessionId") != Text(operation, "sessionId") ||
            !Equal(previous.Value.GetProperty("scope"), operation.GetProperty("scope")) || !Equal(previous.Value.GetProperty("binding"), operation.GetProperty("binding"))) throw Reject("EACCES");
        var original = ParseOperation(previous.Value); var receipt = await options.Journal.ReceiptAsync(previous.Value, ct).ConfigureAwait(false);
        if (!original.HasValue || original.Value.TryGetProperty("escalation", out _) || !receipt.HasValue || Text(receipt.Value, "status") != "completed" ||
            Text(original.Value.GetProperty("call"), "turnId") != Text(request.GetProperty("call"), "turnId") ||
            Text(original.Value.GetProperty("call"), "toolCallId") == Text(request.GetProperty("call"), "toolCallId") ||
            !Equal(CommandIdentity(original.Value, previous.Value), CommandIdentity(request, operation))) throw Reject("EACCES");
        ExecutionJson.Receipt(previous.Value, receipt.Value);
        var tool = WireJson.Parse(Encoding.UTF8.GetBytes(Text(receipt.Value.GetProperty("result").GetProperty("args"), "resultJson")), 32768);
        if (Text(tool, "status") != "ok" || tool.GetProperty("content").GetArrayLength() != 1 || Text(tool.GetProperty("content")[0], "t") != "text") throw Reject("EACCES");
        var response = WireJson.Parse(Encoding.UTF8.GetBytes(Text(tool.GetProperty("content")[0], "text")), 32768);
        TerminalShellSandboxContract.ValidateResponse(response, original.Value); var state = response.GetProperty("sandbox");
        if (!state.GetProperty("isolationDenied").GetBoolean() || state.GetProperty("escalated").GetBoolean() || Text(state, "mode") != "on" || Text(state, "capability") == "none") throw Reject("EACCES");
    }

    private static JsonElement CommandIdentity(JsonElement request, JsonElement operation) => Json(writer =>
    {
        var args = Text(request, "action") == "exec" ? request : request.GetProperty("request");
        writer.WriteString("action", Text(request, "action"));
        if (Text(request, "action") == "background") writer.WriteString("mode", Text(args, "mode"));
        writer.WriteString("command", Text(args, "command")); writer.WriteString("cwd", Text(args, "cwd")); writer.WritePropertyName("interpreter");
        (Text(request, "action") == "exec" ? request.GetProperty("interpreter") : operation.GetProperty("binding").GetProperty("target").GetProperty("interpreter")).WriteTo(writer);
    });
    private static JsonElement? ParseOperation(JsonElement operation)
    {
        var resource = operation.GetProperty("request");
        if (Text(operation, "toolName") != "Shell" || Text(resource, "operation") != "tool.invoke") return null;
        var args = resource.GetProperty("args");
        if (Text(args, "name") != TerminalShellSandboxContract.ToolName || Text(args, "definitionDigest") != TerminalShellSandboxContract.DefinitionDigest) return null;
        var result = WireJson.Parse(Encoding.UTF8.GetBytes(Text(args, "argsJson")), 32768); TerminalShellSandboxContract.ValidateRequest(result); return result;
    }
    public Task CloseAsync()
    {
        lock (gate)
        {
            if (closing != null) return closing; closed = true; lifetime.Cancel();
            return closing = CloseCoreAsync(pending.ToArray());
        }
    }
    private async Task CloseCoreAsync(Task[] tasks)
    {
        try
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* Individual invocations keep their original failure. */ }
            await background.CloseAsync().ConfigureAwait(false);
            if (Volatile.Read(ref cleanupUnconfirmed) != 0) throw new IOException("sandbox_cleanup_unconfirmed");
        }
        finally { lifetime.Dispose(); }
    }
    public void Dispose() { _ = CloseAsync(); }
    private static string Preview(byte[] bytes, int maximum)
    {
        var length = Math.Min(bytes.Length, maximum);
        while (true)
        {
            try { return StrictUtf8.GetString(bytes, 0, length); }
            catch (DecoderFallbackException) when (length > 0) { length--; }
        }
    }
    private static JsonElement Json(Action<Utf8JsonWriter> write) { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); } return WireJson.Parse(stream.ToArray(), 32768); }
    private static bool Equal(JsonElement first, JsonElement second) => WireJson.CanonicalString(first) == WireJson.CanonicalString(second);
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
    private static ExecutionRejectedException Reject(string code) => new(code);
    private sealed class Settings(WindowsSandboxMode mode, IWindowsShellSandboxRuntime? runtime, WindowsSandboxCapability capability, WindowsSandboxPolicy policy)
    { internal WindowsSandboxMode Mode = mode; internal IWindowsShellSandboxRuntime? Runtime = runtime; internal WindowsSandboxCapability Capability = capability; internal WindowsSandboxPolicy Policy = policy; }
    private sealed class Invocation(Settings settings, JsonElement request, WindowsBusinessToolContext context)
    { internal Settings Settings = settings; internal JsonElement Request = request; internal WindowsBusinessToolContext Context = context; internal bool IsolationDenied, Escalated; }
}
