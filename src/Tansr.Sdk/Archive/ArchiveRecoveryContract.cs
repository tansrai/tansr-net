using System.Text.Json;
using Tansr.Sdk.Protocol;
using J = Tansr.Sdk.Archive.ArchiveJson;

namespace Tansr.Sdk.Archive;

internal static class ArchiveRecoveryContract
{
    internal const int RequestEnvelopeBytes = 1024;
    internal const int ResponseEnvelopeBytes = 4096;
    internal const int MaximumRequestBytes = 263168;
    internal const int MaximumResponseBytes = 528384;
    private static readonly EmbeddedWireContract Schema = new EmbeddedWireContract(
        "Tansr.Sdk.Protocol.sdk2-archive-recovery-v1.schema.json",
        "f530de1096b4f5d56ea688b7f2cec9ae66deb7d3719db85ab1f48287d3bd7ad4",
        new[] { "ArchiveAckRequest", "RequestIdentity", "Id", "Sequence", "Generations", "LegacyId", "Coverage", "RecordSequence", "Digest", "ArchiveArtifactReceipt", "ArchiveAckFormat", "MutationReceipt" });

    internal static JsonElement Request(JsonElement value, int controlBytes = WireJson.MaximumControlBytes)
    {
        Bounds(controlBytes); var fixedValue = J.Copy(value, maximum: controlBytes + RequestEnvelopeBytes);
        Schema.Validate("AckRebaseRequest", fixedValue, controlBytes + RequestEnvelopeBytes);
        var previous = Ack(fixedValue.GetProperty("previous"), controlBytes); var request = fixedValue.GetProperty("request");
        J.Need(J.Text(fixedValue, "bindingId") == J.Text(previous, "bindingId") && !J.Equal(request, previous.GetProperty("request")) &&
            J.Text(request, "operationEpoch") == J.Text(previous.GetProperty("request"), "operationEpoch"), "invalid_request");
        return fixedValue;
    }

    internal static JsonElement Receipt(JsonElement value, JsonElement intent, JsonElement scope, int controlBytes = WireJson.MaximumControlBytes)
    {
        var request = Request(intent, controlBytes); var fixedValue = ReceiptShape(value, controlBytes);
        var previous = request.GetProperty("previous"); var next = Ack(fixedValue.GetProperty("next"), controlBytes);
        J.Need(J.Text(fixedValue, "bindingId") == J.Text(request, "bindingId") && J.Equal(fixedValue.GetProperty("previous"), previous) &&
            J.Equal(fixedValue.GetProperty("request"), request.GetProperty("request")) && J.Equal(next.GetProperty("request"), request.GetProperty("request")) &&
            J.Seq(next, "expectedRevision") > J.Seq(previous, "expectedRevision"));
        var original = J.Build(writer =>
        {
            foreach (var property in next.EnumerateObject())
                if (property.Name == "request" || property.Name == "expectedRevision") J.Put(writer, property.Name, previous.GetProperty(property.Name));
                else property.WriteTo(writer);
        });
        J.Need(J.Equal(original, previous));
        var receipt = J.Copy(fixedValue.GetProperty("receipt"), "MutationReceipt", controlBytes);
        J.VerifyOperation(next, receipt, scope, "archive-ack");
        return fixedValue;
    }

    internal static JsonElement ReceiptShape(JsonElement value, int controlBytes = WireJson.MaximumControlBytes)
    {
        Bounds(controlBytes); var fixedValue = J.Copy(value, maximum: 2 * controlBytes + ResponseEnvelopeBytes);
        Schema.Validate("AckRebaseReceipt", fixedValue, 2 * controlBytes + ResponseEnvelopeBytes); return fixedValue;
    }

    internal static JsonElement Ack(JsonElement value, int controlBytes = WireJson.MaximumControlBytes)
    {
        var ack = J.Copy(value, "ArchiveAckRequest", controlBytes); var coverage = ack.GetProperty("coverage"); J.Coverage(coverage);
        J.Need(J.Seq(coverage, "throughSequence") - J.Seq(coverage, "fromSequence") < 128);
        J.Unique(ack.GetProperty("payloads"), "artifactId"); J.Unique(ack.GetProperty("attachments"), "artifactId");
        var hashes = ack.GetProperty("payloads").EnumerateArray().ToDictionary(item => J.Text(item, "artifactId"), item => J.Text(item, "sha256"), StringComparer.Ordinal);
        foreach (var item in ack.GetProperty("attachments").EnumerateArray())
            if (hashes.TryGetValue(J.Text(item, "artifactId"), out var hash)) J.Need(hash == J.Text(item, "sha256"));
        return ack;
    }

    private static void Bounds(int controlBytes) => J.Need(controlBytes >= 1 && controlBytes <= WireJson.MaximumControlBytes, "invalid_input");
}
