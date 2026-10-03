using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Api;

/// <summary>Fence state of one closure operation (RFC-UAPI-1 §1.4; 方案 §3.6).</summary>
public static class UnifiedOperationState
{
    /// <summary>Inside the fence: the call may be made.</summary>
    public const string Enabled = "enabled";
    /// <summary>A capability source exists but does not admit the call now → the facade answers 403 <c>capability_unavailable</c>.</summary>
    public const string Disabled = "disabled";
    /// <summary>The domain is not installed or has no capability source for this session → 404 <c>capability_unavailable</c>.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>Capability closure (<c>GET /api/sessions/:id/capabilities</c>; unified-v1 <c>CapabilityClosure</c>). Decoding is strict:
/// exactly the five keys, all eight closure domains, all 77 closure operations (every manifest operation outside <c>discovery</c>
/// except the session-contract probe), and the <c>closureId</c> must equal
/// <c>sha256("tansr.unified.closure.v1" ‖ 0x00 ‖ canonical({authorizationRevision, domains, operations}))</c>. Anything else is
/// <c>invalid_closure</c> — a closure the SDK cannot verify is never consulted. The closure is the <em>declared capability
/// intersection</em>: an operation that is not <see cref="UnifiedOperationState.Enabled"/> is expected to be answered with
/// <c>capability_unavailable</c> by the facade and the SDK never substitutes a legacy path or another operation for it.</summary>
public sealed class UnifiedCapabilityClosure
{
    /// <summary>The eight closure domains in schema order (<c>discovery</c> is facade-owned and has no closure state).</summary>
    public static readonly IReadOnlyList<string> Domains = new[] { "session", "execution", "archive", "archive-sync", "cache", "terminal", "terminal-observation", "terminal-profile" };

    /// <summary>The 77 operations a closure must enumerate, in manifest order: every operation whose domain is not
    /// <c>discovery</c>, minus <see cref="ApiRoutes.SessionCapabilities"/> (the SDK1 session-contract probe under
    /// <c>/api/capabilities/sessions</c>, which is discovery by nature even though the manifest files it under <c>session</c>).</summary>
    public static readonly IReadOnlyList<ApiOperation> Operations = BuildOperations();

    private static IReadOnlyList<ApiOperation> BuildOperations()
    {
        var list = new List<ApiOperation>(ApiRoutes.All.Count);
        foreach (var operation in ApiRoutes.All)
            if (operation.Domain != "discovery" && !ReferenceEquals(operation, ApiRoutes.SessionCapabilities)) list.Add(operation);
        return list.ToArray();
    }

    /// <summary>Per-domain deployment fact: <c>installed</c> and the domain's self-reported generation (null when it has none).</summary>
    public sealed class DomainState
    {
        internal DomainState(bool installed, string? revision) { Installed = installed; Revision = revision; }
        public bool Installed { get; }
        public string? Revision { get; }
    }

    private readonly IReadOnlyDictionary<string, DomainState> domains;
    private readonly IReadOnlyDictionary<string, string> operations;

    private UnifiedCapabilityClosure(string closureId, string? authorizationRevision, IReadOnlyDictionary<string, DomainState> domains, IReadOnlyDictionary<string, string> operations, string rawText)
    { ClosureId = closureId; AuthorizationRevision = authorizationRevision; this.domains = domains; this.operations = operations; RawText = rawText; }

    public string Contract => ApiRoutes.Contract;
    /// <summary>Verified closure digest; equals the <c>tansr-closure-id</c> the server stamps and the value to send back on guarded writes.</summary>
    public string ClosureId { get; }
    /// <summary>Authorization generation (<c>Sequence</c>), or null when the deployment has no policy source.</summary>
    public string? AuthorizationRevision { get; }
    /// <summary>Original response text (for diagnostics; never re-encoded).</summary>
    public string RawText { get; }

    public DomainState Domain(string domain) =>
        domains.TryGetValue(domain ?? throw new ArgumentNullException(nameof(domain)), out var state) ? state : throw new ArgumentOutOfRangeException(nameof(domain));

    /// <summary>Fence state of a closure operation; <see cref="ArgumentOutOfRangeException"/> for names outside the 77.</summary>
    public string State(string operation) =>
        operations.TryGetValue(operation ?? throw new ArgumentNullException(nameof(operation)), out var state) ? state : throw new ArgumentOutOfRangeException(nameof(operation));

    public string State(ApiOperation operation) => State((operation ?? throw new ArgumentNullException(nameof(operation))).Name);

    /// <summary>True only for <see cref="UnifiedOperationState.Enabled"/>; false for disabled / unavailable and for non-closure
    /// operations (discovery and the session-contract probe are never fenced, so callers must not ask).</summary>
    public bool IsEnabled(ApiOperation operation) => State(operation) == UnifiedOperationState.Enabled;

    /// <summary>Operations the closure declares <see cref="UnifiedOperationState.Enabled"/>, in manifest order.</summary>
    public IReadOnlyList<ApiOperation> Enabled()
    {
        var list = new List<ApiOperation>();
        foreach (var operation in Operations) if (operations[operation.Name] == UnifiedOperationState.Enabled) list.Add(operation);
        return list;
    }

    /// <summary>Operations outside the fence (<c>disabled</c> or <c>unavailable</c>), in manifest order.</summary>
    public IReadOnlyList<ApiOperation> Fenced()
    {
        var list = new List<ApiOperation>();
        foreach (var operation in Operations) if (operations[operation.Name] != UnifiedOperationState.Enabled) list.Add(operation);
        return list;
    }

    /// <summary>HTTP status the facade uses for a fenced operation: 403 for <c>disabled</c>, 404 for <c>unavailable</c>, null when enabled.</summary>
    public int? ExpectedStatus(ApiOperation operation) => State(operation) switch
    {
        UnifiedOperationState.Disabled => 403,
        UnifiedOperationState.Unavailable => 404,
        _ => null,
    };

    private static TansrProtocolException Invalid() => new TansrProtocolException("invalid_closure");

    /// <summary>Schema <c>Sequence</c>: canonical unsigned decimal 0..2^63-1, compared ordinally (never through a double).</summary>
    private static readonly Regex SequencePattern = new Regex("^(0|[1-9][0-9]{0,18})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static bool IsSequence(string text) => SequencePattern.IsMatch(text) && !(text.Length == 19 && string.CompareOrdinal(text, "9223372036854775807") > 0);

    /// <summary>Parses and verifies one closure body. Throws <see cref="TansrProtocolException"/> (<c>invalid_closure</c>).</summary>
    public static UnifiedCapabilityClosure Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        int count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            switch (property.Name)
            {
                case "contract": case "closureId": case "authorizationRevision": case "domains": case "operations": break;
                default: throw Invalid();
            }
        }
        if (count != 5) throw Invalid();
        if (value.GetProperty("contract").ValueKind != JsonValueKind.String || value.GetProperty("contract").GetString() != ApiRoutes.Contract) throw Invalid();
        var idValue = value.GetProperty("closureId");
        if (idValue.ValueKind != JsonValueKind.String || !UnifiedHeaders.Digest.IsMatch(idValue.GetString()!)) throw Invalid();
        var revisionValue = value.GetProperty("authorizationRevision");
        string? authorizationRevision = null;
        if (revisionValue.ValueKind == JsonValueKind.String) { authorizationRevision = revisionValue.GetString()!; if (!IsSequence(authorizationRevision)) throw Invalid(); }
        else if (revisionValue.ValueKind != JsonValueKind.Null) throw Invalid();

        var domainsValue = value.GetProperty("domains");
        if (domainsValue.ValueKind != JsonValueKind.Object) throw Invalid();
        var domains = new Dictionary<string, DomainState>(StringComparer.Ordinal);
        foreach (var property in domainsValue.EnumerateObject())
        {
            if (domains.ContainsKey(property.Name) || !Contains(Domains, property.Name)) throw Invalid();
            var state = property.Value;
            if (state.ValueKind != JsonValueKind.Object) throw Invalid();
            int fields = 0; bool? installed = null; string? revision = null; bool revisionSeen = false;
            foreach (var field in state.EnumerateObject())
            {
                fields++;
                switch (field.Name)
                {
                    case "installed":
                        if (field.Value.ValueKind != JsonValueKind.True && field.Value.ValueKind != JsonValueKind.False) throw Invalid();
                        installed = field.Value.GetBoolean(); break;
                    case "revision":
                        revisionSeen = true;
                        if (field.Value.ValueKind == JsonValueKind.String) { revision = field.Value.GetString()!; if (revision.Length < 1 || revision.Length > 512) throw Invalid(); }
                        else if (field.Value.ValueKind != JsonValueKind.Null) throw Invalid();
                        break;
                    default: throw Invalid();
                }
            }
            if (fields != 2 || installed == null || !revisionSeen) throw Invalid();
            domains[property.Name] = new DomainState(installed.Value, revision);
        }
        if (domains.Count != Domains.Count) throw Invalid();

        var operationsValue = value.GetProperty("operations");
        if (operationsValue.ValueKind != JsonValueKind.Object) throw Invalid();
        var operations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in operationsValue.EnumerateObject())
        {
            if (operations.ContainsKey(property.Name) || !IsClosureOperation(property.Name)) throw Invalid();
            if (property.Value.ValueKind != JsonValueKind.String) throw Invalid();
            var state = property.Value.GetString()!;
            if (state != UnifiedOperationState.Enabled && state != UnifiedOperationState.Disabled && state != UnifiedOperationState.Unavailable) throw Invalid();
            operations[property.Name] = state;
        }
        if (operations.Count != Operations.Count) throw Invalid();

        // closureId = sha256(domain ‖ 0x00 ‖ canonical({authorizationRevision, domains, operations})); canonical = the SDK2 control
        // encoding (sorted keys, no whitespace), which is what the facade hashes. A body whose digest does not match is not a
        // closure the SDK may act on.
        using var subset = JsonDocument.Parse(BuildSubset(revisionValue, domainsValue, operationsValue));
        var expected = WireJson.DomainDigest("tansr.unified.closure.v1", WireJson.EncodeControl(subset.RootElement));
        if (!string.Equals(expected, idValue.GetString(), StringComparison.Ordinal)) throw Invalid();
        return new UnifiedCapabilityClosure(idValue.GetString()!, authorizationRevision, domains, operations, value.GetRawText());
    }

    private static string BuildSubset(JsonElement authorizationRevision, JsonElement domains, JsonElement operations) =>
        "{\"authorizationRevision\":" + authorizationRevision.GetRawText() + ",\"domains\":" + domains.GetRawText() + ",\"operations\":" + operations.GetRawText() + "}";

    /// <summary>Parses UTF-8 JSON text of one closure body.</summary>
    public static UnifiedCapabilityClosure Parse(byte[] utf8, int maximumBytes = WireJson.MaximumControlBytes)
    {
        if (utf8 == null) throw new ArgumentNullException(nameof(utf8));
        if (utf8.Length > maximumBytes) throw new TansrProtocolException("response_too_large");
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw Invalid(); }
        using (document) return Parse(document.RootElement.Clone());
    }

    /// <summary>True when <paramref name="name"/> is one of the 77 closure operation names.</summary>
    public static bool IsClosureOperation(string name)
    {
        foreach (var operation in Operations) if (operation.Name == name) return true;
        return false;
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        foreach (var item in list) if (item == value) return true;
        return false;
    }
}
