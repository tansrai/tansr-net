using System.Text.Json;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>New encrypted SQLite format, explicitly selected by the trusted host. Old publication files are never opened or migrated implicitly.</summary>
public sealed class SqliteTerminalPersistenceOptions
{
    public bool EnableProfile { get; set; }
    public string Path { get; set; } = "";
    public StorageOpenMode Mode { get; set; }
    public JsonElement Identity { get; set; }
    public Func<JsonElement> ReadContext { get; set; } = null!;
    public IArchiveKeyProvider KeyProvider { get; set; } = null!;
    public int MaxActiveTransfers { get; set; } = 32;
    public int MaxStagingBytes { get; set; } = 67108864;
    public int MaxReceiptEntries { get; set; } = 1048576;
    public int MaxTransferFacts { get; set; } = 262144;
    public int MaxObjects { get; set; } = 1048576;
    public long MaxRetainedBytes { get; set; } = 1073741824;
    /// <summary>Main SQLite page cap. WAL and filesystem free space remain separate physical limits; failures preserve the original transfer.</summary>
    public int MaxPages { get; set; } = 327680;
    /// <summary>Optional trusted permission to query an original owner's transfer; never grants put or commit.</summary>
    public Func<TerminalPersistenceRecoveryContext, bool>? AuthorizeRecovery { get; set; }
    /// <summary>Explicit custody migration. Every writer in this domain must use the same trusted shared authority.</summary>
    public bool EnableCustodyHandoff { get; set; }
    public JsonElement Custody { get; set; }
    public ITerminalPersistenceCustodyAuthority? CustodyAuthority { get; set; }
    /// <summary>可信宿主显式配置的目录对象迁移预算；不增加原业务容量。每次交接首次受理后固定。</summary>
    public PersistenceCustodyMigrationBudget? MigrationBudget { get; set; }
}

/// <summary>完整目录唯一对象正文的预算；目录页、数据库空间和网络编码仍受各自限制。</summary>
public sealed class PersistenceCustodyMigrationBudget
{
    public long Bytes { get; set; } = 1073741824;
    public int Objects { get; set; } = 1048576;
}

public sealed class TerminalPersistenceRecoveryContext
{
    internal TerminalPersistenceRecoveryContext(JsonElement identity, string id, JsonElement original, JsonElement current)
    { Identity = identity.Clone(); TransferId = id; OriginalOwner = original.Clone(); CurrentOwner = current.Clone(); }
    public JsonElement Identity { get; }
    public string TransferId { get; }
    public JsonElement OriginalOwner { get; }
    public JsonElement CurrentOwner { get; }
}
