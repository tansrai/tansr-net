using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Api;

/// <summary>Unified SSE event envelope (RFC-UAPI-1 §1.5; 开发方案 §0 D18: seven keys
/// <c>contract, eventId, domain, type, cursorSet, terminalStatus, raw</c>). Decoding is strict: unknown or missing
/// keys are <c>invalid_envelope</c>; <c>raw</c> is kept as the original JSON text so family decoders see unchanged bytes.</summary>
public sealed class UnifiedEventEnvelope
{
    private UnifiedEventEnvelope(string? eventId, string domain, string? type, CursorSet cursorSet, string? terminalStatus, JsonElement raw, string rawText)
    { EventId = eventId; Domain = domain; Type = type; Cursors = cursorSet; TerminalStatus = terminalStatus; Raw = raw; RawText = rawText; }

    public string Contract => ApiRoutes.Contract;
    /// <summary>Envelope-level event id (D18); null when the server did not assign one.</summary>
    public string? EventId { get; }
    public string Domain { get; }
    /// <summary>Lower-case dotted event type, or null when the frame has none.</summary>
    public string? Type { get; }
    public CursorSet Cursors { get; }
    /// <summary>null | accepted | completed | aborted | unknown.</summary>
    public string? TerminalStatus { get; }
    /// <summary>Original family event object.</summary>
    public JsonElement Raw { get; }
    /// <summary>Original family event text exactly as it appeared inside the envelope.</summary>
    public string RawText { get; }

    /// <summary>Five separated cursor positions (RFC §1.5); positions are never interchangeable.</summary>
    public sealed class CursorSet
    {
        internal CursorSet(string? eventCursor, JsonElement? archiveCoverage, string? outputWatermark, string? materialConsumed, string? ackReceipt)
        { EventCursor = eventCursor; ArchiveCoverage = archiveCoverage; OutputWatermark = outputWatermark; MaterialConsumed = materialConsumed; AckReceipt = ackReceipt; }
        public string? EventCursor { get; }
        /// <summary>Coverage object or cursor string as emitted by the archive family; null when not applicable.</summary>
        public JsonElement? ArchiveCoverage { get; }
        public string? OutputWatermark { get; }
        public string? MaterialConsumed { get; }
        public string? AckReceipt { get; }
    }

    private static TansrProtocolException Invalid() => new TansrProtocolException("invalid_envelope");

    /// <summary>Schema <c>EventType</c>: lower-case dotted segments, <c>_</c> and <c>-</c> allowed inside a segment.</summary>
    private static readonly Regex TypePattern = new Regex("^[a-z][a-z0-9_-]*(\\.[a-z][a-z0-9_-]*)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    /// <summary>Schema <c>Sequence</c>: canonical unsigned decimal 0..2^63-1, never parsed through a double.</summary>
    private static readonly Regex SequencePattern = new Regex("^(0|[1-9][0-9]{0,18})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Schema <c>ArchiveCoverage</c>: exactly fromSequence / throughSequence / headDigest.</summary>
    private static void ValidateCoverage(JsonElement coverage)
    {
        int count = 0;
        foreach (var property in coverage.EnumerateObject())
        {
            count++;
            if (property.Value.ValueKind != JsonValueKind.String) throw Invalid();
            var text = property.Value.GetString()!;
            switch (property.Name)
            {
                case "fromSequence":
                case "throughSequence":
                    if (!SequencePattern.IsMatch(text) || text.Length == 19 && string.CompareOrdinal(text, "9223372036854775807") > 0) throw Invalid();
                    break;
                case "headDigest": if (!UnifiedHeaders.Digest.IsMatch(text)) throw Invalid(); break;
                default: throw Invalid();
            }
        }
        if (count != 3) throw Invalid();
    }

    private static string? NullableText(JsonElement value, string name, int maxLength = 128)
    {
        if (!value.TryGetProperty(name, out var field)) throw Invalid();
        if (field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String) throw Invalid();
        var text = field.GetString()!;
        if (text.Length == 0 || text.Length > maxLength) throw Invalid();
        return text;
    }

    /// <summary>Parses one <c>data:</c> payload. Throws <see cref="TansrProtocolException"/> (<c>invalid_envelope</c>).</summary>
    public static UnifiedEventEnvelope Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        int count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            switch (property.Name)
            {
                case "contract": case "eventId": case "domain": case "type": case "cursorSet": case "terminalStatus": case "raw": break;
                default: throw Invalid();
            }
        }
        if (count != 7) throw Invalid();
        if (value.GetProperty("contract").ValueKind != JsonValueKind.String || value.GetProperty("contract").GetString() != ApiRoutes.Contract) throw Invalid();
        var eventId = NullableText(value, "eventId", 512);
        var domain = NullableText(value, "domain") ?? throw Invalid();
        if (ApiRoutes.DomainFamily(domain) == null || domain == "discovery") throw Invalid();
        var type = NullableText(value, "type");
        if (type != null && !TypePattern.IsMatch(type)) throw Invalid();
        var terminalStatus = NullableText(value, "terminalStatus");
        if (terminalStatus != null && terminalStatus != "accepted" && terminalStatus != "completed" && terminalStatus != "aborted" && terminalStatus != "unknown") throw Invalid();
        var cursors = value.GetProperty("cursorSet");
        if (cursors.ValueKind != JsonValueKind.Object) throw Invalid();
        int cursorCount = 0;
        foreach (var property in cursors.EnumerateObject())
        {
            cursorCount++;
            switch (property.Name)
            {
                case "eventCursor": case "archiveCoverage": case "outputWatermark": case "materialConsumed": case "ackReceipt": break;
                default: throw Invalid();
            }
        }
        if (cursorCount != 5) throw Invalid();
        JsonElement? coverage = null;
        var coverageValue = cursors.GetProperty("archiveCoverage");
        if (coverageValue.ValueKind == JsonValueKind.Object) { ValidateCoverage(coverageValue); coverage = coverageValue.Clone(); }
        else if (coverageValue.ValueKind == JsonValueKind.String) { NullableText(cursors, "archiveCoverage", 512); coverage = coverageValue.Clone(); }
        else if (coverageValue.ValueKind != JsonValueKind.Null) throw Invalid();
        var set = new CursorSet(NullableText(cursors, "eventCursor", 512), coverage, NullableText(cursors, "outputWatermark", 512),
            NullableText(cursors, "materialConsumed", 512), NullableText(cursors, "ackReceipt", 512));
        var raw = value.GetProperty("raw");
        if (raw.ValueKind != JsonValueKind.Object) throw Invalid();
        return new UnifiedEventEnvelope(eventId, domain, type, set, terminalStatus, raw.Clone(), raw.GetRawText());
    }

    /// <summary>Parses UTF-8 JSON text of one <c>data:</c> payload.</summary>
    public static UnifiedEventEnvelope Parse(byte[] utf8, int maximumBytes = 2 * 1024 * 1024)
    {
        if (utf8 == null) throw new ArgumentNullException(nameof(utf8));
        if (utf8.Length > maximumBytes) throw new TansrProtocolException("response_too_large");
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw Invalid(); }
        using (document) return Parse(document.RootElement.Clone());
    }
}
