using System.Globalization;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

// SQLite media integration; the authority fixture is deliberately not a substitute for shared SQL acceptance.
public sealed class SqliteTerminalPersistenceCustodyTests
{
    [Fact]
    public async Task MigrationBudgetIsSeparateFromBusinessCapacityAndFrozenAcrossColdReopen()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        var prepared = await f.Prepare(source); var proof = prepared.GetProperty("ticket").GetProperty("proof");
        var options = f.Options("target"); options.MaxRetainedBytes = 0; options.MaxObjects = 0;
        options.MigrationBudget = new() { Bytes = proof.GetProperty("bytes").GetInt64(), Objects = proof.GetProperty("objects").GetInt32() };
        var first = (await f.Custody(source, "export-page", new { cursor = (object?)null, limit = 1 })).GetProperty("page");
        using (var target = await SqliteTerminalPersistenceStore.OpenAsync(options))
        {
            await f.Custody(target, "import-page", new { proof, page = first });
            // Calling code cannot change the already fixed trusted configuration.
            options.MigrationBudget.Bytes = 0; options.MigrationBudget.Objects = 0;
            await f.Custody(target, "import-page", new { proof, page = first });
        }
        options.Mode = StorageOpenMode.Reopen;
        using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(options);
        await f.Copy(source, reopened, proof, 1, 127);
        await f.Custody(reopened, "verify", new { proof });
        await f.Custody(reopened, "cutover", new { proof, expectedWriterFence = "2", expectedCustodyRevision = "1" });
        Assert.Equal(Canon((await f.Call(source, f.Request("head"))).GetProperty("root")), Canon((await f.Call(reopened, f.Request("head"))).GetProperty("root")));
        await Assert.ThrowsAsync<MemoryPublicationRejectedException>(() => f.Call(reopened, f.Plan("not-expanded", 1, 0).Begin));
    }

    [Fact]
    public async Task MigrationBudgetDefaultRejectsAdditionalPhysicalCapacityAndExplicitBudgetIsRetained()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        var prepared = await f.Prepare(source); var original = prepared.GetProperty("ticket").GetProperty("proof");
        // The bounded first page can test admission of a large proof without allocating a GiB.
        // It remains incomplete and must never be treated as an installed full-capacity domain.
        var proof = Change(Change(original, "bytes", 1073741825L), "objects", 1048577);
        var page = (await f.Custody(source, "export-page", new { cursor = (object?)null, limit = 1 })).GetProperty("page");
        f.Authority.Ticket = Change(prepared.GetProperty("ticket"), "proof", proof);
        var options = f.Options("target");
        using (var target = await SqliteTerminalPersistenceStore.OpenAsync(options))
        {
            var error = await Assert.ThrowsAsync<MemoryPublicationRejectedException>(() => f.Custody(target, "import-page", new { proof, page }));
            Assert.Equal("capacity_exceeded", error.Code);
        }
        options.Mode = StorageOpenMode.Reopen; options.MigrationBudget = new() { Bytes = 1102569742, Objects = 1310721 };
        using (var target = await SqliteTerminalPersistenceStore.OpenAsync(options)) await f.Custody(target, "import-page", new { proof, page });
        options.MigrationBudget = null;
        using (var target = await SqliteTerminalPersistenceStore.OpenAsync(options))
        {
            await f.Custody(target, "import-page", new { proof, page });
            await Assert.ThrowsAsync<MemoryPublicationRejectedException>(() => f.Custody(target, "verify", new { proof }));
        }
        options.MigrationBudget = new() { Bytes = 1102569743, Objects = 1310721 };
        await Assert.ThrowsAsync<MemoryPublicationRejectedException>(() => SqliteTerminalPersistenceStore.OpenAsync(options));
    }

    [Fact]
    public async Task OriginalNodeCatalogKeepsReleasedReuseReservationsAndContinuesAfterColdCutover()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SourcePath())!, "Fixtures", "persistence-custody-node-v1.json")));
        var input = document.RootElement; using var f = new Fixture();
        Assert.Equal(Canon(f.Identity), Canon(input.GetProperty("identity"))); Assert.Equal(f.Owner, Text(input, "ownerCanonical"));
        f.PrepareRequest = input.GetProperty("prepare").Clone(); f.Authority.Ticket = input.GetProperty("ticket").Clone();
        f.Authority.Selected = input.GetProperty("ticket").GetProperty("selectedCustody").Clone();
        var options = f.Options("node-return"); options.Custody = Json(new { custodyId = "new-csharp-target", custodyRevision = "3", mode = "terminal" });
        var proof = input.GetProperty("proof"); var records = input.GetProperty("records").EnumerateArray().ToArray();
        using (var target = await SqliteTerminalPersistenceStore.OpenAsync(options))
        {
            string digest = WireJson.Sha256(Bytes(Json(new object[] { "PST-CUSTODY-DIRECTORY", f.Identity, Text(proof, "originFormat"), Text(proof, "fixedRoot") })));
            for (int offset = 0; offset < records.Length; offset += 5)
            {
                var pageRecords = records.Skip(offset).Take(5).ToArray(); string previous = digest;
                for (int n = 0; n < pageRecords.Length; n++) digest = WireJson.Sha256(Bytes(Json(new object[] { "PST-CUSTODY-RECORD", (offset + n).ToString(CultureInfo.InvariantCulture), digest, pageRecords[n] })));
                var page = new { index = offset.ToString(CultureInfo.InvariantCulture), previousDigest = previous, sha256 = digest, records = pageRecords, complete = offset + pageRecords.Length == records.Length };
                await f.Custody(target, "import-page", new { proof, page });
            }
            Assert.Equal(Text(proof, "inventoryDigest"), digest);
            foreach (var item in input.GetProperty("objects").EnumerateArray())
            {
                var reference = item.GetProperty("object"); byte[] body = Convert.FromBase64String(Text(item, "base64"));
                Assert.Equal(Text(reference, "sha256"), WireJson.Sha256(body));
                for (int offset = 0; offset < body.Length; offset += 4096)
                {
                    byte[] bytes = body.Skip(offset).Take(4096).ToArray();
                    await f.Custody(target, "import-object", new { chunk = new { @object = reference, offset, byteLength = bytes.Length, payloadDigest = WireJson.Sha256(bytes), base64 = Convert.ToBase64String(bytes) } });
                }
            }
            await f.Custody(target, "verify", new { proof });
            Assert.Equal(Canon(input.GetProperty("head")), Canon(await f.Call(target, f.Request("head"))));
            await f.Custody(target, "cutover", new { proof, expectedWriterFence = "3", expectedCustodyRevision = "2" });
        }
        options.Mode = StorageOpenMode.Reopen; using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(options);
        Assert.Equal(Canon(input.GetProperty("head")), Canon(await f.Call(reopened, f.Request("head"))));
        foreach (string name in new[] { "committed", "pending" })
        {
            var original = input.GetProperty(name); var transfer = original.GetProperty("transfer");
            Assert.Equal(Canon(original), Canon(await f.Call(reopened, f.Request("query", new { transferId = Text(transfer, "transferId"), intentSha256 = Text(transfer, "intentSha256") }))));
        }
        var pending = input.GetProperty("pendingBegin");
        foreach (var item in input.GetProperty("pendingObjects").EnumerateArray())
        {
            byte[] bytes = Convert.FromBase64String(Text(item, "base64"));
            await f.Call(reopened, f.Request("put", new { transferId = Text(pending, "transferId"), intentSha256 = Text(pending, "intentSha256"), kind = Text(item, "kind"), sha256 = WireJson.Sha256(bytes), byteLength = bytes.Length, base64 = Text(item, "base64") }));
        }
        var committed = await f.Call(reopened, f.Request("commit", new { transferId = Text(pending, "transferId"), intentSha256 = Text(pending, "intentSha256") }));
        Assert.Equal("committed", Text(committed.GetProperty("transfer"), "status"));
        Assert.Equal(Text(input.GetProperty("head").GetProperty("root").GetProperty("index"), "count"), Text(committed.GetProperty("transfer").GetProperty("result").GetProperty("index"), "count"));
    }

    [Fact]
    public async Task PortableCatalogExportsOriginalLegacyReuseReservationsForIndependentConsumer()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        var first = f.Plan("committed", 1, 2); await f.Stage(source, first); await f.Call(source, first.Action("commit"));
        var root = (await f.Call(source, f.Request("head"))).GetProperty("root");
        var pending = f.Plan("pending", 1, 2, root, 0); await f.Call(source, pending.Begin);
        var head = await f.Call(source, f.Request("head")); var prepared = await f.Prepare(source);
        var records = new List<JsonElement>(); JsonElement? cursor = null;
        do
        {
            var page = await f.Custody(source, "export-page", new { cursor, limit = 32 });
            records.AddRange(page.GetProperty("page").GetProperty("records").EnumerateArray().Select(x => x.Clone()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").Clone();
        } while (cursor.HasValue);
        var objects = new List<object>();
        foreach (var record in records.Where(x => Text(x, "kind") == "object"))
        {
            var reference = record.GetProperty("object"); using var memory = new MemoryStream();
            while (memory.Length < reference.GetProperty("byteLength").GetInt32())
            {
                var result = await f.Custody(source, "export-object", new { @object = reference, offset = (int)memory.Length, length = 4096 });
                var bytes = Convert.FromBase64String(Text(result.GetProperty("chunk"), "base64")); memory.Write(bytes, 0, bytes.Length);
            }
            Assert.Equal(Text(reference, "sha256"), WireJson.Sha256(memory.ToArray())); objects.Add(new { @object = reference, base64 = Convert.ToBase64String(memory.ToArray()) });
        }
        Assert.Equal(Canon(head), Canon(await f.Call(source, f.Request("head"))));
        var portable = Json(new
        {
            identity = f.Identity,
            ownerCanonical = f.Owner,
            prepare = f.PrepareRequest,
            ticket = prepared.GetProperty("ticket"),
            proof = prepared.GetProperty("ticket").GetProperty("proof"),
            records,
            objects,
            head,
            committed = await f.Call(source, first.Action("query")),
            pending = await f.Call(source, pending.Action("query")),
            pendingBegin = pending.Begin,
            pendingObjects = pending.Objects.Select(x => new { kind = x.Kind, base64 = Convert.ToBase64String(x.Bytes) })
        });
        string? output = Environment.GetEnvironmentVariable("TANSR_TEST_CUSTODY_EXPORT");
        if (!string.IsNullOrEmpty(output)) File.WriteAllBytes(output, Bytes(portable));
    }

    [Fact]
    public async Task FullCatalogKeepsMoreThan256ReceiptsOriginalRootsAndPendingFactsAcrossColdCutover()
    {
        using var f = new Fixture(); JsonElement before, oldQuery, pendingQuery; Plan pending;
        using (var old = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source", enabled: false)))
        {
            var first = f.Plan("first", 1, 256); await f.Stage(old, first); await f.Call(old, first.Action("commit"));
            var root = (await f.Call(old, f.Request("head"))).GetProperty("root");
            var next = f.Plan("next", 257, 1, root); await f.Stage(old, next); await f.Call(old, next.Action("commit"));
            root = (await f.Call(old, f.Request("head"))).GetProperty("root");
            pending = f.Plan("pending", 257, 1, root, 0); await f.Call(old, pending.Begin);
            before = await f.Call(old, f.Request("head")); oldQuery = await f.Call(old, first.Action("query")); pendingQuery = await f.Call(old, pending.Action("query"));
        }
        using (var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source", StorageOpenMode.Reopen)))
        using (var target = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("target")))
        {
            var prepared = await f.Prepare(source); var proof = prepared.GetProperty("ticket").GetProperty("proof");
            Assert.Equal("257", proof.GetProperty("indexCount").GetString());
            var records = await f.Copy(source, target, proof, 7, 4096);
            Assert.Equal(257, records.Count(x => Text(x, "kind") == "receipt-index"));
            Assert.Equal(3, records.Count(x => Text(x, "kind") == "transfer"));
            await Assert.ThrowsAnyAsync<Exception>(() => f.Call(target, pending.Action("commit")));
            await f.Custody(target, "verify", new { proof });
            Assert.Equal(Canon(before), Canon(await f.Call(target, f.Request("head"))));
            await f.Custody(target, "cutover", new { proof, expectedWriterFence = "2", expectedCustodyRevision = "1" });
            await Assert.ThrowsAnyAsync<Exception>(() => f.Call(source, pending.Action("commit")));
        }
        using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("target", StorageOpenMode.Reopen));
        Assert.Equal(Canon(before), Canon(await f.Call(reopened, f.Request("head"))));
        Assert.Equal(Canon(oldQuery), Canon(await f.Call(reopened, f.Plan("first", 1, 256).Action("query"))));
        Assert.Equal(Canon(pendingQuery), Canon(await f.Call(reopened, pending.Action("query"))));
        foreach (int id in new[] { 1, 128, 256, 257 })
        {
            var result = await f.Call(reopened, f.Request("lookup", new { commitRoot = Text(before.GetProperty("root"), "commitRoot"), key = new { kind = "primary", digest = KeyDigest(id) } }));
            Assert.Equal("receipt-" + id, Encoding.UTF8.GetString(Convert.FromBase64String(Text(result.GetProperty("entry"), "base64"))));
        }
        foreach (var item in pending.Objects.Where(x => x.Kind == "index-page")) await f.Call(reopened, pending.Put(item));
        await f.Call(reopened, pending.Action("commit"));
        Assert.Equal("committed", (await f.Call(reopened, pending.Action("query"))).GetProperty("transfer").GetProperty("status").GetString());
    }

    [Fact]
    public async Task LostPublicationResponseRetainsVerifiedCopyAndQueryActivatesOnlySharedPublishedCustody()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        using var target = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("target"));
        var plan = f.Plan("small", 1, 2); await f.Stage(source, plan); await f.Call(source, plan.Action("commit"));
        var prepared = await f.Prepare(source); var proof = prepared.GetProperty("ticket").GetProperty("proof");
        await f.Copy(source, target, proof, 2, 127); await f.Custody(target, "verify", new { proof });
        f.Authority.LoseCutover = true;
        await Assert.ThrowsAsync<IOException>(() => f.Custody(target, "cutover", new { proof, expectedWriterFence = "2", expectedCustodyRevision = "1" }));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Call(target, f.Plan("new", 3, 1).Begin));
        var query = await f.Custody(target, "query"); Assert.Equal("published", Text(query.GetProperty("ticket"), "phase"));
        Assert.Equal("target", Text(query.GetProperty("ticket").GetProperty("selectedCustody"), "custodyId"));
        Assert.Equal("committed", Text((await f.Call(target, plan.Action("query"))).GetProperty("transfer"), "status"));
    }

    [Fact]
    public async Task IncompleteAndChangedCatalogNeverInstallsRootAndOriginalWriterStaysFrozen()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        using var target = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("target"));
        var plan = f.Plan("one", 1, 1); await f.Stage(source, plan); await f.Call(source, plan.Action("commit"));
        var prepared = await f.Prepare(source); var proof = prepared.GetProperty("ticket").GetProperty("proof");
        var page = await f.Custody(source, "export-page", new { cursor = (object?)null, limit = 1 });
        await f.Custody(target, "import-page", new { proof, page = page.GetProperty("page") });
        await f.Custody(target, "import-page", new { proof, page = page.GetProperty("page") });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Custody(target, "verify", new { proof }));
        Assert.Equal(JsonValueKind.Null, (await f.Call(target, f.Request("head"))).GetProperty("root").ValueKind);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Call(source, f.Plan("new", 2, 1).Begin));
        var bad = Change(page.GetProperty("page"), "sha256", new string('a', 64));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Custody(target, "import-page", new { proof, page = bad }));
    }

    [Fact]
    public async Task CommittedWriterSettlementFailureIsUnknownAndCannotRepeatBusinessWork()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        var plan = f.Plan("one", 1, 1); f.Authority.LoseFinish = true;
        var error = await Assert.ThrowsAsync<IOException>(() => f.Call(source, plan.Begin)); Assert.Equal("custody_outcome_unconfirmed", error.Message);
        Assert.Equal("staging", Text((await f.Call(source, plan.Action("query"))).GetProperty("transfer"), "status"));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Call(source, plan.Begin));
        Assert.Equal(1, f.Authority.Acquires); Assert.Equal(1, f.Authority.Finishes);
    }

    [Fact]
    public async Task AuthorizationDriftAfterClaimNeverExecutesOrReleasesUnknownWriter()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        f.Authority.AfterAcquire = () => f.Revision = "2";
        await Assert.ThrowsAsync<WireProtocolException>(() => f.Call(source, f.Plan("one", 1, 1).Begin));
        Assert.Equal(0, f.Authority.Finishes);
        Assert.Equal(JsonValueKind.Null, (await f.Call(source, f.Request("head"))).GetProperty("root").ValueKind);
    }

    [Fact]
    public async Task SettledRequestUsesOnlyOriginalCurrentProjectionAfterLaterProgress()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        var plan = f.Plan("replay", 1, 1); var first = await f.Call(source, plan.Begin);
        Assert.Equal("staging", Text(first.GetProperty("transfer"), "status"));
        foreach (var item in plan.Objects) await f.Call(source, plan.Put(item)); await f.Call(source, plan.Action("commit"));
        var before = await f.Call(source, f.Request("head")); int acquired = f.Authority.Acquires, finished = f.Authority.Finishes;
        var repeat = await f.Call(source, plan.Begin);
        Assert.Equal("committed", Text(repeat.GetProperty("transfer"), "status"));
        Assert.Equal(acquired, f.Authority.Acquires); Assert.Equal(finished, f.Authority.Finishes);
        Assert.Equal(Canon(before), Canon(await f.Call(source, f.Request("head"))));
    }

    [Fact]
    public async Task CustodyMediaCannotReopenWithoutItsExplicitAuthorityProfile()
    {
        using var f = new Fixture(); using (var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"))) { }
        await Assert.ThrowsAsync<StorageException>(() => SqliteTerminalPersistenceStore.OpenAsync(f.Options("source", StorageOpenMode.Reopen, false)));
        using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source", StorageOpenMode.Reopen));
        Assert.True(reopened.CustodyHandoffEnabled);
    }

    [Fact]
    public async Task PhysicalStagingLimitKeepsOriginalSourceAndUnpublishedTargetAcrossReopen()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        var plan = f.Plan("physical", 1, 64); await f.Stage(source, plan); await f.Call(source, plan.Action("commit"));
        var before = await f.Call(source, f.Request("head")); var prepared = await f.Prepare(source); var proof = prepared.GetProperty("ticket").GetProperty("proof");
        var options = f.Options("target"); options.MaxPages = 32;
        using (var target = await SqliteTerminalPersistenceStore.OpenAsync(options))
        {
            var error = await Assert.ThrowsAsync<StorageException>(() => f.Copy(source, target, proof, 8, 127));
            Assert.Equal("capacity_exceeded", error.Code);
            Assert.Equal(JsonValueKind.Null, (await f.Call(target, f.Request("head"))).GetProperty("root").ValueKind);
        }
        foreach (var file in System.IO.Directory.GetFiles(f.Directory, "target.sqlite*"))
        {
            byte[] bytes = File.ReadAllBytes(file); Assert.False(bytes.AsSpan().IndexOf(Enumerable.Repeat((byte)42, 64).ToArray()) >= 0);
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Convert.ToBase64String(Enumerable.Repeat((byte)42, 64).ToArray()))) >= 0);
        }
        options.Mode = StorageOpenMode.Reopen; using var reopened = await SqliteTerminalPersistenceStore.OpenAsync(options);
        Assert.Equal(JsonValueKind.Null, (await f.Call(reopened, f.Request("head"))).GetProperty("root").ValueKind);
        Assert.Equal(Canon(before), Canon(await f.Call(source, f.Request("head"))));
    }

    [Fact]
    public async Task ActivatedTargetCanHandoffAgainAndOldPublishedQueryCannotUnlockIt()
    {
        using var f = new Fixture(); using var source = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("source"));
        using var target = await SqliteTerminalPersistenceStore.OpenAsync(f.Options("target"));
        var prepared = await f.Prepare(source); var proof = prepared.GetProperty("ticket").GetProperty("proof");
        await f.Copy(source, target, proof, 32, 4096); await f.Custody(target, "verify", new { proof });
        await f.Custody(target, "cutover", new { proof, expectedWriterFence = "2", expectedCustodyRevision = "1" });
        var oldPublished = f.Authority.Ticket!.Value;
        JsonElement Next(string id)
        {
            var value = Change(Change(Change(f.PrepareRequest!.Value, "operationId", id), "source", f.CustodyIdentity("target")), "destination", new { custodyId = "third", mode = "terminal" });
            return Change(value, "intentSha256", WireJson.Sha256(Bytes(Json(new { identity = value.GetProperty("identity"), source = value.GetProperty("source"), destination = value.GetProperty("destination"), expectedWriterFence = value.GetProperty("expectedWriterFence"), expectedRoot = value.GetProperty("expectedRoot") }))));
        }
        var next = await target.ExecuteCustodyAsync(Next("move-next"), f.Owner); Assert.Equal("prepared", Text(next.GetProperty("ticket"), "phase"));
        f.Authority.QueryOverride = oldPublished;
        var historical = await f.Custody(target, "query"); Assert.Equal("published", Text(historical.GetProperty("ticket"), "phase"));
        await Assert.ThrowsAsync<IOException>(() => target.ExecuteCustodyAsync(Next("unrelated-next"), f.Owner));
    }

    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    private static JsonElement Json(object? value) => WireJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value), 524288);
    private static string Canon(JsonElement value) => WireJson.CanonicalString(value, 524288);
    private static byte[] Bytes(JsonElement value) => WireJson.EncodeControl(value, 524288);
    private static JsonElement Change(JsonElement value, string name, object? replacement)
    { var fields = value.EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.Clone()); fields[name] = replacement; return Json(fields); }
    private static string KeyDigest(int id) => id.ToString("x64", CultureInfo.InvariantCulture);
    private sealed class Key : IArchiveKeyProvider { public string KeyId => "custody-test"; public byte[] ReadKey() => Enumerable.Range(1, 32).Select(x => (byte)x).ToArray(); }
    private sealed record ObjectData(string Kind, byte[] Bytes);
    private sealed class Plan
    {
        internal Fixture F = null!; internal JsonElement Begin; internal List<ObjectData> Objects = new();
        internal JsonElement Action(string action) => F.Request(action, new { transferId = Text(Begin, "transferId"), intentSha256 = Text(Begin, "intentSha256") });
        internal JsonElement Put(ObjectData item) => F.Request("put", new { transferId = Text(Begin, "transferId"), intentSha256 = Text(Begin, "intentSha256"), kind = item.Kind, sha256 = WireJson.Sha256(item.Bytes), byteLength = item.Bytes.Length, base64 = Convert.ToBase64String(item.Bytes) });
    }
    private sealed class Fixture : IDisposable
    {
        internal string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pst-custody-" + Guid.NewGuid().ToString("N"));
        internal string Revision = "1"; internal Authority Authority = new(); internal JsonElement? PrepareRequest;
        internal JsonElement Identity => Json(new { applicationScopeId = "app", endUserId = "user", sourceId = "source", sourceGeneration = "1", domainKey = "domain" });
        internal JsonElement Scope => Json(new { applicationScopeId = "app", endUserId = "user", authorizationRevision = Revision });
        internal string Owner => Canon(Json(new { scope = Scope, sessionId = "session", binding = new { bindingId = "binding", revision = "1", target = new { executorId = "executor", connectionId = "connection", connectionRevision = "1", workspaceId = "workspace", workspaceRevision = "1" } } }));
        internal JsonElement CustodyIdentity(string name) => Json(new { custodyId = name, custodyRevision = name == "source" ? "1" : "2", mode = "terminal" });
        internal Fixture() { System.IO.Directory.CreateDirectory(Directory); Authority.Selected = CustodyIdentity("source"); }
        internal SqliteTerminalPersistenceOptions Options(string name, StorageOpenMode mode = StorageOpenMode.Create, bool enabled = true) => new()
        { EnableProfile = true, Path = System.IO.Path.Combine(Directory, name + ".sqlite"), Mode = mode, Identity = Identity, ReadContext = () => Scope, KeyProvider = new Key(), EnableCustodyHandoff = enabled, Custody = CustodyIdentity(name), CustodyAuthority = Authority };
        internal JsonElement Request(string action, object? extra = null)
        {
            var fields = new Dictionary<string, object?> { ["contract"] = "terminal-persistence-v1", ["action"] = action };
            foreach (string field in new[] { "sourceId", "sourceGeneration", "domainKey" }) fields[field] = Identity.GetProperty(field);
            if (extra != null) foreach (var p in Json(extra).EnumerateObject()) fields[p.Name] = p.Value.Clone(); return Json(fields);
        }
        internal Task<JsonElement> Call(SqliteTerminalPersistenceStore store, JsonElement request) => store.ExecuteAsync(request, Owner);
        internal Task<JsonElement> Custody(SqliteTerminalPersistenceStore store, string action, object? extra = null)
        {
            var fields = new Dictionary<string, object?> { ["contract"] = "persistence-custody-handoff-v1", ["identity"] = Identity, ["operationId"] = PrepareRequest!.Value.GetProperty("operationId"), ["intentSha256"] = PrepareRequest.Value.GetProperty("intentSha256"), ["action"] = action };
            if (extra != null) foreach (var p in Json(extra).EnumerateObject()) fields[p.Name] = p.Value.Clone();
            return store.ExecuteCustodyAsync(Json(fields), Owner);
        }
        internal async Task<JsonElement> Prepare(SqliteTerminalPersistenceStore store)
        {
            var root = (await Call(store, Request("head"))).GetProperty("root");
            var request = Json(new { contract = "persistence-custody-handoff-v1", identity = Identity, operationId = "move", intentSha256 = new string('a', 64), action = "prepare", source = CustodyIdentity("source"), destination = new { custodyId = "target", mode = "terminal" }, expectedWriterFence = "1", expectedRoot = root.ValueKind == JsonValueKind.Null ? null : Text(root, "commitRoot") });
            PrepareRequest = Change(request, "intentSha256", WireJson.Sha256(Bytes(Json(new { identity = request.GetProperty("identity"), source = request.GetProperty("source"), destination = request.GetProperty("destination"), expectedWriterFence = request.GetProperty("expectedWriterFence"), expectedRoot = request.GetProperty("expectedRoot") }))));
            return await store.ExecuteCustodyAsync(PrepareRequest.Value, Owner);
        }
        internal async Task Stage(SqliteTerminalPersistenceStore store, Plan plan) { await Call(store, plan.Begin); foreach (var item in plan.Objects) await Call(store, plan.Put(item)); }
        internal Plan Plan(string id, int first, int count, JsonElement? root = null, int? added = null)
        {
            var plan = new Plan { F = this }; var body = Enumerable.Repeat((byte)42, 7000).ToArray();
            var bodyPage = new ObjectData("body-page", Bytes(Json(new { version = 1, kind = "body-page", index = 0, refs = new[] { new { sha256 = WireJson.Sha256(body), byteLength = body.Length } } })));
            plan.Objects.Add(bodyPage); plan.Objects.Add(new("body-block", body));
            var entries = new List<object>(); var pages = new List<string>();
            for (int i = first; i < first + count; i++)
            {
                var receipt = Encoding.UTF8.GetBytes("receipt-" + i); plan.Objects.Add(new("receipt-value", receipt));
                entries.Add(new { primaryKey = KeyDigest(i), secondaryKey = KeyDigest(100000 + i), value = new { sha256 = WireJson.Sha256(receipt), byteLength = receipt.Length } });
            }
            for (int start = 0; start < count; start += 32)
            {
                var bytes = Bytes(Json(new { version = 1, kind = "index-page", index = start / 32, entries = entries.Skip(start).Take(32) }));
                plan.Objects.Add(new("index-page", bytes)); pages.Add(WireJson.Sha256(bytes));
            }
            plan.Objects = plan.Objects.OrderBy(x => Array.IndexOf(new[] { "body-page", "index-page", "body-block", "receipt-value" }, x.Kind)).ToList();
            object? expected = root.HasValue ? new { commitRoot = Text(root.Value, "commitRoot"), generation = Text(root.Value, "generation"), bodyEtag = Text(root.Value.GetProperty("body"), "sha256"), indexRoot = Text(root.Value.GetProperty("index"), "root"), indexCount = Text(root.Value.GetProperty("index"), "count") } : null;
            var begin = Request("begin", new { transferId = id, expected, body = new { byteLength = body.Length, sha256 = WireJson.Sha256(body), blockCount = 1, pageHashes = new[] { WireJson.Sha256(bodyPage.Bytes) } }, index = new { entryCount = count, addedCount = added ?? count, pageHashes = pages }, declared = new { objects = plan.Objects.Count, bytes = plan.Objects.Sum(x => x.Bytes.Length) } });
            plan.Begin = Change(begin, "intentSha256", WireJson.Sha256(Bytes(begin))); return plan;
        }
        internal async Task<List<JsonElement>> Copy(SqliteTerminalPersistenceStore source, SqliteTerminalPersistenceStore target, JsonElement proof, int pageSize, int chunkSize)
        {
            var records = new List<JsonElement>(); JsonElement? cursor = null;
            do
            {
                var exported = await Custody(source, "export-page", new { cursor, limit = pageSize }); var page = exported.GetProperty("page");
                await Custody(target, "import-page", new { proof, page }); await Custody(target, "import-page", new { proof, page });
                records.AddRange(page.GetProperty("records").EnumerateArray().Select(x => x.Clone()));
                cursor = exported.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : exported.GetProperty("nextCursor").Clone();
            } while (cursor.HasValue);
            foreach (var record in records.Where(x => Text(x, "kind") == "object"))
            {
                var reference = record.GetProperty("object"); int length = reference.GetProperty("byteLength").GetInt32();
                for (int offset = 0; offset < length;)
                {
                    var exported = await Custody(source, "export-object", new { @object = reference, offset, length = chunkSize }); var chunk = exported.GetProperty("chunk");
                    await Custody(target, "import-object", new { chunk }); await Custody(target, "import-object", new { chunk });
                    offset += chunk.GetProperty("byteLength").GetInt32();
                }
            }
            return records;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    private sealed class Authority : ITerminalPersistenceCustodyAuthority
    {
        internal JsonElement Selected; internal JsonElement? Ticket, QueryOverride; internal Dictionary<string, JsonElement> Claims = new();
        internal bool LoseFinish, LoseCutover; internal int Acquires, Finishes; internal Action? AfterAcquire;
        public Task<ITerminalPersistenceWriterLease> AcquireWriterAsync(JsonElement identity, JsonElement custody, JsonElement originalPstRequest, CancellationToken cancellationToken = default)
        {
            string id = WireJson.Sha256(Bytes(Json(new object[] { "PST-CUSTODY", originalPstRequest })));
            if (Canon(custody) != Canon(Selected) || Ticket.HasValue && Text(Ticket.Value, "phase") != "published" || Claims.ContainsKey(id)) throw new IOException("writer is not newly acquired");
            var claim = Json(new { operationId = id, intentSha256 = WireJson.Sha256(Bytes(originalPstRequest)), claimId = Guid.NewGuid().ToString(), writerFence = "1", status = "active", selectedCustody = custody, responseDigest = (string?)null });
            Claims.Add(id, claim); Acquires++; AfterAcquire?.Invoke(); return Task.FromResult<ITerminalPersistenceWriterLease>(new Writer(this, claim));
        }
        public Task<ITerminalPersistenceHandoffLease> BeginHandoffAsync(JsonElement prepareRequest, CancellationToken cancellationToken = default)
        {
            if (Claims.Values.Any(x => Text(x, "status") != "settled")) throw new IOException("active writer");
            return Task.FromResult<ITerminalPersistenceHandoffLease>(new Handoff(this, prepareRequest));
        }
        public Task<JsonElement> ExecuteAsync(JsonElement request, CancellationToken cancellationToken = default)
        {
            string action = Text(request, "action");
            if (action == "writer-query") return Task.FromResult(Response(request, new { claim = Claims.TryGetValue(Text(request, "operationId"), out var claim) ? (JsonElement?)claim : null }));
            if (action == "verify") Ticket = Change(Ticket!.Value, "phase", "verified");
            if (action == "cutover")
            {
                if (Text(Ticket!.Value, "phase") != "verified") throw new IOException("not verified");
                Selected = Json(new { custodyId = Text(Ticket.Value.GetProperty("destination"), "custodyId"), custodyRevision = (long.Parse(Text(Ticket.Value.GetProperty("source"), "custodyRevision"), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture), mode = "terminal" });
                Ticket = Change(Change(Ticket.Value, "phase", "published"), "selectedCustody", Selected);
                if (LoseCutover) { LoseCutover = false; throw new IOException("synthetic lost cutover"); }
            }
            return Task.FromResult(action == "query" ? Response(request, new { preparing = false, ticket = QueryOverride ?? Ticket }) : Response(request, new { ticket = Ticket }));
        }
        private static JsonElement Response(JsonElement request, object extra)
        {
            var fields = new Dictionary<string, object?>(); foreach (string key in new[] { "contract", "identity", "operationId", "intentSha256", "action" }) fields[key] = request.GetProperty(key);
            foreach (var p in Json(extra).EnumerateObject()) fields[p.Name] = p.Value.Clone(); return Json(fields);
        }
        private sealed class Writer(Authority authority, JsonElement claim) : ITerminalPersistenceWriterLease
        {
            public JsonElement Claim => claim;
            public void AssertCurrent() { if (Canon(authority.Selected) != Canon(claim.GetProperty("selectedCustody"))) throw new IOException("revoked"); }
            public Task<JsonElement> FinishAsync(JsonElement settlement, CancellationToken cancellationToken = default)
            {
                authority.Finishes++; if (authority.LoseFinish) throw new MemoryPublicationRejectedException("request_conflict");
                var value = Change(Change(claim, "status", "settled"), "responseDigest", settlement.TryGetProperty("responseDigest", out var digest) ? digest : null);
                authority.Claims[Text(claim, "operationId")] = value; return Task.FromResult(value);
            }
        }
        private sealed class Handoff(Authority authority, JsonElement prepare) : ITerminalPersistenceHandoffLease
        {
            public void AssertCurrent() { if (Canon(authority.Selected) != Canon(prepare.GetProperty("source"))) throw new IOException("revoked"); }
            public Task<JsonElement> RecordPreparedAsync(JsonElement proof, CancellationToken cancellationToken = default)
            {
                authority.Ticket = Json(new { identity = prepare.GetProperty("identity"), operationId = prepare.GetProperty("operationId"), intentSha256 = prepare.GetProperty("intentSha256"), phase = "prepared", source = prepare.GetProperty("source"), destination = prepare.GetProperty("destination"), writerFence = "2", proof, selectedCustody = prepare.GetProperty("source") });
                return Task.FromResult(authority.Ticket.Value);
            }
        }
    }
}
