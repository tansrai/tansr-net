using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class PersistenceCustodyReadClientTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Scope(string revision = "1") => Json(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = revision });
    private static JsonElement Query(string user = "user") => Json(new
    { contract = "persistence-custody-handoff-v1", identity = new { applicationScopeId = "app", endUserId = user, sourceId = "memory", sourceGeneration = "1", domainKey = "domain" }, operationId = "original", intentSha256 = new string('a', 64), action = "query" });
    private static JsonElement Response(bool preparing = true, string operation = "original") => Json(new
    { contract = "persistence-custody-handoff-v1", identity = Query().GetProperty("identity"), operationId = operation, intentSha256 = new string('a', 64), action = "query", preparing, ticket = (object?)null });
    private sealed class Handler(Func<HttpRequestMessage, JsonElement> response) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(WireJson.CanonicalString(response(request)), Encoding.UTF8, "application/json") });
        }
    }
    private static TansrClient Client(HttpClient http, Func<JsonElement>? scope = null) => new(new TansrClientOptions
    { BaseUri = new Uri("https://serve.test"), TokenProvider = _ => Task.FromResult("synthetic"), PrincipalProvider = () => "app/user", ExecutionScopeProvider = scope ?? (() => Scope()) }, http);

    [Fact]
    public async Task QueryUsesReadOnlyOriginalKeyAndKeepsPreparingDistinct()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            Assert.Equal("/api/terminal/persistence/custody/operations/original", request.RequestUri!.AbsolutePath);
            Assert.Contains("sourceGeneration=1", request.RequestUri.Query); Assert.Contains("intentSha256=", request.RequestUri.Query);
            Assert.DoesNotContain("authorizationRevision", request.RequestUri.Query); return Response();
        });
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        var response = await new PersistenceCustodyReadClient(client).QueryAsync(Query());
        Assert.True(response.GetProperty("preparing").GetBoolean()); Assert.Equal(JsonValueKind.Null, response.GetProperty("ticket").ValueKind); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AnotherIdentityCannotProbeTheOriginalOperation()
    {
        using var handler = new Handler(_ => Response()); using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        await Assert.ThrowsAsync<WireProtocolException>(() => new PersistenceCustodyReadClient(client).QueryAsync(Query("other"))); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AnotherOperationResponseDoesNotCountAsOriginalOutcome()
    {
        using var handler = new Handler(_ => Response(operation: "another")); using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        await Assert.ThrowsAsync<WireProtocolException>(() => new PersistenceCustodyReadClient(client).QueryAsync(Query())); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AuthorizationChangedDuringHttpReadRejectsResponse()
    {
        string revision = "1"; using var handler = new Handler(_ => { revision = "2"; return Response(); });
        using var http = UnifiedStamp.Client(handler); using var client = Client(http, () => Scope(revision));
        var error = await Assert.ThrowsAsync<TansrProtocolException>(() => new PersistenceCustodyReadClient(client).QueryAsync(Query()));
        Assert.Equal("context_changed", error.Code); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnavailableCapabilitiesAreDiscoveryNotPermissionToUseFallback()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("/api/terminal/persistence/custody/capabilities", request.RequestUri!.AbsolutePath);
            return Json(new
            {
                contract = "persistence-custody-handoff-v1",
                capability = "persistence-custody-handoff-v1",
                installed = false,
                schemaRevision = "2026-10-21.v1",
                writerAuthority = "unavailable",
                limits = new
                {
                    maxControlBytes = 32768,
                    maxObjectChunkBytes = 12288,
                    maxPageRecords = 32,
                    maxReceiptEntries = 1048576,
                    maxTransferFacts = 262144,
                    maxRetainedBytes = 1073741824,
                    maxTransferMetadataBytes = 524288
                }
            });
        });
        using var http = UnifiedStamp.Client(handler); using var client = Client(http);
        var capabilities = await new PersistenceCustodyReadClient(client).GetCapabilitiesAsync(); Assert.False(capabilities.GetProperty("installed").GetBoolean()); Assert.Equal(1, handler.Calls);
        Assert.False(typeof(Tansr.Sdk.Storage.ITerminalPersistenceCustodyAuthority).IsAssignableFrom(typeof(PersistenceCustodyReadClient)));
    }
}
