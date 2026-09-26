using System.Text.Json;
using Tansr.Sdk.Storage;
using A = Tansr.Sdk.Archive.Replication.ArchiveReceiverValidation;
using R = Tansr.Sdk.Archive.ArchiveRecoveryContract;
using S = Tansr.Sdk.Archive.Replication.ArchiveSyncValidation;

namespace Tansr.Sdk.Archive.Replication;

public sealed partial class ReplicatedArchiveStore
{
    /// <summary>两侧都必须为显式新版恢复介质。第一侧已准备而第二侧失败时，保留同一键继续补齐。</summary>
    public Task<JsonElement> PrepareAckRebaseAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        Preflight(); var fixedRequest = A.Copy(request, "RequestIdentity"); var stores = RecoveryStores();
        return Run(async check =>
        {
            var pending = await RebaseIntent(stores, check, cancellationToken).ConfigureAwait(false);
            if (pending.HasValue) A.Need(S.Equal(pending.Value.GetProperty("request"), fixedRequest), "pending_ack");
            else
            {
                var a = await Facts(_stores[0], check, cancellationToken).ConfigureAwait(false); var b = await Facts(_stores[1], check, cancellationToken).ConfigureAwait(false);
                A.Need(Same(a.Head, b.Head) && Same(a.Pending, b.Pending), "reconciliation_required");
                if (a.Pending.HasValue)
                    foreach (var store in _stores)
                    {
                        var original = await Operation(store, a.Pending.Value, check, cancellationToken).ConfigureAwait(false);
                        A.Need(original.GetProperty("receipt").ValueKind == JsonValueKind.Null, "reconciliation_required");
                        await VerifyStored(store, a.Pending.Value, check, cancellationToken).ConfigureAwait(false);
                    }
            }
            JsonElement? result = null;
            foreach (var store in stores)
            {
                var intent = R.Request(await store.PrepareAckRebaseAsync(fixedRequest, cancellationToken).ConfigureAwait(false)); check(); MatchRecovery(intent);
                A.Need(S.Equal(intent.GetProperty("request"), fixedRequest) && (!result.HasValue || S.Equal(result.Value, intent)), "receipt_mismatch"); result = intent;
            }
            return result!.Value;
        }, cancellationToken);
    }

    /// <summary>部分提交时返回另一侧仍持有的原恢复意图；已完成侧必须有对应的真实 next 或原回执。</summary>
    public Task<JsonElement?> PendingAckRebaseAsync(CancellationToken cancellationToken = default)
    {
        var stores = RecoveryStores(); return Run(check => RebaseIntent(stores, check, cancellationToken), cancellationToken);
    }

    public Task ConfirmAckRebaseAsync(JsonElement receipt, CancellationToken cancellationToken = default)
    {
        Preflight(); var fixedReceipt = R.ReceiptShape(receipt); var stores = RecoveryStores();
        return Run<object?>(async check =>
        {
            for (int i = 0; i < stores.Length; i++)
            {
                var pending = await stores[i].PendingAckRebaseAsync(cancellationToken).ConfigureAwait(false); check();
                if (pending.HasValue)
                {
                    var intent = R.Request(pending.Value); MatchRecovery(intent);
                    R.Receipt(fixedReceipt, intent, _identity.GetProperty("scope"));
                }
                else
                {
                    // Confirm cannot create an intent. An already-completed side must prove the exact next operation.
                    var saved = await Operation(_stores[i], fixedReceipt.GetProperty("next"), check, cancellationToken).ConfigureAwait(false);
                    A.Need(S.Equal(saved.GetProperty("receipt"), fixedReceipt.GetProperty("receipt")), "receipt_mismatch");
                }
            }
            foreach (var store in _stores) await VerifyStored(store, fixedReceipt.GetProperty("previous"), check, cancellationToken).ConfigureAwait(false);
            foreach (var store in stores) { await store.ConfirmAckRebaseAsync(fixedReceipt, cancellationToken).ConfigureAwait(false); check(); }
            foreach (var store in _stores)
            {
                var operation = await Operation(store, fixedReceipt.GetProperty("next"), check, cancellationToken).ConfigureAwait(false);
                A.Need(S.Equal(operation.GetProperty("receipt"), fixedReceipt.GetProperty("receipt")), "receipt_mismatch");
            }
            var aligned = await Aligned(check, false, cancellationToken).ConfigureAwait(false); A.Need(!aligned.Pending.HasValue, "reconciliation_required"); return null;
        }, cancellationToken);
    }

    private IRecoverableArchiveStore[] RecoveryStores()
    {
        var result = new IRecoverableArchiveStore[2];
        for (int i = 0; i < _stores.Length; i++) result[i] = _stores[i] as IRecoverableArchiveStore ?? throw new StorageException("invalid_input");
        return result;
    }

    private async Task<JsonElement?> RebaseIntent(IRecoverableArchiveStore[] stores, Action check, CancellationToken ct)
    {
        var a = await stores[0].PendingAckRebaseAsync(ct).ConfigureAwait(false); check(); var b = await stores[1].PendingAckRebaseAsync(ct).ConfigureAwait(false); check();
        if (!a.HasValue && !b.HasValue) return null;
        var intent = R.Request((a ?? b)!.Value); MatchRecovery(intent);
        A.Need(!a.HasValue || !b.HasValue || S.Equal(a.Value, b.Value), "reconciliation_required");
        var previous = intent.GetProperty("previous"); var expected = previous.GetProperty("coverage");
        for (int i = 0; i < _stores.Length; i++)
        {
            var state = await Facts(_stores[i], check, ct).ConfigureAwait(false);
            A.Need(state.Head.HasValue && A.String(state.Head.Value, "sequence") == A.String(expected, "throughSequence") && A.String(state.Head.Value, "recordDigest") == A.String(expected, "headDigest"), "reconciliation_required");
            if (state.Pending.HasValue) A.Need(S.Equal(state.Pending.Value, previous), "reconciliation_required");
            else
            {
                A.Need(!(i == 0 ? a : b).HasValue, "reconciliation_required");
                var rebased = await _stores[i].ReplicaOperationAsync(intent.GetProperty("request"), ct).ConfigureAwait(false); check();
                if (rebased.HasValue)
                {
                    S.Fields(rebased.Value, "ack", "receipt");
                    var result = A.Object(w =>
                    {
                        foreach (var field in intent.EnumerateObject()) field.WriteTo(w);
                        A.Property(w, "next", rebased.Value.GetProperty("ack")); A.Property(w, "receipt", rebased.Value.GetProperty("receipt"));
                    });
                    R.Receipt(result, intent, _identity.GetProperty("scope"));
                }
                else
                {
                    var original = await Operation(_stores[i], previous, check, ct).ConfigureAwait(false);
                    A.Need(original.GetProperty("receipt").ValueKind != JsonValueKind.Null, "reconciliation_required");
                }
            }
            await VerifyStored(_stores[i], previous, check, ct).ConfigureAwait(false);
        }
        return intent;
    }

    private void MatchRecovery(JsonElement intent)
    {
        var previous = intent.GetProperty("previous");
        A.Need(A.String(previous, "bindingId") == A.String(_identity, "bindingId") && A.String(previous, "sourceId") == A.String(_identity, "sourceId") &&
            A.String(previous, "sourceGeneration") == A.String(_identity, "sourceGeneration") && S.Equal(previous.GetProperty("generations"), _identity.GetProperty("target").GetProperty("generations")), "identity_mismatch");
    }
}
