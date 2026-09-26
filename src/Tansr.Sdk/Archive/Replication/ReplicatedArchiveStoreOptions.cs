using System.Text.Json;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Archive.Replication;

public sealed class ReplicatedArchiveStoreOptions
{
    public IReplicaArchiveStore Primary { get; set; } = null!;
    public IReplicaArchiveStore Replica { get; set; } = null!;
    public string ReplicationId { get; set; } = "";
    public JsonElement Identity { get; set; }
    public JsonElement Limits { get; set; }
    public Func<JsonElement> ReadContext { get; set; } = null!;
}
