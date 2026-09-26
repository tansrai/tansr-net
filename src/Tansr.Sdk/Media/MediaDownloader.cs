using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Tansr.Sdk.Media;

public sealed class MediaDownloadOptions
{
    /// <summary>宿主明确授权的精确 HTTPS 主机；缺省空列表，不接受模型提供的白名单。</summary>
    public IReadOnlyList<string> AllowedHttpsHosts { get; set; } = Array.Empty<string>();
    public int MaximumBytes { get; set; } = 256 * 1024 * 1024;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class MediaContent
{
    private readonly byte[] _bytes;
    internal MediaContent(byte[] bytes, string mime) { _bytes = bytes; MimeType = mime; }
    public string MimeType { get; }
    public string FileExtension => MediaFormats.Extension(MimeType);
    public int Length => _bytes.Length;
    public Stream OpenRead() => new MemoryStream(_bytes, false);
    public Task WriteToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        return destination.WriteAsync(_bytes, 0, _bytes.Length, cancellationToken);
    }
}

/// <summary>专用无认证媒体传输。拒绝重定向、Cookie、URL凭据；不使用会话 HttpClient 或用户票据。</summary>
public sealed class MediaDownloader : IDisposable
{
    private readonly HttpClient _client;
    private readonly HashSet<string> _hosts;
    private readonly int _maximum;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    /// <param name="handler">可选受信测试/宿主传输；必须禁用自动重定向，不得注入认证头或 Cookie，其生命周期归调用者。</param>
    public MediaDownloader(MediaDownloadOptions options, HttpMessageHandler? handler = null)
    {
        if (options == null || options.AllowedHttpsHosts == null || options.MaximumBytes < 1 || options.MaximumBytes > 256 * 1024 * 1024 ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(10)) throw new ArgumentException("invalid_media_options", nameof(options));
        _maximum = options.MaximumBytes; _timeout = options.Timeout;
        _hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in options.AllowedHttpsHosts)
        {
            if (string.IsNullOrEmpty(host) || !Uri.TryCreate("https://" + host, UriKind.Absolute, out var uri) || uri.Host.Length == 0 ||
                uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443 ||
                !string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("invalid_media_host", nameof(options));
            _hosts.Add(uri.IdnHost);
        }
        _client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) : new HttpClient(handler, false);
        _client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    public async Task<MediaContent> ReadAsync(MediaResource resource, CancellationToken cancellationToken = default)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MediaDownloader));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token); timeout.CancelAfter(_timeout);
        timeout.Token.ThrowIfCancellationRequested();
        if (resource.SourceKind == MediaSourceKind.Text)
        {
            if (Encoding.UTF8.GetByteCount(resource.Value) > _maximum) throw new MediaException("media_too_large");
            return new MediaContent(Encoding.UTF8.GetBytes(resource.Value), "text/plain");
        }
        if (resource.SourceKind == MediaSourceKind.InlineAudio)
            return MediaFormats.DecodeBase64(resource.Value, resource.MimeType ?? "", resource.Kind, _maximum);
        if (resource.Value.StartsWith("data:", StringComparison.Ordinal)) return MediaFormats.DecodeDataUri(resource.Value, resource.Kind, _maximum);
        if (resource.Value.Length > 16384 || !Uri.TryCreate(resource.Value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.Port != 443 || !_hosts.Contains(uri.IdnHost)) throw new MediaException("media_host_not_authorized");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage response;
        try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new MediaException("media_download_failed"); }
        using (response)
        {
            var code = (int)response.StatusCode;
            if (code >= 300 && code < 400) throw new MediaException("media_redirect_rejected");
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone) throw new MediaException("media_expired_or_removed");
            if (code == 401 || code == 403) throw new MediaException("media_access_denied_or_expired");
            if (!response.IsSuccessStatusCode || response.Content == null) throw new MediaException("media_download_failed");
            var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            MediaFormats.EnsureType(mime, resource.Kind);
            if (response.Content.Headers.ContentLength > _maximum) throw new MediaException("media_too_large");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var cancelRead = timeout.Token.Register(() => input.Dispose());
            using var output = new MemoryStream(); var buffer = new byte[65536];
            while (true)
            {
                var count = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, _maximum + 1L - output.Length), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                output.Write(buffer, 0, count);
                if (output.Length > _maximum) throw new MediaException("media_too_large");
            }
            if (output.Length == 0) throw new MediaException("media_empty_response");
            return new MediaContent(output.ToArray(), mime);
        }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _lifetime.Cancel(); _client.Dispose(); }
}

internal static class MediaFormats
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.Ordinal)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/gif"] = "gif",
        ["image/webp"] = "webp",
        ["video/mp4"] = "mp4",
        ["video/webm"] = "webm",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
        ["audio/mpeg"] = "mp3",
        ["audio/mp4"] = "m4a",
        ["audio/ogg"] = "ogg",
        ["audio/webm"] = "webm",
        ["audio/flac"] = "flac",
        ["text/plain"] = "txt",
    };
    internal static string Extension(string mime) => Extensions.TryGetValue(mime, out var extension) ? extension : throw new MediaException("media_type_not_supported");
    internal static void EnsureType(string mime, MediaKind kind)
    {
        var prefix = kind == MediaKind.Speech ? "audio/" : kind == MediaKind.Transcript ? "text/" : kind.ToString().ToLowerInvariant() + "/";
        if (!mime.StartsWith(prefix, StringComparison.Ordinal) || !Extensions.ContainsKey(mime)) throw new MediaException("media_type_not_supported");
    }
    internal static MediaContent DecodeDataUri(string value, MediaKind kind, int maximum)
    {
        var split = value.IndexOf(";base64,", StringComparison.Ordinal);
        if (!value.StartsWith("data:", StringComparison.Ordinal) || split < 5 || split > 128) throw new MediaException("invalid_media_data_uri");
        return DecodeBase64(value.Substring(split + 8), value.Substring(5, split - 5), kind, maximum);
    }
    internal static MediaContent DecodeBase64(string value, string mime, MediaKind kind, int maximum)
    {
        EnsureType(mime, kind);
        if (value.Length == 0 || value.Length > ((maximum + 2L) / 3) * 4 || value.Length % 4 != 0 || value.Any(char.IsWhiteSpace)) throw new MediaException("invalid_media_base64");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); } catch (FormatException) { throw new MediaException("invalid_media_base64"); }
        if (bytes.Length == 0 || bytes.Length > maximum || Convert.ToBase64String(bytes) != value) throw new MediaException("invalid_media_base64");
        if (kind == MediaKind.Image)
        {
            var valid = mime == "image/png" ? bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) :
                mime == "image/jpeg" ? bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 :
                mime == "image/gif" ? bytes.Length >= 6 && (Encoding.ASCII.GetString(bytes, 0, 6) == "GIF87a" || Encoding.ASCII.GetString(bytes, 0, 6) == "GIF89a") :
                mime == "image/webp" && bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP";
            if (!valid) throw new MediaException("invalid_media_signature");
        }
        return new MediaContent(bytes, mime);
    }
}
