using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class PersistenceCustodyContractTests
{
    [Fact]
    public void PinnedSchemaAndSharedByteVectorsStayIdentical()
    {
        var root = WireJson.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "persistence-custody-handoff-v1.golden.json")), 1048576);
        Assert.Equal(PersistenceCustodyContract.SchemaSha256, root.GetProperty("schemaSha256").GetString());
        foreach (var row in root.GetProperty("positive").EnumerateArray())
            PersistenceCustodyContract.Validate(row.GetProperty("definition").GetString()!, row.GetProperty("value"), 524288);
        var v = root.GetProperty("digestVectors");
        string Text(string name) => v.GetProperty(name).GetString()!;
        Assert.Equal(Text("fixedRoot"), PersistenceCustodyContract.SnapshotDigest(v.GetProperty("identity"), v.GetProperty("rootRecord")));
        Assert.Equal(Text("previousDigest"), PersistenceCustodyContract.InventorySeed(v.GetProperty("identity"), "terminal-persistence-v1", Text("fixedRoot")));
        Assert.Equal(Text("recordDigest"), PersistenceCustodyContract.RecordDigest(Text("recordIndex"), Text("previousDigest"), v.GetProperty("rootRecord")));
        Assert.Equal(Text("writerOperationId"), PersistenceCustodyContract.WriterOperationId(v.GetProperty("writerRequest")));
        Assert.Equal(Text("writerIntentSha256"), PersistenceCustodyContract.Digest(v.GetProperty("writerRequest")));
        Assert.Equal(v.GetProperty("controlRef").GetProperty("sha256").GetString(), PersistenceCustodyContract.Digest(v.GetProperty("control")));
    }

    [Fact]
    public void SharedNegativeStructuralVectorsRemainRejected()
    {
        var root = WireJson.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "persistence-custody-handoff-v1.golden.json")), 1048576);
        foreach (var row in root.GetProperty("negative").EnumerateArray())
            Assert.Throws<WireProtocolException>(() => PersistenceCustodyContract.Validate(row.GetProperty("definition").GetString()!, row.GetProperty("value"), 524288));
    }
}
