using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalPersistenceContractTests
{
    private static byte[] GoldenBytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "terminal-persistence-v1.golden.json"));
    private static JsonElement Golden() { using var value = JsonDocument.Parse(GoldenBytes()); return value.RootElement.Clone(); }
    public static IEnumerable<object[]> Vectors()
    {
        foreach (string group in new[] { "positive", "negative" })
            foreach (var item in Golden().GetProperty(group).EnumerateArray())
                yield return [item.GetProperty("id").GetString()!, item.GetProperty("definition").GetString()!, item.GetProperty("value").GetRawText(), group == "positive"];
    }
    [Theory, MemberData(nameof(Vectors))]
    public void FrozenSharedVectorsKeepExactShapes(string id, string definition, string text, bool valid)
    {
        Assert.NotEmpty(id); using var value = JsonDocument.Parse(text);
        if (valid) TerminalPersistenceContract.ValidateStructure(definition, value.RootElement);
        else Assert.Throws<WireProtocolException>(() => TerminalPersistenceContract.ValidateStructure(definition, value.RootElement));
    }
    [Fact]
    public void ExactIndependentSchemaAndGoldenFingerprintsAndCounts()
    {
        Assert.Equal("4b2c492ae590fda9447a5a53d13f3f7a935956c929cc23441a765947e3ef6503", WireJson.Sha256(GoldenBytes()));
        using var input = typeof(TerminalPersistenceContract).Assembly.GetManifestResourceStream("Tansr.Sdk.Terminal.terminal-persistence-v1.schema.json")!;
        using var output = new MemoryStream(); input.CopyTo(output);
        Assert.Equal("47387fb03308d00244e876a3bb429e24b81ac8d94d5f611aeda43e03b7c8b16c", WireJson.Sha256(output.ToArray()));
        Assert.Equal(38, Golden().GetProperty("positive").GetArrayLength()); Assert.Equal(32, Golden().GetProperty("negative").GetArrayLength());
        Assert.Equal("d7310cdaf0cd34d0b9fb2664e6d679422cfcebd7b6f930c5c03d15d42b6fc8bf", Golden().GetProperty("normativeSha256").GetString());
    }
}
