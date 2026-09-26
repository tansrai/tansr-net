using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

/// <summary>Internal consumer of Serve's configuration and memory candidate. No separate
/// model loop, memory selector, configuration store, or automatic side-effect retry.</summary>
internal sealed class TerminalControlClient
{
    private readonly TansrClient client;
    private readonly JsonElement owner;

    internal TerminalControlClient(TansrClient client, bool enableCandidate = false)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        if (!enableCandidate) throw new TansrProtocolException("unsupported_capability");
        owner = client.ReadExecutionScope();
    }

    internal static void CheckOwner(JsonElement expected, JsonElement actual, string code = "context_changed")
    {
        if (TerminalJson.Text(expected, "applicationScopeId") != TerminalJson.Text(actual, "applicationScopeId") ||
            TerminalJson.Text(expected, "endUserId") != TerminalJson.Text(actual, "endUserId")) throw new TansrProtocolException(code);
    }

    private JsonElement Scope()
    {
        var scope = client.ReadExecutionScope(); CheckOwner(owner, scope); return scope;
    }

    private JsonElement Session(JsonElement session)
    {
        var captured = session.Clone(); TerminalCandidateContract.Validate("SessionReference", captured);
        if (TerminalJson.Text(captured, "sessionContract") != client.TerminalSessionContract) throw new TansrProtocolException("binding_conflict");
        Segment(TerminalJson.Text(captured, "sessionId")); return captured;
    }

    private static string Segment(string value)
    {
        if (value == "." || value == "..") throw new TansrProtocolException("invalid_request");
        return Uri.EscapeDataString(value);
    }

    private static string Path(JsonElement session) => "/v3/terminal/sessions/" + Segment(TerminalJson.Text(session, "sessionId"));
    private static string Query(JsonElement session) => "?contract=terminal-services-v1&sessionContract=" + Uri.EscapeDataString(TerminalJson.Text(session, "sessionContract"));
    private static void SameSession(JsonElement response, JsonElement session)
    {
        if (!TerminalJson.Equal(response.GetProperty("session"), session)) throw new TansrProtocolException("invalid_response");
    }

    internal async Task<JsonElement> ReadConfigurationAsync(JsonElement session, CancellationToken cancellationToken = default)
    {
        var selected = Session(session); var scope = Scope();
        var value = await client.SendTerminalControlAsync(HttpMethod.Get, Path(selected) + "/configuration" + Query(selected), null,
            "ConfigurationResponse", scope, cancellationToken).ConfigureAwait(false);
        SameSession(value, selected); return value;
    }

    /// <summary>Capture an immutable CAS request before the first send. Keep it if the response is lost.</summary>
    internal TerminalConfigurationOperation CreateConfigurationOperation(JsonElement session, string requestId, long expectedRevision, JsonElement changes)
    {
        var selected = Session(session); var scope = Scope();
        var request = TerminalJson.Object(w =>
        {
            w.WriteString("contract", TerminalCandidateContract.Protocol); TerminalJson.Field(w, "session", selected);
            w.WriteString("requestId", requestId); w.WriteNumber("expectedRevision", expectedRevision); TerminalJson.Field(w, "changes", changes);
        });
        ValidateConfigurationRequest(request);
        return new TerminalConfigurationOperation(request, scope, false);
    }

    internal TerminalConfigurationOperation RestoreConfigurationOperation(JsonElement originalRequest, JsonElement originalScope)
    {
        WireJson.ValidateNamed("Scope", originalScope); CheckOwner(originalScope, Scope());
        ValidateConfigurationRequest(originalRequest); Session(originalRequest.GetProperty("session"));
        return new TerminalConfigurationOperation(originalRequest, originalScope, true);
    }

    private static void ValidateConfigurationRequest(JsonElement request)
    {
        TerminalCandidateContract.Validate("ConfigurationRequest", request);
        if (!request.GetProperty("changes").EnumerateObject().MoveNext() || request.GetProperty("expectedRevision").GetInt64() >= 9007199254740991L)
            throw new TansrProtocolException("invalid_request");
    }

    internal Task<JsonElement> ApplyConfigurationAsync(TerminalConfigurationOperation operation, CancellationToken cancellationToken = default) =>
        ConfigureAsync(operation, false, cancellationToken);

    /// <summary>Explicitly replay the identical request. Serve supplies changed/replayed and the original
    /// revision. Candidate 7 has no configuration operation GET; reading current state cannot prove this commit.</summary>
    internal Task<JsonElement> ReplayConfigurationAsync(TerminalConfigurationOperation operation, CancellationToken cancellationToken = default) =>
        ConfigureAsync(operation, true, cancellationToken);

    private async Task<JsonElement> ConfigureAsync(TerminalConfigurationOperation operation, bool replay, CancellationToken cancellationToken)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        CheckOwner(operation.Scope, Scope()); var request = operation.Request; var session = Session(request.GetProperty("session"));
        ValidateConfigurationRequest(request); cancellationToken.ThrowIfCancellationRequested(); operation.Begin(replay);
        var value = await client.SendTerminalControlAsync(HttpMethod.Post, Path(session) + "/configuration", request,
            "ConfigurationCommitResponse", operation.Scope, cancellationToken).ConfigureAwait(false);
        SameSession(value, session);
        var configuration = value.GetProperty("configuration"); var changes = request.GetProperty("changes");
        if (TerminalJson.Text(value, "requestId") != TerminalJson.Text(request, "requestId") ||
            configuration.GetProperty("revision").GetInt64() != request.GetProperty("expectedRevision").GetInt64() + 1 ||
            changes.TryGetProperty("model", out var model) && !TerminalJson.Equal(model, configuration.GetProperty("model")) ||
            changes.TryGetProperty("thinking", out var thinking) && !TerminalJson.Equal(thinking, configuration.GetProperty("thinking")))
            throw new TansrProtocolException("invalid_response");
        return value;
    }

    internal async Task<JsonElement> ReadMemoryAsync(JsonElement session, CancellationToken cancellationToken = default)
    {
        var selected = Session(session); var scope = Scope();
        var value = await client.SendTerminalControlAsync(HttpMethod.Get, Path(selected) + "/memory" + Query(selected), null,
            "MemoryStateResponse", scope, cancellationToken).ConfigureAwait(false);
        SameSession(value, selected); return value;
    }

    /// <summary>Source, generation and revision are copied from the actual state, never inferred from
    /// a path, timestamp, text similarity or a model-produced memory identifier.</summary>
    internal TerminalMemoryOperation CreateMemoryOperation(JsonElement session, JsonElement memoryStateResponse,
        string requestId, string operationId, JsonElement command)
    {
        var selected = Session(session); var scope = Scope();
        TerminalCandidateContract.Validate("MemoryStateResponse", memoryStateResponse); SameSession(memoryStateResponse, selected);
        var state = memoryStateResponse.GetProperty("memory"); var identity = state.GetProperty("identity");
        CheckOwner(identity, scope, "integrity_mismatch");
        if (!state.GetProperty("available").GetBoolean()) throw new TansrProtocolException("source_unavailable");
        var request = TerminalJson.Object(w =>
        {
            w.WriteString("contract", TerminalCandidateContract.Protocol); TerminalJson.Field(w, "session", selected);
            w.WriteString("requestId", requestId); w.WriteString("operationId", operationId);
            TerminalJson.Field(w, "sourceId", identity.GetProperty("sourceId")); TerminalJson.Field(w, "sourceGeneration", identity.GetProperty("sourceGeneration"));
            TerminalJson.Field(w, "expectedRevision", state.GetProperty("revision")); TerminalJson.Field(w, "command", command);
        });
        ValidateMemoryRequest(request); return new TerminalMemoryOperation(request, scope, false);
    }

    internal TerminalMemoryOperation RestoreMemoryOperation(JsonElement originalRequest, JsonElement originalScope)
    {
        WireJson.ValidateNamed("Scope", originalScope); CheckOwner(originalScope, Scope());
        ValidateMemoryRequest(originalRequest); Session(originalRequest.GetProperty("session"));
        return new TerminalMemoryOperation(originalRequest, originalScope, true);
    }

    private static void ValidateMemoryRequest(JsonElement request)
    {
        TerminalCandidateContract.Validate("MemoryCommandRequest", request);
        Segment(TerminalJson.Text(request, "operationId"));
    }

    internal Task<JsonElement> SubmitMemoryAsync(TerminalMemoryOperation operation, CancellationToken cancellationToken = default) =>
        SubmitMemoryCoreAsync(operation, false, cancellationToken);

    /// <summary>Caller-controlled original-key replay only; query first when commit is unknown.</summary>
    internal Task<JsonElement> ReplayMemoryAsync(TerminalMemoryOperation operation, CancellationToken cancellationToken = default) =>
        SubmitMemoryCoreAsync(operation, true, cancellationToken);

    private async Task<JsonElement> SubmitMemoryCoreAsync(TerminalMemoryOperation operation, bool replay, CancellationToken cancellationToken)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        CheckOwner(operation.Scope, Scope()); var request = operation.Request; var session = Session(request.GetProperty("session"));
        ValidateMemoryRequest(request); cancellationToken.ThrowIfCancellationRequested(); operation.Begin(replay);
        var value = await client.SendTerminalControlAsync(HttpMethod.Post, Path(session) + "/memory/commands", request,
            "MemoryReceiptResponse", operation.Scope, cancellationToken).ConfigureAwait(false);
        ValidateMemoryResponse(value, request, false); return value;
    }

    /// <summary>A null receipt means unknown to this source. It neither authorizes a new operation nor
    /// proves failure. Pending, unknown, durability and consumption remain separate facts.</summary>
    internal async Task<JsonElement> QueryMemoryAsync(TerminalMemoryOperation operation, CancellationToken cancellationToken = default)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        CheckOwner(operation.Scope, Scope()); var request = operation.Request; var session = Session(request.GetProperty("session"));
        var path = Path(session) + "/memory/commands/" + Segment(TerminalJson.Text(request, "operationId")) + Query(session) +
            "&requestId=" + Uri.EscapeDataString(TerminalJson.Text(request, "requestId"));
        var value = await client.SendTerminalControlAsync(HttpMethod.Get, path, null, "MemoryReceiptResponse", operation.Scope, cancellationToken).ConfigureAwait(false);
        ValidateMemoryResponse(value, request, true); return value;
    }

    private static void ValidateMemoryResponse(JsonElement response, JsonElement originalRequest, bool allowMissing)
    {
        SameSession(response, originalRequest.GetProperty("session")); var receipt = response.GetProperty("receipt");
        if (receipt.ValueKind == JsonValueKind.Null)
        {
            if (!allowMissing) throw new TansrProtocolException("invalid_response");
            return;
        }
        foreach (var key in new[] { "requestId", "operationId", "sourceId", "sourceGeneration", "expectedRevision" })
            if (!TerminalJson.Equal(receipt.GetProperty(key), originalRequest.GetProperty(key))) throw new TansrProtocolException("invalid_response");
    }
}
