# .NET SDK 协议快照

事实源为 `tansr-cli/doc/rfc/sdk2-ext-v1.schema.json`。此目录按原字节锁定 schema、既有 wire 金样与参考编码器；`manifest.json` 记录来源提交、路径、字节数及 SHA256。引用材料不能作为第二套 wire 规范编辑。源仓其它文件有新提交不会单独令快照失效；改变协议文件必须先会签并重新核对消费者。

`scripts/check-contract.ps1` 核本地指纹；指定 `-SourceRoot J:/tansr/tansr-cli` 另核上游对应文件。脚本只检查，不更新、下载或改写任何文件。运行时具名校验使用嵌入的同一 schema；网络 JSON 不可提供 schema、程序集或类型名。公共指纹在 `WireContract`，公共方法接受 `JsonElement`，不依赖反射 DTO 序列化。

`sdk2-wire-v1.json` 原文件同时含规范控制字节正例、非规范反例及不可重编码的原 IR。`schema-vectors.json` 中前23个端点 DTO 正例机械提取原 `sdk2-extension-schema.test.mjs` 的 `goldens`；额外边界例仅用于验证 C# 消费，不定义新合同。具名校验不证明当前身份、持久 CAS、SHA 语义或副作用已授权；这些检查继续由会话、执行与存储层负责。

SDK1 的 JSON、默认持久化和未知可选字段规则不变；SDK2 扩展显式启用。`RFC-SERVE-NET-1` 的 `terminal-services-v1` 仍未冻结，本快照不定义、不实现其候选新路由。

本批只实现 NET-01 的协议消费部分，完整功能映射、真实 HTTP/平台/AOT 消费及 NET-A01—04 仍须由原父卡统一验收；未以快照检查关闭工程卡。

## 统一 API 合同（UAPI-01，`unified-v1`）

事实源为 tansr-cli `main`（`manifest.json` 的 `apiManifest.sourceRevision` 记录具体提交；当前钉在 `64df76b2`，manifest revision 7，schemaHash `b60e77ff…bb57`）。按原字节 vendoring 四份文件，并在 `manifest.json` 的 `files[]` 登记字节数与 SHA256：

- `api-manifest.json`：`tansr-api-manifest-v1`，81 条操作（含 `approval.credential.submit` 与 `:ticketId` 占位）、11 个合同族、9 个域；revision 7 起每条操作带 `etagPath` / `expectedRevision{path,kind}`、每个族带 `requestIdPath`，生成器一并校验并产出到 `ApiOperation`（`EtagPath` / `ExpectedRevisionPath` / `ExpectedRevisionKind` / `AcceptsIfMatch` / `AcceptsIdempotencyKey`）与 `ApiRoutes.FamilyRequestIdPath`。`src/Tansr.Sdk/Api/ApiRoutes.generated.cs` 由 `node scripts/generate-api-routes.mjs` 从它生成，`--check` 逐字节比对生成物，生成物内嵌本文件 SHA256、`revision` 与 `schemaHash`，`scripts/check-contract.ps1` 核对三者与锁一致。SDK 发出的每一条路径都来自这张表（D10：无 `/v2`、`/v3/sdk2`、`/v3/terminal*` 回退）。
- `unified-v1.schema.json` / `unified-v1.golden.json`：统一响应头、错误信封（`FacadeError` / `UnifiedError`，revision 7：`detail.reason` 17 值、`limitBytes`、`precondition_failed` 允许 `refresh`）、事件包络（D18 七键 `{contract,eventId,domain,type,cursorSet,terminalStatus,raw}`）、部署发现 `Capabilities` 与会话围栏 `CapabilityClosure` 的结构定义与金样（165 向量）；`tests/Tansr.Sdk.Tests/Api/UnifiedGoldenTests.cs` 逐向量回放并逐条归账（`EveryGoldenVectorIsReplayedOrExplicitlyNotConsumed`：消费 140、生成器校验对象 23、明示不消费 1），解码器不自行拒绝的反例须在该测试的 `Divergences` 登记原因。
- `canonical-cross-vectors.json`：RFC-UAPI-1 §4.1 的 127 条 canonical 跨实现向量；`CanonicalCrossVectorTests` 以严格控制入口 `WireJson.DecodeControl` 与规范化入口 `Parse`+`EncodeControl` 双路比对，分歧登记在 `NormalisingDivergences`。

`session-compatibility.json` 新增 `apiRequests`（C# SDK 在 `/api` 统一入口的请求序列）；`requests` 仍是钉在 `sourceRevision` 的 Node sdk2 客户端旧入口序列，供 `scripts/check-session-compatibility.mjs` 使用。
