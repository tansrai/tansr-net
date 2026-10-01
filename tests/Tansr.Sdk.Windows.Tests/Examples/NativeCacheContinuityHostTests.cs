using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Cache;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Windows.Tests.Examples;

public sealed class NativeCacheContinuityHostTests
{
    [Fact]
    public async Task DemoPersistsTheOriginalBeforeLossAndQueriesItAfterReopenWithoutRepostingOrDisposingBorrowedClient()
    {
        using var f = new Fixture(); var host = await f.Open();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => f.Open()); // Same path has one live writer.
            Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => host.NewAsync())).Code);
            Assert.Contains("待对账", await host.ReadStatusAsync()); Assert.Equal(1, f.Handler.Posts);
            Assert.DoesNotContain(Fixture.Ticket, Encoding.UTF8.GetString(File.ReadAllBytes(f.StatePath)));
            Assert.Equal("cache_query_original_required", (await Assert.ThrowsAsync<InvalidOperationException>(() => host.NewAsync())).Message);
        }
        finally { await host.StopAsync(); }
        f.Mode = "reopen"; var restored = await f.Open();
        try
        {
            var outcome = await restored.QueryAsync(); Assert.Contains("logicalRef=logical", outcome); Assert.Contains("unknown", outcome);
            Assert.Equal(1, f.Handler.Posts); Assert.Equal(1, f.Handler.Queries);
            Assert.Contains("供应商命中/费用=unknown", await restored.ReadStatusAsync());
            int calls = f.Handler.Calls; f.WriteScope("other");
            Assert.Equal("cache_state_owner_changed", (await Assert.ThrowsAsync<InvalidOperationException>(() => restored.ReadStatusAsync())).Message);
            Assert.Equal(calls, f.Handler.Calls);
        }
        finally { await restored.StopAsync(); }
        f.WriteScope("user"); Assert.True((await new CacheContinuityClient(f.Client, true).DiscoverAsync()).Available);
    }

    [Fact]
    public async Task CorruptProtectedDemoStateIsNotRecreatedOrSubmitted()
    {
        using var f = new Fixture(); var host = await f.Open(); await host.StopAsync();
        var bytes = File.ReadAllBytes(f.StatePath); bytes[bytes.Length - 1] ^= 1; File.WriteAllBytes(f.StatePath, bytes); f.Mode = "reopen";
        await Assert.ThrowsAsync<CryptographicException>(() => f.Open()); Assert.Equal(bytes, File.ReadAllBytes(f.StatePath)); Assert.Equal(0, f.Handler.Posts);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Ticket = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "tansr-demo-cache-" + Guid.NewGuid().ToString("N"));
        internal readonly Handler Handler = new(); internal readonly HttpClient Http; internal readonly TansrClient Client;
        internal string Mode = "create"; internal string StatePath => Path.Combine(Directory, "state.dpapi");
        private string ScopePath => Path.Combine(Directory, "scope.json");
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory); WriteScope("user"); Http = new HttpClient(UnifiedStamp.Stamp(Handler));
            Client = new TansrClient(new TansrClientOptions
            {
                BaseUri = new Uri("https://serve.test/"),
                PrincipalProvider = () => "app/user",
                ExecutionScopeProvider = () => JsonSerializer.SerializeToElement(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" }),
                TokenProvider = _ => Task.FromResult("synthetic")
            }, Http);
        }
        internal void WriteScope(string user) => File.WriteAllBytes(ScopePath, JsonSerializer.SerializeToUtf8Bytes(new { principal = "app/" + user, scope = new { applicationScopeId = "app", endUserId = user, authorizationRevision = "1" } }));
        internal Task<NativeCacheContinuityHost> Open()
        {
            string config = Path.Combine(Directory, "config.json"); File.WriteAllBytes(config, JsonSerializer.SerializeToUtf8Bytes(new
            { format = "tansr-example-cache-continuity-v1", enablePreview = true, serveUrl = "https://serve.test/", sessionId = "session", trustedScopeFile = ScopePath, statePath = StatePath, mode = Mode }));
            return NativeCacheContinuityHost.StartAsync(config, "session", new Uri("https://serve.test/"), Client);
        }
        public void Dispose() { Client.Dispose(); Http.Dispose(); Handler.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Posts, Queries, Calls; private JsonElement request;
        private const string Limits = """
            {"controlBytes":65536,"exchangeBytes":33554432,"projectionBytes":262144,"projectionSegments":64,"ticketTtlMs":60000,"mappingIdleMs":60000,"mappingLifetimeMs":60000,"bindingsPerLogical":1,"mappingsPerUser":1,"mappingsPerApplication":1,"mappingsGlobal":1,"mappingBytes":1024,"receiptsGlobal":1,"receiptBytes":1024,"receiptRetentionMs":60000,"epochLifetimeMs":86400000,"diagnosticRowsPerUser":1,"diagnosticRowsPerApplication":1,"diagnosticRowsGlobal":1,"diagnosticBytes":1024,"diagnosticRetentionMs":60000,"diagnosticSamplePerMillion":0,"diagnosticsPageRows":100,"negotiatedTtlMs":1000,"retainedRowsPerUser":1,"retainedRowsPerApplication":1,"retainedRowsGlobal":1,"retainedBytesPerUser":1,"retainedBytesPerApplication":1,"retainedBytesGlobal":1}
            """;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            Calls++; string path = message.RequestUri!.AbsolutePath;
            using var limits = JsonDocument.Parse(Limits);
            if (path == "/api/capabilities/cache") return Json(JsonSerializer.SerializeToElement(new
            {
                protocol = "sdk2-cache-v1",
                audience = "serve-cache",
                features = new[] { "private-logical-cache-v1" },
                gateway = "confirmed",
                availability = "legacy-complete",
                revision = "1",
                limits = limits.RootElement,
                operationEpoch = new { id = "epoch", issuedAt = "2026-01-01T00:00:00Z", expiresAt = "2099-01-01T00:00:00Z", state = "active" }
            }));
            if (message.Method == HttpMethod.Post)
            {
                Posts++; request = WireJson.Parse(await message.Content!.ReadAsByteArrayAsync(ct)).GetProperty("request").Clone();
                throw new HttpRequestException("synthetic committed receipt lost");
            }
            Assert.Equal("/api/cache/operations", path); Queries++; Assert.Contains(Uri.EscapeDataString(request.GetProperty("requestId").GetString()!), message.RequestUri.Query);
            return Json(JsonSerializer.SerializeToElement(new
            {
                protocol = "sdk2-cache-v1",
                request,
                binding = new { protocol = "sdk2-cache-v1", audience = "serve-cache", bindingId = "binding", logicalRef = "logical", revision = "1", state = "active", mode = "private-logical-v1", groupGeneration = "0", projection = (object?)null, expiresAt = "2099-01-01T00:00:00Z", availability = "legacy-complete" },
                ticket = Fixture.Ticket,
                ticketExpiresAt = "2099-01-01T00:00:00Z",
                relation = "new"
            }));
        }
        private static HttpResponseMessage Json(JsonElement body) => new(HttpStatusCode.OK) { Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json") };
    }
}
