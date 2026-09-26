using System.Text.Json;
using Tansr.Sdk.Terminal;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    internal Uri TerminalOrigin => transport.Origin;
    internal CancellationToken TerminalLifetime => lifetime.Token;
    internal JsonElement ReadTerminalScope()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TansrClient));
        return ReadExecutionScope();
    }

    // Borrow the original handler as well as its credentials. This preserves the owned local
    // Serve process/PID checks; a new anonymous HTTP client would bypass that boundary.
    internal TerminalCandidateHttpTransport BorrowTerminalTransport()
    {
        _ = ReadTerminalScope();
        return new TerminalCandidateHttpTransport(transport, ReadTerminalScope, requestTimeout, streamIdleTimeout, lifetime.Token);
    }
}
