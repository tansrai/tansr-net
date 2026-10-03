using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Api;

/// <summary>Deployment-level discovery (<c>GET /api/capabilities</c>; unified-v1 <c>Capabilities</c>): which domains this serve has
/// installed, each domain's contract family / status / schema fingerprint, and the session families it offers. Decoding is strict
/// (exact key sets, the eight closure domains, the per-domain family constants from the manifest) and <em>no URL navigation field
/// is ever accepted</em> — the SDK walks the fixed manifest paths (RFC-SDK2-1 §10.5). <c>installed=false</c> is a deployment fact;
/// calling such a domain is answered <c>capability_unavailable</c> and the SDK does not substitute anything for it.</summary>
public sealed class UnifiedCapabilities
{
    /// <summary>Family lifecycle vocabulary (manifest <c>families[].status</c> plus <c>unregistered</c> for a domain without a registered family).</summary>
    public static readonly IReadOnlyList<string> Statuses = new[] { "frozen", "final", "additive", "candidate", "drafted", "unregistered" };
    /// <summary>Session families a deployment may offer (<c>tansr-session-family</c> vocabulary).</summary>
    public static readonly IReadOnlyList<string> SessionFamilies = new[] { "sdk1", "sdk2-offload-v1" };

    private static readonly Regex SchemaHashPattern = new Regex("^(sha256:[a-f0-9]{64}|none)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex FamilyIdPattern = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>One domain's deployment view.</summary>
    public sealed class DomainCapability
    {
        internal DomainCapability(bool installed, string contract, string status, string schemaHash, string? preferredFamily, IReadOnlyList<string>? availableFamilies)
        { Installed = installed; Contract = contract; Status = status; SchemaHash = schemaHash; PreferredFamily = preferredFamily; AvailableFamilies = availableFamilies; }
        /// <summary>The deployment has this domain assembled — not "you may use it" (方案 §3.6).</summary>
        public bool Installed { get; }
        /// <summary>Contract family id (fixed per domain by the manifest).</summary>
        public string Contract { get; }
        public string Status { get; }
        /// <summary><c>sha256:&lt;hex&gt;</c> or <c>none</c>.</summary>
        public string SchemaHash { get; }
        /// <summary>Session domain only: the family used when <c>tansr-session-family</c> is absent (null = none).</summary>
        public string? PreferredFamily { get; }
        /// <summary>Session domain only: families actually assembled; null for every other domain.</summary>
        public IReadOnlyList<string>? AvailableFamilies { get; }
    }

    private readonly IReadOnlyDictionary<string, DomainCapability> domains;

    private UnifiedCapabilities(int manifestRevision, string schemaHash, IReadOnlyDictionary<string, DomainCapability> domains)
    { ManifestRevision = manifestRevision; SchemaHash = schemaHash; this.domains = domains; }

    public string Contract => ApiRoutes.Contract;
    public int ManifestRevision { get; }
    /// <summary>Manifest aggregate fingerprint (<c>sha256:&lt;hex&gt;</c>, always present).</summary>
    public string SchemaHash { get; }

    public DomainCapability Domain(string domain) =>
        domains.TryGetValue(domain ?? throw new ArgumentNullException(nameof(domain)), out var value) ? value : throw new ArgumentOutOfRangeException(nameof(domain));

    /// <summary>True when the operation's domain is installed here. Discovery operations are facade-owned and always available.</summary>
    public bool IsInstalled(ApiOperation operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        return operation.Domain == "discovery" || Domain(operation.Domain).Installed;
    }

    private static TansrProtocolException Invalid() => new TansrProtocolException("invalid_capabilities");

    private static bool In(IReadOnlyList<string> list, string? value)
    {
        foreach (var item in list) if (item == value) return true;
        return false;
    }

    public static UnifiedCapabilities Parse(byte[] utf8, int maximumBytes = WireJson.MaximumControlBytes)
    {
        if (utf8 == null) throw new ArgumentNullException(nameof(utf8));
        if (utf8.Length > maximumBytes) throw new TansrProtocolException("response_too_large");
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw Invalid(); }
        using (document) return Parse(document.RootElement.Clone());
    }

    public static UnifiedCapabilities Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        int count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            switch (property.Name) { case "contract": case "manifestRevision": case "schemaHash": case "domains": break; default: throw Invalid(); }
        }
        if (count != 4) throw Invalid();
        if (value.GetProperty("contract").ValueKind != JsonValueKind.String || value.GetProperty("contract").GetString() != ApiRoutes.Contract) throw Invalid();
        var revision = value.GetProperty("manifestRevision");
        if (revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out var manifestRevision) || manifestRevision < 1 || revision.GetRawText().IndexOfAny(new[] { '.', 'e', 'E' }) >= 0) throw Invalid();
        var aggregate = value.GetProperty("schemaHash");
        if (aggregate.ValueKind != JsonValueKind.String || !aggregate.GetString()!.StartsWith("sha256:", StringComparison.Ordinal) || !SchemaHashPattern.IsMatch(aggregate.GetString()!)) throw Invalid();

        var domainsValue = value.GetProperty("domains");
        if (domainsValue.ValueKind != JsonValueKind.Object) throw Invalid();
        var domains = new Dictionary<string, DomainCapability>(StringComparer.Ordinal);
        foreach (var property in domainsValue.EnumerateObject())
        {
            var domain = property.Name;
            if (domains.ContainsKey(domain) || !In(UnifiedCapabilityClosure.Domains, domain)) throw Invalid();
            domains[domain] = ParseDomain(domain, property.Value);
        }
        if (domains.Count != UnifiedCapabilityClosure.Domains.Count) throw Invalid();
        return new UnifiedCapabilities(manifestRevision, aggregate.GetString()!, domains);
    }

    private static DomainCapability ParseDomain(string domain, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        bool session = domain == "session";
        bool? installed = null; string? contract = null, status = null, schemaHash = null, preferred = null; List<string>? available = null;
        int fields = 0; bool familySeen = false;
        foreach (var field in value.EnumerateObject())
        {
            fields++;
            switch (field.Name)
            {
                case "installed":
                    if (field.Value.ValueKind != JsonValueKind.True && field.Value.ValueKind != JsonValueKind.False) throw Invalid();
                    installed = field.Value.GetBoolean(); break;
                case "contract":
                    if (field.Value.ValueKind != JsonValueKind.String) throw Invalid();
                    contract = field.Value.GetString()!;
                    if (contract.Length < 1 || contract.Length > 64 || !FamilyIdPattern.IsMatch(contract) || contract != ApiRoutes.DomainFamily(domain)) throw Invalid();
                    break;
                case "status":
                    if (field.Value.ValueKind != JsonValueKind.String || !In(Statuses, field.Value.GetString())) throw Invalid();
                    status = field.Value.GetString(); break;
                case "schemaHash":
                    if (field.Value.ValueKind != JsonValueKind.String || !SchemaHashPattern.IsMatch(field.Value.GetString()!)) throw Invalid();
                    schemaHash = field.Value.GetString(); break;
                case "family":
                    // Only the session domain carries family{preferred, available[]}; anywhere else it is an unknown field.
                    if (!session || field.Value.ValueKind != JsonValueKind.Object) throw Invalid();
                    familySeen = true;
                    int familyFields = 0; bool preferredSeen = false;
                    foreach (var member in field.Value.EnumerateObject())
                    {
                        familyFields++;
                        switch (member.Name)
                        {
                            case "preferred":
                                preferredSeen = true;
                                if (member.Value.ValueKind == JsonValueKind.String) { preferred = member.Value.GetString(); if (!In(SessionFamilies, preferred)) throw Invalid(); }
                                else if (member.Value.ValueKind != JsonValueKind.Null) throw Invalid();
                                break;
                            case "available":
                                if (member.Value.ValueKind != JsonValueKind.Array) throw Invalid();
                                available = new List<string>();
                                foreach (var item in member.Value.EnumerateArray())
                                {
                                    if (item.ValueKind != JsonValueKind.String || !In(SessionFamilies, item.GetString()) || available.Contains(item.GetString()!)) throw Invalid();
                                    available.Add(item.GetString()!);
                                }
                                if (available.Count > 2) throw Invalid();
                                break;
                            default: throw Invalid();
                        }
                    }
                    if (familyFields != 2 || !preferredSeen || available == null) throw Invalid();
                    break;
                default: throw Invalid(); // includes any URL navigation field (entry / capabilities / prefix)
            }
        }
        if (fields != (session ? 5 : 4) || installed == null || contract == null || status == null || schemaHash == null || (session && !familySeen)) throw Invalid();
        return new DomainCapability(installed.Value, contract, status, schemaHash, preferred, available?.ToArray());
    }
}
