using System.Text.Json;

namespace Tansr.Sdk.Terminal;

/// <summary>An immutable configuration request. Save Request and Scope in the application's
/// protected operation journal before its first send. Attempted is not a server commit receipt.</summary>
public sealed class SessionConfigurationOperation
{
    internal SessionConfigurationOperation(TerminalConfigurationOperation inner) { Inner = inner; }
    internal TerminalConfigurationOperation Inner { get; }
    public JsonElement Request => Inner.Request;
    public JsonElement Scope => Inner.Scope;
    public bool Attempted => Inner.Attempted;
}
