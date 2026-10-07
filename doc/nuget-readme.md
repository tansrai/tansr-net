# Tansr .NET SDK

原生 C# 客户端，用于将 .NET 服务和 Windows 应用接入独立部署的 Tansr Serve。当前包版本为 `0.2.0`；显式预览 API 和能力仍须与所连接 Serve 的合同及能力发现结果匹配。

## 选择与安装

| 包 | 目标框架 | 用途 |
|---|---|---|
| `Tansr.Sdk` | `netstandard2.0`、`net10.0` | HTTP/SSE、会话、执行协调、存储接口和界面数据投影 |
| `Tansr.Sdk.Windows` | `net48`、`net10.0-windows` | Windows 文件／进程、SQLite、DPAPI、本地档案与受控 Serve 宿主；自动依赖核心包 |

在提供此版本的 NuGet 源中，按应用需要选择一个包并锁定版本：

```powershell
# 只需连接 Serve 的 .NET 应用
dotnet add YourApp.csproj package Tansr.Sdk --version 0.2.0

# 需要 Windows 本机能力的应用
dotnet add YourApp.csproj package Tansr.Sdk.Windows --version 0.2.0
```

本版实际消费验证范围为 Windows x64，包括 .NET Framework 4.8、现代 .NET 控制台、WPF self-contained 和核心控制台 Native AOT。TFM 不等同于所有 Windows 版本或 CPU 架构均已实测；不承诺 WPF Native AOT。SDK 是库，不携带完整 .NET 运行时；由应用选择系统运行时或自包含发布。Windows 适配使用 SQLite 原生依赖，不能当成纯托管单文件分发。

## 连接与一轮会话

Serve/kernel 统一管理模型循环、会话、上下文、记忆决策、权限与用量。它可以部署在远端，也可以作为开发者批准的独立本地进程运行；这两个 NuGet 包不包含或自动下载 Serve 核心。C# 客户端运行无需 Node，已有 Electron 应用仍沿用完整 SDK 和 IPC 模式。

下面沿用公开会话 API。应用提供自己的 HTTPS Serve 地址和短期令牌获取函数；长期 appkey 应留在开发者服务端。

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

public static class AgentExample
{
    public static async Task RunAsync(
        Uri serveUri,
        Func<CancellationToken, Task<string>> getShortLivedToken,
        CancellationToken cancellationToken)
    {
        using var client = new TansrClient(new TansrClientOptions
        {
            BaseUri = serveUri,
            TokenProvider = getShortLivedToken,
        });
        var session = await client.CreateSessionAsync(
            new CreateSessionOptions(), cancellationToken);
        using var run = session.StartRun("查询这笔订单", observer: (item, ct) =>
        {
            // 将事件交给应用界面；WPF/WinForms 应切换到 UI 线程。
            // Render events on the application's UI thread where required.
            return Task.CompletedTask;
        }, cancellationToken: cancellationToken);
        await run.Acceptance;
        var result = await run.Completion;
        Console.WriteLine($"{result.Reason}; aborted={result.WasAborted}");
    }
}
```

`Acceptance` 只表示接纳，`Completion` 才是轮次终值；仍须检查原因与 `WasAborted`。多轮对话复用同一会话。取消本地等待、释放 `run` 或断开连接不会自动中断远端任务；显式调用 `session.CancelAsync` 才请求中断。应用还须按服务端实际清理回执管理会话和工具资源，不能把 HTTP 202 当成清理完成。

默认使用 HTTPS。明文开发连接仅在显式 `AllowInsecureLoopback=true` 且地址为环回时允许。SDK 自管传输禁止重定向和共享 Cookie；注入 `HttpClient` 时由宿主保持这些约束。`TokenProvider` 每次请求提供当前短期票据，不应返回长期平台密钥。

会话默认使用 SDK1。SDK2 档案卸载需要显式选择 `SessionContract.Sdk2OffloadV1`，并提供稳定的可信主体／scope、必要存储和经发现确认的 Serve 能力；不能仅打开一个开关就跳过鉴权或能力协商。SDK2 的设备记忆、终端执行和缓存连续性属于显式预览能力，核心决策仍在 Serve。终端文件／命令能力由应用授权并限制工作区；本地命令执行本身不提供 OS 沙箱。

## English

Tansr is a native C# client for a separately deployed Tansr Serve instance. The package version is `0.1.0.2`. Match optional preview APIs to the server's negotiated contracts and capabilities.

Choose `Tansr.Sdk` for transport, sessions, execution coordination, storage interfaces and view data. It targets .NET Standard 2.0 and .NET 10. Choose `Tansr.Sdk.Windows` for Windows files/processes, SQLite, DPAPI, local archives and controlled local Serve hosting; it targets .NET Framework 4.8 and modern Windows .NET and depends on the core package. Use either exact-version installation command above with a NuGet source that provides this version.

The tested consumer scope is Windows x64: .NET Framework 4.8, a modern console, self-contained WPF and core console Native AOT. Other architectures, every historical Windows version and WPF Native AOT are not implied. These packages are libraries, not complete .NET runtimes. Your application chooses a framework-dependent or self-contained distribution and must include the Windows adapter's SQLite native dependencies.

Serve/kernel owns the agent loop, context, memory decisions, permissions and usage. Deploy it remotely or as an approved local service; these packages neither embed nor automatically download it. C# consumers do not require Node. Existing Electron applications retain their integrated SDK and IPC model.

The complete example above accepts your HTTPS Serve address and an application-owned short-lived token provider. Keep long-lived appkeys on your backend. `Acceptance` means that a run was accepted; await `Completion` and inspect both its reason and `WasAborted`. Reuse the session for another turn. Cancelling local observation or disposing a run does not stop remote work; call `session.CancelAsync` to request interruption and observe the server's actual cleanup state separately.

HTTPS is the default. Plain HTTP is limited to explicit loopback development with `AllowInsecureLoopback=true`. Preserve redirect and shared-cookie restrictions when supplying your own `HttpClient`. Refresh short-lived credentials through `TokenProvider`; do not put a platform secret in the client.

SDK1 is the default session contract. SDK2 archive offload requires explicit `SessionContract.Sdk2OffloadV1`, a stable trusted principal/scope, configured storage and successful capability discovery. Device memory, terminal execution and cache continuity are explicit preview integrations, not a second client-side agent kernel. Authorize local tools and constrain their workspace; a command launcher alone is not an OS sandbox.

## License

MIT. `LICENSE` and `NOTICE` accompany both packages. Runtime and native dependencies retain their own licenses and distribution requirements.
