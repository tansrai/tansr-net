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
    public TansrHttpException(int statusCode, string code, string? scope = null, string? reason = null) : base(code)
    { StatusCode = statusCode; Scope = scope; Reason = reason; }
    public int StatusCode { get; }
    public string? Scope { get; }
    public string? Reason { get; }
}

public sealed class TansrProtocolException : TansrException
{
    public TansrProtocolException(string code) : base(code) { }
}
