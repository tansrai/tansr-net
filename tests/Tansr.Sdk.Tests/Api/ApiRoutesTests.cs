using System.Text.Json;
using Tansr.Sdk.Api;

namespace Tansr.Sdk.Tests.Api;

/// <summary>UAPI-01 D10: the generated route table is the only source of `/api` paths. It must mirror the vendored
/// api-manifest operation for operation, instantiate placeholders strictly and never produce a legacy prefix.</summary>
public sealed class ApiRoutesTests
{
    private static JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "api-manifest.json")));
    private static JsonDocument Lock() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "manifest.json")));

    [Fact]
    public void GeneratedTableMirrorsTheVendoredManifestOperationForOperation()
    {
        using var manifest = Manifest();
        var root = manifest.RootElement;
        Assert.Equal("tansr-api-manifest-v1", root.GetProperty("format").GetString());
        Assert.Equal(ApiRoutes.Contract, root.GetProperty("contract").GetString());
        Assert.Equal(ApiRoutes.ManifestRevision, root.GetProperty("revision").GetInt32());
        Assert.Equal(ApiRoutes.ManifestSchemaHash, root.GetProperty("schemaHash").GetString());
        var operations = root.GetProperty("operations").EnumerateArray().ToList();
        Assert.Equal(ApiRoutes.OperationCount, operations.Count); Assert.Equal(operations.Count, ApiRoutes.All.Count);
        foreach (var expected in operations)
        {
            var name = expected.GetProperty("name").GetString()!;
            var actual = ApiRoutes.Find(name); Assert.NotNull(actual);
            Assert.Equal(expected.GetProperty("domain").GetString(), actual!.Domain);
            Assert.Equal(expected.GetProperty("method").GetString(), actual.Method);
            Assert.Equal(expected.GetProperty("apiPath").GetString(), actual.Template);
            Assert.Equal(expected.GetProperty("kind").GetString(), actual.Kind);
            Assert.Equal(expected.GetProperty("sse").GetBoolean(), actual.Sse);
            Assert.Equal(expected.GetProperty("sse").GetBoolean(), actual.Kind == "stream");
            Assert.Equal(expected.GetProperty("family").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("family").GetString(), actual.Family);
            Assert.Equal(expected.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!), actual.Aliases);
            Assert.Equal(expected.GetProperty("query").EnumerateArray().Select(q => q.GetString()!), actual.QueryParameters);
        }
        Assert.Equal(ApiRoutes.All.Count, ApiRoutes.All.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LockPinsTheVendoredManifestBytesThatTheTableWasGeneratedFrom()
    {
        using var lockFile = Lock();
        var api = lockFile.RootElement.GetProperty("apiManifest");
        Assert.Equal(ApiRoutes.ManifestRevision, api.GetProperty("revision").GetInt32());
        Assert.Equal(ApiRoutes.ManifestSchemaHash, api.GetProperty("schemaHash").GetString());
        var entry = Assert.Single(lockFile.RootElement.GetProperty("files").EnumerateArray(), f => f.GetProperty("snapshot").GetString() == "contract/api-manifest.json");
        Assert.Equal(ApiRoutes.ManifestSha256, entry.GetProperty("sha256").GetString());
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "api-manifest.json"));
        Assert.Equal(entry.GetProperty("bytes").GetInt32(), bytes.Length);
        Assert.Equal(ApiRoutes.ManifestSha256, Tansr.Sdk.Protocol.WireJson.Sha256(bytes));
    }

    [Fact]
    public void EveryTemplateAndAliasLivesUnderApiAndNeverUnderALegacyPrefix()
    {
        foreach (var operation in ApiRoutes.All)
        {
            foreach (var path in new[] { operation.Template }.Concat(operation.Aliases))
            {
                Assert.StartsWith(ApiRoutes.Prefix + "/", path);
                // Legacy entry prefixes never appear (D10). `/api/cache/core/v1/...` is a family-internal segment, not an entry prefix.
                Assert.DoesNotContain("/v2/", path); Assert.DoesNotContain("/v3/", path); Assert.False(path.StartsWith("/v1/", StringComparison.Ordinal));
                Assert.True(ApiRoutes.IsApiPath(path));
            }
            foreach (var placeholder in operation.Placeholders) Assert.Contains(placeholder, new[] { "id", "targetId", "uploadId", "ticketId" });
            Assert.True(ApiRoutes.DomainFamily(operation.Domain) != null || operation.Domain == "discovery", operation.Name);
        }
        Assert.Equal(9, ApiRoutes.Domains.Count);
        Assert.False(ApiRoutes.IsApiPath("/v2/sessions")); Assert.False(ApiRoutes.IsApiPath("/v3/sdk2/sessions")); Assert.False(ApiRoutes.IsApiPath("/apix"));
        Assert.True(ApiRoutes.IsApiPath("/api")); Assert.True(ApiRoutes.IsApiPath("/api/sessions?limit=1"));
    }

    [Fact]
    public void PlaceholdersAreInstantiatedStrictlyAndEscaped()
    {
        Assert.Equal("/api/sessions/s%2F%E4%B8%AD", ApiRoutes.SessionGet.Path(id: "s/中"));
        Assert.Equal("/api/sessions/s/binding-target", ApiRoutes.ArchiveBindingTarget.Path(id: "s"));
        Assert.Equal("/api/archive/bindings/b/materials/m/uploads/u", ApiRoutes.MaterialUploadStatus.Path(id: "b", targetId: "m", uploadId: "u"));
        Assert.Equal("/api/sessions/", ApiRoutes.SessionGet.Root); Assert.Equal("/api/sessions", ApiRoutes.SessionList.Root);
        Assert.Equal("/api/terminal/observation/sessions/", ApiRoutes.TerminalObservationResources.Root);
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionGet.Path());
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionGet.Path(id: "s", targetId: "x"));
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionList.Path(id: "s"));
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionGet.Path(id: ""));
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionGet.Path(id: ".."));
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionGet.Path(id: "a\nb"));
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionGet.Path(id: new string('x', 513)));
        Assert.Equal("/api/sessions/" + new string('x', 512), ApiRoutes.SessionGet.Path(id: new string('x', 512)));
    }

    [Fact]
    public void QueryStringsAreWhitelistedPerOperationAndEscaped()
    {
        Assert.Equal("", ApiRoutes.SessionList.Query());
        Assert.Equal("?limit=1&offset=0", ApiRoutes.SessionList.Query(("limit", "1"), ("offset", "0")));
        Assert.Equal("?protocol=sdk2-ext-v1", ApiRoutes.SessionCapabilities.Query(("protocol", "sdk2-ext-v1")));
        Assert.Equal("?exclude=a%26b", ApiRoutes.SessionEventsObserve.Query(("exclude", "a&b")));
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionList.Query(("protocol", "x")));
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionList.Query(("limit", "1"), ("limit", "2")));
        Assert.Throws<ArgumentException>(() => ApiRoutes.SessionCreate.Query(("limit", "1")));
    }

    [Fact]
    public void MatchResolvesTemplatesAndAliasesWithOrWithoutQuery()
    {
        Assert.Same(ApiRoutes.SessionGet, ApiRoutes.Match("GET", "/api/sessions/abc"));
        Assert.Same(ApiRoutes.SessionList, ApiRoutes.Match("GET", "/api/sessions?limit=1"));
        Assert.Same(ApiRoutes.SessionCreate, ApiRoutes.Match("POST", "/api/sessions"));
        Assert.Same(ApiRoutes.TerminalMemoryRead, ApiRoutes.Match("GET", ApiRoutes.TerminalMemoryRead.Aliases[0].Replace(":id", "s")));
        Assert.Same(ApiRoutes.CacheBindingOpen, ApiRoutes.Match("POST", "/api/cache/bindings"));
        Assert.Same(ApiRoutes.CacheOperationQuery, ApiRoutes.Match("GET", "/api/cache/operations?protocol=sdk2-cache-v1"));
        Assert.Null(ApiRoutes.Match("GET", "/v2/sessions/abc")); Assert.Null(ApiRoutes.Match("GET", "/api/sessions/a/b/c/d"));
        Assert.True(ApiRoutes.SessionGet.Matches("/api/sessions/x")); Assert.False(ApiRoutes.SessionGet.Matches("/api/sessions/"));
    }
}
