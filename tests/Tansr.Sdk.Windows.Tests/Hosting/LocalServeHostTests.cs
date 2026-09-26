using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class LocalServeHostTests : IClassFixture<LocalServeFixture>, IDisposable
{
    private readonly LocalServeFixture program;
    private readonly string root = Path.Combine(Path.GetTempPath(), "tansr-local-serve-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsWorkspace workspace;
    public LocalServeHostTests(LocalServeFixture program)
    { this.program = program; Directory.CreateDirectory(root); workspace = new WindowsWorkspace(root); }

    [Fact]
    public async Task AuthenticatedReadyClientAndStopUseOwnedProcessWithoutNode()
    {
        using var host = await LocalServeHost.StartAsync(Options("healthy"));
        Assert.True(host.IsReady);
        using var client = host.CreateClient();
        var sessions = await client.ListSessionsAsync();
        Assert.Equal(0, sessions.GetProperty("total").GetInt32());
        var pid = host.ProcessId;
        var exit = await host.StopAsync();
        Assert.True(exit.CleanupConfirmed); Assert.True(exit.IoSettled);
        Assert.False(host.IsReady); Exited(pid);
        await Assert.ThrowsAsync<TansrProtocolException>(() => client.ListSessionsAsync());
        Assert.Throws<TansrProtocolException>(() => host.CreateClient());
    }

    [Fact]
    public async Task ColdStartWaitsForTheOriginalProcessToListen()
    {
        using var host = await LocalServeHost.StartAsync(Options("slow"));
        Assert.True(host.IsReady);
        var exit = await host.StopAsync();
        Assert.True(exit.CleanupConfirmed);
    }

    [Fact]
    public async Task TrustedHeadersAreFrozenForBothReadinessChallengesAndNotGrantedToCreatedClients()
    {
        var options = Options("ticket-slow"); var headers = new Dictionary<string, string> { ["x-tansr-demo-user-token"] = "synthetic-fixed-user-ticket" }; options.ReadinessHeaders = headers;
        var records = Path.Combine(root, "readiness.txt");
        options.Environment["TANSR_FIXTURE_USER_TOKEN"] = "synthetic-fixed-user-ticket";
        options.Environment["TANSR_FIXTURE_REQUESTS"] = records;
        var starting = LocalServeHost.StartAsync(options); headers["x-tansr-demo-user-token"] = "changed-after-start";
        using var host = await starting; Assert.True(host.IsReady);
        var observed = await File.ReadAllLinesAsync(records);
        Assert.Equal(new[] { "synthetic-fixed-user-ticket|invalid-token", "synthetic-fixed-user-ticket|valid-token" }, observed);
        using var anonymousClient = host.CreateClient();
        await Assert.ThrowsAsync<TansrProtocolException>(() => anonymousClient.ListSessionsAsync());
        Assert.Equal("<missing>|valid-token", (await File.ReadAllLinesAsync(records)).Last());
        var clientHeaders = new Dictionary<string, string> { ["x-tansr-demo-user-token"] = "synthetic-fixed-user-ticket" };
        using var authenticatedClient = host.CreateClient(SessionContract.Sdk1, () => "local-binding-not-user-ticket", null, 2097152, 2097152, clientHeaders);
        clientHeaders["x-tansr-demo-user-token"] = "changed-after-client-construction";
        Assert.Equal(0, (await authenticatedClient.ListSessionsAsync()).GetProperty("total").GetInt32());
        Assert.True((await host.StopAsync()).CleanupConfirmed);
    }

    [Fact]
    public async Task RequiredApplicationTicketIsNotBypassedByTheRandomToken()
    {
        var options = Options("ticket"); options.Environment["TANSR_FIXTURE_USER_TOKEN"] = "synthetic-fixed-user-ticket";
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => LocalServeHost.StartAsync(options));
        Assert.Equal("serve_authenticated_readiness_failed", error.Code); Exited(int.Parse(File.ReadAllText(Path.Combine(root, "pid.txt"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("user\r\nInjected: true")]
    [InlineData("user\0")]
    [InlineData("用户")]
    public async Task InvalidReadinessHeaderCannotStartAProcess(string principal)
    {
        var options = Options("healthy"); options.ReadinessHeaders = new Dictionary<string, string> { ["x-tansr-demo-user-token"] = principal };
        await Assert.ThrowsAsync<ArgumentException>(() => LocalServeHost.StartAsync(options));
        Assert.False(File.Exists(Path.Combine(root, "pid.txt")));
    }

    [Fact]
    public async Task ReadinessHeadersCannotOverrideBearerAndUseTheSameControlledBounds()
    {
        var options = Options("healthy"); options.ReadinessHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer forbidden" };
        await Assert.ThrowsAsync<ArgumentException>(() => LocalServeHost.StartAsync(options));
        options.ReadinessHeaders = new Dictionary<string, string> { ["x-tansr-demo-user-token"] = new string('x', 4097) };
        await Assert.ThrowsAsync<ArgumentException>(() => LocalServeHost.StartAsync(options));
        Assert.False(File.Exists(Path.Combine(root, "pid.txt")));
    }

    [Fact]
    public async Task ImmediateExitIsReportedWithoutUsingADisposedLifetime()
    {
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => LocalServeHost.StartAsync(Options("exit")));
        Assert.Contains(error.Code, new[] { "serve_exited_before_ready", "serve_owner_exited", "process_unavailable" });
        Exited(int.Parse(File.ReadAllText(Path.Combine(root, "pid.txt"))));
    }

    [Fact]
    public async Task WrongCandidateDigestFailsBeforeProcessStart()
    {
        var options = new LocalServeHostOptions(program.Executable, new string('a', 64), workspace) { Port = Port() };
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => LocalServeHost.StartAsync(options));
        Assert.Equal("serve_binary_mismatch", error.Code);
        Assert.False(File.Exists(Path.Combine(root, "pid.txt")));
    }

    [Fact]
    public async Task AnonymousReadinessIsRejectedAndOwnedProcessReaped()
    {
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => LocalServeHost.StartAsync(Options("anonymous")));
        Assert.Equal("serve_authentication_not_enforced", error.Code);
        Exited(int.Parse(File.ReadAllText(Path.Combine(root, "pid.txt"))));
    }

    [Fact]
    public async Task StartupTimeoutReapsProcessRatherThanRestartingIt()
    {
        var options = Options("stall"); options.StartupTimeout = TimeSpan.FromSeconds(2);
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => LocalServeHost.StartAsync(options));
        Assert.Equal("serve_start_timeout", error.Code);
        Exited(int.Parse(File.ReadAllText(Path.Combine(root, "pid.txt"))));
    }

    [Fact]
    public async Task ReservedAuthenticationAndListenOverridesAreRejected()
    {
        var options = Options("healthy"); options.AdditionalArguments = ["--host=0.0.0.0"];
        await Assert.ThrowsAsync<ArgumentException>(() => LocalServeHost.StartAsync(options));
        options = Options("healthy"); options.Environment["TANSR_SERVE_TOKEN"] = "host-must-not-accept-this";
        await Assert.ThrowsAsync<ArgumentException>(() => LocalServeHost.StartAsync(options));
        Assert.False(File.Exists(Path.Combine(root, "pid.txt")));
    }

    private LocalServeHostOptions Options(string mode)
    {
        var options = new LocalServeHostOptions(program.Executable, program.Digest, workspace)
        { Port = Port(), CommandPrefix = [mode], StartupTimeout = TimeSpan.FromSeconds(10) };
        options.Environment["TANSR_FIXTURE_PID"] = Path.Combine(root, "pid.txt");
        return options;
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static void Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }
    public void Dispose() { workspace.Dispose(); Directory.Delete(root, true); }
}

/// <summary>真实本地HTTP+随机令牌探针；模拟Serve已冻结列表路径，不启动模型、不访问外网。</summary>
public sealed class LocalServeFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tansr-local-serve-fixture-" + Guid.NewGuid().ToString("N"));
    public LocalServeFixture()
    {
        Directory.CreateDirectory(root); Executable = Path.Combine(root, "serve-fixture.exe");
        var source = Path.Combine(root, "serve-fixture.cs"); File.WriteAllText(source, Source, new UTF8Encoding(false));
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        using var process = Process.Start(new ProcessStartInfo(compiler, "/nologo /target:exe /out:\"" + Executable + "\" \"" + source + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("fixture compile timeout"); }
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        using var file = File.OpenRead(Executable); Digest = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
    public string Executable { get; }
    public string Digest { get; }
    public void Dispose() => Directory.Delete(root, true);
    private const string Source = """
        using System;
        using System.IO;
        using System.Net;
        using System.Net.Sockets;
        using System.Text;
        using System.Threading;
        using System.Diagnostics;
        class Program {
          static void Main(string[] args) {
            File.WriteAllText(Environment.GetEnvironmentVariable("TANSR_FIXTURE_PID"),Process.GetCurrentProcess().Id.ToString());
            if(args[0]=="exit") return;
            if(args[0]=="slow" || args[0]=="ticket-slow") Thread.Sleep(500);
            if(args[0]=="stall") { Thread.Sleep(60000); return; }
            int port=0;
            for(int i=0;i<args.Length-1;i++) if(args[i]=="--port")port=int.Parse(args[i+1]);
            var listener=new TcpListener(IPAddress.Loopback,port); listener.Start();
            while(true) using(var peer=listener.AcceptTcpClient()) using(var stream=peer.GetStream()) {
              var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
              string line=reader.ReadLine(), auth=null, principal=null; int size=0;
              while((line=reader.ReadLine())!=null && line.Length>0) {
                size+=line.Length;if(size>65536)throw new Exception("header cap");
                if(line.StartsWith("Authorization:",StringComparison.OrdinalIgnoreCase))auth=line.Substring(14).Trim();
                if(line.StartsWith("x-tansr-demo-user-token:",StringComparison.OrdinalIgnoreCase))principal=line.Substring("x-tansr-demo-user-token:".Length).Trim();
              }
              bool validToken=auth=="Bearer "+Environment.GetEnvironmentVariable("TANSR_SERVE_TOKEN");
              var records=Environment.GetEnvironmentVariable("TANSR_FIXTURE_REQUESTS");
              if(records!=null)File.AppendAllText(records,(principal??"<missing>")+"|"+(validToken?"valid-token":"invalid-token")+"\n");
              bool allowed=args[0]=="anonymous" || validToken && (!args[0].StartsWith("ticket") || principal==Environment.GetEnvironmentVariable("TANSR_FIXTURE_USER_TOKEN"));
              var body=allowed?"{\"sessions\":[],\"total\":0}":"{\"error\":{\"code\":\"unauthorized\"}}";
              var response="HTTP/1.1 "+(allowed?"200 OK":"401 Unauthorized")+"\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body;
              var bytes=Encoding.UTF8.GetBytes(response);stream.Write(bytes,0,bytes.Length);stream.Flush();
            }
          }
        }
        """;
}
