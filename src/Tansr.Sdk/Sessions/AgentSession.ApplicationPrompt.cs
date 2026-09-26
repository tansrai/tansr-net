using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Sessions;

public sealed partial class AgentSession
{
    /// <summary>显式读取当前应用级提示词的 policy/source。只发起原会话元信息 GET，
    /// 不创建会话、发送消息、刷新模型状态或写入提示词。原默认元信息请求保持不变。</summary>
    /// <remarks>元信息成功但旧 Serve 未返回该字段、会话非活动或加法字段非法时返回 Unknown；不等于确认没有提示词。
    /// HTTP 拒绝、网络故障、会话归属不匹配和取消仍沿用原异常，不降格为 Unknown。
    /// Sdk 来源是可信开发者/Serve 宿主配置，不是终端用户输入。</remarks>
    public async Task<ApplicationPromptState> ReadApplicationPromptAsync(CancellationToken cancellationToken = default)
    {
        var value = await client.SendSessionAsync(HttpMethod.Get, Path + "?include=applicationPrompt", null, cancellationToken).ConfigureAwait(false);
        client.VerifyFamily(value);
        if (SessionJson.String(value, "sessionId") != Id) throw new TansrProtocolException("invalid_response");
        return ApplicationPromptState.FromMetadata(value);
    }
}
