using Tansr.Sdk.Client;
using Tansr.Sdk.Terminal;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
#endif

namespace Tansr.Examples;

internal sealed class ExampleConnection
{
    private ExampleConnection(TansrClient client, Uri endpoint, SessionContract sessionContract) { Client = client; Endpoint = endpoint; Contract = sessionContract; }
    internal TansrClient Client { get; }
    internal Uri Endpoint { get; }
    internal SessionContract Contract { get; }
    internal bool OwnsLocalServe { get; private set; }
    internal TerminalSessionControl? SessionControl { get; private set; }
    internal TerminalObservationClient? Observation { get; private set; }
    internal TerminalProfileClient? Profile { get; private set; }
    private NativeMcpToolConnection? _mcp;
    private NativeSkillConnection? _skills;
    internal IReadOnlyList<NativeToolBinding> NativeTools => (_mcp?.Bindings ?? Array.Empty<NativeToolBinding>()).Concat(_skills?.Bindings ?? Array.Empty<NativeToolBinding>()).ToArray();
    internal string DescribeServices() => (OwnsLocalServe ? "受控本地 Serve（本实例拥有）" : "远端 Serve（本实例不拥有）") +
        "\n会话合同：" + Contract + "；终端 preview：" + (SessionControl == null ? "未启用" : "显式启用") +
        "\n本机业务工具：application_info、set_window_title" +
        "\nMCP 连接：" + (_mcp == null ? "未配置" : "已连接；白名单=" + string.Join(", ", NativeTools.Select(x => x.Name))) +
        "\n本机 Skills：" + (_skills == null ? "未配置或已撤销" : "已加载受信目录/内联目录；通过 native_skill 按需取材") +
        "\n可信 profile 请求：" + (Environment.GetEnvironmentVariable("TANSR_PROFILE") ?? "服务端默认") +
        "\n模型循环、上下文、权限裁决、记忆选择及计费由 Serve/kernel 执行。";
#if WINDOWS || NETFRAMEWORK
    private WindowsWorkspace? _workspace;
    private LocalServeHost? _serve;
#endif
    internal static async Task<ExampleConnection> ConnectAsync(string? endpoint, Func<CancellationToken, Task<string>> tokenProvider, bool allowHttp, CancellationToken ct = default)
    {
        var scope = TrustedExampleScope.FromEnvironment();
        var contract = ExampleSessionOptions.ReadContract();
        if (contract == SessionContract.Sdk2OffloadV1 && scope == null) throw new InvalidOperationException("sdk2_requires_trusted_scope_file");
        var preview = Environment.GetEnvironmentVariable("TANSR_TERMINAL_PREVIEW") == "1";
        var userToken = Environment.GetEnvironmentVariable("TANSR_SERVE_USER_TOKEN");
        IReadOnlyDictionary<string, string>? hostHeaders = string.IsNullOrEmpty(userToken) ? null : new Dictionary<string, string> { ["x-tansr-demo-user-token"] = userToken! };
        if (preview && scope == null) throw new InvalidOperationException("terminal_preview_requires_trusted_scope_file");
        var executable = Environment.GetEnvironmentVariable("TANSR_LOCAL_SERVE_EXE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("TANSR_SERVE_URL_required");
            var connection = new ExampleConnection(new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri(endpoint),
                SessionContract = contract,
                TokenProvider = tokenProvider,
                AllowInsecureLoopback = allowHttp,
                MaxResponseBytes = 32 * 1024 * 1024,
                MaxEventBytes = 32 * 1024 * 1024,
                PrincipalProvider = scope == null ? null : scope.ReadPrincipal,
                ExecutionScopeProvider = scope == null ? null : scope.ReadScope,
                AdditionalRequestHeaders = hostHeaders
            }), new Uri(endpoint), contract);
            try { if (preview) { connection.SessionControl = new TerminalSessionControl(connection.Client, true); connection.Observation = new TerminalObservationClient(connection.Client, true); connection.Profile = new TerminalProfileClient(connection.Client, true); } connection._mcp = await NativeMcpToolConnection.OpenConfiguredAsync(ct); connection._skills = await NativeSkillConnection.OpenConfiguredAsync(ct); return connection; }
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
            options.ReadinessHeaders = hostHeaders;
            options.AdditionalArguments = ExampleSessionOptions.ReadLocalHostModuleArguments();
            var port = Environment.GetEnvironmentVariable("TANSR_LOCAL_SERVE_PORT"); if (!string.IsNullOrEmpty(port)) options.Port = int.Parse(port, System.Globalization.CultureInfo.InvariantCulture);
            if (Environment.GetEnvironmentVariable("TANSR_LOCAL_SERVE_DIRECT") == "1") options.CommandPrefix = Array.Empty<string>();
            foreach (var name in (Environment.GetEnvironmentVariable("TANSR_LOCAL_ENV_NAMES") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var key = name.Trim(); var value = Environment.GetEnvironmentVariable(key);
                if (value != null) options.Environment.Add(key, value);
            }
            serve = await LocalServeHost.StartAsync(options, ct);
            ownedConnection = new ExampleConnection(serve.CreateClient(contract: contract, principalProvider: scope == null ? null : scope.ReadPrincipal, executionScopeProvider: scope == null ? null : scope.ReadScope,
                maximumResponseBytes: 32 * 1024 * 1024, maximumEventBytes: 32 * 1024 * 1024, additionalRequestHeaders: hostHeaders), serve.BaseUri, contract) { _serve = serve, _workspace = workspace, OwnsLocalServe = true };
            if (preview) { ownedConnection.SessionControl = new TerminalSessionControl(ownedConnection.Client, true); ownedConnection.Observation = new TerminalObservationClient(ownedConnection.Client, true); ownedConnection.Profile = new TerminalProfileClient(ownedConnection.Client, true); }
            ownedConnection._mcp = await NativeMcpToolConnection.OpenConfiguredAsync(ct); ownedConnection._skills = await NativeSkillConnection.OpenConfiguredAsync(ct); return ownedConnection;
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
        try { await RevokeExtensionsAsync(); }
        finally
        {
            Profile?.Dispose(); Observation?.Dispose(); Client.Dispose();
#if WINDOWS || NETFRAMEWORK
            if (_serve != null)
            {
                var serve = _serve; _serve = null;
                try { await serve.StopAsync(); } finally { serve.Dispose(); _workspace?.Dispose(); _workspace = null; }
            }
#endif
        }
    }
    internal async Task RevokeExtensionsAsync()
    {
        var mcp = _mcp; var skills = _skills;
        try { if (mcp != null) { await mcp.CloseAsync(); _mcp = null; } }
        finally { if (skills != null) { await skills.RevokeAsync(); _skills = null; } }
    }
}
