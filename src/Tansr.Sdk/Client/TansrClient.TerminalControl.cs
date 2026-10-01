using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    internal string TerminalSessionContract => contract == SessionContract.Sdk1 ? "sdk1" : "sdk2-offload-v1";

    // The terminal candidate has its own response/error contract. It must not be decoded as
    // sdk2-ext-v1 or create a second authentication/lifetime owner for the same session.
    internal async Task<JsonElement> SendTerminalControlAsync(HttpMethod method, string path, JsonElement? request,
        string responseName, JsonElement originalScope, CancellationToken cancellationToken)
    {
        if ((method != HttpMethod.Get && method != HttpMethod.Post) || !path.StartsWith(ApiRoutes.TerminalConfigurationRead.Root, StringComparison.Ordinal) ||
            path.IndexOf('#') >= 0 || path.IndexOf('\\') >= 0 || Encoding.UTF8.GetByteCount(path) > 8192 ||
            responseName != "ConfigurationResponse" && responseName != "ConfigurationCommitResponse" &&
            responseName != "MemoryStateResponse" && responseName != "MemoryReceiptResponse")
            throw new TansrProtocolException("invalid_request");
        var scope = ReadExecutionScope();
        TerminalControlClient.CheckOwner(originalScope, scope);
        var bytes = request.HasValue ? WireJson.EncodeControl(request.Value) : null;
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        void Check()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            transport.AssertCurrent(access);
            if (!TerminalJson.Equal(scope, ReadExecutionScope())) throw new TansrProtocolException("context_changed");
        }
        Check();
        // Discover using this exact authenticated access, not a previously successful token or
        // another transport. Global discovery proves installation only; Serve still checks owner
        // and live authority for the actual session operation.
        using var capabilitiesResponse = await transport.SendAsync(HttpMethod.Get,
            ApiRoutes.TerminalCapabilities.Path() + ApiRoutes.TerminalCapabilities.Query(("contract", TerminalCandidateContract.Protocol)), access, null, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        Check();
        var capabilities = await ReadTerminalResponseAsync(capabilitiesResponse, "CapabilitiesResponse", WireJson.MaximumControlBytes, Check, cancellation.Token).ConfigureAwait(false);
        Check();
        if (TerminalJson.Text(capabilities, "schemaRevision") != TerminalCandidateContract.Revision ||
            TerminalJson.Text(capabilities, "schemaSha256") != TerminalCandidateContract.SchemaSha256)
            throw new TansrProtocolException("contract_mismatch");
        var familySupported = false;
        foreach (var family in capabilities.GetProperty("sessionContracts").EnumerateArray())
            if (family.GetString() == TerminalSessionContract) familySupported = true;
        var expectedFeature = responseName.StartsWith("Memory", StringComparison.Ordinal) ? "memory-lifecycle-v1" : "session-configuration-v1";
        var installed = false;
        foreach (var feature in capabilities.GetProperty("features").EnumerateArray())
            if (TerminalJson.Text(feature, "feature") == expectedFeature)
                installed = feature.GetProperty("supported").GetBoolean() && feature.GetProperty("installed").GetBoolean();
        if (!familySupported || !installed) throw new TansrProtocolException("unsupported_capability");
        var maximum = Math.Min(WireJson.MaximumControlBytes, capabilities.GetProperty("limits").GetProperty("maxControlBytes").GetInt32());
        if (bytes is not null && bytes.Length > maximum) throw new TansrProtocolException("payload_too_large");
        using var response = await transport.SendAsync(method, path, access, bytes, "application/json",
            bytes is null ? null : "application/json", null, cancellation.Token).ConfigureAwait(false);
        Check();
        var value = await ReadTerminalResponseAsync(response, responseName, maximum, Check, cancellation.Token).ConfigureAwait(false);
        Check();
        if (responseName == "MemoryStateResponse")
            TerminalControlClient.CheckOwner(value.GetProperty("memory").GetProperty("identity"), scope, "integrity_mismatch");
        return value;
    }

    private static async Task<JsonElement> ReadTerminalResponseAsync(HttpResponseMessage response, string responseName, int maximum, Action check, CancellationToken cancellationToken)
    {
        SessionTransport.ExpectContent(response, "application/json");
        var body = await SessionTransport.ReadBodyAsync(response, response.IsSuccessStatusCode ? maximum : WireJson.MaximumControlBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        check();
        if (!response.IsSuccessStatusCode)
        {
            SessionTransport.ThrowUnified(response, body);
            var error = TerminalCandidateContract.Decode("ErrorResponse", body);
            if (error.GetProperty("status").GetInt32() != (int)response.StatusCode) throw new TansrProtocolException("invalid_response");
            throw new TansrHttpException((int)response.StatusCode, TerminalJson.Text(error, "code"), retryAction: TerminalJson.Text(error, "retryAction"));
        }
        if ((int)response.StatusCode != 200) throw new TansrProtocolException("invalid_response");
        return TerminalCandidateContract.Decode(responseName, body);
    }
}
