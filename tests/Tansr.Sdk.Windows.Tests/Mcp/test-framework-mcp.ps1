#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$LibraryDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'The original net48 runtime gate requires Windows x64.' }
$library = (Resolve-Path -LiteralPath $LibraryDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; prior failures are retained.' }
foreach ($name in @('Tansr.Sdk.dll', 'Tansr.Sdk.Windows.dll', 'System.Text.Json.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $library $name) -PathType Leaf)) { throw "Missing approved net48 input: $name" }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$manifest = [ordered]@{ source = $library; startedAt = [DateTime]::UtcNow.ToString('o'); outcome = 'incomplete'; inputs = @(); compileExitCode = $null; runExitCode = $null; boundary = 'Real CLR4 MCP HTTP calls against owned loopback sockets; no product build, external network, credentials, signing or publication.' }
$references = @(); $redirects = @()
foreach ($file in Get-ChildItem -LiteralPath $library -File -Filter '*.dll') {
    $copy = Join-Path $output $file.Name
    Copy-Item -LiteralPath $file.FullName -Destination $copy
    $manifest.inputs += @{ name = $file.Name; sha256 = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() }
    try { $assembly = [Reflection.AssemblyName]::GetAssemblyName($copy) }
    catch [BadImageFormatException] { continue }
    $references += '/r:' + $copy
    $key = [BitConverter]::ToString($assembly.GetPublicKeyToken()).Replace('-', '').ToLowerInvariant()
    if ($key) {
        $redirects += '<dependentAssembly><assemblyIdentity name="' + [Security.SecurityElement]::Escape($assembly.Name) + '" publicKeyToken="' + $key + '" culture="neutral"/><bindingRedirect oldVersion="0.0.0.0-' + $assembly.Version + '" newVersion="' + $assembly.Version + '"/></dependentAssembly>'
    }
}
$program = Join-Path $output 'FrameworkMcpProbe.cs'
$executable = Join-Path $output 'FrameworkMcpProbe.exe'
@'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Tansr.Sdk.Windows.Mcp;

internal static class FrameworkMcpProbe
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Within(Task task, int milliseconds, string name)
    { if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) throw new TimeoutException(name); await task; }
    private static async Task ExpectCancellation(Task task)
    { try { await task; throw new Exception("A canceled MCP read returned success."); } catch (OperationCanceledException) { } }
    private static async Task BodyCancellation(string mode)
    {
        using (var server = new Server(mode))
        {
            var client = await McpClient.ConnectHttpAsync(server.Options());
            Task close = null;
            try
            {
                var call = client.RequestAsync("tools/call", null, TimeSpan.FromSeconds(mode == "deadline-json" ? 1 : 10));
                await Within(server.Headers.Task, 3000, "Response headers did not arrive.");
                if (mode == "close-sse") close = client.CloseAsync();
                await Within(ExpectCancellation(call), 3000, mode + ": body cancellation did not settle.");
                if (close == null) close = client.CloseAsync();
                await Within(close, 4000, mode + ": Close did not drain.");
                Require(client.State == McpConnectionState.Closed, "Client did not close.");
                Require(server.Calls == 1 && server.Deletes == 1, "Unexpected call replay or missing DELETE.");
                if (mode == "deadline-json") Require(server.Cancellations == 1, "Missing cancellation notification.");
            }
            finally
            {
                // On a historical-product failure, release only our own stalled sockets,
                // then wait for cleanup; do not let the probe itself leak a child or socket.
                server.Stop();
                try { await Within(close ?? client.CloseAsync(), 4000, "Failure cleanup did not settle."); } catch { }
            }
        }
        Console.WriteLine("PASS " + mode);
    }
    private static async Task ReverseRequestCapacity()
    {
        using (var server = new Server("reverse"))
        {
            // Framework ordinarily exempts loopback from the remote two-connection
            // default. Set only this owned endpoint to the actual remote constraint.
            ServicePointManager.FindServicePoint(server.Endpoint).ConnectionLimit = 2;
            var client = await McpClient.ConnectHttpAsync(server.Options(), new McpClientOptions { MaximumPendingRequests = 2 });
            try
            {
                var first = client.RequestAsync("tools/call", null, TimeSpan.FromSeconds(6));
                var second = client.RequestAsync("tools/call", null, TimeSpan.FromSeconds(6));
                await Within(server.Headers.Task, 3000, "Both forward SSE requests did not arrive.");
                try { await client.RequestAsync("tools/call"); throw new Exception("Pending request cap was expanded."); }
                catch (McpException error) { Require(error.Code == "pending_limit", "Wrong pending rejection."); }
                server.ReleasePings.TrySetResult(true);
                await Within(Task.WhenAll(first, second), 4000, "Reverse responses starved behind forward SSE streams.");
                Require(first.Result.GetProperty("ok").GetBoolean() && second.Result.GetProperty("ok").GetBoolean(), "Wrong correlated result.");
                await Within(client.CloseAsync(), 4000, "Reverse connection close did not drain.");
                Require(server.Calls == 2 && server.Replies == 2 && server.Deletes == 1, "Unexpected reverse flow or replay.");
                Require(server.MaximumConnections <= 3, "Connection reserve is not bounded.");
            }
            finally { server.Stop(); try { await Within(client.CloseAsync(), 4000, "Cleanup"); } catch { } }
        }
        Console.WriteLine("PASS reverse-reserved-connection");
    }
    private static async Task<bool> Scenario(string name, Func<Task> run)
    {
        try { await run(); return true; }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); return false; }
    }
    private static async Task Run()
    {
        Require(Environment.Version.Major == 4, "Probe did not use CLR4.");
        int failures = 0;
        if (!await Scenario("deadline-json", () => BodyCancellation("deadline-json"))) failures++;
        if (!await Scenario("close-sse", () => BodyCancellation("close-sse"))) failures++;
        if (!await Scenario("reverse-reserved-connection", ReverseRequestCapacity)) failures++;
        if (failures != 0) throw new Exception("CLR4 MCP failed scenarios: " + failures + "/3");
        Console.WriteLine("PASS CLR4 MCP 3/3");
    }
    private static int Main()
    { try { Run().GetAwaiter().GetResult(); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }

    private sealed class Server : IDisposable
    {
        private readonly string mode;
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly ConcurrentBag<TcpClient> sockets = new ConcurrentBag<TcpClient>();
        private readonly ConcurrentBag<Task> workers = new ConcurrentBag<Task>();
        private readonly Task loop;
        private readonly TaskCompletionSource<bool> allReplies = new TaskCompletionSource<bool>();
        internal readonly TaskCompletionSource<bool> Headers = new TaskCompletionSource<bool>();
        internal readonly TaskCompletionSource<bool> ReleasePings = new TaskCompletionSource<bool>();
        internal int Calls, Replies, Deletes, Cancellations, MaximumConnections;
        private int active;
        internal Uri Endpoint;
        internal Server(string value)
        { mode = value; listener.Start(); Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/mcp"); loop = Accept(); }
        internal McpHttpOptions Options() { return new McpHttpOptions(Endpoint, new[] { Endpoint }) { AllowLoopbackHttp = true }; }
        private async Task Accept()
        {
            try { while (!stop.IsCancellationRequested) { var socket = await listener.AcceptTcpClientAsync(); sockets.Add(socket); workers.Add(Handle(socket)); } }
            catch (Exception) { if (!stop.IsCancellationRequested) throw; }
        }
        private async Task Handle(TcpClient socket)
        {
            int now = Interlocked.Increment(ref active), before;
            do { before = MaximumConnections; if (before >= now) break; } while (Interlocked.CompareExchange(ref MaximumConnections, now, before) != before);
            using (socket)
            try
            {
                var stream = socket.GetStream(); var head = new List<byte>(); var one = new byte[1];
                while (head.Count < 32768)
                { if (await stream.ReadAsync(one, 0, 1, stop.Token) == 0) return; head.Add(one[0]); int n = head.Count; if (n >= 4 && head[n-4] == 13 && head[n-3] == 10 && head[n-2] == 13 && head[n-1] == 10) break; }
                string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                int length = 0; foreach (string line in lines) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                var body = new byte[length]; for (int read = 0; read < length;) { int n = await stream.ReadAsync(body, read, length-read, stop.Token); if (n == 0) return; read += n; }
                var json = new JavaScriptSerializer(); var message = length == 0 ? new Dictionary<string, object>() : json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                string method = message.ContainsKey("method") ? (string)message["method"] : null;
                if (lines[0].StartsWith("DELETE ")) { Interlocked.Increment(ref Deletes); await Reply(stream, "204 No Content", "", "application/json", ""); return; }
                if (method == "initialize")
                { await Reply(stream, "200 OK", json.Serialize(new { jsonrpc = "2.0", id = message["id"], result = new { protocolVersion = "2025-11-25", capabilities = new { }, serverInfo = new { name = "owned-clr4", version = "1" } } }), "application/json", "Mcp-Session-Id: owned-clr4\r\n"); return; }
                if (method == "notifications/cancelled") Interlocked.Increment(ref Cancellations);
                if (method == null)
                { if (Interlocked.Increment(ref Replies) == 2) allReplies.TrySetResult(true); await Reply(stream, "202 Accepted", "", "application/json", ""); return; }
                if (method.StartsWith("notifications/")) { await Reply(stream, "202 Accepted", "", "application/json", ""); return; }
                Require(method == "tools/call", "Unexpected request."); int call = Interlocked.Increment(ref Calls);
                string type = mode == "deadline-json" ? "application/json" : "text/event-stream";
                await Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nConnection: close\r\n\r\n");
                if (mode != "reverse") { await Write(stream, mode == "deadline-json" ? "{" : ": open\n\n"); Headers.TrySetResult(true); await Task.Delay(Timeout.Infinite, stop.Token); return; }
                if (call == 2) Headers.TrySetResult(true);
                await ReleasePings.Task;
                await Write(stream, "data: " + json.Serialize(new { jsonrpc = "2.0", id = 900 + call, method = "ping" }) + "\n\n");
                await allReplies.Task;
                await Write(stream, "data: " + json.Serialize(new { jsonrpc = "2.0", id = message["id"], result = new { ok = true } }) + "\n\n");
            }
            catch (Exception) { if (!stop.IsCancellationRequested) throw; }
            finally { Interlocked.Decrement(ref active); }
        }
        private Task Write(NetworkStream stream, string text)
        { var bytes = Encoding.UTF8.GetBytes(text); return stream.WriteAsync(bytes, 0, bytes.Length, stop.Token); }
        private Task Reply(NetworkStream stream, string status, string body, string type, string extra)
        { return Write(stream, "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n" + extra + "\r\n" + body); }
        internal void Stop()
        { if (stop.IsCancellationRequested) return; stop.Cancel(); listener.Stop(); ReleasePings.TrySetCanceled(); allReplies.TrySetCanceled(); foreach (var socket in sockets) socket.Close(); }
        public void Dispose()
        { Stop(); loop.GetAwaiter().GetResult(); Task.WhenAll(workers.ToArray()).GetAwaiter().GetResult(); stop.Dispose(); }
    }
}
'@ | Set-Content -LiteralPath $program -Encoding utf8NoBOM
('<configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup><runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">' + ($redirects -join '') + '</assemblyBinding></runtime></configuration>') | Set-Content -LiteralPath ($executable + '.config') -Encoding utf8NoBOM
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compile = @('/nologo', '/target:exe', '/langversion:5', ('/out:' + $executable), '/r:System.Net.Http.dll', '/r:System.Web.Extensions.dll', ('/r:' + (Join-Path $framework 'Facades/netstandard.dll'))) + $references + @($program)
try {
    & (Join-Path $framework 'csc.exe') @compile 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
    $manifest.compileExitCode = $LASTEXITCODE
    if ($manifest.compileExitCode -ne 0) { throw 'Framework probe compilation failed; no product was rebuilt.' }
    $manifest.executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    & $executable 2>&1 | Tee-Object -FilePath (Join-Path $output 'run.log')
    $manifest.runExitCode = $LASTEXITCODE
    if ($manifest.runExitCode -ne 0) { throw 'Actual CLR4 MCP regression did not pass.' }
    $manifest.outcome = 'passed'
}
catch { $manifest.failure = $_.Exception.Message; $manifest.outcome = 'failed'; throw }
finally {
    $manifest.finishedAt = [DateTime]::UtcNow.ToString('o')
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
}
