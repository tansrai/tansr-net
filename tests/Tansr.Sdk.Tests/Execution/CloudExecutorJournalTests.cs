using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Tests.Execution;

public sealed class CloudExecutorJournalTests
{
    private sealed record Request(string Method, string Path, JsonElement? Body);
    private sealed class Handler(Func<Request, Task<JsonElement>> respond) : HttpMessageHandler
    {
        internal readonly List<Request> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer synthetic", request.Headers.Authorization?.ToString());
            Assert.False(request.Headers.Contains("Idempotency-Key"));
            var item = new Request(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content == null ? null : WireJson.DecodeControl(await request.Content.ReadAsByteArrayAsync(ct)));
            Requests.Add(item);
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(WireJson.EncodeControl(await respond(item), 16 * 1024 * 1024)) };
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return result;
        }
    }
    private static TansrClient Client(HttpClient http, Func<JsonElement>? scope = null) => new(new TansrClientOptions
    {
        BaseUri = new Uri("https://serve.test/"),
        TokenProvider = _ => Task.FromResult("synthetic"),
        PrincipalProvider = () => "app/user",
        ExecutionScopeProvider = scope ?? (() => ExecutionFixture.Scope())
    }, http);
    private static JsonElement Record(JsonElement operation, string attempt, JsonElement? receipt = null) => JsonSerializer.SerializeToElement(new
    {
        operation,
        attemptId = attempt,
        connectionId = "connection-1",
        status = receipt.HasValue ? receipt.Value.GetProperty("status").GetString() : "pending",
        receipt
    });
    private static JsonElement Read(JsonElement? record) => JsonSerializer.SerializeToElement(new { protocol = WireContract.Protocol, record });
    private static readonly string ClaimToken = new('s', 43);
    private static JsonElement Claim(JsonElement record, string disposition) => disposition == "granted"
        ? JsonSerializer.SerializeToElement(new { protocol = WireContract.Protocol, disposition, record, claimToken = ClaimToken })
        : JsonSerializer.SerializeToElement(new { protocol = WireContract.Protocol, disposition, record });

    private sealed class CountingBackend : IExecutionBackend
    {
        internal int SideEffects;
        public JsonElement Registration => ExecutionFixture.Registration();
        public async Task<JsonElement> ExecuteAsync(JsonElement operation, Func<CancellationToken, Task> guard, CancellationToken ct)
        { await guard(ct); SideEffects++; return ExecutionFixture.Result(); }
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("pending")]
    [InlineData("response_lost")]
    public async Task OriginalExecutionHostNeverExecutesOrSubmitsWhenCloudOwnershipIsUnconfirmed(string outcome)
    {
        var operation = ExecutionFixture.Operation(); var backend = new CountingBackend();
        using var handler = new Handler(request =>
        {
            if (request.Path == "/api/executors/connections") return Task.FromResult(ExecutionFixture.Connection());
            if (request.Path.Contains("/operations?", StringComparison.Ordinal)) return Task.FromResult(ExecutionFixture.Batch(operation));
            if (request.Path.EndsWith("/journal/claim", StringComparison.Ordinal))
            {
                if (outcome == "response_lost") throw new HttpRequestException("synthetic committed grant response loss");
                return Task.FromResult(Claim(Record(operation, "other-owner"), outcome));
            }
            throw new InvalidOperationException("unexpected request: " + request.Path);
        });
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http);
        ExecutionHost? current = null;
        var journal = new CloudExecutorJournal(client, "session-1", () => current!.Connection!.Value);
        using var host = new ExecutionHost(new ExecutionClient(client), backend, journal, (_, _) => Task.CompletedTask);
        current = host;
        var error = await Xunit.Record.ExceptionAsync(() => host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        if (outcome == "response_lost") Assert.Equal("network_error", Assert.IsType<TansrProtocolException>(error).Code);
        else Assert.Equal(outcome, Assert.IsType<CloudJournalClaimUnavailableException>(error).Disposition);
        Assert.Equal(0, backend.SideEffects);
        Assert.Single(handler.Requests, request => request.Path.EndsWith("/journal/claim", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.Path.EndsWith("/complete", StringComparison.Ordinal) || request.Path.Contains("/receipts", StringComparison.Ordinal));
        await journal.CloseAsync();
    }

    [Fact]
    public async Task DurableGrantThenCompleteReusesOwnerIdentityAcrossHeartbeatRefreshWithoutAnyLocalStore()
    {
        var operation = ExecutionFixture.Operation(); var receipt = ExecutionFixture.Receipt(operation);
        string? attempt = null; JsonElement? stored = null;
        using var handler = new Handler(request =>
        {
            if (request.Path.EndsWith("/claim", StringComparison.Ordinal))
            {
                Assert.Equal("/api/executors/executor-1/journal/claim", request.Path);
                attempt = request.Body!.Value.GetProperty("attemptId").GetString();
                Assert.Equal(32, attempt!.Length);
                stored = Record(operation, attempt);
                return Task.FromResult(Claim(stored.Value, "granted"));
            }
            if (request.Path.EndsWith("/complete", StringComparison.Ordinal))
            {
                Assert.Equal(attempt, request.Body!.Value.GetProperty("attemptId").GetString());
                Assert.Equal(ClaimToken, request.Body.Value.GetProperty("claimToken").GetString());
                Assert.True(ExecutionFixture.Same(receipt, request.Body.Value.GetProperty("receipt")));
                stored = Record(operation, attempt!, receipt);
            }
            return Task.FromResult(Read(stored));
        });
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http);
        var journal = new CloudExecutorJournal(client, "session-1", () => ExecutionFixture.Connection());
        Assert.Equal(ExecutorJournalClaimStatus.Claimed, (await journal.ClaimAsync(operation)).Status);
        await journal.CompleteAsync(operation, receipt);
        Assert.True(ExecutionFixture.Same(receipt, (await journal.ReceiptAsync(operation))!.Value));
        Assert.DoesNotContain(handler.Requests, item => item.Path.Contains("operations", StringComparison.Ordinal));
        Assert.False((object)journal is IEncryptedExecutorJournal);
        await journal.CloseAsync();
    }

    [Fact]
    public async Task GrantWithoutOwnerProofIsRejectedBeforeExecution()
    {
        var operation = ExecutionFixture.Operation();
        using var handler = new Handler(request => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            protocol = WireContract.Protocol,
            disposition = "granted",
            record = Record(operation, request.Body!.Value.GetProperty("attemptId").GetString()!)
        })));
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http);
        var journal = new CloudExecutorJournal(client, "session-1", () => ExecutionFixture.Connection());
        await Assert.ThrowsAsync<WireProtocolException>(() => journal.ClaimAsync(operation));
        await Assert.ThrowsAsync<CloudJournalClaimUnavailableException>(() => journal.ClaimAsync(operation));
        Assert.Single(handler.Requests);
        await journal.CloseAsync();
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("pending")]
    public async Task ExistingOwnerNeverMapsToLocalPendingAndCannotBeCompletedAsUnknown(string disposition)
    {
        var operation = ExecutionFixture.Operation(); var active = Record(operation, "original-owner");
        using var handler = new Handler(request => Task.FromResult(request.Method == "POST" ? Claim(active, disposition) : Read(active)));
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http);
        var journal = new CloudExecutorJournal(client, "session-1", () => ExecutionFixture.Connection());
        var error = await Assert.ThrowsAsync<CloudJournalClaimUnavailableException>(() => journal.ClaimAsync(operation));
        Assert.Equal(disposition, error.Disposition);
        await Assert.ThrowsAsync<CloudJournalClaimUnavailableException>(() => journal.CompleteAsync(operation, ExecutionFixture.Receipt(operation, "unknown")));
        Assert.Single(handler.Requests, item => item.Method == "POST");
        await journal.CloseAsync();
    }

    [Fact]
    public async Task LostFirstClaimResponseDoesNotReclaimOrAuthorizeFromRead()
    {
        var operation = ExecutionFixture.Operation(); JsonElement? saved = null;
        using var handler = new Handler(request =>
        {
            if (request.Method == "POST")
            {
                saved = Record(operation, request.Body!.Value.GetProperty("attemptId").GetString()!);
                throw new HttpRequestException("synthetic response loss after commit");
            }
            return Task.FromResult(Read(saved));
        });
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http);
        var journal = new CloudExecutorJournal(client, "session-1", () => ExecutionFixture.Connection());
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => journal.ClaimAsync(operation))).Code);
        Assert.Null(await journal.ReceiptAsync(operation));
        await Assert.ThrowsAsync<CloudJournalClaimUnavailableException>(() => journal.ClaimAsync(operation));
        await Assert.ThrowsAsync<CloudJournalClaimUnavailableException>(() => journal.CompleteAsync(operation, ExecutionFixture.Receipt(operation, "unknown")));
        Assert.Single(handler.Requests, item => item.Method == "POST");
        await journal.CloseAsync();
    }

    [Fact]
    public async Task CompletedClaimIsReadOnlyReplayAndNewInstanceCannotStealOwnership()
    {
        var operation = ExecutionFixture.Operation(); var receipt = ExecutionFixture.Receipt(operation);
        var complete = Record(operation, "past-owner", receipt);
        using var handler = new Handler(request => Task.FromResult(request.Method == "POST" ? Claim(complete, "completed") : Read(complete)));
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http);
        var journal = new CloudExecutorJournal(client, "session-1", () => ExecutionFixture.Connection());
        Assert.Equal(ExecutorJournalClaimStatus.Completed, (await journal.ClaimAsync(operation)).Status);
        await journal.CompleteAsync(operation, receipt);
        await Assert.ThrowsAsync<CloudJournalClaimUnavailableException>(() => journal.CompleteAsync(operation, ExecutionFixture.Receipt(operation, "unknown")));
        Assert.Single(handler.Requests, item => item.Method == "POST");
        await journal.CloseAsync();
    }

    [Fact]
    public async Task ScopeOrConnectionDriftCannotPublishOwnerReceipt()
    {
        var operation = ExecutionFixture.Operation(); var connection = ExecutionFixture.Connection();
        using var handler = new Handler(request => Task.FromResult(Claim(Record(operation, request.Body!.Value.GetProperty("attemptId").GetString()!), "granted")));
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http);
        var journal = new CloudExecutorJournal(client, "session-1", () => connection);
        await journal.ClaimAsync(operation);
        connection = ExecutionFixture.Connection("2");
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.CompleteAsync(operation, ExecutionFixture.Receipt(operation)));
        Assert.Single(handler.Requests);
        await journal.CloseAsync();
    }

    [Fact]
    public async Task AuthorizationChangedWhileClaimWasBeingPublishedNeverGrantsTheNewContextExecution()
    {
        var operation = ExecutionFixture.Operation(); var scope = ExecutionFixture.Scope();
        using var handler = new Handler(request =>
        {
            scope = ExecutionFixture.Scope(revision: "2");
            return Task.FromResult(Claim(Record(operation, request.Body!.Value.GetProperty("attemptId").GetString()!), "granted"));
        });
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http, () => scope);
        var journal = new CloudExecutorJournal(client, "session-1", () => ExecutionFixture.Connection());
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<TansrProtocolException>(() => journal.ClaimAsync(operation))).Code);
        Assert.Single(handler.Requests); await journal.CloseAsync();
    }

    [Fact]
    public async Task ListIsSessionFilteredAndRejectsUnorderedOrForeignRecords()
    {
        var one = ExecutionFixture.Operation("a"); var two = ExecutionFixture.Operation("b");
        var reversed = false;
        using var handler = new Handler(request =>
        {
            Assert.Contains("sessionId=session-1", request.Path, StringComparison.Ordinal);
            Assert.Contains("connectionId=connection-1", request.Path, StringComparison.Ordinal);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                protocol = WireContract.Protocol,
                records = reversed ? new[] { Record(two, "b"), Record(one, "a") } : new[] { Record(one, "a"), Record(two, "b") },
                nextCursor = "b"
            }));
        });
        using var http = Tansr.Sdk.Tests.Api.UnifiedStamp.Client(handler); using var client = Client(http);
        var journal = new CloudExecutorJournal(client, "session-1", () => ExecutionFixture.Connection());
        Assert.Equal(2, (await journal.OperationsAsync()).Count);
        reversed = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.OperationsAsync());
        await journal.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => journal.OperationsAsync());
    }
}
