using System.Text.Json;

namespace Tansr.Sdk.Storage;

/// <summary>开发者显式装配的共同托管权威。必须连接所有原域写者共用的强一致控制面。
/// 只读 HTTP 查询不能实现此接口；不得以本地布尔值、导入证明或普通终端自报结果代替授权。</summary>
public interface ITerminalPersistenceCustodyAuthority
{
    Task<JsonElement> ExecuteAsync(JsonElement request, CancellationToken cancellationToken = default);

    /// <summary>仅本次新认领可获得执行权；旧 active／unknown 必须查询原结果，不能再次派工。</summary>
    Task<ITerminalPersistenceWriterLease> AcquireWriterAsync(JsonElement identity, JsonElement custody,
        JsonElement originalPstRequest, CancellationToken cancellationToken = default);

    /// <summary>先在共同权威冻结全域写者，再读取端侧固定目录。未知 prepare 不得解除围栏。</summary>
    Task<ITerminalPersistenceHandoffLease> BeginHandoffAsync(JsonElement prepareRequest,
        CancellationToken cancellationToken = default);
}

/// <summary>受信认领的原键写者。没有 Dispose／超时释放；未知提交必须保留原认领并对账。</summary>
public interface ITerminalPersistenceWriterLease
{
    JsonElement Claim { get; }
    /// <summary>必须在等待之后和本地事务最终提交前同步检查；失权立即抛出。</summary>
    void AssertCurrent();
    /// <summary>只接收原 SQLite 的确定提交或明确回滚；失败不等于回滚，不自动重发。</summary>
    Task<JsonElement> FinishAsync(JsonElement settlement, CancellationToken cancellationToken = default);
}

/// <summary>原 prepare 的固定域围栏。记录证明不切换 writer，切换必须显式通过共同权威 CAS。</summary>
public interface ITerminalPersistenceHandoffLease
{
    void AssertCurrent();
    Task<JsonElement> RecordPreparedAsync(JsonElement proof, CancellationToken cancellationToken = default);
}
