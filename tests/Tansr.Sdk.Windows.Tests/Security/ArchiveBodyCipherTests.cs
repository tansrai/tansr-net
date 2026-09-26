using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;

namespace Tansr.Sdk.Windows.Tests.Security;

public sealed class ArchiveBodyCipherTests
{
    // 由 tansr-cli 7fe5829 的 receiver-encryption.ts 与 wire-codec.ts 实际生成；仅合成资料。
    private const string IdentityJson = """
        {"bindingId":"binding-1","scope":{"applicationScopeId":"app","endUserId":"user"},"sourceGeneration":"generation-1","sourceId":"source-1","target":{"generations":{"deletionGeneration":"0","historyEpoch":"history-1","projectionRevision":"1"},"sessionId":"session-1"}}
        """;
    private const string ReferenceJson = """
        {"artifactId":"artifact-1","bytes":36,"mediaType":"text/plain","sha256":"40010abff2704e48d9bb111617af237124c28b364ff8a699c11741f417b7bd31","sourceId":"source-1"}
        """;
    private const string NodeCiphertext = "pTMK0jZYdYedchW+e7Cxqns5Q71S+vP4PrkBhbflJKFFGcnFQV4sVNlOyc3V24DZK6oFHEIJjpeEZKCHjgK7cA==";
    private const string NodeKeyCheck = "VIZpp08wYKNlFXYdpQAHl0b5UWmlNxoqPhJj4jneiA5VM563q8cAjn050lm+e956C6iv04yR";
    private static readonly byte[] NodePlaintext = Convert.FromBase64String("U0RLMiBzeW50aGV0aWMgYXJjaGl2ZTog5Lit5paHIPCfmIAK");

    [Fact]
    public void OpensOriginalTypeScriptVectorAndCheck()
    {
        var provider = new SyntheticKeys();
        var cipher = new ArchiveBodyCipher(provider, Identity());
        cipher.VerifyCheck(Convert.FromBase64String(NodeKeyCheck));
        Assert.Equal(NodePlaintext, cipher.Open(Parse(ReferenceJson), Convert.FromBase64String(NodeCiphertext)));
        AssertCleared(provider);
    }

    [Fact]
    public void ChunkRangesAuthenticateWholeChunksAndRejectEmptyArtifactReferences()
    {
        var provider = new SyntheticKeys();
        var cipher = new ArchiveBodyCipher(provider, Identity());
        var plaintext = Enumerable.Range(0, ArchiveBodyCipher.ChunkBytes * 2 + 37).Select(i => (byte)(i % 251)).ToArray();
        var reference = Reference(plaintext);
        var encrypted = cipher.Seal(reference, plaintext);
        Assert.Equal(plaintext.LongLength + 84, encrypted.LongLength);
        var range = cipher.Range(reference, ArchiveBodyCipher.ChunkBytes - 5, 19);
        Assert.Equal(0, range.Start);
        Assert.Equal(ArchiveBodyCipher.ChunkBytes * 2L + 56, range.Bytes);
        Assert.Equal(plaintext.Skip(ArchiveBodyCipher.ChunkBytes - 5).Take(19).ToArray(),
            cipher.OpenRange(reference, encrypted.Take((int)range.Bytes).ToArray(), ArchiveBodyCipher.ChunkBytes - 5, 19));
        var lastRange = cipher.Range(reference, ArchiveBodyCipher.ChunkBytes * 2 + 1, 4);
        Assert.Equal(ArchiveBodyCipher.ChunkBytes * 2L + 56, lastRange.Start);
        Assert.Equal(65, lastRange.Bytes);
        Assert.Equal(plaintext.Skip(ArchiveBodyCipher.ChunkBytes * 2 + 1).Take(4).ToArray(),
            cipher.OpenRange(reference, encrypted.Skip((int)lastRange.Start).ToArray(), ArchiveBodyCipher.ChunkBytes * 2 + 1, 4));
        Assert.Equal(plaintext, cipher.Open(reference, encrypted));
        var empty = Reference(Array.Empty<byte>());
        Assert.Equal("invalid_input", Assert.Throws<StorageException>(() => cipher.Seal(empty, Array.Empty<byte>())).Code);
        Assert.Equal("invalid_input", Assert.Throws<StorageException>(() => cipher.Open(empty, Array.Empty<byte>())).Code);
        AssertCleared(provider);
    }

    [Theory]
    [InlineData("user", "another-user")]
    [InlineData("app", "another-app")]
    [InlineData("binding-1", "binding-2")]
    [InlineData("generation-1", "generation-2")]
    [InlineData("history-1", "history-2")]
    public void IdentityCannotBeChanged(string original, string replacement)
    {
        var provider = new SyntheticKeys();
        var cipher = new ArchiveBodyCipher(provider, Parse(IdentityJson.Replace("\"" + original + "\"", "\"" + replacement + "\"", StringComparison.Ordinal)));
        Assert.Equal("integrity_mismatch", Assert.Throws<StorageException>(() =>
            cipher.Open(Parse(ReferenceJson), Convert.FromBase64String(NodeCiphertext))).Code);
        AssertCleared(provider);
    }

    [Fact]
    public void WrongKeyReferenceOffsetAndAuthenticationTagFail()
    {
        var provider = new SyntheticKeys();
        var cipher = new ArchiveBodyCipher(provider, Identity());
        var altered = Convert.FromBase64String(NodeCiphertext);
        altered[12] ^= 1;
        Assert.Equal("integrity_mismatch", Assert.Throws<StorageException>(() => cipher.Open(Parse(ReferenceJson), altered)).Code);
        var otherRef = Parse(ReferenceJson.Replace("artifact-1", "artifact-2", StringComparison.Ordinal));
        Assert.Equal("integrity_mismatch", Assert.Throws<StorageException>(() => cipher.Open(otherRef, Convert.FromBase64String(NodeCiphertext))).Code);
        provider.Key[0] ^= 1;
        var wrongKeyCipher = new ArchiveBodyCipher(provider, Identity());
        Assert.Equal("integrity_mismatch", Assert.Throws<StorageException>(() => wrongKeyCipher.VerifyCheck(Convert.FromBase64String(NodeKeyCheck))).Code);
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => cipher.VerifyCheck(Convert.FromBase64String(NodeKeyCheck))).Code);
        provider.Key[0] ^= 1;
        var plaintext = new byte[ArchiveBodyCipher.ChunkBytes * 2];
        var reference = Reference(plaintext);
        var encrypted = cipher.Seal(reference, plaintext);
        var range = cipher.Range(reference, ArchiveBodyCipher.ChunkBytes, 1);
        var firstChunk = encrypted.Take((int)range.Bytes).ToArray();
        Assert.Equal("integrity_mismatch", Assert.Throws<StorageException>(() => cipher.OpenRange(reference, firstChunk, ArchiveBodyCipher.ChunkBytes, 1)).Code);
        AssertCleared(provider);
    }

    [Fact]
    public void SameIdKeyReplacementCannotHideBehindAnEndOfTransactionCheck()
    {
        var provider = new SyntheticKeys();
        var cipher = new ArchiveBodyCipher(provider, Identity());
        var check = cipher.CreateCheck();
        provider.Key[0] ^= 1;
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => cipher.Seal(Parse(ReferenceJson), NodePlaintext)).Code);
        provider.Key[0] ^= 1;
        cipher.VerifyCheck(check);
        Assert.Equal(NodePlaintext, cipher.Open(Parse(ReferenceJson), cipher.Seal(Parse(ReferenceJson), NodePlaintext)));
        AssertCleared(provider);
    }

    [Fact]
    public void RevocationMalformedKeyAndIdChangeNeverUseAnOldKey()
    {
        var provider = new SyntheticKeys();
        var cipher = new ArchiveBodyCipher(provider, Identity());
        var check = cipher.CreateCheck();
        provider.Revoked = true;
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => cipher.VerifyCheck(check)).Code);
        provider.Revoked = false;
        provider.KeyId = "replacement-key";
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => cipher.CreateCheck()).Code);
        provider.KeyId = "synthetic-key";
        provider.Length = 31;
        Assert.Equal("context_changed", Assert.Throws<StorageException>(() => cipher.CreateCheck()).Code);
        AssertCleared(provider);
    }

    [Fact]
    public void InvalidRangesAndNonIdentityDataAreRejected()
    {
        var cipher = new ArchiveBodyCipher(new SyntheticKeys(), Identity());
        var reference = Parse(ReferenceJson);
        Assert.Equal("invalid_input", Assert.Throws<StorageException>(() => cipher.Range(reference, -1, 2)).Code);
        Assert.Equal("invalid_input", Assert.Throws<StorageException>(() => cipher.Range(reference, 35, 2)).Code);
        Assert.Equal("integrity_mismatch", Assert.Throws<StorageException>(() => cipher.Open(reference, new byte[27])).Code);
        Assert.Equal("invalid_input", Assert.Throws<StorageException>(() => new ArchiveBodyCipher(new SyntheticKeys(), Parse("{}"))).Code);
        Assert.Equal("invalid_input", Assert.Throws<StorageException>(() => new ArchiveBodyCipher(new SyntheticKeys { KeyId = "" }, Identity())).Code);
    }

    [Fact]
    public async Task NodeOpensCSharpChunkedOutputWithItsOwnAad()
    {
        var provider = new SyntheticKeys();
        var cipher = new ArchiveBodyCipher(provider, Identity());
        var plaintext = Enumerable.Range(0, ArchiveBodyCipher.ChunkBytes + 31).Select(i => (byte)(i % 247)).ToArray();
        var reference = Reference(plaintext);
        var encrypted = cipher.Seal(reference, plaintext);
        var input = JsonSerializer.Serialize(new
        {
            identity = Identity(),
            reference,
            keyId = provider.KeyId,
            key = Convert.ToBase64String(provider.Key),
            ciphertext = Convert.ToBase64String(encrypted),
            check = Convert.ToBase64String(cipher.CreateCheck())
        });
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(SourcePath())!, "node-cipher-interop.mjs"));
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await error);
            Assert.Equal(WireJson.Sha256(plaintext), (await output).Trim());
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
        AssertCleared(provider);
    }

    private static JsonElement Identity() => Parse(IdentityJson);
    private static JsonElement Parse(string json) => WireJson.Parse(Encoding.UTF8.GetBytes(json));
    private static string SourcePath([CallerFilePath] string path = "") => path;
    private static JsonElement Reference(byte[] bytes) => JsonSerializer.SerializeToElement(new
    {
        artifactId = "artifact-1",
        sourceId = "source-1",
        bytes = bytes.Length,
        sha256 = WireJson.Sha256(bytes),
        mediaType = "application/octet-stream"
    });

    private static void AssertCleared(SyntheticKeys provider)
    {
        Assert.NotEmpty(provider.Copies);
        Assert.All(provider.Copies, key => Assert.All(key, value => Assert.Equal((byte)0, value)));
    }

    private sealed class SyntheticKeys : IArchiveKeyProvider
    {
        public string KeyId { get; set; } = "synthetic-key";
        public byte[] Key { get; } = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        public List<byte[]> Copies { get; } = new();
        public bool Revoked { get; set; }
        public int Length { get; set; } = 32;
        public byte[] ReadKey()
        {
            if (Revoked) { throw new InvalidOperationException("synthetic revocation"); }
            var copy = Key.Take(Length).ToArray();
            Copies.Add(copy);
            return copy;
        }
    }
}
