using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Views;

namespace Tansr.Sdk.Sessions;

/// <summary>
/// One bounded presentation recovery coordinator per observation. It never sends/replays input,
/// downloads full history, polls a running source, or restores approval/usage authority.
/// Use it through SessionObservationOptions.Recovery, or explicitly for HTTP-only consumers.
/// </summary>
public sealed class SessionRecovery
{
    private readonly AgentSession session;
    private readonly SessionView view;
    private readonly Func<SessionRecoveryProjection, CancellationToken, Task>? installed;
    private readonly Func<SessionRecovery, CancellationToken, Task>? changed;
    private readonly SemaphoreSlim serial = new(1, 1);
    private long? firstSequence;
    private long? skipThrough;
    private bool attempted;

    public SessionRecovery(AgentSession session, SessionView view,
        Func<SessionRecoveryProjection, CancellationToken, Task>? installed = null,
        Func<SessionRecovery, CancellationToken, Task>? changed = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.view = view ?? throw new ArgumentNullException(nameof(view));
        this.installed = installed; this.changed = changed;
    }
    public bool Required { get; private set; } = true;
    public string? BlockedReason { get; private set; }
    public SessionRecoveryProjection? Projection { get; private set; }
    /// <summary>The new presentation/observation origin, not a declaration that all older events were consumed.</summary>
    public long? ObservationSequence => skipThrough;
    internal AgentSession Session => session;

    public Task<bool> RecoverAsync(CancellationToken cancellationToken = default) => RecoverCoreAsync(null, false, cancellationToken);

    private async Task<bool> RecoverCoreAsync(long? terminalSequence, bool newEpoch, CancellationToken token)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (newEpoch) { firstSequence = null; skipThrough = null; Projection = null; }
            Required = true; BlockedReason = "recovery_projection_pending"; attempted = true;
            // Preserve active block/tool associations and current pending requests while blocked.
            // A real replay gap already invalidates old requests in SessionView.Apply.
            if (!view.Snapshot.HasEventGap) view.MarkRecoveryPending();
            if (changed != null) await changed(this, token).ConfigureAwait(false);
            var before = view.Snapshot;
            var request = new SessionRecoveryRequest { AfterSequence = before.LastSequence ?? session.LastSequence,
                ExpectedFirstSequence = firstSequence,
                MinimumTerminalSequence = firstSequence.HasValue ? terminalSequence : null };
            SessionRecoveryProjection projection;
            try { projection = await session.GetRecoveryProjectionAsync(request, token).ConfigureAwait(false); }
            catch (TansrHttpException error) when (error.FamilyStatus == 409 && error.FamilyCode is
                "recovery_projection_pending" or "recovery_projection_unsupported" or "recovery_projection_stale" or "recovery_projection_limit")
            {
                BlockedReason = error.FamilyCode;
                if (error.FamilyCode == "recovery_projection_stale") { firstSequence = null; skipThrough = null; }
                if (changed != null) await changed(this, token).ConfigureAwait(false);
                return false;
            }
            token.ThrowIfCancellationRequested();
            view.RestoreRecoveryProjection(projection, before.Version);
            if (installed != null) await installed(projection, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            firstSequence = projection.FirstSequence; skipThrough = projection.HistorySequence; Projection = projection;
            Required = false; BlockedReason = null;
            if (changed != null) await changed(this, token).ConfigureAwait(false);
            return true;
        }
        finally { serial.Release(); }
    }

    internal bool Includes(AgentEvent item)
    {
        if (item.Name == "server.replay.gap") return true;
        var sequence = Sequence(item);
        return !skipThrough.HasValue || !sequence.HasValue || sequence > skipThrough;
    }

    internal async Task AfterEventAsync(AgentEvent item, CancellationToken token)
    {
        try { await AfterEventCoreAsync(item, token).ConfigureAwait(false); }
        catch (TansrException error)
        {
            Required = true; BlockedReason = error.Code;
            view.MarkSourceFailure("recovery_projection", error.Code);
            if (changed != null) await changed(this, token).ConfigureAwait(false);
        }
    }

    private async Task AfterEventCoreAsync(AgentEvent item, CancellationToken token)
    {
        if (item.Name == "server.replay.gap")
        {
            // No old-epoch terminal fence may constrain a new replay origin.
            await RecoverCoreAsync(null, true, token).ConfigureAwait(false);
        }
        else if (Required && item.Name is "turn.completed" or "turn.aborted")
        {
            // The real terminal can precede asynchronous archive settlement. Preserve the
            // previous bounded read-only settlement allowance, never retry business input.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    if (await RecoverCoreAsync(Sequence(item), false, deadline.Token).ConfigureAwait(false) ||
                        BlockedReason != "recovery_projection_pending") return;
                    if (attempt < 5) await Task.Delay(100 * (attempt + 1), deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested) { }
        }
        else if (!Required && view.Snapshot.HasEventGap || !attempted && view.Snapshot.HasEventGap)
            await RecoverCoreAsync(null, false, token).ConfigureAwait(false);
    }

    private static long? Sequence(AgentEvent item)
    {
        if (item.Data.ValueKind == JsonValueKind.Object && item.Data.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var value)) return value;
        return long.TryParse(item.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}
