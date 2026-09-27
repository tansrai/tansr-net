# 原生示例的统一工作台

WPF、WinForms net48 和 Console 使用相同的 `ExampleSessionWorkspace`、`ExampleSessionOptions` 及公开 SDK。工作台提供能力发现、任务/待办/用量、分页历史、快照创建/恢复/删除/导入/导出/分叉和受控 cwd。分叉返回新 ID，原会话继续保持；不自动迁移到分叉，不重放工具。导出只创建新文件，不覆盖已有档案。

三个入口都读取短期票据 `TANSR_SESSION_TOKEN`、`TANSR_SERVE_URL`，本机开发 HTTP 必须显式 `TANSR_ALLOW_HTTP_LOOPBACK=1`。票据不写草稿或日志。本地 Serve 继续使用 `ExampleConnection` → `LocalServeHost` 的 PID/私有口令所有权检查，不另外启动 Node 客户端代理。

本机草稿、呈现和未决插入记录按示例形态、规范 Serve 来源及可信宿主配置中的 `principal` / `applicationScopeId` / `endUserId` 分区。主体改变时清除当前窗口的旧草稿、呈现和待批状态，旧回调仍受原会话和分区约束；用户明确填写的恢复 ID 继续交 Serve 鉴权，不偷偷清空以新建会话。原文件不删除、不自动迁移；同主体续票或授权修订更新不换分区。同一可信主体可在无票据、无网络时读取自己的陈旧本机副本，不能据此证明实时权限。未配置可信身份时，每次连接都是新的未绑定交互，旧无主体副本不自动装入待发送草稿；不从票据正文、JWT 或旧文件推断身份。独立“离线授权档案 / 记忆”仍使用其原可信离线授权入口。

开发者如需为本地 SEA 预置档案、记忆或受信扩展，可同时设置 `TANSR_LOCAL_SERVE_HOST_MODULE`（规范化绝对 `.cjs` 路径）与 `TANSR_LOCAL_SERVE_HOST_MODULE_SHA256`（64 位十六进制 SHA256）。四种入口共用原 `LocalServeHost.AdditionalArguments` 传入 `--host-module` / `--host-module-sha256`；默认不加载模块，不接受聊天、工具参数或任意 argv 决定模块。Serve 在原进程中校验模块摘要并装配公开扩展，无须外置 Node。

For a developer-provisioned local SEA extension, set both `TANSR_LOCAL_SERVE_HOST_MODULE` (canonical absolute `.cjs` path) and `TANSR_LOCAL_SERVE_HOST_MODULE_SHA256` (64 hexadecimal digits). The four entry points forward only this validated pair to the existing owned `LocalServeHost`; default startup is unchanged. Conversation input never selects host code, and the Serve process validates and loads its public extension factory without a separate Node installation.

无需连接 Serve 的“离线授权档案”/“离线授权记忆”按钮与 Console `--offline-archive <配置>` / `--offline-memory <配置>` 使用已有本机受护存储及可信离线授权读取，绝不同步、ACK、消费材料或创建新会话。授权过期、已知撤权、已知删除须拒绝；离线结果不能证明当前服务端状态。这与普通“本机离线回看”的陈旧呈现和草稿分开。具体配置见存储手册。

The native **authorized offline archive/memory** actions and Console `--offline-archive` / `--offline-memory` read existing protected storage using explicit trusted local authority before any Serve connection is created. They perform no sync, ACK, material submission or new session. Expired authority, known revocation and known deletion fail closed; offline data is not proof of current remote authority. Ordinary offline presentation/drafts remain a separate feature.

如果受信 Serve host-module 要求第二层用户认证，显式配置 `TANSR_SERVE_USER_TOKEN`；示例只把它作为 `x-tansr-demo-user-token` 传给本地就绪探测及原客户端，远端同样使用附加认证头，不从主体标识推导用户票据。本地私有 Bearer 仍由 `LocalServeHost` 持有。仅在受信设备配置中显式 `useControllerForDevice: true` 才允许设备两腿共享该已拥有客户端；远端独立设备票据保持原模式。

For host modules requiring a second user credential, `TANSR_SERVE_USER_TOKEN` supplies only the explicit `x-tansr-demo-user-token` header to readiness and the original client. Principal names are never treated as credentials. Local private Bearer ownership stays in `LocalServeHost`; sharing the owned transport with a device requires explicit trusted `useControllerForDevice: true`. Separate remote device credentials remain supported.

配置窗口的“授权模型目录”读取 Serve 公开白名单并填充实际模型下拉；选择后使用原配置 CAS 修改当前会话。Console 对应 `/control 10` 与 `/control 11`（本人最近1天用量）。配额数值未开放时显示 `not_exposed`，由网关执行；不把1天用量、会话累计或上下文估算冒充剩余额度。

The configuration window loads the authorized catalog into its model selector and changes the current session through the original configuration CAS. Console exposes catalog and the authenticated user's one-day usage via `/control 10` and `/control 11`. Quota values remain explicitly `not_exposed` and gateway-enforced; usage totals and context estimates are never presented as a remaining balance.

可信宿主可统一提供 `TANSR_PROFILE`、`TANSR_CAPABILITIES_PROFILE`、`TANSR_SESSION_CWD`、`TANSR_THINKING_BUDGET`、`TANSR_MAX_TOKENS`、`TANSR_MAX_USD`。这些只是给 Serve 的请求，不能提高应用授权。profile 对应 Serve 已注册的模型、工具、hook 和子代理配置，不远程上传程序集或执行代码。动态切模/思考及记忆命令沿现有“配置 / 记忆”入口，保留原操作键与未知结果。

## 本机工具、实时输出与自动记忆

先建立会话，再选择“连接本机设备工具”。Console 使用 `/device-start <配置文件>`。配置中的会话和服务来源必须与当前连接相同。SDK `TerminalDeviceHost` 完成发现、执行绑定、通知/原领取、持久回执和输出续接；示例不编写 SSE、心跳或 ACK 协议。

```json
{
  "format": "tansr-example-terminal-device-v1",
  "enablePreview": true,
  "serveUrl": "https://serve.example.com",
  "sessionId": "<当前会话 ID>",
  "trustedScopeFile": "C:\\AppData\\identity.json",
  "deviceTokenEnvironment": "TANSR_DEVICE_TOKEN",
  "executorId": "windows-workstation",
  "bindingRequestId": "<保存的原绑定请求 ID>",
  "workspace": { "path": "C:\\AppData\\workspace", "id": "work", "revision": "1", "allWritersCooperate": false },
  "journal": { "path": "C:\\AppData\\state\\execution.sqlite", "mode": "create", "compactCompletedReceipts": true },
  "allowedTools": ["Read", "List", "Shell", "SearchMemory"],
  "shell": {
    "executable": "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
    "sha256": "<宿主核验的固定解释器 SHA256>",
    "interpreter": { "id": "approved-powershell", "revision": "1", "hostShell": "powershell" }
  }
}
```

`trustedScopeFile` 沿 [设备记忆示例](device-memory.md) 的 `principal` 和 `scope` 结构。主窗口借用原 controller 客户端，不复制受控本地 Serve 私有口令；独立程序直接调用 wrapper 时才需额外 `controllerTokenEnvironment`。device 票据只拥有设备侧权限。未知绑定结果保留配置中的原身份，先对账，不换键重试。已有 journal 必须显式 `reopen`，损坏或锁占不会退回创建新库。

上例的 `journal.compactCompletedReceipts: true` 显式选择新建 `sdk2-execution-sqlite-compact-v1` 介质：未完成操作仍保留完整回执预留；耐久写入最终回执时才在同一事务释放未用预留，操作、原回执和未知结果保护仍保留。只接受 JSON 布尔值；缺省或 `false` 使用原 `sdk2-execution-sqlite-v1`，不扩大原容量。重开必须保持创建时的模式，已有 v1 不自动迁移，也不能改为 `true` 强行打开。此字段属于三端共用的 `tansr-example-terminal-device-v1` 配置；旧独立 `--device-memory` 示例仍使用原 v1 介质。

文件/进程操作同时受 Serve 审批和本机批准约束。WPF/WinForms 的本机批准窗口随取消失效；Console 使用 `/device-allow <原ID>` 或 `/device-deny <原ID>`，不会另开一个 stdin 读取器。固定解释器校验后才启动，模型不能指定解释器路径或环境变量。这个普通用户进程不是操作系统沙箱；不支持隐式提权。默认工作区只承诺读操作；只有全部写者协作且满足 SDK 用户私有 ACL 检查时，才显式启用 `allWritersCooperate` 以提供条件写入。

需要终端工具与自动记忆同时工作时，在**同一份配置**添加现有 `publication` 段（identity、path、create/reopen 与容量），字段与 [device-memory.md](device-memory.md) 相同。`WindowsMemoryPublicationHost` 和资源工具注册到同一个后端、journal、设备绑定，不再启动另一个同会话记忆执行器。`MemoryPublication` 是受信后台能力，不填入 `allowedTools`；需要模型召回时声明 `SearchMemory`。记忆决策仍由核心作出，示例仅验证固定工具摘要并维护指定本机介质。旧独立记忆宿主用于独立会话或纯记忆设备，仍保留。

输出窗口只展示 Serve 权威输出流，区分 stdout/stderr、缺口和输出封印。输出完整不等于进程成功；停止本机设备也不等于关闭远端会话。显式启用 terminal 观察面时，关闭后通过 `WaitForResourcesAsync` 确认核心排空；旧合同只能说明关闭请求已受理。

## Skills、MCP 和 worker

默认继续使用 SDK1；`TANSR_SESSION_CONTRACT=sdk2-offload-v1` 显式选择 source-required 家族，同时必须提供可信 `TANSR_TRUSTED_SCOPE_FILE`。新建须提供持久保存的 `TANSR_SESSION_REQUEST_ID`，未知结果保持原键及原配置，恢复原 ID 不新造建会键。SDK 会先发现合同；Serve 未启用新家族时明确报错，不降级或换会话。本地拥有的 Serve 也通过原 `LocalServeHost.CreateClient(contract: ...)` 选择，保留私有票据及 PID 校验。SDK2 档案源要由开发者的 Serve `createServeArchiveHost` 或已配置的可信存储源装配；连接后在工作台接本机档案，不从聊天构造绑定。SDK1 与启用归属管理的 Serve 同样可使用档案扩展，两个开关并不等价。Worker 在原批次键与固定 job ID 上确定性派生每条作业的建会键。

SDK1 镜像若临时 checkpoint 清理结果未知，显示原编号及错误码，暂停新捕获；使用工作台动作 20“重查并清理 SDK1 镜像临时快照”处理原操作，不新建快照。断开前保留界面显示的待办编号。

工作台动作 21～27 接入缓存连续性；动作 21 参数为可信配置文件，空参数读取 `TANSR_CACHE_CONFIGURATION`。分别提供创建、原票据续接、查询原意图、诊断、显式关闭逻辑缓存与本地状态。写入前将原请求和票据保存在当前用户 DPAPI 文件，响应未知时仅查询原键，不默默创建新运行。退出 UI 只停止本机装配并释放本地锁，不关闭远端缓存；诊断缺失供应商命中或金额时显示 unknown，不能据此宣称费用下降。配置见 [存储与缓存接入](../../doc/storage.md)。

三端工作台操作 11～19 提供 SDK1 加密上下文镜像配置/启用/关闭/读取，以及 SDK2 档案连接/同步/读取/停止/状态。Console 使用 `/workspace 11 <配置路径>` 或 `/workspace 15 <配置路径>`；UI 在相同工作台选操作并填路径。空参数分别读取 `TANSR_SNAPSHOT_CONFIGURATION` / `TANSR_ARCHIVE_CONFIGURATION`。配置说明见 [SDK1 镜像](../../doc/session-snapshot-persistence.md) 和 [SDK2 档案](../../doc/storage.md)。SDK1 默认关闭，启用后在真实终局保存原 export 完整字节；断开保留镜像，只有明确关闭镜像才删除镜像内容。SDK2 的 SSE、原 ACK、材料请求由 `ArchiveSessionHost` 处理；所有控制请求借用原连接，保留本地私有 Serve 的票据与进程验证。档案源与呈现草稿独立，错误不会被改写成空历史或自动重建。

`TANSR_SKILL_INLINE` 配置 `inline-guide`；`TANSR_SKILL_DIRECTORY` 和可选 `TANSR_SKILL_FILE` 配置 `device-guide`。技能正文由原公开 catalog 按冻结名称/摘要提供，核心决定采用什么。工作台“撤销本机 Skills / MCP”或 Console `/revoke-extensions` 立即撤销本机连接和 catalog，旧工具声明不会绕过撤销。

MCP 继续支持固定可执行文件+SHA256 的 stdio 方式，也可显式选择 `TANSR_MCP_HTTP_URL`（与 EXE 互斥）；HTTP 环回须 `TANSR_MCP_HTTP_ALLOW_LOOPBACK=1`。`TANSR_MCP_TOOLS` 是宿主提供的工具映射白名单，不能从发现结果生成授权；每次调用复核原远端定义摘要。详细配置见原 MCP 接入说明。

Console `--worker jobs.jsonl` 保持有界并发、原单轮接纳/终局和 SIGTERM 收尾。无人值守默认拒绝权限请求；仅 `TANSR_WORKER_ALLOWED_PERMISSION_TOOLS` 逗号分隔的受信工具名允许通过原请求 digest 回复批准。该政策不跳过 Serve 裁决，也不授予本机设备执行权。cleanup 输出分别标明本机资源/关闭请求及 `remoteCleanup=unknown`，不把 HTTP 202 写作远端排空。

## Native host parity

All three examples share the public SDK workflow. The workspace exposes capabilities, tasks, usage, history pages, checkpoint lifecycle, fork and controlled cwd. A fork returns an identity without replacing the active conversation. Configuration profiles are trusted Serve references, never uploaded code.

Local drafts, presentation and pending insertions are partitioned by example, canonical Serve origin and the trusted host's principal, application and end user. Switching users clears the visible draft, conversation and pending requests; delayed callbacks remain tied to their original session and store. An explicitly entered resume ID is still checked by Serve, never silently replaced with a new session. Existing files are retained without automatic migration. Ticket renewal and authorization revisions for the same person preserve the partition. That trusted person can read their stale local copy offline without a token; it is not proof of current remote authority. Without trusted identity, each connection starts a fresh unbound interaction and cannot adopt an old draft. JWT contents and old files never establish identity. The separate authorized offline archive/memory readers retain their original offline authority checks.

The optional terminal device configuration owns one execution binding for files, a pinned shell and memory publication. `TerminalDeviceHost` owns protocol coordination; the application supplies trusted identity, local resources, durable storage and local consent. Output is observed from Serve with explicit channel/gap/seal state. A seal is not a successful process receipt, and stopping the device is not remote settlement.

Set the JSON boolean `journal.compactCompletedReceipts: true` explicitly when creating a compact `sdk2-execution-sqlite-compact-v1` journal. Pending operations retain their full receipt reserve; unused reserve is released atomically only when the original final receipt becomes durable. Operations and receipts remain available, and unknown results never authorize re-execution. Omission or `false` retains the original v1 format and limits. Reopen with the same mode; existing v1 files are never migrated automatically. This option applies to the shared terminal device host, not the separate legacy `--device-memory` example.

Inline/directory skills and allowlisted stdio/HTTP MCP calls can be revoked from the native UI or Console. Background jobs use the same session API and explicit unattended policy. Presentation copies and drafts remain separate from authoritative archives; they are never silently replayed into the model or treated as current authorization.

Workspace actions 11–19 configure and control the encrypted SDK1 context mirror or the SDK2 archive host. The SDK1 mirror remains off until explicitly enabled, captures complete original exports at actual turn boundaries, and never replaces the current session. SDK2 transfer ACKs and material responses remain inside the public `ArchiveSessionHost`. Closing the UI retains stored archives; only explicit mirror disable deletes mirror content.

SDK1 remains the default. Set `TANSR_SESSION_CONTRACT=sdk2-offload-v1` with a trusted scope file to select the discovered source-required contract. New creation also requires the caller's durable `TANSR_SESSION_REQUEST_ID`; unknown creation results retain that key and original configuration, while resume keeps the original session identity. This applies to remote and owned local Serve connections. The developer's Serve must configure its authoritative archive/source; the demo never invents a binding.

Actions 21–27 expose opt-in cache continuity through `TANSR_CACHE_CONFIGURATION`: configure, create, resume with the original protected ticket, query the original operation, read diagnostics, explicitly close the logical binding, and inspect local state. Exiting only stops the local host and releases its lock. Missing provider cache-hit or billing evidence remains unknown.
