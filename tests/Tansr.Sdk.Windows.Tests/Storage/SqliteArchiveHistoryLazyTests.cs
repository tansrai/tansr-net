using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;
using static Tansr.Sdk.Windows.Tests.Storage.SqliteArchiveStoreTests;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteArchiveHistoryLazyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAndOneRecordPageDoNotReadUnselectedBodiesButSelectedBodyIsFullyVerified(bool encrypted)
    {
        // 跨三个原 GCM 块；一次记录页读取不读正文，选中 artifact 后仍验证完整 524305 字节。
        using var fixture = new Fixture(encrypted, System.Text.Encoding.UTF8.GetBytes(new string('q', 524305)));
        var authority = await Populate(fixture, twoRecords: true);
        Mutate(fixture, "UPDATE artifacts SET body=zeroblob(length(body)) WHERE id='body-2'");
        int keys = 0;
        if (encrypted) fixture.Keys!.OnRead = () => { if (++keys > 1) throw new InvalidOperationException("open must only verify key_check, never decrypt archive bodies"); };
        var history = await SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority);
        if (encrypted) { Assert.Equal(1, keys); fixture.Keys!.OnRead = null; }
        try
        {
            int metadataKeyReads = 0;
            if (encrypted)
            {
                // 用空页测出原身份/密钥后验成本；非空页不能额外打开任一正文的 GCM 块。
                fixture.Keys!.OnRead = () => metadataKeyReads++;
                Assert.Empty((await history.ReadPageAsync(new ArchiveReadRequest { Identity = fixture.Identity, Selection = Element(new { recordIds = new[] { "absent" } }) })).Records);
                keys = 0;
                fixture.Keys.OnRead = () => { if (++keys > metadataKeyReads) throw new InvalidOperationException("record page must not decrypt archive bodies"); };
            }
            var page = await history.ReadPageAsync(new ArchiveReadRequest
            { Identity = fixture.Identity, Selection = Element(new { fromSequence = "1", throughSequence = "2" }), MaxRecords = 1 });
            Assert.Single(page.Records); Assert.Equal("2", page.NextFromSequence);
            if (encrypted) { Assert.Equal(metadataKeyReads, keys); fixture.Keys!.OnRead = null; }
            Assert.Equal(fixture.Body, await history.ReadArtifactAsync("record", fixture.Reference));
            Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => history.ReadArtifactAsync("record-2", authority.Records[1].GetProperty("payload")))).Code);
        }
        finally { await history.CloseAsync(); }
        // 普通 source 写者不能把按需历史查看当作完整审计。
        await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveStore.OpenAsync(fixture.Options(StorageOpenMode.Reopen)));
    }

    [Theory]
    [InlineData("missing-link")]
    [InlineData("extra-link")]
    [InlineData("artifact-reference")]
    [InlineData("database-sequence")]
    [InlineData("foreign-record")]
    [InlineData("oversized-record")]
    public async Task SelectedRecordMetadataIsValidatedBeforeItCanBeReturned(string corruption)
    {
        using var fixture = new Fixture(); var authority = await Populate(fixture);
        switch (corruption)
        {
            case "missing-link": Mutate(fixture, "DELETE FROM record_artifacts WHERE record_id='record'"); break;
            case "extra-link": Mutate(fixture, "INSERT INTO record_artifacts VALUES('foreign-body','record')"); break;
            case "artifact-reference": Mutate(fixture, "UPDATE artifacts SET json=$json WHERE id='body'", ("$json", WireJson.CanonicalString(Replace(fixture.Reference, "sha256", Element(new string('b', 64)))))); break;
            case "database-sequence": Mutate(fixture, "UPDATE records SET sequence=2 WHERE record_id='record'"); break;
            case "foreign-record":
                var record = authority.Records[0]; var foreign = Redigest(Replace(record, "target", Replace(record.GetProperty("target"), "sessionId", Element("foreign-session"))));
                Mutate(fixture, "UPDATE records SET json=$json WHERE record_id='record'", ("$json", WireJson.CanonicalString(foreign))); break;
            case "oversized-record": Mutate(fixture, "UPDATE records SET json=$json WHERE record_id='record'", ("$json", new string(' ', 1048577))); break;
        }
        var history = await SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority);
        try
        {
            var error = await Assert.ThrowsAsync<StorageException>(() => history.ReadPageAsync(Request(fixture)));
            Assert.Equal(corruption == "foreign-record" ? "identity_mismatch" : "integrity_mismatch", error.Code);
        }
        finally { await history.CloseAsync(); }
    }

    [Fact]
    public async Task AValidArtifactHashDoesNotReplaceTheRecordsPayloadDomainDigest()
    {
        using var fixture = new Fixture(); var authority = await Populate(fixture);
        var record = Redigest(Replace(authority.Records[0], "payloadDigest", Element(new string('b', 64))));
        authority.Records[0] = record;
        Mutate(fixture, "UPDATE records SET json=$json WHERE record_id='record'; UPDATE state SET head=$head WHERE id=1",
            ("$json", WireJson.CanonicalString(record)), ("$head", WireJson.CanonicalString(authority.Head)));
        var history = await SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority);
        try
        {
            Assert.Single((await history.ReadPageAsync(Request(fixture))).Records);
            Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => history.ReadArtifactAsync("record", fixture.Reference))).Code);
        }
        finally { await history.CloseAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossConnectionChangesWithUnchangedHeadAreRejectedEvenAfterBodyRead(bool afterBody)
    {
        using var fixture = new Fixture(); var authority = await Populate(fixture);
        var history = await SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority);
        int checks = 0;
        authority.BeforeRead = _ =>
        {
            // ReadArtifact 先查权威再读正文，第二次查权威发生在正文已读之后。
            if (++checks == (afterBody ? 2 : 1)) Mutate(fixture, "UPDATE artifacts SET body=zeroblob(length(body)) WHERE id='body'");
            return Task.CompletedTask;
        };
        try
        {
            Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => history.ReadArtifactAsync("record", fixture.Reference))).Code);
            Assert.Equal(afterBody ? 2 : 1, checks);
        }
        finally { await history.CloseAsync(); }
    }

    [Fact]
    public async Task OpenDetectsAConcurrentMutationFromTheKeyProviderWithoutScanningAnyBodies()
    {
        using var fixture = new Fixture(true); var authority = await Populate(fixture);
        fixture.Keys!.OnRead = () => Mutate(fixture, "UPDATE artifacts SET body=zeroblob(length(body)) WHERE id='body'");
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority))).Code);
        fixture.Keys.OnRead = null;
    }

    [Fact]
    public async Task CancellationDuringTheKeyCheckClosesTheHistoricalConnection()
    {
        using var fixture = new Fixture(true); var authority = await Populate(fixture);
        using var cancel = new CancellationTokenSource(); fixture.Keys!.OnRead = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority, cancel.Token));
        fixture.Keys.OnRead = null;
        // 打开失败必须释放 pinned file 与 SQLite；独占打开不需要停止其它进程或删除资料。
        using var exclusive = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public async Task IndependentReadOnlyViewsDoNotShareSqliteReadersAndCancellationReleasesTheView()
    {
        using var fixture = new Fixture(); var authority = await Populate(fixture);
        var otherAuthority = new Authority(authority.Records);
        var first = await SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority);
        var second = await SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), otherAuthority);
        using var cancel = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        authority.BeforeRead = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        try
        {
            var pending = first.ReadPageAsync(Request(fixture), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(fixture.Body, await second.ReadArtifactAsync("record", fixture.Reference));
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            authority.BeforeRead = null;
            Assert.Equal(fixture.Body, await first.ReadArtifactAsync("record", fixture.Reference));
        }
        finally { cancel.Cancel(); await first.CloseAsync(); await second.CloseAsync(); }
    }

    [Fact]
    public async Task AConcurrentReadOfTheSameViewIsRejectedWithoutReenteringItsSqliteConnection()
    {
        using var fixture = new Fixture(); var authority = await Populate(fixture);
        var history = await SqliteArchiveHistory.OpenAsync(fixture.Options(StorageOpenMode.Reopen), authority);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        authority.BeforeRead = async _ => { entered.TrySetResult(); await release.Task; };
        try
        {
            var pending = history.ReadPageAsync(Request(fixture)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => history.ReadArtifactAsync("record", fixture.Reference))).Code);
            release.TrySetResult(); Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)))).Code);
            authority.BeforeRead = null; Assert.Single((await history.ReadPageAsync(Request(fixture))).Records);
        }
        finally { release.TrySetResult(); await history.CloseAsync(); }
    }

    private static ArchiveReadRequest Request(Fixture fixture) => new() { Identity = fixture.Identity, Selection = Element(new { recordIds = new[] { "record" } }) };

    private static async Task<Authority> Populate(Fixture fixture, bool twoRecords = false)
    {
        var records = new List<JsonElement> { fixture.Input.Page.GetProperty("records")[0] };
        using var store = await SqliteArchiveStore.OpenAsync(fixture.Options());
        var ack = await store.ReceiveAsync(fixture.Input); await store.ConfirmAsync(fixture.Receipt(ack));
        if (twoRecords)
        {
            var input = SqliteArchiveHistoryTests.Next(fixture, ack); var second = await store.ReceiveAsync(input);
            await store.ConfirmAsync(Replace(fixture.Receipt(second), "revision", Element("4")));
            records.Add(input.Page.GetProperty("records")[0]);
        }
        return new Authority(records);
    }

    private static JsonElement Redigest(JsonElement record)
    {
        var values = record.EnumerateObject().Where(item => item.Name != "recordDigest").ToDictionary(item => item.Name, item => item.Value.Clone());
        values["recordDigest"] = Element(WireJson.DomainDigest("tansr.sdk2.record.v1", WireJson.EncodeControl(Element(values))));
        return Element(values);
    }

    private static void Mutate(Fixture fixture, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private sealed class Authority : IArchiveHistoryAuthority
    {
        internal List<JsonElement> Records { get; }
        internal Func<CancellationToken, Task>? BeforeRead { get; set; }
        internal JsonElement Head => Element(new { sequence = Records[Records.Count - 1].GetProperty("sequence"), recordDigest = Records[Records.Count - 1].GetProperty("recordDigest") });
        internal Authority(IEnumerable<JsonElement> records) => Records = records.Select(record => record.Clone()).ToList();
        public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(Head);
        public Task<string> RetentionRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult("0");
        public async Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default)
        {
            if (BeforeRead != null) await BeforeRead(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var ids = request.Selection.GetProperty("recordIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
            var records = ids.Select(id => Records.SingleOrDefault(record => record.GetProperty("recordId").GetString() == id)).Where(record => record.ValueKind != JsonValueKind.Undefined).ToArray();
            var missing = ids.Where(id => !records.Any(record => record.GetProperty("recordId").GetString() == id)).ToArray();
            return new ArchiveRecordPage(records, records.Sum(record => WireJson.EncodeControl(record).Length), missing, null, Element(new { }));
        }
    }
}
