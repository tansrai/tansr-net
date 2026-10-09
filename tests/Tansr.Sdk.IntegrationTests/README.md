# Serve 源码 HTTP/SSE 联验

本节所述原工装的回环监听导入指定的 CLI 源码；下文独立封存包工装不消费活动源码。SDK1 传输回归采用原 `FakeAgentFactory` 的受控子类；高阶链通过公开 Serve 工厂，使用真实平台装配、kernel 查询循环、设备执行、档案介质和 HTTP/SSE，仅把上游平台与模型替换为确定性合成响应。所有运行零真实平台凭据、零付费采样。

先由统一工程门编译 Release，再单独运行：

```powershell
$env:TANSR_SERVE_SOURCE = 'J:/tansr/tansr-cli'
node scripts/serve-integration.mjs
```

脚本硬性要求已安装依赖的干净 CLI 源码、源码导出与 tsx loader，记录当前 Git SHA、入口/夹具/schema SHA256，拒绝旧 `dist`。它仅监听随机回环端口，用五分钟有效的随机合成令牌；令牌不输出、不落文件。高阶链临时数据保留于忽略目录 `artifacts/serve-integration/run-*`，包括真实执行/档案 SQLite 和 DPAPI 加密密钥，便于失败查账。`dotnet test` 使用 `--no-build --no-restore` 与专用类过滤器。`TANSR_DOTNET` 可指定 dotnet 可执行文件，`TANSR_INTEGRATION_CONFIGURATION` 可显式选择已编译配置。

经作者会签指纹的未提交候选采用独立显式入口，不改变默认干净源码门：

```powershell
$env:TANSR_SERVE_SOURCE = 'J:/tansr/worktrees/cli-SRV-01-terminal-services'
node scripts/serve-source-snapshot.mjs --write '<本次归档目录>/serve-source-snapshot.json'
$env:TANSR_SERVE_SOURCE_SNAPSHOT = '<本次归档目录>/serve-source-snapshot.json'
node scripts/serve-integration.mjs
```

快照写入新文件并固定原 base commit、受检 Git 路径是否已提交的 `sourceCommitted`、三份已会签 schema 和所有运行源码依赖。脚本在运行前后逐文件核对；任一漂移使本次验收失败，不能在运行中刷新快照。当前终端合同是 `2026-09-26.candidate-7`，仍为显式 preview；来源区间通过不表示整个 Serve 开发树已经最终冻结。

显式快照模式在原链之外加入设备自动记忆、会话控制和原生进程三组场景。默认 `TANSR_SERVE_TEST_SUITE=all` 运行全部 16 项；只复验已修改部分时，可选择 `controls`（8项）、`execution`（3项）、`new`（这两组11项）或 `memory`（原自动记忆1项）。具名选择必须使用源码快照，并写入结果中的 `suite`，不得把子集写成全量通过。原五项包含 SDK1 三项、公开工具/档案一项和自动记忆一项；不同批次存在重叠时不相加。

会话控制覆盖原图文输入、同轮插入与回执丢失后原键恢复、取消、配置 CAS/回滚/忙态，以及 fallback/prepend 与三种可信宿主提示词组合。元信息游标、SSE 与历史均消费真实 Serve，不能用等待时间或跳过 gap 检查掩盖事件发布竞态。

原生进程组通过 Windows 真实进程、公开 `DeviceSessionHost`、协商后的执行通知、分块 HTTP 和原输出 SSE，检查进程存活时可见的 UTF-8 stdout/stderr、重连去重、进程树取消及丢响应后只查询原操作。IPC 只控制合成进程的确定性释放屏障，不代替产品输出流。实际运行、接收和视图时间分别记录；这些记录没有 Electron 同机场景基准，不能作为完整性能对照通过的证据。

未设置必要环境直接失败，不静默跳过。SDK1 独立测试覆盖默认族的 create/send、真实在线非 ASCII SSE、取消观察不 interrupt、原游标重连、历史及分页、窗口缺口和身份隔离。高阶产品链覆盖公开 DeviceSessionHost 自动初始化/注册/绑定、原审批、Windows 真实 Read、原执行回执、真实轮终局、ArchiveClient 发现既有绑定、ArchiveTransferSession 拉页/正文、DPAPI/AES-GCM SQLite 耐久 ACK 和重开，再通过原档案 SSE 将真实材料请求交给 MaterialSource/SQLite 响应箱，上传后由原核心下一轮消费。材料请求/入队由受控文件 IPC 调用可信宿主生命周期；IPC 不传输材料正文、不代理 SSE，也不是新增产品 HTTP 路由。

候选分支还通过公开 TerminalSessionControl 检查配置修改/重放及记忆 pin/原键查询。原 ACK 的真实成功 HTTP 响应被夹具丢弃后，重开原密文存储并查询原键；跨用户检查直接到目标 ACK 查询。另一独立的明文 source 数据库显式采用新恢复 DDL，以真实提交屏障制造旧 ACK 409，再验证 rebase 成功丢响应、重开、同键重放及跨用户拒绝；原密文档案没有迁移或降级。主场景严格固定 4 次合成模型请求；受信后台记忆请求须匹配 `meta.purpose`、精确工具集合和任务标记，单独有界计数，并返回原指令允许的无写结束。

测试会继续接收第二轮档案；如果该步暴露原修订冲突，保留原 ACK 并使联验失败，不能以延时、改键重投或绕开产品协调器遮掩。传输测试不能证明真实外部模型、跨进程 Serve 冷恢复或完整 SDK 行为等价。端口和子进程退出时收口，工程卡与完整验收项仍须统一审阅。

## 独立的封存包加密记忆链

`ServeEncryptedMemoryPublicationTests` 使用独立的 `PstMemoryPublication` 类过滤器，不纳入上面的源码快照子集计数。先构建 IntegrationTests 和 `ConsoleAssistant` 的 Release/net10.0-windows，再运行：

```powershell
node scripts/packed-memory-integration.mjs '<共享封存Host清单.json>' '<新的证据目录>'
```

清单固定公开 Serve/SDK/API-client 三个 tgz 的路径及 SHA256、已安装公共包 Host 的路径和 SHA256。Host 由本批统一准备，返回原 scope/publicationIdentity/执行器 ID，并提供仅供合成验收的认证控制。脚本不导入活动 CLI 源码，也不建立替代执行服务。每项独占一个随机回环 Host 和系统临时根，顺序消费；保留 TRX、候选文件哈希、命令/退出码及清理回执，配置里的合成令牌随临时根删除。

三项分别覆盖真实 Console 设备进程的 DPAPI 双库、原 pin HTTP 200 失回与原键查询、进程重开、撤权/错 scope 及句柄释放；真实 chunk 已写入 SQLite 后丢返回形成 unknown；在相同提交点取消设备并关闭，保留未送回的原 unknown，再按原冻结执行 API 补投/查询。两条故障用例只包装公开 Store 接口注入失回，不制造 Serve operation/receipt，不改密文或直接写数据库。数据库及活跃 sidecar 同时扫描合成正文和真实 chunk Base64。

初始化阶段的明确 `busy`/`backoff` 不算失回。测试先查询原键，无回执且原错误明确未受理时，才在截止内重投完全相同的 requestId/operationId/body；任何 unknown、其它错误或单独的空查询均不触发重放。设备重启得到新连接不等于旧 transfer 写权恢复；本组只证明冷重开和原事实保全，不伪造维护授权。

这是 Windows 真实 DPAPI/SQLite、公开包 HTTP 和 Console 进程链；不是物理掉电、跨平台数据库互开、CLR4/WPF/WinForms 实机或完整生态验收。历史源码 extraction/recall 测试仍独立保留。
