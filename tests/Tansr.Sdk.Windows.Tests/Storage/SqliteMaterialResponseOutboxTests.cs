using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteMaterialResponseOutboxTests
{
    [Fact]
    public async Task SavesOriginalCanonicalBytesAndRecoversAfterCloseWithoutClearingPending()
    {
        using var fixture = new Fixture();
        using (var outbox = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options()))
        {
            Assert.Null(await outbox.ReadAsync()); await outbox.SaveIfEmptyAsync(fixture.Response);
            Assert.Equal(Canonical(fixture.Response), Canonical((await outbox.ReadAsync())!.Value));
            await outbox.CloseAsync(); await outbox.CloseAsync();
        }
        using (var connection = OpenRaw(fixture.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT response FROM pending WHERE id=1";
            Assert.Equal(WireJson.EncodeControl(fixture.Response), (byte[])command.ExecuteScalar()!);
            command.CommandText = "SELECT json FROM metadata WHERE id=1";
            using var metadata = JsonDocument.Parse((string)command.ExecuteScalar()!);
            Assert.Equal(SqliteMaterialResponseOutbox.Format, metadata.RootElement.GetProperty("format").GetString());
        }
        fixture.AuthorizationRevision = "2";
        using var reopened = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.Equal(Canonical(fixture.Response), Canonical((await reopened.ReadAsync())!.Value));
        await reopened.ClearIfExactAsync(fixture.Response); Assert.Null(await reopened.ReadAsync());
        Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => reopened.ClearIfExactAsync(fixture.Response))).Code);
    }

    [Fact]
    public async Task CanonicalReplayIsIdempotentButAnotherPendingResponseCannotOverwriteOrClearIt()
    {
        using var fixture = new Fixture(); using var outbox = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options());
        await outbox.SaveIfEmptyAsync(fixture.Response);
        var reordered = Json(fixture.Response.EnumerateObject().Reverse().ToDictionary(property => property.Name, property => property.Value.Clone()));
        await outbox.SaveIfEmptyAsync(reordered);
        var other = ReplacePath(fixture.Response, new[] { "request", "requestId" }, Json("different-response"));
        Assert.Equal("pending_conflict", (await Assert.ThrowsAsync<StorageException>(() => outbox.SaveIfEmptyAsync(other))).Code);
        Assert.Equal("receipt_mismatch", (await Assert.ThrowsAsync<StorageException>(() => outbox.ClearIfExactAsync(other))).Code);
        Assert.Equal(Canonical(fixture.Response), Canonical((await outbox.ReadAsync())!.Value));
    }

    [Theory]
    [InlineData("bindingId", "other-binding")]
    [InlineData("sourceId", "other-source")]
    [InlineData("sourceGeneration", "other-source-generation")]
    [InlineData("target.sessionId", "other-session")]
    [InlineData("target.generations.historyEpoch", "other-history")]
    [InlineData("target.generations.deletionGeneration", "1")]
    [InlineData("target.generations.projectionRevision", "1")]
    public async Task ForeignResponseAndReopenedGenerationCannotUseTheOriginalPendingSlot(string path, string replacement)
    {
        using var fixture = new Fixture();
        using (var outbox = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options()))
        {
            var response = ReplacePath(fixture.Response, path.Split('.'), Json(replacement));
            Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => outbox.SaveIfEmptyAsync(response))).Code);
            Assert.Null(await outbox.ReadAsync()); await outbox.SaveIfEmptyAsync(fixture.Response);
        }
        var options = fixture.Options(StorageOpenMode.Reopen); options.Identity = ReplacePath(fixture.Identity, path.Split('.'), Json(replacement));
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteMaterialResponseOutbox.OpenAsync(options))).Code);
        using var reopened = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.Equal(Canonical(fixture.Response), Canonical((await reopened.ReadAsync())!.Value));
    }

    [Theory]
    [InlineData("applicationScopeId")]
    [InlineData("endUserId")]
    public async Task ChangedPrincipalCannotReadExistingPendingAndCloseDoesNotRequireAuthority(string principal)
    {
        using var fixture = new Fixture(); var outbox = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options());
        try
        {
            await outbox.SaveIfEmptyAsync(fixture.Response);
            if (principal == "applicationScopeId") fixture.Application = "another-app"; else fixture.User = "another-user";
            Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => outbox.ReadAsync())).Code);
        }
        finally { await outbox.CloseAsync(); }
        var foreign = fixture.Options(StorageOpenMode.Reopen);
        foreign.Identity = ReplacePath(fixture.Identity, new[] { "scope", principal }, Json(principal == "applicationScopeId" ? fixture.Application : fixture.User));
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteMaterialResponseOutbox.OpenAsync(foreign))).Code);
        fixture.Application = "app"; fixture.User = "user";
        using var restored = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.NotNull(await restored.ReadAsync());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("authorization")]
    [InlineData("cancel")]
    [InlineData("reentry")]
    public async Task FinalAuthorityCallbackCannotCommitAfterCancellationReentryOrScopeChange(string change)
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
        SqliteMaterialResponseOutbox? outbox = null; bool armed = false; int reads = 0;
        var options = fixture.Options(); options.ReadContext = () =>
        {
            if (armed && ++reads == 3)
            {
                switch (change)
                {
                    case "user": fixture.User = "other-user"; break;
                    case "authorization": fixture.AuthorizationRevision = "2"; break;
                    case "cancel": cancel.Cancel(); break;
                    case "reentry": try { outbox!.ReadAsync().GetAwaiter().GetResult(); } catch (StorageException error) { Assert.Equal("reentrant", error.Code); } break;
                }
            }
            return fixture.Scope;
        };
        outbox = await SqliteMaterialResponseOutbox.OpenAsync(options);
        using (outbox)
        {
            armed = true;
            if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.SaveIfEmptyAsync(fixture.Response, cancel.Token));
            else
            {
                var error = await Assert.ThrowsAsync<StorageException>(() => outbox.SaveIfEmptyAsync(fixture.Response));
                Assert.Equal(change == "user" ? "identity_mismatch" : change == "reentry" ? "reentrant" : "context_changed", error.Code);
            }
            armed = false; fixture.User = "user"; fixture.AuthorizationRevision = "1";
            Assert.Null(await outbox.ReadAsync()); await outbox.SaveIfEmptyAsync(fixture.Response); Assert.NotNull(await outbox.ReadAsync());
        }
    }

    [Fact]
    public async Task CancelledClearKeepsTheExactPendingResponseForRecovery()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource(); bool armed = false; int reads = 0;
        var options = fixture.Options(); options.ReadContext = () => { if (armed && ++reads == 3) cancel.Cancel(); return fixture.Scope; };
        using var outbox = await SqliteMaterialResponseOutbox.OpenAsync(options); await outbox.SaveIfEmptyAsync(fixture.Response); armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.ClearIfExactAsync(fixture.Response, cancel.Token));
        armed = false; Assert.Equal(Canonical(fixture.Response), Canonical((await outbox.ReadAsync())!.Value));
    }

    [Fact]
    public async Task OpenSnapshotsEveryOptionBeforeCallingHostContext()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); string originalPath = options.Path; int calls = 0;
        options.ReadContext = () =>
        {
            calls++;
            options.Path = System.IO.Path.Combine(fixture.Directory, "unexpected.sqlite"); options.Mode = StorageOpenMode.Reopen;
            options.Identity = Json(new { }); options.MaxPages = 0; options.ReadContext = () => throw new InvalidOperationException("changed callback must not replace original");
            return fixture.Scope;
        };
        using (var outbox = await SqliteMaterialResponseOutbox.OpenAsync(options))
        { await outbox.SaveIfEmptyAsync(fixture.Response); Assert.True(calls >= 2); Assert.True(File.Exists(originalPath)); Assert.False(File.Exists(options.Path)); }
        using var reopened = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options(StorageOpenMode.Reopen)); Assert.NotNull(await reopened.ReadAsync());
    }

    [Fact]
    public async Task SqliteCommitFailureClosesTheGateUntilTheOriginalFileIsReopened()
    {
        using var fixture = new Fixture(); var outbox = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options());
        try
        {
            var connection = (SqliteConnection)typeof(SqliteMaterialResponseOutbox).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(outbox)!;
            int commits = 0;
            // 真 SQLite COMMIT hook 否决提交；此例验证不确定提交分支，不冒充已提交后丢返回包。
            SQLitePCL.raw.sqlite3_commit_hook(connection.Handle!, _ => { commits++; return 1; }, null);
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => outbox.SaveIfEmptyAsync(fixture.Response))).Code);
            SQLitePCL.raw.sqlite3_commit_hook(connection.Handle!, null, null);
            Assert.Equal(1, commits);
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => outbox.ReadAsync())).Code);
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => outbox.ClearIfExactAsync(fixture.Response))).Code);
        }
        finally { await outbox.CloseAsync(); }
        using var reopened = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options(StorageOpenMode.Reopen));
        Assert.Null(await reopened.ReadAsync()); await reopened.SaveIfEmptyAsync(fixture.Response); Assert.NotNull(await reopened.ReadAsync());
    }

    [Fact]
    public async Task PhysicalQuotaFailureRollsBackTheSinglePendingSlot()
    {
        using var fixture = new Fixture(); var options = fixture.Options(); options.MaxPages = 8;
        var outbox = await SqliteMaterialResponseOutbox.OpenAsync(options);
        var largeResults = Enumerable.Range(0, 32).Select(index => new
        {
            recordId = "record-" + index,
            digest = new string('b', 64),
            payload = new { uploadId = "payload-" + index },
            attachments = Enumerable.Range(0, 32).Select(attachment => new { uploadId = index + "-" + attachment + "-" + new string('u', 110) }).ToArray()
        }).ToArray();
        var large = ReplacePath(fixture.Response, new[] { "results" }, Json(largeResults)); WireJson.ValidateNamed("MaterialResponseRequest", large);
        try
        {
            var error = await Assert.ThrowsAsync<StorageException>(() => outbox.SaveIfEmptyAsync(large));
            Assert.Contains(error.Code, new[] { "storage_error", "reconciliation_required" });
        }
        finally { await outbox.CloseAsync(); }
        options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteMaterialResponseOutbox.OpenAsync(options);
        Assert.Null(await reopened.ReadAsync()); await reopened.SaveIfEmptyAsync(fixture.Response); Assert.NotNull(await reopened.ReadAsync());
    }

    [Fact]
    public async Task CorruptCanonicalResponseIsNotTreatedAsAnEmptyOutbox()
    {
        using var fixture = new Fixture();
        using (var outbox = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options())) await outbox.SaveIfEmptyAsync(fixture.Response);
        using (var connection = OpenRaw(fixture.Path))
        using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE pending SET response=$bad WHERE id=1"; command.Parameters.AddWithValue("$bad", new byte[] { (byte)'{', (byte)'}' }); command.ExecuteNonQuery(); }
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteMaterialResponseOutbox.OpenAsync(fixture.Options(StorageOpenMode.Reopen)))).Code);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        await Assert.ThrowsAsync<StorageException>(() => SqliteMaterialResponseOutbox.OpenAsync(fixture.Options()));
    }

    [Fact]
    public async Task DuplicateRecordResultsAndCancelledCallsNeverCreatePending()
    {
        using var fixture = new Fixture(); using var outbox = await SqliteMaterialResponseOutbox.OpenAsync(fixture.Options());
        var result = fixture.Response.GetProperty("results")[0]; var duplicate = ReplacePath(fixture.Response, new[] { "results" }, Json(new[] { result, result }));
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<StorageException>(() => outbox.SaveIfEmptyAsync(duplicate))).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.SaveIfEmptyAsync(fixture.Response, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.CloseAsync(cancelled.Token));
        Assert.Null(await outbox.ReadAsync());
    }

    private static SqliteConnection OpenRaw(string path)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open(); return connection; }
    private static string Canonical(JsonElement value) => WireJson.CanonicalString(value);
    private static JsonElement Json(object value) => WireJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value), 262144);
    private static JsonElement ReplacePath(JsonElement value, IReadOnlyList<string> path, JsonElement replacement, int index = 0)
    {
        var properties = value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        properties[path[index]] = index + 1 == path.Count ? replacement : ReplacePath(properties[path[index]], path, replacement, index + 1); return Json(properties);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr-net-material-outbox-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "material-outbox.sqlite");
        internal string Application { get; set; } = "app";
        internal string User { get; set; } = "user";
        internal string AuthorizationRevision { get; set; } = "1";
        internal JsonElement Scope => Json(new { applicationScopeId = Application, endUserId = User, authorizationRevision = AuthorizationRevision });
        internal JsonElement Identity { get; }
        internal JsonElement Response { get; }
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var generations = new { historyEpoch = "history", deletionGeneration = "0", projectionRevision = "0" };
            Identity = Json(new { scope = new { applicationScopeId = "app", endUserId = "user" }, bindingId = "binding", target = new { sessionId = "session", generations }, sourceId = "source", sourceGeneration = "source-generation" });
            Response = Json(new
            {
                protocol = "sdk2-ext-v1",
                request = new { operationEpoch = "epoch", requestId = "response-one" },
                bindingId = "binding",
                materialRequestId = "material",
                target = new { sessionId = "session", generations, sourceSnapshotDigest = new string('a', 64) },
                sourceId = "source",
                sourceGeneration = "source-generation",
                results = new[] { new { recordId = "record", digest = new string('b', 64), payload = new { uploadId = "payload-upload" }, attachments = Array.Empty<object>() } }
            });
            WireJson.ValidateNamed("MaterialResponseRequest", Response);
        }
        internal SqliteMaterialResponseOutboxOptions Options(StorageOpenMode mode = StorageOpenMode.Create)
            => new() { Path = Path, Mode = mode, Identity = Identity, ReadContext = () => Scope };
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
