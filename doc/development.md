# NETSDK 实施记录

日期：2026-09-26。状态：前三批本地集成与候选包消费已通过；新增 `20260926-NET-04-prompt-observation` 继续原 NET-02/05 的提示词来源接线，验收事实见末节。整体仍在开发，尚未正式发行。对应原方案 `tansr-cli/doc/report/NETSDK-*2026-09-26.md`；仍为六张父卡、24项完整验收、P01—P16，未拆进度卡。下文保留既有批次事实。

## 责任、源码和边界

用户已要求 SDK2.0 会话实施 Serve 补齐、本会话立即实施 C# SDK。已通过 Codex 会话消息完成交接。Serve 是运行内核和新 wire 的单一写者；本仓是协议消费者、设备执行器、存储和原生示例。原 Electron 完整 SDK/IPC 不变。

- 固定目录 `J:/tansr/tansr-net`，主分支 `main`；初始工程提交 `9bcb40c`。
- 开发树 `J:/tansr/worktrees/net-NET-01-sdk`，本地分支 `lane/net/NET-01-sdk`，不推开发分支。
- 原协议/同源源码 `tansr-cli 027de7e2d9b647374b7fe94cb0e4a7429a9195e2`。
- SDK2 schema SHA256 `969273844ca9196f19dd71b292b65a49307d63be0d20a0caf557e105ba6d8605`，详见 `contract/manifest.json`。
- Serve terminal 新候选 `2026-09-26.candidate-2`，SHA256 `3cbb311f568bdc6d85b816fafbdb95254664219fdbd625dcadc0dfbdf4df84ae`，仅只读会签反馈，未导入稳定 API。

## 首批固定父卡进度快照

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

## 首批结束时的下一步和交付事实

优先接入 Serve 已冻结的新输出合同，复用现有 Windows 原始字节管道和执行日志；随后接记忆管理/供材和控制观察。保留当前 source/原 SDK1 服务端持久化，不以新接口替代旧数据。新合同未冻结时，C# 不猜测生产 API 或静默降级执行。

NuGet名称为候选，尚未声明注册所有权；本轮不发布NuGet、不签名、不部署，不调用真实模型。Git主线、远端及包事实在最终回执明确记录；当前产品仓尚无远端。

## 20260926-NET-02-sdk：本批实现与候选边界

本节是本批开发事实回填，状态为**本批本地集成与候选包消费通过，六张原卡继续开发**。上述首批日志、红例及包消费事实保留；本节不把首批候选的通过结果移作本批新产物的通过结果。NET 起点为 `c6dc44732291efffea3e60b6c3c68b1910057df2`，实现于 `lane/net/NET-01-sdk` 隔离树，按门禁收编本地 main；最终 Git 回执另记于本批归档，不代表远端推送或正式发布。证据集中在 `J:/tansr/archive/20260926-NET-02-sdk`。

### 原卡归属与本批差量

| 原卡 | 本批已经落下的产品实现 | 完整 DoD 仍待完成 |
|---|---|---|
| NET-01 | 保留两个产品包及原核心/Windows双目标；原协议镜像未改；新增终端候选与缓存候选均有独立源码指纹和来源登记；Console同时提供跨平台与Windows目标 | 新 Serve 合同最终会签；稳定公共 API 与16组完整行为映射复核；未知 schema 不得自动视作支持 |
| NET-02 | 补会话导出/导入/fork/恢复与类型化元信息、文本块插入；`SessionRun` 区分HTTP接纳和模型终局，先观察后发送、单次副作用提交、断流恢复及取消；`DeviceSessionHost` 协调初始化/绑定/心跳/执行/收尾，控制与设备凭据可分离；`ExecutionRecovery` 查原账、不重跑未知副作用；档案独立SSE真实消费入口与耐久回调顺序 | 同会话动态模型/思考、提示词与预算/治理控制、新公共观察面仍依赖SRV-05；高阶默认可信装配、多写者归属及全部恢复/失败语义需最终实证 |
| NET-03 | Windows同一进程双向标准输入/输出、增量读取、背压/输出帽/取消/Job清理；受信程序摘要与最终对象身份固定；本地Serve与MCP复用此进程管道；原生stdio/受控HTTP MCP、工具适配、白名单及Windows技能目录/资源索引与受控读取；新终端输出候选的producer/observer/HTTP/SSE桥 | 分块候选还不是稳定发布合同；后台任务持久身份、托管/恢复/超时转交仍缺完整闭环；Skills/MCP/hooks/插件与子代理的完整可信Serve装配、资源归属和攻击矩阵未全部验完 |
| NET-04 | 公开 `ArchiveClient`、耐久接收与ACK、按需材料上传/响应/状态查询、材料响应箱及原文恢复；SQLite历史分页/元信息/原格式读取、备份/增量同步与墓碑/冲突处理；独立binding SSE接材料；内部缓存候选支持绑定/票据/原操作状态/原文与原回执恢复；真实材料链已通过 | 耐久接收后的ACK竞争尚需公开恢复协议及介质增量；记忆读取/检索/写回/维护/删除完整生命周期及新动态控制仍属SRV-03/04/05前置；第三方存储组合、跨设备删史不复活和故障矩阵仍待完整验证 |
| NET-05 | WPF/WinForms/Console接真实会话API；媒体产物投影/下载/保存、ASR草稿、TTS分段与未知结果保护；Windows录音与原生预览代码；三示例共享本地Serve连接、原生MCP工具及两个业务工具；提供公开设备宿主装配入口 | 真实图片/视频/录音ASR/TTS消费尚未完成；同轮输入编辑器、动态控制、长期记忆、离线档案、任意MCP/Skills/子代理管理、多会话worker和默认可信设备装配仍未齐；安装/升级UI与16组Electron真实对照未完成 |
| NET-06 | 本批解决方案Release构建、核心/Windows/真实Serve联验和两包独立消费通过；原本地检查、包/AOT机制继续复用，详见末节最终回执 | 固定24完整断言、Mac/Linux原生宿主、多架构/标准用户、签名/渠道/远端CI等未完成，不能用Windows编译替代 |

`SessionRun` 的接纳、终局、耐久提交与清理是不同事实。原消息ACK不携带turnId，跨独立写者的严格关联仍需要可信宿主独占写入；不为求“已完成”捏造轮次。设备账本旧授权修订只允许用当前认证核对同一app/user的原事实，不能拿来授权新派工。本地Serve托管的是当前实例拥有的受信进程；这不等于SDK内嵌Node或将Serve/kernel移植成C#，Electron原集成模式继续保持。

### 16组能力对照仍沿原口径

下表是差量和缺口登记，不另拆任务卡，也不把局部编译或入口出现当作该组通过。完整组通过须有同业务场景与Electron的最终证据。

| 固定组 | 本批可复用组件/接线 | 当前保留项 |
|---|---|---|
| P01 身份、能力与配置 | 续票守卫、scope分域、设备协商与本地Serve随机认证 | 统一平台配置读面、新配置合同会签 |
| P02 查询与会话 | `StartRun`/`SendAndObserveAsync`、原会话API、高阶设备宿主 | 可信ModelClient/采样/护栏/注入等效桥；多写者轮次关联 |
| P03 同轮追加 | 原inputs与文本块提交、状态及错误消费 | 三示例输入编辑器/命令、原轮确认级别完整UI证据 |
| P04 模型、思考与上下文 | 创建参数、元信息和事件投影 | 同会话动态切模/思考、失败回滚及实时控制观察 |
| P05 提示词与治理 | 保留核心治理及现有事件/用量投影 | 应用/会话配置来源、预算/裁决/告警的新控制和呈现 |
| P06 事件与视图 | 会话SSE、独立档案SSE、SessionView/媒体投影 | 新治理事件与16组完整呈现对照 |
| P07 系统与业务工具 | 真实文件/进程/业务工具；增量输出候选及设备恢复 | 稳定新wire、后台托管及完整跨端安全联验 |
| P08 权限与提问 | 原权限/提问回执与原生UI；设备执行前后再授权 | 全部过期、晚到、取消、轮次及审计场景集中对照 |
| P09 Skills、MCP、hooks、插件 | 原生stdio/HTTP MCP、固定原生MCP示例、技能目录读取 | hooks/插件可信桥及任意服务管理；默认MCP结果目前仅text/image，音频/资源等不能冒称已覆盖 |
| P10 子代理 | 复用既有事件/工具状态，保持核心子代理归属 | 完整生命周期、嵌套权限、后台持续通知/产物和用量对照 |
| P11 多媒体输入与生成 | 图文发送、结构化媒体/历史投影、ASR草稿、TTS分段续合、原生预览/录音代码 | 真实图/视频/录音ASR/TTS及解码器消费；Serve大媒体SSE端到端，客户端配置帽不等于服务端已承载 |
| P12 历史、快照与工作区 | 原compact/checkpoint/export/import/fork、SQLite历史、受控工作区 | 持久化组合/开关不重建会话及全部真实恢复场景 |
| P13 SDK2档案与同步 | 公开档案HTTP与binding SSE、材料源、耐久响应箱、SQLite同步；真实材料往返已通过 | 耐久ACK竞争恢复；完整故障/跨设备/删除矩阵；不能把档案同步称长期记忆同步 |
| P14 长期记忆与缓存连续性 | 原格式存储保留，缓存内部候选绑定与原事实恢复 | 记忆全生命周期新合同与本地落地；缓存候选会签、真实供应商受理和费用验证 |
| P15 生命周期与扩展结果 | 接纳/终局分离、设备停止等待、执行对账、局部清理与观察分离 | 远端清理终态、新治理回执、强制结构化结果及跨恢复语义 |
| P16 原生宿主与交付 | 两原生UI与Console、本地受信Serve进程管理、原生MCP、可选HTTP对象所有权 | 本地Serve发行/安装/升级回滚/卸载、多平台原生环境与正式包发布 |

### 候选合同不提升为稳定API

- 原 `sdk2-ext-v1` 仍锁定 `027de7e2d9b647374b7fe94cb0e4a7429a9195e2` 与首批schema指纹。新候选快照不悄悄扩充这个稳定镜像。
- 当前终端快照为 `terminal-services-v1 / 2026-09-26.candidate-4`，源修订 `b2cd6641e1886d9e74439e3b7623be57bc3247ba`，schema SHA256 `5973fde3f029f9c92794cde385e4041a150de0ace476bb2f6ff2c63c2a166dd0`，见 `src/Tansr.Sdk/Terminal/Contract/manifest.json`。producer/observer/HTTP transport均为内部候选；存在配置schema或验证向量不表示动态配置产品API已经实现。未知修订/指纹不能静默接受。
- 缓存 `sdk2-cache-v1` 原文标明私有、未发布候选；`CacheClient` 保持internal且显式启用。七份原文件锁定于 `Cache/Contract/sources.json`，其中旧schema SHA256 `a2b2c7c08bfcab934e6c184d946a62e44094dfa14546ff54be8ebbaecc20da07` 保留为baseline。当前消费的错误状态修正版来自 `cli-SRV-01-terminal-services` 基点 `b2cd6641e1886d9e74439e3b7623be57bc3247ba` 的未提交schema候选，SHA256 `88e7941861fab1a6380b9226e3a42adb8c872efdf720dc36e92ffa42da43d818`，作为第八份独立来源登记，不冒作原027冻结文件。票据、缓存组、历史见证及provider exchange的权威仍在Serve；终端只消费与恢复原操作。
- 原缓存RFC与实际路由的错误状态漂移已按Serve源头修正对齐：`mapping_unavailable` 为404/409/503、容量为429/503、epoch为409/503，mapping的404/409必须无重试和无回落。消费者重新采用完整具名schema条件验证并核对HTTP状态；没有跳过 `allOf` 或更改实际运行逻辑。本消费者所有副作用仍无自动重发；这次候选修正不等于完成稳定合同会签。
- 档案事件仍是已有独立binding协议；它的cursor不是会话数字序号。三份事实源分别锁定于 `Client/Contract/archive-events-sources.json`。只有业务回调完成耐久处理才推进游标；流结束、HTTP成功和上传完成均不能代替核心采纳或档案ACK。
- 档案消费的取消不释放尚未结束的业务任务槽：同client/binding重连等待原消费者真实结束，总在途消费者最多8个，防止不断重连造成悬挂任务或重复供材。原任务晚完成不推进已取消连接游标；此项新增回归随最终池验证。

### 本批验证事实：阶段通过与最终待验分开

| 范围 | 当前事实与证据 | 不能据此推出 |
|---|---|---|
| 合流解决方案构建 | `build-combined.log`：Release构建成功，0警告、0错误 | 固定24断言全部通过、真实UI/跨平台/正式产物已验 |
| 会话/档案/设备宿主阶段集中测试 | `core-assembly.log` 与 `results/core-assembly.trx`：65/65，0失败/跳过；早期 `host-archive.log` 红例保留，修正后由此阶段重验覆盖 | 与首批202或本批其他重叠池相加计完成率 |
| 核心候选阶段池 | `core-candidate.log`：467/467，0失败/跳过 | 后续新增/修正材料链已包含；最终池仍须以根任务冻结候选回执为准 |
| 真实Serve公开接线 | 早期4/4只含首轮档案；追加材料后3/4红保留；修正接收前修订刷新后最终4/4通过，见末节完整日志 | 一次无竞争通过不代表耐久ACK后的竞态已经闭合；公开恢复仍属NET-04/06内部缺口 |
| 本地Serve与进程阶段验证 | `local-serve-systemroot.log`：5/5；Windows管道、MCP等局部证据位于本批各子目录 | 本地Serve正式发行、升级/卸载或所有操作系统环境已验 |
| 媒体实现 | 代码、原生UI入口和受控单测已落，最终候选池待结算 | 真实图片/视频/麦克风、ASR/TTS付费请求、系统解码器或大帧网络消费已经通过 |
| 本批最终门 | 核心/Windows/集成最终池、格式、锁定恢复、本批包/AOT独立消费由根集中运行并补最终回执 | 用首批包SHA或局部结果代替本批最终产物；本节不提前写“全部通过” |

本批曾出现的构建/夹具/生命周期红例保留原日志，不覆盖、不累计为新增产品功能。`cache-candidate-audit.md`、`archive-events-audit.md`、`session-surface-audit.md` 是静态自审和归属记录，不是执行通过证明。Mac/Linux、标准用户、签名/公证、真实媒体和供应商费用未实测的部分继续据实保留，不因当前Windows本地门成功而关闭。

### 仍须处理的原卡事项与最终回填入口

1. **NET-04/06：** 公开档案→耐久接收→材料事件→原文响应→核心采纳的真实链已通过。接下来消费 Serve 单写的公开 ACK 恢复合同及显式版本化 receiver ledger；接收前刷新修订不代表消除了耐久接收后的竞争。未知结果按原账本查询，不能重生成请求号掩盖问题。
2. **NET-01/02/03/04：** 接收Serve单一写者最终合同，分别补动态控制/完整记忆/后台托管；候选配置定义、私有transport、原生管道都不能替代稳定公开装配。依赖仍归SRV-01～05对应原卡，不另增NET卡。
3. **NET-05：** 完成默认可信装配、原计划缺少的UI/无界面入口和16组真实对照；MCP默认仅text/image及媒体真实消费缺口保留，不以“平台差异”免验。
4. **NET-06：** 根任务冻结准确NET/Serve SHA与协议指纹，集中补最终测试/格式/锁定恢复/打包/独立消费结果；然后按工程纪律收编主线。提交、推送、主线CI、签名、渠道和发布分别记录，不互相替代。

| 固定统计层级 | 已完整完成 | 剩余 | 完整进度 | 本批新增完整关闭 |
|---|---:|---:|---:|---:|
| NET工程父卡 | 0/6 | 6 | 0% | 0 |
| NET完整对抗验收 | 0/24 | 24 | 0% | 0 |
| Electron完整能力对照组 | 0/16 | 16 | 0% | 0 |

上述比例只表示原完整DoD/同场景对照尚未结算，不表示功能代码没有前进，也不把内部候选折算成已完成百分比。本批未新增统计分母，原SDK2的20/118不变。

### 本批最终本地回执

以下结果覆盖本批代码；阶段结果不重复加总。它们只结算本次增量，不能代替六张原卡全部条件。

| 验证 | 实际结果 | 证据（相对本批archive） |
|---|---|---|
| 锁定恢复、完整Release构建 | exit0，全部核心/Windows目标及三示例0警告0错误 | `restore-locked-final.log`、`build-closure.log` |
| 全仓格式 | verify-no-changes exit0 | `format-verify-final.log` |
| 核心最终集中池 | 515/515，0失败/跳过 | `core-accepted.log`、`results/core-accepted.trx` |
| Windows完整池与原生MCP安装物 | 209/209，0失败/跳过；含原Node源库/缓存库双向消费、5项真实MCP EXE消费、本地Serve与输出清理 | `windows-final/manifest.json`、`windows-final/results/windows.trx` |
| 最后同步回调修正的受影响复验 | 8/8，0失败/跳过，包含新增凭据回调重入零HTTP断言；与209项有重叠，不相加 | `archive-sync-accepted.log`、`results/archive-sync-accepted.trx` |
| 真实Serve公开链 | 4/4；SDK1 3会话/9发送/34请求/0意外interrupt；公开kernel合成3次模型交换，设备Read、两轮档案ACK、DPAPI/AES-GCM重开、SSE材料响应箱及core-consumed | `serve-integration-final.log`；工作树忽略目录`artifacts/serve-integration/run-e7GJ0H/result.json` |
| 原合同/映射与候选 | sdk2-ext-v1原5文件校验通过；680映射、629已枚举出口、16组、行为完整验收仍0；内部terminal4快照自检通过，活跃Serve草案已变化而来源漂移门正确拒绝 | `parity-final.log`、`terminal-candidate4-provenance-final.log`、`terminal-source-readonly-diff.json` |
| 两包独立消费 | 新隔离NuGet源的net48真实CLR4、现代Windows消费及win-x64 NativeAOT实际运行全部exit0 | `pack.log`、`package-consumption.log` |

SDK核心候选包 SHA256 `298eb0ac0278b71b0b38edfc85230a71477851173f0c14ae1b909b9c184cf396`；Windows候选包 `ab90a38a246f73a1b55f6fe92689c51ba0c5d140af80fef2ed6a1d4b802db1db`。版本仍为本地开发候选`0.1.0-preview.1`，没有上传NuGet、签名、Git远端推送或生产部署。当前NET仓未配置remote。

修复与未关闭边界：本地Serve冷启动连接拒绝、极速退出CTS竞态、端口归属/EXE对象固定；输出Append拒绝必标截断、执行取消后独立封口、未知输送跨调用8槽保持、成功capture释放；档案SSE晚消费者保留原槽；同步凭据回调统一重入护栏。旧800ms实时断言改真实父子握手，保留原红。ACK冲突测试按原canonical JSON合同校验完整请求并额外固定requestId/epoch/revision，未放宽产品校验。单文件发布使用`NuGetLockFilePath`隔离RID恢复，正式锁哈希守卫及默认locked restore已实际通过；早期误用属性的失败日志保留。

`archive-ack-race.md`明确区分已修快照窗口与未修durable后竞争。后续新增ACK恢复接口必须先耐久固定原恢复request，再按Serve会签的新版本ledger、实际回执、配额预留和双副本同步证明处理；不得改旧ACK、伪造旧receipt、静默升级旧库或绕过删除/权限边界。Serve会话已接手同源协议和Node格式，C#待正式字段对齐后继续。

## 20260926-NET-03-native-services：终端服务、恢复与示例合流

起点 `d1b70d2827dd3a5589fe25730a4efc4afddb90f7`；沿用原隔离树 `J:/tansr/worktrees/net-NET-01-sdk`，新本地分支 `lane/net/NET-04-native-services`。实现按原卡分区并行，集中构建和测试。证据目录 `J:/tansr/archive/20260926-NET-03-native-services`；不另拆工程卡。

| 原卡 | 本批实装差量 | 保留边界 |
|---|---|---|
| NET-01 | 固定 terminal candidate-7、独立 ACK recovery schema/金样/DDL，以及原 SDK2 共用定义机械核对；原五份合同正文不改 | 上游工作树增量仍需最终主线源码锁定；公开入口属于显式 preview，不是稳定发行会签 |
| NET-02 | 同一客户端认证及 scope 下的配置 CAS、记忆指令/原操作查账；原键请求对象、显式恢复/重放；控制与设备分凭据的公开输出绑定、SSE视图及捕获接线 | 新配置候选支持 model/thinking；原创建预算及动态 systemFor 继续有效。应用提示词 policy/source 观察尚待原 P05 接线；不把新增预算来源 API 或配置专用只读查账路由追加为原 DoD 阻塞，配置原键完整 POST 是显式幂等恢复 |
| NET-03 | 原 Windows 执行器扩展后台启动/查询/取消/输出读取/产物删除，真实 started witness、Job收尾、固定文件句柄、输出帽与未知 runtime 语义；保留前台原执行行为 | 旧 runtime 不可凭 PID 接管；unknown/totalBytes:null 不能称成功；完整 hooks/插件/子代理与跨端对照仍待完成 |
| NET-04 | 原 SQLite Store 显式明文 sync recovery 创建/重开/迁移，耐久恢复意图、完整容量预留、不可变账本、COMMIT未知重开与原回执；独立 HTTP/coordinator、双副本恢复和 Node 互通 | terminal普通族、加密/cache/history恢复未覆盖；原文件和加密能力保留；记忆管理命令不等于完整终端自动出版生命周期 |
| NET-05 | WPF/WinForms/Console 同轮插入、原输入对账、本机草稿/离线呈现、preview配置/记忆；Console 1–8并发多会话worker，逐会话接纳/终局/清理 | 本机UI历史不作为可信模型供材；真实媒体录音/解码、当前授权约束下的档案装配和完整16组对照仍保留 |
| NET-06 | 本批集中构建、核心/Windows/真实Serve及包消费按最终回执登记；原父卡和验收口径保持 | 不把编译、模拟HTTP或局部通过当完整24项；远端CI、标准用户/多架构、签名与发行另记 |

### 已核出的修正与真实边界

- 远程分块捕获反复取消/释放时，旧 HTTP 若不响应取消，可能仍在后台运行。本批同时记录保留捕获与实际在途泵；只有真实 Completion 结束才释放容量，显式释放正文不绕过在途帽。公开对账支持调用者取消等待，预取消不先停止原泵。后者底层行为在起点已存在，本批公开接线时补强，不记成本批引入回归。
- SQLite 授权检查原先在 Scope 后调用 retention 委托；该委托若切换主体，事务可能先提交才被外层检查发现。共享末次检查改为读取 retention 后再核 Scope，覆盖新恢复与原 receive/confirm/body 路径；原格式不变。
- 双副本首次 Prepare 先验证两边完整耐久正文和原待确认操作，再写任何恢复意图。只比 head/pending 不足以证明正文完好。
- 后台输出达到上限后继续排空原进程管道，截断状态及时可见，不把已丢弃后缀拼成连续完整输出；实际局部红例已修，原前台进程回归同池验证。
- 首轮核心测试新夹具误用了异常基类精确断言或非法的协商 Limits；按原错误合同和原限制修夹具，没有放宽产品约束。原红日志保留。
- 真实 Serve 联验发现公开 archive 工厂漏透 `terminalCapabilities`，导致有效配置/记忆安装事实在发现层丢失，C# 正确拒绝 `unsupported_capability`。已交 Serve 单写者补实时 getter；不在测试夹具伪造支持，不做消费者降级。最终验证结果另记。

完整工程卡仍按原完整 DoD 统计：**已完整完成0/6，剩余6，本批新增关闭0，进度0%**。依原断言独立结算，**NET-A03、NET-A06 已通过；验收2/24，剩余22，本批新增关闭2，进度8.3%**。这是完整闭环比例，不是代码完成比例；不得把其他卡的发行或真实 UI 控件门追加给这两项原断言。

### 本批最终本地回执

产品提交为 `b33be4b76dda10ba86ae12fe704edcd646476342`，后续收编提交仅修正联验/消费夹具与回填文档，不改变已打包的产品源。以下证据均相对 `J:/tansr/archive/20260926-NET-03-native-services`；局部重叠结果不再累加。

| 验证 | 实际结果 | 证据 |
|---|---|---|
| 依赖锁、Release 全解决方案构建 | locked restore exit0；原依赖锁不变；0警告0错误 | `windows-final/manifest.json`、`build-candidate.log` |
| 全仓格式 | 产品候选及最终夹具 verify-no-changes exit0 | `format-verify.log`、`format-closure.log` |
| 核心集中池 | 627/627，0失败、取消、跳过 | `core-accepted.log`、`results/core-accepted.trx`、`suite-summary.json` |
| Windows 集中池 | 252/252，0失败、取消、跳过；含真实 Node 旧明文/密文与新恢复库双向互通、后台进程、原生 MCP 消费 | `windows-final/manifest.json`、`windows-final/results/windows.trx` |
| 真实 Serve 公开链 | 4/4，0失败/跳过，12.9071秒；主任务4次与独立无写记忆提取2次合成模型交换，无付费调用 | `serve-integration-7.log`、`serve-integration-evidence.json` |
| 真实链同源证明 | 3018个运行源码文件前后相同，35个 workspace source 解析；基点 `b2cd6641e1886d9e74439e3b7623be57bc3247ba` 加未提交候选 | `serve-source-snapshot-3.json`，SHA256 `f62f28ca2b2ae26980cd4badb3ac48c420ec4aa888e81fb07ce55cfe010fcc18` |
| 两个本地候选包 | 均为 `0.1.0-preview.1`；程序集产品版本指向 `b33be4b`，未上传 NuGet | `pack-core.log`、`pack-windows.log`、`candidate-artifacts.json` |
| 独立包消费 | 隔离 NuGet 源下 net48 实际 CLR4、现代 Windows 与 win-x64 NativeAOT 可执行文件均运行通过 | `package-consumption-accepted.log`、`consumers-accepted/` |

本次关闭的原断言按实际证据映射如下，不另生成任务卡：

| 原断言 | 已验证的原条件 | 同批证据 |
|---|---|---|
| NET-A03 | TS/C#原始同源向量逐字节编解码；UTF-8/边界数字/null与缺失/重复和未知关键字段/深度及长度/篡改摘要拒绝；原定义、候选定义与指纹核对 | `WireJsonTests`、`TerminalCandidateContractTests`、`EmbeddedCandidateContractTests`，均在 `results/core-accepted.trx` 通过；候选离线校验 `candidate-final-audit.json` |
| NET-A06 | 实际 HTTP Stream 每字节 SSE、UTF-8半字/CRLF/多行data、游标重连/重复/缺口、多观察者、慢UI有界、关闭消费；正文/思考stream/final/off、重放不双计 | `SessionEventTests`、`SessionViewTests`，均在 `results/core-accepted.trx` 通过；真实 Serve HTTP/SSE `serve-integration-7.log` |

固定16组完整Electron业务对照仍为0/16，六张父卡均为实施中。A01完整行为映射、A07完整动态控制与提示词、A09流式同命令基准、A12扩展组合、A14完整自动记忆、A17/18真实两框架UI等原条件不因上述两条通过而关闭；未齐证据不等于代码均未实现。

真实链覆盖公开 `DeviceSessionHost` 的 Windows Read、配置更新及原键重放、记忆 pin/原键查询、原密文档案 ACK 成功但丢响应后的重开查账、跨用户原 ACK 查询403、原档案 SSE 按需供材及核心 consumed；另外以真实 store 提交屏障形成旧 ACK 的409，再验证恢复请求已提交但丢响应、新格式 SQLite 重开后继续原恢复键、跨用户 rebase403。没有伪造 ACK 回执、重做原工具或重新生成请求号。Serve archive 工厂漏透能力的缺陷由 Serve 单写者修复，本轮真实链已验证该修正。

SDK 核心包462595字节，SHA256 `51abcca685b14da2256929ab55922d70f8983c58ba98d1512205b6ffeabc6d09`；Windows 包275221字节，SHA256 `3dacae41a57c3540367c77c9fa71507bf21232823180383ab55a15065189fafe`。原 SDK2 五份稳定合同保持不变；终端候选明确锁定 `2026-09-26.candidate-7`，schema SHA256 `8cd8c7c55a84c5700373aed75d5653a0737d718bfe0546641be367bda1a11896`；ACK 恢复 schema SHA256 `f530de1096b4f5d56ea688b7f2cec9ae66deb7d3719db85ab1f48287d3bd7ad4`。它们是显式 opt-in 预览，来源未提交的事实继续保留。

候选镜像离线20项校验通过；与当时活跃 Serve 源码的严格字节比对保留3处已解释差异：两份 TypeScript 参考文件换行规范化，一份 RFC 增补原 DDL 的 SHA 说明。schema/金样均不变，原镜像未被静默改写；运行源码另由成功联验的完整 snapshot 固定，见 `candidate-final-audit.json`。该比对 exit1 保留为来源差异，不能写成在线镜像门全绿。

早期红例和来源漂移门拒绝均保留：联验夹具错把只申请 Read 的工具面当成 Read+SearchMemory、漏给恢复会话做平台 initialize、外来主体先查能力而未抵达目标 ACK 路由，以及没有单独响应真实自动记忆任务；均按实际协议修夹具，未放宽权限、15秒空闲限制或产品重试。包消费夹具遗漏明确 PrincipalProvider，补回合成主体后以原包重验，不重打已验产品包。

本批源码收编到本地 NET `main`；准确最终提交及归档文件校验记入本批 `closure-manifest.json`。NET 仓尚未配置 Git remote，因此没有远端推送、GitHub CI、正式签名或发布事实。保留活动隔离树供后续原卡开发，不清除未知文件或其他会话工作树。

下一批继续原卡剩余项：消费 Serve 提示词来源观察与完整终端记忆出版链，补可信宿主的 hooks/插件/子代理装配与故障边界，完成两种原生 UI 的真实媒体及同场景对照，然后按原24项验收结算。原 NETSDK 方案/计划/验收单的跨仓状态由 Serve 文档单写者同步本回执；不得把旧节“未开始”当成当前实现事实，也不得把本批局部通过当作原完整 DoD 已关闭。

## 20260926-NET-04-prompt-observation：已应用提示词的只读来源

沿原 NET-02/05、P05 实施，不新增卡。起点 `0e56bd6d1fa94946cec925f41f6607863f98cfac`，隔离分支 `lane/net/NET-02-prompt-observation`；证据集中在 `J:/tansr/archive/20260926-NET-04-prompt-observation`。来源观察是原 Serve 元信息的显式加法，保持 terminal candidate-7 与 SDK1 默认请求不变。

- 公开 `AgentSession.ReadApplicationPromptAsync()` 显式请求原会话路径的 `?include=applicationPrompt`；旧 `GetMetadataAsync`/`ReadMetadataAsync` 的签名、默认请求与行为不改。`SessionMetadata.ApplicationPrompt` 使用相同只读类型投影。
- `ApplicationPromptState` 区分 Unknown 和 None。只有 live:true、恰好两个合法字段、可成立的 policy/source 组合才为已知；缺席/null/未知枚举/附正文/非活动会话均未知。原 HTTP/鉴权/网络/取消与主体不匹配错误继续抛出，不伪装成功未知。
- WPF/WinForms 增加“提示词来源”，Console 增加 `/prompt`；不要求 terminal preview。共享展示只使用校验后的枚举，不默认输出诊断 Raw。`sdk` 明确指开发者/Serve 可信宿主段，不授予终端修改系统提示词的能力；none 也不代表整个系统提示词为空。
- 真实 Serve 平台采用 prepend 的 P 段加可信宿主 S 段，同一会话首轮前后来源均为 platform+sdk，模型实际 system 前两段分别为 P、S；旧 metadata 默认仍无新增字段，观察响应仅 policy/source，不含正文。

锁定恢复及 Release 全解决方案（含 net48 WinForms、现代 WPF/Console）0警告0错误。受影响元信息/会话控制池83/83通过，包含新增30项来源验证，0跳过；没有重跑未变的627/252全池。首轮局部29/30的失败为新 SDK2 能力夹具遗漏原 canonical JSON 编码，按原合同修正后纳入83项通过，原红日志保留。

增强后的真实 Serve 集中链4/4通过、0跳过，16.8799秒；旧工具/档案/恢复链同时保留。主模型4次和自动记忆无写提取2次均合成受控，未增加业务轮或付费请求。最后源码快照 `serve-source-snapshot-2.json` SHA256 `46d628564dbc4bebf87c0227d91709354bc6977f584bf87ed2a90fb5e19899da`，3019个文件前后相同；前一份准备快照保留，未冒作实际运行来源。完整日志及回执为 `build.log`、`metadata-accepted.log`、`results/metadata-accepted.trx`、`serve-integration-1.log`、`serve-integration-evidence.json`。

本批典型来源观察不替代 NET-A07 的完整切模、失败回滚、活动轮快照及提示词变更矩阵。**工程完整关闭0/6，剩6，本批新增0，0%；完整验收2/24（A03/A06），剩22，本批新增0，8.3%。** 原16组完整对照仍待实结。完整记忆出版链、扩展装配、真实媒体与原生 UI 对照继续归原卡。

最终源码提交、格式、两包消费、主线收编与归档校验见本批 `closure-manifest.json`；阶段已通过结果不等于已发布。NET 仓没有 remote，不声明远端推送或 NuGet 发布。

本批最终产品提交 `2b8b269cef9fb1f71735e0e0966824772ee22733`；两包程序集信息版本均指向此提交。格式 verify-no-changes exit0，两包 `pack` exit0，隔离本地源下 net48/CLR4、net10 Windows 和 win-x64 NativeAOT 实际执行均通过，见 `format-verify.log`、`pack-core.log`、`pack-windows.log`、`package-consumption.log`。SDK 核心包465131字节，SHA256 `ddfe6d2cee794851da99bbdba5d490307f7029400e3af70541771feac42cd86a`；Windows包275907字节，SHA256 `f4afe81ff75e4d0573f1730073ac30cb582f9fcb9897a28d0425f67f41201f6b`。包消费证明兼容加载、原工具和AOT入口，新增来源方法的实际行为由83项元信息回归与真实Serve链证明，不把包内其他入口的烟测冒充新API真实调用。

## 20260926-NET-05-device-memory：设备自动记忆介质与原执行链

沿原 NET-04，并补原 NET-03/05 所需装配；不增卡。起点 `317c083f0fac231cb8858c4585441a589e392dc7`，分支 `lane/net/NET-04-device-memory`。证据目录 `J:/tansr/archive/20260926-NET-05-device-memory`。实现写区完成后冻结，统一锁定恢复、构建及核心/Windows/真实 Serve/包消费；最终命令与结果在下文回填。

- `SqliteMemoryPublicationStore` 复用原 Node `terminal-memory-publication-sqlite-v1` 三表格式，提供完整六动作、分块 UTF-8 正文、CAS 与不可变原 transfer 终态。身份、物理文件、固定容量及源代际在重开时复核；提交未知保持待对账，不能误作确定失败或空库重建。现有历史/档案库不迁移、不改格式。
- `WindowsMemoryPublicationHost` 把原保留 profile 接入原 Windows backend、DeviceSessionHost 与耐久执行日志。执行前后核原 scope、session、binding、source 及摘要。副作用后撤权、取消或回执不明进入 unknown；新 owner 恢复只授予可信 query，不授予旧 transfer 写权。
- Console、WPF、WinForms 共用显式设备记忆配置：原会话、可信来源、分离控制/设备凭据、create/reopen 及容量。设备只追加原保留工具，初始化省略 RequestedTools 以保留原会话收窄结果。Serve 的普通 CLI 启动仍无自动 `memoryPublicationFor` 配置；必须由可信宿主接线，见[接入边界](device-memory.md)。
- 执行宿主状态观察改用 controller；设备登记、领取与回执继续使用 device。原四参数构造保持兼容，查询前后核两票据主体一致。实际调度不再为每个非空批次支付250毫秒空闲退避；仍逐操作耐久认领/结算，只有空批次退避，不增加执行并发。

原32个连续就绪操作在4秒内结算的用例先实际失败（旧逻辑至少等待31次250毫秒），去除非空批次退避后和分票回归共30/30通过。首轮 Host 编译暴露新 Store 的 Equal helper 缺失，作者补齐后进入统一构建；没有把未运行测试登记通过。存储静态复核还修正 SQLITE_FULL 已自动回滚后的重复 ROLLBACK 误判，以及失败打开后的 owner 资源清理恢复；相应用例进入本批集中池。

真实链固定为真实 Serve/kernel 的合成模型提取 Read/Write、设备出版、SearchMemory 后续轮采用、pin 已提交 HTTP 失回原键查询、forget 及同会话后续检索、撤权零派工和同 owner 重开。它不是付费供应商测试，也不等于备份回放/跨连接维护全矩阵已验；后者仍归原 NET-A14/16。Node/C# 使用同一个物理 SQLite 文件双向接棒，来源14文件固定到 `42224634aa06d83f6bd8179935a072cc406bde5a`；运行 HEAD 另记，不能用无关提交移动代替字节漂移。

本批仍按原完整 DoD 结算：**工程完整关闭0/6，剩6；完整验收2/24（A03/A06），剩22，8.3%；16组完整业务对照0/16。** 局部实现和测试数量不折算完成百分比。NET 仓无 remote，未发布 NuGet；部署、签名和远端CI不是本批事实。

### 集中验收回执

| 验证 | 结果及证据（相对本批archive） |
|---|---|
| 锁定恢复、Release全解决方案 | exit0，0警告0错误；`restore-locked.log`、`build.log` |
| 核心完整池 | 661/661，0失败/跳过；`core.log`、`results/core.trx` |
| Windows完整池 | 333项执行，329通过、4条新夹具失败、0跳过；真实MCP单文件候选及默认锁恢复exit0；`windows-r1/manifest.json`、`windows-r1/results/windows.trx` |
| Windows受影响复验 | Host及原Node互通两个类18/18通过，覆盖上述4红，未重复启动其余315项；`memory-host-interop.log`、`results/memory-host-interop.trx` |
| 原合同及能力映射 | 原SDK2五文件与主线一致；680映射、629已枚举出口、16组，完整业务对照仍0；`contract.log`、`parity.log` |

Windows四条失败只修测试：原执行账本按 canonical JSON 保存，回读的字段次序与首次内存 receipt 不同，现比较完整规范化对象；Node进程的UTF-8输出原被系统CP936解码，现为三条标准流显式固定严格UTF-8。原全部字段、digest、中文、分裂UTF-8和不重执行断言保留。54项新Store断言在333池一次通过。两个池有重叠，不能按329+18虚增测试总数，也不把首轮333全部登记为通过；`suite-summary.json`记录原始计数。

联验准备发现并交Serve单写者修复原内部缺口：forget耐久提交后，旧MemoryHost固定删除代际，导致同会话原命令查询/下轮准备也被stale_generation阻断。不能在C#自动新建会话或重试写回规避。修复应保持旧任务失效，在合法安全点从原可信authority取得新host；真实链最终回执须包含此行为。
