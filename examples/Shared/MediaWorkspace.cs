using System.IO;
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
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tansr-media-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _materialize = new(1, 1);
    private readonly List<string> _cachePaths = new();
    private long _sourceBytes, _cacheBytes;
    private bool _disposed;
    internal MediaWorkspace(AgentSession session)
    {
        Session = session;
        _downloader = new MediaDownloader(new MediaDownloadOptions
        { AllowedHttpsHosts = (Environment.GetEnvironmentVariable("TANSR_MEDIA_HOSTS") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray() });
    }
    internal AgentSession Session { get; }
    internal IReadOnlyList<MediaItem> Items => _items;
    internal IReadOnlyList<SpeechModel> SpeechModels { get; private set; } = Array.Empty<SpeechModel>();
    internal IReadOnlyList<string> TranscriptionModels { get; private set; } = Array.Empty<string>();
    internal bool PresentationTruncated { get; private set; }
    internal void Register(SessionViewSnapshot snapshot)
    {
        foreach (var tool in snapshot.Tools)
            if (tool.Status == "completed" && tool.Result.HasValue && _seen.Add("live:" + tool.InstanceId)) Add(tool.Result.Value, "工具 " + tool.Name);
    }
    internal void Add(JsonElement result, string label)
    {
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
        var metadata = await Session.GetMetadataAsync(ct);
        // 现有 HTTP 返回会话 meta 字段；同时接受调用者直接传来的 meta 形状。
        if (metadata.TryGetProperty("meta", out var meta)) metadata = meta;
        if (!metadata.TryGetProperty("media", out var media) || !media.TryGetProperty("models", out var models)) throw new MediaException("media_catalog_unavailable");
        if (models.TryGetProperty("textToSpeech", out var speech) && speech.ValueKind == JsonValueKind.Array)
            SpeechModels = speech.EnumerateArray().Select(SpeechModel.Parse).Where(x => x != null).Cast<SpeechModel>().ToArray();
        if (models.TryGetProperty("speechToText", out var asr) && asr.ValueKind == JsonValueKind.Array)
            TranscriptionModels = asr.EnumerateArray().Select(x => Text(x, "model")).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
    }
    internal async Task<string> TranscribeAsync(byte[] bytes, string mime, string model, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024) throw new MediaException("audio_input_limit");
        if (!TranscriptionModels.Contains(model, StringComparer.Ordinal)) throw new MediaException("transcription_model_not_in_catalog");
        var response = await Session.TranscribeAsync(new SpeechTranscriptionOptions { Audio = "data:" + mime + ";base64," + Convert.ToBase64String(bytes), Model = model }, ct);
        var artifact = MediaArtifactParser.Parse(response);
        if (artifact?.Kind != MediaKind.Transcript) throw new MediaException("transcription_invalid_result");
        Add(response, "转写草稿"); return artifact.Resources[0].Value;
    }
    internal async Task<string> MaterializeAsync(MediaItem item, CancellationToken ct)
    {
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
                linked.Token.ThrowIfCancellationRequested(); item.LocalPath = path; _cachePaths.Add(path); _cacheBytes += content.Length; item.Status = "已缓存 " + content.Length + " 字节"; return path;
            }
            catch { if (File.Exists(path)) File.Delete(path); throw; }
        }
        catch (MediaException error) { item.Status = error.Code; throw; }
        finally { _materialize.Release(); }
    }
    internal async Task SaveAsync(MediaItem item, string destination, CancellationToken ct)
    {
        var source = await MaterializeAsync(item, ct);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, 65536, ct);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _stop.Cancel(); _downloader.Dispose();
        // 目录为本实例随机创建；仅删除登记的已完成文件，不递归清理未知内容。
        foreach (var path in _cachePaths) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    internal static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
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
