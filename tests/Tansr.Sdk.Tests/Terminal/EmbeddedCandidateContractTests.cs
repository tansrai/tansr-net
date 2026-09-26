using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class EmbeddedCandidateContractTests
{
    [Fact]
    public void SourceRecoveryGoldenPreservesExactWireAndOriginalRevisionChain()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Terminal", "Fixtures", "sdk2-archive-recovery-v1.golden.json")));
        var golden = document.RootElement;
        var recovery = new EmbeddedWireContract("Tansr.Sdk.Protocol.sdk2-archive-recovery-v1.schema.json",
            golden.GetProperty("schemaSha256").GetString()!, ["ArchiveAckRequest", "MutationReceipt"]);
        foreach (var entry in new[] { ("request", "AckRebaseRequest", 263168), ("response", "AckRebaseReceipt", 528384) })
        {
            var expected = golden.GetProperty("wire").GetProperty(entry.Item1);
            var bytes = WireJson.EncodeControl(golden.GetProperty(entry.Item1), entry.Item3);
            Assert.Equal(expected.GetProperty("canonical").GetString(), Encoding.UTF8.GetString(bytes));
            Assert.Equal(expected.GetProperty("bytes").GetInt32(), bytes.Length);
            Assert.Equal(expected.GetProperty("sha256").GetString(), WireJson.Sha256(bytes));
            recovery.Decode(entry.Item2, bytes, entry.Item3);
        }
        var response = golden.GetProperty("response");
        Assert.Equal("5", response.GetProperty("previous").GetProperty("expectedRevision").GetString());
        Assert.Equal("6", response.GetProperty("next").GetProperty("expectedRevision").GetString());
        Assert.Equal("7", response.GetProperty("receipt").GetProperty("revision").GetString());
        foreach (var error in golden.GetProperty("errors").EnumerateArray())
        {
            var bytes = WireJson.EncodeControl(error.GetProperty("body"));
            WireJson.ValidateNamed("ErrorResponse", WireJson.DecodeControl(bytes));
            Assert.Equal(error.GetProperty("wire").GetProperty("sha256").GetString(), WireJson.Sha256(bytes));
        }
    }

    [Fact]
    public void BusinessBackgroundExitPreservesNegativeInt32WithoutChangingControlEncoding()
    {
        var digest = new string('a', 64);
        var value = JsonSerializer.SerializeToElement(new
        {
            contract = "terminal-services-v1",
            action = "query",
            task = new
            {
                identity = new { taskId = "task", operationId = "operation", requestDigest = digest, runtimeInstanceId = "runtime", processInstanceId = "process" },
                artifact = new { artifactId = "output.task", operationId = "operation", requestDigest = digest },
                state = "failed",
                exitCode = int.MinValue,
                signalName = (string?)null,
                totalBytes = "0",
                truncated = false
            }
        });
        TerminalCandidateContract.Validate("BackgroundResponse", value);
        Assert.Throws<WireProtocolException>(() => WireJson.EncodeControl(value));
        Assert.Throws<WireProtocolException>(() => TerminalCandidateContract.Decode("BackgroundResponse", Encoding.UTF8.GetBytes(value.GetRawText())));
    }

    [Theory]
    [InlineData("source/中文")]
    [InlineData("operation.é")]
    public void MemoryIdentifiersUseThePinnedUnicodeDefinition(string value)
        => TerminalCandidateContract.Validate("MemoryIdentifier", JsonSerializer.SerializeToElement(value));

    [Theory]
    [InlineData("")]
    [InlineData("bad\u0000id")]
    [InlineData("bad\u007fid")]
    public void MemoryIdentifiersRejectControls(string value)
        => Assert.Throws<WireProtocolException>(() => TerminalCandidateContract.Validate("MemoryIdentifier", JsonSerializer.SerializeToElement(value)));

    [Fact]
    public void AdditiveRecoveryCopiesFrozenDefinitionsAndHonorsExplicitEnvelopeBudget()
    {
        var recovery = new EmbeddedWireContract("Tansr.Sdk.Protocol.sdk2-archive-recovery-v1.schema.json",
            "f530de1096b4f5d56ea688b7f2cec9ae66deb7d3719db85ab1f48287d3bd7ad4",
            ["ArchiveAckRequest", "RequestIdentity", "Id", "Sequence", "Generations", "LegacyId", "Coverage", "RecordSequence", "Digest", "ArchiveArtifactReceipt", "ArchiveAckFormat", "MutationReceipt"]);
        recovery.Validate("Sequence", JsonSerializer.SerializeToElement("9223372036854775807"));
        Assert.Throws<WireProtocolException>(() => recovery.Validate("Sequence", JsonSerializer.SerializeToElement("9223372036854775808")));
        Assert.Throws<WireProtocolException>(() => recovery.Decode("RequestIdentity", Encoding.UTF8.GetBytes("{\"operationEpoch\":\"epoch\",\"requestId\":\"one\",\"requestId\":\"two\"}")));
        var request = JsonSerializer.SerializeToElement(new { operationEpoch = "epoch", requestId = "request" });
        var bytes = WireJson.EncodeControl(request);
        recovery.Decode("RequestIdentity", bytes, bytes.Length);
        Assert.Equal("payload_too_large", Assert.Throws<WireProtocolException>(() => recovery.Decode("RequestIdentity", bytes, bytes.Length - 1)).Code);
    }

    [Fact]
    public void WrongEmbeddedFingerprintCannotLoad()
        => Assert.Throws<InvalidOperationException>(() => new EmbeddedWireContract(
            "Tansr.Sdk.Terminal.terminal-services-v1.schema.json", new string('0', 64), []));
}
