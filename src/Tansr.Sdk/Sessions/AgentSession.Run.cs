using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Sessions;

public sealed partial class AgentSession
{
    /// <summary>先监听后发送一次。宿主必须独占会话消息写入；Acceptance 与 Completion 分别表示接纳和真实轮终局。</summary>
    public SessionRun StartRun(string prompt, SessionRunOptions? options = null,
        Func<AgentEvent, CancellationToken, Task>? observer = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt must not be blank.", nameof(prompt));
        SessionJson.Unicode(prompt);
        var bytes = SessionJson.Object(w => w.WriteString("prompt", prompt));
        if (bytes.Length > 20 * 1024 * 1024) throw new TansrProtocolException("payload_too_large");
        return StartRunCore(ct => client.SendSessionAsync(HttpMethod.Post, Path + "/messages", bytes, ct, 20 * 1024 * 1024, expectedStatus: 202), options, observer, cancellationToken);
    }

    public SessionRun StartRun(IReadOnlyList<MessageBlock> blocks, SessionRunOptions? options = null,
        Func<AgentEvent, CancellationToken, Task>? observer = null, CancellationToken cancellationToken = default)
    {
        if (blocks is null) throw new ArgumentNullException(nameof(blocks));
        // 固定调用时字节，宿主后续改动列表不能改变已准备发送的请求。
        var bytes = SessionJson.Object(w => { w.WritePropertyName("blocks"); SessionRequestWriter.Blocks(w, blocks); });
        if (bytes.Length > 20 * 1024 * 1024) throw new TansrProtocolException("payload_too_large");
        return StartRunCore(ct => client.SendSessionAsync(HttpMethod.Post, Path + "/messages", bytes, ct, 20 * 1024 * 1024, expectedStatus: 202), options, observer, cancellationToken);
    }

    private SessionRun StartRunCore(Func<CancellationToken, Task<System.Text.Json.JsonElement>> send, SessionRunOptions? options,
        Func<AgentEvent, CancellationToken, Task>? observer, CancellationToken cancellationToken)
    {
        var release = client.AcquireSessionRun(Id);
        try { return new SessionRun(this, send, options ?? new SessionRunOptions(), observer, cancellationToken, release); }
        catch { release(); throw; }
    }

    public async Task<SessionRunResult> SendAndObserveAsync(string prompt, SessionRunOptions? options = null,
        Func<AgentEvent, CancellationToken, Task>? observer = null, CancellationToken cancellationToken = default)
    {
        using var run = StartRun(prompt, options, observer, cancellationToken);
        return await run.Completion.ConfigureAwait(false);
    }

    public async Task<SessionRunResult> SendAndObserveAsync(IReadOnlyList<MessageBlock> blocks, SessionRunOptions? options = null,
        Func<AgentEvent, CancellationToken, Task>? observer = null, CancellationToken cancellationToken = default)
    {
        using var run = StartRun(blocks, options, observer, cancellationToken);
        return await run.Completion.ConfigureAwait(false);
    }

    internal Task ObserveReadyAsync(Func<AgentEvent, CancellationToken, Task> observer, EventStreamOptions options,
        CancellationToken cancellationToken, Action connected)
        => client.ObserveSessionAsync(this, observer, options, cancellationToken, connected);
}
