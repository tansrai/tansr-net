using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Sessions;

/// <summary>远端会话引用。观察连接取消不会调用 interrupt；显式 CancelAsync 才中断当前轮。</summary>
public sealed partial class AgentSession
{
    private readonly TansrClient client;
    private long lastSequence;
    internal AgentSession(TansrClient client, string id, long lastSequence, bool resumed)
    { this.client = client; Id = id; this.lastSequence = lastSequence; Resumed = resumed; }
    public string Id { get; }
    public bool Resumed { get; }
    public long LastSequence => Interlocked.Read(ref lastSequence);
    internal void ObserveSequence(long value)
    {
        long previous;
        do { previous = Interlocked.Read(ref lastSequence); if (value <= previous) return; }
        while (Interlocked.CompareExchange(ref lastSequence, value, previous) != previous);
    }
    private string Path => client.SessionPath(Id);
    private Task<JsonElement> Post(string suffix, byte[]? body, CancellationToken ct, int limit = 2 * 1024 * 1024, InputErrorMode inputErrorMode = InputErrorMode.None)
        => client.SendSessionAsync(HttpMethod.Post, Path + suffix, body ?? SessionJson.Object(), ct, limit, inputErrorMode);

    public Task<JsonElement> SendAsync(string prompt, CancellationToken cancellationToken = default)
    {
        client.AssertSessionWritable(Id);
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt must not be blank.", nameof(prompt));
        SessionJson.Unicode(prompt);
        return Post("/messages", SessionJson.Object(w => w.WriteString("prompt", prompt)), cancellationToken, 20 * 1024 * 1024);
    }

    public Task<JsonElement> SendBlocksAsync(IReadOnlyList<MessageBlock> blocks, CancellationToken cancellationToken = default)
    {
        client.AssertSessionWritable(Id);
        return Post("/messages", SessionJson.Object(w => { w.WritePropertyName("blocks"); SessionRequestWriter.Blocks(w, blocks); }), cancellationToken, 20 * 1024 * 1024);
    }

    public async Task<JsonElement> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var value = await client.SendSessionAsync(HttpMethod.Get, Path, null, cancellationToken).ConfigureAwait(false);
        client.VerifyFamily(value);
        if (SessionJson.String(value, "sessionId") != Id) throw new TansrProtocolException("invalid_response");
        return value;
    }

    public Task<JsonElement> GetHistoryAsync(CancellationToken cancellationToken = default)
        => client.SendSessionAsync(HttpMethod.Get, Path + "/history", null, cancellationToken);

    public Task<JsonElement> GetHistoryAsync(int? offset, int? limit, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var query = new List<string>();
        if (offset.HasValue) query.Add("offset=" + offset.Value.ToString(CultureInfo.InvariantCulture));
        if (limit.HasValue) query.Add("limit=" + limit.Value.ToString(CultureInfo.InvariantCulture));
        return client.SendSessionAsync(HttpMethod.Get, Path + "/history" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty), null, cancellationToken);
    }

    public Task<JsonElement> CancelAsync(CancellationToken cancellationToken = default) => Post("/interrupt", null, cancellationToken);
    public Task<JsonElement> CloseAsync(CancellationToken cancellationToken = default)
        => client.SendSessionAsync(HttpMethod.Delete, Path, null, cancellationToken);

    public Task<JsonElement> GetInputCapabilitiesAsync(CancellationToken cancellationToken = default)
        => client.SendSessionAsync(HttpMethod.Get, Path + "/input-capabilities", null, cancellationToken);

    public Task<JsonElement> SubmitInputAsync(string inputId, SessionInputTarget target, string text, bool durable = false, CancellationToken cancellationToken = default)
    {
        SessionJson.Text(inputId, 128, nameof(inputId)); ValidateTarget(target);
        if (string.IsNullOrEmpty(text) || text.Length > 262144) throw new ArgumentException("Invalid input text.", nameof(text));
        SessionJson.Unicode(text);
        return Post("/inputs", SessionJson.Object(w =>
        {
            w.WriteString("inputId", inputId); w.WriteStartObject("target");
            w.WriteString("historyEpoch", target.HistoryEpoch); w.WriteString("turnId", target.TurnId); w.WriteEndObject();
            w.WriteStartObject("content"); w.WriteString("text", text); w.WriteEndObject(); w.WriteString("ack", durable ? "durable" : "memory");
        }), cancellationToken, 20 * 1024 * 1024, InputErrorMode.Submit);
    }

    /// <summary>冻结插入协议只接受文本块；图片仍使用空闲会话 messages blocks。</summary>
    public Task<JsonElement> SubmitInputBlocksAsync(string inputId, SessionInputTarget target, IReadOnlyList<string> textBlocks,
        bool durable = false, CancellationToken cancellationToken = default)
    {
        SessionJson.Text(inputId, 128, nameof(inputId)); ValidateTarget(target);
        if (textBlocks is null || textBlocks.Count < 1 || textBlocks.Count > 64) throw new ArgumentException("Invalid input blocks.", nameof(textBlocks));
        return Post("/inputs", SessionJson.Object(w =>
        {
            w.WriteString("inputId", inputId); w.WriteStartObject("target");
            w.WriteString("historyEpoch", target.HistoryEpoch); w.WriteString("turnId", target.TurnId); w.WriteEndObject();
            w.WriteStartObject("content"); w.WriteStartArray("blocks");
            foreach (var text in textBlocks)
            {
                if (string.IsNullOrEmpty(text) || text.Length > 262144) throw new ArgumentException("Invalid input text.", nameof(textBlocks));
                SessionJson.Unicode(text); w.WriteStartObject(); w.WriteString("t", "text"); w.WriteString("text", text); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject(); w.WriteString("ack", durable ? "durable" : "memory");
        }), cancellationToken, 20 * 1024 * 1024, InputErrorMode.Submit);
    }

    private static void ValidateTarget(SessionInputTarget target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        SessionJson.Text(target.HistoryEpoch, 128, "historyEpoch"); SessionJson.Text(target.TurnId, 128, "turnId");
    }

    public Task<JsonElement> GetInputStatusAsync(string inputId, SessionInputTarget target, CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        return client.SendSessionAsync(HttpMethod.Get, Path + "/inputs/" + SessionJson.Segment(inputId) + "?historyEpoch=" + SessionJson.Segment(target.HistoryEpoch) + "&turnId=" + SessionJson.Segment(target.TurnId), null, cancellationToken, inputErrorMode: InputErrorMode.Status);
    }

    public Task<JsonElement> PermissionAsync(string requestId, string digest, bool allow, CancellationToken cancellationToken = default)
        => Permission(requestId, digest, allow, null, cancellationToken);

    public Task<JsonElement> DenyExpiredPermissionAsync(string requestId, string digest, CancellationToken cancellationToken = default)
        => Permission(requestId, digest, false, "expired_on_arrival", cancellationToken);

    private Task<JsonElement> Permission(string requestId, string digest, bool allow, string? reason, CancellationToken ct)
    {
        SessionJson.Text(digest, 4096, nameof(digest));
        return Post("/permission/" + SessionJson.Segment(requestId), SessionJson.Object(w =>
        { w.WriteString("digest", digest); w.WriteString("verdict", allow ? "allow" : "deny"); SessionJson.Optional(w, "reason", reason); }), ct);
    }

    public Task<JsonElement> AnswerAsync(string requestId, IReadOnlyList<QuestionAnswer> answers, CancellationToken cancellationToken = default)
    {
        if (answers is null || answers.Count == 0) throw new ArgumentException("Answers are required.", nameof(answers));
        return Post("/questions/" + SessionJson.Segment(requestId), SessionJson.Object(w =>
        {
            w.WriteStartArray("answers");
            foreach (var answer in answers)
            {
                if (answer is null || answer.SelectedOptionIds is null || answer.FreeText?.Length > 16384) throw new ArgumentException("Invalid answer.", nameof(answers));
                SessionJson.Text(answer.QuestionId, 512, "questionId");
                w.WriteStartObject(); w.WriteString("questionId", answer.QuestionId); w.WriteStartArray("selectedOptionIds");
                foreach (var option in answer.SelectedOptionIds) { SessionJson.Text(option, 512, "optionId", true); w.WriteStringValue(option); }
                w.WriteEndArray(); SessionJson.Optional(w, "freeText", answer.FreeText); w.WriteEndObject();
            }
            w.WriteEndArray();
        }), cancellationToken);
    }

    public Task<JsonElement> SubmitToolResultAsync(string callId, JsonElement receipt, CancellationToken cancellationToken = default)
    {
        if (receipt.ValueKind != JsonValueKind.Object || !receipt.TryGetProperty("status", out var status)) throw new ArgumentException("Invalid tool receipt.", nameof(receipt));
        var state = status.ValueKind == JsonValueKind.String ? status.GetString() : null;
        if (state == "ok")
        {
            if (!receipt.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() < 1 || content.GetArrayLength() > 64)
                throw new ArgumentException("Invalid tool content.", nameof(receipt));
        }
        else if (state == "error")
        {
            if (!receipt.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(message.GetString()) || message.GetString()!.Length > 4096)
                throw new ArgumentException("Invalid tool error.", nameof(receipt));
        }
        else throw new ArgumentException("Invalid tool status.", nameof(receipt));
        return Post("/tool-results/" + SessionJson.Segment(callId), SessionJson.Write(receipt.WriteTo), cancellationToken, 20 * 1024 * 1024);
    }

    public Task<JsonElement> CompactAsync(CompactOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options?.Instructions?.Length > 4096 || options?.CheckpointLabel?.Length > 120 || options?.Checkpoint == false && options.CheckpointLabel is not null)
            throw new ArgumentException("Invalid compact options.", nameof(options));
        return Post("/compact", SessionJson.Object(w =>
        {
            SessionJson.Optional(w, "instructions", options?.Instructions);
            if (options?.CheckpointLabel is not null) { w.WriteStartObject("checkpoint"); w.WriteString("label", options.CheckpointLabel); w.WriteEndObject(); }
            else if (options?.Checkpoint is bool enabled) w.WriteBoolean("checkpoint", enabled);
        }), cancellationToken);
    }

    public Task<JsonElement> CheckpointAsync(string? label = null, CancellationToken cancellationToken = default)
    {
        if (label?.Length > 120) throw new ArgumentException("Checkpoint label is too long.", nameof(label));
        if (label is not null) SessionJson.Unicode(label);
        return Post("/checkpoints", SessionJson.Object(w => SessionJson.Optional(w, "label", label)), cancellationToken);
    }
    public Task<JsonElement> ListCheckpointsAsync(CancellationToken cancellationToken = default)
        => client.SendSessionAsync(HttpMethod.Get, Path + "/checkpoints", null, cancellationToken);
    public Task<JsonElement> RestoreCheckpointAsync(string id, bool? checkpoint = null, CancellationToken cancellationToken = default)
        => Post("/checkpoints/" + SessionJson.Segment(id) + "/restore", SessionJson.Object(w => { if (checkpoint.HasValue) w.WriteBoolean("checkpoint", checkpoint.Value); }), cancellationToken);
    public Task<JsonElement> DeleteCheckpointAsync(string id, CancellationToken cancellationToken = default)
        => client.SendSessionAsync(HttpMethod.Delete, Path + "/checkpoints/" + SessionJson.Segment(id), null, cancellationToken);
    public Task<byte[]> ExportCheckpointAsync(string id, CancellationToken cancellationToken = default)
        => client.SendBinaryAsync(HttpMethod.Get, Path + "/checkpoints/" + SessionJson.Segment(id) + "/export", null, cancellationToken);
    public async Task<JsonElement> ImportCheckpointAsync(byte[] bytes, string? label = null, CancellationToken cancellationToken = default)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        if (label?.Length > 120) throw new ArgumentException("Checkpoint label is too long.", nameof(label));
        if (label is not null) SessionJson.Unicode(label);
        var copy = (byte[])bytes.Clone();
        var value = await client.SendBinaryAsync(HttpMethod.Post, Path + "/checkpoints/import" + (label is null ? string.Empty : "?label=" + Uri.EscapeDataString(label)), copy, cancellationToken).ConfigureAwait(false);
        return SessionJson.Parse(value);
    }
    public Task<JsonElement> SetCwdAsync(string cwd, CancellationToken cancellationToken = default)
    {
        SessionJson.Text(cwd, 32768, nameof(cwd));
        return Post("/cwd", SessionJson.Object(w => w.WriteString("cwd", cwd)), cancellationToken);
    }

    public Task<JsonElement> TranscribeAsync(SpeechTranscriptionOptions options, CancellationToken cancellationToken = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrEmpty(options.Audio) || options.Prompt?.Length > 2048 || options.Language?.Length > 16 || options.Language?.Length < 2)
            throw new ArgumentException("Invalid transcription options.", nameof(options));
        SessionJson.Unicode(options.Audio);
        return Post("/audio/transcriptions", SessionJson.Object(w =>
        {
            w.WriteString("audio", options.Audio); SessionJson.Optional(w, "model", options.Model);
            SessionJson.Optional(w, "language", options.Language); SessionJson.Optional(w, "prompt", options.Prompt);
            if (options.Diarize.HasValue) w.WriteBoolean("diarize", options.Diarize.Value);
        }), cancellationToken, 32 * 1024 * 1024);
    }
    public Task<JsonElement> SpeakAsync(SpeechOptions options, CancellationToken cancellationToken = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input) || options.Input.Length > 20000 || options.Voice?.Length > 64 ||
            options.Format is not null && options.Format != "wav" && options.Format != "mp3" ||
            options.Speed.HasValue && (double.IsNaN(options.Speed.Value) || double.IsInfinity(options.Speed.Value) || options.Speed <= 0))
            throw new ArgumentException("Invalid speech options.", nameof(options));
        SessionJson.Unicode(options.Input);
        return Post("/audio/speech", SessionJson.Object(w =>
        {
            w.WriteString("input", options.Input); SessionJson.Optional(w, "model", options.Model);
            SessionJson.Optional(w, "voice", options.Voice); SessionJson.Optional(w, "format", options.Format);
            if (options.Speed.HasValue) w.WriteNumber("speed", options.Speed.Value);
        }), cancellationToken, 32 * 1024 * 1024);
    }

    public Task ObserveAsync(Func<AgentEvent, CancellationToken, Task> observer, CancellationToken cancellationToken = default)
        => ObserveAsync(observer, new EventStreamOptions(), cancellationToken);
    public Task ObserveAsync(Func<AgentEvent, CancellationToken, Task> observer, EventStreamOptions options, CancellationToken cancellationToken = default)
        => client.ObserveSessionAsync(this, observer, options, cancellationToken);
}
