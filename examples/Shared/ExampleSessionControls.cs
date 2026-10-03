using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Terminal;

namespace Tansr.Examples;

// 公开 preview facade 的应用装配；每个原操作先保存在本机日志，不建立新协议。
internal sealed class ExampleSessionControls
{
    private readonly TerminalSessionControl control;
    private readonly string sessionId;
    private readonly string journalPrefix;
    private readonly TerminalProfileClient? profile;
    private readonly SessionContract sessionContract;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal ExampleSessionControls(TerminalSessionControl control, string endpoint, string sessionId, string statePath,
        TerminalProfileClient? profile = null, SessionContract sessionContract = SessionContract.Sdk1)
    {
        this.control = control; this.sessionId = sessionId;
        this.profile = profile; this.sessionContract = sessionContract;
        using var sha = SHA256.Create();
        var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(endpoint + "\n" + sessionId))).Replace("-", "").ToLowerInvariant();
        journalPrefix = statePath + "." + key;
    }

    internal Task<JsonElement> ReadConfigurationAsync(CancellationToken ct = default) => control.ReadConfigurationAsync(sessionId, ct);
    internal Task<JsonElement> ReadMemoryAsync(CancellationToken ct = default) => control.ReadMemoryAsync(sessionId, ct);
    internal Task<TerminalProfileCatalog> ReadCatalogAsync(CancellationToken ct = default) => (profile ?? throw new InvalidOperationException("terminal_profile_preview_not_enabled")).ReadCatalogAsync(sessionId, sessionContract, ct);
    internal Task<TerminalProfileUsage> ReadUsageAsync(CancellationToken ct = default) => (profile ?? throw new InvalidOperationException("terminal_profile_preview_not_enabled")).ReadUsageAsync(sessionId, sessionContract, ct);
    internal static readonly string[] Actions = { "读取配置", "修改模型", "修改思考预算", "重放原配置", "读取记忆来源", "记住本轮", "置顶文字", "遗忘主题", "查询原记忆操作", "重放原记忆操作", "授权模型目录", "本人最近1天用量" };
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
        if (action == 10) return ReadCatalogJsonAsync(ct);
        if (action == 11) return ReadUsageJsonAsync(ct);
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
    private async Task<JsonElement> ReadCatalogJsonAsync(CancellationToken ct) => (await ReadCatalogAsync(ct).ConfigureAwait(false)).Raw;
    private async Task<JsonElement> ReadUsageJsonAsync(CancellationToken ct) => (await ReadUsageAsync(ct).ConfigureAwait(false)).Raw;

    internal async Task<JsonElement> ChangeConfigurationAsync(JsonElement changes, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = AcquireOperationLease("configuration");
            EnsureResolved("configuration");
            var state = await control.ReadConfigurationAsync(sessionId, ct).ConfigureAwait(false);
            var operation = control.CreateConfigurationOperation(sessionId, Guid.NewGuid().ToString("N"), state.GetProperty("configuration").GetProperty("revision").GetInt64(), changes);
            Write("configuration", operation.Request, operation.Scope, null);
            JsonElement result;
            try { result = await control.ApplyConfigurationAsync(operation, ct).ConfigureAwait(false); }
            catch (TansrException error) when (IsDefiniteRejection("configuration", error))
            { Write("configuration", operation.Request, operation.Scope, null, error); throw; }
            Write("configuration", operation.Request, operation.Scope, result); return result;
        }
        finally { gate.Release(); }
    }

    internal async Task<JsonElement> ReplayConfigurationAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = AcquireOperationLease("configuration");
            var saved = Read("configuration") ?? throw new InvalidOperationException("no_original_configuration_operation");
            EnsureReplayable(saved, "configuration");
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
            using var lease = AcquireOperationLease("memory");
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
            JsonElement result;
            try { result = await control.SubmitMemoryAsync(operation, ct).ConfigureAwait(false); }
            catch (TansrException error) when (IsDefiniteRejection("memory", error))
            { Write("memory", operation.Request, operation.Scope, null, error); throw; }
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
            using var lease = AcquireOperationLease("memory");
            var saved = Read("memory") ?? throw new InvalidOperationException("no_original_memory_operation");
            EnsureReplayable(saved, "memory");
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
        {
            var value = Read(kind); if (!value.HasValue) continue;
            values.Add(kind + " 原操作：\n" + value.Value.GetRawText());
            if (Rejected(value.Value)) values.Add("原操作已确定拒绝；可再次选择修改或记忆命令，明确发起新请求。原失败保持可见，不自动重投。");
            if (value.Value.TryGetProperty("lastRejection", out _)) values.Add("确定拒绝的完整原日志保留在：" + journalPrefix + "." + kind + ".rejected.<sha256>.json");
        }
        return string.Join("\n", values);
    }

    private void EnsureResolved(string kind)
    {
        var saved = Read(kind); if (!saved.HasValue) return;
        if (!IsResolved(saved.Value, kind)) throw new InvalidOperationException(kind + "_original_operation_unresolved");
    }
    private static bool IsResolved(JsonElement saved, string kind)
    {
        if (Rejected(saved)) return true;
        if (saved.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (kind == "configuration" && result.TryGetProperty("status", out var configurationStatus) &&
                (configurationStatus.GetString() == "changed" || configurationStatus.GetString() == "unchanged" || configurationStatus.GetString() == "replayed")) return true;
            if (kind == "memory" && result.TryGetProperty("receipt", out var receipt) && receipt.ValueKind == JsonValueKind.Object &&
                receipt.TryGetProperty("status", out var status) && (status.GetString() == "committed" || status.GetString() == "failed")) return true;
        }
        return false;
    }

    private static bool Rejected(JsonElement saved) => saved.TryGetProperty("rejection", out var rejection) && rejection.ValueKind == JsonValueKind.Object;
    private static void EnsureReplayable(JsonElement saved, string kind)
    {
        if (Rejected(saved)) throw new InvalidOperationException(kind + "_original_operation_rejected_start_new_explicitly");
    }

    // Only a first, newly generated request can use these pre-commit refusals. A rejection while
    // replaying an earlier unknown request cannot prove that the earlier attempt did not commit.
    // In particular memory authority/lifecycle errors may follow the commit, so 4xx is not enough.
    private static bool IsDefiniteRejection(string kind, TansrException error)
    {
        if (error is TansrProtocolException)
            return error.Code == "contract_mismatch" || error.Code == "unsupported_capability" || error.Code == "payload_too_large";
        if (error is not TansrHttpException http) return false;
        // D19: the refusals below are the terminal family's own pre-commit answers, so the family fact decides — read from
        // UnifiedApiException.Detail on a unified envelope (null when the facade translated nothing), from the wire on a passthrough.
        string? code = FamilyFacts.Code(http), retry = FamilyFacts.RetryAction(http); int? status = FamilyFacts.Status(http);
        if (status == 409 && ((code == "revision_conflict" && retry == "refresh") || (code == "busy" && retry == "backoff"))) return true;
        if (kind != "configuration") return false;
        // Refused before any domain saw it: a unified 400 invalid_request / none with no family translation (facade or request-header layer).
        if (http is UnifiedApiException unified && unified.Code == UnifiedErrorCode.InvalidRequest && unified.StatusCode == 400 &&
            unified.RetryAction == UnifiedRetryAction.None && unified.Detail.DomainStatus == null) return true;
        return (status == 409 && ((code == "context_transition_required" && retry == "refresh") ||
            (code == "unsupported_capability" && retry == "discover"))) ||
            (status == 400 && code == "invalid_request" && retry == "none") ||
            (status == 429 && code == "request_limit" && retry == "backoff");
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

    private FileStream AcquireOperationLease(string kind)
    {
        var path = journalPrefix + "." + kind + ".active.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Do not let another example instance replay this request while its first outcome is
        // being classified. A process crash releases the handle but leaves the journal unknown.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Write(string kind, JsonElement request, JsonElement scope, JsonElement? result, TansrException? rejection = null)
    {
        var path = journalPrefix + "." + kind + ".json"; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Read(kind);
        if (existing.HasValue && existing.Value.GetProperty("request").GetRawText() != request.GetRawText() &&
            (result.HasValue || rejection is not null || !IsResolved(existing.Value, kind))) throw new InvalidOperationException("original_operation_changed_by_another_instance");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject(); writer.WriteString("kind", kind); writer.WriteString("sessionId", sessionId);
                    writer.WritePropertyName("request"); request.WriteTo(writer); writer.WritePropertyName("scope"); scope.WriteTo(writer);
                    writer.WritePropertyName("result"); if (result.HasValue) result.Value.WriteTo(writer); else writer.WriteNullValue();
                    if (rejection is not null)
                    {
                        writer.WritePropertyName("rejection"); WriteFailure(writer, rejection);
                        writer.WritePropertyName("lastRejection"); writer.WriteStartObject();
                        writer.WritePropertyName("request"); request.WriteTo(writer); writer.WritePropertyName("scope"); scope.WriteTo(writer);
                        writer.WritePropertyName("failure"); WriteFailure(writer, rejection); writer.WriteEndObject();
                    }
                    else if (existing.HasValue && existing.Value.TryGetProperty("lastRejection", out var lastRejection))
                    { writer.WritePropertyName("lastRejection"); lastRejection.WriteTo(writer); }
                    writer.WriteEndObject(); writer.Flush();
                }
                output.Flush(true);
            }
            if (rejection is not null)
            {
                using var sha = SHA256.Create();
                var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(request.GetRawText()))).Replace("-", "").ToLowerInvariant();
                var rejectedPath = journalPrefix + "." + kind + ".rejected." + key + ".json";
                // Preserve the exact original failure before allowing any replacement request.
                // An existing archive is never overwritten or silently accepted as this receipt.
                using var source = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var archive = new FileStream(rejectedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(archive); archive.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteFailure(Utf8JsonWriter writer, TansrException error)
    {
        writer.WriteStartObject(); writer.WriteString("code", error.Code);
        if (error is TansrHttpException http)
        { writer.WriteNumber("status", http.StatusCode); writer.WriteString("retryAction", http.RetryAction); }
        else writer.WriteString("stage", "before_control_post");
        writer.WriteEndObject();
    }
}
