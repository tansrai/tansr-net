using System.Text.Json;
using System.Text.Json.Nodes;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

// Controlled transport seam, not an HTTP endpoint implementation. Shared raw goldens are tested separately.
internal sealed class TerminalTestAdapter : ITerminalCandidateTransport
{
    internal JsonElement ExistingOperation { get; } = ExecutionFixture.Operation();
    internal JsonElement Scope { get; set; } = ExecutionFixture.Scope();
    internal JsonObject Capabilities { get; }
    internal List<JsonElement> Sent { get; } = [];
    internal List<JsonElement> Received { get; } = [];
    internal TaskCompletionSource<JsonElement> BatchObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool CommitThenThrow { get; set; }
    internal bool FailBeforeCommit { get; set; }
    internal bool Unavailable { get; set; }
    internal string? BindingFailure { get; set; }
    internal Func<JsonElement, JsonElement>? AlterResponse { get; set; }
    internal JsonElement? ExecutionState { get; set; }
    internal int StatusReads { get; private set; }
    private JsonElement? seal;

    internal TerminalTestAdapter()
    {
        Capabilities = Node(TerminalCandidateContractTests.Goldens().GetProperty("positive").EnumerateArray()
            .Single(x => x.GetProperty("definition").GetString() == "CapabilitiesResponse").GetProperty("value"));
        var feature = Capabilities["features"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["feature"]!.GetValue<string>() == "execution-stream-v1");
        feature["supported"] = true; feature["installed"] = true; feature["authorization"] = "allowed"; feature["device"] = "available";
    }
    internal static JsonObject Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
    internal static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
    internal async Task<(TerminalCandidateClient Client, TerminalCandidateBinding Binding)> BindAsync()
    {
        var client = new TerminalCandidateClient(this, () => Scope);
        var request = TerminalJson.Object(w =>
        {
            w.WriteString("contract", TerminalCandidateContract.Protocol); w.WriteString("requestId", "candidate-request");
            w.WriteStartObject("session"); w.WriteString("sessionContract", "sdk2-offload-v1"); w.WriteString("sessionId", "session-1"); w.WriteEndObject();
            TerminalJson.Field(w, "executionBinding", ExistingOperation.GetProperty("binding"));
            w.WriteStartArray("required"); w.WriteStringValue("execution-stream-v1"); w.WriteEndArray();
            w.WriteStartArray("optional"); w.WriteEndArray();
        });
        return (client, await client.BindAsync(request));
    }
    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(Element(Capabilities));
    public Task<JsonElement> BindAsync(JsonElement request, CancellationToken cancellationToken) => BindingFailure != null
        ? Task.FromException<JsonElement>(new WireProtocolException(BindingFailure)) : Task.FromResult(TerminalJson.Object(w =>
    {
        w.WriteString("contract", TerminalCandidateContract.Protocol); w.WriteString("requestId", TerminalJson.Text(request, "requestId"));
        TerminalJson.Field(w, "session", request.GetProperty("session")); TerminalJson.Field(w, "scope", Scope);
        TerminalJson.Field(w, "executionBinding", request.GetProperty("executionBinding"));
        w.WriteStartArray("accepted"); w.WriteStringValue("execution-stream-v1"); w.WriteEndArray();
        w.WriteStartArray("unavailable"); w.WriteEndArray(); w.WriteString("outputAuthority", "tool-output");
        TerminalJson.Field(w, "limits", Element(Capabilities["limits"]!));
    }));
    public Task<JsonElement> SendBatchAsync(JsonElement request, CancellationToken cancellationToken)
    {
        Sent.Add(request.Clone());
        if (FailBeforeCommit) { FailBeforeCommit = false; throw new IOException("synthetic request lost"); }
        foreach (var block in request.GetProperty("blocks").EnumerateArray()) Received.Add(block.Clone());
        if (request.GetProperty("seal").ValueKind != JsonValueKind.Null) seal = request.GetProperty("seal").Clone();
        if (CommitThenThrow) { CommitThenThrow = false; throw new IOException("synthetic response lost"); }
        var result = Status(request.GetProperty("operation"));
        BatchObserved.TrySetResult(result);
        return Task.FromResult(AlterResponse?.Invoke(result) ?? result);
    }
    public Task<JsonElement> GetOutputStatusAsync(JsonElement session, JsonElement operation, CancellationToken cancellationToken)
    { StatusReads++; return Task.FromResult(Status(operation)); }
    public Task<JsonElement> GetExecutionStateAsync(JsonElement session, JsonElement operation, string executorId, string connectionId, CancellationToken cancellationToken)
        => ExecutionState.HasValue ? Task.FromResult(ExecutionState.Value.Clone()) : throw new NotSupportedException("This controlled adapter has no execution state configured.");
    internal JsonElement Status(JsonElement operation) => TerminalJson.Object(w =>
    {
        w.WriteString("contract", TerminalCandidateContract.Protocol); TerminalJson.Field(w, "operation", operation);
        w.WriteString("state", Unavailable ? "unavailable" : seal.HasValue ? seal.Value.GetProperty("truncated").GetBoolean() ? "truncated" : "complete" : Received.Count == 0 ? "available" : "receiving");
        TerminalJson.Decimal(w, "acceptedThrough", Unavailable || Received.Count == 0 ? null : TerminalJson.Sequence(Received.Last(), "seq"));
        w.WriteNull("durableThrough"); TerminalJson.Decimal(w, "retainedFrom", Unavailable || Received.Count == 0 ? null : 0);
        TerminalJson.Decimal(w, "nextByteOffset", Unavailable ? null : Received.Sum(x => (long)x.GetProperty("byteLength").GetInt32()));
        if (seal.HasValue && !Unavailable) TerminalJson.Field(w, "seal", seal.Value); else w.WriteNull("seal");
    });
}
