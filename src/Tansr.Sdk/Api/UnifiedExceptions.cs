using System.Collections.Generic;
using System.Text.Json;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Api;

/// <summary>An <c>/api</c> response did not carry a usable <c>tansr-contract: unified-v1</c> header set. The SDK never
/// falls back to legacy prefixes or family decoders on this signal (RFC-UAPI-1, 手册 §16.6 纪律二).
/// <see cref="TansrException.Code"/> is always <c>contract_unavailable</c>; <see cref="Reason"/> refines it.</summary>
public sealed class ContractUnavailableException : TansrException
{
    public ContractUnavailableException(string reason, int statusCode) : base(UnifiedErrorCode.ContractUnavailable)
    { Reason = reason; StatusCode = statusCode; }
    /// <summary>missing_contract_header | contract_mismatch | invalid_contract_headers | non_json_body | invalid_json | invalid_error_body</summary>
    public string Reason { get; }
    public int StatusCode { get; }
}

/// <summary>A unified error envelope (<c>contract: unified-v1</c>) decoded from an <c>/api</c> error response. This is the
/// public error face of the SDK (开发方案 §0 D19, 手册 §16.5): <see cref="TansrException.Code"/> is one of the 19
/// <see cref="UnifiedErrorCode"/> values, <see cref="RetryAction"/> one of the 6 <see cref="UnifiedRetryAction"/> values,
/// <see cref="TansrHttpException.StatusCode"/> the unified status. The original family error is <em>not</em> the primary
/// discriminator any more: it is kept as <see cref="Detail"/> (<c>detail.domainCode / domainStatus / domainRetryAction</c>)
/// for callers that need a family-specific distinction after branching on <c>Code</c>.
/// <para>Consumption rule: <c>switch (error.Code)</c> first; inside a branch, refine with <c>error.Detail.DomainCode</c>
/// or <c>error.Detail.Reason</c>. Never replay a request unless <see cref="RetryAction"/> is <c>same-request</c> and the
/// original <c>Idempotency-Key</c> is reused (see <see cref="UnifiedRetry"/>).</para></summary>
public sealed class UnifiedApiException : TansrHttpException
{
    public UnifiedApiException(int status, string code, string retryAction, int? retryAfterMs, string traceId, string? requestId,
        JsonElement? detail, UnifiedResponseMeta meta, bool facadeOwned, string? message = null)
        : base(status, code, reason: Text(detail, "reason"), retryAction: retryAction,
            retryAfterMs: retryAfterMs ?? (meta.RetryAfterSeconds.HasValue ? meta.RetryAfterSeconds.Value * 1000 : (int?)null), inputOutcome: Text(detail, "outcome"))
    {
        TraceId = traceId; RequestId = requestId; Meta = meta; FacadeOwned = facadeOwned; ServerMessage = message;
        Detail = detail.HasValue ? new UnifiedErrorDetail(detail) : UnifiedErrorDetail.Empty;
        ClosureId = Detail.ClosureId ?? meta.ClosureId;
    }

    /// <summary>Unified retry action (never null for an envelope; RFC §2.3).</summary>
    public new string RetryAction => base.RetryAction!;
    /// <summary>Observability correlation id (envelope <c>traceId</c> = response <c>x-request-id</c>).</summary>
    public string TraceId { get; }
    /// <summary>Client idempotency key echoed by the server (<c>Idempotency-Key</c> / body <c>requestId</c>); null when the request carried none.</summary>
    public string? RequestId { get; }
    /// <summary>Server <c>message</c> (≤ 1024 chars, never a token or request body); null when the decoder was given none.</summary>
    public string? ServerMessage { get; }
    /// <summary>Typed <c>detail</c> (never null; <see cref="UnifiedErrorDetail.Empty"/> when absent). Family facts live here.</summary>
    public UnifiedErrorDetail Detail { get; }
    public UnifiedResponseMeta Meta { get; }
    /// <summary>True when the envelope was produced by the facade itself (requestId null, 8-code vocabulary, no family translation).</summary>
    public bool FacadeOwned { get; }
    /// <summary>Accepting domain (<c>tansr-domain</c>).</summary>
    public string Domain => Meta.Domain;
    /// <summary>Current closure id for closure errors: <c>detail.closureId</c> first, then the <c>tansr-closure-id</c> header; else null.</summary>
    public string? ClosureId { get; }
    private static string? Text(JsonElement? detail, string name) =>
        detail.HasValue && detail.Value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    // ---- D19 bridge: family facts forwarded from Detail. These exist so pre-D19 catch sites keep compiling; they are not
    // the primary discriminator. A missing family fact falls back to the unified value (what the family would have said).
    /// <summary>Bridge: <c>Detail.DomainCode ?? Code</c>. Prefer <c>Detail.DomainCode</c> (null when no family code exists).</summary>
    public override string DomainCode => Detail.DomainCode ?? Code;
    /// <summary>Bridge: <c>Detail.DomainStatus ?? StatusCode</c>.</summary>
    public override int DomainStatus => Detail.DomainStatus ?? StatusCode;
    /// <summary>Bridge: <c>Detail.DomainRetryAction</c> when the key was present on the wire (possibly null), else <see cref="RetryAction"/>.</summary>
    public override string? DomainRetryAction => Detail.HasDomainRetryAction ? Detail.DomainRetryAction : RetryAction;
    /// <summary>Bridge: cache family <c>detail.fallback</c> (none | legacy-cold).</summary>
    public string? Fallback => Detail.Fallback;
    /// <summary>Bridge: <c>detail.family</c>.</summary>
    public string? Family => Detail.Family;
    /// <summary>Bridge: <c>detail.header</c>.</summary>
    public string? Header => Detail.Header;
    /// <summary>Bridge: <c>detail.operation</c>.</summary>
    public string? Operation => Detail.Operation;
    /// <summary>Bridge: <c>detail.state</c>.</summary>
    public string? State => Detail.State;

    /// <summary><c>retryAction: rediscover</c> (412 <c>closure_stale</c>): the caller must re-read the capability closure before
    /// retrying. Not raised by <c>precondition_failed / refresh</c> (<c>If-Match</c> stale), which asks for a re-read of the resource.</summary>
    public bool RequiresRediscovery => RetryAction == UnifiedRetryAction.Rediscover;
    /// <summary><c>retryAction: refresh</c> (e.g. 412 <c>if_match_stale</c>): re-read the resource, then decide.</summary>
    public bool RequiresRefresh => RetryAction == UnifiedRetryAction.Refresh;
    /// <summary>Normalised retry advice (RFC §2.3; see <see cref="UnifiedRetry.Advice(System.Exception)"/>).</summary>
    public UnifiedRetryAdvice Advice => UnifiedRetry.Advice(this);
}

/// <summary>The request asked for <c>tansr-event-envelope: unified-v1</c> but the SSE response did not echo it; frames
/// are therefore family-native and the caller's envelope expectation cannot be met.</summary>
public sealed class EnvelopeNotNegotiatedException : TansrException
{
    public EnvelopeNotNegotiatedException() : base("envelope_not_negotiated") { }
}

/// <summary>Normalised retry advice for one failure (RFC-UAPI-1 §2.3; mirrors <c>@tansr/api-client/api</c> <c>retryAdvice</c>).
/// Only <see cref="Replayable"/> permits replaying the identical request; every other action is returned, never executed.</summary>
public sealed class UnifiedRetryAdvice
{
    internal UnifiedRetryAdvice(string action, bool stated, string? domainRetryAction, int? retryAfterMs, string? closureId, string source)
    { Action = action; Stated = stated; DomainRetryAction = domainRetryAction; RetryAfterMs = retryAfterMs; ClosureId = closureId; Source = source; }
    /// <summary>Unified action (<see cref="UnifiedRetryAction"/>).</summary>
    public string Action { get; }
    /// <summary>True when the server stated an action (family envelopes without a retry position → false; never infer replay).</summary>
    public bool Stated { get; }
    /// <summary>Original family word (unified envelope → <c>detail.domainRetryAction</c> or the unified action; absent → null).</summary>
    public string? DomainRetryAction { get; }
    /// <summary>Milliseconds to wait before acting (<c>retryAfterMs</c> or <c>retry-after</c>); null when the server gave none.</summary>
    public int? RetryAfterMs { get; }
    /// <summary>Current closure id when <see cref="Action"/> is <c>rediscover</c>; else null.</summary>
    public string? ClosureId { get; }
    /// <summary>unified | domain | none</summary>
    public string Source { get; }
    /// <summary><c>same-request</c>, stated by the server, and not an unknown-outcome code: the same bytes may be replayed once.</summary>
    public bool Replayable => Stated && Action == UnifiedRetryAction.SameRequest;
    public static readonly UnifiedRetryAdvice None = new UnifiedRetryAdvice(UnifiedRetryAction.None, false, null, null, null, "none");
}

/// <summary>Retry discipline helpers (RFC-UAPI-1 §2.3, 手册 §16.6 第 3 条). Nothing here performs I/O on its own; the
/// replay helper calls back into the caller's own send delegate so the identical <c>Idempotency-Key</c> / body is reused.</summary>
public static class UnifiedRetry
{
    /// <summary>Family retry words → unified action (unknown word → none).</summary>
    public static readonly IReadOnlyDictionary<string, string> DomainRetryActionMap = new Dictionary<string, string>(System.StringComparer.Ordinal)
    {
        ["none"] = UnifiedRetryAction.None,
        ["same-request"] = UnifiedRetryAction.SameRequest,
        ["backoff"] = UnifiedRetryAction.SameRequest,
        ["query-status"] = UnifiedRetryAction.QueryStatus,
        ["reconcile"] = UnifiedRetryAction.QueryStatus,
        ["rebind"] = UnifiedRetryAction.Rebind,
        ["refresh"] = UnifiedRetryAction.Refresh,
        ["refresh-projection"] = UnifiedRetryAction.Refresh,
        ["discover"] = UnifiedRetryAction.Rediscover,
        ["rediscover"] = UnifiedRetryAction.Rediscover,
    };

    /// <summary>Codes whose side effect is unknown: only <c>query-status</c> / <c>rebind</c>, never a replay under a new key.</summary>
    public static bool IsResultUnknown(string? code) => code == UnifiedErrorCode.ResultUnknown || code == "commit_unknown";

    /// <summary>Normalises <see cref="UnifiedApiException"/> (unified action) and family <see cref="TansrHttpException"/>
    /// (family word → unified action) into advice; anything else (local errors, contract unavailable) → <see cref="UnifiedRetryAdvice.None"/>.</summary>
    public static UnifiedRetryAdvice Advice(System.Exception error)
    {
        if (error is UnifiedApiException unified)
        {
            bool unknownEffect = IsResultUnknown(unified.Code) || IsResultUnknown(unified.Detail.DomainCode);
            var action = unknownEffect && unified.RetryAction == UnifiedRetryAction.SameRequest ? UnifiedRetryAction.QueryStatus : unified.RetryAction;
            return new UnifiedRetryAdvice(action, true, unified.Detail.HasDomainRetryAction ? unified.Detail.DomainRetryAction : unified.RetryAction, unified.RetryAfterMs,
                action == UnifiedRetryAction.Rediscover ? unified.ClosureId : null, "unified");
        }
        if (error is TansrHttpException family)
        {
            bool stated = family.RetryAction != null;
            var mapped = stated && DomainRetryActionMap.TryGetValue(family.RetryAction!, out var value) ? value : UnifiedRetryAction.None;
            var action = IsResultUnknown(family.Code) && mapped == UnifiedRetryAction.SameRequest ? UnifiedRetryAction.QueryStatus : mapped;
            return new UnifiedRetryAdvice(action, stated, family.RetryAction, family.RetryAfterMs, null, "domain");
        }
        return UnifiedRetryAdvice.None;
    }

    /// <summary>The idempotency key a call carried: <see cref="ApiCallOptions.IdempotencyKey"/> first, else the JSON body value at the
    /// operation family's <c>requestIdPath</c> (manifest revision 7, <see cref="ApiRoutes.FamilyRequestIdPath"/>: the SDK2 families read
    /// <c>request.requestId</c>, the terminal families <c>requestId</c>, a family without such a position — agent-session-v1,
    /// archive-sync-v1 — recognises the header only). A raw body, a non-object on the path or an empty string → null. This is the
    /// position the facade maps <c>Idempotency-Key</c> onto, so a key read anywhere else would not identify the same request.</summary>
    public static string? IdempotencyKeyOf(ApiOperation operation, ApiCallOptions options)
    {
        if (operation == null) throw new System.ArgumentNullException(nameof(operation));
        if (options == null) throw new System.ArgumentNullException(nameof(options));
        if (!string.IsNullOrEmpty(options.IdempotencyKey)) return options.IdempotencyKey;
        var path = operation.Family == null ? null : ApiRoutes.FamilyRequestIdPath(operation.Family);
        if (path == null || options.RawBody != null || options.Body is not JsonElement node) return null;
        foreach (var key in path)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out var next)) return null;
            node = next;
        }
        return node.ValueKind == JsonValueKind.String && node.GetString()!.Length > 0 ? node.GetString() : null;
    }

    /// <summary>Replays <em>the same request once</em> — only when the server stated <c>same-request</c> (never for unknown outcomes),
    /// the original call carried an idempotency key, the stated wait fits <paramref name="maxWait"/> (default 30 s) and the original
    /// <see cref="ApiCallOptions.Deadline"/> would not be crossed after waiting (deadlines are never extended; RFC §1.2). Uses the
    /// same <paramref name="options"/> instance, so key, body, precondition and deadline are byte-identical. Local refusals:
    /// <c>not_retryable</c>, <c>retry_key_missing</c>, <c>retry_after_exceeds_budget</c>, <c>deadline_exceeded</c>.</summary>
    public static async System.Threading.Tasks.Task<ApiCallResult> RetrySameRequestAsync(TansrClient client, ApiOperation operation, ApiCallOptions options,
        System.Exception error, System.TimeSpan? maxWait = null, System.Threading.CancellationToken cancellationToken = default)
    {
        if (client == null) throw new System.ArgumentNullException(nameof(client));
        if (operation == null) throw new System.ArgumentNullException(nameof(operation));
        if (options == null) throw new System.ArgumentNullException(nameof(options));
        var advice = Advice(error);
        if (!advice.Replayable) throw new TansrProtocolException("not_retryable");
        if (IdempotencyKeyOf(operation, options) == null) throw new TansrProtocolException("retry_key_missing");
        var budget = maxWait ?? System.TimeSpan.FromSeconds(30);
        if (budget < System.TimeSpan.Zero) throw new System.ArgumentOutOfRangeException(nameof(maxWait));
        var wait = System.TimeSpan.FromMilliseconds(advice.RetryAfterMs ?? 0);
        if (wait > budget) throw new TansrProtocolException("retry_after_exceeds_budget");
        if (options.Deadline is System.DateTimeOffset deadline && System.DateTimeOffset.UtcNow + wait >= deadline) throw new TansrProtocolException("deadline_exceeded");
        if (wait > System.TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await client.CallAsync(operation, options, cancellationToken).ConfigureAwait(false);
    }
}
