using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Storage;
using J = Tansr.Sdk.Archive.ArchiveJson;

namespace Tansr.Sdk.Archive;

/// <summary>显式恢复已耐久但修订过期的原 ACK。只恢复档案账，不重新下载、执行工具或运行模型。</summary>
public sealed class ArchiveRecoverySession
{
    private readonly IArchiveRecoveryClient _client;
    private readonly IRecoverableArchiveStore _store;
    private readonly JsonElement _identity;
    private readonly Func<JsonElement> _readContext;
    private int _busy, _poisoned;
    public ArchiveRecoverySession(IArchiveRecoveryClient client, IRecoverableArchiveStore store, JsonElement identity, Func<JsonElement> readContext)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client)); _store = store ?? throw new ArgumentNullException(nameof(store));
        _identity = J.Identity(identity); _readContext = readContext ?? throw new ArgumentNullException(nameof(readContext));
    }

    /// <summary>调用方一次生成恢复键。先查原操作；原回执不可得时才耐久准备并请求服务端证明可恢复。不会据 receipt_expired 推断旧操作未提交。</summary>
    public Task<ArchiveAcknowledgementRecoveryResult> RecoverAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        var fixedRequest = J.Copy(request, "RequestIdentity");
        return Run(async (scope, check) =>
        {
            var prepared = await _store.PendingAckRebaseAsync(cancellationToken).ConfigureAwait(false); check();
            JsonElement previous;
            if (prepared.HasValue)
            {
                var intent = ArchiveRecoveryContract.Request(prepared.Value); J.Need(J.Equal(intent.GetProperty("request"), fixedRequest), "pending_ack"); previous = intent.GetProperty("previous");
            }
            else
            {
                var pending = await _store.PendingAsync(cancellationToken).ConfigureAwait(false); check();
                if (!pending.HasValue) throw new StorageException("pending_ack"); previous = ArchiveRecoveryContract.Ack(pending.Value);
            }
            return await Recover(previous, fixedRequest, scope, check, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>重启／失回后只读取并继续本地已固定的恢复键。没有待办返回 null，不产生替代键。</summary>
    public Task<ArchiveAcknowledgementRecoveryResult?> ResumeAsync(CancellationToken cancellationToken = default)
        => Run<ArchiveAcknowledgementRecoveryResult?>(async (scope, check) =>
        {
            var pending = await _store.PendingAckRebaseAsync(cancellationToken).ConfigureAwait(false); check(); if (!pending.HasValue) return null;
            var intent = ArchiveRecoveryContract.Request(pending.Value);
            return await Recover(intent.GetProperty("previous"), intent.GetProperty("request"), scope, check, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<ArchiveAcknowledgementRecoveryResult> Recover(JsonElement previous, JsonElement request, JsonElement scope, Action check, CancellationToken ct)
    {
        Match(previous); var original = await ReadOriginal(previous, scope, check, ct).ConfigureAwait(false);
        if (original.HasValue) return await ConfirmOriginal(previous, original.Value, check, ct).ConfigureAwait(false);
        // 双副本也经同一个入口补齐两侧意图；一侧 prepare 失回不会生成另一恢复键。
        var intent = ArchiveRecoveryContract.Request(await _store.PrepareAckRebaseAsync(request, ct).ConfigureAwait(false)); check();
        J.Need(J.Equal(intent.GetProperty("previous"), previous) && J.Equal(intent.GetProperty("request"), request)); Match(intent.GetProperty("previous"));
        JsonElement result;
        try { result = await _client.RebaseAckAsync(intent, ct).ConfigureAwait(false); check(); }
        catch (TansrHttpException error) when (error.FamilyStatus == 409 && (error.FamilyCode == "binding_conflict" || error.FamilyCode == "request_id_conflict"))
        {
            check(); original = await ReadOriginal(previous, scope, check, ct).ConfigureAwait(false);
            if (original.HasValue) return await ConfirmOriginal(previous, original.Value, check, ct).ConfigureAwait(false);
            throw;
        }
        var verified = ArchiveRecoveryContract.Receipt(result, intent, scope); check();
        await _store.ConfirmAckRebaseAsync(verified, ct).ConfigureAwait(false); check();
        return new ArchiveAcknowledgementRecoveryResult(ArchiveAcknowledgementRecoveryOutcome.Rebased, previous, verified.GetProperty("receipt"), verified);
    }

    private async Task<JsonElement?> ReadOriginal(JsonElement previous, JsonElement scope, Action check, CancellationToken ct)
    {
        try
        {
            var receipt = await _client.GetOperationAsync(J.Request(J.Text(_identity, "bindingId"), "archive-ack", previous.GetProperty("request")), ct).ConfigureAwait(false);
            check(); J.VerifyOperation(previous, receipt, scope, "archive-ack"); return receipt.Clone();
        }
        // 此错误只意味着原回执不可得。是否真正未受理仍由恢复端点在同一 idle lease 内判定。
        catch (TansrHttpException error) when (error.FamilyStatus == 410 && error.FamilyCode == "receipt_expired") { check(); return null; }
    }

    private async Task<ArchiveAcknowledgementRecoveryResult> ConfirmOriginal(JsonElement previous, JsonElement receipt, Action check, CancellationToken ct)
    {
        await _store.ConfirmAsync(receipt, ct).ConfigureAwait(false); check();
        return new ArchiveAcknowledgementRecoveryResult(ArchiveAcknowledgementRecoveryOutcome.OriginalConfirmed, previous, receipt, null);
    }

    private void Match(JsonElement ack) => J.Need(J.Text(ack, "bindingId") == J.Text(_identity, "bindingId") && J.Text(ack, "sourceId") == J.Text(_identity, "sourceId") &&
        J.Text(ack, "sourceGeneration") == J.Text(_identity, "sourceGeneration") && J.Equal(ack.GetProperty("generations"), _identity.GetProperty("target").GetProperty("generations")), "context_changed");
    private async Task<T> Run<T>(Func<JsonElement, Action, Task<T>> work, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Exchange(ref _poisoned, 1); throw new StorageException("reentrant"); }
        Interlocked.Exchange(ref _poisoned, 0);
        try
        {
            ct.ThrowIfCancellationRequested(); var scope = J.Scope(_readContext, _identity);
            void Check() { ct.ThrowIfCancellationRequested(); J.Need(Volatile.Read(ref _poisoned) == 0, "reentrant"); J.Need(J.Equal(scope, J.Scope(_readContext, _identity)) && J.Equal(scope, _client.ReadScope()), "context_changed"); J.Need(Volatile.Read(ref _poisoned) == 0, "reentrant"); }
            Check(); var value = await work(scope, Check).ConfigureAwait(false); Check(); return value;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}

public enum ArchiveAcknowledgementRecoveryOutcome { Rebased, OriginalConfirmed }

public sealed class ArchiveAcknowledgementRecoveryResult
{
    public ArchiveAcknowledgementRecoveryOutcome Outcome { get; }
    public JsonElement OriginalAcknowledgement { get; }
    public JsonElement Receipt { get; }
    public JsonElement? RecoveryReceipt { get; }
    internal ArchiveAcknowledgementRecoveryResult(ArchiveAcknowledgementRecoveryOutcome outcome, JsonElement original, JsonElement receipt, JsonElement? recovery)
    { Outcome = outcome; OriginalAcknowledgement = original.Clone(); Receipt = receipt.Clone(); RecoveryReceipt = recovery?.Clone(); }
}
