# 档案、记忆与缓存的宿主接线 / Storage integration

三种介质职责不同：档案保存原会话资料；记忆 publication 保存 Serve 决定的自动记忆；缓存连续性保留逻辑分组及不透明票据。它们都不在客户端重新实现上下文选择、记忆决策、权限或计费。

## 档案自动接收与按需材料

`ArchiveSessionHost` 组合原 `ArchiveTransferSession`、`ArchiveRecoverySession` 和 `MaterialSource`。宿主只配置可信身份、本地介质与事件游标；SDK 自动消费原 SSE、耐久接收后 ACK、处理 `material.request` 并在断线后查询原操作。

```csharp
var archive = new ArchiveClient(client);
await archive.GetCapabilitiesAsync(ct);
var host = new ArchiveSessionHost(archive, archiveStore,
    new ArchiveSessionOptions {
        Identity = identityFromCurrentBindingAndStatus,
        ReadContext = trustedScope.Read,
        MaterialOutbox = durableMaterialOutbox,
        ReadEventCursorAsync = cursorStore.ReadAsync,
        SaveEventCursorAsync = cursorStore.SaveAsync
    });
var observation = host.ObserveAsync(lifetime.Token);
await host.Ready; // 已验证原 SSE 响应头及当前 scope，不能用 sleep 代替
var transfer = await host.SynchronizeAsync(ct);
// transfer.ArchivedRecords == 0 表示确实没有新记录，不冒充已经迁移历史。
// UI 只投影会话。关闭前取消 lifetime，并等待 observation，然后关闭自有介质。
```

`identityFromCurrentBindingAndStatus` 来自同一会话的真实 `GetBindingTargetAsync`、`GetBindingAsync` 和 `GetArchiveStatusAsync`；scope 由可信宿主提供并由 Serve 鉴权。不能从模型参数、未验证 JWT、旧备份或手填 sessionId 推导身份。`SqliteArchiveStore.OpenAsync`、`SqliteMaterialResponseOutbox.OpenAsync` 是现成 Windows 耐久介质；`CurrentUserDpapiArchiveKeyProvider.Create/Open` 配合原 AES 文件族保护档案正文。`Create` 不覆盖，`Reopen` 不重建损坏文件，用户/密钥不匹配不降级明文。

SDK 生成的 ACK 请求键固定于原操作 epoch 和受理前 binding revision；原合同 ACK 必须推进 revision，下一页重新读取当前 binding。材料响应键排除会变化的剩余 TTL，保留原请求语义；任何已耐久 ACK/outbox 都优先查原操作。冲突不会换键。需要原 ACK 恢复时使用 `OpenRecoverableAsync` 与显式 `RecoverAcknowledgementAsync(originalRecoveryRequest)`；保留原恢复意图，不能将未知提交当作失败重做。当前恢复文件族只支持原明文 source，不能将已加密介质偷偷改成明文来启用该能力。

SDK 保存 cursor 只在事件所有耐久动作成功后进行；保存失败不会推进游标。连接内同 cursor 异文拒绝。进程重开仅有 cursor、没有原 frame 时若上游重放相同 cursor，会明确拒绝 `cursor_replay_unverifiable`，要求核对；不会忽略未知内容。正常 SSE 从已保存 cursor 之后继续。

ACK 恢复默认关闭。只有显式通过 `OpenRecoverableAsync` 打开的新版明文 source 才将 `ArchiveSessionOptions.EnableAcknowledgementRecovery = true`；原明文/密文文件族保持默认 `false`。`IArchiveRecoveryAvailability` 使多文件族介质公开当前实例能力，不靠同一个类实现了接口就误启用。未知提交的原 ACK 查询在两种模式下都保留。

### 三个原生示例的实际接线

Console、WinForms、WPF 共同使用 `examples/Shared/NativeArchiveHost.cs`。菜单中选择可信配置文件后，示例协商原已存在会话绑定，打开受护档案和耐久材料 outbox，等真实 SSE Ready 后提供同步、覆盖状态及有界正文查看。界面草稿/呈现副本不属于授权档案。配置示例：

```json
{
  "format": "tansr-example-archive-v1",
  "serveUrl": "http://127.0.0.1:8788/",
  "allowInsecureLoopback": true,
  "sessionId": "原会话ID",
  "trustedScopeFile": "C:\\TansrDemo\\scope.json",
  "tokenEnvironment": "TANSR_EXAMPLE_TOKEN",
  "retentionAuthorityFile": "C:\\TansrDemo\\retention-authority.json",
  "storage": {
    "mode": "create",
    "archivePath": "C:\\TansrDemo\\archive.sqlite",
    "outboxPath": "C:\\TansrDemo\\material-outbox.sqlite",
    "cursorPath": "C:\\TansrDemo\\archive-cursor.dpapi",
    "keyPath": "C:\\TansrDemo\\archive-key.dpapi.json",
    "keyId": "archive-user-key",
    "replicationId": "archive-primary"
  }
}
```

目录由开发者提前创建并限制为当前用户。第二次开启用 `reopen`，不自动换密钥或档案身份。`retention-authority.json` 是开发者受保护的当前删除授权，不是档案里的旧值：`{"identity": <当前真实 receiver identity>, "revision":"0"}`；发生已授权删除后写入新 revision 及 `approvedRetention` 原完整 `archive-retention-v1` 意图。示例同步时调用原删除入口，存储每次读取都核对该外部权威；撤权/代际变化先拒绝访问，旧备份不能自证可用。生产应用可以将这些委托接自己的可信控制面，不能让模型或普通文件工具编辑权威文件。

### 兼容及第三方存储

SDK1 完整会话快照与 SDK2 source/retention 是两个独立选项：关闭可选本地 SDK1 镜像不关闭现有会话，不停止服务器持久化，不删除原记忆；不能关闭 Serve 已要求的 SDK2 source。切换必须先查询 Serve 当前策略。`SessionSnapshotPersistence` 管理可选镜像，`ArchiveSessionHost` 管理已协商档案；同会话可以同时装配，两者不互相替代。

第三方档案介质实现 `IArchiveStore`；需要恢复/删除时同时实现其原可选接口。已有 `ReplicatedArchiveStore` 和 `ArchiveSyncClient` 组合原同步合同实现源/副本传输；cache 不能冒充 source 发上游 ACK。恢复读数据要保留原 scope、sourceGeneration、retention 和 receipt，而不是复制任意 SQLite 文件改身份。

## 自动记忆本地化

```csharp
IMemoryPublicationStore publication = await SqliteMemoryPublicationStore.OpenAsync(options, ct);
var memoryTool = new WindowsMemoryPublicationHost(publication, enablePreview: true).CreateTool();
// 将 memoryTool 加入同一个 WindowsExecutorBackend 的 BusinessTools，
// 与文件/进程工具共用 DeviceSessionHost、执行账本和绑定，不再起第二个设备绑定。
```

`MemoryPublication` 是原受信设备能力，模型工具清单中按应用配置申请 `SearchMemory` 等业务能力，不能把后台保留工具当成用户可任意调用的业务函数。记忆提取、选择、删除、generation 和晚到写入裁定仍在 Serve；Windows 持有 publication 正文和原 CAS transfer 回执。已有 SQLite 实现与 Node 冻结格式、旧构造调用保持兼容。

第三方介质可实现 `IMemoryPublicationStore` 注入，但必须真实满足 `AtomicDurablePublication`：正文 CAS 和 transfer 终局同一耐久事务、原 owner 隔离、原重复查询、原容量限制。只有明确提交前拒绝/已回滚才抛固定 `MemoryPublicationRejectedException`；网络、介质或提交状态未知保持未知，不能为求重试把它映射成业务失败。Host 验证响应来源、transfer、范围和摘要；注入接口不等于已验证每个供应商存储实现。

## 缓存连续性（显式 preview）

```csharp
var cache = new CacheContinuityClient(client, enablePreview: true);
var capabilities = await cache.DiscoverAsync(ct);
if (!capabilities.Available) return;
var original = await cache.PrepareOpenAsync(sessionId, CacheContinuityOpenKind.New,
    requestId: stableOperationId, cancellationToken: ct);
// 先受保护地保存 Action、ExportOriginalRequest() 和 OriginalPrincipal。
var receipt = await cache.SubmitAsync(original, ct);
// 受保护地保存 receipt.Ticket.ExportProtectedValue() 及原回执；不写日志/UI/URL。
// 失回/重开：RestoreOperation(original...)，然后 QueryAsync(original)；不自动换组重做。
```

此门面仅导出原 `sdk2-cache-v1` 候选的受控操作、票据及回执，不把未稳定低阶实现全部公开。`Resume`/`Import`/`Fork`/`Rebind` 是明确的宿主选择。票据不能代替当前认证；跨主体、过期、删除、撤权或冲突必须沿原合同返回错误。诊断空页没有供应商命中或费用事实，不能解释为零费用，也不保证更高命中率。

三个示例的共享 `NativeCacheContinuityHost` 直接消费该门面。工作台提供配置、新建、原票 resume、查询原请求、诊断、显式关闭和本地状态。它借用当前 `ExampleConnection.Client`，保留本地 OwnedServe 私有票据传输；不从环境重造另一个控制器。配置明确 opt-in：

```json
{
  "format": "tansr-example-cache-continuity-v1",
  "enablePreview": true,
  "serveUrl": "https://your-serve.example/",
  "sessionId": "原会话ID",
  "trustedScopeFile": "C:\\TansrDemo\\scope.json",
  "statePath": "C:\\TansrDemo\\cache-state.dpapi",
  "mode": "create"
}
```

首次 `create`，重开 `reopen`。原 operation 请求、主体、回执和票据以当前 Windows 用户的 DPAPI 保护，原子替换且同一状态文件只允许一个活动宿主。提交前先耐久保存；失回或进程退出后先查询原意图，不自动新建或降级。退出UI调用本地 `StopAsync` 仅释放状态文件写锁，远端 `CloseAsync` 必须由用户明确点击。真正跨 runtime 的续接使用 Serve 已授权的原会话恢复/可信来源机制；示例 `ResumeAsync` 只是原 C1 票据操作，不能绕过 Serve 的跨运行边界。

## English integration notes

Use `ArchiveSessionHost` for the original archive event channel, durable ACK recovery and on-demand material responses. Await `Ready` after starting observation; it represents validated SSE headers and current authorization scope, not a scheduled background task. Configure the existing SQLite archive, durable material outbox and a protected cursor store. Keep source identity and deletion authority external to backups. Do not create a new operation key after an uncertain write.

Register `WindowsMemoryPublicationHost.CreateTool()` on the same device backend as other terminal tools. Inject `IMemoryPublicationStore` for another durable backend only if body CAS and transfer outcomes commit atomically. Serve remains responsible for extraction, memory decisions and deletion generations. Closing optional SDK1 snapshot persistence must not close the session or disable a required SDK2 source.

`CacheContinuityClient` is an explicit preview of the existing cache contract. Persist original operation bytes, principal and opaque tickets in protected storage. Recover by querying the original operation; do not silently enroll a replacement group. Empty diagnostics are unknown evidence, not proof of cache hits or lower cost. The three native examples share the same archive adapter and never implement another ACK or context-management state machine.
# Cold offline reading / 冷启动离线只读

三示例提供无需创建连接的离线授权档案、离线授权记忆入口；Console 使用 `--offline-archive <原档案配置>` 或 `--offline-memory <原设备记忆配置>`。它们调用 `NativeOfflineStorageReader`，只重开已经存在的原介质，不创建 Serve 客户端，不读取网络票据，不同步、不 ACK、不提取记忆，也不会将正文写回平台。若在线本地宿主仍持有独占文件，应先显式停止该本地宿主再离线阅读。

既有 `trustedScopeFile` 必须由可信应用宿主维护，并增加明确的本机离线许可；它不是 Serve 新协议，也不能从聊天文本、旧数据库或未验证 JWT 自证授权：

```json
{
  "principal": "原可信主体",
  "scope": { "applicationScopeId": "原应用", "endUserId": "原用户", "authorizationRevision": "7" },
  "offlineRead": { "allowed": true, "authorizationRevision": "7", "expiresAt": "2026-09-28T00:00:00Z" }
}
```

日期仅为配置形状示意，不是默认授权。许可缺失、过期、撤销或授权 revision 不匹配时拒绝。档案复用原 `retentionAuthorityFile` 的 identity/revision；本地删除版本未追上已知权威，或记录已有墓碑时不返回正文，也不擅自应用删除来使检查通过。DPAPI 密钥和原 source 身份必须匹配，读取期间本地权威变化同样拒绝。

设备记忆配置额外指定 `offlineAuthorityFile`，由可信宿主保存**已确认的原记忆状态**以及同次已提交 publication 的 SHA256 etag：

```json
{
  "memory": {
    "identity": { "kind": "client-managed", "domain": "原记忆域", "sourceId": "原source", "sourceGeneration": "1", "applicationScopeId": "原应用", "endUserId": "原用户" },
    "revision": "3", "deletionGeneration": "1", "available": true
  },
  "publicationEtag": "可信宿主已观察的原64位小写SHA256"
}
```

此文件不能由离线 reader 从待读旧备份反向生成。记忆读取通过原 `SqliteMemoryPublicationStore.ReadPublicationAsync` 取得有界、不透明的已提交字节，再核对原 identity、revision、deletionGeneration、etag 和文件摘要。只显示记忆 Markdown 正文；隐藏治理、票据与操作回执，已知删除或活动治理尚未完成则拒绝。不会另造执行 owner 或记忆决策循环。

离线仅能遵循**已经接收并持久保存**的授权与删除状态，不能得知尚未接收的远端撤权。界面明确显示该边界；恢复网络后仍由原 Serve 协议重新确认，离线许可不授予新的远端能力。

The offline entry points reopen the original local stores and enforce an explicit, unexpired host-managed offline permission. They never create a network client, acknowledge an archive, apply retention, or run memory governance. Known revocation/deletion and source/version mismatches fail closed. Remote changes that have not reached the device cannot be discovered offline; the displayed result is not a claim of current global authorization.

原生示例的真实界面验收使用 `ServeNativeStorageFixture.mjs` 装配公开的 `createServeOffloadArchiveHost`、`createServeCacheHost` 和原文件冷层。它与工具、媒体和设备记忆验收使用同一应用/用户范围，单独监听以保留各自明确的存储配置。新会话显式声明 `sdk2-offload-v1`；旧 SDK1 界面继续使用原工厂。

夹具只在真实会话创建且原 binding-target、binding、archive status 核对成功后签发可信本机配置。界面依次启动档案宿主、发送合成任务、同步并读取原记录；材料处理和耐久 ACK 均由公开 SDK 完成。缓存界面保存原请求、回执和受护票据，重开只查询原意图，关闭不伪造跨运行恢复。自然过期、跨运行和供应商费用另按原验收证据归属，不以菜单点击代替。

The native UI fixture composes the original public offload/archive/cache hosts. Configuration comes from the actual session binding and trusted host state, not from UI-generated identities. Archive synchronization uses the SDK's durable ACK path; cache reopening retains the original operation. The upstream model/cache peer is synthetic, so this proves protocol consumption and local persistence, not provider cache savings or production billing.
