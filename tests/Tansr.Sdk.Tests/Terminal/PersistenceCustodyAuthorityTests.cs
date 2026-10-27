using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class PersistenceCustodyAuthorityTests
{
    private static readonly string Hash = new('a', 64);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Identity(string user = "user") => Json(new
    { applicationScopeId = "app", endUserId = user, sourceId = "memory", sourceGeneration = "1", domainKey = "domain" });
    private static JsonElement Scope(string revision = "1") => Json(new
    { applicationScopeId = "app", endUserId = "user", authorizationRevision = revision });
    private static JsonElement Custody() => Json(new { custodyId = "terminal", custodyRevision = "1", mode = "terminal" });
    private static JsonElement Pst() => Json(new
    { contract = "terminal-persistence-v1", sourceId = "memory", sourceGeneration = "1", domainKey = "domain", action = "commit", transferId = "transfer", intentSha256 = Hash });
    private static JsonElement Change(JsonElement value, string name, object? replacement)
    {
        var fields = value.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()); fields[name] = replacement; return Json(fields);
    }
    private static JsonElement Claim(JsonElement request) => Json(new
    {
        operationId = PersistenceCustodyContract.WriterOperationId(request),
        intentSha256 = PersistenceCustodyContract.Digest(request),
        claimId = "claim",
        writerFence = "2",
        status = "active",
        selectedCustody = Custody(),
        responseDigest = (string?)null
    });
    private static JsonElement Prepare()
    {
        var value = Json(new
        {
            contract = PersistenceCustodyContract.Protocol,
            identity = Identity(),
            operationId = "move",
            intentSha256 = Hash,
            action = "prepare",
            source = Custody(),
            destination = new { custodyId = "cloud", mode = "cloud" },
            expectedWriterFence = "2",
            expectedRoot = (string?)null
        });
        return Change(value, "intentSha256", PersistenceCustodyContract.PrepareIntentDigest(value));
    }
    private static JsonElement Proof() => Json(new
    { originFormat = "terminal-persistence-v1", fixedRoot = Hash, originalCommitRoot = (string?)null, indexRoot = (string?)null, indexCount = "0", inventoryDigest = Hash, records = 1, objects = 1, bytes = 1 });

    private sealed class Authority : ITerminalPersistenceCustodyAuthority
    {
        internal int Acquires, Begins, Executes;
        internal Action? AfterAcquire { get; set; }
        internal Action? AfterBegin { get; set; }
        internal Action? AfterExecute { get; set; }
        internal JsonElement? ExecuteResult { get; set; }
        internal Writer Writer = new(); internal Handoff Handoff = new();
        public Task<ITerminalPersistenceWriterLease> AcquireWriterAsync(JsonElement identity, JsonElement custody, JsonElement originalPstRequest, CancellationToken cancellationToken = default)
        { Acquires++; Writer.Original ??= Claim(originalPstRequest); AfterAcquire?.Invoke(); return Task.FromResult<ITerminalPersistenceWriterLease>(Writer); }
        public Task<ITerminalPersistenceHandoffLease> BeginHandoffAsync(JsonElement prepareRequest, CancellationToken cancellationToken = default)
        { Begins++; Handoff.Prepare = prepareRequest; AfterBegin?.Invoke(); return Task.FromResult<ITerminalPersistenceHandoffLease>(Handoff); }
        public Task<JsonElement> ExecuteAsync(JsonElement request, CancellationToken cancellationToken = default)
        { Executes++; AfterExecute?.Invoke(); return Task.FromResult(ExecuteResult ?? Change(Change(request, "preparing", true), "ticket", null)); }
    }
    private sealed class Writer : ITerminalPersistenceWriterLease
    {
        internal JsonElement? Original;
        internal Action? Current, AfterFinish;
        internal int Finishes;
        internal bool LoseFinish;
        internal string? AlterClaimId;
        public JsonElement Claim => Original!.Value;
        public void AssertCurrent() => Current?.Invoke();
        public Task<JsonElement> FinishAsync(JsonElement settlement, CancellationToken cancellationToken = default)
        {
            Finishes++; AfterFinish?.Invoke(); if (LoseFinish) throw new IOException("synthetic committed response loss");
            var result = Change(Change(Claim, "status", "settled"), "responseDigest", settlement.TryGetProperty("responseDigest", out var digest) ? digest : null);
            if (AlterClaimId != null) result = Change(result, "claimId", AlterClaimId); return Task.FromResult(result);
        }
    }
    private sealed class Handoff : ITerminalPersistenceHandoffLease
    {
        internal JsonElement Prepare;
        internal Action? AfterRecord;
        internal int Records;
        public void AssertCurrent() { }
        public Task<JsonElement> RecordPreparedAsync(JsonElement proof, CancellationToken cancellationToken = default)
        {
            Records++; AfterRecord?.Invoke(); return Task.FromResult(Json(new
            {
                identity = Prepare.GetProperty("identity"),
                operationId = Prepare.GetProperty("operationId"),
                intentSha256 = Prepare.GetProperty("intentSha256"),
                phase = "prepared",
                source = Prepare.GetProperty("source"),
                destination = Prepare.GetProperty("destination"),
                writerFence = "3",
                proof,
                selectedCustody = Prepare.GetProperty("source")
            }));
        }
    }

    [Fact]
    public async Task FreshClaimPreservesOriginalKeyAndOnlyConfirmedCommitSettles()
    {
        var authority = new Authority(); var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope());
        var lease = await guard.AcquireWriterAsync(Identity(), Custody(), Pst()); lease.AssertCurrent();
        var result = await lease.FinishAsync(Json(new { state = "committed", responseDigest = Hash }));
        Assert.Equal("settled", result.GetProperty("status").GetString()); Assert.Equal(1, authority.Writer.Finishes);
        Assert.Throws<WireProtocolException>(lease.AssertCurrent);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("settled")]
    public async Task OldQueryFactsDoNotGrantWriterExecution(string status)
    {
        var authority = new Authority(); authority.Writer.Original = Change(Claim(Pst()), "status", status);
        var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope());
        await Assert.ThrowsAsync<WireProtocolException>(() => guard.AcquireWriterAsync(Identity(), Custody(), Pst()));
        Assert.Equal(0, authority.Writer.Finishes);
    }

    [Fact]
    public async Task CrossUserOrDifferentDomainRejectsBeforeAuthority()
    {
        var authority = new Authority(); var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope());
        await Assert.ThrowsAsync<WireProtocolException>(() => guard.AcquireWriterAsync(Identity("other"), Custody(), Pst()));
        await Assert.ThrowsAsync<WireProtocolException>(() => guard.AcquireWriterAsync(Change(Identity(), "domainKey", "other"), Custody(), Pst()));
        Assert.Equal(0, authority.Acquires);
    }

    [Fact]
    public async Task AuthorizationChangesWhileClaimWaitsNeverReleaseOrExecuteIt()
    {
        string revision = "1"; var authority = new Authority { AfterAcquire = () => revision = "2" };
        var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope(revision));
        await Assert.ThrowsAsync<WireProtocolException>(() => guard.AcquireWriterAsync(Identity(), Custody(), Pst()));
        Assert.Equal(0, authority.Writer.Finishes);
    }

    [Fact]
    public async Task FinalLocalCommitGuardChecksCurrentLeaseAndScope()
    {
        bool revoked = false; var authority = new Authority(); authority.Writer.Current = () => { if (revoked) throw new InvalidOperationException("revoked"); };
        var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope());
        var lease = await guard.AcquireWriterAsync(Identity(), Custody(), Pst()); revoked = true;
        Assert.Throws<InvalidOperationException>(lease.AssertCurrent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.FinishAsync(Json(new { state = "rolled-back" })));
        Assert.Equal(0, authority.Writer.Finishes);
    }

    [Fact]
    public async Task LostFinishNeverRetriesNorInventsRollback()
    {
        var authority = new Authority(); authority.Writer.LoseFinish = true;
        var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope());
        var lease = await guard.AcquireWriterAsync(Identity(), Custody(), Pst());
        await Assert.ThrowsAsync<IOException>(() => lease.FinishAsync(Json(new { state = "committed", responseDigest = Hash })));
        await Assert.ThrowsAsync<WireProtocolException>(() => lease.FinishAsync(Json(new { state = "rolled-back" })));
        Assert.Equal(1, authority.Writer.Finishes);
    }

    [Fact]
    public async Task FinishRejectsAnotherOriginalClaimAndPostAwaitRevocation()
    {
        string revision = "1"; var authority = new Authority(); authority.Writer.AlterClaimId = "another";
        var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope(revision));
        var lease = await guard.AcquireWriterAsync(Identity(), Custody(), Pst());
        await Assert.ThrowsAsync<WireProtocolException>(() => lease.FinishAsync(Json(new { state = "rolled-back" })));
        authority = new Authority(); authority.Writer.AfterFinish = () => revision = "2";
        guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope(revision));
        lease = await guard.AcquireWriterAsync(Identity(), Custody(), Pst());
        await Assert.ThrowsAsync<WireProtocolException>(() => lease.FinishAsync(Json(new { state = "rolled-back" })));
    }

    [Fact]
    public async Task PrepareBindsExactOriginalProofAndAdvancesOnlyTheWriterFence()
    {
        var authority = new Authority(); var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope());
        var lease = await guard.BeginHandoffAsync(Prepare()); var ticket = await lease.RecordPreparedAsync(Proof());
        Assert.Equal("prepared", ticket.GetProperty("phase").GetString()); Assert.Equal("terminal", ticket.GetProperty("selectedCustody").GetProperty("custodyId").GetString());
        Assert.Equal(1, authority.Begins); Assert.Equal(1, authority.Handoff.Records);
    }

    [Fact]
    public async Task PrepareCannotAcceptChangedIntentOrAuthorizationAfterSourceRead()
    {
        string revision = "1"; var authority = new Authority(); authority.Handoff.AfterRecord = () => revision = "2";
        var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope(revision));
        await Assert.ThrowsAsync<WireProtocolException>(() => guard.BeginHandoffAsync(Change(Prepare(), "expectedWriterFence", "9")));
        Assert.Equal(0, authority.Begins);
        var lease = await guard.BeginHandoffAsync(Prepare());
        await Assert.ThrowsAsync<WireProtocolException>(() => lease.RecordPreparedAsync(Proof()));
    }

    [Fact]
    public async Task QueryKeepsPreparingDistinctFromMissingTicket()
    {
        var authority = new Authority(); var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope());
        var request = Json(new { contract = PersistenceCustodyContract.Protocol, identity = Identity(), operationId = "move", intentSha256 = Hash, action = "query" });
        var response = await guard.ExecuteAsync(request); Assert.True(response.GetProperty("preparing").GetBoolean()); Assert.Equal(JsonValueKind.Null, response.GetProperty("ticket").ValueKind);
        await Assert.ThrowsAsync<WireProtocolException>(() => guard.ExecuteAsync(Prepare())); Assert.Equal(1, authority.Executes);
    }

    [Fact]
    public async Task OriginalWriterQuerySupportsSettledReplayWithoutGrantingExecution()
    {
        var request = Json(new
        {
            contract = PersistenceCustodyContract.Protocol,
            identity = Identity(),
            operationId = PersistenceCustodyContract.WriterOperationId(Pst()),
            intentSha256 = PersistenceCustodyContract.Digest(Pst()),
            action = "writer-query",
            custody = Custody()
        });
        var claim = Change(Change(Claim(Pst()), "status", "settled"), "responseDigest", Hash);
        JsonElement Response(JsonElement fact) => Json(new
        {
            contract = PersistenceCustodyContract.Protocol,
            identity = Identity(),
            operationId = request.GetProperty("operationId"),
            intentSha256 = request.GetProperty("intentSha256"),
            action = "writer-query",
            claim = fact
        });
        var authority = new Authority { ExecuteResult = Response(claim) };
        var guard = new TerminalPersistenceCustodyAuthorityGuard(authority, () => Scope());
        var result = await guard.ExecuteAsync(request); Assert.Equal("settled", result.GetProperty("claim").GetProperty("status").GetString());
        Assert.Equal(0, authority.Acquires); Assert.Equal(0, authority.Writer.Finishes);
        authority.ExecuteResult = Response(Change(claim, "selectedCustody", Change(Custody(), "custodyId", "old-source")));
        Assert.Equal("old-source", (await guard.ExecuteAsync(request)).GetProperty("claim").GetProperty("selectedCustody").GetProperty("custodyId").GetString());
        authority.ExecuteResult = Response(Change(claim, "intentSha256", new string('b', 64)));
        await Assert.ThrowsAsync<WireProtocolException>(() => guard.ExecuteAsync(request));
    }
}
