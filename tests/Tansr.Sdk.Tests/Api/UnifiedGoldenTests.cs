using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Tests.Api;

/// <summary>Replays the unified-v1 golden vectors (vendored `contract/unified-v1.golden.json`) against the C# decoders:
/// FacadeError / UnifiedError → <see cref="UnifiedErrorEnvelope"/>, EventEnvelope → <see cref="UnifiedEventEnvelope"/>,
/// ResponseHeaders → <see cref="UnifiedResponseMeta"/>, RequestHeaders → the SDK's own header validators. Negative
/// vectors come either inline or as base + RFC 6902 patch (add / replace / remove), applied in order to a deep copy.</summary>
public sealed class UnifiedGoldenTests
{
    private static readonly JsonDocument Golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "unified-v1.golden.json")));

    private static IEnumerable<JsonElement> Vectors(string definition) =>
        Golden.RootElement.GetProperty("vectors").EnumerateArray().Where(v => v.GetProperty("definition").GetString() == definition);

    public static IEnumerable<object[]> Names(string definition) => Vectors(definition).Select(v => new object[] { v.GetProperty("name").GetString()! });
    public static IEnumerable<object[]> FacadeErrors => Names("FacadeError");
    public static IEnumerable<object[]> UnifiedErrors => Names("UnifiedError");
    public static IEnumerable<object[]> EventEnvelopes => Names("EventEnvelope");
    public static IEnumerable<object[]> ResponseHeaders => Names("ResponseHeaders");
    public static IEnumerable<object[]> RequestHeaders => Names("RequestHeaders");

    private static JsonElement Vector(string name) => Golden.RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

    /// <summary>Materialises the vector value: inline `value`, `valueFile` (a repo artifact referenced by path — the vendored copy
    /// under `contract/` is byte-identical to the cli source, as the lock pins), or `base` + `patch`.</summary>
    internal static JsonNode? Value(JsonElement vector)
    {
        if (vector.TryGetProperty("value", out var inline)) return JsonNode.Parse(inline.GetRawText());
        if (vector.TryGetProperty("valueFile", out var file))
            return JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", Path.GetFileName(file.GetString()!))));
        var node = Value(Vector(vector.GetProperty("base").GetString()!));
        foreach (var operation in vector.GetProperty("patch").EnumerateArray())
        {
            var pointer = operation.GetProperty("path").GetString()!.Split('/').Skip(1).Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToArray();
            var parent = node; for (int i = 0; i < pointer.Length - 1; i++) parent = parent![pointer[i]]!;
            var key = pointer[^1];
            switch (operation.GetProperty("op").GetString())
            {
                case "add":
                case "replace":
                    var value = JsonNode.Parse(operation.GetProperty("value").GetRawText());
                    if (parent is JsonArray array) { if (key == "-") array.Add(value); else array[int.Parse(key)] = value; }
                    else parent!.AsObject()[key] = value;
                    break;
                case "remove":
                    if (parent is JsonArray list) list.RemoveAt(int.Parse(key)); else Assert.True(parent!.AsObject().Remove(key), "remove " + key);
                    break;
                default: throw new InvalidOperationException("unsupported patch op");
            }
        }
        return node;
    }

    private static byte[] Bytes(JsonNode? node) => Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null");

    private static HttpResponseMessage Response(int status, byte[] body, string domain = "session")
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        UnifiedStamp.Apply(response, domain);
        return response;
    }

    private static Exception? Decode(JsonElement vector)
    {
        var node = Value(vector); var status = node?["status"]?.GetValue<int>() ?? 500;
        if (status < 100 || status > 599) status = 500;
        var body = Bytes(node);
        using var response = Response(status, body, node?["detail"]?["domain"]?.GetValue<string>() ?? "session");
        var meta = UnifiedResponseMeta.Read(response);
        var json = JsonDocument.Parse(body).RootElement;
        try { Assert.True(UnifiedErrorEnvelope.TryThrow(response, json, meta)); return null; }
        catch (TansrException error) { return error; }
    }

    [Theory]
    [MemberData(nameof(FacadeErrors))]
    public void FacadeErrorVectorsDecodeToFacadeOwnedExceptionsOrAreRejected(string name)
    {
        var vector = Vector(name); var error = Decode(vector);
        if (vector.GetProperty("expect").GetString() == "valid")
        {
            var unified = Assert.IsType<UnifiedApiException>(error); var value = Value(vector)!;
            Assert.True(unified.FacadeOwned, name); Assert.Null(unified.RequestId);
            Assert.Equal(value["code"]!.GetValue<string>(), unified.Code); Assert.Equal(value["status"]!.GetValue<int>(), unified.StatusCode);
            Assert.Equal(value["retryAction"]!.GetValue<string>(), unified.RetryAction); Assert.Equal(value["traceId"]!.GetValue<string>(), unified.TraceId);
            Assert.Contains(unified.Code, UnifiedErrorEnvelope.FacadeCodes);
            // D19: the follow-up is read from retryAction, not inferred from the code.
            Assert.Equal(unified.RetryAction == UnifiedRetryAction.Rediscover, unified.RequiresRediscovery);
            Assert.Equal(unified.RetryAction == UnifiedRetryAction.Refresh, unified.RequiresRefresh);
            Assert.Equal(value["detail"]?["reason"]?.GetValue<string>(), unified.Reason);
            Assert.Equal(value["detail"]?["operation"]?.GetValue<string>(), unified.Detail.Operation); Assert.Equal(value["detail"]?["state"]?.GetValue<string>(), unified.Detail.State);
            // Facade-owned: no family translation happened; the only facade domain code is closure_stale (precondition_failed).
#pragma warning disable CS0618 // the obsolete bridge must still read the same facts
            Assert.Equal(value["detail"]?["domainCode"]?.GetValue<string>() ?? unified.Code, unified.DomainCode); Assert.Equal(unified.StatusCode, unified.DomainStatus);
#pragma warning restore CS0618
            Assert.Equal(value["detail"]?["closureId"]?.GetValue<string>(), unified.ClosureId);
        }
        else Assert.True(error is ContractUnavailableException { Reason: "invalid_error_body" } || Divergence("FacadeError", name, error), Describe(name, error));
    }

    [Theory]
    [MemberData(nameof(UnifiedErrors))]
    public void UnifiedErrorVectorsCarryDomainFactsOrAreRejected(string name)
    {
        var vector = Vector(name); var error = Decode(vector);
        if (vector.GetProperty("expect").GetString() == "valid")
        {
            var unified = Assert.IsType<UnifiedApiException>(error); var value = Value(vector)!; var detail = value["detail"]?.AsObject();
            Assert.Equal(value["code"]!.GetValue<string>(), unified.Code); Assert.Equal(value["status"]!.GetValue<int>(), unified.StatusCode);
            Assert.Equal(value["requestId"]?.GetValue<string>(), unified.RequestId);
            Assert.Equal(value["retryAfterMs"]?.GetValue<int>(), unified.RetryAfterMs);
            // Obsolete Domain* bridge: translated values when present, otherwise the unified values (a null domainRetryAction on
            // the wire means "the family had no action position"). The primary reads are Code / Detail.DomainCode below.
#pragma warning disable CS0618
            Assert.Equal(detail?["domainCode"]?.GetValue<string>() ?? unified.Code, unified.DomainCode);
            Assert.Equal(detail?["domainStatus"]?.GetValue<int>() ?? unified.StatusCode, unified.DomainStatus);
            Assert.Equal(detail != null && detail.ContainsKey("domainRetryAction") ? detail["domainRetryAction"]?.GetValue<string>() : unified.RetryAction, unified.DomainRetryAction);
#pragma warning restore CS0618
            Assert.Equal(detail?["family"]?.GetValue<string>(), unified.Family);
            Assert.Equal(detail?["fallback"]?.GetValue<string>(), unified.Fallback);
            Assert.Equal(detail?["closureId"]?.GetValue<string>(), unified.ClosureId);
            if (detail != null && detail.ContainsKey("domainStatus")) Assert.False(unified.FacadeOwned, name);
            // D19 primary face: unified code + retryAction; the family code is a detail.
            Assert.Equal(value["retryAction"]!.GetValue<string>(), unified.RetryAction);
            Assert.Equal(detail?["domainCode"]?.GetValue<string>(), unified.Detail.DomainCode);
            Assert.Equal(detail?["reason"]?.GetValue<string>(), unified.Reason); Assert.Equal(detail?["header"]?.GetValue<string>(), unified.Header);
            Assert.Equal(detail?["limitBytes"]?.GetValue<int>(), unified.Detail.LimitBytes);
            Assert.Equal(unified.RetryAction == UnifiedRetryAction.Refresh, unified.RequiresRefresh);
            Assert.Equal(unified.RetryAction == UnifiedRetryAction.Rediscover, unified.RequiresRediscovery);
        }
        else Assert.True(error is ContractUnavailableException { Reason: "invalid_error_body" } || Divergence("UnifiedError", name, error), Describe(name, error));
    }

    [Theory]
    [MemberData(nameof(EventEnvelopes))]
    public void EventEnvelopeVectorsParseStrictlyWithSevenKeys(string name)
    {
        var vector = Vector(name); var bytes = Bytes(Value(vector));
        if (vector.GetProperty("expect").GetString() == "valid")
        {
            var envelope = UnifiedEventEnvelope.Parse(bytes); var value = Value(vector)!;
            Assert.Equal(value["domain"]!.GetValue<string>(), envelope.Domain); Assert.Equal(value["type"]?.GetValue<string>(), envelope.Type);
            Assert.Equal(value["eventId"]?.GetValue<string>(), envelope.EventId); Assert.Equal(value["terminalStatus"]?.GetValue<string>(), envelope.TerminalStatus);
            Assert.Equal(value["raw"]!.ToJsonString(), JsonNode.Parse(envelope.RawText)!.ToJsonString());
            Assert.Equal(value["cursorSet"]!["eventCursor"]?.GetValue<string>(), envelope.Cursors.EventCursor);
        }
        else
        {
            var error = Record.Exception(() => UnifiedEventEnvelope.Parse(bytes));
            Assert.True(error is TansrProtocolException { Code: "invalid_envelope" } || Divergence("EventEnvelope", name, error), Describe(name, error));
        }
    }

    [Theory]
    [MemberData(nameof(ResponseHeaders))]
    public void ResponseHeaderVectorsAreAcceptedOrRaiseContractUnavailable(string name)
    {
        var vector = Vector(name); var value = Value(vector)!.AsObject();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        foreach (var (key, node) in value) response.Headers.TryAddWithoutValidation(key, node!.ToJsonString().Trim('"'));
        if (Divergences.ContainsKey("ResponseHeaders/" + name)) { Assert.Null(Record.Exception(() => UnifiedResponseMeta.Read(response))); return; }
        if (vector.GetProperty("expect").GetString() == "valid")
        {
            var meta = UnifiedResponseMeta.Read(response);
            Assert.Equal(value["tansr-contract"]!.GetValue<string>(), meta.Contract);
            Assert.Equal(int.Parse(value["tansr-manifest-revision"]!.GetValue<string>()), meta.ManifestRevision);
            Assert.Equal(value["tansr-domain"]!.GetValue<string>(), meta.Domain); Assert.Equal(value["tansr-schema-hash"]!.GetValue<string>(), meta.SchemaHash);
            Assert.Equal(value["tansr-closure-id"]?.GetValue<string>(), meta.ClosureId);
            Assert.Equal(value["tansr-event-envelope"] != null, meta.EventEnvelopeNegotiated);
            Assert.Equal(value["x-request-id"]?.GetValue<string>(), meta.RequestId);
            Assert.Equal(value["retry-after"] == null ? null : int.Parse(value["retry-after"]!.GetValue<string>()), meta.RetryAfterSeconds);
        }
        else
        {
            var error = Record.Exception(() => UnifiedResponseMeta.Read(response));
            Assert.True(error is ContractUnavailableException || Divergence("ResponseHeaders", name, error), Describe(name, error));
        }
    }

    [Theory]
    [MemberData(nameof(RequestHeaders))]
    public void RequestHeaderVectorsAgreeWithTheHeadersTheSdkIsAllowedToEmit(string name)
    {
        // The SDK only ever emits tansr-session-family / tansr-closure-id / tansr-event-envelope; the other request
        // headers in the vocabulary are server-side validation subjects. Vectors not touching a tansr-* header are vacuous here.
        var vector = Vector(name); var value = Value(vector)!.AsObject(); bool valid = vector.GetProperty("expect").GetString() == "valid";
        var tansr = value.Where(p => p.Key.StartsWith("tansr-", StringComparison.Ordinal)).ToList();
        bool accepted = tansr.All(p => p.Key switch
        {
            UnifiedHeaders.SessionFamily => p.Value!.GetValue<string>() is "sdk1" or "sdk2-offload-v1",
            UnifiedHeaders.ClosureId => UnifiedHeaders.Digest.IsMatch(p.Value!.GetValue<string>()),
            UnifiedHeaders.EventEnvelope => p.Value!.GetValue<string>() == UnifiedHeaders.EventEnvelopeContract,
            _ => false,
        });
        if (valid) Assert.True(accepted, name);
        else if (tansr.Count > 0) Assert.False(accepted, name);
        else Assert.True(true, "server-side vocabulary vector: " + name);
    }

    public static IEnumerable<object[]> CapabilityClosures => Names("CapabilityClosure");
    public static IEnumerable<object[]> Capabilities => Names("Capabilities");
    public static IEnumerable<object[]> Manifests => Names("Manifest");

    [Theory]
    [MemberData(nameof(CapabilityClosures))]
    public void CapabilityClosureVectorsDecodeStrictlyWithTheIdRederived(string name)
    {
        var vector = Vector(name); var bytes = Bytes(Value(vector));
        if (vector.GetProperty("expect").GetString() == "valid")
        {
            var closure = UnifiedCapabilityClosure.Parse(bytes); var value = Value(vector)!;
            Assert.Equal(value["closureId"]!.GetValue<string>(), closure.ClosureId);
            Assert.Equal(value["authorizationRevision"]?.GetValue<string>(), closure.AuthorizationRevision);
            foreach (var domain in UnifiedCapabilityClosure.Domains)
            {
                Assert.Equal(value["domains"]![domain]!["installed"]!.GetValue<bool>(), closure.Domain(domain).Installed);
                Assert.Equal(value["domains"]![domain]!["revision"]?.GetValue<string>(), closure.Domain(domain).Revision);
            }
            foreach (var operation in UnifiedCapabilityClosure.Operations)
            {
                var state = value["operations"]![operation.Name]!.GetValue<string>();
                Assert.Equal(state, closure.State(operation)); Assert.Equal(state == "enabled", closure.IsEnabled(operation));
                Assert.Equal(state switch { "disabled" => 403, "unavailable" => 404, _ => (int?)null }, closure.ExpectedStatus(operation));
            }
            Assert.Equal(UnifiedCapabilityClosure.Operations.Count, closure.Enabled().Count + closure.Fenced().Count);
        }
        else
        {
            var error = Record.Exception(() => UnifiedCapabilityClosure.Parse(bytes));
            Assert.True(error is TansrProtocolException { Code: "invalid_closure" }, Describe(name, error));
        }
    }

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void CapabilitiesVectorsDecodeStrictlyWithoutUrlNavigationFields(string name)
    {
        var vector = Vector(name); var bytes = Bytes(Value(vector));
        if (vector.GetProperty("expect").GetString() == "valid")
        {
            var capabilities = UnifiedCapabilities.Parse(bytes); var value = Value(vector)!;
            Assert.Equal(value["manifestRevision"]!.GetValue<int>(), capabilities.ManifestRevision);
            Assert.Equal(value["schemaHash"]!.GetValue<string>(), capabilities.SchemaHash);
            foreach (var domain in UnifiedCapabilityClosure.Domains)
            {
                var expected = value["domains"]![domain]!; var actual = capabilities.Domain(domain);
                Assert.Equal(expected["installed"]!.GetValue<bool>(), actual.Installed); Assert.Equal(expected["contract"]!.GetValue<string>(), actual.Contract);
                Assert.Equal(expected["status"]!.GetValue<string>(), actual.Status); Assert.Equal(expected["schemaHash"]!.GetValue<string>(), actual.SchemaHash);
                Assert.Equal(ApiRoutes.DomainFamily(domain), actual.Contract);
                Assert.Equal(expected["family"]?["preferred"]?.GetValue<string>(), actual.PreferredFamily);
                Assert.Equal(expected["family"]?["available"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray(), actual.AvailableFamilies);
            }
            foreach (var operation in ApiRoutes.All)
                Assert.Equal(operation.Domain == "discovery" || value["domains"]![operation.Domain]!["installed"]!.GetValue<bool>(), capabilities.IsInstalled(operation));
        }
        else
        {
            var error = Record.Exception(() => UnifiedCapabilities.Parse(bytes));
            Assert.True(error is TansrProtocolException { Code: "invalid_capabilities" }, Describe(name, error));
        }
    }

    /// <summary>The manifest is consumed at build time (generate-api-routes.mjs → ApiRoutes.generated.cs); the runtime never parses
    /// one. The valid repo-artifact vector is therefore replayed against the generated table — every operation fact the SDK acts on
    /// (method, template, kind, sse, family, domain, etagPath, expectedRevision) and every family fact (sha256, requestIdPath) must
    /// agree. The negative vectors are generator-validation subjects and are accounted as such in <see cref="NotConsumed"/>.</summary>
    [Fact]
    public void ManifestRepoArtifactVectorAgreesWithTheGeneratedRouteTable()
    {
        var manifest = Value(Vector("manifest-repo-artifact"))!.AsObject();
        Assert.Equal(ApiRoutes.Contract, manifest["contract"]!.GetValue<string>());
        var operations = manifest["operations"]!.AsArray();
        Assert.Equal(ApiRoutes.All.Count, operations.Count);
        for (int i = 0; i < operations.Count; i++)
        {
            var expected = operations[i]!.AsObject(); var actual = ApiRoutes.All[i];
            Assert.Equal(expected["name"]!.GetValue<string>(), actual.Name); Assert.Equal(expected["domain"]!.GetValue<string>(), actual.Domain);
            Assert.Equal(expected["family"]?.GetValue<string>(), actual.Family); Assert.Equal(expected["method"]!.GetValue<string>(), actual.Method);
            Assert.Equal(expected["apiPath"]!.GetValue<string>(), actual.Template); Assert.Equal(expected["kind"]!.GetValue<string>(), actual.Kind);
            Assert.Equal(expected["sse"]!.GetValue<bool>(), actual.Sse);
            Assert.Equal(expected["aliases"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray(), actual.Aliases);
            Assert.Equal(expected["query"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray(), actual.QueryParameters);
            Assert.Equal(expected["etagPath"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray(), actual.EtagPath);
            Assert.Equal(expected["expectedRevision"]?["path"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray(), actual.ExpectedRevisionPath);
            Assert.Equal(expected["expectedRevision"]?["kind"]?.GetValue<string>(), actual.ExpectedRevisionKind);
            Assert.Equal(actual.ExpectedRevisionPath != null, actual.AcceptsIfMatch); Assert.Equal(actual.Kind == "write", actual.AcceptsIdempotencyKey);
        }
        var families = manifest["families"]!.AsArray();
        foreach (var family in families)
        {
            var id = family!["id"]!.GetValue<string>();
            Assert.Equal(family["requestIdPath"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray(), ApiRoutes.FamilyRequestIdPath(id));
        }
        // Closure catalogue = manifest operations minus discovery minus the session-contract probe (closure.ts CLOSURE_OPERATIONS).
        var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "unified-v1.schema.json")));
        var fenced = schema.RootElement.GetProperty("definitions").GetProperty("CapabilityClosure").GetProperty("properties").GetProperty("operations").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(fenced, UnifiedCapabilityClosure.Operations.Select(o => o.Name).ToArray());
        Assert.Equal(77, fenced.Length);
    }

    /// <summary>Vectors with no C# consumer, each with the reason. Everything else in the fixture must be replayed by a theory above;
    /// <see cref="EveryGoldenVectorIsReplayedOrExplicitlyNotConsumed"/> fails when the fixture grows without this list (or a test) following.</summary>
    private static readonly Dictionary<string, string> NotConsumed = new(StringComparer.Ordinal)
    {
        ["Manifest/manifest-runtime-view"] = "runtime manifest view is served by the facade; the SDK consumes the manifest only at build time (generated ApiRoutes)",
    };

    private static bool ManifestNegative(JsonElement vector) =>
        vector.GetProperty("definition").GetString() == "Manifest" && vector.GetProperty("expect").GetString() == "invalid";

    [Fact]
    public void EveryGoldenVectorIsReplayedOrExplicitlyNotConsumed()
    {
        var replayed = new[] { "FacadeError", "UnifiedError", "EventEnvelope", "ResponseHeaders", "RequestHeaders", "CapabilityClosure", "Capabilities" };
        int total = 0, consumed = 0, generatorSubjects = 0, declared = 0;
        foreach (var vector in Golden.RootElement.GetProperty("vectors").EnumerateArray())
        {
            total++;
            var definition = vector.GetProperty("definition").GetString()!; var name = vector.GetProperty("name").GetString()!;
            if (replayed.Contains(definition) || name == "manifest-repo-artifact") { consumed++; continue; }
            if (ManifestNegative(vector)) { generatorSubjects++; continue; } // validated by generate-api-routes.mjs at build time, not by the runtime
            Assert.True(NotConsumed.ContainsKey(definition + "/" + name), "unaccounted golden vector: " + definition + "/" + name);
            declared++;
        }
        Assert.Equal(165, total); Assert.Equal(total, consumed + generatorSubjects + declared);
        Assert.Equal(NotConsumed.Count, declared); Assert.Equal(23, generatorSubjects);
        foreach (var entry in NotConsumed) Assert.Equal(entry.Key.Substring(0, entry.Key.IndexOf('/')), Vector(entry.Key.Substring(entry.Key.IndexOf('/') + 1)).GetProperty("definition").GetString());
    }

    /// <summary>Negative vectors the C# decoder does not reject on its own. Each one is a registered divergence that must
    /// be listed here with the reason; adding to this list is a review decision, not a convenience.</summary>
    private static readonly Dictionary<string, string> Divergences = new(StringComparer.Ordinal)
    {
        // The wire carries one envelope shape; a client cannot tell FacadeError from UnifiedError by definition name.
        // Both vectors are schema-valid UnifiedError bodies, so the decoder accepts them (as non-facade-owned errors).
        ["FacadeError/facade-error-request-id-not-null"] = "valid UnifiedError (not_found with an idempotency key); FacadeOwned=false",
        ["FacadeError/facade-error-code-not-facade"] = "valid UnifiedError (forbidden 403); FacadeOwned=false",
        // HTTP header values are strings by construction; the JSON-typed negative cannot occur on the wire.
        ["ResponseHeaders/response-headers-revision-number"] = "not representable in HTTP (header values are strings)",
    };

    private static bool Divergence(string definition, string name, Exception? error) =>
        Divergences.ContainsKey(definition + "/" + name);

    [Fact]
    public void RegisteredDivergencesAreTheOnlyNegativeVectorsTheDecoderAccepts()
    {
        // Every registered divergence must still exist in the fixture and must still be a vector the decoder does not
        // reject on its own; a stale entry means the decoder (or the fixture) changed and the registry must be revisited.
        foreach (var entry in Divergences)
        {
            var definition = entry.Key.Substring(0, entry.Key.IndexOf('/')); var name = entry.Key.Substring(definition.Length + 1);
            var vector = Vector(name); Assert.Equal(definition, vector.GetProperty("definition").GetString()); Assert.Equal("invalid", vector.GetProperty("expect").GetString());
            if (definition == "FacadeError")
            {
                var unified = Assert.IsType<UnifiedApiException>(Decode(vector)); Assert.False(unified.FacadeOwned);
            }
        }
    }

    private static string Describe(string name, Exception? error) => name + " -> " + (error == null ? "accepted" : error.GetType().Name + ": " + error.Message);

    [Fact]
    public void GoldenSchemaFingerprintMatchesTheVendoredSchemaAndManifestFamily()
    {
        var schema = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "unified-v1.schema.json"));
        Assert.Equal(Golden.RootElement.GetProperty("schemaSha256").GetString(), Tansr.Sdk.Protocol.WireJson.Sha256(schema));
        Assert.Equal(ApiRoutes.Families.UnifiedV1, Tansr.Sdk.Protocol.WireJson.Sha256(schema));
    }

    [Fact]
    public void SchemaHashIsComparedPerDomainWithDiscoveryCarryingTheManifestAggregate()
    {
        // 手册 §16.4 / facade.ts schemaHashOf: discovery = manifest aggregate schemaHash (golden response-headers-discovery
        // carries the revision-4 aggregate d97e35a9…), every other domain = its primary family source SHA.
        Assert.Equal("sha256:" + ApiRoutes.ManifestSchemaHash, ApiRoutes.DomainSchemaHash("discovery"));
        Assert.NotEqual("sha256:" + ApiRoutes.Families.UnifiedV1, ApiRoutes.DomainSchemaHash("discovery"));
        Assert.Equal("sha256:" + ApiRoutes.Families.AgentSessionV1, ApiRoutes.DomainSchemaHash("session"));
        Assert.Equal("sha256:" + ApiRoutes.Families.Sdk2ExtV1, ApiRoutes.DomainSchemaHash("archive"));
        Assert.Equal("sha256:" + ApiRoutes.Families.Sdk2CacheV1, ApiRoutes.DomainSchemaHash("cache"));
        var discovery = Value(Vector("response-headers-discovery"))!.AsObject();
        Assert.Equal("discovery", discovery["tansr-domain"]!.GetValue<string>());
        Assert.NotEqual("sha256:" + Golden.RootElement.GetProperty("schemaSha256").GetString(), discovery["tansr-schema-hash"]!.GetValue<string>());
        foreach (var domain in ApiRoutes.Domains) Assert.Matches(UnifiedHeaders.SchemaHashValue, ApiRoutes.DomainSchemaHash(domain)!);
    }
}
