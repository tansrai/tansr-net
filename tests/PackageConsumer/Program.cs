using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Tansr.Sdk.Windows.Security;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(tempRoot, "tansr-net-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri("https://example.invalid"),
                TokenProvider = _ => Task.FromResult("synthetic-no-request"),
            })) { }
            var control = WireJson.Parse(Encoding.UTF8.GetBytes("{\"sequence\":\"9223372036854775807\"}"));
            if (WireJson.CanonicalString(control) != "{\"sequence\":\"9223372036854775807\"}") throw new Exception("wire");
            using (var workspace = new WindowsWorkspace(root))
            {
                var backend = new WindowsExecutorBackend("package-consumer", new[] { new WindowsExecutorWorkspace("workspace", "1", workspace) });
                WireJson.ValidateNamed("ExecutorRegistrationRequest", backend.Registration);
                workspace.WriteAtomic("hello.txt", Encoding.UTF8.GetBytes("原生 SDK 消费 😀"));
                if (Encoding.UTF8.GetString(workspace.Read("hello.txt")) != "原生 SDK 消费 😀") throw new Exception("workspace");
                var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                var request = new WindowsProcessRequest(command, new[] { "/d", "/s", "/c", "echo tansr-package-consumer" }, () => workspace.AcquireProcessDirectory());
                var process = await new WindowsProcessExecutor().ExecuteAsync(request, null, CancellationToken.None);
                if (!process.CleanupConfirmed || process.ExitCode != 0 || !process.StandardOutput.Contains("tansr-package-consumer")) throw new Exception("process");
            }
            Console.WriteLine("PACKAGE_CONSUMER_OK " + Environment.Version);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("PACKAGE_CONSUMER_FAILED " + error.GetType().FullName + " " + error.Message);
            return 1;
        }
        finally
        {
            if (!root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("cleanup_scope");
            Directory.Delete(root, true);
        }
    }
}
