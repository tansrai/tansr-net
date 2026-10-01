using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;
using Xunit;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class OwnedServeHttpHandlerTests : IDisposable, IClassFixture<OwnedHttpFixture>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tansr-owned-http-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsWorkspace _workspace;
    private readonly OwnedHttpFixture _fixture;
    public OwnedServeHttpHandlerTests(OwnedHttpFixture fixture) { Directory.CreateDirectory(_directory); _workspace = new WindowsWorkspace(_directory); _fixture = fixture; }

    [Fact]
    public async Task ApprovedLiveChildUsesSameVerifiedSocketForCredentialsAndPostBody()
    {
        using var process = await Start(); var origin = await Origin(process);
        using var http = Client(origin, process);
        Assert.True(origin.Port > 32767); // 系统动态高位端口，覆盖网络序有符号转换。
        using var response = await http.PostAsync(new Uri(origin, "/echo"), new StringContent("中文 payload", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("中文 payload", await response.Content.ReadAsStringAsync());
        Assert.Equal("yes", response.Headers.GetValues("X-Synthetic-Auth").Single());
    }

    [Fact]
    public async Task UnownedPortReceivesNoHttpBytesEvenWhenItCouldImpersonateReadiness()
    {
        using var process = await Start(); _ = await Origin(process);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = listener.AcceptTcpClientAsync();
            using var http = Client(origin, process);
            var failure = await Assert.ThrowsAsync<TansrProtocolException>(() => http.GetAsync(new Uri(origin, "/api/sessions")));
            Assert.Equal("serve_peer_not_owned", failure.Code);
            using var attacker = await accepted.WaitAsync(TimeSpan.FromSeconds(3));
            var bytes = new byte[512];
            Assert.Equal(0, await attacker.GetStream().ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task OriginalOwnerExitPreventsNewRequestsWhenPortIsTakenOver()
    {
        using var process = await Start(); var origin = await Origin(process);
        using var http = Client(origin, process);
        Assert.Equal("hello", await http.GetStringAsync(new Uri(origin, "/fixed")));
        Assert.True((await process.CloseAsync()).CleanupConfirmed);
        var attacker = new TcpListener(IPAddress.Loopback, origin.Port); attacker.Start();
        try
        {
            var failure = await Assert.ThrowsAsync<TansrProtocolException>(() => http.GetAsync(new Uri(origin, "/fixed")));
            Assert.Equal("serve_owner_exited", failure.Code); Assert.False(attacker.Pending());
        }
        finally { attacker.Stop(); }
    }

    [Fact]
    public async Task ChunkedUnicodeAndSseAreDecodedIncrementally()
    {
        using var process = await Start(); var origin = await Origin(process); using var http = Client(origin, process);
        Assert.Equal("中文🙂", await http.GetStringAsync(new Uri(origin, "/chunked")));
        using var response = await http.GetAsync(new Uri(origin, "/sse"), HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync(); var buffer = new byte[256];
        int read = await stream.ReadAsync(buffer);
        Assert.Contains("first", Encoding.UTF8.GetString(buffer, 0, read));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Contains("second", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("/conflicting", "serve_http_framing_unsupported")]
    [InlineData("/gzip", "serve_http_framing_unsupported")]
    [InlineData("/headers", "serve_http_framing_invalid")]
    [InlineData("/truncated", "serve_http_truncated")]
    [InlineData("/chunk-extension", "serve_http_chunk_unsupported")]
    public async Task UnsupportedOrTruncatedFramingFailsClearly(string path, string expected)
    {
        using var process = await Start(); var origin = await Origin(process); using var http = Client(origin, process);
        var failure = await Assert.ThrowsAsync<TansrProtocolException>(() => http.GetStringAsync(new Uri(origin, path)));
        Assert.Equal(expected, failure.Code);
    }

    [Fact]
    public async Task RedirectsAreReturnedAndNeverFollowed()
    {
        using var process = await Start(); var origin = await Origin(process); using var http = Client(origin, process);
        using var response = await http.GetAsync(new Uri(origin, "/redirect"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task CancellationClosesStreamingSocketWithoutReplay()
    {
        using var process = await Start(); var origin = await Origin(process); using var http = Client(origin, process);
        using var response = await http.GetAsync(new Uri(origin, "/hang"), HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync(); var buffer = new byte[32];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(buffer, 0, buffer.Length, cancellation.Token));
    }

    [Fact]
    public async Task UnknownLengthRequestContentIsBoundedBeforeAnyNetworkAndCapacityIsReleased()
    {
        using var process = await Start(); var origin = await Origin(process); using var http = Client(origin, process);
        using var content = new OverflowContent();
        var failure = await Assert.ThrowsAsync<TansrProtocolException>(() => http.PostAsync(new Uri(origin, "/echo"), content));
        Assert.Equal("serve_request_too_large", failure.Code);
        Assert.Equal(32, content.BlocksWritten);
        Assert.Equal("hello", await http.GetStringAsync(new Uri(origin, "/fixed")));
    }

    private sealed class OverflowContent : HttpContent
    {
        internal int BlocksWritten;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var block = new byte[1024 * 1024];
            for (int i = 0; i < 40; i++) { await stream.WriteAsync(block, 0, block.Length); BlocksWritten++; }
        }
    }

    private async Task<WindowsDuplexProcess> Start()
    {
        var options = new WindowsDuplexProcessOptions(_fixture.Executable, Array.Empty<string>(), () => _workspace.AcquireProcessDirectory());
        options.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return await WindowsDuplexProcess.StartAsync(options);
    }
    private static async Task<Uri> Origin(WindowsDuplexProcess process) => new("http://127.0.0.1:" + await process.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    private static HttpClient Client(Uri origin, WindowsDuplexProcess process)
    {
        var http = new HttpClient(new OwnedServeHttpHandler(origin, process));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-owned-token"); return http;
    }
    public void Dispose() { _workspace.Dispose(); Directory.Delete(_directory, true); }
}

public sealed class OwnedHttpFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tansr-owned-http-fixture-" + Guid.NewGuid().ToString("N"));
    public string Executable { get; }
    public OwnedHttpFixture()
    {
        Directory.CreateDirectory(_directory); Executable = Path.Combine(_directory, "http.exe"); var source = Path.Combine(_directory, "http.cs");
        File.WriteAllText(source, Source, Encoding.UTF8);
        string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", Environment.Is64BitProcess ? "Framework64" : "Framework", "v4.0.30319", "csc.exe");
        using var build = Process.Start(new ProcessStartInfo(compiler, "/nologo /target:exe /out:\"" + Executable + "\" \"" + source + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = build.StandardOutput.ReadToEndAsync(); var error = build.StandardError.ReadToEndAsync();
        if (!build.WaitForExit(30000)) { build.Kill(true); throw new TimeoutException("Fixture compile timed out."); }
        Assert.True(build.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
    public void Dispose() => Directory.Delete(_directory, true);
    private const string Source = """
        using System; using System.Collections.Generic; using System.Net; using System.Net.Sockets; using System.Text; using System.Threading;
        class Program {
          static void Write(NetworkStream s,string text) { var b=Encoding.UTF8.GetBytes(text); s.Write(b,0,b.Length); s.Flush(); }
          static void Chunk(NetworkStream s,string text) { var b=Encoding.UTF8.GetBytes(text); Write(s,b.Length.ToString("x")+"\r\n"); s.Write(b,0,b.Length); Write(s,"\r\n"); }
          static void Main() {
            var server=new TcpListener(IPAddress.Loopback,0); server.Start(); Console.WriteLine(((IPEndPoint)server.LocalEndpoint).Port);
            while(true) using(var client=server.AcceptTcpClient()) {
              var stream=client.GetStream(); var h=new StringBuilder(); int ch;
              while((ch=stream.ReadByte())>=0) { h.Append((char)ch); if(h.ToString().EndsWith("\r\n\r\n")) break; if(h.Length>65536)return; }
              if(ch<0)continue; string header=h.ToString(), path=header.Split(' ')[1]; int length=0;
              foreach(string line in header.Split(new[]{"\r\n"},StringSplitOptions.None)) if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=int.Parse(line.Substring(15));
              byte[] body=new byte[length]; for(int p=0;p<length;) { int n=stream.Read(body,p,length-p); if(n==0)return;p+=n; }
              string auth=header.Contains("Authorization: Bearer synthetic-owned-token")?"yes":"no";
              if(path=="/truncated") { Write(stream,"HTTP/1.1 200 OK\r\nContent-Length: 8\r\n\r\nabc");continue; }
              if(path=="/headers") { Write(stream,"HTTP/1.1 200 OK\r\nX-Large: "+new string('x',9000)+"\r\nContent-Length: 0\r\n\r\n");continue; }
              if(path=="/conflicting") { Write(stream,"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");continue; }
              if(path=="/gzip") { Write(stream,"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 0\r\n\r\n");continue; }
              if(path=="/redirect") { Write(stream,"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/never\r\nContent-Length: 0\r\n\r\n");continue; }
              if(path=="/chunked" || path=="/sse" || path=="/hang" || path=="/chunk-extension") {
                Write(stream,"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: text/event-stream\r\n\r\n");
                if(path=="/chunk-extension") { Write(stream,"1;x=1\r\nx\r\n0\r\n\r\n");continue; }
                if(path=="/hang") { Chunk(stream,"x");Thread.Sleep(60000);continue; }
                if(path=="/chunked") Chunk(stream,"中文🙂");
                else { Chunk(stream,"data: first\n\n");Thread.Sleep(150);Chunk(stream,"data: second\n\n"); }
                Write(stream,"0\r\n\r\n");continue;
              }
              if(path!="/echo") body=Encoding.UTF8.GetBytes("hello");
              Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nX-Synthetic-Auth: "+auth+"\r\nContent-Length: "+body.Length+"\r\n\r\n");stream.Write(body,0,body.Length);
            }
          }
        }
        """;
}
