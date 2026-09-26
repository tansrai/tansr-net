using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    internal async Task<JsonElement> ReadTerminalObservationAsync(string path, string definition, JsonElement originalScope, CancellationToken cancellationToken)
    {
        if (!path.StartsWith("/v3/terminal-observation/sessions/", StringComparison.Ordinal) || path.IndexOf('#') >= 0 ||
            path.IndexOf('\\') >= 0 || System.Text.Encoding.UTF8.GetByteCount(path) > 8192 || definition != "ResourcesResponse" && definition != "CorrelationsResponse")
            throw new TansrProtocolException("invalid_request");
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        void Check()
        {
            cancellation.Token.ThrowIfCancellationRequested(); transport.AssertCurrent(access);
            if (!TerminalJson.Equal(originalScope, ReadExecutionScope())) throw new TansrProtocolException("context_changed");
        }
        Check();
        using var response = await transport.SendAsync(HttpMethod.Get, path, access, null, "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        Check(); SessionTransport.ExpectContent(response, "application/json");
        var bytes = await SessionTransport.ReadBodyAsync(response, WireJson.MaximumControlBytes, cancellation.Token).ConfigureAwait(false); Check();
        if (!response.IsSuccessStatusCode)
        {
            var error = TerminalObservationContract.Decode("ErrorResponse", bytes);
            if (error.GetProperty("status").GetInt32() != (int)response.StatusCode) throw new TansrProtocolException("invalid_response");
            throw new TansrHttpException((int)response.StatusCode, error.GetProperty("code").GetString()!, retryAction: error.GetProperty("retryAction").GetString());
        }
        if ((int)response.StatusCode != 200) throw new TansrProtocolException("invalid_response");
        return TerminalObservationContract.Decode(definition, bytes);
    }

    // Checkpoint deletion is the original route's only 204 success. Do not parse an absent
    // JSON body, accept a different successful status, or retry this mutation after loss.
    internal async Task<JsonElement> DeleteCheckpointAsync(string path, CancellationToken cancellationToken)
    {
        using var cancellation = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(cancellation.Token).ConfigureAwait(false);
        await EnsureContractAsync(access, cancellation.Token).ConfigureAwait(false);
        using var response = await transport.SendAsync(HttpMethod.Delete, Route(path), access, null,
            "application/json", null, null, cancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await SessionTransport.ThrowHttpAsync(response, maxResponseBytes, cancellation.Token).ConfigureAwait(false);
        if ((int)response.StatusCode != 204 ||
            (await SessionTransport.ReadBodyAsync(response, 1, cancellation.Token).ConfigureAwait(false)).Length != 0)
            throw new TansrProtocolException("invalid_response");
        transport.AssertCurrent(access);
        // Preserve the published Task<JsonElement> signature. JSON null denotes an empty
        // response; no synthetic server receipt, request key or cleanup fact is invented.
        using var empty = JsonDocument.Parse("null");
        return empty.RootElement.Clone();
    }

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
