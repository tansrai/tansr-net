# SDK1 上下文快照镜像

`SessionSnapshotPersistence` 保留原 `AgentSession` 对象与会话编号，控制一份本地上下文快照镜像。它使用原 `ExportAsync` 的 checkpoint + export 路由，逐字保存原导出的全部字段、图片及未知扩展，不把消息重新投影为文本。它默认关闭。

这与 Electron 的 `persistence.ts` 当前会话镜像语义对应：压缩或 restore 之后，镜像保存核心当前上下文。它不是完整长历史、长期记忆、平台留存设置或 SDK2 的必需材料源。`sdk2-offload-v1` / `source-required` 会话在工厂读取原元信息时被拒绝，尚未触碰磁盘；SDK2 使用原 `ArchiveSessionHost` 与档案存储。

```csharp
var scope = new SessionSnapshotScope(
    serveUri.AbsoluteUri, trustedApplicationId, trustedEndUserId, session.Id);
// Scope 每次操作从可信应用宿主重新读取，不能解析未验证 JWT 自证身份。
using var store = new WindowsSessionSnapshotStore(new()
{
    Path = snapshotPath,
    Scope = scope,
    ReadScope = ReadCurrentTrustedScope,
    KeyProvider = () => CurrentUserDpapiArchiveKeyProvider.Open(keyPath, "context-key"),
});
using var mirror = await SessionSnapshotPersistence.CreateAsync(
    session, store, ReadCurrentTrustedScope);

await mirror.EnableAsync();   // 显式用户动作；立即读取原 export 并耐久保存
await mirror.FlushAsync();    // 每个实际轮次完成后调用一次；关闭时不做网络/磁盘写入
var copy = await mirror.ReadAsync(); // 只读本地副本，不会 Import 或 Restore
await mirror.DisableAsync(); // 先阻断晚到写入，待当前写入结束后删除镜像内容
```

宿主预先选择可信目录和密钥策略。Windows store 构造、空目录下的 `ReadAsync` 不创建文件或读取密钥；示例 `NativeSnapshotHost` 只在用户第一次启用时创建目录和 CurrentUser DPAPI 密钥。已有镜像缺失或损坏密钥会失败，不能自动生成新密钥覆盖旧内容。

`EnableAsync` 失败会回到关闭状态。`DisableAsync` 删除失败仍保持关闭，后续 `FlushAsync` 不得重新写盘，同时向调用者报告失败。每次 Flush 创建一个原 checkpoint；Serve 的原保留策略缺省不限，所以本镜像取得完整 export 后会清理本次自有临时 checkpoint，包括随后本地写入失败或取消的情况。清理只调用一次原 Delete 路由，使用独立的 10 秒上限，不删除其它 checkpoint、不改变留存策略，也不改变公共 `ExportAsync` 的保留语义。清理失败保留 `PendingCheckpointId` 和 `LastCheckpointCleanupErrorCode`，不掩盖本地写入原异常；已耐久保存的本地内容仍可读，后续捕获明确以 `snapshot_checkpoint_cleanup_pending` 拒绝，避免无限制造新快照。宿主核对后调用 `RetryCheckpointCleanupAsync` 显式重试。取消或失败的 export 若没有返回完整结果和 checkpoint 编号，仍按原 Export 合同保留，不能猜测并删除。

应用应只在需要同步的真实终局调用 Flush，不在渲染、每字增量或轮询中调用。临时 checkpoint 清理后，副本中的编号只标识其来源；恢复本地内容应走显式 Import/Restore，不能假定远端临时 checkpoint 仍在。

存储接口 `ISessionSnapshotStore` 可由开发者实现第三方后端，必须兑现：完整字节、原子保存、耐久提交后返回、expectedRevision CAS、删除也推进修订、权限失败可见。Windows 后端复用原 `ArchiveBodyCipher` 和 `IArchiveKeyProvider`、`StorageFileIdentity`，使用 SQLite FULL 同步事务；单快照缺省上限 32 MiB。密文 AAD 的本地身份由服务地址、应用、用户、会话四元组绑定；内部 `sdk1-context-mirror` 标签仅作为本地加密域，不是伪造的 Serve SDK2 授权或协议字段。

关闭后清除 SQLite 中的快照内容，保留递增修订及加密格式元信息以拒绝旧写入。密钥仍归宿主所有，关闭不删除其它档案或共享密钥。此行为是逻辑删除镜像，不承诺物理介质安全擦除。既有库损坏、磁盘满、锁占、范围改变、错误密钥及冲突均明确失败，不把损坏当空镜像重新初始化。

重新读取副本不赋予它当前上下文权威。若用户显式要求恢复，应用可检查原身份、来源和当前服务允许的操作后使用现有 Import/Restore 流程；该流程与镜像开关独立，没有恢复时重放历史工具的隐式行为。

示例配置由可信宿主提供，使用 `TANSR_SNAPSHOT_CONFIGURATION` 指向 JSON：

```json
{
  "scopeFile": "C:/MyApp/private/trusted-scope.json",
  "directory": "C:/Users/example/AppData/Local/MyApp/context-mirrors",
  "keyId": "sdk1-context-mirror"
}
```

示例目录按上述四元组的 SHA-256 分文件。应用退出调用 `CloseAsync` 可完成最后一次已开启镜像的 Flush 并释放句柄；单独 `Dispose` 只阻止本实例后续写入，不等于显式关闭或删除持久内容。SDK 包和示例没有新增服务端 wire 或第三个产品包。

## Validation boundary

The persisted bytes are the original Serve **context checkpoint export**, not a complete SDK2 archive or memory database. Enabling/disabling preserves the original session object and ID; reading never imports automatically. The Windows backend uses the existing chunked authenticated cipher and CurrentUser DPAPI key provider, with an atomic revision check and durable SQLite transaction. A required SDK2 persistence source cannot be replaced or disabled by this SDK1 mirror.

The focused test suites are `SessionSnapshotPersistenceTests` and `WindowsSessionSnapshotStoreTests`. They exercise public export HTTP calls, in-flight cancellation and late writes, failure retention, changed trusted scope, exact bytes, encrypted reopening, deletion revisions, bounds, corruption, and real CurrentUser DPAPI. Native UI and actual Serve acceptance are recorded separately in the delivery evidence; local tests alone do not imply a published NuGet package or all A15 conditions passed.
