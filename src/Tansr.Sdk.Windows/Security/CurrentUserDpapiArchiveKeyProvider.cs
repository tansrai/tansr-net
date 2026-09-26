using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Security;

/// <summary>独立文件中的 Windows CurrentUser DPAPI 密钥；不支持明文回退或坏卷换钥。</summary>
public sealed class CurrentUserDpapiArchiveKeyProvider : IArchiveKeyProvider
{
    public const string Format = "tansr-archive-key-dpapi-current-user-v1";
    private const int MaximumFileBytes = 16384;
    private readonly string _path;
    private readonly byte[] _keyDigest;

    private CurrentUserDpapiArchiveKeyProvider(string path, string keyId, byte[] keyDigest)
    {
        _path = path;
        KeyId = keyId;
        _keyDigest = keyDigest;
    }

    public string KeyId { get; }

    /// <summary>只创建新密钥文件；父目录须已由宿主创建。现有文件或失败残片绝不覆盖。</summary>
    public static CurrentUserDpapiArchiveKeyProvider Create(string path, string keyId)
    {
        var fullPath = ValidatePath(path);
        ArchiveSecurityJson.ValidateId(keyId);
        var key = new byte[32];
        try
        {
            using (var random = RandomNumberGenerator.Create()) { random.GetBytes(key); }
            var protectedKey = ProtectedData.Protect(key, Entropy(keyId), DataProtectionScope.CurrentUser);
            var body = WireJson.EncodeControl(ArchiveSecurityJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("format", Format);
                writer.WriteString("keyId", keyId);
                writer.WriteString("protectedKey", Convert.ToBase64String(protectedKey));
                writer.WriteEndObject();
            }), MaximumFileBytes);
            using var parent = StorageFileIdentity.Open(Path.GetDirectoryName(fullPath)!, true);
            // CREATE_NEW 由保持不共享 DELETE 的句柄执行；之后只打开同一受护文件。
            using var file = StorageFileIdentity.Open(fullPath, false, true, metadataOnly: true);
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough))
            {
                parent.Check(); file.Check();
                stream.Write(body, 0, body.Length);
                stream.Flush(true);
                parent.Check(); file.Check();
            }
            return new CurrentUserDpapiArchiveKeyProvider(fullPath, keyId, Digest(key));
        }
        catch { throw new StorageException("key_unavailable"); }
        finally { ArchiveSecurityJson.Clear(key); }
    }

    /// <summary>打开并验证既有密钥；错误用户、错误 ID、丢失或损坏均失败，不创建文件。</summary>
    public static CurrentUserDpapiArchiveKeyProvider Open(string path, string keyId)
    {
        var fullPath = ValidatePath(path);
        ArchiveSecurityJson.ValidateId(keyId);
        var key = ReadProtectedKey(fullPath, keyId);
        try { return new CurrentUserDpapiArchiveKeyProvider(fullPath, keyId, Digest(key)); }
        finally { ArchiveSecurityJson.Clear(key); }
    }

    /// <summary>每次重新读盘和解密；调用方拥有独立副本，并负责清零。</summary>
    public byte[] ReadKey()
    {
        var key = ReadProtectedKey(_path, KeyId);
        byte[]? digest = null;
        try
        {
            digest = Digest(key);
            if (!ArchiveSecurityJson.EqualBytes(digest, _keyDigest))
            {
                throw new StorageException("context_changed");
            }
            return key;
        }
        catch { ArchiveSecurityJson.Clear(key); throw; }
        finally { ArchiveSecurityJson.Clear(digest); }
    }

    private static byte[] ReadProtectedKey(string path, string keyId)
    {
        byte[]? key = null;
        try
        {
            using var parent = StorageFileIdentity.Open(Path.GetDirectoryName(path)!, true);
            using var file = StorageFileIdentity.Open(path, false, metadataOnly: true);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            parent.Check(); file.Check();
            if (stream.Length < 1 || stream.Length > MaximumFileBytes) { throw new StorageException("context_changed"); }
            var body = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < body.Length)
            {
                var count = stream.Read(body, offset, body.Length - offset);
                if (count == 0) { throw new StorageException("context_changed"); }
                offset += count;
            }
            if (stream.ReadByte() != -1) { throw new StorageException("context_changed"); }
            parent.Check(); file.Check();
            var value = WireJson.DecodeControl(body, MaximumFileBytes);
            ArchiveSecurityJson.Fields(value, "format", "keyId", "protectedKey");
            if (value.GetProperty("format").GetString() != Format || value.GetProperty("keyId").GetString() != keyId)
            {
                throw new StorageException("context_changed");
            }
            var encrypted = WireJson.DecodeBase64(value.GetProperty("protectedKey").GetString()!);
            key = ProtectedData.Unprotect(encrypted, Entropy(keyId), DataProtectionScope.CurrentUser);
            if (key.Length != 32) { throw new StorageException("context_changed"); }
            parent.Check(); file.Check();
            return key;
        }
        catch
        {
            ArchiveSecurityJson.Clear(key);
            throw new StorageException("context_changed");
        }
    }

    private static byte[] Entropy(string keyId) => Encoding.UTF8.GetBytes(Format + "\0" + keyId);

    private static byte[] Digest(byte[] key)
    {
        using (var sha = SHA256.Create()) { return sha.ComputeHash(key); }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/') ||
            path.IndexOf(':', 2) >= 0)
        {
            throw new StorageException("invalid_input");
        }
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Path.GetDirectoryName(fullPath) == null || string.IsNullOrEmpty(Path.GetFileName(fullPath)))
            {
                throw new StorageException("invalid_input");
            }
            return fullPath;
        }
        catch { throw new StorageException("invalid_input"); }
    }

}
