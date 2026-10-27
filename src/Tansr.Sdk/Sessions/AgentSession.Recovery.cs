using System.Globalization;
using System.Net.Http;

namespace Tansr.Sdk.Sessions;

public sealed partial class AgentSession
{
    /// <summary>Reads only the explicit bounded recovery projection. Unsupported sources never fall back to full history.</summary>
    public async Task<SessionRecoveryProjection> GetRecoveryProjectionAsync(SessionRecoveryRequest? request = null, CancellationToken cancellationToken = default)
    {
        var frozen = (request ?? new SessionRecoveryRequest { AfterSequence = LastSequence }).Copy();
        var query = "?projection=recovery&afterSeq=" + frozen.AfterSequence.ToString(CultureInfo.InvariantCulture) +
            "&maxMessages=" + frozen.MaximumMessages.ToString(CultureInfo.InvariantCulture) + "&maxBytes=" + frozen.MaximumBytes.ToString(CultureInfo.InvariantCulture);
        if (frozen.ExpectedFirstSequence.HasValue) query += "&expectedFirstSeq=" + frozen.ExpectedFirstSequence.Value.ToString(CultureInfo.InvariantCulture);
        if (frozen.MinimumTerminalSequence.HasValue) query += "&minTerminalSeq=" + frozen.MinimumTerminalSequence.Value.ToString(CultureInfo.InvariantCulture);
        var value = await client.SendSessionAsync(HttpMethod.Get, Path + "/history" + query, null, cancellationToken, frozen.MaximumBytes).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return SessionRecoveryProjection.Parse(value, Id, frozen);
    }
}
