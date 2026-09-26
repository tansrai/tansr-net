# 多会话无人值守 worker

`ConsoleAssistant --worker jobs.jsonl --concurrency 4` 使用公开 C# SDK 为每条作业创建独立会话，先接通事件流，再发送一次完整提示词。并行度为 1–8，默认入口使用 2。跨平台 `net10.0` 直接连接远端 Serve；`net10.0-windows` 还可使用现有 `TANSR_LOCAL_SERVE_*` 装配受控本地 Serve。客户端不需要 Node。

```jsonl
{"id":"job-1","prompt":"读取当前工作区并概述，不修改文件。"}
{"id":"job-2","prompt":"解释这段代码。\n换行与完整长文本均保留。","model":"服务实际提供的模型标识"}
```

连接使用与交互 Console 相同的 `TANSR_SERVE_URL`、`TANSR_SESSION_TOKEN` 和显式本机 HTTP 开关 `TANSR_ALLOW_HTTP_LOOPBACK=1`。省略作业 `model` 时使用 `TANSR_MODEL`，没有设置则交给 Serve。worker 不使用 `TANSR_RESUME_SESSION`；每条作业有独立会话，避免多个作业竞争同一轮。`id` 同时进入会话的 `worker.job` 归因标签，便于结果未知后人工对账。

UTF-8 JSONL 文件上限 32 MiB，最多 1024 条作业；每条只允许 `id`、`prompt`、可选 `model`。重复 id、重复字段、未知字段、非法 UTF-8、空正文及超出公开 SDK 20 MiB 消息请求限制的正文，会在连接前整份拒绝。正文不会截断或自动拆轮。空白行忽略，正文换行写为 JSON 的 `\n`。

输出为逐行 JSON，带 `jobId`、已知时的 `sessionId`，并区分 `acceptance`、`terminal`、`cleanup_step` 与 `cleanup`。`acceptance: accepted` 只说明 HTTP 接纳，不表示模型完成、持久化或计费已确认。只有关联到该轮的 `turn.completed` 且 reason 为 `completed` / `structured_output` 才显示 `terminal: succeeded`。断流、接纳响应丢失、取消、终局身份不明或任一收尾未确认都会保留失败/未知，不自动再发。文本和工具增量输出同样保留 job/session 归属；输出可能含业务内容，日志由应用自行保管，连接票据不输出。

无人值守权限请求一律拒绝；问题回传“无人值守宿主无法回答”的明确自由文本，不选择猜测答案。示例原生业务函数仍通过 `NativeToolHost`，可复用已配置的原生 MCP；无界面 worker 明确拒绝窗口标题工具。

Ctrl+C、SIGTERM 或外部停止 token 会停止领取新作业，取消各活动会话的本地等待，再对未确认终局执行公开 `CancelAsync`。中断、业务工具排空、关闭会话分别使用独立的 5 秒、12 秒和 10 秒预算，不复用已取消的停止 token。各作业收尾均被等待后才关闭共用连接、本地 Serve 与 MCP。预算耗尽显示 `unconfirmed`，不冒称远端资源已经清完；未领取作业输出 `not_sent` / `not_started`。退出码：0 表示全部作业成功且本次清理已确认，2 表示作业或清理失败/未知，130 表示外部停止，1 表示读取/验证/连接失败。

这是一进程内的批处理示例，不是耐久作业队列：进程崩溃后不会从文件自动续跑，重复启动同一文件可能产生新的会话和费用。上层调度器必须保留输出并先对账未知作业；不得以新 id 或重启掩盖未知结果。`CloseAsync` 回应只作为现有公开服务合同下的关闭确认，不表示历史删除、供应商计费或更细的服务端资源事实。
