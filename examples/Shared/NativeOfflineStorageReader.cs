using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
#if WINDOWS || NETFRAMEWORK
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;
#endif

namespace Tansr.Examples;

// 冷启动离线阅读：只开原本机介质，不构造客户端、执行 owner、同步、ACK 或治理请求。
internal static class NativeOfflineStorageReader
{
    internal static async Task<string> ReadArchiveAsync(string configurationPath, CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var config = ReadJson(configurationPath, 65536); ct.ThrowIfCancellationRequested();
        if (Text(config, "format") != "tansr-example-archive-v1") throw new InvalidOperationException("archive_configuration_mismatch");
        var permission = new LocalReadPermission(Absolute(config, "trustedScopeFile"));
        string retentionPath = Absolute(config, "retentionAuthorityFile"); var retained = ReadJson(retentionPath);
        string retainedText = WireJson.CanonicalString(retained); var identity = retained.GetProperty("identity").Clone();
        if (identity.GetProperty("target").GetProperty("sessionId").GetString() != Text(config, "sessionId")) throw new InvalidOperationException("archive_session_mismatch");
        JsonElement Retention()
        {
            permission.Check(); var current = ReadJson(retentionPath);
            if (WireJson.CanonicalString(current) != retainedText) throw new InvalidOperationException("offline_retention_changed");
            return current;
        }
        var storage = config.GetProperty("storage");
        var keys = CurrentUserDpapiArchiveKeyProvider.Open(Absolute(storage, "keyPath"), Text(storage, "keyId"));
        using var store = await SqliteArchiveStore.OpenAsync(new SqliteArchiveStoreOptions
        {
            Path = Absolute(storage, "archivePath"), Mode = StorageOpenMode.Reopen, Identity = identity,
            Replica = Json(new { replicationId = Text(storage, "replicationId"), role = "primary" }),
            KeyProvider = keys, ReadContext = permission.ReadScope, ReadRetentionRevision = () => Text(Retention(), "revision"),
            AuthorizeRetention = _ => throw new InvalidOperationException("offline_mutation_forbidden")
        }, ct).ConfigureAwait(false);
        // 已知删除权威领先本地介质时拒绝读取；离线入口不能自行批准或应用新的删除意图。
        if (await store.RetentionRevisionAsync(ct).ConfigureAwait(false) != Text(Retention(), "revision")) throw new InvalidOperationException("offline_retention_sync_required");
        var head = await store.HeadAsync(ct).ConfigureAwait(false);
        if (!head.HasValue) { permission.Check(); _ = Retention(); return Banner + "\n本地授权档案暂无记录。"; }
        var page = await store.ReadRecordsAsync(new ArchiveReadRequest
        {
            Identity = identity, Selection = Json(new { fromSequence = "1", throughSequence = head.Value.GetProperty("sequence").GetString() }),
            MaxRecords = 128, MaxBytes = 1048576
        }, ct).ConfigureAwait(false);
        var lines = new List<string> { Banner }; int length = 0;
        foreach (var record in page.Records)
        {
            var reference = record.GetProperty("payload");
            if (reference.GetProperty("bytes").GetInt64() > 1048576 - length) { lines.Add("正文显示到达 1 MiB 上限。"); break; }
            var body = await store.BodyAsync(reference, ct).ConfigureAwait(false);
            try { length += body.Length; lines.Add(new UTF8Encoding(false, true).GetString(body)); }
            finally { Array.Clear(body, 0, body.Length); }
        }
        if (page.NextFromSequence != null) lines.Add("后续记录从 sequence=" + page.NextFromSequence + "，本页最多显示 128 条。");
        permission.Check(); _ = Retention(); ct.ThrowIfCancellationRequested(); return string.Join(Environment.NewLine, lines);
#else
        await Task.CompletedTask; ct.ThrowIfCancellationRequested(); throw new PlatformNotSupportedException("离线受护档案阅读需 Windows 目标。");
#endif
    }

    internal static async Task<string> ReadMemoryAsync(string configurationPath, CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var config = ReadJson(configurationPath, 65536);
        var configuration = NativeMemoryDeviceConfiguration.Parse(config);
        var permission = new LocalReadPermission(configuration.ScopeFile);
        string authorityPath = Absolute(config, "offlineAuthorityFile"); var authority = ReadJson(authorityPath);
        string authorityText = WireJson.CanonicalString(authority);
        void Check()
        {
            permission.Check();
            if (WireJson.CanonicalString(ReadJson(authorityPath)) != authorityText) throw new InvalidOperationException("offline_memory_authority_changed");
            if (!authority.GetProperty("memory").GetProperty("available").GetBoolean()) throw new InvalidOperationException("offline_memory_unavailable");
        }
        Check();
        using var store = await SqliteMemoryPublicationStore.OpenAsync(new SqliteMemoryPublicationOptions
        {
            EnablePreview = true, Path = configuration.PublicationPath, Mode = StorageOpenMode.Reopen,
            Identity = configuration.PublicationIdentity, ReadContext = () => { Check(); return permission.ReadScope(); },
            MaxTransfers = configuration.MaxTransfers, MaxStagingBytes = configuration.MaxStagingBytes, MaxPages = configuration.MaxPages
        }, ct).ConfigureAwait(false);
        var body = await store.ReadPublicationAsync(ct).ConfigureAwait(false);
        if (body == null) { Check(); return Banner + "\n本地尚无已提交的记忆出版。"; }
        try
        {
            // SHA 是可信宿主已观察的原出版，不从旧数据库自身反向生成授权；旧备份或新但未获准的版本均拒绝。
            if (WireJson.Sha256(body) != Text(authority, "publicationEtag")) throw new InvalidOperationException("offline_memory_sync_required");
            using var document = JsonDocument.Parse(body); var state = document.RootElement;
            var expected = authority.GetProperty("memory"); var memoryIdentity = state.GetProperty("identity");
            if (state.GetProperty("version").GetInt32() != 1 || WireJson.CanonicalString(memoryIdentity) != WireJson.CanonicalString(expected.GetProperty("identity")) ||
                Text(state, "revision") != Text(expected, "revision") || Text(state, "deletionGeneration") != Text(expected, "deletionGeneration"))
                throw new InvalidOperationException("offline_memory_source_changed");
            var identity = configuration.PublicationIdentity;
            if (Text(memoryIdentity, "sourceId") != Text(identity, "sourceId") || Text(memoryIdentity, "sourceGeneration") != Text(identity, "sourceGeneration") ||
                Text(memoryIdentity, "applicationScopeId") != Text(identity.GetProperty("scope"), "applicationScopeId") ||
                Text(memoryIdentity, "endUserId") != Text(identity.GetProperty("scope"), "endUserId")) throw new InvalidOperationException("offline_memory_source_changed");
            var domain = Json(memoryIdentity.EnumerateObject().Where(p => p.Name != "sourceGeneration").ToDictionary(p => p.Name, p => p.Value.Clone()));
            if (WireJson.Sha256(Encoding.UTF8.GetBytes(WireJson.CanonicalString(domain))) != Text(identity, "domainKey")) throw new InvalidOperationException("offline_memory_source_changed");
            if (state.GetProperty("activeOperation").ValueKind != JsonValueKind.Null || state.GetProperty("lease").ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("offline_memory_reconciliation_required");
            var files = state.GetProperty("files").EnumerateObject().ToArray(); var tombstones = state.GetProperty("tombstones");
            if (files.Length > 256 || tombstones.EnumerateObject().Count() > 512) throw new InvalidOperationException("offline_memory_capacity_exceeded");
            var lines = new List<string> { Banner }; int displayed = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested(); var value = file.Value; string text = value.GetProperty("text").GetString() ?? throw new InvalidOperationException("offline_memory_corrupt");
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
                if (bytes.Length > 1048576 || WireJson.Sha256(bytes) != Text(value, "sha256") || Number(value, "deletionGeneration") > Number(state, "deletionGeneration")) throw new InvalidOperationException("offline_memory_corrupt");
                bool deleted = tombstones.TryGetProperty(Hash(file.Name), out _) || value.GetProperty("provenance").EnumerateArray().Any(origin => tombstones.TryGetProperty(Hash(origin.GetString()!), out _));
                if (deleted) throw new InvalidOperationException("offline_memory_deleted");
                // 只呈现正文；不显示治理元数据、待处理邀请、receipt、lease 或已失效的索引。
                if (!file.Name.StartsWith("memory/", StringComparison.Ordinal) || !file.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Name.Equals("memory/MEMORY.md", StringComparison.OrdinalIgnoreCase) && !state.GetProperty("indexValid").GetBoolean()) continue;
                if (displayed + bytes.Length > 1048576) { lines.Add("记忆显示到达 1 MiB 上限。"); break; }
                displayed += bytes.Length; lines.Add(file.Name + Environment.NewLine + text);
            }
            Check(); ct.ThrowIfCancellationRequested(); return string.Join(Environment.NewLine, lines);
        }
        finally { Array.Clear(body, 0, body.Length); }
#else
        await Task.CompletedTask; ct.ThrowIfCancellationRequested(); throw new PlatformNotSupportedException("离线本地记忆阅读需 Windows 目标。");
#endif
    }

#if WINDOWS || NETFRAMEWORK
    private const string Banner = "离线只读：依据本机仍有效的既有授权读取；未联系 Serve，不代表已获得远端最新权限或删除状态。";
    private sealed class LocalReadPermission
    {
        private readonly string path, snapshot;
        internal LocalReadPermission(string path) { this.path = path; snapshot = WireJson.CanonicalString(ReadJson(path, 65536)); Check(); }
        internal JsonElement ReadScope() { var current = Check(); return current.GetProperty("scope").Clone(); }
        internal JsonElement Check()
        {
            var current = ReadJson(path, 65536); var permission = current.GetProperty("offlineRead");
            if (WireJson.CanonicalString(current) != snapshot || !permission.GetProperty("allowed").GetBoolean() ||
                Text(permission, "authorizationRevision") != Text(current.GetProperty("scope"), "authorizationRevision") ||
                !DateTimeOffset.TryParse(Text(permission, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry) || expiry <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("offline_read_not_authorized");
            _ = Text(current, "principal"); return current;
        }
    }
    private static JsonElement ReadJson(string path, int maximum = 1048576)
    {
        if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path) throw new InvalidOperationException("offline_absolute_path_required");
        for (string? item = path; item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("offline_reparse_point");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidOperationException("offline_configuration_too_large");
        using var stream = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { stream.Write(buffer, 0, count); if (stream.Length > maximum) throw new InvalidOperationException("offline_configuration_too_large"); }
        using var document = JsonDocument.Parse(stream.ToArray()); return document.RootElement.Clone();
    }
    private static JsonElement Json(object value) { using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value)); return document.RootElement.Clone(); }
    private static string Hash(string value) => WireJson.Sha256(Encoding.UTF8.GetBytes(value));
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() is { Length: > 0 } text && !text.Any(char.IsControl) ? text : throw new InvalidOperationException("offline_invalid_" + name);
    private static long Number(JsonElement value, string name) => long.TryParse(Text(value, name), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : throw new InvalidOperationException("offline_invalid_" + name);
    private static string Absolute(JsonElement value, string name) { var path = Text(value, name); if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path) throw new InvalidOperationException("offline_absolute_path_required"); return path; }
#endif
}
