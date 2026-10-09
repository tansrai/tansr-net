using System.Security.Cryptography;
using System.Text;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Security;

/// <summary>Local authenticated storage codec shared by publication and execution journals. Keys and authenticated plaintext never enter SQLite.</summary>
internal sealed class MemoryPublicationBodyCipher
{
    internal const string Format = "terminal-memory-publication-encrypted-net-sqlite-v1";
    internal const int Overhead = 28;
    private readonly IArchiveKeyProvider _provider;
    private readonly string _metadata;
    private byte[]? _keyDigest;

    internal MemoryPublicationBodyCipher(IArchiveKeyProvider provider, string keyId, string metadata)
    { _provider = provider; KeyId = keyId; _metadata = metadata; }

    internal string KeyId { get; }

    internal static string ReadKeyId(IArchiveKeyProvider provider)
    {
        string id;
        try { id = provider.KeyId; }
        catch { throw new StorageException("context_changed"); }
        ArchiveSecurityJson.ValidateId(id);
        return id;
    }

    internal byte[] Seal(string context, byte[] plaintext) => WithKey(key =>
    {
        var nonce = new byte[12];
        using (var random = RandomNumberGenerator.Create()) { random.GetBytes(nonce); }
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var cipher = new AesGcm(key, 16)) { cipher.Encrypt(nonce, plaintext, ciphertext, tag, Aad(context)); }
        var result = new byte[plaintext.Length + Overhead];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, Overhead, ciphertext.Length);
        return result;
    });

    internal byte[] Open(string context, byte[] encoded)
    {
        if (encoded.Length < Overhead) throw new StorageException("integrity_mismatch");
        return WithKey(key =>
        {
            var nonce = new byte[12]; var tag = new byte[16];
            var ciphertext = new byte[encoded.Length - Overhead]; var plaintext = new byte[ciphertext.Length];
            Buffer.BlockCopy(encoded, 0, nonce, 0, nonce.Length);
            Buffer.BlockCopy(encoded, nonce.Length, tag, 0, tag.Length);
            Buffer.BlockCopy(encoded, Overhead, ciphertext, 0, ciphertext.Length);
            try
            {
                using (var cipher = new AesGcm(key, 16)) { cipher.Decrypt(nonce, ciphertext, tag, plaintext, Aad(context)); }
                return plaintext;
            }
            catch (CryptographicException) { ArchiveSecurityJson.Clear(plaintext); throw new StorageException("integrity_mismatch"); }
            catch { ArchiveSecurityJson.Clear(plaintext); throw; }
        });
    }

    private byte[] Aad(string context) => Encoding.UTF8.GetBytes(_metadata + "\0" + context);

    private T WithKey<T>(Func<byte[], T> action)
    {
        byte[]? key = null;
        try
        {
            try
            {
                if (_provider.KeyId != KeyId) throw new StorageException("context_changed");
                key = _provider.ReadKey();
                if (key == null || key.Length != 32 || _provider.KeyId != KeyId) throw new StorageException("context_changed");
                byte[] digest;
                using (var sha = SHA256.Create()) { digest = sha.ComputeHash(key); }
                try
                {
                    if (_keyDigest == null) _keyDigest = (byte[])digest.Clone();
                    else if (!ArchiveSecurityJson.EqualBytes(_keyDigest, digest)) throw new StorageException("context_changed");
                }
                finally { ArchiveSecurityJson.Clear(digest); }
            }
            catch { throw new StorageException("context_changed"); }
            return action(key);
        }
        finally { ArchiveSecurityJson.Clear(key); }
    }
}
