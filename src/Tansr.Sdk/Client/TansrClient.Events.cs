using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Sessions;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    private sealed class EventCursor
    {
        internal string? Id;
        internal long Sequence = -1;
    }
    private sealed class ObserverFailure : Exception
    {
        internal ObserverFailure(Exception cause) { Cause = ExceptionDispatchInfo.Capture(cause); }
        internal ExceptionDispatchInfo Cause { get; }
    }

    internal async Task ObserveSessionAsync(AgentSession session, Func<AgentEvent, CancellationToken, Task> observer,
        EventStreamOptions options, CancellationToken cancellationToken)
    {
        if (observer is null) throw new ArgumentNullException(nameof(observer));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var cursor = new EventCursor { Id = options.LastEventId };
        if (cursor.Id is not null) cursor.Sequence = ParseEventId(cursor.Id);
        bool reconnect = options.Reconnect, stopOnGap = options.StopOnGap;
        using var observation = RequestCancellation(cancellationToken, false);
        int retries = 0;
        for (; ; )
        {
            observation.Token.ThrowIfCancellationRequested();
            try
            {
                if (await ObserveConnectionAsync(session, observer, cursor, stopOnGap, observation.Token).ConfigureAwait(false)) return;
                if (!reconnect || retries >= maxReconnectAttempts) throw new TansrProtocolException("event_stream_disconnected");
            }
            catch (ObserverFailure failure) { failure.Cause.Throw(); throw; }
            catch (TansrProtocolException error) when (reconnect && retries < maxReconnectAttempts &&
                (error.Code == "network_error" || error.Code == "stream_idle_timeout"))
            { }
            catch (TansrHttpException error) when (reconnect && retries < maxReconnectAttempts &&
                (error.StatusCode == 408 || error.StatusCode == 429 || error.StatusCode >= 500))
            { }
            retries++;
            await Task.Delay(reconnectDelay, observation.Token).ConfigureAwait(false);
        }
    }

    private async Task<bool> ObserveConnectionAsync(AgentSession session, Func<AgentEvent, CancellationToken, Task> observer,
        EventCursor cursor, bool stopOnGap, CancellationToken cancellationToken)
    {
        using var connection = RequestCancellation(cancellationToken);
        var access = await transport.AccessAsync(connection.Token).ConfigureAwait(false);
        await EnsureContractAsync(access, connection.Token).ConfigureAwait(false);
        using var response = await transport.SendAsync(HttpMethod.Get, Route(SessionPath(session.Id)) + "/events", access,
            null, "text/event-stream", null, cursor.Id, connection.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, maxResponseBytes, connection.Token).ConfigureAwait(false);
        if ((int)response.StatusCode != 200) throw new TansrProtocolException("invalid_response");
        SessionTransport.ExpectContent(response, "text/event-stream");
        connection.CancelAfter(Timeout.InfiniteTimeSpan);
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var closeOnCancel = cancellationToken.Register(stream.Dispose);
        using var decoder = new SseDecoder(maxEventBytes);
        var bytes = new byte[8192];
        for (; ; )
        {
            transport.AssertCurrent(access);
            int count = await ReadEventBytesAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
            transport.AssertCurrent(access);
            if (count == 0) { decoder.Complete(); return false; }
            foreach (var frame in decoder.Feed(bytes, count))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Data.Length == 0 && frame.Name is null && frame.Id is null) continue;
                var value = SessionJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
                if (frame.Name == "server.replay.gap")
                {
                    if (SessionJson.String(value, "type") != frame.Name || SessionJson.String(value, "sessionId") != session.Id || frame.Id is not null)
                        throw new TansrProtocolException("invalid_response");
                    await NotifyAsync(observer, new AgentEvent(frame.Name, null, value), cancellationToken).ConfigureAwait(false);
                    if (stopOnGap) throw new TansrProtocolException("event_replay_gap");
                    if (value.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "ahead_of_log")
                    { cursor.Sequence = -1; cursor.Id = null; }
                    continue;
                }
                if (frame.Id is null) throw new TansrProtocolException("invalid_event_id");
                long sequence = ParseEventId(frame.Id);
                bool control = frame.Name?.StartsWith("server.", StringComparison.Ordinal) == true;
                string name;
                JsonElement normalized;
                if (control)
                {
                    if (!value.TryGetProperty("ts", out var ts) || ts.ValueKind != JsonValueKind.Number || !ts.TryGetDouble(out var time) || double.IsNaN(time) || double.IsInfinity(time))
                        throw new TansrProtocolException("invalid_response");
                    name = frame.Name!;
                    normalized = SessionJson.Parse(SessionJson.Object(w =>
                    {
                        w.WriteString("type", name); w.WriteString("sessionId", session.Id); w.WriteNumber("seq", sequence);
                        w.WritePropertyName("ts"); ts.WriteTo(w); w.WritePropertyName("payload"); value.WriteTo(w);
                    }));
                }
                else
                {
                    if (frame.Name is not null && frame.Name != "message" || SessionJson.String(value, "sessionId") != session.Id || SessionJson.Sequence(value, "seq") != sequence)
                        throw new TansrProtocolException("invalid_response");
                    name = SessionJson.String(value, "type"); normalized = value;
                }
                if (sequence <= cursor.Sequence) continue;
                // 仅在消费者成功处理后推进其独立游标；其他订阅不消费本订阅队列。
                await NotifyAsync(observer, new AgentEvent(name, frame.Id, normalized), cancellationToken).ConfigureAwait(false);
                cursor.Sequence = sequence; cursor.Id = frame.Id; session.ObserveSequence(sequence);
                if (name == "session.ended") return true;
            }
        }
    }

    private static async Task NotifyAsync(Func<AgentEvent, CancellationToken, Task> observer, AgentEvent item, CancellationToken cancellationToken)
    {
        try { await observer(item, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new ObserverFailure(error); }
    }

    private async Task<int> ReadEventBytesAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(streamIdleTimeout);
        using var closeOnIdle = idle.Token.Register(stream.Dispose);
        try { return await stream.ReadAsync(bytes, 0, bytes.Length, idle.Token).ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (idle.IsCancellationRequested) { throw new TansrProtocolException("stream_idle_timeout"); }
        catch (IOException) { throw new TansrProtocolException("network_error"); }
    }

    private static long ParseEventId(string id)
    {
        if (id.Length == 0 || id.Length > 16 || id.Length > 1 && id[0] == '0' ||
            !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0 || value > SessionJson.SafeInteger)
            throw new TansrProtocolException("invalid_event_id");
        return value;
    }
}
