using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Archive;

/// <summary>以当前可信来源复验旧版本的只读查看器。接管 version 的关闭责任，不公开原存储、ACK 或供材能力。</summary>
public sealed class ArchiveHistoryView
{
    private readonly IArchiveStore _version;
    private readonly IArchiveHistoryAuthority _current;
    private readonly JsonElement _identity;
    private readonly Func<JsonElement> _readContext;
    private readonly object _gate = new object();
    private bool _busy, _poisoned, _closed;

    public ArchiveHistoryView(IArchiveStore version, IArchiveHistoryAuthority current, JsonElement identity, Func<JsonElement> readContext)
    {
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _readContext = readContext ?? throw new ArgumentNullException(nameof(readContext));
        _identity = Identity(identity);
    }

    public Task<ArchiveHistoryPage> ReadPageAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default)
    {
        ArchiveReadRequest? fixedRequest = null;
        return RunAsync(async (verify, facts) =>
        {
            var page = Page(await _version.ReadRecordsAsync(fixedRequest!, cancellationToken).ConfigureAwait(false), fixedRequest!);
            await verify(page.Records).ConfigureAwait(false);
            return new ArchiveHistoryPage(facts.Version, facts.Current, facts.Revision, page.Records,
                page.Bytes, page.MissingRecordIds, page.NextFromSequence);
        }, cancellationToken, () => fixedRequest = Request(request));
    }

    public async Task<byte[]> ReadArtifactAsync(string recordId, JsonElement reference, CancellationToken cancellationToken = default)
    {
        byte[]? body = null;
        JsonElement fixedReference = default;
        try
        {
            return await RunAsync(async (verify, _) =>
            {
                var request = RecordRequest(new[] { recordId }, 1, 262144);
                var page = Page(await _version.ReadRecordsAsync(request, cancellationToken).ConfigureAwait(false), request);
                Need(page.Records.Count == 1 && page.Records[0].GetProperty("recordId").GetString() == recordId, "identity_mismatch");
                var record = page.Records[0];
                Need(Equal(record.GetProperty("payload"), fixedReference) || record.GetProperty("attachments").EnumerateArray().Any(item => Equal(item, fixedReference)), "identity_mismatch");
                await verify(page.Records).ConfigureAwait(false);
                var supplied = await _version.BodyAsync(fixedReference, cancellationToken).ConfigureAwait(false);
                Need(supplied != null, "integrity_mismatch"); body = (byte[])supplied!.Clone();
                Need(body.LongLength == fixedReference.GetProperty("bytes").GetInt64() && WireJson.Sha256(body) == fixedReference.GetProperty("sha256").GetString(), "integrity_mismatch");
                await verify(page.Records).ConfigureAwait(false); return body;
            }, cancellationToken, () =>
            {
                Id(recordId); fixedReference = Copy(reference, "ArtifactRef");
                Need(fixedReference.GetProperty("sourceId").GetString() == _identity.GetProperty("sourceId").GetString(), "identity_mismatch");
            }).ConfigureAwait(false);
        }
        catch { if (body != null) Array.Clear(body, 0, body.Length); throw; }
    }

    public async Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closed) return;
            Enter();
        }
        try
        {
            await _version.CloseAsync().ConfigureAwait(false);
            lock (_gate) { _closed = true; Need(!_poisoned, "reentrant"); }
        }
        finally { lock (_gate) _busy = false; }
    }

    private async Task<T> RunAsync<T>(Func<Func<IReadOnlyList<JsonElement>, Task>, Facts, Task<T>> work, CancellationToken cancellationToken, Action? prepare = null)
    {
        lock (_gate) Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepare?.Invoke();
            var scope = Scope(); Active(cancellationToken);
            var head = Head(await _current.HeadAsync(cancellationToken).ConfigureAwait(false)); Active(cancellationToken);
            var revision = await _current.RetentionRevisionAsync(cancellationToken).ConfigureAwait(false); Sequence.Parse(revision); Active(cancellationToken);
            var version = Head(await _version.HeadAsync(cancellationToken).ConfigureAwait(false)); Active(cancellationToken);
            Need(HeadSequence(version) <= HeadSequence(head), "context_changed");
            async Task Check()
            {
                Active(cancellationToken); Need(Equal(scope, Scope()), "context_changed"); Active(cancellationToken);
                var currentHead = Head(await _current.HeadAsync(cancellationToken).ConfigureAwait(false)); Active(cancellationToken);
                Need(Equal(scope, Scope()) && EqualHead(head, currentHead), "context_changed"); Active(cancellationToken);
                var currentRevision = await _current.RetentionRevisionAsync(cancellationToken).ConfigureAwait(false); Active(cancellationToken);
                Need(Equal(scope, Scope()) && revision == currentRevision, "context_changed"); Active(cancellationToken);
            }
            async Task Verify(IReadOnlyList<JsonElement> records)
            {
                Active(cancellationToken);
                if (records.Count != 0)
                {
                    var request = RecordRequest(records.Select(record => record.GetProperty("recordId").GetString()!).ToArray(), 128, 1048576);
                    var allowed = Page(await _current.ReadRecordsAsync(request, cancellationToken).ConfigureAwait(false), request); Active(cancellationToken);
                    Need(allowed.MissingRecordIds.Count == 0 && allowed.Records.Count == records.Count &&
                        allowed.Records.Select((record, index) => Equal(record, records[index])).All(equal => equal), "context_changed");
                }
                await Check().ConfigureAwait(false);
            }
            await Check().ConfigureAwait(false);
            var result = await work(Verify, new Facts(version, head, revision)).ConfigureAwait(false);
            await Check().ConfigureAwait(false); return result;
        }
        finally { lock (_gate) _busy = false; }
    }

    private void Enter()
    {
        if (_busy) { _poisoned = true; throw new StorageException("reentrant"); }
        Need(!_closed, "closed"); _busy = true; _poisoned = false;
    }

    private void Active(CancellationToken cancellationToken)
    {
        lock (_gate) Need(!_poisoned, "reentrant");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private JsonElement Scope()
    {
        JsonElement scope;
        try { scope = Copy(_readContext(), "Scope"); }
        catch (StorageException) { throw; }
        catch { throw new StorageException("context_changed"); }
        Need(Equal(Without(scope, "authorizationRevision"), _identity.GetProperty("scope")), "identity_mismatch"); return scope;
    }

    private ArchiveReadRequest Request(ArchiveReadRequest request)
    {
        Need(request != null, "invalid_input");
        var identity = Identity(request!.Identity); var selection = Copy(request.Selection, maximum: 65536);
        int maximum = request.MaxRecords, bytes = request.MaxBytes;
        Need(Equal(identity, _identity), "identity_mismatch");
        Need(maximum >= 1 && maximum <= 128 && bytes >= 1 && bytes <= 1048576, "invalid_input");
        if (selection.TryGetProperty("recordIds", out var ids))
        {
            Fields(selection, "recordIds"); Need(ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() >= 1 && ids.GetArrayLength() <= maximum, "invalid_input");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids.EnumerateArray()) { WireJson.ValidateNamed("Id", id); Need(seen.Add(id.GetString()!), "invalid_input"); }
        }
        else
        {
            Fields(selection, "fromSequence", "throughSequence");
            Need(Sequence.Parse(selection.GetProperty("fromSequence").GetString()!, false).CompareTo(Sequence.Parse(selection.GetProperty("throughSequence").GetString()!, false)) <= 0, "invalid_input");
        }
        return new ArchiveReadRequest { Identity = identity, Selection = selection, MaxRecords = maximum, MaxBytes = bytes };
    }

    private ArchiveReadRequest RecordRequest(IReadOnlyList<string> ids, int maximum, int bytes) => new ArchiveReadRequest
    {
        Identity = _identity.Clone(),
        MaxRecords = maximum,
        MaxBytes = bytes,
        Selection = Object(writer => { writer.WriteStartArray("recordIds"); foreach (string id in ids) writer.WriteStringValue(id); writer.WriteEndArray(); })
    };

    private static ArchiveRecordPage Page(ArchiveRecordPage page, ArchiveReadRequest request)
    {
        Need(page != null && page.Records != null && page.MissingRecordIds != null, "integrity_mismatch");
        Need(page!.Records!.Count <= request.MaxRecords, "integrity_mismatch");
        var records = page.Records.Select(record => Copy(record, "ArchiveRecord")).ToArray(); var missing = page.MissingRecordIds!.ToArray();
        int bytes = records.Sum(record => WireJson.EncodeControl(record).Length);
        Need(bytes <= request.MaxBytes && page.Bytes == bytes, "integrity_mismatch");
        Need(records.Select(record => record.GetProperty("recordId").GetString()).Distinct(StringComparer.Ordinal).Count() == records.Length, "integrity_mismatch");
        foreach (string id in missing) Id(id);
        Need(missing.Distinct(StringComparer.Ordinal).Count() == missing.Length, "integrity_mismatch");
        if (page.NextFromSequence != null) Sequence.Parse(page.NextFromSequence, false);
        if (request.Selection.TryGetProperty("recordIds", out var ids))
        {
            var requested = ids.EnumerateArray().Select(id => id.GetString()!).ToArray();
            var returned = records.Select(record => record.GetProperty("recordId").GetString()!).ToArray();
            Need(page.NextFromSequence == null && returned.All(id => requested.Contains(id)) && missing.All(id => requested.Contains(id)) &&
                !returned.Intersect(missing, StringComparer.Ordinal).Any() && returned.Length + missing.Length == requested.Length, "integrity_mismatch");
        }
        else
        {
            long from = Sequence.Parse(request.Selection.GetProperty("fromSequence").GetString()!, false).ToInt64();
            long through = Sequence.Parse(request.Selection.GetProperty("throughSequence").GetString()!, false).ToInt64();
            Need(missing.Length == 0 && records.Length > 0, "integrity_mismatch");
            for (int index = 0; index < records.Length; index++)
                Need(index <= through - from && Sequence.Parse(records[index].GetProperty("sequence").GetString()!, false).ToInt64() == from + index, "integrity_mismatch");
            long last = Sequence.Parse(records[records.Length - 1].GetProperty("sequence").GetString()!, false).ToInt64();
            Need(last == through ? page.NextFromSequence == null : page.NextFromSequence != null && Sequence.Parse(page.NextFromSequence, false).ToInt64() == last + 1, "integrity_mismatch");
        }
        return new ArchiveRecordPage(Array.AsReadOnly(records), bytes, Array.AsReadOnly(missing), page.NextFromSequence, page.SourceCoverage);
    }

    private static JsonElement Identity(JsonElement value)
    {
        var result = Copy(value); Fields(result, "scope", "bindingId", "target", "sourceId", "sourceGeneration");
        var scope = result.GetProperty("scope"); Fields(scope, "applicationScopeId", "endUserId");
        WireJson.ValidateNamed("Scope", Object(writer => { foreach (var item in scope.EnumerateObject()) item.WriteTo(writer); writer.WriteString("authorizationRevision", "0"); }));
        var target = result.GetProperty("target"); Fields(target, "sessionId", "generations");
        WireJson.ValidateNamed("Target", Object(writer => { foreach (var item in target.EnumerateObject()) item.WriteTo(writer); writer.WriteString("sourceSnapshotDigest", new string('0', 64)); }));
        foreach (string name in new[] { "bindingId", "sourceId", "sourceGeneration" }) WireJson.ValidateNamed("Id", result.GetProperty(name));
        return result;
    }

    private static JsonElement? Head(JsonElement? value)
    {
        if (!value.HasValue || value.Value.ValueKind == JsonValueKind.Null) return null;
        var result = Copy(value.Value); Fields(result, "sequence", "recordDigest");
        WireJson.ValidateNamed("RecordSequence", result.GetProperty("sequence")); WireJson.ValidateNamed("Digest", result.GetProperty("recordDigest")); return result;
    }

    private static long HeadSequence(JsonElement? value) => value == null ? 0 : Sequence.Parse(value.Value.GetProperty("sequence").GetString()!, false).ToInt64();
    private static bool EqualHead(JsonElement? a, JsonElement? b) => a.HasValue == b.HasValue && (!a.HasValue || Equal(a.Value, b!.Value));
    private static JsonElement Copy(JsonElement value, string? schema = null, int maximum = 1048576)
    { var copy = WireJson.Parse(WireJson.EncodeControl(value, maximum), maximum); if (schema != null) WireJson.ValidateNamed(schema, copy); return copy; }
    private static bool Equal(JsonElement a, JsonElement b) => WireJson.CanonicalString(a, 1048576) == WireJson.CanonicalString(b, 1048576);
    private static JsonElement Without(JsonElement value, string excluded) => Object(writer => { foreach (var property in value.EnumerateObject()) if (property.Name != excluded) property.WriteTo(writer); });
    private static JsonElement Object(Action<Utf8JsonWriter> write)
    { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); } return WireJson.Parse(stream.ToArray()); }
    private static void Fields(JsonElement value, params string[] names)
    { Need(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal)), "invalid_input"); }
    private static void Id(string value) => WireJson.ValidateNamed("Id", Object(writer => writer.WriteString("value", value)).GetProperty("value"));
    private static void Need(bool condition, string code) { if (!condition) throw new StorageException(code); }

    private sealed class Facts
    {
        internal JsonElement? Version { get; }
        internal JsonElement? Current { get; }
        internal string Revision { get; }
        internal Facts(JsonElement? version, JsonElement? current, string revision) { Version = version; Current = current; Revision = revision; }
    }
}
