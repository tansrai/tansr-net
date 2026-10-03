# NETSDK 实施记录

更新日期：2026-09-28。原 NET-01—06 已关闭，工程 **6/6（100%），剩0，本轮新增1**；A01—24通过，验收 **24/24（100%），剩0，本轮新增1**；P01—16为16/16。源码已推送公开MIT仓库 `tansrai/tansr-net`，主线 `13db8d5` 的GitHub CI通过，Core946/946、Windows451/451；后续文档提交 `c5a14b7` 的主线CI也已通过。用户指定的 `0.1.0.2` 双包来源为 `13bdf29`，后续只固定构建SDK并修正测试就绪标记的竞态，产品源码、锁文件与包元数据未变。NuGet账号 `Tansr` 已发布两个包；V3索引、公开下载、内容/仓库签名及三类独立消费均通过。上轮把额外作者签名误加为原卡阻塞条件，本轮按原DoD纠正；作者签名仅为可选增强，不能把仓库签名称为作者签名。具体事实以本页末节和 `J:/tansr/archive/20260928-NET-06-nuget-publication/closure-assessment-correction.json` 为准。

原六张工程卡、24项验收、P01—P16不变。本批开始前的已收编状态为工程0/6、验收4/24（A03/A05/A06/A07）；本批当前原卡结算在末节列示，正式跨仓单据由单写者同步，未将待UI或发行条件写成通过。以下各日期段是历史快照，其旧“未齐”、旧候选、旧计数及红例保留，不代表当前状态；最新渠道事实单独记录。

## 责任、源码和边界

用户已要求 SDK2.0 会话实施 Serve 补齐、本会话立即实施 C# SDK。已通过 Codex 会话消息完成交接。Serve 是运行内核和新 wire 的单一写者；本仓是协议消费者、设备执行器、存储和原生示例。原 Electron 完整 SDK/IPC 不变。

- 固定目录 `J:/tansr/tansr-net`，主分支 `main`；初始工程提交 `9bcb40c`。
- 开发树 `J:/tansr/worktrees/net-NET-01-sdk`，首批本地分支 `lane/net/NET-01-sdk`，当前分支 `lane/net/NET-06-code-audit`；本次从固定main `da7f3c29de8db4f6a843077ade53bb648b4ad2d5` 审计，产品冻结于 `759f123ad57b22d733f67929d7036c3a6cd08d8f`，门后连同文档快进收编本地main，准确提交见本次 `closure.json`。不推开发分支；原批次提交按历史记录保留。
- 原协议/同源源码 `tansr-cli 027de7e2d9b647374b7fe94cb0e4a7429a9195e2`。
- SDK2 schema SHA256 `969273844ca9196f19dd71b292b65a49307d63be0d20a0caf557e105ba6d8605`，详见 `contract/manifest.json`。
- 首批 Serve terminal 候选 `2026-09-26.candidate-2`，SHA256 `3cbb311f568bdc6d85b816fafbdb95254664219fdbd625dcadc0dfbdf4df84ae`，当时仅只读反馈；当前使用 candidate-7，来源及显式 preview 边界见末批记录，不据历史快照回退当前合同。

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

独立net48消费新增真实记忆IO后暴露旧打包缺口：SQLite初始化失败。原生包未缺字节，根目录DLL哈希与NuGet原包x86一致；当前SDK10.0.301在默认.NET Framework可执行项目先隐式推断win-x86，依赖按此复制单个x86 DLL，随后ResolveReferences把默认PlatformTarget恢复AnyCPU，实际CLR4为64位。上游SQLite的无RID双目录分支此时又因隐式RID而没有启用。修复须在包的buildTransitive中限定这个推断组合，使用已解析原依赖的x86/x64分目录，不覆盖用户RID/PlatformTarget、不切成32位求绿。原失败包、inner异常、PE/原依赖哈希和MSBuild前后属性留在本批archive；首次失败不计消费通过。

上述修复已加入 `buildTransitive/net48/Tansr.Sdk.Windows.targets`，WinForms项目引用也导入同一文件；保持依赖锁不变，只从实际已解析的原生资产定位同包两架构目录。定向诊断中默认CLR4/publish实际记忆链通过；显式x64仍为win-x64/x64，新增补目录item为空；WinForms也解析出正确runtime路径。正式新包自动导入与独立消费由后续回执单独证明，不用诊断的手动Import代替NuGet行为。

第二轮包检查又定位到构建目标未入包：原 `None Update` 在单目标inner build存在，但多目标outer build没有对应实例。改为显式Remove/Include后，outer/inner/_GetPackageFiles均只有一个打包项。保留 `packages-native-layout` 的原失败包与消费日志，第三轮从 `packages-final` 和全新独立消费目录验证，禁止在缓存中手工补文件后冒称NuGet已修。

第三轮两包产品提交 `799ebc4a8ecf39cef168c3836f2d850ba2fa0c99`；全解决方案重建仍0警告0错误。新Windows nupkg实际包含与源码一致的buildTransitive目标，全新消费工程由NuGet自动导入，net48实际CLR4.0.30319.42000与.NET10.0.9的Windows消费均完成新记忆创建、分块提交、关闭/重开/原终态/中文读取以及原文件/进程消费；win-x64 NativeAOT实际运行也通过（验证核心协议，不冒充Windows存储AOT）。见 `build-native-layout.log`、`native-layout-package-final.json`、`package-consumption-final.log`、`consumers-final/`。格式前次verify通过，最终增量另核；未重复启动未变的核心和现代Windows池。

第三轮SDK核心包466349字节，SHA256 `ada5a63439d56038f1a8761211e3dec1f015a027a09fae96af56a9a596071582`；Windows包295634字节，SHA256 `fb88041dbec9c8958a361c08d42c3489e991e0ca11547e3520c8f2ecd9e4db6f`，见 `candidate-artifacts-final.json`。版本仍是隔离本地候选0.1.0-preview.1，不代表已经上架。

### 真实记忆联验的修正记录

`serve-integration-r1.log` 在读取 Git 状态时因本机提交内存不足退出，未进入测试；关闭本任务闲置的 MSBuild 编译服务后继续，未关闭用户应用。r2执行5项，旧传输3项通过、两条公开链因 Windows 拒绝覆盖正在读取的测试命令文件而失败。测试 IPC 改为每条命令独立且不可覆盖的原子文件，保留原业务断言。

r3执行5项、4通过1失败：新记忆链已通过真实提取/写入，但 SearchMemory 等待用户审批，测试只记录事件、没有调用原 PermissionAsync，最终 stream_idle_timeout。现仅批准属于原会话、工具名 SearchMemory、未过期且原 requestId/digest 有效的两次请求，权限引擎不变。持久执行库当时169个操作均为 completed，不能把该失败称为客户端丢回执或 Serve 死锁；见 `serve-r3-operation-diagnosis.json`。

r4补回审批后保留110秒总期限、40秒业务轮、30秒流空闲及原30秒提取排空断言。实际提取写入成功，但分块链每次短暂空轮询后等待250毫秒，提取资源未在30秒内排空；4项旧链已通过，新链未产生通过结论，最终runner退出1。测试辅助程序对首个错误缺少即时失败响应也拖延收尾；本次补有界失败文件与独立关闭通道。正在修正真实执行宿主的空轮询退避，产品变化与后续完整回执单独记录，不抹去这些原红。

执行宿主现保持原HTTP轮询合同与逐条耐久执行，在收到并结算非空批次后，从25毫秒短等待逐步退避至原250毫秒上限；启动和持续空闲仍为250毫秒。它不并发工具、不自动重试HTTP错误、不改变旧请求或回执。脚本化的即时空批测试补上了原阻塞Channel替身的盲点，覆盖短等待/封顶等待中取消、顺序和一次副作用。完整记忆联验显式规划1024操作、512MiB逻辑字节和131072页；原默认64MiB只容纳约255份262KiB永久回执，不能承诺覆盖整组多次提取/检查/删除链。测试不自动扩容或清账，产品默认与容量负例不变。

### 本批最终联验

暖态轮询修正后的核心完整池663/663通过，0失败/跳过；完整Release构建0警告0错误，格式verify-no-changes通过，见 `core-poll.log`、`results/core-poll.trx`、`build-poll.log`、`format-final-poll.log`。不与先前661项重复相加；Windows333项及覆盖其4个夹具红的18项复验沿前表结算。

r5已完成所有业务链，最后因夹具把 `AgentHandle.settleResources(): Promise<void>` 的成功空返回值误当数组而失败（4/5）。修正为先在原10秒帽内等待真实handle清理，再核lifecycle的settlement数组。r6最终5/5通过、exit0、1.1108分钟：SDK1原3会话/9发送/32请求/0意外interrupt；公开kernel工具/档案恢复/提示词来源保持；新设备记忆5次主模型和6次提取交换均为合成任务。核心实际提取后经原MemoryPublication派工在Windows SQLite耐久保存，后续轮经真实审批检索并采用；pin丢回包后仅查询原键；forget移除正文及索引，同会话继续检索确认缺席；撤权零派工，真正排空后关闭设备，原transfer终态在同owner重开后仍可查询。406个操作、DB/WAL/SHM实测111558736字节，是该合成场景的账本证据，不是模型费用或通用性能基准。

本轮行为代码提交 `cc9ffe5`，最终夹具源码 `661612ad6c5f9128f2496c31fbdb4c5423e7cf26`；Serve运行源码为已提交的 `3ac68177d1f286a979671fb75f8bbfa0a1361192`。3020个受检文件与35条workspace source解析在联验前后保持一致；`serve-source-snapshot-r5.json` SHA256 `60b0616d7dceef8def941eabe0fc88c4d9c534d8ae90ff127a91e93c62eff8d8`。快照工具现在据受检Git路径实际状态记录sourceCommitted，不把已提交源误记为未提交候选。依据为 `serve-integration-r6.log`、`serve-integration-evidence.json`、`serve-final-summary.json` 及两份最终阶段日志；原SQLite身份绑定运行目录 `artifacts/serve-integration/run-kdA254` 保留，不移动后冒充原介质。

本次仍未覆盖NET-A14全部晚到写回、备份回放与跨连接维护矩阵，未增加完整关闭项。**工程0/6、剩6、新增0、0%；对抗验收2/24、剩22、新增0、8.3%；完整Electron对照0/16。** 后续重点仍为原卡的扩展装配、UI/媒体实操、混装与完整记忆恢复矩阵，不能把这条典型链或本地包当整套SDK已经发行。

最终两包从 `661612ad6c5f9128f2496c31fbdb4c5423e7cf26` 重建；程序集信息版本也指向该提交，包含暖态轮询修正。`build-pack-closure.log` 为0警告0错误，两个pack exit0。`packages-closure` 经全新 `consumers-closure` 隔离本地源实际消费，net48/CLR4与.NET10 Windows的新记忆IO、旧文件/进程入口，以及win-x64核心NativeAOT均通过，见 `package-consumption-closure.log`。SDK核心包466692字节、SHA256 `9d15c35c8b9a20bfda35b3848a34d866bf6c4cde80c41bb03c553c0ac3b8b02b`；Windows包295632字节、SHA256 `97f05efa4fde62ae7d83fa90d21772bbabbae353f09544d1f342f35b1058c839`，完整索引为 `candidate-artifacts-closure.json`。前述799ebc4的第三轮包保留为中间证据，不再是本批最终候选。

本批收编本地NET main的准确提交、运行目录归属和归档清单位于本批 `closure-manifest.json`。后续仅回填文档，不改变已验产品源和候选包。没有配置remote、没有GitHub推送/CI、没有签名或NuGet上传；活动开发树保留在 `worktrees/`，不删除其他会话材料。

## 20260926-NET-06-session-execution：执行通知与真实会话控制

起点 `8b072a23a1dd1fba3e28adbdc5c0b477e34161b7`；原隔离树 `J:/tansr/worktrees/net-NET-01-sdk`，分支 `lane/net/NET-03-execution-notifications`。本批证据集中在 `J:/tansr/archive/20260926-NET-06-session-execution`。沿原六张工程卡及24项断言实施，不新增统计卡。Serve/kernel 仍由 SDK2.0 会话单写，C# 只消费原候选协议。

- **NET-03：** `ExecutionHostOptions` 和 `DeviceSessionOptions.ExecutionNotifications` 显式装配已协商的 executor SSE；`TerminalExecutionNotifications` 使用设备自己的窄凭据。通知只唤醒原领取循环或查询完整原操作，不代替耐久 claim/receipt，不执行通知正文。原构造函数与轮询路径保留，失联只重连原连接，权限/代际/绑定错误失败，不自动重注册。停止时等待真实观察退出；忽略取消的来源报明确超时。
- **NET-02/05：** 三种示例通过公开 `ReadMetadataAsync` 分开展示 selected、active、核心预算和 lastUsage；缺席保持未知，累计用量不冒充上下文占用。共享配置按钮对已确定的首次拒绝保留原不可变证据，允许用户明确发起下一次操作；未知提交仍只恢复原键。Console `--once` 改用已有 `StartRun` 的接受/本轮终局关联，避免旧终局重放误判本轮完成，交互式多轮入口保留。
- **NET-06：** 新增8项真实 Serve 会话控制/提示词场景及3项 Windows 原生执行场景，覆盖原操作回执丢失、同轮插入、配置回滚、实际分块双流、输出重连与进程树取消。真实 kernel、执行账本和输出 spool 均运行，仅上游模型为合成响应；未付费调用，未宣称 Electron 同机场景性能对照。

### 实际红例与修正归属

1. SSE 的合法初始重试帧无 name/id 且 data 为空，候选 JSON 解析原先将其误判。只跳过此精确空帧；命名帧、有 id 的空帧和非空坏 JSON 仍拒绝。executor 的 `reconcile-required` 沿原 Node 合同允许较低/相同通知游标重开，不改变工具输出序号和原回执。
2. 执行通知停止时，关闭 HTTP 流可能返回 `network_error` 而非取消异常，旧外层 catch 会误报宿主失败。新增确定性红例复现原栈；仅停止后的流收尾按取消处理，停止前已记录的致命错误仍抛出。原红/绿日志分别保留。
3. Windows 全池的4项旧 Node 互通在进入行为前被固定源码 SHA 校验拒绝。建立只读 detached 参考树 `J:/tansr/worktrees/cli-NET-legacy-contract`，准确锁定原 `027de7e2d9b647374b7fe94cb0e4a7429a9195e2` 后4项通过；没有修改产品、原合同或SHA门，参考环境归属见 `legacy-contract-reference.json`。
4. 新会话真实控制链发现 Serve metadata 使用已签发但尚未进入可重放日志的游标，立即订阅产生 `event_replay_gap`。交 Serve 单写者修正事件发布屏障；C# 不关闭 gap 校验、不改客户端起始游标或增加等待时间。最终来源与实跑结果须以下方回执为准。

5. 原生 STREAM 联验曾被停止后的通知错误打断；修正停止竞态后正常流、真实中断、输出失回三例通过。LOSS 的原输出捕获已明确失败，`CloseAsync` 仍保留同一异常对象；测试精确核对此事实，不能把它当成正常输出成功，也不吞掉任意清理异常。所有失败路径保留主异常与时间记录。
6. 新控制夹具初跑误将 `recoverable:true` 的 `context.output_budget` 告警当致命终局；同时设置合成输出帽128，导致核心依法关闭低于最小1024的思考预算。夹具按原终局合同区分告警/致命失败，并将合成输出帽设8192、思考预算1024，实际验证 TWP `reasoning=low` 及关闭后缺席；产品默认和付费预算未改。
7. 原记忆联验暴露 `forget` 已耐久提交但回包 `stale_generation` 的回归；原管理观察与实际 SQLite 互证 `committed/durable:true`、revision3→4、删除代际0→1，主题和索引已删除。Serve新增已观测删除代际防回退后，既有路由提交后仍用旧代际生命周期复验，遮蔽了成功回执。初审把两份未变化文件误概括为三份不变；根复核确认 `memory-management.ts` 有实际差异，按新增防回退与既有回执语义的兼容回归登记。原输入/操作键保留，未重投或跳过；由 Serve 原记忆写区修正，最终复验另见下方回执。

### 本批本地回执与固定卡结算

产品源码为 `c7be8aaab523885909f0f62ad0611e1ca9ce2a21`，后续夹具/文档修正不改变产品或包。以下证据相对本批 archive；重叠批次不相加。

| 验证 | 结果 | 证据 |
|---|---|---|
| 锁定恢复、全解决方案 Release 构建 | exit0，核心两目标、Windows两目标及三示例0警告0错误；项目引用锁更新不改变依赖版本 | `restore-locked.log`、`build-r3.log` |
| 格式 | 全仓 verify-no-changes exit0；最后控制夹具另复验 | `format-final.log`、`format-controls.log` |
| 核心完整池 | 703/703，0失败/跳过 | `core-final.log`、`results/core-final.trx` |
| Windows 完整池与环境复验 | 原343项通过339、4项固定源门拒绝；正确原SHA复验4/4，无未解决行为失败 | `windows/manifest.json`、`windows/results/windows.trx`、`legacy-reference.log`、`results/legacy-reference.trx` |
| 原生执行3场景 | 3/3，真实进程/Serve spool/SSE，原操作各执行一次；177块为后续同源all组事实，不能当性能基准 | `serve-execution-r2.log`；后续`serve-integration-r3.log` |
| 会话控制与提示词8场景 | 8/8，0失败/跳过，4.9262秒；25次合成请求，无付费模型 | `serve-controls-r4.log`，工作树`artifacts/serve-integration/run-2v3my1/result.json` |
| 上游同源 | candidate-7；base `07b32658` 加已冻结未提交源，3020个文件/35条workspace解析前后相同 | `serve-source-snapshot-final.json`，SHA256 `c13968658df8f23700c5cb56b7a0cea66c606b0443def75b66dd14dc0bb73058` |
| 两包与独立消费 | `0.1.0-preview.1`；net48实际CLR4、现代Windows及win-x64 NativeAOT均exit0 | `pack-core.log`、`pack-windows.log`、`package-consumption.log`、`candidate-artifacts.json` |

SDK 核心包477255字节，SHA256 `79680cf7c41bf9af306a623ae9e85dd0f2054a068a46104f35ccaa7052ec9efd`；Windows包295628字节，SHA256 `ca1e85889b845c1d679296e21327fbdad489aed3a80ef970c1943bcc8c35bfa2`。两包程序集信息版本均指向 `c7be8aa`。原红保留：首次all16为6通过/10失败；修正发布屏障后all16为13通过/3失败，其中两项控制夹具已有8/8补验，记忆删除仍待本轮最终补验；不能把旧分组结果直接称为一次16/16全绿。

| 原断言 | 本轮完整证据 | 结算 |
|---|---|---|
| NET-A05 | 真实图文/同轮插入、accepted失回原键恢复、冲突/旧target/终态拒绝、观察断开不interrupt、取消后原会话继续；另以703池中的 TurnInputEditorTests/SessionRunTests 验证拒绝保留原草稿、未知不换键以及受理/终局/取消观察边界 | 通过 |
| NET-A07 | 真实同会话配置CAS、失回原请求重放、切大/切小拒绝及回滚、忙态保持模型快照、思考启用/关闭；六种fallback/prepend与宿主缺席/显式/空段组合及下一轮刷新；703池中的6项 SessionContextTextTests 和三示例展示接线保持模型/预算/用量分别来自核心 | 通过 |

**原工程卡0/6，剩余6，本批新增完整关闭0，进度0%；原对抗验收4/24，剩余20，本批新增关闭2（A05、A07），进度16.7%。** 原A03/A06保持通过；完整Electron对照0/16。此比例是完整DoD闭环比例，不是已实现代码比例，不给部分卡另折算权重。A08全公开API、A09同机场景性能、A10四向取消、A12扩展装配、A14完整记忆恢复、A17/18实际UI与原发行条件继续保留，未转为额外阻塞A05/A07的条件。

### 最终记忆回归与收编

Serve 单写者将提交后核验改为读取当前 source、重新核当前 authority 与可读权限，保留写前旧代际拒绝；上游局部证据覆盖 POST forget/原回执 GET/原 POST 重放不重做、旧 writer 失效，以及提交后撤权不泄漏回执。C# 原记忆断言、请求键和超时均未改。

原真实记忆单场景最终 **1/1通过、exit0、59.5252秒**，主模型5次/自动提取6次均为合成响应。自动提取→终端耐久出版→下一轮检索采用、pin失回查询原回执、forget正文/索引与下一轮缺席、撤权零派工、同owner重开原回执及全部收尾均通过。证据 `serve-memory-r5.log`，原运行目录 `artifacts/serve-integration/run-ZJHnjF/result.json`；source快照 `serve-source-snapshot-memory.json` SHA256 `75b2f25d659ac3d0077b67972df4be27de4f206f90210d43f1c7f0e5231c00cd`，3020文件/35解析前后相同，base07b的未提交候选事实保留。

16项真实联验已按受影响分组全部覆盖通过：r3原13通过中含6项提示词、3项执行及4项旧公开会话/档案；r4控制8/8覆盖两项夹具失败并与其中6项重叠；r5记忆1/1覆盖剩余失败。不同源与重叠批次不得相加或写成一次16/16。最后两次只跑受影响分组，未反复启动完整记忆池；本批无未解决的新增NET或本链Serve正确性失败。A14其余矩阵仍未因此自动通过。

NET产品提交 `c7be8aa`、最终夹具提交 `d70a4b4` 及本节文档收编本地main，准确最终SHA与归档文件校验见 `closure-manifest.json`。三单回填输入为本批 `net-doc-handoff.md`，CLI中的正式三单由Serve会话同步。NET仍无remote，未推GitHub、未做远端CI、未上传NuGet、未签名或部署；原固定仓保持main，活动NET树与只读旧合同参考树均保留并登记。

## 20260927-NET-full-delivery：集中实现与验收回填

本节为该批历史状态，保留上面各历史批次的原红和当时数量；最新状态见末节代码审计。归档为 `J:/tansr/archive/20260927-NET-full-delivery`。仍只有原 NET-01～06、A01～24、P01～16；源码实现、局部行为、完整验收和正式发行分列。

### 当前来源与实际门

- 功能代码及示例的主线收编点为 `a247abe346d319bd088d388529becd36ca09ffd3`，包含下述产品2958；最终含工装与回填的主线SHA见本归档 `net-final-closure.json`。本批产品经 `9dd4c16`、`105bf9c`、`83f99f8` 及 `c8b6dfe` 补齐，后续示例/夹具经 `afc8e01` 等收编；开发树 `a71769c` 修复注册后刷新能力版本再绑定，`1304c92` 补原生媒体与窗口收尾，最新产品为 `2958ed9ba827ab64d9ca5174da18a58eb3f8bee8` 的显式紧凑日志。后续 `e477995` 仅新脚本，已随main收编；`a247abe` 已入main，仅修示例主体隔离，未改两NuGet产品。各次运行仍按实际源快照和文件摘要归属，未提交增量不能仅以HEAD标识，也不能将后续未收编脚本或证据提前归为主线。
- 已实际消费的历史两个包来自 `c8b6dfe1ef074c466a140a8dd98f0d6482b9f8c3`，版本 `0.1.0-preview.1`。Core：559561 字节，SHA256 `c9d8c3f6e79d25c0f33f3754a6a5a4784e207101f470e5e40e72b8a3a4d71dfa`；Windows：330461 字节，SHA256 `a53134c5a91e3dfaf3e2b2311aeb3b937a111c6a40a9345a3ff0ef21c664d028`。见 `consumers-final-r2/manifest.json`。a717与2958已改变Core/Windows产品实现，`package-source-stability-r3.json` 的源码未变结论仅属当时历史，不能以旧包的通过代替新候选。
- 产品2958的新包已在 `packages-final-r4` 重新产出，版本仍为 `0.1.0-preview.1`。Core：559748字节，SHA256 `faec64499dcf641d8a1d015b59b51635a82fd4d294edac0feec1b996d1c8eff5`；Windows：331017字节，SHA256 `e1ca8a555696e345138634005367a9110b9ce39fec7d8b8217aed4a010ffcfec`。`consumers-final-r3/manifest.json` outcome=passed，四类消费者实际运行；`api-compatibility-final-r2` 原公开兼容通过。实际NuGet三示例在 `A24/package-examples-r1` 构建3/3，完整业务由GUI/Console原入口继续验证，不以构建替代。
- HTTP `http-closure-r4` 及性能 r5 使用 Serve `04c6472aacf0f169fcf9a6b28ecbbf0df83a74fb`，运行源快照 SHA256 `366780e4ed48a2802ad515a2acfdfa3a38c628f58c21e6be46c5899a935e5eb6`。后续预算恢复和可信扩展使用 `d0dc9cfb5b00313a6751d04c3813abf4fc97a380`，运行源快照 SHA256 `5d5093f6d1f82d96396c8e6098a457686dca8646b49b11c6fe74498196e12b2c`。均为 candidate-7；原 SDK1/SDK2 wire 锁不变。模型/媒体上游为合成材料，真实的是 Serve/kernel、HTTP/SSE、公开 C# SDK、Windows 文件/进程和本地存储。
- 原配置错误分类修后UI r13使用Serve `b988eee00e51fa31133e13429bb84b54f9a61526`，运行源快照SHA256 `b8f96db8dbc823a5f05568e6c61f20ba43e9c38964b9c30358128ca86c67f32c`。A20 Ctrl+C复验使用此前不可变b9e独立SEA，该次Core/Windows为c8、仅Console示例增量，DLL SHA256 `7ec44124719a3d2c73a6bd49f30ffca809e1dfcfaa2835a2916da0b3ebd05f2b`；保留这次历史来源，不将其外推为后续a717候选或伪称同源新整池。
- A23 最终 `shared-final-r10` 使用 Serve `3a0659fbc0ba91399a738df5c849ffa4fe5f9776`，源快照SHA256 `e36087ac3c1f3f713328e93398387c9f71f87d92ab539690d4fc65700594cf39`；同源3053文件、candidate-7。C#、Electron、Android、iOS、鸿蒙实际五客户端同场通过，具体客户端二进制与脚本SHA见 `A23/shared-final-r10/shared-manifest.json`，不能用单个当前HEAD替代它们各自的产物来源。

| 本轮门 | 实际结果 | 证据与范围 |
|---|---|---|
| Core / Windows 当前唯一用例 | **Core 914、Windows 452个唯一用例均有通过证据** | `coverage-final-r6.json` / `.md` 按当前DLL发现清单和完整testName（含Theory参数）选最近实际结果，缺证/未决均0。原r5为903/452；`identity-tests-r1/identity.trx` 14/14仅新增11、替代3；`identity-windows-r1/console-offline.trx` 16/16全部替代旧名。原日志17/17只增9及Hosting60/60只增5的事实保留。不是一次新1366项全池，也不是行覆盖率；原红/跳过及各池计数保留 |
| 协议/版本混装 | C#21/21；原 Node 共享向量及同票刷新对照通过 | `session-compatibility-final.log`、`session-compatibility-node-final.log`；默认 SDK1 无新发现请求，显式 SDK2 失效不降级/重建/换存储 |
| 旧公开 API 兼容 | 2958新候选四 TFM 5286 个原公共/受保护成员实例通过；旧 source→旧 DLL、旧 source→新 DLL、旧 binary→新 DLL 三类退出码均0 | `api-compatibility-final-r2/result.json`、`consumer-result.json`；r1保留为历史。含 TFM 重复，不是5286项行为验收，保留旧 `null` 重载兼容见证 |
| 完整入口归属 | 681入口、16组、11源快照；39职责路由和10个检查器对抗用例通过 | `doc/compatibility/public-api-map.json`、`parity-final-main.log`、`parity-final-checker-tests-r2.log`；39条是索引，16组完整业务条件逐项结算。最终回填后原检查器测试3项仍依赖历史零完成初态，原7/10日志保留；仅将测试显式构造为待验状态并保留未验入口/剩余条件拒绝，原10项复验全过，检查器产品规则未改 |
| 真实 HTTP 修复链 | `http-closure-r4` 原19项17过2红；随后预算恢复典型通过、可信扩展典型通过 | `http-budget-closure-r2/runner.log` 原2项1过1红，A08已过，原可信扩展文件帽红保留；`http-trusted-closure-r1/runner.log` 对后者独立1/1通过。按各自源码/夹具归属，不称同一次新完整池全绿 |
| A09 同机输出性能 | 各100样本；Electron P95 **39.2560 ms**，Serve loopback **37.7073 ms ≤150**，受控20 ms RTT **57.1933 ms ≤250** | `benchmark-final-r5/acceptance-evidence.md`、原 `comparison.json` 和 `runner.log`；真实多块、单一执行、原 receipt/输出相等，八项资源清理均完成。C#终点为原 TerminalOutputView，非两GUI绘制或真实公网时延 |
| 双包/实际消费者 | 2958新包的net48 CLR4 WinForms、现代控制台、WPF self-contained、核心 Native AOT 四类实际运行通过，NodeOnPath=false；实际NuGet Console本地/远端×新建/恢复4/4通过 | `consumers-final-r3/manifest.json` 对应packages-final-r4两新SHA；四 TFM 资产选择正确，不宣称 WPF Native AOT。r2旧包消费另行保留。三套真实NuGet示例构建3/3见 `A24/package-examples-r1`；Console业务见 `A24/console-package-r1/closure.json`：合成8次exchange、resourcesCompleted、7PID/2端口已回收，Serve为b988不可变SEA。GUI业务按原组合证据已完成，最终主体隔离消费另见 `A24/package-examples-identity-r1` 和 `native-user-switch-r3`；不重复计入原A20/Ctrl+C |
| 依赖及标准用户安装 | 当前新两包与34依赖/native/许可已核；原新普通用户首次应用配置实际安装→升级→回滚→卸载、私有文件和跨用户 DPAPI 拒绝均通过 | `consumers-final-r3/dependency-audit.json`；标准用户完整安装链仍归原 `A21/standard-c8-r2/results/child-result.json`，不冒称在2958重新执行。原链非提权、非管理员组、不继承开发环境，账号/profile已回收。新包普通消费者进程与原标准用户链分列；现有Windows新用户不冒充全新OS，3项legacy license URL原样登记 |
| A23 同 Serve 五客户端 | C#、Electron、Android、iOS、鸿蒙同场通过，原故障隔离、健康会话及宿主边界核验成立 | `A23/shared-final-r10/shared-manifest.json` passed=true，SHA256 `fde98055f7bed5177843da0ed0b892a9d731c6502a9df458e4d24192c53e8e43`；三个不同主体、移动三端共用其原主体，故障施加于C#，不是五端×四故障笛卡尔矩阵。独立Electron完整SDK/IPC原回归仍按自身证据归属 |

### 已实现与此次发现的实际差额

本批高阶接线包括授权主模型目录/别名/能力、本人1d用量；可信 Serve 模型装饰/采样、原权限/预算调度；公开会话、档案、记忆、缓存和资源观察；三端共享工作台、MCP/Skills、媒体与显式录音/分段TTS、Worker、本地受信 host-module 和冷启动介质只读。存在实现不等于完整 UI 已过，原 Electron 完整 SDK/IPC 保持不变。

| 原卡内发现 | 修正与当前事实 | 验收边界 |
|---|---|---|
| 缺文件回执与根目录 mkdir 阻断 Write | 确定的 `not_found` 映射原 `ENOENT`；仅 `fs.mkdir` 支持已存在 workspace 根的幂等结果，并重核根句柄，其他写/删不放开空路径 | 原真实 Read/Write/Edit/List/Glob/Grep/BusinessLookup、撤权零调用及 Serve 宿主不回落已通过；未知 IO 与重解析守卫保留 |
| SQLite 锁/FULL与原子保存 | 锁有界；按事务实际状态处理 FULL 自动回滚。进程祖先目录 pin 修正避免阻断同盘原子移动，保留可执行文件身份、写保护和重解析边界 | writer 前/后 kill、锁、真实 SQLite 页帽 FULL、进程 lease 期间保存均有证据；无必要的媒体锁重试已撤回，不冒充整块物理盘耗尽 |
| 旧 API 源码兼容 | 内联 Skill 使用 `FromInline`，旧构造及存储异常语义保留 | 旧 source/binary 实际通过，不以新接口存在替代兼容证明 |
| P06 增量叙述与运行中呈现档位 | `SessionNarrator` 消费原事件，`SessionView.SetDelivery` 保留同一视图，Off 立即清思考且不复活 | `core-closure-r1/core.trx` 原9项覆盖叙述、动态档位及失败隔离；三端完整实际控件门仍归A17/A18 |
| 预算终局后持久恢复被拒绝 | Serve 原稳定点修复预算 aborted 终局遗留 released/running 所有权的实际缺口；不放宽未知副作用的对账边界 | d0dc 上原快照/镜像链620→645、旧checkpoint仍645、fork父账不变、预算拒绝零新请求、再恢复下一轮累计800通过。原治理70费用/60预算、同ID恢复及零新模型/设备写也通过 |
| A07 确定配置拒绝被误报未知 | 未授权模型原被Serve误映射503 `source_unavailable`；Serve原写者修正确定错误分类，客户端未知请求保护不变 | 原r10失败保留；b988源`native-ui-r13/ui/partial.json`实际通过非法模型确定拒绝→原配置不变→清thinking changed→原键replayed。A07及NET-02恢复通过，不以此称UI整批通过 |
| A20 新 Ctrl+C 输入阻塞 | 原输入适配器同步读阻止取消等待；Console改为唯一顺序读者、取消只结束等待，input/service/device原寿命保留 | `A20/ctrlc-final-r1`原红保留；r2真实CTRL_C在原25秒内完成interrupt→quiesce→resourcesCompleted→device_cleanup，未关闭stdin或补发命令助跑。驱动exit0、Console取消exit130；A20恢复通过，见`A20/ctrlc-acceptance-evidence.md` |
| 注册替换连接后使用旧能力版本绑定 | `DeviceSessionHost` 原私有RunAsync在注册后以同一初始化参数重新读取并校验有效能力，再进行唯一bind；保留原scope/platform/session核验，刷新失回不重试初始化、不绑定 | `a71769c` 产品修复；原类19/19含新增5项，最终 `device-rebind-hosting-r2/hosting.trx` 相关60/60，原装配顺序核验为两次initialize/一次bind；多目标构建零警告错误。该注册修复后来由两GUI原恢复/新工具链消费；新包和完整UI仍按各自回执归属，不把本60项局部通过冒称完整UI |
| 默认执行日志预留耗尽阻断示例记忆 | r23实际254个已终结操作仍按每条256KiB耐久预留计量，66,893,580/67,108,864逻辑字节，剩215,284不足下次262,144字节claim。2958保留默认 `sdk2-execution-sqlite-v1`，显式 `CompactCompletedReceipts=true` 才使用独立 `sdk2-execution-sqlite-compact-v1`；pending仍全额预留，终态同事务释放未用padding，永久保留原键/完整回执/操作数；不隐式迁移旧库或重做unknown。示例JSON必须真bool，并只显示安全storage code | `r23-memory-journal-diagnosis.md` 原UI红保留；`compact-journal-r1/journal.trx` 17/17（新增9、原8）、locked restore及全Release构建0警告0错误、`format-compact-final-r1`退出0。新格式仍受字节/操作数/页帽约束；后来r26 WPF、r27 WinForms实际记忆链已过，各331个原操作全部completed、0pending/0reserve，逻辑字节922080/922358；两个原批整体exit1保留，只采用已完成记忆链及关闭事实。两端冷启动离线记忆控件亦实际通过，后续r30补齐net48朗读/保存/取消unknown链，A17/A19经原DoD独立核对通过；不以17单测代替 |

双GUI记忆增量有实际控件证据：`native-ui-r26/ui/partial.json`（WPF）和`native-ui-r27/ui/partial.json`（WinForms）均完成提取→pin/query/replay→批准remember→forget→关闭。两原批整体仍exit1，不能重写为两次全批绿。`r26-compact-journal-readonly.json`、`r27-compact-journal-readonly.json`确认原64MiB帽、各331永久键、14次出版全部提交；只读数据库核账不作为授权来源。`native-offline-memory-r1/ui-r1/result.json`两端在无凭据、不连接Serve的冷进程中通过真实“离线授权记忆”控件读取，原forget删除内容未复活；离线许可由可信同scope/revision材料提供，不声称离线实时获知新的远端撤权。最后speech由 `native-ui-r30/ui/result.json` 完成，uiCode/fixtureCode均0；`original-native-card-closure-review.md` 已逐条核原条件，A17/A19可独立关闭，A18后续修后组合实证及独立核对见下段。

原A18切用户已产生真实红例：`native-user-switch-r1/ui/partial.json` 中B尝试恢复A原会话得到403，B新会话历史、pending与模型轮次均正确隔离，但编辑器仍显示 `NATIVE_PRIVATE_DRAFT_A`。原因是示例按应用固定文件加载草稿，且未将呈现/异步保存绑定可信主体。`a247abe` 在共享示例层按来源与可信principal/application/endUser分区；三个示例在连接前绑定，换主体清原呈现/草稿/窗口，回调捕获原会话和原分区，读写重新核验，未知主体不自动认领旧文件，同主体续票保持原分区；显式无票据档案/记忆离线授权入口保留。不新增SDK或wire，也不伪造Serve认证。原14/14与16/16受影响回归、全解决方案构建0警告0错误、format退出0均有回执；`A24/package-examples-identity-r1` 已从2958原两包重建三个示例。`console-identity-offline-r1/receipt.json` 的四次实际新Console进程均exit0：A与同主体authRevision变化可读原草稿，B与未知主体不认领A或原无主legacy数据，原A种子文件SHA不变；不联网、不用票据、不启动Serve。`provenance.json` 同时绑定a247示例DLL、2958双库DLL和实际包构建manifest，原合成临时目录已按映射归档。两GUI原切用户场景现由 `native-user-switch-r3/manifest.json` 组合关闭：WinForms完整原场景exit0；WPF保留r3定位失败exit1，以原前段加同一B会话ID的 `ui-WPF-tail/result.json` exit0补齐公开恢复、空历史、B模型轮及A/B无票据cold草稿。不是一次WPF整轮绿。`cleanup-receipt.json` 记录fixture退出0、自有GUI0、SSE订阅/排队0及原源指纹未变；原r1真实草稿红、r2无效认证观察和r3定位红全部保留。完整GUI索引见 `native-ui-acceptance-final.md`；`original-native-card-closure-review.md` 已独立逐原DoD核对，允许关闭A18及NET-05。

### 按原条件结算

**工程已完整关闭5/6（83.3%），仅剩NET-06；相对本批开始前已收编状态新增5张，本次最终结算新增关闭NET-05一张。** NET-01 的 A01—04、NET-02 的 A05—08和完整可信扩展、NET-03 的 A09—12、NET-04 的 A13—16、NET-05的A17—20均已满足原条件。A07原错误分类新红已经实际修后通过，NET-02恢复；A11窗口效果补齐使NET-03可独立关闭。不能给已过协议/工具/存储卡追加全部UI或正式发行前置。

A15 同一会话实际 Enable→Flush→Disable→Enable 保留原ID/history/图片与SSE语义，SQLite密文可用原DPAPI key独立重开；默认不开盘、不自动导入、不冒充长档案/current-authority、不关掉 source-required 或平台留存。与旧 Node 三类介质互通、第三方Store、显式迁移/保源/中断回滚、不支持组合启动前拒绝和原 S1/S2/S3 组合互补，见 `storage-closure-review.md`。A16 的原票自然TTL拒绝由 `api-cache-expiry-r1/receipt.json` 互证，不将 epoch 失效或未观测费用改称供应商缓存收益。

A11最后的自定义窗口工具已由`native-ui-r11/ui/partial.json`及同会话原journal核实：批准后实际标题更新，拒绝/过期标题不变，与真实文件/授权/宿主不回落链互补。见`net03-final-closure-review.md`。r11整批UI仍失败，仅采用实际完成的原窗口断言，不宣称A17—19通过。

A20双模式原链见`A20/acceptance-evidence.md`、`console-final-r1/manifest.json`：同一发布Console和独立SEA，本地/远端×新建/恢复4/4，原自动`/quit`、历史/会话归属、owned/shared退出正确；离线0 TCP/HTTP且可读历史和草稿。后来真实Ctrl+C红由`ctrlc-final-r2/manifest.json`修后消除，并补原12/12受影响回归；真实信号后原25秒内完整收尾，未关闭stdin助跑、未改变共享Serve归属。原r1红保留，A20恢复通过，该独立A20事实与已补齐的A17—19共同满足NET-05，不将其中任一局部通过单独充当父卡关闭依据。

此外，A23 的最终 `shared-final-r10` 已取得五客户端真实同场回执：C#、Electron、Android、iOS、鸿蒙均完成原业务；C#失连不重发、容量拒绝、撤权、移动端三次真实文件写入及Serve宿主哨兵不变有原证据。此前MuMu系统runner缺失及各轮原红继续保留；最终使用原标准Android模拟器等已登记环境，未用单端通过替代同场。见 `A23/shared-final-r10/shared-manifest.json`；该项不替代A17—19原完整GUI/媒体条件。

**对抗已通过23/24（95.8%），仅剩A24；相对本批开始前已收编4项净增19项，本次接续从20/24新增关闭3项（A17、A18、A19）。** A01—A23均按原条件通过；固定六张工程卡与24项验收分母不变。

A24已有2958双包四类消费、旧API兼容、三NuGet示例和原Console/两GUI完整业务证据，尚不能标为完整交付：截至本次功能收编点13d1520，NET仓尚无remote，也尚无 `.github/workflows` 配置；后续工作流与发行机制接线结果见下一节。主线推送与实际CI、签名及NuGet正式渠道仍须逐项完成或按用户决定登记延期。未签名、未上架、未部署；不能把本地pack或功能验收写成正式发布。最终候选/文档与归档收编由根登记最终main SHA。上述发行接线不追加为已过NET-01—05的门，A24/NET-06仍独立保留未关闭。

本节按原卡完整条件记录，不以代码编写比例代替闭环进度；不增小卡，不把内部UI/生命周期缺口归为外部发行延期。正式跨仓六份单据仍由指定单写者同步。新产品或示例修正完成后只重验受影响入口，并更新这些当前事实；旧红和历史回执不删除。

## NET-06 发行机制接续（2026-09-27）

本节接续本地功能收编点 `13d1520e6095cd029b8edf0073880dce6b904ffb`，归档为 `archive/20260927-NET-full-delivery/release-preparation-r1`。只继续原 NET-06／A24，不增卡；NET-01—05、A01—23和原16组的功能结论保留。本次不改 SDK 的 C# 运行逻辑、协议及 Electron 集成模式。

- 增加主线专用 `.github/workflows/ci.yml` 和同一个本地 `scripts/test-ci.ps1`。仅 main push／main 手动调度运行，使用只读权限和固定官方 Action 提交，不自动检出其它私库，不托管密钥，不签名或发布。runner 记录实际命令、源码/锁文件摘要、退出码、TRX与产物；缺少私有源码的原跨语言／Serve／多端门逐项保留未执行，不把独立Windows CI标为24项完整重验。
- 两个产品包增加原 `LICENSE`／`NOTICE`。`audit-packages.ps1 -RequireNotices` 核对原文存在及与候选源的字节一致性，并原子创建回执；默认仍可审旧包用于原兼容与回滚。旧r4包的宽松审计通过、严格新候选条件准确拒绝缺失原文，见 `notice-negative-receipt.json`。
- `scripts/test-release.ps1` 对两个显式包路径、版本、可选批准SHA及签名者SHA256指纹进行真实内容／NuGet验证，保持原文件并锁定检查副本。未签名可作为本地候选，明确 `signaturesVerified=false`／`releaseReady=false`；`-RequireSigned` 拒绝未签名，损坏签名在任意模式均失败。已在原r4双包上实测未签名分类、强制签名拒绝、ZIP内容篡改摘要拒绝、无效签名拒绝；没有创建证书或签名，详见 `cases.json`。
- 新统一入口已在冻结候选 `6414db9134420cac5a4c643442a3fc2d9d7b72cb` 实际运行。原 `local-ci-r1` 合同、681入口/16组映射、检查器10项、MCP消费构建、锁定restore、全Release构建及Core914/914通过；Windows锁定期间407/443通过、36失败，原整轮结果保持failed。用户解除锁定后，同一编译物、同一过滤与原超时/断言复验443/443通过、0跳过，见 `windows-after-unlock-r1`，未修改产品或测试逻辑。仅恢复原未执行8步，见 `completion-after-unlock-r1/manifest.json`；全部通过、源码逐文件摘要前后相同，结论是同候选分段接续完成，不伪称原整轮绿。独立Windows入口未执行的原9个外部实例及其它跨端门仍在scope中具名登记，既有功能实证不重写为本轮新执行。
- 本次8步包含独立sandbox金样（1用例/32原向量）、格式、双包、严格NOTICE审计、四类实际消费者、三示例包构建和旧公开API兼容。net48 WinForms、现代Console、WPF self-contained及Core Native AOT实际退出0；原安装/升级/回滚/卸载链通过，记录本机实际身份，不冒充新增干净标准用户验收。三个示例仅记 `built-not-executed`，不重复声称完整GUI业务。对原2958双包比较四TFM共6564个原公共/受保护成员实例无差异，旧source→旧DLL、旧source→新DLL及旧binary→新DLL三见证均通过；成员数含TFM重复，不是行为测试数量。
- 本次两个未签名候选仍为 `0.1.0-preview.1`：Core **561135字节**、SHA256 `ce79c0acf00c27785fcad8fdc20ffa72a0c53f0a0777e9748a7f2d2262f26a68`；Windows **332396字节**、SHA256 `b9211a4cd11c87028900bbd10bd65a56a25ee65e6caf0ae8067df713b4844119`。准确目录为 `completion-after-unlock-r1/packages`；仅供本地候选，未覆盖原2958双包。`release-candidate-check-r1/manifest.json` 用这两个摘要与 `-RequireNotices` 实际复核通过，真实verify均为未签名NU3004，`readiness=unsigned`、`signaturesVerified=false`、`releaseReady=false`、`published=false`。当前源码与双包身份、回填及最终main收编点分别登记于 `release-preparation-closure.json`，不将文档提交SHA当作原包构建源。
- Serve 新增独立公开包介质互开证据位于 `archive/20260927-SRV-independent-closure/journal-interop/receipt.json`，SHA256 `de4971befd8c17ca7b6456b1c179126041767c739f47c0bd2d26d527b4ea78ef`。原r4 NuGet与实际API-client包在同一Windows物理SQLite上完成5场景、23次消费进程，原pending/unknown、格式与复制身份拒绝通过，无NET产品缺陷。正向范围是无 `workspaceBinding` 公共子集；TS可选带绑定库被NET明确拒绝，不能称所有格式都可互开。本轮只核对并引用该回执，未重复运行。

原完整进度仍为工程 **5/6（83.3%），剩1；对抗23/24（95.8%），剩1；本次新增完整关闭0**。NET-06／A24待实际主线远端CI、批准签名及正式渠道；仓库/工作流机制与外部账号条件分别登记。当前未配置remote、未推送、未发布；本机CurrentUser/My未发现有效且含私钥的代码签名证书，不推断其它外部签名环境不存在。正式计划/对抗单仍由Serve任务单写同步。

## 20260927-NET-code-audit：代码缺口修复与收编

按原六卡、24项验收和16组能力重新检查协议、运行/执行、媒体、存储、扩展、示例与交付脚本。本次从主线 `da7f3c2` 开始，产品冻结于 `759f123ad57b22d733f67929d7036c3a6cd08d8f`；随后仅回填本文并快进本地main，最终提交由 `J:/tansr/archive/20260927-NET-code-audit/closure.json` 登记。全部证据位于同一归档，旧失败保留。下列分组属于原卡补漏，不新增卡、不重计完成率。

| 确认的代码缺口 | 修复与原边界 |
|---|---|
| SSE回调中切换主体后，已缓冲的下一事件仍可能交付；取消/空闲关闭返回正常EOF或字节时仍可能被当成功 | 每帧和回调后核主体，JSON及流读取后再核取消；普通/档案事件、终端输出和执行器观察共享严格读取。合法同主体续票继续生效，不重发副作用 |
| .NET Framework持续流已有部分字节仍卡在异步读取；多个长连接占满默认连接池，控制请求无法到达服务端 | 仅已知默认HTTP传输的CLR4 BCL响应流采用工作线程中的块读取；默认客户端把持续流与控制连接隔离，事件池上限32，不改全局ServicePoint。注入的HttpClient始终保留异步读取及原所有权；未知自定义流不猜测兼容策略 |
| MCP旧框架JSON体取消不能及时结束；反向请求等待连接；非法JSON类型漏出底层异常 | 关闭所持响应流，HTTP连接上限按原pending容量预留反向请求，保持原pending限制；无效类型归为稳定协议错误，不回放请求 |
| 快照密钥提供者回调可在最后检查后切主体或取消；技能大小写别名绕过撤权 | 快照提交/释放正文前再次核可信身份及取消；技能以注册描述符的规范名称做授权，原存储格式和权限决策不变 |
| 媒体下载取消返回部分成功、IO错误失去稳定分类；合法AAC/Opus历史被本地解码能力误拒 | 下载生命周期显式处理取消与IO，协议接纳和本地预览分开，未知结果不冒称可播放 |
| WinForms排队标题更新可能写入旧主体内容，自动保存脱离观察时未持命令门；本地冲突哈希整文件加载 | UI执行回调内重新核身份/取消，自动保存复用原RunAsync门；哈希使用64KiB块、原32MiB上限和相同摘要/CAS格式 |
| CI回执只核TRX Counters，可把缺少真实结果的文件计为通过 | 同时核实际UnitTestResult、数量、状态和一致性；14项轻量回归。真实CLR4 MCP与公开会话探针已纳入原CI入口 |

旧框架同步块读取会占用一个工作线程直至本次读取结束；既有取消/流释放负责退出，默认事件连接池限制为32。它不是协议会话上限，也未改变现代.NET或自定义传输的异步模型。中英文quickstart已说明默认池与注入客户端的容量责任。此批不新增公开API、wire版本或存储格式，不改变Serve/kernel职责及Electron集成形态。

### 从原失败到最终候选

- `baseline-regressions-r1` 使用da7原产品配本次回归，Core72项18失败、Windows51项8失败；原CLR4探针2项失败（JSON取消、反向连接），关闭SSE原已通过；原WinForms真实窗口4项2失败。辅助编译夹具早期错误另存，不计为产品缺陷。
- 首个候选94c9的Core942/Windows451通过，但实际CLR4 MCP反向调用失败；强制头与ping合并写出的复现证实部分帧读取阻塞。6858初修仍失败，随后定位真实BCL包装流，并通过公开会话探针复现长SSE占满控制连接。终端idle旧二进制4/4红例也保留。
- ee945候选原21步检查全部通过，但继续检查发现注入异步流被错误切到同步路径，真实CLR4红例见 `injected-framework-red-r1`；759按传输来源限定兼容逻辑，实际注入流 `asyncReads=2 / syncReads=0`，通过后才封最终包。
- 最终 `candidate-ci-r3` 的Core **946/946**；Windows首次 **450/451**，一条未改动的进程回收用例报 `sandbox_execution_unconfirmed`。保留原失败，原编译物/条件的所属类14/14、原完整独立池 `windows-final-r1` **451/451** 均通过，未修改断言或超时。首次失败根因未被确定，不改写为代码修复或删除记录。
- `completion-r1/manifest.json` 在同一759源码清单不变的条件下续跑原未启动的 **10项全部通过**。这是原失败后的受影响复验与续跑，不是一次连续全绿CI。独立只读复核核对了原TRX、filter、源码清单与包摘要。

| 最终验证 | 实际结果与范围 |
|---|---|
| 本地基本门 | 锁定恢复、Release编译零警告/错误，Core946/946、Windows独立池451/451、格式通过；原5个需额外环境的方法共9实例仍排除，未冒称整仓460实例或重跑全部跨仓/设备验收 |
| 真实CLR4 | MCP3/3（关闭SSE、取消JSON、反向连接）；3条公开会话持续SSE与独立控制请求并行、取消清理，以及注入async-only流通过。探针参考程序集编译警告CS1685/CS1702保留，运行退出0，不与产品Release零警告混称 |
| Sandbox / CI回执 | 原32个金样由1个集成断言通过；TRX回执14/14独立轻量回归通过 |
| 原生WinForms补漏 | `candidate-winforms-r1/result.json` 实际HWND/BeginInvoke/Timer路径4/4、网络0、窗口已释放；来源94c9，后续仅传输修改，非759新包完整GUI重跑 |
| 最终包与独立消费 | 两包内容/依赖notice检查通过；net48真实CLR4、现代Windows、WPF self-contained、Core NativeAOT实际运行；无外置Node；安装/升级/回滚/卸载通过。本次宿主是管理员，不计新的干净标准用户证据 |
| 示例与旧API | 三个示例实际引用新NuGet包构建通过，状态为built-not-executed；四TFM **6564** 个原成员兼容，旧源码配旧/新SDK、旧二进制配新SDK均通过 |

最终两包均为未发布 `0.1.0-preview.1`，构建源759，目录 `completion-r1/packages`：Core **564153字节**，SHA256 `d80a3003ffb312ea52936a09c7da1339c6dbf3d6fa71ba18227a6da1f5ddad80`；Windows **333185字节**，SHA256 `2fba121c9fa95ccbd419f51b227a8870ad3cdb489f341844171d2ad8ba942e92`。`release-check-r2/manifest.json` 按这两个预期摘要和必需notice检查通过；真实NuGet verify仍为未签名NU3004，`readiness=unsigned`、`signaturesVerified=false`、`releaseReady=false`、`published=false`。此前ee945包及release-check-r1只是历史候选，不作为最终产物。

本轮确认的代码缺口均已修复并通过受影响验收，不把这一结论外推为绝无缺陷或重新通过全部外部环境。原工程 **5/6完成，剩1，83.3%**；原对抗验收 **23/24通过，剩1，95.8%**；能力对照 **16/16**；本轮新增完整关闭 **0**。NET-06/A24只保留批准remote后的主线推送/实际GitHub CI、批准签名与正式NuGet渠道。代码、示例、测试和本文统一收编本地主干；正式三单由Serve任务单写同步，不留工作树独有产品提交。

## 2026-09-28 NET-06：0.1.0.2 公开源码与 NuGet 发布（历史记录，结算更正见末节）

用户明确指定双包版本 `0.1.0.2`、公开MIT仓库 `tansrai/tansr-net` 和NuGet账号 `Tansr`。原 `0.1.0-preview.1` 网页上传已在提交前取消，未发布旧版。开发分支只作本地隔离，全部发布相关提交按本地检查→快进main→只推main的顺序收编。

### 本地与主线检查

- `13bdf29` 完成版本、包README、源码地址和旧版本参数接线。本地Core946/946；Windows原443/451（含清理失败）、首次复验449/451保留。用户确认360中断后，同编译物、filter、断言、超时重测451/451、0跳过、22秒；原未执行10门在同源校验下续跑通过，不称本地一次连续全绿。
- 两包严格NOTICE检查、net48/现代Windows/WPF自包含/Core AOT四类实际消费、原旧包升级/回滚、三个示例构建和四TFM旧公开API兼容通过。包源码为13bdf29；示例构建不冒充本轮完整GUI复验。
- 最初远端workflow的job级 `runner.temp` 表达式非法，在13bdf29改为首个PowerShell步骤写GITHUB_ENV。随后run36391474072因SDK10.0.303与锁定ILLink10.0.9冲突而失败；83e1b8a将global.json的rollForward固定disable，保留SDK10.0.301与locked restore，本地恢复/Release编译0警告错误。
- run36391932067的SDK、恢复和编译通过，Core946/946、Windows450/451。唯一失败是测试子进程创建writer-ready时尚未关闭写句柄，父进程凭File.Exists过早读取。13db8d5仅将该夹具改为同目录临时文件写完关闭后File.Move公布；原SQLite hook、phase、忙库/杀进程/恢复/ACK断言和全部超时不变。所属类2/2、关键场景连续3次、受影响格式检查通过。
- 最终[主线CI 36392524187](https://github.com/tansrai/tansr-net/actions/runs/36392524187)在main@13db8d54601c17a6625114504a590e4cadf28ccd通过。最终远端Core946/946、Windows451/451、sandbox黄金1/1均0失败0跳过。外部原Node互通、完整GUI/五客户端/普通用户、作者签名等仍按原独立证据与范围记录，不扩大本轮结论。

### 固定包与渠道

已验本地包位于 `version-0.1.0.2-completion/packages`：

| 包 | 字节 | SHA256 |
|---|---:|---|
| Tansr.Sdk.0.1.0.2.nupkg | 561800 | 5c5ed557e574c9691c4736d7b6aff4b75f25bb9df5d53a2f9bfaf218e10643ea |
| Tansr.Sdk.Windows.0.1.0.2.nupkg | 330803 | 6199c4976b6c734e68568401bea410a9ba9b8b718f15d8d5141f4cf445c67eb7 |

两包已正式发布：[Tansr.Sdk 0.1.0.2](https://www.nuget.org/packages/Tansr.Sdk/0.1.0.2)、[Tansr.Sdk.Windows 0.1.0.2](https://www.nuget.org/packages/Tansr.Sdk.Windows/0.1.0.2)。2026-09-28 07:48 UTC，两个官方V3索引均返回200且列出0.1.0.2。先前仅提交/待索引回执保留在 `nuget-0.1.0.2-submitted.json`，不改写历史。

公开CDN下载的Core为574884字节，SHA256 `efda6a1df73c5012e83d2df0bfe49181e9db6e0449d7b520394557ddf69f61e9`；Windows为343886字节，SHA256 `4ca914b29ebb9d40f7be511498d743abb172383597984ab134b5c0c97a3e59a3`。ZIP逐条内容比对没有丢失/变更，仅新增 `.signature.p7s`；不是把签名后ZIP摘要误要求为原未签名ZIP摘要。`test-release.ps1 -RequireNotices -RequireSigned` 对官方公开下载包通过，签名类型为Repository、服务索引为api.nuget.org，签名者为NuGet.org Repository by Microsoft。未钉选作者指纹，不能把仓库签名写成Tansr作者签名。

`nuget-public-consumers/manifest.json` 的net48真实CLR4、现代Windows控制台、WPF自包含三类实际消费退出0；均无外置Node，资源清理确认。这里使用从官方CDN取回并验证的包作为独立本地源，不冒称这一脚本直接向NuGet执行Tansr依赖恢复；公开V3可用性另有HTTP200回执。本轮未重复AOT/完整GUI/普通用户验收，引用前述已验本地候选及原独立证据。作者签名证书和指纹授权仍未具备，原NET-06/A24按该剩余条件保留。

所有本轮回执统一位于 `J:/tansr/archive/20260928-NET-06-nuget-publication/`。`approved-release-0.1.0.2.json`记录包源码13bdf29与主线CI源码13db8d5的差量；只有global.json和测试夹具，产品源、锁及包元数据一致。历史失败、预览候选和旧SHA均保留。原工程5/6、剩1、83.3%；原对抗23/24、剩1、95.8%；本轮新增完整关闭0。

## 2026-09-28 NET-06/A24：按原签名与发布条件更正结算（当前）

上节“作者签名仍是原卡剩余条件”的判断有误。初始方案、计划及验收单的CLI提交为 `f600d3ffd9611ce8f50fe5047185c13db74c51f9`：NET-06要求“包名可用性、所有权、签名与渠道按实际条件处理”，A24要求“包签名/发布/安装事实逐项对应”，没有要求必须额外取得作者签名证书。把这一条件扩大成作者签名阻塞项，始于本仓上轮文档提交 `c5a14b7`，并非用户新增要求。本轮按原DoD纠正，不新增任务卡或改写旧失败。

两个 `0.1.0.2` 包的公开V3索引、下载、内容一致性、NuGet仓库签名和独立消费已有上节具名证据。原 `test-release.ps1 -RequireNotices -RequireSigned` 已通过；实际是Repository签名，未签作者证书、未钉选作者指纹，不能称作Tansr作者签名。额外作者签名属于可选后续增强，不是NET-06/A24的未完成条件。检查脚本只检查包、不实施发布，其 `releaseReady=false` / `published=false` 是脚本的保守范围标记；实际公开发行由单独的NuGet渠道回执证明。

[发布候选主线CI 36392524187](https://github.com/tansrai/tansr-net/actions/runs/36392524187)对应 `13db8d54601c17a6625114504a590e4cadf28ccd`，原20项独立门通过，Core946/946、Windows451/451、sandbox黄金1/1均无失败和跳过。[后续文档主线CI 36393914962](https://github.com/tansrai/tansr-net/actions/runs/36393914962)对应 `c5a14b7de345f71da11f9a5adfc08e4521896d70`，也已completed/success。原跨仓、五客户端、完整GUI、普通用户、AOT和旧包兼容沿各自既有证据，不冒称本次全部重跑。

原工程 **6/6完成，剩0，100%，本轮新增1（NET-06）**；原对抗验收 **24/24通过，剩0，100%，本轮新增1（A24）**；P01—16保持16/16。NET与原V2分开结算：原V2仍为 **17/20、剩3、85%，本轮新增0**，不因NET发行改变其原20卡/118断言范围。本次只改文档状态，没有修改产品、包、锁文件或测试；不为此重复产品全量验收。

更正回执为 `J:/tansr/archive/20260928-NET-06-nuget-publication/closure-assessment-correction.json`，列出原DoD、核验依据与更正后的计数。原 `publication-0.1.0.2-closure.json` 中的5/6、23/24及文档CI仍在运行保留为历史记录，由该更正回执及本节覆盖当前结算。CLI主线的方案、执行清单和验收单同步回填。

## 2026-10-01 UAPI-01 U3-NET：C# SDK 收敛到 `/api` 统一入口

本批把 SDK 的全部请求路径从 `/v2/...`、`/v3/sdk2/...`、`/v3/terminal/...` 字面迁到由 `contract/api-manifest.json` 生成的 `Tansr.Sdk.Api.ApiRoutes`（80 条操作、9 个域、11 个合同族），并接入统一响应头、统一错误信封与 D18 事件包络。不改业务逻辑：路径字面只换成常量引用，错误解码只增 `unified-v1` 信封分支，各族原解码器与原帧字节保持不变。

### 合同来源与锁

事实源按任务钉在 `lane/uapi/UAPI-01-facade` 的 `94116d5492f88a74a3132af8595f8752597c2995`（manifest revision 4）。实现期间集成树推进到 `46984bf2c73a4c249a18c3e3991aef91ad582eb8`（revision 6）：80 条操作逐字节相同，差异仅在 `families[].clients[]`（新增五客户端锁登记，csharp 条目 `{repo: tansr-net, file: contract/manifest.json, lock: "manifest-sha256"}`）与 `unified-v1` schema/golden 的事件包络定义收敛到 D18 七键。本批按已提交的 `46984bf2` 重新 vendoring（`git show HEAD:path` 取原字节，不取脏工作树），`contract/manifest.json` 的 `apiManifest` 块记录 `revision 6 / schemaHash 19cafed0… / sourceRevision 46984bf2…`，`files[]` 新增四项。偏离钉定 HEAD 一事在报告中单列，由主线定夺。tansr-cli main（`d1818d8f`）此时仍是 revision 3。

### 实现

- `src/Tansr.Sdk/Api/ApiRoutes.generated.cs`：`node scripts/generate-api-routes.mjs` 生成；`ApiOperation` 携 `Name/Method/Path/Domain/Family/Query/Closure`，`ApiRoutes.Path(...)` 填占位符，`Query(...)` 只接受 manifest 白名单参数，`Match(method, path)` 反查操作；`Families.*` 内嵌各族 SHA，`ManifestRevision/ManifestSchemaHash/ManifestSha256` 内嵌锁指纹。生成器 `--check` 逐字节比对，`scripts/check-contract.ps1` 一并核 revision 与 schemaHash。`src/**/*.cs` 与 `examples/**/*.cs` 旧前缀字面残留 0（vendored 参考材料 `src/Tansr.Sdk/*/Contract/` 与 `tests/Tansr.Sdk.Tests/Transport/test-framework-session.ps1` 原样保留）。
- `UnifiedHeaders`：每条 `/api` 响应必须带 `tansr-contract: unified-v1`、`tansr-manifest-revision`、`tansr-domain`、`tansr-schema-hash`，缺失/异族/未知 `tansr-*` 头/非法 `retry-after` 一律 `ContractUnavailableException`，不回退（D10）。请求侧按会话族发 `tansr-session-family`，缓存 `tansr-closure-id` 并在 412 `precondition_failed` 时按 `rediscover` 清空；`AdditionalRequestHeaders` 拒绝调用方伪造 `tansr-*`。
- `UnifiedErrorEnvelope`：判别式仅 `contract == "unified-v1"`；其它 `contract`（`terminal-services-v1`、`sdk2-ext-v1`、`archive-sync-v1`…）原样交各族解码器。信封键集、19 个 `code`、6 个 `retryAction`、`HttpStatus` 枚举、code→status 表、code 绑定 retry（`result_unknown→query-status|rebind`、`precondition_failed→rediscover`、`not_canonical→none`）、`detail` 词表及 facade 自有规则（`requestId == null` + 8 码子集 + 无 `domainStatus`）全部校验；通过则抛 `UnifiedApiException{Code,StatusCode,RetryAction,RetryAfterMs,TraceId,RequestId,Detail,DomainCode,DomainStatus,DomainRetryAction}`，不通过抛 `ContractUnavailableException("invalid_error_body")`。
- `UnifiedEventEnvelope`：`TansrClientOptions.NegotiateEventEnvelope` 显式开启后发 `tansr-event-envelope: unified-v1`，服务端未回响抛 `EnvelopeNotNegotiatedException`；回响后每帧必须是七键 `{contract,eventId,domain,type,cursorSet,terminalStatus,raw}`（`type` 正则、`archiveCoverage` 严格三键、`eventId ≤ 512`），`raw` 解包后交原族解码；缺省关闭时帧字节不变。

### 指纹与向量

- `sdk2-ext-v1` 锁与 manifest 族 SHA 一致；`consumer-conformance.mjs`（集成树运行，`--workspace-root` 指向把 `tansr-net` 映射到本开发树的 junction 目录）对 tansr-net：`sdk2-ext-v1` match、`terminal-services-v1` match、`unified-v1`（csharp）`source`——因 r6 manifest 的 csharp `lock` 仍是占位 `"manifest-sha256"`。用补丁副本验证：csharp `lock` 设为 `unified-v1` 族 SHA `a2449bc121b5183e50b0c2432d29042446659ec8b32b355af7f204c6cb684a8e` 后 summary match=4。此为给 tansr-cli 的 `clients[]` 修改建议，本批不改 tansr-cli。
- `canonical-cross-vectors.json` 127 条：严格控制入口 `WireJson.DecodeControl` 只接受字节规范输入（51 accept 全中；非规范输入全部拒绝）；规范化入口 `Parse`+`EncodeControl` 登记 3 条分歧 `num-exp-lower`、`num-exp-upper-plus`、`num-decimal-1.0`（业务读取器按合同保留小数/指数字面，不在本批改动范围）。
- `unified-v1.golden.json` 回放：登记 3 条分歧——`FacadeError/facade-error-request-id-not-null`、`FacadeError/facade-error-code-not-facade`（是合法的 `UnifiedError`，解码为 `FacadeOwned=false` 而非拒绝）、`ResponseHeaders/response-headers-revision-number`（HTTP 头无数值类型，不可表示）。`RegisteredDivergencesAreTheOnlyNegativeVectorsTheDecoderAccepts` 锁住名单。

### 验证

`dotnet build Tansr.Sdk.slnx -c Release` 0 警告 0 错误；`dotnet test tests/Tansr.Sdk.Tests` 1329/1329（新增 `Api/ApiRoutesTests`、`Api/UnifiedGoldenTests`、`Api/UnifiedTransportTests`、`Protocol/CanonicalCrossVectorTests`）；Windows 测试只跑改动涉及的类 75/75（`NativeMcpBridgeTests` 5 例因缺 `TANSR_TEST_MCP_EXE` 失败，属既有环境前置，与本批无关）；`node scripts/generate-api-routes.mjs --check` ok；`scripts/check-contract.ps1` 通过（9 文件、api-manifest revision 6）。回执在 `J:/tansr/archive/UAPI-01-net-U3/`。真实 Serve 集成测试（`tests/Tansr.Sdk.IntegrationTests`）与 `scripts/check-session-compatibility.mjs` 未运行：前者须 UAPI-01 facade Serve，后者钉定的 Node sdk2 客户端仍走旧入口；`session-compatibility.json` 为 C# 新增 `apiRequests`，`requests` 保留给 Node。

### 待主线处理

偏离钉定 HEAD 到 r6 的认可；tansr-cli `families[unified-v1].clients[csharp].lock` 改为族 SHA；r4 时期 Serve 曾发 6 键事件包络（无 `eventId`），须在 r6 Serve 上复验 `NegotiateEventEnvelope`；archive-sync 族错误体仍走原解码器；tansr-cli main 收编到 revision ≥ 6 后再核一次 `check-contract.ps1 -SourceRoot`。

### 2026-10-01 复验修正（V-NET）

对上述四个提交逐文件复验后落三处修正，详见 `doc/report/UAPI-01-统一API接入复验-2026-10-01.md`：

- `ApiRoutes.DomainSchemaHash("discovery")` 原为 `unified-v1` 族文件 SHA，与门面 `schemaHashOf`、手册 §16.4 不符——discovery 域响应头携带 manifest 聚合 `schemaHash`（r6 = `19cafed0…`），其余域为主族源 SHA。生成器与 `UnifiedGoldenTests` 一并修正。
- `examples/Shared/ExampleSessionControls.IsDefiniteRejection` 的 configuration 分支仍对照外层 `Code/StatusCode/RetryAction` 匹配原族码，在统一信封下永不命中；改为 `DomainCode/DomainStatus/DomainRetryAction`。
- `NativeMcpBridgeTests` 假 Serve 迁到 `/api` 后未加盖四个必带响应头，SDK 按 D10 抛 `contract_unavailable`。上文"5 例因缺 `TANSR_TEST_MCP_EXE` 失败"的归因不成立：按 CI 方式发布 ConsoleAssistant 后，3 例失败原因是缺头；补 `UnifiedStamp` 后 Windows 测试（`test-ci.ps1` 同款排除过滤）451/451。

复验门禁：`dotnet build` Release 0 警告 0 错误；核心测试 1330/1330；Windows 451/451；`IntegrationTests` 仅 `sandbox-golden` 1/1（其余须真实 UAPI-01 Serve）；`dotnet format --verify-no-changes` 通过；生成器 `--check`、`check-contract.ps1` 通过。

## 2026-10-03 UAPI-01 U8-NET：合同 revision 7 对齐、D19 统一码主位异常模型、金样全量归账

事实源改钉 tansr-cli `main` `64df76b24bfd3bc62850f4b04093553708b7708c`（manifest revision 7，schemaHash `b60e77ff…bb57`）；四件套逐字节 vendoring，`contract/manifest.json` 更新 `apiManifest` 块与四项 `files[]`（SHA 见 [报告](report/UAPI-01-统一合同r7对齐与D19异常模型-2026-10-03.md)）。

- 生成器校验并产出 revision 7 事实（操作级 `etagPath` / `expectedRevision{path,kind}`、族级 `requestIdPath`），`ApiRoutes.generated.cs` 81 操作（`ApprovalCredentialSubmit`、`:ticketId`）。
- D19：`UnifiedApiException` 公开面为统一码 + `RetryAction`，族码降为 `Detail.DomainCode`；`Domain*` 作桥接读；`RequiresRediscovery` / `RequiresRefresh` 按 `retryAction` 判定；新增 `UnifiedErrorCode` / `UnifiedRetryAction` / `UnifiedErrorReason` / `UnifiedErrorDetail` / `UnifiedRetry`。迁移表见 `CHANGELOG.md` 与 README。
- 三头：`TansrClient.CallAsync(ApiOperation, ApiCallOptions)` 通用入口，`Idempotency-Key` / `If-Match` / `deadline` 按操作事实本地校验后逐字发出；`UnifiedResponseMeta.ETag` 仅强形；`UnifiedRetry.RetrySameRequestAsync` 同键同体单次重放、deadline 不延。
- 发现：`GetCapabilitiesAsync` / `GetCapabilityClosureAsync` 与 `UnifiedCapabilities` / `UnifiedCapabilityClosure` 严格解码（`closureId` 复推）；围栏外 `capability_unavailable` 原码上浮，零降级。
- 金样 165 向量逐条归账：重放 141（新接 CapabilityClosure 18、Capabilities 16、`manifest-repo-artifact` 对照生成表）、生成器校验 12、明示不消费 12（收编前复验更正，原登记「消费 140 / 生成器校验对象 23 / 明示不消费 1」不成立，见下）；`closure-partial-mixed` 驱动 73 条非流式围栏操作能力交集重放。

验收门读数（原泳道自报）：`dotnet build` Release 0 警告 0 错误；核心测试 1416/1416；`dotnet format --verify-no-changes`、生成器 `--check`、`check-contract.ps1` 通过。cli 侧 `consumer-conformance.mjs --strict`：`tansr-net` 行 match 2 / source 7 / stale 0 / missing 0；全局退出码 1 仅由 android / ios 固定目录的 3 项 `missing` 造成。Windows 测试池本轮未运行。本仓无 serve-demo 副本，Demo 尾项不适用。

### 主线收编前复验（2026-10-03，Fable 5.1 Max 独立泳道）

原泳道四笔提交逐笔复验：`acdd3e5` 采纳；`662603b` / `668aa91` / `cebde98` 修正后采纳，修正各自独立成笔、不改写历史：`6a83f00`（幂等键体内位改按族 `requestIdPath`、`tansr-closure-id` 仅会话内写操作可携、去同义反复断言）、`a37143b`（`Domain*` 桥接标 `[Obsolete]` 改非虚只读，内部族客户端走 internal `Family*`，示例改 `FamilyFacts`）、`094de57`（生成器词表取 schema 并补 `allOf` 共约束与 `--validate`，`generate-api-routes.test.mjs` 进 CI，A29 分账改 141 / 12 / 12）。独立读数：全解 Release 构建 0 警告 0 错误；`Tansr.Sdk.Tests` 1419/1419；`Tansr.Sdk.Windows.Tests` 446 过 / 7 红 / 4 跳（7 红全为 `TANSR_TEST_MCP_EXE` / `TANSR_TEST_RECOVERY_CLI_ROOT` 工装前置缺席，须 `scripts/test-ci.ps1` 驱动）；`Tansr.Sdk.IntegrationTests` 1 过 / 33 红（全为 `TANSR_SERVE_*` 前置缺席，须 `scripts/serve-integration.mjs`）；`dotnet format --verify-no-changes` 通过；生成器 `--check` ok（81 操作，revision 7）；`check-contract.ps1` verified（9 文件，revision 7）；`consumer-conformance.mjs`（`%TEMP%` 连接点工作区）`tansr-net` 行 match 2 / source 8 / stale 0 / missing 0，全局 match 8 / stale 0 / missing 2 / absent 12 / source 27，`--strict` 退出码 1 仅由 android / ios 两项 `missing` 造成。详见[报告 § 主线收编前复验](report/UAPI-01-统一合同r7对齐与D19异常模型-2026-10-03.md)。
