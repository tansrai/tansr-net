using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalBorrowedClientTests
{
    [Fact]
    public void DefaultPreviewRefusesWithoutInvokingTheOwnersHandlerOrTokenProvider()
    {
        var tokens = 0;
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request is allowed."));
        using var http = new HttpClient(handler); var options = Options(); options.TokenProvider = _ => { tokens++; return Task.FromResult("owned-token"); };
        using var client = new TansrClient(options, http);
        Assert.Equal("unsupported_capability", Assert.Throws<TansrProtocolException>(() => TerminalConnection.ForClient(client)).Code);
        Assert.Equal(0, handler.Calls); Assert.Equal(0, tokens);
    }

    [Fact]
    public async Task BindingUsesTheExactOriginalAuthenticatedHandlerAndTerminalDisposalDoesNotDisposeTheOwner()
    {
        var adapter = new TerminalTestAdapter();
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal("owned-private-token", request.Headers.Authorization!.Parameter);
            Assert.Equal("127.0.0.1", request.RequestUri!.Host); Assert.Equal(34567, request.RequestUri.Port);
            if (request.RequestUri.AbsolutePath == "/v3/terminal/capabilities") return Json(await adapter.GetCapabilitiesAsync(ct));
            if (request.RequestUri.AbsolutePath == "/v3/terminal/bindings") return Json(await adapter.BindAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(ct)), ct));
            Assert.Equal("/v2/sessions", request.RequestUri.AbsolutePath); Assert.Equal(HttpMethod.Get, request.Method);
            return Json(JsonSerializer.SerializeToElement(new { sessions = Array.Empty<object>(), total = 0 }));
        });
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        using (var terminal = TerminalConnection.ForClient(client, true))
        {
            var binding = await terminal.BindAsync(Request(adapter)); Assert.Equal("borrowed-binding", binding.Value.GetProperty("requestId").GetString());
        }
        Assert.Equal(0, (await client.ListSessionsAsync()).GetProperty("total").GetInt32()); Assert.Equal(3, handler.Calls); Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDisposalCancelsPendingSseAndPreventsFurtherAttachmentOrOperations(bool executorNotifications)
    {
        var adapter = new TerminalTestAdapter(); using var stream = new PendingStream();
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v3/terminal/capabilities") return Json(await adapter.GetCapabilitiesAsync(ct));
            if (request.Method == HttpMethod.Post) return Json(await adapter.BindAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(ct)), ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };
        });
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http); using var terminal = TerminalConnection.ForClient(client, true);
        var binding = await terminal.BindAsync(Request(adapter));
        var observing = executorNotifications ? terminal.ObserveExecutorAsync(binding, null, (_, _) => Task.CompletedTask)
            : terminal.ObserveOutputAsync(binding, Reference(adapter), null, (_, _) => Task.CompletedTask);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); client.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.Disposed);
        Assert.Throws<ObjectDisposedException>(() => terminal.AttachBinding(binding));
        Assert.Throws<ObjectDisposedException>(() => terminal.CreateOutputSink(binding));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => terminal.DiscoverAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => terminal.GetOutputStatusAsync(binding, Reference(adapter)));
        Assert.Throws<ObjectDisposedException>(() => TerminalConnection.ForClient(client, true)); Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task OwnerDisposalStopsAnEmptyCapturePumpWithoutWaitingForAnotherAppendOrHttpRequest()
    {
        var adapter = new TerminalTestAdapter();
        using var handler = new Handler(async (request, ct) => request.Method == HttpMethod.Post
            ? Json(await adapter.BindAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(ct)), ct)) : Json(await adapter.GetCapabilitiesAsync(ct)));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http); using var terminal = TerminalConnection.ForClient(client, true);
        var binding = await terminal.BindAsync(Request(adapter)); using var sink = terminal.CreateOutputSink(binding);
        using var capture = await sink.OpenAsync(adapter.ExistingOperation, default);
        Assert.False(capture.Completion.IsCompleted); Assert.Equal(2, handler.Calls);
        client.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(capture.Append("stdout", "utf-8", Encoding.UTF8.GetBytes("late")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("user")]
    [InlineData("authorization")]
    public async Task TheBorrowedBindingRejectsEveryChangedScopeComponentBeforeIo(string field)
    {
        var adapter = new TerminalTestAdapter(); var scope = ExecutionFixture.Scope();
        using var handler = new Handler(async (request, ct) => request.Method == HttpMethod.Post
            ? Json(await adapter.BindAsync(WireJson.DecodeControl(await request.Content!.ReadAsByteArrayAsync(ct)), ct)) : Json(await adapter.GetCapabilitiesAsync(ct)));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(() => scope), http); using var terminal = TerminalConnection.ForClient(client, true);
        var binding = await terminal.BindAsync(Request(adapter)); scope = ExecutionFixture.Scope(user: field == "user" ? "other" : "user", application: field == "application" ? "other" : "app", revision: field == "authorization" ? "2" : "1");
        Assert.Equal("context_changed", Assert.Throws<WireProtocolException>(() => terminal.AttachBinding(binding)).Code);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<WireProtocolException>(() => terminal.GetOutputStatusAsync(binding, Reference(adapter)))).Code);
        Assert.Equal(2, handler.Calls);
    }

    private static TansrClientOptions Options(Func<JsonElement>? scope = null) => new()
    {
        BaseUri = new("http://127.0.0.1:34567/"),
        AllowInsecureLoopback = true,
        TokenProvider = _ => Task.FromResult("owned-private-token"),
        PrincipalProvider = () => "owned-principal",
        ExecutionScopeProvider = scope ?? (() => ExecutionFixture.Scope())
    };
    private static JsonElement Reference(TerminalTestAdapter adapter) => JsonSerializer.SerializeToElement(new
    { operationId = adapter.ExistingOperation.GetProperty("operationId").GetString(), requestDigest = adapter.ExistingOperation.GetProperty("digest").GetString() });
    private static JsonElement Request(TerminalTestAdapter adapter) => JsonSerializer.SerializeToElement(new
    {
        contract = "terminal-services-v1",
        requestId = "borrowed-binding",
        session = new { sessionContract = "sdk2-offload-v1", sessionId = "session-1" },
        executionBinding = adapter.ExistingOperation.GetProperty("binding"),
        required = new[] { "execution-stream-v1" },
        optional = Array.Empty<string>()
    });
    private static HttpResponseMessage Json(JsonElement value) => new(HttpStatusCode.OK)
    { Content = new StringContent(WireJson.CanonicalString(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public int Calls; public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return action(request, ct); }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
    private sealed class PendingStream : Stream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
