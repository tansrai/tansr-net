# 设备端自动记忆接入（开发候选）

本接线属于原 NET-04 的显式 preview，使用 Serve 拥有的 `terminal-services-v1 / 2026-09-26.candidate-7`，不改变 SDK1 历史/记忆和现有档案存储。当前实现与验收状态以[开发记录](development.md)为准；本页不代表已发布或完整 NET-A14 已通过。

## 职责与介质

Serve/kernel 继续决定何时提取、选取、固化、更新和删除记忆。C# 负责执行已授权的 `TansrTerminalMemoryPublication` 保留 profile，将完整记忆 publication 耐久保存在应用指定的 Windows SQLite 文件。该 profile 使用 `MemoryPublication` 权限及固定定义摘要，不进入模型工具列表，不借 Shell 权限执行，也不允许任意路径或命令。

本地 `SqliteMemoryPublicationStore` 沿用 Node 的 `terminal-memory-publication-sqlite-v1` 格式：`metadata`、`publication`、`transfers` 三表，WAL/FULL 事务，`head/read/begin/chunk/commit/query` 六动作。阶段正文完整预留，最终 publication 的 CAS 与 transfer 终态在同一事务中落下。它是专用 publication 介质，不是 Archive/历史/材料响应箱；不能通过将旧档案数据库重命名来迁移。

不提供 `KeyProvider` 时仍显式选择原明文格式，需使用受限用户目录；原 Node 明文互操作保持。设置 `SqliteMemoryPublicationOptions.KeyProvider` 可选择独立的 `terminal-memory-publication-encrypted-net-sqlite-v1` 格式。该格式不承诺与 Node 或其他生态的加密数据库直接互开；Serve wire、六动作与原回执保持相同。`EncryptedBody` 表示此实例是否启用正文加密，不代替 `AtomicDurablePublication`。

加密复用 `IArchiveKeyProvider` 的 32 字节独立密钥副本与固定 `KeyId`；可直接注入自定义密钥来源，或使用 `CurrentUserDpapiArchiveKeyProvider.Create/Open`。每次操作短暂读钥并清零副本，同 ID 在实例存续期间换钥明确失败。publication 和 staging 正文在传给 SQLite 前完成 AES-256-GCM 加密；DB、WAL/rollback journal 不收到正文。每个密文为 12 字节随机 nonce、16 字节 tag 与正文。终态 transfer 不保留旧正文，只保存认证空正文的 28 字节见证。额外 `encryption` 表保存认证空正文 key-check，空库也会核对原密钥。

AAD 绑定格式、KeyId、完整介质 identity、物理文件身份和固定容量，以及 publication etag 或 transfer 的完整 owner/request/status/received/etag。身份、请求、摘要、状态、长度及路径相关元数据仍可见；这不是整个 SQLite 文件加密。逻辑 staging 配额按明文字节计算，物理 `MaxPages` 仍覆盖密文开销，终态见证仍受原 `MaxTransfers` 限制。

Create 不覆盖已有文件；Reopen 按原身份、格式、容量、密钥及正文认证复核，不把损坏、撤权或缺失文件视为空库重建。错钥、篡改、截断或错 AAD 拒绝返回正文；缺钥、KeyId 改变、运行中换钥和密钥回调后的主体/授权变化均明确失败。已经 COMMIT 后发生钥/身份失败仍返回 `reconciliation_required`，重开后查询原 transferId，不重建新请求。

可信宿主公开接线示例（identity、readCurrentScope 和路径必须来自现有可信配置）：

```csharp
var key = CurrentUserDpapiArchiveKeyProvider.Open(keyPath, "memory-key");
using var store = await SqliteMemoryPublicationStore.OpenAsync(new SqliteMemoryPublicationOptions
{
    EnablePreview = true,
    Path = publicationPath,
    Mode = StorageOpenMode.Reopen,
    Identity = identity,
    ReadContext = readCurrentScope,
    MaxTransfers = 4096,
    MaxStagingBytes = 8388608,
    MaxPages = 8192,
    KeyProvider = key,
});
var tool = new WindowsMemoryPublicationHost(store, enablePreview: true, requireEncryption: true).CreateTool();
// Register tool with the existing WindowsExecutorBackend and stop execution before closing store.
```

首次初始化必须另行显式 `Create` 密钥与 Store，失败时保留残片并处理真实错误，不用“文件存在/打开失败”猜测可覆盖或换钥。示例 JSON 现在必须显式提供 encryption 配置，双库与离线读取共享原 DPAPI key；旧明文路径须先分别显式迁移，不能自动升级或换钥。

**显式迁移与换钥**：已提供 `source.CopyToEncryptedAsync(destinationPath, stagingPath, newKeyProvider, cancellationToken)`。它只从已打开并完整审计通过的源复制到新加密库，支持明文 v1 → 加密与旧钥 → 新钥。身份、容量上限、所有 transferId/owner/请求/终态/游标及 publication 均原样保留；新物理身份与新 KeyId 重新进入 AAD，不改 Serve wire、绑定或请求号。

`stagingPath` 和 `destinationPath` 必须是同目录内两个不同的新路径，不能等于源路径，也不能已有库或 sidecar。先停止业务写入，再在源实例保持打开的期间执行：

```csharp
await source.CopyToEncryptedAsync(newPublicationPath, migrationStagingPath, newKeyProvider);
// Success: close the old source and explicitly reopen newPublicationPath with the same identity/limits and newKeyProvider.
```

复制在暂存库单事务中完成；逐项重新解密比较后完整审计，关闭 SQLite、确认 sidecar 已排清后，持禁止写入的文件句柄重新以不可变只读模式逐项认证及比对；保持该保护直至核对文件身份和 SHA256，最后以 Windows 句柄重命名且禁止覆盖目标。目标路径只在校验完成后出现；原源未写入，当前源实例和宿主配置不会自动切换。认证、容量、取消或目标竞争失败时保留源、已有目标及具名暂存文件，不自动清库。失败重试应保留原暂存证据并指定另一新暂存路径；不能对残片调用 Create 或换请求号求通。若发布已完成后的最终授权检查失败，返回 `reconciliation_required`；恢复原授权后按已知目标路径、原身份和新钥显式 Reopen/查询对账，不重复覆盖。

原地轮换和自动迁移仍被拒绝：向旧明文库传钥、向加密库漏钥/换 KeyId、传入未知格式不会暗迁或降明文。新路径已成功后也保留原源供宿主按恢复策略处理。迁移入口不执行跨进程业务接管或物理断电承诺；完整 Serve、安装物及原生 UI 运行仍须按原 PST 卡统一验收；JSON Demo 已显式接入 DPAPI 双库加密。

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

Configure the trusted Serve factory with `platform.memoryPublicationFor`. The ordinary CLI serve command does not enable this automatically. Source identity, deletion generations and provenance must come from trusted application records. Client configuration is not authorization. Without `KeyProvider`, the store retains the original plaintext format and Node interoperability. Explicit `KeyProvider` selects the local `terminal-memory-publication-encrypted-net-sqlite-v1` format using the existing 32-byte `IArchiveKeyProvider` contract, including the Windows CurrentUser DPAPI provider. It encrypts publication and staging bodies before SQLite sees them. Nonces and GCM tags add 28 bytes per body; terminal receipts keep an authenticated empty body, not the former content. Logical staging capacity excludes that overhead; the physical page limit still includes it.

AAD binds the fixed storage identity, format, key ID, physical identity and limits, plus publication etag or all transfer request/owner/status/cursor/etag metadata. Those metadata remain visible; this is body encryption, not full-database encryption. `EncryptedBody` reports the actual instance choice. Keys are never stored in the database or logs. Wrong or unavailable keys, tampering, truncation and unknown formats fail without returning plaintext or recreating the source. Host authority is checked again after key callbacks. A key failure after COMMIT remains `reconciliation_required`; reopen with the original key and reconcile the original transfer.

The public C# example above is the encrypted integration path. The example JSON now requires an explicit DPAPI encryption configuration for both the publication store and its execution journal. `source.CopyToEncryptedAsync(destinationPath, stagingPath, newKeyProvider)` explicitly copies a verified plaintext or encrypted source to a new encrypted file, including every publication, original transfer, terminal receipt and partial cursor. Identity and capacity limits stay fixed; physical identity and key ID are rebound in AAD. Both output paths must be new and in the same directory. Stop business writers first. The staging transaction is decrypted and compared before commit, then closed and checked for unresolved sidecars. A file guard denies writes continuously while an immutable read-only SQLite connection authenticates and compares every fact again, through the final SHA256 and file-identity checks and no-replace handle rename. The source remains unchanged; switching host configuration is explicit.

Failure preserves the source, any existing target and named staging file. Preserve that evidence and retry with a new staging path. If the final authority check fails after publication, the result is `reconciliation_required`: reopen the known destination with its original identity and new key, and reconcile; never overwrite it. In-place rotation and automatic format upgrades remain unsupported. Supplying a key to legacy media never silently migrates it, and missing keys never downgrade encrypted media. Encrypted database files are not claimed to interoperate with another ecosystem's private file format. Full Serve, package, CLR4 runtime and Demo consumption remain separate acceptance work.

Keep the device executor running until the server has settled its memory work. Unknown outcomes require original-operation reconciliation. A new connection does not inherit write authority over old transfers; recovery authorization permits read-only queries and server maintenance remains a trusted host responsibility. See the development record for actual tested behavior and remaining requirements.

## 执行 journal 与双库准入 / Execution journal and host admission

`SqliteExecutorJournalOptions.KeyProvider` 显式选择 `sdk2-execution-encrypted-net-sqlite-v1`，同时选 `CompactCompletedReceipts` 则使用独立 `sdk2-execution-encrypted-net-sqlite-compact-v1`。原两种明文格式及旧构造保持可用，不在原路径隐式升级。完整 canonical operation 和 receipt（包括 chunk 参数、read 返回、工具正文）在 SQLite 接收前 AES-256-GCM 加密；格式/密钥 ID/原身份/容量/物理文件身份与记录种类、operationId 纳入 AAD。数据库、WAL 和回滚日志不接收这些明文。operationId、记录数量、长度及固定元数据仍可见；不声称整文件加密。

密文以规范 Base64 存在原 TEXT 列，密文编码长度计入 `MaxStoredBytes`，物理页仍受 `MaxPages` 限制。每个 pending 保留足够存放原最大 receipt 的编码空间；compact 只在终态事务内释放未用预留，永久保留 operation 和完整 receipt。Pending/unknown、原 digest、canonical 及 ACK 时点不变。

`new WindowsMemoryPublicationHost(store, enablePreview: true, requireEncryption: true)` 要求实际 store 提供 `IEncryptedMemoryPublicationStore.EncryptedBody`；所创建工具把要求交给实际 `WindowsExecutorBackend`。原 `ExecutionHost` 在注册、认领或写 journal 前，通过可选 `IExecutionJournalRequirements` 校验真正注入的 `IExecutorJournal` 是否具备 `IEncryptedExecutorJournal.EncryptedAtRest`。无加密能力即 `ENOTSUP`；无法用另一个未参与执行的加密 journal 代签。自定义适配器和 backend 包装器必须如实提供并转发这些本地保证，不添加任何 wire 字段。

```csharp
using var journal = await SqliteExecutorJournal.OpenAsync(new SqliteExecutorJournalOptions
{
    Path = executionJournalPath, Mode = StorageOpenMode.Reopen,
    ApplicationScopeId = applicationScopeId, EndUserId = endUserId, ExecutorId = executorId,
    ReadContext = readCurrentScope, KeyProvider = key, CompactCompletedReceipts = true,
});
var memoryHost = new WindowsMemoryPublicationHost(store, enablePreview: true, requireEncryption: true);
// Register memoryHost.CreateTool() in the original WindowsExecutorBackend and inject this journal into ExecutionHost.
```

停执行后可对 journal 调用同形 `CopyToEncryptedAsync(destinationPath, stagingPath, keyProvider)`。显式新路径迁移/换钥保留原源字节、身份、容量、compact 选择及全部 Pending/unknown/终态事实，最终认证比对与禁止覆盖发布沿用 publication 的路径设施；不改 source/binding。加密编码超出原配额时拒绝迁移并保源，不提高配额或删回执。迁移后宿主显式更换路径与钥。双库分别迁移，不承诺跨库原子切换；两份成功回执齐备前保留原路径并停止执行。JSON 配置见 [Demo](../examples/Shared/device-memory.md)，在线及离线读取均显式使用原 DPAPI key 文件，不因缺钥重建。

The journal encrypts the entire canonical operation and receipt before SQLite receives either, including publication chunk parameters and read results. Its explicit encrypted formats preserve the original plaintext and compact formats. AAD binds fixed metadata, physical identity, record kind and operation ID; operation IDs and metadata remain visible. Base64 ciphertext and the full encoded receipt reservation count toward the existing byte quota. Compact completion releases only unused reservation, retaining every permanent operation/receipt and the original pending/unknown semantics.

`requireEncryption: true` verifies both the publication store and the actual journal injected into the original ExecutionHost before registration or claim. Custom adapters and backend wrappers must truthfully implement/forward the optional local capability interfaces. No protocol, canonical digest or ACK behavior changes. The demo's explicit DPAPI key configuration is passed through both memory-only and combined terminal hosts; offline memory reads reopen that same key.

`journal.CopyToEncryptedAsync(destinationPath, stagingPath, keyProvider)` copies to new paths and supports explicit rotation while preserving original bytes and all facts. Both paths must be new and share a directory. Stop execution first. Quota, key, cancellation or path failures preserve the source and any named staging file. Post-publication failure remains `reconciliation_required`; reopen the known target and reconcile without overwriting it. Publication and journal migration are separate operations, not a distributed transaction. Switch the stopped host only after both verified copies exist. Local tests and cross-target builds do not substitute for real Serve, CLR4/UI, operating-system or release evidence.

## 封存包实链入口 / Sealed-package consumer

`tests/Tansr.Sdk.IntegrationTests/ServeEncryptedMemoryPublicationTests.cs` 与 `scripts/packed-memory-integration.mjs` 提供专用原协议实链：共享封存 Serve 包产生实际 operation，Windows Console/公开宿主执行，publication 与实际 execution journal 均使用 CurrentUser DPAPI 供钥。正文和其 chunk Base64 的磁盘探针同时涵盖两库及活跃 sidecar。查询保持原请求身份，显式进程重开保留原完整终态；故障注入落在真实 chunk COMMIT 之后，unknown 与在飞停止仍保原键，异体/新 owner 写入被拒。

此入口不依赖私有 kernel idle 钩子、不更改冻结协议、不复用活动 CLI 源码；目标测试用独立系统临时根并记录清理。它补充此前 extraction/recall 与存储迁移的测试，不替代其它平台、CLR4/UI、物理掉电或整版发布验收。执行步骤见 [集成测试说明](../tests/Tansr.Sdk.IntegrationTests/README.md)。

The dedicated packed-host consumer drives real Serve operations into the original Windows device host and encrypted SQLite stores. It covers actual Console restart, original-key response loss, committed-chunk uncertainty, in-flight stop, authorization rejection and handle release. Fault wrappers preserve the existing Store and execution contracts. This Windows chain is additional evidence; it does not certify other platforms, native UI variants, power-loss behavior or a release.
