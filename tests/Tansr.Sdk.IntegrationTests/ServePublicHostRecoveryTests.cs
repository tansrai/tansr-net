using System.Text;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.IntegrationTests;

public sealed partial class ServePublicHostIntegrationTests
{
    private static TansrClientOptions Options(Uri origin, bool foreign = false) => new()
    {
        BaseUri = origin,
        AllowInsecureLoopback = true,
        TokenProvider = _ => Task.FromResult(Required(foreign ? "TANSR_SERVE_TEST_OTHER_TOKEN" : "TANSR_SERVE_TEST_TOKEN")),
        PrincipalProvider = () => foreign ? "net-integration-app/net-integration-other" : "net-integration-app/net-integration-user",
        ExecutionScopeProvider = () => foreign ? Json(new { applicationScopeId = "net-integration-app", endUserId = "net-integration-other", authorizationRevision = "1" }) : Scope,
        RequestTimeout = TimeSpan.FromSeconds(10),
        StreamIdleTimeout = TimeSpan.FromSeconds(15),
        MaxReconnectAttempts = 0
    };

    private static async Task VerifyTerminalControlAsync(TansrClient client, string sessionId, CancellationToken ct)
    {
        var control = new TerminalSessionControl(client, enablePreview: true);
        Assert.Equal("2026-09-26.candidate-7", TerminalSessionControl.SchemaRevision);
        var before = await control.ReadConfigurationAsync(sessionId, ct);
        var change = control.CreateConfigurationOperation(sessionId, "net-configuration", before.GetProperty("configuration").GetProperty("revision").GetInt64(), Json(new { thinking = new { budget = 64 } }));
        var changed = await control.ApplyConfigurationAsync(change, ct);
        Assert.Equal("changed", changed.GetProperty("status").GetString());
        Assert.Equal(64, changed.GetProperty("configuration").GetProperty("thinking").GetProperty("budget").GetInt32());
        Assert.Equal("replayed", (await control.ReplayConfigurationAsync(change, ct)).GetProperty("status").GetString());
        var memory = await control.ReadMemoryAsync(sessionId, ct);
        Assert.True(memory.GetProperty("memory").GetProperty("available").GetBoolean());
        var operation = control.CreateMemoryOperation(sessionId, memory, "net/request/中文", "net/operation/中文", Json(new { kind = "pin", text = "Keep the original integration note." }));
        var committed = await control.SubmitMemoryAsync(operation, ct);
        var receipt = committed.GetProperty("receipt");
        Assert.Equal("committed", receipt.GetProperty("status").GetString()); Assert.True(receipt.GetProperty("durable").GetBoolean());
        Assert.Equal(WireJson.CanonicalString(committed), WireJson.CanonicalString(await control.QueryMemoryAsync(operation, ct)));
        Assert.Equal(receipt.GetProperty("publishedRevision").GetString(), (await control.ReadMemoryAsync(sessionId, ct)).GetProperty("memory").GetProperty("revision").GetString());
    }

    private static async Task VerifyForeignAckQueryRejectedAsync(Uri origin, JsonElement acknowledgement, CancellationToken ct)
    {
        using var other = new TansrClient(Options(origin, true)); var archive = new ArchiveClient(other);
        var error = await Assert.ThrowsAsync<TansrHttpException>(() => archive.GetOperationAsync(Json(new
        {
            protocol = "sdk2-ext-v1",
            bindingId = acknowledgement.GetProperty("bindingId").GetString(),
            operation = "archive-ack",
            request = acknowledgement.GetProperty("request")
        }), ct));
        Assert.Equal(403, error.StatusCode);
    }

    private static async Task VerifyRebaseRecoveryAsync(Uri origin, string directory, JsonElement platform, CancellationToken ct)
    {
        using var lost = new LostResponseHandler("/archive/ack-rebases"); using var http = new HttpClient(lost);
        using var client = new TansrClient(Options(origin), http); var archive = new ArchiveClient(client);
        var session = await client.CreateSessionAsync(new CreateSessionOptions { Tools = [] }, ct);
        await new ExecutionClient(client).InitializeAsync(Json(new { protocol = "sdk2-ext-v1", sessionId = session.Id, platform, requestedTools = Array.Empty<string>() }), ct);
        await HostCommandAsync(new { id = "arm-recovery", action = "arm-recovery-store", sessionId = session.Id }, ct);
        await session.SendAsync("Produce recovery original answer once.", ct);
        await HostCommandAsync(new { id = "entered-recovery", action = "await-recovery-store", sessionId = session.Id }, ct);
        await archive.GetCapabilitiesAsync(ct);
        var target = await archive.GetBindingTargetAsync(Json(new { protocol = "sdk2-ext-v1", sessionId = session.Id }), ct);
        var bindingId = target.GetProperty("bindingId").GetString()!;
        var binding = await archive.GetBindingAsync(bindingId, ct); var status = await archive.GetArchiveStatusAsync(bindingId, ct);
        var generations = status.GetProperty("generations");
        var page = await archive.ReadRecordsAsync(Json(new { protocol = "sdk2-ext-v1", bindingId, generations, afterSequence = (string?)null, limit = 128, maxBytes = 1048576 }), ct);
        var record = Assert.Single(page.GetProperty("records").EnumerateArray());
        var references = new[] { record.GetProperty("payload") }.Concat(record.GetProperty("attachments").EnumerateArray()).ToArray();
        var artifacts = new List<ArchiveArtifact>();
        foreach (var reference in references)
        {
            var total = reference.GetProperty("bytes").GetInt32(); using var body = new MemoryStream();
            while (body.Length < total)
            {
                var chunk = await archive.ReadArtifactAsync(Json(new
                {
                    protocol = "sdk2-ext-v1",
                    bindingId,
                    generations,
                    artifactId = reference.GetProperty("artifactId").GetString(),
                    offset = (int)body.Length,
                    maxBytes = Math.Min(262144, total - (int)body.Length)
                }), ct);
                var bytes = WireJson.DecodeBase64(chunk.GetProperty("base64").GetString()!); Assert.NotEmpty(bytes); body.Write(bytes);
            }
            artifacts.Add(new ArchiveArtifact(reference.GetProperty("artifactId").GetString()!, body.ToArray()));
        }
        var identity = Json(new
        {
            scope = new { applicationScopeId = "net-integration-app", endUserId = "net-integration-user" },
            bindingId,
            sourceId = status.GetProperty("sourceId").GetString(),
            sourceGeneration = status.GetProperty("sourceGeneration").GetString(),
            target = new { sessionId = session.Id, generations }
        });
        // The new recovery file family is explicit. The earlier encrypted archive is neither migrated
        // nor downgraded; this separate synthetic-only source database follows the Node recovery DDL.
        var settings = new SqliteArchiveStoreOptions
        {
            Path = Path.Combine(directory, "recovery-source.sqlite"),
            Mode = StorageOpenMode.Create,
            Identity = identity,
            Replica = Json(new { replicationId = "net-recovery", role = "primary" }),
            ReadContext = () => Scope,
            ReadRetentionRevision = () => "0",
            AuthorizeRetention = _ => throw new InvalidOperationException("No deletion authorized.")
        };
        JsonElement original, intent;
        using (var store = await SqliteArchiveStore.OpenRecoverableAsync(settings, ct))
        {
            original = await store.ReceiveAsync(new ArchiveReceiveInput
            {
                Binding = binding,
                Status = status,
                Page = page,
                Artifacts = artifacts,
                Request = Json(new { operationEpoch = binding.GetProperty("operationEpoch").GetProperty("id").GetString(), requestId = "net-stale-original-ack" })
            }, ct);
            await HostCommandAsync(new { id = "release-recovery", action = "release-recovery-store", sessionId = session.Id }, ct);
            // Observe the exact revision transition that creates this regression. This is a bounded
            // deterministic race fixture, not a delay before normal product receive or an idle gate.
            JsonElement current;
            do { current = await archive.GetBindingAsync(bindingId, ct); if (current.GetProperty("revision").GetString() == original.GetProperty("expectedRevision").GetString()) await Task.Yield(); }
            while (current.GetProperty("revision").GetString() == original.GetProperty("expectedRevision").GetString());
            var stale = await Assert.ThrowsAsync<TansrHttpException>(() => archive.AcknowledgeAsync(original, ct));
            Assert.Equal(409, stale.StatusCode); Assert.Equal("binding_conflict", stale.Code);
            var request = Json(new { operationEpoch = original.GetProperty("request").GetProperty("operationEpoch").GetString(), requestId = "net-fixed-recovery-request" });
            using (var foreign = new TansrClient(Options(origin, true)))
            {
                var other = new ArchiveClient(foreign);
                var denied = await Assert.ThrowsAsync<TansrHttpException>(() => other.RebaseAckAsync(Json(new { protocol = "sdk2-ext-v1", bindingId, previous = original, request }), ct));
                Assert.Equal(403, denied.StatusCode);
            }
            var recovery = new ArchiveRecoverySession(archive, store, identity, () => Scope);
            var error = await Assert.ThrowsAnyAsync<TansrException>(() => recovery.RecoverAsync(request, ct));
            Assert.Equal("network_error", error.Code); Assert.Equal(1, lost.LostResponseCount);
            intent = (await store.PendingAckRebaseAsync(ct))!.Value;
            Assert.Equal(WireJson.CanonicalString(lost.LostRequest!.Value, 263168), WireJson.CanonicalString(intent, 263168));
            Assert.Equal(WireJson.CanonicalString(original), WireJson.CanonicalString((await store.PendingAsync(ct))!.Value));
        }
        settings.Mode = StorageOpenMode.Reopen;
        using (var reopened = await SqliteArchiveStore.OpenRecoverableAsync(settings, ct))
        {
            using var resumedClient = new TansrClient(Options(origin), http); var resumedArchive = new ArchiveClient(resumedClient); await resumedArchive.GetCapabilitiesAsync(ct);
            Assert.Equal(WireJson.CanonicalString(intent, 263168), WireJson.CanonicalString((await reopened.PendingAckRebaseAsync(ct))!.Value, 263168));
            var revision = (await resumedArchive.GetBindingAsync(bindingId, ct)).GetProperty("revision").GetString();
            var resumed = await new ArchiveRecoverySession(resumedArchive, reopened, identity, () => Scope).ResumeAsync(ct);
            Assert.NotNull(resumed); Assert.Equal(ArchiveAcknowledgementRecoveryOutcome.Rebased, resumed.Outcome);
            Assert.Equal(WireJson.CanonicalString(lost.LostReceipt!.Value, 528384), WireJson.CanonicalString(resumed.RecoveryReceipt!.Value, 528384));
            Assert.Equal(revision, (await resumedArchive.GetBindingAsync(bindingId, ct)).GetProperty("revision").GetString());
            Assert.Null(await reopened.PendingAsync(ct)); Assert.Null(await reopened.PendingAckRebaseAsync(ct));
            Assert.Equal("1", (await reopened.HeadAsync(ct))!.Value.GetProperty("sequence").GetString());
            Assert.Contains("NET_RECOVERY_ORIGINAL", Encoding.UTF8.GetString(await reopened.BodyAsync(record.GetProperty("payload"), ct)));
            var posts = lost.Requests.Where(request => request.Method == "POST" && request.Path.EndsWith("/archive/ack-rebases", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, posts.Length);
            Assert.All(posts, request => Assert.Equal(WireJson.CanonicalString(intent, 263168), WireJson.CanonicalString(request.Body!.Value, 263168)));
        }
        await session.CloseAsync(ct);
    }
}
