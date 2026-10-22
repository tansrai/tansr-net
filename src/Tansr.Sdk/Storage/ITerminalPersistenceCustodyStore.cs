using System.Text.Json;

namespace Tansr.Sdk.Storage;

/// <summary>
/// Optional, explicitly configured custody handoff for the original terminal persistence domain.
/// The trusted shared authority decides the active writer; importing a complete catalog is not a grant.
/// </summary>
public interface ITerminalPersistenceCustodyStore : ITerminalPersistenceStore
{
    bool CustodyHandoffEnabled { get; }

    /// <summary>
    /// Executes one bounded handoff control message under the original authenticated owner.
    /// An unknown outcome must be queried using the original operation and intent, never replayed automatically.
    /// </summary>
    Task<JsonElement> ExecuteCustodyAsync(JsonElement request, string ownerCanonical, CancellationToken cancellationToken = default);
}
