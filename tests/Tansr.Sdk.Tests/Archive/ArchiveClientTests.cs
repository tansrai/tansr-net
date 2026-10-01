using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Archive;

public sealed class ArchiveClientTests
{
    [Fact]
    public async Task OriginalArtifactEnvelopeCanExceedControlBytesWithoutChangingChunkLimit()
    {
        var fixture = new ArchiveFlowFixture(); byte[] body = Enumerable.Range(0, 262144).Select(i => (byte)i).ToArray(); string hash = WireJson.Sha256(body);
        var chunk = ArchiveFlowFixture.Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", artifactId = "artifact", sourceId = "source", generations = fixture.Target.GetProperty("generations"), offset = 0, bytes = body.Length, totalBytes = body.Length, sha256 = hash, chunkSha256 = hash, base64 = Convert.ToBase64String(body) });
        using var http = UnifiedStamp.Client(new Handler(request =>
        {
            Assert.Equal("/api/archive/bindings/binding/archive/artifacts/artifact", request.RequestUri!.AbsolutePath); Assert.Contains("maxBytes=262144", request.RequestUri.Query);
            return Response(chunk);
        }));
        using var tansr = Client(http, fixture); var client = new ArchiveClient(tansr);
        var read = ArchiveFlowFixture.Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", generations = fixture.Target.GetProperty("generations"), artifactId = "artifact", offset = 0, maxBytes = 262144 });
        var output = await client.ReadArtifactAsync(read); Assert.Equal(body, WireJson.DecodeBase64(output.GetProperty("base64").GetString()!));
    }
    [Fact]
    public async Task BindingNegotiationConstrainsLaterReadsAndScopeChangeDropsOldNegotiation()
    {
        var fixture = new ArchiveFlowFixture(); var limits = ArchiveFlowFixture.Set(fixture.Limits, "chunkBytes", 1024); var binding = ArchiveFlowFixture.Set(fixture.Binding, "limits", limits); int sends = 0;
        using var http = UnifiedStamp.Client(new Handler(_ => { sends++; return Response(binding); })); using var tansr = Client(http, fixture); var client = new ArchiveClient(tansr);
        await client.GetBindingAsync("binding"); Assert.Equal(1024, client.GetEffectiveLimits("binding").GetProperty("chunkBytes").GetInt32());
        var read = ArchiveFlowFixture.Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", generations = fixture.Target.GetProperty("generations"), artifactId = "artifact", offset = 0, maxBytes = 2048 });
        await Assert.ThrowsAsync<TansrProtocolException>(() => client.ReadArtifactAsync(read)); Assert.Equal(1, sends);
        fixture.User = "other"; Assert.Equal(262144, client.GetEffectiveLimits("binding").GetProperty("chunkBytes").GetInt32());
    }
    [Fact]
    public async Task MaterialResponseRequiresOriginal202AndReceiptRecordSet()
    {
        var fixture = new ArchiveFlowFixture(); var response = ArchiveFlowFixture.Element(new { protocol = "sdk2-ext-v1", request = fixture.RequestIdentity, bindingId = "binding", materialRequestId = "material", target = fixture.Target, sourceId = "source", sourceGeneration = "source-generation", results = new[] { new { recordId = "record", digest = fixture.Record.GetProperty("recordDigest").GetString(), payload = new { uploadId = "upload" }, attachments = Array.Empty<object>() } } });
        HttpStatusCode status = HttpStatusCode.OK; using var http = UnifiedStamp.Client(new Handler(request => { Assert.Equal(HttpMethod.Post, request.Method); return Response(fixture.MaterialReceipt(), status); })); using var tansr = Client(http, fixture); var client = new ArchiveClient(tansr);
        await Assert.ThrowsAsync<TansrProtocolException>(() => client.RespondMaterialsAsync(response)); status = HttpStatusCode.Accepted;
        Assert.Equal("received", (await client.RespondMaterialsAsync(response)).GetProperty("state").GetString());
    }
    [Fact]
    public async Task ReturnedArchiveRecordDigestIsVerifiedBeforeExposure()
    {
        var fixture = new ArchiveFlowFixture(); var bad = ArchiveFlowFixture.Set(fixture.Record, "recordDigest", new string('f', 64)); var page = ArchiveFlowFixture.Set(fixture.Page, "records", new[] { bad });
        using var http = UnifiedStamp.Client(new Handler(_ => Response(page))); using var tansr = Client(http, fixture); var client = new ArchiveClient(tansr);
        var read = ArchiveFlowFixture.Element(new { protocol = "sdk2-ext-v1", bindingId = "binding", generations = fixture.Target.GetProperty("generations"), afterSequence = (string?)null, limit = 128, maxBytes = 1048576 });
        await Assert.ThrowsAsync<TansrProtocolException>(() => client.ReadRecordsAsync(read));
    }
    private static TansrClient Client(HttpClient http, ArchiveFlowFixture fixture) => new(new TansrClientOptions { BaseUri = new Uri("https://serve.invalid"), TokenProvider = _ => Task.FromResult("synthetic-token"), PrincipalProvider = () => "app/" + fixture.User, ExecutionScopeProvider = () => fixture.Scope }, http);
    private static HttpResponseMessage Response(JsonElement value, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(WireJson.EncodeControl(value, 1048576)) }; response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request)); }
}
