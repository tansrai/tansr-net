# 终端服务预览接入

本页对应 `terminal-services-v1` 的 `2026-09-26.candidate-7`。该候选由 Serve 提供，C# 固定校验 schema 指纹 `8cd8c7c55a84c5700373aed75d5653a0737d718bfe0546641be367bda1a11896`。这是显式启用的开发预览，不代表稳定协议或正式 NuGet 发行。旧 SDK1 默认连接和原 SDK2 合同不变。

## 同会话配置与记忆

`TerminalSessionControl` 借用现有 `TansrClient` 的凭据、可信 scope 和生命周期。客户端必须配置 `ExecutionScopeProvider`，内容由可信应用宿主提供；不能从模型输入、未经验证的 JWT 或旧数据库自行推导当前授权。Serve 每次独立验证主体、权限和能力。

```csharp
var control = new TerminalSessionControl(client, enablePreview: true);
var current = await control.ReadConfigurationAsync(session.Id, cancellationToken);
using var changes = JsonDocument.Parse("{\"thinking\":{\"budget\":2048}}");
var operation = control.CreateConfigurationOperation(
    session.Id, Guid.NewGuid().ToString("N"),
    current.GetProperty("configuration").GetProperty("revision").GetInt64(),
    changes.RootElement);
// 在应用的受保护日志中耐久保存 operation.Request 和 operation.Scope，再调用：
var receipt = await control.ApplyConfigurationAsync(operation, cancellationToken);
```

思考参数是否适用于所选模型由 Serve 判定。候选仅定义 `model` 和 `thinking` 变更，其中 `thinking.budget` 是思考 token 配置；系统提示词、任务／费用预算和来源说明不在此候选字段中，不能用客户端本地状态伪装成服务端已修改。

HTTP 失回时保留原请求。配置目前没有按请求键只读查询的接口；读取当前配置不能证明某笔变更已经提交。恢复进程后，用 `RestoreConfigurationOperation(originalRequest, originalScope)` 恢复，再由应用明确选择 `ReplayConfigurationAsync`，仍使用原键和原正文。SDK 不自动重放。

记忆操作先 `ReadMemoryAsync`，再将原响应交给 `CreateMemoryOperation`。支持 `{"kind":"pin","text":"…"}`、`{"kind":"remember"}`、`{"kind":"forget","topic":"…"}`；源身份、代际和修订均来自真实响应。保存原请求后 `SubmitMemoryAsync`；失回先 `QueryMemoryAsync` 查询原操作，必要时由应用明确调用 `ReplayMemoryAsync`。空回执表示未知，不能当成写入失败或再次创建操作的依据。

这些接口只负责管理命令与原回执。Serve 继续负责记忆生成、筛选、使用和权限裁决；有记忆管理按钮并不等于终端已装配完整记忆出版存储工具。

## 分块输出与身份分离

控制端用 `TerminalConnection(controllerOptions, enablePreview: true)` 发现并绑定固定合同，在设备绑定完成后、首次发送前执行 `BindAsync`。绑定请求字段沿 Serve schema；能力发现不等于执行授权。

设备用自己的窄凭据创建另一 `TerminalConnection`，调用 `AttachBinding(controllerBinding)` 接收已验证的类型化绑定。SDK 要求相同服务地址和当前完整 scope，不向设备复制控制端票据。每一次输出写入仍由 Serve 验证原 executor、connection、operation 和 digest。

`CreateOutputSink` 的返回值实现 `IExecutionOutputSink`，可传给 `WindowsExecutorBackend` 的 `executionOutput` 参数。它只捕获已经授权的原执行；未知结果不会重新启动进程。失回后 `ReconcileAsync(operationId)` 只查原输出状态。`ReleaseCapture` 明确放弃该内存来源的重放能力。

控制端通过 `ObserveOutputAsync` 接收原始 SSE 事件，将其交给同一 `TerminalOutputView`，跨重连保留该视图及已实际应用的序号。视图处理 UTF-8 跨块、重复、缺口和封口摘要。输出封口、耐久水位、执行回执是不同事实；不能把 EOF 或完整文本当作进程执行成功。

每连接最多 8 个输出 sink、8 条观察；同一操作只允许一条观察。每 sink 最多 8 个保留捕获或实际在途泵。取消不释放仍在执行的回调和请求所占限额；`CloseAsync` 等原泵退出，`Dispose` 仅发起收尾。宿主注入的 HTTP handler 和回调仍须遵守取消。

## 档案 ACK 恢复

独立的 `sdk2-archive-recovery-v1` 扩展保留原 ACK 和旧控制消息大小限制。恢复请求上限为 263,168 字节，响应为 528,384 字节；内部 ACK/receipt 仍各受旧 262,144 字节限制。

新恢复介质通过 `SqliteArchiveStore.OpenRecoverableAsync` 显式选择；旧 `OpenAsync` 不自动迁移。此批支持明文同步 source 格式 `sdk2-archive-sync-recovery-sqlite-v1`。原明文和加密格式继续可用，但加密恢复、普通 terminal recovery 文件族不能冒称已支持。迁移必须明确选择 `StorageOpenMode.MigrateV1`，旧 reader 会拒绝新格式。

恢复顺序是：保留原 pending ACK → 先耐久固定恢复意图并预留完整回执空间 → 请求 Serve 重基 → 验证原键、代际、覆盖和摘要 → 同事务记录恢复证明、迁移当前批次身份并确认。原 ACK 若已经提交，保存的是旧真实回执。响应丢失或 COMMIT 结果不明时重开后核对原意图和原回执，不重复接收正文、不重新执行模型或工具。

SDK 的恢复协调器和 SQLite 介质是两个独立能力，须明确装配；仅看到对象实现某接口不能证明所打开文件允许恢复。双副本必须共同固定相同恢复意图，未对齐前不继续常规写入或保留策略操作。

## 保留的边界

WPF、WinForms 和 Console 示例的本地草稿／显示历史属于应用界面缓存，可离线回看，不作为可信模型供材。原 SDK2 档案读取和材料提供仍要求当前可信授权，不能用离线快照自证成员关系或撤权状态。

本批无真实模型收费请求、NuGet 发布或生产部署。完整的 Electron 能力对照、原生界面验收、记忆自动出版链以及发行条件继续按原六张工程卡、24 项验收记录。
