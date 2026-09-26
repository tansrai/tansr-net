# WinForms 原生示例

目标为 .NET Framework 4.8，直接消费 `Tansr.Sdk` / `Tansr.Sdk.Windows` 公共 API，无 Node 客户端代理。需要 Windows 与 .NET Framework 4.8；构建引用程序集不能替代运行时。

```powershell
dotnet build examples/WinFormsAssistant/WinFormsAssistant.csproj -c Release
```

从输出目录启动 `WinFormsAssistant.exe`，填写 Serve 地址与短期用户票据。票据不写入配置文件；更新票据只替换后续请求使用的内存值。生产连接使用 HTTPS；本机 HTTP 必须显式勾选。服务端必须先装配模型、身份、工具和存储；此示例不隐式下载或启动 Serve。

已接通：创建/恢复、连续事件观察、正文/思考流与定稿呈现、文本/图片输入、取消、工具状态/输出/结果、审批与提问、历史/状态、压缩与快照创建/列表/恢复/导出/导入。UI 更新通过 WinForms 同步上下文，不在后台线程操作控件。草稿仅在输入接纳后清空，未知结果不自动重发。关闭失败仍保留窗口与会话供重试；也能选择“仅断开本机连接”后正常退出。断开不调用远端取消/关闭/删除，并提示远端工作可能继续、上次关闭未确认。不能将关闭请求等同为删除历史。

新会话会真实注册 `application_info` 与 `set_window_title` 两个 SDK1 业务工具，使用 C# 委托、公开 tool-results 回执与 UI 线程调度。取消与事件泵独立。回执去重当前仅在同一宿主进程有效，恢复旧会话时保守拒绝旧工具的重新执行；耐久业务日志是待接项。时钟偏差超过 5 秒时示例拒绝执行时间许可不确定的请求。

本轮已接同轮输入编辑器、自动本机草稿及离线呈现回看，公开 preview 的模型/思考与记忆控制有独立原生窗口。原输入键与目标在发送前保存，未知结果只查原键；输入不截断，不取消/重建会话。WinForms 草稿解除默认32767字符限制，协议上限仍明确拒绝超量而不裁剪。文件位置、可信scope装配及回执恢复操作见 [共享说明](../Shared/session-controls.md)。未配置preview时明确显示未启用。
媒体窗口通过公开 `TranscribeAsync` / `SpeakAsync` 接真实 Serve：音频文件或 WinMM 麦克风录音（16kHz 单声道 WAV，120秒上限）转写后只加入草稿；模型、音色、格式和输入限额来自 `meta.media` 目录。朗读先规划，明确允许分段后逐段合成与续合；成功片段缓存，不重新计费，未知结果停止本批次，创建新批次须明确确认。

实时结构化图片、视频、音频与转写产物可原生预览、停止和保存；WPF 使用 Image/MediaElement，WinForms 使用 PictureBox/Windows MCI。系统缺少解码器时显示实际错误并允许保存，不宣称跨系统解码器一致。历史按冻结 `tool_result.artifact` 恢复；无材料、历史裁剪和失效 URL 留可见状态，不从文本猜链接。SDK 下载组件默认不访问外链；通过宿主环境 `TANSR_MEDIA_HOSTS=cdn.example.com,other.example.com` 精确授权 HTTPS 主机，不带会话认证、不跟重定向、不读远端 path。音频输入最多16MiB，下载最多256MiB，本次会话呈现最多256项，缓存退出清理；用户保存的文件保留。

可选本地 Serve 使用同一 `ExampleConnection`：配置 `TANSR_LOCAL_SERVE_EXE`（受信独立CLI/Serve安装物）、`TANSR_LOCAL_SERVE_SHA256`、`TANSR_LOCAL_WORKSPACE`；可选 `TANSR_LOCAL_SERVE_PORT`，专用Serve入口用 `TANSR_LOCAL_SERVE_DIRECT=1`。需要透传的运行环境名称必须逐个列入 `TANSR_LOCAL_ENV_NAMES`；随机 Serve token 由SDK管理，不使用票据输入框，不打印密钥。此模式会启动本机子进程，退出等待回收；“仅断开本机连接”仍会停止本实例拥有的本地Serve进程，远端Serve模式不会停止远端服务。安装、自动下载和升级UI仍未实现。

当前明确缺口：系统提示词治理 UI、长期记忆完整管理/迁移、带当前授权的共享档案离线阅读与设备执行器默认可信装配、MCP/Skills/子代理专用管理、Serve安装/升级UI。已提供NativeDeviceHost公开SDK装配示例，但尚未假设业务应用的可信scope和工具摘要。麦克风、视频解码器、真实付费ASR/TTS及大媒体SSE端到端仍需实际环境验收；客户端可配32MiB不证明Serve订阅缓冲已承载该大小。现有编译或局部测试不关闭NET-05完整交付。

## 可选 Serve 智能体 → 原生 MCP 工具

三种示例共用 `ExampleConnection` 和原 `NativeToolHost`：显式配置 `TANSR_MCP_EXE`、`TANSR_MCP_SHA256`、`TANSR_MCP_WORKSPACE` 后，连接前启动受信原生 MCP 候选，新会话 `clientTools` 额外声明固定只读 `mcp_echo(text)`。让智能体调用 `mcp_echo`，实际路径是 Serve 请求 → C# 委托 → SDK MCP stdio → 本示例 `--mcp` → 原 SDK1 `tool-results` 回执。工具列表只用于确认固定 echo 存在，不自动采信发现的参数、权限或其它工具。未配置时不启动子进程、不声明工具；恢复会话保留拒绝未知旧调用的保护。

建议先在仓库验收后，发布并批准自包含单文件 Console MCP 候选，再把该候选的 SHA256 写入宿主配置：

```powershell
dotnet publish examples/ConsoleAssistant/ConsoleAssistant.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetLockFilePath=obj/mcp-publish.packages.lock.json -o archive/native-mcp-candidate
Get-FileHash archive/native-mcp-candidate/ConsoleAssistant.exe -Algorithm SHA256
dotnet restore Tansr.Sdk.slnx --locked-mode
```

发布输出请使用新的具名目录；`NuGetLockFilePath` 将 RID 发布锁隔离到 obj，不能换成此 SDK 不识别的 `PackagesLockFile`。发布后（包括失败后）恢复默认解决方案锁定资产，再构建/测试；完整 Windows 消费验收优先运行根目录的 `scripts/test-windows.ps1`，它还核对正式锁文件未变、保存证据并恢复测试环境变量。

`TANSR_MCP_EXE` 填批准后的绝对路径，工作目录须已存在且由宿主管理。SDK 在固定最终可执行文件句柄后核验摘要，不接受模型替换路径或参数。普通 apphost 的 EXE 哈希不认证旁边的 DLL；此智能体桥不接受 `TANSR_MCP_DLL` 参数，应使用经批准的单文件发布候选及受信安装目录。以上命令是复现入口，文档不表示已发布或已验证候选。

同一原工具宿主负责调用去重、取消与原回执保存；SSE 不因 MCP 调用阻塞。断线或未知执行结果不自动重做。关闭/断开先等待 MCP Job 清理；失败保留明确清理未确认，仍允许用户显式断开退出。此入口只完成固定原生 MCP 工具闭环，不能代表任意MCP服务器管理、SDK2耐久设备/档案默认装配已完成。
