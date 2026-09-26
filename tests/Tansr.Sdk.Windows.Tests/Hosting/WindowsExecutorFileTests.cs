using System.Security.AccessControl;
using System.Security.Principal;
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

    [Theory]
    [InlineData("")]
    [InlineData("parent")]
    [InlineData("parent/child")]
    public async Task RemoteMkdirEnsuresRootAndNestedDirectoriesIdempotently(string path)
    {
        using var mutable = MutableWorkspace();
        var backend = new WindowsExecutorBackend("executor", [new WindowsExecutorWorkspace("work", "1", mutable)]);
        var guarded = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await backend.ExecuteAsync(Mkdir(path), token => { token.ThrowIfCancellationRequested(); guarded++; return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal("fs.mkdir", result.GetProperty("operation").GetString());
            Assert.Empty(result.GetProperty("args").EnumerateObject());
            Assert.Equal(WindowsWorkspaceEntryKind.Directory, mutable.Inspect(path).Kind);
        }
        Assert.Equal(2, guarded);
        Assert.Throws<WindowsWorkspaceException>(() => mutable.CreateFileAtomic("", [1]));
        Assert.Throws<WindowsWorkspaceException>(() => mutable.CreateDirectory(""));
    }

    [Fact]
    public async Task RemoteMkdirDoesNotTreatAFileAsAnExistingDirectoryOrSwallowCancellation()
    {
        using var mutable = MutableWorkspace();
        var backend = new WindowsExecutorBackend("executor", [new WindowsExecutorWorkspace("work", "1", mutable)]);
        mutable.CreateFileAtomic("file", [1, 2, 3]);
        foreach (var path in new[] { "file", "file/child" })
            await Assert.ThrowsAsync<WindowsWorkspaceException>(() => backend.ExecuteAsync(Mkdir(path), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, mutable.Read("file"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ExecuteAsync(Mkdir("absent/child"), _ => Task.CompletedTask, canceled.Token));
        Assert.False(Directory.Exists(Path.Combine(mutable.RootDirectory, "absent")));
    }

    private WindowsWorkspace MutableWorkspace()
    {
        var path = Path.Combine(directory, "mutable"); Directory.CreateDirectory(path);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        using var user = WindowsIdentity.GetCurrent(); security.SetOwner(user.User!);
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
        return new WindowsWorkspace(path, new WindowsWorkspaceOptions { AllWritersCooperate = true });
    }

    private static JsonElement Mkdir(string path) => JsonSerializer.SerializeToElement(new
    {
        protocol = "sdk2-ext-v1",
        operationId = "mkdir",
        sessionId = "session",
        digest = new string('a', 64),
        scope = new { applicationScopeId = "app", endUserId = "user", authorizationRevision = "1" },
        binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "work", workspaceRevision = "1" } },
        toolName = "Write",
        request = new { operation = "fs.mkdir", args = new { path } },
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
    });

    public void Dispose()
    {
        workspace.Dispose();
        Directory.Delete(directory, true);
    }
}
