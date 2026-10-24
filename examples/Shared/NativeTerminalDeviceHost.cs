using System.IO;
using System.Text.Json;
using Tansr.Sdk.Client;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Storage;
#endif

namespace Tansr.Examples;

// 配置由操作员选择，永不从模型输出/消息/工具参数读取；核心协调全部由公开 TerminalDeviceHost 完成。
internal sealed class NativeTerminalDeviceHost
{
#if WINDOWS || NETFRAMEWORK
    private readonly WindowsWorkspace workspace;
    private readonly IExecutorJournal journal;
    private readonly bool cloudStorage;
    private readonly Func<Task>? closePublication;
    private readonly TansrClient controller, device;
    private readonly bool ownsController, ownsDevice;
    private readonly TerminalConnection controllerTerminal, deviceTerminal;
    private TerminalDeviceHost host = null!;
    private readonly CancellationTokenSource observation = new();
    private readonly List<Task> outputs = new();
    private readonly object gate = new();
    private Task? stopTask;
    private readonly NativeOperationConsent consent = new();
    private NativeTerminalDeviceHost(WindowsWorkspace workspace, IExecutorJournal journal,
        TansrClient controller, TansrClient device, TerminalConnection controllerTerminal, TerminalConnection deviceTerminal, Func<Task>? closePublication, bool ownsController, bool ownsDevice, bool cloudStorage)
    { this.workspace = workspace; this.journal = journal; this.controller = controller; this.device = device; this.controllerTerminal = controllerTerminal; this.deviceTerminal = deviceTerminal; this.closePublication = closePublication; this.ownsController = ownsController; this.ownsDevice = ownsDevice; this.cloudStorage = cloudStorage; }
    internal Task Completion => host.Completion;
    internal string? LastNotificationErrorCode => host.LastNotificationErrorCode;
    internal string Status => "device=" + host.State + "；存储=" + (cloudStorage ? "cloud（Serve平台保管，无本地数据库）" : "terminal") + "；自动记忆=" + (cloudStorage ? "平台来源，以持久化发现为准" : closePublication == null ? "未装配" : "与终端工具共用原设备绑定") + "；仅表示本机设备状态，远端任务和记忆收尾以原回执为准。";
#else
    private NativeTerminalDeviceHost() { }
    internal Task Completion => Task.CompletedTask;
    internal string? LastNotificationErrorCode => null;
    internal string Status => "需要 Windows 目标";
#endif
    // Closing the remote conversation retires its notification authority. Stop that optional
    // transport first, but keep polling, operation settlement and memory publication alive
    // until the original resource observation confirms that the core has finished draining.
    internal Task PrepareSessionCloseAsync(CancellationToken cancellationToken = default)
    {
#if WINDOWS || NETFRAMEWORK
        return host.QuiesceNotificationsAsync(cancellationToken);
#else
        return Task.CompletedTask;
#endif
    }
    internal static async Task<NativeTerminalDeviceHost> StartAsync(string configurationFile, string expectedSessionId, Uri expectedEndpoint,
        Func<string, CancellationToken, Task<bool>> approve, Action<string> output, CancellationToken ct = default, TansrClient? borrowedController = null, SessionContract sessionContract = SessionContract.Sdk1,
        NativeToolHost? nativeTools = null)
    {
#if WINDOWS || NETFRAMEWORK
        using var document = JsonDocument.Parse(await BoundedFiles.ReadAsync(configurationFile, 65536, ct).ConfigureAwait(false));
        var config = document.RootElement;
        if (Text(config, "format") != "tansr-example-terminal-device-v1" || !config.GetProperty("enablePreview").GetBoolean())
            throw new InvalidOperationException("terminal_device_explicit_preview_required");
        var storageMode = ReadStorageMode(config);
        var cloudStorage = storageMode == "cloud";
        var publicationProfile = config.TryGetProperty("publication", out var selectedMemory) && selectedMemory.TryGetProperty("profile", out var selectedProfile)
            ? selectedProfile.GetString() : "terminal-services-v1";
        if (publicationProfile != "terminal-services-v1" && publicationProfile != "terminal-persistence-v1")
            throw new InvalidOperationException("terminal_memory_unknown_profile");
        var sessionId = Text(config, "sessionId");
        if (sessionId != expectedSessionId) throw new InvalidOperationException("terminal_device_session_mismatch");
        var authority = TrustedExampleScope.FromPath(Absolute(config, "trustedScopeFile"));
        var endpoint = new Uri(Text(config, "serveUrl"), UriKind.Absolute);
        if (endpoint != expectedEndpoint) throw new InvalidOperationException("terminal_device_endpoint_mismatch");
        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("terminal_device_endpoint_credentials_forbidden");
        var allowHttp = config.TryGetProperty("allowInsecureLoopback", out var http) && http.GetBoolean();
        var useControllerForDevice = config.TryGetProperty("useControllerForDevice", out var sharedDevice) && sharedDevice.GetBoolean();
        if (useControllerForDevice && borrowedController == null) throw new InvalidOperationException("terminal_device_borrowed_controller_required");
        var controlToken = borrowedController == null ? Text(config, "controllerTokenEnvironment") : "";
        var deviceToken = useControllerForDevice ? "" : Text(config, "deviceTokenEnvironment");
        var userToken = Environment.GetEnvironmentVariable("TANSR_SERVE_USER_TOKEN");
        IReadOnlyDictionary<string, string>? hostHeaders = string.IsNullOrEmpty(userToken) ? null : new Dictionary<string, string> { ["x-tansr-demo-user-token"] = userToken! };
        TansrClientOptions Options(string variable) => new()
        {
            BaseUri = endpoint,
            AllowInsecureLoopback = allowHttp,
            PrincipalProvider = authority.ReadPrincipal,
            ExecutionScopeProvider = authority.ReadScope,
            SessionContract = sessionContract,
            AdditionalRequestHeaders = hostHeaders,
            TokenProvider = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : throw new InvalidOperationException("terminal_device_token_missing")); }
        };
        var controller = borrowedController ?? new TansrClient(Options(controlToken)); var device = useControllerForDevice ? controller : new TansrClient(Options(deviceToken));
        var controllerTerminal = TerminalConnection.ForClient(controller, true); var deviceTerminal = TerminalConnection.ForClient(device, true);
        WindowsWorkspace? workspace = null; IExecutorJournal? journal = null; Func<Task>? closePublication = null; NativeTerminalDeviceHost? result = null;
        try
        {
            // 显式选择必须与可信 Serve 装配一致；发现失败不能降级成本地库。旧配置保留原调用。
            if (config.TryGetProperty("storageMode", out _))
            {
                var persistence = await controller.GetSessionPersistenceAsync(sessionId, ct).ConfigureAwait(false);
                if (Text(persistence, "mode") != storageMode) throw new InvalidOperationException("terminal_device_storage_mode_mismatch");
                if (cloudStorage && (!persistence.GetProperty("handoff").GetProperty("executionJournal").GetBoolean() ||
                    !persistence.GetProperty("handoff").GetProperty("trustedControl").GetBoolean() ||
                    !persistence.GetProperty("coverage").EnumerateArray().Any(item => item.GetString() == "executions")))
                    throw new InvalidOperationException("terminal_device_cloud_journal_unavailable");
            }
            var work = config.GetProperty("workspace");
            var workPath = Absolute(work, "path"); string? journalPath = null;
            var compactCompletedReceipts = false;
            IArchiveKeyProvider? keyProvider = null;
            var scope = authority.ReadScope(); var executorId = Text(config, "executorId"); var workspaceId = Text(work, "id");
            if (!cloudStorage)
            {
                var log = config.GetProperty("journal"); journalPath = Absolute(log, "path");
                if (log.TryGetProperty("compactCompletedReceipts", out var compact))
                {
                    if (compact.ValueKind != JsonValueKind.True && compact.ValueKind != JsonValueKind.False)
                        throw new InvalidOperationException("terminal_device_journal_compact_boolean_required");
                    compactCompletedReceipts = compact.GetBoolean();
                }
                if (journalPath.StartsWith(workPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("terminal_journal_must_be_outside_workspace");
                if (config.TryGetProperty("encryption", out var encryption))
                {
                    if (Text(encryption, "provider") != "dpapi-current-user") throw new InvalidOperationException("terminal_device_explicit_encryption_required");
                    string keyPath = Absolute(encryption, "path");
                    if (string.Equals(keyPath, journalPath, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(keyPath, Absolute(config, "trustedScopeFile"), StringComparison.OrdinalIgnoreCase) ||
                        keyPath.StartsWith(workPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        config.TryGetProperty("publication", out var publicationConfig) && string.Equals(keyPath, Absolute(publicationConfig, "path"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("terminal_device_key_path_must_be_separate");
                    keyProvider = Text(encryption, "mode") == "create" ? CurrentUserDpapiArchiveKeyProvider.Create(keyPath, Text(encryption, "keyId")) :
                        Text(encryption, "mode") == "reopen" ? CurrentUserDpapiArchiveKeyProvider.Open(keyPath, Text(encryption, "keyId")) :
                        throw new InvalidOperationException("terminal_device_explicit_key_mode_required");
                }
                if (config.TryGetProperty("publication", out _) && keyProvider == null) throw new InvalidOperationException("terminal_memory_encryption_required");
                journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
                {
                    Path = journalPath,
                    Mode = Text(log, "mode") == "create" ? StorageOpenMode.Create : Text(log, "mode") == "reopen" ? StorageOpenMode.Reopen : throw new InvalidOperationException("terminal_device_open_mode_required"),
                    CompactCompletedReceipts = compactCompletedReceipts,
                    KeyProvider = keyProvider,
                    ExecutorId = executorId,
                    ApplicationScopeId = Text(scope, "applicationScopeId"),
                    EndUserId = Text(scope, "endUserId"),
                    ReadContext = authority.ReadScope,
                }, ct).ConfigureAwait(false);
            }
            else journal = new CloudExecutorJournal(device, sessionId, () => result?.host.Connection ?? throw new InvalidOperationException("terminal_device_connection_required"));
            workspace = new WindowsWorkspace(workPath, new WindowsWorkspaceOptions { AllWritersCooperate = work.TryGetProperty("allWritersCooperate", out var cooperate) && cooperate.GetBoolean() });
            var allowed = config.GetProperty("allowedTools").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (allowed.Length == 0 || allowed.Any(string.IsNullOrWhiteSpace) || allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Length)
                throw new InvalidOperationException("terminal_device_allowed_tools_required");
            WindowsBusinessTool? memoryTool = null;
            if (config.TryGetProperty("publication", out var memory))
            {
                var memoryPath = Absolute(memory, "path");
                if (memoryPath == journalPath || memoryPath.StartsWith(workPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("terminal_memory_store_must_be_outside_workspace");
                var mode = Text(memory, "mode") == "create" ? StorageOpenMode.Create : Text(memory, "mode") == "reopen" ? StorageOpenMode.Reopen : throw new InvalidOperationException("terminal_memory_open_mode_required");
                var profile = publicationProfile;
                if (profile == "terminal-persistence-v1")
                {
                    var store = await SqliteTerminalPersistenceStore.OpenAsync(new SqliteTerminalPersistenceOptions
                    {
                        EnableProfile = true,
                        Path = memoryPath,
                        Mode = mode,
                        Identity = memory.GetProperty("identity").Clone(),
                        ReadContext = authority.ReadScope,
                        KeyProvider = keyProvider!,
                        MaxActiveTransfers = memory.GetProperty("maxTransfers").GetInt32(),
                        MaxStagingBytes = memory.GetProperty("maxStagingBytes").GetInt32(),
                        MaxPages = memory.GetProperty("maxPages").GetInt32(),
                    }, ct).ConfigureAwait(false);
                    closePublication = () => store.CloseAsync();
                    memoryTool = new WindowsTerminalPersistenceHost(store, enableProfile: true).CreateTool();
                }
                else if (profile == "terminal-services-v1")
                {
                    var store = await SqliteMemoryPublicationStore.OpenAsync(new SqliteMemoryPublicationOptions
                    {
                        EnablePreview = true,
                        Path = memoryPath,
                        Mode = mode,
                        Identity = memory.GetProperty("identity").Clone(),
                        ReadContext = authority.ReadScope,
                        KeyProvider = keyProvider,
                        MaxTransfers = memory.GetProperty("maxTransfers").GetInt32(),
                        MaxStagingBytes = memory.GetProperty("maxStagingBytes").GetInt32(),
                        MaxPages = memory.GetProperty("maxPages").GetInt32(),
                    }, ct).ConfigureAwait(false);
                    closePublication = () => store.CloseAsync();
                    memoryTool = new WindowsMemoryPublicationHost(store, enablePreview: true, requireEncryption: true).CreateTool();
                }
                else throw new InvalidOperationException("terminal_memory_unknown_profile");
            }
            JsonElement? interpreter = null; Func<JsonElement, WindowsWorkspace, WindowsProcessRequest>? process = null;
            if (config.TryGetProperty("shell", out var shell))
            {
                var executable = Absolute(shell, "executable"); var digest = Text(shell, "sha256");
                var descriptor = shell.GetProperty("interpreter").Clone();
                if (Text(descriptor, "hostShell") != "powershell") throw new InvalidOperationException("example_requires_fixed_powershell");
                interpreter = descriptor;
                process = (arguments, directory) =>
                {
                    var request = new WindowsProcessRequest(executable,
                        new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", arguments.GetProperty("command").GetString()! },
                        () => directory.AcquireProcessDirectory(arguments.GetProperty("cwd").GetString()!))
                    { ExpectedExecutableSha256 = digest };
                    request.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows); return request;
                };
            }
            result = new NativeTerminalDeviceHost(workspace, journal, controller, device, controllerTerminal, deviceTerminal, closePublication, borrowedController == null, !useControllerForDevice, cloudStorage);
            var owner = result;
            var configurationIdentity = WireJson.CanonicalString(config);
            var initialPrincipal = authority.ReadPrincipal();
            string TokenIdentity()
            {
                var currentDevice = useControllerForDevice ? "borrowed-device" : Environment.GetEnvironmentVariable(deviceToken);
                var currentController = borrowedController == null ? Environment.GetEnvironmentVariable(controlToken) : "borrowed-controller";
                if (string.IsNullOrEmpty(currentDevice) || string.IsNullOrEmpty(currentController)) throw new ExecutionRejectedException("ESTALE");
                return WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(currentDevice + "\0" + currentController));
            }
            var tokenIdentity = TokenIdentity();
            async Task<string> ValidateCurrentAsync(JsonElement operation, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                using var currentConfiguration = JsonDocument.Parse(await BoundedFiles.ReadAsync(configurationFile, 65536, token).ConfigureAwait(false));
                var currentScope = WireJson.CanonicalString(authority.ReadScope());
                if (WireJson.CanonicalString(currentConfiguration.RootElement) != configurationIdentity || TokenIdentity() != tokenIdentity ||
                    authority.ReadPrincipal() != initialPrincipal || currentScope != WireJson.CanonicalString(operation.GetProperty("scope")))
                    throw new ExecutionRejectedException("ESTALE");
                return tokenIdentity + ":" + WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(currentScope));
            }
            var businessTools = nativeTools?.SelectDeviceExecution().ToList() ?? new List<WindowsBusinessTool>();
            if (memoryTool != null) businessTools.Add(memoryTool);
            result.host = new TerminalDeviceHost(new ExecutionClient(controller), new ExecutionClient(device), controllerTerminal, deviceTerminal,
                sink => new WindowsExecutorBackend(executorId, new[] { new WindowsExecutorWorkspace(workspaceId, Text(work, "revision"), workspace) },
                    tools: businessTools, interpreter: interpreter, processFactory: process, executionOutput: sink), journal,
                new TerminalDeviceOptions { SessionId = sessionId, SessionContract = sessionContract, WorkspaceId = workspaceId, BindingRequestId = Text(config, "bindingRequestId"), RequestedTools = allowed },
                async (operation, token) =>
                {
                    try { await ValidateCurrentAsync(operation, token).ConfigureAwait(false); }
                    catch { owner.consent.Revoke(operation); throw; }
                    var tool = Text(operation, "toolName");
                    var request = operation.GetProperty("request");
                    if (tool == "MemoryPublication" && memoryTool != null && request.GetProperty("operation").GetString() == "tool.invoke")
                    {
                        var arguments = request.GetProperty("args");
                        if (Text(arguments, "name") != memoryTool.Name || Text(arguments, "definitionDigest") != memoryTool.DefinitionDigest) throw new ExecutionRejectedException("EACCES");
                        return;
                    }
                    if (!allowed.Contains(tool, StringComparer.Ordinal)) throw new ExecutionRejectedException("EACCES");
                    var description = "终端本地批准：" + tool + "\n工作区：" + workPath + "\n" + request.GetRawText();
                    if (request.GetProperty("operation").GetString() == "process.exec") description += "\n此操作以当前用户运行固定解释器，不提供操作系统沙箱；不接受隐式提权。";
                    await owner.consent.EnsureAsync(operation, t => ValidateCurrentAsync(operation, t), t => approve(description, t),
                        () => { if (request.GetProperty("operation").GetString() == "process.exec") owner.Observe(operation, output); }, token).ConfigureAwait(false);
                });
            await result.host.StartAsync(ct).ConfigureAwait(false); return result;
        }
        catch
        {
            if (result != null) await result.StopAsync().ConfigureAwait(false);
            else { if (closePublication != null) await closePublication().ConfigureAwait(false); if (journal != null) await journal.CloseAsync().ConfigureAwait(false); workspace?.Dispose(); deviceTerminal.Dispose(); controllerTerminal.Dispose(); if (!useControllerForDevice) device.Dispose(); if (borrowedController == null) controller.Dispose(); }
            throw;
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("设备工具需要 net10.0-windows 或 net48。");
#endif
    }
#if WINDOWS || NETFRAMEWORK
    private void Observe(JsonElement operation, Action<string> output)
    {
        using var document = JsonDocument.Parse("{\"operationId\":" + JsonSerializer.Serialize(Text(operation, "operationId")) + ",\"requestDigest\":" + JsonSerializer.Serialize(Text(operation, "digest")) + "}");
        var reference = document.RootElement.Clone();
        async Task Watch()
        {
            try
            {
                await host.ObserveOutputAsync(reference, (update, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var segment in update.Segments) output("[" + segment.Channel + "] " + (segment.Text ?? "[binary output]"));
                    if (update.HasPresentationGap) output("[output gap: use original operation status]");
                    if (update.SealVerified) output("[output seal verified; process result is separate]");
                    return Task.CompletedTask;
                }, observation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
            catch (Exception error) { output("[output unconfirmed] " + (error is TansrException sdk ? sdk.Code : error.GetType().Name)); }
        }
        lock (gate) { outputs.RemoveAll(x => x.IsCompleted); outputs.Add(Task.Run(Watch)); }
    }
#endif
    internal Task StopAsync()
    {
#if WINDOWS || NETFRAMEWORK
        lock (gate) return stopTask ??= StopCoreAsync();
#else
        return Task.CompletedTask;
#endif
    }
#if WINDOWS || NETFRAMEWORK
    private async Task StopCoreAsync()
    {
        consent.Dispose();
        try { if (host != null) await host.StopAsync().ConfigureAwait(false); }
        finally
        {
            observation.Cancel(); Task[] pending; lock (gate) pending = outputs.ToArray();
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            finally
            {
                try { if (closePublication != null) await closePublication().ConfigureAwait(false); }
                finally
                {
                    try { await journal.CloseAsync().ConfigureAwait(false); }
                    finally { host?.Dispose(); workspace.Dispose(); deviceTerminal.Dispose(); controllerTerminal.Dispose(); if (ownsDevice) device.Dispose(); if (ownsController) controller.Dispose(); observation.Dispose(); }
                }
            }
        }
    }
    internal static string ReadStorageMode(JsonElement config)
    {
        var mode = config.TryGetProperty("storageMode", out var value) ? value.GetString() : "terminal";
        if (mode != "terminal" && mode != "cloud") throw new InvalidOperationException("terminal_device_explicit_storage_mode_required");
        if (mode == "cloud" && (config.TryGetProperty("journal", out _) || config.TryGetProperty("encryption", out _) || config.TryGetProperty("publication", out _)))
            throw new InvalidOperationException("terminal_device_cloud_local_store_forbidden");
        return mode;
    }
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString() is { Length: > 0 } text && !text.Any(char.IsControl) ? text : throw new InvalidOperationException("terminal_device_invalid_" + key);
    private static string Absolute(JsonElement value, string key)
    { var path = Text(value, key); return Path.IsPathRooted(path) && Path.GetFullPath(path) == path ? path : throw new InvalidOperationException("terminal_device_absolute_path_required"); }
#endif
}

#if WINDOWS || NETFRAMEWORK
// Guard 会在原账本/资源边界重复调用。这里只复用一次人审决定，不跳过任何当前授权检查。
internal sealed class NativeOperationConsent : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly int maximum;
    private bool closed;
    internal NativeOperationConsent(int maximum = 256) { if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum)); this.maximum = maximum; }

    internal async Task EnsureAsync(JsonElement operation, Func<CancellationToken, Task<string>> validate,
        Func<CancellationToken, Task<bool>> approve, Action observe, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); token = linked.Token;
        var key = operation.GetProperty("operationId").GetString()!;
        var identity = WireJson.Sha256(System.Text.Encoding.UTF8.GetBytes(WireJson.CanonicalString(operation)));
        Entry entry; bool owner = false; bool decision;
        try
        {
            token.ThrowIfCancellationRequested(); var authority = await validate(token).ConfigureAwait(false);
            lock (gate)
            {
                if (closed) throw new ExecutionRejectedException("ESTALE");
                foreach (var expired in entries.Where(item => item.Value.Expires <= DateTimeOffset.UtcNow && item.Value.Completion.Task.IsCompleted).Select(item => item.Key).ToArray())
                { entries[expired].Stop.Dispose(); entries.Remove(expired); }
                if (entries.TryGetValue(key, out var retained))
                {
                    entry = retained;
                    if (entry.Identity != identity || entry.Authority != authority || entry.Revoked) { entry.Revoked = true; throw new ExecutionRejectedException("ESTALE"); }
                }
                else
                {
                    if (entries.Count >= maximum) throw new ExecutionRejectedException("local_approval_capacity");
                    var expires = DateTimeOffset.Parse(operation.GetProperty("expiresAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                    if (expires <= DateTimeOffset.UtcNow) throw new ExecutionRejectedException("ESTALE");
                    entry = new Entry(identity, authority, expires); entries.Add(key, entry); owner = true;
                }
            }
            using var approval = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Stop.Token);
            token = approval.Token;
            var expiry = CancelAtExpiryAsync(entry.Expires, approval);
            try
            {
                if (owner)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var allowed = await WaitAsync(approve(token), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (await validate(token).ConfigureAwait(false) != authority) throw new ExecutionRejectedException("ESTALE");
                        lock (gate)
                        {
                            if (closed || entry.Revoked || entry.Expires <= DateTimeOffset.UtcNow) throw new ExecutionRejectedException("ESTALE");
                        }
                        if (allowed) observe();
                        // 用户拒绝是该原操作的稳定决定，不等同于配置或身份撤销。
                        lock (gate) { if (closed || entry.Revoked) throw new ExecutionRejectedException("ESTALE"); entry.Completion.TrySetResult(allowed); }
                    }
                    catch (Exception error) { lock (gate) { entry.Revoked = true; entry.Completion.TrySetException(error); } }
                }
                decision = await WaitAsync(entry.Completion.Task, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (await validate(token).ConfigureAwait(false) != authority) throw new ExecutionRejectedException("ESTALE");
                lock (gate) if (closed || entry.Revoked || entry.Expires <= DateTimeOffset.UtcNow) throw new ExecutionRejectedException("ESTALE");
            }
            finally { approval.Cancel(); await expiry.ConfigureAwait(false); }
        }
        catch { Revoke(operation); throw; }
        if (!decision) throw new ExecutionRejectedException("EACCES");
    }

    internal void Revoke(JsonElement operation)
    {
        Entry? revoked;
        lock (gate)
        {
            entries.TryGetValue(operation.GetProperty("operationId").GetString()!, out revoked);
            if (revoked != null) revoked.Revoked = true;
        }
        try { revoked?.Stop.Cancel(); }
        catch (ObjectDisposedException) { } // The same revoked entry has already been retired.
    }

    private static async Task CancelAtExpiryAsync(DateTimeOffset expires, CancellationTokenSource waiting)
    {
        try
        {
            while (!waiting.IsCancellationRequested)
            {
                var remaining = expires - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) { waiting.Cancel(); return; }
                // Delay's platform timer range is not a new approval deadline.
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(int.MaxValue) ? remaining : TimeSpan.FromMilliseconds(int.MaxValue), waiting.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
    }

    private static async Task<T> WaitAsync<T>(Task<T> work, CancellationToken token)
    {
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => canceled.TrySetResult(true));
        if (await Task.WhenAny(work, canceled.Task).ConfigureAwait(false) != work)
        {
            _ = work.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            token.ThrowIfCancellationRequested();
        }
        return await work.ConfigureAwait(false);
    }

    public void Dispose()
    {
        Entry[] retired;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            retired = entries.Values.ToArray();
            foreach (var entry in retired) { entry.Revoked = true; entry.Completion.TrySetException(new ExecutionRejectedException("ESTALE")); }
            entries.Clear();
        }
        lifetime.Cancel(); lifetime.Dispose();
        foreach (var entry in retired) { entry.Stop.Cancel(); entry.Stop.Dispose(); }
    }
    private sealed class Entry(string identity, string authority, DateTimeOffset expires)
    {
        internal readonly string Identity = identity, Authority = authority;
        internal readonly DateTimeOffset Expires = expires;
        internal readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource Stop = new();
        internal bool Revoked;
    }
}
#endif
