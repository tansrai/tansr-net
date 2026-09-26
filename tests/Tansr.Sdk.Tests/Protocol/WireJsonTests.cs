using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Tests.Protocol;

public sealed class WireJsonTests
{
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static JsonDocument Fixture(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", name)));

    [Fact]
    public void OriginalWireGoldensEncodeAndDecodeWithoutByteDrift()
    {
        using var fixture = Fixture("sdk2-wire-v1.json");
        foreach (var vector in fixture.RootElement.GetProperty("metadata").EnumerateArray())
        {
            var expected = Bytes(vector.GetProperty("utf8").GetString()!);
            Assert.Equal(expected, WireJson.EncodeControl(vector.GetProperty("value")));
            Assert.Equal(expected, WireJson.EncodeControl(WireJson.DecodeControl(expected)));
        }
    }

    [Fact]
    public void OriginalWireNegativeVectorsAreRejected()
    {
        using var fixture = Fixture("sdk2-wire-v1.json");
        foreach (var vector in fixture.RootElement.GetProperty("invalidMetadata").EnumerateArray())
        {
            Assert.Throws<WireProtocolException>(() => WireJson.DecodeControl(Bytes(vector.GetString()!)));
        }
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("{\"nest\":[{\"x\":1,\"x\":2}]}")]
    [InlineData("{\"s\":\"\\ud800\"}")]
    [InlineData("{\"s\":\"\\udc00\"}")]
    [InlineData("{\"s\":\"\\ud800X\"}")]
    [InlineData("{\"n\":9007199254740992}")]
    [InlineData("{\"n\":1e999}")]
    [InlineData("{\"n\":NaN}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("/*x*/{}")]
    [InlineData("{} true")]
    public void JsonParserRejectsAmbiguousOrUnboundedValues(string text)
        => Assert.Throws<WireProtocolException>(() => WireJson.Parse(Bytes(text)));

    [Fact]
    public void InvalidUtf8AndBomAreRejected()
    {
        foreach (var invalid in new[]
        {
            new byte[] { 0x22, 0xc0, 0xaf, 0x22 },
            new byte[] { 0x22, 0xed, 0xa0, 0x80, 0x22 },
            new byte[] { 0x22, 0xf0, 0x9f, 0x98, 0x22 },
            new byte[] { 0xef, 0xbb, 0xbf, 0x7b, 0x7d }
        })
        {
            Assert.Throws<WireProtocolException>(() => WireJson.Parse(invalid));
        }
    }

    [Fact]
    public void ByteLimitCountsUtf8AndDepthIsBounded()
    {
        var text = Bytes("\"汉😀\"");
        Assert.Equal(9, text.Length);
        Assert.Equal("汉😀", WireJson.Parse(text, 9).GetString());
        Assert.Equal("payload_too_large", Assert.Throws<WireProtocolException>(() => WireJson.Parse(text, 8)).Code);
        Assert.Equal("payload_too_large", Assert.Throws<WireProtocolException>(() => WireJson.EncodeControl(WireJson.Parse(text), 8)).Code);
        var accepted = new string('[', 32) + "0" + new string(']', 32);
        WireJson.DecodeControl(Bytes(accepted));
        Assert.Throws<WireProtocolException>(() => WireJson.Parse(Bytes("[" + accepted + "]")));
    }

    [Fact]
    public void NodeCountIsBoundedEvenWhenByteAndDepthCapsAllowIt()
    {
        var json = "[" + string.Join(",", new string[WireJson.MaximumNodes]).Replace(",", "0,") + "0]";
        Assert.Throws<WireProtocolException>(() => WireJson.Parse(Bytes(json), 1048576));
    }

    [Fact]
    public void CanonicalStringsKeepUnicodeScalarsAndAsciiOrdering()
    {
        const string expected = "{\"A\":\"汉😀\",\"Z\":\"<>&/\",\"a\":\"\u2028\u2029\",\"z\":\"é\"}";
        var value = WireJson.Parse(Bytes("{\"z\":\"é\",\"a\":\"\\u2028\\u2029\",\"Z\":\"<>&/\",\"A\":\"汉😀\"}"));
        Assert.Equal(expected, WireJson.CanonicalString(value));
        Assert.Equal(expected, WireJson.CanonicalString(WireJson.DecodeControl(Bytes(expected))));
    }

    [Theory]
    [InlineData(" {\"a\":1}")]
    [InlineData("{\"a\":1}\n")]
    [InlineData("{\"z\":1,\"a\":2}")]
    [InlineData("{\"s\":\"\\u0061\"}")]
    [InlineData("{\"s\":\"a\\/b\"}")]
    public void ControlDecodeRequiresExactCanonicalBytes(string value)
        => Assert.Throws<WireProtocolException>(() => WireJson.DecodeControl(Bytes(value)));

    [Fact]
    public void LegacyBusinessJsonKeepsNegativeAndFractionalNumbersAndOriginalIrBytes()
    {
        using var fixture = Fixture("sdk2-wire-v1.json");
        var original = fixture.RootElement.GetProperty("opaqueIr").GetString()!;
        var bytes = Bytes(original);
        var parsed = WireJson.Parse(bytes);
        Assert.Equal(JsonValueKind.Array, parsed.ValueKind);
        Assert.Equal(original, Encoding.UTF8.GetString(bytes));
        Assert.Throws<WireProtocolException>(() => WireJson.EncodeControl(parsed));
        Assert.Equal(-1.25, WireJson.Parse(Bytes("-1.25")).GetDouble());
        Assert.Equal("1000", WireJson.CanonicalString(WireJson.Parse(Bytes("1e3"))));
        Assert.Throws<WireProtocolException>(() => WireJson.DecodeControl(Bytes("1e3")));
    }

    [Fact]
    public void ParserOwnsReturnedElementAndRedactsErrors()
    {
        var result = WireJson.Parse(Bytes("{\"key\":\"value\"}"));
        Assert.Equal("value", result.GetProperty("key").GetString());
        var failure = Assert.Throws<WireProtocolException>(() => WireJson.Parse(Bytes("{\"secret-token\":NaN}")));
        Assert.DoesNotContain("secret-token", failure.ToString());
    }

    [Fact]
    public void ShaAndDomainSeparatedDigestMatchOriginalByteConstruction()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", WireJson.Sha256(Bytes("abc")));
        var payload = Bytes("[]");
        Assert.Equal(WireJson.Sha256(Bytes("tansr.sdk2.payload.v1\0[]")), WireJson.DomainDigest("tansr.sdk2.payload.v1", payload));
        Assert.NotEqual(WireJson.Sha256(payload), WireJson.DomainDigest("tansr.sdk2.payload.v1", payload));
        Assert.Throws<WireProtocolException>(() => WireJson.DomainDigest("bad\0domain", payload));
    }

    [Theory]
    [InlineData("W 10=")]
    [InlineData("W10=\n")]
    [InlineData("W10")]
    [InlineData("Zh==")]
    public void Base64RejectsNonCanonicalSpellings(string value)
        => Assert.Throws<WireProtocolException>(() => WireJson.DecodeBase64(value));

    [Fact]
    public void Base64KeepsExactBodyBytes() => Assert.Equal(Bytes("[]"), WireJson.DecodeBase64("W10="));

    [Fact]
    public void SchemaResourceAndManifestFingerprintStayPinned()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "sdk2-ext-v1.schema.json"));
        Assert.Equal(WireContract.SchemaSha256, WireJson.Sha256(bytes));
        using var fixture = Fixture("manifest.json");
        Assert.Equal(WireContract.SourceRevision, fixture.RootElement.GetProperty("sourceRevision").GetString());
        Assert.Equal("not-frozen-not-implemented", fixture.RootElement.GetProperty("terminalServices").GetString());
    }

    public static IEnumerable<object[]> SchemaVectors()
    {
        using var fixture = Fixture("schema-vectors.json");
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            yield return new object[] { vector.GetProperty("id").GetString()!, vector.GetProperty("schema").GetString()!,
                vector.GetProperty("valid").GetBoolean(), vector.GetProperty("value").GetRawText() };
        }
    }

    [Theory]
    [MemberData(nameof(SchemaVectors))]
    public void NamedSchemaConsumesPinnedPositiveAndNegativeVectors(string id, string schema, bool valid, string json)
    {
        Assert.NotEmpty(id);
        var value = WireJson.Parse(Bytes(json), 1048576);
        if (valid)
        {
            WireJson.ValidateNamed(schema, value);
            WireJson.ValidateNamed(schema, WireJson.DecodeControl(WireJson.EncodeControl(value, 1048576), 1048576));
        }
        else
        {
            Assert.Throws<WireProtocolException>(() => WireJson.ValidateNamed(schema, value));
        }
    }

    [Fact]
    public void UnknownNamedSchemaIsNotAcceptedByRootUnion()
        => Assert.Throws<WireProtocolException>(() => WireJson.ValidateNamed("FutureTerminalRequest", WireJson.Parse(Bytes("{}"))));
}
