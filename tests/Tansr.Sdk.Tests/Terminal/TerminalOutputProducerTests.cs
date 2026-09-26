using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalOutputProducerTests
{
    [Fact]
    public async Task CaptureInterleavedUtf8UsesSharedRawSealAndNeverClaimsMemoryDurability()
    {
        var adapter = new TerminalTestAdapter(); var bound = await adapter.BindAsync();
        using var producer = bound.Client.CreateOutputProducer(bound.Binding, adapter.ExistingOperation);
        var golden = TerminalCandidateContractTests.Goldens().GetProperty("semantic").GetProperty("interleavedUtf8");
        foreach (var block in golden.GetProperty("blocks").EnumerateArray())
            Assert.True(producer.TryAppend(TerminalJson.Text(block, "channel"), "utf-8", WireJson.DecodeBase64(TerminalJson.Text(block, "base64"))));
        var status = await producer.CompleteAsync(false);
        Assert.Null(status.DurableThrough); Assert.Equal("complete", status.State);
        Assert.Equal(WireJson.CanonicalString(golden.GetProperty("seal")), WireJson.CanonicalString(status.Seal!.Value));
        Assert.Equal(golden.GetProperty("blocks").EnumerateArray().Select(x => WireJson.CanonicalString(x)), adapter.Received.Select(x => WireJson.CanonicalString(x)));
        Assert.Equal(5, producer.CapturedBytes); Assert.False(producer.IsTruncated);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostResponseRequiresOriginalOperationReadBeforeAnyPostRetry(bool committed)
    {
        var adapter = new TerminalTestAdapter { CommitThenThrow = committed, FailBeforeCommit = !committed };
        var bound = await adapter.BindAsync();
        using var producer = bound.Client.CreateOutputProducer(bound.Binding, adapter.ExistingOperation);
        producer.TryAppend("stdout", "binary", [1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => producer.FlushAsync());
        Assert.True(producer.RequiresReconciliation);
        Assert.Equal("commit_unknown", (await Assert.ThrowsAsync<WireProtocolException>(() => producer.FlushAsync())).Code);
        Assert.Single(adapter.Sent);
        await producer.ReconcileAsync(); Assert.Equal(1, adapter.StatusReads);
        await producer.CompleteAsync(false);
        Assert.False(producer.RequiresReconciliation); Assert.Single(adapter.Received);
        if (!committed) Assert.Equal(WireJson.CanonicalString(adapter.Sent[0]), WireJson.CanonicalString(adapter.Sent[1]));
        else Assert.Empty(adapter.Sent[1].GetProperty("blocks").EnumerateArray());
    }
    [Fact]
    public async Task FullPendingQueueStopsCapturePermanentlyWithoutAdvancingDroppedWatermarks()
    {
        var adapter = new TerminalTestAdapter();
        var limits = adapter.Capabilities["limits"]!.AsObject();
        limits["maxBlockBytes"] = 4; limits["maxBatchBytes"] = 8; limits["maxPendingBytes"] = 8; limits["maxRetainedBytes"] = 16;
        var bound = await adapter.BindAsync();
        using var producer = bound.Client.CreateOutputProducer(bound.Binding, adapter.ExistingOperation);
        Assert.True(producer.TryAppend("stdout", "binary", [1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.False(producer.TryAppend("stderr", "binary", [9]));
        await producer.FlushAsync();
        Assert.False(producer.TryAppend("stderr", "binary", [10]));
        var status = await producer.CompleteAsync(false);
        Assert.Equal("truncated", status.State); Assert.Equal(1, status.AcceptedThrough); Assert.Equal(8, status.NextByteOffset);
        Assert.Equal(2, producer.DiscardedBytes); Assert.Equal(8, producer.CapturedBytes); Assert.Equal(2, adapter.Received.Count);
    }
    [Fact]
    public async Task UnknownWindowNeverBecomesOffsetZeroOrPermissionToResend()
    {
        var adapter = new TerminalTestAdapter { CommitThenThrow = true }; var bound = await adapter.BindAsync();
        using var producer = bound.Client.CreateOutputProducer(bound.Binding, adapter.ExistingOperation);
        producer.TryAppend("stdout", "binary", [7]); await Assert.ThrowsAsync<IOException>(() => producer.FlushAsync());
        adapter.Unavailable = true;
        var status = await producer.ReconcileAsync(); Assert.Null(status.NextByteOffset); Assert.True(producer.RequiresReconciliation);
        Assert.Equal("source_unavailable", (await Assert.ThrowsAsync<WireProtocolException>(() => producer.FlushAsync())).Code);
        Assert.Single(adapter.Sent); Assert.Equal(1, producer.CapturedBytes);
    }
    [Fact]
    public async Task IncorrectAcknowledgmentDoesNotReleaseOrAdvanceOriginalBytes()
    {
        var adapter = new TerminalTestAdapter { AlterResponse = value => { var node = TerminalTestAdapter.Node(value); node["nextByteOffset"] = "2"; return TerminalTestAdapter.Element(node); } };
        var bound = await adapter.BindAsync(); using var producer = bound.Client.CreateOutputProducer(bound.Binding, adapter.ExistingOperation);
        producer.TryAppend("stdout", "binary", [7]);
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<WireProtocolException>(() => producer.FlushAsync())).Code);
        Assert.True(producer.RequiresReconciliation); Assert.Equal(1, producer.CapturedBytes);
    }
    [Fact]
    public async Task ChangedScopeAndForeignOperationFailBeforeOutputCanBeSent()
    {
        var adapter = new TerminalTestAdapter(); var bound = await adapter.BindAsync();
        Assert.Equal("binding_conflict", Assert.Throws<WireProtocolException>(() => bound.Client.CreateOutputProducer(bound.Binding, ExecutionFixture.Operation(user: "foreign"))).Code);
        using var producer = bound.Client.CreateOutputProducer(bound.Binding, adapter.ExistingOperation);
        adapter.Scope = ExecutionFixture.Scope(revision: "2");
        Assert.Equal("context_changed", Assert.Throws<WireProtocolException>(() => producer.TryAppend("stdout", "binary", [1])).Code);
        Assert.Empty(adapter.Sent);
    }
    [Theory]
    [InlineData("authorization", "denied", "forbidden")]
    [InlineData("authorization", "unconfirmed", "capability_unconfirmed")]
    [InlineData("device", "unavailable", "source_unavailable")]
    [InlineData("device", "unconfirmed", "capability_unconfirmed")]
    public async Task InstalledFeatureFailureIsNotAnOptionalFallback(string field, string value, string code)
    {
        var adapter = new TerminalTestAdapter { BindingFailure = code }; adapter.Capabilities["features"]![0]![field] = value;
        Assert.Equal(code, (await Assert.ThrowsAsync<WireProtocolException>(() => adapter.BindAsync())).Code);
        Assert.Empty(adapter.Sent);
    }
    [Fact]
    public async Task DiscoveryUnconfirmedDoesNotReplaceTrustedBindingDecision()
    {
        var adapter = new TerminalTestAdapter();
        adapter.Capabilities["features"]![0]!["authorization"] = "unconfirmed";
        adapter.Capabilities["features"]![0]!["device"] = "unconfirmed";
        var bound = await adapter.BindAsync();
        Assert.Equal("tool-output", bound.Binding.Raw.GetProperty("outputAuthority").GetString());
    }
    [Fact]
    public async Task CandidateHashMismatchCannotEnterBinding()
    {
        var adapter = new TerminalTestAdapter(); adapter.Capabilities["schemaSha256"] = new string('0', 64);
        Assert.Equal("protocol_version_mismatch", (await Assert.ThrowsAsync<WireProtocolException>(() => adapter.BindAsync())).Code);
    }
}
