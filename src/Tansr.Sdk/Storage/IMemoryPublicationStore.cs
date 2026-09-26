using System.Text.Json;

namespace Tansr.Sdk.Storage;

/// <summary>
/// 原 terminal-services-v1 设备记忆 publication 介质。正文是不透明 UTF-8 数据；
/// 提取、选择、删除代际及内容格式仍由 Serve/kernel 决定。
/// </summary>
public interface IMemoryPublicationStore
{
    /// <summary>只有 CAS 正文和 transfer 终局在同一耐久事务提交的后端才可返回 true。</summary>
    bool AtomicDurablePublication { get; }
    /// <summary>可信配置固定的 scope、sourceId、sourceGeneration、domainKey；不能由模型参数选择。</summary>
    JsonElement Identity { get; }
    /// <summary>
    /// 消费原 MemoryPublicationRequest，返回原 MemoryPublicationResponse；ownerCanonical 是原
    /// scope/sessionId/binding 的规范 JSON。必须保留原 transfer 回执、CAS 和隔离语义。
    /// 仅确定未提交/已回滚的业务拒绝可以抛 MemoryPublicationRejectedException；介质/提交未知应保留异常，
    /// 调用方会按未知结果对账，不能重造 transfer。授权及当前绑定由执行宿主在调用前后校验。
    /// </summary>
    Task<JsonElement> ExecuteAsync(JsonElement request, string ownerCanonical, CancellationToken cancellationToken = default);
    /// <summary>由持有者在执行宿主停止后关闭。工具适配器不拥有注入的介质。</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>已确认事务前拒绝/回滚，不可用来表示提交未知或不确定的传输失败。</summary>
public class MemoryPublicationRejectedException : Exception
{
    public MemoryPublicationRejectedException(string code) : this(code, true) { }
    /// <summary>供原公开派生异常保留其构造兼容性；工具响应仍必须通过原协议固定错误码校验。</summary>
    protected MemoryPublicationRejectedException(string code, bool validateCode) : base("Tansr memory publication: " + (validateCode ? ValidateCode(code) : code)) { Code = code; }
    public string Code { get; }

    private static string ValidateCode(string code)
    {
        if (code != "invalid_request" && code != "integrity_mismatch" && code != "request_conflict" &&
            code != "stale_generation" && code != "revision_conflict" && code != "capacity_exceeded")
            throw new ArgumentException("Use a fixed publication rejection code; unknown storage failures must not be classified as rejected.", nameof(code));
        return code;
    }
}
