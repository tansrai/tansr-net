using System.Text;
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using J = Tansr.Sdk.Archive.ArchiveJson;

namespace Tansr.Sdk.Hosting;

/// <summary>可信绑定及本地耐久介质装配；不从模型输入、旧备份或 token 文本推断来源身份。</summary>
public sealed class ArchiveSessionOptions
{
    public JsonElement Identity { get; set; }
    public Func<JsonElement> ReadContext { get; set; } = null!;
    public IMaterialResponseOutbox MaterialOutbox { get; set; } = null!;
    /// <summary>宿主保存 binding/generations 所属的原 cursor；首次绑定返回 null。</summary>
    public Func<CancellationToken, Task<string?>> ReadEventCursorAsync { get; set; } = null!;
    /// <summary>只在事件涉及的耐久动作成功后保存；失败不能推进本地 cursor。</summary>
    public Func<string, CancellationToken, Task> SaveEventCursorAsync { get; set; } = null!;
    public int MaximumPagesPerNotification { get; set; } = 128;
    public int MaximumReconnectAttempts { get; set; } = 3;
    /// <summary>仅已显式选择新版恢复介质时启用；实现可选接口不证明当前打开的文件族支持恢复。</summary>
    public bool EnableAcknowledgementRecovery { get; set; }
}

/// <summary>
/// 档案高阶装配：原耐久 ACK、恢复和材料协调器单一复用，自动消费原档案 SSE。
/// 调用方拥有客户端、store/outbox/cursor 介质，取消并等待 ObserveAsync 后才能关闭它们。
/// 网络或冲突不会更换来源、绑定、请求键或降级持久化策略。
/// </summary>
public sealed class ArchiveSessionHost
{
    private readonly IArchiveClient client;
    private readonly IArchiveEventSource events;
    private readonly IArchiveStore store;
    private readonly ArchiveTransferSession transfer;
    private readonly ArchiveRecoverySession? recovery;
    private readonly MaterialSource materials;
    private readonly JsonElement identity;
    private readonly Func<JsonElement> readContext;
    private readonly Func<CancellationToken, Task<string?>> readCursor;
    private readonly Func<string, CancellationToken, Task> saveCursor;
    private readonly int pages, reconnects;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncLocal<bool> inside = new();
    private string? materialEpoch, cursor;
    private string? lastFrameCanonical;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int observing;

    public ArchiveSessionHost(IArchiveClient client, IArchiveStore store, ArchiveSessionOptions options)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client)); this.store = store ?? throw new ArgumentNullException(nameof(store));
        events = client as IArchiveEventSource ?? throw new ArgumentException("Archive event consumption is required.", nameof(client));
        if (options == null || options.ReadContext == null || options.MaterialOutbox == null ||
            options.ReadEventCursorAsync == null || options.SaveEventCursorAsync == null) throw new ArgumentException("Archive host requires explicit durable storage and cursor ownership.", nameof(options));
        if (options.MaximumPagesPerNotification < 1 || options.MaximumPagesPerNotification > 128 || options.MaximumReconnectAttempts < 0 || options.MaximumReconnectAttempts > 8)
            throw new ArgumentOutOfRangeException(nameof(options));
        identity = J.Identity(options.Identity); readContext = options.ReadContext; readCursor = options.ReadEventCursorAsync; saveCursor = options.SaveEventCursorAsync;
        pages = options.MaximumPagesPerNotification; reconnects = options.MaximumReconnectAttempts;
        transfer = new ArchiveTransferSession(client, store, identity, readContext, binding =>
            RequestIdentity("archive-ack", binding.GetProperty("operationEpoch").GetProperty("id").GetString()!, J.Build(writer =>
            { writer.WriteString("bindingId", J.Text(binding, "bindingId")); writer.WriteString("revision", J.Text(binding, "revision")); })));
        materials = new MaterialSource(client, store, identity, readContext,
            request => RequestIdentity("material-response", materialEpoch ?? throw new TansrProtocolException("epoch_unavailable"), J.Without(request, "remainingTtlMs")), options.MaterialOutbox);
        if (options.EnableAcknowledgementRecovery)
        {
            if (!(store is IRecoverableArchiveStore recoverable) || !(client is IArchiveRecoveryClient recoveryClient))
                throw new TansrProtocolException("unsupported_capability");
            if (store is IArchiveRecoveryAvailability availability && !availability.AcknowledgementRecoveryAvailable)
                throw new TansrProtocolException("unsupported_capability");
            recovery = new ArchiveRecoverySession(recoveryClient, recoverable, identity, readContext);
        }
    }

    public bool SupportsAcknowledgementRecovery => recovery != null;
    public string? LastEventCursor => Volatile.Read(ref cursor);
    public string? LastTransportErrorCode { get; private set; }
    /// <summary>仅当前事件源已核对 SSE 响应头和 scope 后完成；未知旧事件源不伪报通道已就绪。</summary>
    public Task Ready => ready.Task;

    /// <summary>先续办原耐久恢复意图/材料响应，再有界下载并 ACK；没有新数据返回实际零记录结果。</summary>
    public Task<ArchiveTransferResult> SynchronizeAsync(CancellationToken cancellationToken = default)
        => Serial(async () => { await ResumeCore(cancellationToken).ConfigureAwait(false); return await transfer.PullAsync(pages, cancellationToken).ConfigureAwait(false); }, cancellationToken);

    /// <summary>只续办已持久的原意图，不生成恢复 requestId，不重传已确认档案或重跑历史工具。</summary>
    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => Serial(async () => { await ResumeCore(cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task<ArchiveAcknowledgementRecoveryResult> RecoverAcknowledgementAsync(JsonElement originalRecoveryRequest, CancellationToken cancellationToken = default)
    {
        if (recovery == null) throw new TansrProtocolException("unsupported_capability");
        var fixedRequest = J.Copy(originalRecoveryRequest, "RequestIdentity");
        return Serial(() => recovery.RecoverAsync(fixedRequest, cancellationToken), cancellationToken);
    }

    public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        var fixedRequest = new ArchiveReadRequest { Identity = J.Identity(request.Identity), Selection = J.Copy(request.Selection), MaxRecords = request.MaxRecords, MaxBytes = request.MaxBytes };
        if (!J.Equal(identity, fixedRequest.Identity)) throw new TansrProtocolException("context_changed");
        return Serial(() => store.ReadRecordsAsync(fixedRequest, cancellationToken), cancellationToken);
    }

    /// <summary>自动处理 records-available 与 material.request；失败保留原 cursor/待办并显式退出。</summary>
    public async Task ObserveAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref observing, 1, 0) != 0) throw new TansrProtocolException("observation_already_active");
        try
        {
            await ResumeAsync(cancellationToken).ConfigureAwait(false);
            cursor = await Serial(() => readCursor(cancellationToken), cancellationToken).ConfigureAwait(false);
            int attempts = 0;
            for (; ; )
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool callbackFailed = false;
                try
                {
                    Func<JsonElement, CancellationToken, Task> accept = async (frame, ct) =>
                        {
                            try
                            {
                                frame = J.Copy(frame, "EventFrame");
                                J.Need(J.Text(frame, "bindingId") == J.Text(identity, "bindingId") &&
                                    J.Equal(frame.GetProperty("generations"), identity.GetProperty("target").GetProperty("generations")), "context_changed");
                                string next = J.Text(frame, "cursor"); string canonical = J.Canonical(frame);
                                if (next == cursor)
                                {
                                    J.Need(J.Equal(J.Scope(readContext, identity), client.ReadScope()), "context_changed");
                                    J.Need(lastFrameCanonical != null, "cursor_replay_unverifiable");
                                    J.Need(canonical == lastFrameCanonical, "cursor_conflict"); return;
                                }
                                await Serial(async () =>
                                {
                                    string type = J.Text(frame, "eventType");
                                    if (type == "archive.records-available")
                                    {
                                        var pulled = await transfer.PullAsync(pages, ct).ConfigureAwait(false);
                                        if (!pulled.Complete) throw new TansrProtocolException("archive_backlog_pending");
                                    }
                                    else if (type == "material.request")
                                    {
                                        await materials.RecoverPendingAsync(cancellationToken: ct).ConfigureAwait(false);
                                        var binding = J.Copy(await client.GetBindingAsync(J.Text(identity, "bindingId"), ct).ConfigureAwait(false), "BindingView");
                                        materialEpoch = binding.GetProperty("operationEpoch").GetProperty("id").GetString();
                                        await materials.RespondFrameAsync(frame, ct).ConfigureAwait(false);
                                    }
                                    await saveCursor(next, ct).ConfigureAwait(false); return true;
                                }, ct).ConfigureAwait(false);
                                lastFrameCanonical = canonical; Volatile.Write(ref cursor, next); attempts = 0; LastTransportErrorCode = null;
                            }
                            catch { callbackFailed = true; throw; }
                        };
                    if (events is IArchiveConnectionSource connected)
                        await connected.ConsumeConnectedEventsAsync(J.Text(identity, "bindingId"), identity.GetProperty("target").GetProperty("generations"), accept,
                            async ct => { await Serial(() => Task.FromResult(true), ct).ConfigureAwait(false); ready.TrySetResult(true); }, cursor, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        ready.TrySetException(new TansrProtocolException("connection_readiness_unavailable"));
                        await events.ConsumeEventsAsync(J.Text(identity, "bindingId"), identity.GetProperty("target").GetProperty("generations"), accept, cursor, cancellationToken).ConfigureAwait(false);
                    }
                    LastTransportErrorCode = "stream_closed";
                }
                catch (TansrProtocolException error) when (!callbackFailed && (error.Code == "network_error" || error.Code == "stream_idle_timeout" || error.Code == "request_timeout"))
                { LastTransportErrorCode = error.Code; }
                if (++attempts > reconnects) throw new TansrProtocolException(LastTransportErrorCode ?? "stream_closed");
                await Task.Delay(Math.Min(2000, 250 * attempts), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(); throw; }
        catch (Exception error) { ready.TrySetException(error); throw; }
        finally { Volatile.Write(ref observing, 0); }
    }

    private async Task ResumeCore(CancellationToken ct)
    {
        if (recovery != null) await recovery.ResumeAsync(ct).ConfigureAwait(false);
        await transfer.RecoverPendingAsync(cancellationToken: ct).ConfigureAwait(false);
        await materials.RecoverPendingAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<T> Serial<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (inside.Value) throw new TansrProtocolException("reentrant");
        await gate.WaitAsync(ct).ConfigureAwait(false); inside.Value = true;
        try
        {
            var scope = J.Scope(readContext, identity); ct.ThrowIfCancellationRequested();
            J.Need(J.Equal(scope, client.ReadScope()), "context_changed");
            var result = await work().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            J.Need(J.Equal(scope, J.Scope(readContext, identity)) && J.Equal(scope, client.ReadScope()), "context_changed"); return result;
        }
        finally { inside.Value = false; gate.Release(); }
    }

    private static JsonElement RequestIdentity(string kind, string epoch, JsonElement original)
    {
        var semantic = J.Build(writer => { writer.WriteString("operation", kind); writer.WriteString("operationEpoch", epoch); J.Put(writer, "original", original); });
        string requestId = "net-" + WireJson.DomainDigest("tansr.net.archive-request.v1", Encoding.UTF8.GetBytes(J.Canonical(semantic)));
        return J.Build(writer => { writer.WriteString("operationEpoch", epoch); writer.WriteString("requestId", requestId); });
    }
}
