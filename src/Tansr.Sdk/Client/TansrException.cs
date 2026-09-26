using System;

namespace Tansr.Sdk.Client;

/// <summary>错误只暴露协议码，不包含票据、请求正文、服务端自由文本或完整 URL。</summary>
public class TansrException : Exception
{
    public TansrException(string code) : base("Tansr request failed: " + code) { Code = code; }
    public string Code { get; }
}

public sealed class TansrHttpException : TansrException
{
    public TansrHttpException(int statusCode, string code, string? scope = null, string? reason = null,
        string? retryAction = null, int? retryAfterMs = null, string? inputOutcome = null) : base(code)
    { StatusCode = statusCode; Scope = scope; Reason = reason; RetryAction = retryAction; RetryAfterMs = retryAfterMs; InputOutcome = inputOutcome; }
    public int StatusCode { get; }
    public string? Scope { get; }
    public string? Reason { get; }
    /// <summary>已验证的 SDK2 恢复建议；仅提供事实，客户端不自动重做副作用。</summary>
    public string? RetryAction { get; }
    public int? RetryAfterMs { get; }
    /// <summary>仅原 inputs 端点的 closed/rejected 事实；不会把其它端点的顶层 code 当作合法错误。</summary>
    public string? InputOutcome { get; }
}

public sealed class TansrProtocolException : TansrException
{
    public TansrProtocolException(string code) : base(code) { }
}
