# NETSDK 实施记录

日期：2026-09-26。状态：首批实现集成，尚未正式发行。对应原方案 `tansr-cli/doc/report/NETSDK-*2026-09-26.md`；仍为六张父卡、24项完整验收、P01—P16，未拆进度卡。

## 责任、源码和边界

用户已要求 SDK2.0 会话实施 Serve 补齐、本会话立即实施 C# SDK。已通过 Codex 会话消息完成交接。Serve 是运行内核和新 wire 的单一写者；本仓是协议消费者、设备执行器、存储和原生示例。原 Electron 完整 SDK/IPC 不变。

- 固定目录 `J:/tansr/tansr-net`，主分支 `main`；初始工程提交 `9bcb40c`。
- 开发树 `J:/tansr/worktrees/net-NET-01-sdk`，本地分支 `lane/net/NET-01-sdk`，不推开发分支。
- 原协议/同源源码 `tansr-cli 027de7e2d9b647374b7fe94cb0e4a7429a9195e2`。
- SDK2 schema SHA256 `969273844ca9196f19dd71b292b65a49307d63be0d20a0caf557e105ba6d8605`，详见 `contract/manifest.json`。
- Serve terminal 新候选 `2026-09-26.candidate-2`，SHA256 `3cbb311f568bdc6d85b816fafbdb95254664219fdbd625dcadc0dfbdf4df84ae`，仅只读会签反馈，未导入稳定 API。

## 固定父卡进度

| 原卡 | 本轮实际实现 | 未满足的完整条件 |
|---|---|---|
| NET-01 | 双包/双目标框架、锁定依赖、原 schema/金样、严格 codec、680项原公开入口映射和自动检查 | 新 Serve 完整合同、P01—16 全场景行为证据和最终公共 API 冻结 |
| NET-02 | SDK1/SDK2 显式家族、HTTP/SSE、身份续票委托、会话/输入/控制/音频/快照接口、SessionView、原执行端点 | 动态模型/预算/提示词/治理观察的新增协议，高阶完整装配及全部恢复语义 |
| NET-03 | 真实 Windows 文件/搜索/受控协作 CAS、Job进程/增量/取消、原执行协调和耐久回执 | 新远程分块 wire、后台任务托管、终端 MCP/Skills 装配及全链安全对照 |
| NET-04 | 执行日志、原档案 SQLite source/同步/加密格式、AES-GCM、DPAPI、ACK/墓碑/配额及重开验证 | 新记忆全生命周期、完整供材/网络同步/跨设备/存储组合的真实公开装配 |
| NET-05 | WPF、WinForms、Console 使用公开 SDK；两真实业务工具；显式观察断开 | Electron16组全能力演示，包括完整媒体、MCP/Skills、本地Serve管理及系统记忆等 |
| NET-06 | 本地门、真实Serve路由联验、本地NuGet打包/独立消费入口、AOT入口 | 固定24项完整验收、多架构/标准用户/实际UI、远端CI、正式渠道与发布 |

完整工程卡 **0/6，剩余6，0%**；完整验收 **0/24，剩余24，0%**；本轮新增完整关闭均0。这里的0%是完整DoD关闭比例，不是已写功能比例。不得将局部测试数、可编译示例或680映射条目换算为完成率。

## 已定位并处理的问题

| 触发/证据 | 原因 | 修正及边界 |
|---|---|---|
| net48 build无法复制win-arm SQLite资产 | SQLitePCLRaw 2.1.12 包目标文件引用已移除文件 | 显式固定上游2.1.13修正版；保留Microsoft.Data.Sqlite10.0.12，不修改全局缓存或跳过复制错误；[上游记录](https://github.com/ericsink/SQLitePCL.raw/issues/678) |
| 独立net48消费初始化Windows执行后端被误判旧系统 | 无supportedOS manifest时Environment.OSVersion返回兼容版本 | 使用系统ntdll的RtlGetVersion检测真实Windows版本；保留Windows10+要求，不迫使使用SDK的应用修改manifest |
| 真实/v2创建受控空会话被误拒 | 原lastSeq=-1哨兵被当非法事件序号 | 专用lastSeq解析允许-1；事件id/seq仍非负；Views初始水位一致 |
| 主体在落账或密钥回调中变化 | 异步和可注入回调之间需要重新核验身份 | 原事实与新派工分离，提交前核原app/user；存储提交/返回正文前再核可信scope和重入毒化 |
| 输出回调不响应取消 | 单次超时不足以限制跨调用遗留回调 | 同一执行器保留在途槽直到回调真实结束，满额启动前拒绝；不声称任意用户委托可被强行终止 |
| 空ArtifactRef加密测试失败 | 测试误把0字节引用当合法，原schema最小1 | 保留产品严格校验，回归断言改为拒绝非法引用，未放宽协议 |

## 验证记录

统一证据目录：`J:/tansr/archive/20260926-NET-01-sdk`。Windows后端局部证据由同一任务泳道早期存于 `J:/tansr/archive/20260926-SERVE-NET-01/process-local/`，以manifest记录文件及归属，不迁移正在使用的目录。

已证实：双核心和双Windows目标及三个示例 Release 编译零警告/零错误；核心202项、Windows83项通过，无失败或跳过；后者包含原Node与C#明文、加密SQLite源库双向消费两个场景，不再重复加总。真实Serve源码HTTP/SSE另有三个受控场景通过。格式、锁定恢复及两包独立消费结果如下，不能代替完整24项DoD。

| 门禁 | 最终证据 |
|---|---|
| 全仓Release编译/格式/锁定依赖 | `build-accepted.log`、`build-windows-version-fix.log`、`format-verify.log`、`restore-locked.log`；均exit0；build零警告/错误 |
| 核心与Windows集中单测 | `core-tests-accepted.log`及`core-accepted.trx`：202/202；`windows-tests-final.log`及`windows-final.trx`：83/83；均0失败/跳过 |
| 原Node双向档案消费 | `node-storage-tests.log`：2/2，原源码027de7e2 |
| 真实Serve路由、HTTP/SSE重连 | `scripts/serve-integration.mjs`：3/3，3会话/9发送/34请求/0意外interrupt；受控工厂，无模型调用 |
| 两包独立net48/现代消费及AOT | `package-consumption-final.log`：全新独立包目录、net48/现代客户端、工作区、执行后端、真实进程与清理；`package-consumption.log`：同一核心包的win-x64 NativeAOT实际运行。Windows包补实际系统检测后重新pack/消费，核心包SHA未变；候选不是正式NuGet发布 |

## 下一步和交付事实

优先接入 Serve 已冻结的新输出合同，复用现有 Windows 原始字节管道和执行日志；随后接记忆管理/供材和控制观察。保留当前 source/原 SDK1 服务端持久化，不以新接口替代旧数据。新合同未冻结时，C# 不猜测生产 API 或静默降级执行。

NuGet名称为候选，尚未声明注册所有权；本轮不发布NuGet、不签名、不部署，不调用真实模型。Git主线、远端及包事实在最终回执明确记录；当前产品仓尚无远端。
