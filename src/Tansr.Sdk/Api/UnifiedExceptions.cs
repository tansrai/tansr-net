using System.Text.Json;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Api;

/// <summary>An <c>/api</c> response did not carry a usable <c>tansr-contract: unified-v1</c> header set. The SDK never
/// falls back to legacy prefixes or family decoders on this signal (RFC-UAPI-1, 手册 §16.6 纪律二).
/// <see cref="TansrException.Code"/> is always <c>contract_unavailable</c>; <see cref="Reason"/> refines it.</summary>
public sealed class ContractUnavailableException : TansrException
{
    public ContractUnavailableException(string reason, int statusCode) : base("contract_unavailable")
    { Reason = reason; StatusCode = statusCode; }
    /// <summary>missing_contract_header | contract_mismatch | invalid_contract_headers | non_json_body | invalid_json | invalid_error_body</summary>
    public string Reason { get; }
    public int StatusCode { get; }
}

/// <summary>A unified error envelope (<c>contract: unified-v1</c>) decoded from an <c>/api</c> error response.
/// <see cref="TansrException.Code"/>/<see cref="TansrHttpException.StatusCode"/>/<see cref="TansrHttpException.RetryAction"/>
/// hold the unified values; the original family facts stay in <see cref="DomainCode"/>, <see cref="DomainStatus"/>,
/// <see cref="DomainRetryAction"/> and <see cref="Detail"/>.</summary>
public sealed class UnifiedApiException : TansrHttpException
{
    public UnifiedApiException(int status, string code, string retryAction, int? retryAfterMs, string traceId, string? requestId,
        JsonElement? detail, UnifiedResponseMeta meta, bool facadeOwned)
        : base(status, code, retryAction: retryAction, retryAfterMs: retryAfterMs,
            inputOutcome: detail.HasValue && detail.Value.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String ? outcome.GetString() : null)
    {
        TraceId = traceId; RequestId = requestId; Detail = detail; Meta = meta; FacadeOwned = facadeOwned;
        if (detail.HasValue)
        {
            var value = detail.Value;
            DomainCode = Text(value, "domainCode") ?? code;
            // A present-but-null domainRetryAction means the family envelope had no action position (/v2 agent-session-v1).
            DomainRetryAction = value.TryGetProperty("domainRetryAction", out var domainRetryAction)
                ? (domainRetryAction.ValueKind == JsonValueKind.String ? domainRetryAction.GetString() : null)
                : retryAction;
            DomainStatus = value.TryGetProperty("domainStatus", out var domainStatus) && domainStatus.ValueKind == JsonValueKind.Number && domainStatus.TryGetInt32(out var number) ? number : status;
            Fallback = Text(value, "fallback"); DetailReason = Text(value, "reason"); Header = Text(value, "header");
            ClosureId = Text(value, "closureId") ?? meta.ClosureId; Operation = Text(value, "operation"); State = Text(value, "state");
            Family = Text(value, "family");
        }
        else { DomainCode = code; DomainRetryAction = retryAction; DomainStatus = status; ClosureId = meta.ClosureId; }
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    /// <summary>Observability correlation id (= response <c>x-request-id</c>).</summary>
    public string TraceId { get; }
    /// <summary>Client idempotency key echoed by the server; null when the request carried none.</summary>
    public string? RequestId { get; }
    /// <summary>Open <c>detail</c> object (domainCode/domainStatus/domainRetryAction/fallback/reason/header/closureId/operation/state).</summary>
    public JsonElement? Detail { get; }
    public UnifiedResponseMeta Meta { get; }
    /// <summary>True when the envelope was produced by the facade itself (requestId null, 8-code vocabulary).</summary>
    public bool FacadeOwned { get; }
    public string Domain => Meta.Domain;
    public override string DomainCode { get; }
    public override int DomainStatus { get; }
    public override string? DomainRetryAction { get; }
    /// <summary>cache family: none | legacy-cold; never acted upon by the unified layer.</summary>
    public string? Fallback { get; }
    public string? Family { get; }
    /// <summary>detail.reason: not_installed | outside_closure (facade) or a family reason.</summary>
    public string? DetailReason { get; }
    public string? Header { get; }
    public string? ClosureId { get; }
    public string? Operation { get; }
    public string? State { get; }
    /// <summary>412 precondition_failed → the caller must rediscover the capability closure before retrying.</summary>
    public bool RequiresRediscovery => Code == "precondition_failed" || RetryAction == "rediscover";
}

/// <summary>The request asked for <c>tansr-event-envelope: unified-v1</c> but the SSE response did not echo it; frames
/// are therefore family-native and the caller's envelope expectation cannot be met.</summary>
public sealed class EnvelopeNotNegotiatedException : TansrException
{
    public EnvelopeNotNegotiatedException() : base("envelope_not_negotiated") { }
}
