using Tansr.Sdk.Client;
using Tansr.Sdk.Terminal;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
#endif

namespace Tansr.Examples;

internal sealed class ExampleConnection
{
    private ExampleConnection(TansrClient client) { Client = client; }
    internal TansrClient Client { get; }
    internal bool OwnsLocalServe { get; private set; }
    internal TerminalSessionControl? SessionControl { get; private set; }
    private NativeMcpToolConnection? _mcp;
    internal IReadOnlyList<NativeToolBinding> NativeTools => _mcp?.Bindings ?? Array.Empty<NativeToolBinding>();
#if WINDOWS || NETFRAMEWORK
    private WindowsWorkspace? _workspace;
    private LocalServeHost? _serve;
#endif
    internal static async Task<ExampleConnection> ConnectAsync(string? endpoint, Func<CancellationToken, Task<string>> tokenProvider, bool allowHttp, CancellationToken ct = default)
    {
        var scope = TrustedExampleScope.FromEnvironment();
        var preview = Environment.GetEnvironmentVariable("TANSR_TERMINAL_PREVIEW") == "1";
        if (preview && scope == null) throw new InvalidOperationException("terminal_preview_requires_trusted_scope_file");
        var executable = Environment.GetEnvironmentVariable("TANSR_LOCAL_SERVE_EXE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("TANSR_SERVE_URL_required");
            var connection = new ExampleConnection(new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri(endpoint),
                TokenProvider = tokenProvider,
                AllowInsecureLoopback = allowHttp,
                MaxResponseBytes = 32 * 1024 * 1024,
                MaxEventBytes = 32 * 1024 * 1024,
                PrincipalProvider = scope == null ? null : scope.ReadPrincipal,
                ExecutionScopeProvider = scope == null ? null : scope.ReadScope
            }));
            try { if (preview) connection.SessionControl = new TerminalSessionControl(connection.Client, true); connection._mcp = await NativeMcpToolConnection.OpenConfiguredAsync(ct); return connection; }
            catch { await connection.CloseAsync(); throw; }
        }
#if WINDOWS || NETFRAMEWORK
        var path = Environment.GetEnvironmentVariable("TANSR_LOCAL_WORKSPACE") ?? throw new InvalidOperationException("TANSR_LOCAL_WORKSPACE_required");
        var digest = Environment.GetEnvironmentVariable("TANSR_LOCAL_SERVE_SHA256") ?? throw new InvalidOperationException("TANSR_LOCAL_SERVE_SHA256_required");
        var workspace = new WindowsWorkspace(path);
        LocalServeHost? serve = null;
        ExampleConnection? ownedConnection = null;
        try
        {
            var options = new LocalServeHostOptions(executable!, digest, workspace);
            var port = Environment.GetEnvironmentVariable("TANSR_LOCAL_SERVE_PORT"); if (!string.IsNullOrEmpty(port)) options.Port = int.Parse(port, System.Globalization.CultureInfo.InvariantCulture);
            if (Environment.GetEnvironmentVariable("TANSR_LOCAL_SERVE_DIRECT") == "1") options.CommandPrefix = Array.Empty<string>();
            foreach (var name in (Environment.GetEnvironmentVariable("TANSR_LOCAL_ENV_NAMES") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var key = name.Trim(); var value = Environment.GetEnvironmentVariable(key);
                if (value != null) options.Environment.Add(key, value);
            }
            serve = await LocalServeHost.StartAsync(options, ct);
            ownedConnection = new ExampleConnection(serve.CreateClient(principalProvider: scope == null ? null : scope.ReadPrincipal, executionScopeProvider: scope == null ? null : scope.ReadScope,
                maximumResponseBytes: 32 * 1024 * 1024, maximumEventBytes: 32 * 1024 * 1024)) { _serve = serve, _workspace = workspace, OwnsLocalServe = true };
            if (preview) ownedConnection.SessionControl = new TerminalSessionControl(ownedConnection.Client, true);
            ownedConnection._mcp = await NativeMcpToolConnection.OpenConfiguredAsync(ct); return ownedConnection;
        }
        catch
        {
            if (ownedConnection != null) await ownedConnection.CloseAsync();
            else { try { if (serve != null) await serve.StopAsync(); } finally { workspace.Dispose(); } }
            throw;
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("本地 Serve 启动需要 ConsoleAssistant 的 net10.0-windows 目标；跨平台 net10.0 可直接连接远端 Serve。");
#endif
    }
    internal async Task CloseAsync()
    {
        try { if (_mcp != null) await _mcp.CloseAsync(); }
        finally
        {
            Client.Dispose();
#if WINDOWS || NETFRAMEWORK
            if (_serve != null)
            {
                var serve = _serve; _serve = null;
                try { await serve.StopAsync(); } finally { serve.Dispose(); _workspace?.Dispose(); _workspace = null; }
            }
#endif
        }
    }
}
