# electron-chat — Electron 终端应用样板(@tansr/sdk 全能力演示)

## 源码 Demo 的依赖基线与自动更新（REL-06-D）

REL-13-DEMO 当前最低基线为已正式发布的 `@tansr/sdk@0.17.0`，Electron 示例版本为 `0.1.1`。Current baseline: published SDK `0.17.0`; Electron demo version: `0.1.1`.

本示例携带同源的 `demo-dependencies.mjs`。交互启动先检查 npm 官方 `latest` 稳定版，检查成功后退出，再执行原启动命令；默认 24 小时内复用成功检查。单独运行 `npm run demo:update`（仓根可用 `pnpm demo:update`）会立即重新检查相关公开 Tansr 包。没有 Tansr 依赖的 token-server 不升级 express；store-s3 的测试/一致性命令保持冻结，可先显式更新。

源码声明使用 `>=最低版本`，仓内最低版本集中在 `scripts/demo/dependencies.json`，拷出后从本例 package.json 读取；lock 记录实际安装版本，构建、测试和 CI 均沿现有 lock，不自行联网改依赖。首次使用仍先运行包管理器 install 生成锁文件。只点名更新 Tansr 直接依赖，不点名升级 Electron、express 或工具链；传递依赖由原生包管理器解析并记录于 lock，可能同步变化。新版需通过包管理器 engine-strict 与本例类型检查，失败则恢复本次持有的原锁和安装。

设置 `TANSR_DEMO_AUTO_UPDATE=0` 可固定当前满足最低基线的安装以排障；离线仅复用已安装且满足基线的版本，否则明确失败。新稳定版不等于任意未来破坏性修改都语义兼容，正式验收仍应运行本例原测试。已经打包的 Electron 二进制需要重新构建并分发，源码更新器不会修改已发安装物。

The source demo carries the same standalone updater. Interactive startup checks the official npm latest stable release, then exits before running the original application command. Successful checks are cached for 24 hours; `npm run demo:update` forces a fresh check. Version ranges declare a minimum baseline, while the lockfile records the installed versions. Build, test and CI stay frozen. Set `TANSR_DEMO_AUTO_UPDATE=0` to keep the current installation; offline reuse requires an installed version meeting the baseline. Updates must pass engine and type checks; future breaking releases are not guaranteed to remain semantically compatible. Packaged Electron applications require a new build and distribution.


tansr SDK 三方闭环里「终端应用」这一角的官方示例:Electron 主进程内嵌 `@tansr/sdk`(平台令牌档),经 [`examples/token-server`](../token-server/) 换取短期令牌后直连 tansr 平台;renderer 是零框架的聊天 UI,渲染恒经 SDK 的 **SessionView 状态投影**(层1),全程不手拼事件。SDK 的全部能力面都接进了这一个窗口,每项能力都有真实可触发的 UI 入口(见[集成清单](#全能力集成清单ui-入口)),供全面测试与照搬集成。

## 架构(主 / 渲染进程分工)

```
┌─ Electron 主进程(dist/main.mjs;接线全在 src/app.ts startChatApp)─┐
│  fetchDemoToken() ──▶ token-server(appkey 恒在那侧,本进程恒不可见)│
│  createSession({ token, baseUrl, model })──▶ 平台网关(x-tansr-app-token)
│                                                                      │
│  session.events ─▶ 扇出泵 ─┬▶ reduceSessionView(层1)─▶ 'chat:state'│
│                            └▶ createNarrator(层2)──▶ 'chat:narrator'│
│  defineTool ×2 / defineSkill 内联 + skills.dirs 目录 / createMcpHost │
│  tools.builtin 媒体四名按 platform 位列名 + 环2 WebSearch 平台后端   │
│  (kernel 环2 工具 + 平台提供方;平台代调,按张/秒/字符/次计费)     │
│  'chat:state' 投影:completed 工具卡附 parseMediaArtifact 判别结果  │
│  permission.askUser(权限桥)+ promptChannel(提问桥,两条独立缝)  │
│  SessionStore / resume(开关只切换落盘,保留当前会话)              │
└───────────┬───────────┬──────────────────────────────────────────────┘
   IPC 白名单通道(contextBridge,preload.cjs;nodeIntegration 关)
  'chat:state/info/narrator/restored' ▼  ▲ 'chat:send/set-model/set-persistence'
┌─ renderer(纯 UI,零框架)──────────────────────────────────────────┐
│  聊天气泡 / 思考折叠 / 工具卡片(按 artifact.kind 内嵌 <img>/<video>/│
│  <audio>/转写块 + 断链兜底)                                          │
│  「试一试」能力芯片(位关置灰)+ 模型切换器 + 权限/提问双弹框       │
│  能力侧面板:17+5 能力位网格 / 装配集 / 技能 / MCP / 待办 /          │
│  叙述日志 / 用量自查 / 持久化开关                                    │
└──────────────────────────────────────────────────────────────────────┘
```

安全形态(demo 即样板):**令牌与业务逻辑恒在主进程**;renderer `nodeIntegration` 关、`contextIsolation` 开、`sandbox` 开、CSP 只许本地资源;preload 逐通道具名封装,恒不暴露 `ipcRenderer` 本体;模型输出经 `textContent` 落 DOM(恒不 `innerHTML`)。

## 快速开始

### 系统工具目录与用户音频操作

右侧统一展示“系统工具”，按本地与任务、网络、媒体三组浏览。四媒体不是另一套平台工具；实际传入 SDK 的 `tools.builtin` 包含所有选中的本地、网络及媒体项。目录显示本会话是否装配，媒体授权池为空时明确提示“待配置模型”。能力字段保留在默认折叠的“开发者详情”中，`platform.*` 仍是兼容授权合同，不改变工具所属层。快捷入口禁用时给出检查授权/重新连接指引。

“音频转写 / 录音转写”先填入可编辑草稿，不自动发送；“朗读输入”直接生成音频，独立展示，不制造模型工具事件。智能体仍可自主调用 SpeechToText / TextToSpeech，并产生原有真实工具生命周期卡。目录语言选择器同步切换本次目录、提示词侧栏及媒体控制说明的中英文；其余历史示例界面目前仍以中文为主。

### Windows 便携包

先按既有流程安装依赖，再向一个尚不存在的绝对输出目录打包：

```powershell
npm run package:windows -- J:/tansr/archive/your-task/windows-demo
```

打包器重新构建应用，并使用当前已安装的 Electron x64 运行时。应用只收录 `dist/main.mjs` / `preload.cjs`、renderer、assets 和运行 manifest，不复制 `.env`、凭据、源码映射或测试夹具。结果是完整 Windows 目录及 ZIP，附文件清单、源码身份、实际 SDK/Electron 版本与 SHA256；不是 Authenticode 已签安装器。解压后使用方法见 [PORTABLE-README.md](./PORTABLE-README.md)。正式分发仍须对应受验源与真实 registry 依赖；候选清单中 `sourceDirty: true` 不能作为发布完成凭据。

### System tools and portable Windows demo

The sidebar has one system tool catalog with Local and tasks, Network, and Media groups. All selected items enter `tools.builtin`; `platform.*` remains a compatibility authorization namespace in the collapsed Developer details. An assembled media tool with an empty authorized model pool is shown as needing model configuration.

Transcription fills an editable draft without sending it automatically. Read-aloud produces audio separately from agent tool events. The catalog language selector translates the updated catalog, prompt sidebar, and media controls; other existing demo sections remain Chinese. Build a Windows x64 portable ZIP with `npm run package:windows -- <new-absolute-directory>`. Keep its entire runtime directory together and configure your developer token server; credentials are not bundled. See the portable readme and generated provenance manifest before distribution.

### 关闭与重新连接（ARC-07，需配套新SDK）

`src/app.ts` 退出时等待 `closeAsync({ timeoutMs: 30_000, flushStore: true })` 完成，再释放共享 MCP host；超时／失败时保留会话所有权。应用 dispose 合并正在进行的调用，并等待连接中途得到的晚到会话；真实层显式等待，`src/main.ts` 每次最多观察 30 秒，超时提示“再次等待／直接退出”，不会自动把 timeout 当成功。确定失败后可再次调用 dispose。持久化开关只切换同一会话的落盘状态，不会重建会话；下次启动通过 SDK `resume` 恢复已保存记录。

`src/lifecycle.ts` 的观察函数可单独测试。`src/advanced-api.ts` 展示 query 的 `cleanupTimeoutMs` / `onLifecycleError` 与 runAgent 的 settle 回执。仅有 `session.ended` 不能证明存储及外部资源已经关闭。手机端连接远端 serve 时沿用会话协议，断线不是销毁服务端会话。

前置:monorepo 根已 `pnpm install`(Electron 二进制随装)。

**本地一键世界**(最快路径;需同级 `tansr-api` 仓,其 `.env` 携 UCloud 真凭据):

```bash
# 终端 1:一键拉起演示世界并驻留——dev-memory 网关(:8787,聊天真调 kimi,
# 分钱级)+ 假 dashscope(:8790,图像/视频零真计费)+ 控制台全配(能力位
# 全谱开)+ token-server(:8788,appkey 每次全新、经 env 注入不落盘)
cd examples/electron-chat && npm run dev-world

# 终端 2:本应用(构建 + 启动;demo 缺省基址已指线上,连本地世界须显式覆盖)
cd examples/electron-chat
TANSR_API_BASE=http://127.0.0.1:8787 npm start   # PowerShell:$env:TANSR_API_BASE="http://127.0.0.1:8787"; npm start
```

注意 dev-memory 网关是**内存态**:世界终端 Ctrl+C 即整树蒸发,appkey 随之作废;下次重跑 `dev-world` 重建重配,恒不需要手工收拾。

**对接真平台**(生产形态,两角各自为政):token-server 配好 `.env`(控制台签发的 appkey + `TANSR_API_BASE=https://api.tansr.com`)后:

```bash
# 终端 1:令牌换发服务(appkey 与平台基址读自 examples/token-server/.env)
cd examples/token-server && npm start

# 终端 2:本应用(demo 缺省基址即线上 https://api.tansr.com,直接启动;
# SDK 持令牌直连平台,不经 token-server 转发)
cd examples/electron-chat && npm start
```

真平台上 App 的模型/能力位/配额以控制台配置为准(demo 的能力面板照 bundle 呈现,「媒体授权模型」节列四键授权媒体集、顺位第一标缺省);聊天走真钱包计费;图像/视频/语音生成池未在生产配置前,对应能力 fail-closed(`imagegen_not_configured` / `videogen_not_configured` / `asr_not_configured` / `tts_not_configured`),系设计。

连接就绪后,输入框上方是一排「试一试」能力芯片:每颗驱动一项 SDK 能力(读/写/编辑文件、检索三件套、跑命令、抓网页、Http、待办、提问、子代理、技能双例、MCP 记笔记、画图、生成视频、读出来(TTS)、联网搜索……),未装配能力的芯片自动置灰，悬停提示检查授权与重新连接；具体兼容字段可在折叠的「开发者详情」中查看。也可直接输入自然语言。

环境变量(均可缺省):`TANSR_TOKEN_SERVER`(缺省 `http://127.0.0.1:8788`)、`TANSR_API_BASE`(缺省 `http://127.0.0.1:8787`)、`TANSR_CHAT_MODEL`(起始对话模型,缺省取 bundle 别名 `main` 或目录首行)、`TANSR_APP_TOKEN`(直给令牌跳过 token-server;冒烟与夹具用,生产恒走换发)。

> **`TANSR_IMAGE_MODEL` / `TANSR_VIDEO_MODEL` 已退役(S-G1,2026-08-30)**:媒体生成模型名不再手配——App 授权媒体集随 bundle `platformModels` 段下发(序 = 平台收敛稳定序,**顺位第一 = 不点名时的缺省模型**),SDK 装配自取并写进工具描述,LLM 可点名集内任一行、不点名即缺省。想换缺省/换集,在控制台改应用的模型授权面即可,demo 与集成代码零改动。

## 应用业务提示词（AP-SP）

本示例默认省略 SDK system，由 SDK 在实际装配时读取平台正文和策略；沙箱、工具、MCP、搜索与四媒体指南通过 systemAppend 追加。预读配置只用于能力展示，平台角色不会被宿主再复制一遍。没有平台或 SDK 业务段时，仍有示例的简洁中文和工具使用指南。

可设置 `TANSR_DEMO_SYSTEM`：未设=使用平台默认；非空=显式 SDK 业务段；空值=`[]`。`npm start` 读取进程环境，不自动加载 `.env`；部分 shell 会把空值解释为删除环境变量，验证显式空数组时可在 `src/main.ts` 使用 `startChatApp({ ...configFromEnv(), system: [] })`。平台“SDK 传入时保留平台提示词”关闭时是 `fallback`（显式 `system` 替换）；开启是 `prepend`（P→S，`[]` 也保留 P）。

侧栏在每轮开始时从 `session.applicationPrompt` 显示本轮实际采用的来源：平台、SDK、平台+SDK、仅示例指南；新字段中英文均可用。平台正文或策略修改后，SDK 会在同一会话的下一轮开始前自动读取，无需重开应用、创建新会话或清空历史。执行中的一轮固定使用开始时的配置，工具和媒体指南继续保留；`TANSR_DEMO_SYSTEM` 是宿主启动选项，修改该环境变量仍需重启示例。

`src/advanced-api.ts` 的 query 和 runAgent 示例同样消费 TANSR_DEMO_SYSTEM；低阶 runAgent 经 resolveApplicationSystem 显式组装，不把低阶接口说成自动装配。完整代码与规则见 [跨端指南](../../doc/AP-SP-提示词层级与跨端示例.md)。

离线验证：`npm run test:system` 覆盖平台/SDK 组合、空数组、预读与逐轮读取变化、来源和媒体指南保留；`npm run test:demo` 用隐藏 Electron 窗口验证现有界面与中英文来源；`npm run test:system-refresh` 调用真实应用接线和本地假 HTTP 上游，连续三轮验证策略更新、平台角色移除、侧栏来源变化及同会话历史保留。

## 媒体交互（PM-01）

- **图像输入**：选择 PNG/JPEG/WebP（≤16 MiB），预览并复制到应用工作区。发送后由既有 Read 工具提供图像上下文，仍经过工具权限和模型能力检查。图片不是伪造的音频/视频 IR。
- **语音输入**：选择 WAV/MP3/M4A/OGG/WebM/FLAC（≤16 MiB），或点击录音、再次点击停止（60 秒自停；真实单声道 PCM WAV）。转写走 session.platform.transcribe，结果填入草稿供检查，不自动发送。实际支持格式与时长以授权 ASR 模型为准。
- **朗读**：输入栏「朗读输入」或助手消息「朗读」调用 session.platform.speak。独立产物区提供播放和保存，音频直连不制造 KernelEvent、不自行进入会话历史；模型仍可调用系统工具 SpeechToText/TextToSpeech，其结果走真实工具事件卡。
- **产物**：ImageGen/VideoGen 图片与视频、SpeechToText 转写文本、TextToSpeech 音频均可保存。支持图像 data URI、音频 URL/base64；后续事件快照保留已完成媒体播放器。SDK/serve 不配置 CLI artifactSink，path 是附加信息，不能当任意读盘授权；宿主本地预览/保存只接受自身 media-artifacts 根内路径。
- **边界**：视频生成/参考 URL 仍由普通文字提示驱动 VideoGen；主模型消息协议未增加原生音频或视频块。token-server 只换发令牌，媒体调用由 SDK 承担，令牌不进入页面。
- **下载策略**：保存按钮只传主进程登记的产物 ID，用户选择目标文件。远端保存限 HTTPS、256 MiB、60 秒，拒绝重定向和不匹配 MIME；默认允许 *.aliyuncs.com 与 api.tansr.com。其它 CDN 须由宿主设置 TANSR_DEMO_MEDIA_HOSTS=cdn.example,other.example（精确域名），不向 CDN 附带平台令牌。URL 过期、位关、模型未配置、越界路径均显示失败原因。

离线验收：仓根运行 node --import ./scripts/inject-globals.mjs --import tsx --test examples/electron-chat/test/media.test.ts examples/electron-chat/test/media-chain.test.ts；Electron 真页面运行 npm run test:demo。前者走真实 SDK/内核/权限/事件/投影，仅 HTTP 上游替身；后者验证真 DOM/IPC、视频和 WAV 解码、控件、播放器连续性。两者不宣称真实付费上游验收；麦克风设备/系统授权、真实四媒体模型和 CDN 保存仍需接入材料后实测。

## SDK 双轨：固定 npm 依赖与临时 workspace 开发态

### 闲置后的令牌续期（OBS-01）

主进程保留 token-server 返回的令牌和有效期，在下一次平台请求前按需续期，并合并并发换发；不需要后台定时刷新。SDK 的模型、提示词、媒体及示例 bundle/usage 请求共用同一出网适配器。取消一个调用不取消其他调用的共用换发，退出应用终止换发，迟到结果不再生效。换发有 30 秒等待上限，提前量取 30 秒与剩余有效期十分之一的较小值。

该接线使用既有 `fetchImpl`，保留精确 npm 依赖，不要求先升级 SDK getter API。`TANSR_APP_TOKEN` 仍是静态票据测试模式，没有换发来源；到期后需要提供新票据。平台提前吊销、权限或额度错误不会触发模型/媒体请求自动重放。登录和令牌值先校验为非空可打印 ASCII 串，异常回执不会原样送入界面。

The main process renews the developer-issued platform token before a request when needed, using its expiry and a shared in-flight renewal. Model, application-prompt, media, bundle and usage calls use the same adapter. Closing the app stops renewal; cancelling one caller does not cancel other waiters. Static `TANSR_APP_TOKEN` values have no renewal source. Requests that may incur charges are never automatically replayed after an authentication error. This uses the existing `fetchImpl` API and keeps the pinned npm SDK dependency.

本 demo 的依赖声明在两态间切换:

- **npm 消费模式**——`"@tansr/sdk": ">=0.17.0"`:装的是 registry 上的正式发布物(dist 单文件 ESM + d.ts),与真实集成开发者拿到的形态同源同物;pnpm 按最低基线与锁文件从 registry 解析、不软链 workspace 本地包(`link-workspace-packages` 未开),demo 跑的就是发布物。这是 demo 的**常态**。
- **workspace 开发态**——`"@tansr/sdk": "workspace:*"`:SDK 双态 exports 的开发态入口直指 `src/index.ts`,无 dist、无 registry 中间层,改一行 SDK 源码即时生效;`build.mjs` 把 kernel/protocol 连同 SDK 一起打进单文件产物,构建脚本两态零改。

切换法:改依赖声明后仓根 `pnpm install`(非 frozen,`pnpm-lock.yaml` 随之更新)。

**依赖声明采用 `>=最低基线`，lock 精确记录实际 registry 版本**（REL-06-D）：基线由 `scripts/demo/dependencies.json` 管理，交互启动只自动检查稳定版；完整消费验收仍须对应真实已安装版本。SDK 开发联调可临时切 `workspace:*`，交付前须恢复 registry 基线声明并重新验证锁文件、类型和构建。不能用 workspace 链接或候选 tarball 冒充正式依赖。

发布史(历史,供追溯):2026-08-30 `0.1.0` 上架后由 `workspace:*` 切换为 npm 消费模式;`0.2.0` 携环3 webSearch 客户端;2026-09-01 `0.3.0` 携媒体池化自取/会话管理/S-WS2 平台搜索单通道;`0.4.1` 携媒体双工具超时定谳修(VideoGen 660s / ImageGen 180s);`0.5.0` 携媒体异步任务径(提交+轮询,超时恒带 taskId 找回径)与视图呈现档 delivery(设置面切换器即此);`0.6.0` 携思考生成面旋钮(setThinking,设置面「思考生成」即此);`0.7.0`–`0.7.2` 携思考双半场端侧 / features 接线修 / retryable:false 一次判死;`0.10.0` 携媒体双工具于历史写法 `tools.platform`(deprecated,见下节)。此前 demo 为跨版本兼容保留过一批运行时特性探测(`SDK_HAS_MEDIA_POOL` / `SDK_HAS_AUDIO_POOL` / `SDK_HAS_PLATFORM_SEARCH` / `SDK_HAS_VIEW_DELIVERY` 等「SDK 未携…如实降级」shim,历史),doc/128 D-1 起**全部删除**:demo 恒按配套 SDK 公共面编码,版本错配由发版节奏与本节切换程序管理,不再在代码里探测。

## 媒体四工具 = kernel 环2 工具 + 平台提供方(doc/128)

图像 / 视频 / 语音转文字 / 文字转语音四项能力自 2026-09-05 起是 **kernel 环2 内置工具** `ImageGen` / `VideoGen` / `SpeechToText` / `TextToSpeech`(`packages/kernel/src/tools/media/`:参数 schema / 描述装配 / 超时 / 错误纪律 / `data` 组装)+ **`@tansr/sdk` 平台提供方**(`createPlatform*Provider`:令牌闭包打平台 `/t1/{imagegen,videogen,asr,tts}`,任务径轮询,网关错误信封 → 结构化错误码)。工具名、`data` 形、错误码、hint 文案与迁移前逐字相同——"平台托管能力"不再是独立的"环3 工具",而是"环2 工具 + 平台提供方"。

**装配律**(demo `src/app.ts` `builtinSelectionOf` 即样板):

- 装配键 = `tools.builtin` 词表 +4:`'imageGen' | 'videoGen' | 'speechToText' | 'textToSpeech'`;四名的**工具位 = `capabilities.platform.<name>`**(不是 `capabilities.tools`)。demo 按位列名:位开即列、位关不列。
- **缺省全集档不装四工具**:`tools` 缺席 / `builtin` 缺席时 SDK 只装本地环2 全集,四个计费工具必须显式列名(既有 App 不因位开而突然多四个进 prompt 的计费工具)。因此 demo 的 `tools.builtin` 是显式列表 = `resolveBuiltinSelection(undefined, caps)`(本地全集,按 `tools.<name>` 位过滤;`webSearch` 再按 `platform.webSearch` 通道位过滤)+ 媒体四名按位。
- 位关而显式列名 → 装配期 fail-fast `TansrSdkError('capability_disabled')`(文案携位名与控制台指引;demo 保留一层兜底捕获,防拉档与装配之间控制台改位的窄窗竞态:重拉最新 bundle 按新位重建)。
- 旧写法 `tools.platform: [...]` 退为 **deprecated 等价别名**(SDK 仍受理、等价并入 builtin 选择集、同名同描述同 schema;不 fail-fast,因 `0.10.0` 已按该写法发布);demo 不再使用。
- 装配序固定:… → `http` → `ImageGen` → `VideoGen` → `SpeechToText` → `TextToSpeech`(prompt 前缀稳定)。
- 模型名零手配不变:授权集来自 bundle `platformModels` 四键(`AppBundle.platformModels` 恒四键在场),SDK 随取随注进平台提供方 `models`,kernel 骨架据此在工具描述自列授权集(顺位第一 = 缺省);demo 的 system 用法段仍**只在工具真装配时注入**,集空时如实告知「平台未配置」。

**产物形单源**:四个 `data` 形逐字不变——`ImageGenData { model, images:[{url}], imageCount }` / `VideoGenData { model, videos:[{url}], videoCount, billedSeconds }` / `TranscriptData { model, text, language?, durationSec?, segments? }` / `SpeechData { model, audio?: { url?, b64?, mime, format, durationMs?, sampleRate?, path? }, billedChars }`,错误回执携 `errorCode`。形状判别不再各端手写:protocol `parseMediaArtifact(data): MediaArtifact | null`(`@tansr/sdk` 重导)是 doc/123 §3.7 三端形状规则的 TS 参考实现(判序 speech → video → image → transcript;`errorCode` 字符串在场恒 null——错误回执不是产物),Android / iOS 的形状嗅探以它为对表基准(doc/107 §二)。

**demo 的投影 / 渲染分工**:renderer 是裸 JS、不经打包、不能 import sdk,所以判别恒在主进程——`app.ts` `projectViewForRenderer` 在 SessionView 快照过 `'chat:state'` IPC 前,对每张 `completed` 工具卡的 `result`(`ToolResult.data` 透传位)调 `parseMediaArtifact`,把 `artifact: MediaArtifact | null` 随 part 下发;`renderer.js` `renderMediaArtifact` 只按 `artifact.kind` 分派四种渲染:`image` → 逐张 `<img>` 真解码 + 可复制 URL 行;`video` → `<video>` + URL 行(附计费秒数);`transcript` → 转写文本块(`textContent`;有说话人分离时逐段列出)+ 模型/语种/时长元信息;`speech` → `<audio>`(URL 直播 / 内联 b64 拼 `data:` URI,CSP `media-src` 为此放宽 `data:`)+ 格式/时长/计费字符行。`artifact === null`(WebSearch 结果卡 / 非媒体工具 / 错误回执)走既有分支或文本摘要;一切内嵌媒体加载失败仍落**可见兜底块**。renderer 里不再出现基于 `images` / `videos` / `audio.mime` 的形状判别(对抗验收 A-14 grep 门)。ASR 有音频文件与录音转写入口，先填入草稿；模型发起的 SpeechToText 工具仍走同一产物投影。

**待发形态守门轨**(验证当前 workspace 源码打出的下一版 tarball,发布前用):

```bash
npm run verify:dist   # 需 npm registry 网络装 demo 依赖;Electron 二进制跳过下载(本轨不启 GUI)
```

四关全过才绿:①仓根 `release:pack-sdk` 打 tarball(pnpm pack 原生径 + manifest 清洁与文件表守卫);②demo 整目录拷进 **workspace 外**临时目录、依赖改写 `file:<tarball>` 后 `npm install`(`ELECTRON_SKIP_BINARY_DOWNLOAD=1`;改写对 demo 当前处 `^x.y.z` 还是 `workspace:*` 无感,两态皆验待发 tarball);③断言安装物 main/exports 指 dist、包内恒无 src,再跑导入探针(demo 用到的全部 SDK API 面逐名在场——含 `parseMediaArtifact` / `setSessionViewDelivery` / `stripThinkingParts`、声明期真可调、缺省档 builtin 选择集未漂移);④发布形态 d.ts 下 demo 全源 `tsc --noEmit` + esbuild 全目标构建(dist 形态可打包进应用产物)。失败保留临时目录供排查。

## 全能力集成清单(UI 入口)

### SDK-OBS 正式包消费与显式候选验证

本轮新增 `contextState/subscribeContext`、安全 `switchModel`、图文 `sendBlocks`、共享 `planSpeechInput`。`pnpm test:obs` 缺省构建并验收 lock 对应的 registry SDK，能力缺失直接失败，不静默换成本地源码；正式包发布并安装后必须运行该门。`pnpm test:obs:candidate` 才会先构建本树 SDK dist，再用 `--sdk-candidate` 显式绑定候选。两条路径都启动隐藏 Electron 真页面/IPC 与受控 HTTP，并运行同一组原始行为断言。`dist/sdk-build-source.json` 记录 registry 或 local-candidate 来源；候选入口与 SHA256 不代表 npm 已更新或已发布。

图片选择器现在把原图与问题放在同一条用户消息，不再生成 Read 提示词；支持纯图，忙时保留草稿。模型和上下文面板区分下轮选择、执行轮绑定、usage回执观测、本地估算与未知设置。缩窗由 SDK 保留原文、按源窗整理摘要并检查目标预算，失败不切模；包含原件的轮次保留相对顺序。

朗读模型与聊天模型分开选择，动态帽来自授权媒体目录；汉字计2、其余码点计1。长文必须显式允许分段，总计最多32000加权字符/32段。已完成片段保留产物，失败结果不明的片段不自动重放；“继续尚未请求的片段”仅请求未开始部分，并明确提示缺段，避免重复计费。恢复图片与Read图片按原始历史投影，图片原件不会被空文本过滤。

The default `pnpm test:obs` consumes the installed registry SDK and fails if required public capabilities are missing. `pnpm test:obs:candidate` explicitly builds and selects the local SDK candidate. Both run the same hidden Electron DOM/IPC assertions against a controlled HTTP server; neither command updates dependencies. Images and text share one user message, busy drafts are retained, and model/context observations stay separate from cumulative billing. Speech limits follow the selected authorized model. Segmentation is opt-in (32000 weighted characters, 32 segments); completed results are retained and uncertain segments are never automatically charged again.

每项能力都有真实可触发入口,与 SDK 装配缝一一对应(接线全在 `src/app.ts`,main 与端到端探针装配同一份——测的即跑的):

| 能力 | 装配缝 | UI 入口 / 可见面 |
|---|---|---|
| 令牌档会话 | `createSession({ token, baseUrl, model })` | 启动即连;顶栏 phase 显示连接进度与能力位计数;令牌恒在主进程 |
| 三层渲染喷口 | 层0 `session.events` 扇出 → 层1 `reduceSessionView` → 层2 `createNarrator` | 聊天区 = 层1 投影;侧面板「叙述日志」= 层2 verbose 行流(事件流是单消费者,双消费经扇出泵——`src/app.ts` 即样板) |
| 环1 defineTool | `tools.custom` | 芯片「应用信息」(`readOnly: true` 声明;令牌档裁决人 auto 模式下自定义工具仍走确认门——见 `test:demo-e2e` 确认门名单)/「改窗口标题」(副作用走确认门,窗口标题真实变更) |
| 系统工具目录 | Demo 显式选择本地/网络及获授权媒体；SDK 缺省不自动添加媒体 | 芯片及本地/网络/媒体目录；未装配项置灰，原授权字段在开发者详情 |
| todoWrite 台账 | SessionView 的 `todos` 投影 | 侧面板「待办」实时台账(状态徽章) |
| askUser 提问 | `promptChannel`(提问桥 `src/question-bridge.ts`) | 芯片「问我问题」→ 提问对话框:选项 + 自由文本并存,结构化回流进工具结果 |
| 运行时权限 | `permission.askUser`(权限桥 `src/permission-bridge.ts`)+ 令牌档缺省裁决人(doc/113,bundle 生效裁决人档即装配,引擎 auto 模式) | 沙箱内 Write/Edit 由裁决人按资格半径就地放行(引擎 auto 模式工作区快路径,本地判定不出网、不弹框;事件面无 `tool.permission.requested` / `decided`——kernel 对门面级放行不发 decided);越界/危险工具(命令写能管道/花钱的媒体与搜索/mcp__*/自定义工具)仍弹确认框(`decided` 归因 `user`);与提问框是两条独立注入缝 |
| skills 双例 | `skills.custom`(defineSkill 内联)+ `skills.dirs`(目录装载 `assets/skills/`) | 侧面板「技能」列两例;芯片「技能:内联」「技能:目录装载」点名装载(正文带确认口令,装载即可证) |
| MCP | `createMcpHost`(stdio 接本地示例服务器 `assets/mcp/demo-notes-server.mjs`) | 侧面板「MCP」显连接态与 `mcp__demo__*` 桥接工具名(eager 展开);芯片「MCP 记笔记」真调子进程 |
| Task 子代理 | agent 位开 SDK 自动附着 | 芯片「派子代理」;叙述日志可见 `subagent spawned/completed` 生命周期 |
| 媒体四工具 imageGen / videoGen / speechToText / textToSpeech | `tools.builtin` 按 `platform.<name>` 位列名(kernel 环2 工具 + 平台提供方,doc/128;缺省全集不装,须显式列名) | 三个模型工具芯片「画一张图」「生成视频」「读出来(TTS)」；ASR 另有文件与录音转写入口，先入草稿、不自动发送。完成卡按 `parseMediaArtifact` 判别渲染:`<img>`/`<video>`/`<audio>` 真解码 / 转写文本块 + 断链可见兜底 + 计费注记；未装配的芯片置灰并提示检查授权 |
| webSearch(平台通道) | 双位齐开(`tools.webSearch` + `platform.webSearch`)→ 环2 `WebSearch` 工具(平台后端,S-WS2) | 芯片「联网搜索」;完成卡结构化结果列表(标题/URL/摘要恒 `textContent`,URL 纯文本恒不成链) |
| setModel 切模 | `session.setModel(handle)` | 顶栏模型切换器(选项 = bundle 收敛的模型目录;displayName 主显、handle 括注,厂商 optgroup 分组——bundle `manufacturer`/`family` 两维,旧 bundle/旧版 SDK 无维回落平铺;选定值恒 handle,2026-08-30 ④);切后下一轮生效,失败会话状态不变 |
| 可注入持久化 | 五方法 `SessionStore` 常驻内存，`resume` 沿用完整记录(`src/persistence.ts`) | 开关只控制整卷磁盘镜像；不重建会话、不打断权限或媒体请求；重启恢复历史及原平台会话身份 |
| 能力面板 | bundle `capabilities` + SDK 同一 `resolveBuiltinSelection`(`builtinSelectionOf` 与装配同一函数) | 侧面板统一系统工具目录(本地与任务 / 网络 / 媒体，16 项按本会话装配显示状态)及自定义工具 / Task / Skill / MCP 附着信息；默认折叠的开发者详情保留 17+5 个兼容授权字段(位关灰+删除线)，另列媒体授权模型四键、用量自查(`/v1/my-usage`,schema 恒无金额)、配额注记(限额在网关侧执行,撞限 429 携 detail) |

三档 API:聊天主流程用 `createSession`(上表);另两档 `query`(单轮便捷面)与 `runAgent`(低阶 QueryHandle,经 `assemblePlatformModel` 取令牌档 client/model)不便塞进聊天 UI,给了独立可跑样例:

```bash
npm run demo:advanced   # = node dist/advanced-api.mjs;无 GUI 纯 Node,会真实调用两次模型(少量计费)
```

### 会话保存与恢复（REL-10）

Demo 使用 SDK 的 `store` / `resume` 承诺面；开关持久化时保留当前 `AgentSession`、上下文、平台 ULID、权限桥、MCP 和媒体草稿。关闭后迟到的轮末提交只更新内存，不会重新生成已清除的历史镜像。开启或删除失败会提示，设置面板显示当前写入状态；删除失败不代表磁盘已清理成功。

历史位于用户数据目录的 `sessions/<作用域摘要>/chat-history.json`。作用域由 API 基址、token-server 基址、平台已接受令牌中的应用 `sub` 与用户 `eu` 构成；密码、短期 token、jti 不参与。该解析只用于本机分域，平台仍负责签名与归属验证。无法取得稳定身份时保留纯内存并提示；换票改变身份会在传输前拒绝，需重启后进入正确作用域。

旧版根目录 `chat-history.json` 没有归属信息，启动时须确认属于当前账号及应用才能导入。导入后使用 v2 完整 SessionRecord 恢复；原 v1 文件保留，并用迁移指纹防止再次自动导入。旧文件可能仍含历史，关闭新镜像不会删除这份迁移备份。坏 JSON、版本或身份异常会停止恢复并保留原卷；不能把损坏当成空历史后自动覆盖。每次写入使用同目录独立 UUID 临时文件，失败只清理本次创建的临时文件。崩溃遗留的 `.tmp` 文件会保留，不会阻断恢复和后续写入；应由文件所有者核实后处理。

`npm run test:persistence` 使用合成历史及正式 npm SDK 验证开关、迟到提交、写入/删除失败、账号隔离、旧卷迁移、换票、切模与重启恢复。通用合同与逐端验收见[SDK 接入手册 §4.7](../../doc/90-SDK技术手册.md)及[REL-10 会话与缓存接入检查单](../../doc/REL-10-会话与缓存接入检查单.md)。恢复身份不保证厂商缓存尚未过期，费用仍以实际供应商缓存语义及计价快照为准。

### 全能力演示的两级探针

```bash
npm run test:demo       # 离线 UI 真人路径:合成数据驱动全部新 UI 面,零网络确定性可复跑
npm run test:demo-e2e   # 端到端三相:自孵 dev-memory 网关 + 假上游,真 GUI 逐能力驱动断言(需同级 tansr-api 仓)
TANSR_DEMO_LIVE=1 npm run test:demo-e2e   # 同上但聊天走真 kimi(需 tansr-api/.env 真凭据;分钱级计费;图像/视频恒假上游)
```

`test:demo`(`src/demo-rtt.ts`)守 UI 半边:统一系统工具目录的本地与任务 / 网络 / 媒体三组、16 项不重复及四媒体实际装配状态，中英文目录与说明切换；兼容授权字段默认折叠，展开后保留 22 位(17 工具 + 5 平台)逐位呈现与位关灰显；自定义工具 / Task / Skill / MCP 附着信息、用量面板、媒体授权模型四键面板；芯片按实际装配决定可用性(媒体仍为画图、视频、TTS 三个芯片，未装配时置灰并提示检查授权)，用户音频直连与模型工具调用分开呈现、转写先入草稿；切模器与持久化开关经真 IPC 回流、提问对话框全往返(选项+自由文本+abort 收口)、媒体完成卡经 `app.ts` 同一 `projectViewForRenderer` 投影(`parseMediaArtifact` 单源判别):VideoGen `<video>` 真解码与断链兜底、SpeechToText 转写块、TextToSpeech 内联 b64 WAV `<audio>` 真解码、错误回执卡恒不当产物渲染、WebSearch 结果卡(S-WS2 平台通道;标题/URL/摘要三段呈现 + 恒 `textContent` 防注入:HTML 片段素材原样成文本、零 `<a>`/`<script>` 元素)、待办/叙述/已恢复历史区——可见性恒走计算样式、可点恒走命中测试(permission-rtt 同纪律)。

`test:demo-e2e`(`fixtures/demo-e2e.mjs` + `src/demo-e2e-rtt.ts`)守全链三相:**main 相**逐能力驱动(确定性档经假聊天上游 `#script` 脚本化 tool_calls;每项断言取客观 side-fact:磁盘逐字节/窗口标题/`naturalWidth` 解码尺寸/对话框往返/子进程状态/搜索结果卡携假上游 NONCE),外加 wire 证据(假上游全录:19 个工具名真上 `tools[]`——含 `ImageGen` / `VideoGen` / `WebSearch` 单态必到、system 真携 `<available-skills>` 索引、假搜索上游真收到本轮 query 且 `max_results` 透传保真、媒体双式回显 wan-turbo / wan-video);**restore 相**重启同一 userData 验持久化恢复(恢复区呈现标记文本 + SDK `resume` 恢复身份与历史);**degraded 相**控制台关位(shell、videoGen、platform.webSearch)后验降级可见(面板位灰/芯片禁用/装配集收缩——媒体行存 imageGen 无 videoGen,会话仍健康)。权限语义按 doc/113 裁决人(令牌档缺省装配,引擎 auto 模式)锁两半:③ 沙箱内 Write 由裁决人按资格半径就地放行——整轮 `#permission-backdrop` 计算样式恒 `none`、事件面有 `tool.proposed` 无 `tool.permission.requested` / `decided`(kernel 对门面级放行不发 decided;用户点击必发 decided 归因 `user`)、磁盘实检照旧,harness wire 段同证「Write 零分类器咨询」(auto 工作区快路径 = 本地判定不出网);⑥ 起危险工具(Shell 写能管道/mcp__* /ImageGen/VideoGen/WebSearch/setWindowTitle)仍弹确认框,由「自动允许驾驶员」命中测试代点,确认名单与「弹框现身 + decided 归因 `user`」全程记录——`WebSearch` 虽在 kernel 声明只读,SDK 权限缺省仍逐工具询问(按次计费可见地过门;T-K4 结构放行只辖 shell 面,裸 `echo` 这类只读命令自动放行不弹框,系设计)。harness 的 App 能力档只开 imageGen / videoGen / webSearch 三平台位(音频两位未配,面板如实位关:22 位呈现、20 位开)。

## imageGen:平台托管能力样板(= 环2 工具 + 平台提供方)

SDK 宿主(本 Demo 的 Electron 主进程)调度环1 `defineTool` 业务函数和环2 系统工具；业务函数的实际副作用取决于其实现，文件读写等本地工具操作宿主工作区。网络请求和媒体生成不因此变成本地业务：WebFetch / Http 访问远端服务，WebSearch 与媒体四工具通过平台提供方完成远端搜索、生成或转写。**平台托管能力**(doc/128 起 = kernel 环2 工具 + 平台提供方,不再是独立的"环3 工具"):`tools.builtin: ['imageGen']` 装配出的 `ImageGen` 工具,骨架在 kernel(参数 schema / 描述 / 超时 / 错误纪律),执行后端是 SDK 平台提供方持会话令牌打平台 `POST /t1/imagegen`,由平台代调图像上游(万相/千问图像系),**按成功张数计量计费,归属你的 App 钱包**(对终端用户怎么转售归你;终端 `my-usage` 恒无金额字段)。旧写法 `tools.platform: ['imageGen']` 为 deprecated 等价别名(历史;见「媒体四工具」节)。

联网搜索(S-WS2,用户拍板 2026-08-31):SDK 里恒一件工具 = 环2 kernel `WebSearch`,后端恒为平台通道(`POST /t1/websearch`:平台代调搜索供应商池,加权选池 + 故障转移,**按次计费**;查询文本平台侧恒不落日志不落库)。`tools.webSearch` 工具位 + `platform.webSearch` 通道位双开即自动装配;**BYO(终端自配搜索端点/密钥)在 SDK 恒不存在**——终端持搜索商密钥即 appkey 防盗模型要消灭的反模式,可审计面(逐笔 usage、预扣结算、endUserId 归因)恒在平台。renderer 对其完成卡走结构化结果列表(`part.result.results[]` 的 title/url/snippet 三段恒经 `textContent` 落 DOM——搜索结果来自外部网页属不受信输入;URL 以纯文本呈现供复制,恒不 `<a>` 成链)。fail-closed 码(经 kernel provider_error 结构化透传):位关 `forbidden`、平台搜索池未配 `websearch_not_configured`、日配额尽 `websearch_quota_exceeded`(全为结构化工具错误,不炸会话)。

本 demo 的接线(`src/app.ts`)演示了两件事:

1. **装配**:能力位驱动——`platform.imageGen` / `platform.videoGen` / `platform.speechToText` / `platform.textToSpeech` 位开才把对应名字列进 `tools.builtin`(`builtinSelectionOf`;缺省全集档不装四工具,必须显式列名),并只在工具真装配时往 system 注入用法段(计费口径、时效提醒;**模型名零手配**——授权集已由 SDK 自 bundle `platformModels` 取来经平台提供方写进 kernel 工具描述,顺位第一 = 缺省,LLM 不点名即用之;不装而提,模型会空指)。renderer 侧不再特判形状:主进程 `projectViewForRenderer` 对完成卡 `part.result`(`ToolResult.data` 透传位)调 `parseMediaArtifact`,renderer 按 `artifact.kind` 内嵌渲染 `<img>` / `<video>` / `<audio>` / 转写块(CSP 仅对 `img-src` / `media-src` 放宽外链与 `data:`,脚本样式恒 `'self'`);URL 约 24h 失效,卡片同时给出可复制文本(视频卡附计费秒数,语音卡附计费字符)。智能体文本里的 markdown 图片(`![alt](http…)`)同样渲染为真 `<img>`——只认图片这一种 markdown 语法(URL 锚定 http/https),恒不引入渲染引擎,其余文本仍走 `textContent`;一切内嵌媒体加载失败(断链/过期/上游未启)都落**可见兜底块**,恒不留 0 尺寸空白装失败。
2. **能力位降级可见**:`platform.imageGen` / `videoGen` / `speechToText` / `textToSpeech` 是控制台治理的 App 能力位(**平台缺省档为关**)。demo 按位列名,位关即不列(静默不装)+ 系统工具目录显示未启用、折叠开发者详情保留位灰 + 「画一张图」「生成视频」「读出来(TTS)」芯片置灰并提示检查授权(降级在输入口可见);位关而硬列名会得到装配期 `TansrSdkError('capability_disabled')` 可读错,demo 保留一层兜底捕获(拉档与装配之间控制台改位的窄窗竞态:重拉最新 bundle 按新位重建)。控制台开位(`PATCH /v1/apps/:id` 的 `capabilities.platform.*`)后重启应用即可用。

其余 fail-closed 面(工具拿到的都是结构化错误结果 `data.errorCode`,不炸会话;`parseMediaArtifact` 对错误回执恒 null,renderer 走文本摘要而不当产物渲染):平台侧图像池未配置 → `imagegen_not_configured`;模型名不在 App 授权面 → `model_not_authorized`;日张数配额尽 → `imagegen_quota_exceeded`。`ImageGen` 有真实计费副作用(按张扣 App 余额),缺省权限模式下每次调用都会走权限确认弹框——demo 有意保留这层「花钱要点头」的体验。

### 本地验证夹具:假图像/视频上游(fixtures/)

真 dashscope 凭据未交割前,本地端到端验证用 `fixtures/fake-dashscope.mjs` 充当图像+视频上游(**验证夹具,非生产**):

```bash
node fixtures/fake-dashscope.mjs   # 127.0.0.1:8790;FAKE_DASHSCOPE_PORT / TANSR_DEV_FAKE_DASHSCOPE_KEY 可覆盖
```

- 形态对齐 dashscope 官方任务径(图像经典万相 + 视频万相 `video-synthesis`,创建回 `task_id` → 轮询 `SUCCEEDED`;视频径按官方语义强制 `X-DashScope-Async`),Bearer 密钥与 api 仓 `dev:memory` 网关的池行夹具配置同值;网关把池指到本夹具属 api 仓配置面,demo 侧无感知——真凭据到位后池行改指真 dashscope + 平台侧 env 放真 key,demo 零改动、同一显示路径直接生效。
- `/img/*.png` 回吐**人眼可见**的占位 PNG(`placeholder-png.mjs` 纯 Node 现场编码,零依赖):尺寸尊重创建请求的 `parameters.size`(网关从工具入参 `size` 透传),未带 size 按缺省 768×768;图上题字 `TANSR FAKE UPSTREAM` + 文件名,一眼认出是假图;任务 URL 携启动戳跨重启唯一(防 Chromium HTTP 缓存把旧轮产图串给新轮)。
- `/vid/*.mp4` 回吐**真可解码可播放**的占位 MP4(`placeholder-mp4.mjs` 纯 Node 零依赖手写 H.264 全 I_PCM + ISO BMFF 封装):时长尊重创建请求 `duration`(计费口径),176×144@2fps,背景色逐帧轮换 + 底部进度条 + 题字——在 `<video>` 里一眼看出「在播,而且是假片」。可播性有独立探针:`npx electron fixtures/probe-video-playback.mjs`(Chromium 真解码四相:首帧解码/分辨率/时长/片中 seek)。
- 曾经的坑(2026-08-29,双事故同日):①旧版夹具 `/img/*` 恒回 70 字节 **1×1** PNG——HTTP 200、`image/png`、魔数全真,demo 也真内嵌渲染了,但 1×1 人眼不可见,真人体验直接判成「imageGen 失败」;②夹具重启后 `igtask-` 序号归零,新旧轮撞同名 URL,Chromium 缓存把旧图串给新轮。探针为此把断言从 `naturalWidth>0` 收紧到「≥256 且与请求 size 相符」(见下节);视频夹具自出生即守同律(恒不回「魔数真而不可解码」的假 mp4)。

### 平台能力收敛链端到端走查(image + video 双链)

```bash
node fixtures/capability-chain-e2e.mjs   # 需同级 tansr-api 仓(或 TANSR_API_DIR 指定);零真上游、零真计费
```

自孵 dev-memory 网关 + 假上游,把「控制台配置(模型+凭据 env 名+成本+乘数)→ 平台池 →
账户收敛(model-selection)→ 应用确定(modelCatalogIds + capabilities)→ 终端只持
app_user 令牌请求」全过程对 imageGen 与 videoGen 各跑一轮并逐步断言:运行时算价
(0.18×1.5/张、0.30×1.5×秒,全夹具值)、fail-closed(乘数未配 → `*_not_configured`)、
治理位即时生效(PATCH 关位 → 403)、endUserId 用量归因、以及**本地零上游凭据**
(SDK 发布产物与 demo 源码字节级 grep;上游 key 只活在 api 子进程 env)。

### 计量与双向审计端到端验收(五面 × 三维)

```bash
node fixtures/metering-audit-e2e.mjs   # 需同级 tansr-api 仓 + 真凭据 env TANSR_COMPAT_UCLOUD_API_KEY;真调 kimi(分厘级真实计费)
```

自孵 dev-memory 网关(独立端口 8841)+ 进程内假搜索上游,真调 kimi 产真实用量后对五面逐格断言
(连通/有效/可控三维,证据 JSON 落 `%TEMP%/cap-audit-U/`):①终端自查面 `/v1/my-usage`
(深扫响应树证 schema 级**无金额键**;`?endUserId=<别人>` 恒被忽略只返自己);②开发者对账面
`/v1/app-usage/by-end-user`(逐 endUser 明细+金额,序列化键集与 `/v1/usage/by-app` 逐一相等,
窗口 1–90d 钳制/分页/App 间隔离);③同源对账(同窗双投影逐 endUser 逐项相等,再与 by-app 三投影合账);
④流上实时计量(SDK `fetchImpl` 缝截获每次 exchange 的 `meta.requestId`,`cost.usage.updated`
逐请求到达应用层,与结算行 `twp:<requestId>` cost-explain 下钻逐笔对表——t.usage 帧即
settled.counts,同源恒等);⑤混流归因(3 endUser 并发 6 笔、提示词长度逐笔互异 →
by-end-user 逐桶恒等恒不串)。附带复核 S-A4 观察项:websearch 令牌径 usage 行未落
endUserId,该笔恒落 NULL 未归因桶(运行时实证,产品码恒不在本夹具改)。

### 模型收敛 + 配额 + 令牌生命周期验收(三能力 × 三维)

```bash
node fixtures/cap-audit-m.mjs   # 需同级 tansr-api 仓(.env 供 admin key 与 UCloud 采购凭据);真调 kimi(分钱级,全程 <¥0.1)
```

自孵 dev-memory 网关(独立端口 8831)一跑到底,对三能力逐格断言(连通/有效/可控三维,
终局打印全矩阵):①**对话模型收敛**——平台池(seed 4 chat 行)→ 账户 `PUT /v1/model-selection`
→ 应用 `modelCatalogIds` 三级收敛逐级读面;别名 `kimi` 终端真解出真调(响应归一 canonical
`kimi-k3`、真出文、usage 按模型分账 + endUserId 归因);账户级/应用级收窄与池外模型三拒恒
403 `model_not_authorized`(出网前拒零计费);SDK 发布形态 dist `createSession` → `setModel`
切模跟随(切前切后按模型分账逐笔对表,kimi 零增量)。②**配额**——`PATCH /v1/apps/:id`
quota 三值写读回;rpm 滑窗 429 `rate_limited`、日 token 429 `quota_exceeded`(detail 携
limit/used/resetAt)且**恒零计费**(429 前后余额逐字节对表);per-end-user 桶隔离(eu-qa
满桶时 eu-qb 同刻 200);4 并发打限原子恰 `[200,200,429,429]`(Lua 全桶先判后记零穿桶);
`quota:null` 清档即恢复、键内 null=该维不限。③**令牌生命周期**——appkey 换发 app_user
令牌即用;claims 七项逐验(typ/aud/iss/eu/sub/jti/exp);ttl=60 真过期即拒;四级吊销即时
(毫秒级):endUserCut(`POST /v1/apps/:id/app-user-revocations`,他人不累及、重换发恢复、
旧牌恒拒)/ appCut(rotate 断全部存量令牌 + 旧密文即死)/ 停用行复查(403 `app_disabled`,
复用即恢复)/ jti 级(设计上无 HTTP 面,api 仓 `test/auth/app-token-auth.test.ts` 单测覆盖)。

### 环1/环2 工具能力位全谱验收(17 位 × 连通/有效/可控三维)

```bash
npm run test:capability   # = node fixtures/capability-matrix-e2e.mjs;需同级 tansr-api 仓;零真上游、零真计费
```

自孵 dev-memory 网关(独立端口 8811)+ 假 openai_chat 聊天上游(8813,**提示词脚本化
tool_calls**:`#script [{tool,args},…]` 逐步回演调用、工具真实输出回显进终文)+ 假 web
靶点(8814),SDK 发布形态 dist 令牌档 `createSession` 逐位一会话跑三维:**连通性**
= 控制台开位 → 工具真进 wire `tools[]` 且端到端被调;**有效性** = read 真读到文件内容/
shell 真跑出命令输出(PowerShell 径)/write·edit 磁盘实检/glob·grep·list 真检索/
webFetch 真抓本地靶正文(经 `127.0.0.1.nip.io` 环回域;环回**字面量**恒被 SSRF 守卫拒
系设计)/http 结构化响应回流(`allow_private` 显式开)/todoWrite 真记台账
(`plan.todo.updated`)/askUser 通道真往返/defineTool handler 真被调/Task 子代理真跑
真回报/defineSkill 索引真进 system、正文点名真装载/createMcpHost 真桥 stdio 假 MCP
server(`fixtures/fake-mcp-server.mjs`)真列出真调;**可控性** = 控制台
`PATCH capabilities` 关位 → 装配期 fail-fast `capability_disabled` 可读错(携位名与
控制台指引),开回恢复(shell 位显式实证),位关未选静默不装(全关档 wire 零工具);
`process` 预留位恒随 shell 联动。缺省档核对:App capabilities 未配时 bundle 下发段
20 位逐位对表 doc/84 §4.1(缺席选择恰装缺省 6 工具,固定装配序)。末相拉起 Electron
探针 `capability-askuser-rtt`(`TANSR_CAP_SKIP_ELECTRON=1` 可跳):与 permission-rtt
同纪律(可见性=计算样式、可点=命中测试)在**真会话**里锁 AskUser 弹框选项真往返
(点允许=选首项)与 Write 权限 allow/deny 双径(允许则文件真落盘、拒绝则恒不产生,
`tool.permission.decided` 事件双向可回放)。

## 冒烟探针(无 GUI)

```bash
npm run smoke        # = node build.mjs && electron --no-sandbox smoke.mjs
```

在 Electron 主进程环境跑一轮真实链路:令牌换发 → `createSession`(令牌档)→ `send` → 流式收链至 `turn.completed` → 打印 usage → 退出码 0。验证三道关:ESM 产物可加载 / 主进程网络栈可出网 / 令牌档全链可跑。**会真实调用一次模型(少量计费)**;令牌可用 `TANSR_APP_TOKEN` 环境变量直给(跳过 token-server)。

## 真人路径探针(权限 + 内嵌图)

```bash
npm run test:permission                       # 缺省不出网,确定性可复跑(五相)
TANSR_PERM_RTT_E2E=1 npm run test:permission  # 追加⑥相:真会话 imageGen 成功径(需网关/token-server/假图像上游;真实计费)
```

程序化注入(`executeJavaScript`)测不出只坑真人的缺陷,2026-08-29 两桩事故实锤:其一,`#permission-backdrop` 的 `display: flex` 把 UA 样式表 `[hidden] { display: none }` 压掉(层叠源优先级作者恒胜 UA,与特异性无关),弹框启动即永久可见、空白且点不动,而当时的自动化验证全绿;其二,假上游回 1×1 PNG,`naturalWidth>0` 之类的弱断言照样全绿,人眼里却「没有图」。`permission-rtt` 探针以**用户视角**断言补上盲区:可见性恒走计算样式(`getComputedStyle`)、可点性恒走命中测试(`elementFromPoint`,鼠标真正会打到谁),装配与 `main.ts` 同一份权限桥(`src/permission-bridge.ts`)+ preload + renderer。

缺省五相:①启动首帧遮罩不可见、输入框可点;②allow 往返(SDK `askUser` 真收到答复);③deny 往返;④abort 收口无幽灵框;⑤内嵌图渲染——合成 SessionView 快照经真 `'chat:state'` 通道推给真 renderer,断言完成卡 img 真解码且 `naturalWidth≥256`(图源=夹具同款生成器经进程内环回服务,生成器回归成隐形小图会当场咬红)、气泡 markdown 图渲染为 img 且无裸文本残留、断链图有可见兜底。⑥相(E2E)真会话全链:UI 弹框真点允许 → 平台 `/t1/imagegen` → 假上游产图 → renderer 内嵌 `<img>` 真解码、`naturalWidth≥256` 且**尺寸与模型请求的 size 全链相符**(探针特意让模型请求非缺省的 `1024*768`,证 size 从模型入参 → SDK → 网关 → 上游全程保真)。退出码 0=全过,1=断言失败,2=超时/环境缺失。

## 目录

| 文件 | 职责 |
|---|---|
| `src/main.ts` | 主进程入口(薄壳:进程生命周期;接线全在 app.ts) |
| `src/app.ts` | 应用接线 `startChatApp`(会话装配 `builtinSelectionOf` 媒体四名按位/事件扇出双喷口/`projectViewForRenderer` 媒体产物判别投影/defineTool/defineSkill/MCP host/切模/持久化/能力面板;main 与 demo-e2e-rtt 共用,测的即跑的) |
| `src/permission-bridge.ts` | 权限桥(SDK askUser → IPC → 确认框;main 与探针共用,类型锚定 `AskUserCallback`) |
| `src/question-bridge.ts` | 提问桥(AskUser 工具 PromptChannel → IPC → 提问对话框;与权限桥两条独立缝) |
| `src/persistence.ts` | SDK SessionStore内存记录、按身份分域的磁盘镜像、原生resume、旧卷迁移及展示投影 |
| `src/advanced-api.ts` | 三档 API 进阶样例(query / runAgent;无 GUI,`npm run demo:advanced`) |
| `src/preload.ts` | contextBridge 白名单(renderer 唯一可用面) |
| `src/token.ts` | 向 token-server 换令牌(演示登录 → 换发) |
| `src/smoke.ts` + `smoke.mjs` | S-B6 冒烟探针(无 GUI 一轮全链) |
| `src/permission-rtt.ts` | 真人路径探针(计算样式 + 命中测试 + 内嵌图断言;可选 E2E 相) |
| `src/capability-askuser-rtt.ts` | 能力位真人路径探针(真会话 AskUser 选项往返 + Write 权限 allow/deny;由 capability-matrix-e2e 拉起) |
| `src/demo-rtt.ts` | 全能力演示 UI 离线探针(`npm run test:demo`;合成数据驱动全部新 UI 面) |
| `src/demo-e2e-rtt.ts` | 全能力演示端到端 GUI 探针(main/restore/degraded 三相;由 fixtures/demo-e2e.mjs 拉起) |
| `renderer/` | 零框架聊天 UI(index.html / style.css / renderer.js;能力面板/芯片/双弹框/媒体完成卡按 `artifact.kind` 内嵌图·视频·音频·转写 + 断链可见兜底) |
| `assets/` | demo 资产:本地示例 MCP 服务器(`mcp/demo-notes-server.mjs`,手写 stdio JSON-RPC)+ 目录技能(`skills/electron-tips/SKILL.md`) |
| `fixtures/` | 假图像/视频/聊天/搜索/MCP 上游验证夹具(非生产;可见占位 PNG + 可播占位 MP4 + 解码探针 + 收敛链/计量审计/模型收敛配额令牌/工具能力位/全能力演示 e2e 验收 + verify-dist 发布形态验证轨 + dev-world 交互演示常驻世界) |
| `build.mjs` | esbuild 多目标构建(main / smoke / advanced-api / permission-rtt / capability-askuser-rtt / demo-rtt / demo-e2e-rtt ESM,preload CJS) |

## 为什么有构建步骤

`@tansr/sdk` 发布产物是内联 dist(单文件 ESM + 单文件 d.ts;第三方运行时依赖 undici/zod 走常规 node_modules;workspace 开发态下则是 `src/index.ts` + kernel/protocol 源码,见「SDK 双轨」节)。Electron 主进程理论上可直接 `import '@tansr/sdk'`,但桌面应用发行恒要打包(asar 内 node_modules 解析、启动体积、依赖收敛),故本示例演示生产形态:esbuild 把应用源码连同 SDK 依赖图整体打进 `dist/`,只把 `electron` 留作 external(运行时由 Electron 提供)。换 electron-vite / webpack 均可,要点相同。SDK 要求 **Node ≥22.19 / Electron ≥39**(内嵌 Node ≥22);本示例锁定当前稳定大版本(Electron 44)。`tsconfig.json` 独立不依赖仓根,整目录拷走即可 `npm run typecheck`。

## 同轮追加输入

运行中仍可在输入框发送文字。Demo 使用 `getInputTarget()` 绑定当前轮，再以固定 `inputId` 调用 `submitInput({ target, content: { text }, ack: 'memory' })`，不会调用 stop 或重新创建会话。权限和提问弹窗内有独立补充输入区；它只提交文字，审批仍需单独答复。

`accepted` 表示内存接纳，`consumed` 表示正文已进入原轮历史，均不承诺模型已处理或落盘。回执只保留当前轮和最近结束轮。网络/IPC 错误时保留草稿；已结束目标返回 closed，不自动改用 send 开新轮。运行中仅接受文字，图片等内容应在空闲时发送。

本轮平台/serve/SDK 系统提示词快照、上下文和预算保持原轮语义，平台修改在下一新轮按既有刷新规则生效。同轮追加作为 user 内容，不能覆盖更高层系统指令或自动批准工具。

`npm run test:input` 执行真实 SDK 时序测试及隐藏 Electron 窗口的 DOM → IPC → SDK 验收。后者使用本机受控 HTTP 上游，检查原 turn、终态拒绝、权限补充区隔离和平台提示词快照；它不是手机真机或已发布安装包的验收。

## 排障

- **顶栏「连接失败:token-server 返回 HTTP 502 … token_exchange_failed / fetch failed」**:token-server 活着,但它背后的平台网关(缺省 `127.0.0.1:8787`)不在——要么网关没启动,要么 dev-memory 网关重启过(内存态,重启即新世界,token-server 手里的旧 appkey 已作废)。一键修复:收掉旧 token-server 进程,跑 `npm run dev-world`(重建世界 + 注入新 appkey),再重启本应用。
- **`npm start` 报 "Electron failed to install correctly"**:npm 装 electron 时 postinstall 未跑(常见于 npm 缓存元数据缺 `hasInstallScript` 标志的机器)。修复:`node node_modules/electron/install.js`(或 `npm rebuild electron`)后重跑。
- **生成的图片/视频不显示,终端刷 `SSL handshake failed … net_error -101`**:生成本身已成功(链接在完成卡里),是渲染器加载产物域(阿里 OSS 全球加速 `*.oss-accelerate.aliyuncs.com`)时 TLS 被**系统代理**重置——Electron 遵循系统代理,而该域走代理分流常被掐(2026-09-01 实录:curl 直连同域 0.1s 通)。修复:给代理客户端加 `*.aliyuncs.com` 直连规则,或测试时暂关系统代理;链接复制到浏览器若同样打不开,同因。
- **「画一张图」「生成视频」「读出来(TTS)」「联网搜索」芯片置灰 / 侧面板位灰**:App 能力位 `platform.imageGen` / `platform.videoGen` / `platform.textToSpeech` / `platform.webSearch` 未开(平台缺省档为关)。控制台开位后重启应用;媒体四工具还需平台侧生成池已配置、webSearch 需平台侧搜索商池已配置(未配置时工具返回 `imagegen_not_configured` / `videogen_not_configured` / `asr_not_configured` / `tts_not_configured` / `websearch_not_configured` 结构化错误)。历史上还有过一种置灰(位已开但 npm 旧版 `@tansr/sdk` 未携对应面,悬停注记可辨)——doc/128 D-1 起 demo 不再做版本探测,按「SDK 双轨」节切换依赖即可,不会再踩到。

## 渲染为什么恒经 SessionView

`session.events` 是完整事实流(约 40 种 KernelEvent)。直接消费它渲染,意味着每个应用都要重写增量拼接、工具生命周期状态机、轮次边界推导——SDK 已把这段做成纯函数投影器(`reduceSessionView`):事件进、可渲染快照出,快照不可变且 structuredClone-safe,恰好可以直接 `webContents.send` 过 IPC。renderer 收到就画,想接 React/Vue 也只是把快照塞进状态绑定。用户输入不经内核事件流,投影器提供显式入口 `appendUserMessage`。

能力矩阵 `pnpm test:capability` 默认从示例 manifest 解析实际安装的 registry SDK，日志显示 mode 与入口；原断言不变。`pnpm test:capability:candidate` 才显式选择本树 SDK。`TANSR_E2E_SKIP_SDK_BUILD=1` 只对候选构建步骤有效，不切换默认来源。

English: the default capability matrix resolves the installed registry SDK from the demo manifest and reports its source. Use `pnpm test:capability:candidate` for explicit local SDK validation; skipping the candidate build does not change the default registry source.


## MEDIA-03 历史媒体恢复（开发候选）

媒体产物随工具回执保存，恢复后的 result 可复用现有媒体组件；artifactUnavailable 单独表示预览不可恢复，不代表工具失败。首次写产物会启用存储版本 2，请统一升级写者并先备份。配置、TS 示例及英文说明见[媒体历史恢复 / Media history recovery](../serve-demo/MEDIA-HISTORY.md)。候选尚未正式发布。
