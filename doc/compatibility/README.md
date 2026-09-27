# 能力与合同核对 / Capability and contract review

`public-api-map.json` 保留原 P01—P16 分组，将 TypeScript SDK 出口、AgentSession 成员、Electron 桥和能力表逐项对应到原生实现、Serve 前置与剩余条件。原 680 项中漏列的 `ChatApp.paths` 已补入，共 **681 项**；分组、工程卡和验收卡数量不变。

2026-09-27 的原入口清点对照 Serve 主线 `e63c4e709d2086f9e7343cb764814ecce130e3df`：11份能力源文件与 `027de7e2` 快照逐字节相同，因此不移动 `WireContract.SourceRevision`、旧数据互通基准和原五份合同锁。实现审阅、产品包和真实联验来源分别记录在 `reviewedAgainst`，不会用较新的 Serve 候选改写旧 Node 数据互通基准。

映射的 `evidence` 引用可定位的行为、源码入口、命令和回执。局部测试通过与完整分组通过分开：`complete` 只在全部原行为完成时为真；`ready-not-run` 不得用于关闭。检查器不会把方法名存在解释为真实行为通过，也不会再硬编码所有证据必须永远为空。

本批681行分别落到39条具体职责路由（`implementationRoute`），包含本地实现/测试锚点和保留在 Serve 的原职责路径。原协议类型、classifier、平台bundle、分段冷层等仍由 Serve/kernel 承担，不要求在C#复制同名内核。`originalResponsibilitySource` 进一步指向原SDK具体实现。39条是文档索引，不是新增任务卡或测试分母。

NET-A01验的是完整、可审计的归属及漏项防护；NET-A02验同一候选包的真实框架消费；NET-A04验版本发现及失败边界。它们不额外承接A17—24的全部界面、性能与发行条件。反过来，映射覆盖681项也不能自动把完整行为标绿。现按原条件和具名实际回执独立关闭 **16/16组（100%）**，681条职责入口均有已核实归属。分组、入口、六张工程卡与24项对抗断言是不同分母；该结论不代表已经完成签名、远端CI或正式上传NuGet。

`native-ui-r23`补齐两端同轮追加、动态正文/思考档位、三档增量叙述、同ID恢复、Task及原SDK2档案ACK/重开/离线只读。该批真实记忆容量红由显式兼容compact介质修复，再由r26/r27实际管理与两个无凭据冷启动记忆按钮补验；最后WinForms媒体余段由r30通过。切用户首轮又发现草稿残留，a247修复后WinForms r3完整链、WPF原前段与同一个B会话的续跑共同闭环。原失败均保留，不把分段补证改写成一次整批全绿。逐组证据见`archive/20260927-NET-full-delivery/parity-group-final-review.md`，原13组依据保留于此前两份group review。

全部原生UI索引为同archive的`native-ui-acceptance-final.md`，最终可复用工装提交`9d980465`；示例源码`a247abe`与SDK包`2958ed9`分别具名，最终主线文档提交由根的外部收口回执记录，避免自引用。

```powershell
node scripts/check-parity.mjs --source-root J:/tansr/tansr-cli
node --test scripts/check-parity.test.mjs
pwsh -File scripts/check-contract.ps1
```

检查会拒绝新出口未映射、删除困难成员的映射、源字节漂移、失效的实现/测试入口，以及使用未验材料关闭行为。成员枚举按锁定源码的声明语法运行，任何源码变化先失败，再审阅抽取器及映射。能力表是研发回填入口，不是完整 Electron 对等的自动证明。

## SDK1 / SDK2 发现与失败

默认 `TansrClient` 继续调用原 `/v2`，无需先访问 SDK2 发现接口。选择 `SessionContract.Sdk2OffloadV1` 时，客户端先验证当前身份和票据的能力，再访问 `/v3/sdk2`。404、401、网络错误、超时、损坏响应和缺能力均不会自动降级、重建会话或改换存储。

`GetSessionCapabilitiesAsync` 是显式刷新：刷新开始即撤销旧的发现缓存，并与自动发现共用锁。若同票下服务已经撤回 SDK2 能力，后续调用必须重新确认，不能沿用旧成功结果。新票据也需重新发现；旧请求晚到不能授权新票据。请求错误保持原协议码；取消/期限使用 .NET 的取消异常。宿主可以显式再次查询，但 SDK 不自动重做有副作用的调用。

`contract/session-compatibility.json` 是 Node 与 C# 共用的受控 HTTP 行为向量。Node 使用原项目的 `Sdk2SessionClient`，C# 使用公开 `TansrClient`，比较请求路由、次数、拒绝边界和不降级行为。原 Node 的 `capability_unconfirmed` 包装与 C# 的具体 HTTP/协议异常分别保留，未将两种错误外形强行改成同名。这些向量不是替代真实 Serve 联验。

```powershell
dotnet test tests/Tansr.Sdk.Tests/Tansr.Sdk.Tests.csproj -c Release --filter FullyQualifiedName~SessionCompatibilityTests
node --import file:///J:/tansr/tansr-cli/node_modules/tsx/dist/loader.mjs scripts/check-session-compatibility.mjs J:/tansr/tansr-cli
```

The map preserves all 16 capability groups and records implementation and actual evidence separately. All 16 groups and their 681 responsibility entries are now independently verified against the original conditions. The real journal-capacity and cross-user draft failures were fixed and retested; their original failures remain recorded. WPF user switching is supported by the original prefix plus a continuation of the same B session, not a falsely reported all-green original run. The wire lock is unchanged. Group closure does not claim production signing, GitHub CI or public NuGet publication.

Every original entry now references a concrete responsibility route and implementation/test anchors. Shared scenarios may support multiple symbols of the same responsibility; these routes are documentation indexes, not new tasks. Retained Serve/kernel responsibilities are explicit. The version and package gates keep their original scope and do not inherit the complete UI or publication gate.

SDK1 remains the default. SDK2 is explicit, authenticated and capability-gated. Failed discovery never changes the selected contract or storage strategy. Explicit refresh invalidates an earlier decision, and a renewed ticket requires fresh discovery. Side effects are not retried automatically. Shared vectors exercise the actual original Node client and the public .NET client with the same HTTP replies; language-specific error types remain explicit. Real Serve integration is a separate acceptance layer.

## 本批证据边界

- 原21项版本混装通过：`archive/20260927-NET-full-delivery/session-compatibility-final.log`；Node同向量与同票刷新反例见同目录`session-compatibility-node-final.log`。
- 原四TFM公共API检查5286个公共/受保护成员实例通过，含原源码→旧DLL、原源码→新DLL、旧binary→新DLL消费；这不是5286项行为验收。当前产品证据为`api-compatibility-final-r2/result.json`及对应`consumer-result.json`；旧r1继续保留原候选身份。
- 当前两包为产品`2958ed9`、`packages-final-r4`，独立net48 CLR4 WinForms、现代控制台、WPF self-contained和核心NativeAOT消费见`consumers-final-r3/manifest.json`。三示例确实从两包的PackageReference构建，`A24/package-examples-r1`及主体隔离修复后的`package-examples-identity-r1`分别保存构建/产物身份，实际运行另有UI与Console回执。WPF不是NativeAOT。原`c8b6dfe`候选在真实新建普通Windows用户下完成安装/升级/回滚/卸载与私有文件/DPAPI隔离，见`A21/standard-c8-r2/results/child-result.json`；保留为原候选证据，不改称新候选重跑或全新操作系统镜像。
- `coverage-final-r6.json`按完整用例名/Theory参数和最近实际结果归并Core914、Windows452，缺证/未决为0。身份原14项只新增11项唯一Core用例，旧3项不重计；旧r3的898/443、r5的903/452保留为历史。该合集不是一次新全池或行覆盖率。A09仍由`benchmark-final-r5/runner.log`原1/1及三组100样本实证覆盖，原红与各池原数保留。
- 原HTTP修复组`http-closure-r4`为17/19；Serve `d0dc9cf`上A08快照/预算/镜像典型和后续可信扩展原1/1已过。配置拒绝的新红经Serve `b988eee`修复，由`native-ui-r13/ui/partial.json`实际证明确定拒绝、原配置不变及原键重放；窗口工具由r11实际标题及批准/拒绝/过期回执补齐。A20真实Ctrl+C由`A20/ctrlc-final-r2/manifest.json`修后通过。
- A23 `shared-final-r10/shared-manifest.json`在Serve `3a0659f`实际运行C#、Electron SDK2、Android、iOS、鸿蒙：三个主体、三个会话、五个客户端，移动三端共享同一获授权会话，不是五主体或5×4故障矩阵。原SDK1公开持久化、令牌、HTTP/SSE与完整Electron SDK/IPC保留另由`SRV-completion/test-full-r1.json`和`test-full-reconciliation.json`互证；原整池15537项、15504通过、5失败、28跳过、exit1及源中途变化保留，受影响叶按原修后回执归并，不能称整池最终全绿。用量是`partial-unpriced`，不冒称真实费用。
- `reviewedAgainst`区分示例候选`a247abe`、产品包`2958ed9`以及Serve来源。最终GUI/Console仍用原冻结Serve `b988eee`/快照`b8f96db8…67f32c`；A08/A12是`d0dc`，A23是`3a0659f`，不合并为同一源码全池。固定main收编、推送与CI由本批相应回执独立记录。
- r23真实容量红保留：254个合法操作的已完成回执仍各占256KiB，耗尽默认64MiB后remember保持pending/unknown。2958新增显式`sdk2-execution-sqlite-compact-v1`，旧v1/default不变、不自动迁移或删锚；pending仍完整预留，完成同事务只释放未用padding，原17项通过。最终包WPF r26/net48 r27完整提取、pin/query/replay、批准remember/forget、排空，各331操作全部终态、0 pending/预留，逻辑字节分别922080/922358，原帽未改。`native-offline-memory-r1/ui-r1/result.json`记录两包实际无凭据、127.0.0.1:1冷启动按钮读取原库，可信authority来自原Serve metadata/spool而非设备自授，删除的主题/事实未复活。r25证据文件rename崩溃及其网络错误单列保留。
- 媒体录音用实际Mobiola输入设备进入原录音器，经合成ASR先写草稿且0消息，不称物理麦克风、虚拟环回或识别准确率。原四媒体工具/图片输入/坏链/关闭能力见r22；WPF分段见r23、net48录音见r24、最终net48分段/保存/unknown取消不重发见r30（uiCode0/fixtureCode0）。所有上游为合成端点，不冒称真实付费供应商质量。
- 切用户首轮真实草稿残留由a247的可信来源/应用/用户分区和原回调绑定修正。`native-user-switch-r3/manifest.json`保留WinForms全链0、WPF原前段exit1与同B ID续跑exit0：B访问A被拒，新B草稿/历史/待批清空，B回复及A/B无票冷恢复分域，fixture0、自有进程0。`console-identity-offline-r1/receipt.json`证明同人续期保持草稿/呈现，B或未知主体不可见，原seed不变。该组合满足P16及原A18最后条件；不把WPF原失败改为整轮0。工程和对抗卡以[开发记录](../development.md)为准；A24实际签名、远端CI、正式渠道仍分列。
