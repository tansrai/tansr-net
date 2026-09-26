using System.Collections.ObjectModel;
using System.Text.Json;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Terminal;

public sealed class TerminalProfileModel
{
    internal TerminalProfileModel(JsonElement value)
    {
        Handle = value.GetProperty("handle").GetString()!; DisplayName = value.GetProperty("displayName").GetString()!;
        Manufacturer = value.GetProperty("manufacturer").GetString(); Family = value.GetProperty("family").GetString();
    }
    public string Handle { get; }
    public string DisplayName { get; }
    public string? Manufacturer { get; }
    public string? Family { get; }
}

/// <summary>Quota is enforced by the gateway. This profile exposes no numerical allowance,
/// remaining balance, developer price or budget.</summary>
public sealed class TerminalProfileQuota
{
    internal TerminalProfileQuota(JsonElement value) { State = value.GetProperty("state").GetString()!; Enforcement = value.GetProperty("enforcement").GetString()!; }
    public string State { get; }
    public string Enforcement { get; }
}

public sealed class TerminalProfileCatalog
{
    internal TerminalProfileCatalog(JsonElement value)
    {
        Raw = value.Clone(); Models = Array.AsReadOnly(value.GetProperty("models").EnumerateArray().Select(item => new TerminalProfileModel(item)).ToArray());
        Aliases = new ReadOnlyDictionary<string, string>(value.GetProperty("aliases").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal));
        Quota = new TerminalProfileQuota(value.GetProperty("quota")); Capabilities = value.GetProperty("capabilities").Clone();
    }
    public JsonElement Raw { get; }
    public IReadOnlyList<TerminalProfileModel> Models { get; }
    public IReadOnlyDictionary<string, string> Aliases { get; }
    public TerminalProfileQuota Quota { get; }
    /// <summary>Original strict application capability projection. The legacy wire key
    /// platform contains system multimedia/search switches; it does not authorize host execution.</summary>
    public JsonElement Capabilities { get; }
}

/// <summary>The platform's authenticated end user's rolling 1d usage, distinct from a session's
/// accumulated usage and from developer billing or available quota.</summary>
public sealed class TerminalProfileUsage
{
    internal TerminalProfileUsage(JsonElement value)
    {
        Raw = value.Clone(); var usage = value.GetProperty("usage"); Window = usage.GetProperty("window").GetString()!; EndUserId = usage.GetProperty("endUserId").GetString()!;
        Requests = usage.GetProperty("requests").GetInt64(); InTokens = usage.GetProperty("inTokens").GetInt64(); OutTokens = usage.GetProperty("outTokens").GetInt64();
        CacheRTokens = usage.GetProperty("cacheRTokens").GetInt64(); CacheWTokens = usage.GetProperty("cacheWTokens").GetInt64(); Quota = new TerminalProfileQuota(value.GetProperty("quota"));
    }
    public JsonElement Raw { get; }
    public string Window { get; }
    public string EndUserId { get; }
    public long Requests { get; }
    public long InTokens { get; }
    public long OutTokens { get; }
    public long CacheRTokens { get; }
    public long CacheWTokens { get; }
    public TerminalProfileQuota Quota { get; }
}

/// <summary>Explicit preview of the independent read-only terminal-profile-v1 contract.
/// Reuses the owner's authenticated transport, local Serve ownership checks and lifetime.</summary>
public sealed class TerminalProfileClient : IDisposable
{
    public const string Protocol = TerminalProfileContract.Protocol;
    public const string SchemaSha256 = TerminalProfileContract.SchemaSha256;
    private readonly TansrClient client;
    private readonly JsonElement owner;
    private readonly CancellationTokenSource stop = new();
    private int disposed;
    public TerminalProfileClient(TansrClient client, bool enablePreview = false)
    {
        if (!enablePreview) throw new TansrProtocolException("unsupported_capability");
        this.client = client ?? throw new ArgumentNullException(nameof(client)); owner = client.ReadTerminalScope();
    }
    private JsonElement Scope()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TerminalProfileClient));
        var scope = client.ReadTerminalScope(); TerminalControlClient.CheckOwner(owner, scope); return scope;
    }
    private async Task<JsonElement> ReadAsync(string sessionId, SessionContract sessionContract, string action, string definition, CancellationToken ct)
    {
        var scope = Scope();
        if (sessionContract != SessionContract.Sdk1 && sessionContract != SessionContract.Sdk2OffloadV1) throw new ArgumentOutOfRangeException(nameof(sessionContract));
        var session = TerminalJson.Object(w => { w.WriteString("sessionContract", sessionContract == SessionContract.Sdk1 ? "sdk1" : "sdk2-offload-v1"); w.WriteString("sessionId", sessionId); });
        TerminalProfileContract.Validate("SessionReference", session);
        if (session.GetProperty("sessionContract").GetString() != client.TerminalSessionContract) throw new TansrProtocolException("binding_conflict");
        if (sessionId == "." || sessionId == "..") throw new TansrProtocolException("invalid_request");
        var path = "/v3/terminal-profile/sessions/" + Uri.EscapeDataString(sessionId) + "/" + action + "?contract=" + Protocol + "&sessionContract=" + session.GetProperty("sessionContract").GetString();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        var result = await client.ReadTerminalProfileAsync(path, definition, scope, cancellation.Token).ConfigureAwait(false);
        if (!TerminalJson.Equal(session, result.GetProperty("session")) || !TerminalJson.Equal(scope, Scope())) throw new TansrProtocolException("integrity_mismatch");
        if (definition == "UsageResponse" && result.GetProperty("usage").GetProperty("endUserId").GetString() != scope.GetProperty("endUserId").GetString())
            throw new TansrProtocolException("integrity_mismatch");
        return result;
    }
    public async Task<TerminalProfileCatalog> ReadCatalogAsync(string sessionId, SessionContract sessionContract = SessionContract.Sdk1, CancellationToken cancellationToken = default)
        => new(await ReadAsync(sessionId, sessionContract, "catalog", "CatalogResponse", cancellationToken).ConfigureAwait(false));
    public async Task<TerminalProfileUsage> ReadUsageAsync(string sessionId, SessionContract sessionContract = SessionContract.Sdk1, CancellationToken cancellationToken = default)
        => new(await ReadAsync(sessionId, sessionContract, "usage", "UsageResponse", cancellationToken).ConfigureAwait(false));
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) stop.Cancel(); }
}
