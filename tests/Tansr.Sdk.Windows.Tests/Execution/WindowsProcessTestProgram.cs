using System.Diagnostics;
using System.Text;

namespace Tansr.Sdk.Windows.Tests.Execution;

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
        using System.IO;
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
                    case "paced":
                        File.AppendAllText("launches.txt", Process.GetCurrentProcess().Id + "\n");
                        Console.WriteLine("PIPE_TIME|" + Stopwatch.GetTimestamp() + "|" + Stopwatch.Frequency + "|" + DateTime.UtcNow.Ticks + "|" + Process.GetCurrentProcess().Id);
                        Console.Out.Flush();
                        var nativeOut = Console.OpenStandardOutput(); var nativeErr = Console.OpenStandardError();
                        var outBytes = Encoding.UTF8.GetBytes("PIPE_OUT_中文🙂\n"); var errBytes = Encoding.UTF8.GetBytes("PIPE_ERR_中文🙂\n");
                        for(var i = 0; i < Math.Max(outBytes.Length, errBytes.Length); i++) {
                            if(i < outBytes.Length) { nativeOut.WriteByte(outBytes[i]); nativeOut.Flush(); }
                            if(i < errBytes.Length) { nativeErr.WriteByte(errBytes[i]); nativeErr.Flush(); }
                            Thread.Sleep(2);
                        }
                        using(var pacedRelease = EventWaitHandle.OpenExisting(args[1])) {
                            if(!pacedRelease.WaitOne(45000)) { Environment.ExitCode=73; return; }
                        }
                        Console.Write("DONE\n"); Console.Out.Flush(); break;
                    case "tree-pipeline":
                        File.AppendAllText("launches.txt", Process.GetCurrentProcess().Id + "\n");
                        var heldChild = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "child-sleep") { UseShellExecute = false, CreateNoWindow = true });
                        Console.WriteLine("PIPE_TREE|" + Process.GetCurrentProcess().Id + "|" + heldChild.Id + "|" + DateTime.UtcNow.Ticks);
                        Console.Out.Flush(); Thread.Sleep(60000); break;
                    case "unicode":
                        var output = Console.OpenStandardOutput();
                        foreach(var value in Encoding.UTF8.GetBytes("中文🙂")) { output.WriteByte(value); output.Flush(); Thread.Sleep(20); }
                        Console.Error.Write("error-stream"); Console.Error.Flush();
                        if(args.Length>1) {
                            using(var release=EventWaitHandle.OpenExisting(args[1])) {
                                if(!release.WaitOne(15000)) { Environment.ExitCode=73; return; }
                            }
                        } else Thread.Sleep(800);
                        Console.Write("done"); break;
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
