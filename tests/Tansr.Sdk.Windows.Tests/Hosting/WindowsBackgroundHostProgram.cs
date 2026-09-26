using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Tansr.Sdk.Windows.Tests.Hosting;

/// <summary>独立 CLR4 宿主实际加载候选 Windows SDK，允许测试骤停宿主而不杀死测试进程。</summary>
public sealed class WindowsBackgroundHostProgram : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-background-host-program-" + Guid.NewGuid().ToString("N"));

    public WindowsBackgroundHostProgram()
    {
        Directory.CreateDirectory(directory);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Tansr.Sdk.slnx"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Cannot locate this candidate's solution.");
        var candidate = Path.Combine(root.FullName, "src", "Tansr.Sdk.Windows", "bin", "Release", "net48");
        Assert.True(File.Exists(Path.Combine(candidate, "Tansr.Sdk.Windows.dll")), "Build the complete Release solution before native host tests.");
        foreach (var library in Directory.GetFiles(candidate, "*.dll")) File.Copy(library, Path.Combine(directory, Path.GetFileName(library)));
        Executable = Path.Combine(directory, "background-host.exe");
        XNamespace bindingNamespace = "urn:schemas-microsoft-com:asm.v1";
        var bindings = new XElement(bindingNamespace + "assemblyBinding");
        foreach (var library in Directory.GetFiles(directory, "*.dll"))
        {
            var identity = AssemblyName.GetAssemblyName(library); var token = identity.GetPublicKeyToken();
            if (token == null || token.Length == 0) continue;
            bindings.Add(new XElement(bindingNamespace + "dependentAssembly",
                new XElement(bindingNamespace + "assemblyIdentity", new XAttribute("name", identity.Name!), new XAttribute("publicKeyToken", Convert.ToHexStringLower(token)), new XAttribute("culture", "neutral")),
                new XElement(bindingNamespace + "bindingRedirect", new XAttribute("oldVersion", "0.0.0.0-" + identity.Version), new XAttribute("newVersion", identity.Version!))));
        }
        new XDocument(new XElement("configuration", new XElement("startup", new XElement("supportedRuntime", new XAttribute("version", "v4.0"), new XAttribute("sku", ".NETFramework,Version=v4.8"))),
            new XElement("runtime", bindings))).Save(Executable + ".config");
        var source = Path.Combine(directory, "background-host.cs"); File.WriteAllText(source, Source, new UTF8Encoding(false));
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319");
        var references = Directory.GetFiles(directory, "*.dll").Select(path => "/reference:\"" + path + "\"").ToList();
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "src", "Tansr.Sdk.Windows", "obj", "project.assets.json")));
        var net48 = assets.RootElement.GetProperty("libraries").EnumerateObject().Single(item => item.Name.StartsWith("Microsoft.NETFramework.ReferenceAssemblies.net48/", StringComparison.OrdinalIgnoreCase)).Name.ToLowerInvariant();
        var facade = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(folder => Path.Combine(folder.Name, net48.Replace('/', Path.DirectorySeparatorChar), "build", ".NETFramework", "v4.8", "Facades", "netstandard.dll"))
            .First(File.Exists);
        references.Add("/reference:\"" + facade + "\"");
        using var compiler = Process.Start(new ProcessStartInfo(Path.Combine(framework, "csc.exe"),
            "/nologo /target:exe /out:\"" + Executable + "\" " + string.Join(" ", references) + " \"" + source + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("Compiler did not start.");
        var stdout = compiler.StandardOutput.ReadToEndAsync(); var stderr = compiler.StandardError.ReadToEndAsync();
        if (!compiler.WaitForExit(30000)) { compiler.Kill(true); throw new TimeoutException("Background host compiler did not finish."); }
        Assert.True(compiler.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    public string Executable { get; }
    public void Dispose() => Directory.Delete(directory, true);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Text;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using Tansr.Sdk.Protocol;
        using Tansr.Sdk.Terminal;
        using Tansr.Sdk.Windows.Execution;
        using Tansr.Sdk.Windows.Hosting;
        class Program {
            static void Main(string[] args) {
                Console.OutputEncoding = new UTF8Encoding(false);
                try { Run(args); } catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            }
            static void Run(string[] args) {
                var operation = JsonDocument.Parse(File.ReadAllText(args[2])).RootElement.Clone();
                using (var workspace = new WindowsWorkspace(args[1])) {
                    var options = new WindowsBackgroundHostOptions(args[1], (request, location) => {
                        var process = new WindowsProcessRequest(args[0], new [] { "tree-pipeline" }, () => location.AcquireProcessDirectory(""));
                        process.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                        return process;
                    }) { CandidateRevision = WindowsBackgroundHost.CandidateRevision };
                    using (var host = new WindowsBackgroundHost(options)) {
                        var backend = new WindowsExecutorBackend("executor", new [] { new WindowsExecutorWorkspace("workspace", "1", workspace) }, new [] { host.CreateTool() });
                        var result = backend.ExecuteAsync(operation, token => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
                        Console.WriteLine(result.GetRawText()); Console.Out.Flush();
                        // Keep the original runtime alive until a real window/host close or external process termination.
                        if (Console.ReadLine() == "close") host.CloseAsync().GetAwaiter().GetResult();
                        else Thread.Sleep(60000);
                    }
                }
            }
        }
        """;
}
