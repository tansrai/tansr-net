using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

/// <summary>共同托管权威的只读发现与原键查询。不会认领 writer，也不能发布迁移或释放认领。</summary>
public sealed class PersistenceCustodyReadClient
{
    private readonly TansrClient client;
    public PersistenceCustodyReadClient(TansrClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendControlAsync(HttpMethod.Get, ApiRoutes.TerminalCustodyCapabilities.Path() +
            ApiRoutes.TerminalCustodyCapabilities.Query(("contract", PersistenceCustodyContract.Protocol)), null,
            cancellationToken, 32768, 200).ConfigureAwait(false);
        return PersistenceCustodyContract.Copy("CapabilitiesResponse", response);
    }

    /// <summary>prepare 已受理但尚无完整证明时，preparing=true／ticket=null 必须继续视为未决。</summary>
    public async Task<JsonElement> QueryAsync(JsonElement originalQuery, CancellationToken cancellationToken = default)
    {
        var request = PersistenceCustodyContract.Copy("QueryRequest", originalQuery);
        var identity = request.GetProperty("identity"); var scope = client.ReadExecutionScope();
        if (PersistenceCustodyContract.Text(identity, "applicationScopeId") != PersistenceCustodyContract.Text(scope, "applicationScopeId") ||
            PersistenceCustodyContract.Text(identity, "endUserId") != PersistenceCustodyContract.Text(scope, "endUserId"))
            throw new WireProtocolException("custody_binding_mismatch");
        var query = new List<(string Name, string Value)> { ("contract", PersistenceCustodyContract.Protocol) };
        foreach (string name in new[] { "applicationScopeId", "endUserId", "sourceId", "sourceGeneration", "domainKey" })
            query.Add((name, PersistenceCustodyContract.Text(identity, name)));
        query.Add(("intentSha256", PersistenceCustodyContract.Text(request, "intentSha256")));
        var response = await client.SendControlAsync(HttpMethod.Get,
            ApiRoutes.TerminalCustodyQuery.Path(id: PersistenceCustodyContract.Text(request, "operationId")) + ApiRoutes.TerminalCustodyQuery.Query(query.ToArray()),
            null, cancellationToken, 32768, 200).ConfigureAwait(false);
        if (!PersistenceCustodyContract.Equal(scope, client.ReadExecutionScope())) throw new WireProtocolException("context_changed");
        response = PersistenceCustodyContract.Copy("QueryResponse", response);
        PersistenceCustodyContract.ValidateResponse(request, response); return response;
    }
}
