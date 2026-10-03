using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Api;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    /// <summary>Families whose request bodies must be strict canonical control JSON (RFC-UAPI-1 §4; the server compares bytes).</summary>
    private static readonly HashSet<string> CanonicalBodyFamilies = new HashSet<string>(StringComparer.Ordinal)
    { "sdk2-ext-v1", "sdk2-archive-recovery-v1", "archive-sync-v1", "sdk2-cache-v1", "sdk2-cache-core-v1", "terminal-services-v1", "terminal-observation-v1", "terminal-profile-v1" };

    /// <summary>Generic unified <c>/api</c> call for any non-stream manifest operation (the C# counterpart of
    /// <c>client.call(name, options)</c> in <c>@tansr/api-client/api</c>). Paths come from <paramref name="operation"/> only;
    /// <c>Idempotency-Key</c> / <c>If-Match</c> / <c>deadline</c> are validated against the operation before anything is sent
    /// (<c>invalid_idempotency_key</c>, <c>invalid_if_match</c>, <c>if_match_not_applicable</c>, <c>invalid_deadline</c>,
    /// <c>deadline_exceeded</c> as <see cref="TansrProtocolException"/>). Errors surface as <see cref="UnifiedApiException"/>
    /// (unified code first, D19) or <see cref="ContractUnavailableException"/>; nothing is retried here — see
    /// <see cref="UnifiedRetry.RetrySameRequestAsync"/> for the one permitted replay.</summary>
    public async Task<ApiCallResult> CallAsync(ApiOperation operation, ApiCallOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (operation.Sse) throw new TansrProtocolException("invalid_operation");
        options ??= new ApiCallOptions();
        if (options.Body.HasValue && options.RawBody is not null) throw new TansrProtocolException("invalid_body");
        var conditions = ApiRequestConditions.From(operation, options);
        var path = operation.Path(options.Id, options.TargetId, options.UploadId, options.TicketId);
        if (options.Query is { Count: > 0 })
        {
            var pairs = new (string Name, string Value)[options.Query.Count];
            for (int i = 0; i < pairs.Length; i++) pairs[i] = (options.Query[i].Key, options.Query[i].Value);
            path += operation.Query(pairs);
        }
        string? closureId = options.ClosureId;
        if (closureId is not null && !UnifiedHeaders.Digest.IsMatch(closureId)) throw new TansrProtocolException("invalid_closure_id");
        byte[]? body = null; string? contentType = options.ContentType;
        if (options.RawBody is not null) { body = options.RawBody; contentType ??= "application/octet-stream"; }
        else if (options.Body.HasValue)
        {
            body = operation.Family is not null && CanonicalBodyFamilies.Contains(operation.Family)
                ? WireJson.EncodeControl(options.Body.Value)
                : Encoding.UTF8.GetBytes(options.Body.Value.GetRawText());
            contentType ??= "application/json";
        }
        int maximum = options.MaxResponseBytes ?? maxResponseBytes;
        if (maximum < 1 || maximum > 32 * 1024 * 1024) throw new TansrProtocolException("invalid_options");
        if (body is not null && body.Length > 32 * 1024 * 1024) throw new TansrProtocolException("payload_too_large");
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        if (operation.Domain == "session" || operation.Domain == "execution") await EnsureContractAsync(access, cancellation.Token).ConfigureAwait(false);
        using var response = await transport.SendAsync(new HttpMethod(operation.Method), path, access, body, "application/json", contentType, null,
            cancellation.Token, closureId, conditions.IsEmpty ? null : conditions).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, maximum, cancellation.Token).ConfigureAwait(false);
        var meta = SessionTransport.Meta(response);
        int status = (int)response.StatusCode;
        JsonElement? value = null;
        if (status != 204 && response.Content.Headers.ContentLength != 0)
        {
            SessionTransport.ExpectContent(response, "application/json");
            var bytes = await SessionTransport.ReadBodyAsync(response, maximum, cancellation.Token).ConfigureAwait(false);
            if (bytes.Length > 0) value = SessionJson.Parse(bytes);
        }
        transport.AssertCurrent(access);
        return new ApiCallResult(status, value, meta);
    }

    /// <summary>Deployment-level discovery (<c>GET /api/capabilities</c>): installed domains, families, fingerprints. Strictly parsed
    /// (<see cref="UnifiedCapabilities.Parse(byte[], int)"/>); an uninstalled domain is a fact to intersect with, not a reason to
    /// try a different path.</summary>
    public async Task<UnifiedCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        using var response = await transport.SendAsync(HttpMethod.Get, ApiRoutes.DiscoveryCapabilities.Path(), access,
            null, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, maxResponseBytes, cancellation.Token).ConfigureAwait(false);
        if ((int)response.StatusCode != 200) throw new TansrProtocolException("invalid_response");
        var meta = SessionTransport.Meta(response);
        SessionTransport.ExpectContent(response, "application/json");
        var capabilities = UnifiedCapabilities.Parse(await SessionTransport.ReadBodyAsync(response, WireJson.MaximumControlBytes, cancellation.Token).ConfigureAwait(false));
        // Discovery headers and body describe the same manifest: revision and aggregate fingerprint must agree.
        if (capabilities.ManifestRevision != meta.ManifestRevision || !string.Equals(capabilities.SchemaHash, meta.SchemaHash, StringComparison.Ordinal))
            throw new TansrProtocolException("invalid_response");
        transport.AssertCurrent(access);
        return capabilities;
    }

    /// <summary>Declared fence for a session (<c>GET /api/sessions/:id/capabilities</c>, facade-owned discovery). The returned
    /// closure is what a caller intersects its operations with; its <see cref="UnifiedCapabilityClosure.ClosureId"/> is the value to
    /// send as <see cref="ApiCallOptions.ClosureId"/> on guarded writes. The body is parsed strictly and its id re-derived
    /// (<see cref="UnifiedCapabilityClosure.Parse(byte[], int)"/>); a <c>tansr-closure-id</c> header that disagrees with the body
    /// is <c>closure_id_mismatch</c>.</summary>
    public async Task<UnifiedCapabilityClosure> GetCapabilityClosureAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        SessionJson.Text(sessionId, 512, "sessionId");
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        using var response = await transport.SendAsync(HttpMethod.Get, ApiRoutes.DiscoverySessionCapabilities.Path(id: sessionId), access,
            null, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, maxResponseBytes, cancellation.Token).ConfigureAwait(false);
        if ((int)response.StatusCode != 200) throw new TansrProtocolException("invalid_response");
        var meta = SessionTransport.Meta(response);
        SessionTransport.ExpectContent(response, "application/json");
        var closure = UnifiedCapabilityClosure.Parse(await SessionTransport.ReadBodyAsync(response, WireJson.MaximumControlBytes, cancellation.Token).ConfigureAwait(false));
        if (meta.ClosureId != null && !string.Equals(meta.ClosureId, closure.ClosureId, StringComparison.Ordinal)) throw new TansrProtocolException("closure_id_mismatch");
        transport.AssertCurrent(access);
        return closure;
    }
}
