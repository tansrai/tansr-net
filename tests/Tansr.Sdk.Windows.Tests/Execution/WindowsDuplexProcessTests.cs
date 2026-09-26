using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Tests.Execution;

public sealed class WindowsDuplexProcessTests : IDisposable, IClassFixture<WindowsDuplexTestProgram>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-duplex-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsDuplexTestProgram fixture;
    private readonly WindowsWorkspace workspace;

    public WindowsDuplexProcessTests(WindowsDuplexTestProgram fixture)
    {
        this.fixture = fixture;
        Directory.CreateDirectory(directory);
        workspace = new WindowsWorkspace(directory);
    }

    [Fact]
    public async Task MaintainsOneProcessForMultipleUnicodeLineFrames()
    {
        using var process = await WindowsDuplexProcess.StartAsync(Options("split-echo"));
        var pid = process.ProcessId;
        for (var number = 0; number < 5; number++)
        {
            var json = "{\"id\":" + number + ",\"value\":\"中文🙂\"}";
            await process.WriteLineAsync(json);
            Assert.Equal(json, await process.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(pid, process.ProcessId);
            Assert.False(process.Completion.IsCompleted);
        }

        var closed = await process.CloseAsync();
        Assert.True(closed.CleanupConfirmed);
        Assert.True(closed.IoSettled);
        AssertExited(pid);
    }

    [Fact]
    public async Task CancelingPendingReadDoesNotDestroyTheConnectionOrLoseNextFrame()
    {
        using var process = await WindowsDuplexProcess.StartAsync(Options("echo"));
        using var canceled = new CancellationTokenSource(40);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.ReadLineAsync(canceled.Token));
        await process.WriteLineAsync("{\"after\":true}");
        Assert.Equal("{\"after\":true}", await process.ReadLineAsync());
        Assert.False(process.Completion.IsCompleted);
    }

    [Fact]
    public async Task FramesAtTheExactByteLimitSupportCrLf()
    {
        var options = Options("echo");
        options.MaxLineBytes = 128;
        using var process = await WindowsDuplexProcess.StartAsync(options);
        var line = new string('x', 128);
        await process.WriteLineAsync(line);
        Assert.Equal(line, await process.ReadLineAsync());
        var rejected = await Assert.ThrowsAsync<WindowsDuplexProcessException>(() => process.WriteLineAsync(line + "x"));
        Assert.Equal("line_limit", rejected.Code);
        Assert.False(rejected.OperationMayHaveStarted);
        await Assert.ThrowsAsync<WindowsDuplexProcessException>(() => process.WriteLineAsync("{}\n{}"));
    }

    [Theory]
    [InlineData("long-line", WindowsDuplexProcessTermination.LineLimitExceeded)]
    [InlineData("bad-utf8", WindowsDuplexProcessTermination.InvalidUtf8)]
    [InlineData("tail", WindowsDuplexProcessTermination.TruncatedLine)]
    [InlineData("stderr-flood", WindowsDuplexProcessTermination.StandardErrorLimitExceeded)]
    [InlineData("frame-flood", WindowsDuplexProcessTermination.OutputBackpressure)]
    public async Task MalformedOrUnconsumedOutputTerminatesWithBoundedResources(string mode, WindowsDuplexProcessTermination reason)
    {
        var options = Options(mode);
        options.MaxLineBytes = 128;
        options.MaxPendingLines = 2;
        options.MaxStandardErrorBytes = 128;
        using var process = await WindowsDuplexProcess.StartAsync(options);
        var result = await process.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reason, result.Termination);
        Assert.True(result.CleanupConfirmed);
        Assert.True(result.IoSettled);
        AssertExited(process.ProcessId);
    }

    [Fact]
    public async Task UnresponsiveStdinWriteTimesOutAndCannotBeRetriedOnTheSameProcess()
    {
        var options = Options("silent");
        options.WriteTimeout = TimeSpan.FromMilliseconds(120);
        using var process = await WindowsDuplexProcess.StartAsync(options);
        var failure = await Assert.ThrowsAsync<WindowsDuplexProcessException>(() => process.WriteLineAsync(new string('x', 200000)));
        Assert.Equal("write_timeout", failure.Code);
        Assert.True(failure.OperationMayHaveStarted);
        var ended = await process.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WindowsDuplexProcessTermination.WriteTimedOut, ended.Termination);
        Assert.True(ended.CleanupConfirmed);
        Assert.True(ended.IoSettled);
        var retry = await Assert.ThrowsAsync<WindowsDuplexProcessException>(() => process.WriteLineAsync("{}"));
        Assert.Equal("closed", retry.Code);
        Assert.False(retry.OperationMayHaveStarted);
    }

    [Fact]
    public async Task LifetimeCancellationReclaimsTheWholeTree()
    {
        using var lifetime = new CancellationTokenSource();
        using var process = await WindowsDuplexProcess.StartAsync(Options("tree"), lifetime.Token);
        var child = int.Parse((await process.ReadLineAsync())!);
        lifetime.Cancel();
        var ended = await process.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WindowsDuplexProcessTermination.Canceled, ended.Termination);
        Assert.True(ended.CleanupConfirmed);
        AssertExited(child);
        AssertExited(process.ProcessId);
    }

    [Fact]
    public async Task NaturalExitDrainsItsFinalFrameBeforeEof()
    {
        using var process = await WindowsDuplexProcess.StartAsync(Options("one-frame"));
        Assert.Equal("{\"last\":true}", await process.ReadLineAsync());
        Assert.Null(await process.ReadLineAsync());
        var ended = await process.Completion;
        Assert.Equal(0, ended.ExitCode);
        Assert.True(ended.CleanupConfirmed);
    }

    [Fact]
    public async Task ExplicitDrainModeAllowsLongLivedServiceLogsWithoutRetainingTheirContents()
    {
        var options = Options("logs");
        options.StandardOutputMode = WindowsDuplexProcessOutputMode.Drain;
        options.StandardErrorOverflow = WindowsDuplexProcessErrorOverflow.Drain;
        options.MaxLineBytes = 128;
        options.MaxStandardErrorBytes = 128;
        using var process = await WindowsDuplexProcess.StartAsync(options);
        await Task.Delay(250);
        Assert.False(process.Completion.IsCompleted);
        await Assert.ThrowsAsync<WindowsDuplexProcessException>(() => process.ReadLineAsync());
        await process.WriteLineAsync("quit");
        var ended = await process.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, ended.ExitCode);
        Assert.True(ended.StandardErrorBytes >= 65536);
        Assert.True(ended.StandardOutputBytes >= 65536);
        Assert.True(ended.StandardErrorTruncated);
        Assert.True(ended.CleanupConfirmed);
        Assert.True(ended.IoSettled);
    }

    [Fact]
    public async Task DoesNotInheritTheHostsSecrets()
    {
        var name = "TANSR_DUPLEX_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "never-inherit");
        try
        {
            using var process = await WindowsDuplexProcess.StartAsync(Options("environment", name));
            Assert.Equal("not-set", await process.ReadLineAsync());
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public async Task MatchingApprovedDigestAllowsThePinnedExecutableToStart()
    {
        var marker = Path.Combine(directory, "digest-approved.txt");
        var options = Options("marker", marker);
        options.ExpectedExecutableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Executable)));
        using var process = await WindowsDuplexProcess.StartAsync(options);
        await process.WriteLineAsync("{\"approved\":true}");
        Assert.Equal("{\"approved\":true}", await process.ReadLineAsync());
        Assert.Equal("executed", File.ReadAllText(marker));
    }

    [Fact]
    public async Task DigestMismatchPreventsAnyProcessSideEffect()
    {
        var marker = Path.Combine(directory, "must-not-exist.txt");
        var options = Options("marker", marker);
        options.ExpectedExecutableSha256 = new string('0', 64);
        var error = await Assert.ThrowsAsync<WindowsDuplexProcessException>(() => WindowsDuplexProcess.StartAsync(options));
        Assert.Equal("executable_digest_mismatch", error.Code);
        Assert.False(error.OperationMayHaveStarted);
        Assert.False(File.Exists(marker));
        // 拒绝后租约和 pins 释放，下一次获准启动仍可正常运行。
        options.ExpectedExecutableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Executable)));
        using var approved = await WindowsDuplexProcess.StartAsync(options);
        await approved.WriteLineAsync("{}");
        Assert.Equal("{}", await approved.ReadLineAsync());
        Assert.True(File.Exists(marker));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task MalformedDigestIsRejectedBeforeAcquiringTheWorkspace(string digest)
    {
        var acquired = false;
        var options = new WindowsDuplexProcessOptions(fixture.Executable, new[] { "echo" }, () =>
        {
            acquired = true;
            return workspace.AcquireProcessDirectory();
        })
        { ExpectedExecutableSha256 = digest };
        await Assert.ThrowsAsync<ArgumentException>(() => WindowsDuplexProcess.StartAsync(options));
        Assert.False(acquired);
    }

    [Fact]
    public async Task DuplicatedProcessIdentitySurvivesOriginalHandleClosureAndCannotBeReopenedAfterExit()
    {
        using var process = await WindowsDuplexProcess.StartAsync(Options("echo"));
        using var identity = DuplicateIdentity(process);
        Assert.Equal(258U, WaitForSingleObject(identity, 0));
        await process.CloseAsync();
        Assert.False(identity.IsClosed);
        Assert.Equal(0U, WaitForSingleObject(identity, 0));
        var failure = Assert.Throws<TargetInvocationException>(() => DuplicateIdentity(process));
        Assert.Equal("closed", Assert.IsType<WindowsDuplexProcessException>(failure.InnerException).Code);
    }

    [Fact]
    public async Task ConcurrentCloseAndIdentityDuplicationEitherHoldOriginalObjectOrFailClosed()
    {
        using var process = await WindowsDuplexProcess.StartAsync(Options("echo"));
        var operations = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try
            {
                using var identity = DuplicateIdentity(process);
                Assert.Contains(WaitForSingleObject(identity, 0), new[] { 0U, 258U });
            }
            catch (TargetInvocationException error)
            {
                Assert.Equal("closed", Assert.IsType<WindowsDuplexProcessException>(error.InnerException).Code);
            }
        })).ToArray();
        await process.CloseAsync();
        await Task.WhenAll(operations);
    }

    private static SafeHandle DuplicateIdentity(WindowsDuplexProcess process) =>
        (SafeHandle)typeof(WindowsDuplexProcess).GetMethod("DuplicateProcessHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(process, null)!;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    private WindowsDuplexProcessOptions Options(params string[] arguments)
    {
        var options = new WindowsDuplexProcessOptions(fixture.Executable, arguments, () => workspace.AcquireProcessDirectory());
        options.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return options;
    }

    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }

    public void Dispose()
    {
        workspace.Dispose();
        Directory.Delete(directory, true);
    }
}

public sealed class WindowsDuplexTestProgram : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-duplex-program-" + Guid.NewGuid().ToString("N"));
    public WindowsDuplexTestProgram()
    {
        Directory.CreateDirectory(directory);
        Executable = Path.Combine(directory, "duplex.exe");
        var source = Path.Combine(directory, "duplex.cs");
        File.WriteAllText(source, Source, Encoding.UTF8);
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET",
            Environment.Is64BitProcess ? "Framework64" : "Framework", "v4.0.30319", "csc.exe");
        using var build = Process.Start(new ProcessStartInfo(compiler, "/nologo /target:exe /out:\"" + Executable + "\" \"" + source + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = build.StandardOutput.ReadToEndAsync();
        var error = build.StandardError.ReadToEndAsync();
        if (!build.WaitForExit(30000)) { build.Kill(true); throw new TimeoutException("Fixture compiler timed out."); }
        Assert.True(build.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    public string Executable { get; }
    public void Dispose() => Directory.Delete(directory, true);
    private const string Source = """
        using System;
        using System.Diagnostics;
        using System.Reflection;
        using System.Text;
        using System.Threading;
        class Program {
            static void Main(string[] args) {
                Console.InputEncoding = new UTF8Encoding(false, true); Console.OutputEncoding = new UTF8Encoding(false);
                string mode=args[0];
                if(mode=="silent") { Thread.Sleep(60000); return; }
                if(mode=="long-line") { Console.Write(new string('x',4096)); Thread.Sleep(60000); return; }
                if(mode=="bad-utf8") { var stream=Console.OpenStandardOutput(); stream.WriteByte(255); stream.WriteByte(10); stream.Flush(); return; }
                if(mode=="tail") { Console.Write("{}"); return; }
                if(mode=="stderr-flood") { while(true) Console.Error.Write(new string('s',4096)); }
                if(mode=="frame-flood") { while(true) Console.WriteLine("{}"); }
                if(mode=="one-frame") { Console.WriteLine("{\"last\":true}"); return; }
                if(mode=="environment") { Console.WriteLine(Environment.GetEnvironmentVariable(args[1]) ?? "not-set"); }
                if(mode=="marker") { System.IO.File.WriteAllText(args[1], "executed"); }
                if(mode=="logs") { for(int i=0;i<16;i++) { Console.Write(new string('o',4096)); Console.Error.Write(new string('e',4096)); } }
                if(mode=="tree") { var child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"silent") { UseShellExecute=false, CreateNoWindow=true }); Console.WriteLine(child.Id); }
                string line;
                while((line=Console.ReadLine())!=null) {
                    if(line=="quit") return;
                    if(mode=="split-echo") { var stream=Console.OpenStandardOutput(); foreach(byte b in Encoding.UTF8.GetBytes(line+"\n")) { stream.WriteByte(b); stream.Flush(); Thread.Sleep(2); } }
                    else Console.WriteLine(line);
                }
            }
        }
        """;
}
