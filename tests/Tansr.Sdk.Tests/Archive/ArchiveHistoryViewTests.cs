using System.Text;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Tests.Archive;

public sealed class ArchiveHistoryViewTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReturnsHistoricalFactsWithoutExposingWriteOrMaterialAuthority()
    {
        var fixture = new Fixture(); fixture.Current.AddNext(); fixture.Current.Revision = "3";
        var view = fixture.View(); var page = await view.ReadPageAsync(fixture.Request("record-1"));
        Assert.Equal("archive-history-view-v1", page.Format);
        Assert.Equal("1", page.VersionHead!.Value.GetProperty("sequence").GetString());
        Assert.Equal("2", page.CurrentHead!.Value.GetProperty("sequence").GetString());
        Assert.Equal("3", page.RetentionRevision); Assert.Single(page.Records);
        Assert.Equal(fixture.Version.Page(fixture.Request("record-1")).Bytes, page.Bytes);
        Assert.Empty(page.MissingRecordIds); Assert.Null(page.NextFromSequence);
        Assert.False(typeof(IArchiveStore).IsAssignableFrom(typeof(ArchiveHistoryView)));
        Assert.Equal(0, fixture.Version.Writes); Assert.Equal(0, fixture.Current.Writes);
        await view.CloseAsync(); await view.CloseAsync();
        Assert.Equal(1, fixture.Version.Closes); Assert.Equal(0, fixture.Current.Closes);
        Assert.Equal("closed", (await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(fixture.Request("record-1")))).Code);
    }

    [Fact]
    public async Task ReadsExactRecordArtifactAndChecksCurrentRecordOnBothSidesOfBodyRead()
    {
        var fixture = new Fixture(); var view = fixture.View();
        Assert.Equal(fixture.Version.Body, await view.ReadArtifactAsync("record-1", fixture.Reference));
        Assert.Equal(2, fixture.Current.RecordReads); Assert.Equal(1, fixture.Version.BodyReads);
        var unrelated = Replace(fixture.Reference, "artifactId", Json("other-artifact"));
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => view.ReadArtifactAsync("record-1", unrelated))).Code);
        Assert.Equal(1, fixture.Version.BodyReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentDeletionOrChangedRecordCannotBeReadFromTheOldVersion(bool changed)
    {
        var fixture = new Fixture(); var view = fixture.View();
        if (changed) fixture.Current.Records[0] = Replace(fixture.Current.Records[0], "turnState", Json("cancelled"));
        else fixture.Current.Hidden.Add("record-1");
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(fixture.Request("record-1")))).Code);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => view.ReadArtifactAsync("record-1", fixture.Reference))).Code);
        Assert.Equal(0, fixture.Version.BodyReads);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("authorization")]
    [InlineData("retention")]
    [InlineData("head")]
    [InlineData("head-digest")]
    public async Task ChangesDuringOldPageReadPreventAnyResult(string change)
    {
        var fixture = new Fixture(); var view = fixture.View();
        fixture.Version.Read = (request, _) =>
        {
            var page = fixture.Version.Page(request);
            switch (change)
            {
                case "user": fixture.User = "other-user"; break;
                case "authorization": fixture.AuthorizationRevision = "2"; break;
                case "retention": fixture.Current.Revision = "1"; break;
                case "head": fixture.Current.AddNext(); break;
                case "head-digest": fixture.Current.Head = Replace(fixture.Current.Head!.Value, "recordDigest", Json(new string('f', 64))); break;
            }
            return Task.FromResult(page);
        };
        var error = await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(fixture.Request("record-1")));
        Assert.Equal(change == "user" ? "identity_mismatch" : "context_changed", error.Code);
    }

    [Fact]
    public async Task DeletionWhileBodyIsLoadingPreventsReturningItsBytes()
    {
        var fixture = new Fixture(); var view = fixture.View();
        fixture.Version.ReadBody = (_, _) =>
        {
            fixture.Current.Hidden.Add("record-1"); fixture.Current.Revision = "1";
            return Task.FromResult((byte[])fixture.Version.Body.Clone());
        };
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => view.ReadArtifactAsync("record-1", fixture.Reference))).Code);
        Assert.Equal(1, fixture.Version.BodyReads);
    }

    [Fact]
    public async Task CancellationWaitsForTheOutstandingReadButNeverReturnsItsLateBytes()
    {
        var fixture = new Fixture(); var view = fixture.View(); using var cancel = new CancellationTokenSource();
        var entered = Signal(); var release = Signal();
        fixture.Version.ReadBody = async (_, token) =>
        {
            Assert.Equal(cancel.Token, token); entered.TrySetResult();
            await release.Task.ConfigureAwait(false); return (byte[])fixture.Version.Body.Clone();
        };
        var read = view.ReadArtifactAsync("record-1", fixture.Reference, cancel.Token);
        await entered.Task.WaitAsync(Deadline); cancel.Cancel(); Assert.False(read.IsCompleted);
        release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Deadline));
        fixture.Version.ReadBody = null;
        Assert.Equal(fixture.Version.Body, await view.ReadArtifactAsync("record-1", fixture.Reference));
    }

    [Fact]
    public async Task SwallowedReentryPoisonsTheOuterReadAndTheNextIndependentReadRecovers()
    {
        var fixture = new Fixture(); var view = fixture.View();
        fixture.Version.Read = async (request, token) =>
        {
            Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(request, token))).Code);
            return fixture.Version.Page(request);
        };
        Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(fixture.Request("record-1")))).Code);
        fixture.Version.Read = null; Assert.Single((await view.ReadPageAsync(fixture.Request("record-1"))).Records);
    }

    [Fact]
    public async Task CloseDuringActiveReadCannotDisposeTheStoreUnderTheRead()
    {
        var fixture = new Fixture(); var view = fixture.View(); var entered = Signal(); var release = Signal();
        fixture.Version.Read = async (request, _) =>
        {
            entered.TrySetResult(); await release.Task.ConfigureAwait(false); return fixture.Version.Page(request);
        };
        var read = view.ReadPageAsync(fixture.Request("record-1")); await entered.Task.WaitAsync(Deadline);
        Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => view.CloseAsync())).Code);
        Assert.Equal(0, fixture.Version.Closes); release.TrySetResult();
        Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => read.WaitAsync(Deadline))).Code);
        await view.CloseAsync(); Assert.Equal(1, fixture.Version.Closes); Assert.Equal(0, fixture.Current.Closes);
    }

    [Fact]
    public async Task CopiesCallerSelectionBeforeTheFirstAwait()
    {
        var fixture = new Fixture(); var view = fixture.View(); var entered = Signal(); var release = Signal(); int reads = 0;
        fixture.Current.ReadHead = async _ =>
        {
            if (Interlocked.Increment(ref reads) == 1) { entered.TrySetResult(); await release.Task.ConfigureAwait(false); }
            return fixture.Current.Head;
        };
        var request = fixture.Request("record-1"); var read = view.ReadPageAsync(request); await entered.Task.WaitAsync(Deadline);
        request.Selection = Json(new { recordIds = new[] { "other-record" } }); request.MaxBytes = 1;
        release.TrySetResult(); Assert.Equal("record-1", (await read.WaitAsync(Deadline)).Records[0].GetProperty("recordId").GetString());
    }

    [Fact]
    public async Task VersionAheadOfCurrentAuthorityIsRejectedBeforeReadingAnyRecords()
    {
        var fixture = new Fixture(); fixture.Version.AddNext(); var view = fixture.View();
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(fixture.Request("record-1")))).Code);
        Assert.Equal(0, fixture.Version.RecordReads); Assert.Equal(0, fixture.Current.RecordReads);
    }

    [Fact]
    public async Task MissingHistoricalIdsRemainMissingAndDoNotReadArtifactBodies()
    {
        var fixture = new Fixture(); var page = await fixture.View().ReadPageAsync(fixture.Request("missing"));
        Assert.Empty(page.Records); Assert.Equal(new[] { "missing" }, page.MissingRecordIds);
        Assert.Equal(0, fixture.Current.RecordReads); Assert.Equal(0, fixture.Version.BodyReads);
    }

    [Fact]
    public async Task RangePagesKeepTheirOriginalCursorAndRejectAFalseCompletedRange()
    {
        var fixture = new Fixture(); fixture.Version.AddNext(); fixture.Current.AddNext(); var view = fixture.View();
        bool falseComplete = false;
        fixture.Version.Read = (_, _) =>
        {
            var first = fixture.Version.Page(fixture.Request("record-1"));
            return Task.FromResult(new ArchiveRecordPage(first.Records, first.Bytes, first.MissingRecordIds, falseComplete ? null : "2", first.SourceCoverage));
        };
        var request = new ArchiveReadRequest { Identity = fixture.Identity, Selection = Json(new { fromSequence = "1", throughSequence = "2" }), MaxRecords = 1 };
        var page = await view.ReadPageAsync(request); Assert.Single(page.Records); Assert.Equal("2", page.NextFromSequence);
        falseComplete = true;
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(request))).Code);
    }

    [Fact]
    public async Task OldStoreCannotSubstituteAnotherAuthorizedRecordForTheRequestedId()
    {
        var fixture = new Fixture(); fixture.Version.AddNext(); fixture.Current.AddNext(); var view = fixture.View();
        fixture.Version.Read = (_, _) => Task.FromResult(fixture.Version.Page(fixture.Request("record-2")));
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(fixture.Request("record-1")))).Code);
        Assert.Equal(0, fixture.Current.RecordReads);
    }

    [Fact]
    public async Task BodyMustMatchItsOriginalArtifactHash()
    {
        var fixture = new Fixture(); fixture.Version.ReadBody = (_, _) => Task.FromResult(new byte[fixture.Version.Body.Length]);
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => fixture.View().ReadArtifactAsync("record-1", fixture.Reference))).Code);
    }

    [Fact]
    public async Task CancellingBeforeEntryAndRejectingForeignIdentityDoNotReadEitherStore()
    {
        var fixture = new Fixture(); var view = fixture.View(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.ReadPageAsync(fixture.Request("record-1"), cancelled.Token));
        var request = fixture.Request("record-1"); request.Identity = Replace(fixture.Identity, "sourceId", Json("other-source"));
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => view.ReadPageAsync(request))).Code);
        Assert.Equal(0, fixture.Current.HeadReads); Assert.Equal(0, fixture.Version.HeadReads);
    }

    [Fact]
    public async Task RevokedAuthorityCanStillCloseTheOwnedVersionWithoutReadingCurrentAuthority()
    {
        var fixture = new Fixture(); var view = fixture.View(); fixture.User = "revoked-user";
        fixture.Current.ReadHead = _ => throw new InvalidOperationException("revoked authority must not be consulted while closing");
        await view.CloseAsync(); Assert.Equal(1, fixture.Version.Closes); Assert.Equal(0, fixture.Current.HeadReads);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonElement Json(object value) => WireJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value), 1048576);
    private static JsonElement Replace(JsonElement value, string name, JsonElement replacement)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
        { writer.WriteStartObject(); foreach (var property in value.EnumerateObject()) { if (property.Name == name) { writer.WritePropertyName(name); replacement.WriteTo(writer); } else property.WriteTo(writer); } writer.WriteEndObject(); }
        return WireJson.Parse(stream.ToArray());
    }

    private sealed class Fixture
    {
        internal string User { get; set; } = "user";
        internal string AuthorizationRevision { get; set; } = "1";
        internal JsonElement Scope => Json(new { applicationScopeId = "app", endUserId = User, authorizationRevision = AuthorizationRevision });
        internal JsonElement Identity { get; }
        internal JsonElement Reference => Version.Records[0].GetProperty("payload");
        internal FakeStore Version { get; } = new();
        internal FakeStore Current { get; } = new();
        internal Fixture()
        {
            Identity = Json(new { scope = new { applicationScopeId = "app", endUserId = "user" }, bindingId = "binding", target = new { sessionId = "session", generations = new { historyEpoch = "history", deletionGeneration = "0", projectionRevision = "0" } }, sourceId = "source", sourceGeneration = "source-generation" });
        }
        internal ArchiveHistoryView View() => new(Version, ArchiveHistoryAuthority.FromStore(Current), Identity, () => Scope);
        internal ArchiveReadRequest Request(params string[] ids) => new() { Identity = Identity, Selection = Json(new { recordIds = ids }) };
    }

    private sealed class FakeStore : IArchiveRetentionStore
    {
        internal List<JsonElement> Records { get; } = new();
        internal HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
        internal byte[] Body { get; } = Encoding.UTF8.GetBytes("synthetic history body 中文");
        internal JsonElement? Head { get; set; }
        internal string Revision { get; set; } = "0";
        internal int HeadReads, RecordReads, BodyReads, Writes, Closes;
        internal Func<CancellationToken, Task<JsonElement?>>? ReadHead { get; set; }
        internal Func<ArchiveReadRequest, CancellationToken, Task<ArchiveRecordPage>>? Read { get; set; }
        internal Func<JsonElement, CancellationToken, Task<byte[]>>? ReadBody { get; set; }
        internal FakeStore() => AddNext();
        internal void AddNext()
        {
            string sequence = (Records.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var payload = Json(new { artifactId = "artifact-" + sequence, sourceId = "source", bytes = Body.Length, sha256 = WireJson.Sha256(Body), mediaType = "text/plain" });
            var unsigned = Json(new
            {
                recordId = "record-" + sequence,
                sequence,
                target = new { sessionId = "session", sourceSnapshotDigest = new string('a', 64), generations = new { historyEpoch = "history", deletionGeneration = "0", projectionRevision = "0" } },
                turnId = "turn-" + sequence,
                recordKind = "turn",
                turnState = "completed",
                predecessorDigest = Head?.GetProperty("recordDigest").GetString() ?? new string('0', 64),
                payload,
                attachments = Array.Empty<object>(),
                payloadDigest = WireJson.DomainDigest("tansr.sdk2.payload.v1", Body)
            });
            var values = unsigned.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            values.Add("recordDigest", Json(WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(unsigned))));
            var record = Json(values); WireJson.ValidateNamed("ArchiveRecord", record); Records.Add(record);
            Head = Json(new { sequence, recordDigest = record.GetProperty("recordDigest").GetString() });
        }
        internal ArchiveRecordPage Page(ArchiveReadRequest request)
        {
            var ids = request.Selection.GetProperty("recordIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
            var records = new List<JsonElement>(); var missing = new List<string>();
            foreach (string id in ids)
            {
                var record = Records.FirstOrDefault(item => item.GetProperty("recordId").GetString() == id);
                if (Hidden.Contains(id) || record.ValueKind == JsonValueKind.Undefined) missing.Add(id); else records.Add(record);
            }
            return new ArchiveRecordPage(records, records.Sum(record => WireJson.EncodeControl(record).Length), missing, null, Json(new { }));
        }
        public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) { HeadReads++; return ReadHead?.Invoke(cancellationToken) ?? Task.FromResult(Head); }
        public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default) { RecordReads++; return Read?.Invoke(request, cancellationToken) ?? Task.FromResult(Page(request)); }
        public Task<byte[]> BodyAsync(JsonElement artifactReference, CancellationToken cancellationToken = default) { BodyReads++; return ReadBody?.Invoke(artifactReference, cancellationToken) ?? Task.FromResult((byte[])Body.Clone()); }
        public Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Revision);
        public Task CloseAsync() { Closes++; return Task.CompletedTask; }
        public Task<JsonElement> ReceiveAsync(ArchiveReceiveInput input, CancellationToken cancellationToken = default) { Writes++; throw new InvalidOperationException("history must never receive"); }
        public Task ConfirmAsync(JsonElement receipt, CancellationToken cancellationToken = default) { Writes++; throw new InvalidOperationException("history must never confirm"); }
        public Task ApplyRetentionAsync(JsonElement retention, CancellationToken cancellationToken = default) { Writes++; throw new InvalidOperationException("history must never apply retention"); }
        public Task<JsonElement?> PendingAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("history must never obtain ACK");
        public Task<JsonElement> CoverageAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("history must never become a material source");
        public Task<JsonElement?> RetentionPageAsync(string afterRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<byte[]> BodyChunkAsync(JsonElement artifactReference, long offset, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
