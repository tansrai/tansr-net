using System.Text.Json;

namespace Tansr.Sdk.Archive;

/// <summary>原 strict 档案事件源；只使用调用方原 cursor，不替代业务会话 SSE 或创建新绑定。</summary>
public interface IArchiveEventSource
{
    Task<string?> ConsumeEventsAsync(string bindingId, JsonElement generations, Func<JsonElement, CancellationToken, Task> onFrame,
        string? lastEventId = null, CancellationToken cancellationToken = default);
}

/// <summary>在 strict SSE 响应头和当前 scope 验证后通知就绪；旧单次事件源无需实现。</summary>
public interface IArchiveConnectionSource : IArchiveEventSource
{
    Task<string?> ConsumeConnectedEventsAsync(string bindingId, JsonElement generations, Func<JsonElement, CancellationToken, Task> onFrame,
        Func<CancellationToken, Task> onConnected, string? lastEventId = null, CancellationToken cancellationToken = default);
}
