using Tansr.Sdk.Sessions;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Client;

public sealed partial class TansrClient
{
    internal SessionContract InitializationContract => contract;
    internal void BindInitializationIdentity() => transport.BindCurrentIdentity();
    internal string? CaptureInitializationScope() => contract == SessionContract.Sdk2OffloadV1 ? WireJson.CanonicalString(ReadExecutionScope()) : null;
    internal void AssertInitializationScope(string? scope)
    {
        transport.AssertBoundIdentity();
        if (scope != CaptureInitializationScope()) throw new TansrProtocolException("context_changed");
    }

    /// <summary>统一会话接入；复用 Serve 已完成的核心恢复，不在客户端装配或筛选上下文。</summary>
    /// <remarks>不自动重试创建、改变原请求键或发送业务消息。成功后的连接由调用者停止。</remarks>
    public SessionInitialization StartSessionInitialization(CreateSessionOptions request, SessionInitializationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        var fixedRequest = SessionRequestWriter.Copy(request);
        fixedRequest.Labels = request.Labels?.ToDictionary(item => item.Key, item => item.Value);
        fixedRequest.Tools = request.Tools?.ToArray();
        fixedRequest.ClientTools = request.ClientTools?.Clone();
        // 首次 await 前核定并冻结意图；宿主后续修改原集合不能改变同键请求。
        _ = SessionRequestWriter.Create(fixedRequest, contract);
        options ??= new SessionInitializationOptions();
        if (options.CleanupTimeout <= TimeSpan.Zero || options.CleanupTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options), "CleanupTimeout must be positive and at most one minute.");
        var fixedOptions = new SessionInitializationOptions
        {
            CleanupTimeout = options.CleanupTimeout,
            Platform = options.Platform?.Clone(),
            RequestedTools = options.RequestedTools?.ToArray(),
            PreparePersistenceAsync = options.PreparePersistenceAsync,
            SessionConfirmedAsync = options.SessionConfirmedAsync,
            RequiredServices = options.RequiredServices,
            PrepareObservationAsync = options.PrepareObservationAsync,
            Progress = options.Progress
        };
        return new SessionInitialization(this, fixedRequest, fixedOptions, cancellationToken);
    }
}
