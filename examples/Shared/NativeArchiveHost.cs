using System.IO;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
#if WINDOWS || NETFRAMEWORK
using System.Security.Cryptography;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Security;
using Tansr.Sdk.Windows.Storage;
#endif

namespace Tansr.Examples;

// 只装配可信宿主配置和公开 SDK；SSE、耐久 ACK、材料上传/响应由 ArchiveSessionHost 负责。
internal sealed class NativeArchiveHost
{
#if WINDOWS || NETFRAMEWORK
    private readonly TansrClient client;
    private readonly bool ownsClient;
    private readonly SqliteArchiveStore store;
    private readonly SqliteMaterialResponseOutbox outbox;
    private readonly ArchiveSessionHost host;
    private readonly JsonElement identity;
    private readonly Func<JsonElement> retention;
    private readonly CancellationTokenSource stop;
    private readonly Task completion;
    private Task? stopping;
    private readonly object gate = new();
    private NativeArchiveHost(TansrClient client, SqliteArchiveStore store, SqliteMaterialResponseOutbox outbox,
        ArchiveSessionHost host, JsonElement identity, Func<JsonElement> retention, CancellationTokenSource stop, bool ownsClient)
    { this.client = client; this.ownsClient = ownsClient; this.store = store; this.outbox = outbox; this.host = host; this.identity = identity; this.retention = retention; this.stop = stop; completion = host.ObserveAsync(stop.Token); }
    internal Task Completion => completion;
#else
    private NativeArchiveHost() { }
    internal Task Completion => Task.CompletedTask;
#endif
    internal static async Task<NativeArchiveHost> StartAsync(string configurationPath, string expectedSessionId, Uri expectedServeUri, CancellationToken ct = default, TansrClient? borrowedController = null)
    {
#if WINDOWS || NETFRAMEWORK
        using var document = JsonDocument.Parse(await BoundedFiles.ReadAsync(configurationPath, 65536, ct).ConfigureAwait(false)); var config = document.RootElement;
        if (Text(config, "format") != "tansr-example-archive-v1" || Text(config, "sessionId") != expectedSessionId) throw new InvalidOperationException("archive_configuration_mismatch");
        var endpoint = new Uri(Text(config, "serveUrl"), UriKind.Absolute);
        if (endpoint != expectedServeUri || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) throw new InvalidOperationException("archive_endpoint_mismatch");
        var authority = TrustedExampleScope.FromPath(Absolute(config, "trustedScopeFile")); string tokenName = Text(config, "tokenEnvironment");
        if (tokenName.Length > 128 || tokenName.Any(c => !char.IsLetterOrDigit(c) && c != '_')) throw new InvalidOperationException("archive_token_environment_required");
        var client = borrowedController ?? new TansrClient(new TansrClientOptions
        {
            BaseUri = endpoint, AllowInsecureLoopback = config.TryGetProperty("allowInsecureLoopback", out var http) && http.GetBoolean(),
            PrincipalProvider = authority.ReadPrincipal, ExecutionScopeProvider = authority.ReadScope,
            TokenProvider = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(Environment.GetEnvironmentVariable(tokenName) is { Length: > 0 } value ? value : throw new InvalidOperationException("archive_token_environment_empty")); }
        });
        SqliteArchiveStore? store = null; SqliteMaterialResponseOutbox? outbox = null; NativeArchiveHost? result = null;
        var observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var archive = new ArchiveClient(client); await archive.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            var target = await archive.GetBindingTargetAsync(Json(new { protocol = "sdk2-ext-v1", sessionId = expectedSessionId }), ct).ConfigureAwait(false);
            if (!target.TryGetProperty("bindingId", out var id) || id.ValueKind != JsonValueKind.String) throw new InvalidOperationException("archive_session_binding_unavailable");
            string bindingId = id.GetString()!; var binding = await archive.GetBindingAsync(bindingId, ct).ConfigureAwait(false);
            var status = await archive.GetArchiveStatusAsync(bindingId, ct).ConfigureAwait(false); var scope = authority.ReadScope();
            if (binding.GetProperty("target").GetProperty("sessionId").GetString() != expectedSessionId) throw new InvalidOperationException("archive_session_mismatch");
            var identity = Json(new { scope = new { applicationScopeId = scope.GetProperty("applicationScopeId").GetString(), endUserId = scope.GetProperty("endUserId").GetString() }, bindingId,
                sourceId = status.GetProperty("sourceId").GetString(), sourceGeneration = status.GetProperty("sourceGeneration").GetString(),
                target = new { sessionId = expectedSessionId, generations = status.GetProperty("generations") } });
            var storage = config.GetProperty("storage"); string modeText = Text(storage, "mode");
            if (modeText != "create" && modeText != "reopen") throw new InvalidOperationException("archive_explicit_storage_mode_required");
            var mode = modeText == "create" ? StorageOpenMode.Create : StorageOpenMode.Reopen;
            string archivePath = Absolute(storage, "archivePath"), outboxPath = Absolute(storage, "outboxPath"), cursorPath = Absolute(storage, "cursorPath"), keyPath = Absolute(storage, "keyPath"), retentionPath = Absolute(config, "retentionAuthorityFile");
            if (new[] { archivePath, outboxPath, cursorPath, keyPath, retentionPath, Absolute(config, "trustedScopeFile") }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 6) throw new InvalidOperationException("archive_storage_paths_must_be_distinct");
            JsonElement Retention()
            {
                using var input = new FileStream(retentionPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > 1048576) throw new InvalidOperationException("archive_retention_authority_too_large");
                using var saved = JsonDocument.Parse(input); var value = saved.RootElement.Clone();
                if (WireJson.CanonicalString(value.GetProperty("identity")) != WireJson.CanonicalString(identity)) throw new InvalidOperationException("archive_retention_identity_changed");
                return value;
            }
            _ = Retention();
            var keys = mode == StorageOpenMode.Create ? CurrentUserDpapiArchiveKeyProvider.Create(keyPath, Text(storage, "keyId")) : CurrentUserDpapiArchiveKeyProvider.Open(keyPath, Text(storage, "keyId"));
            store = await SqliteArchiveStore.OpenAsync(new SqliteArchiveStoreOptions
            {
                Path = archivePath, Mode = mode, Identity = identity, Replica = Json(new { replicationId = Text(storage, "replicationId"), role = "primary" }),
                KeyProvider = keys, ReadContext = authority.ReadScope, ReadRetentionRevision = () => Text(Retention(), "revision"),
                AuthorizeRetention = value => { if (WireJson.CanonicalString(Retention().GetProperty("approvedRetention")) != WireJson.CanonicalString(value)) throw new InvalidOperationException("archive_retention_not_authorized"); }
            }, ct).ConfigureAwait(false);
            outbox = await SqliteMaterialResponseOutbox.OpenAsync(new SqliteMaterialResponseOutboxOptions { Path = outboxPath, Mode = mode, Identity = identity, ReadContext = authority.ReadScope }, ct).ConfigureAwait(false);
            var cursor = new CursorFile(cursorPath, identity, authority, mode);
            var host = new ArchiveSessionHost(archive, store, new ArchiveSessionOptions
            {
                Identity = identity, ReadContext = authority.ReadScope, MaterialOutbox = outbox,
                ReadEventCursorAsync = cursor.ReadAsync, SaveEventCursorAsync = cursor.SaveAsync
            });
            result = new NativeArchiveHost(client, store, outbox, host, identity, Retention, observation, borrowedController == null);
            await host.Ready.ConfigureAwait(false); return result;
        }
        catch
        {
            if (result != null) await result.StopAsync().ConfigureAwait(false);
            else { observation.Cancel(); observation.Dispose(); if (outbox != null) await outbox.CloseAsync().ConfigureAwait(false); if (store != null) await store.CloseAsync().ConfigureAwait(false); if (borrowedController == null) client.Dispose(); }
            throw;
        }
#else
        await Task.CompletedTask; throw new PlatformNotSupportedException("本地受护档案演示需 Windows 目标。");
#endif
    }
    internal async Task<string> SynchronizeAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var current = retention(); string revision = Text(current, "revision");
        if (await store.RetentionRevisionAsync(ct).ConfigureAwait(false) != revision) await store.ApplyRetentionAsync(current.GetProperty("approvedRetention"), ct).ConfigureAwait(false);
        var result = await host.SynchronizeAsync(ct).ConfigureAwait(false);
        return "本次档案记录=" + result.ArchivedRecords + "；下载完成=" + result.Complete + "；已确认 ACK=" + result.Receipts.Count;
#else
        await Task.CompletedTask; ct.ThrowIfCancellationRequested(); throw new PlatformNotSupportedException();
#endif
    }
    internal async Task<string> ReadStatusAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var coverage = await store.CoverageAsync(ct).ConfigureAwait(false);
        return "绑定=" + identity.GetProperty("bindingId").GetString() + "；本地覆盖=" + coverage.GetRawText() + "；待对账 ACK=" + (await store.PendingAsync(ct).ConfigureAwait(false) != null) + "；事件通道=" + (completion.IsCompleted ? "已停止" : "已连接");
#else
        await Task.CompletedTask; ct.ThrowIfCancellationRequested(); throw new PlatformNotSupportedException();
#endif
    }
    internal async Task<string> ReadRecordsAsync(CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        var head = await store.HeadAsync(ct).ConfigureAwait(false); if (!head.HasValue) return "本地授权档案暂无记录；界面草稿不属于此档案。";
        var page = await host.ReadRecordsAsync(new ArchiveReadRequest { Identity = identity, Selection = Json(new { fromSequence = "1", throughSequence = head.Value.GetProperty("sequence").GetString() }), MaxRecords = 128, MaxBytes = 1048576 }, ct).ConfigureAwait(false);
        var lines = new List<string>(); int shownBytes = 0;
        foreach (var record in page.Records)
        {
            var reference = record.GetProperty("payload");
            if (reference.GetProperty("bytes").GetInt64() > 1048576 - shownBytes) { lines.Add("正文显示到达 1 MiB 上限，剩余记录仍保存在受护档案中。"); break; }
            var body = await store.BodyAsync(reference, ct).ConfigureAwait(false); shownBytes += body.Length; lines.Add(new UTF8Encoding(false, true).GetString(body));
        }
        if (page.NextFromSequence != null) lines.Add("后续记录从 sequence=" + page.NextFromSequence + "；显示已按 128 条/1 MiB 有界截页。");
        return string.Join(Environment.NewLine, lines);
#else
        await Task.CompletedTask; ct.ThrowIfCancellationRequested(); throw new PlatformNotSupportedException();
#endif
    }
    internal Task StopAsync()
    {
#if WINDOWS || NETFRAMEWORK
        lock (gate) return stopping ??= StopCore();
#else
        return Task.CompletedTask;
#endif
    }
#if WINDOWS || NETFRAMEWORK
    private async Task StopCore()
    {
        stop.Cancel(); try { await completion.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { /* 原失败由 Completion/调用入口呈现；释放所有本地介质。 */ }
        try { await outbox.CloseAsync().ConfigureAwait(false); } finally { try { await store.CloseAsync().ConfigureAwait(false); } finally { if (ownsClient) client.Dispose(); stop.Dispose(); } }
    }
    private static JsonElement Json(object value) { using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value)); return document.RootElement.Clone(); }
    private static string Text(JsonElement value, string field) => value.GetProperty(field).GetString() is { Length: > 0 } text && !text.Any(char.IsControl) ? text : throw new InvalidOperationException("archive_invalid_" + field);
    private static string Absolute(JsonElement value, string field)
    { var path = Text(value, field); if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path) throw new InvalidOperationException("archive_absolute_path_required"); return path; }
    // 示例游标是受 DPAPI 保护的本机进度，不充当授权来源、档案正文或恢复票。
    private sealed class CursorFile
    {
        private readonly string path, identityText; private readonly TrustedExampleScope authority; private readonly byte[] entropy;
        internal CursorFile(string path, JsonElement identity, TrustedExampleScope authority, StorageOpenMode mode)
        {
            this.path = path; this.authority = authority; identityText = WireJson.CanonicalString(identity);
            entropy = Encoding.UTF8.GetBytes("tansr.example.archive-cursor.v1\n" + identityText);
            if (mode == StorageOpenMode.Create) Write(null, true); else if (!File.Exists(path)) throw new InvalidOperationException("archive_cursor_missing");
        }
        internal Task<string?> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Check(); byte[] bytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > 16384) throw new InvalidOperationException("archive_cursor_too_large");
                using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { output.Write(buffer, 0, count); if (output.Length > 16384) throw new InvalidOperationException("archive_cursor_too_large"); }
                bytes = output.ToArray();
            }
            byte[] plain = ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.CurrentUser);
            try { using var document = JsonDocument.Parse(plain); var value = document.RootElement; if (value.GetProperty("identity").GetString() != identityText) throw new InvalidOperationException("archive_cursor_identity_changed"); Check(); ct.ThrowIfCancellationRequested(); return Task.FromResult(value.GetProperty("cursor").GetString()); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        internal Task SaveAsync(string cursor, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Check(); Write(cursor, false); Check(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        private void Check()
        {
            var scope = authority.ReadScope(); using var document = JsonDocument.Parse(identityText); var saved = document.RootElement.GetProperty("scope");
            if (scope.GetProperty("applicationScopeId").GetString() != saved.GetProperty("applicationScopeId").GetString() || scope.GetProperty("endUserId").GetString() != saved.GetProperty("endUserId").GetString()) throw new InvalidOperationException("archive_cursor_scope_changed");
            foreach (var candidate in new[] { path, Path.GetDirectoryName(path)! }) if (File.Exists(candidate) || Directory.Exists(candidate))
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("archive_cursor_reparse_point");
        }
        private void Write(string? cursor, bool create)
        {
            Check(); var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new { identity = identityText, cursor }), entropy, DataProtectionScope.CurrentUser);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                Check(); if (create) File.Move(temporary, path); else File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
#endif
}
