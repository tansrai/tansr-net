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
foreach ($name in @('Tansr.Sdk.dll', 'System.Text.Json.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $library $name) -PathType Leaf)) { throw "Missing approved net48 input: $name" }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$manifest = [ordered]@{ source = $library; startedAt = [DateTime]::UtcNow.ToString('o'); outcome = 'incomplete'; inputs = @(); compileExitCode = $null; runExitCode = $null; boundary = 'Real CLR4 public session calls against owned loopback sockets and an injected async-only SSE stream; no product build, external network, credentials, signing or publication.' }
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
$program = Join-Path $output 'FrameworkSessionProbe.cs'
$executable = Join-Path $output 'FrameworkSessionProbe.exe'
@'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

internal static class FrameworkSessionProbe
{
    // UAPI-01: the SDK now calls the unified /api surface and refuses responses without the tansr-* contract headers
    // (ContractUnavailableException, no legacy fallback). Both owned fake servers stamp the four headers from the
    // generated ApiRoutes constants so a manifest revision bump does not break this probe.
    private static readonly string MetadataPath = ApiRoutes.SessionGet.Path("s");
    private static readonly string EventsPath = ApiRoutes.SessionEventsObserve.Path("s");
    private static readonly string UnifiedStamp =
        UnifiedHeaders.Contract + ": " + ApiRoutes.Contract + "\r\n" +
        UnifiedHeaders.ManifestRevision + ": " + ApiRoutes.ManifestRevision.ToString() + "\r\n" +
        UnifiedHeaders.Domain + ": " + ApiRoutes.SessionGet.Domain + "\r\n" +
        UnifiedHeaders.SchemaHash + ": " + ApiRoutes.DomainSchemaHash(ApiRoutes.SessionGet.Domain) + "\r\n";
    private static HttpResponseMessage Stamped(HttpResponseMessage response)
    {
        response.Headers.TryAddWithoutValidation(UnifiedHeaders.Contract, ApiRoutes.Contract);
        response.Headers.TryAddWithoutValidation(UnifiedHeaders.ManifestRevision, ApiRoutes.ManifestRevision.ToString());
        response.Headers.TryAddWithoutValidation(UnifiedHeaders.Domain, ApiRoutes.SessionGet.Domain);
        response.Headers.TryAddWithoutValidation(UnifiedHeaders.SchemaHash, ApiRoutes.DomainSchemaHash(ApiRoutes.SessionGet.Domain));
        return response;
    }
    private static async Task Within(Task task, int milliseconds, string name)
    { if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) throw new TimeoutException(name); await task; }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Run()
    {
        Require(Environment.Version.Major == 4, "Must run actual CLR4.");
        Console.WriteLine("CLR " + Environment.Version);
        using (var server = new Server())
        using (var observation = new CancellationTokenSource())
        {
            ServicePoint point = ServicePointManager.FindServicePoint(server.Origin);
            int original = point.ConnectionLimit;
            point.ConnectionLimit = 2;
            Console.WriteLine("OWNED_ENDPOINT " + server.Origin + " originalLimit=" + original + " explicitLimit=" + point.ConnectionLimit);
            var options = new TansrClientOptions { BaseUri = server.Origin, AllowInsecureLoopback = true,
                TokenProvider = token => Task.FromResult("synthetic-pool-token"), RequestTimeout = TimeSpan.FromSeconds(5),
                StreamIdleTimeout = TimeSpan.FromSeconds(30), MaxReconnectAttempts = 0 };
            using (var client = new TansrClient(options))
            {
                Task first = null, second = null, third = null;
                try
                {
                    var session = await client.GetSessionAsync("s");
                    Require(server.Metadata == 1, "Initial public metadata did not complete.");
                    first = session.ObserveAsync((item, token) => Task.FromResult(true), new EventStreamOptions { Reconnect = false }, observation.Token);
                    second = session.ObserveAsync((item, token) => Task.FromResult(true), new EventStreamOptions { Reconnect = false }, observation.Token);
                    third = session.ObserveAsync((item, token) => Task.FromResult(true), new EventStreamOptions { Reconnect = false }, observation.Token);
                    await Within(server.AllEvents.Task, 3000, "Three actual SSE requests did not reach the owned endpoint.");
                    Require(!first.IsCompleted && !second.IsCompleted && !third.IsCompleted, "Observers did not stay active.");
                    Console.WriteLine("THREE_STREAMS metadata=" + server.Metadata + " events=" + server.Events + " active=" + server.Active + " limit=" + point.ConnectionLimit);
                    using (var deadline = new CancellationTokenSource(1500))
                        await Within(session.GetMetadataAsync(deadline.Token), 3000, "Control starved behind persistent SSE streams.");
                    Require(server.Metadata == 2 && server.Events == 3, "Unexpected control replay or stream count.");
                    Require(!first.IsCompleted && !second.IsCompleted && !third.IsCompleted, "Control required terminating a stream.");
                    Console.WriteLine("CONTROL_WITH_THREE_STREAMS metadata=" + server.Metadata + " events=" + server.Events + " maximum=" + server.Maximum);
                    observation.Cancel();
                    server.ReleaseStreams();
                    await Settle(first); await Settle(second); await Settle(third);
                    await session.GetMetadataAsync();
                    Require(server.Metadata == 3, "Control did not recover after stream cancellation or was replayed.");
                    Require(server.Maximum <= 4, "Unexpected connection fanout.");
                    Console.WriteLine("PASS CLR4 three persistent subscriptions, independent control and cancellation cleanup.");
                }
                finally
                {
                    observation.Cancel(); server.ReleaseStreams();
                    if (first != null) Settle(first).GetAwaiter().GetResult();
                    if (second != null) Settle(second).GetAwaiter().GetResult();
                    if (third != null) Settle(third).GetAwaiter().GetResult();
                    point.ConnectionLimit = original;
                }
            }
        }
        Console.WriteLine("CLEANUP owned listener, streams and client disposed; no global limit changed.");
        await InjectedAsyncOnlyStream();
    }
    private static async Task InjectedAsyncOnlyStream()
    {
        using (var handler = new AsyncOnlyHandler())
        using (var http = new HttpClient(handler))
        using (var cancellation = new CancellationTokenSource())
        {
            var options = new TansrClientOptions { BaseUri = new Uri("http://127.0.0.1:1/"), AllowInsecureLoopback = true,
                TokenProvider = token => Task.FromResult("synthetic-injected-token"), RequestTimeout = TimeSpan.FromSeconds(5),
                StreamIdleTimeout = TimeSpan.FromSeconds(30), MaxReconnectAttempts = 0 };
            Task observation = null;
            using (var client = new TansrClient(options, http))
            try
            {
                var session = await client.GetSessionAsync("s");
                observation = session.ObserveAsync((item, token) => Task.FromResult(true), new EventStreamOptions { Reconnect = false }, cancellation.Token);
                Task reached = await Task.WhenAny(observation, handler.Stream.Waiting.Task, Task.Delay(3000));
                if (reached == observation) await observation;
                Require(reached == handler.Stream.Waiting.Task && !observation.IsCompleted,
                    "Injected async-only stream did not deliver its heartbeat and remain subscribed.");
                Require(handler.Stream.AsyncReads >= 2 && handler.Stream.SyncReads == 0,
                    "Injected stream was not read through its asynchronous contract.");
                cancellation.Cancel();
                await Settle(observation);
                Require(handler.Metadata == 1 && handler.Events == 1, "Injected requests were unexpectedly retried.");
                Require(handler.Stream.Disposed, "Canceled injected SSE response stream was not disposed.");
                Console.WriteLine("PASS CLR4 injected async-only SSE heartbeat and cancellation; asyncReads=" + handler.Stream.AsyncReads + " syncReads=" + handler.Stream.SyncReads);
            }
            finally
            {
                cancellation.Cancel();
                if (observation != null)
                {
                    // Preserve an earlier protocol failure while still settling our own subscription.
                    try { Settle(observation).GetAwaiter().GetResult(); } catch { }
                }
            }
            Require(!handler.Disposed, "The SDK disposed the caller-owned injected HTTP client.");
        }
        Console.WriteLine("CLEANUP injected stream and caller-owned client disposed.");
    }
    private static async Task Settle(Task task)
    { try { await Within(task, 4000, "Owned observation failed to settle."); throw new Exception("Canceled observation returned success."); } catch (OperationCanceledException) { } }
    private static int Main()
    { try { Run().GetAwaiter().GetResult(); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }

    private sealed class AsyncOnlyHandler : HttpMessageHandler
    {
        internal readonly AsyncOnlyStream Stream = new AsyncOnlyStream();
        internal int Metadata, Events;
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.Method == HttpMethod.Get, "Unexpected injected request method.");
            if (request.RequestUri.AbsolutePath == EventsPath)
            {
                Interlocked.Increment(ref Events);
                var content = new StreamContent(Stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(Stamped(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
            }
            Require(request.RequestUri.AbsolutePath == MetadataPath, "Unexpected injected request route: " + request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref Metadata);
            return Task.FromResult(Stamped(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"sessionId\":\"s\",\"lastSeq\":-1}", Encoding.UTF8, "application/json") }));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class AsyncOnlyStream : Stream
    {
        internal readonly TaskCompletionSource<bool> Waiting = new TaskCompletionSource<bool>();
        internal int AsyncReads, SyncReads;
        internal bool Disposed;
        public override int Read(byte[] buffer, int offset, int count)
        { Interlocked.Increment(ref SyncReads); throw new NotSupportedException("Synthetic async-only SSE cannot use synchronous Read."); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (Interlocked.Increment(ref AsyncReads) == 1)
            {
                byte[] heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");
                Require(count >= heartbeat.Length, "Unexpected SSE read buffer size.");
                Buffer.BlockCopy(heartbeat, 0, buffer, offset, heartbeat.Length);
                return heartbeat.Length;
            }
            Waiting.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
    private sealed class Server : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly ConcurrentBag<TcpClient> sockets = new ConcurrentBag<TcpClient>();
        private readonly ConcurrentBag<Task> workers = new ConcurrentBag<Task>();
        private readonly TaskCompletionSource<bool> release = new TaskCompletionSource<bool>();
        private readonly Task loop;
        internal readonly TaskCompletionSource<bool> AllEvents = new TaskCompletionSource<bool>();
        internal int Metadata, Events, Active, Maximum;
        internal readonly Uri Origin;
        internal Server()
        { listener.Start(); Origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"); loop = Accept(); }
        private async Task Accept()
        {
            try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(); sockets.Add(client); workers.Add(Handle(client)); } }
            catch { if (!stop.IsCancellationRequested) throw; }
        }
        private async Task Handle(TcpClient client)
        {
            int active = Interlocked.Increment(ref Active), previous;
            do { previous = Maximum; if (previous >= active) break; } while (Interlocked.CompareExchange(ref Maximum, active, previous) != previous);
            using (client)
            try
            {
                NetworkStream stream = client.GetStream(); var bytes = new List<byte>(); var one = new byte[1];
                while (bytes.Count < 32768)
                { if (await stream.ReadAsync(one, 0, 1, stop.Token) == 0) return; bytes.Add(one[0]); int n = bytes.Count; if (n >= 4 && bytes[n-4] == 13 && bytes[n-3] == 10 && bytes[n-2] == 13 && bytes[n-1] == 10) break; }
                string first = Encoding.ASCII.GetString(bytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                Console.WriteLine("SERVER " + first);
                if (first == "GET " + EventsPath + " HTTP/1.1")
                {
                    await Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" + UnifiedStamp + "Connection: close\r\n\r\n: connected\n\n");
                    if (Interlocked.Increment(ref Events) == 3) AllEvents.TrySetResult(true);
                    await release.Task;
                    return;
                }
                Require(first == "GET " + MetadataPath + " HTTP/1.1", "Unexpected route: " + first);
                Interlocked.Increment(ref Metadata);
                const string json = "{\"sessionId\":\"s\",\"lastSeq\":-1}";
                await Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + UnifiedStamp + "Content-Length: " + Encoding.UTF8.GetByteCount(json) + "\r\nConnection: close\r\n\r\n" + json);
            }
            catch { if (!stop.IsCancellationRequested) throw; }
            finally { Interlocked.Decrement(ref Active); }
        }
        private Task Write(NetworkStream stream, string value)
        { var data = Encoding.UTF8.GetBytes(value); return stream.WriteAsync(data, 0, data.Length, stop.Token); }
        internal void ReleaseStreams() { release.TrySetResult(true); }
        public void Dispose()
        { stop.Cancel(); listener.Stop(); release.TrySetResult(true); foreach (var client in sockets) client.Close(); loop.GetAwaiter().GetResult(); Task.WhenAll(workers.ToArray()).GetAwaiter().GetResult(); Require(Active == 0, "Owned sockets did not drain."); stop.Dispose(); }
    }
}

'@ | Set-Content -LiteralPath $program -Encoding utf8NoBOM
('<configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup><runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">' + ($redirects -join '') + '</assemblyBinding></runtime></configuration>') | Set-Content -LiteralPath ($executable + '.config') -Encoding utf8NoBOM
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$facades = @(
    (Join-Path $library 'netstandard.dll'),
    (Join-Path $framework 'Facades/netstandard.dll'),
    (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8/Facades/netstandard.dll')
)
$packageRoots = @($env:NUGET_PACKAGES, (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages')) | Where-Object { $_ } | Select-Object -Unique
foreach ($packageRoot in $packageRoots) {
    $referencePackage = Join-Path $packageRoot 'microsoft.netframework.referenceassemblies.net48'
    if (Test-Path -LiteralPath $referencePackage -PathType Container) {
        foreach ($installed in Get-ChildItem -LiteralPath $referencePackage -Directory | Sort-Object Name -Descending) {
            $facades += Join-Path $installed.FullName 'build/.NETFramework/v4.8/Facades/netstandard.dll'
        }
    }
}
$netstandard = $facades | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
try {
    if (-not $netstandard) { throw 'Install the existing net48 reference-assemblies dependency or supply its netstandard.dll beside the approved candidate; no reference assembly was downloaded.' }
    $manifest.netstandardReference = @{ path = $netstandard; sha256 = (Get-FileHash -LiteralPath $netstandard -Algorithm SHA256).Hash.ToLowerInvariant() }
    $compile = @('/nologo', '/target:exe', '/langversion:5', ('/out:' + $executable), '/r:System.Net.Http.dll', ('/r:' + $netstandard)) + $references + @($program)
    & (Join-Path $framework 'csc.exe') @compile 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
    $manifest.compileExitCode = $LASTEXITCODE
    if ($manifest.compileExitCode -ne 0) { throw 'Framework probe compilation failed; no product was rebuilt.' }
    $manifest.executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    & $executable 2>&1 | Tee-Object -FilePath (Join-Path $output 'run.log')
    $manifest.runExitCode = $LASTEXITCODE
    if ($manifest.runExitCode -ne 0) { throw 'Actual CLR4 session pool regression did not pass.' }
    $manifest.outcome = 'passed'
}
catch { $manifest.failure = $_.Exception.Message; $manifest.outcome = 'failed'; throw }
finally {
    $manifest.finishedAt = [DateTime]::UtcNow.ToString('o')
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
}
