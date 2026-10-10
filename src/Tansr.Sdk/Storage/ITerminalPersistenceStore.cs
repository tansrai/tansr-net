using System.Text.Json;

namespace Tansr.Sdk.Storage;

/// <summary>Explicit terminal-persistence-v1 opaque storage. No memory policy or recovery authority is inferred from data.</summary>
public interface ITerminalPersistenceStore
{
    /// <summary>Root, permanent dual-key index, transfer result and capacity share one durable atomic commit.</summary>
    bool AtomicDurablePersistence { get; }
    /// <summary>All object bodies and state are authenticated and encrypted. The execution journal is checked separately.</summary>
    bool EncryptedState { get; }
    /// <summary>Fixed flat applicationScopeId/endUserId/sourceId/sourceGeneration/domainKey identity.</summary>
    JsonElement Identity { get; }
    /// <summary>Consumes the pinned v1 request. Only a confirmed pre-commit rejection or rollback may use MemoryPublicationRejectedException.</summary>
    Task<JsonElement> ExecuteAsync(JsonElement request, string ownerCanonical, CancellationToken cancellationToken = default);
    /// <summary>Called by the storage owner after execution is stopped. Unknown outcomes are never discarded.</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
