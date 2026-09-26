using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

internal static class TerminalJson
{
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    internal static long Sequence(JsonElement value, string name) => Protocol.Sequence.Parse(Text(value, name)).ToInt64();
    internal static long? OptionalSequence(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Sequence(value, name);
    internal static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static bool Equal(JsonElement left, JsonElement right) => WireJson.CanonicalString(left) == WireJson.CanonicalString(right);
    internal static JsonElement Object(Action<Utf8JsonWriter> action)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); action(writer); writer.WriteEndObject(); writer.Flush(); }
        return WireJson.Parse(stream.ToArray());
    }
    internal static void Field(Utf8JsonWriter writer, string name, JsonElement value) { writer.WritePropertyName(name); value.WriteTo(writer); }
    internal static void Decimal(Utf8JsonWriter writer, string name, long? value)
    { if (value.HasValue) writer.WriteString(name, Decimal(value.Value)); else writer.WriteNull(name); }
    internal static void Check(bool condition, string code = "invalid_response") { if (!condition) throw new WireProtocolException(code); }
}

internal sealed class TerminalCandidateLimits
{
    internal TerminalCandidateLimits(JsonElement value)
    {
        TerminalCandidateContract.Validate("Limits", value);
        MaxControlBytes = value.GetProperty("maxControlBytes").GetInt32(); MaxBlockBytes = value.GetProperty("maxBlockBytes").GetInt32();
        MaxBatchBytes = value.GetProperty("maxBatchBytes").GetInt32(); MaxPendingBytes = value.GetProperty("maxPendingBytes").GetInt32();
        MaxRetainedBytes = value.GetProperty("maxRetainedBytes").GetInt32();
    }
    internal int MaxControlBytes { get; }
    internal int MaxBlockBytes { get; }
    internal int MaxBatchBytes { get; }
    internal int MaxPendingBytes { get; }
    internal int MaxRetainedBytes { get; }
}

internal sealed class TerminalOutputStatus
{
    internal TerminalOutputStatus(JsonElement value)
    {
        TerminalCandidateContract.Validate("OutputStatus", value); Raw = value.Clone();
        State = TerminalJson.Text(value, "state"); AcceptedThrough = TerminalJson.OptionalSequence(value, "acceptedThrough");
        DurableThrough = TerminalJson.OptionalSequence(value, "durableThrough"); RetainedFrom = TerminalJson.OptionalSequence(value, "retainedFrom");
        NextByteOffset = TerminalJson.OptionalSequence(value, "nextByteOffset");
        var seal = value.GetProperty("seal"); Seal = seal.ValueKind == JsonValueKind.Null ? null : seal.Clone();
    }
    internal JsonElement Raw { get; }
    internal string State { get; }
    internal long? AcceptedThrough { get; }
    internal long? DurableThrough { get; }
    internal long? RetainedFrom { get; }
    internal long? NextByteOffset { get; }
    internal JsonElement? Seal { get; }
}

internal sealed class TerminalOutputPiece
{
    internal TerminalOutputPiece(long? sequence, string channel, string encoding, byte[] bytes, string? text, bool presentationGap)
    { Sequence = sequence; Channel = channel; Encoding = encoding; Bytes = (byte[])bytes.Clone(); Text = text; PresentationGap = presentationGap; }
    internal long? Sequence { get; }
    internal string Channel { get; }
    internal string Encoding { get; }
    internal byte[] Bytes { get; }
    internal string? Text { get; }
    internal bool PresentationGap { get; }
}
