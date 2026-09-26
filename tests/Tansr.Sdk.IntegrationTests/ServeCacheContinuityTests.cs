using System.Text.Json;
using Tansr.Sdk.Cache;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.IntegrationTests;

public sealed class ServeCacheContinuityTests
{
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required; use the Serve integration runner.");

    [Fact]
    [Trait("Category", "ServeCacheContinuity")]
    public async Task PublicCacheAssemblyKeepsOriginalReceiptAcrossClientAndServeRestartAndRejectsForeignUserAndRevocation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        var origin = new Uri(Required("TANSR_SERVE_CACHE_URL")); Assert.Equal("127.0.0.1", origin.Host);
        using var lost = new LostOpenResponse(); using var http = new HttpClient(lost); using var client = Client(origin, http);
        var session = await client.CreateSessionAsync(new CreateSessionOptions(), ct);
        var cache = new CacheContinuityClient(client, enablePreview: true);
        Assert.True((await cache.DiscoverAsync(ct)).Available);
        var operation = await cache.PrepareOpenAsync(session.Id, CacheContinuityOpenKind.New, requestId: "net-cache-original", cancellationToken: ct);
        byte[] original = operation.ExportOriginalRequest();
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => cache.SubmitAsync(operation, ct))).Code);
        Assert.Equal("query_status_required", (await Assert.ThrowsAsync<TansrProtocolException>(() => cache.SubmitAsync(operation, ct))).Code);
        var recovered = await cache.QueryAsync(operation, ct); Assert.Equal(1, lost.Posts); Assert.NotNull(lost.Receipt);
        Assert.Equal(WireJson.CanonicalString(lost.Receipt!.Value), WireJson.CanonicalString(WireJson.Parse(operation.ExportOriginalReceipt()!)));
        var prior = await Command(new { id = "before-restart", action = "inspect" }, ct);
        Assert.Equal(0, prior.GetProperty("modelExchanges").GetInt32()); Assert.Equal(1, prior.GetProperty("runtimeCount").GetInt32());
        Assert.Single(prior.GetProperty("intents").EnumerateArray(), item => item.GetProperty("operation").GetString() == "enroll");

        var restarted = await Command(new { id = "restart-once", action = "restart" }, ct);
        using var restoredClient = Client(new Uri(restarted.GetProperty("url").GetString()!));
        var restoredCache = new CacheContinuityClient(restoredClient, enablePreview: true);
        var restoredOperation = restoredCache.RestoreOperation(operation.Action, original, operation.OriginalPrincipal, operation.ExportOriginalReceipt());
        var afterRestart = await restoredCache.QueryAsync(restoredOperation, ct);
        Assert.Equal(recovered.Binding.Id, afterRestart.Binding.Id); Assert.Equal(recovered.Binding.LogicalReference, afterRestart.Binding.LogicalReference);
        Assert.Equal(recovered.Ticket!.ExportProtectedValue(), afterRestart.Ticket!.ExportProtectedValue());
        var current = await restoredCache.ReadBindingAsync(afterRestart.Binding.Id, ct); Assert.Equal("active", current.State);
        Assert.Equal(afterRestart.Binding.LogicalReference, current.LogicalReference);
        var diagnostics = await restoredCache.ReadDiagnosticsAsync(current.Id, cancellationToken: ct);
        Assert.Equal(0, diagnostics.GetProperty("rows").GetArrayLength()); Assert.Equal(JsonValueKind.Null, diagnostics.GetProperty("next").ValueKind);
        using var otherClient = Client(new Uri(restarted.GetProperty("url").GetString()!), other: true);
        var other = new CacheContinuityClient(otherClient, enablePreview: true);
        Assert.Equal("context_changed", Assert.Throws<TansrProtocolException>(() => other.RestoreOperation(operation.Action, original, operation.OriginalPrincipal)).Code);
        var foreign = await Assert.ThrowsAsync<CacheContinuityException>(() => other.ReadBindingAsync(current.Id, ct)); Assert.Equal(404, foreign.StatusCode);
        await Command(new { id = "revoke-cache-access", action = "enabled", enabled = false }, ct);
        var revoked = await Assert.ThrowsAsync<CacheContinuityException>(() => restoredCache.ReadBindingAsync(current.Id, ct)); Assert.Equal(401, revoked.StatusCode);
        await Command(new { id = "restore-cache-access", action = "enabled", enabled = true }, ct);
        var final = await Command(new { id = "final-cache-facts", action = "inspect" }, ct);
        Assert.Equal(1, final.GetProperty("restarts").GetInt32()); Assert.Equal(0, final.GetProperty("modelExchanges").GetInt32());
        Assert.Equal(prior.GetProperty("intents").GetRawText(), final.GetProperty("intents").GetRawText());
        Assert.Equal(original, restoredOperation.ExportOriginalRequest());

        // Close is a cache control operation, not source-history deletion. Its original ticket must stop working.
        var continued = await restoredClient.ResumeSessionAsync(session.Id, cancellationToken: ct);
        current = await restoredCache.ReadBindingAsync(current.Id, ct);
        var closed = await restoredCache.SubmitAsync(await restoredCache.PrepareCloseAsync(current, afterRestart.Ticket!, "net-cache-close", ct), ct);
        Assert.Equal("closed", closed.Binding.State); Assert.Null(closed.Ticket);
        var closedTicket = await restoredCache.PrepareOpenAsync(continued.Id, CacheContinuityOpenKind.Resume, afterRestart.Ticket, "net-closed-ticket", ct);
        var closedDenied = await Assert.ThrowsAsync<CacheContinuityException>(() => restoredCache.SubmitAsync(closedTicket, ct));
        Assert.Equal(404, closedDenied.StatusCode); Assert.Equal("mapping_unavailable", closedDenied.Code);
        await continued.CloseAsync(ct);

        // A new client/device restores the original session; only the trusted Serve continuity pipeline may change its runtime.
        var beforeMove = await restoredClient.CreateSessionAsync(new CreateSessionOptions(), ct);
        var beforeMoveCache = await restoredCache.SubmitAsync(await restoredCache.PrepareOpenAsync(beforeMove.Id, CacheContinuityOpenKind.New,
            requestId: "net-runtime-original", cancellationToken: ct), ct);
        await SyntheticTurn(beforeMove, "SYNTHETIC CACHE BEFORE DEVICE RESTART", ct);
        var moveSource = await Command(new { id = "before-runtime-move", action = "inspect", sessionId = beforeMove.Id }, ct);
        string sourceId = moveSource.GetProperty("latestSource").GetProperty("sourceId").GetString()!;
        string oldRuntime = moveSource.GetProperty("latestSource").GetProperty("runtimeSessionId").GetString()!;
        string oldLogical = Assert.Single(moveSource.GetProperty("continuityReceipts").EnumerateArray(), row => row.GetProperty("sourceId").GetString() == sourceId && row.GetProperty("operation").GetString() == "enroll").GetProperty("logicalReference").GetString()!;
        await beforeMove.CloseAsync(ct);
        var moved = await Command(new { id = "restart-new-runtime", action = "restart", runtimePolicy = "renew-on-restore" }, ct);
        using var movedClient = Client(new Uri(moved.GetProperty("url").GetString()!)); var movedCache = new CacheContinuityClient(movedClient, true);
        var afterMove = await movedClient.ResumeSessionAsync(beforeMove.Id, cancellationToken: ct); Assert.Equal(beforeMove.Id, afterMove.Id);
        var moveReceipt = await Command(new { id = "after-runtime-move", action = "inspect", sessionId = afterMove.Id }, ct);
        Assert.NotEqual(oldRuntime, moveReceipt.GetProperty("latestSource").GetProperty("runtimeSessionId").GetString());
        var resumedReceipt = Assert.Single(moveReceipt.GetProperty("continuityReceipts").EnumerateArray(), row => row.GetProperty("operation").GetString() == "resume" && row.GetProperty("sourceId").GetString() == sourceId);
        Assert.Equal(oldLogical, resumedReceipt.GetProperty("logicalReference").GetString());
        Assert.Equal(1, moveReceipt.GetProperty("modelExchanges").GetInt32()); // Restore does not replay the first turn or its tools.
        var oldTicket = await movedCache.PrepareOpenAsync(afterMove.Id, CacheContinuityOpenKind.Resume, beforeMoveCache.Ticket, "net-pre-move-ticket", ct);
        Assert.Equal("mapping_unavailable", (await Assert.ThrowsAsync<CacheContinuityException>(() => movedCache.SubmitAsync(oldTicket, ct))).Code);
        await SyntheticTurn(afterMove, "SYNTHETIC CACHE AFTER DEVICE RESTART", ct); await afterMove.CloseAsync(ct);

        // A separate live source is deleted through the original trusted Store hook. Its previously valid ticket cannot revive it.
        var toDelete = await movedClient.CreateSessionAsync(new CreateSessionOptions(), ct);
        var deleteCache = await movedCache.SubmitAsync(await movedCache.PrepareOpenAsync(toDelete.Id, CacheContinuityOpenKind.New,
            requestId: "net-delete-original", cancellationToken: ct), ct);
        await toDelete.CloseAsync(ct);
        var deletion = await Command(new { id = "delete-original-source", action = "delete-history", user = "net-integration-user", sessionId = toDelete.Id }, ct);
        Assert.True(deletion.GetProperty("absent").GetBoolean()); Assert.True(deletion.GetProperty("deletedSources").GetInt32() >= 1);
        var deletedTicket = await movedCache.PrepareOpenAsync(toDelete.Id, CacheContinuityOpenKind.Resume, deleteCache.Ticket, "net-deleted-ticket", ct);
        Assert.Equal("mapping_unavailable", (await Assert.ThrowsAsync<CacheContinuityException>(() => movedCache.SubmitAsync(deletedTicket, ct))).Code);
        await Assert.ThrowsAsync<TansrHttpException>(() => movedClient.ResumeSessionAsync(toDelete.Id, cancellationToken: ct));
        var complete = await Command(new { id = "completed-cache-facts", action = "inspect" }, ct);
        Assert.Equal(2, complete.GetProperty("restarts").GetInt32()); Assert.Equal(2, complete.GetProperty("modelExchanges").GetInt32());

        // Natural ticket TTL is separate from operationEpoch or mapping lifetime. The real
        // Serve forwards one original operation to the authoritative synthetic peer, which
        // rejects its expired ticket without a C2 request, fallback or replacement identity.
        await Command(new { id = "arm-natural-ticket-expiry", action = "arm-ticket-expiry" }, ct);
        var expirySession = await movedClient.CreateSessionAsync(new CreateSessionOptions(), ct);
        var expiryOpen = await movedCache.PrepareOpenAsync(expirySession.Id, CacheContinuityOpenKind.New,
            requestId: "net-short-ticket-original", cancellationToken: ct);
        var expiryReceipt = await movedCache.SubmitAsync(expiryOpen, ct); Assert.NotNull(expiryReceipt.Ticket);
        var expiryAt = DateTimeOffset.Parse(expiryReceipt.TicketExpiresAt!, System.Globalization.CultureInfo.InvariantCulture);
        var renew = await movedCache.PrepareRenewAsync(expiryReceipt.Binding, expiryReceipt.Ticket!, "net-expired-ticket-renew", ct);
        byte[] renewOriginal = renew.ExportOriginalRequest(); string originalOwner = renew.OriginalPrincipal;
        var beforeExpiry = await Command(new { id = "short-ticket-issued", action = "inspect", sessionId = expirySession.Id }, ct);
        var issued = Assert.Single(beforeExpiry.GetProperty("shortTickets").EnumerateArray());
        Assert.Equal(expiryAt, DateTimeOffset.Parse(issued.GetProperty("expiresAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1500, (expiryAt - DateTimeOffset.Parse(issued.GetProperty("issuedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).TotalMilliseconds);
        var wait = expiryAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100); if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        var stillActive = await movedCache.ReadBindingAsync(expiryReceipt.Binding.Id, ct);
        Assert.Equal("active", stillActive.State); Assert.Equal(expiryReceipt.Binding.LogicalReference, stillActive.LogicalReference);
        Assert.Equal(renew.OperationEpoch, (await movedCache.DiscoverAsync(ct)).OperationEpoch);
        var expired = await Assert.ThrowsAsync<CacheContinuityException>(() => movedCache.SubmitAsync(renew, ct));
        Assert.Equal(410, expired.StatusCode); Assert.Equal("ticket_expired", expired.Code);
        Assert.Equal("none", expired.RetryAction); Assert.Equal("none", expired.Fallback);
        Assert.Null(renew.ExportOriginalReceipt()); Assert.Equal(renewOriginal, renew.ExportOriginalRequest()); Assert.Equal(originalOwner, renew.OriginalPrincipal);
        Assert.Equal("query_status_required", (await Assert.ThrowsAsync<TansrProtocolException>(() => movedCache.SubmitAsync(renew, ct))).Code);
        var afterExpiry = await Command(new { id = "short-ticket-rejected", action = "inspect", sessionId = expirySession.Id }, ct);
        Assert.Equal(beforeExpiry.GetProperty("latestSource").GetRawText(), afterExpiry.GetProperty("latestSource").GetRawText());
        Assert.Equal(beforeExpiry.GetProperty("intents").GetRawText(), afterExpiry.GetProperty("intents").GetRawText());
        Assert.Equal(2, afterExpiry.GetProperty("modelExchanges").GetInt32());
        var rejection = Assert.Single(afterExpiry.GetProperty("expiryRejections").EnumerateArray());
        Assert.Equal("active", rejection.GetProperty("mappingState").GetString());
        Assert.True(DateTimeOffset.Parse(rejection.GetProperty("observedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture) >= expiryAt);
        var unknownDiagnostics = await movedCache.ReadDiagnosticsAsync(expiryReceipt.Binding.Id, cancellationToken: ct);
        Assert.Empty(unknownDiagnostics.GetProperty("rows").EnumerateArray()); Assert.Equal(JsonValueKind.Null, unknownDiagnostics.GetProperty("next").ValueKind);
        await expirySession.CloseAsync(ct);
    }

    private static async Task SyntheticTurn(AgentSession session, string prompt, CancellationToken ct)
    {
        var result = await session.SendAndObserveAsync(prompt, new SessionRunOptions { Timeout = TimeSpan.FromSeconds(20) }, (_, _) => Task.CompletedTask, ct);
        Assert.False(result.WasAborted); Assert.Equal("turn.completed", result.TerminalEvent.Name);
    }

    private static TansrClient Client(Uri origin, HttpClient? http = null, bool other = false)
    {
        string user = other ? "net-integration-other" : "net-integration-user";
        return new TansrClient(new TansrClientOptions
        {
            BaseUri = origin,
            AllowInsecureLoopback = true,
            TokenProvider = _ => Task.FromResult(Required(other ? "TANSR_SERVE_TEST_OTHER_TOKEN" : "TANSR_SERVE_TEST_TOKEN")),
            PrincipalProvider = () => "synthetic-app/" + user,
            ExecutionScopeProvider = () => JsonSerializer.SerializeToElement(new { applicationScopeId = "synthetic-app", endUserId = user, authorizationRevision = "1" })
        }, http);
    }

    private static async Task<JsonElement> Command(object command, CancellationToken ct)
    {
        string directory = Path.Combine(Required("TANSR_SERVE_TEST_DIRECTORY"), "cache-continuity");
        var value = JsonSerializer.SerializeToElement(command); string id = value.GetProperty("id").GetString()!;
        string path = Path.Combine(directory, "host-commands", id + ".tmp");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(command), ct);
        File.Move(path, Path.Combine(directory, "host-commands", id + ".json"));
        for (; ; )
        {
            ct.ThrowIfCancellationRequested(); string failed = Path.Combine(directory, "host-failure.json");
            if (File.Exists(failed)) throw new InvalidOperationException("Synthetic cache fixture failed: " + await File.ReadAllTextAsync(failed, ct));
            string response = Path.Combine(directory, "host-responses", id + ".json");
            if (File.Exists(response))
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(response, ct));
                Assert.Equal(id, document.RootElement.GetProperty("id").GetString()); return document.RootElement.GetProperty("value").Clone();
            }
            await Task.Delay(20, ct);
        }
    }

    private sealed class LostOpenResponse : DelegatingHandler
    {
        internal int Posts;
        internal JsonElement? Receipt;
        internal LostOpenResponse() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v3/sdk2/cache/bindings" && ++Posts == 1)
            {
                Assert.True(response.IsSuccessStatusCode); using (response)
                { Receipt = WireJson.Parse(await response.Content.ReadAsByteArrayAsync(ct)); }
                throw new HttpRequestException("Synthetic loss after real Serve cache receipt.");
            }
            return response;
        }
    }
}
