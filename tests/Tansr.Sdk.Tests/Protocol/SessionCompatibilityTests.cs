using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Tests.Protocol;

public sealed class SessionCompatibilityTests
{
    private const string CapabilitiesPath = "/api/capabilities/sessions?protocol=sdk2-ext-v1";
    private const string Available = "{\"contracts\":[{\"availability\":\"legacy-complete\",\"contract\":\"sdk1\"},{\"availability\":\"source-required\",\"contract\":\"sdk2-offload-v1\"}],\"protocol\":\"sdk2-ext-v1\"}";
    private const string Missing = "{\"contracts\":[{\"availability\":\"legacy-complete\",\"contract\":\"sdk1\"}],\"protocol\":\"sdk2-ext-v1\"}";
    private const string Created = "{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":0,\"contract\":\"sdk2-offload-v1\",\"availability\":\"source-required\"}";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        /// <summary>UAPI-01: both families share `/api/sessions`; the family travels in `tansr-session-family`.</summary>
        internal List<string?> Families { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            Families.Add(request.Headers.TryGetValues("tansr-session-family", out var values) ? string.Join(",", values) : null);
            return respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string text, int status = 200) => new((HttpStatusCode)status)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static TansrClientOptions Options(bool sdk2 = true) => new()
    {
        BaseUri = new("https://serve.test/"),
        TokenProvider = _ => Task.FromResult("synthetic-ticket"),
        PrincipalProvider = () => "app/user",
        SessionContract = sdk2 ? SessionContract.Sdk2OffloadV1 : SessionContract.Sdk1
    };

    public static IEnumerable<object[]> CompatibilityVectors()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "session-compatibility.json")));
        foreach (var item in fixture.RootElement.GetProperty("vectors").EnumerateArray())
            yield return [item.GetProperty("id").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(CompatibilityVectors))]
    public async Task OriginalNodeAndCSharpUseTheSameDiscoveryAndNoFallbackBoundary(string id, string text)
    {
        Assert.NotEmpty(id);
        using var document = JsonDocument.Parse(text);
        var vector = document.RootElement;
        bool sdk2 = vector.GetProperty("contract").GetString() == "sdk2-offload-v1";
        using var handler = new Handler((request, _) =>
        {
            var response = request.RequestUri!.PathAndQuery == CapabilitiesPath ? vector.GetProperty("discovery") : vector.GetProperty("created");
            if (response.TryGetProperty("networkFailure", out var network) && network.GetBoolean()) throw new HttpRequestException("synthetic-private-detail");
            return Task.FromResult(Json(response.GetProperty("body").GetString()!, response.GetProperty("status").GetInt32()));
        });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        using var client = new TansrClient(Options(sdk2), http);
        Exception? failure = await Record.ExceptionAsync(() => client.CreateSessionAsync(new() { RequestId = sdk2 ? "create-original" : null }));
        var expectedError = vector.GetProperty("netError");
        if (expectedError.ValueKind == JsonValueKind.Null) Assert.Null(failure);
        else
        {
            Assert.NotNull(failure);
            Assert.Equal(expectedError.GetString(), Assert.IsAssignableFrom<TansrException>(failure).Code);
            Assert.DoesNotContain("synthetic-private-detail", failure.ToString());
        }
        // `requests` are the legacy Node sdk2 client paths (scripts/check-session-compatibility.mjs); the C# SDK emits the unified /api form.
        Assert.Equal(vector.GetProperty("apiRequests").EnumerateArray().Select(v => v.GetString()), handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitDiscoveryInvalidatesAnEarlierDecisionWhenRefreshFails(bool missing)
    {
        int discovery = 0;
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.PathAndQuery != CapabilitiesPath ? Json(Created)
            : ++discovery == 1 ? Json(Available) : missing ? Json(Missing) : Json("{\"error\":{\"code\":\"unauthorized\"}}", 401)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        using var client = new TansrClient(Options(), http);
        await client.CreateSessionAsync(new() { RequestId = "first" });
        Assert.NotNull(await Record.ExceptionAsync(() => client.GetSessionCapabilitiesAsync()));
        Assert.NotNull(await Record.ExceptionAsync(() => client.CreateSessionAsync(new() { RequestId = "second" })));
        Assert.Equal(["GET " + CapabilitiesPath, "POST /api/sessions", "GET " + CapabilitiesPath, "GET " + CapabilitiesPath], handler.Requests);
    }

    [Fact]
    public async Task RefreshAndCreationShareTheDiscoveryBarrier()
    {
        int discovery = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.PathAndQuery != CapabilitiesPath) return Json(Created);
            if (++discovery == 1) return Json(Available);
            if (discovery == 2) { entered.SetResult(); await release.Task.WaitAsync(cancellationToken); }
            return Json(Missing);
        });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        using var client = new TansrClient(Options(), http);
        await client.CreateSessionAsync(new() { RequestId = "first" });
        var refresh = client.GetSessionCapabilitiesAsync();
        await entered.Task;
        var create = client.CreateSessionAsync(new() { RequestId = "second" });
        Assert.False(create.IsCompleted);
        release.SetResult();
        Assert.NotNull(await Record.ExceptionAsync(() => refresh));
        Assert.NotNull(await Record.ExceptionAsync(() => create));
        Assert.Single(handler.Requests, request => request.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoveryCancellationDoesNotFallBackOrPoisonTheNextExplicitAttempt()
    {
        int discovery = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.PathAndQuery != CapabilitiesPath) return Json(Created);
            if (++discovery == 1) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return Json(Available);
        });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        using var client = new TansrClient(Options(), http);
        using var cancellation = new CancellationTokenSource();
        var first = client.CreateSessionAsync(new() { RequestId = "original" }, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(handler.Requests);
        Assert.Equal("s", (await client.CreateSessionAsync(new() { RequestId = "original" })).Id);
        Assert.Equal(3, handler.Requests.Count);
        // No silent sdk1 fallback: every request still declares the sdk2 family (paths are shared under /api).
        Assert.All(handler.Families, family => Assert.Equal("sdk2-offload-v1", family));
    }

    [Fact]
    public async Task ANewTicketRequiresFreshDiscoveryWithoutChangingStorageOrPrincipal()
    {
        string token = "first-ticket";
        var options = Options();
        options.TokenProvider = _ => Task.FromResult(token);
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.PathAndQuery == CapabilitiesPath
            ? Json(token == "first-ticket" ? Available : Missing) : Json(Created)));
        using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        using var client = new TansrClient(options, http);
        await client.CreateSessionAsync(new() { RequestId = "first" });
        token = "renewed-ticket";
        Assert.Equal("unsupported_capability", (await Assert.ThrowsAsync<TansrProtocolException>(() => client.CreateSessionAsync(new() { RequestId = "second" }))).Code);
        Assert.Equal(["GET " + CapabilitiesPath, "POST /api/sessions", "GET " + CapabilitiesPath], handler.Requests);
    }

    [Fact]
    public async Task DiscoveryDeadlineHasNoCreationOrAutomaticFallback()
    {
        var options = Options(); options.RequestTimeout = TimeSpan.FromMilliseconds(100);
        using var handler = new Handler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The request must be canceled.");
        });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        using var client = new TansrClient(options, http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateSessionAsync(new() { RequestId = "original" }));
        Assert.Equal(["GET " + CapabilitiesPath], handler.Requests);
    }

    [Fact]
    public async Task LateOldTicketDiscoveryCannotAuthorizeARenewedTicket()
    {
        string token = "old-ticket";
        var options = Options(); options.TokenProvider = _ => Task.FromResult(token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            if (request.Headers.Authorization!.Parameter == "old-ticket")
            {
                entered.SetResult(); await release.Task.WaitAsync(cancellationToken); return Json(Available);
            }
            return Json(Missing);
        });
        using var http = new HttpClient(UnifiedStamp.Stamp(handler));
        using var client = new TansrClient(options, http);
        var refresh = client.GetSessionCapabilitiesAsync();
        await entered.Task;
        token = "renewed-ticket";
        var create = client.CreateSessionAsync(new() { RequestId = "new-ticket-original" });
        release.SetResult();
        await refresh;
        Assert.Equal("unsupported_capability", (await Assert.ThrowsAsync<TansrProtocolException>(() => create)).Code);
        Assert.Equal(["GET " + CapabilitiesPath, "GET " + CapabilitiesPath], handler.Requests);
    }
}
