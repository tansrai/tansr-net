using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Examples;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class NativeMemoryDeviceConfigurationTests
{
    private static JsonObject Configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), "tansr-device-config-fixture");
        return new JsonObject
        {
            ["format"] = "tansr-example-device-memory-v1",
            ["enablePreview"] = true,
            ["serveUrl"] = "https://serve.example",
            ["sessionId"] = "trusted-session",
            ["executorId"] = "trusted-device",
            ["trustedScopeFile"] = Path.Combine(root, "scope.json"),
            ["controllerTokenEnvironment"] = "FIXTURE_CONTROLLER",
            ["deviceTokenEnvironment"] = "FIXTURE_DEVICE",
            ["encryption"] = new JsonObject { ["provider"] = "dpapi-current-user", ["path"] = Path.Combine(root, "memory.key"), ["keyId"] = "memory-key", ["mode"] = "reopen" },
            ["workspace"] = new JsonObject { ["path"] = Path.Combine(root, "workspace"), ["id"] = "workspace", ["revision"] = "1" },
            ["journal"] = new JsonObject { ["path"] = Path.Combine(root, "execution.sqlite"), ["mode"] = "create", ["maxOperations"] = 4096, ["maxStoredBytes"] = 67108864, ["maxPages"] = 32768 },
            ["publication"] = new JsonObject
            {
                ["path"] = Path.Combine(root, "publication.sqlite"),
                ["mode"] = "reopen",
                ["maxTransfers"] = 32,
                ["maxStagingBytes"] = 8388608,
                ["maxPages"] = 8192,
                ["identity"] = new JsonObject { ["scope"] = new JsonObject { ["applicationScopeId"] = "app", ["endUserId"] = "user" }, ["sourceId"] = "trusted-source", ["sourceGeneration"] = "9007199254740993", ["domainKey"] = new string('a', 64) },
            },
        };
    }
    private static NativeMemoryDeviceConfiguration Parse(JsonObject value)
    { using var document = JsonDocument.Parse(value.ToJsonString()); return NativeMemoryDeviceConfiguration.Parse(document.RootElement); }

    [Fact]
    public void TrustedSourceAndExplicitStorageModesAreKeptWithoutDerivation()
    {
        var input = Configuration(); var parsed = Parse(input);
        input["publication"]!["identity"]!["sourceId"] = "changed-after-parse";
        Assert.Equal("trusted-source", parsed.PublicationIdentity.GetProperty("sourceId").GetString());
        Assert.Equal("9007199254740993", parsed.PublicationIdentity.GetProperty("sourceGeneration").GetString());
        Assert.Equal("create", parsed.JournalMode); Assert.Equal("reopen", parsed.PublicationMode);
        Assert.Equal("FIXTURE_CONTROLLER", parsed.ControllerTokenEnvironment); Assert.Equal("FIXTURE_DEVICE", parsed.DeviceTokenEnvironment);
        Assert.Equal(4096, parsed.JournalMaxOperations); Assert.Equal(32, parsed.MaxTransfers);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("mode")]
    [InlineData("credential-url")]
    [InlineData("query-url")]
    [InlineData("same-path")]
    [InlineData("relative-path")]
    [InlineData("secret-instead-of-environment-name")]
    [InlineData("zero-capacity")]
    [InlineData("plaintext-provider")]
    [InlineData("key-path-alias")]
    [InlineData("auto-key-mode")]
    [InlineData("key-in-workspace")]
    public void AmbiguousOrUnsafeHostConfigurationFailsBeforeOpeningStorage(string scenario)
    {
        var input = Configuration();
        switch (scenario)
        {
            case "key-in-workspace": input["encryption"]!["path"] = Path.Combine(input["workspace"]!["path"]!.GetValue<string>(), "key.json"); break;
            case "plaintext-provider": input["encryption"]!["provider"] = "none"; break;
            case "key-path-alias": input["encryption"]!["path"] = input["journal"]!["path"]!.DeepClone(); break;
            case "auto-key-mode": input["encryption"]!["mode"] = "auto"; break;
            case "preview": input["enablePreview"] = false; break;
            case "mode": input["publication"]!["mode"] = "auto"; break;
            case "credential-url": input["serveUrl"] = "https://user:secret@serve.example"; break;
            case "query-url": input["serveUrl"] = "https://serve.example/?token=secret"; break;
            case "same-path": input["publication"]!["path"] = input["journal"]!["path"]!.DeepClone(); break;
            case "relative-path": input["publication"]!["path"] = "publication.sqlite"; break;
            case "secret-instead-of-environment-name": input["deviceTokenEnvironment"] = "Bearer this-is-a-token"; break;
            case "zero-capacity": input["publication"]!["maxTransfers"] = 0; break;
        }
        Assert.Throws<InvalidOperationException>(() => Parse(input));
    }
}
