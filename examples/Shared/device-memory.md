# 原生设备自动记忆宿主 / Native device memory host

该入口把公开 `SqliteMemoryPublicationStore`、`WindowsMemoryPublicationHost.CreateTool()`、`WindowsExecutorBackend`、`SqliteExecutorJournal` 和 `DeviceSessionHost` 组装在一起。它只消费 Serve 原执行通道，不创建模型循环、摘要器、记忆选择器或另一套工具轮询。原 SDK1 `NativeToolHost` / `clientTools` / MCP 示例保持独立；保留 profile `MemoryPublication` 不暴露为模型业务工具，也不借用 Shell 权限。

## Serve 前提

当前生产 `tansr serve` CLI 尚未装配 `memoryPublicationFor`。只设置 `TANSR_LOCAL_SERVE_EXE` 不会自动获得设备记忆能力；没有所谓可直接开启它的 `TANSR_MEMORY_*` 开关。需要可信 Serve 应用使用公开 `createAgentSessionFactory({ platform: { memoryPublicationFor(scope), ... }, execution })` 和 `startServer`，为真实认证主体配置持久会话存储、执行 spool、controller/executor 权限、当前 authorizationRevision，以及 client-managed 记忆来源。`memoryPublicationFor` 不与 `platform.memoryFor` / `execution.memoryFor` 并用。

Serve 当前设备记忆链使用默认 SDK1 会话与 `/v2` 的 execution 扩展，不要求示例强切 `sdk2-offload-v1`。模型能力可包含 `SearchMemory`；设备固定注册 `TansrTerminalMemoryPublication`，定义摘要由 SDK 候选合同提供。配置文件不能授予服务端权限：最终认证、原请求、scope、完整 binding、工具摘要、MemoryPublication 专用授权都由已有协议和执行宿主核对。没有配置正确服务时显示真实错误，不回退为 Serve 本地文件。

## 显式宿主配置

先用正常控制入口创建目标会话，保留会话 ID。下面所有标识、修订、域键和来源必须由**可信应用注册/登录/配置管理**给出，不接受模型、聊天输入、未验证 JWT 或旧数据库的自我声明。`trustedScopeFile` 沿 [配置/记忆说明](session-controls.md) 的 `{principal,scope}` 格式。两种角色票据只从配置指定的环境名读取；可由应用续票更新。不要把票据值写入 JSON。

```json
{
  "format": "tansr-example-device-memory-v1",
  "enablePreview": true,
  "serveUrl": "https://serve.example.com",
  "allowInsecureLoopback": false,
  "sessionId": "由控制端创建的真实会话ID",
  "executorId": "应用获准的固定设备ID",
  "trustedScopeFile": "C:\\TansrHost\\identity\\scope.json",
  "controllerTokenEnvironment": "TANSR_MEMORY_CONTROLLER_TOKEN",
  "deviceTokenEnvironment": "TANSR_MEMORY_DEVICE_TOKEN",
  "workspace": {
    "path": "C:\\TansrHost\\workspace",
    "id": "受信工作区ID",
    "revision": "1"
  },
  "encryption": {
    "provider": "dpapi-current-user",
    "path": "C:\\TansrHost\\keys\\memory.key",
    "keyId": "memory-key",
    "mode": "create"
  },
  "journal": {
    "path": "C:\\TansrHost\\state\\execution.sqlite",
    "mode": "create",
    "maxOperations": 4096,
    "maxStoredBytes": 67108864,
    "maxPages": 32768
  },
  "publication": {
    "path": "C:\\TansrHost\\state\\memory-publication.sqlite",
    "mode": "create",
    "maxTransfers": 32,
    "maxStagingBytes": 8388608,
    "maxPages": 8192,
    "identity": {
      "scope": { "applicationScopeId": "应用实际标识", "endUserId": "当前用户实际标识" },
      "sourceId": "来源注册实际标识",
      "sourceGeneration": "1",
      "domainKey": "由Serve可信来源登记给出的64位小写SHA256"
    }
  }
}
```

这是结构示例，中文占位符不能直接当真实身份或 digest 使用。使用规范绝对路径，父目录由宿主预先建立并管理访问权限。Demo 同时启用 publication 正文和完整 execution journal 加密（compact 终态保留）。`encryption` 为必填的显式 CurrentUser DPAPI 供钥配置；key 文件必须与配置、身份、两份数据库分离且放在受限目录，不能存于工具可访问工作区。首次钥模式 `create`，原钥重开 `reopen`，不按存在性猜测、不自动换钥。旧明文库须先用 SDK 的两个 `CopyToEncryptedAsync` 分别迁至新路径并保留原件，不能直接加字段原地启动。SQLite 不覆盖已有文件：首次显式 `create`；随后使用同一文件、原身份及原容量显式 `reopen`。不按文件存在性自动选择模式，不删旧库，不另建来源掩盖未知结果。配置与存储分开，不复用路径。

publication `identity` 的 `domainKey` 由 Serve 对 `MemoryIdentity` 去掉 `sourceGeneration` 后按原 canonical JSON 计算；不是工作区路径、sessionId 或客户端随机键。示例直接使用可信来源提供的值，不自行猜来源。generation 改变也不能绕过原物理域键。

## 启动、运行与停止

```powershell
dotnet run --project examples/ConsoleAssistant/ConsoleAssistant.csproj -f net10.0-windows -- --device-memory C:\TansrHost\device-memory.json
```

WPF 和 WinForms 提供“启动设备记忆宿主”，选择同一配置文件即可。该宿主可以先于普通聊天连接单独运行，目标是配置中的明确 sessionId；不把当前编辑框或另一会话自动当目标。没有额外注册模型函数。

设备宿主等待公开 SDK 完成初始化、注册与绑定后进入 Ready，并持续领取操作。Ready 后再从控制端调用读取记忆或发送消息；不要在 `AfterBindingAsync` 里读记忆，因为该时刻 poll 尚未开始，读记忆会派回本设备并自等。设备启动失败或领取失败保留原 SQLite 文件，报错后由可信宿主排查；不自动注册下一连接重做。

Console `/status`、窗口“设备记忆状态”显示连接与真实 SQLite 容量。`stdin` 关闭不会停止无人值守设备；用 `/stop`、Ctrl+C 或窗口“停止设备”显式停止。窗口保持设备运行时会提示先核对控制端工作并显式停止，再退出。控制会话的 SSE 应继续处理审批/提问；remember 缺少合法 targets 或 askUser 桥时须拒绝，不自动批准。

停止顺序是：控制端先完成需要的会话/记忆工作，保持设备 poll 可用；检查真实操作回执，然后显式停止设备。已有接口没有通用的只读“所有后台记忆已排空”回执，示例不把空轮询、等待五秒、模型终态或会话 close ACK 当成该证据。`StopAsync` 等待本机执行收尾后关闭 journal/publication/client，不发送远端 close/delete；本机停止不代表远端 remember 已完成、已耐久或已被模型消费。用户提前停止的未决状态仍需对账。

## 容量、恢复与范围

- publication transfer 终态永久占用名额，ID 不能复用。帽满报错，不能通过重开改帽、删除旧回执或换库重写。同一物理库的容量参数持久化，重开必须一致。
- 原执行 journal 也永久保留操作。4MiB 正文按 12KiB 块写入需要 342 个 chunk，加 begin/commit 共 344 个操作；完整读取另需同量级操作。示例的 4096 个 journal 名额与 32 个 publication transfer **不是**保证可容纳 32 次最大正文及恢复；宿主须联合规划实际正文、操作数、存储字节和页帽。示例只报告容量，不虚增容量或自动清理。
- COMMIT 未知时保留原介质和键，按公开恢复合同关闭/重开并查原事实。跨连接 owner 改变默认拒绝；本示例没有批准任意新 owner 的 `AuthorizeRecovery`，不自动把只读 query 权限提升为继续写权限。
- 换连接恢复若需要 Serve 维护见证，应由可信 Serve 运维走既有维护 API、独立 evidenceId 和原执行日志。C# 示例没有通用 HTTP 维护授权，不自动声明恢复成功。
- 本设备模式只放行原 `tool.invoke` 的固定 MemoryPublication 名称、摘要和专用 toolName。文件/进程等其它后端操作在示例 authorizer 拒绝；没有把记忆预授权扩成通用设备权限。

## English deployment boundary

This is a real native Windows consumer of the existing execution protocol. It composes the public SQLite publication store, reserved memory tool, Windows backend, durable executor journal and DeviceSessionHost. It does not implement a second agent or memory engine, and it does not change the SDK1 clientTools/MCP path.

The production `tansr serve` command does **not** yet wire `memoryPublicationFor`. Deploy a trusted Serve host using `createAgentSessionFactory` and `startServer`, with authenticated controller/device roles, durable stores and the actual client-managed memory identity. A local JSON file is host configuration, not server authorization. Provision its scope, source generation and domain key through trusted application administration; never infer them from model output or an unverified JWT. Keep token values out of the file and logs.

Start the native host against an existing SDK1 session, wait for Ready, and only then initiate memory reads or model work from the controller. Keep polling alive while Serve finishes memory operations. `/stop`, Ctrl+C or the native Stop action wait for local cleanup only; they do not close the remote session or prove remote memory is drained, durable or consumed. No timeout or empty poll is treated as a drain receipt. Reopen the original stores explicitly after restart; unknown outcomes and new connection owners require the original reconciliation/maintenance procedure, never automatic replay or a replacement database.

## Encryption configuration (English)

The memory-only demo requires the explicit `encryption` block above. The combined terminal-device demo also requires this block whenever `publication` is configured; terminal-only legacy configurations remain valid. Both databases receive the same explicitly selected CurrentUser DPAPI key provider, and the memory host requires an encrypted publication store and the actual encrypted execution journal. Keep the key outside the tool workspace. `create` never overwrites a key or database; `reopen` requires their original identity, capacity, format and key. Offline reads always reopen the original key, regardless of an old configuration's create mode.

Plaintext SDK formats remain supported. Migrate existing publication and journal files to separate new paths using each store's `CopyToEncryptedAsync`, verify both outcomes, then switch configuration while execution is stopped. Retain originals and failed staging files for reconciliation. Missing/corrupt keys never trigger fresh keys or empty databases. These are local storage guarantees, not proof of server drain, cloud durability, native UI acceptance or a release.
