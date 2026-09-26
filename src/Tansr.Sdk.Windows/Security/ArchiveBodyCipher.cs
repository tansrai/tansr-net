using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Security;

/// <summary>与 SDK2 原档案格式兼容的正文编码；每次操作短暂持钥，同一 ID 不允许换钥。不负责存储事务、ACK 或记忆决策。</summary>
public sealed class ArchiveBodyCipher
{
    public const string Format = "sdk2-archive-encrypted-sqlite-v1";
    public const int ChunkBytes = 262144;
    private const int Overhead = 28;
    private static readonly byte[] KeyCheck = Encoding.UTF8.GetBytes("tansr.archive.key-check.v1");
    private readonly IArchiveKeyProvider _provider;
    private readonly JsonElement _identity;
    private readonly object _keyGate = new object();
    private byte[]? _keyDigest;

    public ArchiveBodyCipher(IArchiveKeyProvider keyProvider, JsonElement receiverIdentity)
    {
        _provider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        try { KeyId = keyProvider.KeyId; }
        catch { throw new StorageException("context_changed"); }
        ArchiveSecurityJson.ValidateId(KeyId);
        _identity = ValidateIdentity(receiverIdentity);
    }

    public string KeyId { get; }

    public static long EncryptedBytes(long plaintextBytes)
    {
        if (plaintextBytes < 0 || plaintextBytes > WireJson.MaximumSafeInteger)
        {
            throw new StorageException("invalid_input");
        }
        return checked(plaintextBytes + ((plaintextBytes + ChunkBytes - 1) / ChunkBytes) * Overhead);
    }

    public byte[] CreateCheck() => WithKey(key => SealChunk(key, KeyCheck, Aad(null, 0, KeyCheck.Length)));

    public void VerifyCheck(byte[] bytes)
    {
        if (bytes == null || bytes.Length != KeyCheck.Length + Overhead)
        {
            throw new StorageException("integrity_mismatch");
        }
        WithKey(key =>
        {
            var plaintext = OpenChunk(key, bytes, 0, KeyCheck.Length, Aad(null, 0, KeyCheck.Length));
            try
            {
                if (!ArchiveSecurityJson.EqualBytes(plaintext, KeyCheck)) { throw new StorageException("integrity_mismatch"); }
                return true;
            }
            finally { ArchiveSecurityJson.Clear(plaintext); }
        });
    }

    public byte[] Seal(JsonElement artifact, byte[] bytes)
    {
        var reference = Reference(artifact, out var length);
        if (bytes == null || bytes.LongLength != length) { throw new StorageException("integrity_mismatch"); }
        var encodedLength = EncryptedBytes(length);
        if (encodedLength > int.MaxValue) { throw new StorageException("capacity_exceeded"); }
        return WithKey(key =>
        {
            var result = new byte[(int)encodedLength];
            var cursor = 0;
            try
            {
                for (var offset = 0; offset < bytes.Length;)
                {
                    var count = Math.Min(ChunkBytes, bytes.Length - offset);
                    var chunk = new byte[count];
                    Buffer.BlockCopy(bytes, offset, chunk, 0, count);
                    try
                    {
                        var encrypted = SealChunk(key, chunk, Aad(reference, offset, count));
                        Buffer.BlockCopy(encrypted, 0, result, cursor, encrypted.Length);
                        cursor += encrypted.Length;
                    }
                    finally { ArchiveSecurityJson.Clear(chunk); }
                    offset += count;
                }
                return result;
            }
            catch { ArchiveSecurityJson.Clear(result); throw; }
        });
    }

    public byte[] Open(JsonElement artifact, byte[] bytes)
    {
        var reference = Reference(artifact, out var length);
        if (length > int.MaxValue) { throw new StorageException("capacity_exceeded"); }
        if (bytes == null || bytes.LongLength != EncryptedBytes(length)) { throw new StorageException("integrity_mismatch"); }
        return OpenRange(reference, bytes, 0, (int)length);
    }

    /// <summary>返回需从密文介质读取的整块范围；Start/Bytes 是密文位置，First/End 是明文位置。</summary>
    public ArchiveCipherRange Range(JsonElement artifact, long offset, int size)
    {
        Reference(artifact, out var length);
        if (offset < 0 || size < 0 || offset > length || size > length - offset)
        {
            throw new StorageException("invalid_input");
        }
        var first = offset / ChunkBytes * ChunkBytes;
        var end = Math.Min(length, (offset + size + ChunkBytes - 1) / ChunkBytes * ChunkBytes);
        return new ArchiveCipherRange(first + first / ChunkBytes * Overhead,
            EncryptedBytes(end - first), first, end);
    }

    public byte[] OpenRange(JsonElement artifact, byte[] bytes, long offset, int size)
    {
        var reference = Reference(artifact, out var length);
        var range = Range(reference, offset, size);
        if (bytes == null || bytes.LongLength != range.Bytes) { throw new StorageException("integrity_mismatch"); }
        return WithKey(key =>
        {
            var result = new byte[size];
            var cursor = 0;
            try
            {
                for (var position = range.First; position < range.End; position += ChunkBytes)
                {
                    var count = (int)Math.Min(ChunkBytes, length - position);
                    var part = OpenChunk(key, bytes, cursor, count, Aad(reference, position, count));
                    try
                    {
                        var from = Math.Max(offset, position);
                        var through = Math.Min(offset + size, position + count);
                        Buffer.BlockCopy(part, (int)(from - position), result, (int)(from - offset), (int)(through - from));
                    }
                    finally { ArchiveSecurityJson.Clear(part); }
                    cursor += count + Overhead;
                }
                return result;
            }
            catch { ArchiveSecurityJson.Clear(result); throw; }
        });
    }

    private T WithKey<T>(Func<byte[], T> action)
    {
        byte[]? key = null;
        try
        {
            try
            {
                if (_provider.KeyId != KeyId) { throw new StorageException("context_changed"); }
                key = _provider.ReadKey();
                if (key == null || key.Length != 32 || _provider.KeyId != KeyId)
                {
                    throw new StorageException("context_changed");
                }
                // 一个 ID 表示固定密钥；防同一事务多次调用间 A→B→A 换钥后末端核验漏过。
                byte[] digest;
                using (var sha = SHA256.Create()) { digest = sha.ComputeHash(key); }
                try
                {
                    lock (_keyGate)
                    {
                        if (_keyDigest == null) { _keyDigest = (byte[])digest.Clone(); }
                        else if (!ArchiveSecurityJson.EqualBytes(_keyDigest, digest)) { throw new StorageException("context_changed"); }
                    }
                }
                finally { ArchiveSecurityJson.Clear(digest); }
            }
            catch { throw new StorageException("context_changed"); }
            return action(key);
        }
        finally { ArchiveSecurityJson.Clear(key); }
    }

    private byte[] Aad(JsonElement? reference, long offset, int size) => WireJson.EncodeControl(ArchiveSecurityJson.Build(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("format", Format);
        writer.WritePropertyName("identity"); _identity.WriteTo(writer);
        writer.WriteString("keyId", KeyId);
        writer.WritePropertyName("ref");
        if (reference.HasValue) { reference.Value.WriteTo(writer); } else { writer.WriteNullValue(); }
        writer.WriteNumber("offset", offset);
        writer.WriteNumber("bytes", size);
        writer.WriteEndObject();
    }));

    private static byte[] SealChunk(byte[] key, byte[] plaintext, byte[] aad)
    {
        var nonce = new byte[12];
        using (var random = RandomNumberGenerator.Create()) { random.GetBytes(nonce); }
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var cipher = new AesGcm(key, 16)) { cipher.Encrypt(nonce, plaintext, ciphertext, tag, aad); }
        var result = new byte[plaintext.Length + Overhead];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, Overhead, ciphertext.Length);
        return result;
    }

    private static byte[] OpenChunk(byte[] key, byte[] bytes, int start, int count, byte[] aad)
    {
        var nonce = new byte[12];
        var tag = new byte[16];
        var ciphertext = new byte[count];
        var plaintext = new byte[count];
        try
        {
            Buffer.BlockCopy(bytes, start, nonce, 0, nonce.Length);
            Buffer.BlockCopy(bytes, start + nonce.Length, tag, 0, tag.Length);
            Buffer.BlockCopy(bytes, start + Overhead, ciphertext, 0, count);
            using (var cipher = new AesGcm(key, 16)) { cipher.Decrypt(nonce, ciphertext, tag, plaintext, aad); }
            return plaintext;
        }
        catch (CryptographicException) { ArchiveSecurityJson.Clear(plaintext); throw new StorageException("integrity_mismatch"); }
        catch { ArchiveSecurityJson.Clear(plaintext); throw; }
    }

    private static JsonElement Reference(JsonElement value, out long length)
    {
        try
        {
            var copy = WireJson.Parse(WireJson.EncodeControl(value));
            WireJson.ValidateNamed("ArtifactRef", copy);
            length = copy.GetProperty("bytes").GetInt64();
            return copy;
        }
        catch { throw new StorageException("invalid_input"); }
    }

    private static JsonElement ValidateIdentity(JsonElement value)
    {
        try
        {
            var copy = WireJson.Parse(WireJson.EncodeControl(value));
            ArchiveSecurityJson.Fields(copy, "scope", "bindingId", "target", "sourceId", "sourceGeneration");
            var scope = copy.GetProperty("scope");
            ArchiveSecurityJson.Fields(scope, "applicationScopeId", "endUserId");
            WireJson.ValidateNamed("Scope", ArchiveSecurityJson.WithProperty(scope, "authorizationRevision", "0"));
            var target = copy.GetProperty("target");
            ArchiveSecurityJson.Fields(target, "sessionId", "generations");
            WireJson.ValidateNamed("Target", ArchiveSecurityJson.WithProperty(target, "sourceSnapshotDigest", new string('0', 64)));
            foreach (var name in new[] { "bindingId", "sourceId", "sourceGeneration" }) { WireJson.ValidateNamed("Id", copy.GetProperty(name)); }
            return copy;
        }
        catch { throw new StorageException("invalid_input"); }
    }
}

public readonly struct ArchiveCipherRange
{
    public ArchiveCipherRange(long start, long bytes, long first, long end)
    {
        Start = start; Bytes = bytes; First = first; End = end;
    }
    public long Start { get; }
    public long Bytes { get; }
    public long First { get; }
    public long End { get; }
}

internal static class ArchiveSecurityJson
{
    internal static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream)) { write(writer); writer.Flush(); }
            return WireJson.Parse(stream.ToArray());
        }
    }

    internal static JsonElement WithProperty(JsonElement value, string name, string text) => Build(writer =>
    {
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject()) { property.WriteTo(writer); }
        writer.WriteString(name, text);
        writer.WriteEndObject();
    });

    internal static void ValidateId(string id)
    {
        try { WireJson.ValidateNamed("Id", Build(writer => writer.WriteStringValue(id))); }
        catch { throw new StorageException("invalid_input"); }
    }

    internal static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new StorageException("invalid_input"); }
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (Array.IndexOf(fields, property.Name) < 0) { throw new StorageException("invalid_input"); }
            count++;
        }
        if (count != fields.Length) { throw new StorageException("invalid_input"); }
    }

    internal static void Clear(byte[]? bytes)
    {
        if (bytes != null) { Array.Clear(bytes, 0, bytes.Length); }
    }

    internal static bool EqualBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) { return false; }
        var difference = 0;
        for (var i = 0; i < left.Length; i++) { difference |= left[i] ^ right[i]; }
        return difference == 0;
    }
}
