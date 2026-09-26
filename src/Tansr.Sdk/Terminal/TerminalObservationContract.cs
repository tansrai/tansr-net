using System.Text.Json;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

internal static class TerminalObservationContract
{
    internal const string Protocol = "terminal-observation-v1";
    internal const string SchemaSha256 = "b6668463458e78b2cfe23baa72d9d9ac6263a248467dd8aeb60387fc2f67c28a";
    private static readonly EmbeddedWireContract Contract = new("Tansr.Sdk.Terminal.terminal-observation-v1.schema.json", SchemaSha256, Array.Empty<string>());
    internal static JsonElement Decode(string name, byte[] bytes) => Contract.Decode(name, bytes);
    internal static void Validate(string name, JsonElement value) => Contract.Validate(name, value);
    internal static void ValidateResource(JsonElement value)
    {
        Validate("ResourcesResponse", value);
        var state = value.GetProperty("state").GetString(); var closed = value.GetProperty("closeRequested").GetBoolean();
        var ended = value.GetProperty("executionEnded").GetBoolean(); var error = value.GetProperty("errorCode").GetString();
        if (state == "active" && (closed || ended || error != null) || state == "accepted" && (!closed || ended || error != null) ||
            (state == "completed" || state == "draining") && (!ended || error != null) ||
            state == "unknown" && (!ended || error != "settlement_unavailable") || state == "failed" && (error == null || error == "settlement_unavailable"))
            throw new WireProtocolException("integrity_mismatch");
    }
}
