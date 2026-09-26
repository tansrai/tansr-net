using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Hosting;

public sealed class LocalServeHostOptions
{
    public LocalServeHostOptions(string trustedExecutablePath, string executableSha256, WindowsWorkspace workingDirectory)
    { TrustedExecutablePath = trustedExecutablePath; ExecutableSha256 = executableSha256; WorkingDirectory = workingDirectory; }
    public string TrustedExecutablePath { get; }
    /// <summary>宿主批准的已安装候选摘要。升级/回滚显式选择另一候选，不从网络自动下载可执行代码。</summary>
    public string ExecutableSha256 { get; }
    public WindowsWorkspace WorkingDirectory { get; }
    public int Port { get; set; } = 7433;
    /// <summary>完整 CLI 可执行文件默认使用 serve 子命令；专用 Serve 入口可显式设为空。</summary>
    public IReadOnlyList<string> CommandPrefix { get; set; } = new[] { "serve" };
    public IReadOnlyList<string> AdditionalArguments { get; set; } = Array.Empty<string>();
    /// <summary>仅透传宿主明确选定的环境，不继承其它凭据。TANSR_SERVE_TOKEN 由本宿主管理。</summary>
    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Windows 上受控、只监听 127.0.0.1 的本地 Serve。随机认证仅经环境传入子进程，stdout/stderr 排空但不保存。
/// 可消费独立 Serve 安装物；SDK 不要求 Node，不下载程序，不把进程所有权当作 OS 沙箱。
/// Dispose 只请求停止；须等待 StopAsync/Completion 才可释放工作区或选择下一安装候选。
/// </summary>
public sealed class LocalServeHost : IDisposable
{
    private readonly WindowsDuplexProcess _process;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private bool _exited;
    private string? _token;
    private int _ready, _disposed;
    private LocalServeHost(WindowsDuplexProcess process, CancellationTokenSource lifetime, string token, int port)
    {
        _process = process; _lifetime = lifetime; _token = token;
        BaseUri = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
        Completion = ObserveExitAsync();
    }

    public Uri BaseUri { get; }
    public int ProcessId => _process.ProcessId;
    public bool IsReady { get { lock (_gate) return !_exited && Volatile.Read(ref _ready) == 1 && !_process.Completion.IsCompleted && !_lifetime.IsCancellationRequested; } }
    /// <summary>真实进程树及管道清理结果，不将发出取消请求写成清理成功。</summary>
    public Task<WindowsDuplexProcessExit> Completion { get; }

    public static async Task<LocalServeHost> StartAsync(LocalServeHostOptions options, CancellationToken cancellationToken = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.WorkingDirectory == null || options.Port < 1 || options.Port > 65535 ||
            options.StartupTimeout <= TimeSpan.Zero || options.StartupTimeout > TimeSpan.FromMinutes(10) ||
            options.CleanupTimeout <= TimeSpan.Zero || options.CleanupTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.ExecutableSha256 == null || options.ExecutableSha256.Length != 64 ||
            options.ExecutableSha256.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("需要已批准安装物的SHA256。", nameof(options));
        var arguments = (options.CommandPrefix ?? throw new ArgumentException("缺少命令前缀。", nameof(options)))
            .Concat(options.AdditionalArguments ?? throw new ArgumentException("缺少参数集合。", nameof(options))).ToList();
        var ownedFlags = new[] { "--host", "--port", "--token", "--v2", "--cwd" };
        if (arguments.Any(arg => arg == null || ownedFlags.Any(flag => arg == flag || arg.StartsWith(flag + "=", StringComparison.Ordinal))))
            throw new ArgumentException("监听、鉴权及工作区参数由本地宿主管理。", nameof(options));
        arguments.AddRange(new[] { "--v2", "--host", "127.0.0.1", "--port", options.Port.ToString(CultureInfo.InvariantCulture), "--cwd", options.WorkingDirectory.RootDirectory });
        cancellationToken.ThrowIfCancellationRequested();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        LocalServeHost? host = null;
        try
        {
            // 在进程退出观察器接管 lifetime 前登记启动取消；极速退出可同步释放 lifetime。
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = Secret();
            var processOptions = new WindowsDuplexProcessOptions(options.TrustedExecutablePath, arguments,
                () => options.WorkingDirectory.AcquireProcessDirectory(""))
            {
                StandardOutputMode = WindowsDuplexProcessOutputMode.Drain,
                StandardErrorOverflow = WindowsDuplexProcessErrorOverflow.Drain,
                CleanupTimeout = options.CleanupTimeout,
                ExpectedExecutableSha256 = options.ExecutableSha256,
            };
            foreach (var item in options.Environment)
            {
                if (item.Key.Equals("TANSR_SERVE_TOKEN", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("不能覆盖本次启动的认证身份。", nameof(options));
                processOptions.Environment.Add(item.Key, item.Value);
            }
            processOptions.Environment["TANSR_SERVE_TOKEN"] = token;
            processOptions.Environment["TANSR_SERVE_V2"] = "1";
            // Windows 网络提供方需要系统目录变量；来源是系统API，不继承任意父进程环境。
            processOptions.Environment["SystemRoot"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
            WindowsDuplexProcess process;
            try { process = await WindowsDuplexProcess.StartAsync(processOptions, lifetime.Token).ConfigureAwait(false); }
            catch (WindowsDuplexProcessException error) when (error.Code == "executable_digest_mismatch")
            { throw new TansrProtocolException("serve_binary_mismatch"); }
            // 原生层先固定完整路径与最终文件句柄，再从同一文件对象核摘要并启动，无二次路径解析窗口。
            host = new LocalServeHost(process, lifetime, token, options.Port);
            startup.CancelAfter(options.StartupTimeout);
            try { await host.WaitReadyAsync(startup.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && process.Completion.IsCompleted)
            { throw new TansrProtocolException("serve_exited_before_ready"); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !process.Completion.IsCompleted)
            { throw new TansrProtocolException("serve_start_timeout"); }
            return host;
        }
        catch
        {
            if (host != null) await host.StopAsync().ConfigureAwait(false);
            else { lifetime.Cancel(); lifetime.Dispose(); }
            throw;
        }
    }

    /// <summary>每个调用返回独立客户端。客户端不会越过本次子进程寿命向下一进程继续发送认证。</summary>
    public TansrClient CreateClient(SessionContract contract = SessionContract.Sdk1, Func<string>? principalProvider = null,
        Func<JsonElement>? executionScopeProvider = null, int maximumResponseBytes = 2 * 1024 * 1024, int maximumEventBytes = 2 * 1024 * 1024)
    {
        RequireReady();
        var http = new HttpClient(new OwnedServeHttpHandler(BaseUri, _process)) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            return new TansrClient(new TansrClientOptions
            {
                BaseUri = BaseUri,
                AllowInsecureLoopback = true,
                SessionContract = contract,
                MaxResponseBytes = maximumResponseBytes,
                MaxEventBytes = maximumEventBytes,
                PrincipalProvider = principalProvider,
                ExecutionScopeProvider = executionScopeProvider,
                TokenProvider = ct => { ct.ThrowIfCancellationRequested(); RequireReady(); return Task.FromResult(_token!); },
            }, http, disposeInjectedClient: true);
        }
        catch { http.Dispose(); throw; }
    }

    public async Task<WindowsDuplexProcessExit> StopAsync()
    {
        RequestStop();
        await _process.CloseAsync().ConfigureAwait(false);
        return await Completion.ConfigureAwait(false);
    }

    private void RequireReady()
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsReady) throw new TansrProtocolException("serve_not_ready");
    }

    private async Task WaitReadyAsync(CancellationToken ct)
    {
        using var http = new HttpClient(new OwnedServeHttpHandler(BaseUri, _process)) { Timeout = Timeout.InfiniteTimeSpan };
        var invalidToken = Secret();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.Completion.IsCompleted) throw new TansrProtocolException("serve_exited_before_ready");
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var challenge = await ProbeAsync(http, invalidToken, attempt.Token).ConfigureAwait(false);
                if (challenge.StatusCode != HttpStatusCode.Unauthorized && challenge.StatusCode != HttpStatusCode.Forbidden)
                    throw new TansrProtocolException("serve_authentication_not_enforced");
                using var response = await ProbeAsync(http, _token!, attempt.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json")
                    throw new TansrProtocolException("serve_authenticated_readiness_failed");
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var body = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, attempt.Token).ConfigureAwait(false)) != 0)
                {
                    if (body.Length + read > 65536) throw new TansrProtocolException("serve_readiness_too_large");
                    body.Write(buffer, 0, read);
                }
                var json = WireJson.Parse(body.ToArray(), 65536);
                if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
                    throw new TansrProtocolException("serve_readiness_invalid");
                ct.ThrowIfCancellationRequested();
                if (_process.Completion.IsCompleted) throw new TansrProtocolException("serve_exited_before_ready");
                Volatile.Write(ref _ready, 1);
                return;
            }
            catch (HttpRequestException) { /* 尚未监听；只重试只读就绪探针，不重启程序。 */ }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused || error.SocketErrorCode == SocketError.TimedOut)
            { /* 自有TCP通道不包装SocketException；冷启动仍受同一个启动期限限制。 */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private Task<HttpResponseMessage> ProbeAsync(HttpClient client, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "/v2/sessions?limit=1&offset=0"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return SendProbeAsync(client, request, ct);
    }
    private static async Task<HttpResponseMessage> SendProbeAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    { using (request) return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }

    private async Task<WindowsDuplexProcessExit> ObserveExitAsync()
    {
        try { return await _process.Completion.ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                Volatile.Write(ref _ready, 0); _token = null; _exited = true;
                _lifetime.Cancel(); _lifetime.Dispose();
            }
        }
    }
    private static string Secret()
    {
        var value = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(value);
        try { return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { Array.Clear(value, 0, value.Length); }
    }
    private void RequestStop() { lock (_gate) if (!_exited) _lifetime.Cancel(); }
    public void Dispose()
    { if (Interlocked.Exchange(ref _disposed, 1) == 0) RequestStop(); }
}
