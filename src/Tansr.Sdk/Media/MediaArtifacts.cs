using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace Tansr.Sdk.Media;

public enum MediaKind { Image, Video, Speech, Transcript }
public enum MediaSourceKind { Uri, InlineAudio, Text }

public sealed class MediaException : Exception
{
    public MediaException(string code) : base(code) { Code = code; }
    public string Code { get; }
}

/// <summary>由真实结构化产物解析出的呈现材料。路径字段不构成读取客户端磁盘的授权。</summary>
public sealed class MediaResource
{
    internal MediaResource(MediaKind kind, MediaSourceKind sourceKind, string value, string? mime, int index)
    { Kind = kind; SourceKind = sourceKind; Value = value; MimeType = mime; Index = index; }
    public MediaKind Kind { get; }
    public MediaSourceKind SourceKind { get; }
    public string Value { get; }
    public string? MimeType { get; }
    public int Index { get; }
}

public sealed class MediaArtifact
{
    internal MediaArtifact(MediaKind kind, string model, JsonElement data, IList<MediaResource> resources)
    { Kind = kind; Model = model; Data = data.Clone(); Resources = new ReadOnlyCollection<MediaResource>(resources); }
    public MediaKind Kind { get; }
    public string Model { get; }
    public JsonElement Data { get; }
    public IReadOnlyList<MediaResource> Resources { get; }
}

public sealed class HistoryMediaArtifact
{
    internal HistoryMediaArtifact(string state, string? reason, MediaArtifact? artifact)
    { State = state; Reason = reason; Artifact = artifact; }
    public string State { get; }
    public string? Reason { get; }
    public MediaArtifact? Artifact { get; }
}

/// <summary>与 protocol/media.ts 的 image→video→speech→transcript 判序一致，不从文本或工具名称猜媒体。</summary>
public static class MediaArtifactParser
{
    public static MediaArtifact? Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || HasError(data)) return null;
        var model = Text(data, "model"); if (model == null) return null;
        // 与原共享谓词一致：首个在场的产物族不可渲染时不能降级到另一个族假装成功。
        if (data.TryGetProperty("images", out var images) && !HasMaterial(images)) return null;
        else if (!data.TryGetProperty("images", out _) && data.TryGetProperty("videos", out var videos) && !HasMaterial(videos)) return null;
        else if (!data.TryGetProperty("images", out _) && !data.TryGetProperty("videos", out _) && data.TryGetProperty("audio", out var audioCheck) &&
            string.IsNullOrEmpty(Text(audioCheck, "url")) && string.IsNullOrEmpty(Text(audioCheck, "b64"))) return null;
        if (HasStringUrl(data, "images", out var imageList)) return UrlArtifact(data, model, MediaKind.Image, imageList);
        if (HasStringUrl(data, "videos", out var videoList)) return UrlArtifact(data, model, MediaKind.Video, videoList);
        if (data.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object && Text(audio, "mime") is { } mime)
        {
            var b64 = Text(audio, "b64"); var url = Text(audio, "url");
            if (!string.IsNullOrEmpty(b64)) return new MediaArtifact(MediaKind.Speech, model, data, new[] { new MediaResource(MediaKind.Speech, MediaSourceKind.InlineAudio, b64!, mime, 0) });
            if (!string.IsNullOrEmpty(url)) return new MediaArtifact(MediaKind.Speech, model, data, new[] { new MediaResource(MediaKind.Speech, MediaSourceKind.Uri, url!, mime, 0) });
        }
        var text = Text(data, "text");
        if (text != null && !data.TryGetProperty("images", out _) && !data.TryGetProperty("videos", out _) &&
            !data.TryGetProperty("results", out _) && !data.TryGetProperty("audio", out _))
            return new MediaArtifact(MediaKind.Transcript, model, data, new[] { new MediaResource(MediaKind.Transcript, MediaSourceKind.Text, text, "text/plain", 0) });
        return null;
    }

    public static HistoryMediaArtifact ParseHistory(JsonElement artifact)
    {
        if (artifact.ValueKind == JsonValueKind.Undefined) return new HistoryMediaArtifact("absent", null, null);
        if (artifact.ValueKind != JsonValueKind.Object) return new HistoryMediaArtifact("invalid", "invalid_history_artifact", null);
        if (!artifact.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1 ||
            Text(artifact, "kind") is not ("image" or "video" or "speech")) return new HistoryMediaArtifact("unsupported", "unsupported_history_artifact", null);
        // 历史是已出版的受限呈现扩展；超大/未知/坏材料保留不可用，不反向猜用户正文中的链接。
        if (Encoding.UTF8.GetByteCount(artifact.GetRawText()) > 8 * 1024 * 1024) return new HistoryMediaArtifact("invalid", "history_artifact_over_limit", null);
        var state = Text(artifact, "state");
        if (state == "unavailable") return Text(artifact, "reason") is "invalid_result" or "over_limit" or "history_limit" ? new HistoryMediaArtifact("unavailable", Text(artifact, "reason"), null) : new HistoryMediaArtifact("invalid", "invalid_history_artifact", null);
        if (state != "available" || !artifact.TryGetProperty("data", out var data)) return new HistoryMediaArtifact("invalid", "invalid_history_artifact", null);
        var parsed = Parse(data);
        if (parsed == null || parsed.Resources.Count < 1 || parsed.Resources.Count > 16 || parsed.Kind.ToString().ToLowerInvariant() != Text(artifact, "kind") ||
            parsed.Model.Length > 1024 || !ValidHistoryShape(data, parsed.Kind) || !ValidHistoryCounts(data, parsed.Kind)) return new HistoryMediaArtifact("invalid", "invalid_history_artifact", null);
        foreach (var resource in parsed.Resources)
        {
            if (resource.SourceKind == MediaSourceKind.Uri)
            {
                if (resource.Value.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (resource.Kind != MediaKind.Image) return new HistoryMediaArtifact("invalid", "invalid_history_material", null);
                    try { MediaFormats.DecodeDataUri(resource.Value, MediaKind.Image, 8 * 1024 * 1024); }
                    catch (MediaException) { return new HistoryMediaArtifact("invalid", "invalid_history_material", null); }
                }
                else if (resource.Value.Length > 16384 || !Uri.TryCreate(resource.Value, UriKind.Absolute, out var url) ||
                    (url.Scheme != "https" && url.Scheme != "http") || !string.IsNullOrEmpty(url.UserInfo)) return new HistoryMediaArtifact("invalid", "invalid_history_material", null);
            }
            else if (resource.SourceKind == MediaSourceKind.InlineAudio)
            {
                try { MediaFormats.DecodeBase64(resource.Value, resource.MimeType ?? "", MediaKind.Speech, 8 * 1024 * 1024); }
                catch (MediaException) { return new HistoryMediaArtifact("invalid", "invalid_history_material", null); }
            }
        }
        return new HistoryMediaArtifact("available", null, parsed);
    }

    private static bool ValidHistoryShape(JsonElement data, MediaKind kind)
    {
        if (data.TryGetProperty("taskId", out var taskId) && (taskId.ValueKind != JsonValueKind.String || taskId.GetString()!.Length > 4096)) return false;
        if (kind == MediaKind.Speech)
        {
            var audio = data.GetProperty("audio");
            if (audio.TryGetProperty("url", out var rawUrl) && (rawUrl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(rawUrl.GetString()) ||
                rawUrl.GetString()!.Length > 16384 || !Uri.TryCreate(rawUrl.GetString(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo))) return false;
            if (audio.TryGetProperty("b64", out var rawBase64))
            {
                if (rawBase64.ValueKind != JsonValueKind.String) return false;
                try { MediaFormats.DecodeBase64(rawBase64.GetString()!, Text(audio, "mime") ?? "", MediaKind.Speech, 8 * 1024 * 1024); }
                catch (MediaException) { return false; }
            }
            return Text(audio, "format") is { Length: <= 64 } && Text(audio, "mime") is { Length: <= 128 } && OptionalCount(audio, "durationMs") && OptionalCount(audio, "sampleRate");
        }
        var list = data.GetProperty(kind == MediaKind.Image ? "images" : "videos");
        return list.GetArrayLength() >= 1 && list.GetArrayLength() <= 16 && list.EnumerateArray().All(x => !string.IsNullOrEmpty(Text(x, "url")));
    }
    private static bool OptionalCount(JsonElement data, string key) => !data.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && !double.IsNaN(number) && !double.IsInfinity(number) && number >= 0;
    private static bool ValidHistoryCounts(JsonElement data, MediaKind kind)
    {
        var keys = kind == MediaKind.Image ? new[] { "imageCount" } : kind == MediaKind.Video ? new[] { "videoCount", "billedSeconds" } : new[] { "billedChars" };
        return keys.All(key => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && !double.IsNaN(number) && !double.IsInfinity(number) && number >= 0);
    }
    private static bool HasError(JsonElement data) => data.TryGetProperty("errorCode", out var error) && error.ValueKind != JsonValueKind.Null && !(error.ValueKind == JsonValueKind.String && error.GetString() == "");
    private static bool HasMaterial(JsonElement list) => list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(x => !string.IsNullOrEmpty(Text(x, "url")) || !string.IsNullOrEmpty(Text(x, "path")));
    private static bool HasStringUrl(JsonElement data, string key, out JsonElement list) => data.TryGetProperty(key, out list) && list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(x => Text(x, "url") != null);
    private static MediaArtifact UrlArtifact(JsonElement data, string model, MediaKind kind, JsonElement list)
    {
        var resources = new List<MediaResource>(); var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var url = Text(item, "url"); if (!string.IsNullOrEmpty(url)) resources.Add(new MediaResource(kind, MediaSourceKind.Uri, url!, null, index)); index++;
        }
        return new MediaArtifact(kind, model, data, resources);
    }
    internal static string? Text(JsonElement data, string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
