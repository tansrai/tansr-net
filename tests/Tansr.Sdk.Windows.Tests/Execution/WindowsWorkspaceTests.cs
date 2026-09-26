using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Tansr.Sdk.Windows.Execution;

namespace Tansr.Sdk.Windows.Tests.Execution;

public sealed class WindowsWorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tansr-workspace-test-" + Guid.NewGuid().ToString("N"));

    public WindowsWorkspaceTests()
    {
        Directory.CreateDirectory(_directory);
        // 私有测试域只属于当前用户、SYSTEM与管理员；不修改任何已有目录。
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent(); security.SetOwner(identity.User!);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(security);
    }

    [Fact]
    public void ActualFilesReadWriteEditListAndSearch()
    {
        using var workspace = new WindowsWorkspace(_directory, new WindowsWorkspaceOptions { AllWritersCooperate = true });
        workspace.CreateDirectory("中文目录");
        byte[] original = Encoding.UTF8.GetBytes("你好 first\nsecond line\n");
        var created = workspace.CreateFileAtomic("中文目录/sample.txt", original);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), created.Hash);
        Assert.Equal(original, workspace.Read("中文目录/sample.txt"));
        Assert.Equal(original.LongLength, workspace.Inspect("中文目录/sample.txt").Length);
        Assert.Equal(WindowsWorkspaceEntryKind.Directory, workspace.Inspect("中文目录").Kind);
        Assert.Equal(new[] { "中文目录" }, workspace.List().Select(value => value.RelativePath));
        Assert.Equal(new[] { "中文目录/sample.txt" }, workspace.Glob("**/*.txt"));
        var matches = workspace.Grep("second");
        Assert.False(matches.Truncated);
        Assert.Equal(2, Assert.Single(matches.Matches).Line);
        var edited = workspace.EditText("中文目录/sample.txt", "first", "updated", created.Hash);
        Assert.Equal("你好 updated\nsecond line\n", Encoding.UTF8.GetString(workspace.Read("中文目录/sample.txt")));
        Assert.NotEqual(created.Hash, edited.Hash);
        Assert.Equal("write_conflict", Assert.Throws<WindowsWorkspaceException>(() => workspace.CompareExchange("中文目录/sample.txt", created.Hash, original)).Code);
        Assert.Empty(Directory.GetFiles(_directory, ".tansr-sdk-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SharedDirectoryAtomicWriteDoesNotClaimUncoordinatedCas()
    {
        using var workspace = new WindowsWorkspace(_directory);
        Assert.True(workspace.SupportsAtomicReplacement);
        Assert.False(workspace.SupportsCooperativeCompareExchange);
        Assert.False(workspace.SupportsUncoordinatedCompareExchange);
        var result = workspace.WriteAtomic("normal.txt", Encoding.UTF8.GetBytes("original"));
        Assert.Equal("compare_exchange_unavailable", Assert.Throws<WindowsWorkspaceException>(() => workspace.CompareExchange("normal.txt", result.Hash, Encoding.UTF8.GetBytes("other"))).Code);
        workspace.WriteAtomic("normal.txt", Encoding.UTF8.GetBytes("replace"));
        Assert.Equal("replace", File.ReadAllText(Path.Combine(_directory, "normal.txt")));
        Assert.Equal("already_exists", Assert.Throws<WindowsWorkspaceException>(() => workspace.CreateFileAtomic("normal.txt", Array.Empty<byte>())).Code);
    }

    [Fact]
    public void InspectDoesNotEnumerateTheParentDirectory()
    {
        File.WriteAllText(Path.Combine(_directory, "first.txt"), "one");
        File.WriteAllText(Path.Combine(_directory, "second.txt"), "two");
        using var workspace = new WindowsWorkspace(_directory, new WindowsWorkspaceOptions { MaximumEntries = 1 });
        Assert.Equal(3, workspace.Inspect("second.txt").Length);
        Assert.Equal("entry_limit", Assert.Throws<WindowsWorkspaceException>(() => workspace.List()).Code);
    }

    [Fact]
    public void ExistingForeignFileHandlePreventsWriteAndDoesNotLeaveTemp()
    {
        File.WriteAllText(Path.Combine(_directory, "busy.txt"), "unchanged");
        using var workspace = new WindowsWorkspace(_directory);
        using var held = new FileStream(Path.Combine(_directory, "busy.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal("sharing_conflict", Assert.Throws<WindowsWorkspaceException>(() => workspace.WriteAtomic("busy.txt", Encoding.UTF8.GetBytes("replacement"))).Code);
        Assert.Empty(Directory.GetFiles(_directory, ".tansr-sdk-*.tmp"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("dir/../outside.txt")]
    [InlineData("/rooted")]
    [InlineData("C:/rooted")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("\\\\?\\C:\\device")]
    [InlineData("data.txt:hidden")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("dir\\file")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData(".tansr-sdk-writer.lock")]
    public void UnsafePathsRejectedBeforeIo(string path)
    {
        using var workspace = new WindowsWorkspace(_directory);
        Assert.Equal("unsafe_path", Assert.Throws<WindowsWorkspaceException>(() => workspace.WriteAtomic(path, Encoding.UTF8.GetBytes("not written"))).Code);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ParentRenameIsDeniedWhileWorkspaceHoldsIdentity()
    {
        string nested = Path.Combine(_directory, "parent", "workspace"); Directory.CreateDirectory(nested);
        using (var workspace = new WindowsWorkspace(nested))
        {
            Assert.Throws<IOException>(() => Directory.Move(Path.Combine(_directory, "parent"), Path.Combine(_directory, "changed")));
            workspace.WriteAtomic("stable.txt", Encoding.UTF8.GetBytes("still in original root"));
            Assert.True(File.Exists(Path.Combine(nested, "stable.txt")));
        }
        Directory.Move(Path.Combine(_directory, "parent"), Path.Combine(_directory, "changed"));
    }

    [Fact]
    public void JunctionTargetIsNotReadWrittenOrSearched()
    {
        string outside = Path.Combine(_directory, "outside"), root = Path.Combine(_directory, "root");
        Directory.CreateDirectory(outside); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "synthetic-outside-secret");
        string junction = Path.Combine(root, "linked");
        using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c mklink /j \"" + junction + "\" \"" + outside + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        { process.WaitForExit(); Assert.Equal(0, process.ExitCode); }
        try
        {
            using var workspace = new WindowsWorkspace(root);
            Assert.Equal(WindowsWorkspaceEntryKind.ReparsePoint, Assert.Single(workspace.List()).Kind);
            Assert.Equal(WindowsWorkspaceEntryKind.ReparsePoint, workspace.Inspect("linked").Kind);
            Assert.Throws<WindowsWorkspaceException>(() => workspace.Read("linked/secret.txt"));
            Assert.Throws<WindowsWorkspaceException>(() => workspace.WriteAtomic("linked/new.txt", Encoding.UTF8.GetBytes("never")));
            Assert.Empty(workspace.Glob("**/*"));
            Assert.Empty(workspace.Grep("synthetic").Matches);
            Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
            Assert.Equal("synthetic-outside-secret", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void HardLinksCannotSmuggleExternalFileData()
    {
        string source = Path.Combine(_directory, "outside.txt"), root = Path.Combine(_directory, "root"); Directory.CreateDirectory(root);
        File.WriteAllText(source, "synthetic-secret");
        Assert.True(CreateHardLinkW(Path.Combine(root, "linked.txt"), source, IntPtr.Zero));
        using var workspace = new WindowsWorkspace(root);
        Assert.Equal("hard_link_rejected", Assert.Throws<WindowsWorkspaceException>(() => workspace.Read("linked.txt")).Code);
        Assert.Equal("hard_link_rejected", Assert.Throws<WindowsWorkspaceException>(() => workspace.WriteAtomic("linked.txt", Encoding.UTF8.GetBytes("bad"))).Code);
        Assert.Equal("synthetic-secret", File.ReadAllText(source));
    }

    [Fact]
    public void CooperativeWriterLockRejectsSecondOwnerAndReleasesOnDispose()
    {
        var options = new WindowsWorkspaceOptions { AllWritersCooperate = true };
        using (var first = new WindowsWorkspace(_directory, options))
        { Assert.Equal("sharing_conflict", Assert.Throws<WindowsWorkspaceException>(() => new WindowsWorkspace(_directory, options)).Code); }
        using var reopened = new WindowsWorkspace(_directory, options);
        reopened.CompareExchange("fresh.txt", null, Encoding.UTF8.GetBytes("created"));
        Assert.Equal("created", Encoding.UTF8.GetString(reopened.Read("fresh.txt")));
    }

    [Fact]
    public void CooperativeWriterModeRequiresPrivatePermissions()
    {
        var directory = new DirectoryInfo(_directory); var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Assert.Equal("private_permissions_required", Assert.Throws<WindowsWorkspaceException>(() => new WindowsWorkspace(_directory, new WindowsWorkspaceOptions { AllWritersCooperate = true })).Code);
        using var normal = new WindowsWorkspace(_directory);
        normal.WriteAtomic("normal.txt", Encoding.UTF8.GetBytes("ordinary write remains available"));
    }

    [Fact]
    public void OutputLimitsAndCancellationLeaveOriginalUnchanged()
    {
        using var workspace = new WindowsWorkspace(_directory, new WindowsWorkspaceOptions { MaximumWriteBytes = 4 });
        workspace.WriteAtomic("original.txt", Encoding.UTF8.GetBytes("old"));
        Assert.Equal("write_limit", Assert.Throws<WindowsWorkspaceException>(() => workspace.WriteAtomic("original.txt", new byte[5])).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => workspace.WriteAtomic("original.txt", Encoding.UTF8.GetBytes("new"), true, cancellation.Token));
        Assert.Equal("old", Encoding.UTF8.GetString(workspace.Read("original.txt")));
    }

    [Fact]
    public void ProcessDirectoryLeasePinsApprovedDirectoryUntilDisposed()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "cwd"));
        using var workspace = new WindowsWorkspace(_directory);
        using (var lease = workspace.AcquireProcessDirectory("cwd"))
        {
            lease.ValidateForExecution();
            Assert.Equal(Path.Combine(_directory, "cwd"), lease.DirectoryPath, StringComparer.OrdinalIgnoreCase);
            Assert.Throws<IOException>(() => Directory.Move(Path.Combine(_directory, "cwd"), Path.Combine(_directory, "other")));
        }
        Directory.Move(Path.Combine(_directory, "cwd"), Path.Combine(_directory, "other"));
    }

    [Fact]
    public void ProcessDirectoryLeaseKeepsItsOwnNonemptyGuardWithoutBlockingAtomicSaves()
    {
        var cwd = Path.Combine(_directory, "cwd"); var sibling = Path.Combine(_directory, "sibling");
        Directory.CreateDirectory(cwd); Directory.CreateDirectory(sibling);
        using var workspace = new WindowsWorkspace(_directory);
        string guard;
        using (var lease = workspace.AcquireProcessDirectory("cwd"))
        {
            guard = Assert.Single(Directory.GetFiles(cwd, ".tansr-sdk-process-*.lock"));
            Assert.Throws<IOException>(() => File.Delete(guard));
            Assert.Throws<IOException>(() => Directory.Move(cwd, cwd + "-replaced"));
            foreach (var location in new[] { _directory, sibling, cwd })
            {
                var original = Path.Combine(location, "original.txt"); var replacement = Path.Combine(location, "replacement.tmp");
                File.WriteAllText(replacement, "first"); File.Move(replacement, original);
                File.WriteAllText(replacement, "second"); File.Replace(replacement, original, null);
                Assert.Equal("second", File.ReadAllText(original));
            }
            lease.ValidateForExecution();
        }
        Assert.False(File.Exists(guard));
        Assert.Empty(Directory.GetFiles(cwd, ".tansr-sdk-process-*.lock"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
