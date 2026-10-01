using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Tests.Mcp;

[CollectionDefinition("Native MCP published candidate", DisableParallelization = true)]
public sealed class NativeMcpCandidateCollection { }

[Collection("Native MCP published candidate")]
public sealed class NativeMcpBridgeTests
{
    [Fact]
    public async Task Sdk1SseRequestRunsPublishedNativeMcpAndConsumesOriginalReceiptOnce()
    {
        using var candidate = new Candidate(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var before = CandidateProcesses(candidate.Executable);
        var connection = await NativeMcpToolConnection.OpenConfiguredAsync(deadline.Token);
        Assert.NotNull(connection);
        var processes = CandidateProcesses(candidate.Executable).Except(before).ToArray();
        try
        {
            Assert.Single(processes);
            using var server = new Sdk1Server(false, false);
            using var client = server.Client();
            var original = Assert.Single(connection.Bindings); var calls = 0; var observedRequests = 0;
            var duplicateObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var instrumented = new NativeToolBinding(original.Declaration, async (arguments, token) =>
            { Interlocked.Increment(ref calls); await duplicateObserved.Task.WaitAsync(token); return await original.ExecuteAsync(arguments, token); });
            var declarations = NativeToolHost.GetDeclarations(new[] { instrumented });
            var session = await client.CreateSessionAsync(new CreateSessionOptions { ClientTools = declarations }, deadline.Token);
            var reports = new ConcurrentQueue<string>();
            using var host = new NativeToolHost(session, "bridge-test", (_, _) => Task.CompletedTask, reports.Enqueue, new[] { instrumented });
            await host.Ready.WaitAsync(deadline.Token);
            await session.ObserveAsync((item, _) =>
            {
                host.HandleEvent(item);
                if (item.Name == "server.tool.request" && Interlocked.Increment(ref observedRequests) == 2) duplicateObserved.TrySetResult(true);
                return Task.CompletedTask;
            }, new EventStreamOptions { Reconnect = false }, deadline.Token);
            await host.DrainAsync(deadline.Token);
            Assert.Equal(1, calls);
            var receipt = Assert.Single(server.Receipts);
            Assert.Equal("ok", receipt.GetProperty("status").GetString());
            Assert.Equal("C# 原生 MCP through SDK1 \"quoted\" 🙂", receipt.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("text", receipt.GetProperty("content")[0].GetProperty("t").GetString());
            Assert.Equal("mcp_echo", server.Creation!.Value.GetProperty("clientTools")[2].GetProperty("name").GetString());
            Assert.True(server.Creation.Value.GetProperty("clientTools")[2].GetProperty("readOnly").GetBoolean());
            Assert.Empty(reports); Assert.Empty(server.Errors);
        }
        finally { await connection.CloseAsync(); }
        Assert.All(processes, AssertExited);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Bindings[0].ExecuteAsync(Json("{\"text\":\"after-close\"}"), deadline.Token));
    }

    [Fact]
    public async Task ResumedSdk1SessionRefusesToReexecuteNativeMcpEvenWithLiveConnection()
    {
        using var candidate = new Candidate(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connection = await NativeMcpToolConnection.OpenConfiguredAsync(deadline.Token); Assert.NotNull(connection);
        try
        {
            using var server = new Sdk1Server(true, false); using var client = server.Client();
            var original = Assert.Single(connection.Bindings); var calls = 0;
            var binding = new NativeToolBinding(original.Declaration, (args, ct) => { Interlocked.Increment(ref calls); return original.ExecuteAsync(args, ct); });
            var session = await client.CreateSessionAsync(new CreateSessionOptions { ResumeSessionId = "session" }, deadline.Token);
            var reports = new ConcurrentQueue<string>();
            using var host = new NativeToolHost(session, "bridge-test", (_, _) => Task.CompletedTask, reports.Enqueue, new[] { binding });
            await host.Ready.WaitAsync(deadline.Token);
            await session.ObserveAsync((item, _) => { host.HandleEvent(item); return Task.CompletedTask; }, new EventStreamOptions { Reconnect = false }, deadline.Token);
            await host.DrainAsync(deadline.Token);
            Assert.Equal(0, calls);
            Assert.Equal("native_tool_prior_outcome_unknown", Assert.Single(server.Receipts).GetProperty("message").GetString());
            Assert.Equal(1, server.MetadataReads);
            Assert.Empty(reports); Assert.Empty(server.Errors);
        }
        finally { await connection.CloseAsync(); }
    }

    [Fact]
    public async Task UntrustedArgumentShapeReturnsOriginalToolErrorWithoutExpandingMcpCapabilities()
    {
        using var candidate = new Candidate(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connection = await NativeMcpToolConnection.OpenConfiguredAsync(deadline.Token); Assert.NotNull(connection);
        try
        {
            using var server = new Sdk1Server(false, true); using var client = server.Client();
            var session = await client.CreateSessionAsync(new CreateSessionOptions { ClientTools = NativeToolHost.GetDeclarations(connection.Bindings) }, deadline.Token);
            var reports = new ConcurrentQueue<string>();
            using var host = new NativeToolHost(session, "bridge-test", (_, _) => Task.CompletedTask, reports.Enqueue, connection.Bindings);
            await host.Ready.WaitAsync(deadline.Token);
            await session.ObserveAsync((item, _) => { host.HandleEvent(item); return Task.CompletedTask; }, new EventStreamOptions { Reconnect = false }, deadline.Token);
            await host.DrainAsync(deadline.Token);
            var receipt = Assert.Single(server.Receipts);
            Assert.Equal("error", receipt.GetProperty("status").GetString());
            Assert.Equal("native_mcp_invalid_arguments", receipt.GetProperty("message").GetString());
            Assert.Empty(reports); Assert.Empty(server.Errors);
        }
        finally { await connection.CloseAsync(); }
    }

    [Fact]
    public async Task CancelingConnectionLifetimeAfterHandshakeTerminatesOwnedNativeMcp()
    {
        using var candidate = new Candidate(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var before = CandidateProcesses(candidate.Executable);
        var connection = await NativeMcpToolConnection.OpenConfiguredAsync(lifetime.Token); Assert.NotNull(connection);
        try
        {
            var pid = Assert.Single(CandidateProcesses(candidate.Executable).Except(before));
            using var process = Process.GetProcessById(pid);
            lifetime.Cancel();
            // 先证明原生命周期取消已经终止进程，再调用Close等待管道/Job清理，不能由Close制造该证据。
            await process.WaitForExitAsync(deadline.Token); Assert.True(process.HasExited);
        }
        finally { await connection.CloseAsync(); }
    }

    [Fact]
    public async Task UnapprovedCandidateDigestPreventsNativeMcpProcessCreation()
    {
        using var candidate = new Candidate(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var before = CandidateProcesses(candidate.Executable);
        Environment.SetEnvironmentVariable("TANSR_MCP_SHA256", new string('0', 64));
        var error = await Assert.ThrowsAsync<WindowsDuplexProcessException>(() => NativeMcpToolConnection.OpenConfiguredAsync(deadline.Token));
        Assert.False(error.OperationMayHaveStarted); Assert.Equal("executable_digest_mismatch", error.Code);
        Assert.Empty(CandidateProcesses(candidate.Executable).Except(before));
    }

    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static int[] CandidateProcesses(string path)
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            using (process) try { if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase) && !process.HasExited) ids.Add(process.Id); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
        return ids.ToArray();
    }
    private static void AssertExited(int pid)
    { try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); } catch (ArgumentException) { } }

    private sealed class Candidate : IDisposable
    {
        private static readonly string[] Names = { "TANSR_MCP_EXE", "TANSR_MCP_SHA256", "TANSR_MCP_WORKSPACE", "TANSR_MCP_DLL" };
        private readonly string?[] _previous = Names.Select(Environment.GetEnvironmentVariable).ToArray();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tansr-native-mcp-bridge-" + Guid.NewGuid().ToString("N"));
        internal string Executable { get; }
        internal Candidate()
        {
            var configured = Environment.GetEnvironmentVariable("TANSR_TEST_MCP_EXE");
            Assert.False(string.IsNullOrWhiteSpace(configured), "必须提供集中发布的 TANSR_TEST_MCP_EXE 候选；缺少候选不能算通过。");
            Executable = Path.GetFullPath(configured!);
            Assert.True(File.Exists(Executable), "集中发布的原生MCP候选不存在。");
            using var file = File.OpenRead(Executable); var digest = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable(Names[0], Executable); Environment.SetEnvironmentVariable(Names[1], digest);
            Environment.SetEnvironmentVariable(Names[2], _root); Environment.SetEnvironmentVariable(Names[3], null);
        }
        public void Dispose()
        {
            for (var index = 0; index < Names.Length; index++) Environment.SetEnvironmentVariable(Names[index], _previous[index]);
            Directory.Delete(_root, false);
        }
    }

    /// <summary>真实HTTP/SSE socket发送冻结SDK1工具控制帧；此对端不冒充真实Serve模型循环。</summary>
    private sealed class Sdk1Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = new();
        private readonly Task _loop;
        private readonly bool _resumed, _invalidArguments;
        private readonly TaskCompletionSource<bool> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<JsonElement> Receipts = new();
        internal readonly ConcurrentQueue<Exception> Errors = new();
        internal int MetadataReads;
        internal JsonElement? Creation;
        internal Sdk1Server(bool resumed, bool invalidArguments)
        { _resumed = resumed; _invalidArguments = invalidArguments; _listener.Start(); _loop = AcceptAsync(); }
        internal TansrClient Client() => new(new TansrClientOptions
        { BaseUri = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port), AllowInsecureLoopback = true, TokenProvider = _ => Task.FromResult("synthetic-bridge-token") });
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) { var socket = await _listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(HandleAsync(socket)); } }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }
        private async Task HandleAsync(TcpClient socket)
        {
            using (socket)
            {
                try
                {
                    var stream = socket.GetStream(); var header = new List<byte>(); var one = new byte[1];
                    while (header.Count < 32768)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                        header.Add(one[0]);
                        if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
                    Assert.Contains("Authorization: Bearer synthetic-bridge-token", lines);
                    var first = lines[0].Split(' '); var lengthLine = lines.FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var length = lengthLine == null ? 0 : int.Parse(lengthLine.Substring(15).Trim()); Assert.InRange(length, 0, 65536);
                    var body = new byte[length]; await stream.ReadExactlyAsync(body, _stop.Token);
                    if (first[0] == "POST" && first[1] == "/api/sessions")
                    {
                        Creation = Json(Encoding.UTF8.GetString(body));
                        await RespondAsync(stream, "{\"sessionId\":\"session\",\"resumed\":" + (_resumed ? "true" : "false") + ",\"lastSeq\":-1}");
                    }
                    else if (first[0] == "GET" && first[1] == "/api/sessions/session")
                    {
                        Assert.True(_resumed); Interlocked.Increment(ref MetadataReads);
                        // The replayed call at seq 0 predates this authoritative watermark. A
                        // live MCP connection never turns its unknown prior outcome into permission to redo it.
                        await RespondAsync(stream, "{\"sessionId\":\"session\",\"endUserId\":\"bridge-test\",\"live\":true,\"status\":\"idle\",\"lastSeq\":0,\"createdAt\":\"2026-09-27T00:00:00Z\",\"lastActivityAt\":\"2026-09-27T00:00:00Z\"}");
                    }
                    else if (first[1] == "/api/sessions/session/events")
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" + UnifiedStamp + "Connection: close\r\n\r\n"), _stop.Token);
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        using var bytes = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(bytes))
                        {
                            writer.WriteStartObject(); writer.WriteNumber("ts", now); writer.WriteString("callId", "native-call"); writer.WriteString("name", "mcp_echo");
                            writer.WriteNumber("deadlineAt", now + 10000); writer.WriteNumber("ttlMs", 10000); writer.WriteStartObject("args");
                            writer.WriteString(_invalidArguments ? "unexpected" : "text", "C# 原生 MCP through SDK1 \"quoted\" 🙂"); writer.WriteEndObject(); writer.WriteEndObject();
                        }
                        var data = Encoding.UTF8.GetString(bytes.ToArray());
                        // 同一call以不同合法seq重复抵达，仍只能调用一次实际MCP委托。
                        var frames = "id: 0\nevent: server.tool.request\ndata: " + data + "\n\n";
                        if (!_resumed && !_invalidArguments) frames += "id: 1\nevent: server.tool.request\ndata: " + data + "\n\n";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(frames), _stop.Token);
                        await _received.Task.WaitAsync(_stop.Token);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("id: 2\ndata: {\"type\":\"session.ended\",\"sessionId\":\"session\",\"seq\":2}\n\n"), _stop.Token);
                    }
                    else if (first[1] == "/api/sessions/session/tool-results/native-call")
                    { Receipts.Enqueue(Json(Encoding.UTF8.GetString(body))); await RespondAsync(stream, "{\"accepted\":true}"); _received.TrySetResult(true); }
                    else throw new InvalidOperationException("Unexpected test route: " + first[0] + " " + first[1]);
                }
                catch (Exception error) when (_stop.IsCancellationRequested || error is IOException && Receipts.Count > 0) { }
                catch (Exception error) { Errors.Enqueue(error); _received.TrySetException(error); }
            }
        }
        // UAPI-01: the unified facade stamps every /api response (session domain here) with the four mandatory headers;
        // without them the SDK refuses the response as contract_unavailable (D10, no legacy fallback).
        private static readonly string UnifiedStamp =
            UnifiedHeaders.Contract + ": " + ApiRoutes.Contract + "\r\n" +
            UnifiedHeaders.ManifestRevision + ": " + ApiRoutes.ManifestRevision.ToString(CultureInfo.InvariantCulture) + "\r\n" +
            UnifiedHeaders.Domain + ": " + ApiRoutes.SessionCreate.Domain + "\r\n" +
            UnifiedHeaders.SchemaHash + ": " + ApiRoutes.DomainSchemaHash(ApiRoutes.SessionCreate.Domain) + "\r\n";
        private async Task RespondAsync(NetworkStream stream, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + UnifiedStamp + "Connection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n"), _stop.Token);
            await stream.WriteAsync(bytes, _stop.Token);
        }
        public void Dispose()
        { _stop.Cancel(); _listener.Stop(); _loop.GetAwaiter().GetResult(); Task.WhenAll(_clients.ToArray()).GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
