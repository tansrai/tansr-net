using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Hosting;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Tests.Archive;
using static Tansr.Sdk.Tests.Archive.ArchiveFlowFixture;

namespace Tansr.Sdk.Tests.Hosting;

public sealed class ArchiveSessionHostTests
{
    [Fact]
    public async Task HostOwnsDurableAckAndMaterialResponseBeforeSavingEventCursor()
    {
        var f = new ArchiveFlowFixture(); var client = new ArchiveFlowFixture.Client(f); var store = new Store(f); var outbox = new Outbox();
        using var stop = new CancellationTokenSource(); var saved = new List<string>();
        client.OnRespond = () => Assert.NotNull(outbox.Pending);
        client.Observe = async (cursor, accept, ct) =>
        {
            Assert.Null(cursor); await accept(Frame(f, "archive.records-available", 1), ct);
            Assert.Null(store.Pending); await accept(Frame(f, "material.request", 2), ct);
            stop.Cancel(); return null;
        };
        var host = Create(f, client, store, outbox, (cursor, _) =>
        {
            Assert.Null(store.Pending); Assert.Null(outbox.Pending); saved.Add(cursor); return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ObserveAsync(stop.Token));
        Assert.Equal(2, saved.Count); Assert.Equal(saved[1], host.LastEventCursor);
        Assert.Single(client.Calls, c => c == "ack"); Assert.Single(client.Calls, c => c == "respond");
        Assert.False(host.SupportsAcknowledgementRecovery);
        Assert.Equal("unsupported_capability", (await Assert.ThrowsAsync<TansrProtocolException>(() => host.RecoverAcknowledgementAsync(f.RequestIdentity))).Code);
    }

    [Fact]
    public async Task UnknownAckKeepsOriginalIntentAndResumeQueriesItWithoutAnotherPost()
    {
        var f = new ArchiveFlowFixture(); var client = new ArchiveFlowFixture.Client(f) { LoseAck = true }; var store = new Store(f); int cursors = 0;
        client.Observe = async (_, accept, ct) => { await accept(Frame(f, "archive.records-available", 1), ct); return null; };
        var host = Create(f, client, store, new Outbox(), (_, _) => { cursors++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => host.ObserveAsync());
        string original = WireJson.CanonicalString(store.Pending!.Value); Assert.Null(host.LastEventCursor); Assert.Equal(0, cursors);
        client.LoseAck = false; await host.ResumeAsync();
        Assert.Null(store.Pending); Assert.Single(client.Calls, c => c == "ack"); Assert.Single(client.Calls, c => c == "operation");
        Assert.Equal(original, WireJson.CanonicalString(client.LastAck!.Value));
    }

    [Fact]
    public async Task CursorFailureDoesNotAdvanceOrAutomaticallyReconnect()
    {
        var f = new ArchiveFlowFixture(); var client = new ArchiveFlowFixture.Client(f); var store = new Store(f); int streams = 0;
        client.Observe = async (_, accept, ct) => { streams++; await accept(Frame(f, "archive.records-available", 1), ct); return null; };
        var host = Create(f, client, store, new Outbox(), (_, _) => throw new TansrProtocolException("network_error"));
        Assert.Equal("network_error", (await Assert.ThrowsAsync<TansrProtocolException>(() => host.ObserveAsync())).Code);
        Assert.Equal(1, streams); Assert.Null(host.LastEventCursor); Assert.Null(store.Pending);
    }

    [Fact]
    public async Task ReconnectionKeepsOriginalCursorAndDoesNotReprocessDuplicateFrame()
    {
        var f = new ArchiveFlowFixture(); var client = new ArchiveFlowFixture.Client(f); var store = new Store(f); int streams = 0, cursors = 0;
        using var stop = new CancellationTokenSource(); var frame = Frame(f, "archive.records-available", 1);
        client.Observe = async (cursor, accept, ct) =>
        {
            if (++streams == 1) { Assert.Null(cursor); await accept(frame, ct); throw new TansrProtocolException("stream_idle_timeout"); }
            Assert.Equal(frame.GetProperty("cursor").GetString(), cursor); await accept(frame, ct); stop.Cancel(); return cursor;
        };
        var host = Create(f, client, store, new Outbox(), (_, _) => { cursors++; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ObserveAsync(stop.Token));
        Assert.Equal(2, streams); Assert.Equal(1, cursors); Assert.Single(client.Calls, c => c == "ack");
    }

    [Fact]
    public async Task RepeatedCursorWithDifferentFrameIsRejected()
    {
        var f = new ArchiveFlowFixture(); var client = new ArchiveFlowFixture.Client(f); var store = new Store(f); var frame = Frame(f, "archive.records-available", 1);
        client.Observe = async (_, accept, ct) => { await accept(frame, ct); await accept(Set(frame, "revision", "3"), ct); return null; };
        var host = Create(f, client, store, new Outbox(), (_, _) => Task.CompletedTask);
        Assert.Equal("cursor_conflict", (await Assert.ThrowsAsync<TansrProtocolException>(() => host.ObserveAsync())).Code);
        await host.Ready; Assert.Single(client.Calls, c => c == "ack");
    }

    [Fact]
    public async Task ForeignEventAndChangedAuthorizationCannotExposeArchiveOrSaveCursor()
    {
        var f = new ArchiveFlowFixture(); var client = new ArchiveFlowFixture.Client(f); var store = new Store(f, true); int cursors = 0;
        client.Observe = async (_, accept, ct) => { await accept(Set(Frame(f, "archive.records-available", 1), "bindingId", "other"), ct); return null; };
        var host = Create(f, client, store, new Outbox(), (_, _) => { cursors++; return Task.CompletedTask; });
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<TansrProtocolException>(() => host.ObserveAsync())).Code);
        f.User = "other";
        await Assert.ThrowsAsync<TansrProtocolException>(() => host.ReadRecordsAsync(new ArchiveReadRequest { Identity = f.Identity, Selection = Element(new { recordIds = new[] { "record" } }) }));
        Assert.Equal(0, cursors); Assert.Empty(client.Calls);
    }

    private static ArchiveSessionHost Create(ArchiveFlowFixture f, ArchiveFlowFixture.Client client, Store store, Outbox outbox,
        Func<string, CancellationToken, Task> save) => new(client, store, new ArchiveSessionOptions
        { Identity = f.Identity, ReadContext = () => f.Scope, MaterialOutbox = outbox, ReadEventCursorAsync = _ => Task.FromResult<string?>(null), SaveEventCursorAsync = save });
    private static JsonElement Frame(ArchiveFlowFixture f, string type, int index)
    {
        string part = index.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), owner = new('A', 43);
        return Element(new
        {
            protocol = "sdk2-ext-v1",
            bindingId = "binding",
            eventId = "e1." + owner + ".0000000000000001." + part,
            cursor = "e1.k1." + owner + ".0000000000000001." + part + "." + owner,
            revision = "2",
            generations = f.Target.GetProperty("generations"),
            eventType = type,
            payload = type == "material.request" ? f.MaterialRequest : Element(new { publishedThroughSequence = "1" })
        });
    }
    private sealed class Outbox : IMaterialResponseOutbox
    {
        internal JsonElement? Pending;
        public Task<JsonElement?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Pending);
        public Task SaveIfEmptyAsync(JsonElement response, CancellationToken cancellationToken = default)
        { if (Pending.HasValue) Assert.Equal(WireJson.CanonicalString(Pending.Value), WireJson.CanonicalString(response)); Pending = response.Clone(); return Task.CompletedTask; }
        public Task ClearIfExactAsync(JsonElement response, CancellationToken cancellationToken = default)
        { Assert.Equal(WireJson.CanonicalString(Pending!.Value), WireJson.CanonicalString(response)); Pending = null; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
