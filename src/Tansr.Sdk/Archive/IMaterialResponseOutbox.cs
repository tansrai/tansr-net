using System.Text.Json;

namespace Tansr.Sdk.Archive;

/// <summary>保存未查清的原 MaterialResponseRequest。此接口是终端耐久介质，不是新增 Serve 协议。</summary>
public interface IMaterialResponseOutbox
{
    /// <summary>读取固定主体、档案代际下的原响应；空箱返回 null。</summary>
    Task<JsonElement?> ReadAsync(CancellationToken cancellationToken = default);
    /// <summary>耐久保存后才返回。已有完全相同响应可幂等返回，不得覆盖不同响应或重新生成操作键。</summary>
    Task SaveIfEmptyAsync(JsonElement response, CancellationToken cancellationToken = default);
    /// <summary>仅精确匹配才清理。调用方须先核实本次受理或原操作回执，缺失回执不能据此清理。</summary>
    Task ClearIfExactAsync(JsonElement response, CancellationToken cancellationToken = default);
    /// <summary>关闭介质，不删除未解决的响应。</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
