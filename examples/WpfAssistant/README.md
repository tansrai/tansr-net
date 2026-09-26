# WPF 原生示例

本示例目标为 `net10.0-windows`，通过公开 `TansrClient` / `AgentSession` 消费已存在的 Serve 合同。应用进程不依赖 Node；模型、核心治理和持久会话仍由独立运行的 Serve 提供。

```powershell
dotnet run --project examples/WpfAssistant/WpfAssistant.csproj
```

输入 Serve HTTPS 地址与开发者登录服务签发的短期用户票据。票据只放内存，“更新票据”替换后续请求使用的值，不重建会话；不要输入开发者 appkey。只有显式勾选本机开发选项才允许环回 HTTP。服务端仍需正确配置身份、会话工厂、模型和对应存储。

已接入创建/恢复、持续 SSE、正文和思考呈现档、发送文本/图片、取消、工具输出/终值、四路 token 用量、独立审批与提问、历史/状态、压缩、快照创建/列表/恢复/导出/导入。上述操作使用实际公开 SDK 请求，服务端未装配时保留真实错误；没有伪造成功结果。历史与快照回执以只读 JSON 窗口展示。图片和快照读取有大小上界。

“发送”清空草稿的条件是请求已接纳，不是模型已结束；失败时保留草稿。结果未知先查历史，示例不会自动重发。关闭请求最多等待 10 秒，失败保留窗口和会话句柄供重试；也可选择“仅断开本机连接”后正常退出。后者不发送远端取消/关闭/删除，明确保留“远端可能继续”及上次关闭未确认信息。关闭不删除历史，也不把服务接纳当作所有远端资源已经耐久释放。

新建会话通过冻结的 SDK1 `clientTools` 注册 `application_info` 与 `set_window_title` 两个真实 C# 函数；后者通过 WPF Dispatcher 修改当前窗口标题。SSE 继续接收取消，工具不阻塞观察循环。工具回执在本进程内按 callId 去重；示例未接入业务工具的耐久日志，因此恢复会话时拒绝重新执行旧调用，要求新建会话。没有把这条示例路径冒充 SDK2 的耐久 execution host。示例没有可信时钟校准，工具请求与本机时间偏差超过 5 秒时保守拒绝。

媒体窗口通过公开 `TranscribeAsync` / `SpeakAsync` 接真实 Serve：音频文件或 WinMM 麦克风录音（16kHz 单声道 WAV，120秒上限）转写后只加入草稿；模型、音色、格式和输入限额来自 `meta.media` 目录。朗读先规划，明确允许分段后逐段合成与续合；成功片段缓存，不重新计费，未知结果停止本批次，创建新批次须明确确认。

实时结构化图片、视频、音频与转写产物可原生预览、停止和保存；WPF 使用 Image/MediaElement，WinForms 使用 PictureBox/Windows MCI。系统缺少解码器时显示实际错误并允许保存，不宣称跨系统解码器一致。历史按冻结 `tool_result.artifact` 恢复；无材料、历史裁剪和失效 URL 留可见状态，不从文本猜链接。SDK 下载组件默认不访问外链；通过宿主环境 `TANSR_MEDIA_HOSTS=cdn.example.com,other.example.com` 精确授权 HTTPS 主机，不带会话认证、不跟重定向、不读远端 path。音频输入最多16MiB，下载最多256MiB，本次会话呈现最多256项，缓存退出清理；用户保存的文件保留。

可选本地 Serve 使用同一 `ExampleConnection`：配置 `TANSR_LOCAL_SERVE_EXE`（受信独立CLI/Serve安装物）、`TANSR_LOCAL_SERVE_SHA256`、`TANSR_LOCAL_WORKSPACE`；可选 `TANSR_LOCAL_SERVE_PORT`，专用Serve入口用 `TANSR_LOCAL_SERVE_DIRECT=1`。需要透传的运行环境名称必须逐个列入 `TANSR_LOCAL_ENV_NAMES`；随机 Serve token 由SDK管理，不使用票据输入框，不打印密钥。此模式会启动本机子进程，退出等待回收；“仅断开本机连接”仍会停止本实例拥有的本地Serve进程，远端Serve模式不会停止远端服务。安装、自动下载和升级UI仍未实现。

当前明确缺口：同轮输入编辑器、动态模型/思考切换、系统提示词治理 UI、完整长期记忆管理/迁移、档案离线阅读与设备执行器的默认可信装配、MCP/Skills/子代理专用管理、Serve安装/升级UI。已提供NativeDeviceHost公开SDK装配示例，但尚未假设业务应用的可信scope和工具摘要。麦克风、视频解码器、真实付费ASR/TTS及大媒体SSE端到端仍需实际环境验收；客户端可配32MiB不证明Serve订阅缓冲已承载该大小。现有编译或局部测试不关闭NET-05完整交付。

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
