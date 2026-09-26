# SDK2 ACK recovery v1：NET-04 交接合同

本扩展修复终端在 revision 5 耐久接收后，源 `finish-run` 推进 revision 6 导致原 pending ACK 永久 CAS 冲突的问题。它属于原 SRV-04/05、NET-04 的恢复接线，不改 `sdk2-ext-v1` ACK、MutationReceipt、archive-sync-v1 或旧接收器六方法。

事实源为 [独立 schema](sdk2-archive-recovery-v1.schema.json)，SHA256：`f530de1096b4f5d56ea688b7f2cec9ae66deb7d3719db85ab1f48287d3bd7ad4`。Node 参考实现为 `packages/api-client/src/sdk2/receiver-sqlite.ts`；新增 DDL 和状态行为在本文固定。跨运行时共同消费 [独立金样](sdk2-archive-recovery-v1.golden.json)，金样由真实 kernel source ACK 和 Node SQLite 接收/同步路径生成，不把本地数据库物理身份当跨运行时授权。

金样最终 SHA256：`614adf2088585a6213d010acde29598d40757d0f2d7b4608f1670ccbde3a5553`，36475 字节。金样 `wire.request.canonical` 为 995B，SHA256 `489fa46f954c3c51d7f675c4fd8908cdfcfe4c809fce111fddd1f33b8ed739a9`；`wire.response.canonical` 为 2231B，SHA256 `36938514f93a8e7091507b66afda9b2f64c97fc38b9f7585063d0e9108dcbc7b`。这些是字段内规范字符串的 UTF-8 原字节，不包括外层金样文件缩进或结尾换行。

## 1. HTTP 与不变量

`POST /v3/sdk2/bindings/{bindingId}/archive/ack-rebases`，原 Bearer 身份及原应用/用户/binding 授权。必须 `Content-Type: application/json`；无 query。正文为规范 UTF-8 控制 JSON：对象键按原控制编码排序，不加空白，字符串按 JSON 原 Unicode 值编码；重复键、BOM、替代数字和未知字段拒绝。整数序号仍是规范十进制字符串，不经过浮点数。

请求字段恰为 `protocol="sdk2-ext-v1"`、`bindingId`、`previous`（原 ArchiveAckRequest）及 `request`（由调用方先耐久保存的恢复 RequestIdentity）。200 响应恰为这四字段及 `next`（原 ArchiveAckRequest）、`receipt`（原 MutationReceipt）。HTTP 不接收可序列化 idle proof。

- `request.operationEpoch == previous.request.operationEpoch`，`request.requestId != previous.request.requestId`。仅本 v1 当前仍活跃的同 epoch 恢复；跨 epoch 不猜测新的 issuedAt/expiresAt。
- 响应 `previous`、`request` 与请求精确相等；`next.request == request`；`next.expectedRevision > previous.expectedRevision`。将 next 的 request/expectedRevision 换回 previous 后，两对象须精确相等，包括数组顺序。
- 源确认原 ACK 从未受理，复核原记录/工件完整见证、所需副本、scope/source/代际、当前权限。然后在原 driver 排他读、原 gate idle lease、原 control 无 fence 的边界内，将真实 next ACK、receipt 和 previous 映射同一控制/companion 事务提交。
- 已完成映射按原请求重放，即使其 expectedRevision 随后来运行过时；仍须当前权限。不同恢复键不能再次映射同一个 previous。200 不触发模型、重新 Receive、正文复制或自动清退。
- 若 previous 已先完成，rebase 返回 request_id_conflict；使用旧 `GET /v3/sdk2/operations?protocol=sdk2-ext-v1&operation=archive-ack&bindingId=…&operationEpoch=…&requestId=…` 查询原 receipt，并调用原 confirm。不能伪造 previous 的 completed receipt。
- 失回只重发持久固定的同一恢复请求。客户端不得读最新 revision 后自行修改 pending ACK、换 requestId 或重执行模型。

单 ACK/receipt 保持原 controlBytes。恢复请求外包帽 `controlBytes + 1024`，响应外包帽 `2 * controlBytes + 4096`；硬上限分别 263168/528384。旧端点和旧 DTO 帽不变。最长合法字段的实际规范 JSON：ACK 67460B、请求 67944B、响应 136202B、receipt 779B。

## 2. 显式介质版本与迁移

| 原格式 | 恢复格式 | 显式入口 |
|---|---|---|
| sdk2-terminal-sqlite-v1 | sdk2-terminal-recovery-sqlite-v1 | openSdk2RecoverableSqliteArchiveReceiver |
| sdk2-archive-sync-sqlite-v1，source | sdk2-archive-sync-recovery-sqlite-v1 | openSdk2RecoverableSqliteSyncArchiveStore |

入口 mode 仅 `create`、`reopen`、`migrate-v1`。`migrate-v1` 只接受精确旧格式；新库重开用 reopen，不自动探测或降级。旧工厂拒绝恢复格式。cache、密文及历史只读库不在本恢复格式内，不能移除其守卫。

迁移步骤固定：按原身份、路径/物理文件、当前权限和独占所有权打开旧库 → 完整原 schema、记录摘要链、工件、operations/checkpoints、计费和 foreign-key 审计 → 一个 `BEGIN IMMEDIATE` 事务内执行以下新增 DDL、只改 metadata.json.format、重算并核 logical_bytes → 再核当前授权后 COMMIT。正文、原 ACK、checkpoint 和 head 不在迁移时改写。Node metadata 中原 physical/pageSize/identity/limits/maxPages 原值保持；C# 保留其既有受信物理身份规则，不从金样复制 inode/path。

```sql
CREATE TABLE ack_rebases (request TEXT PRIMARY KEY, previous_request TEXT NOT NULL UNIQUE, intent TEXT NOT NULL, result TEXT, original_receipt TEXT, reserve BLOB NOT NULL) STRICT;
CREATE UNIQUE INDEX ack_rebases_pending ON ack_rebases((1)) WHERE result IS NULL AND original_receipt IS NULL;
```

以上为新增 DDL，[可下载原文](sdk2-archive-recovery-v1.sqlite.sql)由真实 sqlite_master 同金样再生；原 operations、state 及 sync checkpoints 的表结构和唯一约束保持原样。Node schema 审计比较 sqlite_master 的 SQL 原文本，实际执行上述语句时不将末尾分号计入 sqlite_master 字段。并非允许外来路径或任意 SQL。

COMMIT 抛错按原不确定事务规则标 `reconciliation_required`：当前连接只允许关闭、再显式重开核真实库。回滚确定成功则保留旧格式可读。不得在不确定提交后再次自动 migrate 或删除文件。

## 3. ledger 字段与状态

`C(x)` 表示原规范控制 JSON，`B(x)` 表示字符串 x 的 UTF-8 字节数。所有 JSON 列须规范编码，request 键是整个 RequestIdentity 对象的 C 值，不是单独 requestId。

| 列 | 内容 |
|---|---|
| request | C(调用方固定的新 RequestIdentity)，主键 |
| previous_request | C(previous.request)，唯一且永久保留 |
| intent | 完整 C(AckRebaseRequest)，永久原 ACK 与恢复意图 |
| result | null 或完整 C(AckRebaseReceipt)，不能只存 next |
| original_receipt | null 或 previous 真正先受理时的原 C(MutationReceipt) |
| reserve | 零字节 BLOB，预留终态及迁移增量的逻辑/物理空间 |

无新增 state 字符串列，状态由列派生：

| 派生状态 | result | original_receipt | 原 operations/checkpoint |
|---|---|---|---|
| prepared | null | null | 仍为 previous，receipt=null，state.pending=previous_request |
| rebased | 完整真实响应 | null | 唯一当前批次迁到 next；真实 next receipt；state.pending=null |
| original-confirmed | null | 原真实 receipt | 保持 previous 键，原真实 receipt；state.pending=null |

两终态字段同时非 null 非法；最多一个 prepared。恢复 request 或 previous_request 不可再用于其它 Receive/恢复，也不可构造 old→next→next 链。旧行不能被删除或改写来释放 requestId。ledger 行数 ≤ maxRecords，字节计入 maxStoredBytes。

`prepareAckRebase(request)` 在独立本地事务内保存完整 intent 和预留，返回时仍保留原 pending ACK。相同 request 返回原 intent；不同 request 不得替换现存 prepared。`pendingAckRebase()` 定点读取唯一 prepared。调用端必须成功 prepare 后才调用 HTTP。

`confirmAckRebase(response)` 在原当前身份下验证 schema、上节所有等式、scope 对应的真实 semanticDigest 及 archive-ack completed receipt。一个本地事务开启 `PRAGMA defer_foreign_keys=ON`，保存 result，将原 operations 的主键/ack/receipt 迁到 next，sync 时迁当前 checkpoint，清 state.pending 并重新核计费/FK。只改一个既有批次，不增第二条同 coverage 的 operation/checkpoint。records/artifacts/head 不变。重复同响应幂等；不同响应拒绝。

如果 prepared 后旧 ACK 先完成，原 confirm 保存 original_receipt，仍保留 intent，按旧操作清 pending；后续 confirmAckRebase 必须拒绝。两种结局都可关闭重开审计，不能把旧 ACK 的缺 receipt 改成假 completed。

## 4. 容量预留精确算法

旧 operations 仍保持 `B(receipt JSON 或空) + length(reserve) = 4096`。

对 intent 与候选 expectedRevision=r：`k=B(C(intent.request))-B(C(intent.previous.request))`，`v=B(r)-B(intent.previous.expectedRevision)`；普通库迁移字节差 `D(r)=2*k+v-B(C(intent.previous.request))`，sync 库再加 `2*k+2*v`（checkpoint 键、request 及 binding/status 两处 revision）。

令 `M="9223372036854775807"`，`P=B(C({...intent,next:{...previous,request:intent.request,expectedRevision:M},receipt:{}}))+4096+max(0,D(M))`。prepare 为 ledger 保存 zeroblob(P)，该 P 必须 ≤1048576，整个库须先满足逻辑/page/记录帽。

prepared 保留 P；rebased 后 ledger reserve 长度为 `P-B(C(result))-D(next.expectedRevision)`；original-confirmed 为 `P-B(C(original_receipt))`。审计分别恢复等式。实际 result 及原 receipt 均替换预留；operations 自有 receipt 也只消费原 4096 预留。负的 D 表示迁移释放空间，可转成 ledger reserve；不增加 prepare 后逻辑总占用。不能等服务成功后再发现长新 request 无本地确认容量。

## 5. sync-page 证明

恢复后的 `syncPage` 仍是原 `format="archive-sync-v1"`，不添加 ledger 字段。其 ack=next、receipt=真实 next receipt；checkpoint.request=next.request，checkpoint.binding.revision 和 checkpoint.status.revision 均为 next.expectedRevision，**不是 receipt.revision**。checkpoint.page、records、artifact refs、scope/source/target/generations、epoch issued/expires、原 status 水位等其余字段全部保持原快照。

原 operations↔checkpoints 一一对应、from_sequence 唯一以及 from/through/摘要链保持不变。下游 cache 不能靠单纯 schema 通过接受：用当前读取权限/retentionRevision、同 identity、连续前驱、实际 raw bytes SHA、record/payload digest 重建批次 ACK，与 sync.ack 精确比较，再核 receipt 的原 scope semanticDigest。墓碑须有当前已核准的 retention 事实，不能凭响应省略正文。已确认的 next ACK 可以由旧 archive-sync-v1 cache 接收；cache 不生成新 source ACK，也不自行调用 rebase。

## 6. 成功、错误与金样

金样 success 带原 rev5 ACK、调用方固定请求、next rev6 与真实 receipt rev7，并带 prepared/rebased ledger、最终 sync-page、原 body base64/摘要及规范 JSON 字节/hash。生成器与校验测试核源 kernel 真实回执、Node SQLite 迁移和旧 cache 消费，不拿示例字段替代 wire/语义验证。

错误沿原 SDK2 ErrorResponse，不新造错误 DTO。响应 message 等于 code，不回显路径/原文；调用方看 status/code/retryAction，不匹配中文或日志。已解析请求时 requestId 为新 requestId；解析前错误可能为服务生成的关联 ID，不能当幂等 requestId。

| 场景 | HTTP / code | retryAction |
|---|---|---|
| 格式、未知键、不规范 JSON | 400 / invalid_request | none |
| 无认证 / 无该 binding 权限 | 401 / unauthorized，403 / forbidden | none |
| 原 runtime 非 idle、revision 尚未推进或并发 CAS 失去 | 409 / binding_conflict | none |
| 同恢复键异意图、同 previous 换新键、previous 已受理 | 409 / request_id_conflict | none；查原操作 |
| 代际变化 | 409 / stale_generation | none |
| 原 epoch 已过期/非当前、跨 epoch 请求 | 503 / epoch_unavailable | same-request；不意味着可以换 epoch |
| 必需副本/依赖尚未满足 | 503 / source_unavailable | query-status |
| 控制/配额不足 | 413 / payload_too_large，429 / capacity_exceeded | none / same-request |
| 旧宿主未装恢复端口 | 422 / unsupported_capability | none |
| 未能归类的受信宿主/提交未知异常 | 500 / internal_error | query-status |

429/503 的 Retry-After 是 5 秒；它不是自动换键或重复模型许可。宿主撤权回调若以未分类异常拒绝，仍可能落 internal_error，而不是承诺所有撤权都返回 403；两者均不能继续消费或推断操作未提交。

生成与检查入口：`node --import tsx scripts/sdk2/generate-archive-recovery-golden.mts [--check]`；`node --import tsx --test tests/sdk2/archive-recovery-golden.test.ts`。独立 schema 原有生成检查仍为 `node scripts/sdk2/generate-archive-recovery-wire.mjs --check`。
