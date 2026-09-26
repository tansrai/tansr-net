using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Sessions;

public enum SessionStatus { Idle, Running, Ended, Unknown }

/// <summary>冻结元信息的只读投影；未知加法字段保留在 Raw。没有更改 model/thinking 等配置的隐式写接口。</summary>
public sealed class SessionMetadata
{
    internal SessionMetadata(JsonElement raw)
    {
        Raw = raw.Clone(); Id = SessionJson.String(raw, "sessionId"); LastSequence = SessionJson.LastSequence(raw);
        EndUserId = SessionJson.String(raw, "endUserId"); StatusName = SessionJson.String(raw, "status");
        Status = StatusName == "idle" ? SessionStatus.Idle : StatusName == "running" ? SessionStatus.Running : StatusName == "ended" ? SessionStatus.Ended : SessionStatus.Unknown;
        if (!raw.TryGetProperty("live", out var live) || live.ValueKind != JsonValueKind.True && live.ValueKind != JsonValueKind.False)
            throw new TansrProtocolException("invalid_response");
        IsLive = live.GetBoolean(); CreatedAt = SessionJson.String(raw, "createdAt"); LastActivityAt = SessionJson.String(raw, "lastActivityAt");
        if (raw.TryGetProperty("title", out var title))
        {
            if (title.ValueKind != JsonValueKind.String) throw new TansrProtocolException("invalid_response");
            Title = title.GetString();
        }
        Context = Optional(raw, "context"); Media = Optional(raw, "media"); HistoryOrigin = Optional(raw, "historyOrigin");
        ApplicationPrompt = ApplicationPromptState.FromMetadata(raw);
    }
    private static JsonElement? Optional(JsonElement raw, string key) => raw.TryGetProperty(key, out var value) ? value.Clone() : (JsonElement?)null;
    public string Id { get; }
    public string EndUserId { get; }
    public SessionStatus Status { get; }
    public string StatusName { get; }
    public bool IsLive { get; }
    public long LastSequence { get; }
    public string CreatedAt { get; }
    public string LastActivityAt { get; }
    public string? Title { get; }
    public JsonElement? Context { get; }
    public JsonElement? Media { get; }
    public JsonElement? HistoryOrigin { get; }
    /// <summary>仅对合法活动会话来源字段给出已知状态；缺席、失活或非法字段均为 Unknown。</summary>
    public ApplicationPromptState ApplicationPrompt { get; }
    public JsonElement Raw { get; }
}

public sealed class SessionPage
{
    internal SessionPage(IList<SessionMetadata> sessions, long total)
    { Sessions = new ReadOnlyCollection<SessionMetadata>(sessions); Total = total; }
    public IReadOnlyList<SessionMetadata> Sessions { get; }
    public long Total { get; }
}

/// <summary>上下文快照原字节；不等价于完整历史、记忆或运行恢复档案。Bytes 每次返回副本。</summary>
public sealed class SessionExport
{
    private readonly byte[] bytes;
    internal SessionExport(string checkpointId, byte[] value) { CheckpointId = checkpointId; bytes = (byte[])value.Clone(); }
    public string CheckpointId { get; }
    public byte[] Bytes => (byte[])bytes.Clone();
}
