using System.Text.Json;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Terminal;

/// <summary>Explicit preview of Serve-owned session configuration and memory management.
/// Uses the original client's authentication, scope and lifetime. It does not own the client.</summary>
/// <remarks>The candidate is opt-in and schema-pinned, not a stable protocol promise. Serve
/// retains configuration, memory selection and authorization decisions. Persist an operation's
/// Request and Scope before sending if recovery must survive application exit. An uncertain
/// response never causes automatic mutation replay or creation of a replacement operation.</remarks>
public sealed class TerminalSessionControl
{
    private readonly TansrClient client;
    private readonly TerminalControlClient control;

    public TerminalSessionControl(TansrClient client, bool enablePreview = false)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        control = new TerminalControlClient(client, enablePreview);
    }

    /// <summary>The exact Serve candidate required by this SDK build.</summary>
    public static string SchemaRevision => TerminalCandidateContract.Revision;
    public static string SchemaSha256 => TerminalCandidateContract.SchemaSha256;

    private JsonElement Session(string sessionId)
    {
        if (sessionId is null) throw new ArgumentNullException(nameof(sessionId));
        return TerminalJson.Object(w =>
        {
            w.WriteString("sessionContract", client.TerminalSessionContract);
            w.WriteString("sessionId", sessionId);
        });
    }

    public Task<JsonElement> ReadConfigurationAsync(string sessionId, CancellationToken cancellationToken = default) =>
        control.ReadConfigurationAsync(Session(sessionId), cancellationToken);

    /// <summary>Capture model/thinking changes at an observed configuration revision.
    /// Unsupported fields are rejected; this candidate does not define prompt or budget changes.</summary>
    public SessionConfigurationOperation CreateConfigurationOperation(string sessionId, string requestId,
        long expectedRevision, JsonElement changes) =>
        new(control.CreateConfigurationOperation(Session(sessionId), requestId, expectedRevision, changes));

    public SessionConfigurationOperation RestoreConfigurationOperation(JsonElement originalRequest, JsonElement originalScope) =>
        new(control.RestoreConfigurationOperation(originalRequest, originalScope));

    public Task<JsonElement> ApplyConfigurationAsync(SessionConfigurationOperation operation, CancellationToken cancellationToken = default) =>
        control.ApplyConfigurationAsync((operation ?? throw new ArgumentNullException(nameof(operation))).Inner, cancellationToken);

    /// <summary>Explicit replay of the unchanged original request. Reading current configuration
    /// does not prove whether an earlier request committed; candidate 7 has no configuration receipt GET.</summary>
    public Task<JsonElement> ReplayConfigurationAsync(SessionConfigurationOperation operation, CancellationToken cancellationToken = default) =>
        control.ReplayConfigurationAsync((operation ?? throw new ArgumentNullException(nameof(operation))).Inner, cancellationToken);

    public Task<JsonElement> ReadMemoryAsync(string sessionId, CancellationToken cancellationToken = default) =>
        control.ReadMemoryAsync(Session(sessionId), cancellationToken);

    /// <summary>Build a memory command from a real state response. Source identity, generation
    /// and revision remain those of the observed source; this call does not write memory.</summary>
    public MemoryOperation CreateMemoryOperation(string sessionId, JsonElement memoryStateResponse,
        string requestId, string operationId, JsonElement command) =>
        new(control.CreateMemoryOperation(Session(sessionId), memoryStateResponse, requestId, operationId, command));

    public MemoryOperation RestoreMemoryOperation(JsonElement originalRequest, JsonElement originalScope) =>
        new(control.RestoreMemoryOperation(originalRequest, originalScope));

    public Task<JsonElement> SubmitMemoryAsync(MemoryOperation operation, CancellationToken cancellationToken = default) =>
        control.SubmitMemoryAsync((operation ?? throw new ArgumentNullException(nameof(operation))).Inner, cancellationToken);

    /// <summary>Read the receipt for the original operation. A missing receipt is unknown,
    /// not permission to create another operation or evidence that a write failed.</summary>
    public Task<JsonElement> QueryMemoryAsync(MemoryOperation operation, CancellationToken cancellationToken = default) =>
        control.QueryMemoryAsync((operation ?? throw new ArgumentNullException(nameof(operation))).Inner, cancellationToken);

    /// <summary>Explicit original-key replay after the caller has reconciled an uncertain result.</summary>
    public Task<JsonElement> ReplayMemoryAsync(MemoryOperation operation, CancellationToken cancellationToken = default) =>
        control.ReplayMemoryAsync((operation ?? throw new ArgumentNullException(nameof(operation))).Inner, cancellationToken);
}
