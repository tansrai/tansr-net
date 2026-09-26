using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Transport;

/// <summary>原 strict-event-stream：独立档案帧，不能使用 SDK1 宽松 SSE 字段语义。</summary>
internal sealed class ArchiveEventDecoder
{
    internal const int MaximumJsonBytes = 262144;
    internal const int MaximumFrameBytes = 262317;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Regex Cursor = new Regex("^e1\\.k1\\.[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\\.[0-7][0-9a-f]{15}\\.[0-7][0-9a-f]{15}\\.[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly byte[] raw = new byte[MaximumFrameBytes + 4];
    private readonly string bindingId;
    private readonly string generations;
    private readonly string application;
    private readonly string user;
    private int used, normalized, lineStart, crCount, active;
    private bool pendingCr, comments, closed;
    private volatile bool failed;
    private string? id, eventName, data;

    internal ArchiveEventDecoder(string bindingId, JsonElement generations, JsonElement scope)
    {
        SessionJson.Text(bindingId, 128, nameof(bindingId));
        Validate("Id", SessionJson.Parse(SessionJson.Object(writer => writer.WriteString("id", bindingId))).GetProperty("id"));
        Validate("Generations", generations); Validate("Scope", scope);
        this.bindingId = bindingId; this.generations = WireJson.CanonicalString(generations);
        application = scope.GetProperty("applicationScopeId").GetString()!; user = scope.GetProperty("endUserId").GetString()!;
    }

    internal static void ValidateCursor(string value)
    {
        if (value is null || value.Length > 128 || !Cursor.IsMatch(value)) throw new TansrProtocolException("invalid_event_id");
        var parts = value.Split('.');
        if (long.Parse(parts[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) == 0 ||
            long.Parse(parts[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) == 0) Fail("invalid_event_id");
    }

    private static void Validate(string name, JsonElement value)
    {
        try { WireJson.ValidateNamed(name, value); }
        catch (WireProtocolException) { Fail("invalid_response"); }
    }
    private static void Fail(string code) => throw new TansrProtocolException(code);

    internal async Task FeedAsync(byte[] bytes, int count, Func<JsonElement, CancellationToken, Task> deliver, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            if (count < 0 || count > bytes.Length) throw new ArgumentOutOfRangeException(nameof(count));
            for (int offset = 0; offset < count; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (used >= raw.Length) Fail("sse_frame_too_large");
                var b = bytes[offset]; raw[used++] = b;
                var crlf = pendingCr;
                if (crlf && b != 10) Fail("invalid_utf8");
                pendingCr = false;
                if (b == 13)
                {
                    if (++crCount > 4) Fail("invalid_response");
                    pendingCr = true; continue;
                }
                if (++normalized > MaximumFrameBytes) Fail("sse_frame_too_large");
                if (b != 10) continue;
                var end = used - (crlf ? 2 : 1); string line;
                try { line = Utf8.GetString(raw, lineStart, end - lineStart); }
                catch (DecoderFallbackException) { throw new TansrProtocolException("invalid_utf8"); }
                lineStart = used;
                if (line.Length != 0) { ReadLine(line); continue; }
                var frame = CompleteFrame(); Reset();
                if (frame.HasValue)
                {
                    await deliver(frame.Value, cancellationToken).ConfigureAwait(false);
                    if (failed) Fail("reentrant");
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        catch { failed = true; throw; }
        finally { Volatile.Write(ref active, 0); }
    }

    internal void Complete()
    {
        Enter();
        try { if (used != 0 || pendingCr) Fail("sse_incomplete_frame"); closed = true; }
        catch { failed = true; throw; }
        finally { Volatile.Write(ref active, 0); }
    }

    private void Enter()
    {
        if (Interlocked.Exchange(ref active, 1) != 0) { failed = true; Fail("reentrant"); }
        if (closed || failed) { Volatile.Write(ref active, 0); Fail("event_stream_closed"); }
    }

    private void ReadLine(string line)
    {
        if (line[0] == ':')
        {
            if (id is not null || eventName is not null || data is not null) Fail("invalid_response");
            comments = true; return;
        }
        if (comments) Fail("invalid_response");
        if (id is null && eventName is null && data is null && line.StartsWith("id: ", StringComparison.Ordinal)) id = line.Substring(4);
        else if (id is not null && eventName is null && data is null && line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line.Substring(7);
        else if (id is not null && eventName is not null && data is null && line.StartsWith("data: ", StringComparison.Ordinal)) data = line.Substring(6);
        else Fail("invalid_response");
    }

    private JsonElement? CompleteFrame()
    {
        if (id is null && eventName is null && data is null) return null;
        if (id is null || eventName is null || data is null) throw new TansrProtocolException("invalid_response");
        ValidateCursor(id);
        var value = SessionJson.Control(Utf8.GetBytes(data), MaximumJsonBytes);
        Validate("EventFrame", value);
        if (SessionJson.String(value, "cursor") != id || SessionJson.String(value, "eventType") != eventName ||
            SessionJson.String(value, "bindingId") != bindingId || WireJson.CanonicalString(value.GetProperty("generations")) != generations)
            Fail("context_changed");
        var parts = id.Split('.');
        if (SessionJson.String(value, "eventId") != "e1." + parts[2] + "." + parts[3] + "." + parts[4]) Fail("invalid_event_id");
        var payload = value.GetProperty("payload");
        if (eventName == "archive.records-available")
        {
            if (!Sequence.TryParse(SessionJson.String(payload, "publishedThroughSequence"), out _, false)) Fail("invalid_response");
        }
        else
        {
            if (SessionJson.String(payload, "bindingId") != bindingId) Fail("context_changed");
            if (eventName == "archive.status" || eventName == "binding.status")
                if (SessionJson.String(payload, "revision") != SessionJson.String(value, "revision")) Fail("invalid_response");
            if (payload.TryGetProperty("generations", out var actual) && WireJson.CanonicalString(actual) != generations) Fail("context_changed");
            if (payload.TryGetProperty("target", out var target) && WireJson.CanonicalString(target.GetProperty("generations")) != generations) Fail("context_changed");
            if (payload.TryGetProperty("scope", out var scope) && (SessionJson.String(scope, "applicationScopeId") != application || SessionJson.String(scope, "endUserId") != user)) Fail("context_changed");
        }
        return value;
    }

    private void Reset()
    {
        used = 0; normalized = 0; lineStart = 0; crCount = 0; pendingCr = false;
        id = null; eventName = null; data = null; comments = false;
    }
}
