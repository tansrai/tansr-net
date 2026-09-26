using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteArchiveHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldPrefixIsReadOnlyUnderCurrentDeletionAuthorityAndNeverBecomesCurrentSource(bool encrypted)
    {
        using var old = new Fixture(encrypted); using var live = new Fixture(encrypted);
        using (var store = await SqliteArchiveStore.OpenAsync(old.Options()))
        { var ack = await store.ReceiveAsync(old.Input); await store.ConfirmAsync(old.Receipt(ack)); }
        var liveOptions = live.Options(); liveOptions.AuthorizeRetention = _ => { };
        using var current = await SqliteArchiveStore.OpenAsync(liveOptions);
        var first = await current.ReceiveAsync(live.Input); await current.ConfirmAsync(live.Receipt(first));
        var secondInput = Next(live, first); var second = await current.ReceiveAsync(secondInput);
        await current.ConfirmAsync(Replace(live.Receipt(second), "revision", Element("4")));
        live.RetentionRevision = "1";
        await current.ApplyRetentionAsync(Retention(live.Identity, secondInput.Page.GetProperty("records")[0], "0", "1", "delete-second"));

        var options = old.Options(StorageOpenMode.Reopen); options.ReadRetentionRevision = () => live.RetentionRevision;
        using (var ordinary = await SqliteArchiveStore.OpenAsync(options))
            Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => ordinary.BodyAsync(old.Reference))).Code);

        var before = File.ReadAllBytes(old.Path); var attributes = File.GetAttributes(old.Path); File.SetAttributes(old.Path, attributes | FileAttributes.ReadOnly);
        try
        {
            var view = await SqliteArchiveHistory.OpenAsync(options, ArchiveHistoryAuthority.FromStore(current));
            try
            {
                var page = await view.ReadPageAsync(new ArchiveReadRequest { Identity = old.Identity, Selection = Element(new { recordIds = new[] { "record" } }) });
                Assert.Equal("1", page.VersionHead!.Value.GetProperty("sequence").GetString());
                Assert.Equal("2", page.CurrentHead!.Value.GetProperty("sequence").GetString());
                Assert.Equal("1", page.RetentionRevision); Assert.Single(page.Records);
                Assert.Equal(old.Body, await view.ReadArtifactAsync("record", old.Reference));
                Assert.False((object)view is IArchiveStore);

                live.RetentionRevision = "2";
                await current.ApplyRetentionAsync(Retention(live.Identity, live.Input.Page.GetProperty("records")[0], "1", "2", "delete-first"));
                Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => view.ReadArtifactAsync("record", old.Reference))).Code);
            }
            finally { await view.CloseAsync(); }
            Assert.Equal(before, File.ReadAllBytes(old.Path));
            Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(old.Path) & FileAttributes.ReadOnly);
        }
        finally { File.SetAttributes(old.Path, attributes); }
        using var reopened = await SqliteArchiveStore.OpenAsync(options);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => reopened.BodyAsync(old.Reference))).Code);
    }

    [Fact]
    public async Task HistoryFactoryNeverCreatesOrUpgradesAMissingFileEvenWhenOptionsSayCreate()
    {
        using var old = new Fixture(); using var live = new Fixture();
        using var current = await SqliteArchiveStore.OpenAsync(live.Options()); var options = old.Options();
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveHistory.OpenAsync(options, ArchiveHistoryAuthority.FromStore(current)));
        Assert.False(File.Exists(old.Path)); Assert.Equal(StorageOpenMode.Create, options.Mode);
    }

    [Fact]
    public async Task ClosingRevokedEncryptedHistoryDoesNotRequestAKeyOrCurrentAuthority()
    {
        using var old = new Fixture(true); using var live = new Fixture(true);
        using (var store = await SqliteArchiveStore.OpenAsync(old.Options()))
        { var ack = await store.ReceiveAsync(old.Input); await store.ConfirmAsync(old.Receipt(ack)); }
        using var current = await SqliteArchiveStore.OpenAsync(live.Options()); var first = await current.ReceiveAsync(live.Input); await current.ConfirmAsync(live.Receipt(first));
        int reads = 0; old.Keys!.OnRead = () => reads++;
        var view = await SqliteArchiveHistory.OpenAsync(old.Options(StorageOpenMode.Reopen), ArchiveHistoryAuthority.FromStore(current));
        Assert.Equal(old.Body, await view.ReadArtifactAsync("record", old.Reference)); int before = reads;
        old.Keys!.Available = false; old.User = "revoked-user"; live.User = "revoked-user";
        await view.CloseAsync(); await view.CloseAsync(); Assert.Equal(before, reads);
    }

    [Fact]
    public async Task WrongKeyOrChangedIdentityCannotOpenOrModifyTheHistoricalFile()
    {
        using var old = new Fixture(true); using var live = new Fixture(true);
        using (var store = await SqliteArchiveStore.OpenAsync(old.Options()))
        { var ack = await store.ReceiveAsync(old.Input); await store.ConfirmAsync(old.Receipt(ack)); }
        using var current = await SqliteArchiveStore.OpenAsync(live.Options()); var before = File.ReadAllBytes(old.Path);
        var wrongKey = old.Options(StorageOpenMode.Reopen); wrongKey.KeyProvider = new WrongKey();
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveHistory.OpenAsync(wrongKey, ArchiveHistoryAuthority.FromStore(current)))).Code);
        var otherIdentity = old.Options(StorageOpenMode.Reopen); otherIdentity.Identity = Replace(old.Identity, "bindingId", Element("another-binding"));
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveHistory.OpenAsync(otherIdentity, ArchiveHistoryAuthority.FromStore(current)))).Code);
        Assert.Equal(before, File.ReadAllBytes(old.Path));
    }

    private static ArchiveReceiveInput Next(Fixture fixture, JsonElement previousAck)
    {
        var old = fixture.Input.Page.GetProperty("records")[0]; var payload = Replace(fixture.Reference, "artifactId", Element("body-2"));
        var values = old.EnumerateObject().Where(property => property.Name != "recordDigest").ToDictionary(property => property.Name, property => property.Value.Clone());
        values["recordId"] = Element("record-2"); values["sequence"] = Element("2"); values["turnId"] = Element("turn-2");
        values["predecessorDigest"] = old.GetProperty("recordDigest"); values["payload"] = payload;
        var unsigned = Element(values); values["recordDigest"] = Element(WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(unsigned)));
        var record = Element(values); WireJson.ValidateNamed("ArchiveRecord", record);
        return new ArchiveReceiveInput
        {
            Binding = Replace(fixture.Input.Binding, "revision", Element("3")),
            Status = Replace(Replace(Replace(fixture.Input.Status, "revision", Element("3")), "acknowledgedCoverage", previousAck.GetProperty("coverage")), "publishedThroughSequence", Element("2")),
            Page = Replace(Replace(Replace(fixture.Input.Page, "records", Element(new[] { record })), "nextAfterSequence", Element("2")), "publishedThroughSequence", Element("2")),
            Request = Replace(fixture.Input.Request, "requestId", Element("ack-2")),
            Artifacts = new[] { new ArchiveArtifact("body-2", (byte[])fixture.Body.Clone()) },
        };
    }

    private static JsonElement Retention(JsonElement identity, JsonElement record, string previous, string revision, string requestId)
        => Element(new { format = "archive-retention-v1", identity, previousRevision = previous, revision, requestId, records = new[] { new { recordId = record.GetProperty("recordId").GetString(), sequence = record.GetProperty("sequence").GetString(), recordDigest = record.GetProperty("recordDigest").GetString() } } });

    private sealed class WrongKey : IArchiveKeyProvider
    {
        public string KeyId => "key";
        public byte[] ReadKey() => Enumerable.Repeat((byte)213, 32).ToArray();
    }
}
