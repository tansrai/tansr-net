using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Execution;

/// <summary>Optional, explicitly negotiated executor notifications. The original polling protocol remains authoritative.</summary>
public sealed class ExecutionHostOptions
{
    /// <summary>Called once after onConnected has finished binding. The caller owns the returned source and its connection.</summary>
    public Func<JsonElement, CancellationToken, Task<IExecutionNotificationSource>>? NotificationSourceFactory { get; set; }
}

/// <summary>Authenticated candidate notifications for one bound device. Events only trigger original polling or status reads.</summary>
public interface IExecutionNotificationSource
{
    JsonElement Scope { get; }
    Task ObserveAsync(JsonElement connection, long? lastEventId, Func<JsonElement, CancellationToken, Task> observer, CancellationToken cancellationToken);
    /// <summary>Return the original ExecutionStatus, including its complete operation and receipt.</summary>
    Task<JsonElement> GetStatusAsync(JsonElement originalOperation, CancellationToken cancellationToken);
}

/// <summary>Candidate-7 adapter over an explicitly enabled, controller-authenticated terminal binding.</summary>
public sealed class TerminalExecutionNotifications : IExecutionNotificationSource
{
    private readonly TerminalConnection _terminal;
    private readonly TerminalBinding _binding;
    private readonly JsonElement _scope, _target;

    public TerminalExecutionNotifications(TerminalConnection terminal, TerminalBinding binding)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        var value = binding.Value;
        _scope = value.GetProperty("scope").Clone();
        _target = value.GetProperty("executionBinding").GetProperty("target").Clone();
        if (!value.GetProperty("accepted").EnumerateArray().Any(item => item.GetString() == "execution-stream-v1"))
            throw new ArgumentException("Executor notifications require the accepted execution-stream-v1 capability.", nameof(binding));
    }

    public JsonElement Scope => _scope.Clone();

    public Task ObserveAsync(JsonElement connection, long? lastEventId, Func<JsonElement, CancellationToken, Task> observer, CancellationToken cancellationToken)
    {
        WireJson.ValidateNamed("ExecutorConnection", connection);
        foreach (var field in new[] { "executorId", "connectionId", "connectionRevision" })
            ExecutionJson.Check(ExecutionJson.Text(connection, field) == ExecutionJson.Text(_target, field));
        return _terminal.ObserveExecutorAsync(_binding, lastEventId, observer, cancellationToken);
    }

    public async Task<JsonElement> GetStatusAsync(JsonElement originalOperation, CancellationToken cancellationToken) =>
        (await _terminal.GetExecutionStateAsync(_binding, originalOperation, cancellationToken).ConfigureAwait(false)).GetProperty("execution").Clone();
}
