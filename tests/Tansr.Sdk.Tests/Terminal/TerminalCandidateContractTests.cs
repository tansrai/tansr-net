using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalCandidateContractTests
{
    internal static JsonElement Goldens()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Terminal", "Fixtures", "terminal-services-v1.golden.json")));
        return doc.RootElement.Clone();
    }
    public static IEnumerable<object[]> StructuralVectors()
    {
        var root = Goldens();
        foreach (var group in new[] { "positive", "negative" })
            foreach (var value in root.GetProperty(group).EnumerateArray())
                yield return [value.GetProperty("id").GetString()!, value.GetProperty("definition").GetString()!, value.GetProperty("value").GetRawText(), group == "positive"];
    }
    public static IEnumerable<object[]> SemanticVectors()
    {
        var root = Goldens().GetProperty("semantic").GetProperty("outputStatus");
        foreach (var group in new[] { "positive", "negative" })
            foreach (var value in root.GetProperty(group).EnumerateArray())
                yield return [value.GetProperty("id").GetString()!, value.GetProperty("value").GetRawText(), group == "positive"];
    }
    [Theory]
    [MemberData(nameof(StructuralVectors))]
    public void ConsumeUnmodifiedServeStructuralGoldens(string id, string name, string json, bool valid)
    {
        Assert.NotEmpty(id); using var document = JsonDocument.Parse(json);
        if (valid) TerminalCandidateContract.ValidateStructure(name, document.RootElement);
        else Assert.Throws<WireProtocolException>(() => TerminalCandidateContract.ValidateStructure(name, document.RootElement));
    }
    [Theory]
    [MemberData(nameof(SemanticVectors))]
    public void ConsumeUnmodifiedServeCrossFieldGoldens(string id, string json, bool valid)
    {
        Assert.NotEmpty(id); using var document = JsonDocument.Parse(json);
        if (valid) TerminalCandidateContract.Validate("OutputStatus", document.RootElement);
        else Assert.Throws<WireProtocolException>(() => TerminalCandidateContract.Validate("OutputStatus", document.RootElement));
    }
    [Fact]
    public void EmbeddedCandidateIsPinnedAndSharedDefinitionsStayIdenticalToSdk2()
    {
        var assembly = typeof(TerminalCandidateContract).Assembly;
        using var candidateStream = assembly.GetManifestResourceStream("Tansr.Sdk.Terminal.terminal-services-v1.schema.json")!;
        using var buffer = new MemoryStream(); candidateStream.CopyTo(buffer);
        Assert.Equal(TerminalCandidateContract.SchemaSha256, WireJson.Sha256(buffer.ToArray()));
        Assert.Equal(TerminalCandidateContract.SchemaSha256, Goldens().GetProperty("schemaSha256").GetString());
        using var candidate = JsonDocument.Parse(buffer.ToArray());
        using var originalStream = assembly.GetManifestResourceStream("Tansr.Sdk.Protocol.sdk2-ext-v1.schema.json")!;
        using var original = JsonDocument.Parse(originalStream);
        foreach (var name in Goldens().GetProperty("copiedDefinitions").EnumerateArray())
            Assert.Equal(WireJson.CanonicalString(original.RootElement.GetProperty("definitions").GetProperty(name.GetString()!)),
                WireJson.CanonicalString(candidate.RootElement.GetProperty("definitions").GetProperty(name.GetString()!)));
        Assert.DoesNotContain(assembly.ExportedTypes, type => type.Namespace == "Tansr.Sdk.Terminal");
    }
    [Fact]
    public void RepresentableBoundaryBlockIsStructurallyValidButCannotOverflowOffset()
    {
        var value = Goldens().GetProperty("positive").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "output-block-string-boundaries").GetProperty("value");
        TerminalCandidateContract.ValidateStructure("OutputBlock", value);
        Assert.Equal("capacity_exceeded", Assert.Throws<WireProtocolException>(() => TerminalCandidateContract.Validate("OutputBlock", value)).Code);
    }
}
