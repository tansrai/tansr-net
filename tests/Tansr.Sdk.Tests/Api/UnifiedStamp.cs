using System.Globalization;
using Tansr.Sdk.Api;

namespace Tansr.Sdk.Tests.Api;

/// <summary>UAPI-01 test fixture: every `/api` response must carry the four mandatory `tansr-*` headers, otherwise the SDK
/// raises <see cref="ContractUnavailableException"/> and reads nothing. Legacy fakes only shape bodies, so they are wrapped
/// here; the domain is inferred from the request path through the generated route table (same as the facade would do).
/// Tests that exercise the negative path construct responses without the stamp on purpose.</summary>
internal static class UnifiedStamp
{
    internal static HttpMessageHandler Stamp(HttpMessageHandler inner, Action<HttpRequestMessage, HttpResponseMessage>? decorate = null) =>
        new StampHandler(inner, decorate);

    /// <summary>`new HttpClient(handler)` with the stamp in between (same argument shape as the HttpClient constructor).</summary>
    internal static HttpClient Client(HttpMessageHandler inner, bool disposeHandler = true) => new HttpClient(Stamp(inner), disposeHandler);

    internal static void Apply(HttpResponseMessage response, string domain = "session", bool echoEnvelope = false)
    {
        response.Headers.Remove(UnifiedHeaders.Contract); response.Headers.Remove(UnifiedHeaders.ManifestRevision);
        response.Headers.Remove(UnifiedHeaders.Domain); response.Headers.Remove(UnifiedHeaders.SchemaHash);
        response.Headers.TryAddWithoutValidation(UnifiedHeaders.Contract, ApiRoutes.Contract);
        response.Headers.TryAddWithoutValidation(UnifiedHeaders.ManifestRevision, ApiRoutes.ManifestRevision.ToString(CultureInfo.InvariantCulture));
        response.Headers.TryAddWithoutValidation(UnifiedHeaders.Domain, domain);
        response.Headers.TryAddWithoutValidation(UnifiedHeaders.SchemaHash, ApiRoutes.DomainSchemaHash(domain) ?? "none");
        if (echoEnvelope) response.Headers.TryAddWithoutValidation(UnifiedHeaders.EventEnvelope, UnifiedHeaders.EventEnvelopeContract);
    }

    internal static string DomainOf(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        return ApiRoutes.Match(request.Method.Method, path)?.Domain ?? "discovery";
    }

    private sealed class StampHandler(HttpMessageHandler inner, Action<HttpRequestMessage, HttpResponseMessage>? decorate) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.Headers.Contains(UnifiedHeaders.Contract))
            {
                bool sse = request.Headers.Contains(UnifiedHeaders.EventEnvelope);
                Apply(response, DomainOf(request), sse);
            }
            decorate?.Invoke(request, response);
            return response;
        }
    }
}
