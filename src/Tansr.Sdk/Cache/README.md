# 私有逻辑缓存候选消费者

此目录只实现 `tansr-cli@027de7e2` 已有的 Serve HTTP 消费面，所有类型均为 `internal`，还不是稳定 NuGet API。原 RFC 标明 `private ... candidate v0.2`、`Unreleased independent SDK2 contract`；调用方必须显式 `new CacheClient(client, enableCandidate: true)`，并提供可信 `PrincipalProvider` 和 `ExecutionScopeProvider`。Serve 会签前不得把这个实现描述为已冻结公开合同。它不会改 SDK1 默认会话合同，也不会自动打开缓存或调用付费模型。

`Contract/sources.json` 记录完整来源修订、原文件路径、字节数与 SHA256。原 `027de7e2` 的七份源码保留，旧schema另存 `sdk2-cache-v1.baseline.schema.json`；当前嵌入schema来自 Serve 独占树 `cli-SRV-01-terminal-services`，基点 `b2cd6641e1886d9e74439e3b7623be57bc3247ba` 加未提交的错误状态修正，SHA256 `88e7941861fab1a6380b9226e3a42adb8c872efdf720dc36e92ffa42da43d818`。它仍是未发布候选，不能冒作原 `027de7e2` 的冻结schema。路由、两跳账本、provider DTO 与控制编码器原字节未改，既有 `contract/sdk2-ext-v1` 快照保持不变。局部 `CacheSchema` 沿用本仓现有有界 schema 词表实现，加载独立资源，避免拿根 `anyOf` 接受不属于终端的 gateway/host DTO。

## 消费流程

1. `DiscoverAsync` 读取实际 `serve-cache` 能力和服务端 epoch。缺能力、失效 epoch、错误 audience 均不能受理新关系。
2. `PrepareOpenAsync` 支持原 `new/import/resume/fork`，`PrepareRenew/Rotate/Close/RebindAsync` 支持原操作。只生成客户端请求关联号；缓存组、逻辑授权、可信历史见证、projection 与 provider exchange 均由 Serve/kernel 管理。
3. 调用 `ExportOriginalRequest` 将完整原文及 `Owner` 存入宿主可信、受保护的存储，然后 `SubmitAsync`。不在日志中输出票据、原文或回执。`CacheTicket` 与 `CacheReceipt` 的字符串展示固定脱敏。
4. 副作用结果未知时，先 `GetOperationStatusAsync`。此 GET 可以继续协调原已受理两跳事务，并非保证无内部恢复写入。SDK 不换请求号、不造新 epoch、不自动重发。
5. 崩溃后 `RestoreOperation` 只接受同一应用/用户的规范原文；已有终局时一并持久化 `ExportOriginalReceipt` 并交回恢复入口，继续比对原回执。旧 epoch、旧授权 revision 不阻碍使用当前认证查询原操作；查询只返回原不可变事实，不授予新执行权限。明确决定原文重放时才调用 `ReplayOriginalAsync`。原操作重复 `SubmitAsync` 被拒绝，原回执发生改变被拒绝。
6. 新动作使用新能力发现与服务器当前 CAS；`ReadBindingAsync` 读取当前绑定。不能把历史回执中的 `active` 解释为当前授权。

## 原合同边界与已记录差异

- 认证、HTTPS/显式 loopback、同源、重定向拒绝、取消、超时和保密均复用已有 `TansrClient` / `SessionTransport`。调用前后核对可信 app/user 与调用期间授权 revision；缓存消费者不会从 token 字符串推断身份。
- 控制体请求/响应/错误最多 65,536 字节，严格 UTF-8、重复键、ASCII 键、非负安全整数原词法。原 cache decoder 允许空白和非排序响应；请求始终规范编码。持久原请求恢复要求规范字节完全一致。
- 原 JSON schema 与实际 Serve 的错误状态差异已由 Serve 源头修正：`mapping_unavailable` 为404/409/503（404/409必须无重试/回落），`capacity_exceeded` 为429/503，`epoch_unavailable` 为409/503。当前消费者使用已 pin 新候选的完整 `ErrorResponse`，包括所有 code/status/retry/fallback 条件，并核对HTTP状态；不再跳过原 `allOf`。该修正只对齐既有运行事实，没有让候选变成稳定合同。错误不返回自由文本或请求号。
- 原 v1 `diagnostics` 经 `GatewayCacheClient.emptyDiagnostic` 只允许空页。它不提供命中或费用结论。独立 `cache-core-v1` 诊断、可信 projection、provider exchange、跨运行授权和总费用验证不由此消费者替代。
- 结构正确、票据格式正确、HMAC 字段完整都不等于本地验证了授权；只有 Serve 的当前鉴权、历史见证及账本能决定是否受理。客户端不持有 HMAC 密钥，也不自行计算“缓存命中金额”。

## 验证入口

`tests/Tansr.Sdk.Tests/Cache/CacheClientTests.cs` 覆盖现有 URL/请求体、四种 open、四种 mutation、错误归属/回执、未知副作用与原文恢复、续权后的历史查询、异步身份变化、候选显式开关、64 KiB、词法边界和真实 404 差异。统一由本轮根任务执行构建与测试；代码落盘不表示通过真实供应商费用或缓存收益验收。
