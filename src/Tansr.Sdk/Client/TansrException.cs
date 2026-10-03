using System;

namespace Tansr.Sdk.Client;

/// <summary>错误只暴露协议码，不包含票据、请求正文、服务端自由文本或完整 URL。
/// <para>D19（开发方案 §0，2026-10-01 拍板）：<c>/api</c> 的公开异常面以统一码为主——<see cref="Code"/> 为 19 统一码
/// （<c>Tansr.Sdk.Api.UnifiedErrorCode</c>），配 <c>RetryAction</c>（6 值）；原族码只作 <c>UnifiedApiException.Detail.DomainCode</c>
/// 等保留位。业务代码先 <c>switch (Code)</c>，需要族级区分时再读 <c>Detail</c>。</para></summary>
public class TansrException : Exception
{
    public TansrException(string code) : base("Tansr request failed: " + code) { Code = code; }
    /// <summary>统一码（<c>/api</c> 统一信封）或本地协议码；仅族直通残余形（今日仅 archive-sync-v1）为原族码。</summary>
    public string Code { get; }
}

/// <summary>HTTP 错误事实。统一信封（<c>/api</c>，UAPI-01 / D19）由派生类 <c>Tansr.Sdk.Api.UnifiedApiException</c> 承载：
/// 其 <see cref="Code"/>/<see cref="StatusCode"/>/<see cref="RetryAction"/> 为统一值，原族事实在其 <c>Detail</c>
/// （<c>Detail.DomainCode / DomainStatus / DomainRetryAction</c>，无族事实时为 null，与 Node <c>ApiError.domainCode</c> 同形）。
/// 旧读法 <see cref="DomainCode"/>/<see cref="DomainStatus"/>/<see cref="DomainRetryAction"/> 保留为只读桥接并已标弃用：
/// 无族事实时回落统一值，不再是主判据，1.0 前移除。直接实例仅出现在门面未包装的族直通信封（archive-sync-v1）与各族 wire 严格
/// <c>ErrorResponse</c> 残余路径，此时族事实即 <see cref="Code"/>/<see cref="StatusCode"/>/<see cref="RetryAction"/>。</summary>
public class TansrHttpException : TansrException
{
    internal const string DomainBridgeObsolete = "D19: branch on Code / StatusCode / RetryAction (the unified face). The family fact is UnifiedApiException.Detail.DomainCode / DomainStatus / DomainRetryAction (null when absent); on a family passthrough it is Code / StatusCode / RetryAction itself. This bridge falls back to the unified value and will be removed before 1.0.";
    public TansrHttpException(int statusCode, string code, string? scope = null, string? reason = null,
        string? retryAction = null, int? retryAfterMs = null, string? inputOutcome = null) : base(code)
    { StatusCode = statusCode; Scope = scope; Reason = reason; RetryAction = retryAction; RetryAfterMs = retryAfterMs; InputOutcome = inputOutcome; }
    public int StatusCode { get; }
    public string? Scope { get; }
    /// <summary>统一信封 <c>detail.reason</c>（<c>Tansr.Sdk.Api.UnifiedErrorReason</c> 17 值）；族直通形为原 <c>error.detail.reason</c>。</summary>
    public string? Reason { get; }
    /// <summary>已验证的 SDK2 恢复建议；仅提供事实，客户端不自动重做副作用。</summary>
    public string? RetryAction { get; }
    public int? RetryAfterMs { get; }
    /// <summary>仅原 inputs 端点的 closed/rejected 事实；不会把其它端点的顶层 code 当作合法错误。</summary>
    public string? InputOutcome { get; }
    /// <summary>桥接（已弃用）：原族错误码；统一信封取 <c>Detail.DomainCode</c>，缺席回落 <see cref="Code"/>。改读 <c>UnifiedApiException.Detail.DomainCode</c>。</summary>
    [Obsolete(DomainBridgeObsolete)]
    public string DomainCode => FamilyCode;
    /// <summary>桥接（已弃用）：原族 wire 状态；统一信封取 <c>Detail.DomainStatus</c>，缺席回落 <see cref="StatusCode"/>。改读 <c>UnifiedApiException.Detail.DomainStatus</c>。</summary>
    [Obsolete(DomainBridgeObsolete)]
    public int DomainStatus => FamilyStatus;
    /// <summary>桥接（已弃用）：原族 retryAction；统一信封在 <c>domainRetryAction</c> 在场时取其值（可为 null），否则回落 <see cref="RetryAction"/>。改读 <c>UnifiedApiException.Detail.DomainRetryAction</c>。</summary>
    [Obsolete(DomainBridgeObsolete)]
    public string? DomainRetryAction => FamilyRetryAction;

    // SDK 内部的族客户端（会话 / 执行 / 档案）按各族状态机分支，需要族事实；统一信封从 Detail 取、族直通形即自身。
    internal virtual string FamilyCode => Code;
    internal virtual int FamilyStatus => StatusCode;
    internal virtual string? FamilyRetryAction => RetryAction;
}

public sealed class TansrProtocolException : TansrException
{
    public TansrProtocolException(string code) : base(code) { }
}
