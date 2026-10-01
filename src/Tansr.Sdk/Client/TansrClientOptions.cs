using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tansr.Sdk.Client;

/// <summary>会话 HTTP 家族；旧合同保持缺省，新合同必须通过发现。</summary>
public enum SessionContract { Sdk1, Sdk2OffloadV1 }

/// <summary>客户端不保存长期密钥；每次请求均从宿主取得短期票。</summary>
public sealed class TansrClientOptions
{
    public Uri BaseUri { get; set; } = null!;
    public Func<CancellationToken, Task<string>> TokenProvider { get; set; } = null!;
    /// <summary>开发者显式配置的附加 x- 认证头；构造客户端时复制，不替代 Bearer，不从模型或聊天输入读取。</summary>
    public IReadOnlyDictionary<string, string>? AdditionalRequestHeaders { get; set; }
    /// <summary>可信宿主提供稳定应用/用户身份；不以票据文本或未经核验的 JWT 推断身份。</summary>
    public Func<string>? PrincipalProvider { get; set; }
    /// <summary>执行扩展使用可信宿主完整 scope，不从终端自报或票据文本推导。</summary>
    public Func<JsonElement>? ExecutionScopeProvider { get; set; }
    public bool AllowInsecureLoopback { get; set; }
    public SessionContract SessionContract { get; set; }
    /// <summary>UAPI-01:在 SSE 请求上携带 <c>tansr-event-envelope: unified-v1</c> 协商统一事件包络;服务端须以同名响应头回响,
    /// 否则抛 <c>EnvelopeNotNegotiatedException</c>。缺省关闭(各族原帧字节不变)。</summary>
    public bool NegotiateEventEnvelope { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxEventBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxReconnectAttempts { get; set; } = 3;
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
