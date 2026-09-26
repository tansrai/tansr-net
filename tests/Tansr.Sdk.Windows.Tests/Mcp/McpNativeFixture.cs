using System.Diagnostics;
using System.Text;

namespace Tansr.Sdk.Windows.Tests.Mcp;

public sealed class McpNativeFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tansr-native-mcp-fixture-" + Guid.NewGuid().ToString("N"));
    public string Executable { get; }
    public McpNativeFixture()
    {
        Directory.CreateDirectory(_directory); Executable = Path.Combine(_directory, "mcp.exe"); var source = Path.Combine(_directory, "mcp.cs");
        File.WriteAllText(source, Source, Encoding.UTF8);
        string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", Environment.Is64BitProcess ? "Framework64" : "Framework", "v4.0.30319", "csc.exe");
        using var build = Process.Start(new ProcessStartInfo(compiler, "/nologo /target:exe /r:System.Web.Extensions.dll /out:\"" + Executable + "\" \"" + source + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = build.StandardOutput.ReadToEndAsync(); var error = build.StandardError.ReadToEndAsync();
        if (!build.WaitForExit(30000)) { build.Kill(true); throw new TimeoutException("Fixture compile timed out."); }
        Assert.True(build.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
    public void Dispose() => Directory.Delete(_directory, true);
    private const string Source = """
        using System; using System.Collections.Generic; using System.Text; using System.Threading; using System.Web.Script.Serialization;
        class Program {
          static void Main() {
            Console.InputEncoding = new UTF8Encoding(false,true); Console.OutputEncoding = new UTF8Encoding(false);
            var json = new JavaScriptSerializer(); string line;
            while((line=Console.ReadLine())!=null) {
              var message = json.Deserialize<Dictionary<string,object>>(line);
              if(!message.ContainsKey("id") || !message.ContainsKey("method")) continue;
              string method = (string)message["method"]; object result;
              if(method=="malformed") { Console.WriteLine("this is not JSON"); continue; }
              if(method=="delay") Thread.Sleep(200);
              if(method=="initialize") result=new { protocolVersion="2025-03-26", capabilities=new { tools=new {} },serverInfo=new { name="native-fixture",version="1" } };
              else if(method=="tools/list") result=new { tools=new [] { new {name="echo",inputSchema=new {type="object"} },new {name="unapproved",inputSchema=new {type="object"} } } };
              else if(method=="tools/call") { var p=(Dictionary<string,object>)message["params"]; var args=(Dictionary<string,object>)p["arguments"]; result=new {content=new [] {new { type="text", text=(string)args["text"] } } }; }
              else result=new { method=method };
              Console.WriteLine(json.Serialize(new { jsonrpc="2.0",id=message["id"],result=result }));
            }
          }
        }
        """;
}
