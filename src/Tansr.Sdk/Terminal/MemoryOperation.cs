using System.Text.Json;

namespace Tansr.Sdk.Terminal;

/// <summary>An immutable command against one memory source and generation. Request and Scope
/// may be durably recorded for original-key recovery. Attempted does not mean applied or consumed.</summary>
public sealed class MemoryOperation
{
    internal MemoryOperation(TerminalMemoryOperation inner) { Inner = inner; }
    internal TerminalMemoryOperation Inner { get; }
    public JsonElement Request => Inner.Request;
    public JsonElement Scope => Inner.Scope;
    public bool Attempted => Inner.Attempted;
}
