using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Tests.Security;

public sealed class CurrentUserDpapiArchiveKeyProviderTests
{
    [Fact]
    public void NewFileReopensWithIndependentCopiesAndNoPlaintext()
    {
        using var directory = new SyntheticDirectory();
        var path = Path.Combine(directory.Path, "key.dpapi");
        var provider = CurrentUserDpapiArchiveKeyProvider.Create(path, "key-1");
        var first = provider.ReadKey();
        var second = provider.ReadKey();
        var reopened = CurrentUserDpapiArchiveKeyProvider.Open(path, "key-1").ReadKey();
        try
        {
            Assert.Equal(32, first.Length);
            Assert.NotSame(first, second);
            Assert.Equal(first, second);
            Assert.Equal(first, reopened);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(Convert.ToBase64String(first), text, StringComparison.Ordinal);
            var data = WireJson.DecodeControl(File.ReadAllBytes(path));
            Assert.Equal(CurrentUserDpapiArchiveKeyProvider.Format, data.GetProperty("format").GetString());
            Assert.Equal("key-1", data.GetProperty("keyId").GetString());
            Array.Clear(first);
            Assert.Contains(second, value => value != 0);
        }
        finally { Array.Clear(first); Array.Clear(second); Array.Clear(reopened); }
    }

    [Fact]
    public void ExistingOrDamagedFilesAreNeverReplaced()
    {
        using var directory = new SyntheticDirectory();
        var path = Path.Combine(directory.Path, "key.dpapi");
        var provider = CurrentUserDpapiArchiveKeyProvider.Create(path, "key-1");
        var original = File.ReadAllBytes(path);
        Assert.Equal("key_unavailable", Assert.Throws<StorageException>(() => CurrentUserDpapiArchiveKeyProvider.Create(path, "key-1")).Code);
        Assert.Equal(original, File.ReadAllBytes(path));
        var corrupt = Encoding.UTF8.GetBytes("synthetic corrupt key file");
        File.WriteAllBytes(path, corrupt);
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => CurrentUserDpapiArchiveKeyProvider.Open(path, "key-1")).Code);
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => provider.ReadKey()).Code);
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        File.Delete(path);
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => provider.ReadKey()).Code);
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => CurrentUserDpapiArchiveKeyProvider.Open(path, "key-1")).Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WrongIdTamperingAndSameIdReplacementFail()
    {
        using var directory = new SyntheticDirectory();
        var path = Path.Combine(directory.Path, "key.dpapi");
        var provider = CurrentUserDpapiArchiveKeyProvider.Create(path, "key-1");
        var original = File.ReadAllBytes(path);
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => CurrentUserDpapiArchiveKeyProvider.Open(path, "key-2")).Code);
        var parsed = WireJson.DecodeControl(original);
        var changedId = JsonSerializer.SerializeToElement(new
        {
            format = CurrentUserDpapiArchiveKeyProvider.Format,
            keyId = "key-2",
            protectedKey = parsed.GetProperty("protectedKey").GetString()
        });
        File.WriteAllBytes(path, WireJson.EncodeControl(changedId));
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => CurrentUserDpapiArchiveKeyProvider.Open(path, "key-2")).Code);
        File.WriteAllBytes(path, original);
        var protectedBytes = Convert.FromBase64String(parsed.GetProperty("protectedKey").GetString()!);
        protectedBytes[protectedBytes.Length / 2] ^= 1;
        var changedCiphertext = JsonSerializer.SerializeToElement(new
        {
            format = CurrentUserDpapiArchiveKeyProvider.Format,
            keyId = "key-1",
            protectedKey = Convert.ToBase64String(protectedBytes)
        });
        File.WriteAllBytes(path, WireJson.EncodeControl(changedCiphertext));
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => provider.ReadKey()).Code);
        var replacement = Path.Combine(directory.Path, "other.dpapi");
        CurrentUserDpapiArchiveKeyProvider.Create(replacement, "key-1");
        File.Copy(replacement, path, true);
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => provider.ReadKey()).Code);
    }

    [Fact]
    public void RejectsRelativeAdsAndUnboundedKeyFiles()
    {
        Assert.Equal("invalid_input", Assert.Throws<StorageException>(() => CurrentUserDpapiArchiveKeyProvider.Create("key.dpapi", "key-1")).Code);
        Assert.Equal("invalid_input", Assert.Throws<StorageException>(() => CurrentUserDpapiArchiveKeyProvider.Create("C:\\keys\\key.dpapi:extra", "key-1")).Code);
        using var directory = new SyntheticDirectory();
        var path = Path.Combine(directory.Path, "oversized.dpapi");
        File.WriteAllBytes(path, new byte[16385]);
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => CurrentUserDpapiArchiveKeyProvider.Open(path, "key-1")).Code);
        Assert.Equal(16385, new FileInfo(path).Length);
    }

    private sealed class SyntheticDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr-net-security-" + Guid.NewGuid().ToString("N"));
        public SyntheticDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
