using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalOutputObserverTests
{
    private static JsonElement Operation => JsonSerializer.SerializeToElement(new { operationId = "operation-1", requestDigest = new string('b', 64) });
    private static JsonElement Golden => TerminalCandidateContractTests.Goldens().GetProperty("semantic").GetProperty("interleavedUtf8");
    private static JsonElement Status(string state = "complete", long? retainedFrom = 0) => TerminalJson.Object(w =>
    {
        w.WriteString("contract", TerminalCandidateContract.Protocol); TerminalJson.Field(w, "operation", Operation); w.WriteString("state", state);
        w.WriteString("acceptedThrough", "2"); w.WriteNull("durableThrough"); TerminalJson.Decimal(w, "retainedFrom", retainedFrom);
        w.WriteString("nextByteOffset", "5"); TerminalJson.Field(w, "seal", Golden.GetProperty("seal"));
    });
    [Fact]
    public void IndependentChannelDecodersSurviveReconnectAndVerifyGlobalRawSeal()
    {
        using var observer = new TerminalOutputObserver(Operation);
        var blocks = Golden.GetProperty("blocks");
        Assert.Equal("", Assert.Single(observer.ApplyBlock(blocks[0])).Text);
        // Keeping this observer across reconnect preserves stdout's two pending bytes.
        Assert.Equal("x", Assert.Single(observer.ApplyBlock(blocks[1])).Text);
        Assert.Equal("😀", Assert.Single(observer.ApplyBlock(blocks[2])).Text);
        Assert.Empty(observer.ApplyStatus(Status())); Assert.True(observer.SealVerified); Assert.False(observer.HasPresentationGap);
        Assert.Empty(observer.ApplyBlock(blocks[2])); Assert.Equal(2, observer.LastSequence);
    }
    [Fact]
    public void WindowBeginningInsideScalarPreservesRawBytesAndDisplaysAnExplicitGap()
    {
        using var observer = new TerminalOutputObserver(Operation);
        var block = Golden.GetProperty("blocks")[2]; var piece = Assert.Single(observer.ApplyBlock(block));
        Assert.Equal(new byte[] { 0x98, 0x80 }, piece.Bytes); Assert.Contains("�", piece.Text!);
        Assert.True(piece.PresentationGap); observer.ApplyStatus(Status("gap", 2)); Assert.False(observer.SealVerified);
    }
    [Fact]
    public void LostDecoderStateCannotReconstructMissingUtf8PrefixOrClaimFullSeal()
    {
        using var observer = new TerminalOutputObserver(Operation); var blocks = Golden.GetProperty("blocks");
        observer.ApplyBlock(blocks[0]); observer.NoticeGap(); observer.ApplyBlock(blocks[1]);
        Assert.Contains("�", Assert.Single(observer.ApplyBlock(blocks[2])).Text!);
        observer.ApplyStatus(Status()); Assert.True(observer.HasPresentationGap); Assert.False(observer.SealVerified);
    }
    [Fact]
    public void SameSequenceMustMatchEveryFieldAndEncodingCannotChangeBetweenChunks()
    {
        using var observer = new TerminalOutputObserver(Operation); var blocks = Golden.GetProperty("blocks");
        observer.ApplyBlock(blocks[0]);
        var duplicate = TerminalTestAdapter.Node(blocks[0]); duplicate["channel"] = "stderr";
        Assert.Equal("revision_conflict", Assert.Throws<WireProtocolException>(() => observer.ApplyBlock(TerminalTestAdapter.Element(duplicate))).Code);
        observer.ApplyBlock(blocks[1]);
        var changed = TerminalTestAdapter.Node(blocks[2]); changed["encoding"] = "binary";
        Assert.Equal("revision_conflict", Assert.Throws<WireProtocolException>(() => observer.ApplyBlock(TerminalTestAdapter.Element(changed))).Code);
        Assert.Equal(1, observer.LastSequence);
    }
    [Fact]
    public void CorruptBlockAndSealAreRejectedInsteadOfLookingComplete()
    {
        using var observer = new TerminalOutputObserver(Operation); var blocks = Golden.GetProperty("blocks");
        var corrupt = TerminalTestAdapter.Node(blocks[0]); corrupt["payloadDigest"] = new string('0', 64);
        Assert.Equal("integrity_mismatch", Assert.Throws<WireProtocolException>(() => observer.ApplyBlock(TerminalTestAdapter.Element(corrupt))).Code);
        Assert.Null(observer.LastSequence);
        foreach (var block in blocks.EnumerateArray()) observer.ApplyBlock(block);
        var status = TerminalTestAdapter.Node(Status()); status["seal"]!["payloadDigest"] = new string('0', 64);
        Assert.Equal("integrity_mismatch", Assert.Throws<WireProtocolException>(() => observer.ApplyStatus(TerminalTestAdapter.Element(status))).Code);
        Assert.False(observer.SealVerified);
    }
    [Fact]
    public void ExecutorReplayCursorDoesNotBecomeOutputOrDurableWatermark()
    {
        var cursor = new TerminalExecutorEventCursor("executor-1", "connection-1");
        var notification = TerminalJson.Object(w =>
        {
            w.WriteString("contract", TerminalCandidateContract.Protocol); w.WriteString("eventId", "9007199254740993");
            w.WriteString("type", "reconcile-required"); w.WriteString("executorId", "executor-1"); w.WriteString("connectionId", "connection-1"); w.WriteNull("operation");
        });
        Assert.True(cursor.Apply(notification)); Assert.Equal(9007199254740993L, cursor.LastEventId);
        Assert.True(cursor.RequiresReconciliation); Assert.True(cursor.Apply(notification));
    }
    [Fact]
    public void ExplicitReconciliationResetsAnEqualOrLowerNotificationWindow()
    {
        var cursor = new TerminalExecutorEventCursor("executor-1", "connection-1");
        JsonElement Event(string type, string id) => TerminalJson.Object(w =>
        {
            w.WriteString("contract", TerminalCandidateContract.Protocol); w.WriteString("eventId", id);
            w.WriteString("type", type); w.WriteString("executorId", "executor-1"); w.WriteString("connectionId", "connection-1"); w.WriteNull("operation");
        });
        Assert.True(cursor.Apply(Event("operations-available", "9007199254740993")));
        Assert.True(cursor.Apply(Event("reconcile-required", "9007199254740993")));
        Assert.True(cursor.RequiresReconciliation); cursor.ClearReconciliation();
        Assert.True(cursor.Apply(Event("reconcile-required", "0")));
        Assert.True(cursor.RequiresReconciliation); Assert.Equal(0, cursor.LastEventId);
        cursor.ClearReconciliation();
        Assert.True(cursor.Apply(Event("operations-available", "1")));
        Assert.False(cursor.RequiresReconciliation);
        Assert.False(cursor.Apply(Event("operations-available", "1")));
    }
}
