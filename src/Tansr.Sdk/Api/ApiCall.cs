using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Api;

/// <summary>Request-side header vocabulary of RFC-UAPI-1 §1.2 wired in 0.14.0 (U7-HDR): <c>Idempotency-Key</c> →
/// family <c>requestId</c> (same key + same bytes → the original receipt, side effect exactly once; same key + different
/// bytes → 409 <c>conflict</c>), <c>If-Match</c> → family <c>expectedRevision</c> (mismatch → 412 <c>precondition_failed</c>
/// / <c>refresh</c>, nothing written) with <c>ETag</c> derived on versioned resources, and <c>deadline</c> (RFC-3339;
/// already past on arrival → 408 <c>invalid_request</c>, never retried or extended). Validation here mirrors
/// <c>@tansr/api-client/api</c> so the same request is rejected locally before a round trip that must fail.</summary>
public static class UnifiedRequestHeaders
{
    public const string IdempotencyKey = "Idempotency-Key";
    public const string IfMatch = "If-Match";
    public const string ETag = "ETag";
    public const string Deadline = "deadline";

    /// <summary>Schema <c>IdempotencyKey</c>: 1–128 printable ASCII (0x21–0x7e).</summary>
    public static readonly Regex IdempotencyKeyPattern = new Regex("^[\\x21-\\x7e]{1,128}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    /// <summary>Strong validator <c>"&lt;decimal revision&gt;"</c> — the only <c>ETag</c> form the facade derives and the only <c>If-Match</c> form it accepts.</summary>
    public static readonly Regex StrongETagPattern = new Regex("^\"(0|[1-9][0-9]{0,18})\"$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex BareRevisionPattern = new Regex("^(0|[1-9][0-9]{0,18})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    /// <summary>RFC-3339 date-time (case-insensitive <c>T</c> / <c>Z</c>, ≤ 9 fractional digits, <c>±hh:mm</c>); epoch milliseconds are not accepted.</summary>
    public static readonly Regex Rfc3339Pattern = new Regex("^\\d{4}-\\d{2}-\\d{2}[Tt]\\d{2}:\\d{2}:\\d{2}(\\.\\d{1,9})?([Zz]|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True when <paramref name="value"/> is a well-formed idempotency key.</summary>
    public static bool IsIdempotencyKey(string? value) => value != null && IdempotencyKeyPattern.IsMatch(value);

    /// <summary><c>If-Match</c> option → header text: a strong validator is kept, a bare decimal revision is quoted, anything else
    /// (weak validators, <c>*</c>, lists) → null.</summary>
    public static string? NormalizeIfMatch(string? value)
    {
        if (value == null) return null;
        var text = value.Trim();
        if (StrongETagPattern.IsMatch(text)) return text;
        return BareRevisionPattern.IsMatch(text) ? "\"" + text + "\"" : null;
    }

    /// <summary>Strong <c>ETag</c> text → its decimal revision (<c>"7"</c> → <c>7</c>), or null for any other form.</summary>
    public static string? RevisionOf(string? etag)
    {
        if (etag == null) return null;
        var text = etag.Trim();
        return StrongETagPattern.IsMatch(text) ? text.Substring(1, text.Length - 2) : null;
    }

    /// <summary>Header text for a deadline: UTC RFC-3339 with millisecond precision (<c>2026-10-03T08:00:00.000Z</c>).</summary>
    public static string FormatDeadline(DateTimeOffset deadline) => deadline.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses RFC-3339 header text; null when the form (or the instant) is not acceptable to the facade.</summary>
    public static DateTimeOffset? ParseDeadline(string? text)
    {
        if (text == null) return null;
        var trimmed = text.Trim();
        if (!Rfc3339Pattern.IsMatch(trimmed)) return null;
        return DateTimeOffset.TryParse(trimmed.ToUpperInvariant(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : (DateTimeOffset?)null;
    }
}

/// <summary>Per-call options of <see cref="TansrClient.CallAsync"/> (mirrors <c>ApiCallOptions</c> of <c>@tansr/api-client/api</c>).
/// Reusing the <em>same instance</em> for a replay is what makes the replay carry the same <c>Idempotency-Key</c>, the same
/// body and the same <c>deadline</c> (deadlines are never extended by a retry).</summary>
public sealed class ApiCallOptions
{
    /// <summary>Template placeholders (<see cref="ApiOperation.Path"/>).</summary>
    public string? Id { get; set; }
    public string? TargetId { get; set; }
    public string? UploadId { get; set; }
    public string? TicketId { get; set; }
    /// <summary>Query parameters; keys must be in the operation's whitelist (<see cref="ApiOperation.Query"/>).</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Query { get; set; }
    /// <summary>JSON body. Strict families (sdk2-ext / recovery / archive-sync / cache / terminal) are encoded canonically
    /// (RFC-UAPI-1 §4); <c>agent-session-v1</c> and facade-owned operations as plain UTF-8 JSON.</summary>
    public JsonElement? Body { get; set; }
    /// <summary>Raw body bytes (<c>application/octet-stream</c> unless <see cref="ContentType"/> says otherwise); exclusive with <see cref="Body"/>.</summary>
    public byte[]? RawBody { get; set; }
    public string? ContentType { get; set; }
    /// <summary>Closure precondition → <c>tansr-closure-id</c> (session-scoped guarded writes; stale → 412 <c>rediscover</c>).</summary>
    public string? ClosureId { get; set; }
    /// <summary>Write-operation idempotency key → <c>Idempotency-Key</c> (1–128 printable ASCII). Rejected locally on reads / streams
    /// (<c>invalid_idempotency_key</c>) because the facade answers 400 <c>idempotency_key_not_applicable</c>.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>Resource precondition → <c>If-Match</c>: the <c>ETag</c> of the last read (<c>"&lt;revision&gt;"</c>) or a bare decimal
    /// revision. Only operations with an <c>expectedRevision</c> position accept it (<see cref="ApiOperation.AcceptsIfMatch"/>).</summary>
    public string? IfMatch { get; set; }
    /// <summary>Absolute request deadline → <c>deadline</c> (RFC-3339). Already past when sending → local <c>deadline_exceeded</c>, not sent;
    /// past on arrival → 408 <c>invalid_request</c> (<c>reason: deadline_exceeded</c>). A replay reuses the same instant.</summary>
    public DateTimeOffset? Deadline { get; set; }
    /// <summary>Response body cap for this call (default: client <c>MaxResponseBytes</c>).</summary>
    public int? MaxResponseBytes { get; set; }
}

/// <summary>Result of one <see cref="TansrClient.CallAsync"/>: HTTP status, parsed JSON body (null for 204 / empty) and the
/// unified response metadata including <see cref="UnifiedResponseMeta.ETag"/>. <c>202</c> means accepted, not completed.</summary>
public sealed class ApiCallResult
{
    internal ApiCallResult(int status, JsonElement? body, UnifiedResponseMeta meta) { Status = status; Body = body; Meta = meta; }
    public int Status { get; }
    public JsonElement? Body { get; }
    public UnifiedResponseMeta Meta { get; }
    /// <summary>Strong <c>ETag</c> of a versioned resource, ready to be passed back as <see cref="ApiCallOptions.IfMatch"/>; null otherwise.</summary>
    public string? ETag => Meta.ETag;
}

/// <summary>Validated request-side conditions attached to one transport send (built by <see cref="TansrClient.CallAsync"/>).</summary>
internal sealed class ApiRequestConditions
{
    internal ApiRequestConditions(string? idempotencyKey, string? ifMatch, DateTimeOffset? deadline)
    { IdempotencyKey = idempotencyKey; IfMatch = ifMatch; Deadline = deadline; }
    internal string? IdempotencyKey { get; }
    internal string? IfMatch { get; }
    internal DateTimeOffset? Deadline { get; }
    internal bool IsEmpty => IdempotencyKey == null && IfMatch == null && !Deadline.HasValue;

    /// <summary>Validates options against the operation the way the facade would (D27 reasons), so a request that must fail is
    /// rejected locally with the same vocabulary as <c>@tansr/api-client/api</c>.</summary>
    internal static ApiRequestConditions From(ApiOperation operation, ApiCallOptions options)
    {
        string? key = options.IdempotencyKey, ifMatch = null;
        if (key != null)
        {
            if (!UnifiedRequestHeaders.IsIdempotencyKey(key)) throw new TansrProtocolException("invalid_idempotency_key");
            if (!operation.AcceptsIdempotencyKey) throw new TansrProtocolException("invalid_idempotency_key");
        }
        if (options.IfMatch != null)
        {
            ifMatch = UnifiedRequestHeaders.NormalizeIfMatch(options.IfMatch) ?? throw new TansrProtocolException("invalid_if_match");
            if (!operation.AcceptsIfMatch) throw new TansrProtocolException("if_match_not_applicable");
        }
        if (options.Deadline.HasValue && (options.Deadline.Value == DateTimeOffset.MinValue || options.Deadline.Value == DateTimeOffset.MaxValue))
            throw new TansrProtocolException("invalid_deadline");
        return new ApiRequestConditions(key, ifMatch, options.Deadline);
    }
}
