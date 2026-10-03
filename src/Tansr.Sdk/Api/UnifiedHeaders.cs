using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Tansr.Sdk.Api;

/// <summary>Header vocabulary of the unified <c>/api</c> contract (RFC-UAPI-1 §1.1–§1.2, 手册 §16.4).</summary>
public static class UnifiedHeaders
{
    public const string Contract = "tansr-contract";
    public const string ManifestRevision = "tansr-manifest-revision";
    public const string Domain = "tansr-domain";
    public const string SchemaHash = "tansr-schema-hash";
    public const string ClosureId = "tansr-closure-id";
    public const string EventEnvelope = "tansr-event-envelope";
    public const string SessionFamily = "tansr-session-family";
    public const string RequestId = "x-request-id";
    public const string RetryAfter = "retry-after";
    /// <summary>Only value of <see cref="EventEnvelope"/> on both request and response.</summary>
    public const string EventEnvelopeContract = "unified-v1";

    internal static readonly Regex Digest = new Regex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static readonly Regex SchemaHashValue = new Regex("^(sha256:[a-f0-9]{64}|none)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static readonly Regex Revision = new Regex("^[1-9][0-9]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static readonly Regex TraceId = new Regex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Session family wire values for <see cref="SessionFamily"/>.</summary>
    public static string FamilyValue(Client.SessionContract contract) =>
        contract == Client.SessionContract.Sdk2OffloadV1 ? "sdk2-offload-v1" : "sdk1";
}

/// <summary>The four mandatory response headers plus optional ones, read from one <c>/api</c> response.</summary>
public sealed class UnifiedResponseMeta
{
    private UnifiedResponseMeta(string contract, int manifestRevision, string domain, string schemaHash, string? closureId, string? eventEnvelope, string? requestId, int? retryAfterSeconds, string? etag)
    { Contract = contract; ManifestRevision = manifestRevision; Domain = domain; SchemaHash = schemaHash; ClosureId = closureId; EventEnvelope = eventEnvelope; RequestId = requestId; RetryAfterSeconds = retryAfterSeconds; ETag = etag; }

    public string Contract { get; }
    public int ManifestRevision { get; }
    public string Domain { get; }
    /// <summary><c>sha256:&lt;hex&gt;</c> or <c>none</c>.</summary>
    public string SchemaHash { get; }
    public string? ClosureId { get; }
    public string? EventEnvelope { get; }
    public string? RequestId { get; }
    public int? RetryAfterSeconds { get; }
    /// <summary>Strong validator <c>"&lt;revision&gt;"</c> the facade derives for versioned resources (revision 7 <c>etagPath</c>);
    /// null when absent or not in the strong decimal form. Feed it back as <c>If-Match</c> for a conditional write.</summary>
    public string? ETag { get; }
    public bool EventEnvelopeNegotiated => EventEnvelope == UnifiedHeaders.EventEnvelopeContract;

    private static string? Single(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        string? found = null;
        foreach (var value in values)
        {
            if (found != null) throw new ContractUnavailableException("invalid_contract_headers", (int)response.StatusCode);
            found = value.Trim();
        }
        return found;
    }

    /// <summary>Validates and reads the unified headers. Missing or foreign <c>tansr-contract</c> is never a fallback
    /// signal: it raises <see cref="ContractUnavailableException"/> (D10, 手册 §16.6 纪律二).</summary>
    public static UnifiedResponseMeta Read(HttpResponseMessage response)
    {
        if (response == null) throw new ArgumentNullException(nameof(response));
        int status = (int)response.StatusCode;
        var contract = Single(response, UnifiedHeaders.Contract);
        if (contract == null) throw new ContractUnavailableException("missing_contract_header", status);
        if (contract != ApiRoutes.Contract) throw new ContractUnavailableException("contract_mismatch", status);
        var revisionText = Single(response, UnifiedHeaders.ManifestRevision);
        var domain = Single(response, UnifiedHeaders.Domain);
        var schemaHash = Single(response, UnifiedHeaders.SchemaHash);
        if (revisionText == null || domain == null || schemaHash == null ||
            !UnifiedHeaders.Revision.IsMatch(revisionText) || revisionText.Length > 9 ||
            !UnifiedHeaders.SchemaHashValue.IsMatch(schemaHash) || !IsDomain(domain))
            throw new ContractUnavailableException("invalid_contract_headers", status);
        var closureId = Single(response, UnifiedHeaders.ClosureId);
        if (closureId != null && !UnifiedHeaders.Digest.IsMatch(closureId)) throw new ContractUnavailableException("invalid_contract_headers", status);
        var envelope = Single(response, UnifiedHeaders.EventEnvelope);
        if (envelope != null && envelope != UnifiedHeaders.EventEnvelopeContract) throw new ContractUnavailableException("invalid_contract_headers", status);
        // RFC §1.1: the response header vocabulary is closed on the tansr-* prefix and never carries identity
        // (endUserId / sessionId / tokens); any other tansr-* header is a contract violation, not an extension point.
        foreach (var header in response.Headers)
            if (header.Key.StartsWith("tansr-", StringComparison.OrdinalIgnoreCase) && !IsKnownHeader(header.Key))
                throw new ContractUnavailableException("invalid_contract_headers", status);
        var requestId = Single(response, UnifiedHeaders.RequestId);
        if (requestId != null && !UnifiedHeaders.TraceId.IsMatch(requestId)) requestId = null;
        int? retryAfter = null;
        var retryText = Single(response, UnifiedHeaders.RetryAfter);
        if (retryText != null)
        {
            if (!UnifiedHeaders.Revision.IsMatch(retryText) || retryText.Length > 9) throw new ContractUnavailableException("invalid_contract_headers", status);
            retryAfter = int.Parse(retryText, CultureInfo.InvariantCulture);
        }
        // ETag is only meaningful in the strong decimal form the facade derives; anything else is not a revision and is dropped.
        string? etag = null;
        if (response.Headers.TryGetValues(UnifiedRequestHeaders.ETag, out var etags))
        {
            int count = 0;
            foreach (var value in etags) { count++; var text = value.Trim(); etag = UnifiedRequestHeaders.StrongETagPattern.IsMatch(text) ? text : null; }
            if (count != 1) etag = null;
        }
        return new UnifiedResponseMeta(contract, int.Parse(revisionText, CultureInfo.InvariantCulture), domain, schemaHash, closureId, envelope, requestId, retryAfter, etag);
    }

    private static bool IsKnownHeader(string name) =>
        string.Equals(name, UnifiedHeaders.Contract, StringComparison.OrdinalIgnoreCase) || string.Equals(name, UnifiedHeaders.ManifestRevision, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, UnifiedHeaders.Domain, StringComparison.OrdinalIgnoreCase) || string.Equals(name, UnifiedHeaders.SchemaHash, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, UnifiedHeaders.ClosureId, StringComparison.OrdinalIgnoreCase) || string.Equals(name, UnifiedHeaders.EventEnvelope, StringComparison.OrdinalIgnoreCase);

    private static bool IsDomain(string value)
    {
        foreach (var domain in ApiRoutes.Domains) if (string.Equals(domain, value, StringComparison.Ordinal)) return true;
        return false;
    }
}
