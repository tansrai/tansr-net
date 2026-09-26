using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    private sealed class ArchiveConsumerSlot { internal Task? Consumer; }
    private readonly object archiveConsumerGate = new object();
    private readonly Dictionary<string, ArchiveConsumerSlot> archiveConsumers = new Dictionary<string, ArchiveConsumerSlot>(StringComparer.Ordinal);
    internal const int MaximumArchiveConsumers = 8;

    /// <summary>原档案事件单次连接；不自动重连、不发 ACK，回调负责业务验证和游标耐久提交。</summary>
    internal async Task<string?> ConsumeArchiveEventsAsync(string bindingId, JsonElement generations,
        Func<JsonElement, CancellationToken, Task> onFrame, string? lastEventId, CancellationToken cancellationToken, int controlBytes = 262144,
        Func<CancellationToken, Task>? onConnected = null)
    {
        if (bindingId is null) throw new ArgumentNullException(nameof(bindingId));
        ArchiveConsumerSlot slot;
        lock (archiveConsumerGate)
        {
            if (archiveConsumers.ContainsKey(bindingId)) throw new TansrProtocolException("consumer_pending");
            if (archiveConsumers.Count >= MaximumArchiveConsumers) throw new TansrProtocolException("consumer_capacity_exceeded");
            slot = new ArchiveConsumerSlot(); archiveConsumers.Add(bindingId, slot);
        }
        try { return await ConsumeArchiveConnectionAsync(bindingId, generations, onFrame, lastEventId, cancellationToken, controlBytes, slot, onConnected).ConfigureAwait(false); }
        finally
        {
            var pending = slot.Consumer;
            if (pending is null || pending.IsCompleted) ReleaseArchiveConsumer(bindingId, slot);
            else
                _ = pending.ContinueWith(task =>
                {
                    _ = task.Exception;
                    ReleaseArchiveConsumer(bindingId, slot);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void ReleaseArchiveConsumer(string bindingId, ArchiveConsumerSlot slot)
    {
        lock (archiveConsumerGate)
            if (archiveConsumers.TryGetValue(bindingId, out var current) && ReferenceEquals(current, slot)) archiveConsumers.Remove(bindingId);
    }

    private async Task<string?> ConsumeArchiveConnectionAsync(string bindingId, JsonElement generations,
        Func<JsonElement, CancellationToken, Task> onFrame, string? lastEventId, CancellationToken cancellationToken, int controlBytes, ArchiveConsumerSlot slot,
        Func<CancellationToken, Task>? onConnected)
    {
        if (onFrame is null) throw new ArgumentNullException(nameof(onFrame));
        if (controlBytes < 1024 || controlBytes > WireJson.MaximumControlBytes) throw new ArgumentOutOfRangeException(nameof(controlBytes));
        var scope = ReadExecutionScope(); var expectedScope = WireJson.CanonicalString(scope);
        var decoder = new ArchiveEventDecoder(bindingId, generations, scope);
        if (lastEventId is not null) ArchiveEventDecoder.ValidateCursor(lastEventId);
        using var observation = RequestCancellation(cancellationToken, false);
        using var connection = RequestCancellation(observation.Token);
        var access = await transport.AccessAsync(connection.Token).ConfigureAwait(false);
        void Check()
        {
            observation.Token.ThrowIfCancellationRequested(); transport.AssertCurrent(access);
            if (WireJson.CanonicalString(ReadExecutionScope()) != expectedScope) throw new TansrProtocolException("context_changed");
        }
        Check();
        using var response = await transport.SendAsync(HttpMethod.Get, "/v3/sdk2/bindings/" + Uri.EscapeDataString(bindingId) + "/events?protocol=sdk2-ext-v1",
            access, null, "text/event-stream", null, lastEventId, connection.Token).ConfigureAwait(false);
        Check();
        if ((int)response.StatusCode != 200)
        {
            if (!response.IsSuccessStatusCode) await SessionTransport.ThrowHttpAsync(response, controlBytes, connection.Token, true).ConfigureAwait(false);
            throw new TansrProtocolException("invalid_response");
        }
        SessionTransport.ExpectContent(response, "text/event-stream");
        if (response.Content.Headers.ContentType?.CharSet is null || response.Headers.CacheControl?.ToString().ToLowerInvariant() != "no-store")
            throw new TansrProtocolException("invalid_response");
        connection.CancelAfter(Timeout.InfiniteTimeSpan);
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var closeOnCancel = observation.Token.Register(stream.Dispose);
        Check(); if (onConnected != null) { await onConnected(observation.Token).ConfigureAwait(false); Check(); }
        var bytes = new byte[8192]; var cursor = lastEventId;
        for (; ; )
        {
            Check();
            int count = await ReadEventBytesAsync(stream, bytes, observation.Token).ConfigureAwait(false);
            Check();
            if (count == 0) { decoder.Complete(); return cursor; }
            await decoder.FeedAsync(bytes, count, async (frame, token) =>
            {
                Check();
                Task consumer;
                try { consumer = onFrame(frame, token) ?? throw new TansrProtocolException("consumer_failed"); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { throw new TansrProtocolException("consumer_failed"); }
                slot.Consumer = consumer;
                var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => stopped.TrySetResult(true));
                if (await Task.WhenAny(consumer, stopped.Task).ConfigureAwait(false) != consumer)
                {
                    // 消费者真实结束前保留同绑定和总量槽；取消不为重连释放副作用容量。
                    token.ThrowIfCancellationRequested();
                }
                try { await consumer.ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { throw new TansrProtocolException("consumer_failed"); }
                Check(); cursor = frame.GetProperty("cursor").GetString();
            }, observation.Token).ConfigureAwait(false);
        }
    }
}
