# 安装、候选消费与发行

本页提供原 NET-05/P16 和 NET-06 的交付入口，不把脚本存在写成验收通过。以[开发记录](development.md)中的候选 SHA、命令退出码及安装物回执为准。当前产品尚无 NuGet 正式发布事实。

## 运行时和资产

| 消费方式 | 安装边界 | 本仓验收入口 |
|---|---|---|
| .NET Framework 4.8 WinForms | 使用系统 .NET Framework；NuGet 自动选 Windows `net48` 和核心 `netstandard2.0` | 空工程引用本地候选包，真实窗口/消息循环及 SQLite、进程消费 |
| .NET 10 Windows 控制台 | framework-dependent，由应用明确要求 .NET 运行时 | 空工程引用本地包并直接运行 apphost |
| .NET 10 WPF self-contained | 应用携带运行时与 Windows Desktop 资产；SDK 不安装全局运行时 | 检查 coreclr、hostfxr、PresentationFramework、SQLite native，真实 WPF 窗口/Dispatcher 消费 |
| 核心 Native AOT | 当前只承诺核心控制台候选验证 | `-Aot` 编译并运行真实可执行文件；不声称 WPF Native AOT |

本批消费目标为 `win-x64`。包中通用框架或上游依赖包含其它 RID 资产不等于它们已获运行验收；x86/arm64 应在对应系统按同一入口出具回执。Windows 10 的具体系统构建和运行时支持矩阵须在发行候选中固定，不能把 TFM 的最低 API 版本当成所有 Windows 10 版本均获支持。

现代 SDK、PowerShell 7、C++ AOT 工具链属于构建/验收机器的前置。终端消费应用不需要这些开发工具，也不需要 Node 客户端代理。framework-dependent 和 self-contained 必须分别说明，不能以 SDK 是“纯 C#”隐去 SQLite native 或应用运行时。

## 集中候选门

在本地完整实现合流、锁定恢复、Release build、测试、format 通过后，将两个包打到新的具名目录，再运行一次消费门：

```powershell
./scripts/test-packages.ps1 `
  -PackageDirectory J:/tansr/archive/NET-release/packages `
  -OutputDirectory J:/tansr/archive/NET-release/consumers `
  -Aot `
  -PreviousPackageDirectory J:/tansr/archive/NET-previous/packages
```

目录须改为本次实际批准的候选位置；输出必须是尚不存在的目录。脚本不发布 NuGet、不下载 Serve、不改仓库正式锁文件。它从空工程经本地 NuGet 源安装包，生成独立依赖锁和包缓存，依次运行 net48 WinForms、现代控制台、self-contained WPF，最后可选 AOT。先前同版本预览包使用另一个缓存，不会用旧字节掩盖本次变化。

运行子进程的 PATH 仅含系统目录，不继承开发机模型凭据；self-contained 子进程的 DOTNET_ROOT 指向不存在的目录。每个消费回执记录实际进程、框架/体系结构、加载的 SDK/SQLite 程序集位置及身份令牌状态。此证据证明这些消费进程不依赖 PATH 中的 Node；不是“已从开发机卸载 Node”或“已经在干净系统验证”的替代说法。

`package-audit.json` 核两个包的 ID、版本、TFM、README、net48 native 复制目标、依赖、内容及 SHA256。`dependency-audit.json` 记录恢复后的许可证声明和 native 资产摘要。遗留包只有 license URL 时保留原 URL 和待发行审查状态，不推测 SPDX；审计清单不能替发行方履行许可证/NOTICE 分发义务，也不构成包签名验证。根 `LICENSE`、`NOTICE` 及依赖原文必须随相应发行方式处理。

## 用户目录安装、升级、回滚、卸载

`-PreviousPackageDirectory` 先用当前同一公共 API 消费夹具构建旧包应用，再调用 `scripts/test-installation.ps1`。当前和旧候选字节完全相同时拒绝“升级”演练。这里比较批准候选的实际 payload，不把同预览版本的两个构建伪称两个正式版本。

独立调用也可使用已构建的消费应用：

```powershell
./scripts/test-installation.ps1 `
  -CurrentDirectory J:/tansr/archive/NET-release/consumers/net10.0-windows/bin/Release/net10.0-windows `
  -PreviousDirectory J:/tansr/archive/NET-release/consumers/previous/bin/Release/net10.0-windows `
  -OutputDirectory J:/tansr/archive/NET-release/installation-standard-user `
  -RequireStandardUser
```

这是一条可复用的安装验收入口，应用发行方仍负责其安装 UI/策略。脚本使用当前用户 `LocalApplicationData/Tansr/Acceptance/原生 SDK <唯一编号>`，不写 Program Files、注册表或全局 PATH，不提升权限。安装前拒绝重解析点，逐文件校验候选；升级与回滚通过原子替换受管版本指针选择完整目录，每次从中文和空格路径真实运行。卸载前再检查所有文件仍是原受管 payload；未知/被改文件保留，禁止递归清除用户工作目录。应用数据独立于版本目录，卸载后保留合成草稿与归属标记，其绝对路径在 manifest 中登记。

`-RequireStandardUser` 会拒绝管理员组成员（包括 UAC 过滤后的令牌）和已提权进程；不创建用户、不改组、不自称取得其它 OS 身份。默认模式记录真实身份，只能作为当前身份下的安装机制证据。干净普通用户、不同用户 DPAPI/ACL 拒绝、真实持久档案跨发行迁移仍须独立事实；安装演练中的合成草稿不替代这些项目。

## 受控本地 Serve

远端模式只需 HTTPS 服务和短期用户票据。本地模式复用 `LocalServeHost`，没有额外 C# launcher 或 Node 客户端代理：

```csharp
using Tansr.Sdk.Windows.Execution;
using Tansr.Sdk.Windows.Hosting;

using var workspace = new WindowsWorkspace(approvedWorkspace);
using var local = await LocalServeHost.StartAsync(
    new LocalServeHostOptions(approvedExecutable, approvedSha256, workspace), cancellationToken);
using var client = local.CreateClient();
// 按快速接入创建会话、运行并处理完整的远端资源收尾。
var stopped = await local.StopAsync();
if (!stopped.CleanupConfirmed || !stopped.IoSettled)
    throw new InvalidOperationException("本地 Serve 进程树或管道尚未清理完成");
```

仅消费开发者批准的独立 CLI/Serve 可执行文件，默认使用 `serve` 子命令；专用 Serve 程序可显式 `CommandPrefix = Array.Empty<string>()`。EXE 的摘要只认证该文件，普通 apphost 旁边的 DLL/配置仍须由受信安装目录及完整发行清单保证；不要把一个 apphost 的 hash 说成认证了整个应用。推荐使用已有发行流水线产生的独立 Serve 安装物，记录源 SHA、文件摘要及签名事实。SDK 不从不可信 URL 自动下载或运行代码。

监听地址、端口、`--v2`、工作区和认证参数由宿主管理。每次启动生成新的随机凭据，经环境交给自有进程；就绪探针先证伪口令被拒，再证实私有口令可访问，并对真实 TCP 对端 PID/进程句柄核归属。任意占用端口、伪服务或进程退出后端口复用不能收到认证/请求正文。就绪意味着本实例进程与基础 HTTP 合同可用，不意味着某个模型、设备记忆或可信扩展已配置。

三个示例的本地模式均使用现有 `ExampleConnection`：

| 配置 | 含义 |
|---|---|
| `TANSR_LOCAL_SERVE_EXE` / `TANSR_LOCAL_SERVE_SHA256` | 已批准安装物及其摘要；启用后不用 URL/票据输入框连接任意端口 |
| `TANSR_LOCAL_WORKSPACE` | 已存在、受控的工作区 |
| `TANSR_LOCAL_SERVE_PORT` | 可选端口，仍只监听环回 |
| `TANSR_LOCAL_SERVE_DIRECT=1` | 明确选择专用 Serve 入口 |
| `TANSR_LOCAL_ENV_NAMES` | 逗号分隔的显式环境透传名单，不整体继承开发机秘密 |
| `TANSR_LOCAL_SERVE_HOST_MODULE` / `TANSR_LOCAL_SERVE_HOST_MODULE_SHA256` | 开发者批准的绝对 `.cjs` 宿主路径和摘要；由原 Serve 装配模型、工具、存储与鉴权，不从模型消息选代码 |
| `TANSR_SERVE_USER_TOKEN` | 需要额外用户认证的开发者宿主票据；示例显式放入 `x-tansr-demo-user-token`，与本地进程随机 Bearer 分别校验 |

普通 CLI `serve` 不会凭空启用设备记忆/可信引用；需按[设备记忆](device-memory.md)和 Serve 宿主合同配置。关闭/断开本实例拥有的本地模式会停止该进程，远端模式不会关闭远端服务进程。升级/回滚先停止新派工、完成会话与本地持久回执、等待 `StopAsync` 的真实清理，再切换已批准 payload；不得在未知工具副作用时自动换进程重跑。

使用原独立 Serve 的 `--host-module` 和 `--host-module-sha256` 可加载受信 CJS 配置，复用原核心，无需客户端另装 Node。应用开发者负责模块及依赖的可信分发和权限配置；文件摘要验证不将任意 JavaScript 变为安全沙箱。宿主须独立验证最终用户身份，不能把终端自报的 `PrincipalProvider`、平台名或 `endUserId` 当作认证。需要额外票据时，直接调用者分别设置 `LocalServeHostOptions.ReadinessHeaders` 和 `CreateClient` 六参数重载的 `additionalRequestHeaders`；就绪票不会自动授予新客户端。SDK2 仍须显式声明合同与可信作用域，原 SDK1 默认不变。

可将批准的正式格式可执行候选追加给包门：`-LocalServeExecutable <绝对路径> -LocalServeSha256 <SHA256>`。它通过包内公开 `StartAsync → ListSessionsAsync → StopAsync` 验证真实产物与本机归属，失败直接保留；不启动模型，不伪造自动记忆已经装配，也不把未签名候选写成正式发行。

## 发行剩余项

本地 pack、消费、合并、推送、主线 CI、代码签名、NuGet 包签名和上传是独立事实。没有仓库 remote、渠道所有权或签名材料时登记待办，不擅自创建仓库或上传包。源码完整验收后按项目纪律收编主线，再核对远端主线 CI。当前两个包不可因本地成功自动宣称已上架。

## English

The package gate installs the two approved candidate packages into independent empty projects. It runs .NET Framework 4.8 WinForms, a framework-dependent modern Windows console, self-contained .NET 10 WPF and optional core Native AOT. It checks the real Windows Desktop/runtime/SQLite native assets and records the loaded assembly paths. Windows x64 is the consumption target; other RIDs and WPF Native AOT are not implied.

Run `scripts/test-packages.ps1 -PackageDirectory <candidate> -OutputDirectory <new-evidence-directory> -Aot`. Add `-PreviousPackageDirectory <previous-candidate>` to exercise a separately restored earlier payload and the user-directory installation lifecycle. Independent caches avoid confusing two previews with the same version. Children receive an explicit environment, system-only PATH and, for self-contained applications, a nonexistent DOTNET_ROOT. This demonstrates that those applications do not require Node on PATH; it is not a claim that the host has no Node installation or that a clean OS was used.

`test-installation.ps1` copies approved payloads into a new per-user path containing spaces and Chinese characters. It verifies the bytes, selects versions atomically, runs install/upgrade/rollback scenarios, and uninstalls only the unchanged files it owns. Synthetic user data and an ownership marker remain and are recorded. Unknown files are preserved. `-RequireStandardUser` rejects administrator group membership, including a filtered UAC token. Without it, evidence states the actual identity rather than claiming standard-user acceptance. Clean-user OS, cross-user ACL/DPAPI denial and real archive migration require their own evidence.

The content/license audit records SDK identities, targets, dependencies, hashes, declared license terms and native assets. Legacy license URLs remain explicitly subject to distribution review. It does not replace license/NOTICE obligations or signature verification.

Local Serve uses the existing `LocalServeHost`: approve an executable and digest, provide a controlled workspace, call `StartAsync`, obtain `CreateClient`, and await `StopAsync`. The host generates private credentials, validates authenticated readiness and verifies the actual TCP peer belongs to its original child process before sending credentials or bodies. It does not download code, inherit arbitrary credentials, silently connect to another port owner or add a Node client proxy. Executable hashing alone does not authenticate adjacent apphost DLLs; the complete installation remains a trusted distributor responsibility.

All three examples share `ExampleConnection`. Set `TANSR_LOCAL_SERVE_EXE`, `TANSR_LOCAL_SERVE_SHA256` and `TANSR_LOCAL_WORKSPACE`; optionally select port, dedicated Serve entry or explicit environment names. A local executable does not automatically install the platform memory/profile configuration. Stop and reconcile work before changing a version; never restart an unknown side effect. The optional package-gate parameters `-LocalServeExecutable` and `-LocalServeSha256` exercise the real approved artifact through public SDK startup/list/stop, without model calls.

For the existing Serve trusted-host entry, set `TANSR_LOCAL_SERVE_HOST_MODULE` to an approved absolute `.cjs` path and `TANSR_LOCAL_SERVE_HOST_MODULE_SHA256` to its digest. The original Serve assembles the core and deployment extensions; this requires no external client Node runtime. A verified module is developer-trusted code, not a sandbox for arbitrary JavaScript. The deployment must authenticate the end user independently of the local process Bearer and must not trust client-declared identity or platform. The examples explicitly transmit `TANSR_SERVE_USER_TOKEN` as `x-tansr-demo-user-token`. Direct consumers set `ReadinessHeaders` and the six-argument `CreateClient` overload's `additionalRequestHeaders` separately; readiness credentials are not implicitly copied to clients. SDK2 contract and trusted-scope selection remain explicit, with SDK1 the default.

Package creation, installation tests, mainline integration, CI, signing and publication are separate facts. This candidate has no automatic NuGet publication; missing ownership/signing/CI conditions remain visible.
