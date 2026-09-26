using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.IntegrationTests;

public sealed partial class ServePublicHostIntegrationTests
{
    private static async Task VerifyApplicationPromptAsync(AgentSession session, CancellationToken ct)
    {
        // Default metadata retains its original body. Source observation is an explicit GET,
        // never a synthetic business turn or a client-supplied prompt configuration.
        var original = await session.GetMetadataAsync(ct);
        Assert.False(original.TryGetProperty("applicationPrompt", out _));
        var observed = await session.ReadApplicationPromptAsync(ct);
        Assert.True(observed.IsKnown);
        Assert.Equal(ApplicationPromptPolicy.Prepend, observed.Policy);
        Assert.Equal(ApplicationPromptSource.PlatformAndSdk, observed.Source);
        Assert.NotNull(observed.Raw);
        var raw = observed.Raw!.Value;
        Assert.Equal(JsonValueKind.Object, raw.ValueKind);
        Assert.Equal(new[] { "policy", "source" }, raw.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("{\"policy\":\"prepend\",\"source\":\"platform+sdk\"}", WireJson.CanonicalString(raw));
        var after = await session.GetMetadataAsync(ct);
        Assert.False(after.TryGetProperty("applicationPrompt", out _));
        Assert.DoesNotContain("NET_P05_SYNTHETIC_", original.GetRawText());
        Assert.DoesNotContain("NET_P05_SYNTHETIC_", after.GetRawText());
    }
}
