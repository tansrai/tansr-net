using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Tansr.Sdk.Media;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace Tansr.Examples;

/// <summary>示例的会话级媒体呈现状态。只下载用户选中的项目，远端 path 永远不映射为客户端文件。</summary>
internal sealed class MediaWorkspace : IDisposable
{
    private readonly MediaDownloader _downloader;
    private readonly List<MediaItem> _items = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenSpeech = new(StringComparer.Ordinal);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tansr-media-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _materialize = new(1, 1);
    private readonly List<string> _cachePaths = new();
    private readonly object _requestsGate = new();
    private readonly Dictionary<string, TranscriptionRequest> _transcriptions = new(StringComparer.Ordinal);
    private long _sourceBytes, _cacheBytes;
    private string? _speechModel;
    private string? _speechId;
    private bool _disposed;
    internal MediaWorkspace(AgentSession session, MediaDownloader? downloader = null)
    {
        Session = session;
        _downloader = downloader ?? new MediaDownloader(new MediaDownloadOptions
        { AllowedHttpsHosts = (Environment.GetEnvironmentVariable("TANSR_MEDIA_HOSTS") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray() });
    }
    internal AgentSession Session { get; }
    internal IReadOnlyList<MediaItem> Items => _items;
    internal IReadOnlyList<SpeechModel> SpeechModels { get; private set; } = Array.Empty<SpeechModel>();
    internal IReadOnlyList<string> TranscriptionModels { get; private set; } = Array.Empty<string>();
    internal SpeechBatch? Speech { get; private set; }
    internal string SpeechStatus => Speech == null ? "未建立朗读批次" : "朗读批次：" + Speech.States.Count(x => x == SpeechSegmentState.Done) + "/" + Speech.Plan.Segments.Count + " 段已完成" +
        (Speech.States.Any(x => x == SpeechSegmentState.Unknown) ? "；存在未知付费请求，禁止续发，请核对服务端结果" : Speech.States.Any(x => x == SpeechSegmentState.Running) ? "；请求处理中" : "；下一段需显式操作");
    internal bool PresentationTruncated { get; private set; }
    internal void Register(SessionViewSnapshot snapshot)
    {
        foreach (var tool in snapshot.Tools)
        {
            if (tool.Status != "completed" || !tool.Result.HasValue || _seen.Contains("live:" + tool.InstanceId) || MediaArtifactParser.Parse(tool.Result.Value) == null) continue;
            if (_seen.Count >= 256) { PresentationTruncated = true; return; }
            _seen.Add("live:" + tool.InstanceId); Add(tool.Result.Value, "工具 " + tool.Name);
        }
    }
    internal void Add(JsonElement result, string label)
    {
        ThrowIfDisposed();
        var artifact = MediaArtifactParser.Parse(result);
        if (artifact == null) return;
        if (artifact.Resources.Count == 0) AddItem(new MediaItem(label, null, "产物只有服务端路径，客户端无可读取材料"));
        foreach (var resource in artifact.Resources) AddItem(new MediaItem(label + " · " + artifact.Kind + " " + (resource.Index + 1), resource, "可读取"));
    }
    private void AddItem(MediaItem item)
    {
        var bytes = item.Resource == null ? 0 : System.Text.Encoding.UTF8.GetByteCount(item.Resource.Value);
        if (_items.Count >= 256 || _sourceBytes + bytes > 32 * 1024 * 1024) { PresentationTruncated = true; return; }
        _sourceBytes += bytes; _items.Add(item);
    }
    internal async Task LoadHistoryAsync(CancellationToken ct)
    {
        var history = await Session.GetHistoryAsync(ct);
        if (!history.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) throw new MediaException("history_messages_missing");
        // 压缩/快照恢复可改变历史位置；每次读取用本次权威列表替换，绝不把旧索引当作稳定产物身份。
        _items.RemoveAll(x => x.IsHistory);
        _sourceBytes = _items.Sum(x => x.Resource == null ? 0L : System.Text.Encoding.UTF8.GetByteCount(x.Resource.Value));
        var messageIndex = 0;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                var blockIndex = 0;
                foreach (var block in blocks.EnumerateArray())
                {
                    blockIndex++;
                    if (Text(block, "t") != "tool_result" || !block.TryGetProperty("artifact", out var raw) ||
                        (block.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)) continue;
                    var artifact = MediaArtifactParser.ParseHistory(raw);
                    var label = "历史 " + messageIndex + "/" + blockIndex;
                    if (artifact.Artifact != null) foreach (var resource in artifact.Artifact.Resources) AddItem(new MediaItem(label + " · " + resource.Kind + " " + (resource.Index + 1), resource, "历史材料；读取时验证有效期") { IsHistory = true });
                    else AddItem(new MediaItem(label, null, "不可用：" + artifact.Reason) { IsHistory = true });
                }
            }
            messageIndex++;
        }
    }
    internal async Task LoadCatalogAsync(CancellationToken ct)
    {
        // 失败或撤权不能继续使用上一份已授权目录。
        SpeechModels = Array.Empty<SpeechModel>(); TranscriptionModels = Array.Empty<string>();
        var metadata = await Session.GetMetadataAsync(ct);
        // 现有 HTTP 返回会话 meta 字段；同时接受调用者直接传来的 meta 形状。
        if (metadata.TryGetProperty("meta", out var meta)) metadata = meta;
        if (!metadata.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object ||
            !media.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
            !media.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object) throw new MediaException("media_catalog_unavailable");
        if (Enabled(capabilities, "textToSpeech") && models.TryGetProperty("textToSpeech", out var speech) && speech.ValueKind == JsonValueKind.Array)
            SpeechModels = speech.EnumerateArray().Select(SpeechModel.Parse).Where(x => x != null).Cast<SpeechModel>().ToArray();
        if (Enabled(capabilities, "speechToText") && models.TryGetProperty("speechToText", out var asr) && asr.ValueKind == JsonValueKind.Array)
            TranscriptionModels = asr.EnumerateArray().Select(x => Text(x, "model")).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
    }
    internal async Task<string> TranscribeAsync(byte[] bytes, string mime, string model, CancellationToken ct)
    {
        ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
        if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024) throw new MediaException("audio_input_limit");
        if (mime is not ("audio/wav" or "audio/mpeg" or "audio/mp4" or "audio/ogg" or "audio/webm" or "audio/flac")) throw new MediaException("audio_format_unsupported");
        string key;
        using (var hash = SHA256.Create()) key = model + "\n" + mime + "\n" + Convert.ToBase64String(hash.ComputeHash(bytes));
        TranscriptionRequest? existing;
        lock (_requestsGate) _transcriptions.TryGetValue(key, out existing);
        if (existing != null) return await existing.ReadAsync();
        await LoadCatalogAsync(ct);
        if (!TranscriptionModels.Contains(model, StringComparer.Ordinal)) throw new MediaException("transcription_model_not_in_catalog");
        // 保存不可变载荷；同材料在媒体窗口关闭/重开后仍共享一次请求。未知结果禁止重复收费。
        var audio = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        lock (_requestsGate)
        {
            ThrowIfDisposed();
            if (!_transcriptions.TryGetValue(key, out existing))
            {
                if (_transcriptions.Count >= 128) throw new MediaException("transcription_session_request_limit");
                existing = new TranscriptionRequest(); _transcriptions.Add(key, existing);
                existing.Start(async () =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                    var response = await Session.TranscribeAsync(new SpeechTranscriptionOptions { Audio = audio, Model = model }, linked.Token);
                    var artifact = MediaArtifactParser.Parse(response);
                    if (artifact?.Kind != MediaKind.Transcript) throw new MediaException("transcription_invalid_result");
                    // 已拿到并校验完整结果就是已知结果；之后关闭窗口不能把它降成未知付费请求。
                    lock (_requestsGate) { if (!_disposed) Add(response, "转写草稿"); }
                    return artifact.Resources[0].Value;
                });
            }
        }
        return await existing.ReadAsync();
    }
    internal SpeechBatch PrepareSpeech(string text, SpeechModel model, string? voice, string? format, bool segment, bool replace)
    {
        ThrowIfDisposed();
        if (Speech != null && Speech.States.Any(x => x != SpeechSegmentState.Ready) && !replace) throw new MediaException("speech_batch_exists_use_explicit_reset");
        Speech = new SpeechBatch(model.Plan(text, segment), new SpeechOptions { Model = model.Model, Voice = voice, Format = format });
        _speechModel = model.Model;
        _speechId = Guid.NewGuid().ToString("N");
        return Speech;
    }
    internal void ResetSpeech() { ThrowIfDisposed(); Speech = null; }
    internal async Task<JsonElement> SpeakNextAsync(CancellationToken ct)
    {
        ThrowIfDisposed(); var batch = Speech ?? throw new MediaException("speech_batch_not_prepared");
        var batchId = _speechId;
        if (batch.States.Any(x => x == SpeechSegmentState.Unknown)) throw new MediaException("speech_result_unknown_do_not_repeat");
        var index = batch.States.ToList().FindIndex(x => x == SpeechSegmentState.Ready);
        if (index < 0) throw new MediaException(batch.States.Any(x => x == SpeechSegmentState.Running) ? "speech_request_in_progress" : "speech_batch_completed");
        await LoadCatalogAsync(ct);
        var model = SpeechModels.FirstOrDefault(x => x.Model == _speechModel) ?? throw new MediaException("speech_model_not_in_catalog");
        _ = model.Plan(batch.Plan.Segments[index], false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var response = await batch.SpeakSegmentAsync(index, Session.SpeakAsync, linked.Token);
        lock (_requestsGate)
        {
            if (!_disposed && _seenSpeech.Count < 256 && _seenSpeech.Add(batchId + ":" + index)) Add(response, "朗读段 " + (index + 1));
        }
        return response;
    }
    internal async Task<string> MaterializeAsync(MediaItem item, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _materialize.WaitAsync(linked.Token);
        try
        {
            if (item.LocalPath != null && File.Exists(item.LocalPath)) return item.LocalPath;
            if (item.Resource == null) throw new MediaException("media_unavailable");
            var content = await _downloader.ReadAsync(item.Resource, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_cacheBytes + content.Length > 512L * 1024 * 1024) throw new MediaException("media_session_cache_limit");
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + "." + content.FileExtension);
            try
            {
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await content.WriteToAsync(output, linked.Token);
                lock (_requestsGate)
                {
                    ThrowIfDisposed(); linked.Token.ThrowIfCancellationRequested();
                    item.LocalPath = path; _cachePaths.Add(path); _cacheBytes += content.Length; item.Status = "已缓存 " + content.Length + " 字节";
                }
                return path;
            }
            catch { if (File.Exists(path)) File.Delete(path); TryRemoveDirectory(); throw; }
        }
        catch (MediaException error) { item.Status = error.Code; throw; }
        finally { _materialize.Release(); }
    }
    internal async Task SaveAsync(MediaItem item, string destination, CancellationToken ct, bool overwrite = true)
    {
        var source = await MaterializeAsync(item, ct);
        var target = Path.GetFullPath(destination);
        var staging = Path.Combine(Path.GetDirectoryName(target)!, ".tansr-media-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, 65536, ct);
                output.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            if (overwrite && File.Exists(target)) File.Replace(staging, target, null);
            else File.Move(staging, target);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    public void Dispose()
    {
        lock (_requestsGate)
        {
            if (_disposed) return; _disposed = true; _stop.Cancel(); _downloader.Dispose();
            // 目录为本实例随机创建；仅删除登记的已完成文件，不递归清理未知内容。
            foreach (var path in _cachePaths) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            TryRemoveDirectory();
        }
    }
    private void TryRemoveDirectory() { try { if (Directory.Exists(_directory)) Directory.Delete(_directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(MediaWorkspace)); }
    private static bool Enabled(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;
    internal static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private sealed class TranscriptionRequest
    {
        private Task<string> _task = null!;
        private int _unknown;
        internal void Start(Func<Task<string>> send) => _task = SendAsync(send);
        private async Task<string> SendAsync(Func<Task<string>> send)
        {
            await Task.Yield();
            try { return await send(); } catch { Interlocked.Exchange(ref _unknown, 1); throw; }
        }
        internal Task<string> ReadAsync() => Volatile.Read(ref _unknown) == 1 ? Task.FromException<string>(new MediaException("transcription_result_unknown_do_not_repeat")) : _task;
    }
}

internal sealed class MediaItem(string label, MediaResource? resource, string status)
{
    internal string Label { get; } = label;
    internal MediaResource? Resource { get; } = resource;
    internal string Status { get; set; } = status;
    internal string? LocalPath { get; set; }
    internal bool IsHistory { get; set; }
    public override string ToString() => Label + " — " + Status;
}

internal sealed class SpeechModel
{
    internal string Model { get; private set; } = "";
    internal string[] Voices { get; private set; } = Array.Empty<string>();
    internal string[] Formats { get; private set; } = Array.Empty<string>();
    internal string? DefaultVoice { get; private set; }
    internal int? MaxChars { get; private set; }
    internal int? InputMax { get; private set; }
    internal SpeechInputUnit InputUnit { get; private set; }
    internal SpeechPlan Plan(string text, bool segment) => SpeechPlan.Create(text, new SpeechPlanOptions
    { MaximumWeightedCharacters = MaxChars, MaximumInputUnits = InputMax, InputUnit = InputUnit, AllowSegmentation = segment });
    internal static SpeechModel? Parse(JsonElement value)
    {
        var name = MediaWorkspace.Text(value, "model"); if (name == null) return null;
        if (!value.TryGetProperty("constraints", out var c) || c.ValueKind != JsonValueKind.Object) return new SpeechModel { Model = name };
        var result = new SpeechModel { Model = name, MaxChars = Number(c, "maxChars"), DefaultVoice = MediaWorkspace.Text(c, "defaultVoice") };
        if (c.TryGetProperty("voices", out var voices) && voices.ValueKind == JsonValueKind.Array) result.Voices = voices.EnumerateArray().Select(x => MediaWorkspace.Text(x, "id")).Where(x => x != null).Cast<string>().ToArray();
        if (c.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array) result.Formats = formats.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
        if (c.TryGetProperty("inputLimit", out var limit) && limit.ValueKind == JsonValueKind.Object)
        {
            result.InputMax = Number(limit, "max");
            var unit = MediaWorkspace.Text(limit, "unit");
            if (unit == "utf16_code_units") result.InputUnit = SpeechInputUnit.Utf16CodeUnits;
            else if (unit == "utf8_bytes") result.InputUnit = SpeechInputUnit.Utf8Bytes;
            else if (unit != "unicode_code_points") result.MaxChars = null;
        }
        return result;
    }
    private static int? Number(JsonElement value, string key) => value.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var number) && number > 0 ? number : null;
    public override string ToString() => Model;
}
