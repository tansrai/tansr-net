# Tansr .NET SDK

原生 C# 接入 Tansr Serve 的 SDK，供 Windows 桌面应用与 .NET 服务使用。

两个产品包已在NuGet.org发布 `0.1.0.2`：[Tansr.Sdk](https://www.nuget.org/packages/Tansr.Sdk/0.1.0.2) 与 [Tansr.Sdk.Windows](https://www.nuget.org/packages/Tansr.Sdk.Windows/0.1.0.2)。发布相关源码已合入main并推送，主线CI通过；公开下载包的内容和NuGet仓库签名已验证。原NET-06/A24已关闭；额外作者签名属于可选增强，不是本版未完成条件。具体源码、验收范围和结算依据见[开发记录](doc/development.md)。

开发方案、六张工程卡和24项验收的事实源位于 `J:/tansr/tansr-cli/doc/report/NETSDK-*2026-09-26.md`。本仓实施与验证记录见 [开发记录](doc/development.md)。Serve 新协议独立由 Serve 会话维护，不能将候选协议当成生产能力。

验收中的 `CliRoot` 必须检出 `WireContract.SourceRevision` 对应的原协议基线（当前为 `027de7e2d9b647374b7fe94cb0e4a7429a9195e2`），不能以持续前进的 CLI 主线代替。`RecoveryCliRoot` 则指当前会签的恢复及设备记忆候选；两个来源分别记录。

## 包与运行边界

| 包 | 框架 | 职责 |
|---|---|---|
| `Tansr.Sdk` | `netstandard2.0;net10.0` | HTTP/SSE、会话、控制合同、执行协调、存储接口、会话视图 |
| `Tansr.Sdk.Windows` | `net48;net10.0-windows` | Windows 文件/进程、本地 SQLite、DPAPI 与档案正文加密 |

模型循环、上下文组装、记忆决策、权限裁决和计量在 Serve。客户端不包含另一套内核。已有 Electron 继续使用完整 `@tansr/sdk` 与 IPC，此仓不替换它的依赖或运行方式。

.NET Framework 应用需要系统运行时；现代 .NET 应用可以由应用开发者自包含发布。SDK 本身是库，不替应用安装框架。产品 C# 运行不依赖 Node；Serve 是独立服务，本地或远端部署。当前只对本机 Windows x64 进行消费验证，其它体系结构与发行条件见记录。

## 会话接入

```csharp
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

using var client = new TansrClient(new TansrClientOptions
{
    BaseUri = new Uri("https://your-serve.example"),
    TokenProvider = ct => YourLoginService.GetShortLivedTokenAsync(ct),
});
var session = await client.CreateSessionAsync(new CreateSessionOptions(), cancellationToken);
using var run = session.StartRun("处理这项任务", observer: (item, ct) =>
{
    // 将 item 投影到 SessionView，或交给应用自己的呈现层。
    return Task.CompletedTask;
}, cancellationToken: cancellationToken);
await run.Acceptance; // 接纳与任务完成分开。
var completed = await run.Completion;
Console.WriteLine(completed.Reason); // 同时检查 WasAborted，不能把任何终态都算成功。
// 取消/Dispose run 只停止本地等待；显式 session.CancelAsync 才中断远端任务。
// 会话关闭接纳与远端持久化/资源排空同样分开。
```

默认 SDK1 会话；SDK2 为显式 `SessionContract.Sdk2OffloadV1` 并要求稳定主体、可信 scope 及服务端能力发现。客户端 SDK 中不放长期 appkey。开发时明文 HTTP 仅在显式 `AllowInsecureLoopback=true` 且回环地址时允许；默认 handler 禁重定向和共享 Cookie。注入 `HttpClient` 的宿主须保持这些约束。

`ExecutionClient`、`ExecutionHost` 和 Windows 后端消费已经冻结的执行合同；先耐久认领，后执行，再存回执，未知副作用只对账。受控工作区的协作 CAS 要求全部写者遵守同一 owner，不能作为任意共享目录或同用户恶意进程的沙箱。启动本地命令也不等于提供 OS 沙箱，宿主仍须落实终端授权及受控执行环境。

SDK2 本地档案采用既有同步/加密格式，正文密钥可使用当前 Windows 用户 DPAPI 保存。加密不覆盖所有协议元数据；不能以加密代替应用/用户授权、删除修订和可信密钥管理。设备记忆、远程增量输出、配置控制和缓存连续性已有公开的显式预览消费入口，由 Serve 的独立合同约束，不替换原稳定协议。完整 Electron 对照、真实界面、性能与跨端联验仍按原验收单结算。

## 示例和开发

原生窗口提供“提示词来源”，Console 提供 `/prompt`。开发者可用 `await session.ReadApplicationPromptAsync(ct)` 获取只读 `ApplicationPromptState`；先检查 `IsKnown`，再读取 `Policy`/`Source`。该方法显式请求已应用的来源与策略，既有 `GetMetadataAsync`/`ReadMetadataAsync` 默认请求不变。`sdk` 指开发者/Serve 可信宿主段，终端不获得提示词正文或修改权；详见[示例说明](examples/Shared/session-controls.md)。

终端分块输出、同会话配置／记忆管理和 ACK 恢复已有显式候选消费入口；参见[终端服务预览接入](doc/terminal-services.md)。预览与稳定协议、管理命令与完整记忆本地化、代码实现与正式发行分别记录。

设备自动记忆可通过 `SqliteMemoryPublicationStore` 和 `WindowsMemoryPublicationHost` 接入原执行管道，也可显式注入实现相同存储接口的第三方介质；三种示例共用[设备记忆配置入口](examples/Shared/device-memory.md)。Serve 负责提取、检索和删除，客户端持久化原 Node 兼容的 publication 介质。可信 Serve 宿主须配置 `memoryPublicationFor`，本地 Serve 可用显式的受信 `--host-module` 装配；默认 CLI 启动不自动启用。参见[身份、容量与恢复边界](doc/device-memory.md)。

- [WPF](examples/WpfAssistant/README.md)：现代 Windows UI。
- [WinForms](examples/WinFormsAssistant/README.md)：.NET Framework 4.8。
- [控制台](examples/ConsoleAssistant/README.md)：原生 .NET 宿主。
- [中文快速接入](doc/quickstart.md) / [English quickstart](doc/quickstart.en.md)：包安装、会话、运行与错误处理。
- [安装与发行](doc/installation.md)：运行时、原生资产、本地 Serve、升级回滚及可执行消费门。
- [公共能力映射](doc/compatibility/public-api-map.json)：固定16组能力，完整枚举原 SDK 与 Electron 入口，缺口保留。

```powershell
dotnet restore Tansr.Sdk.slnx --locked-mode
dotnet build Tansr.Sdk.slnx -c Release --no-restore
dotnet test tests/Tansr.Sdk.Tests -c Release --no-build
./scripts/test-windows.ps1 -OutputDirectory J:/tansr/archive/NET-05-windows-unique-run -CliRoot J:/tansr/worktrees/cli-NET-legacy-contract -RecoveryCliRoot J:/tansr/worktrees/cli-SRV-01-terminal-services
dotnet format Tansr.Sdk.slnx --verify-no-changes --no-restore
```

Windows 测试需要真实发布的原生 MCP 候选及锁定版本的原 CLI 源码。`test-windows.ps1` 要求新的输出目录，先发布 `net10.0-windows / win-x64` 自包含单文件 Console 示例，通过独立 `NuGetLockFilePath` 隔离发布所需的 RID 锁文件，核对正式锁文件未变，再恢复默认解决方案锁定资产并构建、运行一个 Windows 测试池。脚本设置并在退出时恢复 `TANSR_TEST_MCP_EXE`、`TANSR_TEST_CLI_ROOT`、`TANSR_TEST_RECOVERY_CLI_ROOT`，保存候选摘要、日志、TRX 和 manifest。请把示例输出目录换成每次新的任务归档目录；CLI 目录须先按其锁文件安装依赖，测试会检查原协议版本。

已经由集中构建准备候选时，可显式设置 `TANSR_TEST_MCP_EXE` 为已批准的自包含单文件 `ConsoleAssistant.exe` 绝对路径、`TANSR_TEST_CLI_ROOT` 为原 CLI 源码路径，再运行原 Windows `dotnet test` 命令，避免重复发布。缺少 MCP 候选会失败，不默认记为通过；普通 apphost 的 EXE 哈希不能证明旁边的 DLL 也已获批准。此消费门通过真实 HTTP/SSE 夹具驱动共享 C# 工具宿主及原生 MCP 进程，不等于真实 Serve 模型循环或媒体内容验收已完成。

真实 Serve 路由联验另由 `scripts/serve-integration.mjs` 驱动，要求明确指定 Serve 源码路径；使用受控会话夹具而非付费模型。`scripts/check-contract.ps1` 与 `scripts/check-parity.mjs` 检查上游合同和入口变化。统一 API 合同（UAPI-01）下，SDK 只走 `/api` 入口：路径常量模块 `Tansr.Sdk.Api.ApiRoutes` 由 `node scripts/generate-api-routes.mjs` 从 vendored `contract/api-manifest.json` 生成（`--check` 核对生成物未漂移）；每个响应必须带 `tansr-contract: unified-v1` 等四个头，缺失或异族抛 `ContractUnavailableException`，不回退旧前缀；统一错误信封解码为 `UnifiedApiException`（`Code/StatusCode/RetryAction` 为统一值，`DomainCode/DomainStatus/DomainRetryAction` 保留原族事实）；`TansrClientOptions.NegotiateEventEnvelope` 显式协商 `tansr-event-envelope: unified-v1`，服务端未回响抛 `EnvelopeNotNegotiatedException`。详见 [contract/README.md](contract/README.md)。`scripts/test-packages.ps1` 从独立本地源安装两个包，实际运行 net48 WinForms、现代控制台、WPF self-contained 和可选 Native AOT；`-PreviousPackageDirectory` 增加独立旧包消费与用户目录升级/回滚/卸载演练。它们不替代固定24项完整验收，详见[消费与安装入口](doc/installation.md)。

`RecoveryCliRoot` 单独指定包含已锁定 ACK 恢复 receiver 的源码目录；新 Node 互通检查其 schema 和实现指纹，不借此放宽原 `CliRoot` 的 SDK2 合同锁。直接运行测试时也须显式设置 `TANSR_TEST_RECOVERY_CLI_ROOT`；缺少此环境的跳过不能算作跨实现恢复通过。Windows 正确性门不接受依赖缺失造成的跳过；同机 Electron 时序性能测试单独准备并记录，尚未运行时不能关闭对应性能验收。

本地候选包的消费范围包括 net48 CLR4 WinForms、现代控制台、WPF self-contained 和核心 Native AOT；消费进程不依赖 PATH 中的 Node。安装／升级／回滚／卸载、普通用户权限及文件／密钥隔离各有独立验收，普通用户环境为现有 Windows 主机的新用户配置。每个候选的源码、包指纹、实际结果及剩余条件统一见[开发记录](doc/development.md)，不将旧候选的结果冒充新包实证；NuGet已发布版本为 `0.1.0.2`。

## English

Tansr provides a native C# client for Tansr Serve. The core package targets .NET Standard 2.0 and .NET 10; the Windows adapter targets .NET Framework 4.8 and modern Windows .NET. Serve owns the agent runtime, context, memory decisions and authorization. The client provides transport, local execution, durable storage and presentation. Electron keeps its existing embedded Node SDK.

The explicit device-memory preview uses `SqliteMemoryPublicationStore` and `WindowsMemoryPublicationHost` through the existing execution pipeline, or a caller-supplied implementation of the storage interface. The trusted Serve host must install `memoryPublicationFor`; local Serve supports explicit trusted `--host-module` assembly, while the default CLI launcher does not enable it automatically. The examples share explicit configuration for source identity, separate control/device credentials, storage creation or reopening, and capacity. See [device memory](doc/device-memory.md) for recovery and lifecycle boundaries.

Use `await session.ReadApplicationPromptAsync(ct)` to explicitly observe the applied application prompt policy and source. Check `IsKnown` before reading `Policy` and `Source`; missing, invalid or non-live observations remain unknown. Existing metadata methods keep their default requests unchanged. `sdk` denotes the trusted developer/Serve host segment, not a client-side override. This observation never supplies prompt text or write authority. The desktop examples expose a prompt-source button and the console exposes `/prompt`.

Version `0.1.0.2` is available on NuGet.org as [Tansr.Sdk](https://www.nuget.org/packages/Tansr.Sdk/0.1.0.2) and [Tansr.Sdk.Windows](https://www.nuget.org/packages/Tansr.Sdk.Windows/0.1.0.2). Mainline CI passed; downloaded package contents and NuGet repository signatures were verified. NET-06/A24 are complete. Additional author signing is optional, not an unmet requirement of this release. The SDK uses the existing REST/SSE and SDK2 contracts. Terminal streaming, memory management and dynamic control previews follow the agreed Serve schemas and fixtures. See the development record for the exact capability mapping, source identities, commands, evidence and validation boundaries.

Start with the [English quickstart](doc/quickstart.en.md) and the [installation and distribution guide](doc/installation.md#english). Local package gates cover .NET Framework WinForms, a modern console, self-contained WPF and core Native AOT without Node on the consumer process PATH. Installation, upgrade, rollback, uninstall and standard-user file/key isolation have separate evidence. The standard-user environment is a fresh profile on the existing Windows host. Consult the [development record](doc/development.md) for each candidate's exact source, package hashes, results and remaining gates; an earlier package's result does not certify a later package. Pin the published NuGet version to `0.1.0.2`.
