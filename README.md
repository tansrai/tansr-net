# Tansr .NET SDK

原生 C# 接入 Tansr Serve 的 SDK，供 Windows 桌面应用与 .NET 服务使用。

当前处于开发阶段，尚未发布到 NuGet.org。两个计划产物为 `Tansr.Sdk` 与 `Tansr.Sdk.Windows`；正式能力以实际实现及验收记录为准。

开发方案、六张工程卡和24项验收的事实源位于 `J:/tansr/tansr-cli/doc/report/NETSDK-*2026-09-26.md`。本仓实施与验证记录见 [开发记录](doc/development.md)。Serve 新协议独立由 Serve 会话维护，不能将候选协议当成生产能力。

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
using var observationStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
var observation = session.ObserveAsync((item, ct) =>
{
    // 将 item 投影到 SessionView，或交给应用自己的呈现层。
    return Task.CompletedTask;
}, observationStop.Token);
await session.SendAsync("处理这项任务", cancellationToken);
// Send 返回代表受理；任务终局、存储提交和资源清理各有自己的事实。
// 退出观察不会自动中断远端任务。
observationStop.Cancel();
try { await observation; }
catch (OperationCanceledException) when (observationStop.IsCancellationRequested) { }
```

默认 SDK1 会话；SDK2 为显式 `SessionContract.Sdk2OffloadV1` 并要求稳定主体、可信 scope 及服务端能力发现。客户端 SDK 中不放长期 appkey。开发时明文 HTTP 仅在显式 `AllowInsecureLoopback=true` 且回环地址时允许；默认 handler 禁重定向和共享 Cookie。注入 `HttpClient` 的宿主须保持这些约束。

`ExecutionClient`、`ExecutionHost` 和 Windows 后端消费已经冻结的执行合同；先耐久认领，后执行，再存回执，未知副作用只对账。受控工作区的协作 CAS 要求全部写者遵守同一 owner，不能作为任意共享目录或同用户恶意进程的沙箱。启动本地命令也不等于提供 OS 沙箱，宿主仍须落实终端授权及受控执行环境。

SDK2 本地档案采用既有同步/加密格式，正文密钥可使用当前 Windows 用户 DPAPI 保存。加密不覆盖所有协议元数据；不能以加密代替应用/用户授权、删除修订和可信密钥管理。完整新记忆管理和远程输出传输仍依赖 Serve 新合同，不能据此版本宣称已经达到 Electron 全功能等价。

## 示例和开发

- [WPF](examples/WpfAssistant/README.md)：现代 Windows UI。
- [WinForms](examples/WinFormsAssistant/README.md)：.NET Framework 4.8。
- [控制台](examples/ConsoleAssistant/README.md)：原生 .NET 宿主。
- [公共能力映射](doc/compatibility/public-api-map.json)：固定16组能力，完整枚举原 SDK 与 Electron 入口，缺口保留。

```powershell
dotnet restore Tansr.Sdk.slnx --locked-mode
dotnet build Tansr.Sdk.slnx -c Release --no-restore
dotnet test tests/Tansr.Sdk.Tests -c Release --no-build
./scripts/test-windows.ps1 -OutputDirectory J:/tansr/archive/NET-05-windows-unique-run -CliRoot J:/tansr/tansr-cli
dotnet format Tansr.Sdk.slnx --verify-no-changes --no-restore
```

Windows 测试需要真实发布的原生 MCP 候选及锁定版本的原 CLI 源码。`test-windows.ps1` 要求新的输出目录，先发布 `net10.0-windows / win-x64` 自包含单文件 Console 示例，通过独立 `NuGetLockFilePath` 隔离发布所需的 RID 锁文件，核对正式锁文件未变，再恢复默认解决方案锁定资产并构建、运行一个 Windows 测试池。脚本设置并在退出时恢复 `TANSR_TEST_MCP_EXE`、`TANSR_TEST_CLI_ROOT`，保存候选摘要、日志、TRX 和 manifest。请把示例输出目录换成每次新的任务归档目录；CLI 目录须先按其锁文件安装依赖，测试会检查原协议版本。

已经由集中构建准备候选时，可显式设置 `TANSR_TEST_MCP_EXE` 为已批准的自包含单文件 `ConsoleAssistant.exe` 绝对路径、`TANSR_TEST_CLI_ROOT` 为原 CLI 源码路径，再运行原 Windows `dotnet test` 命令，避免重复发布。缺少 MCP 候选会失败，不默认记为通过；普通 apphost 的 EXE 哈希不能证明旁边的 DLL 也已获批准。此消费门通过真实 HTTP/SSE 夹具驱动共享 C# 工具宿主及原生 MCP 进程，不等于真实 Serve 模型循环或媒体内容验收已完成。

真实 Serve 路由联验另由 `scripts/serve-integration.mjs` 驱动，要求明确指定 Serve 源码路径；使用受控会话夹具而非付费模型。`scripts/check-contract.ps1` 与 `scripts/check-parity.mjs` 检查上游合同和入口变化，`scripts/test-packages.ps1` 从独立本地源消费包。未完成固定24项完整验收前，不标记正式功能齐套。

## English

Tansr provides a native C# client for Tansr Serve. The core package targets .NET Standard 2.0 and .NET 10; the Windows adapter targets .NET Framework 4.8 and modern Windows .NET. Serve owns the agent runtime, context, memory decisions and authorization. The client provides transport, local execution, durable storage and presentation. Electron keeps its existing embedded Node SDK.

This is an unpublished development candidate. It uses the existing REST/SSE and SDK2 contracts. New terminal streaming, memory management and dynamic control contracts are not exposed as stable APIs before Serve and .NET agree on the same schema and fixtures. The examples document their actual capabilities and remaining gaps; successful compilation is not a claim of full Electron parity. See the development record for commands, evidence and release status.
