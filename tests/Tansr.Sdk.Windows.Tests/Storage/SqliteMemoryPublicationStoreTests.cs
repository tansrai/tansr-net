using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class SqliteMemoryPublicationStoreTests
{
    [Fact]
    public async Task PublicationCasAndOriginalTransferReceiptsSurviveReopenWithoutRewritingTheWinner()
    {
        using var f = new Fixture(); byte[] first = Encoding.UTF8.GetBytes("{\"memory\":\"偏好 😀\"}"), second = Encoding.UTF8.GetBytes("other memory");
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options()))
        {
            Assert.True(store.AtomicDurablePublication); Assert.Equal(Text(f.Identity), Text(store.Identity));
            Assert.Equal(JsonValueKind.Null, (await f.Run(store, "head")).GetProperty("publication").ValueKind);
            await f.Stage(store, "first", first); await f.Stage(store, "second", second);
            var committed = await f.Run(store, "commit", new { transferId = "first" }); AssertTransfer(committed, "committed", first.Length, WireJson.Sha256(first));
            var conflict = await f.Run(store, "commit", new { transferId = "second" }); AssertTransfer(conflict, "conflict", second.Length, null);
            Assert.Equal(Text(committed), Text(await f.Run(store, "commit", new { transferId = "first" })));
            Assert.Equal(Text(conflict), Text(await f.Run(store, "commit", new { transferId = "second" })));
            Assert.Equal("request_conflict", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => f.Run(store, "begin", f.Begin("first", second)))).Code);
            var cap = await store.GetCapacityAsync(); Assert.Equal(2, cap.StoredTransfers); Assert.Equal(0, cap.StagingBytes); Assert.Equal(f.MaxTransfers - 2, cap.RemainingTransfers); Assert.True(cap.AllocatedPages > 0);
        }
        using (var db = Raw(f.Path))
        {
            Assert.Equal(first, Scalar(db, "SELECT body FROM publication")); Assert.Equal(2L, Scalar(db, "SELECT count(*) FROM transfers")); Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM transfers WHERE body IS NOT NULL"));
        }
        using (var reopened = await SqliteMemoryPublicationStore.OpenAsync(f.Options(StorageOpenMode.Reopen)))
        {
            AssertTransfer(await f.Run(reopened, "query", new { transferId = "first" }), "committed", first.Length, WireJson.Sha256(first));
            AssertTransfer(await f.Run(reopened, "query", new { transferId = "second" }), "conflict", second.Length, null);
            var head = (await f.Run(reopened, "head")).GetProperty("publication"); Assert.Equal(WireJson.Sha256(first), head.GetProperty("etag").GetString()); Assert.Equal(first.Length, head.GetProperty("byteLength").GetInt32());
            Assert.Equal(first, await f.ReadAll(reopened));
        }
    }

    [Fact]
    public async Task PartialUtf8BlocksAreDurableAndExactDuplicateChunksAreIdempotent()
    {
        using var f = new Fixture(); byte[] body = Encoding.UTF8.GetBytes("记" + new string('a', 25000) + "😀");
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options()))
        {
            await f.Run(store, "begin", f.Begin("split", body)); var chunk = f.Chunk("split", 0, body.Take(1).ToArray());
            Assert.Equal(Text(await f.Run(store, "chunk", chunk)), Text(await f.Run(store, "chunk", chunk)));
        }
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(f.Options(StorageOpenMode.Reopen)); AssertTransfer(await f.Run(reopened, "query", new { transferId = "split" }), "staging", 1, null);
        for (int offset = 1; offset < body.Length; offset += SqliteMemoryPublicationStore.MaximumChunkBytes)
            await f.Run(reopened, "chunk", f.Chunk("split", offset, body.Skip(offset).Take(SqliteMemoryPublicationStore.MaximumChunkBytes).ToArray()));
        await f.Run(reopened, "commit", new { transferId = "split" }); Assert.Equal(body, await f.ReadAll(reopened));
        var eof = await f.Run(reopened, "read", new { etag = WireJson.Sha256(body), offset = body.Length, length = 1 });
        Assert.Equal(0, eof.GetProperty("byteLength").GetInt32()); Assert.True(eof.GetProperty("complete").GetBoolean()); Assert.Equal(WireJson.Sha256(Array.Empty<byte>()), eof.GetProperty("payloadDigest").GetString());
        Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => f.Run(reopened, "read", new { etag = "obsolete", offset = 0, length = 1 }))).Code);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("chunk")]
    [InlineData("commit")]
    public async Task UnknownTransferNeverCreatesAReplacementOrPublication(string action)
    {
        using var f = new Fixture(); using var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options());
        object fields = action == "chunk" ? f.Chunk("absent", 0, new byte[] { 1 }) : new { transferId = "absent" };
        var response = await f.Run(store, action, fields); var transfer = response.GetProperty("transfer");
        Assert.Equal("unknown", transfer.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, transfer.GetProperty("receivedBytes").ValueKind); Assert.Equal(JsonValueKind.Null, transfer.GetProperty("etag").ValueKind);
        Assert.Equal(0, (await store.GetCapacityAsync()).StoredTransfers); Assert.Equal(JsonValueKind.Null, (await f.Run(store, "head")).GetProperty("publication").ValueKind);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("digest")]
    [InlineData("bytes")]
    [InlineData("beyond")]
    public async Task InvalidChunksDoNotAdvanceDurableProgress(string failure)
    {
        using var f = new Fixture(); using var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options()); byte[] body = Encoding.UTF8.GetBytes("abcde");
        await f.Run(store, "begin", f.Begin("partial", body)); await f.Run(store, "chunk", f.Chunk("partial", 0, body.Take(2).ToArray()));
        var chunk = Json(f.Chunk("partial", failure == "gap" ? 3 : failure == "overlap" ? 1 : failure == "beyond" ? 5 : 2, failure == "overlap" ? new byte[] { 0 } : new byte[] { 99 }));
        if (failure == "digest") chunk = Replace(chunk, "payloadDigest", Json(new string('f', 64)));
        if (failure == "bytes") chunk = Replace(chunk, "byteLength", Json(2));
        Assert.Equal(failure is "digest" or "bytes" ? "integrity_mismatch" : "request_conflict", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => f.Run(store, "chunk", chunk))).Code);
        AssertTransfer(await f.Run(store, "query", new { transferId = "partial" }), "staging", 2, null);
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => f.Run(store, "commit", new { transferId = "partial" }))).Code);
    }

    [Fact]
    public async Task InvalidUtf8AndCompleteBodyHashMismatchCannotBePublished()
    {
        using var f = new Fixture(); using var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options());
        await f.Stage(store, "invalid", new byte[] { 0xc3, 0x28 });
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => f.Run(store, "commit", new { transferId = "invalid" }))).Code);
        await f.Run(store, "begin", new { transferId = "digest", expectedEtag = (string?)null, byteLength = 1, sha256 = WireJson.Sha256(new byte[] { 2 }) });
        await f.Run(store, "chunk", f.Chunk("digest", 0, new byte[] { 1 }));
        Assert.Equal("integrity_mismatch", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => f.Run(store, "commit", new { transferId = "digest" }))).Code);
        Assert.Equal(JsonValueKind.Null, (await f.Run(store, "head")).GetProperty("publication").ValueKind);
    }

    [Theory]
    [InlineData("applicationScopeId")]
    [InlineData("endUserId")]
    [InlineData("authorizationRevision")]
    [InlineData("sessionId")]
    [InlineData("binding")]
    public async Task TransferOwnerIncludesFullScopeSessionAndBinding(string field)
    {
        using var f = new Fixture(); using var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options()); await f.Stage(store, "owned", new byte[] { 97 });
        var owner = f.OwnerValue;
        if (field is "applicationScopeId" or "endUserId" or "authorizationRevision") owner = Replace(owner, "scope", Replace(owner.GetProperty("scope"), field, Json(field == "authorizationRevision" ? "2" : "other")));
        else if (field == "sessionId") owner = Replace(owner, "sessionId", Json("another-session"));
        else owner = Replace(owner, "binding", Replace(owner.GetProperty("binding"), "revision", Json("2")));
        Assert.Equal("request_conflict", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => store.ExecuteAsync(f.Request("query", new { transferId = "owned" }), Text(owner)))).Code);
        AssertTransfer(await f.Run(store, "query", new { transferId = "owned" }), "staging", 1, null);
    }

    [Theory]
    [InlineData("sourceId")]
    [InlineData("sourceGeneration")]
    [InlineData("domainKey")]
    public async Task RequestIdentityCannotBorrowAnotherPublication(string field)
    {
        using var f = new Fixture(); using var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options());
        Assert.Equal("stale_generation", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => store.ExecuteAsync(Replace(f.Request("head"), field, Json(field == "sourceGeneration" ? "2" : "other")), f.Owner))).Code);
    }

    [Fact]
    public async Task TrustedRecoveryCanOnlyQueryOriginalOwnerAndCannotTransferWriteAuthority()
    {
        using var f = new Fixture(); byte[] body = Encoding.UTF8.GetBytes("published");
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options())) { await f.Stage(store, "original", body); await f.Run(store, "commit", new { transferId = "original" }); }
        f.AuthorizationRevision = "2"; var owner = Replace(f.OwnerValue, "sessionId", Json("recovery-session")); int called = 0; var options = f.Options(StorageOpenMode.Reopen);
        options.AuthorizeRecovery = context =>
        {
            called++; Assert.Equal("original", context.TransferId); Assert.Equal("1", context.OriginalOwner.GetProperty("scope").GetProperty("authorizationRevision").GetString());
            Assert.Equal(Text(owner), Text(context.CurrentOwner)); Assert.Equal(Text(f.Identity), Text(context.Identity)); return true;
        };
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(options);
        AssertTransfer(await reopened.ExecuteAsync(f.Request("query", new { transferId = "original" }), Text(owner)), "committed", body.Length, WireJson.Sha256(body)); Assert.Equal(1, called);
        foreach (string action in new[] { "begin", "chunk", "commit" })
        {
            object fields = action == "begin" ? f.Begin("original", body) : action == "chunk" ? f.Chunk("original", 0, body) : new { transferId = "original" };
            Assert.Equal("request_conflict", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => reopened.ExecuteAsync(f.Request(action, fields), Text(owner)))).Code);
        }
        Assert.Equal(1, called);
    }

    [Fact]
    public async Task RecoveryCallbackCannotSwallowReentryOrChangeTheCurrentPrincipal()
    {
        using var f = new Fixture(); using (var original = await SqliteMemoryPublicationStore.OpenAsync(f.Options())) await f.Stage(original, "old", new byte[] { 97 });
        f.AuthorizationRevision = "2"; var options = f.Options(StorageOpenMode.Reopen); SqliteMemoryPublicationStore? store = null; bool reenter = true;
        options.AuthorizeRecovery = _ => { if (reenter) { try { store!.GetCapacityAsync().GetAwaiter().GetResult(); } catch (StorageException) { } } else f.User = "other"; return true; };
        using (store = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => f.Run(store, "query", new { transferId = "old" }))).Code);
            reenter = false; Assert.Equal("context_changed", (await Assert.ThrowsAsync<StorageException>(() => f.Run(store, "query", new { transferId = "old" }))).Code);
        }
    }

    [Fact]
    public async Task TransferCapacityIsPermanentAndStagingCapacityIsReservedAtBegin()
    {
        using var f = new Fixture { MaxTransfers = 2 }; var options = f.Options(); options.MaxStagingBytes = SqliteMemoryPublicationStore.MaximumBodyBytes;
        using var store = await SqliteMemoryPublicationStore.OpenAsync(options);
        await f.Run(store, "begin", new { transferId = "large", expectedEtag = (string?)null, byteLength = SqliteMemoryPublicationStore.MaximumBodyBytes, sha256 = new string('a', 64) });
        var capacity = await store.GetCapacityAsync(); Assert.Equal(SqliteMemoryPublicationStore.MaximumBodyBytes, capacity.StagingBytes); Assert.Equal(0, capacity.RemainingStagingBytes);
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => f.Run(store, "begin", f.Begin("no-space", new byte[] { 97 })))).Code);
        Assert.Equal(1, (await store.GetCapacityAsync()).StoredTransfers);
    }

    [Fact]
    public async Task ReachingTheTransferCountCapDoesNotEvictCommittedReceipts()
    {
        using var f = new Fixture { MaxTransfers = 1 }; using var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options()); byte[] body = new byte[] { 97 };
        await f.Stage(store, "retained", body); await f.Run(store, "commit", new { transferId = "retained" });
        Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => f.Run(store, "begin", f.Begin("new", body)))).Code);
        AssertTransfer(await f.Run(store, "query", new { transferId = "retained" }), "committed", 1, WireJson.Sha256(body)); Assert.Equal(0, (await store.GetCapacityAsync()).RemainingTransfers);
    }

    [Theory]
    [InlineData("sourceGeneration")]
    [InlineData("domainKey")]
    [InlineData("user")]
    [InlineData("transfers")]
    [InlineData("staging")]
    [InlineData("pages")]
    public async Task ReopenCannotChangeIdentityOrRaisePersistedLimits(string field)
    {
        using var f = new Fixture(); using (await SqliteMemoryPublicationStore.OpenAsync(f.Options())) { }
        var options = f.Options(StorageOpenMode.Reopen);
        if (field is "sourceGeneration" or "domainKey") options.Identity = Replace(options.Identity, field, Json(field == "sourceGeneration" ? "2" : "changed"));
        else if (field == "user") { f.User = "other"; options.Identity = Replace(options.Identity, "scope", Json(new { applicationScopeId = "app", endUserId = "other" })); }
        else if (field == "transfers") options.MaxTransfers++;
        else if (field == "staging") options.MaxStagingBytes++;
        else options.MaxPages++;
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(options))).Code);
    }

    [Fact]
    public async Task MissingReopenNeverCreatesAndCreateNeverOverwritesAnExistingStore()
    {
        using var f = new Fixture(); await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(f.Options(StorageOpenMode.Reopen))); Assert.False(File.Exists(f.Path));
        using (await SqliteMemoryPublicationStore.OpenAsync(f.Options())) { }
        byte[] before = File.ReadAllBytes(f.Path); await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(f.Options())); Assert.Equal(before, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public async Task PreviewAndRequiredCapacityAreValidatedBeforeOpeningTheFile()
    {
        using var f = new Fixture(); var options = f.Options(); options.EnablePreview = false;
        await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => SqliteMemoryPublicationStore.OpenAsync(options)); options.EnablePreview = true; options.MaxTransfers = 0;
        await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => SqliteMemoryPublicationStore.OpenAsync(options)); options.MaxTransfers = 1; options.Mode = StorageOpenMode.MigrateV1;
        await Assert.ThrowsAsync<SqliteMemoryPublicationException>(() => SqliteMemoryPublicationStore.OpenAsync(options)); Assert.False(File.Exists(f.Path));
    }

    [Fact]
    public async Task OpenFreezesAllOptionsBeforeCallingTheHost()
    {
        using var f = new Fixture(); var options = f.Options(); options.ReadContext = () =>
        {
            options.Identity = Json(new { bad = true }); options.Path = "Z:/wrong.sqlite"; options.Mode = StorageOpenMode.Reopen; options.MaxTransfers = 0; options.MaxStagingBytes = 1; options.MaxPages = 1;
            options.ReadContext = () => throw new InvalidOperationException(); options.AuthorizeRecovery = _ => throw new InvalidOperationException(); return f.Scope;
        };
        using var store = await SqliteMemoryPublicationStore.OpenAsync(options); Assert.Equal(Text(f.Identity), Text(store.Identity)); Assert.Equal(f.MaxTransfers, (await store.GetCapacityAsync()).MaxTransfers);
        await f.Stage(store, "frozen", new byte[] { 97 });
    }

    [Fact]
    public async Task SwallowedSamePathOpenReentryCannotPublishAnOpeningOwner()
    {
        using var f = new Fixture(); var options = f.Options(); bool inside = false;
        options.ReadContext = () =>
        {
            if (!inside) { inside = true; try { SqliteMemoryPublicationStore.OpenAsync(f.Options()).GetAwaiter().GetResult(); } catch (StorageException) { } }
            return f.Scope;
        };
        Assert.Equal("reentrant", (await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(options))).Code); Assert.False(File.Exists(f.Path));
        using var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options()); Assert.Equal(0, (await store.GetCapacityAsync()).StoredTransfers);
    }

    [Fact]
    public async Task SimultaneousOwnersAreRefusedAndDisposeReleasesTheOriginalFile()
    {
        using var f = new Fixture(); var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options());
        await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(f.Options(StorageOpenMode.Reopen)));
        Assert.Equal(0, (await store.GetCapacityAsync()).StoredTransfers); store.Dispose(); store.Dispose();
        Assert.Equal("closed", (await Assert.ThrowsAsync<StorageException>(() => store.GetCapacityAsync())).Code);
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(f.Options(StorageOpenMode.Reopen)); Assert.Equal(0, (await reopened.GetCapacityAsync()).StoredTransfers);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("cancel")]
    [InlineData("reentry")]
    public async Task PrecommitAuthorityFailureRollsBackTheWholeTransfer(string failure)
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource(); var options = f.Options(); SqliteMemoryPublicationStore? store = null; bool active = false; int calls = 0;
        options.ReadContext = () =>
        {
            if (active && ++calls == 4)
            {
                if (failure == "scope") f.User = "other";
                else if (failure == "cancel") cts.Cancel();
                else { try { store!.CloseAsync().GetAwaiter().GetResult(); } catch (StorageException) { } }
            }
            return f.Scope;
        };
        using (store = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            active = true;
            if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExecuteAsync(f.Request("begin", f.Begin("before", new byte[] { 97 })), f.Owner, cts.Token));
            else Assert.Equal(failure == "scope" ? "context_changed" : "reentrant", (await Assert.ThrowsAsync<StorageException>(() => f.Run(store, "begin", f.Begin("before", new byte[] { 97 })))).Code);
            active = false; f.User = "user"; Assert.Equal(0, (await store.GetCapacityAsync()).StoredTransfers);
        }
    }

    [Theory]
    [InlineData("begin", false)]
    [InlineData("begin", true)]
    [InlineData("chunk", false)]
    [InlineData("chunk", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    public async Task RealSqliteCommitFailureOrLostReturnRequiresReopenAndOriginalTransferQuery(string action, bool afterCommit)
    {
        using var f = new Fixture(); byte[] body = Encoding.UTF8.GetBytes("durable once"); var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options());
        try
        {
            if (action != "begin") await f.Run(store, "begin", f.Begin("unknown", body));
            if (action == "commit") await f.Run(store, "chunk", f.Chunk("unknown", 0, body));
            var connection = Connection(store); int commits = 0; WalHook hook = (_, _, _, _) => { commits++; return 10; };
            if (afterCommit) SqliteWalHook(connection.Handle!.DangerousGetHandle(), hook, IntPtr.Zero);
            else SQLitePCL.raw.sqlite3_commit_hook(connection.Handle!, _ => { commits++; return 1; }, null);
            try
            {
                object fields = action == "begin" ? f.Begin("unknown", body) : action == "chunk" ? f.Chunk("unknown", 0, body) : new { transferId = "unknown" };
                Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => f.Run(store, action, fields))).Code); Assert.Equal(1, commits);
            }
            finally { SqliteWalHook(connection.Handle!.DangerousGetHandle(), null, IntPtr.Zero); SQLitePCL.raw.sqlite3_commit_hook(connection.Handle!, null, null); GC.KeepAlive(hook); }
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => f.Run(store, "query", new { transferId = "unknown" }))).Code);
        }
        finally { store.Dispose(); }
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(f.Options(StorageOpenMode.Reopen)); var response = await f.Run(reopened, "query", new { transferId = "unknown" });
        if (action == "begin" && !afterCommit) Assert.Equal("unknown", response.GetProperty("transfer").GetProperty("status").GetString());
        else AssertTransfer(response, action == "commit" && afterCommit ? "committed" : "staging", action == "commit" || action == "chunk" && afterCommit ? body.Length : 0, action == "commit" && afterCommit ? WireJson.Sha256(body) : null);
        Assert.Equal(action == "commit" && afterCommit ? JsonValueKind.Object : JsonValueKind.Null, (await f.Run(reopened, "head")).GetProperty("publication").ValueKind);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("cancel")]
    [InlineData("reentry")]
    public async Task PostCommitFailureIsUnknownAndNeverExposedAsARejectedPublication(string failure)
    {
        using var f = new Fixture(); var options = f.Options(); using var cts = new CancellationTokenSource(); SqliteMemoryPublicationStore? store = null; bool active = false; int calls = 0;
        options.ReadContext = () =>
        {
            if (active && ++calls == 5)
            {
                if (failure == "scope") f.User = "other";
                else if (failure == "cancel") cts.Cancel();
                else { try { store!.GetCapacityAsync().GetAwaiter().GetResult(); } catch (StorageException) { } }
            }
            return f.Scope;
        };
        using (store = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            await f.Stage(store, "after", new byte[] { 97 }); active = true;
            Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => store.ExecuteAsync(f.Request("commit", new { transferId = "after" }), f.Owner, cts.Token))).Code);
            active = false; f.User = "user"; Assert.Equal("reconciliation_required", (await Assert.ThrowsAsync<StorageException>(() => store.GetCapacityAsync())).Code);
        }
        using var reopened = await SqliteMemoryPublicationStore.OpenAsync(f.Options(StorageOpenMode.Reopen)); AssertTransfer(await f.Run(reopened, "query", new { transferId = "after" }), "committed", 1, WireJson.Sha256(new byte[] { 97 }));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("status")]
    [InlineData("received")]
    [InlineData("owner")]
    [InlineData("request")]
    [InlineData("schema")]
    public async Task CorruptMediaIsNotRecreatedOrTreatedAsAnEmptyPublication(string corruption)
    {
        using var f = new Fixture(); using (var store = await SqliteMemoryPublicationStore.OpenAsync(f.Options())) { await f.Stage(store, "one", new byte[] { 97 }); await f.Run(store, "commit", new { transferId = "one" }); }
        using (var db = Raw(f.Path)) Scalar(db, corruption switch
        {
            "body" => "UPDATE publication SET body=zeroblob(length(body))",
            "status" => "UPDATE transfers SET status='unknown'",
            "received" => "UPDATE transfers SET received=0",
            "owner" => "UPDATE transfers SET owner=replace(owner,'user','xxxx')",
            "request" => "UPDATE transfers SET request=replace(request,'one','two')",
            _ => "CREATE TABLE unexpected(x TEXT)",
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => SqliteMemoryPublicationStore.OpenAsync(f.Options(StorageOpenMode.Reopen)));
        Assert.True(error is SqliteMemoryPublicationException or StorageException or WireProtocolException); Assert.True(File.Exists(f.Path));
    }

    [Fact]
    public async Task PhysicalPageCapRefusesBeginWithoutLeavingAnUnaccountedTransfer()
    {
        using var f = new Fixture(); var options = f.Options(); options.MaxPages = 8;
        var store = await SqliteMemoryPublicationStore.OpenAsync(options);
        try
        {
            Assert.Equal("capacity_exceeded", (await Assert.ThrowsAsync<StorageException>(() => f.Run(store, "begin", new { transferId = "large", expectedEtag = (string?)null, byteLength = SqliteMemoryPublicationStore.MaximumBodyBytes, sha256 = new string('a', 64) }))).Code);
            Assert.Equal(0, (await store.GetCapacityAsync()).StoredTransfers);
            Assert.Equal("unknown", (await f.Run(store, "query", new { transferId = "large" })).GetProperty("transfer").GetProperty("status").GetString());
        }
        finally { store.Dispose(); }
        options.Mode = StorageOpenMode.Reopen; using var reopened = await SqliteMemoryPublicationStore.OpenAsync(options); Assert.Equal(0, (await reopened.GetCapacityAsync()).StoredTransfers);
    }

    [Fact]
    public async Task CopiedDatabaseCannotBorrowTheOriginalPhysicalIdentity()
    {
        using var f = new Fixture(); using (await SqliteMemoryPublicationStore.OpenAsync(f.Options())) { }
        string copy = Path.Combine(f.Directory, "copy.sqlite"); File.Copy(f.Path, copy); var options = f.Options(StorageOpenMode.Reopen); options.Path = copy;
        Assert.Equal("identity_mismatch", (await Assert.ThrowsAsync<StorageException>(() => SqliteMemoryPublicationStore.OpenAsync(options))).Code);
    }

    internal static JsonElement Json(object value) => WireJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
    internal static string Text(JsonElement value) => WireJson.CanonicalString(value);
    internal static JsonElement Replace(JsonElement value, string name, JsonElement replacement) => Json(value.EnumerateObject().ToDictionary(property => property.Name, property => property.Name == name ? replacement : property.Value.Clone()));
    private static void AssertTransfer(JsonElement response, string status, int received, string? etag)
    { var transfer = response.GetProperty("transfer"); Assert.Equal(status, transfer.GetProperty("status").GetString()); Assert.Equal(received, transfer.GetProperty("receivedBytes").GetInt32()); Assert.Equal(etag, transfer.GetProperty("etag").GetString()); }
    private static SqliteConnection Connection(SqliteMemoryPublicationStore store) => (SqliteConnection)typeof(SqliteMemoryPublicationStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    private static SqliteConnection Raw(string path) { var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"); connection.Open(); return connection; }
    private static object? Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WalHook(IntPtr argument, IntPtr database, IntPtr name, int pages);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_wal_hook", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SqliteWalHook(IntPtr database, WalHook? callback, IntPtr argument);

    internal sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tansr-net-memory-publication-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "publication.sqlite");
        internal int MaxTransfers { get; set; } = 64;
        internal string User { get; set; } = "user";
        internal string AuthorizationRevision { get; set; } = "1";
        internal JsonElement Scope => Json(new { applicationScopeId = "app", endUserId = User, authorizationRevision = AuthorizationRevision });
        internal JsonElement Identity => Json(new { scope = new { applicationScopeId = "app", endUserId = "user" }, sourceId = "device-memory/中文", sourceGeneration = "1", domainKey = "domain/中文" });
        internal JsonElement OwnerValue => Json(new { scope = Scope, sessionId = "session/中文", binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } } });
        internal string Owner => Text(OwnerValue);
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal SqliteMemoryPublicationOptions Options(StorageOpenMode mode = StorageOpenMode.Create) => new() { EnablePreview = true, Path = Path, Mode = mode, Identity = Identity, ReadContext = () => Scope, MaxTransfers = MaxTransfers };
        internal JsonElement Request(string action, object? fields = null)
        {
            var values = new Dictionary<string, JsonElement> { ["contract"] = Json("terminal-services-v1"), ["action"] = Json(action) };
            foreach (string name in new[] { "sourceId", "sourceGeneration", "domainKey" }) values[name] = Identity.GetProperty(name);
            if (fields != null) foreach (var property in Json(fields).EnumerateObject()) values[property.Name] = property.Value.Clone();
            return Json(values);
        }
        internal Task<JsonElement> Run(SqliteMemoryPublicationStore store, string action, object? fields = null) => store.ExecuteAsync(Request(action, fields), Owner);
        internal object Begin(string id, byte[] bytes, string? etag = null) => new { transferId = id, expectedEtag = etag, byteLength = bytes.Length, sha256 = WireJson.Sha256(bytes) };
        internal object Chunk(string id, int offset, byte[] bytes) => new { transferId = id, offset, byteLength = bytes.Length, base64 = Convert.ToBase64String(bytes), payloadDigest = WireJson.Sha256(bytes) };
        internal async Task Stage(SqliteMemoryPublicationStore store, string id, byte[] bytes, string? etag = null)
        {
            await Run(store, "begin", Begin(id, bytes, etag));
            for (int offset = 0; offset < bytes.Length; offset += SqliteMemoryPublicationStore.MaximumChunkBytes) await Run(store, "chunk", Chunk(id, offset, bytes.Skip(offset).Take(SqliteMemoryPublicationStore.MaximumChunkBytes).ToArray()));
        }
        internal async Task<byte[]> ReadAll(SqliteMemoryPublicationStore store)
        {
            var head = (await Run(store, "head")).GetProperty("publication"); var output = new List<byte>();
            for (int offset = 0; offset < head.GetProperty("byteLength").GetInt32();)
            {
                var response = await Run(store, "read", new { etag = head.GetProperty("etag").GetString(), offset, length = SqliteMemoryPublicationStore.MaximumChunkBytes });
                var bytes = Convert.FromBase64String(response.GetProperty("base64").GetString()!); Assert.Equal(WireJson.Sha256(bytes), response.GetProperty("payloadDigest").GetString()); output.AddRange(bytes); offset = response.GetProperty("nextOffset").GetInt32();
            }
            return output.ToArray();
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
