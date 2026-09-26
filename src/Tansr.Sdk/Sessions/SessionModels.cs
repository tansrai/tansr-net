using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Tansr.Sdk.Sessions;

public sealed class CreateSessionOptions
{
    public string? Model { get; set; }
    public string? Prompt { get; set; }
    public string? Profile { get; set; }
    public string? CapabilitiesProfile { get; set; }
    public string? ResumeSessionId { get; set; }
    public string? ForkSessionId { get; set; }
    public string? ForkCheckpointId { get; set; }
    /// <summary>新合同首次创建的幂等键；调用方在结果未知时保留原键及原请求。</summary>
    public string? RequestId { get; set; }
    public string? Cwd { get; set; }
    public long? ThinkingBudget { get; set; }
    public decimal? MaxUsd { get; set; }
    public long? MaxTokens { get; set; }
    public IReadOnlyList<string>? Tools { get; set; }
    /// <summary>冻结的 clientTools 声明数组；只声明数据，不上传执行代码。</summary>
    public JsonElement? ClientTools { get; set; }
}

public sealed class MessageBlock
{
    private MessageBlock(string type, string value, string? mime) { Type = type; Value = value; Mime = mime; }
    public string Type { get; }
    public string Value { get; }
    public string? Mime { get; }
    public static MessageBlock Text(string text) => new MessageBlock("text", text, null);
    /// <summary>data 遵循冻结 image 消息合同；SDK 不下载任意 URL 或推断 MIME。</summary>
    public static MessageBlock Image(string mime, string data) => new MessageBlock("image", data, mime);
}

public sealed class SessionInputTarget
{
    public SessionInputTarget(string historyEpoch, string turnId) { HistoryEpoch = historyEpoch; TurnId = turnId; }
    public string HistoryEpoch { get; }
    public string TurnId { get; }
}

public sealed class QuestionAnswer
{
    public QuestionAnswer(string questionId, IReadOnlyList<string> selectedOptionIds, string? freeText = null)
    { QuestionId = questionId; SelectedOptionIds = selectedOptionIds; FreeText = freeText; }
    public string QuestionId { get; }
    public IReadOnlyList<string> SelectedOptionIds { get; }
    public string? FreeText { get; }
}

public sealed class CompactOptions
{
    public string? Instructions { get; set; }
    public bool? Checkpoint { get; set; }
    public string? CheckpointLabel { get; set; }
}

public sealed class SpeechTranscriptionOptions
{
    public string Audio { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? Language { get; set; }
    public bool? Diarize { get; set; }
    public string? Prompt { get; set; }
}

public sealed class SpeechOptions
{
    public string Input { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? Voice { get; set; }
    public string? Format { get; set; }
    public double? Speed { get; set; }
}

public sealed class AgentEvent
{
    public AgentEvent(string name, string? id, JsonElement data)
    { Name = name ?? throw new ArgumentNullException(nameof(name)); Id = id; Data = data.Clone(); }
    public string Name { get; }
    public string? Id { get; }
    public JsonElement Data { get; }
}

public sealed class EventStreamOptions
{
    public string? LastEventId { get; set; }
    /// <summary>缺口默认显式终止；调用方先修复投影/历史，再持新游标订阅。</summary>
    public bool StopOnGap { get; set; } = true;
    /// <summary>仅重连只读观察连接，从不自动重发消息、审批或工具回执。</summary>
    public bool Reconnect { get; set; } = true;
}
