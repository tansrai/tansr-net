using System.Text.Json;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Terminal;

/// <summary>Immutable original request and trusted owner for reconciliation. Persist both in the
/// application's protected operation journal if recovery must survive a process restart.</summary>
internal abstract class TerminalControlOperation
{
    private int attempted;
    protected TerminalControlOperation(JsonElement request, JsonElement scope, bool attempted)
    { Request = request.Clone(); Scope = scope.Clone(); this.attempted = attempted ? 1 : 0; }
    internal JsonElement Request { get; }
    internal JsonElement Scope { get; }
    internal bool Attempted => Volatile.Read(ref attempted) != 0;
    internal void Begin(bool replay)
    {
        if (!replay && Interlocked.CompareExchange(ref attempted, 1, 0) != 0) throw new TansrProtocolException("operation_already_attempted");
        if (replay) Interlocked.Exchange(ref attempted, 1);
    }
}

internal sealed class TerminalConfigurationOperation : TerminalControlOperation
{
    internal TerminalConfigurationOperation(JsonElement request, JsonElement scope, bool attempted) : base(request, scope, attempted) { }
}

internal sealed class TerminalMemoryOperation : TerminalControlOperation
{
    internal TerminalMemoryOperation(JsonElement request, JsonElement scope, bool attempted) : base(request, scope, attempted) { }
}
