using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Tests.Api;
using static Tansr.Sdk.Tests.Archive.ArchiveFlowFixture;
using static Tansr.Sdk.Tests.Archive.ArchiveRecoveryFixture;

namespace Tansr.Sdk.Tests.Archive;

public sealed class ArchiveRecoveryClientTests
{
    [Fact]
    public async Task FrozenSourceGoldenUsesExactCanonicalBodyAndOriginalSemanticReceipt()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Terminal", "Fixtures", "sdk2-archive-recovery-v1.golden.json")));
        var data = golden.RootElement; int sends = 0;
        using var http = UnifiedStamp.Client(new Handler(async request =>
        {
            sends++; Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/api/archive/bindings/binding/archive/ack-rebases", request.RequestUri!.AbsolutePath); Assert.Empty(request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(data.GetProperty("wire").GetProperty("request").GetProperty("canonical").GetString(), await request.Content.ReadAsStringAsync());
            return Response(WireJson.EncodeControl(data.GetProperty("response"), 528384));
        }));
        using var tansr = Client(http, () => data.GetProperty("scope")); var client = new ArchiveClient(tansr);
        var receipt = await client.RebaseAckAsync(data.GetProperty("request")); Assert.Equal(Text(data.GetProperty("response")), Text(receipt)); Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData("noncanonical")]
    [InlineData("status")]
    [InlineData("duplicate")]
    [InlineData("changed_next")]
    [InlineData("bad_digest")]
    [InlineData("scope")]
    public async Task HttpRecoveryRejectsChangedOrAmbiguousEvidence(string mode)
    {
        var f = new ArchiveRecoveryFixture(); var intent = f.Intent(f.Request); int sends = 0;
        using var http = UnifiedStamp.Client(new Handler(_ =>
        {
            sends++; var value = f.Result(intent);
            if (mode == "changed_next") value = Set(value, "next", Set(value.GetProperty("next"), "sourceGeneration", "other"));
            if (mode == "bad_digest") value = Set(value, "receipt", Set(value.GetProperty("receipt"), "semanticDigest", new string('f', 64)));
            byte[] bytes = WireJson.EncodeControl(value, 528384);
            if (mode == "noncanonical") bytes = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(bytes));
            if (mode == "duplicate") bytes = Encoding.UTF8.GetBytes("{\"bindingId\":\"binding\"," + Encoding.UTF8.GetString(bytes).Substring(1));
            if (mode == "scope") f.Data.User = "another-user";
            return Task.FromResult(Response(bytes, mode == "status" ? HttpStatusCode.Accepted : HttpStatusCode.OK));
        }));
        using var tansr = Client(http, () => f.Data.Scope); var client = new ArchiveClient(tansr);
        Assert.NotNull(await Record.ExceptionAsync(() => client.RebaseAckAsync(intent))); Assert.Equal(1, sends);
    }

    [Fact]
    public async Task NegotiatedControlCapAppliesToNestedOriginalAckBeforeAnyHttp()
    {
        var f = new ArchiveRecoveryFixture(); var intent = LargeIntent(f); int length = WireJson.EncodeControl(intent.GetProperty("previous")).Length; int sends = 0;
        Assert.True(length > 1024);
        using var http = UnifiedStamp.Client(new Handler(_ => { sends++; return Task.FromResult(Response(WireJson.EncodeControl(f.Result(intent)))); }));
        using var tansr = Client(http, () => f.Data.Scope); var client = new ArchiveClient(tansr, Set(f.Data.Limits, "controlBytes", length - 1));
        await Assert.ThrowsAsync<WireProtocolException>(() => client.RebaseAckAsync(intent)); Assert.Equal(0, sends);
    }

    [Fact]
    public async Task IndependentResponseEnvelopeAllowsTwoAcksWhileEachRespectsControlLimit()
    {
        var f = new ArchiveRecoveryFixture(); var intent = LargeIntent(f); var output = f.Result(intent);
        int cap = Math.Max(WireJson.EncodeControl(intent.GetProperty("previous")).Length, WireJson.EncodeControl(output.GetProperty("next")).Length);
        Assert.True(WireJson.EncodeControl(output).Length > cap);
        using var http = UnifiedStamp.Client(new Handler(_ => Task.FromResult(Response(WireJson.EncodeControl(output))))); using var tansr = Client(http, () => f.Data.Scope);
        var client = new ArchiveClient(tansr, Set(f.Data.Limits, "controlBytes", cap)); Assert.Equal(Text(output), Text(await client.RebaseAckAsync(intent)));
    }

    [Theory]
    [InlineData("same_key")]
    [InlineData("different_epoch")]
    [InlineData("cross_binding")]
    public async Task InvalidRecoveryIntentIsRejectedBeforeHttp(string mode)
    {
        var f = new ArchiveRecoveryFixture(); var intent = f.Intent(f.Request); int sends = 0;
        intent = mode switch { "same_key" => Set(intent, "request", f.Data.RequestIdentity), "different_epoch" => Set(intent, "request", Set(f.Request, "operationEpoch", "new-epoch")), _ => Set(intent, "bindingId", "other-binding") };
        using var http = UnifiedStamp.Client(new Handler(_ => { sends++; throw new InvalidOperationException(); })); using var tansr = Client(http, () => f.Data.Scope);
        await Assert.ThrowsAsync<TansrProtocolException>(() => new ArchiveClient(tansr).RebaseAckAsync(intent)); Assert.Equal(0, sends);
    }

    [Fact]
    public async Task OriginalErrorEnvelopeRemainsBoundedAndExposesOnlyTheRetryFact()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Terminal", "Fixtures", "sdk2-archive-recovery-v1.golden.json")));
        var data = golden.RootElement; var entry = data.GetProperty("errors").EnumerateArray().First(x => x.GetProperty("body").GetProperty("code").GetString() == "epoch_unavailable");
        using var http = UnifiedStamp.Client(new Handler(_ => Task.FromResult(Response(WireJson.EncodeControl(entry.GetProperty("body")), (HttpStatusCode)503))));
        using var tansr = Client(http, () => data.GetProperty("scope")); var error = await Assert.ThrowsAsync<TansrHttpException>(() => new ArchiveClient(tansr).RebaseAckAsync(data.GetProperty("request")));
        Assert.Equal("epoch_unavailable", error.Code); Assert.Equal("same-request", error.RetryAction); Assert.Equal(503, error.StatusCode);
    }

    private static TansrClient Client(HttpClient http, Func<JsonElement> scope) => new(new TansrClientOptions { BaseUri = new Uri("https://serve.invalid"), TokenProvider = _ => Task.FromResult("synthetic-token"), PrincipalProvider = () => "synthetic-principal", ExecutionScopeProvider = scope }, http);
    private static JsonElement LargeIntent(ArchiveRecoveryFixture fixture)
    {
        var intent = fixture.Intent(fixture.Request);
        var payloads = Enumerable.Range(0, 48).Select(i => new { artifactId = "artifact-" + i, sha256 = new string('a', 64), state = "durably-stored" }).ToArray();
        return Set(intent, "previous", Set(intent.GetProperty("previous"), "payloads", payloads));
    }
    private static HttpResponseMessage Response(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK)
    { var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) }; response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); return response; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request); }
}
