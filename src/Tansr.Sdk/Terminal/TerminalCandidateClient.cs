using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

/// <summary>
/// Candidate adapter seam. Implementations bind the Serve-owned candidate routes and reauthenticate
/// each call. Failures never imply SDK1 fallback or permission to execute an operation again.
/// </summary>
internal interface ITerminalCandidateTransport
{
    Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<JsonElement> BindAsync(JsonElement request, CancellationToken cancellationToken);
    Task<JsonElement> SendBatchAsync(JsonElement request, CancellationToken cancellationToken);
    Task<JsonElement> GetOutputStatusAsync(JsonElement session, JsonElement operation, CancellationToken cancellationToken);
    Task<JsonElement> GetExecutionStateAsync(JsonElement session, JsonElement operation, string executorId, string connectionId, CancellationToken cancellationToken);
}

internal sealed class TerminalCandidateBinding
{
    internal TerminalCandidateBinding(TerminalCandidateClient owner, JsonElement value)
    { Owner = owner; Raw = value.Clone(); Limits = new TerminalCandidateLimits(value.GetProperty("limits")); }
    internal TerminalCandidateClient Owner { get; }
    internal JsonElement Raw { get; }
    internal TerminalCandidateLimits Limits { get; }
    internal JsonElement Session => Raw.GetProperty("session");
    internal JsonElement Scope => Raw.GetProperty("scope");
    internal JsonElement ExecutionBinding => Raw.GetProperty("executionBinding");
}

/// <summary>Opt-in, internal candidate consumer. Discovery is not execution authorization.</summary>
internal sealed class TerminalCandidateClient
{
    private readonly ITerminalCandidateTransport transport;
    private readonly Func<JsonElement> scopeProvider;
    internal TerminalCandidateClient(ITerminalCandidateTransport transport, Func<JsonElement> currentScope)
    { this.transport = transport ?? throw new ArgumentNullException(nameof(transport)); scopeProvider = currentScope ?? throw new ArgumentNullException(nameof(currentScope)); }

    private JsonElement CurrentScope()
    {
        var scope = scopeProvider().Clone(); WireJson.ValidateNamed("Scope", scope); return scope;
    }
    private void Guard(JsonElement expected) => TerminalJson.Check(TerminalJson.Equal(CurrentScope(), expected), "context_changed");

    internal async Task<JsonElement> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope();
        var value = await transport.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); Guard(scope);
        TerminalCandidateContract.Validate("CapabilitiesResponse", value);
        TerminalJson.Check(TerminalJson.Text(value, "schemaRevision") == TerminalCandidateContract.Revision &&
            TerminalJson.Text(value, "schemaSha256") == TerminalCandidateContract.SchemaSha256, "protocol_version_mismatch");
        return value.Clone();
    }

    internal async Task<TerminalCandidateBinding> BindAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        request = request.Clone(); TerminalCandidateContract.Validate("BindingRequest", request);
        var scope = CurrentScope();
        var discovered = await DiscoverAsync(cancellationToken).ConfigureAwait(false); Guard(scope);
        var session = request.GetProperty("session");
        TerminalJson.Check(discovered.GetProperty("sessionContracts").EnumerateArray().Any(x => x.GetString() == TerminalJson.Text(session, "sessionContract")), "unsupported_capability");
        // Discovery may be unconfirmed before binding. Only static installation facts narrow this
        // request; trusted binding performs the actual authorization/device decision.
        var expectedAccepted = new HashSet<string>(StringComparer.Ordinal);
        var expectedUnavailable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in new[] { "required", "optional" })
        {
            foreach (var nameValue in request.GetProperty(list).EnumerateArray())
            {
                var name = nameValue.GetString()!;
                var feature = discovered.GetProperty("features").EnumerateArray().Single(x => TerminalJson.Text(x, "feature") == name);
                if (!feature.GetProperty("supported").GetBoolean() || !feature.GetProperty("installed").GetBoolean())
                { TerminalJson.Check(list == "optional", "unsupported_capability"); expectedUnavailable.Add(name); continue; }
                if (name == "execution-stream-v1" || name == "execution-background-v1")
                    TerminalJson.Check(request.GetProperty("executionBinding").ValueKind != JsonValueKind.Null, "stale_generation");
                expectedAccepted.Add(name);
            }
        }
        var value = await transport.BindAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); Guard(scope);
        TerminalCandidateContract.Validate("BindingResponse", value);
        TerminalJson.Check(TerminalJson.Text(value, "requestId") == TerminalJson.Text(request, "requestId") &&
            TerminalJson.Equal(value.GetProperty("session"), session) && TerminalJson.Equal(value.GetProperty("scope"), scope) &&
            TerminalJson.Equal(value.GetProperty("executionBinding"), request.GetProperty("executionBinding")), "binding_conflict");
        var accepted = value.GetProperty("accepted").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var unavailable = value.GetProperty("unavailable").EnumerateArray().Select(x => TerminalJson.Text(x, "feature")).ToArray();
        TerminalJson.Check(expectedAccepted.SetEquals(accepted) && expectedUnavailable.SetEquals(unavailable) &&
            unavailable.Distinct(StringComparer.Ordinal).Count() == unavailable.Length, "binding_conflict");
        foreach (var feature in value.GetProperty("unavailable").EnumerateArray())
            TerminalJson.Check(!feature.GetProperty("supported").GetBoolean() || !feature.GetProperty("installed").GetBoolean(), "binding_conflict");
        TerminalJson.Check(TerminalJson.Text(value, "outputAuthority") == (expectedAccepted.Contains("execution-stream-v1") ? "tool-output" : "session-events"), "binding_conflict");
        var limits = value.GetProperty("limits");
        foreach (var property in limits.EnumerateObject())
            TerminalJson.Check(property.Value.GetInt32() <= discovered.GetProperty("limits").GetProperty(property.Name).GetInt32(), "binding_conflict");
        return new TerminalCandidateBinding(this, value);
    }

    internal TerminalOutputProducer CreateOutputProducer(TerminalCandidateBinding binding, JsonElement existingOperation)
    {
        AssertBinding(binding);
        WireJson.ValidateNamed("ExecutionOperation", existingOperation);
        TerminalJson.Check(TerminalJson.Text(binding.Raw, "outputAuthority") == "tool-output" &&
            binding.Raw.GetProperty("accepted").EnumerateArray().Any(x => x.GetString() == "execution-stream-v1"), "unsupported_capability");
        TerminalJson.Check(TerminalJson.Equal(existingOperation.GetProperty("scope"), binding.Scope) &&
            TerminalJson.Equal(existingOperation.GetProperty("binding"), binding.ExecutionBinding) &&
            TerminalJson.Text(existingOperation, "sessionId") == TerminalJson.Text(binding.Session, "sessionId"), "binding_conflict");
        var operation = TerminalJson.Object(w =>
        {
            w.WriteString("operationId", TerminalJson.Text(existingOperation, "operationId"));
            w.WriteString("requestDigest", TerminalJson.Text(existingOperation, "digest"));
        });
        return new TerminalOutputProducer(this, binding, operation);
    }

    internal void AssertBinding(TerminalCandidateBinding binding)
    {
        if (binding == null || !ReferenceEquals(binding.Owner, this)) throw new ArgumentException("Candidate binding belongs to another client.", nameof(binding));
        Guard(binding.Scope);
    }

    /// <summary>Candidate-4 executor narrow read of the original operation; output seal/watermarks
    /// do not substitute for this original execution state or permit another process launch.</summary>
    internal async Task<JsonElement> GetExecutionStateAsync(TerminalCandidateBinding binding, JsonElement existingOperation, CancellationToken cancellationToken = default)
    {
        AssertBinding(binding); WireJson.ValidateNamed("ExecutionOperation", existingOperation);
        TerminalJson.Check(TerminalJson.Equal(existingOperation.GetProperty("scope"), binding.Scope) &&
            TerminalJson.Equal(existingOperation.GetProperty("binding"), binding.ExecutionBinding) &&
            TerminalJson.Text(existingOperation, "sessionId") == TerminalJson.Text(binding.Session, "sessionId"), "binding_conflict");
        var reference = TerminalJson.Object(w => { w.WriteString("operationId", TerminalJson.Text(existingOperation, "operationId")); w.WriteString("requestDigest", TerminalJson.Text(existingOperation, "digest")); });
        var target = binding.ExecutionBinding.GetProperty("target");
        var value = await transport.GetExecutionStateAsync(binding.Session, reference, TerminalJson.Text(target, "executorId"), TerminalJson.Text(target, "connectionId"), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); AssertBinding(binding);
        TerminalCandidateContract.Validate("ExecutionState", value);
        var execution = value.GetProperty("execution"); WireJson.ValidateNamed("ExecutionStatus", execution);
        TerminalJson.Check(TerminalJson.Equal(value.GetProperty("session"), binding.Session) && TerminalJson.Equal(execution.GetProperty("operation"), existingOperation), "binding_conflict");
        ExecutionJson.Operation(existingOperation);
        var receipt = execution.GetProperty("receipt");
        if (receipt.ValueKind == JsonValueKind.Null)
            TerminalJson.Check(TerminalJson.Text(execution, "status") == "pending" || TerminalJson.Text(execution, "status") == "unknown", "invalid_response");
        else
        {
            ExecutionJson.Receipt(existingOperation, receipt);
            TerminalJson.Check(TerminalJson.Text(execution, "status") == TerminalJson.Text(receipt, "status"), "invalid_response");
        }
        return value.Clone();
    }

    internal async Task<TerminalOutputStatus> SendBatchAsync(TerminalCandidateBinding binding, JsonElement request, CancellationToken cancellationToken)
    {
        AssertBinding(binding); TerminalCandidateContract.Validate("OutputBatchRequest", request);
        WireJson.EncodeControl(request, binding.Limits.MaxControlBytes);
        var value = await transport.SendBatchAsync(request.Clone(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); AssertBinding(binding);
        var status = new TerminalOutputStatus(value);
        TerminalJson.Check(TerminalJson.Equal(value.GetProperty("operation"), request.GetProperty("operation")), "binding_conflict");
        return status;
    }

    internal async Task<TerminalOutputStatus> GetOutputStatusAsync(TerminalCandidateBinding binding, JsonElement operation, CancellationToken cancellationToken)
    {
        AssertBinding(binding); TerminalCandidateContract.Validate("OperationReference", operation);
        var value = await transport.GetOutputStatusAsync(binding.Session.Clone(), operation.Clone(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); AssertBinding(binding);
        var status = new TerminalOutputStatus(value);
        TerminalJson.Check(TerminalJson.Equal(value.GetProperty("operation"), operation), "binding_conflict");
        return status;
    }
}
