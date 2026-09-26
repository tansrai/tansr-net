using System.IO;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
#if WINDOWS || NETFRAMEWORK
using System.Security.Cryptography;
using Tansr.Sdk.Cache;
using Tansr.Sdk.Protocol;
#endif

namespace Tansr.Examples;

// 仅装配原公开 preview 门面与受护原意图；缓存分组、可信跨运行恢复和计费仍在 Serve。
internal sealed class NativeCacheContinuityHost
{
#if WINDOWS || NETFRAMEWORK
    private readonly CacheContinuityClient cache;
    private readonly string sessionId, path, owner;
    private readonly TrustedExampleScope authority;
    private readonly byte[] entropy;
    private readonly FileStream lease;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool closed;
    private JsonElement saved;
    private NativeCacheContinuityHost(TansrClient client, string sessionId, string path, string owner, TrustedExampleScope authority)
    {
        cache = new CacheContinuityClient(client, enablePreview: true); this.sessionId = sessionId; this.path = path; this.owner = owner; this.authority = authority;
        entropy = Encoding.UTF8.GetBytes("tansr.example.cache-state.v1\n" + owner); Check(); string lockPath = path + ".lock";
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("cache_state_reparse_point");
        lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
#else
    private NativeCacheContinuityHost() { }
#endif
    internal static async Task<NativeCacheContinuityHost> StartAsync(string configurationPath, string expectedSessionId, Uri expectedServeUri,
        TansrClient borrowedController, CancellationToken ct = default)
    {
#if WINDOWS || NETFRAMEWORK
        if (borrowedController == null) throw new ArgumentNullException(nameof(borrowedController));
        using var document = JsonDocument.Parse(await BoundedFiles.ReadAsync(configurationPath, 65536, ct).ConfigureAwait(false)); var config = document.RootElement;
        if (Text(config, "format") != "tansr-example-cache-continuity-v1" || !config.GetProperty("enablePreview").GetBoolean()) throw new InvalidOperationException("cache_explicit_preview_required");
        var endpoint = new Uri(Text(config, "serveUrl"), UriKind.Absolute);
        if (endpoint != expectedServeUri || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || Text(config, "sessionId") != expectedSessionId)
            throw new InvalidOperationException("cache_connection_mismatch");
        string path = Absolute(config, "statePath"), scopePath = Absolute(config, "trustedScopeFile");
        if (string.Equals(path, scopePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("cache_paths_must_be_distinct");
        var authority = TrustedExampleScope.FromPath(scopePath); var owner = Owner(authority, endpoint);
        var host = new NativeCacheContinuityHost(borrowedController, expectedSessionId, path, owner, authority);
        try
        {
            var capabilities = await host.cache.DiscoverAsync(ct).ConfigureAwait(false);
            if (!capabilities.Available) throw new InvalidOperationException("cache_not_available");
            string mode = Text(config, "mode");
            if (mode == "create") host.Save(Json(new { format = "tansr-example-cache-state-v1", owner, operation = (object?)null, bindingId = (string?)null, ticket = (string?)null }), true, ct);
            else if (mode == "reopen") host.saved = host.Read(ct);
            else throw new InvalidOperationException("cache_explicit_create_or_reopen_required");
            return host;
        }
        catch { host.lease.Dispose(); throw; }
#else
        await Task.CompletedTask; throw new PlatformNotSupportedException("受护缓存连续性示例需 Windows 目标。");
#endif
    }
    internal Task<string> NewAsync(CancellationToken ct = default) => Run("new", ct);
    internal Task<string> ResumeAsync(CancellationToken ct = default) => Run("resume", ct);
    internal Task<string> CloseAsync(CancellationToken ct = default) => Run("close", ct);
    internal Task<string> QueryAsync(CancellationToken ct = default) => Run("query", ct);
    internal Task<string> ReadDiagnosticsAsync(CancellationToken ct = default) => Run("diagnostics", ct);
    internal Task<string> ReadStatusAsync(CancellationToken ct = default) => Run("status", ct);
    internal async Task StopAsync()
    {
#if WINDOWS || NETFRAMEWORK
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (closed) return; closed = true; lease.Dispose(); }
        finally { gate.Release(); }
#else
        await Task.CompletedTask;
#endif
    }
    private async Task<string> Run(string action, CancellationToken ct)
    {
#if WINDOWS || NETFRAMEWORK
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            saved = Read(ct); var prior = saved.GetProperty("operation");
            bool pending = prior.ValueKind == JsonValueKind.Object && prior.GetProperty("pending").GetBoolean();
            string? bindingId = saved.GetProperty("bindingId").GetString(), ticket = saved.GetProperty("ticket").GetString();
            if (action == "status") return "逻辑缓存=" + (bindingId ?? "未启用") + "；原请求=" + (pending ? "待对账，只能查询原意图" : "已确认或尚未发起") + "；供应商命中/费用=unknown";
            if (action == "diagnostics")
            {
                if (bindingId == null) return "尚无逻辑缓存绑定；供应商命中/费用=unknown";
                var diagnostics = await cache.ReadDiagnosticsAsync(bindingId, cancellationToken: ct).ConfigureAwait(false);
                Check(); return "诊断行数=" + diagnostics.GetProperty("rows").GetArrayLength() + "；缺失供应商命中和计费事实时保持 unknown。";
            }
            CacheContinuityOperation operation;
            if (action == "query")
            {
                if (prior.ValueKind != JsonValueKind.Object) return "没有已保存的原请求可供对账。";
                operation = Restore(prior);
            }
            else
            {
                if (pending) throw new InvalidOperationException("cache_query_original_required");
                if (action == "new") operation = await cache.PrepareOpenAsync(sessionId, CacheContinuityOpenKind.New, cancellationToken: ct).ConfigureAwait(false);
                else if (action == "resume")
                {
                    if (ticket == null) throw new InvalidOperationException("cache_original_ticket_required");
                    operation = await cache.PrepareOpenAsync(sessionId, CacheContinuityOpenKind.Resume, CacheContinuityTicket.Restore(ticket), cancellationToken: ct).ConfigureAwait(false);
                }
                else
                {
                    if (bindingId == null || ticket == null) throw new InvalidOperationException("cache_original_ticket_required");
                    var binding = await cache.ReadBindingAsync(bindingId, ct).ConfigureAwait(false);
                    operation = await cache.PrepareCloseAsync(binding, CacheContinuityTicket.Restore(ticket), cancellationToken: ct).ConfigureAwait(false);
                }
                Check(); Save(State(operation, true, bindingId, ticket), false, ct); // 耐久保存原主体/字节后才允许外发。
            }
            // 失败不换键、不重发：原请求保持 pending，用户必须选择 Query；即使进程重启也一致。
            var receipt = action == "query" ? await cache.QueryAsync(operation, ct).ConfigureAwait(false) : await cache.SubmitAsync(operation, ct).ConfigureAwait(false);
            Check(); Save(State(operation, false, receipt.Binding.Id, receipt.Ticket?.ExportProtectedValue()), false, ct);
            return "逻辑缓存状态=" + receipt.Binding.State + "；logicalRef=" + receipt.Binding.LogicalReference + "；原请求已确认；供应商命中/费用=unknown";
        }
        finally { gate.Release(); }
#else
        await Task.CompletedTask; ct.ThrowIfCancellationRequested(); throw new PlatformNotSupportedException();
#endif
    }
#if WINDOWS || NETFRAMEWORK
    private JsonElement State(CacheContinuityOperation operation, bool pending, string? bindingId, string? ticket) => Json(new
    {
        format = "tansr-example-cache-state-v1", owner, bindingId, ticket,
        operation = new { action = operation.Action, principal = operation.OriginalPrincipal, request = Convert.ToBase64String(operation.ExportOriginalRequest()),
            receipt = operation.ExportOriginalReceipt() is { } bytes ? Convert.ToBase64String(bytes) : null, pending }
    });
    private CacheContinuityOperation Restore(JsonElement value) => cache.RestoreOperation(Text(value, "action"), Convert.FromBase64String(Text(value, "request")),
        Text(value, "principal"), value.GetProperty("receipt").GetString() is { } receipt ? Convert.FromBase64String(receipt) : null);
    private JsonElement Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Check(); byte[] bytes;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length > 1048576) throw new InvalidOperationException("cache_state_too_large");
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { output.Write(buffer, 0, count); if (output.Length > 1048576) throw new InvalidOperationException("cache_state_too_large"); }
            bytes = output.ToArray();
        }
        var plain = ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var document = JsonDocument.Parse(plain); var result = document.RootElement.Clone();
            if (Text(result, "format") != "tansr-example-cache-state-v1" || Text(result, "owner") != owner) throw new InvalidOperationException("cache_state_owner_mismatch");
            if (result.GetProperty("operation").ValueKind == JsonValueKind.Object) _ = Restore(result.GetProperty("operation"));
            Check(); ct.ThrowIfCancellationRequested(); return result;
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }
    private void Save(JsonElement state, bool create, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Check(); var plain = JsonSerializer.SerializeToUtf8Bytes(state); byte[] bytes;
        try { if (plain.Length > 1048000) throw new InvalidOperationException("cache_state_too_large"); bytes = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser); }
        finally { Array.Clear(plain, 0, plain.Length); }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            Check(); ct.ThrowIfCancellationRequested(); if (create) File.Move(temporary, path); else File.Replace(temporary, path, null); saved = state.Clone(); Check();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void Check()
    {
        if (closed) throw new ObjectDisposedException(nameof(NativeCacheContinuityHost));
        using var document = JsonDocument.Parse(owner); var endpoint = new Uri(Text(document.RootElement, "endpoint"), UriKind.Absolute);
        if (Owner(authority, endpoint) != owner) throw new InvalidOperationException("cache_state_owner_changed");
        foreach (var candidate in new[] { path, Path.GetDirectoryName(path)! }) if (File.Exists(candidate) || Directory.Exists(candidate))
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("cache_state_reparse_point");
    }
    private static string Owner(TrustedExampleScope authority, Uri endpoint)
    {
        var scope = authority.ReadScope(); return WireJson.CanonicalString(Json(new { endpoint = endpoint.AbsoluteUri, principal = authority.ReadPrincipal(),
            applicationScopeId = Text(scope, "applicationScopeId"), endUserId = Text(scope, "endUserId") }));
    }
    private static JsonElement Json(object value) { using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value)); return document.RootElement.Clone(); }
    private static string Text(JsonElement value, string field) => value.GetProperty(field).GetString() is { Length: > 0 } text && !text.Any(char.IsControl) ? text : throw new InvalidOperationException("cache_invalid_" + field);
    private static string Absolute(JsonElement value, string field)
    { string path = Text(value, field); if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path) throw new InvalidOperationException("cache_absolute_path_required"); return path; }
#endif
}
