# NETSDK 实施记录

更新日期：2026-09-27。当前为 `20260927-NET-full-delivery` 集中验收批；本地候选包源码 `83f99f8`，开发树后续示例与夹具提交 `e9b4881`。两个产品包已从独立源实际消费，核心884/884，Windows按同名用例去重为434通过、1项性能基线待执行；最新真实HTTP池27/33，余6条正在修复或复验，不能称全绿。完整证据、原卡独立结算条件及尚缺事项见[本批记录](#20260927-net-full-delivery集中实现与验收回填)。

原六张工程卡、24项验收、P01—P16不变。上一已收编状态为工程0/6、验收4/24（A03/A05/A06/A07）；本批新增可独立结算项在末节列示，正式跨仓单据由单写者同步，未将待UI、性能或五端联验写成通过。以下各日期段是历史快照，其旧“未齐”、旧候选、旧计数及红例保留，不代表本批实现仍未开始。尚未正式发行。

## 责任、源码和边界

用户已要求 SDK2.0 会话实施 Serve 补齐、本会话立即实施 C# SDK。已通过 Codex 会话消息完成交接。Serve 是运行内核和新 wire 的单一写者；本仓是协议消费者、设备执行器、存储和原生示例。原 Electron 完整 SDK/IPC 不变。

- 固定目录 `J:/tansr/tansr-net`，主分支 `main`；初始工程提交 `9bcb40c`。
- 开发树 `J:/tansr/worktrees/net-NET-01-sdk`，首批本地分支 `lane/net/NET-01-sdk`，当前分支 `lane/net/NET-06-full-delivery`；固定main已收编到`3ccb4578b6aa6a7f1269ac61ccf617c8f4f9cd85`，本批尚待最终门与收编。不推开发分支。
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

本节替代上面的“当前批”描述，保留所有历史原红和当时数量。归档为 `J:/tansr/archive/20260927-NET-full-delivery`。本次仍只有原NET-01～06、A01～24、P01～16；源码实现、局部行为通过、完整验收关闭和正式发行分别记录。

### 当前来源与实际门

- NET本地main仍为`3ccb4578b6aa6a7f1269ac61ccf617c8f4f9cd85`。本批产品先提交`9dd4c16`，测试`33b4805`；存储/文件修正`105bf9c`，目录保护修正`83f99f8`，随后示例/夹具提交`e9b4881`。正在处理的后续增量须经受影响门后再收编，不能冒称已在main或已发布。
- 已实际消费的双包来自`83f99f88631e19e5ef95c57d073331924810799e`，版本`0.1.0-preview.1`。Core：549531字节，SHA256 `8a5ef981db4dec265992f4301c9255d1d360a275f377c3a1b1fbd69f6317d3cc`；Windows：329799字节，SHA256 `f6384b8ba9bda00646d375f96dd10471288cee471a460238c3f61f97b5f99c9d`。`consumers-final-r1/manifest.json`记录实际内容/依赖/运行来源。后续产品变化不能自动继承这两个包的消费结果。
- 本轮HTTP r4读取Serve提交`1c7039e82e1010e501e53e21ee5e570c75e4d2c3`，3053文件快照SHA256 `965d2bf7fde7b6fc9a336c3ea1c79f01fcfe2e345f45629b391a1902ec4d7d09`，见`serve-source-r4.json`。所有上游模型/媒体响应为合成材料，真实的是Serve/kernel、HTTP/SSE、C# SDK、Windows进程/文件及SQLite链路。

| 本轮门 | 实际结果 | 证据与范围 |
|---|---|---|
| 核心池 | 884/884，0失败/跳过 | `core-r2/core.trx`；不与旧703/627等批次相加，新补产品另需受影响验证 |
| Windows集中池 | 首轮435：433通过、1夹具失败、1性能测试未执行；同产品LocalHost夹具修正14/14后，按完整testName去重为**435唯一项，434通过、1性能待验** | `windows-final-r1/results/windows.trx`、`local-host-final-r1/local-host.trx`；不是433+14。未执行为`WindowsExecutionBenchmarkTests.OriginalElectronAndServeShareSameFlushedCommandAndQpcCollector`，不称性能已过 |
| 协议/版本混装 | C#21/21；原Node共享向量及同票刷新对照通过 | `session-compatibility-final.log`、`session-compatibility-node-final.log`；默认SDK1无新发现请求，显式SDK2失效不降级/重建/换存储 |
| 旧公开API兼容 | 四TFM5286个原公共/受保护成员实例通过；旧source→旧DLL、旧source→新DLL、旧binary→新DLL均通过 | `api-compatibility-r7/result.json`、`consumer-result.json`；成员实例含TFM重复，不是5286行为用例；保留旧源码`null`重载兼容见证 |
| 完整入口归属 | 681符号/入口、16组、11源快照可复核；39职责路由对应具体实现/Serve委托/测试；10个检查器对抗用例通过 | `doc/compatibility/public-api-map.json`与`mapping-attribution-receipt.json`；新增漏项、删除困难成员、伪造入口、未验证据关闭均拒绝。16组完整业务验收仍未标绿 |
| 真实HTTP集中链 | **33项，27通过、6失败**，原失败保留 | `http-r4-retry/runner.log`；不能因核心池或成功子链而称整池通过；具体余项见下表 |
| 双包/实际消费者 | net48 CLR4 WinForms、现代控制台、WPF self-contained、核心NativeAOT均实际运行；NodeOnPath=false | `consumers-final-r1/manifest.json`；前两库四TFM选择正确，WPF不宣称NativeAOT |
| 安装及依赖 | 中文空格路径安装→升级→回滚→卸载通过；两包与34依赖/native/许可已核 | `consumers-final-r1/installation/manifest.json`；保留用户合成数据。当前`elevatedToken=true`、`administratorMembership=true`，标准用户门未满足。3项legacy license URL原样登记，未推断SPDX |

### 已实现与此次发现的实际差额

本批高阶接线已包含：授权主模型目录/别名/能力和本人1d用量；可信Serve模型装饰/采样与原权限/预算调度；完整公开会话、档案、记忆、缓存和资源观察；三端共享原生工作台、MCP/Skills、媒体与录音/分段TTS、Worker、本地受信host-module及冷启动本地介质只读。存在实现不等于已跑完整UI；原Electron完整SDK/IPC继续保持。

| 原卡内发现 | 实际原因与修正 | 当前验收边界 |
|---|---|---|
| fs.inspect不存在文件返回unknown，阻断原Write | 仅将确定的`not_found`映射原`ENOENT`；未知IO/副作用不改成确定失败 | 2项真实Windows缺文件/缺父目录用例通过；HTTP随后继续暴露mkdir回执问题，后者不能被这2项替代 |
| SQLite锁无限等候、FULL自动回滚被误判 | 有界锁期限；按实际事务状态处理SQLite FULL后的自动回滚 | 实际writer提交前/后杀进程、锁冲突与`max_page_count`触发FULL通过；不是宣称将整块系统盘填满 |
| 进程目录保护阻断同盘原子媒体保存 | 祖先目录允许正常写入，仍防替换/删除；可执行文件身份与写保护保持，临时锚使用delete-on-close | `83f99f8`产品修正和同源包实际消费已通过；撤回的3个“重试等待锁”夹具不计入唯一测试数 |
| 新的API重载造成旧null源码歧义 | 内联Skill改命名工厂`FromInline`，旧构造不变；原存储异常构造语义保留 | 旧source/binary兼容实际见证通过；未把IArchiveClient夹具编写中的短暂编译红误登记成产品继承破坏 |
| 原P06增量Narrator被快照Formatter遗漏 | 新`SessionNarrator`消费同一事件，保留三档、原时间、思考只长度与回调异常隔离 | 产品及同源测试入口已补，仍`ready-not-run`，不能据本轮884旧池说新增已验 |
| 原P06运行中切换呈现档位遗漏 | `SessionView.SetDelivery`不重建原视图；旧块档位及Off立即去思考按原语义处理 | 新实现/三端接线与最终受影响回执待结，不把构造时Options当完整动态能力 |

### 按原条件独立结算，不追加其他卡的门

以下是本地独立关卡判定及正式单据回填输入。上一收编的A03/A05/A06/A07维持；新增判定须由正式CLI单据单写者与本批最后候选同步，不能将尚待的产品复验、UI或性能提前填绿。

| 原项 | 本批可独立结算的证据/判定 | 不应追加的条件 |
|---|---|---|
| A01 | 681行已逐职责列具体本地实现/原Serve委托、已验及待验证据；39条只是索引。机械清点和故意漏项等10个检查器对抗通过；真实发现的P06差额没有被隐藏，已补入口待验。**完整映射条件已具备** | 不要求先通过所有P组、所有UI或正式发行；也不把681映射当681项行为测试 |
| A02 | 同一83f99f8双包的net48 CLR4、net10控制台、WPF实际运行、正确资产/无Node消费/无绑定冲突通过 | 干净普通用户属于A21；NuGet正式上架属于A24 |
| A04 | 原21项混装、真实旧SDK1链与独立新SDK2消费，失效/401/超时/坏响应不降级、不改存储；旧source/binary额外互证 | 不把16组全业务或UI强加给版本发现门 |
| A13 | 真实writer杀进程/FULL/锁/损坏/跨域密文/耐久ACK失回与重开；本轮真实Serve加密档案SSE→ACK→供材通过 | 不等WPF/WinForms或正式渠道；不把SQLite FULL夸为系统盘物理耗尽 |
| A14 | 本轮真实核心提取、Windows出版、审批检索、后轮采用、维护与删除，旧writer/旧备份来源均拒绝；原Node publication互通、第三方介质可注入 | 不等全部UI；合成主模型/提取响应与真实协议/存储层分别说明 |
| A16 | 原操作失回/重启、真实Serve恢复跨runtime、两轮C2连续、异主体/撤权/删除旧票拒绝、自然ticket TTL真实两跳410且原键不重发；原API生产验证器到期边界1/1互证 | 不把epoch失效当ticket到期；没有供应商费用事实保持unknown，不以节费账单或UI为本卡新增门 |
| A22 | 同一包的net48、WPF自包含、现代控制台和核心NativeAOT，包内资产/依赖/native/许可事实完整 | 目前只承诺本批win-x64实测；不把x86/arm64或WPF NativeAOT写成已支持 |

存储证据：本域198个唯一Windows用例最终均有通过回执，包含原Node双向明文/密文两参数；不是将多轮重叠数相加。见`storage-affected-r2-results.json`、`storage-node-r3/node-storage.trx`与`storage-handoff.md`。记忆和缓存以本轮`http-r4-retry/runner.log`实际通过的对应方法为准，不沿用旧候选单链代表新候选。自然TTL的原API互证为`api-cache-expiry-r1/receipt.json`，源码`5f82170314c21e19c91677478b9b295246354bf1`；仅注入内存query/clock，不读取.env、不连生产数据库或网络。

**NET-01可以依据A01—04独立结算；NET-04仍只差原A15的最后受影响跨层消费，不等UI。** A15已有第三方HTTP双SQLite8、显式迁移/保源/中断DDL回滚/不支持组合拒绝29、原Node三类互通及原Serve S1/S2/S3组合；当前补在同一个`ServeSessionApiTests`典型内的本地镜像Enable→Flush→Disable→Enable需真实Serve回执，证明同session/原史/序号不变、原export图片保真。已定位的资金/用量Store恢复亦须此受影响链通过。代码已经落下，状态是待实际复验，不是未开发。跨仓S3证据在`archive/20260927-SRV-completion/storage/review.md`和`storage/candidate-final/receipt.json`；不能拿历史绿遮住新候选受影响路径。

### 最小剩余与进度回填入口

当前HTTP 6条红分别是文件工具mkdir后的Write链、资源排空delayed/failed两条、compact/快照/恢复与Store链、可信Skills/MCP/子代理/预算组合、background handoff=true。各自已分配原实现负责人修复或处理夹具，仍等同候选定向回执；不因名称已有就关闭A08/A10/A11/A12。A09原同机Electron时间与资源基线、A17—20真实UI/媒体及两模式、A23同Serve五端并发仍按原入口实跑。A21干净标准用户、A24主线CI/签名/渠道等外部条件显著保留；它们不抹掉已经通过的内部代码证据。

本地独立判定若由正式单据采纳，则新增可关A01/A02/A04/A13/A14/A16/A22共7项，连同原4项为**11/24，剩余13，45.8%**；NET-01为**1/6，剩余5，16.7%**。这是一份有证据的回填建议，最后提交、受影响复验和正式单据结算由本批负责人统一记录；尚无整版24/24或16/16声明。未将数量换算为代码完成比例，也未新增小卡。NET仓仍无remote，不存在GitHub推送/CI、NuGet上架、签名或生产部署事实。
