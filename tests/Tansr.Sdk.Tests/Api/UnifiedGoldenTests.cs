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

    /// <summary>Materialises the vector value: inline `value`, or `base` + `patch`.</summary>
    internal static JsonNode? Value(JsonElement vector)
    {
        if (vector.TryGetProperty("value", out var inline)) return JsonNode.Parse(inline.GetRawText());
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
            Assert.Equal(unified.Code == "precondition_failed", unified.RequiresRediscovery);
            // Facade-owned: no family translation happened; the only facade domain code is closure_stale (precondition_failed).
            Assert.Equal(value["detail"]?["domainCode"]?.GetValue<string>() ?? unified.Code, unified.DomainCode); Assert.Equal(unified.StatusCode, unified.DomainStatus);
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
            // Domain facts: translated values when present, otherwise the unified values (business catch sites read
            // Domain* uniformly; a null domainRetryAction on the wire means "the family had no action position").
            Assert.Equal(detail?["domainCode"]?.GetValue<string>() ?? unified.Code, unified.DomainCode);
            Assert.Equal(detail?["domainStatus"]?.GetValue<int>() ?? unified.StatusCode, unified.DomainStatus);
            Assert.Equal(detail != null && detail.ContainsKey("domainRetryAction") ? detail["domainRetryAction"]?.GetValue<string>() : unified.RetryAction, unified.DomainRetryAction);
            Assert.Equal(detail?["family"]?.GetValue<string>(), unified.Family);
            Assert.Equal(detail?["fallback"]?.GetValue<string>(), unified.Fallback);
            Assert.Equal(detail?["closureId"]?.GetValue<string>(), unified.ClosureId);
            if (detail != null && detail.ContainsKey("domainStatus")) Assert.False(unified.FacadeOwned, name);
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
        Assert.Equal("sha256:" + Tansr.Sdk.Protocol.WireJson.Sha256(schema), ApiRoutes.DomainSchemaHash("discovery"));
        Assert.Equal(ApiRoutes.Families.UnifiedV1, Tansr.Sdk.Protocol.WireJson.Sha256(schema));
    }
}
