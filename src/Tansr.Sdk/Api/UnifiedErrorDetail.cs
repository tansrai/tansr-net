using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Tansr.Sdk.Api;

/// <summary>The 19 unified error codes (RFC-UAPI-1 §2.1; 开发方案 §0 D7 / D19). These are the public error vocabulary of
/// the SDK: business code branches on <see cref="Client.TansrException.Code"/> against these constants first and only then,
/// when it needs a family-specific distinction, on <see cref="UnifiedErrorDetail.DomainCode"/>.</summary>
public static class UnifiedErrorCode
{
    public const string InvalidRequest = "invalid_request";
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string Gone = "gone";
    public const string Conflict = "conflict";
    public const string StaleGeneration = "stale_generation";
    public const string Gap = "gap";
    public const string CapabilityUnavailable = "capability_unavailable";
    public const string CapacityExceeded = "capacity_exceeded";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UpstreamUnavailable = "upstream_unavailable";
    public const string ResultUnknown = "result_unknown";
    public const string Rejected = "rejected";
    public const string InternalError = "internal_error";
    public const string PreconditionFailed = "precondition_failed";
    public const string NotCanonical = "not_canonical";

    /// <summary>Local (not wire) code of <see cref="ContractUnavailableException"/>: the response was not a unified-v1 response.</summary>
    public const string ContractUnavailable = "contract_unavailable";
}

/// <summary>The 6 unified <c>retryAction</c> values (RFC-UAPI-1 §2.3). Only <see cref="SameRequest"/> permits replaying the
/// same bytes (same <c>Idempotency-Key</c>); every other value is advice the caller acts on explicitly.</summary>
public static class UnifiedRetryAction
{
    public const string None = "none";
    public const string SameRequest = "same-request";
    public const string QueryStatus = "query-status";
    public const string Rebind = "rebind";
    public const string Refresh = "refresh";
    public const string Rediscover = "rediscover";
}

/// <summary>Machine-readable <c>detail.reason</c> values (schema <c>UnifiedErrorDetail.reason</c>, revision 7): closure /
/// installation reasons (RFC §1.4) plus the 15 request-header reasons of the <c>Idempotency-Key</c> / <c>If-Match</c> /
/// <c>deadline</c> wiring (RFC §1.2, D27: no new codes, the reason refines an existing code).</summary>
public static class UnifiedErrorReason
{
    public const string NotInstalled = "not_installed";
    public const string OutsideClosure = "outside_closure";
    public const string IdempotencyKeyInvalid = "idempotency_key_invalid";
    public const string IdempotencyKeyNotApplicable = "idempotency_key_not_applicable";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string IdempotencyKeyMismatch = "idempotency_key_mismatch";
    public const string ReceiptNotRetained = "receipt_not_retained";
    public const string ReceiptWindowFull = "receipt_window_full";
    public const string IfMatchInvalid = "if_match_invalid";
    public const string IfMatchNotApplicable = "if_match_not_applicable";
    public const string IfMatchBodyMismatch = "if_match_body_mismatch";
    public const string IfMatchStale = "if_match_stale";
    public const string DeadlineInvalid = "deadline_invalid";
    public const string DeadlineExceeded = "deadline_exceeded";
    public const string HeaderBodyLimit = "header_body_limit";
    public const string HeaderBodyNotCanonical = "header_body_not_canonical";
    public const string HeaderProcessingFailed = "header_processing_failed";

    /// <summary>All 17 values in schema order.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        NotInstalled, OutsideClosure, IdempotencyKeyInvalid, IdempotencyKeyNotApplicable, IdempotencyKeyReused, IdempotencyKeyMismatch,
        ReceiptNotRetained, ReceiptWindowFull, IfMatchInvalid, IfMatchNotApplicable, IfMatchBodyMismatch, IfMatchStale,
        DeadlineInvalid, DeadlineExceeded, HeaderBodyLimit, HeaderBodyNotCanonical, HeaderProcessingFailed,
    };

    /// <summary>True for the 15 request-header reasons (everything except the two closure / installation reasons).</summary>
    public static bool IsRequestHeaderReason(string? reason) =>
        reason != null && reason != NotInstalled && reason != OutsideClosure && Contains(reason);

    internal static bool Contains(string reason)
    {
        for (int i = 0; i < All.Count; i++) if (string.Equals(All[i], reason, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>Typed view of the unified envelope's open <c>detail</c> object (schema <c>UnifiedErrorDetail</c> ∪
/// <c>FacadeErrorDetail</c>). Under D19 the family facts live <em>only</em> here: <see cref="DomainCode"/> /
/// <see cref="DomainStatus"/> / <see cref="DomainRetryAction"/> are the original family error, never the value business
/// code branches on first. Registered keys are validated by <see cref="UnifiedErrorEnvelope"/>; unregistered keys stay
/// reachable through <see cref="Raw"/> / <see cref="TryGet"/>.</summary>
public sealed class UnifiedErrorDetail
{
    /// <summary>Detail of an envelope that carried no <c>detail</c> object at all.</summary>
    public static readonly UnifiedErrorDetail Empty = new UnifiedErrorDetail(null);

    internal UnifiedErrorDetail(JsonElement? raw)
    {
        Raw = raw;
        if (!raw.HasValue) return;
        var value = raw.Value;
        Domain = Text(value, "domain"); Family = Text(value, "family"); DomainCode = Text(value, "domainCode");
        DomainStatus = Integer(value, "domainStatus"); LimitBytes = Integer(value, "limitBytes");
        if (value.TryGetProperty("domainRetryAction", out var action))
        {
            HasDomainRetryAction = true;
            DomainRetryAction = action.ValueKind == JsonValueKind.String ? action.GetString() : null;
        }
        Fallback = Text(value, "fallback"); Reason = Text(value, "reason"); Header = Text(value, "header");
        ClosureId = Text(value, "closureId"); Operation = Text(value, "operation"); State = Text(value, "state");
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static int? Integer(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : (int?)null;

    /// <summary>True when the envelope carried no <c>detail</c> object.</summary>
    public bool IsEmpty => !Raw.HasValue;
    /// <summary>The original <c>detail</c> JSON (open object), or null when absent.</summary>
    public JsonElement? Raw { get; }
    /// <summary>Reads any key of the open object, including unregistered ones.</summary>
    public bool TryGet(string name, out JsonElement value)
    {
        if (Raw.HasValue && Raw.Value.TryGetProperty(name, out value)) return true;
        value = default; return false;
    }

    /// <summary><c>detail.domain</c>: accepting domain when the family translation recorded it.</summary>
    public string? Domain { get; }
    /// <summary><c>detail.family</c>: family schema id of the translated error.</summary>
    public string? Family { get; }
    /// <summary><c>detail.domainCode</c>: the original family code (RFC §2.2). Secondary discriminator under D19; null when
    /// the error was never a family error (facade-owned codes other than <c>closure_stale</c>) or the family had no code.</summary>
    public string? DomainCode { get; }
    /// <summary><c>detail.domainStatus</c>: original family HTTP status (null for facade-owned and request-header errors).</summary>
    public int? DomainStatus { get; }
    /// <summary><c>detail.domainRetryAction</c>: original family retry word; null when absent or when the family envelope had no
    /// retry position (a present-but-null value, see <see cref="HasDomainRetryAction"/>).</summary>
    public string? DomainRetryAction { get; }
    /// <summary>True when <c>domainRetryAction</c> was present on the wire (possibly as JSON null).</summary>
    public bool HasDomainRetryAction { get; }
    /// <summary><c>detail.fallback</c> (cache family): none | legacy-cold. Never acted upon by the unified layer.</summary>
    public string? Fallback { get; }
    /// <summary><c>detail.reason</c>: one of <see cref="UnifiedErrorReason"/>.</summary>
    public string? Reason { get; }
    /// <summary><c>detail.header</c>: request header that triggered the rejection (<c>tansr-closure-id</c>, <c>idempotency-key</c>, <c>if-match</c>, <c>deadline</c>).</summary>
    public string? Header { get; }
    /// <summary><c>detail.limitBytes</c>: body cap of a 413 <c>header_body_limit</c> rejection.</summary>
    public int? LimitBytes { get; }
    /// <summary><c>detail.closureId</c>: current capability closure id on 412 / 403 / 404 closure errors.</summary>
    public string? ClosureId { get; }
    /// <summary><c>detail.operation</c>: closure operation name on 403 / 404 outside_closure.</summary>
    public string? Operation { get; }
    /// <summary><c>detail.state</c>: enabled | disabled | unavailable.</summary>
    public string? State { get; }
}
