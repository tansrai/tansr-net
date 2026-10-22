using System.Text.Json;
using Tansr.Examples;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class NativeStorageModeTests
{
    [Theory]
    [InlineData("{}", "terminal")]
    [InlineData("{\"storageMode\":\"terminal\",\"journal\":{\"mode\":\"reopen\"}}", "terminal")]
    [InlineData("{\"storageMode\":\"cloud\"}", "cloud")]
    public void ExplicitCloudDoesNotRequireAnyLocalDatabaseConfiguration(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, NativeTerminalDeviceHost.ReadStorageMode(document.RootElement));
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("encryption")]
    [InlineData("publication")]
    public void CloudRejectsLocalStoreConfigurationInsteadOfCreatingFallback(string field)
    {
        using var document = JsonDocument.Parse("{\"storageMode\":\"cloud\",\"" + field + "\":{}}");
        var error = Assert.Throws<InvalidOperationException>(() => NativeTerminalDeviceHost.ReadStorageMode(document.RootElement));
        Assert.Equal("terminal_device_cloud_local_store_forbidden", error.Message);
    }
}
