using System.Text.Json;

namespace Tansr.Examples;

// 只读浏览单页；不作为完整历史快照、模型上下文或事件恢复证明。
internal sealed class ExampleHistoryPager
{
    internal const int RequestedPageSize = 50;
    private readonly string sessionId;
    private readonly Func<int, int, CancellationToken, Task<JsonElement>> read;
    private readonly Func<bool> isCurrent;
    private bool busy;
    private long sequence;
    internal JsonElement? Page { get; private set; }
    internal int Offset { get; private set; }
    internal int NextOffset { get; private set; }
    internal int Total { get; private set; }
    internal bool Invalidated { get; private set; }
    internal bool HasNext => Page.HasValue && !Invalidated && NextOffset < Total;

    internal ExampleHistoryPager(string sessionId, Func<int, int, CancellationToken, Task<JsonElement>> read, Func<bool> isCurrent)
    { this.sessionId = sessionId; this.read = read; this.isCurrent = isCurrent; }

    internal Task FirstAsync(CancellationToken cancellationToken) => ReadAsync(true, cancellationToken);
    internal Task NextAsync(CancellationToken cancellationToken) => ReadAsync(false, cancellationToken);

    private async Task ReadAsync(bool first, CancellationToken cancellationToken)
    {
        EnsureCurrent(cancellationToken);
        if (busy) throw new InvalidOperationException("history_page_read_in_progress");
        if (!first && !HasNext) throw new InvalidOperationException("history_page_not_available");
        busy = true;
        try
        {
            int requestedOffset = first ? 0 : NextOffset;
            // 当前呈现检查可能更新宿主界面；保留示例调用方的同步上下文。
            var page = await read(requestedOffset, RequestedPageSize, cancellationToken);
            EnsureCurrent(cancellationToken);
            if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("sessionId", out var id) ||
                id.ValueKind != JsonValueKind.String || id.GetString() != sessionId ||
                !page.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array ||
                !Number(page, "offset", out var offset) || !Number(page, "nextOffset", out var next) || !Number(page, "total", out var total) ||
                !page.TryGetProperty("lastSeq", out var lastSeq) || lastSeq.ValueKind != JsonValueKind.Number ||
                !lastSeq.TryGetInt64(out var currentSequence) || currentSequence < -1 || currentSequence > 9007199254740991L)
                throw new InvalidOperationException("invalid_history_page");

            // 页号属于治理后的原始下标；配对可扩大页面，出流过滤可令可见消息为空。
            if (offset > Math.Min(requestedOffset, total) || next < offset || next > total ||
                requestedOffset < total && next <= requestedOffset || requestedOffset >= total && (offset != total || next != total) ||
                messages.GetArrayLength() > next - offset)
                throw new InvalidOperationException("invalid_history_page");
            if (!first && (currentSequence != sequence || total != Total))
            {
                Invalidated = true;
                throw new InvalidOperationException("history_page_changed");
            }
            Page = page.Clone(); Offset = offset; NextOffset = next; Total = total; sequence = currentSequence; Invalidated = false;
        }
        finally { busy = false; }
    }

    private void EnsureCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new OperationCanceledException("history_presentation_replaced", token);
    }
    private static bool Number(JsonElement value, string name, out int result)
    {
        result = 0;
        return value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out result) && result >= 0;
    }
}
