using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Storage;

/// <summary>在受信适配器外校验原身份、原键及等待后的当前授权。此守卫不提供或伪造共同权威。</summary>
public sealed class TerminalPersistenceCustodyAuthorityGuard : ITerminalPersistenceCustodyAuthority
{
    private readonly ITerminalPersistenceCustodyAuthority authority;
    private readonly Func<JsonElement> scopeProvider;

    public TerminalPersistenceCustodyAuthorityGuard(ITerminalPersistenceCustodyAuthority authority, Func<JsonElement> scopeProvider)
    {
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
    }

    private Action Capture(JsonElement identity, CancellationToken cancellationToken)
    {
        var scope = Scope();
        Need(Text(identity, "applicationScopeId") == Text(scope, "applicationScopeId") &&
            Text(identity, "endUserId") == Text(scope, "endUserId"));
        return () => { cancellationToken.ThrowIfCancellationRequested(); Need(Equal(scope, Scope())); };
    }
    private JsonElement Scope()
    {
        var scope = scopeProvider().Clone(); WireJson.ValidateNamed("Scope", scope); return scope;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        request = PersistenceCustodyContract.Copy("Request", request);
        // 认领及收尾只能经过 lease；prepare 先持有整个源域围栏，再读取固定目录。
        string action = Text(request, "action");
        Need(action != "prepare" && (!action.StartsWith("writer-", StringComparison.Ordinal) || action == "writer-query"));
        var current = Capture(request.GetProperty("identity"), cancellationToken); current();
        var result = await authority.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        current(); result = PersistenceCustodyContract.Copy("Response", result);
        // 原 claim 可以属于迁移前 custody；只读历史事实不能拿来授予当前执行权。
        PersistenceCustodyContract.ValidateResponse(request, result); return result;
    }

    public async Task<ITerminalPersistenceWriterLease> AcquireWriterAsync(JsonElement identity, JsonElement custody,
        JsonElement originalPstRequest, CancellationToken cancellationToken = default)
    {
        identity = PersistenceCustodyContract.Copy("Identity", identity);
        custody = PersistenceCustodyContract.Copy("Custody", custody);
        originalPstRequest = TerminalPersistenceContract.Copy("Request", originalPstRequest);
        foreach (string name in new[] { "sourceId", "sourceGeneration", "domainKey" })
            Need(Equal(identity.GetProperty(name), originalPstRequest.GetProperty(name)));
        string action = Text(originalPstRequest, "action"); Need(action == "begin" || action == "put" || action == "commit");
        var current = Capture(identity, cancellationToken); current();
        var lease = await authority.AcquireWriterAsync(identity, custody, originalPstRequest, cancellationToken).ConfigureAwait(false);
        current();
        if (lease == null) throw new WireProtocolException("custody_writer_unavailable");
        lease.AssertCurrent(); current();
        var claim = PersistenceCustodyContract.Copy("WriterClaim", lease.Claim);
        Need(Text(claim, "operationId") == PersistenceCustodyContract.WriterOperationId(originalPstRequest) &&
            Text(claim, "intentSha256") == PersistenceCustodyContract.Digest(originalPstRequest) &&
            Text(claim, "status") == "active" && claim.GetProperty("responseDigest").ValueKind == JsonValueKind.Null &&
            Equal(claim.GetProperty("selectedCustody"), custody));
        return new WriterLease(lease, current, claim);
    }

    public async Task<ITerminalPersistenceHandoffLease> BeginHandoffAsync(JsonElement prepareRequest,
        CancellationToken cancellationToken = default)
    {
        prepareRequest = PersistenceCustodyContract.Copy("PrepareRequest", prepareRequest);
        Need(Text(prepareRequest, "intentSha256") == PersistenceCustodyContract.PrepareIntentDigest(prepareRequest));
        var current = Capture(prepareRequest.GetProperty("identity"), cancellationToken); current();
        var lease = await authority.BeginHandoffAsync(prepareRequest, cancellationToken).ConfigureAwait(false);
        current();
        if (lease == null) throw new WireProtocolException("custody_handoff_unavailable");
        lease.AssertCurrent(); current(); return new HandoffLease(lease, current, prepareRequest);
    }

    private sealed class WriterLease(ITerminalPersistenceWriterLease lease, Action current, JsonElement claim) : ITerminalPersistenceWriterLease
    {
        private readonly object gate = new();
        private bool finishing;
        // 关闭本实例只阻止重新执行；失回后共同权威仍保留原 active／unknown，必须另行查询。
        public JsonElement Claim => claim.Clone();
        public void AssertCurrent()
        {
            lock (gate) { if (finishing) throw new WireProtocolException("custody_writer_released"); }
            current(); lease.AssertCurrent(); current();
        }
        public async Task<JsonElement> FinishAsync(JsonElement settlement, CancellationToken cancellationToken = default)
        {
            settlement = PersistenceCustodyContract.Copy("WriterSettlement", settlement);
            cancellationToken.ThrowIfCancellationRequested(); AssertCurrent();
            lock (gate) { if (finishing) throw new WireProtocolException("custody_writer_released"); finishing = true; }
            var result = await lease.FinishAsync(settlement, cancellationToken).ConfigureAwait(false);
            current(); cancellationToken.ThrowIfCancellationRequested();
            result = PersistenceCustodyContract.Copy("WriterClaim", result);
            foreach (string name in new[] { "operationId", "intentSha256", "claimId", "writerFence", "selectedCustody" })
                Need(Equal(claim.GetProperty(name), result.GetProperty(name)));
            Need(Text(result, "status") == "settled");
            Need(Text(settlement, "state") == "committed"
                ? Equal(settlement.GetProperty("responseDigest"), result.GetProperty("responseDigest"))
                : result.GetProperty("responseDigest").ValueKind == JsonValueKind.Null);
            return result;
        }
    }

    private sealed class HandoffLease(ITerminalPersistenceHandoffLease lease, Action current, JsonElement request) : ITerminalPersistenceHandoffLease
    {
        public void AssertCurrent() { current(); lease.AssertCurrent(); current(); }
        public async Task<JsonElement> RecordPreparedAsync(JsonElement proof, CancellationToken cancellationToken = default)
        {
            proof = PersistenceCustodyContract.Copy("RootProof", proof);
            cancellationToken.ThrowIfCancellationRequested(); AssertCurrent();
            var ticket = await lease.RecordPreparedAsync(proof, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); AssertCurrent();
            ticket = PersistenceCustodyContract.Copy("Ticket", ticket);
            foreach (string name in new[] { "identity", "operationId", "intentSha256", "source", "destination" })
                Need(Equal(ticket.GetProperty(name), request.GetProperty(name)));
            Need(Text(ticket, "phase") == "prepared" && Equal(proof, ticket.GetProperty("proof")) &&
                Equal(request.GetProperty("source"), ticket.GetProperty("selectedCustody")));
            Need(System.Numerics.BigInteger.Parse(Text(ticket, "writerFence"), System.Globalization.CultureInfo.InvariantCulture) ==
                System.Numerics.BigInteger.Parse(Text(request, "expectedWriterFence"), System.Globalization.CultureInfo.InvariantCulture) + 1);
            return ticket;
        }
    }
    private static string Text(JsonElement value, string name) => PersistenceCustodyContract.Text(value, name);
    private static bool Equal(JsonElement a, JsonElement b) => PersistenceCustodyContract.Equal(a, b);
    private static void Need(bool condition) { if (!condition) throw new WireProtocolException("custody_binding_mismatch"); }
}
