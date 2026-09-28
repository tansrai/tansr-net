# 安装、候选消费与发行

本页提供原 NET-05/P16 和 NET-06 的交付入口，不把脚本存在写成验收通过。以[开发记录](development.md)中的候选 SHA、命令退出码及安装物回执为准。两个产品包已发布至NuGet.org，版本为 `0.1.0.2`；公开包下载、内容和NuGet仓库签名已验证，原NET-06/A24已关闭。额外作者签名属于可选增强，不是本版未完成条件。

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

`package-audit.json` 核两个包的 ID、版本、TFM、README、net48 native 复制目标、依赖、内容及 SHA256。两产品包现在显式包含根 `LICENSE` 和 `NOTICE`；发行检查应使用 `scripts/audit-packages.ps1 -RequireNotices`，缺少任一文件即失败，默认不强制的入口仍可审查历史候选。`dependency-audit.json` 记录恢复后的许可证声明和 native 资产摘要。遗留包只有 license URL 时保留原 URL 和待发行审查状态，不推测 SPDX；包内NOTICE不替代各依赖完整许可证分发义务，内容审计也不构成签名验证。

## 从候选包构建三个原示例

在仓库根目录使用 PowerShell 7，指定同批实际两包和一个尚不存在的归档目录：

```powershell
./scripts/build-package-examples.ps1 `
  -PackageDirectory J:/tansr/archive/NET-release/packages `
  -OutputDirectory J:/tansr/archive/NET-release/package-examples `
  -Version 0.1.0.2
```

`PackageDirectory`、`OutputDirectory` 为必填参数；`Version` 可省略，当前默认 `0.1.0.2`，必须与两包的实际版本一致。脚本复制原 `ConsoleAssistant`、`WpfAssistant`、`WinFormsAssistant` 和 `Shared`，保留原始快照；只在消费副本中将 `ProjectReference` 换成精确版本的 `PackageReference`，并移除 WinForms 的源码 targets 导入，改由包内 `buildTransitive/net48` 目标接管。业务代码、原框架及程序集引用不变，不构建 SDK 源工程，也不改原示例或仓库锁文件。

两个包按 SHA256 复制到私有 `candidate-feed`；独立 NuGet 源映射和缓存防止命中其它同版本预览包。恢复后核对实际包字节、保存依赖锁，并确认构建期间锁及源码副本未变。输出 `executables/ConsoleAssistant`（.NET 10 Windows framework-dependent）、`executables/WpfAssistant`（win-x64 self-contained）、`executables/WinFormsAssistant`（net48 x64）。`manifest.json` 记录原文件／副本映射、源码与包摘要、命令、依赖锁和全部产物。

`example-paths.json` 提供以下路径键，传给既有验收入口即可消费这次产物：

| 路径键 | 既有入口 |
|---|---|
| `TANSR_NATIVE_CONSUMER_EXAMPLE` | `scripts/native-serve-consumer.mjs` 的 Console 本地／远端 Serve 消费 |
| `TANSR_NATIVE_UI_WPF`、`TANSR_NATIVE_UI_WINFORMS` | `scripts/native-ui-integration.mjs` 的两种原生 UI 消费 |

运行时仍需提供原入口要求的 Serve 候选、源码快照及批准配置。此构建脚本不启动示例、不调用模型；`built-not-executed` 仅说明候选包构建完成。实际 Console／UI 运行及资源收尾须另有回执，不能以构建结果替代。

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

## 主线 CI 接线与本地入口

`.github/workflows/ci.yml` 已接线，仅在 `main` 推送或针对 `main` 显式手动触发时运行；不为开发分支或PR运行远端CI。工作流在 `windows-2025` 使用 `global.json` 固定的.NET SDK（当前10.0.301、`rollForward: disable`）及Node22.22.1，权限为 `contents: read`，不克隆其它私库、不签名、不发布，也不保存检出凭据。源码已推送至公开MIT仓库 [tansrai/tansr-net](https://github.com/tansrai/tansr-net)；实际主线CI结果见[运行记录](https://github.com/tansrai/tansr-net/actions)及[开发记录](development.md)。SDK升级时须一起审查SDK隐式工具依赖和锁文件，不能让CI自行滚动补丁版本后跳过锁定恢复。

共享入口要求Windows x64、64位PowerShell7、对应.NET SDK、Node及本地AOT所需C++工具链。输出目录必须是仓库外的新目录。先只列本批计划与缺失范围：

```powershell
./scripts/test-ci.ps1 `
  -OutputDirectory J:/tansr/archive/NET-release/ci-plan `
  -PlanOnly
```

`-PlanOnly` 记录源码/锁文件指纹与步骤，结果为 `planned-not-run`，不恢复、构建、测试或打包。要执行本地基础门，去掉该开关并选择另一新目录；可选输入如下，均须是批准的既有路径：

| 参数 | 实际范围 |
|---|---|
| `OutputDirectory` | 必填；输出manifest、步骤日志、TRX、两包、消费者与示例构建回执 |
| `CliRoot` | 原冻结CLI源码与已安装依赖；启用原合同/映射/会话对照及旧存储互通 |
| `RecoveryCliRoot` | 冻结恢复协议源码与已安装依赖；启用原恢复账本和记忆出版互通 |
| `PreviousPackageDirectory` | 已批准旧两包；启用旧公开API兼容及安装/升级/回滚，不能用新包自己充当旧基线 |

执行入口串行运行仓内合同/能力映射检查、MCP消费产物准备、锁定restore、Release build、Core与适用Windows用例、独立sandbox金样、format、两包pack、严格LICENSE/NOTICE审计、包含AOT的包消费和三个原示例构建。包版本取仓库 `Directory.Build.props`，不是可临时覆盖的CI参数。失败立即停止后续步骤；零用例、未执行或失败用例不写成通过；运行期间源码或正式锁变化也失败。

默认未提供两份CLI源码时，明确排除四个跨仓互通方法及其参数项；Electron/Serve性能基准始终走其原独立入口。真实Serve联验、五客户端、GUI、标准用户OS、签名和发布均不由此脚本补造。`manifest.json` 的 `scope.excludedWindowsMethods`、`scope.notRun`、每步状态及TRX列出实际范围；即使本入口通过，状态仍为 `passed-with-external-gates-not-run`，不能将这些未运行项或A01—24全单视为新通过。工作流只上传 `evidence/` 白名单内日志、回执和未签名候选包，不上传私有缓存或完整self-contained应用目录。

## 只读发行候选检查

`scripts/test-release.ps1` 不签名、不创建证书、不上传包；它持有原包及检查副本，核两包内容并实际调用 `dotnet nuget verify --all`，最后复核字节未变。使用PowerShell7和.NET SDK；签名校验仍依赖主机正常证书根与吊销策略，不表示全程断网。

```powershell
./scripts/test-release.ps1 `
  -CorePackage J:/tansr/archive/NET-release/packages/Tansr.Sdk.0.1.0.2.nupkg `
  -WindowsPackage J:/tansr/archive/NET-release/packages/Tansr.Sdk.Windows.0.1.0.2.nupkg `
  -Version 0.1.0.2 `
  -OutputDirectory J:/tansr/archive/NET-release/release-check `
  -RequireNotices
```

以上四个值参数必填，输出目录必须不存在。可同时提供 `-CoreSha256` 和 `-WindowsSha256` 锁定已批准字节，不能只给其中一个；均为64位十六进制。`-VerifyTimeoutSeconds` 默认120，允许1—600秒，只控制每个dotnet校验进程超时。回执为 `manifest.json`、`package-audit.json` 和原verify标准输出/错误日志，检查副本保存在 `packages/`。

默认允许**记录未签名事实**：仅在包没有签名且真实校验唯一诊断为 `NU3004` 时记 `readiness=unsigned`；检查流程可退出0，但 `signaturesVerified=false`。有签名却校验失败、其它校验不可用/超时或字节变更一律失败。`-RequireSigned` 拒绝未签名包；`-CertificateFingerprint <批准证书SHA256>` 传入实际verify，要求两个包均验证该签名者，即使不加RequireSigned也不能以unsigned成功。不传指纹时 `signerPinned=false`，有效信任链不证明签名者获本项目批准。所有模式的 `releaseReady`、`published` 均保持false，不把本地内容/签名检查等同于完整发行或渠道授权。

## 发行剩余项

本地 pack、消费、合并、推送、主线 CI、代码签名、NuGet 包签名和上传是独立事实。没有仓库 remote、渠道所有权或签名材料时登记待办，不擅自创建仓库或上传包。源码完整验收后按项目纪律收编主线，再核对远端主线 CI。当前两个包不可因本地成功自动宣称已上架。

### 可选：本机作者证书签名与校验

以下是以后选择增加作者签名时的操作入口，不是当前NuGet发行的缺失条件。作者签名只使用发行负责人批准、已安装在本机 `CurrentUser/My` 且带可用私钥的正式代码签名证书，以及批准的时间戳服务。本轮该存储中尚无有效可用签名证书，未执行作者签名；已发布包的仓库签名核验见开发记录。不导出或远端托管私钥，不把密码放入命令，不用自签证书代替正式发行。

使用仓库要求的 .NET 10 SDK。`$certificateSha256` 是证书内容的64位十六进制SHA256指纹，不是Windows常见的SHA1 `Thumbprint`；.NET 10的签名命令要求SHA-2指纹。[Microsoft签名文档](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-sign)

```powershell
# 替换为已批准候选、证书SHA256及时间戳服务；signed目录必须是新目录。
$candidate = 'J:/tansr/archive/NET-release/packages'
$signed = 'J:/tansr/archive/NET-release/signed'
$certificateSha256 = '<approved-certificate-sha256>'
$timestampUrl = '<approved-rfc3161-timestamp-url>'
dotnet nuget sign `
  "$candidate/Tansr.Sdk.0.1.0.2.nupkg" `
  "$candidate/Tansr.Sdk.Windows.0.1.0.2.nupkg" `
  --certificate-store-location CurrentUser --certificate-store-name My `
  --certificate-fingerprint $certificateSha256 `
  --hash-algorithm SHA256 --timestamp-hash-algorithm SHA256 `
  --timestamper $timestampUrl --output $signed
```

显式新输出目录保留原未签名包，不使用 `--overwrite`。签名成功后，在已具备有效代码签名及时间戳信任链的机器上核验两个新包：

```powershell
dotnet nuget verify `
  "$signed/Tansr.Sdk.0.1.0.2.nupkg" `
  "$signed/Tansr.Sdk.Windows.0.1.0.2.nupkg" `
  --all --certificate-fingerprint $certificateSha256
```

记录命令退出码、签名前后包SHA和校验回执；指纹匹配用于绑定批准签名者，不能证明NuGet账号的包所有权或上传授权。NuGet包签名也不等于应用EXE的Authenticode签名。[Microsoft校验文档](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify)

如以后启用额外作者签名，对新签名目录运行上述 `test-release.ps1`，使用新包实际SHA并追加 `-RequireNotices -RequireSigned -CertificateFingerprint $certificateSha256`，保存新的回执。NuGet发布账号已确认为 `Tansr`，两个包已正式发布。当前公开包通过 `-RequireNotices -RequireSigned`，签名类型为NuGet仓库签名，不称为作者签名。原卡要求签名、发布和安装事实对应，未要求额外作者证书；NET-06/A24已按原条件关闭，实际CI、渠道证据及判定更正见[开发记录](development.md)。

## English

The package gate installs the two approved candidate packages into independent empty projects. It runs .NET Framework 4.8 WinForms, a framework-dependent modern Windows console, self-contained .NET 10 WPF and optional core Native AOT. It checks the real Windows Desktop/runtime/SQLite native assets and records the loaded assembly paths. Windows x64 is the consumption target; other RIDs and WPF Native AOT are not implied.

Run `scripts/test-packages.ps1 -PackageDirectory <candidate> -OutputDirectory <new-evidence-directory> -Aot`. Add `-PreviousPackageDirectory <previous-candidate>` to exercise a separately restored earlier payload and the user-directory installation lifecycle. Independent caches avoid confusing two previews with the same version. Children receive an explicit environment, system-only PATH and, for self-contained applications, a nonexistent DOTNET_ROOT. This demonstrates that those applications do not require Node on PATH; it is not a claim that the host has no Node installation or that a clean OS was used.

To build the three original examples from the approved packages, run this from the repository root in PowerShell 7:

```powershell
./scripts/build-package-examples.ps1 `
  -PackageDirectory J:/tansr/archive/NET-release/packages `
  -OutputDirectory J:/tansr/archive/NET-release/package-examples `
  -Version 0.1.0.2
```

Use actual candidate paths and a new output directory. Both directory parameters are required; `Version` defaults to `0.1.0.2` and must match both packages. The script snapshots the original Console, WPF, WinForms and Shared sources. In separate consumer copies, it replaces source project references with exact package references and lets the packaged `buildTransitive/net48` target replace the WinForms source import. It preserves application code, framework declarations and assembly references; original examples, SDK projects and repository lock files are untouched.

An isolated candidate feed, source mapping and caches pin both packages by SHA256. Restored package bytes are verified, dependency locks are saved and checked for changes during the build. The `executables` directory contains framework-dependent Windows Console, win-x64 self-contained WPF and net48 x64 WinForms outputs. `manifest.json` records source/copy mappings, package hashes, commands, locks and output hashes. `example-paths.json` supplies `TANSR_NATIVE_CONSUMER_EXAMPLE` for the existing `scripts/native-serve-consumer.mjs`, and `TANSR_NATIVE_UI_WPF` / `TANSR_NATIVE_UI_WINFORMS` for `scripts/native-ui-integration.mjs`. Those runners still require their original approved Serve candidate, source snapshot and configuration. The build's `built-not-executed` result does not run an example or model and does not replace actual Console/UI and cleanup evidence.

`test-installation.ps1` copies approved payloads into a new per-user path containing spaces and Chinese characters. It verifies the bytes, selects versions atomically, runs install/upgrade/rollback scenarios, and uninstalls only the unchanged files it owns. Synthetic user data and an ownership marker remain and are recorded. Unknown files are preserved. `-RequireStandardUser` rejects administrator group membership, including a filtered UAC token. Without it, evidence states the actual identity rather than claiming standard-user acceptance. Clean-user OS, cross-user ACL/DPAPI denial and real archive migration require their own evidence.

The content/license audit records SDK identities, targets, dependencies, hashes, declared license terms and native assets. Both product packages now include the root `LICENSE` and `NOTICE`; use `scripts/audit-packages.ps1 -RequireNotices` for release candidates. Historical inspection can omit that switch. Legacy license URLs remain explicitly subject to distribution review. Bundled notices do not replace complete dependency license obligations or signature verification.

Local Serve uses the existing `LocalServeHost`: approve an executable and digest, provide a controlled workspace, call `StartAsync`, obtain `CreateClient`, and await `StopAsync`. The host generates private credentials, validates authenticated readiness and verifies the actual TCP peer belongs to its original child process before sending credentials or bodies. It does not download code, inherit arbitrary credentials, silently connect to another port owner or add a Node client proxy. Executable hashing alone does not authenticate adjacent apphost DLLs; the complete installation remains a trusted distributor responsibility.

All three examples share `ExampleConnection`. Set `TANSR_LOCAL_SERVE_EXE`, `TANSR_LOCAL_SERVE_SHA256` and `TANSR_LOCAL_WORKSPACE`; optionally select port, dedicated Serve entry or explicit environment names. A local executable does not automatically install the platform memory/profile configuration. Stop and reconcile work before changing a version; never restart an unknown side effect. The optional package-gate parameters `-LocalServeExecutable` and `-LocalServeSha256` exercise the real approved artifact through public SDK startup/list/stop, without model calls.

For the existing Serve trusted-host entry, set `TANSR_LOCAL_SERVE_HOST_MODULE` to an approved absolute `.cjs` path and `TANSR_LOCAL_SERVE_HOST_MODULE_SHA256` to its digest. The original Serve assembles the core and deployment extensions; this requires no external client Node runtime. A verified module is developer-trusted code, not a sandbox for arbitrary JavaScript. The deployment must authenticate the end user independently of the local process Bearer and must not trust client-declared identity or platform. The examples explicitly transmit `TANSR_SERVE_USER_TOKEN` as `x-tansr-demo-user-token`. Direct consumers set `ReadinessHeaders` and the six-argument `CreateClient` overload's `additionalRequestHeaders` separately; readiness credentials are not implicitly copied to clients. SDK2 contract and trusted-scope selection remain explicit, with SDK1 the default.

Package creation, installation tests, mainline integration, CI, signing and publication are separate facts. Version `0.1.0.2` was published manually to NuGet.org after mainline CI passed. Public downloads, payloads and NuGet repository signatures were verified. NET-06/A24 are complete; additional author signing is optional, not an unmet requirement of this release.

### Mainline CI and release inspection

`.github/workflows/ci.yml` is wired for pushes to `main` and manual dispatch on `main` only, with read-only repository permissions. It uses `windows-2025`, the exact SDK from `global.json` (currently10.0.301 with `rollForward: disable`) and Node22.22.1. It neither clones other private repositories nor signs/publishes packages. The public MIT source repository is [tansrai/tansr-net](https://github.com/tansrai/tansr-net). Consult the [actual mainline runs](https://github.com/tansrai/tansr-net/actions) and [development record](development.md) for results. SDK upgrades must review implicit tool dependencies and lock files together; a changed runner SDK must not bypass locked restore.

From Windows x64 PowerShell7, with the .NET/Node/C++ AOT toolchains installed, use a new evidence directory outside the checkout:

```powershell
./scripts/test-ci.ps1 -OutputDirectory J:/tansr/archive/NET-release/ci-plan -PlanOnly
# Actual local execution uses a different new directory and omits -PlanOnly.
# Optional approved inputs: -CliRoot, -RecoveryCliRoot, -PreviousPackageDirectory.
```

`PlanOnly` inventories source/locks and returns `planned-not-run`; it does not run gates. Actual execution runs repository contract/parity checks, native MCP preparation, locked restore, Release build, applicable tests and sandbox vectors, format, two-package creation, strict notices audit, package consumers including AOT, and the three example builds. Version comes from `Directory.Build.props`. Failed steps or changed source/locks stop acceptance; missing test executions are not passes.

`CliRoot` and `RecoveryCliRoot` supply approved frozen upstream source with installed dependencies for their original comparisons/interoperability. Without them, four cross-repository Windows methods are explicitly excluded. The Electron/Serve benchmark always remains separate. `PreviousPackageDirectory` enables old-package API compatibility and upgrade/rollback; a new package cannot be its own baseline. Real Serve, five-client, GUI, standard-user, signing and publication gates remain separate. Read `scope.excludedWindowsMethods`, `scope.notRun`, step statuses and TRX; `passed-with-external-gates-not-run` is not full A01–24 acceptance. Only allowlisted evidence and unsigned packages are uploaded, not caches or complete application payloads.

For read-only release inspection, use `scripts/test-release.ps1 -CorePackage <core.nupkg> -WindowsPackage <windows.nupkg> -Version <exact-version> -OutputDirectory <new-directory> -RequireNotices`. Optionally provide both `CoreSha256` and `WindowsSha256`; each must be 64 hex digits. `VerifyTimeoutSeconds` defaults to120 (range1–600). The script audits pinned copies, invokes the real NuGet verifier with normal trust/revocation policy, and preserves source hashes and logs. It does not sign or publish.

Unsigned packages may be recorded successfully only when the real verifier reports the sole unsigned diagnostic `NU3004`; the manifest then says `readiness=unsigned`, `signaturesVerified=false`. Invalid signatures, other verification failures, timeouts or changed bytes fail. `RequireSigned` rejects unsigned inputs. `CertificateFingerprint` pins an approved SHA256 signer on both packages and also rejects unsigned inputs; without it `signerPinned=false`. `releaseReady` and `published` remain false in every mode: successful local inspection is not release completion or NuGet account authorization.

For optional future author signing, use an approved production code-signing certificate with its private key already available in the local `CurrentUser/My` store. The commands above use .NET 10, a SHA256 certificate fingerprint, an approved RFC3161 service and a new output directory; never substitute the usual SHA1 Windows Thumbprint. Do not pass passwords in commands or export private keys for this workflow. This host currently has no suitable certificate, so author signing has not been performed; the published packages already have verified NuGet repository signatures. See [Microsoft's sign command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-sign).

Verify both signed packages with `dotnet nuget verify --all --certificate-fingerprint <approved-sha256>`, preserve original candidates and record new hashes and exit codes. Certificate and timestamp trust must succeed. A matched signer does not establish NuGet ownership or publication permission; NuGet signatures do not sign application executables. Self-signed test certificates are not production acceptance. See [Microsoft's verify command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify).

If additional author signing is adopted later, run `test-release.ps1` against those signed outputs with their new expected hashes and `-RequireNotices -RequireSigned -CertificateFingerprint <approved-sha256>`. The authorized NuGet publishing account is `Tansr`. The published packages passed `-RequireNotices -RequireSigned` with NuGet repository signatures, not author signatures. The original requirements call for verified signing, publication and installation facts, without mandating an additional author certificate. NET-06/A24 are complete. See the [development record](development.md) for actual CI, channel evidence and the corrected assessment.
