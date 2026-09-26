using System.Text.Json;
using System.Text.RegularExpressions;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

// Internal consumer of the Serve-owned candidate. Never included in the stable public contract.
internal static class TerminalCandidateContract
{
    internal const string Protocol = "terminal-services-v1";
    internal const string Revision = "2026-09-26.candidate-7";
    internal const string SchemaSha256 = "8cd8c7c55a84c5700373aed75d5653a0737d718bfe0546641be367bda1a11896";
    internal const string BackgroundToolName = "TansrTerminalBackground";
    internal const string BackgroundToolDefinitionSha256 = "03848c0c75af6e2e358ac64f81a61c79f595d8fc70442d06ae3c0576e39157b8";
    internal const string MemoryPublicationToolName = "TansrTerminalMemoryPublication";
    internal const string MemoryPublicationToolDefinitionSha256 = "8532a582d40d2d8993a80db59412a89671670ed7f994eaf9bf972bd544a111b0";
    internal const string EmptyDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly HashSet<string> Shared = new(StringComparer.Ordinal)
        { "Id", "LegacyId", "Sequence", "Digest", "Scope", "ExecutionBinding", "ExecutionTarget", "ExecutionInterpreter",
          "ExecutionStatus", "ExecutionOperation", "ResourceRequest", "ExecutionReceiptRequest", "ResourceResult" };
    private static readonly EmbeddedWireContract Contract = new(
        "Tansr.Sdk.Terminal.terminal-services-v1.schema.json", SchemaSha256, Shared);

    internal static JsonElement Decode(string name, byte[] bytes)
    {
        var value = WireJson.DecodeControl(bytes);
        Validate(name, value);
        return value;
    }

    // Exact reserved profiles from the pinned background-profile.ts. Ordinary tools retain their
    // original name equality rule; an arbitrary tool cannot borrow Shell or memory authority.
    internal static bool ValidToolInvocation(JsonElement operation)
    {
        var request = operation.GetProperty("request");
        if (TerminalJson.Text(request, "operation") != "tool.invoke") return false;
        var args = request.GetProperty("args");
        var name = TerminalJson.Text(args, "name");
        var tool = TerminalJson.Text(operation, "toolName");
        string expectedTool, digest, definition;
        if (name == BackgroundToolName)
        { expectedTool = "Shell"; digest = BackgroundToolDefinitionSha256; definition = "BackgroundRequest"; }
        else if (name == MemoryPublicationToolName)
        { expectedTool = "MemoryPublication"; digest = MemoryPublicationToolDefinitionSha256; definition = "MemoryPublicationRequest"; }
        else return tool == name;
        if (tool != expectedTool || TerminalJson.Text(args, "definitionDigest") != digest) return false;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(TerminalJson.Text(args, "argsJson"));
            Validate(definition, WireJson.Parse(bytes, 32768)); return true;
        }
        catch (WireProtocolException) { return false; }
    }

    internal static void Validate(string name, JsonElement value)
    {
        try
        {
            ValidateStructure(name, value);
            if (name == "OutputBlock") ValidateBlock(value);
            if (name == "OutputSeal") ValidateSeal(value);
            if (name == "OutputStatus") ValidateStatus(value);
            if (name == "OutputBatchRequest")
            {
                var length = 0;
                foreach (var block in value.GetProperty("blocks").EnumerateArray())
                { ValidateBlock(block); length += block.GetProperty("byteLength").GetInt32(); }
                if (length > 65536) Fail("payload_too_large");
                if (value.GetProperty("seal").ValueKind != JsonValueKind.Null) ValidateSeal(value.GetProperty("seal"));
            }
            if (name == "OutputEvent")
            {
                if (TerminalJson.Text(value, "type") == "output.block") ValidateBlock(value.GetProperty("block"));
                else ValidateStatus(value.GetProperty("status"));
            }
            if (name == "Limits") ValidateLimits(value);
            if (name == "CapabilitiesResponse")
            {
                ValidateLimits(value.GetProperty("limits"));
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var feature in value.GetProperty("features").EnumerateArray())
                {
                    if (!names.Add(TerminalJson.Text(feature, "feature"))) Fail();
                    if (feature.GetProperty("installed").GetBoolean() && !feature.GetProperty("supported").GetBoolean()) Fail();
                }
                if (names.Count != 4) Fail();
            }
            if (name == "BindingRequest")
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var list in new[] { "required", "optional" })
                    foreach (var feature in value.GetProperty(list).EnumerateArray()) if (!names.Add(feature.GetString()!)) Fail();
                if (names.Count == 0) Fail();
            }
            if (name == "BindingResponse") ValidateLimits(value.GetProperty("limits"));
        }
        catch (WireProtocolException) { throw; }
        catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is FormatException || error is OverflowException || error is RegexMatchTimeoutException)
        { throw new WireProtocolException("invalid_response"); }
    }

    internal static void ValidateStructure(string name, JsonElement value) => Contract.Validate(name, value);

    internal static void ValidateStatus(JsonElement value)
    {
        var accepted = TerminalJson.OptionalSequence(value, "acceptedThrough");
        var durable = TerminalJson.OptionalSequence(value, "durableThrough");
        var retained = TerminalJson.OptionalSequence(value, "retainedFrom");
        var offset = TerminalJson.OptionalSequence(value, "nextByteOffset");
        var state = TerminalJson.Text(value, "state");
        var seal = value.GetProperty("seal");
        if (durable.HasValue && (!accepted.HasValue || durable > accepted) || retained.HasValue && (!accepted.HasValue || retained > accepted)) Fail();
        if (!offset.HasValue)
        {
            if (state != "unavailable" || accepted.HasValue || durable.HasValue || retained.HasValue || seal.ValueKind != JsonValueKind.Null) Fail();
        }
        else if (!accepted.HasValue && offset != 0) Fail();
        // Each block has at least one byte. Subtraction avoids an Int64.MaxValue + 1 sentinel.
        if (accepted.HasValue && (!offset.HasValue || offset <= accepted)) Fail();
        if (seal.ValueKind != JsonValueKind.Null)
        {
            ValidateSeal(seal);
            if (TerminalJson.OptionalSequence(seal, "lastSeq") != accepted || TerminalJson.Sequence(seal, "totalBytes") != offset) Fail();
        }
        if (state == "complete" && (seal.ValueKind == JsonValueKind.Null || seal.GetProperty("truncated").GetBoolean())) Fail();
        if (state == "truncated" && (seal.ValueKind == JsonValueKind.Null || !seal.GetProperty("truncated").GetBoolean())) Fail();
        if (state == "available" && (accepted.HasValue || seal.ValueKind != JsonValueKind.Null)) Fail();
        if (state == "receiving" && (!accepted.HasValue || seal.ValueKind != JsonValueKind.Null)) Fail();
        if (state == "gap" && !accepted.HasValue) Fail();
    }

    private static void ValidateBlock(JsonElement value)
    {
        var bytes = WireJson.DecodeBase64(TerminalJson.Text(value, "base64"));
        if (bytes.Length != value.GetProperty("byteLength").GetInt32() || WireJson.Sha256(bytes) != TerminalJson.Text(value, "payloadDigest")) Fail("integrity_mismatch");
        if (TerminalJson.Sequence(value, "byteOffset") > long.MaxValue - bytes.Length) Fail("capacity_exceeded");
    }

    private static void ValidateSeal(JsonElement value)
    {
        var last = TerminalJson.OptionalSequence(value, "lastSeq");
        var total = TerminalJson.Sequence(value, "totalBytes");
        if (!last.HasValue && (total != 0 || TerminalJson.Text(value, "payloadDigest") != EmptyDigest) || last.HasValue && total <= last) Fail("integrity_mismatch");
    }

    private static void ValidateLimits(JsonElement value)
    {
        var block = value.GetProperty("maxBlockBytes").GetInt32();
        var batch = value.GetProperty("maxBatchBytes").GetInt32();
        if (block > batch || batch > value.GetProperty("maxPendingBytes").GetInt32() || batch > value.GetProperty("maxRetainedBytes").GetInt32()) Fail();
    }

    private static void Fail(string code = "invalid_response") => throw new WireProtocolException(code);
}
