using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Sessions;

public sealed partial class AgentSession
{
    /// <summary>显式携带原请求身份发送。HTTP/SSE 失回后仅用同一 requestId 查询，不自动重发或生成新键。</summary>
    public Task<JsonElement> SendWithRequestAsync(string prompt, string requestId, CancellationToken cancellationToken = default)
    {
        client.AssertSessionWritable(Id); PersistenceJson.Id(requestId);
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt must not be blank.", nameof(prompt));
        SessionJson.Unicode(prompt);
        return client.SendSessionAsync(HttpMethod.Post, ApiRoutes.SessionMessageSend.Path(id: Id),
            SessionJson.Object(w => { w.WriteString("prompt", prompt); w.WriteString("requestId", requestId); }), cancellationToken, 20 * 1024 * 1024);
    }

    /// <summary>多模态原请求；与文本同样不进行副作用重试。</summary>
    public Task<JsonElement> SendBlocksWithRequestAsync(IReadOnlyList<MessageBlock> blocks, string requestId, CancellationToken cancellationToken = default)
    {
        client.AssertSessionWritable(Id); PersistenceJson.Id(requestId);
        return client.SendSessionAsync(HttpMethod.Post, ApiRoutes.SessionMessageSend.Path(id: Id),
            SessionJson.Object(w => { w.WritePropertyName("blocks"); SessionRequestWriter.Blocks(w, blocks); w.WriteString("requestId", requestId); }), cancellationToken, 20 * 1024 * 1024);
    }

    public Task<JsonElement> GetPersistenceAsync(CancellationToken cancellationToken = default) => client.GetSessionPersistenceAsync(Id, cancellationToken);
    public Task<JsonElement> GetInputAcceptanceAsync(string requestId, CancellationToken cancellationToken = default) => client.GetInputAcceptanceAsync(Id, requestId, cancellationToken);
    public Task<JsonElement> ListInputAcceptancesAsync(string? afterRequestId = null, int limit = 50, CancellationToken cancellationToken = default) => client.ListInputAcceptancesAsync(Id, afterRequestId, limit, cancellationToken);
}
