using System.Net.Http.Headers;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Xunit.Abstractions;

namespace Tansr.Sdk.IntegrationTests;

/// <summary>真实 Serve HTTP 和原设备宿主，SQL 由明确登记的受控连接夹具提供；不冒充生产 MySQL/OSS。</summary>
public sealed class ServeCloudJournalTests(ITestOutputHelper output)
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(name + " is required for the explicit cloud journal fixture.");

    private sealed class Backend : IExecutionBackend
    {
        internal int SideEffects;
        public JsonElement Registration => Json(new
        {
            protocol = "sdk2-ext-v1",
            executorId = "pc",
            platform = new { platform = "windows", arch = "x64", language = "csharp", runtimeVersion = "10", adapterVersion = "1" },
            workspaces = new[] { new { workspaceId = "work", revision = "1" } },
            operations = new[] { "fs.inspect", "fs.read", "fs.write", "fs.mkdir" }
        });
        public async Task<JsonElement> ExecuteAsync(JsonElement operation, Func<CancellationToken, Task> guard, CancellationToken ct)
        {
            Assert.Equal("fs.write", operation.GetProperty("request").GetProperty("operation").GetString());
            var bytes = Convert.FromBase64String(operation.GetProperty("request").GetProperty("args").GetProperty("bytesBase64").GetString()!);
            await guard(ct); Interlocked.Increment(ref SideEffects);
            return Json(new { operation = "fs.write", args = new { hash = WireJson.Sha256(bytes) } });
        }
    }

    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task OriginalDevicePipelineCompletesOnceAndLostCloudClaimNeverRunsTheSecondTool()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        var token = Required("TANSR_CLOUD_JOURNAL_TOKEN");
        using var control = new HttpClient { BaseAddress = new Uri(Required("TANSR_CLOUD_JOURNAL_CONTROL_URL")), Timeout = TimeSpan.FromSeconds(10) };
        control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var client = new TansrClient(new TansrClientOptions
        {
            BaseUri = new Uri(Required("TANSR_CLOUD_JOURNAL_URL")),
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(token),
            PrincipalProvider = () => "app/u-alice",
            ExecutionScopeProvider = () => Json(new { applicationScopeId = "app", endUserId = "u-alice", authorizationRevision = "1" }),
            RequestTimeout = TimeSpan.FromSeconds(10),
            MaxReconnectAttempts = 0
        });
        var mode = await client.GetSessionPersistenceAsync("s", ct);
        Assert.Equal("cloud", mode.GetProperty("mode").GetString());
        Assert.False(mode.GetProperty("localDisk").GetBoolean());
        Assert.True(mode.GetProperty("handoff").GetProperty("executionJournal").GetBoolean());
        DeviceSessionHost? current = null;
        var journal = new CloudExecutorJournal(client, "s", () => current!.Connection!.Value);
        var backend = new Backend();
        using var host = new DeviceSessionHost(new ExecutionClient(client), backend, journal,
            new DeviceSessionOptions { SessionId = "s", WorkspaceId = "work", RequestedTools = ["Write"] }, (_, _) => Task.CompletedTask);
        current = host;
        try
        {
            await host.StartAsync(ct);
            var first = (await Command(HttpMethod.Post, "start")).GetProperty("operation");
            JsonElement state;
            do
            {
                ct.ThrowIfCancellationRequested(); await Task.Delay(25, ct);
                state = await Command(HttpMethod.Get, "status");
            } while (state.GetProperty("jobs")[0].GetProperty("result").ValueKind == JsonValueKind.String);
            Assert.Equal("completed", state.GetProperty("jobs")[0].GetProperty("result").GetProperty("status").GetString());
            Assert.Equal(1, backend.SideEffects);
            Assert.Equal("completed", (await journal.ReceiptAsync(first, ct))!.Value.GetProperty("status").GetString());
            Assert.Single(await journal.OperationsAsync(cancellationToken: ct));
            await Command(HttpMethod.Post, "lose-next-commit");
            var second = (await Command(HttpMethod.Post, "start")).GetProperty("operation");
            var failure = await Record.ExceptionAsync(() => host.Completion.WaitAsync(ct));
            Assert.NotNull(failure); output.WriteLine("Lost grant surfaced as: " + failure.GetType().Name + ": " + failure.Message);
            Assert.Equal(1, backend.SideEffects);
            Assert.Null(await journal.ReceiptAsync(second, ct));
            await Assert.ThrowsAsync<CloudJournalClaimUnavailableException>(() => journal.ClaimAsync(second, ct));
            Assert.Equal(2, (await journal.OperationsAsync(cancellationToken: ct)).Count);
            output.WriteLine("Actual Serve HTTP: initialize/register/bind/poll; cloud publish then one tool; claim response loss => no second effect, read-only pending reconciliation. SQL=controlled connection fixture.");
        }
        finally
        {
            try { await host.StopAsync(); } catch { /* The original failure is asserted above; stopping never reexecutes. */ }
            await journal.CloseAsync();
        }

        async Task<JsonElement> Command(HttpMethod method, string path)
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await control.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct)); return document.RootElement.Clone();
        }
    }
}
