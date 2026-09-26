using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

internal static class TerminalProfileContract
{
    internal const string Protocol = "terminal-profile-v1";
    internal const string SchemaSha256 = "560ad136f0619a2a0592151d56e10871c3799ca438163c4e2872381bf5f5159a";
    private static readonly EmbeddedWireContract Contract = new("Tansr.Sdk.Terminal.terminal-profile-v1.schema.json", SchemaSha256, Array.Empty<string>());
    internal static JsonElement Decode(string name, byte[] bytes)
    {
        var value = Contract.Decode(name, bytes); Validate(name, value); return value;
    }
    internal static void Validate(string name, JsonElement value)
    {
        Contract.Validate(name, value);
        if (name == "Aliases") ValidateAliases(value);
        else if (name == "Catalog" || name == "CatalogResponse")
        {
            var aliases = value.GetProperty("aliases"); ValidateAliases(aliases);
            var handles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in value.GetProperty("models").EnumerateArray())
                if (!handles.Add(model.GetProperty("handle").GetString()!)) throw new WireProtocolException("integrity_mismatch");
            foreach (var alias in aliases.EnumerateObject())
                if (!handles.Contains(alias.Value.GetString()!)) throw new WireProtocolException("integrity_mismatch");
        }
    }
    private static void ValidateAliases(JsonElement aliases)
    {
        // The shared matcher intentionally implements only the original SDK2 schema vocabulary.
        // This independent record adds its exact key/count/value constraints without changing it.
        var count = 0;
        foreach (var alias in aliases.EnumerateObject())
        {
            if (++count > 1024 || WireJson.ScalarLength(alias.Name) < 1 || WireJson.ScalarLength(alias.Name) > 512)
                throw new WireProtocolException("invalid_response");
            Contract.Validate("Text", alias.Value);
        }
    }
}
