using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    // 恢复包络拥有独立上限；旧 SendControlAsync 的请求及错误控制帽保持不变。
    internal async Task<JsonElement> SendArchiveRecoveryAsync(JsonElement request, int requestMaximum, int responseMaximum, CancellationToken cancellationToken)
    {
        if (requestMaximum < 1 || requestMaximum > ArchiveRecoveryContract.MaximumRequestBytes || responseMaximum < 1 || responseMaximum > ArchiveRecoveryContract.MaximumResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(requestMaximum));
        byte[] body = WireJson.EncodeControl(request, requestMaximum); string bindingId = request.GetProperty("bindingId").GetString()!;
        WireJson.ValidateNamed("Id", request.GetProperty("bindingId")); var expectedScope = WireJson.CanonicalString(ReadExecutionScope());
        using var cancellation = RequestCancellation(cancellationToken); var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        void Check() { cancellation.Token.ThrowIfCancellationRequested(); transport.AssertCurrent(access); if (expectedScope != WireJson.CanonicalString(ReadExecutionScope())) throw new TansrProtocolException("context_changed"); }
        Check(); await EnsureContractAsync(access, cancellation.Token).ConfigureAwait(false); Check();
        using var response = await transport.SendAsync(HttpMethod.Post, "/v3/sdk2/bindings/" + Uri.EscapeDataString(bindingId) + "/archive/ack-rebases", access, body, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        Check();
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, WireJson.MaximumControlBytes, cancellation.Token, true).ConfigureAwait(false);
        if ((int)response.StatusCode != 200) throw new TansrProtocolException("invalid_response");
        SessionTransport.ExpectContent(response, "application/json");
        var value = WireJson.DecodeControl(await SessionTransport.ReadBodyAsync(response, responseMaximum, cancellation.Token).ConfigureAwait(false), responseMaximum);
        Check(); return value;
    }
}
