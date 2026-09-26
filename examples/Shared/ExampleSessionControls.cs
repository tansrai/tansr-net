using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Terminal;

namespace Tansr.Examples;

// 公开 preview facade 的应用装配；每个原操作先保存在本机日志，不建立新协议。
internal sealed class ExampleSessionControls
{
    private readonly TerminalSessionControl control;
    private readonly string sessionId;
    private readonly string journalPrefix;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal ExampleSessionControls(TerminalSessionControl control, string endpoint, string sessionId, string statePath)
    {
        this.control = control; this.sessionId = sessionId;
        using var sha = SHA256.Create();
        var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(endpoint + "\n" + sessionId))).Replace("-", "").ToLowerInvariant();
        journalPrefix = statePath + "." + key;
    }

    internal Task<JsonElement> ReadConfigurationAsync(CancellationToken ct = default) => control.ReadConfigurationAsync(sessionId, ct);
    internal Task<JsonElement> ReadMemoryAsync(CancellationToken ct = default) => control.ReadMemoryAsync(sessionId, ct);
    internal static readonly string[] Actions = { "读取配置", "修改模型", "修改思考预算", "重放原配置", "读取记忆来源", "记住本轮", "置顶文字", "遗忘主题", "查询原记忆操作", "重放原记忆操作" };
    internal Task<JsonElement> ExecuteAsync(int action, string value, CancellationToken ct = default)
    {
        if (action == 0) return ReadConfigurationAsync(ct);
        if (action == 3) return ReplayConfigurationAsync(ct);
        if (action == 4) return ReadMemoryAsync(ct);
        if (action == 5) return CommandAsync("remember", ct: ct);
        if (action == 6) return CommandAsync("pin", value, ct);
        if (action == 7) return CommandAsync("forget", value, ct);
        if (action == 8) return QueryMemoryAsync(ct);
        if (action == 9) return ReplayMemoryAsync(ct);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (action == 1) writer.WriteString("model", value);
            else if (action == 2)
            {
                writer.WritePropertyName("thinking");
                if (value == "null") writer.WriteNullValue();
                else { var budget = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture); writer.WriteStartObject(); writer.WriteNumber("budget", budget); writer.WriteEndObject(); }
            }
            else throw new InvalidOperationException("select_control_action");
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray()); return ChangeConfigurationAsync(document.RootElement.Clone(), ct);
    }

    internal async Task<JsonElement> ChangeConfigurationAsync(JsonElement changes, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureResolved("configuration");
            var state = await control.ReadConfigurationAsync(sessionId, ct).ConfigureAwait(false);
            var operation = control.CreateConfigurationOperation(sessionId, Guid.NewGuid().ToString("N"), state.GetProperty("configuration").GetProperty("revision").GetInt64(), changes);
            Write("configuration", operation.Request, operation.Scope, null);
            var result = await control.ApplyConfigurationAsync(operation, ct).ConfigureAwait(false);
            Write("configuration", operation.Request, operation.Scope, result); return result;
        }
        finally { gate.Release(); }
    }

    internal async Task<JsonElement> ReplayConfigurationAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = Read("configuration") ?? throw new InvalidOperationException("no_original_configuration_operation");
            var operation = control.RestoreConfigurationOperation(saved.GetProperty("request"), saved.GetProperty("scope"));
            var result = await control.ReplayConfigurationAsync(operation, ct).ConfigureAwait(false);
            Write("configuration", operation.Request, operation.Scope, result); return result;
        }
        finally { gate.Release(); }
    }

    internal async Task<JsonElement> CommandAsync(string kind, string? value = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureResolved("memory");
            var state = await control.ReadMemoryAsync(sessionId, ct).ConfigureAwait(false);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteString("kind", kind);
                if (kind == "pin") writer.WriteString("text", value);
                else if (kind == "forget") writer.WriteString("topic", value);
                else if (kind != "remember") throw new InvalidOperationException("unsupported_memory_command");
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(stream.ToArray());
            var operation = control.CreateMemoryOperation(sessionId, state, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), document.RootElement);
            Write("memory", operation.Request, operation.Scope, null);
            var result = await control.SubmitMemoryAsync(operation, ct).ConfigureAwait(false);
            Write("memory", operation.Request, operation.Scope, result); return result;
        }
        finally { gate.Release(); }
    }

    internal Task<JsonElement> QueryMemoryAsync(CancellationToken ct = default) => RecoverMemoryAsync(false, ct);
    internal Task<JsonElement> ReplayMemoryAsync(CancellationToken ct = default) => RecoverMemoryAsync(true, ct);
    private async Task<JsonElement> RecoverMemoryAsync(bool replay, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = Read("memory") ?? throw new InvalidOperationException("no_original_memory_operation");
            var operation = control.RestoreMemoryOperation(saved.GetProperty("request"), saved.GetProperty("scope"));
            var result = replay ? await control.ReplayMemoryAsync(operation, ct).ConfigureAwait(false) : await control.QueryMemoryAsync(operation, ct).ConfigureAwait(false);
            Write("memory", operation.Request, operation.Scope, result); return result;
        }
        finally { gate.Release(); }
    }

    internal string DescribePending()
    {
        var values = new List<string>();
        foreach (var kind in new[] { "configuration", "memory" })
        { var value = Read(kind); if (value.HasValue) values.Add(kind + " 原操作：\n" + value.Value.GetRawText()); }
        return string.Join("\n", values);
    }

    private void EnsureResolved(string kind)
    {
        var saved = Read(kind); if (!saved.HasValue) return;
        if (!IsResolved(saved.Value, kind)) throw new InvalidOperationException(kind + "_original_operation_unresolved");
    }
    private static bool IsResolved(JsonElement saved, string kind)
    {
        if (saved.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (kind == "configuration" && result.TryGetProperty("status", out var configurationStatus) &&
                (configurationStatus.GetString() == "changed" || configurationStatus.GetString() == "unchanged" || configurationStatus.GetString() == "replayed")) return true;
            if (kind == "memory" && result.TryGetProperty("receipt", out var receipt) && receipt.ValueKind == JsonValueKind.Object &&
                receipt.TryGetProperty("status", out var status) && (status.GetString() == "committed" || status.GetString() == "failed")) return true;
        }
        return false;
    }

    private JsonElement? Read(string kind)
    {
        var path = journalPrefix + "." + kind + ".json"; if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 1024 * 1024) throw new InvalidOperationException("local_operation_too_large");
        using var document = JsonDocument.Parse(input); var value = document.RootElement;
        if (value.GetProperty("sessionId").GetString() != sessionId || value.GetProperty("kind").GetString() != kind) throw new InvalidOperationException("local_operation_identity_mismatch");
        return value.Clone();
    }

    private void Write(string kind, JsonElement request, JsonElement scope, JsonElement? result)
    {
        var path = journalPrefix + "." + kind + ".json"; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Read(kind);
        if (existing.HasValue && existing.Value.GetProperty("request").GetRawText() != request.GetRawText() &&
            (result.HasValue || !IsResolved(existing.Value, kind))) throw new InvalidOperationException("original_operation_changed_by_another_instance");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject(); writer.WriteString("kind", kind); writer.WriteString("sessionId", sessionId);
                    writer.WritePropertyName("request"); request.WriteTo(writer); writer.WritePropertyName("scope"); scope.WriteTo(writer);
                    writer.WritePropertyName("result"); if (result.HasValue) result.Value.WriteTo(writer); else writer.WriteNullValue(); writer.WriteEndObject(); writer.Flush();
                }
                output.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
