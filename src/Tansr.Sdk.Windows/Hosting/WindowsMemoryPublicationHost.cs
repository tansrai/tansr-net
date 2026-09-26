using System.Text;
using System.Text.Json;
using Tansr.Sdk.Execution;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Hosting;

/// <summary>
/// 设备记忆发布的专用受信宿主。仅通过原 ExecutionHost、耐久执行账本和 MemoryPublication 权限调用。
/// 不读取模型配置、不借用 Shell 权限、不自动声明服务端能力。存储由调用方持有并在执行停止后关闭。
/// </summary>
public sealed class WindowsMemoryPublicationHost
{
    private readonly IMemoryPublicationStore store;
    private readonly JsonElement identity;

    public WindowsMemoryPublicationHost(SqliteMemoryPublicationStore store, bool enablePreview = false)
        : this((IMemoryPublicationStore)store, enablePreview) { }

    public WindowsMemoryPublicationHost(IMemoryPublicationStore store, bool enablePreview = false)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (!enablePreview || !store.AtomicDurablePublication) throw new ExecutionRejectedException("ENOTSUP");
        this.store = store;
        identity = WireJson.DecodeControl(WireJson.EncodeControl(store.Identity, 32768), 32768);
        if (identity.ValueKind != JsonValueKind.Object || identity.EnumerateObject().Count() != 4 ||
            !identity.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object || scope.EnumerateObject().Count() != 2)
            throw new ArgumentException("A fixed memory publication identity is required.", nameof(store));
        WireJson.ValidateNamed("Scope", Object(writer =>
        {
            writer.WriteString("applicationScopeId", Text(scope, "applicationScopeId"));
            writer.WriteString("endUserId", Text(scope, "endUserId")); writer.WriteString("authorizationRevision", "0");
        }));
        TerminalCandidateContract.Validate("MemoryPublicationRequest", Object(writer =>
        {
            writer.WriteString("contract", "terminal-services-v1"); writer.WriteString("action", "head");
            foreach (string field in new[] { "sourceId", "sourceGeneration", "domainKey" }) writer.WriteString(field, Text(identity, field));
        }));
    }

    public JsonElement Identity => identity.Clone();
    public static string CandidateRevision => TerminalCandidateContract.Revision;

    /// <summary>只登记固定保留工具，不替代应用能力协商、执行器绑定或每次本地授权。</summary>
    public WindowsBusinessTool CreateTool() => new(TerminalCandidateContract.MemoryPublicationToolName,
        TerminalCandidateContract.MemoryPublicationToolDefinitionSha256, InvokeAsync);

    private async Task<JsonElement> InvokeAsync(JsonElement input, WindowsBusinessToolContext context, CancellationToken ct)
    {
        ValidateInvocation(input, context.Operation);
        ct.ThrowIfCancellationRequested();
        await context.GuardAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // 只从已校验的原执行操作构造 owner；请求正文不决定身份、会话、绑定或介质。
        string owner = WireJson.CanonicalString(Object(writer =>
        {
            foreach (string field in new[] { "scope", "sessionId", "binding" })
            { writer.WritePropertyName(field); context.Operation.GetProperty(field).WriteTo(writer); }
        }));
        JsonElement response;
        try
        {
            response = await store.ExecuteAsync(input.Clone(), owner, ct).ConfigureAwait(false);
        }
        catch (MemoryPublicationRejectedException error)
        {
            if (error.Code != "invalid_request" && error.Code != "integrity_mismatch" && error.Code != "request_conflict" &&
                error.Code != "stale_generation" && error.Code != "revision_conflict" && error.Code != "capacity_exceeded")
                throw new IOException("memory_publication_outcome_unconfirmed", error);
            // 此类型只表示存储已确认的事务前拒绝/回滚；不能将普通介质异常也归为业务失败。
            try
            {
                await context.GuardAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (ExecutionRejectedException) { throw; }
            catch { throw new ExecutionRejectedException("ESTALE"); }
            return Object(writer => { writer.WriteString("status", "error"); writer.WriteString("message", error.Code); });
        }
        // 存储返回后已可能提交。授权撤销/取消不能令原 ExecutionHost 误记成确定 failed。
        try
        {
            await context.GuardAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            TerminalCandidateContract.Validate("MemoryPublicationResponse", response);
            ValidateResponse(input, response);
        }
        catch (Exception error) { throw new IOException("memory_publication_outcome_unconfirmed", error); }
        return Object(writer =>
        {
            writer.WriteString("status", "ok"); writer.WritePropertyName("content"); writer.WriteStartArray();
            writer.WriteStartObject(); writer.WriteString("t", "text"); writer.WriteString("text", response.GetRawText());
            writer.WriteEndObject(); writer.WriteEndArray();
        });
    }

    private static void ValidateResponse(JsonElement request, JsonElement response)
    {
        foreach (string field in new[] { "contract", "sourceId", "sourceGeneration", "domainKey", "action" })
            if (Text(request, field) != Text(response, field)) throw new IOException("memory_publication_response_mismatch");
        if (request.TryGetProperty("transferId", out var transfer) &&
            transfer.GetString() != Text(response.GetProperty("transfer"), "transferId"))
            throw new IOException("memory_publication_response_mismatch");
        if (Text(request, "action") == "read")
        {
            byte[] body = Convert.FromBase64String(Text(response, "base64"));
            int offset = request.GetProperty("offset").GetInt32();
            if (Text(request, "etag") != Text(response, "etag") || offset != response.GetProperty("offset").GetInt32() ||
                body.Length != response.GetProperty("byteLength").GetInt32() || body.Length > request.GetProperty("length").GetInt32() ||
                offset + body.Length != response.GetProperty("nextOffset").GetInt32() ||
                Convert.ToBase64String(body) != Text(response, "base64") || WireJson.Sha256(body) != Text(response, "payloadDigest"))
                throw new IOException("memory_publication_response_mismatch");
        }
    }

    private void ValidateInvocation(JsonElement input, JsonElement operation)
    {
        // 直接宿主调用也保留原摘要和精确 profile 验证，不只依赖外层调度已做过检查。
        ExecutionJson.Operation(operation);
        var request = operation.GetProperty("request");
        var scope = operation.GetProperty("scope"); var expectedScope = identity.GetProperty("scope");
        if (Text(operation, "toolName") != "MemoryPublication" || Text(request, "operation") != "tool.invoke" ||
            Text(scope, "applicationScopeId") != Text(expectedScope, "applicationScopeId") || Text(scope, "endUserId") != Text(expectedScope, "endUserId"))
            throw new ExecutionRejectedException("EACCES");
        var args = request.GetProperty("args");
        if (Text(args, "name") != TerminalCandidateContract.MemoryPublicationToolName ||
            Text(args, "definitionDigest") != TerminalCandidateContract.MemoryPublicationToolDefinitionSha256 ||
            WireJson.CanonicalString(WireJson.Parse(Encoding.UTF8.GetBytes(Text(args, "argsJson")), 32768)) != WireJson.CanonicalString(input))
            throw new ExecutionRejectedException("EACCES");
        TerminalCandidateContract.Validate("MemoryPublicationRequest", input);
        if (Text(input, "sourceId") != Text(identity, "sourceId") || Text(input, "sourceGeneration") != Text(identity, "sourceGeneration") ||
            Text(input, "domainKey") != Text(identity, "domainKey")) throw new ExecutionRejectedException("EACCES");
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static JsonElement Object(Action<Utf8JsonWriter> write)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return WireJson.Parse(memory.ToArray(), 32768);
    }
}
