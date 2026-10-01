using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Client;

public sealed class ApplicationPromptTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            Requests.Add(request.RequestUri!.PathAndQuery); return Task.FromResult(reply(request));
        }
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static string Metadata(string? prompt, bool sdk2 = false, string live = "true", string id = "session-1") =>
        "{\"sessionId\":\"" + id + "\",\"endUserId\":\"user\",\"status\":\"idle\",\"lastSeq\":-1," +
        "\"createdAt\":\"2026-09-26T00:00:00Z\",\"lastActivityAt\":\"2026-09-26T00:00:00Z\"" +
        (live.Length == 0 ? "" : ",\"live\":" + live) + (prompt is null ? "" : ",\"applicationPrompt\":" + prompt) +
        (sdk2 ? ",\"contract\":\"sdk2-offload-v1\",\"availability\":\"source-required\"" : "") + "}";
    private static TansrClientOptions Options(bool sdk2 = false) => new()
    {
        BaseUri = new Uri("https://serve.test/"),
        TokenProvider = _ => Task.FromResult("fixture-token"),
        PrincipalProvider = () => "app/user",
        SessionContract = sdk2 ? SessionContract.Sdk2OffloadV1 : SessionContract.Sdk1
    };
    private static HttpResponseMessage Capabilities()
    {
        using var document = JsonDocument.Parse("{\"protocol\":\"sdk2-ext-v1\",\"contracts\":[{\"contract\":\"sdk1\",\"availability\":\"legacy-complete\"},{\"contract\":\"sdk2-offload-v1\",\"availability\":\"source-required\"}]}");
        return Json(WireJson.CanonicalString(document.RootElement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSelectorUsesOriginalFamilyWhileDefaultMetadataRequestsRemainUnchanged(bool sdk2)
    {
        const string prompt = "{\"policy\":\"prepend\",\"source\":\"platform+sdk\"}";
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/capabilities/sessions"
            ? Capabilities() : Json(Metadata(request.RequestUri.Query.Length == 0 ? null : prompt, sdk2)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(sdk2), http);
        var session = new AgentSession(client, "session-1", -1, false);
        var raw = await session.GetMetadataAsync(default);
        var metadata = await session.ReadMetadataAsync(default);
        var observed = await session.ReadApplicationPromptAsync(default);
        Assert.False(raw.TryGetProperty("applicationPrompt", out _)); Assert.False(metadata.ApplicationPrompt.IsKnown);
        Assert.True(observed.IsKnown); Assert.Equal(ApplicationPromptSource.PlatformAndSdk, observed.Source);
        // UAPI-01: one /api entry for both families (sdk2 is discovered through /api/capabilities/sessions first).
        Assert.Equal(new[] { "/api/sessions/session-1", "/api/sessions/session-1", "/api/sessions/session-1?include=applicationPrompt" },
            handler.Requests.Where(path => !path.Contains("/capabilities/sessions", StringComparison.Ordinal)));
        Assert.Equal(sdk2, handler.Requests.Any(path => path.Contains("/capabilities/sessions", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, path => path.Contains("/meta", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("fallback", "none", ApplicationPromptPolicy.Fallback, ApplicationPromptSource.None)]
    [InlineData("fallback", "platform", ApplicationPromptPolicy.Fallback, ApplicationPromptSource.Platform)]
    [InlineData("fallback", "sdk", ApplicationPromptPolicy.Fallback, ApplicationPromptSource.Sdk)]
    [InlineData("prepend", "none", ApplicationPromptPolicy.Prepend, ApplicationPromptSource.None)]
    [InlineData("prepend", "platform", ApplicationPromptPolicy.Prepend, ApplicationPromptSource.Platform)]
    [InlineData("prepend", "sdk", ApplicationPromptPolicy.Prepend, ApplicationPromptSource.Sdk)]
    [InlineData("prepend", "platform+sdk", ApplicationPromptPolicy.Prepend, ApplicationPromptSource.PlatformAndSdk)]
    public async Task ValidPolicyAndSourceAreProjectedWithoutInferringMissingPromptContent(string policy, string source,
        ApplicationPromptPolicy expectedPolicy, ApplicationPromptSource expectedSource)
    {
        var prompt = "{\"policy\":\"" + policy + "\",\"source\":\"" + source + "\"}";
        using var handler = new Handler(_ => Json(Metadata(prompt)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = new AgentSession(client, "session-1", -1, false);
        var observed = await session.ReadApplicationPromptAsync();
        Assert.True(observed.IsKnown); Assert.Equal(expectedPolicy, observed.Policy); Assert.Equal(expectedSource, observed.Source);
        using var document = JsonDocument.Parse(Metadata(prompt));
        var metadata = new SessionMetadata(document.RootElement);
        Assert.Equal(expectedPolicy, metadata.ApplicationPrompt.Policy); Assert.Equal(expectedSource, metadata.ApplicationPrompt.Source);
        Assert.Equal(source, metadata.Raw.GetProperty("applicationPrompt").GetProperty("source").GetString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("\"synthetic-body\"")]
    [InlineData("{}")]
    [InlineData("{\"policy\":\"fallback\"}")]
    [InlineData("{\"policy\":\"new-policy\",\"source\":\"platform\"}")]
    [InlineData("{\"policy\":\"fallback\",\"source\":\"new-source\"}")]
    [InlineData("{\"policy\":null,\"source\":\"platform\"}")]
    [InlineData("{\"policy\":\"prepend\",\"source\":42}")]
    [InlineData("{\"policy\":\"fallback\",\"source\":\"platform+sdk\"}")]
    [InlineData("{\"policy\":\"prepend\",\"source\":\"platform\",\"text\":\"synthetic-body\"}")]
    public async Task AbsentNullMalformedUnknownOrBodyBearingAdditionIsUnknownInsteadOfNone(string? prompt)
    {
        using var handler = new Handler(_ => Json(Metadata(prompt)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var observed = await new AgentSession(client, "session-1", -1, false).ReadApplicationPromptAsync();
        Assert.False(observed.IsKnown); Assert.Equal(ApplicationPromptPolicy.Unknown, observed.Policy); Assert.Equal(ApplicationPromptSource.Unknown, observed.Source);
        Assert.Equal("Unknown", observed.ToString()); Assert.DoesNotContain("synthetic-body", observed.ToString());
        using var document = JsonDocument.Parse(Metadata(prompt)); var metadata = new SessionMetadata(document.RootElement);
        Assert.False(metadata.ApplicationPrompt.IsKnown);
        Assert.Equal(prompt is not null, observed.Raw.HasValue);
        if (prompt is not null) Assert.Equal(document.RootElement.GetProperty("applicationPrompt").GetRawText(), observed.Raw!.Value.GetRawText());
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("")]
    public async Task SourceRequiresAnExplicitlyLiveSession(string live)
    {
        using var handler = new Handler(_ => Json(Metadata("{\"policy\":\"prepend\",\"source\":\"platform+sdk\"}", live: live)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var observed = await new AgentSession(client, "session-1", -1, false).ReadApplicationPromptAsync();
        Assert.False(observed.IsKnown); Assert.Equal(ApplicationPromptSource.Unknown, observed.Source);
        Assert.Equal("platform+sdk", observed.Raw!.Value.GetProperty("source").GetString());
    }

    [Fact]
    public void InactiveStoredMetadataDoesNotTurnRetainedSourceIntoAClaimAboutTheLiveSession()
    {
        using var document = JsonDocument.Parse(Metadata("{\"policy\":\"fallback\",\"source\":\"sdk\"}", live: "false"));
        var metadata = new SessionMetadata(document.RootElement); document.Dispose();
        Assert.False(metadata.IsLive); Assert.False(metadata.ApplicationPrompt.IsKnown);
        Assert.Equal("sdk", metadata.Raw.GetProperty("applicationPrompt").GetProperty("source").GetString());
        Assert.Equal("sdk", metadata.ApplicationPrompt.Raw!.Value.GetProperty("source").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadStillRejectsAnotherSessionInsteadOfProjectingItsSource(bool sdk2)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/capabilities/sessions"
            ? Capabilities() : Json(Metadata("{\"policy\":\"fallback\",\"source\":\"none\"}", sdk2, id: "other-session")));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(sdk2), http);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<TansrProtocolException>(() =>
            new AgentSession(client, "session-1", -1, false).ReadApplicationPromptAsync())).Code);
        Assert.Equal("/api/sessions/session-1?include=applicationPrompt", handler.Requests.Last());
    }

    [Fact]
    public async Task ObservationDoesNotMaskHttpFailureAsUnknownOrSendAnythingAfterPreCancellation()
    {
        using var handler = new Handler(_ => Json("{\"error\":{\"code\":\"forbidden\"}}", HttpStatusCode.Forbidden));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler)); using var client = new TansrClient(Options(), http);
        var session = new AgentSession(client, "session-1", -1, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadApplicationPromptAsync(new CancellationToken(true)));
        Assert.Empty(handler.Requests);
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => session.ReadApplicationPromptAsync());
        Assert.Equal(403, error.StatusCode); Assert.Equal("forbidden", error.Code); Assert.Single(handler.Requests);
    }
}
