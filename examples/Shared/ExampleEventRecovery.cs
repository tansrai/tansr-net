using Tansr.Sdk.Sessions;
using Tansr.Sdk.Views;

namespace Tansr.Examples;

// Hosts only connect their presenters. The SDK owns bounded recovery, epoch fences,
// settlement proof and the existing single observation pump.
internal static class ExampleEventRecovery
{
    internal static SessionRecovery Create(AgentSession session, SessionView view, SessionNarrator? narrator = null,
        Func<SessionRecovery, CancellationToken, Task>? changed = null) => new(session, view,
            (projection, token) =>
            {
                token.ThrowIfCancellationRequested();
                narrator?.RestoreRecoveryProjection(projection);
                return Task.CompletedTask;
            }, changed);
}