using System.Text.Json;
using Tansr.Sdk.Client;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;
#endif

namespace Tansr.Examples;

// 此入口只拥有设备资源和两种角色的传输；不会关闭会话或另建记忆决策循环。
internal sealed class NativeMemoryDeviceHost
{
    internal string SessionId { get; }
#if WINDOWS || NETFRAMEWORK
    private readonly NativeDeviceHost device;
    private readonly Func<Task> closeStore;
    private readonly Func<CancellationToken, Task<string>> readCapacity;
    private readonly TansrClient controllerClient, deviceClient;
    private Task? stopTask;
    private readonly object gate = new();
    private NativeMemoryDeviceHost(string sessionId, NativeDeviceHost device, Func<Task> closeStore, Func<CancellationToken, Task<string>> readCapacity, TansrClient controllerClient, TansrClient deviceClient)
    { SessionId = sessionId; this.device = device; this.closeStore = closeStore; this.readCapacity = readCapacity; this.controllerClient = controllerClient; this.deviceClient = deviceClient; }
    internal Task Completion => device.Completion;
#else
    private NativeMemoryDeviceHost() { SessionId = ""; }
    internal Task Completion => Task.CompletedTask;
#endif

    internal static async Task<NativeMemoryDeviceHost> StartAsync(string configurationPath, CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var configuration = await NativeMemoryDeviceConfiguration.LoadAsync(configurationPath, ct).ConfigureAwait(false);
        var authority = TrustedExampleScope.FromPath(configuration.ScopeFile);
        var controller = CreateClient(configuration, authority, configuration.ControllerTokenEnvironment);
        TansrClient? executor = null; Func<Task>? closeStore = null; NativeDeviceHost? device = null;
        try
        {
            var key = configuration.KeyMode == "create"
                ? CurrentUserDpapiArchiveKeyProvider.Create(configuration.KeyPath, configuration.KeyId)
                : CurrentUserDpapiArchiveKeyProvider.Open(configuration.KeyPath, configuration.KeyId);
            executor = CreateClient(configuration, authority, configuration.DeviceTokenEnvironment);
            WindowsBusinessTool tool;
            Func<CancellationToken, Task<string>> readCapacity;
            if (configuration.PublicationProfile == "terminal-persistence-v1")
            {
                var store = await SqliteTerminalPersistenceStore.OpenAsync(new SqliteTerminalPersistenceOptions
                {
                    EnableProfile = true, Path = configuration.PublicationPath, Mode = OpenMode(configuration.PublicationMode),
                    Identity = configuration.PublicationIdentity, ReadContext = authority.ReadScope, KeyProvider = key,
                    MaxActiveTransfers = configuration.MaxTransfers, MaxStagingBytes = configuration.MaxStagingBytes, MaxPages = configuration.MaxPages,
                }, ct).ConfigureAwait(false);
                closeStore = () => store.CloseAsync();
                tool = new WindowsTerminalPersistenceHost(store, enableProfile: true).CreateTool();
                // Read-only status never fabricates a new execution owner to query the store.
                readCapacity = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(" profile=terminal-persistence-v1；容量以原执行通道的 head 回执为准"); };
            }
            else
            {
                var store = await SqliteMemoryPublicationStore.OpenAsync(new SqliteMemoryPublicationOptions
                {
                    EnablePreview = true, Path = configuration.PublicationPath, Mode = OpenMode(configuration.PublicationMode),
                    Identity = configuration.PublicationIdentity, ReadContext = authority.ReadScope, KeyProvider = key,
                    MaxTransfers = configuration.MaxTransfers, MaxStagingBytes = configuration.MaxStagingBytes, MaxPages = configuration.MaxPages,
                }, ct).ConfigureAwait(false);
                closeStore = () => store.CloseAsync();
                tool = new WindowsMemoryPublicationHost(store, enablePreview: true, requireEncryption: true).CreateTool();
                readCapacity = async token =>
                {
                    var capacity = await store.GetCapacityAsync(token).ConfigureAwait(false);
                    return " transfers=" + capacity.StoredTransfers + "/" + capacity.MaxTransfers + " stagingBytes=" + capacity.StagingBytes + "/" + capacity.MaxStagingBytes + " pages=" + capacity.AllocatedPages + "/" + capacity.MaxPages;
                };
            }
            device = await NativeDeviceHost.StartAsync(controller, executor, configuration.SessionId, authority.ReadScope,
                configuration.WorkspacePath, configuration.WorkspaceId, configuration.WorkspaceRevision,
                new SqliteExecutorJournalOptions
                {
                    Path = configuration.JournalPath, Mode = OpenMode(configuration.JournalMode), ExecutorId = configuration.ExecutorId,
                    KeyProvider = key, CompactCompletedReceipts = true,
                    MaxOperations = configuration.JournalMaxOperations, MaxStoredBytes = configuration.JournalMaxStoredBytes, MaxPages = configuration.JournalMaxPages,
                }, (operation, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var request = operation.GetProperty("request");
                    if (operation.GetProperty("toolName").GetString() != "MemoryPublication" || request.GetProperty("operation").GetString() != "tool.invoke")
                        throw new ExecutionRejectedException("EACCES");
                    var args = request.GetProperty("args");
                    if (args.GetProperty("name").GetString() != tool.Name || args.GetProperty("definitionDigest").GetString() != tool.DefinitionDigest)
                        throw new ExecutionRejectedException("EACCES");
                    return Task.CompletedTask;
                }, new[] { tool }, cancellationToken: ct).ConfigureAwait(false);
            return new NativeMemoryDeviceHost(configuration.SessionId, device, closeStore, readCapacity, controller, executor);
        }
        catch
        {
            try { if (device != null) await device.StopAsync().ConfigureAwait(false); }
            finally { try { if (closeStore != null) await closeStore().ConfigureAwait(false); } finally { executor?.Dispose(); controller.Dispose(); } }
            throw;
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("设备自动记忆宿主需要 net10.0-windows 或 net48；跨平台 Console 仍可连接远端会话。");
#endif
    }

    internal async Task<string> ReadStatusAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var capacity = await readCapacity(ct).ConfigureAwait(false);
        return "session=" + SessionId + " device=" + device.Host.State +
            capacity +
            "；容量及本机连接状态不证明记忆已排空、模型已消费或远端关闭已耐久。设备保持领取操作，直到显式停止。";
#else
        await Task.CompletedTask; ct.ThrowIfCancellationRequested(); throw new PlatformNotSupportedException();
#endif
    }

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
        try { await device.StopAsync().ConfigureAwait(false); }
        finally { try { await closeStore().ConfigureAwait(false); } finally { deviceClient.Dispose(); controllerClient.Dispose(); } }
    }
    private static StorageOpenMode OpenMode(string mode) => mode == "create" ? StorageOpenMode.Create : StorageOpenMode.Reopen;
    private static TansrClient CreateClient(NativeMemoryDeviceConfiguration configuration, TrustedExampleScope authority, string tokenName) => new(new TansrClientOptions
    {
        BaseUri = configuration.Endpoint, AllowInsecureLoopback = configuration.AllowHttp,
        PrincipalProvider = authority.ReadPrincipal, ExecutionScopeProvider = authority.ReadScope,
        TokenProvider = ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Environment.GetEnvironmentVariable(tokenName) is { Length: > 0 } token ? token : throw new InvalidOperationException("device_memory_token_environment_empty")); },
    });
#endif
}
