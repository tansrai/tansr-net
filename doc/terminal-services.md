# 终端服务预览接入

本页对应 `terminal-services-v1` 的 `2026-09-26.candidate-7`。该候选由 Serve 提供，C# 固定校验 schema 指纹 `8cd8c7c55a84c5700373aed75d5653a0737d718bfe0546641be367bda1a11896`。这是显式启用的开发预览，不代表稳定协议或正式 NuGet 发行。旧 SDK1 默认连接和原 SDK2 合同不变。

## 同会话配置与记忆

`TerminalSessionControl` 借用现有 `TansrClient` 的凭据、可信 scope 和生命周期。客户端必须配置 `ExecutionScopeProvider`，内容由可信应用宿主提供；不能从模型输入、未经验证的 JWT 或旧数据库自行推导当前授权。Serve 每次独立验证主体、权限和能力。

```csharp
var control = new TerminalSessionControl(client, enablePreview: true);
var current = await control.ReadConfigurationAsync(session.Id, cancellationToken);
using var changes = JsonDocument.Parse("{\"thinking\":{\"budget\":2048}}");
var operation = control.CreateConfigurationOperation(
    session.Id, Guid.NewGuid().ToString("N"),
    current.GetProperty("configuration").GetProperty("revision").GetInt64(),
    changes.RootElement);
// 在应用的受保护日志中耐久保存 operation.Request 和 operation.Scope，再调用：
var receipt = await control.ApplyConfigurationAsync(operation, cancellationToken);
```

思考参数是否适用于所选模型由 Serve 判定。候选仅定义 `model` 和 `thinking` 变更，其中 `thinking.budget` 是思考 token 配置；系统提示词、任务／费用预算和来源说明不在此候选字段中，不能用客户端本地状态伪装成服务端已修改。

HTTP 失回时保留原请求。配置目前没有按请求键只读查询的接口；读取当前配置不能证明某笔变更已经提交。恢复进程后，用 `RestoreConfigurationOperation(originalRequest, originalScope)` 恢复，再由应用明确选择 `ReplayConfigurationAsync`，仍使用原键和原正文。SDK 不自动重放。

记忆操作先 `ReadMemoryAsync`，再将原响应交给 `CreateMemoryOperation`。支持 `{"kind":"pin","text":"…"}`、`{"kind":"remember"}`、`{"kind":"forget","topic":"…"}`；源身份、代际和修订均来自真实响应。保存原请求后 `SubmitMemoryAsync`；失回先 `QueryMemoryAsync` 查询原操作，必要时由应用明确调用 `ReplayMemoryAsync`。空回执表示未知，不能当成写入失败或再次创建操作的依据。

这些接口只负责管理命令与原回执。Serve 继续负责记忆生成、筛选、使用和权限裁决；有记忆管理按钮并不等于终端已装配完整记忆出版存储工具。

## 分块输出与身份分离

普通原生应用可直接使用 `Tansr.Sdk.Hosting.TerminalDeviceHost`。它复用原设备宿主，把初始化、执行绑定、终端输出绑定、输出 sink 和执行通知装配成一次启动；原 `DeviceSessionHost` 和低阶接口仍保留。应用不必实现自己的领取、心跳、分块发送或 SSE 游标循环。

```csharp
using var host = new TerminalDeviceHost(
    new ExecutionClient(controller), new ExecutionClient(device),
    controllerTerminal, deviceTerminal,
    output => new WindowsExecutorBackend(
        executorId, workspaces, interpreter: trustedInterpreter,
        processFactory: trustedProcessFactory, executionOutput: output),
    durableJournal,
    new TerminalDeviceOptions {
        SessionId = session.Id,
        WorkspaceId = workspaceId,
        BindingRequestId = retainedBindingRequestId,
        RequestedTools = new[] { "Shell" }
    }, authorizeOnDevice);
await host.StartAsync(cancellationToken);
// Ready 后才能发送需要该设备的会话任务。
// 退出时先等待真实设备收尾，再释放应用所有的连接、工作区和账本。
await host.StopAsync();
```

`BindingRequestId` 必须由宿主保存到原绑定意图；失回不会自动注册新设备或重新提交绑定。两份 `TerminalConnection` 仍须显式启用预览，并持各自控制／设备凭据。设备批准回调不替代 Serve 的权限判断，工具进程仍受本机工作区、解释器与执行日志约束。

已有 `TansrClient` 时，使用 `TerminalConnection.ForClient(client, enablePreview: true)` 复用它的认证传输。受控本地 Serve 的进程归属校验、私有凭据和取消寿命一起保留，不另取票据或连接任意 localhost 端口。关闭借用的连接不关闭原客户端；原客户端关闭则终止借用连接的请求。控制端和窄权限执行端仍分别借用各自的客户端，不能因此扩大设备权限。

对于从真实输出关联接口得到的 `operation`，调用 `host.ObserveOutputAsync(operation, observer)` 即可。SDK 保留同一个 UTF-8 解码器、摘要和游标，在有界范围内只重连读取；用户回调异常、授权错误和非法协议不会重试。回调收到 `TerminalOutputUpdate`，分别给出片段、序号、缺口和封口状态；完成的输出不代表工具执行成功。`StopAsync` 等待真实在途观察退出，`Dispose` 仅请求取消，不能当作排空完成。

`TerminalDeviceHost` assembles the existing device and terminal protocols without changing
their authority boundaries. The controller and device retain separate credentials. Preserve
the original binding request identity; uncertain writes are not repeated. Output observation
keeps its decoder and cursor across bounded read-only reconnects. Await `StopAsync` before
disposing caller-owned connections, workspaces and durable storage.

控制端用 `TerminalConnection(controllerOptions, enablePreview: true)` 发现并绑定固定合同，在设备绑定完成后、首次发送前执行 `BindAsync`。绑定请求字段沿 Serve schema；能力发现不等于执行授权。

设备用自己的窄凭据创建另一 `TerminalConnection`，调用 `AttachBinding(controllerBinding)` 接收已验证的类型化绑定。SDK 要求相同服务地址和当前完整 scope，不向设备复制控制端票据。每一次输出写入仍由 Serve 验证原 executor、connection、operation 和 digest。

`CreateOutputSink` 的返回值实现 `IExecutionOutputSink`，可传给 `WindowsExecutorBackend` 的 `executionOutput` 参数。它只捕获已经授权的原执行；未知结果不会重新启动进程。失回后 `ReconcileAsync(operationId)` 只查原输出状态。`ReleaseCapture` 明确放弃该内存来源的重放能力。

控制端通过 `ObserveOutputAsync` 接收原始 SSE 事件，将其交给同一 `TerminalOutputView`，跨重连保留该视图及已实际应用的序号。视图处理 UTF-8 跨块、重复、缺口和封口摘要。输出封口、耐久水位、执行回执是不同事实；不能把 EOF 或完整文本当作进程执行成功。

每连接最多 8 个输出 sink、8 条观察；同一操作只允许一条观察。每 sink 最多 8 个保留捕获或实际在途泵。取消不释放仍在执行的回调和请求所占限额；`CloseAsync` 等原泵退出，`Dispose` 仅发起收尾。宿主注入的 HTTP handler 和回调仍须遵守取消。

### 设备派工通知与取消

`DeviceSessionOptions.ExecutionNotifications` 在 `AfterBindingAsync` 完成后调用一次，装配失败不会返回 Ready。将上一节已使用设备凭据附着的绑定交给适配器：

```csharp
deviceOptions.ExecutionNotifications = (connection, cancellationToken) =>
    Task.FromResult<IExecutionNotificationSource>(
        new TerminalExecutionNotifications(deviceTerminal, deviceBinding));
```

所需命名空间是 `Tansr.Sdk.Execution`。`deviceBinding` 必须来自 `AfterBindingAsync` 中已确认的原绑定，并已获准 `execution-stream-v1`；控制凭据和设备凭据仍分离。SDK 自行维护通知 SSE、原游标及重连，不需要应用编写心跳或派工循环。原轮询保留为有界兜底；通知只唤醒领取，取消通知先查询完整原操作状态，绝不直接执行通知正文或把断线当作副作用回滚。

`reconcile-required` 可以从较低游标重开通知窗口，它不重置工具输出的序号、耐久水位或执行账本。网络故障仅重新打开原通知连接；401/403、失效代际、绑定冲突及不支持能力会使宿主失败，不自动登记新设备或降级。可从 `DeviceSessionHost.LastNotificationErrorCode` 读取不含秘密的暂时错误码，最终失败必须观察 `Completion`。

停止设备时等待 `StopAsync`，再释放调用者拥有的 TerminalConnection、输出 sink 和本地存储。若自定义通知回调忽略取消，SDK 在五秒后明确报告 `execution_notifications_stop_timeout`，不会将仍在占用的观察槽当作已释放。

The optional notification factory runs once after binding and before the device reports Ready.
Pass the device's authenticated terminal binding to `TerminalExecutionNotifications`; the SDK
handles SSE wake-ups, the original cursor, reconnects, and status reconciliation. Notifications
never grant tool authority or prove that a side effect was rolled back. Legacy callers keep the
existing polling behavior. Await `StopAsync` before disposing caller-owned connections and stores;
a callback that ignores cancellation produces an explicit shutdown timeout, not a successful stop.

## 档案 ACK 恢复

独立的 `sdk2-archive-recovery-v1` 扩展保留原 ACK 和旧控制消息大小限制。恢复请求上限为 263,168 字节，响应为 528,384 字节；内部 ACK/receipt 仍各受旧 262,144 字节限制。

新恢复介质通过 `SqliteArchiveStore.OpenRecoverableAsync` 显式选择；旧 `OpenAsync` 不自动迁移。此批支持明文同步 source 格式 `sdk2-archive-sync-recovery-sqlite-v1`。原明文和加密格式继续可用，但加密恢复、普通 terminal recovery 文件族不能冒称已支持。迁移必须明确选择 `StorageOpenMode.MigrateV1`，旧 reader 会拒绝新格式。

恢复顺序是：保留原 pending ACK → 先耐久固定恢复意图并预留完整回执空间 → 请求 Serve 重基 → 验证原键、代际、覆盖和摘要 → 同事务记录恢复证明、迁移当前批次身份并确认。原 ACK 若已经提交，保存的是旧真实回执。响应丢失或 COMMIT 结果不明时重开后核对原意图和原回执，不重复接收正文、不重新执行模型或工具。

SDK 的恢复协调器和 SQLite 介质是两个独立能力，须明确装配；仅看到对象实现某接口不能证明所打开文件允许恢复。双副本必须共同固定相同恢复意图，未对齐前不继续常规写入或保留策略操作。

## Windows 进程与目录保护

原生进程运行期间固定工作目录和可执行文件的身份；目录祖先允许正常文件写入，仍禁止替换或删除这些目录。可执行文件保持禁止写入／替换，并从同一文件句柄校验摘要。可写工作目录中会临时创建一个 SDK 保留前缀的随机锚文件，使空目录也不能在运行中变为重解析点；该对象在句柄关闭时自动删除，不扫描或清理用户文件。明确只读的工作目录沿用末级目录保护，不对其祖先或整盘加拒写锁。

这套保护与原 Job 进程树回收共同工作，不代替操作系统沙箱，也不改变核心权限裁决。应用自己的草稿、媒体与其他目录的原子保存不应被工具进程阻止。

## 保留的边界

WPF、WinForms 和 Console 示例的本地草稿／显示历史属于应用界面缓存，可离线回看，不作为可信模型供材。另有 `NativeOfflineStorageReader` 从已授权本地档案／记忆介质只读打开；它不联网重建会话、不确认当前成员关系，也不允许离线材料越过新的在线授权直接供模型采用。原 SDK2 在线材料提供仍要求当前可信授权，不能用离线快照自证撤权状态。

本批无真实模型收费请求、NuGet 发布或生产部署。2026-09-27的真实Serve记忆链已覆盖生成、终端落盘、检索、后续采用、维护、删除及旧writer/备份来源拒绝；缓存续接链也已覆盖原键失回、重启、跨runtime、删除和自然票到期。详见`archive/20260927-NET-full-delivery/http-r4-retry/runner.log`，该池整体27/33，以上成功子链不能替代其余失败链或完整界面验收。完整 Electron 能力对照、原生界面、性能与发行条件继续按原六张工程卡、24项验收记录。
