using System.Text.Json;

namespace Tansr.Sdk.Terminal;

/// <summary>Per-operation output projection. Preserve this object across SSE reconnects to
/// preserve split UTF-8 characters and duplicate detection. It does not determine execution success.</summary>
public sealed class TerminalOutputView : IDisposable
{
    private readonly TerminalOutputObserver observer;
    public TerminalOutputView(JsonElement operationReference) { observer = new TerminalOutputObserver(operationReference); }
    public long? LastSequence => observer.LastSequence;
    public bool HasPresentationGap => observer.HasPresentationGap;
    public bool SealVerified => observer.SealVerified;
    public JsonElement? LastStatus => observer.LastStatus?.Raw;
    public IReadOnlyList<TerminalOutputSegment> ApplyEvent(JsonElement value) =>
        observer.ApplyEvent(value).Select(x => new TerminalOutputSegment(x)).ToArray();
    public void NoticeGap() => observer.NoticeGap();
    public void Dispose() => observer.Dispose();
}

public sealed class TerminalOutputSegment
{
    private readonly byte[] bytes;
    internal TerminalOutputSegment(TerminalOutputPiece value)
    { Sequence = value.Sequence; Channel = value.Channel; Encoding = value.Encoding; bytes = value.Bytes; Text = value.Text; PresentationGap = value.PresentationGap; }
    public long? Sequence { get; }
    public string Channel { get; }
    public string Encoding { get; }
    public byte[] Bytes => (byte[])bytes.Clone();
    public string? Text { get; }
    public bool PresentationGap { get; }
}
