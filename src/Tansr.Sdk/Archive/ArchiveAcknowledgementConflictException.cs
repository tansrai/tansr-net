using System.Text.Json;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Archive;

/// <summary>
/// Serve rejected a durable original ACK with binding_conflict. The original request remains pending.
/// Query it through RecoverPendingAsync; neither the rejection nor matching coverage authorizes a new key
/// or a changed expectedRevision. SDK2 currently has no public HTTP rebase proof contract.
/// </summary>
public sealed class ArchiveAcknowledgementConflictException : TansrException
{
    public JsonElement PendingAcknowledgement { get; }
    public string FailureCode => "binding_conflict";
    public int HttpStatusCode => 409;
    internal ArchiveAcknowledgementConflictException(JsonElement acknowledgement) : base("archive_acknowledgement_conflict")
    { PendingAcknowledgement = acknowledgement.Clone(); }
}
