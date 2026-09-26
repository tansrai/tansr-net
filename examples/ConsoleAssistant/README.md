# 原生控制台与无界面宿主

目标为 .NET 10；依赖公开 .NET SDK 与 Serve，无 Node 客户端依赖。运行前通过运行环境注入：

- `TANSR_SERVE_URL`：Serve HTTPS 地址。
- `TANSR_SESSION_TOKEN`：短期用户票据。不要放开发者 appkey；示例不打印或写入票据。
- 可选 `TANSR_ALLOW_HTTP_LOOPBACK=1`：仅允许本机开发 HTTP。
- 可选 `TANSR_RESUME_SESSION`、`TANSR_MODEL`：恢复已有会话或指定新会话模型。

```powershell
dotnet run --project examples/ConsoleAssistant/ConsoleAssistant.csproj -- --help
dotnet run --project examples/ConsoleAssistant/ConsoleAssistant.csproj
dotnet run --project examples/ConsoleAssistant/ConsoleAssistant.csproj -- --once "说明当前任务的执行步骤"
```

交互模式支持文本发送、`/history`、`/meta`、`/compact`、`/checkpoint`、`/checkpoints`、`/cancel`、`/requests`、`/allow <requestId>`、`/deny <requestId>`、`/answer <requestId> <答案JSON数组>`、`/quit`。答案沿公开合同，例如 `[{"questionId":"q1","selectedOptionIds":["choice1"],"freeText":"补充说明"}]`。请求 ID 与选项 ID 必须来自当前待处理请求；过期由服务端裁定。

`--once` 不显示 UI：审批默认拒绝，提问使用明确的无人值守说明回复，不能暗中自动批准或假装用户做了选择。正常完成返回 0；模型非成功终态返回 2；用户取消返回 130；连接/协议错误返回 1。Ctrl+C 与 POSIX SIGTERM 使用独立的 10 秒预算中断并关闭，失败输出 `close_unconfirmed`；断开观察与取消运行是分开的操作。

事件输出会包含会话正文、工具输出及审批内容，应像应用日志一样管理访问与保留。票据及平台长期密钥从不进入默认输出。真实用量/状态可经 `/meta` 查询；未知持久提交不被转换为成功。日志格式是示例呈现，完整不可变投影由 `SessionView` 提供。

新会话接入两个冻结 SDK1 业务工具。应用信息由当前 .NET 进程返回；设置标题只在有控制台窗口的 Windows 环境执行，其它环境明确报告不支持。恢复旧会话的工具缺少本进程耐久回执时拒绝重执行；示例不能证明 SDK2 设备执行/记忆生命周期已闭环。

待补：多会话 worker 编排、默认持久执行器/档案/记忆装配、同轮输入命令、本地 Serve 启停、MCP 与媒体完整消费、安装包消费和裁剪/AOT实测。编译通过不表示这些功能完成，也不替代真实 Serve 联验。
