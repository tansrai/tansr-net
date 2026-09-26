#if WINDOWS || NETFRAMEWORK
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Examples;

/// <summary>只装配公开SDK；scope/工作区/工具摘要必须由可信应用配置提供，不能采信模型参数。</summary>
internal sealed class NativeDeviceHost
{
    private readonly WindowsWorkspace _workspace;
    private readonly SqliteExecutorJournal _journal;
    internal DeviceSessionHost Host { get; }
    internal Task Completion => Host.Completion;
    private NativeDeviceHost(WindowsWorkspace workspace, SqliteExecutorJournal journal, DeviceSessionHost host)
    { _workspace = workspace; _journal = journal; Host = host; }

    internal static async Task<NativeDeviceHost> StartAsync(TansrClient client, AgentSession session,
        Func<JsonElement> readTrustedScope, string workspacePath, string journalPath, StorageOpenMode journalMode,
        string executorId, string workspaceId, string workspaceRevision,
        Func<JsonElement, CancellationToken, Task> authorizeDeviceOperation,
        IReadOnlyList<WindowsBusinessTool>? tools = null, WindowsWorkspaceOptions? workspaceOptions = null,
        JsonElement? interpreter = null, Func<JsonElement, WindowsWorkspace, WindowsProcessRequest>? processFactory = null,
        CancellationToken cancellationToken = default)
    {
        var scope = readTrustedScope();
        return await StartAsync(client, client, session.Id, readTrustedScope, workspacePath, workspaceId, workspaceRevision,
            new SqliteExecutorJournalOptions
            {
                Path = journalPath, Mode = journalMode, ExecutorId = executorId,
                ApplicationScopeId = scope.GetProperty("applicationScopeId").GetString()!,
                EndUserId = scope.GetProperty("endUserId").GetString()!, ReadContext = readTrustedScope,
            }, authorizeDeviceOperation, tools, workspaceOptions, interpreter, processFactory, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<NativeDeviceHost> StartAsync(TansrClient controllerClient, TansrClient deviceClient, string sessionId,
        Func<JsonElement> readTrustedScope, string workspacePath, string workspaceId, string workspaceRevision,
        SqliteExecutorJournalOptions journalOptions, Func<JsonElement, CancellationToken, Task> authorizeDeviceOperation,
        IReadOnlyList<WindowsBusinessTool>? tools = null, WindowsWorkspaceOptions? workspaceOptions = null,
        JsonElement? interpreter = null, Func<JsonElement, WindowsWorkspace, WindowsProcessRequest>? processFactory = null,
        IReadOnlyList<string>? requestedTools = null, CancellationToken cancellationToken = default)
    {
        var scope = readTrustedScope();
        var workspace = new WindowsWorkspace(workspacePath, workspaceOptions);
        SqliteExecutorJournal? journal = null;
        DeviceSessionHost? host = null;
        try
        {
            journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
            {
                Path = journalOptions.Path, Mode = journalOptions.Mode, ExecutorId = journalOptions.ExecutorId,
                MaxOperations = journalOptions.MaxOperations, MaxStoredBytes = journalOptions.MaxStoredBytes, MaxPages = journalOptions.MaxPages,
                ApplicationScopeId = scope.GetProperty("applicationScopeId").GetString()!,
                EndUserId = scope.GetProperty("endUserId").GetString()!, ReadContext = readTrustedScope,
            }, cancellationToken).ConfigureAwait(false);
            var backend = new WindowsExecutorBackend(journalOptions.ExecutorId,
                new[] { new WindowsExecutorWorkspace(workspaceId, workspaceRevision, workspace) }, tools,
                interpreter, processFactory);
            host = new DeviceSessionHost(new ExecutionClient(controllerClient), new ExecutionClient(deviceClient), backend, journal,
                new DeviceSessionOptions { SessionId = sessionId, WorkspaceId = workspaceId, RequestedTools = requestedTools }, authorizeDeviceOperation);
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            return new NativeDeviceHost(workspace, journal, host);
        }
        catch
        {
            if (host != null) { try { await host.StopAsync().ConfigureAwait(false); } catch { } host.Dispose(); }
            journal?.Dispose(); workspace.Dispose(); throw;
        }
    }

    /// <summary>调用者先停止发送，再等待本机执行全部收尾；不删库、不关远端会话。</summary>
    internal async Task StopAsync()
    {
        try { await Host.StopAsync().ConfigureAwait(false); }
        finally { Host.Dispose(); _journal.Dispose(); _workspace.Dispose(); }
    }
}
#endif
