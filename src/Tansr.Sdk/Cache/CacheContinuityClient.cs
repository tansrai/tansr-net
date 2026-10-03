using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Cache;

/// <summary>
/// 显式 opt-in 的逻辑缓存连续性候选门面。复用独立 sdk2-cache-v1；不会升级会话协议、
/// 自动打开缓存、生成供应商 cache key 或声明命中/费用收益。合同仍为未发行候选。
/// </summary>
public sealed class CacheContinuityClient
{
    private readonly CacheClient client;
    public CacheContinuityClient(TansrClient client, bool enablePreview = false)
        => this.client = new CacheClient(client, enablePreview);

    public Task<CacheContinuityCapabilities> DiscoverAsync(CancellationToken cancellationToken = default)
        => Translate(async () => new CacheContinuityCapabilities(await client.DiscoverAsync(cancellationToken).ConfigureAwait(false)));

    public Task<CacheContinuityOperation> PrepareOpenAsync(string sessionId, CacheContinuityOpenKind kind,
        CacheContinuityTicket? ticket = null, string? requestId = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(typeof(CacheContinuityOpenKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return Translate(async () => new CacheContinuityOperation(await client.PrepareOpenAsync(sessionId,
            (CacheOpenKind)kind, ticket?.Value, requestId, cancellationToken).ConfigureAwait(false)));
    }

    public Task<CacheContinuityOperation> PrepareRenewAsync(CacheContinuityBinding binding, CacheContinuityTicket ticket,
        string? requestId = null, CancellationToken cancellationToken = default)
        => Prepare(() => client.PrepareRenewAsync(Binding(binding), Ticket(ticket), requestId, cancellationToken));
    public Task<CacheContinuityOperation> PrepareRotateAsync(CacheContinuityBinding binding, CacheContinuityTicket ticket,
        string? requestId = null, CancellationToken cancellationToken = default)
        => Prepare(() => client.PrepareRotateAsync(Binding(binding), Ticket(ticket), requestId, cancellationToken));
    public Task<CacheContinuityOperation> PrepareCloseAsync(CacheContinuityBinding binding, CacheContinuityTicket ticket,
        string? requestId = null, CancellationToken cancellationToken = default)
        => Prepare(() => client.PrepareCloseAsync(Binding(binding), Ticket(ticket), requestId, cancellationToken));
    public Task<CacheContinuityOperation> PrepareRebindAsync(CacheContinuityBinding binding, string sessionId,
        string? requestId = null, CancellationToken cancellationToken = default)
        => Prepare(() => client.PrepareRebindAsync(Binding(binding), sessionId, requestId, cancellationToken));

    /// <summary>提交前由宿主保存原请求及主体。失回后只查原操作，不自动重试提交。</summary>
    public Task<CacheContinuityReceipt> SubmitAsync(CacheContinuityOperation operation, CancellationToken cancellationToken = default)
        => Receipt(() => client.SubmitAsync(Operation(operation), cancellationToken));
    /// <summary>原 GET 可继续协调已受理事务；不会换键、创建新缓存组或重新运行历史工具。</summary>
    public Task<CacheContinuityReceipt> QueryAsync(CacheContinuityOperation operation, CancellationToken cancellationToken = default)
        => Receipt(() => client.GetOperationStatusAsync(Operation(operation), cancellationToken));
    /// <summary>仅在宿主明确决定时重放完全相同的原请求；旧 epoch 及原 requestId 保持不变。</summary>
    public Task<CacheContinuityReceipt> ReplayOriginalAsync(CacheContinuityOperation operation, CancellationToken cancellationToken = default)
        => Receipt(() => client.ReplayOriginalAsync(Operation(operation), cancellationToken));
    /// <summary>恢复可信加密存储的原字节；新主体、改写原文或不一致回执会拒绝，不生成替代请求。</summary>
    public CacheContinuityOperation RestoreOperation(string action, byte[] originalRequest, string originalPrincipal,
        byte[]? originalReceipt = null)
        => new(client.RestoreOperation(action, originalRequest, originalPrincipal, originalReceipt));
    public Task<CacheContinuityBinding> ReadBindingAsync(string bindingId, CancellationToken cancellationToken = default)
        => Translate(async () => new CacheContinuityBinding(await client.ReadBindingAsync(bindingId, cancellationToken).ConfigureAwait(false)));
    /// <summary>原诊断面不提供供应商命中或费用事实，空页保持未知，不转为零成本/命中成功。</summary>
    public Task<JsonElement> ReadDiagnosticsAsync(string bindingId, string? after = null, int limit = 100, CancellationToken cancellationToken = default)
        => Translate(() => client.ReadDiagnosticsAsync(bindingId, after, limit, cancellationToken));

    private static CacheBinding Binding(CacheContinuityBinding value) => value?.Value ?? throw new ArgumentNullException(nameof(value));
    private static CacheTicket Ticket(CacheContinuityTicket value) => value?.Value ?? throw new ArgumentNullException(nameof(value));
    private static CacheOperation Operation(CacheContinuityOperation value) => value?.Value ?? throw new ArgumentNullException(nameof(value));
    private static Task<CacheContinuityOperation> Prepare(Func<Task<CacheOperation>> operation)
        => Translate(async () => new CacheContinuityOperation(await operation().ConfigureAwait(false)));
    private static Task<CacheContinuityReceipt> Receipt(Func<Task<CacheReceipt>> operation)
        => Translate(async () => new CacheContinuityReceipt(await operation().ConfigureAwait(false)));
    private static async Task<T> Translate<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (CacheHttpException error) { throw new CacheContinuityException(error.StatusCode, error.Code, error.RetryAction, error.Fallback); }
        // UAPI-01 / D19 统一信封:公开面取统一码 / 状态 / retryAction;原族码 / 状态 / 动作与 fallback 自 detail 保留为 Domain* 位。
        catch (UnifiedApiException error) { throw new CacheContinuityException(error, error.Fallback ?? "none"); }
    }
}

public enum CacheContinuityOpenKind { New, Import, Resume, Fork }

/// <summary>Opaque 服务端票据，只能从可信存储恢复；它不证明当前授权仍然有效。</summary>
public sealed class CacheContinuityTicket
{
    internal CacheContinuityTicket(CacheTicket value) { Value = value; }
    internal CacheTicket Value { get; }
    public static CacheContinuityTicket Restore(string originalValue) => new(new CacheTicket(originalValue));
    /// <summary>仅供加密/受保护持久化；不要写入日志、URL、诊断或UI。</summary>
    public string ExportProtectedValue() => Value.Value;
    public override string ToString() => "CacheContinuityTicket(redacted)";
}

public sealed class CacheContinuityCapabilities
{
    private readonly CacheCapabilities value;
    internal CacheContinuityCapabilities(CacheCapabilities value) { this.value = value; }
    public bool Available => value.Available;
    public string? OperationEpoch => value.OperationEpoch;
    public int MaximumControlBytes => value.ControlBytes;
}

public sealed class CacheContinuityBinding
{
    internal CacheContinuityBinding(CacheBinding value) { Value = value; }
    internal CacheBinding Value { get; }
    public string Id => Value.Id;
    public string LogicalReference => Value.LogicalReference;
    public string Revision => Value.Revision;
    public string State => Value.State;
    public string GroupGeneration => Value.GroupGeneration;
}

/// <summary>原操作不可变受理事实；Binding.State 不代表当前授权，应按需读取当前绑定。</summary>
public sealed class CacheContinuityReceipt
{
    internal CacheContinuityReceipt(CacheReceipt value)
    {
        Binding = new CacheContinuityBinding(value.Binding);
        Ticket = value.Ticket == null ? null : new CacheContinuityTicket(value.Ticket);
        TicketExpiresAt = value.TicketExpiresAt;
    }
    public CacheContinuityBinding Binding { get; }
    public CacheContinuityTicket? Ticket { get; }
    public string? TicketExpiresAt { get; }
    public override string ToString() => "CacheContinuityReceipt(redacted)";
}

/// <summary>固定原请求与不可变回执。恢复所需字节只存入受保护宿主存储，不作为普通诊断。</summary>
public sealed class CacheContinuityOperation
{
    internal CacheContinuityOperation(CacheOperation value) { Value = value; }
    internal CacheOperation Value { get; }
    public string Action => Value.Operation;
    public string OperationEpoch => Value.OperationEpoch;
    public string RequestId => Value.RequestId;
    public string? BindingId => Value.BindingId;
    /// <summary>可信 app/user 的规范组合；与原字节一起保存，不能用当前用户替换。</summary>
    public string OriginalPrincipal => Value.Owner;
    public byte[] ExportOriginalRequest() => Value.ExportOriginalRequest();
    public byte[]? ExportOriginalReceipt() => Value.ExportOriginalReceipt();
    public override string ToString() => "CacheContinuityOperation(redacted)";
}

/// <summary>缓存合同错误与精确恢复建议，未包含服务端自由文本或秘密。D19：经 <c>/api</c> 统一信封到达时
/// <see cref="TansrException.Code"/>/<see cref="StatusCode"/>/<see cref="RetryAction"/> 为统一值，原族事实在
/// <see cref="DomainCode"/>/<see cref="DomainStatus"/>/<see cref="DomainRetryAction"/>（族直通信封二者相同）；
/// 完整统一信封见 <see cref="Unified"/>。</summary>
public sealed class CacheContinuityException : TansrException
{
    internal CacheContinuityException(int statusCode, string code, string retryAction, string fallback) : base(code)
    { StatusCode = statusCode; RetryAction = retryAction; Fallback = fallback; DomainCode = code; DomainStatus = statusCode; DomainRetryAction = retryAction; }
    internal CacheContinuityException(UnifiedApiException unified, string fallback) : base(unified.Code)
    {
        StatusCode = unified.StatusCode; RetryAction = unified.RetryAction; Fallback = fallback; Unified = unified;
        DomainCode = unified.Detail.DomainCode ?? unified.Code; DomainStatus = unified.Detail.DomainStatus ?? unified.StatusCode;
        DomainRetryAction = (unified.Detail.HasDomainRetryAction ? unified.Detail.DomainRetryAction : null) ?? unified.RetryAction;
    }
    public int StatusCode { get; }
    public string RetryAction { get; }
    public string Fallback { get; }
    /// <summary>原族码（统一信封 <c>detail.domainCode</c>；缺席或族直通时等于 <see cref="TansrException.Code"/>）。</summary>
    public string DomainCode { get; }
    public int DomainStatus { get; }
    public string DomainRetryAction { get; }
    /// <summary>到达时的统一信封；族直通残余形为 null。</summary>
    public UnifiedApiException? Unified { get; }
}
