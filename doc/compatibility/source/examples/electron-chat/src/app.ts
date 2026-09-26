/**
 * electron-chat 应用接线(全能力演示)— main.ts 与端到端探针共用的同一份
 * 装配(测的即跑的,permission-bridge 同律)。
 *
 * 进程分工(为什么这么分):
 * - 主进程(本文件):持令牌、跑 @tansr/sdk 会话、执行 defineTool 业务函数、
 *   裁决权限、桥接提问对话框——一切接触平台、密钥、系统资源的事都在这一侧;
 * - renderer(renderer/):纯 UI。经 contextBridge 白名单通道收「SessionView
 *   状态快照 / 叙述行 / 能力面板数据」、发「用户输入 / 权限与提问答复 /
 *   切模与设置」。nodeIntegration 恒关、contextIsolation 恒开:令牌恒不进
 *   renderer,它只见投影状态。
 *
 * 能力装配纪律(doc/84 §4.1/§4.3;doc/128 媒体能力内核化):**一切以 bundle 能力位为准**——
 * - 环2 内置工具:demo 显式列名 `tools.builtin` = SDK 缺省全集(resolveBuiltinSelection
 *   按 tools.<name> 位过滤;webSearch 再按 platform.webSearch 通道位)+ 媒体四名
 *   imageGen / videoGen / speechToText / textToSpeech **按 platform.<name> 位列名**
 *   (doc/128 D-M3/D-M4:媒体四工具 = kernel 环2 工具 + 平台提供方,工具位 =
 *   capabilities.platform.<name>;缺省全集档恒不装四工具,须显式列名;位关而列名
 *   → 装配期 capability_disabled)。面板用同一函数算出装配集给用户看,「位关时
 *   装了什么/没装什么」一眼可见;旧的 `platform` 选择键已退为 deprecated 别名,demo 不再用;
 * - 环1 defineTool / skills / MCP:位开才递交材料,位关不递交(位关而硬塞材料会
 *   得到装配期 capability_disabled 可读错——demo 保留一层兜底捕获,防「拉档与装配
 *   之间控制台改位」的窄窗竞态);
 * - Task(agent 位):位开由 SDK 自动附着,无显式选择面。
 *
 * 数据流(单向;SDK 0.10.0+ 事件流系多消费者广播,两订阅者各自 for-await):
 *   session.events ── 泵(本文件,订阅者①)──▶ reduceSessionView(层1)──▶ 'chat:state'
 *   session.events ── createNarrator(层2,订阅者②)──────────────────▶ 'chat:narrator'
 *   (旧版需手工扇出的样板已退役——doc/90 §9.2 同笔勘正。)
 */
import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { writeFile } from 'node:fs/promises';
import { BrowserWindow, app, ipcMain, dialog } from 'electron';
import {
  HEADER_APP_TOKEN,
  TansrSdkError,
  appendUserMessage,
  createMcpHost,
  createNarrator,
  createSession,
  defineSkill,
  defineTool,
  initialSessionViewState,
  parseMediaArtifact,
  reduceSessionView,
  resolveBuiltinSelection,
  setSessionViewDelivery,
  stripThinkingParts,
} from '@tansr/sdk';
import type {
  AgentSession,
  AppBundle,
  AppBundlePlatformModels,
  AppCapabilities,
  McpHost,
  Narrator,
  PlatformWarning,
  SessionViewReducerState,
} from '@tansr/sdk';
import { fetchBundleForApp } from './bundle.js';
import { createPermissionBridge } from './permission-bridge.js';
import { createQuestionBridge } from './question-bridge.js';
import { createHistoryStore, historyScope, loadSettings, projectRestoredTranscript, saveSettings } from './persistence.js';
import { fetchDemoToken } from './token.js';
import { createDemoPlatformFetch, createDemoTokenManager } from './token-manager.js';
import { MediaFiles, MAX_INPUT_BYTES, decodeMediaData, readSelectedFile, readMediaEntry } from './media.js';
import { ImageDrafts } from './image-input.js';
import { SpeechDrafts, type SpeechInputLimit } from './speech.js';
import { mediaInvokeError } from './media-error.js';
import { selectedHandle, supportsImageInput, type ObservableSession } from './observation.js';
import { t } from './demo-messages.js';
import { projectViewForRenderer } from './view.js';
import { buildDemoSystem, demoSystemFromEnv } from './system.js';
import type { ApplicationPromptInfo, IRSystemSegment } from '@tansr/sdk';
import { closeDemoSession } from './lifecycle.js';
import { submitDemoInput, type DemoInput } from './input.js';
import type { SessionInputTarget } from '@tansr/sdk';

/** 呈现档现值(demo 侧持份,进 info 面板;viewState.internal.delivery 为准绳同源) */
export interface DemoViewDelivery {
  text: 'stream' | 'final';
  thinking: 'stream' | 'final' | 'off';
}

// ———————————————————————— 媒体四工具(doc/128 媒体能力内核化) ————————————————————————
// 图像 / 视频 / ASR / TTS 自 2026-09-05 起是 kernel 环2 工具 ImageGen / VideoGen /
// SpeechToText / TextToSpeech + `@tansr/sdk` 平台提供方(令牌闭包打 /t1/{imagegen,
// videogen,asr,tts}),装配键 = `tools.builtin` 媒体四名,工具位 = `capabilities.platform.<name>`。
// 产物 `data` 形逐字不变(ImageGenData / VideoGenData / TranscriptData / SpeechData),
// 形状判别单源 = protocol `parseMediaArtifact`(doc/123 §3.7 三端形状规则的 TS 参考实现)。

/** 媒体四名(= sdk `tools.builtin` 媒体子集 = `AppCapabilities.platform` 同名四位;序 = SDK 装配序) */
export const MEDIA_TOOL_NAMES = ['imageGen', 'videoGen', 'speechToText', 'textToSpeech'] as const;
export type MediaToolName = (typeof MEDIA_TOOL_NAMES)[number];

/**
 * 本会话 `tools.builtin` 显式列名(buildSession 与面板同一判据;装配集恒与真实装配一致):
 * - local:SDK 缺省全集(`resolveBuiltinSelection(undefined, caps)` 按 tools.<name> 位过滤本地
 *   工具;媒体四名恒不在其中,D-M4)。webSearch 再按 platform.webSearch 通道位过滤——缺席
 *   选择时 SDK 对通道位关是静默不装,显式列名时则 fail-fast,demo 复刻缺省档的静默语义;
 * - media:媒体四名按 `caps.platform.<name>` 位列名(位开即列;位关不列 = 侧面板位灰 +
 *   芯片置灰注明位名,降级在输入口可见)。
 * 递交 SDK 的即 `[...local, ...media]`(SDK 按固定装配序重排:… → http → ImageGen → VideoGen →
 * SpeechToText → TextToSpeech)。
 */
export interface DemoBuiltinSelection {
  local: string[];
  media: MediaToolName[];
}

export function builtinSelectionOf(caps: AppCapabilities): DemoBuiltinSelection {
  return {
    local: resolveBuiltinSelection(undefined, caps).filter((name) => name !== 'webSearch' || caps.platform.webSearch),
    media: MEDIA_TOOL_NAMES.filter((name) => caps.platform[name]),
  };
}

/** 授权媒体模型条目(bundle platformModels 行的面板投影:只带呈现需要的两键) */
export interface DemoMediaModel {
  model: string;
  displayName: string;
  maxChars?: number;
  inputLimit?: SpeechInputLimit;
}
/**
 * 授权媒体模型集(bundle `platformModels` 四键恒在场;序 = 平台收敛稳定序,顺位第一 =
 * model 缺省生效模型;空数组 = 平台未配置)。
 */
export interface DemoPlatformModels {
  imageGen: DemoMediaModel[];
  videoGen: DemoMediaModel[];
  speechToText: DemoMediaModel[];
  textToSpeech: DemoMediaModel[];
}

function mediaModelsOf(pm: AppBundlePlatformModels): DemoPlatformModels {
  const project = (rows: readonly { model: string; displayName: string }[]): DemoMediaModel[] =>
    rows.map((m) => ({ model: m.model, displayName: m.displayName }));
  return {
    imageGen: project(pm.imageGen),
    videoGen: project(pm.videoGen),
    speechToText: project(pm.speechToText),
    textToSpeech: pm.textToSpeech.map(m => ({ model: m.model, displayName: m.displayName,
      ...(m.constraints?.maxChars === undefined ? {} : { maxChars: m.constraints.maxChars }),
      inputLimit: ttsInputLimitOf(m.constraints) })),
  };
}

/** 可移植示例仍可由旧 npm 类型检查；新帽由新版 SDK 解析和 planner 实际守护。 */
function ttsInputLimitOf(constraints: unknown): SpeechInputLimit | undefined {
  return (constraints as { inputLimit?: SpeechInputLimit } | undefined)?.inputLimit;
}

/**
 * renderer 收到的工具卡 part = SDK `UIToolCallPart` + demo 加法键 `artifact`:completed 卡的
 * `result`(ToolResult.data 透传位)经 `parseMediaArtifact` 的判别结果(image / video /
 * transcript / speech;非媒体产物与错误回执恒 null)。renderer 是裸 JS 不经打包、不能 import
 * sdk,产物形状判别恒只在主进程这一处——renderer 按 `artifact.kind` 分派渲染,恒不再嗅探
 * `images` / `videos` / `audio.mime` 之类的形状键。
 */
export { projectViewForRenderer } from './view.js';
export type { DemoToolCallPart, DemoMessagePart, DemoMessage, DemoViewState } from './view.js';

// ———————————————————————— 配置(env 可覆盖) ————————————————————————

export interface ChatAppConfig {
  /** 示例宿主业务段；未设用平台默认，[] 的行为由平台策略决定。 */
  system?: IRSystemSegment[];
  /** 你的服务端(令牌换发面;见 examples/token-server) */
  tokenServer: string;
  /** tansr 平台 API 基址(本地联调指向 dev 网关;生产换正式域名) */
  apiBase: string;
  // S-G1(2026-08-30)起媒体生成模型名零手配:授权媒体集随 bundle platformModels
  // 下发(顺位第一 = 缺省),TANSR_IMAGE_MODEL / TANSR_VIDEO_MODEL 双 env 退役。
  /** 起始对话模型;缺省取 bundle 别名 main / 目录首行 */
  chatModel?: string;
  /** 令牌直给(跳过 token-server;冒烟/夹具用,生产恒走换发) */
  appToken?: string;
  /** 探针可设 false(无头跑) */
  show?: boolean;
}

export function configFromEnv(): ChatAppConfig {
  const system = demoSystemFromEnv(process.env.TANSR_DEMO_SYSTEM);
  return {
    ...(system !== undefined ? { system } : {}),
    tokenServer: process.env.TANSR_TOKEN_SERVER ?? 'http://127.0.0.1:8788',
    apiBase: process.env.TANSR_API_BASE ?? 'https://api.tansr.com',
    ...(process.env.TANSR_CHAT_MODEL !== undefined ? { chatModel: process.env.TANSR_CHAT_MODEL } : {}),
    ...(process.env.TANSR_APP_TOKEN !== undefined ? { appToken: process.env.TANSR_APP_TOKEN } : {}),
  };
}

/** 能力面板载荷('chat:info';renderer 纯展示,结构在此一处定型) */
export interface DemoInfoPayload {
  /** App 能力位(17 工具位 + 5 平台位;bundle 下发,控制台治理) */
  capabilities: AppCapabilities;
  /**
   * 本会话真实装配集(与 buildSession 同一 builtinSelectionOf 算出,恒与装配一致):
   * builtin = 递交 SDK 的完整系统工具集；media = 其中的媒体子集，保留供快捷入口识别。
   * 媒体仍按 platform.<name> 授权字段装配，字段不改变其系统工具归属。
   */
  assembly: {
    builtin: string[];
    custom: string[];
    media: MediaToolName[];
    task: boolean;
    skill: boolean;
    mcp: boolean;
  };
  /**
   * bundle 模型目录(对话模型;切模选择集)。manufacturer/family 为模型呈现
   * 面二波(2026-08-30 ④)两维:切换器按厂商 optgroup 分组呈现;旧 bundle
   * 无键 = null(切换器回落平铺,恒不断链)。
   */
  models: { handle: string; displayName: string; manufacturer: string | null; family: string | null }[];
  currentModel: string;
  selectedAlias: string;
  context: unknown;
  imageInput: boolean;
  /** 当前会话最近一轮实际采用的来源；装配中为 null，不根据预读 bundle 猜测。 */
  systemPrompt: { source: ApplicationPromptInfo['source'] | null };
  /**
   * 授权媒体模型集(S-G1;bundle platformModels 四键自取):顺位第一 = model 缺省
   * 生效模型;空数组 = 平台未配置(工具装配照旧,调用回 *_not_configured 结构化错)。
   */
  media: DemoPlatformModels;
  persistence: boolean;
  /** 注册进本会话的技能(位关时仍列出,UI 置灰示「位关未装」) */
  skills: { name: string; source: 'inline' | 'dir'; description: string }[];
  /** MCP 服务器观测(createMcpHost onEvent 累计;位关恒空) */
  mcpServers: { name: string; state: string; tools: string[] }[];
  /** 终端自查用量(/v1/my-usage 1d;schema 恒无金额)。null = 未取到 */
  usage: {
    endUserId: string;
    requests: number;
    inTokens: number;
    outTokens: number;
    cacheRTokens: number;
    cacheWTokens: number;
  } | null;
  /** 配额注记:限额恒在网关侧执行,bundle 不下发数值(撞限时 429 detail 可见) */
  quotaNote: string;
  /** 视图呈现档(S-V1 delivery;setSessionViewDelivery 动态切换,块粒度生效) */
  viewDelivery: { text: string; thinking: string; note: string };
  /** 思考生成(生成面旋钮,session.setThinking;与呈现档 viewDelivery 正交;开关下一轮生效) */
  thinkingGen: { on: boolean; note: string };
  paths: { sandbox: string; historyFile: string };
}

export interface ChatApp {
  getWin(): BrowserWindow | null;
  getSession(): AgentSession | null;
  /** 首次连接完成(phase=ready)即 resolve;致命失败 reject(探针等待用) */
  whenReady(): Promise<void>;
  paths: { userData: string; sandbox: string; readonly historyFile: string };
  /** 应用收口:关会话 + 断 MCP 连接(window-all-closed 处调用) */
  dispose(): Promise<void>;
}

// ———————————————————————— 应用装配 ————————————————————————
// 事件消费范式(SDK 0.10.0+):AgentSession.events 系多消费者广播——视图泵与 Narrator
// 各自 for-await 同一会话即可(send() 前挂齐,全员同流);旧版 demo 的手工扇出退役。

export function startChatApp(config: ChatAppConfig): ChatApp {
  const userData = app.getPath('userData');
  /** 文件/命令类工具的演示沙箱(会话 cwd):不碰用户真实目录,重启保留 */
  const sandbox = path.join(userData, 'demo-workspace');
  const skillsDir = fileURLToPath(new URL('../assets/skills', import.meta.url));
  const mcpServerPath = fileURLToPath(new URL('../assets/mcp/demo-notes-server.mjs', import.meta.url));

  mkdirSync(sandbox, { recursive: true });
  // 沙箱播种(幂等):read/glob/grep/edit 的演示素材,首次启动即有东西可读
  const seedReadme = path.join(sandbox, 'README.txt');
  if (!existsSync(seedReadme)) {
    writeFileSync(
      seedReadme,
      'tansr 演示沙箱\n这里是 electron-chat 全能力演示的工作目录:文件/命令类工具都在本目录内操作。\n试试让智能体读取本文件、检索 notes.md、或写一个新文件。\n',
      'utf8',
    );
  }
  const seedNotes = path.join(sandbox, 'notes.md');
  if (!existsSync(seedNotes)) {
    writeFileSync(seedNotes, '# 演示笔记\n\n- tansr SDK 集成走查\n- TODO: 把这行改成 DONE(Edit 工具演示素材)\n', 'utf8');
  }

  let win: BrowserWindow | null = null;
  let session: AgentSession | null = null;
  const ownedSessions = new Set<AgentSession>();
  let disposing = false;
  let disposeWork: Promise<void> | undefined;
  let connectWork: Promise<void> | undefined;
  let narrator: Narrator | null = null;
  let mcpHost: McpHost | null = null;
  let bundle: AppBundle | null = null;
  let token = '';
  let activeScope: string | null | undefined;
  let acceptedOpaqueToken: string | undefined;
  const tokens = createDemoTokenManager({
    staticToken: config.appToken,
    mint: (signal) => fetchDemoToken(config.tokenServer, { signal }),
  });
  const platformFetch = createDemoPlatformFetch(config.apiBase, tokens, fetch, (currentToken) => {
    if (activeScope !== undefined && (historyScope(config.apiBase, config.tokenServer, currentToken) !== activeScope
      || (activeScope === null && acceptedOpaqueToken !== currentToken))) throw new Error(t('history_identity_changed'));
    token = currentToken;
  });
  let currentModel = config.chatModel ?? '';
  let settings = loadSettings(userData);
  let store = createHistoryStore(userData);
  let lastUsage: DemoInfoPayload['usage'] = null;
  /** MCP 观测累计(onEvent → 面板;bridged 工具名以 expand 相为准) */
  const mcpState = new Map<string, { state: string; tools: string[] }>();
  const mediaFiles = new MediaFiles();
  const imageDrafts = new ImageDrafts();
  const speechDrafts = new SpeechDrafts();
  let contextUnsubscribe: (() => void) | undefined;
  const artifactRoot = path.join(userData, 'media-artifacts');
  mkdirSync(artifactRoot, { recursive: true });
  const allowedMediaHosts = (process.env.TANSR_DEMO_MEDIA_HOSTS ?? '').split(',').map((host) => host.trim().toLowerCase()).filter(Boolean);
  let mediaCall: AbortController | null = null;

  // ———————————— 环1:defineTool 业务函数样板 ————————————
  // 「注册即说明书」:description 同时是模型的调用依据和人读文档。

  /** 只读工具(readOnly: true)→ 缺省权限模式下静默放行,体验零打扰 */
  const getAppInfo = defineTool({
    name: 'getAppInfo',
    description:
      '返回本应用的版本与运行环境信息(Electron/Node/Chromium 版本、操作系统)。' +
      '用户询问「应用信息 / 版本 / 运行环境」时调用。',
    readOnly: true,
    handler: () => ({
      name: 'tansr electron-chat 示例',
      appVersion: app.getVersion(),
      electron: process.versions.electron,
      node: process.versions.node,
      chrome: process.versions.chrome,
      platform: `${process.platform}-${process.arch}`,
    }),
  });

  /**
   * 有副作用的工具(readOnly 缺省 false)→ 缺省权限模式下每次调用触发
   * ask → 权限桥 → renderer 确认框。demo 特意保留「要授权」的工具,让集成者
   * 第一次跑就看到完整权限链。
   */
  const setWindowTitle = defineTool<{ title: string }>({
    name: 'setWindowTitle',
    description: '把聊天窗口标题改为指定文本。用户要求「改标题 / 给窗口命名」时调用。',
    parameters: { title: { type: 'string', description: '新标题(简短、无换行)' } },
    guide: 'setWindowTitle 会改动用户可见的窗口状态:先向用户说明再调用;标题保持 30 字以内。',
    handler: ({ title }) => {
      win?.setTitle(title);
      return `窗口标题已改为「${title}」`;
    },
  });

  // ———————————— skills:defineSkill 内联 + 目录装载 各一例 ————————————
  // 与 defineTool 的分工:defineTool 注册可执行的动作,defineSkill 注入按需
  // 装载的领域知识——轻量索引进 system,正文在智能体点名调用 Skill 时才进
  // 上下文。两例的正文都带「口令」行:技能被真实装载时模型会复述口令,
  // 用户(和探针)一眼可证「正文真进了上下文」,不是索引在装样子。

  const inlineSkill = defineSkill({
    name: 'session-summary',
    description: '把当前会话的讨论要点压缩成三行以内的摘要,并给出下一步建议。',
    whenToUse: '用户说「总结一下 / 收个尾 / 给个摘要」时装载',
    instructions: [
      '# session-summary(内联注入的技能正文)',
      '装载确认口令:INLINE-SKILL-LOADED(先向用户复述本口令,证明正文已进上下文)。',
      '',
      '摘要步骤:',
      '1. 通读本会话,提炼至多三条要点,每条一行、前缀「·」;',
      '2. 最后一行以「下一步:」开头,给一个具体可执行的建议;',
      '3. 不复述工具调用细节,只讲结论。',
    ].join('\n'),
  });
  /** 目录技能名(assets/skills/<name>/SKILL.md;README 目录表登记) */
  const DIR_SKILL_NAME = 'electron-tips';

  // ———————————— 桥:权限确认 + 提问对话框(两条独立注入缝) ————————————

  const permissionBridge = createPermissionBridge(() => win);
  const questionBridge = createQuestionBridge(() => win);

  // ———————————— 渲染管道:SessionView 投影(层1)→ IPC 快照推送 ————————————
  // 渲染恒经 SDK 投影器,renderer 恒不手拼 KernelEvent。这里用投影器的纯函数
  // 形态(reduce + appendUserMessage)自建泵,便于同一循环里给 Narrator 扇出。

  let viewState: SessionViewReducerState = initialSessionViewState();
  const userImages = new Map<string, string[]>();

  // 呈现档现值(缺省全流式 = initialSessionViewState 缺省同值;切换见
  // 'chat:set-delivery' 通道,块粒度生效恒不追溯——thinking 'off' 例外做
  // 追溯剔除,视图不变量「off ⇒ 恒无思考」)
  let delivery: DemoViewDelivery = { text: 'stream', thinking: 'stream' };

  // 思考生成(生成面):session.setThinking 动态换,下一轮生效;呈现面显隐
  // 走上面的 delivery.thinking,两旋钮正交。缺省关 = 模型缺省(不注思考)。
  const THINKING_BUDGET = 4096;
  // S-TH5:开关随 settings.json 持久(重启恢复;此前内存态重启归零,
  // 用户勾过即忘、思考档位悄然不出线的实测坑)
  let thinkingOn = settings.thinking;

  const inputRows = new Map<string, SessionInputTarget>();
  function publishView(): void {
    // 快照过 IPC 前经 projectViewForRenderer:completed 工具卡附 parseMediaArtifact 判别结果
    // (renderer 只认 artifact.kind 分派媒体渲染,形状判别恒在主进程单源)
    win?.webContents.send('chat:state', {
      ...projectViewForRenderer(viewState.view, mediaFiles),
      userImages: Object.fromEntries(userImages),
      inputTarget: session?.getInputTarget() ?? null,
      inputReceipts: [...inputRows].map(([inputId, target]) => session?.getInputStatus(inputId, target)).filter(Boolean),
    });
  }

  function sendPhase(phase: string, detail?: string): void {
    win?.webContents.send('chat:phase', { phase, detail });
  }

  async function pumpEvents(current: AgentSession): Promise<void> {
    // 视图泵 = 会话事件流的一个订阅者(Narrator 另起订阅,SDK 广播队列各得全量)
    for await (const event of current.events) {
      // SDK 已在轮开始前读取平台角色/策略；从会话实时元数据展示本轮来源。
      if (event.type === 'turn.started' && current === session) publishInfo();
      const next = reduceSessionView(viewState, event);
      if (next !== viewState) {
        // 视图引用去重(SDK 泵壳同律):'final' 档缓冲累积只动内部簿记,
        // 快照未变恒不过 IPC(renderer 零白渲染)
        const viewChanged = next.view !== viewState.view;
        viewState = next;
        if (viewChanged) publishView();
      }
      if (event.type === 'turn.completed' || event.type === 'turn.aborted') {
        void refreshUsage(); // 终端自查面随轮刷新(失败静默,面板保留旧值)
      }
    }
  }

  // ———————————— 平台面:bundle / my-usage ————————————

  /** FX-C-14(Q2-04):经 sdk fetchAppBundle 恒携 x-tansr-client-features(bundle.ts;缺头 = 旧 SDK 分档) */
  function fetchBundle(): Promise<AppBundle> {
    return fetchBundleForApp(config.apiBase, token, platformFetch);
  }

  async function refreshUsage(): Promise<void> {
    try {
      const res = await platformFetch(`${config.apiBase}/v1/my-usage?window=1d`, {
        headers: { [HEADER_APP_TOKEN]: token },
      });
      if (!res.ok) return;
      lastUsage = (await res.json()) as DemoInfoPayload['usage'];
      publishInfo();
    } catch {
      // 用量自查是旁路展示面:取不到不打扰主流程
    }
  }

  // ———————————— MCP:应用级 host(跨会话复用连接,退出 dispose) ————————————

  function ensureMcpHost(): McpHost {
    mcpHost ??= createMcpHost({
      servers: {
        demo: {
          transport: 'stdio',
          command: process.execPath,
          args: [mcpServerPath],
          // Electron 二进制当 Node 用的官方开关:MCP 子进程是纯 Node 脚本
          env: { ELECTRON_RUN_AS_NODE: '1' },
          // eager:装配期全量展开桥接工具(demo 的目录只有 3 个工具,直接
          // 可见比懒加载目录段更直观;大目录场景删掉本行走 McpDiscover 按需展开)
          load: 'eager',
        },
      },
      clientInfo: { name: 'tansr-electron-chat', version: app.getVersion() },
      onEvent: (event) => {
        // 结构化观测缝 → 面板(连接状态/桥接工具名可视)
        if (event.type === 'mcp.connected') {
          mcpState.set(event.server, { state: '已连接', tools: mcpState.get(event.server)?.tools ?? [] });
        } else if (event.type === 'mcp.connect_failed') {
          mcpState.set(event.server, { state: `连接失败:${event.message}`, tools: [] });
        } else if (event.type === 'mcp.closed') {
          mcpState.set(event.server, { state: '已断开', tools: mcpState.get(event.server)?.tools ?? [] });
        } else if (event.type === 'mcp.discovered') {
          const entry = mcpState.get(event.server) ?? { state: '已连接', tools: [] };
          // expand 相给的是桥接名(mcp__demo__*,与模型可见面一致),优先展示
          if (event.phase === 'expand' || entry.tools.length === 0) entry.tools = event.tools;
          mcpState.set(event.server, entry);
        }
        publishInfo();
      },
    });
    return mcpHost;
  }

  // ———————————— 会话装配(平台令牌档;能力位驱动) ————————————

  async function buildSession(caps: AppCapabilities): Promise<AgentSession> {
    const selection = builtinSelectionOf(caps);
    const appBundle = bundle as AppBundle;
    const prompt = buildDemoSystem(caps, selection, appBundle.platformModels);
    const current = await createSession({
      // 令牌档:模型目录与能力位由平台 App 配置(bundle)供给,本地零配置
      token,
      fetchImpl: platformFetch,
      baseUrl: config.apiBase,
      model: currentModel,
      cwd: sandbox,
      tools: {
        // 显式列名 = SDK 缺省全集(本地环2,按位过滤)+ 媒体四名按 platform.<name> 位
        // (doc/128 D-M4:缺省全集档恒不装媒体四工具,必须显式列名;位关不列 = 静默不装,
        // 面板用同一函数展示装配集,降级可见)。旧的 `platform` 选择键已 deprecated,恒不再用。
        builtin: [...selection.local, ...selection.media],
        ...(caps.tools.customTools ? { custom: [getAppInfo, setWindowTitle] } : {}),
      },
      // skills / mcp 位开才递交材料(位关硬塞会得到装配期 capability_disabled)
      ...(caps.tools.skills ? { skills: { custom: [inlineSkill], dirs: [skillsDir] } } : {}),
      ...(caps.tools.mcp ? { mcp: ensureMcpHost() } : {}),
      promptChannel: questionBridge.channel,
      permission: { askUser: permissionBridge.askUser },
      // 指南不触发平台默认覆盖；SDK 真正装配后回报来源，不根据预读 bundle 猜测。
      ...(config.system !== undefined ? { system: config.system } : {}),
      systemAppend: prompt,
      // 同一SessionStore在内存常驻；原生resume同时保全逻辑ID、平台ULID及完整历史。
      ...store.sessionOptions(),
      // S-ARCH 平台提示帧上屏:thinking_unavailable_on_face 等结构化告知走 Narrator 行,
      // 用户可见「为什么这次没有思考卡」——告知恒不静默蒸发
      onPlatformWarning: (w: PlatformWarning) => {
        win?.webContents.send('chat:narrator', `⚠ 平台提示 [${w.code}] ${w.message}`);
      },
    });
    ownedSessions.add(current);
    return current;
  }

  /**
   * 装配 + 竞态兜底:bundle 拉档与 createSession 之间控制台改位的窄窗里,
   * 装配可能 capability_disabled——重拉一次最新档按新位重建(恒不静默吞错,
   * 第二次仍失败则如实上抛)。
   */
  async function buildSessionWithRetry(): Promise<AgentSession> {
    const caps = (bundle as AppBundle).capabilities;
    try {
      return await buildSession(caps);
    } catch (err) {
      if (!(err instanceof TansrSdkError) || err.code !== 'capability_disabled') throw err;
      console.warn(`[electron-chat] 装配期能力位竞态,按最新 bundle 重建:${err.message}`);
      bundle = await fetchBundle();
      return buildSession(bundle.capabilities);
    }
  }

  /** 会话就位后的公共接线:视图泵 + Narrator(层2 叙述喷口)——同一事件流的两个订阅者 */
  function wireSession(current: AgentSession): void {
    contextUnsubscribe?.();
    contextUnsubscribe = (current as ObservableSession).subscribeContext?.(() => {
      if (current === session) publishInfo();
    });
    // 思考生成态跨进程恢复保全；持久化开关不重建会话。
    if (thinkingOn) current.setThinking({ budget: THINKING_BUDGET });
    // 两个订阅者在首轮 send() 前挂齐 → 各得会话创建以来全量(SDK 广播队列直播前语义)
    void pumpEvents(current);
    narrator = createNarrator(current.events, {
      // verbose:轮次/工具/权限/usage/子代理生命周期全出行(演示三层喷口的
      // 「审计留痕」受众;生产日志常用 normal)
      verbosity: 'verbose',
      onLine: (line) => win?.webContents.send('chat:narrator', line),
    });
  }

  // ———————————— 能力面板数据('chat:info') ————————————

  function publishInfo(): void {
    if (win === null || bundle === null) return;
    const caps = bundle.capabilities;
    // 面板呈现与 buildSession 同一 builtinSelectionOf 判据(装配集恒与真实装配一致,恒不另算一套)
    const selection = builtinSelectionOf(caps);
    const payload: DemoInfoPayload = {
      capabilities: caps,
      assembly: {
        builtin: [...selection.local, ...selection.media],
        custom: caps.tools.customTools ? ['getAppInfo', 'setWindowTitle'] : [],
        media: selection.media,
        task: caps.tools.agent,
        skill: caps.tools.skills,
        mcp: caps.tools.mcp,
      },
      models: bundle.models.map((m) => ({
        handle: m.handle,
        displayName: m.displayName,
        manufacturer: m.manufacturer,
        family: m.family,
      })),
      currentModel: selectedHandle(bundle, currentModel),
      selectedAlias: currentModel,
      context: (session as ObservableSession | null)?.contextState?.() ?? null,
      imageInput: supportsImageInput(bundle, currentModel),
      systemPrompt: {
        source: session?.applicationPrompt.source ?? null,
      },
      media: mediaModelsOf(bundle.platformModels),
      persistence: store.persistent,
      skills: [
        { name: inlineSkill.name, source: 'inline', description: '内联注入(defineSkill;正文驻内存零盘面)' },
        { name: DIR_SKILL_NAME, source: 'dir', description: '目录装载(assets/skills/electron-tips/SKILL.md)' },
      ],
      mcpServers: [...mcpState.entries()].map(([name, s]) => ({ name, state: s.state, tools: s.tools })),
      usage: lastUsage,
      quotaNote:
        '配额(rpm/日 token)在网关侧硬执行,限额数值不随 bundle 下发终端;撞限时响应 429 携 limit/used/resetAt。',
      viewDelivery: {
        text: delivery.text,
        thinking: delivery.thinking,
        note: '块粒度生效:开启中的块按其起始档收尾,新块即用新档;「隐藏」会即刻剔除既有思考(切回只影响后续)。',
      },
      thinkingGen: {
        on: thinkingOn,
        note: thinkingOn
          ? `已开(budget ${THINKING_BUDGET};下一轮生效)。思考的显示与否走上面「思考呈现」档,两旋钮正交。`
          : '关(模型缺省,一般不产思考)。开后下一轮生效,需模型支持思考(如 qwen 系);产生的思考按上面「思考呈现」档显示。',
      },
      paths: { sandbox, historyFile: store.historyPath },
    };
    win.webContents.send('chat:info', payload);
  }

  // ———————————— 连接 / 重建 ————————————

  async function connect(): Promise<void> {
    sendPhase('connecting', config.appToken !== undefined ? '使用环境变量令牌…' : '正在向 token-server 换取令牌…');
    token = await tokens.ensureFresh();

    sendPhase('connecting', '正在拉取 App 能力档(bundle)…');
    bundle = await fetchBundle();
    if (currentModel === '') {
      // 缺省对话模型:bundle 显式别名 main 优先,否则目录首行
      currentModel = bundle.aliases['main'] !== undefined ? 'main' : (bundle.models[0]?.handle ?? 'main');
    }

    // 仅在平台已接受当前令牌后确定磁盘作用域；短期token/jti不参与分域。
    activeScope = historyScope(config.apiBase, config.tokenServer, token);
    acceptedOpaqueToken = activeScope === null ? token : undefined;
    store = createHistoryStore(userData, activeScope, settings.persistence);
    if (activeScope === null) win?.webContents.send('chat:narrator', t('history_identity'));
    const legacy = store.legacy();
    if (legacy && win) {
      const choice = await dialog.showMessageBox(win, {
        type: 'question', message: t('history_legacy_question'), detail: t('history_legacy_detail'),
        buttons: [t('history_legacy_skip'), t('history_legacy_import')], defaultId: 0, cancelId: 0,
      });
      if (disposing) return;
      if (choice.response === 1) store.stageLegacy(legacy);
    }
    const restored = store.latest()?.messages ?? store.sessionOptions().initialMessages ?? [];

    sendPhase('connecting', '正在装配会话(能力位驱动)…');
    session = await buildSessionWithRetry();
    wireSession(session);

    if (restored.length > 0) {
      win?.webContents.send('chat:restored', projectRestoredTranscript(restored));
    }
    publishInfo();
    void refreshUsage();
    publishView();

    const caps = (bundle as AppBundle).capabilities;
    const allBits = [...Object.values(caps.tools), ...Object.values(caps.platform)];
    const onBits = allBits.filter(Boolean).length;
    sendPhase(
      'ready',
      `能力位 ${onBits}/${allBits.length} 开;对话模型 ${currentModel}${restored.length > 0 ? `;已恢复 ${restored.length} 条历史` : ''}`,
    );
  }

  // ———————————— renderer 输入通道 ————————————

  // 媒体输入只由原生选择器提供文件；保存只接受已登记 id，不允许 renderer 递交路径或 URL。
  ipcMain.handle('media:pick-image', async (event) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame) throw new Error('无效的媒体请求来源');
    if (!session || !bundle) throw new Error(t('not_ready'));
    if (!supportsImageInput(bundle, currentModel)) throw new Error(t('image_unsupported'));
    const current = session;
    const picked = await dialog.showOpenDialog(win, { properties: ['openFile'], filters: [{ name: '图片', extensions: ['png', 'jpg', 'jpeg', 'webp'] }] });
    if (picked.canceled || !picked.filePaths[0]) return null;
    const image = await imageDrafts.add(picked.filePaths[0]);
    if (disposing || current !== session) { imageDrafts.remove([image.id]); throw new Error(t('image_invalid')); }
    return { ...image, name: path.basename(picked.filePaths[0]) };
  });

  ipcMain.handle('media:speech-plan', (event, payload: unknown) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame || !bundle || !session || disposing) throw new Error(t('not_ready'));
    const input = (payload ?? {}) as { text?: unknown; model?: unknown; segment?: unknown };
    if (typeof input.text !== 'string' || !bundle.capabilities.platform.textToSpeech) throw new Error(t('speech_invalid'));
    const model = input.model === undefined ? bundle.platformModels.textToSpeech[0] : bundle.platformModels.textToSpeech.find(m => m.model === input.model);
    if (!model) throw new Error(t('speech_invalid'));
    return speechDrafts.prepare(input.text, model.model, model.constraints?.maxChars, input.segment === true, ttsInputLimitOf(model.constraints));
  });
  ipcMain.handle('media:speech-status', (event, id: unknown) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame || typeof id !== 'string' || !session || disposing) throw new Error(t('not_ready'));
    return speechDrafts.status(id);
  });
  ipcMain.handle('media:audio', async (event, payload: unknown) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame) throw new Error('无效的媒体请求来源');
    if (disposing || !session || !bundle) throw new Error('会话尚未就绪');
    if (mediaCall) throw new Error('已有音频请求，请完成或取消后再试');
    const input = (payload ?? {}) as { action?: unknown; audio?: unknown; text?: unknown; planId?: unknown; index?: unknown };
    const action = input.action;
    if (action !== 'transcribe-file' && action !== 'transcribe' && action !== 'speak') throw new Error('未知音频操作');
    const capability = action === 'speak' ? 'textToSpeech' : 'speechToText';
    if (!bundle.capabilities.platform[capability]) throw new Error(`能力未启用：${capability}`);
    const current = session;
    const controller = new AbortController();
    mediaCall = controller;
    let speechSegment: { id: string; index: number } | undefined;
    try {
      let result;
      if (action === 'speak') {
        let id = input.planId;
        let index = input.index;
        if (typeof id !== 'string') {
          if (typeof input.text !== 'string') throw new Error(t('speech_invalid'));
          const model = bundle.platformModels.textToSpeech[0];
          if (!model) throw new Error(t('speech_invalid'));
          id = speechDrafts.prepare(input.text, model.model, model.constraints?.maxChars, false, ttsInputLimitOf(model.constraints)).id; index = 0;
        }
        if (typeof id !== 'string' || typeof index !== 'number') throw new Error(t('speech_invalid'));
        const segment = speechDrafts.begin(id, index);
        if (segment.cached) return segment.result;
        speechSegment = { id, index };
        result = await current.platform.speak({ input: segment.input, model: segment.model }, { signal: controller.signal });
      } else {
        let audio: string;
        if (action === 'transcribe-file') {
          const picked = await dialog.showOpenDialog(win, { properties: ['openFile'], filters: [{ name: '音频', extensions: ['wav', 'mp3', 'm4a', 'ogg', 'webm', 'flac'] }] });
          if (picked.canceled || !picked.filePaths[0]) return null;
          const file = await readSelectedFile(picked.filePaths[0], 'audio');
          audio = `data:${file.mime};base64,${file.bytes.toString('base64')}`;
        } else {
          if (typeof input.audio !== 'string') throw new Error('缺少录音');
          const decoded = decodeMediaData(input.audio, MAX_INPUT_BYTES);
          if (!decoded.mime.startsWith('audio/')) throw new Error('录音必须为音频');
          audio = input.audio;
        }
        controller.signal.throwIfAborted();
        result = await current.platform.transcribe({ audio }, { signal: controller.signal });
      }
      controller.signal.throwIfAborted();
      if (session !== current) throw new Error('会话已切换，请重新请求');
      const artifact = parseMediaArtifact(result);
      if (!artifact) throw new Error('平台未返回有效媒体产物');
      const output = { artifact, mediaFiles: mediaFiles.register(`direct-${randomUUID()}`, artifact) };
      if (speechSegment) speechDrafts.complete(speechSegment.id, speechSegment.index, output);
      return output;
    } catch (error) {
      if (speechSegment) speechDrafts.fail(speechSegment.id, speechSegment.index);
      throw mediaInvokeError(error);
    } finally { if (mediaCall === controller) mediaCall = null; void refreshUsage(); }
  });
  ipcMain.on('media:cancel', (event) => {
    if (win && event.sender === win.webContents && event.senderFrame === win.webContents.mainFrame) mediaCall?.abort();
  });
  ipcMain.handle('media:save', async (event, id: unknown) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame || typeof id !== 'string') throw new Error('无效的保存请求');
    const entry = mediaFiles.get(id);
    const chosen = await dialog.showSaveDialog(win, { defaultPath: entry.name });
    if (chosen.canceled || !chosen.filePath) return null;
    const bytes = await readMediaEntry(entry, { allowedHosts: allowedMediaHosts, artifactRoot });
    await writeFile(chosen.filePath, bytes);
    return path.basename(chosen.filePath);
  });
  ipcMain.handle('media:preview', async (event, id: unknown) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame || typeof id !== 'string') throw new Error('无效的预览请求');
    const entry = mediaFiles.get(id);
    if (!('localPath' in entry.source)) throw new Error('只有宿主本地产物需要此预览入口');
    const bytes = await readMediaEntry(entry, { artifactRoot });
    const uri = `data:${entry.mime};base64,${bytes.toString('base64')}`;
    decodeMediaData(uri, MAX_INPUT_BYTES);
    return uri;
  });

  ipcMain.handle('chat:submit', async (event, input: DemoInput) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame
      || disposing || session === null) return { outcome: 'closed', code: 'turn_closed' };
    const current = session;
    const duplicate = input?.target ? current.getInputStatus(input.inputId, input.target) : null;
    const images = imageDrafts.get(input?.imageIds ?? []);
    if (images.length && (!bundle || !supportsImageInput(bundle, currentModel))) throw new Error(t('image_unsupported'));
    const result = await submitDemoInput(current, input, images);
    if (current === session && (result.outcome === 'started' || result.outcome === 'accepted')) {
      if (!duplicate) {
        viewState = appendUserMessage(viewState, input.text);
        // 图片仍随SDK原始历史持久化；视图附加只负责本次展示。
        const last = viewState.view.messages.at(-1);
        if (last && images.length) userImages.set(last.id, images.map(image => `data:${image.mime};base64,${image.data}`));
      }
      imageDrafts.remove(input.imageIds ?? []);
      if (input.target) {
        inputRows.set(input.inputId, input.target);
        while (inputRows.size > 100) inputRows.delete(inputRows.keys().next().value!);
      }
      publishView();
    }
    return result;
  });

  // 保留旧演练脚本的兼容入口；正式界面使用有目标/回执的 chat:submit。
  ipcMain.on('chat:send', (_event, text: unknown) => {
    if (disposing || session === null || typeof text !== 'string' || text.trim() === '') return;
    // 用户消息不经内核事件流,投影器为此提供显式入口(appendUserMessage);
    // 先入投影再递交会话。运行中递交同样安全:SDK 经注入屏障在下一次模型
    // 调用前消费；严格同轮和终态结果由上方新入口提供。
    viewState = appendUserMessage(viewState, text);
    publishView();
    session.send(text);
  });

  ipcMain.on('chat:interrupt', () => {
    session?.interrupt();
  });

  // setModel 切模:对后续轮生效(运行中轮不受影响);别名/handle 经 bundle
  // registry 装配期重解析,失败会话状态不变(可读错回面板)
  ipcMain.on('chat:set-model', async (event, handle: unknown) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame
      || disposing || session === null || typeof handle !== 'string' || handle === '') return;
    const current = session as ObservableSession;
    try {
      if (!current.switchModel) { sendPhase('ready', t('sdk_upgrade')); publishInfo(); return; }
      sendPhase('ready', t('switching'));
      const result = await current.switchModel(handle);
      if (session !== current || disposing) return;
      if (result.status !== 'changed') { sendPhase('ready', t('switch_failed') + ` (${result.reason})`); publishInfo(); return; }
      currentModel = handle;
      publishInfo();
      sendPhase('ready', t('switched'));
    } catch {
      if (session === current && !disposing) { sendPhase('ready', t('switch_failed')); publishInfo(); }
    }
  });

  // 只切磁盘镜像，保留当前AgentSession、平台身份、权限、MCP、媒体草稿及事件订阅。
  ipcMain.on('chat:set-persistence', (event, on: unknown) => {
    if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame
      || disposing || typeof on !== 'boolean' || on === store.persistent || session === null) return;
    let failure: unknown;
    try { store.setPersistence(on, session.messages()); } catch (error) { failure = error; }
    settings = { ...settings, persistence: store.persistent };
    try { saveSettings(userData, settings); } catch (error) { failure ??= error; }
    publishInfo();
    sendPhase('ready', failure ? t('history_write_failed') : t(on ? 'history_enabled' : 'history_disabled'));
    if (failure) console.warn('[electron-chat] persistence:', failure instanceof Error ? failure.message : t('history_write_failed'));
  });

  ipcMain.on('chat:refresh-usage', () => {
    void refreshUsage();
  });

  // 呈现档动态切换(S-V1 delivery,用户拍板 2026-09-01「留切换入口随时切换」):
  // 块粒度生效——开启中的块按起始档收尾,新块即用新档;thinking 'off' 恒配
  // stripThinkingParts 追溯剔除(视图不变量:off ⇒ 视图恒无思考;内核历史不动)。
  ipcMain.on('chat:set-delivery', (_event, payload: unknown) => {
    const p = (payload ?? {}) as { text?: unknown; thinking?: unknown };
    const text = p.text === 'stream' || p.text === 'final' ? p.text : undefined;
    const thinking =
      p.thinking === 'stream' || p.thinking === 'final' || p.thinking === 'off' ? p.thinking : undefined;
    if (text === undefined && thinking === undefined) return;
    let next = setSessionViewDelivery(viewState, {
      ...(text !== undefined ? { text } : {}),
      ...(thinking !== undefined ? { thinking } : {}),
    });
    if (thinking === 'off') next = stripThinkingParts(next);
    if (next !== viewState) {
      const viewChanged = next.view !== viewState.view;
      viewState = next;
      if (viewChanged) publishView();
    }
    delivery = { text: text ?? delivery.text, thinking: thinking ?? delivery.thinking };
    publishInfo();
  });

  // 思考生成开关(生成面;setThinking 下一轮生效,恒不需重建会话)
  ipcMain.on('chat:set-thinking', (_event, on: unknown) => {
    if (typeof on !== 'boolean' || session === null) return;
    session.setThinking(on ? { budget: THINKING_BUDGET } : undefined);
    thinkingOn = on;
    // S-TH5:落盘记忆(重启恢复,wireSession 重放径既有)
    settings = { ...settings, thinking: on };
    saveSettings(userData, settings);
    publishInfo();
    sendPhase('ready', on ? `思考生成已开(budget ${THINKING_BUDGET},下一轮生效)` : '思考生成已关(下一轮生效)');
  });

  // ———————————— 窗口与生命周期 ————————————

  function createWindow(): void {
    win = new BrowserWindow({
      width: 1280,
      height: 800,
      title: 'tansr electron-chat 示例',
      show: config.show !== false,
      webPreferences: {
        preload: fileURLToPath(new URL('./preload.cjs', import.meta.url)),
        // 以下三项是 Electron 缺省,显式写出让安全形态在 demo 里「看得见」:
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        // 探针无头跑时防节流(对可见窗口无行为差异)
        backgroundThrottling: false,
      },
    });
    win.on('closed', () => {
      mediaCall?.abort();
      win = null;
    });
    win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    win.webContents.on('will-navigate', (event) => event.preventDefault());
    // 录音必须来自本窗口的 getUserMedia({ audio:true })，视频/其它权限不开放。
    win.webContents.session.setPermissionRequestHandler((contents, permission, callback, details) => {
      callback(contents === win?.webContents && permission === 'media' && 'mediaTypes' in details && details.mediaTypes?.length === 1 && details.mediaTypes[0] === 'audio');
    });
    void win.loadFile(fileURLToPath(new URL('../renderer/index.html', import.meta.url)));
  }

  const ready = new Promise<void>((resolve, reject) => {
    createWindow();
    // 等 renderer 首帧就绪再连接,连接进度(phase)在 UI 可见
    win?.webContents.once('did-finish-load', () => {
      if (disposing) { resolve(); return; }
      connectWork = connect();
      connectWork.then(resolve, (err: unknown) => {
        const detail =
          err instanceof TansrSdkError
            ? `${err.code}: ${err.message}`
            : err instanceof Error
              ? err.message
              : String(err);
        sendPhase('fatal', detail);
        reject(err instanceof Error ? err : new Error(String(err)));
      });
    });
  });
  // 探针不挂 whenReady 时的兜底消费(fatal 已经 sendPhase 呈现,恒不静默)
  ready.catch(() => {});

  return {
    getWin: () => win,
    getSession: () => session,
    whenReady: () => ready,
    paths: { userData, sandbox, get historyFile() { return store.historyPath; } },
    dispose(): Promise<void> {
      if (disposeWork !== undefined) return disposeWork;
      disposing = true;
      tokens.close();
      mediaCall?.abort();
      mediaFiles.clear();
      imageDrafts.clear();
      speechDrafts.clear();
      contextUnsubscribe?.();
      narrator?.dispose();
      const work = (async () => {
        // 已启动装配可能晚到;先等移交完成,然后处理当前及重建失败留下的旧会话。
        await Promise.allSettled([connectWork]);
        narrator?.dispose(); // 装配晚到时可能在dispose启动后才创建Narrator。
        narrator = null;
        const results = await Promise.allSettled([...ownedSessions].map(async (owned) => {
          // 真实层显式Infinity;main另有30s观察口,超时不会断仍被查询使用的共享host。
          await closeDemoSession(owned, Infinity);
          ownedSessions.delete(owned);
          if (session === owned) session = null;
        }));
        const failures = results.flatMap((result) => result.status === 'rejected' ? [result.reason] : []);
        if (failures.length > 0) throw new AggregateError(failures, '部分会话尚未完成清理,请再次等待。');
        await mcpHost?.dispose(); // 共享host只由应用在全部会话真实完成后处置。
        mcpHost = null;
        mediaFiles.clear();
      })();
      disposeWork = work;
      void work.catch(() => { disposeWork = undefined; }); // 确定失败保留引用,允许显式再次处置。
      return work;
    },
  };
}
