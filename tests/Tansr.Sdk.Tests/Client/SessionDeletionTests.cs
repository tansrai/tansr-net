using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Client;

public sealed class SessionDeletionTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Mutations { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Delete) Mutations++;
            return Task.FromResult(respond(request));
        }
    }
    private static HttpResponseMessage Created() => new(HttpStatusCode.Created)
    { Content = new StringContent("{\"sessionId\":\"original\",\"resumed\":false,\"lastSeq\":-1}", Encoding.UTF8, "application/json") };
    private static TansrClientOptions Options() => new() { BaseUri = new("https://serve.test/"), TokenProvider = _ => Task.FromResult("test") };

    [Fact]
    public async Task CheckpointDeletionAcceptsOnlyTheOriginalEmpty204WithoutInventingAReceipt()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : new(HttpStatusCode.NoContent));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = await client.CreateSessionAsync(new());
        Assert.Equal(JsonValueKind.Null, (await session.DeleteCheckpointAsync("checkpoint")).ValueKind);
        Assert.Equal(1, handler.Mutations);
    }

    [Theory]
    [InlineData(200, "{}")]
    [InlineData(202, "{\"accepted\":true}")]
    [InlineData(204, "x")]
    public async Task OtherSuccessfulStatusesOrBodiesCannotBecomeADeletionSuccess(int status, string body)
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : new((HttpStatusCode)status) { Content = new StringContent(body) });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<TansrProtocolException>(() => session.DeleteCheckpointAsync("checkpoint"))).Code);
        Assert.Equal(1, handler.Mutations);
    }

    [Fact]
    public async Task CheckpointDeletionPreservesStructuredFailureAndNeverRetriesAnUnknownOutcome()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : throw new HttpRequestException("lost after delete"));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => session.DeleteCheckpointAsync("checkpoint"))).Code);
        Assert.Equal(1, handler.Mutations);
    }

    [Fact]
    public async Task RepeatedDeletionRetainsTheOriginalNotFoundError()
    {
        using var handler = new Handler(r => r.Method == HttpMethod.Post ? Created() : new(HttpStatusCode.NotFound)
        { Content = new StringContent("{\"error\":{\"code\":\"checkpoint_not_found\",\"message\":\"private\"}}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http); var session = await client.CreateSessionAsync(new());
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => session.DeleteCheckpointAsync("checkpoint"));
        Assert.Equal(404, error.StatusCode); Assert.Equal("checkpoint_not_found", error.Code); Assert.DoesNotContain("private", error.ToString()); Assert.Equal(1, handler.Mutations);
    }
}
