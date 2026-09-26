# 原生控制台与无界面宿主

目标为跨平台 `net10.0` 与原生Windows `net10.0-windows`；依赖公开 .NET SDK 与 Serve，无 Node 客户端依赖。运行前通过运行环境注入：

- `TANSR_SERVE_URL`：Serve HTTPS 地址。
- `TANSR_SESSION_TOKEN`：短期用户票据。不要放开发者 appkey；示例不打印或写入票据。
- 可选 `TANSR_ALLOW_HTTP_LOOPBACK=1`：仅允许本机开发 HTTP。
- 可选 `TANSR_RESUME_SESSION`、`TANSR_MODEL`：恢复已有会话或指定新会话模型。

```powershell
dotnet run --project examples/ConsoleAssistant/ConsoleAssistant.csproj -f net10.0 -- --help
dotnet run --project examples/ConsoleAssistant/ConsoleAssistant.csproj -f net10.0
dotnet run --project examples/ConsoleAssistant/ConsoleAssistant.csproj -f net10.0 -- --once "说明当前任务的执行步骤"
```

交互模式支持文本发送、`/history`、`/meta`、`/compact`、`/checkpoint`、`/checkpoints`、`/cancel`、`/requests`、`/allow <requestId>`、`/deny <requestId>`、`/answer <requestId> <答案JSON数组>`、`/quit`。答案沿公开合同，例如 `[{"questionId":"q1","selectedOptionIds":["choice1"],"freeText":"补充说明"}]`。请求 ID 与选项 ID 必须来自当前待处理请求；过期由服务端裁定。

`--once` 不显示 UI：审批默认拒绝，提问使用明确的无人值守说明回复，不能暗中自动批准或假装用户做了选择。正常完成返回 0；模型非成功终态返回 2；用户取消返回 130；连接/协议错误返回 1。Ctrl+C 与 POSIX SIGTERM 使用独立的 10 秒预算中断并关闭，失败输出 `close_unconfirmed`；断开观察与取消运行是分开的操作。

事件输出会包含会话正文、工具输出及审批内容，应像应用日志一样管理访问与保留。票据及平台长期密钥从不进入默认输出。真实用量/状态可经 `/meta` 查询；未知持久提交不被转换为成功。日志格式是示例呈现，完整不可变投影由 `SessionView` 提供。

新会话接入两个冻结 SDK1 业务工具。应用信息由当前 .NET 进程返回；设置标题只在有控制台窗口的 Windows 环境执行，其它环境明确报告不支持。恢复旧会话的工具缺少本进程耐久回执时拒绝重执行；示例不能证明 SDK2 设备执行/记忆生命周期已闭环。

媒体命令：`/media` 列当前结构化产物，`/media-history` 加载历史媒体，`/media-save <索引> <用户路径>` 保存（不覆盖已有文件）；`/asr <音频路径>` 仅生成草稿，`/send-draft` 明确发送；`/speech-plan <文字>` 按实际模型限额规划，`/speech-next` 每次合成下一段，`/speech-reset` 明确清空批次后才可重新合成。可选 `TANSR_ASR_MODEL` / `TANSR_TTS_MODEL` 选择已授权目录中的模型。未知朗读结果阻止自动重复；产物可再次保存而不重新合成。外链只允许 `TANSR_MEDIA_HOSTS` 指定的精确HTTPS主机，票据不随下载发送，不跟重定向，历史失效有明确错误。

本地Serve模式使用 `-f net10.0-windows`：设置 `TANSR_LOCAL_SERVE_EXE`、`TANSR_LOCAL_SERVE_SHA256`、`TANSR_LOCAL_WORKSPACE`，可选端口 `TANSR_LOCAL_SERVE_PORT`。默认执行CLI的serve子命令；专用Serve二进制设 `TANSR_LOCAL_SERVE_DIRECT=1`。只透传 `TANSR_LOCAL_ENV_NAMES` 明确列出的环境名。SDK管理随机认证、就绪与进程回收；客户端不需要Node。跨平台target若请求本地Windows宿主会明确拒绝，不回退到net48。

`--mcp` 是纯C# stdio MCP服务，initialize / notifications/initialized / ping / tools/list / tools/call，公开 `application_info` 和 `echo`；stdout只含JSONRPC，单帧64KiB上限，不需要Serve或票据。真实客户端往返用 `--mcp-client`（Windows target），先设 `TANSR_MCP_EXE` 为受信示例apphost，`TANSR_MCP_WORKSPACE` 为专用工作目录；若使用dotnet宿主还需 `TANSR_MCP_DLL` 指向本示例DLL，显式透传 `DOTNET_ROOT`。客户端使用SDK McpClient完成初始化、发现两个固定工具、调用echo、关闭进程树。发现不是模型授权；注册到Serve还需要可信工具定义摘要，`NativeDeviceHost` 只演示公开装配入口，不从不可信MCP元数据创造摘要。

待补：多会话worker编排、默认持久执行器/档案/记忆可信装配、同轮输入命令、安装与升级消费、裁剪/AOT实测。媒体真实付费与Windows录音/播放验收、Serve大帧端到端仍待集中验证。编译通过不表示这些功能完成，也不替代真实Serve联验。

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
