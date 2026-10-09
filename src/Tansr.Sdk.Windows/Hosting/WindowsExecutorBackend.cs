using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Hosting;

/// <summary>可信宿主登记的资源域，不使用服务端自报路径创建工作区。</summary>
public sealed class WindowsExecutorWorkspace
{
    public WindowsExecutorWorkspace(string id, string revision, WindowsWorkspace workspace)
    { Id = id; Revision = revision; Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace)); }
    public string Id { get; }
    public string Revision { get; }
    public WindowsWorkspace Workspace { get; }
}

public sealed class WindowsBusinessTool
{
    public WindowsBusinessTool(string name, string definitionDigest, Func<JsonElement, CancellationToken, Task<JsonElement>> invoke)
    { Name = name; DefinitionDigest = definitionDigest; Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke)); }
    /// <summary>需要原操作身份的设备服务；上下文只由已授权执行后端构造，模型不能提供工作区对象。</summary>
    public WindowsBusinessTool(string name, string definitionDigest, Func<JsonElement, WindowsBusinessToolContext, CancellationToken, Task<JsonElement>> invoke)
    {
        Name = name; DefinitionDigest = definitionDigest; ContextualInvoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        Invoke = (_, _) => throw new InvalidOperationException("此工具必须由受信执行上下文调用。");
    }
    public string Name { get; }
    internal bool RequireEncryptedJournal { get; set; }
    public string DefinitionDigest { get; }
    public Func<JsonElement, CancellationToken, Task<JsonElement>> Invoke { get; }
    internal Func<JsonElement, WindowsBusinessToolContext, CancellationToken, Task<JsonElement>>? ContextualInvoke { get; }
}

public sealed class WindowsBusinessToolContext
{
    internal WindowsBusinessToolContext(JsonElement operation, WindowsWorkspace workspace, Func<CancellationToken, Task> guard,
        Func<WindowsProcessRequest, CancellationToken, Task<WindowsProcessResult>> executeProcess, bool hasExecutionOutput)
    { Operation = operation.Clone(); Workspace = workspace; GuardAsync = guard; ExecuteProcessAsync = executeProcess; HasExecutionOutput = hasExecutionOutput; }
    public JsonElement Operation { get; }
    public WindowsWorkspace Workspace { get; }
    public Func<CancellationToken, Task> GuardAsync { get; }
    internal Func<WindowsProcessRequest, CancellationToken, Task<WindowsProcessResult>> ExecuteProcessAsync { get; }
    internal bool HasExecutionOutput { get; }
}

public sealed class WindowsExecutionOutputFailure
{
    internal WindowsExecutionOutputFailure(string operationId) { OperationId = operationId; }
    public string OperationId { get; }
    public string Code => "output_transfer_unconfirmed";
}

/// <summary>将既有SDK2资源调用映射到真实Windows后端；生命周期、账本及本地批准由ExecutionHost统一负责。</summary>
public sealed class WindowsExecutorBackend : IExecutionBackend, IExecutionJournalRequirements
{
    private readonly Dictionary<string, WindowsExecutorWorkspace> _workspaces;
    private readonly Dictionary<string, WindowsBusinessTool> _tools;
    private readonly JsonElement? _interpreter;
    private readonly Func<JsonElement, WindowsWorkspace, WindowsProcessRequest>? _processFactory;
    private readonly WindowsProcessOutputHandler? _output;
    private readonly WindowsProcessExecutor _process = new();
    private readonly IExecutionOutputSink? _executionOutput;
    private readonly SemaphoreSlim _outputLifetimes = new(8, 8);
    private WindowsExecutionOutputFailure? _lastOutputFailure;
    /// <summary>流式传输未决与进程终态分开；可显示原最终结果，同时提示增量输出需对账。</summary>
    public WindowsExecutionOutputFailure? LastOutputFailure => Volatile.Read(ref _lastOutputFailure);
    public event Action<WindowsExecutionOutputFailure>? OutputFailed;

    public WindowsExecutorBackend(string executorId, IEnumerable<WindowsExecutorWorkspace> workspaces,
        IEnumerable<WindowsBusinessTool>? tools = null, JsonElement? interpreter = null,
        Func<JsonElement, WindowsWorkspace, WindowsProcessRequest>? processFactory = null, WindowsProcessOutputHandler? output = null,
        IExecutionOutputSink? executionOutput = null)
    {
        if (!IsSupportedWindows())
            throw new PlatformNotSupportedException("执行后端要求 Windows 10 或更新版本。");
        _workspaces = workspaces.ToDictionary(x => x.Id, StringComparer.Ordinal);
        _tools = (tools ?? Array.Empty<WindowsBusinessTool>()).ToDictionary(x => x.Name, StringComparer.Ordinal);
        if (interpreter.HasValue != (processFactory != null)) throw new ArgumentException("解释器声明与受控进程工厂必须同时提供。");
        if (interpreter.HasValue) WireJson.ValidateNamed("ExecutionInterpreter", interpreter.Value);
        _interpreter = interpreter?.Clone(); _processFactory = processFactory; _output = output; _executionOutput = executionOutput;
        var write = _workspaces.Values.All(x => x.Workspace.SupportsCooperativeCompareExchange);
        Registration = Object(writer =>
        {
            writer.WriteString("protocol", "sdk2-ext-v1"); writer.WriteString("executorId", executorId);
            writer.WritePropertyName("platform"); writer.WriteStartObject();
            writer.WriteString("platform", "windows"); writer.WriteString("arch", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
            writer.WriteString("language", "csharp"); writer.WriteString("runtimeVersion", Environment.Version.ToString()); writer.WriteString("adapterVersion", "dotnet-v1"); writer.WriteEndObject();
            writer.WritePropertyName("workspaces"); writer.WriteStartArray();
            foreach (var item in _workspaces.Values)
            { writer.WriteStartObject(); writer.WriteString("workspaceId", item.Id); writer.WriteString("revision", item.Revision); writer.WriteEndObject(); }
            writer.WriteEndArray();
            writer.WritePropertyName("operations"); writer.WriteStartArray();
            writer.WriteStringValue("fs.inspect"); writer.WriteStringValue("fs.read"); writer.WriteStringValue("fs.list");
            if (write) { writer.WriteStringValue("fs.write"); writer.WriteStringValue("fs.mkdir"); }
            if (_interpreter.HasValue) writer.WriteStringValue("process.exec");
            if (_tools.Count > 0) writer.WriteStringValue("tool.invoke");
            writer.WriteEndArray();
            if (_interpreter.HasValue) { writer.WritePropertyName("interpreter"); _interpreter.Value.WriteTo(writer); }
            if (_tools.Count > 0)
            {
                writer.WritePropertyName("tools"); writer.WriteStartArray();
                foreach (var tool in _tools.Values) { writer.WriteStartObject(); writer.WriteString("name", tool.Name); writer.WriteString("definitionDigest", tool.DefinitionDigest); writer.WriteEndObject(); }
                writer.WriteEndArray();
            }
        });
        WireJson.ValidateNamed("ExecutorRegistrationRequest", Registration);
    }

    public JsonElement Registration { get; }

    public void ValidateJournal(IExecutorJournal journal)
    {
        if (_tools.Values.Any(tool => tool.RequireEncryptedJournal) &&
            (!(journal is IEncryptedExecutorJournal encrypted) || !encrypted.EncryptedAtRest))
            throw new ExecutionRejectedException("ENOTSUP");
    }

    // Framework应用未带supportedOS manifest时Environment.OSVersion会报告兼容版本。
    // 库自己探测真实内核，不能迫使每个消费应用修改manifest才能在Windows10+使用。
    private static bool IsSupportedWindows()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
        var version = new WindowsVersion { Size = Marshal.SizeOf<WindowsVersion>(), ServicePack = string.Empty };
        return RtlGetVersion(ref version) == 0 && version.Major >= 10;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsVersion
    {
        public int Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RtlGetVersion(ref WindowsVersion version);

    public async Task<JsonElement> ExecuteAsync(JsonElement operation, Func<CancellationToken, Task> guard, CancellationToken cancellationToken)
    {
        WireJson.ValidateNamed("ExecutionOperation", operation);
        if (guard == null) throw new ArgumentNullException(nameof(guard));
        var target = operation.GetProperty("binding").GetProperty("target");
        if (Text(target, "executorId") != Text(Registration, "executorId")) throw new ExecutionRejectedException("EACCES");
        if (!_workspaces.TryGetValue(Text(target, "workspaceId"), out var descriptor) || descriptor.Revision != Text(target, "workspaceRevision"))
            throw new ExecutionRejectedException("ESTALE");
        var workspace = descriptor.Workspace;
        var request = operation.GetProperty("request"); var name = Text(request, "operation"); var args = request.GetProperty("args");
        await guard(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        switch (name)
        {
            case "fs.inspect":
                WindowsWorkspaceEntry entry;
                try { entry = workspace.Inspect(Text(args, "path")); }
                catch (WindowsWorkspaceException error) when (error.Code == "not_found")
                {
                    // A missing target is a known read-only observation. Keep all other
                    // native failures and any uncertain write outcome on the unknown path.
                    throw new ExecutionRejectedException("ENOENT");
                }
                return Result(name, writer =>
                {
                    writer.WriteString("kind", Kind(entry.Kind)); writer.WriteNumber("size", Math.Max(0, entry.Length));
                    writer.WriteString("mtimeMs", Math.Max(0, new DateTimeOffset(entry.LastWriteTimeUtc).ToUnixTimeMilliseconds()).ToString(CultureInfo.InvariantCulture));
                    writer.WriteString("realpath", Text(args, "path"));
                });
            case "fs.read":
                var bytes = workspace.Read(Text(args, "path"), args.GetProperty("offset").GetInt64(), args.GetProperty("length").GetInt32(), cancellationToken);
                return Result(name, writer => writer.WriteString("bytesBase64", Convert.ToBase64String(bytes)));
            case "fs.list":
                var entries = workspace.List(Text(args, "path"));
                return Result(name, writer =>
                {
                    writer.WritePropertyName("entries"); writer.WriteStartArray();
                    foreach (var item in entries.Take(501))
                    {
                        writer.WriteStartObject(); writer.WriteString("name", item.RelativePath.Split('/').Last()); writer.WriteString("kind", Kind(item.Kind)); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                });
            case "fs.write":
                if (!workspace.SupportsCooperativeCompareExchange) throw new ExecutionRejectedException("ENOTSUP");
                var written = workspace.CompareExchange(Text(args, "path"), args.GetProperty("expectedHash").GetString(), WireJson.DecodeBase64(Text(args, "bytesBase64")), cancellationToken);
                return Result(name, writer => writer.WriteString("hash", written.Hash));
            case "fs.mkdir":
                if (!workspace.SupportsCooperativeCompareExchange) throw new ExecutionRejectedException("ENOTSUP");
                workspace.EnsureDirectory(Text(args, "path"), cancellationToken); return Result(name, _ => { });
            case "tool.invoke":
                if (!_tools.TryGetValue(Text(args, "name"), out var tool) || tool.DefinitionDigest != Text(args, "definitionDigest")) throw new ExecutionRejectedException("ENOTSUP");
                var toolArgs = WireJson.Parse(System.Text.Encoding.UTF8.GetBytes(Text(args, "argsJson")), 32768);
                if (toolArgs.ValueKind != JsonValueKind.Object) throw new ExecutionRejectedException("EACCES");
                var toolResult = tool.ContextualInvoke == null
                    ? await tool.Invoke(toolArgs, cancellationToken).ConfigureAwait(false)
                    : await tool.ContextualInvoke(toolArgs, new WindowsBusinessToolContext(operation, workspace, guard,
                        (process, token) => ExecuteProcessAsync(operation, process, guard, token), _executionOutput != null), cancellationToken).ConfigureAwait(false);
                var text = toolResult.GetRawText();
                if (System.Text.Encoding.UTF8.GetByteCount(text) > 32768) throw new InvalidDataException("业务工具结果超过合同限额。");
                return Result(name, writer => writer.WriteString("resultJson", text));
            case "process.exec":
                if (_processFactory == null || !_interpreter.HasValue || WireJson.CanonicalString(args.GetProperty("interpreter")) != WireJson.CanonicalString(_interpreter.Value))
                    throw new ExecutionRejectedException("ENOTSUP");
                var selected = _processFactory(args.Clone(), workspace);
                // 工厂只选择可信程序/参数/环境，工作目录由已绑定工作区及原请求决定。
                var processRequest = new WindowsProcessRequest(selected.TrustedExecutablePath, selected.Arguments,
                    () => workspace.AcquireProcessDirectory(Text(args, "cwd")))
                {
                    ExpectedExecutableSha256 = selected.ExpectedExecutableSha256,
                    ValidateBeforeStart = selected.ValidateBeforeStart,
                    Timeout = TimeSpan.FromMilliseconds(args.GetProperty("timeoutMs").GetInt32()),
                    MaxOutputBytes = args.GetProperty("maxOutputBytes").GetInt32(),
                    ChunkBytes = selected.ChunkBytes,
                    MaxPendingChunks = selected.MaxPendingChunks,
                    OutputCallbackTimeout = selected.OutputCallbackTimeout,
                    CleanupTimeout = selected.CleanupTimeout,
                };
                foreach (var variable in selected.Environment) processRequest.Environment.Add(variable.Key, variable.Value);
                await guard(cancellationToken).ConfigureAwait(false);
                var watch = Stopwatch.StartNew();
                var result = await ExecuteProcessAsync(operation, processRequest, guard, cancellationToken).ConfigureAwait(false);
                if (!result.CleanupConfirmed) throw new IOException("进程资源清理尚未确认。");
                if (!result.Started) throw new ExecutionRejectedException("resource_start_failed");
                if (!result.OutputComplete && result.Termination != WindowsProcessTermination.Canceled && result.Termination != WindowsProcessTermination.TimedOut)
                    throw new IOException("旧协议无法表示不完整输出；保留结果未知，不重执行。");
                return Result(name, writer =>
                {
                    writer.WriteString("stdout", result.StandardOutput); writer.WriteString("stderr", result.StandardError);
                    writer.WriteString("exitCode", result.ExitCode?.ToString(CultureInfo.InvariantCulture)); writer.WriteNull("signalName");
                    writer.WriteNumber("durationMs", watch.ElapsedMilliseconds);
                    writer.WriteBoolean("timedOut", result.Termination == WindowsProcessTermination.TimedOut);
                    writer.WriteBoolean("aborted", result.Termination == WindowsProcessTermination.Canceled);
                });
            default: throw new ExecutionRejectedException("ENOTSUP");
        }
    }

    private async Task<WindowsProcessResult> ExecuteProcessAsync(JsonElement operation, WindowsProcessRequest request,
        Func<CancellationToken, Task> guard, CancellationToken ct)
    {
        IExecutionOutputCapture? capture = null;
        var captureTruncated = 0;
        Task<IExecutionOutputCapture>? opening = null;
        var pending = new List<Task>();
        var reserved = _executionOutput != null;
        if (reserved && !_outputLifetimes.Wait(0)) throw new ExecutionRejectedException("output_transport_capacity");
        try
        {
            if (_executionOutput != null)
            {
                try
                {
                    opening = _executionOutput.OpenAsync(operation.Clone(), ct);
                    if (opening == null) throw new InvalidOperationException("Output sink returned no task.");
                    pending.Add(opening);
                    using var openingTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var elapsed = Task.Delay(TimeSpan.FromSeconds(5), openingTimeout.Token);
                    if (await Task.WhenAny(opening, elapsed).ConfigureAwait(false) != opening)
                    { ct.ThrowIfCancellationRequested(); throw new TimeoutException("Output transport did not open in time."); }
                    openingTimeout.Cancel();
                    capture = await opening.ConfigureAwait(false);
                    if (capture != null) pending.Add(capture.Completion);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { throw new ExecutionRejectedException("output_transport_unavailable"); }
                if (capture == null) throw new ExecutionRejectedException("output_transport_unavailable");
            }
            await guard(ct).ConfigureAwait(false);
            WindowsProcessOutputHandler? handler = capture == null ? _output : async (chunk, token) =>
            {
                if (!capture.Append(chunk.Stream == WindowsProcessOutputStream.StandardOutput ? "stdout" : "stderr", "utf-8", chunk.RawBytes.ToArray()))
                    Interlocked.Exchange(ref captureTruncated, 1);
                if (_output != null) await _output(chunk, token).ConfigureAwait(false);
            };
            var result = await _process.ExecuteAsync(request, handler, ct).ConfigureAwait(false);
            if (capture != null)
            {
                // 即使用户取消也尝试封口已有前缀；输送失败不改变已知的进程事实。
                using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Task? sealing = null;
                try
                {
                    sealing = capture.SealAsync(!result.OutputComplete || Volatile.Read(ref captureTruncated) != 0, finish.Token);
                    pending.Add(sealing);
                    var settled = Task.WhenAll(sealing, capture.Completion);
                    if (await Task.WhenAny(settled, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != settled)
                    {
                        finish.Cancel();
                        _ = settled.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        OutputUnconfirmed(operation);
                    }
                    else await settled.ConfigureAwait(false);
                }
                catch
                {
                    if (sealing != null) _ = sealing.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    OutputUnconfirmed(operation);
                }
            }
            return result;
        }
        finally
        {
            if (capture != null)
            {
                try { capture.Dispose(); }
                catch { OutputUnconfirmed(operation); }
            }
            else if (opening != null) pending.Add(CloseLateCaptureAsync(opening));
            if (reserved)
            {
                // 不响应取消的用户输送仍占原槽；下一操作不能无限累积悬挂Open/Seal/Pump。
                _ = Task.WhenAll(pending).ContinueWith(task => { _ = task.Exception; _outputLifetimes.Release(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static async Task CloseLateCaptureAsync(Task<IExecutionOutputCapture> opening)
    {
        var late = await opening.ConfigureAwait(false);
        if (late == null) return;
        try { late.Dispose(); }
        finally { await late.Completion.ConfigureAwait(false); }
    }

    private void OutputUnconfirmed(JsonElement operation)
    {
        var failure = new WindowsExecutionOutputFailure(Text(operation, "operationId"));
        Volatile.Write(ref _lastOutputFailure, failure);
        try { OutputFailed?.Invoke(failure); } catch { /* 观察回调不得更改执行终态。 */ }
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static string Kind(WindowsWorkspaceEntryKind kind) => kind == WindowsWorkspaceEntryKind.Directory ? "directory" : kind == WindowsWorkspaceEntryKind.File ? "file" : "symlink";
    private static JsonElement Result(string operation, Action<Utf8JsonWriter> write) => Object(writer =>
    { writer.WriteString("operation", operation); writer.WritePropertyName("args"); writer.WriteStartObject(); write(writer); writer.WriteEndObject(); });
    private static JsonElement Object(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return WireJson.Parse(stream.ToArray(), 262144);
    }
}
