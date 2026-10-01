using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    internal async Task<JsonElement> ReadTerminalProfileAsync(string path, string definition, JsonElement originalScope, CancellationToken cancellationToken)
    {
        if (!path.StartsWith(ApiRoutes.TerminalProfileCatalog.Root, StringComparison.Ordinal) || path.IndexOf('#') >= 0 ||
            path.IndexOf('\\') >= 0 || System.Text.Encoding.UTF8.GetByteCount(path) > 8192 || definition != "CatalogResponse" && definition != "UsageResponse")
            throw new TansrProtocolException("invalid_request");
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        void Check()
        {
            cancellation.Token.ThrowIfCancellationRequested(); transport.AssertCurrent(access);
            if (!TerminalJson.Equal(originalScope, ReadTerminalScope())) throw new TansrProtocolException("context_changed");
        }
        Check();
        using var response = await transport.SendAsync(HttpMethod.Get, path, access, null, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        Check(); SessionTransport.ExpectContent(response, "application/json");
        var bytes = await SessionTransport.ReadBodyAsync(response, WireJson.MaximumControlBytes, cancellation.Token).ConfigureAwait(false); Check();
        if (!response.IsSuccessStatusCode)
        {
            SessionTransport.ThrowUnified(response, bytes);
            var error = TerminalProfileContract.Decode("ErrorResponse", bytes);
            if (error.GetProperty("status").GetInt32() != (int)response.StatusCode) throw new TansrProtocolException("invalid_response");
            throw new TansrHttpException((int)response.StatusCode, error.GetProperty("code").GetString()!, retryAction: error.GetProperty("retryAction").GetString());
        }
        if ((int)response.StatusCode != 200) throw new TansrProtocolException("invalid_response");
        return TerminalProfileContract.Decode(definition, bytes);
    }
}
