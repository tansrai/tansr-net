using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tansr.Sdk.Media;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Tests.Views;

public sealed class MediaTests
{
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private const string Speech = "{\"model\":\"speech-model\",\"billedChars\":1,\"audio\":{\"b64\":\"AQID\",\"mime\":\"audio/wav\",\"format\":\"wav\"}}";
    private static MediaResource Image(string url) => MediaArtifactParser.Parse(Json("{\"model\":\"i\",\"images\":[{\"url\":\"" + url + "\"}]}"))!.Resources[0];

    [Fact]
    public void StructuredMediaMatchesPrecedenceAndNeverTreatsPathsOrErrorsAsClientFiles()
    {
        Assert.Null(MediaArtifactParser.Parse(Json("{\"model\":\"m\",\"images\":[],\"videos\":[{\"url\":\"https://cdn.example/v.mp4\"}]}")));
        var pathOnly = MediaArtifactParser.Parse(Json("{\"model\":\"m\",\"images\":[{\"path\":\"C:/secret\"}],\"videos\":[{\"url\":\"https://cdn.example/v.mp4\"}]}"));
        Assert.Equal(MediaKind.Image, pathOnly!.Kind); Assert.Empty(pathOnly.Resources);
        Assert.Null(MediaArtifactParser.Parse(Json("{\"model\":\"m\",\"errorCode\":\"denied\",\"audio\":{\"b64\":\"AQID\",\"mime\":\"audio/wav\"}}")));
        Assert.Equal(MediaKind.Transcript, MediaArtifactParser.Parse(Json("{\"model\":\"m\",\"text\":\"\"}"))!.Kind);
        var both = Json("{\"model\":\"m\",\"audio\":{\"b64\":\"AQID\",\"url\":\"https://untrusted.invalid/audio\",\"mime\":\"audio/wav\"}}");
        Assert.Equal(MediaSourceKind.InlineAudio, MediaArtifactParser.Parse(both)!.Resources[0].SourceKind);
    }

    [Theory]
    [InlineData("{\"v\":\"1\",\"state\":\"available\",\"kind\":\"speech\"}", "unsupported")]
    [InlineData("{\"v\":1,\"state\":\"unavailable\",\"kind\":\"image\",\"reason\":\"history_limit\"}", "unavailable")]
    [InlineData("{\"v\":1,\"state\":\"unavailable\",\"kind\":\"image\",\"reason\":\"made_up\"}", "invalid")]
    [InlineData("{\"v\":1,\"state\":\"available\",\"kind\":\"image\",\"data\":{\"model\":\"m\",\"imageCount\":1,\"images\":[{\"url\":\"data:image/png;base64,AQID\"}]}}", "invalid")]
    public void HistoryUnavailableUnknownAndInvalidMaterialsRemainVisible(string input, string state)
        => Assert.Equal(state, MediaArtifactParser.ParseHistory(Json(input)).State);

    [Fact]
    public void HistorySpeechRequiresOriginalFormatAndRejectsInvalidDurations()
    {
        Assert.Equal("available", MediaArtifactParser.ParseHistory(Json("{\"v\":1,\"state\":\"available\",\"kind\":\"speech\",\"data\":" + Speech + "}")).State);
        var invalid = Speech.Replace("\"format\":\"wav\"", "\"format\":\"wav\",\"durationMs\":-1");
        Assert.Equal("invalid", MediaArtifactParser.ParseHistory(Json("{\"v\":1,\"state\":\"available\",\"kind\":\"speech\",\"data\":" + invalid + "}")).State);
    }

    [Fact]
    public async Task DownloaderUsesSeparateUnauthenticatedExactHostTransportAndCachesOnlyBytes()
    {
        var sent = 0;
        using var handler = new Handler(request =>
        {
            sent++; Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("cdn.example", request.RequestUri!.Host);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png"); return response;
        });
        using var downloader = new MediaDownloader(new MediaDownloadOptions { AllowedHttpsHosts = new[] { "cdn.example" } }, handler);
        var content = await downloader.ReadAsync(Image("https://cdn.example/image.png")); Assert.Equal(3, content.Length); Assert.Equal("png", content.FileExtension);
        foreach (var uri in new[] { "https://cdn.example.attacker.test/i", "http://cdn.example/i", "https://user:pass@cdn.example/i", "https://cdn.example:8443/i", "file:///C:/secret" })
            Assert.Equal("media_host_not_authorized", (await Assert.ThrowsAsync<MediaException>(() => downloader.ReadAsync(Image(uri)))).Code);
        Assert.Equal(1, sent);
    }

    [Theory]
    [InlineData(302, "media_redirect_rejected")]
    [InlineData(404, "media_expired_or_removed")]
    [InlineData(410, "media_expired_or_removed")]
    [InlineData(403, "media_access_denied_or_expired")]
    public async Task DownloaderDoesNotFollowRedirectsAndSurfacesExpiry(int status, string code)
    {
        using var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var downloader = new MediaDownloader(new MediaDownloadOptions { AllowedHttpsHosts = new[] { "cdn.example" } }, handler);
        Assert.Equal(code, (await Assert.ThrowsAsync<MediaException>(() => downloader.ReadAsync(Image("https://cdn.example/image")))).Code);
    }

    [Fact]
    public async Task DownloaderBoundsResponsesAndAllowsInlineWithoutExternalAuthorization()
    {
        using var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[20]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png"); return response;
        });
        using var downloader = new MediaDownloader(new MediaDownloadOptions { AllowedHttpsHosts = new[] { "cdn.example" }, MaximumBytes = 10 }, handler);
        Assert.Equal("media_too_large", (await Assert.ThrowsAsync<MediaException>(() => downloader.ReadAsync(Image("https://cdn.example/i")))).Code);
        using var inline = new MediaDownloader(new MediaDownloadOptions());
        Assert.Equal(3, (await inline.ReadAsync(MediaArtifactParser.Parse(Json(Speech))!.Resources[0])).Length);
        Assert.Equal("invalid_media_signature", (await Assert.ThrowsAsync<MediaException>(() => inline.ReadAsync(Image("data:image/png;base64,AQID")))).Code);
    }

    [Fact]
    public void SpeechPlanningHonorsUnicodeAtomicAndWeightedLimitsWithoutTextLoss()
    {
        var plan = SpeechPlan.Create("  中😀ab  ", new SpeechPlanOptions { MaximumWeightedCharacters = 3, MaximumInputUnits = 5, InputUnit = SpeechInputUnit.Utf8Bytes, AllowSegmentation = true });
        Assert.Equal("中😀ab", string.Concat(plan.Segments)); Assert.Equal(5, plan.EstimatedCharacters);
        Assert.All(plan.Segments, x => Assert.True(System.Text.Encoding.UTF8.GetByteCount(x) <= 5));
        Assert.Equal("speech_limit_unknown", Assert.Throws<MediaException>(() => SpeechPlan.Create("hello", new SpeechPlanOptions())).Code);
        Assert.Equal("speech_segmentation_required", Assert.Throws<MediaException>(() => SpeechPlan.Create("中文", new SpeechPlanOptions { MaximumWeightedCharacters = 2 })).Code);
        Assert.Equal("speech_invalid_unicode", Assert.Throws<MediaException>(() => SpeechPlan.Create("\ud800", new SpeechPlanOptions { MaximumWeightedCharacters = 3 })).Code);
    }

    [Fact]
    public async Task CompletedSpeechIsCachedConcurrentConsumersShareOneRequestAndUnknownNeverRepeats()
    {
        var plan = SpeechPlan.Create("abcd", new SpeechPlanOptions { MaximumWeightedCharacters = 2, AllowSegmentation = true });
        var batch = new SpeechBatch(plan, new SpeechOptions { Model = "model", Voice = "voice" });
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        Task<JsonElement> Send(SpeechOptions options, CancellationToken _) { Interlocked.Increment(ref count); Assert.Equal("ab", options.Input); return completion.Task; }
        var first = batch.SpeakSegmentAsync(0, Send); var concurrent = batch.SpeakSegmentAsync(0, Send); Assert.Same(first, concurrent);
        completion.SetResult(Json(Speech)); await first; await batch.SpeakSegmentAsync(0, Send); Assert.Equal(1, count);
        await Assert.ThrowsAsync<HttpRequestException>(() => batch.SpeakSegmentAsync(1, (_, _) => Task.FromException<JsonElement>(new HttpRequestException())));
        Assert.Equal(SpeechSegmentState.Unknown, batch.States[1]);
        Assert.Equal("speech_result_unknown_do_not_repeat", Assert.Throws<MediaException>(() => { _ = batch.SpeakSegmentAsync(1, Send); }).Code);
    }

    [Fact]
    public void ReusedToolIdAcrossTurnsPreservesSeparateResultInstances()
    {
        using var view = new SessionView();
        void Apply(string value) => view.Apply(new AgentEvent("event", null, Json(value)));
        Apply("{\"type\":\"turn.started\"}"); Apply("{\"type\":\"tool.proposed\",\"toolCallId\":\"same\",\"name\":\"image\"}");
        Apply("{\"type\":\"tool.completed\",\"toolCallId\":\"same\",\"content\":\"old\",\"data\":{\"old\":true}}");
        Apply("{\"type\":\"turn.completed\"}"); Apply("{\"type\":\"turn.started\"}"); Apply("{\"type\":\"tool.proposed\",\"toolCallId\":\"same\",\"name\":\"image\"}");
        Apply("{\"type\":\"tool.completed\",\"toolCallId\":\"same\",\"content\":\"new\",\"data\":{\"new\":true}}");
        Assert.Equal(2, view.Snapshot.Tools.Count); Assert.Equal("same", view.Snapshot.Tools[0].Id); Assert.NotEqual(view.Snapshot.Tools[0].InstanceId, view.Snapshot.Tools[1].InstanceId);
        Assert.True(view.Snapshot.Tools[0].Result!.Value.GetProperty("old").GetBoolean());
        Assert.Contains("old", SessionViewTextFormatter.Format(view.Snapshot)); Assert.Contains("new", SessionViewTextFormatter.Format(view.Snapshot));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
}
