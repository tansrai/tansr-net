# Tansr .NET SDK 工程约定

共同纪律以 `J:/tansr/tansr-cli/doc/工程工作纪律.md` 为准。固定目录 `J:/tansr/tansr-net` 保持 main；开发树放 `J:/tansr/worktrees/`，只推主线。提交格式 `type(scope): 具体变化 (NET-xx)`。

方案事实源为 CLI 主线 `doc/report/NETSDK-开发方案-2026-09-26.md`、对应开发计划和对抗式验收单。固定六张 NET-01～06、24项 NET-A01～24，不另拆统计小卡。

SDK 是 Serve 的协议消费者、终端执行器、存储与 UI 投影。模型循环、上下文、权限裁决、记忆决策和计费留在 Serve/kernel；Electron 原完整 SDK/IPC 不改。新 Serve 协议由 Serve 会话单写；未会签 RFC 不作为稳定公开 API。

目录遵循 `src/Tansr.Sdk`、`src/Tansr.Sdk.Windows`、`examples`、`tests`、`doc`。核心多目标 netstandard2.0/net10.0，Windows 多目标 net48/net10.0-windows；只发布两个产品 NuGet 包。业务代码命名不加批次号和任务编号。

本地门由解决方案与脚本记录：锁定依赖 restore、Release build（warnings as errors）、dotnet test、dotnet format --verify-no-changes、pack 与独立消费。实现阶段仅跑受影响验证，合流后集中验收。未通过的完整卡不勾选，内部缺口不称为发行延期。

全局工程配置、公共跨目录接口和协议生成器单一写者。临时日志放 `J:/tansr/archive/` 的本任务目录。不得提交票据、密钥、用户数据，不触碰其它会话的工作树和未提交文件。默认 HTTP 仅允许显式本机开发，生产用 HTTPS；未知副作用只对账，不自动重做。
