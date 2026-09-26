using System;

namespace Tansr.Sdk.Protocol;

/// <summary>本地协议失败；不携带正文、票据或原始解析异常。</summary>
public sealed class WireProtocolException : Exception
{
    public WireProtocolException(string code) : base("Tansr wire: " + code)
    {
        Code = code;
    }

    public string Code { get; }
}
