using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;
using Tansr.Sdk.Windows.Tests.Storage;

namespace Tansr.Sdk.Windows.Tests.Examples;

public sealed class NativeOfflineStorageReaderTests
{
    [Fact]
    public async Task ColdArchiveReadsOriginalProtectedBodyWithoutAcknowledgingAndRejectsKnownDeletion()
    {
        using var fixture = new SqliteArchiveStoreTests.Fixture(true); string directory = fixture.Directory;
        string keysPath = Path.Combine(directory, "keys.bin"), scopePath = Path.Combine(directory, "scope.json"), retentionPath = Path.Combine(directory, "retention.json"), configPath = Path.Combine(directory, "config.json");
        var keys = CurrentUserDpapiArchiveKeyProvider.Create(keysPath, "offline-key");
        var options = fixture.Options(); options.KeyProvider = keys;
        JsonElement ack;
        using (var store = await SqliteArchiveStore.OpenAsync(options)) ack = await store.ReceiveAsync(fixture.Input);
        Write(scopePath, Permission(fixture.Scope)); Write(retentionPath, new { identity = fixture.Identity, revision = "0", approvedRetention = fixture.Retention() });
        Write(configPath, new
        {
            format = "tansr-example-archive-v1",
            sessionId = "session/中文",
            trustedScopeFile = scopePath,
            retentionAuthorityFile = retentionPath,
            storage = new { archivePath = fixture.Path, keyPath = keysPath, keyId = "offline-key", replicationId = "group" }
        });
        string output = await NativeOfflineStorageReader.ReadArchiveAsync(configPath);
        Assert.Contains(Encoding.UTF8.GetString(fixture.Body), output); Assert.Contains("未联系 Serve", output);
        var reopen = fixture.Options(StorageOpenMode.Reopen); reopen.KeyProvider = keys;
        using (var store = await SqliteArchiveStore.OpenAsync(reopen))
        {
            Assert.Equal(WireJson.CanonicalString(ack), WireJson.CanonicalString((await store.PendingAsync())!.Value));
            Assert.Equal("0", await store.RetentionRevisionAsync());
            // 测试宿主在已证明离线入口没有ACK后，显式确认，准备原删除场景。
            await store.ConfirmAsync(fixture.Receipt(ack));
        }
        // 当前可信删除领先介质，不能为了离线可读自行ApplyRetention。
        Write(retentionPath, new { identity = fixture.Identity, revision = "1", approvedRetention = fixture.Retention() });
        await Assert.ThrowsAnyAsync<Exception>(() => NativeOfflineStorageReader.ReadArchiveAsync(configPath));
        fixture.RetentionRevision = "1";
        using (var store = await SqliteArchiveStore.OpenAsync(reopen)) await store.ApplyRetentionAsync(fixture.Retention());
        Assert.Equal("deleted", (await Assert.ThrowsAsync<StorageException>(() => NativeOfflineStorageReader.ReadArchiveAsync(configPath))).Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DeniedOrExpiredOfflineAuthorityDoesNotReadBody(bool allowed, bool expired)
    {
        using var fixture = new SqliteArchiveStoreTests.Fixture(); string scope = Path.Combine(fixture.Directory, "scope.json"), config = Path.Combine(fixture.Directory, "config.json");
        Write(scope, Permission(fixture.Scope, allowed, expired));
        Write(config, new { format = "tansr-example-archive-v1", trustedScopeFile = scope });
        Assert.Equal("offline_read_not_authorized", (await Assert.ThrowsAsync<InvalidOperationException>(() => NativeOfflineStorageReader.ReadArchiveAsync(config))).Message);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task ColdMemoryReadsOriginalPublicationButRejectsBackupAuthorityChangeAndKnownRevocation()
    {
        using var fixture = new SqliteMemoryPublicationStoreTests.Fixture(); string directory = fixture.Directory;
        string scopePath = Path.Combine(directory, "scope.json"), authorityPath = Path.Combine(directory, "memory-authority.json"), configPath = Path.Combine(directory, "config.json");
        var memoryIdentity = Json(new { kind = "client-managed", domain = "offline-memory", sourceId = "offline-source", sourceGeneration = "1", applicationScopeId = "app", endUserId = "user" });
        var domain = Json(memoryIdentity.EnumerateObject().Where(p => p.Name != "sourceGeneration").ToDictionary(p => p.Name, p => p.Value.Clone()));
        var identity = Json(new { scope = new { applicationScopeId = "app", endUserId = "user" }, sourceId = "offline-source", sourceGeneration = "1", domainKey = WireJson.Sha256(Encoding.UTF8.GetBytes(WireJson.CanonicalString(domain))) });
        const string fact = "已授权本机记忆：violet report headings";
        var state = Json(new
        {
            version = 1,
            identity = memoryIdentity,
            epoch = "1",
            revision = "1",
            deletionGeneration = "0",
            files = new Dictionary<string, object> { ["memory/preferences.md"] = new { text = fact, sha256 = WireJson.Sha256(Encoding.UTF8.GetBytes(fact)), modifiedAt = 1, provenance = new[] { "manual:fixture" }, deletionGeneration = "0" } },
            indexValid = false,
            invites = Array.Empty<object>(),
            consolidation = (string?)null,
            tombstones = new Dictionary<string, string>(),
            receipts = new Dictionary<string, object>(),
            activeOperation = (string?)null,
            lease = (object?)null,
            fence = "0"
        });
        byte[] original = Encoding.UTF8.GetBytes(WireJson.CanonicalString(state));
        var options = fixture.Options(); options.Identity = identity;
        using (var store = await SqliteMemoryPublicationStore.OpenAsync(options))
        {
            JsonElement Request(string action, object extra)
            {
                var value = new Dictionary<string, JsonElement> { ["contract"] = Json("terminal-services-v1"), ["action"] = Json(action) };
                foreach (string key in new[] { "sourceId", "sourceGeneration", "domainKey" }) value[key] = identity.GetProperty(key);
                foreach (var p in Json(extra).EnumerateObject()) value[p.Name] = p.Value.Clone(); return Json(value);
            }
            await store.ExecuteAsync(Request("begin", fixture.Begin("original", original)), fixture.Owner);
            await store.ExecuteAsync(Request("chunk", fixture.Chunk("original", 0, original)), fixture.Owner);
            await store.ExecuteAsync(Request("commit", new { transferId = "original" }), fixture.Owner);
            byte[] copy = (await store.ReadPublicationAsync())!; copy[0] = 0;
            Assert.Equal(original, await store.ReadPublicationAsync());
        }
        Write(scopePath, Permission(fixture.Scope));
        object Authority(string deletion = "0", bool available = true) => new { memory = new { identity = memoryIdentity, revision = "1", deletionGeneration = deletion, available }, publicationEtag = WireJson.Sha256(original) };
        Write(authorityPath, Authority());
        Write(configPath, new
        {
            format = "tansr-example-device-memory-v1",
            enablePreview = true,
            serveUrl = "http://127.0.0.1:1",
            sessionId = "session",
            executorId = "executor",
            trustedScopeFile = scopePath,
            controllerTokenEnvironment = "UNUSED_NO_TOKEN",
            deviceTokenEnvironment = "UNUSED_NO_TOKEN",
            offlineAuthorityFile = authorityPath,
            workspace = new { path = directory, id = "workspace", revision = "1" },
            journal = new { path = Path.Combine(directory, "unused-journal.sqlite"), mode = "reopen", maxOperations = 64, maxStoredBytes = 1048576, maxPages = 8192 },
            publication = new { path = fixture.Path, mode = "reopen", identity, maxTransfers = fixture.MaxTransfers, maxStagingBytes = 8388608, maxPages = 8192 }
        });
        Assert.Contains(fact, await NativeOfflineStorageReader.ReadMemoryAsync(configPath));
        Assert.False(File.Exists(Path.Combine(directory, "unused-journal.sqlite")));
        Write(authorityPath, Authority("1"));
        Assert.Equal("offline_memory_source_changed", (await Assert.ThrowsAsync<InvalidOperationException>(() => NativeOfflineStorageReader.ReadMemoryAsync(configPath))).Message);
        Write(authorityPath, Authority(available: false));
        Assert.Equal("offline_memory_unavailable", (await Assert.ThrowsAsync<InvalidOperationException>(() => NativeOfflineStorageReader.ReadMemoryAsync(configPath))).Message);
        Write(authorityPath, Authority()); Write(scopePath, Permission(fixture.Scope, allowed: false));
        Assert.Equal("offline_read_not_authorized", (await Assert.ThrowsAsync<InvalidOperationException>(() => NativeOfflineStorageReader.ReadMemoryAsync(configPath))).Message);
        options.Mode = StorageOpenMode.Reopen;
        using var final = await SqliteMemoryPublicationStore.OpenAsync(options);
        Assert.Equal(1, (await final.GetCapacityAsync()).StoredTransfers); Assert.Equal(original, await final.ReadPublicationAsync());
    }

    private static object Permission(JsonElement scope, bool allowed = true, bool expired = false) => new
    { principal = "fixture-principal", scope, offlineRead = new { allowed, authorizationRevision = scope.GetProperty("authorizationRevision").GetString(), expiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5).ToString("O") } };
    private static JsonElement Json(object value) { using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value)); return document.RootElement.Clone(); }
    private static void Write(string path, object value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));
}
