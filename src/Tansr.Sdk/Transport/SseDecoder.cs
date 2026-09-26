using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Transport;

/// <summary>逐字节线性扫描；换行字节不可能出现在 UTF-8 多字节编码内部。</summary>
internal sealed class SseDecoder : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly MemoryStream line = new MemoryStream();
    private readonly int maxFrameBytes;
    private readonly List<string> data = new List<string>();
    private int frameBytes;
    private bool skipLf;
    private bool firstLine = true;
    private string? eventName;
    private string? id;

    internal SseDecoder(int maxFrameBytes) { this.maxFrameBytes = maxFrameBytes; }

    internal IReadOnlyList<SseFrame> Feed(byte[] bytes, int count)
    {
        var frames = new List<SseFrame>();
        for (int i = 0; i < count; i++)
        {
            var b = bytes[i];
            if (skipLf) { skipLf = false; if (b == 10) continue; }
            if (++frameBytes > maxFrameBytes) throw new TansrProtocolException("sse_frame_too_large");
            if (b == 10 || b == 13) { ReadLine(frames); skipLf = b == 13; }
            else line.WriteByte(b);
        }
        return frames;
    }

    private void ReadLine(List<SseFrame> frames)
    {
        string text;
        try { text = Utf8.GetString(line.ToArray()); }
        catch (DecoderFallbackException) { throw new TansrProtocolException("invalid_utf8"); }
        line.SetLength(0);
        if (firstLine) { firstLine = false; if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1); }
        if (text.Length == 0)
        {
            if (data.Count > 0) frames.Add(new SseFrame(eventName, id, string.Join("\n", data)));
            data.Clear(); eventName = null; id = null; frameBytes = 0; return;
        }
        if (text[0] == ':') return;
        int colon = text.IndexOf(':');
        string field = colon < 0 ? text : text.Substring(0, colon);
        string value = colon < 0 ? string.Empty : text.Substring(colon + 1);
        if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
        if (field == "data") data.Add(value);
        else if (field == "event") eventName = value;
        else if (field == "id")
        {
            if (value.IndexOf('\0') >= 0) throw new TansrProtocolException("invalid_event_id");
            id = value;
        }
    }

    internal void Complete()
    {
        if (line.Length > 0 || data.Count > 0) throw new TansrProtocolException("sse_incomplete_frame");
    }

    public void Dispose() { line.Dispose(); }
}

internal sealed class SseFrame
{
    internal SseFrame(string? name, string? id, string data) { Name = name; Id = id; Data = data; }
    internal string? Name { get; }
    internal string? Id { get; }
    internal string Data { get; }
}
