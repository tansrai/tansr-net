using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Terminal;

/// <summary>Internal candidate-3 routes copied from the Serve-owned route snapshot. No automatic
/// POST retries, cursor translation, legacy fallback, or inferred output durability.</summary>
internal sealed class TerminalCandidateHttpTransport : ITerminalCandidateTransport, IDisposable
{
    private const int Maximum = 262144;
    private readonly SessionTransport transport;
    private readonly Func<JsonElement> scopeProvider;
    private readonly TimeSpan requestTimeout, idleTimeout;
    private readonly CancellationTokenSource stop;
    private readonly bool ownsTransport;

    internal TerminalCandidateHttpTransport(TansrClientOptions options, HttpClient? httpClient = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        scopeProvider = options.ExecutionScopeProvider ?? throw new ArgumentException("Candidate transport requires the trusted original scope.", nameof(options));
        requestTimeout = options.RequestTimeout; idleTimeout = options.StreamIdleTimeout;
        if (requestTimeout <= TimeSpan.Zero || requestTimeout.TotalMilliseconds > int.MaxValue ||
            idleTimeout <= TimeSpan.Zero || idleTimeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options));
        transport = new SessionTransport(options, httpClient);
        ownsTransport = true; stop = new CancellationTokenSource();
    }

    internal TerminalCandidateHttpTransport(SessionTransport transport, Func<JsonElement> scopeProvider,
        TimeSpan requestTimeout, TimeSpan idleTimeout, CancellationToken clientLifetime)
    {
        this.transport = transport; this.scopeProvider = scopeProvider;
        this.requestTimeout = requestTimeout; this.idleTimeout = idleTimeout;
        stop = CancellationTokenSource.CreateLinkedTokenSource(clientLifetime);
    }

    private JsonElement Scope()
    {
        JsonElement scope;
        try { scope = scopeProvider().Clone(); }
        catch { throw new TansrProtocolException("scope_unavailable"); }
        WireJson.ValidateNamed("Scope", scope); return scope;
    }
    private void Guard(SessionAccess access, JsonElement expected)
    { transport.AssertCurrent(access); TerminalJson.Check(TerminalJson.Equal(expected, Scope()), "context_changed"); }
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string Decimal(long value)
    { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); return value.ToString(CultureInfo.InvariantCulture); }
    private static string SessionPath(JsonElement session, string action)
    {
        TerminalCandidateContract.Validate("SessionReference", session);
        return "/v3/terminal/sessions/" + Escape(TerminalJson.Text(session, "sessionId")) + "/" + action +
            "?contract=terminal-services-v1&sessionContract=" + Escape(TerminalJson.Text(session, "sessionContract"));
    }
    private static string OutputPath(JsonElement session, JsonElement operation, string action)
    {
        TerminalCandidateContract.Validate("OperationReference", operation);
        return SessionPath(session, action) + "&operationId=" + Escape(TerminalJson.Text(operation, "operationId")) +
            "&requestDigest=" + Escape(TerminalJson.Text(operation, "requestDigest"));
    }
    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken) => ControlAsync(HttpMethod.Get,
        "/v3/terminal/capabilities?contract=terminal-services-v1", null, "CapabilitiesResponse", cancellationToken);
    public Task<JsonElement> BindAsync(JsonElement request, CancellationToken cancellationToken)
    {
        TerminalCandidateContract.Validate("BindingRequest", request);
        return ControlAsync(HttpMethod.Post, "/v3/terminal/bindings", request, "BindingResponse", cancellationToken);
    }
    public Task<JsonElement> SendBatchAsync(JsonElement request, CancellationToken cancellationToken)
    {
        TerminalCandidateContract.Validate("OutputBatchRequest", request);
        return ControlAsync(HttpMethod.Post, "/v3/terminal/executors/" + Escape(TerminalJson.Text(request, "executorId")) +
            "/output-batches", request, "OutputStatus", cancellationToken);
    }
    public Task<JsonElement> GetOutputStatusAsync(JsonElement session, JsonElement operation, CancellationToken cancellationToken) =>
        ControlAsync(HttpMethod.Get, OutputPath(session, operation, "tool-output-status"), null, "OutputStatus", cancellationToken);
    public Task<JsonElement> GetExecutionStateAsync(JsonElement session, JsonElement operation, string executorId, string connectionId, CancellationToken cancellationToken)
    {
        TerminalCandidateContract.Validate("SessionReference", session); TerminalCandidateContract.Validate("OperationReference", operation);
        var ids = TerminalJson.Object(w => { w.WriteString("executorId", executorId); w.WriteString("connectionId", connectionId); });
        TerminalCandidateContract.Validate("Id", ids.GetProperty("executorId")); TerminalCandidateContract.Validate("Id", ids.GetProperty("connectionId"));
        var path = "/v3/terminal/executors/" + Escape(executorId) + "/operations/" + Escape(TerminalJson.Text(operation, "operationId")) +
            "?contract=terminal-services-v1&sessionContract=" + Escape(TerminalJson.Text(session, "sessionContract")) +
            "&sessionId=" + Escape(TerminalJson.Text(session, "sessionId")) + "&requestDigest=" + Escape(TerminalJson.Text(operation, "requestDigest")) + "&connectionId=" + Escape(connectionId);
        return ControlAsync(HttpMethod.Get, path, null, "ExecutionState", cancellationToken);
    }

    private async Task<JsonElement> ControlAsync(HttpMethod method, string path, JsonElement? request, string responseName, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        deadline.CancelAfter(requestTimeout);
        var scope = Scope(); var access = await transport.AccessAsync(deadline.Token).ConfigureAwait(false); Guard(access, scope);
        var bytes = request.HasValue ? WireJson.EncodeControl(request.Value, Maximum) : null;
        using var response = await transport.SendAsync(method, path, access, bytes, "application/json", "application/json", null, deadline.Token).ConfigureAwait(false);
        Guard(access, scope); SessionTransport.ExpectContent(response, "application/json");
        var body = await SessionTransport.ReadBodyAsync(response, Maximum, deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested(); Guard(access, scope);
        if (!response.IsSuccessStatusCode) ThrowHttp(response, body);
        TerminalJson.Check((int)response.StatusCode == 200, "invalid_response");
        return TerminalCandidateContract.Decode(responseName, body);
    }
    private static void ThrowHttp(HttpResponseMessage response, byte[] body)
    {
        var error = TerminalCandidateContract.Decode("ErrorResponse", body);
        TerminalJson.Check(error.GetProperty("status").GetInt32() == (int)response.StatusCode, "invalid_response");
        throw new TansrHttpException((int)response.StatusCode, TerminalJson.Text(error, "code"), retryAction: TerminalJson.Text(error, "retryAction"));
    }

    // Each invocation is one connection. The caller preserves its observer decoder and chooses the
    // last successfully applied raw sequence explicitly; EOF never means a sealed or successful tool.
    internal Task ObserveOutputAsync(JsonElement session, JsonElement operation, long? afterSequence,
        Func<JsonElement, CancellationToken, Task> observer, CancellationToken cancellationToken)
    {
        operation = operation.Clone();
        var path = OutputPath(session, operation, "tool-output") + (afterSequence.HasValue ? "&afterSeq=" + Decimal(afterSequence.Value) : "");
        return ObserveAsync(path, null, "OutputEvent", value =>
        {
            var actual = TerminalJson.Text(value, "type") == "output.block" ? value.GetProperty("operation") : value.GetProperty("status").GetProperty("operation");
            TerminalJson.Check(TerminalJson.Equal(actual, operation), "binding_conflict");
        }, observer, cancellationToken);
    }
    internal Task ObserveExecutorAsync(string sessionContract, string executorId, string connectionId, long? lastEventId,
        Func<JsonElement, CancellationToken, Task> observer, CancellationToken cancellationToken)
    {
        // Validate IDs through the shared scalar codec without trusting URL escaping as validation.
        var identity = TerminalJson.Object(w => { w.WriteString("executorId", executorId); w.WriteString("connectionId", connectionId); w.WriteString("family", sessionContract); });
        TerminalCandidateContract.Validate("SessionContract", identity.GetProperty("family"));
        TerminalCandidateContract.Validate("Id", identity.GetProperty("executorId"));
        TerminalCandidateContract.Validate("Id", identity.GetProperty("connectionId"));
        var path = "/v3/terminal/executors/" + Escape(executorId) + "/events?contract=terminal-services-v1&sessionContract=" + Escape(sessionContract) + "&connectionId=" + Escape(connectionId);
        return ObserveAsync(path, lastEventId.HasValue ? Decimal(lastEventId.Value) : null, "ExecutorEvent", value =>
            TerminalJson.Check(TerminalJson.Text(value, "executorId") == executorId && TerminalJson.Text(value, "connectionId") == connectionId, "binding_conflict"), observer, cancellationToken);
    }
    private async Task ObserveAsync(string path, string? lastEventId, string definition, Action<JsonElement> validateIdentity,
        Func<JsonElement, CancellationToken, Task> observer, CancellationToken cancellationToken)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        connection.CancelAfter(requestTimeout);
        var scope = Scope(); var access = await transport.AccessAsync(connection.Token).ConfigureAwait(false); Guard(access, scope);
        using var response = await transport.SendAsync(HttpMethod.Get, path, access, null, "text/event-stream", null, lastEventId, connection.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SessionTransport.ExpectContent(response, "application/json");
            var body = await SessionTransport.ReadBodyAsync(response, Maximum, connection.Token).ConfigureAwait(false);
            Guard(access, scope); ThrowHttp(response, body);
        }
        TerminalJson.Check((int)response.StatusCode == 200, "invalid_response"); SessionTransport.ExpectContent(response, "text/event-stream");
        connection.CancelAfter(Timeout.InfiniteTimeSpan);
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var close = connection.Token.Register(stream.Dispose);
        using var decoder = new SseDecoder(Maximum + 256);
        var buffer = new byte[8192];
        for (; ; )
        {
            Guard(access, scope);
            int count;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
            {
                idle.CancelAfter(idleTimeout); using var closeIdle = idle.Token.Register(stream.Dispose);
                try { count = await StreamingBodyReader.ReadAsync(stream, buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false); }
                catch (Exception) when (connection.IsCancellationRequested) { throw new OperationCanceledException(connection.Token); }
                catch (Exception) when (idle.IsCancellationRequested) { throw new TansrProtocolException("stream_idle_timeout"); }
                catch (IOException) { throw new TansrProtocolException("network_error"); }
            }
            connection.Token.ThrowIfCancellationRequested(); Guard(access, scope);
            if (count == 0) { decoder.Complete(); throw new TansrProtocolException("event_stream_disconnected"); }
            foreach (var frame in decoder.Feed(buffer, count))
            {
                // Serve starts the stream with a retry-only, empty-data control frame. It has
                // no event identity; a named or identified empty frame remains invalid JSON.
                if (frame.Name == null && frame.Id == null && frame.Data.Length == 0) continue;
                var value = TerminalCandidateContract.Decode(definition, Encoding.UTF8.GetBytes(frame.Data));
                TerminalJson.Check(frame.Name == TerminalJson.Text(value, "type"), "invalid_response");
                TerminalJson.Check(definition == "OutputEvent" ? frame.Id == null : frame.Id == TerminalJson.Text(value, "eventId"), "invalid_event_id");
                validateIdentity(value); Guard(access, scope); connection.Token.ThrowIfCancellationRequested();
                await observer(value, connection.Token).ConfigureAwait(false);
            }
        }
    }
    public void Dispose() { stop.Cancel(); if (ownsTransport) transport.Dispose(); }
}
