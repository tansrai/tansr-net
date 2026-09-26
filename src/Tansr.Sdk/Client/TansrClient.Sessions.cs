using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    private readonly object runGate = new object();
    private readonly HashSet<string> runningSessions = new HashSet<string>(StringComparer.Ordinal);

    internal Action AcquireSessionRun(string sessionId)
    {
        lock (runGate) if (!runningSessions.Add(sessionId)) throw new TansrProtocolException("run_already_active");
        return () => { lock (runGate) runningSessions.Remove(sessionId); };
    }

    internal void AssertSessionWritable(string sessionId)
    {
        lock (runGate) if (runningSessions.Contains(sessionId)) throw new TansrProtocolException("run_already_active");
    }

    /// <summary>按原 create.resume 附着或重建会话；不把 GET 元信息误当作服务端恢复。</summary>
    public Task<AgentSession> ResumeSessionAsync(string sessionId, CreateSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copy = SessionRequestWriter.Copy(options);
        if (copy.ResumeSessionId is not null || copy.ForkSessionId is not null || copy.ForkCheckpointId is not null)
            throw new ArgumentException("Resume options cannot already specify resume or fork.", nameof(options));
        copy.ResumeSessionId = sessionId;
        return CreateSessionAsync(copy, cancellationToken);
    }

    public async Task<SessionPage> GetSessionsAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default)
    {
        var raw = await ListSessionsAsync(limit, offset, cancellationToken).ConfigureAwait(false);
        if (!raw.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() > limit)
            throw new TansrProtocolException("invalid_response");
        var total = SessionJson.Sequence(raw, "total"); var items = new List<SessionMetadata>();
        foreach (var session in sessions.EnumerateArray())
        {
            if (session.ValueKind != JsonValueKind.Object) throw new TansrProtocolException("invalid_response");
            VerifyFamily(session); items.Add(new SessionMetadata(session));
        }
        if (total < items.Count) throw new TansrProtocolException("invalid_response");
        return new SessionPage(items, total);
    }
}
