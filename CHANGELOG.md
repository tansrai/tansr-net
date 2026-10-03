# 变更记录

版本号与 NuGet 发布节奏另由发行流程决定；本文件只按任务登记行为变化与迁移要点。

## 未发布（UAPI-01 / U8-NET，2026-10-03）

### 统一合同对齐 revision 7

- `contract/` 四件套逐字节钉到 tansr-cli `main` `64df76b24bfd3bc62850f4b04093553708b7708c`：`api-manifest.json`（revision 7，schemaHash `b60e77ffcbf08d985a993dbdbd4cf610f12f7c7e70f090aee5ff8d523f70bb57`，81 操作）、`unified-v1.schema.json`、`unified-v1.golden.json`（165 向量）、`canonical-cross-vectors.json`；SHA 见 `contract/manifest.json`。
- `ApiRoutes.generated.cs` 重生成：新增 `ApprovalCredentialSubmit`（`POST /api/approvals/:id/credential`），`SessionPermissionDecide` / `SessionQuestionAnswer` 改用 `:ticketId` 占位；`ApiOperation` 新增 `EtagPath` / `ExpectedRevisionPath` / `ExpectedRevisionKind` / `AcceptsIfMatch` / `AcceptsIdempotencyKey`，`ApiRoutes.FamilyRequestIdPath(family)` 给出族级 `requestIdPath`。
- 错误信封解码器对齐 revision 7：`detail.reason` 17 值、`detail.limitBytes`、`precondition_failed` 允许 `refresh`；三头拒绝（携 `requestId` + 请求头 reason）归类为 `UnifiedError`，不再误判为门面自答。

### D19：统一码主位的公开异常模型（破坏性语义变化，类型不删）

`UnifiedApiException` 的公开面改为统一码 + `RetryAction`；族码降为 `Detail.DomainCode`。既有 `TansrException` / `TansrHttpException` / `CacheContinuityException` 等族类型保留为派生或桥接类型，不再是主位。`Code` 对 `/api` 失败恒为 19 统一码之一。

| 旧（族码主位） | 新（统一码主位） |
| --- | --- |
| `catch (TansrHttpException e) when (e.Code == "session_ended")` | `catch (UnifiedApiException e) when (e.Code == UnifiedErrorCode.Gone && e.Detail.DomainCode == "session_ended")` |
| `e.DomainCode` / `e.DomainStatus` / `e.DomainRetryAction` 作主读 | `e.Code` / `e.StatusCode` / `e.RetryAction` 主读；族事实 `e.Detail.DomainCode` / `e.Detail.DomainStatus` / `e.Detail.DomainRetryAction`（旧属性保留为桥接，无族事实时回落统一值） |
| `e.Code == "precondition_failed"` ⇒ 重发现 | `e.RetryAction == UnifiedRetryAction.Rediscover`（`RequiresRediscovery`）或 `Refresh`（`RequiresRefresh`，If-Match 陈旧） |
| 按族词 `backoff` 自行重发 | `UnifiedRetry.Advice(e).Replayable` 为真时 `UnifiedRetry.RetrySameRequestAsync(client, op, sameOptions, e)` 单次重放；`result_unknown` / `commit_unknown` 永不重放 |
| `CacheContinuityException.Code == "binding_rotated"` | `Code == UnifiedErrorCode.StaleGeneration && DomainCode == "binding_rotated"`；`Unified` 保存原统一异常 |
| 族直通 `TansrHttpException` | 不变；`UnifiedRetry.Advice` 把族词映射到统一动作（`Source == "domain"`），无动作位时 `Stated == false` |

新增类型：`UnifiedErrorCode`、`UnifiedRetryAction`、`UnifiedErrorReason`、`UnifiedErrorDetail`、`UnifiedRetryAdvice`、`UnifiedRetry`。

### 三头与通用调用入口

- `TansrClient.CallAsync(ApiOperation, ApiCallOptions)`：任意非流式 manifest 操作的通用 `/api` 入口，路径只出自 `ApiRoutes`；`Idempotency-Key` / `If-Match` / `deadline` 先按操作事实本地校验再发出（`invalid_idempotency_key`、`invalid_if_match`、`if_match_not_applicable`、`invalid_deadline`、`deadline_exceeded`），`ApiCallResult.ETag` 只接受强校验子。
- `UnifiedRetry.RetrySameRequestAsync`：同键同体仅重放一次，默认 30 s 等待预算，deadline 不延长（`not_retryable` / `retry_key_missing` / `retry_after_exceeds_budget` / `deadline_exceeded`）。
- `TansrClient.GetCapabilitiesAsync()` / `GetCapabilityClosureAsync(sessionId)` 与 `UnifiedCapabilities` / `UnifiedCapabilityClosure` 严格解码器：禁 URL 导航字段，`closureId` 本地复推；围栏外操作的 `capability_unavailable` 原码上浮，无任何降级。

### 测试

- 金样 165 向量逐条归账（消费 140 / 生成器校验对象 23 / 明示不消费 1）；`closure-partial-mixed` 驱动 77 围栏操作的能力交集重放；19 码 × 绑定状态 × retryAction 对照 schema `allOf`；三头发出、本地拒绝、重放、ETag 回传、409 / 408 / 412 解码。
