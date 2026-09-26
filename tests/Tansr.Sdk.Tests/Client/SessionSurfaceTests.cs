using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Tests.Client;

public sealed class SessionSurfaceTests
{
    private sealed record Request(string Method, string Path, byte[]? Body, string? ContentType);
    private sealed class Handler(Func<Request, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var item = new Request(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct), request.Content?.Headers.ContentType?.MediaType);
            Requests.Add(item); return respond(item);
        }
    }
    private const string Created = "{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":-1}";
    private const string Meta = "{\"sessionId\":\"s\",\"endUserId\":\"user\",\"status\":\"idle\",\"live\":true,\"lastSeq\":-1,\"createdAt\":\"2026-09-26T00:00:00Z\",\"lastActivityAt\":\"2026-09-26T00:00:00Z\",\"media\":{\"models\":[]},\"future\":true}";
    private static JsonElement Element(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Control(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new ByteArrayContent(WireJson.EncodeControl(Element(text), 1048576)) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } };
    private static TansrClientOptions Options() => new()
    {
        BaseUri = new("https://serve.test/"),
        TokenProvider = _ => Task.FromResult("fixture-token"),
        PrincipalProvider = () => "app/user",
        ExecutionScopeProvider = () => Element("{\"applicationScopeId\":\"app\",\"endUserId\":\"user\",\"authorizationRevision\":\"1\"}")
    };

    [Fact]
    public async Task CreateLabelsAndEndUserUseOnlyTheExistingContractFields()
    {
        using var handler = new Handler(_ => Json(Created)); using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        await client.CreateSessionAsync(new() { EndUserId = "user", Labels = new Dictionary<string, string> { ["campaign"] = "测试" } });
        var body = WireJson.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("user", body.GetProperty("endUser").GetProperty("id").GetString()); Assert.Equal("测试", body.GetProperty("labels").GetProperty("campaign").GetString());
    }

    [Theory]
    [InlineData("labels")]
    [InlineData("label-key")]
    [InlineData("label-value")]
    [InlineData("end-user")]
    public async Task InvalidCreateAttributionIsRejectedBeforeTheNetwork(string invalid)
    {
        var options = new CreateSessionOptions();
        if (invalid == "end-user") options.EndUserId = "user with space";
        else options.Labels = invalid == "labels" ? Enumerable.Range(0, 17).ToDictionary(x => x.ToString(), _ => "x")
            : new Dictionary<string, string> { [invalid == "label-key" ? new string('k', 65) : "key"] = invalid == "label-value" ? new string('v', 257) : "v" };
        using var handler = new Handler(_ => Json(Created)); using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateSessionAsync(options)); Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ResumeAndForkComposeCreateWithoutMutatingCallerOptions()
    {
        using var handler = new Handler(request => Json(request.Body is not null && WireJson.Parse(request.Body).TryGetProperty("fork", out _)
            ? "{\"sessionId\":\"child\",\"resumed\":false,\"lastSeq\":0}" : "{\"sessionId\":\"s\",\"resumed\":true,\"lastSeq\":0}"));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http); var options = new CreateSessionOptions { Model = "main" };
        var session = await client.ResumeSessionAsync("s", options); var fork = await session.ForkAsync("checkpoint", options);
        Assert.True(session.Resumed); Assert.Equal("child", fork.Id); Assert.Null(options.ResumeSessionId); Assert.Null(options.ForkSessionId);
        Assert.All(handler.Requests, request => Assert.Equal("/v2/sessions", request.Path));
        Assert.Equal("s", WireJson.Parse(handler.Requests[0].Body!).GetProperty("resume").GetProperty("sessionId").GetString());
        Assert.Equal("checkpoint", WireJson.Parse(handler.Requests[1].Body!).GetProperty("fork").GetProperty("checkpointId").GetString());
    }

    [Fact]
    public async Task TypedMetadataAndListPreserveAdditionsWithoutInventingConfigurationWrites()
    {
        using var handler = new Handler(request => Json(request.Path.StartsWith("/v2/sessions?", StringComparison.Ordinal) ? "{\"sessions\":[" + Meta + "],\"total\":1}" : request.Method == "POST" ? Created : Meta));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        var metadata = await session.ReadMetadataAsync(); var page = await client.GetSessionsAsync(1, 0);
        Assert.Equal(SessionStatus.Idle, metadata.Status); Assert.Equal(-1, metadata.LastSequence); Assert.True(metadata.Raw.GetProperty("future").GetBoolean());
        Assert.NotNull(metadata.Media); Assert.Equal(1, page.Total); Assert.Equal("s", Assert.Single(page.Sessions).Id);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "PATCH");
    }

    [Fact]
    public async Task ExportImportUseCheckpointBytesAndNeverImplicitlyRestoreOrDelete()
    {
        byte[] bytes = [0, 128, 255, 3];
        using var handler = new Handler(request => request.Path.EndsWith("/export", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } } }
            : Json(request.Path == "/v2/sessions" ? Created : "{\"sessionId\":\"s\",\"checkpointId\":\"cp\"}"));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        var exported = await session.ExportAsync("saved"); await session.ImportAsync(exported.Bytes, "copy");
        Assert.Equal("cp", exported.CheckpointId); Assert.Equal(bytes, exported.Bytes);
        var mutableCopy = exported.Bytes; mutableCopy[0] = 99; Assert.Equal(0, exported.Bytes[0]);
        Assert.Equal(bytes, handler.Requests[^1].Body); Assert.Equal("application/octet-stream", handler.Requests[^1].ContentType);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "DELETE" || r.Path.Contains("/restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextBlockInsertionPreservesTargetAndDurableAck()
    {
        using var handler = new Handler(request => Json(request.Path == "/v2/sessions" ? Created : "{\"outcome\":\"accepted\"}"));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        await session.SubmitInputBlocksAsync("input", new("epoch", "turn"), ["one", "two"], durable: true);
        var body = WireJson.Parse(handler.Requests[^1].Body!);
        Assert.Equal("durable", body.GetProperty("ack").GetString()); Assert.Equal(2, body.GetProperty("content").GetProperty("blocks").GetArrayLength());
        Assert.Equal("turn", body.GetProperty("target").GetProperty("turnId").GetString());
    }

    [Theory]
    [InlineData(404, "input_not_found", "rejected", true)]
    [InlineData(409, "input_conflict", "rejected", false)]
    [InlineData(429, "injection_limit", "rejected", false)]
    [InlineData(409, "input_closed", "closed", false)]
    public async Task InputRefusalKeepsItsFrozenCodeWithoutBroadeningOtherErrorEnvelopes(int status, string code, string outcome, bool lookup)
    {
        using var handler = new Handler(request => request.Path == "/v2/sessions" ? Json(Created) :
            Json("{\"outcome\":\"" + outcome + "\",\"code\":\"" + code + "\"}", (HttpStatusCode)status));
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => lookup ? session.GetInputStatusAsync("input", new("epoch", "turn")) : session.SubmitInputAsync("input", new("epoch", "turn"), "text"));
        Assert.Equal(code, error.Code); Assert.Equal(outcome, error.InputOutcome);
        var unrelated = await Assert.ThrowsAsync<TansrHttpException>(() => session.SendAsync("not an input receipt"));
        Assert.Equal("invalid_response", unrelated.Code); Assert.Null(unrelated.InputOutcome);
    }

    [Fact]
    public async Task EventFilteringUsesFrozenQueryWithoutFilteringControlLocally()
    {
        using var handler = new Handler(request => request.Path == "/v2/sessions" ? Json(Created) :
            new(HttpStatusCode.OK) { Content = new StringContent("id: 0\ndata: {\"sessionId\":\"s\",\"seq\":0,\"type\":\"session.ended\"}\n\n", Encoding.UTF8, "text/event-stream") });
        using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        await session.ObserveAsync((_, _) => Task.CompletedTask, new EventStreamOptions { Exclude = ["msg", "cost"] });
        Assert.Equal("/v2/sessions/s/events?exclude=msg%2Ccost", handler.Requests[^1].Path);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ObserveAsync((_, _) => Task.CompletedTask, new EventStreamOptions { Exclude = ["msg.text"] }));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ControlResponseLimitCanExpandWithoutExpandingRequestsOrErrors()
    {
        var large = "{\"value\":\"" + new string('x', 300000) + "\"}";
        using var handler = new Handler(_ => Control(large, HttpStatusCode.Created)); using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        var received = await client.SendControlAsync(HttpMethod.Get, "/v3/sdk2/test", null, default, responseMaximum: 1048576, expectedStatus: 201);
        Assert.Equal(300000, received.GetProperty("value").GetString()!.Length);
        Assert.Equal("response_too_large", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.SendControlAsync(HttpMethod.Get, "/v3/sdk2/test", null, default))).Code);
        await Assert.ThrowsAsync<WireProtocolException>(() => client.SendControlAsync(HttpMethod.Post, "/v3/sdk2/test", Element(large), default, responseMaximum: 1048576));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ControlSuccessStatusMustMatchTheEndpointContract()
    {
        using var handler = new Handler(_ => Control("{}")); using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.SendControlAsync(HttpMethod.Post, "/v3/sdk2/test", Element("{}"), default, expectedStatus: 201))).Code);
    }

    [Theory]
    [InlineData(503, true)]
    [InlineData(500, false)]
    public async Task ControlErrorPreservesVerifiedRecoveryFactsButNoRawMessage(int status, bool valid)
    {
        const string error = "{\"protocol\":\"sdk2-ext-v1\",\"requestId\":\"private-request-id\",\"code\":\"source_unavailable\",\"status\":503,\"message\":\"secret internal location\",\"retryAction\":\"query-status\",\"retryAfterMs\":500}";
        using var handler = new Handler(_ => Control(error, (HttpStatusCode)status)); using var http = new HttpClient(handler); using var client = new TansrClient(Options(), http);
        if (!valid) await Assert.ThrowsAsync<TansrProtocolException>(() => client.SendControlAsync(HttpMethod.Get, "/v3/sdk2/test", null, default));
        else
        {
            var failure = await Assert.ThrowsAsync<TansrHttpException>(() => client.SendControlAsync(HttpMethod.Get, "/v3/sdk2/test", null, default));
            Assert.Equal("query-status", failure.RetryAction); Assert.Equal(500, failure.RetryAfterMs); Assert.Equal("source_unavailable", failure.Code);
            Assert.DoesNotContain("secret", failure.ToString()); Assert.DoesNotContain("private-request-id", failure.ToString());
        }
        Assert.Single(handler.Requests);
    }
}
