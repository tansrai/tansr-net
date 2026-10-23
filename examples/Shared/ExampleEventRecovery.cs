using System.Text.Json;
using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

// One instance belongs to one event observation. History restores presentation only;
// it never sends an input or manufactures a permission, usage or terminal event.
internal sealed class ExampleEventRecovery
{
    private readonly string _sessionId;
    private readonly Func<CancellationToken, Task<JsonElement>> _history, _metadata;
    private readonly Func<JsonElement, Task> _install;
    private readonly Func<bool> _isCurrent;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _deadline;
    private long _terminalFloor = long.MaxValue;

    internal ExampleEventRecovery(string sessionId,
        Func<CancellationToken, Task<JsonElement>> history,
        Func<CancellationToken, Task<JsonElement>> metadata,
        Func<JsonElement, Task> install, Func<bool> isCurrent,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? deadline = null)
    {
        _sessionId = sessionId; _history = history; _metadata = metadata; _install = install; _isCurrent = isCurrent;
        _delay = delay ?? Task.Delay; _deadline = deadline ?? TimeSpan.FromSeconds(5);
    }

    internal bool Required { get; private set; }
    internal long HistorySequence { get; private set; } = -1;

    internal async Task RecoverGapAsync(CancellationToken cancellationToken)
    {
        Required = true;
        _terminalFloor = long.MaxValue;
        EnsureCurrent(cancellationToken);
        var history = await _history(cancellationToken).ConfigureAwait(false);
        await InstallAsync(history, cancellationToken).ConfigureAwait(false);
        var metadata = await _metadata(cancellationToken).ConfigureAwait(false);
        EnsureCurrent(cancellationToken);
        var status = Text(metadata, "status");
        // This watermark only excludes terminals from older running incarnations.
        // It must never replace the history response's own message coverage.
        _terminalFloor = Sequence(metadata);
        if (status == "idle")
        {
            // Idle excludes Serve's asynchronous turn finalization. Read AFTER that
            // observation so an earlier history response cannot omit the finished turn.
            var settledHistory = await _history(cancellationToken).ConfigureAwait(false);
            await InstallAsync(settledHistory, cancellationToken).ConfigureAwait(false);
            Required = false;
        }
        else if (status == "ended")
        {
            // Closing may precede archive finalization. Only a retained turn terminal
            // and a covering history snapshot can prove the missing body is restored.
            _terminalFloor = HistorySequence + 1;
        }
    }

    internal async Task<bool> ObserveTerminalAsync(AgentEvent item, CancellationToken cancellationToken)
    {
        if (!Required || Text(item.Data, "sessionId") != _sessionId ||
            item.Name is not ("turn.completed" or "turn.aborted")) return false;
        var terminal = Sequence(item.Data, "seq");
        if (terminal < _terminalFloor) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_deadline);
        try
        {
            // Bounded archive-settlement reads, not retries of the original input or
            // model request. The existing SSE stays connected throughout this short wait.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                EnsureCurrent(timeout.Token);
                var history = await _history(timeout.Token).ConfigureAwait(false);
                EnsureCurrent(timeout.Token);
                if (Sequence(history) >= terminal)
                {
                    await InstallAsync(history, timeout.Token).ConfigureAwait(false);
                    Required = false;
                    return true;
                }
                if (attempt < 5) await _delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _isCurrent())
        {
            // Deadline leaves the input locked and the incomplete state visible.
        }
        return false;
    }

    private async Task InstallAsync(JsonElement history, CancellationToken cancellationToken)
    {
        EnsureCurrent(cancellationToken);
        var sequence = Sequence(history);
        await _install(history).ConfigureAwait(false);
        EnsureCurrent(cancellationToken);
        HistorySequence = sequence;
    }

    private long Sequence(JsonElement value, string field = "lastSeq")
    {
        if (Text(value, "sessionId") != _sessionId || !value.TryGetProperty(field, out var property) ||
            !property.TryGetInt64(out var result) || result < (field == "seq" ? 0 : -1) || result > 9007199254740991L)
            throw new InvalidOperationException("invalid_recovery_snapshot");
        return result;
    }

    private void EnsureCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isCurrent()) throw new OperationCanceledException("recovery_presentation_replaced", cancellationToken);
    }

    private static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
