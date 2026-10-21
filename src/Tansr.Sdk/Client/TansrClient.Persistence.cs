using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Api;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    /// <summary>读取可信宿主固定的持久化来源与覆盖；此响应不是改模式或恢复执行权限的凭据。</summary>
    public async Task<JsonElement> GetSessionPersistenceAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var response = await SendSessionAsync(HttpMethod.Get, ApiRoutes.SessionPersistenceRead.Path(id: sessionId), null, cancellationToken).ConfigureAwait(false);
        PersistenceJson.View(response, sessionId);
        return response;
    }

    /// <summary>按原消息 requestId 查询受理事实。未找到和 unknown 均不得自动换键重发。</summary>
    public async Task<JsonElement> GetInputAcceptanceAsync(string sessionId, string requestId, CancellationToken cancellationToken = default)
    {
        PersistenceJson.Id(requestId);
        var response = await SendSessionAsync(HttpMethod.Get, ApiRoutes.SessionInputQuery.Path(id: sessionId, targetId: requestId), null, cancellationToken).ConfigureAwait(false);
        PersistenceJson.Envelope(response, sessionId, "contract", "sessionId", "receipt");
        var receipt = response.GetProperty("receipt");
        if (receipt.ValueKind != JsonValueKind.Null)
        {
            PersistenceJson.Receipt(receipt, sessionId);
            PersistenceJson.Check(receipt.GetProperty("requestId").GetString() == requestId);
        }
        return response;
    }

    /// <summary>列出原请求受理事实。列表不能按最新一项或文本相似度猜测丢失的原请求身份。</summary>
    public async Task<JsonElement> ListInputAcceptancesAsync(string sessionId, string? afterRequestId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterRequestId != null) PersistenceJson.Id(afterRequestId);
        var query = new List<(string Name, string Value)> { ("limit", limit.ToString(CultureInfo.InvariantCulture)) };
        if (afterRequestId != null) query.Add(("cursor", afterRequestId));
        var response = await SendSessionAsync(HttpMethod.Get, ApiRoutes.SessionInputList.Path(id: sessionId) +
            ApiRoutes.SessionInputList.Query(query.ToArray()), null, cancellationToken).ConfigureAwait(false);
        PersistenceJson.Envelope(response, sessionId, "contract", "sessionId", "receipts", "nextCursor");
        var records = response.GetProperty("receipts");
        PersistenceJson.Check(records.ValueKind == JsonValueKind.Array && records.GetArrayLength() <= limit);
        var previous = afterRequestId;
        foreach (var receipt in records.EnumerateArray())
        {
            PersistenceJson.Receipt(receipt, sessionId);
            var id = receipt.GetProperty("requestId").GetString()!;
            PersistenceJson.Check(previous == null || string.CompareOrdinal(previous, id) < 0); previous = id;
        }
        var cursor = response.GetProperty("nextCursor");
        if (cursor.ValueKind != JsonValueKind.Null)
        {
            PersistenceJson.Check(cursor.ValueKind == JsonValueKind.String); PersistenceJson.Id(cursor.GetString()!);
            PersistenceJson.Check(records.GetArrayLength() > 0 && cursor.GetString() == previous);
        }
        return response;
    }
}

// 对应 Serve v2 contract.ts 的 serve-persistence-v1 加法；结构验证不把历史事实变成当前授权。
internal static class PersistenceJson
{
    internal static void Check(bool condition) { if (!condition) throw new TansrProtocolException("invalid_response"); }
    internal static void Id(string value) => WireJson.ValidateNamed("Id", String(value));
    private static JsonElement String(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) writer.WriteStringValue(value);
        return WireJson.Parse(stream.ToArray());
    }
    private static void Fields(JsonElement value, params string[] fields)
    {
        Check(value.ValueKind == JsonValueKind.Object);
        var names = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) Check(names.Remove(field.Name));
        Check(names.Count == 0);
    }
    internal static void Envelope(JsonElement value, string sessionId, params string[] fields)
    {
        Fields(value, fields);
        Check(Text(value, "contract") == "serve-persistence-v1" && Text(value, "sessionId") == sessionId);
    }
    private static string Text(JsonElement value, string name)
    { Check(value.GetProperty(name).ValueKind == JsonValueKind.String); return value.GetProperty(name).GetString()!; }
    private static bool Boolean(JsonElement value, string name)
    { var field = value.GetProperty(name); Check(field.ValueKind == JsonValueKind.False || field.ValueKind == JsonValueKind.True); return field.GetBoolean(); }
    private static void Source(string value)
    {
        SessionJson.Unicode(value);
        Check(!string.IsNullOrWhiteSpace(value) && Encoding.UTF8.GetByteCount(value) <= 1024 && !value.Any(c => c < 32 || c == 127));
    }
    internal static void View(JsonElement value, string sessionId)
    {
        Envelope(value, sessionId, "contract", "sessionId", "mode", "sourceId", "sourceGeneration", "coverage", "handoff", "localDisk");
        var mode = Text(value, "mode"); Check(mode == "terminal" || mode == "cloud");
        Source(Text(value, "sourceId")); Source(Text(value, "sourceGeneration"));
        Check(!Boolean(value, "localDisk"));
        var allowed = new HashSet<string>(new[] { "history", "memory", "checkpoints", "executions", "input-receipts", "artifacts", "events", "control" }, StringComparer.Ordinal);
        var coverage = value.GetProperty("coverage"); Check(coverage.ValueKind == JsonValueKind.Array && coverage.GetArrayLength() <= 8);
        foreach (var item in coverage.EnumerateArray()) Check(item.ValueKind == JsonValueKind.String && allowed.Remove(item.GetString()!));
        var handoff = value.GetProperty("handoff"); Fields(handoff, "archive", "trustedControl", "executionJournal", "inputReceipts");
        Check(Text(handoff, "archive") == (mode == "cloud" ? "cloud-immutable-publish" : "terminal-durable-ack"));
        _ = Boolean(handoff, "trustedControl"); _ = Boolean(handoff, "inputReceipts");
        Check(!Boolean(handoff, "executionJournal") || mode == "cloud");
    }
    internal static void Receipt(JsonElement value, string sessionId)
    {
        Fields(value, "sessionId", "requestId", "digest", "generation", "state", "acceptedAt");
        Check(Text(value, "sessionId") == sessionId); Id(Text(value, "requestId"));
        WireJson.ValidateNamed("Digest", value.GetProperty("digest")); Source(Text(value, "generation"));
        var state = Text(value, "state"); Check(state == "accepted" || state == "unknown");
        var time = Text(value, "acceptedAt");
        Check(Regex.IsMatch(time, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?Z\z", RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }
}
