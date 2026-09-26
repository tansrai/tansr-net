# 设备端自动记忆接入（开发候选）

本接线属于原 NET-04 的显式 preview，使用 Serve 拥有的 `terminal-services-v1 / 2026-09-26.candidate-7`，不改变 SDK1 历史/记忆和现有档案存储。当前实现与验收状态以[开发记录](development.md)为准；本页不代表已发布或完整 NET-A14 已通过。

## 职责与介质

Serve/kernel 继续决定何时提取、选取、固化、更新和删除记忆。C# 负责执行已授权的 `TansrTerminalMemoryPublication` 保留 profile，将完整记忆 publication 耐久保存在应用指定的 Windows SQLite 文件。该 profile 使用 `MemoryPublication` 权限及固定定义摘要，不进入模型工具列表，不借 Shell 权限执行，也不允许任意路径或命令。

本地 `SqliteMemoryPublicationStore` 沿用 Node 的 `terminal-memory-publication-sqlite-v1` 格式：`metadata`、`publication`、`transfers` 三表，WAL/FULL 事务，`head/read/begin/chunk/commit/query` 六动作。阶段正文完整预留，最终 publication 的 CAS 与 transfer 终态在同一事务中落下。它是专用 publication 介质，不是 Archive/历史/材料响应箱；不能通过将旧档案数据库重命名来迁移。

当前介质为明文，需使用受限用户目录；没有暗含的 DPAPI、正文加密、清账、回收或容量扩展。既有加密档案能力保持原样，但不代表这个新介质已经加密。Create 不覆盖已有文件；Reopen 按原身份、格式、容量及正文摘要复核，不把损坏、撤权或缺失文件视为空库重建。

## 两端可信装配

服务端由开发者的可信 Node 宿主装配 `createAgentSessionFactory({platform:{memoryPublicationFor(scope)},execution:{...}})` 和 `startServer`。来源 identity 必须由宿主按当前认证应用/用户从来源登记读取；`domainKey` 由公开 `memoryPublicationKey(identity)` 计算，计算中排除 sourceGeneration。客户端自报的 source/domainKey 或模型建议不能建立授权。

`minimumDeletionGeneration` 来自可信删除见证，provenance 来自可信归档/轮次来源。晋升记忆所需的 projectFile/userFile 和 `askUser` 沿既有源内目标、提问与权限桥配置；没有这些条件的 remember 应明确拒绝，不能自动批准。测试里的固定删除代际、假轮次或固定 scope 只用于合成验收，不能照搬生产。

当前 CLI 的普通 `serve` 命令没有消费 `memoryPublicationFor` 的配置开关。C# 可以接开发者按上述工厂装配的服务，但不能声称启动任意本地 Serve 就自动拥有端侧记忆。原 Electron 集成式完整 SDK/IPC 继续保持。

设备侧显式打开 `SqliteMemoryPublicationStore`，以 `WindowsMemoryPublicationHost.CreateTool()` 注册到原 `WindowsExecutorBackend`，交给原 `DeviceSessionHost`、`ExecutionHost` 和 `SqliteExecutorJournal` 认领及结算。源、应用/用户、会话、完整绑定代际、原操作参数和工具摘要均须吻合，实际 IO 前后再检查当前授权。读取/写入后失去授权或提交是否成功未知时，不将它包装成确定失败并自动重做。

保持设备 polling 直到可信服务已完成记忆任务及耐久结算，再停止设备宿主。会话文本回复结束、HTTP 请求结束、设备 StopAsync 返回和 Serve 记忆排空是不同事实。停止消费不能声称远端未结算工作已经持久保存。

## 容量与恢复

单个 publication 上限为4 MiB，每个正文块最多12 KiB。4 MiB 全写最多需要 begin + 342个chunk + commit，共344个执行 operation；完整读取也需要多次 operation。须同时规划 publication 的 MaxTransfers/MaxStagingBytes/MaxPages、执行日志的 MaxOperations/MaxStoredBytes，以及 Serve 的持久事实容量。

transferId 及其终态永久保留，不能回收后复用；重开不能悄悄扩大原容量。容量不足须显式报错，不能删除旧回执求通。`GetCapacityAsync` 供宿主监控真实已占用和剩余容量；配额数字不等于模型 token 或货币预算。

原连接丢响应时查询原 transfer/执行账本，不更换请求号或重跑原副作用。更换连接/owner 后，普通客户端不自动获得旧 transfer 的写权。`AuthorizeRecovery` 仅允许可信宿主见证旧授权撤销及新连接许可后的只读 query，不能授权新的 chunk/commit。

Serve 的 `restoreMemoryPublicationBinding` 与 `reconcileObservation` 是可信维护入口，不是普通 controller 可调用的通用 HTTP API。宿主须核 owner idle、原执行账及设备持久回执摘要，并提供独立恢复证据；原 unknown 只补充事实，不由客户端自行确认成功。恢复观察期间仍拒绝新模型请求；正常可写重建须由可信宿主显式处理。

## English

The Serve/kernel runtime owns memory extraction, selection, consolidation and deletion. The Windows client executes the existing reserved MemoryPublication profile and stores its publication using the original Node-compatible three-table SQLite format. This is separate from archive/history storage and remains an explicit preview.

Configure the trusted Serve factory with `platform.memoryPublicationFor`. The ordinary CLI serve command does not enable this automatically. Source identity, deletion generations and provenance must come from trusted application records. Client configuration is not authorization. The current publication store is plaintext, uses explicit fixed capacity, retains terminal transfer receipts and does not silently migrate or clear old data.

Keep the device executor running until the server has settled its memory work. Unknown outcomes require original-operation reconciliation. A new connection does not inherit write authority over old transfers; recovery authorization permits read-only queries and server maintenance remains a trusted host responsibility. See the development record for actual tested behavior and remaining requirements.
