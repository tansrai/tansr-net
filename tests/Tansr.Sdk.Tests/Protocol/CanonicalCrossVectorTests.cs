using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Tests.Protocol;

/// <summary>UAPI-01 D17 / RFC-UAPI-1 §4.1: the C# canonical codec is compared against the 127 cross-implementation
/// vectors (vendored `contract/canonical-cross-vectors.json`, reference = Node `decodeMetadata`/`encodeMetadata`).
/// Two entry points are measured: the normalising reader (<see cref="WireJson.Parse"/>, accepts any well-formed strict
/// JSON and re-encodes) must agree with the reference verdict and bytes exactly; the strict control entry
/// (<see cref="WireJson.DecodeControl"/>) additionally rejects inputs whose bytes are not already canonical.</summary>
public sealed class CanonicalCrossVectorTests
{
    private const int DefaultMaximumBytes = WireJson.MaximumControlBytes; // api-client / kernel default 262144

    private static readonly JsonDocument Fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contract", "canonical-cross-vectors.json")));

    public static IEnumerable<object[]> Ids => Fixture.RootElement.GetProperty("vectors").EnumerateArray().Select(v => new object[] { v.GetProperty("id").GetString()! });

    private static JsonElement Vector(string id) => Fixture.RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("id").GetString() == id);

    internal static byte[] Input(JsonElement vector)
    {
        if (vector.TryGetProperty("generator", out var generator))
        {
            Assert.Equal("flat-array", generator.GetProperty("kind").GetString());
            var element = generator.GetProperty("element").GetString()!; int count = generator.GetProperty("count").GetInt32();
            var text = new StringBuilder("[");
            for (int i = 0; i < count; i++) { if (i > 0) text.Append(','); text.Append(element); }
            if (generator.TryGetProperty("tail", out var tail)) text.Append(',').Append(tail.GetString());
            return Encoding.UTF8.GetBytes(text.Append(']').ToString());
        }
        var input = vector.GetProperty("input").GetString()!;
        return vector.GetProperty("inputKind").GetString() == "bytes" ? Convert.FromBase64String(input) : Encoding.UTF8.GetBytes(input);
    }

    private static int MaximumBytes(JsonElement vector) => vector.TryGetProperty("maxBytes", out var max) ? max.GetInt32() : DefaultMaximumBytes;

    private static string Hex(byte[] bytes) { var text = new StringBuilder(bytes.Length * 2); foreach (var b in bytes) text.Append(b.ToString("x2")); return text.ToString(); }

    [Theory]
    [MemberData(nameof(Ids))]
    public void NormalisingReaderAgreesWithTheReferenceVerdictAndCanonicalBytes(string id)
    {
        // The C# normalising decode is Parse (strict JSON, bounded, duplicate-key / Unicode checks) followed by the
        // canonical writer (ASCII keys, unsigned safe integers, sorted keys); the verdict is the pair's.
        var vector = Vector(id); var input = Input(vector); int maximum = MaximumBytes(vector);
        bool accept = vector.GetProperty("expect").GetString() == "accept";
        byte[]? canonical = null;
        var error = Record.Exception(() => canonical = WireJson.EncodeControl(WireJson.Parse(input, maximum), maximum));
        if (!accept)
        {
            if (error == null) { Assert.Contains(id, NormalisingDivergences.Keys); return; }
            Assert.IsType<WireProtocolException>(error); Assert.DoesNotContain(id, NormalisingDivergences.Keys); return;
        }
        Assert.Null(error);
        if (vector.TryGetProperty("canonicalHex", out var hex)) Assert.Equal(hex.GetString(), Hex(canonical!));
        else Assert.Equal(vector.GetProperty("canonicalSha256").GetString(), WireJson.Sha256(canonical!));
        // Idempotence: canonical bytes decode and re-encode to themselves.
        Assert.Equal(canonical, WireJson.EncodeControl(WireJson.DecodeControl(canonical!, maximum), maximum));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void StrictControlEntryAcceptsExactlyTheVectorsWhoseInputIsAlreadyCanonical(string id)
    {
        var vector = Vector(id); var input = Input(vector); int maximum = MaximumBytes(vector);
        bool accept = vector.GetProperty("expect").GetString() == "accept";
        var error = Record.Exception(() => WireJson.DecodeControl(input, maximum));
        if (!accept) { Assert.IsType<WireProtocolException>(error); return; }
        bool alreadyCanonical = vector.TryGetProperty("canonicalHex", out var hex) ? hex.GetString() == Hex(input) : true;
        if (alreadyCanonical) Assert.Null(error);
        else { Assert.IsType<WireProtocolException>(error); Assert.Contains(id, NotCanonicalAccepts); }
    }

    [Fact]
    public void VectorInventoryMatchesTheVendoredFixture()
    {
        var vectors = Fixture.RootElement.GetProperty("vectors").EnumerateArray().ToList();
        Assert.Equal(127, vectors.Count);
        Assert.Equal(51, vectors.Count(v => v.GetProperty("expect").GetString() == "accept"));
        var notCanonical = vectors.Where(v => v.GetProperty("expect").GetString() == "accept" && v.TryGetProperty("canonicalHex", out var hex) && hex.GetString() != Hex(Input(v)))
            .Select(v => v.GetProperty("id").GetString()!).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(NotCanonicalAccepts.OrderBy(s => s, StringComparer.Ordinal), notCanonical);
    }

    /// <summary>Reject vectors the C# *normalising* path accepts (the strict control entry rejects all of them, see
    /// <see cref="StrictControlEntryAcceptsExactlyTheVectorsWhoseInputIsAlreadyCanonical"/>). The business reader
    /// deliberately keeps decimals/exponents for family bodies (`WireJson.Parse` contract) and the canonical writer sees
    /// the parsed number, not the token, so `1e3` / `1E+3` / `1.0` normalise to `1000` / `1000` / `1` instead of being
    /// rejected at token level like the Node reference. Control envelopes never take this path (DecodeControl is
    /// byte-strict), so this is registered rather than changed under UAPI-01 (no business-reader change in this lane).</summary>
    internal static readonly Dictionary<string, string> NormalisingDivergences = new(StringComparer.Ordinal)
    {
        ["num-exp-lower"] = "token-level exponent not visible after System.Text.Json parse; normalises to 1000",
        ["num-exp-upper-plus"] = "token-level exponent not visible after System.Text.Json parse; normalises to 1000",
        ["num-decimal-1.0"] = "integral decimal normalises to 1 (business reader keeps decimals by contract)",
    };

    /// <summary>Accept vectors whose input bytes differ from the canonical bytes: the normalising reader accepts and
    /// re-encodes them; the strict control entry rejects them (`invalid_response`). Registered so that the inventory
    /// cannot drift silently when the fixture is re-vendored.</summary>
    internal static readonly string[] NotCanonicalAccepts =
    {
        "bytes-escape-counted", "bytes-whitespace-counted-accept", "key-order-ascii-unsorted", "key-order-escaped-ascii-key", "key-order-nested-unsorted",
        "key-order-numeric-strings-unsorted", "key-order-punct-unsorted", "num-object-padded", "str-control-long-escape-for-short", "str-control-u-escape-upper",
        "str-slash-escaped", "str-surrogate-pair-escaped", "str-surrogate-pair-escaped-upper", "str-u2028-escaped", "str-unicode-escape-ascii",
        "ws-empty-containers-padded", "ws-leading-newline", "ws-padded-mixed", "ws-trailing-space",
    };
}
