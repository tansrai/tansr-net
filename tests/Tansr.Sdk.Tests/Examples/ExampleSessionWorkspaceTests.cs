using System.Net;
using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Tests.Api;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Tests.Examples;

public sealed class ExampleSessionWorkspaceTests
{
    [Fact]
    public void LocalHostModuleIsAnExplicitPinnedDeveloperPair()
    {
        var values = new Dictionary<string, string>();
        Assert.Empty(ExampleSessionOptions.ReadLocalHostModuleArguments(values.GetValueOrDefault));
        values["TANSR_LOCAL_SERVE_HOST_MODULE"] = Path.Combine(Path.GetTempPath(), "trusted-host.cjs");
        Assert.Throws<InvalidOperationException>(() => ExampleSessionOptions.ReadLocalHostModuleArguments(values.GetValueOrDefault));
        values["TANSR_LOCAL_SERVE_HOST_MODULE_SHA256"] = new string('A', 64);
        Assert.Equal(new[] { "--host-module", values["TANSR_LOCAL_SERVE_HOST_MODULE"], "--host-module-sha256", new string('a', 64) }, ExampleSessionOptions.ReadLocalHostModuleArguments(values.GetValueOrDefault));
        values["TANSR_LOCAL_SERVE_HOST_MODULE"] = "relative.cjs";
        Assert.Throws<InvalidOperationException>(() => ExampleSessionOptions.ReadLocalHostModuleArguments(values.GetValueOrDefault));
        values["TANSR_LOCAL_SERVE_HOST_MODULE"] = Path.Combine(Path.GetTempPath(), "trusted-host.js");
        Assert.Throws<InvalidOperationException>(() => ExampleSessionOptions.ReadLocalHostModuleArguments(values.GetValueOrDefault));
        values["TANSR_LOCAL_SERVE_HOST_MODULE"] = Path.Combine(Path.GetTempPath(), "trusted-host.cjs");
        values["TANSR_LOCAL_SERVE_HOST_MODULE_SHA256"] = "unverified";
        Assert.Throws<InvalidOperationException>(() => ExampleSessionOptions.ReadLocalHostModuleArguments(values.GetValueOrDefault));
    }

    [Fact]
    public void ThreeHostsShareTrustedProfileBudgetAndResumeDeclarations()
    {
        var values = new Dictionary<string, string> { ["TANSR_PROFILE"] = "approved-orders", ["TANSR_THINKING_BUDGET"] = "128", ["TANSR_MAX_TOKENS"] = "2048", ["TANSR_MAX_USD"] = "0.25" };
        using var declaration = JsonDocument.Parse("[]");
        var options = ExampleSessionOptions.Create(" large ", " original ", declaration.RootElement, key => values.GetValueOrDefault(key));
        Assert.Equal("approved-orders", options.Profile); Assert.Equal("large", options.Model); Assert.Equal("original", options.ResumeSessionId);
        Assert.Null(options.ClientTools); Assert.Equal(128, options.ThinkingBudget); Assert.Equal(2048, options.MaxTokens); Assert.Equal(0.25m, options.MaxUsd);
    }

    [Theory]
    [InlineData("TANSR_THINKING_BUDGET", "-1")]
    [InlineData("TANSR_THINKING_BUDGET", "0")]
    [InlineData("TANSR_MAX_TOKENS", "0")]
    [InlineData("TANSR_MAX_USD", "NaN")]
    [InlineData("TANSR_MAX_USD", "-2")]
    public void InvalidHostBudgetsFailBeforeSessionCreation(string name, string value)
        => Assert.Throws<InvalidOperationException>(() => ExampleSessionOptions.Create(null, null, null, key => key == name ? value : null));

    [Fact]
    public void Sdk2CreationRequiresOriginalHostKeyAndResumeDoesNotMintAnother()
    {
        var values = new Dictionary<string, string> { ["TANSR_SESSION_CONTRACT"] = "sdk2-offload-v1" };
        Assert.Throws<InvalidOperationException>(() => ExampleSessionOptions.Create(null, null, null, values.GetValueOrDefault));
        values["TANSR_SESSION_REQUEST_ID"] = "original-key";
        var created = ExampleSessionOptions.Create(null, null, null, values.GetValueOrDefault);
        Assert.Equal("original-key", created.RequestId);
        Assert.Equal("original-key", ExampleSessionOptions.Create(null, null, null, values.GetValueOrDefault).RequestId);
        Assert.Null(ExampleSessionOptions.Create(null, "same-session", null, values.GetValueOrDefault).RequestId);
        Assert.Equal(SessionContract.Sdk2OffloadV1, ExampleSessionOptions.ReadContract(values.GetValueOrDefault));
    }

    [Fact]
    public async Task ClosedWorkspaceCannotSendCommandsOnOldSession()
    {
        using var fixture = new Fixture(); await fixture.StartAsync(); fixture.Lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Workspace.ExecuteAsync(9, "C:/old-user"));
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(2, "2", "/api/sessions?limit=25&offset=50")]
    [InlineData(3, "1", "/api/sessions/s/history?offset=25&limit=25")]
    [InlineData(4, "", "/api/sessions/s/checkpoints")]
    [InlineData(9, "approved-work", "/api/sessions/s/cwd")]
    public async Task ManagementUsesPublicSessionRoutesAndOriginalIdentity(int action, string input, string route)
    {
        using var fixture = new Fixture(); await fixture.StartAsync(); await fixture.Workspace.ExecuteAsync(action, input);
        Assert.Equal(route, fixture.Handler.Requests.Last());
    }

    [Fact]
    public async Task ForkDoesNotReplaceOriginalSessionOrRunItsTools()
    {
        using var fixture = new Fixture(); await fixture.StartAsync(); var result = await fixture.Workspace.ExecuteAsync(8, "snapshot");
        Assert.Contains("forkSessionId=branch", result, StringComparison.Ordinal); Assert.Equal("s", fixture.Session.Id);
        Assert.Equal(new[] { "/api/sessions", "/api/sessions" }, fixture.Handler.Requests);
        Assert.Contains("\"fork\":{\"sessionId\":\"s\",\"checkpointId\":\"snapshot\"}", fixture.Handler.Bodies.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingExportFileRemainsUntouchedAndFailedExportIsNotRetried()
    {
        using var fixture = new Fixture(); await fixture.StartAsync(); var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "keep-user-archive");
            await Assert.ThrowsAsync<IOException>(() => fixture.Workspace.ExportAsync("snapshot", path));
            Assert.Equal("keep-user-archive", await File.ReadAllTextAsync(path));
            Assert.Single(fixture.Handler.Requests, route => route.EndsWith("/export", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExpiredLocalApprovalRejectsLateAnswerWithoutAnotherInputReader()
    {
        var messages = new List<string>(); using var approvals = new ExampleDeviceApprovals(messages.Add); using var stop = new CancellationTokenSource();
        var pending = approvals.RequestAsync("Shell approved only for this operation", stop.Token);
        var id = messages[0].Split('\n')[0].Substring("device_approval=".Length);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(approvals.Answer(id, true)); Assert.Contains("device_approval_closed=" + id, messages);
    }

    [Fact]
    public async Task LocalApprovalRepliesOnceAndKeepsPerOperationIdentity()
    {
        var messages = new List<string>(); using var approvals = new ExampleDeviceApprovals(messages.Add);
        var first = approvals.RequestAsync("first", default); var firstId = messages[0].Split('\n')[0].Substring("device_approval=".Length);
        var second = approvals.RequestAsync("second", default); var secondId = messages[1].Split('\n')[0].Substring("device_approval=".Length);
        Assert.True(approvals.Answer(firstId, true)); Assert.False(approvals.Answer(firstId, false)); Assert.True(await first);
        Assert.False(second.IsCompleted); Assert.True(approvals.Answer(secondId, false)); Assert.False(await second);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Handler Handler = new();
        private readonly HttpClient http;
        private readonly TansrClient client;
        private readonly SessionView view = new();
        internal readonly CancellationTokenSource Lifetime = new();
        internal AgentSession Session = null!;
        internal ExampleSessionWorkspace Workspace = null!;
        internal Fixture()
        {
            http = new HttpClient(UnifiedStamp.Stamp(Handler)); client = new TansrClient(new TansrClientOptions { BaseUri = new Uri("https://example.invalid"), TokenProvider = _ => Task.FromResult("fixture") }, http);
        }
        internal async Task StartAsync()
        { Session = await client.CreateSessionAsync(new CreateSessionOptions()); Workspace = new ExampleSessionWorkspace(client, Session, () => view.Snapshot, () => "fixture host", Lifetime.Token); }
        public void Dispose() { Lifetime.Cancel(); Lifetime.Dispose(); view.Dispose(); client.Dispose(); http.Dispose(); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal readonly List<string> Requests = [];
        internal readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery); var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct); Bodies.Add(body);
            if (request.RequestUri.AbsolutePath.EndsWith("/export", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("original-checkpoint-archive"));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            var response = "{}";
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/sessions")
                response = "{\"sessionId\":\"" + (body.Contains("fork", StringComparison.Ordinal) ? "branch" : "s") + "\",\"resumed\":false,\"lastSeq\":-1}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
