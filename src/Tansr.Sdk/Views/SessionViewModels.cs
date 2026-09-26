using System.Collections.ObjectModel;
using System.Text.Json;

namespace Tansr.Sdk.Views;

public enum TextDeliveryMode { Stream, Final }
public enum ThinkingDeliveryMode { Stream, Final, Off }

/// <summary>只控制呈现，不改变模型是否生成思考。限制为单个视图的内存上界，不是历史保留政策。</summary>
public sealed class SessionViewOptions
{
    public TextDeliveryMode TextDelivery { get; set; } = TextDeliveryMode.Stream;
    public ThinkingDeliveryMode ThinkingDelivery { get; set; } = ThinkingDeliveryMode.Stream;
    public int MaximumMessages { get; set; } = 256;
    public int MaximumPartsPerMessage { get; set; } = 256;
    public int MaximumTextCharacters { get; set; } = 65536;
    public int MaximumOutputCharacters { get; set; } = 8192;
    /// <summary>已消费的会话事件水位；-1 表示 Serve 空日志哨兵，事件本身仍须从 0 开始。</summary>
    public long? InitialSequence { get; set; }
}

public sealed class MessagePartView
{
    internal MessagePartView(string kind, string text, string? toolId, string? toolInstanceId, bool truncated)
    { Kind = kind; Text = text; ToolId = toolId; ToolInstanceId = toolInstanceId; Truncated = truncated; }
    public string Kind { get; }
    public string Text { get; }
    public string? ToolId { get; }
    public string? ToolInstanceId { get; }
    public bool Truncated { get; }
}

public sealed class MessageView
{
    internal MessageView(string id, string role, IList<MessagePartView> parts)
    { Id = id; Role = role; Parts = new ReadOnlyCollection<MessagePartView>(parts); }
    public string Id { get; }
    public string Role { get; }
    public IReadOnlyList<MessagePartView> Parts { get; }
}

public sealed class ToolView
{
    internal ToolView(string id, string instanceId, string name, string status, string output, string? progress,
        string? resultText, JsonElement? result, string? error, bool truncated)
    {
        Id = id; InstanceId = instanceId; Name = name; Status = status; OutputTail = output; Progress = progress;
        ResultText = resultText; Result = result; Error = error; OutputTruncated = truncated;
    }
    public string Id { get; }
    /// <summary>视图中的工具发生实例标识；Id 仍保留原始 toolCallId。</summary>
    public string InstanceId { get; }
    public string Name { get; }
    public string Status { get; }
    public string OutputTail { get; }
    public string? Progress { get; }
    public string? ResultText { get; }
    public JsonElement? Result { get; }
    public string? Error { get; }
    public bool OutputTruncated { get; }
    public bool IsActive => Status == "proposed" || Status == "running" || Status == "awaiting_permission";
}

public sealed class SessionNoticeView
{
    internal SessionNoticeView(string code, string? message, bool isError, JsonElement? detail = null)
    { Code = code; Message = message; IsError = isError; Detail = detail; }
    public string Code { get; }
    public string? Message { get; }
    public bool IsError { get; }
    public JsonElement? Detail { get; }
}

public sealed class SessionRequestView
{
    internal SessionRequestView(string kind, string id, JsonElement data)
    { Kind = kind; Id = id; Data = data; }
    public string Kind { get; }
    public string Id { get; }
    /// <summary>服务端请求原文；审批回执必须使用此请求的 digest。过期以服务端判断为准。</summary>
    public JsonElement Data { get; }
}

public sealed class TodoView
{
    internal TodoView(string id, string content, string status) { Id = id; Content = content; Status = status; }
    public string Id { get; }
    public string Content { get; }
    public string Status { get; }
}

public sealed class AgentProgressView
{
    internal AgentProgressView(string id, string status, string? message) { Id = id; Status = status; Message = message; }
    public string Id { get; }
    public string Status { get; }
    public string? Message { get; }
}

/// <summary>不可变视图快照。用量为服务事件的四路独立 token 总和，不是当前上下文占用。</summary>
public sealed class SessionViewSnapshot
{
    internal SessionViewSnapshot(long version, long? sequence, string status, IList<MessageView> messages,
        IList<ToolView> tools, IList<SessionNoticeView> notices, IList<SessionRequestView> requests,
        IList<TodoView> todos, IList<AgentProgressView> agents, long turnTokens, long sessionTokens,
        long usageRequests, bool hasGap, bool truncated, SessionNoticeView? error)
    {
        Version = version; LastSequence = sequence; Status = status;
        Messages = new ReadOnlyCollection<MessageView>(messages); Tools = new ReadOnlyCollection<ToolView>(tools);
        Notices = new ReadOnlyCollection<SessionNoticeView>(notices); PendingRequests = new ReadOnlyCollection<SessionRequestView>(requests);
        Todos = new ReadOnlyCollection<TodoView>(todos); Agents = new ReadOnlyCollection<AgentProgressView>(agents);
        TurnTokens = turnTokens; SessionTokens = sessionTokens; UsageRequests = usageRequests;
        HasEventGap = hasGap; PresentationTruncated = truncated; LastError = error;
    }
    public long Version { get; }
    public long? LastSequence { get; }
    public string Status { get; }
    public IReadOnlyList<MessageView> Messages { get; }
    public IReadOnlyList<ToolView> Tools { get; }
    public IReadOnlyList<SessionNoticeView> Notices { get; }
    public IReadOnlyList<SessionRequestView> PendingRequests { get; }
    public IReadOnlyList<TodoView> Todos { get; }
    public IReadOnlyList<AgentProgressView> Agents { get; }
    public long TurnTokens { get; }
    public long SessionTokens { get; }
    public long UsageRequests { get; }
    /// <summary>有事件缺口后持续为 true；需由宿主从权威历史重建新视图，不能伪装自动补齐。</summary>
    public bool HasEventGap { get; }
    public bool PresentationTruncated { get; }
    public SessionNoticeView? LastError { get; }
}
