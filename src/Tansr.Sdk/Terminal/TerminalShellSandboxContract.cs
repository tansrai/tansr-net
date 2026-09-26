using System.Globalization;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

/// <summary>Serve 单写的独立 opt-in profile；不扩张 SDK1 或 candidate-7 字段。</summary>
internal static class TerminalShellSandboxContract
{
    internal const string Protocol = "terminal-shell-sandbox-v1";
    internal const string SchemaSha256 = "be3ebcf6dc650844319a18a15f6abbefb9316f704410b591f9ba6dcab7620fe1";
    internal const string ToolName = "TansrTerminalShellSandbox";
    internal const string DefinitionDigest = "058f31335cbb45a6ffe1b5ede8466013462c544c8108d687f41fa18447d51b96";
    private static readonly EmbeddedWireContract Schema = new("Tansr.Sdk.Terminal.terminal-shell-sandbox-v1.schema.json", SchemaSha256,
        new[] { "Id", "Digest", "Sequence", "ExecutionInterpreter" });

    internal static void ValidateRequest(JsonElement request)
    {
        Schema.Validate("Request", request, 32768);
        var launch = Text(request, "action") == "exec" || Text(request.GetProperty("request"), "action") == "launch";
        var hasCall = request.TryGetProperty("call", out var call); var escalation = request.TryGetProperty("escalation", out var approved);
        if (launch && !hasCall || !launch && (hasCall || escalation)) Fail();
        if (escalation && (Text(approved.GetProperty("approvedCall"), "turnId") != Text(call, "turnId") ||
            Text(approved.GetProperty("approvedCall"), "toolCallId") != Text(call, "toolCallId"))) Fail();
    }

    internal static void ValidateResponse(JsonElement response, JsonElement? request = null)
    {
        Schema.Validate("Response", response, 32768);
        var state = response.GetProperty("sandbox"); var result = response.GetProperty("result");
        var mode = Text(state, "mode"); var capability = Text(state, "capability");
        var escalated = state.GetProperty("escalated").GetBoolean(); var denied = state.GetProperty("isolationDenied").GetBoolean();
        if (escalated && (mode != "on" || capability == "none" || denied) || denied && (mode == "off" || capability == "none") ||
            result.ValueKind == JsonValueKind.Null && !denied) Fail();
        if (Text(response, "action") == "exec" && result.ValueKind != JsonValueKind.Null && result.GetProperty("exitCode").ValueKind != JsonValueKind.Null &&
            !int.TryParse(Text(result, "exitCode"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) Fail();
        if (Text(response, "action") == "exec")
        {
            var output = response.GetProperty("output");
            if ((result.ValueKind == JsonValueKind.Null) != (output.ValueKind == JsonValueKind.Null)) Fail();
            if (result.ValueKind != JsonValueKind.Null)
            {
                var stdout = Encoding.UTF8.GetByteCount(Text(result, "stdout")); var stderr = Encoding.UTF8.GetByteCount(Text(result, "stderr"));
                var totalOut = output.GetProperty("stdout").GetProperty("totalBytes").GetInt32(); var totalErr = output.GetProperty("stderr").GetProperty("totalBytes").GetInt32();
                if (stdout + stderr > 4096 || stdout > totalOut || stderr > totalErr || totalOut + totalErr > 65536 ||
                    output.GetProperty("previewTruncated").GetBoolean() != (stdout + stderr < totalOut + totalErr)) Fail();
            }
        }
        if (!request.HasValue) return;
        var original = request.Value;
        if (Text(original, "action") != Text(response, "action") || escalated && !original.TryGetProperty("escalation", out _)) Fail();
        if (result.ValueKind == JsonValueKind.Null) return;
        if (Text(original, "action") == "exec")
        {
            var output = response.GetProperty("output");
            if (output.GetProperty("stdout").GetProperty("totalBytes").GetInt32() + output.GetProperty("stderr").GetProperty("totalBytes").GetInt32() > original.GetProperty("maxOutputBytes").GetInt32()) Fail();
        }
        else if (Text(original.GetProperty("request"), "action") != Text(result, "action")) Fail();
    }

    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
    private static void Fail() => throw new WireProtocolException("invalid_response");
}
