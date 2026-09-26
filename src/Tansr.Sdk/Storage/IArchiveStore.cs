using System.Text.Json;

namespace Tansr.Sdk.Storage;

/// <summary>既有 SDK2 receiver 的加法 .NET 接口。所有 JSON 对象保持原 wire schema，不接受新记忆管理草案。</summary>
public interface IArchiveStore
{
    Task<JsonElement> ReceiveAsync(ArchiveReceiveInput input, CancellationToken cancellationToken = default);
    Task<JsonElement?> PendingAsync(CancellationToken cancellationToken = default);
    Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default);
    Task<byte[]> BodyAsync(JsonElement artifactReference, CancellationToken cancellationToken = default);
    Task ConfirmAsync(JsonElement receipt, CancellationToken cancellationToken = default);
    Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> CoverageAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}

/// <summary>原 archive-retention-v1 可选能力；缺席此接口不得声称支持删除同步。</summary>
public interface IArchiveRetentionStore : IArchiveStore
{
    Task ApplyRetentionAsync(JsonElement retention, CancellationToken cancellationToken = default);
    Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default);
    Task<JsonElement?> RetentionPageAsync(string afterRevision, CancellationToken cancellationToken = default);
    Task<byte[]> BodyChunkAsync(JsonElement artifactReference, long offset, CancellationToken cancellationToken = default);
}

public sealed class ArchiveReceiveInput
{
    public JsonElement Binding { get; set; }
    public JsonElement Status { get; set; }
    public JsonElement Page { get; set; }
    public JsonElement Request { get; set; }
    public IReadOnlyList<ArchiveArtifact> Artifacts { get; set; } = Array.Empty<ArchiveArtifact>();
}

public sealed class ArchiveArtifact
{
    public string ArtifactId { get; }
    public byte[] Body { get; }
    public ArchiveArtifact(string artifactId, byte[] body) { ArtifactId = artifactId; Body = body; }
}

public sealed class ArchiveReadRequest
{
    public JsonElement Identity { get; set; }
    /// <summary>原 selection 联合：recordIds，或 fromSequence/throughSequence。</summary>
    public JsonElement Selection { get; set; }
    public int MaxRecords { get; set; } = 128;
    public int MaxBytes { get; set; } = 1048576;
}

public sealed class ArchiveRecordPage
{
    public IReadOnlyList<JsonElement> Records { get; }
    public int Bytes { get; }
    public IReadOnlyList<string> MissingRecordIds { get; }
    public string? NextFromSequence { get; }
    public JsonElement SourceCoverage { get; }
    public ArchiveRecordPage(IReadOnlyList<JsonElement> records, int bytes, IReadOnlyList<string> missingRecordIds, string? nextFromSequence, JsonElement sourceCoverage)
    { Records = records; Bytes = bytes; MissingRecordIds = missingRecordIds; NextFromSequence = nextFromSequence; SourceCoverage = sourceCoverage.Clone(); }
}
