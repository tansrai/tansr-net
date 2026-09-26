using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tansr.Examples;

// 这是应用自己的草稿/呈现文件；从不提供 archive current authority、模型 history 或材料源。
internal sealed class LocalConversationState
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private readonly string path;
    private readonly object gate = new();
    private LocalConversationSnapshot current = new();
    private string? expectedFileHash;

    internal LocalConversationState(string path) => this.path = Path.GetFullPath(path);
    internal string PathName => path;
    internal LocalConversationSnapshot Snapshot { get { lock (gate) return current; } }
    internal static LocalConversationState ForApplication(string application) => new(
        Environment.GetEnvironmentVariable("TANSR_EXAMPLE_STATE_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tansr", "Examples", application + ".json"));

    internal LocalConversationSnapshot Load()
    {
        lock (gate)
        {
            if (!File.Exists(path)) return current;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidOperationException("local_state_too_large");
            var bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length) { var read = stream.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (Text(root, "format") != "tansr-example-presentation-v1") throw new InvalidOperationException("local_state_format_mismatch");
            TurnInputRecord? input = null;
            if (root.TryGetProperty("input", out var value) && value.ValueKind == JsonValueKind.Object)
                input = new TurnInputRecord(Text(value, "sessionId"), Text(value, "inputId"), Text(value, "historyEpoch"), Text(value, "turnId"), Text(value, "text"), Text(value, "outcome"), Optional(value, "receipt"));
            current = new LocalConversationSnapshot(Text(root, "endpoint"), Text(root, "sessionId"), Text(root, "draft"), Text(root, "presentation"), Optional(root, "history"), Text(root, "savedAt"), input);
            expectedFileHash = Hash(bytes);
            return current;
        }
    }

    internal void Save(string endpoint, string sessionId, string draft, string presentation, string? history = null)
    {
        lock (gate)
        {
            if (current.Input != null && !current.Input.CanStartAnother && (current.Endpoint != endpoint || current.SessionId != sessionId))
                throw new InvalidOperationException("unresolved_input_keep_original_endpoint_and_session");
            // endpoint 只允许普通来源，不保存 URL 中嵌入的认证数据。
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
                throw new InvalidOperationException("local_endpoint_must_not_contain_credentials");
            Write(new LocalConversationSnapshot(endpoint, sessionId, draft, presentation,
                history ?? (current.Endpoint == endpoint && current.SessionId == sessionId ? current.HistoryJson : null),
                DateTimeOffset.UtcNow.ToString("O"), current.Endpoint == endpoint && current.SessionId == sessionId ? current.Input : null));
        }
    }

    internal void SaveInput(TurnInputRecord input)
    {
        lock (gate)
        {
            if (current.SessionId != input.SessionId) throw new InvalidOperationException("local_input_session_mismatch");
            Write(new LocalConversationSnapshot(current.Endpoint, current.SessionId, current.Draft, current.Presentation, current.HistoryJson, DateTimeOffset.UtcNow.ToString("O"), input));
        }
    }

    private void Write(LocalConversationSnapshot next)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("format", "tansr-example-presentation-v1"); writer.WriteString("endpoint", next.Endpoint);
            writer.WriteString("sessionId", next.SessionId); writer.WriteString("draft", next.Draft); writer.WriteString("presentation", next.Presentation);
            writer.WriteString("history", next.HistoryJson); writer.WriteString("savedAt", next.SavedAt);
            if (next.Input != null)
            {
                var input = next.Input; writer.WriteStartObject("input"); writer.WriteString("sessionId", input.SessionId); writer.WriteString("inputId", input.InputId);
                writer.WriteString("historyEpoch", input.HistoryEpoch); writer.WriteString("turnId", input.TurnId); writer.WriteString("text", input.Text);
                writer.WriteString("outcome", input.Outcome); writer.WriteString("receipt", input.ReceiptJson); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        if (buffer.Length > MaximumBytes) throw new InvalidOperationException("local_state_too_large_no_truncation");
        var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
        // 不覆盖另一个活跃写者；临时文件与目标在同目录，替换前 Flush 到磁盘。
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if ((File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null) != expectedFileHash)
            throw new InvalidOperationException("local_state_changed_by_another_instance");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = buffer.ToArray(); output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            current = next;
            expectedFileHash = Hash(buffer.ToArray());
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? "";
    private static string Hash(byte[] value) { using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(value)); }
    private static string? Optional(JsonElement value, string name) => value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
}

internal sealed class LocalConversationSnapshot
{
    internal LocalConversationSnapshot(string endpoint = "", string sessionId = "", string draft = "", string presentation = "", string? historyJson = null, string savedAt = "", TurnInputRecord? input = null)
    { Endpoint = endpoint; SessionId = sessionId; Draft = draft; Presentation = presentation; HistoryJson = historyJson; SavedAt = savedAt; Input = input; }
    internal string Endpoint { get; }
    internal string SessionId { get; }
    internal string Draft { get; }
    internal string Presentation { get; }
    internal string? HistoryJson { get; }
    internal string SavedAt { get; }
    internal TurnInputRecord? Input { get; }
    internal string OfflineText => "本机呈现副本，保存于 " + SavedAt + "\nServe=" + Endpoint + " session=" + SessionId +
        "\n不是当前授权的档案/模型历史；可能已过期或撤权，不会自动回灌模型。\n\n" + Presentation +
        (HistoryJson == null ? "" : "\n\n当时获取的历史副本：\n" + HistoryJson);
}
