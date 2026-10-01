using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Api;

/// <summary>Decoder for the unified error envelope (RFC-UAPI-1 §1.3 / §2; schema <c>UnifiedError</c> and
/// <c>FacadeError</c>, vendored in <c>contract/unified-v1.schema.json</c>). Family decoders call <see cref="TryThrow"/>
/// first; bodies whose <c>contract</c> is not <c>unified-v1</c> (family passthrough such as archive-sync-v1,
/// terminal-services-v1, sdk2-ext-v1) fall through to the family shape unchanged. The checks below mirror the schema's
/// <c>allOf</c> rules (code → status / retryAction binding, facade detail requirements, detail vocabularies) so that a
/// malformed envelope is <c>contract_unavailable</c> rather than a misread business error.</summary>
public static class UnifiedErrorEnvelope
{
    /// <summary>The 19 unified codes (RFC §2.1).</summary>
    public static readonly IReadOnlyList<string> Codes = new[]
    {
        "invalid_request", "protocol_mismatch", "unauthorized", "forbidden", "not_found", "method_not_allowed", "gone", "conflict",
        "stale_generation", "gap", "capability_unavailable", "capacity_exceeded", "payload_too_large", "upstream_unavailable",
        "result_unknown", "rejected", "internal_error", "precondition_failed", "not_canonical",
    };
    /// <summary>The facade-owned subset (RFC §1.3).</summary>
    public static readonly IReadOnlyList<string> FacadeCodes = new[]
    {
        "unauthorized", "not_found", "method_not_allowed", "invalid_request", "capability_unavailable", "capacity_exceeded",
        "precondition_failed", "upstream_unavailable",
    };
    /// <summary>The 6 retry actions (RFC §2.3).</summary>
    public static readonly IReadOnlyList<string> RetryActions = new[] { "none", "same-request", "query-status", "rebind", "refresh", "rediscover" };
    /// <summary>Union of the family retry vocabularies kept in <c>detail.domainRetryAction</c> (schema <c>DomainRetryAction</c>).</summary>
    public static readonly IReadOnlyList<string> DomainRetryActions = new[]
    { "none", "query-status", "rebind", "same-request", "refresh-projection", "backoff", "discover", "reconcile", "refresh" };
    /// <summary>HTTP statuses a unified envelope may carry (schema <c>HttpStatus</c>).</summary>
    public static readonly IReadOnlyList<int> Statuses = new[] { 400, 401, 403, 404, 405, 408, 409, 410, 412, 413, 422, 429, 500, 503 };

    private static readonly Regex DomainCodePattern = new Regex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex FamilyIdPattern = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex HeaderNamePattern = new Regex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool In(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++) if (string.Equals(values[i], value, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Statuses the schema allows for a unified code (UnifiedError <c>allOf</c>).</summary>
    internal static int[] StatusesFor(string code) => code switch
    {
        "invalid_request" => new[] { 400, 408, 422 },
        "protocol_mismatch" => new[] { 400 },
        "unauthorized" => new[] { 401 },
        "forbidden" => new[] { 403 },
        "not_found" => new[] { 404 },
        "method_not_allowed" => new[] { 405 },
        "gone" => new[] { 410 },
        "conflict" => new[] { 409, 412 },
        "stale_generation" => new[] { 409 },
        "gap" => new[] { 409, 410 },
        "capability_unavailable" => new[] { 404, 403 },
        "capacity_exceeded" => new[] { 429, 503 },
        "payload_too_large" => new[] { 413 },
        "upstream_unavailable" => new[] { 503 },
        "result_unknown" => new[] { 503 },
        "rejected" => new[] { 422 },
        "internal_error" => new[] { 500 },
        "precondition_failed" => new[] { 412 },
        "not_canonical" => new[] { 400 },
        _ => Array.Empty<int>(),
    };

    /// <summary>Facade-owned code → (status set, retryAction) binding (FacadeError <c>allOf</c> = facade.ts ERROR_STATUS / ERROR_RETRY).</summary>
    private static (int[] Statuses, string RetryAction) FacadeBinding(string code) => code switch
    {
        "unauthorized" => (new[] { 401 }, "none"),
        "not_found" => (new[] { 404 }, "none"),
        "method_not_allowed" => (new[] { 405 }, "none"),
        "invalid_request" => (new[] { 400 }, "none"),
        "capability_unavailable" => (new[] { 404, 403 }, "none"),
        "capacity_exceeded" => (new[] { 503 }, "same-request"),
        "precondition_failed" => (new[] { 412 }, "rediscover"),
        "upstream_unavailable" => (new[] { 503 }, "same-request"),
        _ => (Array.Empty<int>(), ""),
    };

    /// <summary>True for a closure operation name: every manifest operation except deployment-level discovery reads.</summary>
    internal static bool IsClosureOperation(string name) =>
        ApiRoutes.Find(name) is ApiOperation operation && operation.Domain != "discovery" && name != "session.capabilities";

    /// <summary>True when the parsed body declares itself a unified envelope (<c>contract</c> is the string <c>unified-v1</c>).</summary>
    public static bool IsEnvelope(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("contract", out var contract)
        && contract.ValueKind == JsonValueKind.String && contract.GetString() == ApiRoutes.Contract;

    /// <summary>Throws <see cref="UnifiedApiException"/> when the body is a unified error envelope, or
    /// <see cref="ContractUnavailableException"/> (<c>invalid_error_body</c>) when it claims to be one but is malformed.
    /// Returns false when the body is not a <c>unified-v1</c> envelope so the caller may decode a family shape.</summary>
    public static bool TryThrow(HttpResponseMessage response, JsonElement body, UnifiedResponseMeta meta)
    {
        if (!IsEnvelope(body)) return false;
        int status = (int)response.StatusCode;
        foreach (var key in new[] { "traceId", "requestId", "code", "status", "retryAction", "message" })
            if (!body.TryGetProperty(key, out _)) throw Invalid(status);
        foreach (var property in body.EnumerateObject())
            switch (property.Name)
            {
                case "contract": case "traceId": case "requestId": case "code": case "status": case "retryAction": case "retryAfterMs": case "message": case "detail": break;
                default: throw Invalid(status);
            }
        var traceId = body.GetProperty("traceId");
        if (traceId.ValueKind != JsonValueKind.String || !UnifiedHeaders.TraceId.IsMatch(traceId.GetString()!)) throw Invalid(status);
        var requestIdValue = body.GetProperty("requestId");
        string? requestId = null;
        if (requestIdValue.ValueKind == JsonValueKind.String)
        {
            requestId = requestIdValue.GetString()!;
            if (requestId.Length == 0 || requestId.Length > 128) throw Invalid(status);
            foreach (var c in requestId) if (c < 0x21 || c > 0x7e) throw Invalid(status);
        }
        else if (requestIdValue.ValueKind != JsonValueKind.Null) throw Invalid(status);
        var codeValue = body.GetProperty("code");
        if (codeValue.ValueKind != JsonValueKind.String || !In(Codes, codeValue.GetString()!)) throw Invalid(status);
        var code = codeValue.GetString()!;
        var statusValue = body.GetProperty("status");
        if (statusValue.ValueKind != JsonValueKind.Number || !statusValue.TryGetInt32(out var declared) || declared != status) throw Invalid(status);
        if (Array.IndexOf(StatusesFor(code), declared) < 0 || !Contains(Statuses, declared)) throw Invalid(status);
        var retryValue = body.GetProperty("retryAction");
        if (retryValue.ValueKind != JsonValueKind.String || !In(RetryActions, retryValue.GetString()!)) throw Invalid(status);
        var retryAction = retryValue.GetString()!;
        // Code-bound retry semantics (RFC §2.3 / D7): result_unknown never replays the same request; precondition_failed
        // always rediscovers; not_canonical never retries the same bytes.
        if (code == "result_unknown" && retryAction != "query-status" && retryAction != "rebind") throw Invalid(status);
        if (code == "precondition_failed" && retryAction != "rediscover") throw Invalid(status);
        if (code == "not_canonical" && retryAction != "none") throw Invalid(status);
        int? retryAfterMs = null;
        if (body.TryGetProperty("retryAfterMs", out var retryAfter))
        {
            if (retryAfter.ValueKind != JsonValueKind.Number || !retryAfter.TryGetInt32(out var ms) || ms < 1) throw Invalid(status);
            retryAfterMs = ms;
        }
        var message = body.GetProperty("message");
        if (message.ValueKind != JsonValueKind.String || message.GetString()!.Length == 0 || message.GetString()!.Length > 1024) throw Invalid(status);
        JsonElement? detail = null;
        if (body.TryGetProperty("detail", out var detailValue))
        {
            if (detailValue.ValueKind != JsonValueKind.Object) throw Invalid(status);
            ValidateDetail(detailValue, status);
            detail = detailValue.Clone();
        }
        // Facade-owned envelopes never carry a client idempotency key, use the 8-code subset and carry no translated
        // family facts (RFC §1.3); domain-translated envelopes keep the original status in detail.domainStatus.
        bool facadeOwned = requestId == null && In(FacadeCodes, code) && (detail == null || !detail.Value.TryGetProperty("domainStatus", out _));
        if (facadeOwned) ValidateFacade(code, declared, retryAction, retryAfterMs, detail, status);
        throw new UnifiedApiException(status, code, retryAction, retryAfterMs, traceId.GetString()!, requestId, detail, meta, facadeOwned);
    }

    private static bool Contains(IReadOnlyList<int> values, int value)
    {
        for (int i = 0; i < values.Count; i++) if (values[i] == value) return true;
        return false;
    }

    /// <summary>Open object; the registered keys must carry their schema shape (UnifiedErrorDetail).</summary>
    private static void ValidateDetail(JsonElement detail, int status)
    {
        foreach (var property in detail.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "domain": if (!IsString(value) || ApiRoutes.DomainFamily(value.GetString()!) == null && value.GetString() != "discovery") throw Invalid(status); break;
                case "family": if (!IsString(value) || value.GetString()!.Length > 64 || !FamilyIdPattern.IsMatch(value.GetString()!)) throw Invalid(status); break;
                case "domainCode": if (!IsString(value) || !DomainCodePattern.IsMatch(value.GetString()!)) throw Invalid(status); break;
                case "domainStatus": if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var domainStatus) || domainStatus < 100 || domainStatus > 599) throw Invalid(status); break;
                case "domainRetryAction": if (value.ValueKind != JsonValueKind.Null && (!IsString(value) || !In(DomainRetryActions, value.GetString()!))) throw Invalid(status); break;
                case "fallback": if (!IsString(value) || value.GetString() != "none" && value.GetString() != "legacy-cold") throw Invalid(status); break;
                case "reason": if (!IsString(value) || value.GetString() != "not_installed" && value.GetString() != "outside_closure") throw Invalid(status); break;
                case "header": if (!IsString(value) || !HeaderNamePattern.IsMatch(value.GetString()!)) throw Invalid(status); break;
                case "closureId": if (!IsString(value) || !UnifiedHeaders.Digest.IsMatch(value.GetString()!)) throw Invalid(status); break;
                case "operation": if (!IsString(value) || !IsClosureOperation(value.GetString()!)) throw Invalid(status); break;
                case "state": if (!IsString(value) || value.GetString() != "enabled" && value.GetString() != "disabled" && value.GetString() != "unavailable") throw Invalid(status); break;
                default: break; // additionalProperties: true
            }
        }
    }

    /// <summary>FacadeError <c>allOf</c>: code → status / retryAction binding, no retryAfterMs (seconds travel in
    /// <c>retry-after</c>), precondition_failed and closure (403 / 404 outside_closure) detail requirements.</summary>
    private static void ValidateFacade(string code, int declared, string retryAction, int? retryAfterMs, JsonElement? detail, int status)
    {
        var binding = FacadeBinding(code);
        if (Array.IndexOf(binding.Statuses, declared) < 0 || retryAction != binding.RetryAction || retryAfterMs != null) throw Invalid(status);
        string? Text(string name) => detail.HasValue && detail.Value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        if (code == "precondition_failed" && (Text("domainCode") != "closure_stale" || Text("closureId") == null)) throw Invalid(status);
        if (declared == 403 && (Text("reason") != "outside_closure" || Text("operation") == null || Text("state") != "disabled" || Text("closureId") == null)) throw Invalid(status);
        if (declared == 404 && Text("reason") == "outside_closure" && (Text("operation") == null || Text("state") != "unavailable" || Text("closureId") == null)) throw Invalid(status);
        var state = Text("state");
        if (state != null && state != "disabled" && state != "unavailable") throw Invalid(status);
    }

    private static bool IsString(JsonElement value) => value.ValueKind == JsonValueKind.String;

    private static ContractUnavailableException Invalid(int status) => new ContractUnavailableException("invalid_error_body", status);
}
