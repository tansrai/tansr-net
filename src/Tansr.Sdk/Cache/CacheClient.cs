using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Cache;

/// <summary>
/// 独立私有 cache-v1 候选消费者。必须显式启用；尚非稳定公开 API。
/// Serve 负责真实历史见证、两跳账本和授权；本类型不生成缓存组或 provider exchange。
/// </summary>
internal sealed class CacheClient
{
    private const string Root = "/v3/sdk2/cache";
    private readonly TansrClient client;
    private readonly string owner;
    private readonly Func<DateTimeOffset> now;

    internal CacheClient(TansrClient client, bool enableCandidate = false, Func<DateTimeOffset>? now = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        if (!enableCandidate) throw new TansrProtocolException("unsupported_capability");
        owner = client.CacheOwner(); this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private void CheckOwner()
    { if (client.CacheOwner() != owner) throw new TansrProtocolException("context_changed"); }

    internal async Task<CacheCapabilities> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        CheckOwner();
        var raw = await client.SendCacheAsync(HttpMethod.Get, Root + "/capabilities", null, owner, cancellationToken).ConfigureAwait(false);
        CacheJson.Validate("CapabilitiesResponse", raw);
        if (CacheJson.String(raw, "audience") != "serve-cache") throw new TansrProtocolException("invalid_response");
        return new CacheCapabilities(raw, owner);
    }

    private async Task<CacheCapabilities> AdmissionAsync(CancellationToken cancellationToken)
    {
        var caps = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var epoch = caps.Raw.GetProperty("operationEpoch");
        if (!caps.Available || caps.OperationEpoch is null || CacheJson.String(caps.Raw, "gateway") != "confirmed")
            throw new TansrProtocolException("unsupported_capability");
        if (!DateTimeOffset.TryParse(CacheJson.String(epoch, "expiresAt"), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var expires) || expires <= now()) throw new TansrProtocolException("epoch_unavailable");
        return caps;
    }

    internal async Task<CacheOperation> PrepareOpenAsync(string sessionId, CacheOpenKind kind, CacheTicket? ticket = null,
        string? requestId = null, CancellationToken cancellationToken = default)
    {
        CacheJson.Text("LegacyId", sessionId);
        if (!Enum.IsDefined(typeof(CacheOpenKind), kind) || (kind == CacheOpenKind.Resume || kind == CacheOpenKind.Fork) != (ticket is not null))
            throw new TansrProtocolException("invalid_request");
        requestId = RequestId(requestId);
        var caps = await AdmissionAsync(cancellationToken).ConfigureAwait(false);
        var body = CacheJson.Write(writer =>
        {
            writer.WriteStartObject(); WriteIdentity(writer, caps.OperationEpoch!, requestId);
            writer.WriteString("sessionId", sessionId); writer.WriteString("requiredFeature", CacheJson.Feature);
            writer.WriteStartObject("intent"); writer.WriteString("kind", kind.ToString().ToLowerInvariant());
            if (ticket is not null) writer.WriteString(kind == CacheOpenKind.Fork ? "parentTicket" : "ticket", ticket.Value);
            writer.WriteEndObject(); writer.WriteEndObject();
        });
        return Prepare("open", body, caps.ControlBytes, false);
    }

    internal Task<CacheOperation> PrepareRenewAsync(CacheBinding binding, CacheTicket ticket, string? requestId = null, CancellationToken cancellationToken = default)
        => PrepareMutationAsync("renew", binding, ticket, null, requestId, cancellationToken);
    internal Task<CacheOperation> PrepareRotateAsync(CacheBinding binding, CacheTicket ticket, string? requestId = null, CancellationToken cancellationToken = default)
        => PrepareMutationAsync("rotate", binding, ticket, null, requestId, cancellationToken);
    internal Task<CacheOperation> PrepareCloseAsync(CacheBinding binding, CacheTicket ticket, string? requestId = null, CancellationToken cancellationToken = default)
        => PrepareMutationAsync("close", binding, ticket, null, requestId, cancellationToken);
    internal Task<CacheOperation> PrepareRebindAsync(CacheBinding binding, string sessionId, string? requestId = null, CancellationToken cancellationToken = default)
        => PrepareMutationAsync("rebind", binding, null, sessionId, requestId, cancellationToken);

    private async Task<CacheOperation> PrepareMutationAsync(string operation, CacheBinding binding, CacheTicket? ticket,
        string? sessionId, string? requestId, CancellationToken cancellationToken)
    {
        CheckOwner();
        if (binding is null) throw new ArgumentNullException(nameof(binding));
        if (binding.Owner != owner) throw new TansrProtocolException("context_changed");
        if (operation == "rebind") CacheJson.Text("LegacyId", sessionId!);
        else if (ticket is null) throw new ArgumentNullException(nameof(ticket));
        requestId = RequestId(requestId);
        var caps = await AdmissionAsync(cancellationToken).ConfigureAwait(false);
        var body = CacheJson.Write(writer =>
        {
            writer.WriteStartObject(); WriteIdentity(writer, caps.OperationEpoch!, requestId);
            writer.WriteString("bindingId", binding.Id); writer.WriteString("expectedRevision", binding.Revision);
            if (operation == "rebind") writer.WriteString("sessionId", sessionId);
            else writer.WriteString("ticket", ticket!.Value);
            if (operation == "rotate") writer.WriteString("reason", "manual");
            writer.WriteEndObject();
        });
        return Prepare(operation, body, caps.ControlBytes, false);
    }

    private static string RequestId(string? id)
    {
        var value = id ?? Guid.NewGuid().ToString("D");
        CacheJson.Text("Id", value); return value;
    }

    private static void WriteIdentity(Utf8JsonWriter writer, string epoch, string requestId)
    {
        writer.WriteString("protocol", CacheJson.Protocol); writer.WriteStartObject("request");
        writer.WriteString("operationEpoch", epoch); writer.WriteString("requestId", requestId); writer.WriteEndObject();
    }

    private CacheOperation Prepare(string operation, JsonElement body, int maximum, bool restored)
    {
        string schema;
        switch (operation)
        {
            case "open": schema = "OpenRequest"; break;
            case "renew": schema = "RenewRequest"; break;
            case "rotate": schema = "RotateRequest"; break;
            case "close": schema = "CloseRequest"; break;
            case "rebind": schema = "RebindRequest"; break;
            default: throw new TansrProtocolException("invalid_request");
        }
        CacheJson.Validate(schema, body);
        if (body.TryGetProperty("ticket", out var ticket)) CacheJson.Text("Ticket", ticket.GetString()!);
        if (body.TryGetProperty("intent", out var intent))
        {
            if (intent.TryGetProperty("ticket", out ticket)) CacheJson.Text("Ticket", ticket.GetString()!);
            if (intent.TryGetProperty("parentTicket", out ticket)) CacheJson.Text("Ticket", ticket.GetString()!);
        }
        if (CacheJson.Encode(body).Length > maximum) throw new TansrProtocolException("payload_too_large");
        return new CacheOperation(operation, body, owner, restored);
    }

    /// <summary>只恢复可信存储的原文及原所属人；不会发现新 epoch、换请求号或重建票据。</summary>
    internal CacheOperation RestoreOperation(string operation, byte[] originalRequest, string originalOwner, byte[]? originalReceipt = null)
    {
        CheckOwner();
        if (originalOwner != owner) throw new TansrProtocolException("context_changed");
        if (originalRequest is null) throw new ArgumentNullException(nameof(originalRequest));
        var raw = CacheJson.Read(originalRequest);
        if (Encoding.UTF8.GetString(originalRequest) != CacheJson.Canonical(raw)) throw new TansrProtocolException("request_id_conflict");
        var result = Prepare(operation, raw, CacheJson.MaximumBytes, true);
        if (originalReceipt is not null)
        {
            var receipt = CacheJson.Read(originalReceipt);
            ValidateReceipt(result, receipt);
            var canonical = CacheJson.Canonical(receipt);
            if (Encoding.UTF8.GetString(originalReceipt) != canonical) throw new TansrProtocolException("receipt_conflict");
            result.ReceiptCanonical = canonical;
        }
        return result;
    }

    internal Task<CacheReceipt> SubmitAsync(CacheOperation operation, CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, false, false, cancellationToken);

    /// <summary>显式重放完全相同原文；调用者先查原状态后决定，网络层从不自动触发本方法。</summary>
    internal Task<CacheReceipt> ReplayOriginalAsync(CacheOperation operation, CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, false, true, cancellationToken);

    /// <summary>查询可能继续协调原已受理两跳事务；不新建关系，旧 epoch 也仍查询原操作。</summary>
    internal Task<CacheReceipt> GetOperationStatusAsync(CacheOperation operation, CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, true, false, cancellationToken);

    private async Task<CacheReceipt> ExecuteAsync(CacheOperation operation, bool query, bool replay, CancellationToken cancellationToken)
    {
        CheckOwner();
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (operation.Owner != owner) throw new TansrProtocolException("context_changed");
        await operation.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!query && !replay && operation.Attempted) throw new TansrProtocolException("query_status_required");
            operation.Attempted = true;
            string path;
            if (query)
                path = Root + "/operations?protocol=" + CacheJson.Protocol + "&operation=" + operation.Operation +
                    "&operationEpoch=" + Uri.EscapeDataString(operation.OperationEpoch) + "&requestId=" + Uri.EscapeDataString(operation.RequestId) +
                    (operation.BindingId is null ? "" : "&bindingId=" + Uri.EscapeDataString(operation.BindingId));
            else path = Root + "/bindings" + (operation.BindingId is null ? "" : "/" + Uri.EscapeDataString(operation.BindingId) + "/" + operation.Operation);
            var result = await client.SendCacheAsync(query ? HttpMethod.Get : HttpMethod.Post, path,
                query ? null : operation.ExportOriginalRequest(), owner, cancellationToken).ConfigureAwait(false);
            ValidateReceipt(operation, result);
            var canonical = CacheJson.Canonical(result);
            if (operation.ReceiptCanonical is not null && operation.ReceiptCanonical != canonical) throw new TansrProtocolException("receipt_conflict");
            operation.ReceiptCanonical = canonical;
            return new CacheReceipt(result, owner);
        }
        finally { operation.Gate.Release(); }
    }

    private static void ValidateReceipt(CacheOperation operation, JsonElement result)
    {
        CacheJson.Validate(operation.Operation == "open" ? "OpenResponse" : "MutationReceipt", result);
        CacheJson.Binding(result.GetProperty("binding"));
        var request = result.GetProperty("request");
        if (CacheJson.String(request, "requestId") != operation.RequestId || CacheJson.String(request, "operationEpoch") != operation.OperationEpoch)
            throw new TansrProtocolException("invalid_response");
        if (operation.Operation == "open")
        {
            var kind = CacheJson.String(operation.Body.GetProperty("intent"), "kind");
            var relation = kind == "new" ? "new" : kind == "resume" ? "resumed" : kind == "fork" ? "forked" : "imported";
            if (CacheJson.String(result, "relation") != relation) throw new TansrProtocolException("invalid_response");
        }
        else if (CacheJson.String(result, "operation") != operation.Operation || CacheJson.String(result.GetProperty("binding"), "bindingId") != operation.BindingId)
            throw new TansrProtocolException("invalid_response");
        var ticket = result.GetProperty("ticket"); var expiry = result.GetProperty("ticketExpiresAt");
        if ((ticket.ValueKind == JsonValueKind.Null) != (expiry.ValueKind == JsonValueKind.Null)) throw new TansrProtocolException("invalid_response");
        if (ticket.ValueKind != JsonValueKind.Null) CacheJson.Text("Ticket", ticket.GetString()!);
        if (operation.Operation == "close" && (ticket.ValueKind != JsonValueKind.Null || CacheJson.String(result.GetProperty("binding"), "state") != "closed"))
            throw new TansrProtocolException("invalid_response");
        // semanticDigest 为可信 Serve 的 HMAC 回执。终端仅校验形状/不可变性，不伪称持有服务端密钥验证过它。
    }

    internal async Task<CacheBinding> ReadBindingAsync(string bindingId, CancellationToken cancellationToken = default)
    {
        CacheJson.Text("Id", bindingId); CheckOwner();
        var raw = await client.SendCacheAsync(HttpMethod.Get, Root + "/bindings/" + Uri.EscapeDataString(bindingId) + "?protocol=" + CacheJson.Protocol,
            null, owner, cancellationToken).ConfigureAwait(false);
        var result = new CacheBinding(raw, owner);
        if (result.Id != bindingId) throw new TansrProtocolException("invalid_response");
        return result;
    }

    internal async Task<JsonElement> ReadDiagnosticsAsync(string bindingId, string? after = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        CacheJson.Text("Id", bindingId); if (after is not null) CacheJson.Text("Id", after);
        if (limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        CheckOwner();
        var raw = await client.SendCacheAsync(HttpMethod.Get, Root + "/bindings/" + Uri.EscapeDataString(bindingId) + "/diagnostics?protocol=" + CacheJson.Protocol +
            "&limit=" + limit.ToString(CultureInfo.InvariantCulture) + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)), null, owner, cancellationToken).ConfigureAwait(false);
        CacheJson.Validate("DiagnosticPage", raw);
        // 原 GatewayCacheClient 的此入口目前严格 emptyDiagnostic；真实用量在独立 cache-core-v1。
        if (raw.GetProperty("rows").GetArrayLength() != 0 || raw.GetProperty("next").ValueKind != JsonValueKind.Null)
            throw new TansrProtocolException("invalid_response");
        return raw;
    }
}
