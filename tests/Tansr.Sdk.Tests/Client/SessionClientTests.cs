using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Tests.Client;

public sealed class SessionClientTests
{
    private sealed record Request(string Method, string Path, string? Body, string? Authorization, string? LastEventId);
    private sealed class Handler(Func<Request, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var value = new Request(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Last-Event-ID", out var ids) ? ids.Single() : null);
            lock (Requests) Requests.Add(value);
            return respond(value);
        }
    }
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private const string Created = "{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":0}";
    private static TansrClientOptions Options() => new()
    {
        BaseUri = new Uri("https://serve.test/"),
        TokenProvider = _ => Task.FromResult("private-token"),
        ReconnectDelay = TimeSpan.Zero,
        MaxReconnectAttempts = 1
    };
    private static JsonElement Element(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(9007199254740991L)]
    public async Task SessionMetadataAllowsEmptyLogSentinelAndSafeSequences(long lastSequence)
    {
        using var handler = new Handler(_ => Json("{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":" + lastSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        Assert.Equal(lastSequence, (await client.CreateSessionAsync(new())).LastSequence);
        Assert.Equal(lastSequence, (await client.GetSessionAsync("s")).LastSequence);
    }

    [Theory]
    [InlineData("-2")]
    [InlineData("-1.5")]
    [InlineData("9007199254740992")]
    [InlineData("\"-1\"")]
    public async Task SessionMetadataRejectsInvalidSequencesBeyondTheEmptyLogSentinel(string lastSequence)
    {
        using var handler = new Handler(_ => Json("{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":" + lastSequence + "}"));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.CreateSessionAsync(new()))).Code);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.GetSessionAsync("s"))).Code);
    }

    [Fact]
    public async Task FrozenRoutesPreserveBodiesWithoutUnexpectedDiscovery()
    {
        using var handler = new Handler(r => Json(r.Path == "/v2/sessions" ? Created : "{\"status\":\"accepted\"}"));
        using var http = new HttpClient(handler);
        using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new CreateSessionOptions { Model = "main", MaxTokens = 1000 });
        await session.SendAsync("第一行\n第二行😀");
        await session.SendBlocksAsync([MessageBlock.Text("图"), MessageBlock.Image("image/png", "AA==")]);
        await session.SubmitInputAsync("input-1", new("epoch-1", "turn-1"), "补充");
        await session.PermissionAsync("request-1", "digest", true);
        await session.AnswerAsync("question-1", [new("q1", ["a"], "自由文本")]);
        await session.SubmitToolResultAsync("call-1", Element("{\"status\":\"ok\",\"content\":[{\"t\":\"text\",\"text\":\"done\"}]}"));
        await session.CompactAsync(new CompactOptions { Instructions = "保留事实", CheckpointLabel = "before" });
        await session.CheckpointAsync("label");
        await session.RestoreCheckpointAsync("cp-1", false);
        await session.DeleteCheckpointAsync("cp-1");
        await session.SetCwdAsync("C:\\workspace");
        await session.TranscribeAsync(new() { Audio = "data:audio/wav;base64,AA==" });
        await session.SpeakAsync(new() { Input = "你好", Format = "wav" });
        await session.CancelAsync();
        await session.CloseAsync();
        Assert.Equal(16, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer private-token", r.Authorization));
        Assert.Equal("第一行\n第二行😀", Element(handler.Requests[1].Body!).GetProperty("prompt").GetString());
        Assert.Equal("memory", Element(handler.Requests[3].Body!).GetProperty("ack").GetString());
        Assert.Equal("/v2/sessions/s/permission/request-1", handler.Requests[4].Path);
        Assert.Equal("DELETE", handler.Requests[^1].Method);
    }

    [Fact]
    public async Task SideEffectsAreNeverRetriedAndErrorMessageCannotExposeSecrets()
    {
        using var handler = new Handler(r => r.Path == "/v2/sessions" ? Json(Created) :
            Json("{\"error\":{\"code\":\"upstream_unavailable\",\"message\":\"private-token C:\\\\secret\",\"detail\":{\"reason\":\"busy\"}}}", HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => session.SendAsync("pay"));
        Assert.Equal(503, error.StatusCode); Assert.Equal("upstream_unavailable", error.Code);
        Assert.DoesNotContain("private-token", error.ToString()); Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://example.com/", true)]
    [InlineData("http://localhost/", false)]
    [InlineData("https://user:pass@example.com/", false)]
    [InlineData("https://example.com/path", false)]
    [InlineData("https://example.com/?token=x", false)]
    public void UnsafeOriginsAreRejected(string uri, bool allow)
    {
        var options = Options(); options.BaseUri = new(uri); options.AllowInsecureLoopback = allow;
        Assert.Throws<ArgumentException>(() => new TansrClient(options));
    }

    [Fact]
    public async Task RedirectIsRejectedBeforeReadingBody()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new("https://other.test/") } });
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        Assert.Equal("redirect_rejected", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.CreateSessionAsync(new()))).Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RenewalCannotChangeTheBoundPrincipal()
    {
        string principal = "app-a/user-a";
        var options = Options(); options.PrincipalProvider = () => principal;
        options.TokenProvider = _ => { principal = "app-a/user-b"; return Task.FromResult("next-token"); };
        using var handler = new Handler(_ => Json(Created)); using var http = new HttpClient(handler);
        using var client = new TansrClient(options, http);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.CreateSessionAsync(new()))).Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ExecutionScopeCannotBeConfiguredWithoutTrustedPrincipal()
    {
        var options = Options();
        options.ExecutionScopeProvider = () => Element("{\"applicationScopeId\":\"a\",\"endUserId\":\"u\"}");
        Assert.Throws<ArgumentException>(() => new TansrClient(options));
        options.PrincipalProvider = () => "a/u";
        using var client = new TansrClient(options);
    }

    [Fact]
    public async Task ExplicitSdk2DiscoversAndNeverFallsBack()
    {
        var options = Options(); options.SessionContract = SessionContract.Sdk2OffloadV1; options.PrincipalProvider = () => "a/u";
        using var handler = new Handler(r => r.Path.StartsWith("/v3/sdk2/session-capabilities", StringComparison.Ordinal)
            ? Json("{\"contracts\":[{\"availability\":\"source-required\",\"contract\":\"sdk2-offload-v1\"}],\"protocol\":\"sdk2-ext-v1\"}")
            : Json("{\"availability\":\"source-required\",\"contract\":\"sdk2-offload-v1\",\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":0}"));
        using var http = new HttpClient(handler); using var client = new TansrClient(options, http);
        var session = await client.CreateSessionAsync(new() { RequestId = "creation-1" });
        Assert.Equal("s", session.Id); Assert.Equal("/v3/sdk2/sessions", handler.Requests[1].Path);
        Assert.Equal("creation-1", Element(handler.Requests[1].Body!).GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task MissingSdk2CapabilityHasNoCreateSideEffect()
    {
        var options = Options(); options.SessionContract = SessionContract.Sdk2OffloadV1; options.PrincipalProvider = () => "a/u";
        using var handler = new Handler(_ => Json("{\"contracts\":[{\"availability\":\"legacy-complete\",\"contract\":\"sdk1\"}],\"protocol\":\"sdk2-ext-v1\"}"));
        using var http = new HttpClient(handler); using var client = new TansrClient(options, http);
        Assert.Equal("unsupported_capability", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.CreateSessionAsync(new() { RequestId = "creation-1" }))).Code);
        Assert.Single(handler.Requests); Assert.Equal("GET", handler.Requests[0].Method);
    }

    [Fact]
    public async Task ResponsesAreBoundedBeforeJsonParsing()
    {
        var options = Options(); options.MaxResponseBytes = 32;
        using var handler = new Handler(_ => Json(new string('x', 33))); using var http = new HttpClient(handler);
        using var client = new TansrClient(options, http);
        Assert.Equal("response_too_large", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.CreateSessionAsync(new()))).Code);
    }

    [Fact]
    public async Task BinaryCheckpointBytesAreNotJsonReencoded()
    {
        var binary = new byte[] { 0, 128, 255, 3 };
        using var handler = new Handler(r => r.Path == "/v2/sessions" ? Json(Created) : r.Path.EndsWith("/export", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(binary) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } } }
            : Json("{\"checkpointId\":\"cp\"}"));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        Assert.Equal(binary, await session.ExportCheckpointAsync("cp"));
        await session.ImportCheckpointAsync(binary, "new label");
        Assert.EndsWith("?label=new%20label", handler.Requests[^1].Path);
    }

    [Fact]
    public async Task InvalidUnicodeInputIsRejectedBeforeNetwork()
    {
        using var handler = new Handler(_ => Json(Created)); using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        Assert.Throws<ArgumentException>(() => { _ = session.SendAsync("broken\ud800"); });
        Assert.Single(handler.Requests);
    }
}
