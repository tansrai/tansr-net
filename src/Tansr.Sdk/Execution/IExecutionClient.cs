using System.Text.Json;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Execution;

/// <summary>既有 SDK2 执行协议；HTTP 接纳不等于设备执行完成。</summary>
public interface IExecutionClient
{
    JsonElement ReadScope();
    Task<JsonElement> RegisterAsync(JsonElement registration, CancellationToken cancellationToken);
    Task<JsonElement> HeartbeatAsync(JsonElement connection, CancellationToken cancellationToken);
    Task<JsonElement> PollAsync(JsonElement connection, CancellationToken cancellationToken);
    Task<JsonElement> SubmitAsync(JsonElement receipt, CancellationToken cancellationToken);
    Task<JsonElement> GetStatusAsync(string sessionId, string operationId, CancellationToken cancellationToken);
}

/// <summary>设备初始化与会话绑定；应用策略仍由 Serve 决定。</summary>
public interface IDeviceExecutionClient : IExecutionClient
{
    Task<JsonElement> InitializeAsync(JsonElement initialization, CancellationToken cancellationToken);
    Task<JsonElement> BindExecutionAsync(JsonElement request, JsonElement expectedTarget, CancellationToken cancellationToken);
}

/// <summary>宿主注入的设备资源后端，不在此处创建模型循环或放宽权限。</summary>
public interface IExecutionBackend
{
    JsonElement Registration { get; }

    /// <summary>实际 I/O 前必须调用 guard，返回符合 ResourceResult 的原结构。</summary>
    Task<JsonElement> ExecuteAsync(JsonElement operation, Func<CancellationToken, Task> guard, CancellationToken cancellationToken);
}

/// <summary>可选的本地装配准入；ExecutionHost 在注册或持久化任何请求前检查实际注入的 journal。</summary>
public interface IExecutionJournalRequirements
{
    void ValidateJournal(IExecutorJournal journal);
}

/// <summary>只有后端能确认未发生副作用时，才可报告明确拒绝。</summary>
public sealed class ExecutionRejectedException : Exception
{
    public string Code { get; }
    public ExecutionRejectedException(string code) : base(code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64 || code.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("无效的设备错误码。", nameof(code));
        Code = code;
    }
}
