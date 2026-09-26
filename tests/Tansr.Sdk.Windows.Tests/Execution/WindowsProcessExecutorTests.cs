using System.Diagnostics;
using System.Text;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Tests.Execution;

public sealed class WindowsProcessExecutorTests : IDisposable, IClassFixture<WindowsProcessTestProgram>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-process-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsWorkspace workspace;
    private readonly WindowsProcessTestProgram program;

    public WindowsProcessExecutorTests(WindowsProcessTestProgram program)
    {
        this.program = program;
        Directory.CreateDirectory(directory);
        workspace = new WindowsWorkspace(directory);
    }

    [Fact]
    public async Task DeliversUtf8AndSeparateStreamsBeforeProcessExits()
    {
        var request = Request("unicode");
        request.ChunkBytes = 1;
        request.MaxPendingChunks = 128;
        var chunks = new List<WindowsProcessOutputChunk>();
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new WindowsProcessExecutor().ExecuteAsync(request, (chunk, _) =>
        {
            chunks.Add(chunk);
            first.TrySetResult(true);
            return Task.CompletedTask;
        });

        await first.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(run.IsCompleted);
        var result = await run;
        Assert.Equal(WindowsProcessTermination.Exited, result.Termination);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.CleanupConfirmed);
        Assert.True(result.OutputComplete);
        Assert.Equal("中文🙂done", result.StandardOutput);
        Assert.Equal("error-stream", result.StandardError);
        Assert.Equal(result.StandardOutput, string.Concat(chunks.Where(item => item.Stream == WindowsProcessOutputStream.StandardOutput).Select(item => item.Text)));
        Assert.Equal(result.StandardError, string.Concat(chunks.Where(item => item.Stream == WindowsProcessOutputStream.StandardError).Select(item => item.Text)));
        Assert.Equal(Enumerable.Range(1, chunks.Count).Select(number => (long)number), chunks.Select(item => item.Sequence));
    }

    [Fact]
    public async Task DoesNotInheritParentSecretsAndPreservesApprovedArguments()
    {
        var key = "TANSR_TEST_PROCESS_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, "parent-secret");
        try
        {
            var request = Request("environment", key);
            request.Environment["EXPLICIT_VALUE"] = "selected";
            var result = await new WindowsProcessExecutor().ExecuteAsync(request);
            Assert.Equal(WindowsProcessTermination.Exited, result.Termination);
            Assert.Equal("selected", result.StandardOutput);
            Assert.DoesNotContain("parent-secret", result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task TimeoutKillsOwnedChildTreeIncludingInheritedPipes()
    {
        var request = Request("tree");
        request.Timeout = TimeSpan.FromSeconds(3);
        var result = await new WindowsProcessExecutor().ExecuteAsync(request);
        Assert.Equal(WindowsProcessTermination.TimedOut, result.Termination);
        Assert.True(result.CleanupConfirmed);
        Assert.True(int.TryParse(result.StandardOutput.Trim(), out var childId), result.StandardError);
        AssertExited(childId);
        AssertExited(result.ProcessId!.Value);
    }

    [Fact]
    public async Task CancellationWaitsForOwnedProcessExit()
    {
        using var cancellation = new CancellationTokenSource();
        var request = Request("sleep");
        var result = await new WindowsProcessExecutor().ExecuteAsync(request, (_, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        Assert.Equal(WindowsProcessTermination.Canceled, result.Termination);
        Assert.True(result.Started);
        Assert.True(result.CleanupConfirmed);
        AssertExited(result.ProcessId!.Value);
    }

    [Fact]
    public async Task NormalRootExitStillReclaimsRemainingChildren()
    {
        var result = await new WindowsProcessExecutor().ExecuteAsync(Request("tree-exit"));
        Assert.Equal(WindowsProcessTermination.Exited, result.Termination);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.CleanupConfirmed);
        Assert.True(int.TryParse(result.StandardOutput.Trim(), out var childId), result.StandardError);
        AssertExited(childId);
    }

    [Fact]
    public async Task OutputFloodIsBoundedAndKillsProcess()
    {
        var request = Request("flood");
        request.MaxOutputBytes = 1024;
        var result = await new WindowsProcessExecutor().ExecuteAsync(request);
        Assert.Equal(WindowsProcessTermination.OutputLimitExceeded, result.Termination);
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(result.StandardOutput) + Encoding.UTF8.GetByteCount(result.StandardError));
        Assert.False(result.OutputComplete);
        Assert.True(result.CleanupConfirmed);
        AssertExited(result.ProcessId!.Value);
    }

    [Fact]
    public async Task SlowOutputConsumerCannotBlockPipeDrainOrAccumulateUnboundedQueue()
    {
        var request = Request("slow-flood");
        request.MaxPendingChunks = 1;
        request.ChunkBytes = 128;
        request.OutputCallbackTimeout = TimeSpan.FromMilliseconds(150);
        var result = await new WindowsProcessExecutor().ExecuteAsync(request, async (_, token) => await Task.Delay(TimeSpan.FromSeconds(60), token));
        Assert.Contains(result.Termination, new[] { WindowsProcessTermination.OutputBackpressure, WindowsProcessTermination.OutputCallbackFailed });
        Assert.False(result.OutputComplete);
        Assert.True(result.CleanupConfirmed);
        AssertExited(result.ProcessId!.Value);
    }

    [Fact]
    public async Task CallbackFailureIsExplicitAndDoesNotLeakProcess()
    {
        var request = Request("sleep");
        var result = await new WindowsProcessExecutor().ExecuteAsync(request, (_, _) => throw new InvalidOperationException("test-only"));
        Assert.Equal(WindowsProcessTermination.OutputCallbackFailed, result.Termination);
        Assert.False(result.OutputComplete);
        Assert.True(result.CleanupConfirmed);
    }

    [Fact]
    public async Task NonCooperativeOutputCallbackRetainsCapacityUntilItsActualTaskEnds()
    {
        var executor = new WindowsProcessExecutor(maximumOutstandingOutputHandlers: 1);
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        try
        {
            var request = Request("sleep");
            request.OutputCallbackTimeout = TimeSpan.FromMilliseconds(80);
            var first = await executor.ExecuteAsync(request, (_, _) =>
            {
                Interlocked.Increment(ref entered);
                return blocked.Task; // 故意忽略取消，模拟不合作的宿主。
            });
            Assert.Equal(WindowsProcessTermination.OutputCallbackFailed, first.Termination);
            Assert.True(first.CleanupConfirmed);
            Assert.False(first.OutputDeliverySettled);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var refused = await executor.ExecuteAsync(Request("sleep"), (_, _) =>
                {
                    Interlocked.Increment(ref entered);
                    return blocked.Task;
                });
                Assert.Equal(WindowsProcessTermination.OutputBackpressure, refused.Termination);
                Assert.False(refused.Started);
                Assert.Null(refused.ProcessId);
            }

            Assert.Equal(1, entered);
            blocked.TrySetResult(true);
            WindowsProcessResult? resumed = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                resumed = await executor.ExecuteAsync(Request("environment", "TANSR_NOT_SET"), (_, _) => Task.CompletedTask);
                if (resumed.Started) break;
                await Task.Delay(10);
            }

            Assert.NotNull(resumed);
            Assert.True(resumed.Started);
            Assert.Equal(WindowsProcessTermination.Exited, resumed.Termination);
            Assert.True(resumed.OutputDeliverySettled);
        }
        finally
        {
            blocked.TrySetResult(true);
        }
    }

    [Fact]
    public async Task MissingExecutableIsKnownNotStarted()
    {
        var request = new WindowsProcessRequest(Path.Combine(directory, "not-present.exe"), Array.Empty<string>(), () => workspace.AcquireProcessDirectory());
        var result = await new WindowsProcessExecutor().ExecuteAsync(request);
        Assert.Equal(WindowsProcessTermination.StartFailed, result.Termination);
        Assert.False(result.Started);
        Assert.True(result.CleanupConfirmed);
        Assert.NotNull(result.NativeErrorCode);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData(@"\\server\share\cmd.exe")]
    [InlineData(@"C:\Windows\cmd.exe:payload")]
    [InlineData(@"C:\Windows\..\cmd.exe")]
    public async Task RejectsUnapprovedPathFormsBeforeAcquiringWorkspace(string executable)
    {
        var acquired = false;
        var request = new WindowsProcessRequest(executable, Array.Empty<string>(), () =>
        {
            acquired = true;
            return workspace.AcquireProcessDirectory();
        });
        await Assert.ThrowsAsync<ArgumentException>(() => new WindowsProcessExecutor().ExecuteAsync(request));
        Assert.False(acquired);
    }

    [Fact]
    public async Task AlreadyCanceledRequestNeverAcquiresOrStartsProcess()
    {
        var acquired = false;
        var request = new WindowsProcessRequest(program.Executable, Array.Empty<string>(), () =>
        {
            acquired = true;
            return workspace.AcquireProcessDirectory();
        });
        var result = await new WindowsProcessExecutor().ExecuteAsync(request, cancellationToken: new CancellationToken(true));
        Assert.Equal(WindowsProcessTermination.Canceled, result.Termination);
        Assert.False(result.Started);
        Assert.False(acquired);
    }

    [Fact]
    public async Task RawOutputPreservesInvalidUtf8AndBinaryBytes()
    {
        var request = Request("binary");
        request.ChunkBytes = 1;
        var bytes = new List<byte>();
        var result = await new WindowsProcessExecutor().ExecuteAsync(request, (chunk, _) =>
        {
            bytes.AddRange(chunk.RawBytes);
            return Task.CompletedTask;
        });
        Assert.Equal(WindowsProcessTermination.Exited, result.Termination);
        Assert.Equal(new byte[] { 0, 255, 240, 159, 153, 130, 128, 65 }, bytes);
        Assert.True(result.OutputComplete);
    }

    [Fact]
    public async Task ExplicitArgumentsRoundTripWithoutShellExpansion()
    {
        var arguments = new[] { "", "two words", "quote\"inside", "backslash\\", "\\\"quoted", "%PATH%", "& echo injection" };
        var request = Request(new[] { "arguments" }.Concat(arguments).ToArray());
        var result = await new WindowsProcessExecutor().ExecuteAsync(request);
        Assert.Equal(WindowsProcessTermination.Exited, result.Termination);
        var expected = string.Join("\n", arguments.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));
        Assert.Equal(expected, result.StandardOutput.Replace("\r\n", "\n").TrimEnd('\n'));
    }

    [Fact]
    public async Task ExplicitlyApprovedCmdInterpreterKeepsItsSwitchSemantics()
    {
        var request = new WindowsProcessRequest(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            new[] { "/d", "/s", "/c", "echo approved-cmd" }, () => workspace.AcquireProcessDirectory());
        request.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var result = await new WindowsProcessExecutor().ExecuteAsync(request);
        Assert.Equal(WindowsProcessTermination.Exited, result.Termination);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("approved-cmd", result.StandardOutput.Trim());
    }

    private WindowsProcessRequest Request(params string[] arguments)
    {
        var request = new WindowsProcessRequest(program.Executable, arguments, () => workspace.AcquireProcessDirectory());
        request.Timeout = TimeSpan.FromSeconds(10);
        request.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        request.Environment["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        request.Environment["TEMP"] = directory;
        request.Environment["TMP"] = directory;
        return request;
    }

    private static void AssertExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, "Owned process did not exit.");
        }
        catch (ArgumentException)
        {
            // 此 PID 已不存在。
        }
    }

    public void Dispose()
    {
        workspace.Dispose();
        Directory.Delete(directory, true);
    }
}

/// <summary>仅测试使用的无外部依赖程序；系统 .NET Framework 编译器生成精确字节及进程树夹具。</summary>
public sealed class WindowsProcessTestProgram : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-process-program-" + Guid.NewGuid().ToString("N"));

    public WindowsProcessTestProgram()
    {
        Directory.CreateDirectory(directory);
        Executable = Path.Combine(directory, "process-fixture.exe");
        var source = Path.Combine(directory, "process-fixture.cs");
        File.WriteAllText(source, Source, Encoding.UTF8);
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET",
            Environment.Is64BitProcess ? "Framework64" : "Framework", "v4.0.30319", "csc.exe");
        using var build = Process.Start(new ProcessStartInfo(compiler, "/nologo /target:exe /out:\"" + Executable + "\" \"" + source + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("The process fixture compiler did not start.");
        var output = build.StandardOutput.ReadToEndAsync();
        var error = build.StandardError.ReadToEndAsync();
        if (!build.WaitForExit(30000))
        {
            build.Kill(true);
            throw new TimeoutException("The process fixture compiler timed out.");
        }

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
        class Program
        {
            static void Main(string[] args)
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                switch(args[0])
                {
                    case "unicode":
                        var output = Console.OpenStandardOutput();
                        foreach(var value in Encoding.UTF8.GetBytes("中文🙂")) { output.WriteByte(value); output.Flush(); Thread.Sleep(20); }
                        Console.Error.Write("error-stream"); Thread.Sleep(800); Console.Write("done"); break;
                    case "binary":
                        var binary = Console.OpenStandardOutput();
                        foreach(var value in new byte[] { 0, 255, 240, 159, 153, 130, 128, 65 }) { binary.WriteByte(value); binary.Flush(); }
                        break;
                    case "environment": Console.Write(Environment.GetEnvironmentVariable(args[1])); Console.Write(Environment.GetEnvironmentVariable("EXPLICIT_VALUE")); break;
                    case "sleep": Console.Write("ready"); Thread.Sleep(60000); break;
                    case "child-sleep": Thread.Sleep(60000); break;
                    case "tree":
                    case "tree-exit":
                        var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "child-sleep") { UseShellExecute = false, CreateNoWindow = true });
                        Console.WriteLine(child.Id); if(args[0] == "tree") Thread.Sleep(60000); break;
                    case "flood": while(true) { Console.Write(new string('a', 4096)); Console.Error.Write(new string('b', 4096)); }
                    case "slow-flood": while(true) { Console.Write(new string('x', 128)); Thread.Sleep(2); }
                    case "arguments": for(var index = 1; index < args.Length; index++) Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(args[index]))); break;
                }
            }
        }
        """;
}
