# 内部终端候选消费者

此处是 Serve 单写候选 `2026-09-26.candidate-4` 的原字节快照，SHA256 固定为 `5973fde3f029f9c92794cde385e4041a150de0ace476bb2f6ff2c63c2a166dd0`；来源修订、每个文件哈希见 `manifest.json`。原 `contract/` SDK2 镜像与共享 Protocol 未改。运行时自动核嵌入 schema 的 SHA；`node src/Tansr.Sdk/Terminal/Contract/check-candidate.mjs --source-root <Serve源码目录>` 同时核当前候选源是否漂移，默认仅核独立仓内快照。

所有消费者类型均为 internal：候选 client 负责精确发现/能力绑定/原 Scope 和 operation 校核；输出 producer 负责有界采集、批次、原块对账与封口；observer 负责每通道 UTF-8、原始字节、重复块、缺口和封口校验。输出完成与进程成功分开。网络未知不重做命令，accepted 不当 durable。

`TerminalCandidateHttpTransport` 消费 Serve candidate-3 已写入源码的发现、绑定、输出批、状态和两类 SSE 路由；精确路由来源保留在 reference-terminal-routes.ts.txt。每次请求重新取票并守卫原身份，输出 SSE 使用 afterSeq，执行器通知使用 Last-Event-ID，不互换。断流显式失败，控制写入不自动重试。Discovery 的授权/设备未确认状态交由真实 Binding 裁决，不以未确认代替拒绝或旧端降级。候选3虽含 Configuration 定义，本消费者只校验对应共享向量，没有添加配置产品操作。

`TerminalExecutionOutputSink` 可接已授权原生进程：Append 同步有界、后台每30ms批发、末尾 Seal；输送失败不改变真实进程终态。每个 sink 最多保留8个未决 operation；已成功封口的 Capture.Dispose 自动释放槽和原文，未决 Capture.Dispose 仅停止输送，FindCapture 保留 LastStatus/RequiresReconciliation/ReconcileAsync，显式 ReleaseCapture/Sink.Dispose 才丢弃未知原文。捕获生命周期独立于原进程取消，允许后端用单独有界 token 提交截断封口。任何输送失败（包括尚未 POST 的 pump 取消）均显式要求原 operation 对账，不重做进程；内存不是跨进程耐久源。

candidate-4 新增原 operation 的窄 ExecutionState GET 已接内部适配；执行状态继续复用冻结 SDK2 的 operation/receipt/digest 校验。背景 typed tool.invoke 和 Memory 定义虽在候选快照里，其产品宿主还未接入本消费者，不据此宣称能力已安装。

manifest 的 sourceCommitted=false、sourceWorktreeSnapshot=true 明确候选来自上游未提交工作区；sourceBaseCommit 仅记录该工作树基线，不声称该提交树已含候选。每个快照的原字节 SHA 是实际消费来源，校验器同时核对来源标记。生产公开接线、候选冻结、真实 Serve 新路由联验、跨进程原文耐久恢复及完整背景任务/记忆协议仍是原父卡未完项。

Producer 原文内存最多遵循 maxRetainedBytes（8 MiB 上限）及 4096 块；未确认队列受 maxPendingBytes（2 MiB 上限）限制。任何帽达到后停止捕获并固定截断，后续原生管道仍须排空，丢弃字节不推进 seq/offset。原文保留不是耐久证明，Dispose/进程退出后不能假装仍可恢复。Observer 丢失解码状态或窗口缺口时保留原字节、重置两通道并显式呈现缺口。
