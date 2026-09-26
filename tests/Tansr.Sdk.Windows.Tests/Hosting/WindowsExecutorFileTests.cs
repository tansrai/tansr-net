using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class WindowsExecutorFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tansr-file-inspection-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsWorkspace workspace;

    public WindowsExecutorFileTests()
    {
        Directory.CreateDirectory(directory);
        workspace = new WindowsWorkspace(directory);
    }

    [Theory]
    [InlineData("missing.txt")]
    [InlineData("missing-parent/missing.txt")]
    public async Task MissingTargetInspectionReportsOriginalEnoentWithoutCreatingAnyFile(string path)
    {
        var backend = new WindowsExecutorBackend("executor", [new WindowsExecutorWorkspace("work", "1", workspace)]);
        var guarded = 0;
        var operation = JsonSerializer.SerializeToElement(new
        {
            protocol = "sdk2-ext-v1",
            operationId = "inspect-missing",
            sessionId = "session",
            digest = new string('a', 64),
            scope = new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" },
            binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "work", workspaceRevision = "1" } },
            toolName = "Write",
            request = new { operation = "fs.inspect", args = new { path, followLinks = false } },
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
        });
        var failure = await Assert.ThrowsAsync<ExecutionRejectedException>(() => backend.ExecuteAsync(operation,
            token => { token.ThrowIfCancellationRequested(); guarded++; return Task.CompletedTask; }, CancellationToken.None));

        Assert.Equal("ENOENT", failure.Code);
        Assert.Equal(1, guarded);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    public void Dispose()
    {
        workspace.Dispose();
        Directory.Delete(directory, true);
    }
}
