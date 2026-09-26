using System.Text;
using System.Text.Json;

namespace Tansr.Sdk.Protocol;

// A pinned, assembly-owned contract table. Reuses the original bounded matcher and canonical codec;
// no remote schema loading, DTO reflection, changed SDK2 definitions, or implicit wire limit expansion.
internal sealed class EmbeddedWireContract
{
    private readonly JsonElement definitions;

    internal EmbeddedWireContract(string resourceName, string schemaSha256, IEnumerable<string> sharedDefinitions)
    {
        using var stream = typeof(EmbeddedWireContract).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The pinned wire contract resource is missing.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (WireJson.Sha256(bytes) != schemaSha256) throw new InvalidOperationException("The wire contract fingerprint changed.");
        using var document = JsonDocument.Parse(bytes);
        definitions = document.RootElement.GetProperty("definitions").Clone();
        foreach (var name in sharedDefinitions)
            if (!definitions.TryGetProperty(name, out var copy) ||
                WireJson.CanonicalString(copy) != WireJson.CanonicalString(WireSchema.Definition(name)))
                throw new InvalidOperationException("A copied SDK2 definition changed.");
    }

    internal JsonElement Decode(string name, byte[] bytes, int maximumBytes = WireJson.MaximumControlBytes)
    {
        var value = WireJson.DecodeControl(bytes, maximumBytes); Validate(name, value, maximumBytes); return value;
    }

    internal void Validate(string name, JsonElement value, int maximumBytes = WireJson.MaximumControlBytes)
    {
        // A named schema can also describe an original tool JSON body (for example a negative
        // Windows exitCode). Canonical nonnegative control encoding is imposed by Decode/transport,
        // while structural validation preserves ordinary JSON numbers under the same safety bounds.
        WireJson.Parse(new UTF8Encoding(false, true).GetBytes(value.GetRawText()), maximumBytes);
        WireSchema.ValidateNamed(name, value, definitions);
    }
}
