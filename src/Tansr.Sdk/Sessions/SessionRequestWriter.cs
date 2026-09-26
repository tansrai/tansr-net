using System;
using System.Linq;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Sessions;

internal static class SessionRequestWriter
{
    internal static byte[] Create(CreateSessionOptions options, SessionContract contract)
    {
        if (options.ResumeSessionId is not null && (options.ForkSessionId is not null || options.ForkCheckpointId is not null)) throw new ArgumentException("resume and fork are mutually exclusive.", nameof(options));
        if ((options.ForkSessionId is null) != (options.ForkCheckpointId is null)) throw new ArgumentException("Both fork identifiers are required.", nameof(options));
        if (contract == SessionContract.Sdk1 && options.RequestId is not null) throw new ArgumentException("requestId belongs to SDK2.", nameof(options));
        if (contract == SessionContract.Sdk2OffloadV1)
        {
            if (options.ForkSessionId is not null) throw new TansrProtocolException("unsupported_capability");
            if (options.ResumeSessionId is null) Identifier(options.RequestId, "requestId");
        }
        if (options.MaxTokens.HasValue && (options.MaxTokens <= 0 || options.MaxTokens > SessionJson.SafeInteger) ||
            options.ThinkingBudget.HasValue && (options.ThinkingBudget <= 0 || options.ThinkingBudget > SessionJson.SafeInteger) || options.MaxUsd <= 0)
            throw new ArgumentException("Budgets must be positive and representable.", nameof(options));
        if (options.ClientTools.HasValue && options.ClientTools.Value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("clientTools must be an array.", nameof(options));
        if (options.Labels?.Count > 16) throw new ArgumentException("At most 16 labels are allowed.", nameof(options));
        if (options.EndUserId is not null)
        {
            if (options.EndUserId.Length < 1 || options.EndUserId.Length > 128 || options.EndUserId.Any(c => c < 33 || c > 126))
                throw new ArgumentException("Invalid endUserId.", nameof(options));
        }
        return SessionJson.Object(w =>
        {
            SessionJson.Optional(w, "requestId", options.RequestId);
            SessionJson.Optional(w, "model", options.Model); SessionJson.Optional(w, "prompt", options.Prompt);
            SessionJson.Optional(w, "profile", options.Profile); SessionJson.Optional(w, "capabilitiesProfile", options.CapabilitiesProfile);
            SessionJson.Optional(w, "cwd", options.Cwd);
            if (options.EndUserId is not null) { w.WriteStartObject("endUser"); w.WriteString("id", options.EndUserId); w.WriteEndObject(); }
            if (options.Labels is not null)
            {
                w.WriteStartObject("labels");
                foreach (var pair in options.Labels)
                {
                    if (pair.Key is null || pair.Key.Length > 64 || pair.Value is null || pair.Value.Length > 256) throw new ArgumentException("Invalid label.", nameof(options));
                    SessionJson.Unicode(pair.Key); SessionJson.Unicode(pair.Value); w.WriteString(pair.Key, pair.Value);
                }
                w.WriteEndObject();
            }
            if (options.ResumeSessionId is not null)
            { SessionJson.Segment(options.ResumeSessionId); w.WriteStartObject("resume"); w.WriteString("sessionId", options.ResumeSessionId); w.WriteEndObject(); }
            if (options.ForkSessionId is not null)
            { SessionJson.Segment(options.ForkSessionId); SessionJson.Segment(options.ForkCheckpointId!); w.WriteStartObject("fork"); w.WriteString("sessionId", options.ForkSessionId); w.WriteString("checkpointId", options.ForkCheckpointId); w.WriteEndObject(); }
            if (options.MaxUsd.HasValue || options.MaxTokens.HasValue)
            {
                w.WriteStartObject("budget");
                if (options.MaxUsd.HasValue) w.WriteNumber("maxUsd", options.MaxUsd.Value);
                if (options.MaxTokens.HasValue) w.WriteNumber("maxTokens", options.MaxTokens.Value);
                w.WriteEndObject();
            }
            if (options.ThinkingBudget.HasValue) { w.WriteStartObject("thinking"); w.WriteNumber("budget", options.ThinkingBudget.Value); w.WriteEndObject(); }
            if (options.Tools is not null)
            {
                w.WriteStartArray("tools");
                foreach (var tool in options.Tools) { SessionJson.Text(tool, 512, "tool"); w.WriteStringValue(tool); }
                w.WriteEndArray();
            }
            if (options.ClientTools.HasValue) { w.WritePropertyName("clientTools"); options.ClientTools.Value.WriteTo(w); }
        });
    }

    internal static CreateSessionOptions Copy(CreateSessionOptions? value) => value is null ? new CreateSessionOptions() : new CreateSessionOptions
    {
        Model = value.Model,
        Prompt = value.Prompt,
        Profile = value.Profile,
        Labels = value.Labels,
        EndUserId = value.EndUserId,
        CapabilitiesProfile = value.CapabilitiesProfile,
        ResumeSessionId = value.ResumeSessionId,
        ForkSessionId = value.ForkSessionId,
        ForkCheckpointId = value.ForkCheckpointId,
        RequestId = value.RequestId,
        Cwd = value.Cwd,
        ThinkingBudget = value.ThinkingBudget,
        MaxUsd = value.MaxUsd,
        MaxTokens = value.MaxTokens,
        Tools = value.Tools,
        ClientTools = value.ClientTools
    };

    internal static void Identifier(string? value, string name)
    {
        if (string.IsNullOrEmpty(value) || value!.Length > 128) throw new ArgumentException("Invalid " + name + ".", name);
        foreach (var c in value) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-'))
            throw new ArgumentException("Invalid " + name + ".", name);
    }

    internal static void Blocks(Utf8JsonWriter w, System.Collections.Generic.IReadOnlyList<MessageBlock> blocks)
    {
        if (blocks is null || blocks.Count < 1 || blocks.Count > 64) throw new ArgumentException("Invalid message blocks.", nameof(blocks));
        w.WriteStartArray();
        foreach (var block in blocks)
        {
            if (block is null || string.IsNullOrEmpty(block.Value)) throw new ArgumentException("Invalid message block.", nameof(blocks));
            SessionJson.Unicode(block.Value);
            w.WriteStartObject(); w.WriteString("t", block.Type);
            if (block.Type == "text")
            { if (block.Value.Length > 262144) throw new ArgumentException("Text block is too large.", nameof(blocks)); w.WriteString("text", block.Value); }
            else
            {
                if (block.Mime != "image/png" && block.Mime != "image/jpeg" && block.Mime != "image/gif" && block.Mime != "image/webp")
                    throw new ArgumentException("Unsupported image mime.", nameof(blocks));
                w.WriteString("mime", block.Mime); w.WriteString("data", block.Value);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
