using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Cache;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    // 独立私有候选入口，不拓宽稳定的 sdk2-ext-v1 SendControlAsync 合同。
    internal string CacheOwner()
    {
        var scope = ReadExecutionScope();
        return WireJson.CanonicalString(CacheJson.Write(writer =>
        {
            writer.WriteStartArray();
            writer.WriteStringValue(scope.GetProperty("applicationScopeId").GetString());
            writer.WriteStringValue(scope.GetProperty("endUserId").GetString());
            writer.WriteEndArray();
        }));
    }

    internal async Task<JsonElement> SendCacheAsync(HttpMethod method, string path, byte[]? body,
        string owner, CancellationToken cancellationToken)
    {
        if (!path.StartsWith("/v3/sdk2/cache/", StringComparison.Ordinal) || body is not null && body.Length > CacheJson.MaximumBytes)
            throw new TansrProtocolException("invalid_request");
        var currentScope = WireJson.CanonicalString(ReadExecutionScope());
        if (CacheOwner() != owner) throw new TansrProtocolException("context_changed");
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        void Check()
        {
            transport.AssertCurrent(access);
            if (currentScope != WireJson.CanonicalString(ReadExecutionScope()) || CacheOwner() != owner)
                throw new TansrProtocolException("context_changed");
        }
        Check();
        // cache-v1 自有能力发现；不把 SDK1 会话隐式升级到 sdk2-offload-v1。
        using var response = await transport.SendAsync(method, path, access, body, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        SessionTransport.ExpectContent(response, "application/json");
        var value = CacheJson.Read(await SessionTransport.ReadBodyAsync(response, CacheJson.MaximumBytes, cancellation.Token).ConfigureAwait(false));
        Check();
        if (!response.IsSuccessStatusCode)
        {
            CacheJson.Error(value, (int)response.StatusCode);
            throw new CacheHttpException((int)response.StatusCode, CacheJson.String(value, "code"),
                CacheJson.String(value, "retryAction"), CacheJson.String(value, "fallback"));
        }
        if ((int)response.StatusCode != 200) throw new TansrProtocolException("invalid_response");
        return value;
    }
}
