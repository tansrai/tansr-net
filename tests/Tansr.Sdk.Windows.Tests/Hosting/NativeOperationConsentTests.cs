using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class NativeOperationConsentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSharedDeviceRequiresBorrowedControllerAndDoesNotOwnIt(bool borrowed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tansr-consent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json"); var endpoint = new Uri("https://local.example.test");
        using var http = new HttpClient(new ListHandler());
        using var client = new TansrClient(new TansrClientOptions
        {
            BaseUri = endpoint,
            TokenProvider = _ => Task.FromResult("synthetic-owned-token"),
            PrincipalProvider = () => "app/user",
            ExecutionScopeProvider = () => JsonSerializer.SerializeToElement(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" })
        }, http);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            format = "tansr-example-terminal-device-v1",
            enablePreview = true,
            sessionId = "session",
            serveUrl = endpoint.AbsoluteUri,
            trustedScopeFile = Path.Combine(directory, "trusted.json"),
            useControllerForDevice = true,
            workspace = new { path = Path.Combine(directory, "missing-workspace") },
            journal = new { path = Path.Combine(directory, "journal.sqlite") }
        }));
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => NativeTerminalDeviceHost.StartAsync(path, "session", endpoint,
                (_, _) => Task.FromResult(true), _ => { }, borrowedController: borrowed ? client : null));
            if (!borrowed) Assert.Equal("terminal_device_borrowed_controller_required", error.Message);
            else Assert.NotEqual("terminal_device_borrowed_controller_required", error.Message);
            Assert.Equal(0, (await client.ListSessionsAsync()).GetProperty("total").GetInt32());
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task RepeatedAndConcurrentGuardsShareOneDecisionAndOneObservation()
    {
        using var consent = new NativeOperationConsent(); var approvals = 0; var observations = 0; var checks = 0;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Guard() => consent.EnsureAsync(Operation(), _ => { Interlocked.Increment(ref checks); return Task.FromResult("authority"); },
            _ => { Interlocked.Increment(ref approvals); return answer.Task; }, () => Interlocked.Increment(ref observations), default);
        var guards = Enumerable.Range(0, 5).Select(_ => Guard()).ToArray();
        Assert.Equal(1, approvals); Assert.Equal(0, observations); answer.SetResult(true);
        await Task.WhenAll(guards); await Guard();
        Assert.Equal(1, approvals); Assert.Equal(1, observations); Assert.True(checks >= 12);
    }

    [Fact]
    public async Task CurrentAuthorityRevocationCannotResurrectOldConsentWhenRestored()
    {
        using var consent = new NativeOperationConsent(); var revision = "one"; var approvals = 0;
        Task Guard() => consent.EnsureAsync(Operation(), _ => Task.FromResult(revision), _ => { approvals++; return Task.FromResult(true); }, () => { }, default);
        await Guard(); revision = "two"; await Assert.ThrowsAsync<ExecutionRejectedException>(Guard);
        revision = "one"; await Assert.ThrowsAsync<ExecutionRejectedException>(Guard); Assert.Equal(1, approvals);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("digest")]
    [InlineData("scope")]
    public async Task AnotherOperationIdentityCannotReuseAnApproval(string changed)
    {
        using var consent = new NativeOperationConsent(); var approvals = 0;
        Task Guard(JsonElement operation) => consent.EnsureAsync(operation, _ => Task.FromResult("authority"), _ => { approvals++; return Task.FromResult(true); }, () => { }, default);
        await Guard(Operation()); await Assert.ThrowsAsync<ExecutionRejectedException>(() => Guard(Operation(changed: changed)));
        await Guard(Operation(id: "new-operation", changed: changed)); Assert.Equal(2, approvals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOrStoppedFirstPromptNeverCachesAllow(bool stop)
    {
        using var consent = new NativeOperationConsent(); using var cancellation = new CancellationTokenSource();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var observed = 0;
        var first = consent.EnsureAsync(Operation(), _ => Task.FromResult("authority"), _ => answer.Task, () => observed++, cancellation.Token);
        if (stop) consent.Dispose(); else cancellation.Cancel();
        if (stop) await Assert.ThrowsAnyAsync<Exception>(() => first);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        answer.SetResult(true);
        await Assert.ThrowsAnyAsync<Exception>(() => consent.EnsureAsync(Operation(), _ => Task.FromResult("authority"), _ => Task.FromResult(true), () => observed++, default));
        Assert.Equal(0, observed);
    }

    [Fact]
    public async Task DenialIsNotRetriedAndCapacityFailsClosed()
    {
        using var consent = new NativeOperationConsent(2); var approvals = 0; var observations = 0;
        Task Guard(string id, bool allow) => consent.EnsureAsync(Operation(id), _ => Task.FromResult("authority"), _ => { approvals++; return Task.FromResult(allow); }, () => observations++, default);
        Assert.Equal("EACCES", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => Guard("denied", false))).Code);
        await Assert.ThrowsAsync<ExecutionRejectedException>(() => Guard("denied", true));
        await Guard("allowed", true);
        Assert.Equal("local_approval_capacity", (await Assert.ThrowsAsync<ExecutionRejectedException>(() => Guard("overflow", true))).Code);
        Assert.Equal(2, approvals); Assert.Equal(1, observations);
    }

    [Fact]
    public async Task AuthorityChangedDuringHumanPromptDoesNotStartObservation()
    {
        using var consent = new NativeOperationConsent(); var revision = "one"; var observed = 0;
        await Assert.ThrowsAsync<ExecutionRejectedException>(() => consent.EnsureAsync(Operation(), _ => Task.FromResult(revision),
            _ => { revision = "two"; return Task.FromResult(true); }, () => observed++, default));
        Assert.Equal(0, observed);
    }

    private static JsonElement Operation(string id = "operation", string changed = "") => JsonSerializer.SerializeToElement(new
    {
        operationId = id,
        digest = changed == "digest" ? "new-digest" : "digest",
        scope = new { authorizationRevision = changed == "scope" ? "2" : "1" },
        binding = new { bindingId = changed == "binding" ? "other" : "binding" },
        expiresAt = "2099-01-01T00:00:00.000Z"
    });

    private sealed class ListHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("{\"sessions\":[],\"total\":0}", System.Text.Encoding.UTF8, "application/json") });
    }
}
