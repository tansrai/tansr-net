using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.IntegrationTests;

public sealed partial class ServeSessionApiTests
{
    [Fact]
    [Trait("Category", "ServeSourceIntegration")]
    public async Task ProfileUsesOriginalAuthorizedBundleAndOwnPlatformUsageWithoutSessionExecutionOrFinance()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = deadline.Token;
        using var client = new TansrClient(Options()); using var profile = new TerminalProfileClient(client, true);
        var session = await client.CreateSessionAsync(new() { Tools = [] }, ct); Exception? primary = null;
        try
        {
            var before = await session.ReadMetadataAsync(ct);
            var catalog = await profile.ReadCatalogAsync(session.Id, cancellationToken: ct);
            var model = Assert.Single(catalog.Models); Assert.Equal("fake-main", model.Handle); Assert.Equal("API synthetic", model.DisplayName);
            Assert.Equal("fake-main", catalog.Aliases["main"]); Assert.True(catalog.Capabilities.GetProperty("platform").GetProperty("textToSpeech").GetBoolean());
            Assert.False(catalog.Capabilities.GetProperty("tools").GetProperty("shell").GetBoolean());
            Assert.Equal("not_exposed", catalog.Quota.State); Assert.Equal("gateway", catalog.Quota.Enforcement);
            Assert.False(catalog.Raw.TryGetProperty("pricing", out _)); Assert.False(catalog.Raw.TryGetProperty("plan", out _));
            var usage = await profile.ReadUsageAsync(session.Id, cancellationToken: ct);
            Assert.Equal("net-integration-user", usage.EndUserId); Assert.Equal("1d", usage.Window); Assert.Equal(7, usage.Requests);
            Assert.Equal(1234, usage.InTokens); Assert.Equal(56, usage.OutTokens); Assert.Equal(78, usage.CacheRTokens); Assert.Equal(9, usage.CacheWTokens);
            Assert.Equal(before.LastSequence, (await session.ReadMetadataAsync(ct)).LastSequence);
            Assert.Empty((await session.GetHistoryAsync(ct)).GetProperty("messages").EnumerateArray());
            using var otherClient = new TansrClient(Options(true)); using var other = new TerminalProfileClient(otherClient, true);
            Assert.Equal("not_found", (await Assert.ThrowsAsync<TansrHttpException>(() => other.ReadCatalogAsync(session.Id, cancellationToken: ct))).Code);
            Assert.Equal("not_found", (await Assert.ThrowsAsync<TansrHttpException>(() => other.ReadUsageAsync(session.Id, cancellationToken: ct))).Code);
            // A closed retained session remains a read-only authority anchor; the route must not
            // resume it, mint an execution lease or substitute its zero session usage for 1d usage.
            await session.CloseAsync(ct);
            var closed = await profile.ReadUsageAsync(session.Id, cancellationToken: ct); Assert.Equal(7, closed.Requests);
            Assert.Equal(SessionStatus.Ended, (await session.ReadMetadataAsync(ct)).Status);
            var evidence = await CommandAsync(new { action = "inspect" }, ct);
            Assert.Contains(evidence.GetProperty("profiles").EnumerateArray(), item => item.GetProperty("endUserId").GetString() == "net-integration-user");
            Assert.DoesNotContain(evidence.GetProperty("profiles").EnumerateArray(), item => item.GetProperty("endUserId").GetString() == "net-integration-other");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await CleanupAsync([session], primary); }
    }
}
