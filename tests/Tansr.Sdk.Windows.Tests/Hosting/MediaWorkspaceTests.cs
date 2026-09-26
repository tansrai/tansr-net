using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Media;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class MediaWorkspaceTests
{
    [Fact]
    public async Task FileTranscriptionIsOnlyADraftAndSameMaterialIsNotChargedTwice()
    {
        using var f = await Fixture.CreateAsync(); using var view = new SessionView();
        var path = Path.Combine(Path.GetTempPath(), "tansr-media-input-" + Guid.NewGuid().ToString("N") + ".wav");
        await File.WriteAllBytesAsync(path, Wave());
        try
        {
            var messages = new List<string>(); string? draft = null;
            var commands = new ConsoleMediaCommands(f.Workspace, view, messages.Add, value => draft = value);
            Assert.True(await commands.TryHandleAsync("/asr " + path, default));
            Assert.Equal("synthetic transcript", draft); Assert.Equal(1, f.Transcriptions); Assert.Equal(0, f.Messages);
            // A new presentation of the same session must not lose the paid-result cache.
            commands = new ConsoleMediaCommands(f.Workspace, view, messages.Add, value => draft = value);
            Assert.True(await commands.TryHandleAsync("/asr " + path, default));
            Assert.Equal(1, f.Transcriptions); Assert.Equal(0, f.Messages);
            Assert.True(await commands.TryHandleAsync("/send-draft", default)); Assert.Equal(1, f.Messages);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TranscriptionUnknownAndCanceledRequestsNeverAutomaticallyRepeat()
    {
        using var f = await Fixture.CreateAsync(); f.LoseTranscription = true;
        await Assert.ThrowsAsync<TansrProtocolException>(() => f.Workspace.TranscribeAsync(Wave(), "audio/wav", "asr", default));
        var error = await Assert.ThrowsAsync<MediaException>(() => f.Workspace.TranscribeAsync(Wave(), "audio/wav", "asr", default));
        Assert.Equal("transcription_result_unknown_do_not_repeat", error.Code); Assert.Equal(1, f.Transcriptions);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Workspace.TranscribeAsync(new byte[] { 5 }, "audio/wav", "asr", canceled.Token));
        Assert.Equal(1, f.Transcriptions);
    }

    [Fact]
    public async Task ConcurrentTranscriptionConsumersShareOneInFlightRequestAndCancelLeavesItUnknown()
    {
        using var f = await Fixture.CreateAsync(); f.HoldTranscription = true;
        using var cancel = new CancellationTokenSource();
        var first = f.Workspace.TranscribeAsync(Wave(), "audio/wav", "asr", cancel.Token);
        await f.TranscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = f.Workspace.TranscribeAsync(Wave(), "audio/wav", "asr", default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, f.Transcriptions);
        Assert.Equal("transcription_result_unknown_do_not_repeat", (await Assert.ThrowsAsync<MediaException>(() => f.Workspace.TranscribeAsync(Wave(), "audio/wav", "asr", default))).Code);
    }

    [Fact]
    public async Task PermissionFlagsAndRefreshedCatalogPreventPaidCallsEvenWhenModelsRemainListed()
    {
        using var f = await Fixture.CreateAsync(); await f.Workspace.LoadCatalogAsync(default);
        f.Workspace.PrepareSpeech("abcdef", Assert.Single(f.Workspace.SpeechModels), "v", "wav", true, false);
        f.Enabled = false;
        Assert.Equal("transcription_model_not_in_catalog", (await Assert.ThrowsAsync<MediaException>(() => f.Workspace.TranscribeAsync(Wave(), "audio/wav", "asr", default))).Code);
        Assert.Equal("speech_model_not_in_catalog", (await Assert.ThrowsAsync<MediaException>(() => f.Workspace.SpeakNextAsync(default))).Code);
        Assert.Empty(f.Workspace.SpeechModels); Assert.Empty(f.Workspace.TranscriptionModels);
        Assert.Equal(0, f.Transcriptions); Assert.Equal(0, f.SpeechRequests);
        Assert.All(f.Workspace.Speech!.States, state => Assert.Equal(SpeechSegmentState.Ready, state));
        f.Enabled = true; await f.Workspace.LoadCatalogAsync(default); f.MissingCatalog = true;
        await Assert.ThrowsAsync<MediaException>(() => f.Workspace.LoadCatalogAsync(default));
        Assert.Empty(f.Workspace.SpeechModels); Assert.Empty(f.Workspace.TranscriptionModels);
    }

    [Fact]
    public async Task SegmentsRequireExplicitNextAndSurvivePresentationReopenWithoutRepeatingUnknown()
    {
        using var f = await Fixture.CreateAsync(); using var view = new SessionView(); var messages = new List<string>();
        var commands = new ConsoleMediaCommands(f.Workspace, view, messages.Add);
        await commands.TryHandleAsync("/speech-plan abcdef", default);
        Assert.Equal(3, f.Workspace.Speech!.Plan.Segments.Count); Assert.Equal(0, f.SpeechRequests);
        await commands.TryHandleAsync("/speech-next", default); Assert.Equal(new[] { "ab" }, f.SpeechInputs);
        commands = new ConsoleMediaCommands(f.Workspace, view, messages.Add);
        await commands.TryHandleAsync("/speech-next", default); Assert.Equal(new[] { "ab", "cd" }, f.SpeechInputs);
        f.LoseSpeech = true;
        await Assert.ThrowsAsync<TansrProtocolException>(() => commands.TryHandleAsync("/speech-next", default));
        commands = new ConsoleMediaCommands(f.Workspace, view, messages.Add);
        Assert.Equal("speech_result_unknown_do_not_repeat", (await Assert.ThrowsAsync<MediaException>(() => commands.TryHandleAsync("/speech-next", default))).Code);
        await Assert.ThrowsAsync<MediaException>(() => commands.TryHandleAsync("/speech-plan abcdef", default));
        Assert.Equal(3, f.SpeechRequests); Assert.Equal(2, f.Workspace.Items.Count);
    }

    [Fact]
    public async Task ChangedModelLimitRefusesAnOldPlannedSegmentBeforeBilling()
    {
        using var f = await Fixture.CreateAsync(); await f.Workspace.LoadCatalogAsync(default);
        f.Workspace.PrepareSpeech("ab", Assert.Single(f.Workspace.SpeechModels), "v", "wav", false, false);
        f.CharacterLimit = 1;
        Assert.Equal("speech_segmentation_required", (await Assert.ThrowsAsync<MediaException>(() => f.Workspace.SpeakNextAsync(default))).Code);
        Assert.Equal(0, f.SpeechRequests); Assert.Equal(SpeechSegmentState.Ready, f.Workspace.Speech!.States[0]);
    }

    [Fact]
    public async Task FourMediaKindsAreVisibleAndHistoryReplacementPreservesExpiryAndUnavailableStates()
    {
        using var f = await Fixture.CreateAsync();
        f.Workspace.Add(Json("{\"model\":\"image\",\"images\":[{\"path\":\"C:/private/server-only.png\"}]}"), "image");
        Assert.Contains("服务端路径", Assert.Single(f.Workspace.Items).Status);
        f.Workspace.Add(Json("{\"model\":\"video\",\"videos\":[{\"url\":\"https://cdn.example/movie.mp4\"}]}"), "video");
        f.Workspace.Add(Json(SpeechResponse()), "speech");
        f.Workspace.Add(Json("{\"model\":\"asr\",\"text\":\"draft\"}"), "transcript");
        Assert.Equal(4, f.Workspace.Items.Count);
        await f.Workspace.LoadHistoryAsync(default); Assert.Equal(6, f.Workspace.Items.Count);
        await f.Workspace.LoadHistoryAsync(default); Assert.Equal(6, f.Workspace.Items.Count);
        Assert.Single(f.Workspace.Items, item => item.IsHistory && item.Status.Contains("history_limit", StringComparison.Ordinal));
        var expired = Assert.Single(f.Workspace.Items, item => item.IsHistory && item.Resource != null);
        Assert.Equal("media_expired_or_removed", (await Assert.ThrowsAsync<MediaException>(() => f.Workspace.MaterializeAsync(expired, default))).Code);
        Assert.Equal("media_expired_or_removed", expired.Status);
    }

    [Fact]
    public async Task DownloadedWaveIsPreservedOnSaveAndCacheIsRemovedOnDispose()
    {
        using var f = await Fixture.CreateAsync(); f.Workspace.Add(Json(SpeechResponse()), "speech");
        var item = Assert.Single(f.Workspace.Items); var path = await f.Workspace.MaterializeAsync(item, default);
        var saved = Path.Combine(Path.GetTempPath(), "tansr-media-save-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await f.Workspace.SaveAsync(item, saved, default); Assert.Equal(Wave(), await File.ReadAllBytesAsync(saved));
            f.Workspace.Dispose(); Assert.False(File.Exists(path)); Assert.True(File.Exists(saved));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Workspace.MaterializeAsync(item, default));
        }
        finally { File.Delete(saved); }
    }

    [Fact]
    public async Task SavingDoesNotTruncateExistingUserFileWhenCanceledOrOverwriteWasNotAuthorized()
    {
        using var f = await Fixture.CreateAsync(); f.Workspace.Add(Json(SpeechResponse()), "speech");
        var item = Assert.Single(f.Workspace.Items);
        var saved = Path.Combine(Path.GetTempPath(), "tansr-media-existing-" + Guid.NewGuid().ToString("N") + ".wav");
        await File.WriteAllTextAsync(saved, "keep original");
        try
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Workspace.SaveAsync(item, saved, canceled.Token));
            Assert.Equal("keep original", await File.ReadAllTextAsync(saved));
            await Assert.ThrowsAsync<IOException>(() => f.Workspace.SaveAsync(item, saved, default, overwrite: false));
            Assert.Equal("keep original", await File.ReadAllTextAsync(saved));
            await f.Workspace.SaveAsync(item, saved, default); Assert.Equal(Wave(), await File.ReadAllBytesAsync(saved));
        }
        finally { File.Delete(saved); }
    }

    private static byte[] Wave()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(356); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(320); writer.Write(new byte[320]); return stream.ToArray();
    }
    private static string SpeechResponse() => "{\"model\":\"tts\",\"billedChars\":2,\"audio\":{\"b64\":\"" + Convert.ToBase64String(Wave()) + "\",\"mime\":\"audio/wav\",\"format\":\"wav\"}}";
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class Fixture : HttpMessageHandler
    {
        internal bool Enabled = true, MissingCatalog, LoseTranscription, LoseSpeech, HoldTranscription;
        internal TaskCompletionSource<bool> TranscriptionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Transcriptions, SpeechRequests, Messages, CharacterLimit = 2;
        internal List<string> SpeechInputs { get; } = new();
        private HttpClient _http = null!; private TansrClient _client = null!;
        internal MediaWorkspace Workspace { get; private set; } = null!;
        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); f._http = new HttpClient(f, false);
            f._client = new TansrClient(new TansrClientOptions { BaseUri = new Uri("https://serve.example"), TokenProvider = _ => Task.FromResult("synthetic-ticket") }, f._http);
            var session = await f._client.CreateSessionAsync(new());
            f.Workspace = new MediaWorkspace(session, new MediaDownloader(new MediaDownloadOptions { AllowedHttpsHosts = new[] { "cdn.example" } }, f)); return f;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "cdn.example") { Assert.Null(request.Headers.Authorization); return new HttpResponseMessage(HttpStatusCode.Gone); }
            if (path == "/v2/sessions") return Response("{\"sessionId\":\"s\",\"lastSeq\":0,\"resumed\":false}");
            if (path == "/v2/sessions/s")
            {
                if (MissingCatalog) return Response("{\"sessionId\":\"s\",\"lastSeq\":0}");
                var flag = Enabled ? "true" : "false";
                return Response("{\"sessionId\":\"s\",\"lastSeq\":0,\"media\":{\"capabilities\":{\"speechToText\":" + flag + ",\"textToSpeech\":" + flag + "},\"models\":{\"speechToText\":[{\"model\":\"asr\"}],\"textToSpeech\":[{\"model\":\"tts\",\"constraints\":{\"maxChars\":" + CharacterLimit + ",\"formats\":[\"wav\"],\"voices\":[{\"id\":\"v\"}],\"defaultVoice\":\"v\"}}]}}}");
            }
            if (path.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Transcriptions);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("asr", payload.RootElement.GetProperty("model").GetString()); Assert.StartsWith("data:audio/wav;base64,", payload.RootElement.GetProperty("audio").GetString());
                TranscriptionStarted.TrySetResult(true);
                if (HoldTranscription) await Task.Delay(Timeout.Infinite, token);
                if (LoseTranscription) throw new HttpRequestException("synthetic response loss after paid acceptance");
                return Response("{\"model\":\"asr\",\"text\":\"synthetic transcript\"}");
            }
            if (path.EndsWith("/audio/speech", StringComparison.Ordinal))
            {
                SpeechRequests++; using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); SpeechInputs.Add(payload.RootElement.GetProperty("input").GetString()!);
                if (LoseSpeech) throw new HttpRequestException("synthetic response loss after paid acceptance");
                return Response(SpeechResponse());
            }
            if (path.EndsWith("/messages", StringComparison.Ordinal)) { Messages++; return Response("{\"sessionId\":\"s\",\"accepted\":true}"); }
            if (path.EndsWith("/history", StringComparison.Ordinal)) return Response("{\"messages\":[{\"blocks\":[{\"t\":\"tool_result\",\"artifact\":{\"v\":1,\"kind\":\"image\",\"state\":\"available\",\"data\":{\"model\":\"i\",\"imageCount\":1,\"images\":[{\"url\":\"https://cdn.example/expired.png\"}]}}},{\"t\":\"tool_result\",\"artifact\":{\"v\":1,\"kind\":\"speech\",\"state\":\"unavailable\",\"reason\":\"history_limit\"}}]}]}");
            throw new InvalidOperationException("unexpected media fixture route");
        }
        protected override void Dispose(bool disposing) { if (disposing) { Workspace?.Dispose(); _client?.Dispose(); _http?.Dispose(); } base.Dispose(disposing); }
    }
}
