import { readQuerySettlementSnapshot, observeArchiveSession, observeArchiveHistoryCommit, registerOriginalQuerySettlement, type OriginalQuerySettlementReceipt, type QuerySettlement, snapshotArchiveBindingCandidate, withArchiveSourceReadLease, type ArchiveSourceReadLease, assertArchiveBindingCaptureReady, raceAbort, type ArchiveSessionBindingWork, type ArchiveSessionHistoryPort, type ArchiveSessionHistoryCommit, type ArchiveOriginalHistoryCommit, type ArchiveSessionObservation } from '@tansr/kernel';
/**
 * 多轮会话面:createSession / AgentSession(纯 headless,无 UI)。
 *
 * 多轮语义对齐 cli tui/session-driver.ts 的驱动器(该文件为语义事实源):
 * - 空闲 send → 以「历史快照 + 新 user 消息」起新一轮 runQuery;
 * - 运行中 send → handle.enqueueUserInput(注入屏障,下一 assembling 前消费);
 *   自然终态经 counters().queueInjections 核对,未消费旧输入结转为下一轮开场输入;
 *   中断或硬限制终态不借结转重置预算。strict 输入从不自动结转。
 * - 终态收口:history = handle.messages()(深拷贝快照,历史保真唯一出口);
 * - interrupt → handle.abort()(空闲/终态后为无害空操作);
 * - close → 幂等;运行中先 abort,收口推迟到泵终态(尾部事件不丢失),
 *   签发 session.ended 后终结事件流。
 *
 * 事件包络:入队时重打会话全局单调 seq(与 serve/serve-session.ts 同裁决
 * ——内核 per-query seq 逐轮清零,且装配层合成事件与内核事件存在打戳/入队
 * 竞态;入队序 == 打戳序才满足协议「seq 会话内单调」)。turnId 保留
 * 内核原值作轮内关联。
 *
 * 两档模型来源:
 * - 托管:options.model 为别名/ref 字符串 → SDK 内完成「五层配置加载 →
 *   ProviderRegistry → fallback 链 client → 权限(五层规则)→ hooks
 *   (PreToolUse)」精简装配;
 * - 注入:options.client + options.model 为 ResolvedModel 对象(成对)→
 *   零配置零 IO,工具/权限仍按 options 装配(hooks 需配置来源,注入档不装)。
 */
import { randomUUID } from 'node:crypto';
import { executeArchiveDetachment, stopArchiveInstallationAdmission, assertSessionCaptureSettled, type ArchiveSessionDetachmentWork } from '@tansr/kernel';
import { withArchiveColdHistoryConfirmation, withArchiveColdHistoryLease,
  type ArchiveColdHistoryConfirmation, type ArchiveColdHistoryWitness } from '@tansr/kernel';
import { assertArchiveRecoveryInstallation, assertArchiveInstallationAdmission, isArchiveInstallationAdmissionBlocked, assertPreparedArchiveInstallation, confirmPreparedArchiveInstallation, markArchiveSessionLifecycleInstalled, assertArchiveSessionLifecycleInstalled } from '@tansr/kernel';
import { claimArchiveSessionLifecycleBundle, type ArchiveSessionLifecycleRuntime, type ResourceSettlementResult } from '@tansr/kernel';
type LifecycleRun = ReturnType<ArchiveSessionLifecycleRuntime['run']['planRun']>;
type LifecycleRewrite = ReturnType<ArchiveSessionLifecycleRuntime['rewrite']['prepare']>;
interface LifecycleTurn {
  ticket: LifecycleRun; query: QueryHandle; events: AsyncGenerator<KernelEvent, void, undefined>;
  continuation?: { readonly history: IRMessage[]; readonly source: ArchiveSessionObservation;
    readonly observation: ArchiveSessionObservation; readonly commitId: string;
    readonly rewritten: boolean; readonly reason?: HistoryRewriteReason; readonly unadmitted: boolean;
    work?: Promise<void>; receipt?: ArchiveOriginalHistoryCommit; readonly failureOperationIds: number[] };
}
interface LifecycleRewriteWork {
  ticket: LifecycleRewrite; ended(): void; signal: AbortSignal;
  original: IRMessage[]; source: ArchiveSessionObservation;
  completion?: Promise<void>; receipt?: ArchiveOriginalHistoryCommit;
  settle(): Promise<ResourceSettlementResult>;
  readonly failureOperationIds: number[];
  commitInput?: Parameters<LifecycleRewrite['commitHistory']>[0];
  compensationInput?: Parameters<LifecycleRewrite['compensateHistory']>[0];
  compensationCompletion?: Promise<void>;
}
class ArchiveRewriteReconciliationError extends Error {}

import { validateUserBlocks, type SessionUserBlock, type SendBlocksResult } from './sessions/user-blocks.js';
import { SessionInputDelivery } from '@tansr/kernel';
import type { SessionInputCapabilities, SessionInputReceipt, SessionInputResult, SessionInputSubmission, SessionInputTarget } from '@tansr/kernel';
import { resolveApplicationSystem } from './platform/system-prompt.js';
import { createApplicationPromptRefresherWithRuntime } from './platform/system-prompt-refresh.js';
import { readPlatformConfiguration } from './platform/client.js';
import { prepareApplicationPromptTurn } from './platform/application-prompt-turn.js';
import { applicationPromptFailureMessage } from './platform/config-error.js';
import type { ApplicationPromptInfo, SystemPromptPolicy } from './platform/system-prompt.js';
import type {
  EventBody,
  InputModalityMode,
  IRMessage,
  IRSystemSegment,
  IRToolDef,
  KernelEvent,
  ModelClient,
  ResolvedModel,
} from '@tansr/protocol';
import {
  JournalError,
  ResourceSettlementAccumulator,
  ResourceSettlementScope,
  measureContextBudget,
  prepareContextTransition,
  prepareCapturedGovernance,
  decodeCheckpointImport,
  encodeCheckpointExport,
  newCheckpointId,
  resolveCompactionManager,
  resolveSessionCwd,
  runQuery,
  assertRequiredContextMaterialsReady,
  rebindAttachmentCheckpoint,
} from '@tansr/kernel';
import type {
  CompactionFailureReason,
  ContextView,
  ContextManager,
  AttachmentCheckpoint,
  HostShell,
  LoadConfigOptions,
  LoadedConfig,
  PricingUpdate,
  PromptChannel,
  QueryBeforeCompactContext,
  QueryBeforeCompactHook,
  QueryCompactionOptions,
  QueryHandle,
  SessionCaptureCoordinator,
  ContextMaterialReader,
  ToolDecisionRecord,
  ToolExecutor,
} from '@tansr/kernel';
import type { ProviderRegistry } from '@tansr/providers';
import { ulid } from '@tansr/providers';
import {
  DEFAULT_MAX_TURNS,
  assembleManagedModel,
  assembleTooling,
  buildClientFromRegistry,
  providerSwitchedBody,
  resolveImageInputMode,
} from './assembly.js';
import type { PermissionOptions } from './assembly.js';
import { assembleTokenTierAdjudicator, prepareAdjudication } from './adjudication/assemble.js';
import type { AdjudicationOptions } from './adjudication/assemble.js';
import { sdkEgressClient } from './egress.js';
import { SdkLifecycleError, TansrSdkError } from './errors.js';
import { LifecycleLedger, LifecycleWait, OwnedResources, emptyPending, flushBorrowedStore, lifecycleSummary } from './lifecycle.js';
import type { SessionDrainOptions, SessionDrainResult, SessionLifecycleFailure } from './lifecycle.js';
import type { McpSessionOption } from './mcp.js';
import {
  assemblePlatformModelWithRuntime,
  createAppTokenSourceFetch,
  createSdkWarningChannel,
  readAppToken,
  validateTokenTierOptions,
  warnEmbeddedPlatformMismatch,
} from './platform/client.js';
import type { AppTokenSource, PlatformWarning, SdkWarning } from './platform/client.js';
// doc/123 D-A9 ②:令牌档平台直连面(transcribe / speak;纯 HTTP,不进历史/事件流)
import { createPlatformAudioClient, createUnavailablePlatformAudioClient } from './platform/audio-client.js';
import type { PlatformAudioClient } from './platform/audio-client.js';
import type { AppCapabilities } from './platform/contract.js';
import { DEFAULT_MAX_TOKENS } from './run-agent.js';
import type { SdkSkillsOptions } from './skills.js';
import { attachSdkTaskTool, subagentModelResolverOf } from './task.js';
import type { PlatformToolContext, SdkToolsOptions } from './toolset.js';
import { repairHistoryPairing } from './sessions/pairing.js';
import type { SessionCwdChange, SessionStore } from './sessions/store.js';
import {
  assertValidCheckpointRetention,
  createFileCheckpointStore,
  resolveCheckpointRetention,
  withCheckpointRetention,
} from './sessions/checkpoint-store.js';
import type { CheckpointRetention } from './sessions/checkpoint-store.js';
import type {
  Checkpoint,
  CheckpointInput,
  CheckpointMeta,
  CheckpointStore,
  CheckpointTrigger,
} from './sessions/checkpoint-store.js';
import { runIdleCompaction, runCapturedIdleCompaction } from './sessions/compaction-idle.js';
import { forkIntoStore } from './sessions/fork.js';
import { normalizeHistoryPageOptions, sliceHistoryPage } from './sessions/history-page.js';
import { createSettlementBarrier } from './query-lifecycle.js';
import type { HistoryPage, HistoryPageOptions } from './sessions/history-page.js';
import { publicModel } from './sessions/context-state.js';
import type { ModelTransitionBackup, SessionContextState, SessionModelState, SwitchModelOptions, SwitchModelResult } from './sessions/context-state.js';
import { createSessionBudget, validateSessionBudgetOptions } from './session-budget.js';
import type { SessionBudgetOptions, SessionBudgetWiring } from './session-budget.js';

/**
 * `createSession` 的选项。三档模型来源互斥:令牌档(`token` + `baseUrl`,模型与能力位由平台 bundle 供给)/
 * 托管档(`model` 为别名字符串,五层配置装配)/ 注入档(`client` + `model` 对象,零配置零 IO);
 * 其余为工具与权限、会话参数、持久化与快照 / 恢复 / fork 三组可选项。
 */
export interface CreateSessionOptions {
  /** Actual user input incorporation; not acceptance or a new turn. */
  onInputsConsumed?: (texts: readonly string[]) => void;
  /**
   * 模型档(三档判定):
   * - 字符串(别名/ref)→ 托管模式(五层配置)或令牌模式(bundle 别名,
   *   token 在场时);缺省 'main';
   * - ResolvedModel 对象 → 注入模式,必须与 client 成对提供。
   */
  model?: string | ResolvedModel;
  /** 注入模式的模型客户端(与 model 对象成对;测试传 scripted client) */
  client?: ModelClient;
  /** 注入模式的上下文窗口(托管模式自 capability.maxContext 取,忽略本值) */
  contextWindowTokens?: number;
  /** 注入模式的输出上限(maxTokens 事前钳制上界;托管模式自 capability.maxOutput 取,忽略本值) */
  maxOutputTokens?: number;

  // ———— 平台令牌档(与 client 注入档、五层配置托管档互斥)————
  /**
   * app_user 短期令牌(开发者服务端换发;终端分发形态)。在场即令牌档:
   * 模型与能力位由平台 bundle 供给,恒零本地配置 IO(config/loadOptions/
   * capabilities 均不可同给)。
   * 亦可传 getter(`() => currentToken`):每个平台请求(bundle / heartbeat、模型直连、媒体工具、
   * `session.platform` 直连、提示词刷新)发起时取现值,令牌轮换后无需重建会话;getter 须回非空字符串。
   */
  token?: string | (() => string);
  /** 平台 API 基址(令牌档必填,如 https://api.example.com) */
  baseUrl?: string;
  /**
   * 平台提示帧承接(仅令牌档产生;其余档恒不触发)。平台以 t.warn 结构化告知非致命降级
   * (如 thinking_unavailable_on_face:思考被请求而当前供应商面无从外露,回答照常仅无思考;
   * balance_low:余额预警)。缺席时缺省承接 = console.warn 每码一次(告知恒不静默蒸发);
   * 提供回调即全量接管(不去重,原始帧逐一交付,呈现策略归开发者)。
   */
  onPlatformWarning?: (warning: PlatformWarning) => void;
  /**
   * 通用告警通道:平台提示帧(`source:'platform'`,与 onPlatformWarning 同源同 code)与 SDK 自身非致命提示
   * (`source:'sdk'`:`cwd_mismatch_on_restore` / `cwd_mismatch_on_resume`)统一汇入。与 onPlatformWarning
   * 同给时平台告警各到一次不重复(后者是已发版的平台子集通道,恒不收 sdk 源)。缺席时:平台源按
   * onPlatformWarning / console.warn 现状回落;sdk 源回落为 `turn.error{scope:'sdk.notice',
   * recoverable:true}` 入事件流(提示不丢)。
   */
  onWarning?: (warning: SdkWarning) => void;
  /**
   * 权限终局审计记录出口(kernel `onDecision` 缝;`ToolDecisionRecord` 与 cli `decisions.jsonl` 行同形,
   * 含机器放行 classifier / mode)。缺省:裁决人终局(source 'classifier')经 `onWarning` / `onPlatformWarning`
   * 通报 `permission_decided`;传自己的写入器即接管,传 `() => {}` 静默。事件流本身已携
   * `tool.permission.decided{allow|deny, decisionSource}`(含机器放行),本缝是审计半边。
   */
  onDecision?: (record: ToolDecisionRecord) => void;

  // ———— 托管装配注入缝(注入/令牌模式忽略)————
  /** 项目层配置发现起点与工具 cwd;缺省 process.cwd() */
  cwd?: string;
  /** 环境变量注入缝;缺省 process.env */
  env?: Record<string, string | undefined>;
  /** fetch 注入缝(透传协议适配器) */
  fetchImpl?: typeof fetch;
  /** 已装载配置注入缝:提供时跳过 loadConfig */
  config?: LoadedConfig;
  /** loadConfig 其余注入缝(fs/homedir/platform;测试 hermetic 用) */
  loadOptions?: Omit<LoadConfigOptions, 'cwd' | 'env'>;
  /**
   * 项目层 hooks 信任门(托管档)。缺席 / false = 工作区 `.tansr/settings.json` 与 `settings.local.json`
   * 声明的 command / http hooks 不启用、零执行、零 `hook.*` 事件(user 层 / managed 层 hooks 照常);
   * true = 宿主代表用户为该工作区授信,项目层 hooks 照跑。SDK 不持久化信任、不读取命令行工具的工作区
   * 信任记录——是否授信由宿主自行向用户确认并逐次传入。门住的项目层 hooks 在场时通报一次
   * `project_hooks_untrusted`(`onWarning`;缺席回落事件流 `turn.error{scope:'sdk.notice', recoverable:true}`)。
   * 注入 / 令牌档无配置 hooks,本项无效。
   */
  trustProjectHooks?: boolean;
  /**
   * 权限引擎 Shell 命令词法方言(kernel `HostShell`:'posix' | 'powershell')。缺席随平台(kernel
   * `detectHostShell()`,与内置 Shell 工具实际派生的 shell 一致);显式值供测试或跨平台装配演练——与真实
   * 执行 shell 错配会让 Shell 命令词法守卫与执行环境脱节,生产宿主不应设置。三档皆生效。
   */
  hostShell?: HostShell;

  // ———— 工具与权限 ————
  /**
   * 三环工具入口:builtin(环2 内置,词表逐名)/ platform(环3 平台能力,现只校验位)/ custom
   * (环1 defineTool 产物)。缺席 = 装配能力位放行的全部内置工具;`tools: { builtin: [] }` = 零内置。
   */
  tools?: SdkToolsOptions;
  /** 能力位档;缺省 DEFAULT_APP_CAPABILITIES(令牌档由 bundle 供给) */
  capabilities?: AppCapabilities;
  /**
   * skills 注入:custom(defineSkill 内联)+ dirs(<name>/SKILL.md 目录)。SDK 恒零发现
   * (不扫 ~/.tansr 与 cwd);受 skills 能力位。
   */
  skills?: SdkSkillsOptions;
  /**
   * MCP 外接:McpHost 实例 = 应用级共享(跨会话复用连接,生命周期归调用方 dispose);
   * { servers } 选项对象 = 会话级临时 host,会话 close 时自动断连收口。受 mcp 能力位。
   */
  mcp?: McpSessionOption;
  /** AskUser 工具交互通道;缺省 UnavailableChannel(结构化降级,不挂起) */
  promptChannel?: PromptChannel;
  permission?: PermissionOptions;
  /**
   * 裁决人(仅令牌档):bundle 生效裁决人档缺省即装配——「本应 ask」的工具调用先经裁决人:判危险按姿态落地
   * (人在环 → 经 permission.askUser 携理由交用户终审;无人环 → deny 理由回喂模型),判安全且资格半径覆盖 →
   * 放行,缺省安全的操作恒不送裁决人。姿态缺省按 askUser 在场推导;`enabled:false` 回到 default 模式现状;
   * 与 permission.mode 同现 fail-fast。托管/注入档无任命来源,传本位 fail-fast。
   */
  adjudication?: AdjudicationOptions;

  // ———— 会话参数 ————
  /** 业务提示词：平台 fallback 策略下显式值（含 []）覆盖；prepend 策略保留平台段在前。SDK 段创建时固定；平台正文和策略在每个新轮前自动刷新。 */
  system?: IRSystemSegment[];
  /** 业务段选定后追加的宿主指南，不触发平台默认覆盖；权限与工具指南仍独立装配。 */
  systemAppend?: IRSystemSegment[];
  /** 缺省 8192 */
  maxTokens?: number;
  /**
   * 思考生成(**生成面**旋钮;呈现面走 SessionView 的 delivery 选项,两旋钮
   * 正交):逐轮透传 IRRequest.thinking(budget = 思考 token 预算,按模型
   * 方言映射)。缺省缺席 = 不注入(沿模型/适配器缺省,多数模型即不产思考)。
   * 会话中可经 setThinking 动态换,下一轮生效。
   */
  thinking?: { budget?: number };
  /**
   * 会话预算闸(与 `thinking.budget` 无关):会话累计消耗(USD 金额 / token 总量)触顶时按 `onExceeded`
   * 处置——缺省 `'block'` 在下一次模型调用前终止本轮(`turn.aborted { reason: 'budget_exceeded' }`,会话保持打开),
   * `'warn'` 每次查询至多一条 `turn.error { scope: 'budget' }` 后继续。字段与配置文件 `budget` 段同形,
   * `tansr` 命令行各形态(TUI / headless / serve / acp)与 SDK 读同一段、同一闸门语义。两个上限都缺席 = 不接闸。
   * 金额维只对以 USD 计价的模型出数(令牌档按平台 bundle 计价);其余档请用 `maxTotalTokens`。
   */
  budget?: SessionBudgetOptions;
  /** 单轮 modelTurns 护栏;缺省 200(与 serve 对齐) */
  maxTurnsPerQuery?: number;
  /** 缺省 randomUUID() */
  sessionId?: string;
  /** 会话级中断信号:abort 即 close(运行中轮先优雅中止,尾部事件不丢) */
  signal?: AbortSignal;
  /**
   * 事件流积压上界(首订阅者到来前保留的事件条数 / 近似字节;缺省 1000 条 / 2 MiB)。超界丢最旧、
   * 队首以恰一条 `session.events_dropped{scope:'sdk.events', droppedCount, reason}` 通报丢弃计数与原因
   * (contract-v0.27,RFC-RF-1);上界以内首订阅者仍收全量积压。取值须为正数(可为 Infinity),否则 invalid_options。
   */
  events?: { maxBacklogEvents?: number; maxBacklogBytes?: number };
  /**
   * 上下文压缩(kernel 自动压缩):缺省 {}(自动压缩,仅上下文窗口已知的轮生效——阈值代数依赖窗口,
   * 与 TUI/serve 同守卫);false 显式禁用。
   */
  compaction?: QueryCompactionOptions | false;
  /** 历史预载(恢复场景):后续轮以「预载历史 + 新 user 消息」起步 */
  initialMessages?: readonly IRMessage[];
  /**
   * 可注入持久化缝(形态对齐 cli 的 onHistoryCommit):每轮终态收口(历史快照已就位)
   * 时捕获全量历史深拷贝，按会话提交顺序回调，存到哪由开发者决定。缺省缺席 = 纯内存
   * 零变化(SDK 恒不引入任何隐式落盘)。
   * meta.rewritten:本轮历史前缀被就地改写(session.compacted 压缩折叠,
   * 或 session.microcompacted 旧 tool_result 原位存根化——SDK 无 CLI 的
   * journal 存根重演机制,对落盘方两者同为「已存内容不再与内存对齐」),
   * 落盘方不得按长度增量追加,须整卷轮换。
   * 回调抛错(含异步 reject)恒不破会话主流程:吞错并合成
   * turn.error(scope='sdk.persistence')入事件流;事件流已终结时退化
   * console.warn(错误不静默消失)。rewritten 提交失败后,下一次提交恒再以 rewritten:true 交付
   * (存储与回调独立内存位),落盘方不会只凭「本轮无改写」走增量径而留下「旧前缀 + 新尾巴」拼接体。
   * idle()/session.ended 仍不等异步提交；需要真实完成使用 drain()/closeAsync()。
   * 回调中不得等待本会话 drain()，否则会等待回调自身；retryPersistence 不重放回调。
   */
  onHistoryCommit?: (
    history: readonly IRMessage[],
    meta: HistoryCommitMeta,
  ) => void | Promise<void>;

  // ———— 会话存储(resume / store 双字段,正交可单用)————
  /**
   * 一等恢复:自 store 取回历史与 meta 预载(与 initialMessages 互斥;
   * sessionId 同给时必须与 resume.sessionId 一致)。resume 径内置配对治理
   * (`repairHistoryPairing`):断尾 tool_call 补 isError 占位、孤儿
   * tool_result 截断到干净边界——kernel 对 initialMessages 原样收不校验,
   * 治理保证 wire 恒不 400。目标不存在抛 TansrSdkError('session_not_found');
   * 存储损坏由 store.get 结构化上抛(恒不静默)。
   */
  resume?: { sessionId: string; store: SessionStore };
  /**
   * 在场时自动接管会话登记(store.create,幂等;令牌档的平台会话 ULID
   * 映射随登记落 SessionRecordMeta)与每轮落卷(store.commit;
   * store 先行、onHistoryCommit 回调后行,失败面同 sdk.persistence 吞错
   * 纪律)。与 resume 正交:续存一个恢复的会话把同一 store 同时传两处。
   * 缺席 = 纯内存零变化(SDK 恒不引入任何隐式落盘)。
   */
  store?: SessionStore;
  /**
   * 上下文快照:完整上下文的自包含只读拷贝,可列举 / 可恢复 / 可删除。落点三选一:`store`
   * (自建 CheckpointStore)> `dir`(独立目录,独立生命周期)> 缺省同居——会话 `store` 自带的
   * `checkpoints`(createFileSessionStore = <sessionDir>/checkpoints,随会话删除)。三者皆缺 =
   * 快照未接线:checkpoint()/listCheckpoints()/deleteCheckpoint() 抛 `checkpoints_not_wired`,
   * restore() 返回 `rejected:'store_not_wired'`,compact() 缺省不落快照(显式 `checkpoint:true` 才抛)。
   */
  checkpoints?: SessionCheckpointOptions;
  /**
   * fork 快捷形 = `fork()` + `resume`:以 `store` 里源会话 `sessionId` 的快照 `checkpointId` 建出新会话
   * 记录(源会话零改动)并立即打开它;新会话事件流紧随 session.created 签发
   * `session.forked{ sourceSessionId, checkpointId, sessionId }`。
   * 须与 `store` 同给(新会话落在该 store);与 `resume` / `initialMessages` 互斥(同给 fail-fast
   * `invalid_options`,先于任何 IO);`sessionId` 同给时 = 新会话 id。快照经与 `checkpoints`
   * 选项同一落点解析读取(store > dir > 会话 store 同居);快照不存在抛 `checkpoint_not_found`。
   */
  fork?: { sessionId: string; checkpointId: string };
}

/** createSession.checkpoints 选项(缺省值随注) */
export interface SessionCheckpointOptions {
  /** 自建快照存储(与 dir 互斥;同给抛 invalid_options) */
  store?: CheckpointStore;
  /** 独立快照根目录(createFileCheckpointStore({ dir });与 store 互斥) */
  dir?: string;
  /** compact()/自动压缩前自动落 pre_compaction 快照;缺省 true */
  autoBeforeCompact?: boolean;
  /**
   * 历史清空前自动落 pre_clear 快照;缺省 true。**预留位**:AgentSession 当前无
   * 清空入口(TUI /clear 归 cli sink 自有钩子),本项在场只作类型/缺省登记。
   */
  autoBeforeClear?: boolean;
  /**
   * 同会话快照数上限;到顶按时间序淘汰最旧,不分 trigger(缺省不限)。= `retention.max`
   * 别名(同给时 `retention` 优先)。刚写入者恒保留(`max:0` 留 1)。
   */
  max?: number;
  /**
   * 保留策略(与 kernel `CheckpointRetention` 同形):`max` / `maxAgeDays` 并集,先按龄再按数;
   * store / dir / 同居三形态同一淘汰律(kernel `planCheckpointRetention`)。
   */
  retention?: CheckpointRetention;
}

/** onHistoryCommit 附带信息(落盘方决定增量追加还是整卷轮换) */
export interface HistoryCommitMeta {
  /** true = 历史前缀被就地改写(压缩折叠/存根化),已存内容不再与之对齐 */
  readonly rewritten: boolean;
  /**
   * 改写原因(S-2 加法;rewritten=true 时可在场):compaction = 压缩折叠(compact()
   * 直压或轮内自动压缩)/ clear = 历史清空 / restore = 自快照恢复(restore())。
   * 缺席 = 非改写提交,或改写只来自 microcompact 存根化(不在本词表)。
   */
  readonly reason?: HistoryRewriteReason;
}

/** 历史改写原因词表(HistoryCommitMeta.reason / SessionStoreCommitMeta.reason 同形) */
export type HistoryRewriteReason = 'compaction' | 'clear' | 'restore';

// ———— 手动压缩与上下文快照(结果联合可选位为加法)————

/** compact() 选项 */
export interface CompactOptions {
  /** 用户附加摘要指令(追加在指令模板后,只调焦点) */
  instructions?: string;
  /**
   * 压缩前是否落 pre_compaction 快照:缺席 = 按 checkpoints.autoBeforeCompact
   * (缺省 true;快照未接线时缺省不落);false = 本次不落;true / { label } =
   * 落(未接线抛 checkpoints_not_wired,零副作用)。
   */
  checkpoint?: boolean | { label?: string };
}

/** compact() 结果联合(compacted / rejected / failed;failed 形亦携失败前已落的快照 id) */
export type CompactResult =
  | {
      status: 'compacted';
      /** 压缩执行 id(与 session.compacted.compactionId / 摘要 meta.compactionId 同源) */
      compactionId: string;
      /** 被移除消息的 index 闭区间(压缩前坐标) */
      removedRange: [number, number];
      /** 压缩前快照 id(落了才在场) */
      checkpointId?: string;
    }
  | {
      status: 'rejected';
      /**
       * turn_running = 运行中(不排队,await idle() 后重试)/ empty_history = 历史为空 /
       * not_configured = compaction:false 或上下文窗口未知 / hook_blocked = PreCompact
       * hook 否决(SDK 无查询 hooks 面,保留为联合成员)
       */
      reason: 'turn_running' | 'empty_history' | 'not_configured' | 'hook_blocked';
    }
  | {
      status: 'failed';
      /** kernel 压缩失败原因(nothing_to_compact / model_error / before_compact_failed …) */
      reason: CompactionFailureReason;
      /** 失败前已落的快照不回滚,id 随结果交付 */
      checkpointId?: string;
    };

/** checkpoint() 选项 */
export interface CheckpointOptions {
  /** 开发者/用户标签(≤ 120 字) */
  label?: string;
}

/** restore() 选项 */
export interface RestoreOptions {
  /** 恢复前是否先落 pre_restore 快照;缺省 true */
  checkpoint?: boolean;
}

/** restore() 结果联合(restored / rejected) */
export type RestoreResult =
  | {
      status: 'restored';
      checkpointId: string;
      /** 恢复前历史消息数 */
      fromMessages: number;
      /** 恢复后历史消息数(= 快照 messageCount) */
      toMessages: number;
      /** 恢复前自动落的 pre_restore 快照 id(options.checkpoint !== false 时在场) */
      preRestoreCheckpointId?: string;
    }
  | {
      status: 'rejected';
      /**
       * turn_running = 运行中 / not_found = 本会话下无此快照 / session_mismatch = 快照
       * 属于别的会话(跨会话恢复 = 越界;请用 fork)/ store_not_wired = 快照存储未接线
       */
      reason: 'turn_running' | 'not_found' | 'session_mismatch' | 'store_not_wired';
    };

// ———— fork 与快照导出 / 导入 ————

/** fork() 选项 */
export interface ForkOptions {
  /** 新会话标题(记入 store 会话 meta;缺省与建会同律取首条用户输入截断) */
  label?: string;
  /** 新会话工作目录(记入 store 会话 meta;缺省沿本会话 cwd) */
  cwd?: string;
}

/** fork() 结果:新会话 id(未打开;以 createSession({ resume: { sessionId, store } }) 打开) */
export interface ForkResult {
  sessionId: string;
}

/** importCheckpoint() 选项 */
export interface ImportCheckpointOptions {
  /** 标签覆写(≤ 120 字);缺席沿用导出体内 label */
  label?: string;
}

/** 装配期解析后的快照接线(AgentSession 持有;null = 未接线) */
export interface ResolvedCheckpoints {
  readonly store: CheckpointStore;
  readonly autoBeforeCompact: boolean;
  readonly autoBeforeClear: boolean;
}

// ———— cwd 中途切换 ————

/**
 * setCwd() 结果联合:changed = 已切换(from/to 为切换前后的绝对路径;to 与 from 相同时
 * 亦回 changed,零副作用);rejected 四拒绝码沿 kernel resolveSessionCwd(not_absolute /
 * not_found / not_directory / not_allowed)+ turn_running(运行中不切、不排队)。
 */
export type SetCwdResult =
  | { status: 'changed'; from: string; to: string }
  | {
      status: 'rejected';
      reason: 'turn_running' | 'not_absolute' | 'not_found' | 'not_directory' | 'not_allowed';
    };

/** 会话当前模型档(client 与 model 恒成对;窗口/输出上限随组走) */
interface ModelBinding {
  client: ModelClient;
  model: ResolvedModel;
  contextWindowTokens?: number;
  /** capability.maxOutput(查询环事前钳制 maxTokens 的上界) */
  maxOutputTokens?: number;
}

/** setModel 的注入档参数(client 与 model 必须成对更换) */
export interface SetModelBinding {
  client: ModelClient;
  model: ResolvedModel;
  /** 新模型的上下文窗口;不传则清空(宁缺毋错,与 TUI 驱动同语义) */
  contextWindowTokens?: number;
  /** 新模型的输出上限(maxTokens 事前钳制上界);不传则清空(同窗口的宁缺毋错纪律) */
  maxOutputTokens?: number;
}

/**
 * 异步事件广播队列(2026-09-02 根因修:旧形单缓冲 shift + 单 wake 槽系单消费者,
 * 视图泵与开发者自建 for-await 争同一流即静默丢事件——非容错问题,是设计问题):
 * - 共享追加日志 + **每订阅者独立游标**:每次 `for await (session.events)` 即一个订阅者,
 *   全部订阅者各得全量事件、互不争抢;
 * - 保留纪律:「直播开始」= 首个有订阅者在场的 push;开始前的积压(会话创建以来)恒不裁,
 *   开始前挂上的每个订阅者同得全部积压(视图泵/Narrator/宿主循环在 send() 前挂齐即
 *   全员同流,恒不因谁先拉了一条而丢首事件);开始后日志只保留到「全部在场订阅者皆已
 *   消费」处即裁(内存以最慢订阅者的滞后为界),开始后挂上的订阅者自当前起(确定性
 *   "从现在开始",不重放他人已消费的历史);
 * - 消费方 break/throw 退出 for-await 即注销订阅者(finally),恒不拖住裁剪;
 * - end 终结全部订阅者(各自排空后自然 return);
 * - 有界(RF-04 / RV-3-01,代拍 #3 A):直播后订阅者全部离场,再推的事件不为不存在的订阅者保留
 *   (零语义裁剪,不计丢弃);「从未有订阅者」的积压受条数 / 近似字节双上界约束,超界丢最旧、
 *   最新一条恒保留,并以恰一条溢出通报占队首(重复溢出只更新累计计数,不累加通报条数),
 *   首订阅者先收通报再收尾段。上界以内的既有「晚到首订阅者收全量积压」语义不变。
 */
interface EventQueue<T> {
  push(item: T): void;
  end(): void;
  /** 流已终结(S-B5:持久化回调异步失败的事件面可达性判定) */
  readonly ended: boolean;
  readonly iterable: AsyncIterable<T>;
  /** 当前保留条数(含溢出通报);诊断 / 测试用 */
  readonly retained: number;
  /** 因上界累计丢弃的条数(直播后的零语义裁剪不计入) */
  readonly dropped: number;
}

/** 溢出通报的事实位(marker 生成器入参;词值与 protocol session.events_dropped 同形,RFC-RF-1) */
interface EventDropInfo<T> {
  /** 累计丢弃条数 */
  dropped: number;
  /** 累计丢弃的近似度量(与 measure 同口径;缺省 JSON 文本长度) */
  droppedBytes: number;
  /** 最近一次溢出触发的上界 */
  reason: 'count_limit' | 'bytes_limit';
  /** 首条被丢弃的事件(通报可借其序号占位,保持流内序号单调) */
  firstDropped: T;
  /** 最近一条被丢弃的事件 */
  lastDropped: T;
}

/** 「从未有订阅者」积压的上界;缺省(不传)= 不限(仅零语义裁剪) */
interface EventQueueBounds<T> {
  /** 最多保留条数(不含通报);Infinity = 不限 */
  maxEvents: number;
  /** 最多保留的近似字节数(不含通报);Infinity = 不限 */
  maxBytes: number;
  /** 单条近似字节度量;缺省 JSON 文本长度(不可序列化时按固定估值) */
  measure?: (item: T) => number;
  /** 溢出通报生成器;每次溢出以累计事实重生成,恒只占队首一条 */
  marker: (info: EventDropInfo<T>) => T;
}

interface Subscriber {
  /** 下一条待消费的日志绝对序号 */
  cursor: number;
  wake: (() => void) | null;
}

const UNMEASURABLE_EVENT_BYTES = 1024;

function measureEventBytes(item: unknown): number {
  try {
    const text = JSON.stringify(item);
    return typeof text === 'string' ? text.length : UNMEASURABLE_EVENT_BYTES;
  } catch {
    return UNMEASURABLE_EVENT_BYTES;
  }
}

export function createEventQueue<T>(bounds?: EventQueueBounds<T>): EventQueue<T> {
  const log: T[] = [];
  /** 与 log 平行的近似字节(仅有界时维护) */
  const sizes: number[] = [];
  let bytes = 0;
  /** log[0] 对应的绝对序号(裁剪后前移) */
  let head = 0;
  let ended = false;
  /** 直播开始(首个有订阅者在场的 push);此前积压受上界约束、新订阅者恒自积压起 */
  let liveStarted = false;
  const subscribers = new Set<Subscriber>();
  const measure = bounds?.measure ?? measureEventBytes;
  let dropped = 0;
  let droppedBytes = 0;
  let firstDropped: T | undefined;
  /** 溢出通报(队首恰一条;null = 从未溢出) */
  let marker: T | null = null;
  const wakeAll = (): void => {
    for (const s of subscribers) {
      if (s.wake !== null) {
        const w = s.wake;
        s.wake = null;
        w();
      }
    }
  };
  const trim = (): void => {
    if (!liveStarted) return;
    if (subscribers.size === 0) {
      // 零语义裁剪:无人滞后即裁到 head,不为不存在的订阅者保留
      head += log.length;
      log.length = 0;
      return;
    }
    let min = Number.POSITIVE_INFINITY;
    for (const s of subscribers) min = Math.min(min, s.cursor);
    const drop = min - head;
    if (drop > 0) {
      log.splice(0, drop);
      head = min;
    }
  };
  /** 从未有订阅者:按双上界丢最旧,最新一条恒保留;通报以累计事实重生成 */
  const enforceBounds = (): void => {
    if (bounds === undefined) return;
    while (log.length > 1 && (log.length > bounds.maxEvents || bytes > bounds.maxBytes)) {
      const reason: EventDropInfo<T>['reason'] = log.length > bounds.maxEvents ? 'count_limit' : 'bytes_limit';
      const victim = log.shift() as T;
      const victimBytes = sizes.shift() ?? 0;
      bytes -= victimBytes;
      droppedBytes += victimBytes;
      head += 1;
      dropped += 1;
      firstDropped ??= victim;
      marker = bounds.marker({ dropped, droppedBytes, reason, firstDropped, lastDropped: victim });
    }
  };
  return {
    push(item: T): void {
      if (ended) return;
      if (subscribers.size > 0) liveStarted = true;
      if (liveStarted && subscribers.size === 0) {
        // 直播后无人在场:不保留(序号仍前移,晚订阅者自当前起)
        head += 1;
        return;
      }
      log.push(item);
      if (!liveStarted && bounds !== undefined) {
        const size = measure(item);
        sizes.push(size);
        bytes += size;
        enforceBounds();
      }
      wakeAll();
    },
    end(): void {
      ended = true;
      wakeAll();
    },
    get ended(): boolean {
      return ended;
    },
    get retained(): number {
      return log.length + (marker !== null ? 1 : 0);
    },
    get dropped(): number {
      return dropped;
    },
    iterable: {
      async *[Symbol.asyncIterator](): AsyncGenerator<T> {
        // 直播前:自积压起(全员同流;有溢出则先收队首通报);直播后:自当前起(确定性"从现在开始")
        const fromBacklog = !liveStarted;
        const me: Subscriber = { cursor: fromBacklog ? head : head + log.length, wake: null };
        subscribers.add(me);
        try {
          if (fromBacklog && marker !== null) yield marker;
          for (;;) {
            while (me.cursor < head + log.length) {
              const item = log[me.cursor - head] as T;
              me.cursor += 1;
              trim();
              yield item;
            }
            if (ended) return;
            await new Promise<void>((resolve) => {
              me.wake = resolve;
            });
          }
        } finally {
          subscribers.delete(me);
          trim();
        }
      },
    },
  };
}

/** AgentSession 构造参数(createSession 装配产物;不对外) */
interface AgentSessionInit {
  /** 原可信 Store 旁带；公开 initialMessages/meta 不能注入。 */
  restoredAttachmentCheckpoint?: unknown;
  restoredAttachmentCwd?: string;
  restoredAttachmentRepaired?: boolean;
  /** @internal 仅宿主装配接入，公开 SDK 选项不开放档案依赖。 */
  sdkSessionRuntime?: SdkSessionRuntimeDependencies;
  binding: ModelBinding;
  modelAlias?: string;
  executor: ToolExecutor;
  system: IRSystemSegment[];
  applicationPrompt?: ApplicationPromptInfo;
  prepareApplicationPrompt?: (signal: AbortSignal) => Promise<{ system: IRSystemSegment[]; info: ApplicationPromptInfo }>;
  tools: IRToolDef[];
  cwd: string;
  sessionId: string;
  maxTokens: number;
  thinking: { budget?: number } | undefined;
  maxTurnsPerQuery: number;
  compaction: QueryCompactionOptions | undefined;
  initialMessages: readonly IRMessage[];
  /** 托管/令牌模式的 registry(setModel 字符串档);注入模式为 null */
  registry: ProviderRegistry | null;
  onHistoryCommit:
    | ((history: readonly IRMessage[], meta: HistoryCommitMeta) => void | Promise<void>)
    | undefined;
  /** 会话收口回调(session.ended 后调用;临时 MCP host dispose 等) */
  onEnded?: () => Promise<void>;
  ownedResources?: OwnedResources;
  /** 快照接线(装配期解析;null = 未接线) */
  checkpoints: ResolvedCheckpoints | null;
  /** 轮起跑回调(裁决人 transcript 记录器回填当前轮用户 prompt) */
  onTurnStart?: (prompts: readonly string[]) => void;
  onInputsConsumed?: (texts: readonly string[]) => void;
  /**
   * 按新 cwd 重建执行器(装配层 rebindCwd:权限门 / hooks 同步换新,registry 与
   * 会话级执行态共享);setCwd 成功即以返回值替换 #executor。
   */
  rebindCwd: (cwd: string) => ToolExecutor;
  /**
   * cwd 切换落盘缝(store.recordCwdChange 在场时由 createSession 接线;缺席 = 不落盘)。
   * 失败按 sdk.persistence 吞错纪律入事件流,恒不破切换本身。
   */
  onCwdChanged?: (change: SessionCwdChange) => void | Promise<void>;
  /** 告警通道(createSdkWarningChannel 产物;sdk 源提示经此汇入 onWarning 或回落 sdk.notice) */
  warn: (warning: SdkWarning) => void;
  hasWarningHandler?: boolean;
  /** 会话存储(fork() 安放新会话记录;缺席 = fork() 抛 session_store_not_wired) */
  store?: SessionStore;
  /**
   * 令牌档平台直连材料(与系统工具的平台提供方 PlatformToolContext 同源:baseUrl / token / fetchImpl);
   * 在场即挂载 session.platform 直连客户端,缺席(托管/注入档)= fail-fast 占位
   * (调用即 invalid_options 指引令牌档,imageGen 工具选择同律)。
   */
  platformContext?: PlatformToolContext;
  /** 「从未有订阅者」积压的条数 / 近似字节上界(createSession 已校验;缺省见 DEFAULT_EVENT_BACKLOG) */
  eventBacklog: { maxEvents: number; maxBytes: number };
  /**
   * 装配层合成事件的入流口回填——构造期把类内私有入流方法交给 createSession 的闭包
   * (hooks emit / 告警 notice / session.forked),模块外不再依赖公开方法。
   */
  bindEmit?: (emit: (body: EventBody, source: string) => void) => void;
  /** 会话级中断信号:abort → close;监听在 close 时解除。已 aborted 的信号由 createSession 直接 close */
  signal?: AbortSignal;
  /**
   * 会话预算闸接线(createSession 由 `budget` 选项装配;缺席 = 不接闸、不建账本):`consume` 旁路记账本会话
   * 全部入流事件(内核事件 + 装配层合成的 cost.usage.updated),`option` 逐轮透传 runQuery.budget。
   */
  budget?: SessionBudgetWiring;
}

/** RV-3-01 缺省上界(doc/90 §4 `events` 同笔:改一处必改三处 —— 实现 / doc/90 / rf04-events-bounds 测试) */
const DEFAULT_EVENT_BACKLOG = Object.freeze({ maxEvents: 1000, maxBytes: 2 * 1024 * 1024 });

/** `CreateSessionOptions.events` 校验与缺省合成:取值须为正数(Infinity 可,= 不限),否则 invalid_options */
function resolveEventBacklog(events: CreateSessionOptions['events']): AgentSessionInit['eventBacklog'] {
  const pick = (key: 'maxBacklogEvents' | 'maxBacklogBytes', fallback: number): number => {
    const value = events?.[key];
    if (value === undefined) return fallback;
    if (typeof value !== 'number' || Number.isNaN(value) || value <= 0) {
      throw new TansrSdkError(
        'invalid_options',
        `createSession: "events.${key}" must be a positive number (Infinity = unbounded); got ${String(value)}.`,
      );
    }
    return value;
  };
  return { maxEvents: pick('maxBacklogEvents', DEFAULT_EVENT_BACKLOG.maxEvents), maxBytes: pick('maxBacklogBytes', DEFAULT_EVENT_BACKLOG.maxBytes) };
}

/**
 * 多轮会话句柄(纯 headless,无 UI;由 `createSession` 创建)。`send()` 提交输入(空闲起新轮,运行中经注入
 * 屏障并入下一轮),`events` 为可多消费者的会话事件流,`messages()` / `history()` 读历史快照;另提供
 * 手动压缩 / 快照 / 恢复 / fork、`setModel` / `setThinking` / `setCwd` 空闲期热切换、`interrupt()` 与幂等
 * `close()`。
 */
export class AgentSession {
  /** 最近成功装配／轮前刷新选中的业务提示词来源与策略，不含提示词正文。 */
  #applicationPrompt: ApplicationPromptInfo;
  readonly #prepareApplicationPrompt: AgentSessionInit['prepareApplicationPrompt'];
  get applicationPrompt(): ApplicationPromptInfo { return this.#applicationPrompt; }
  readonly #queue: EventQueue<KernelEvent>;
  /** W-2:setCwd 以派生新实例替换(kernel 执行器自身 cwd 恒为构造期常量) */
  #executor: ToolExecutor;
  readonly #system: IRSystemSegment[];
  readonly #tools: IRToolDef[];
  readonly #maxTokens: number;
  #thinking: { budget?: number } | undefined;
  readonly #sessionId: string;
  readonly #inputDelivery: SessionInputDelivery;
  /** 会话当前 cwd(W-2:setCwd 空闲期改写;快照 meta.cwd / 恢复对账锚随之) */
  #cwd: string;
  readonly #rebindCwd: (cwd: string) => ToolExecutor;
  readonly #onCwdChanged: ((change: SessionCwdChange) => void | Promise<void>) | undefined;
  /** W-3:告警通道(sdk 源提示单一出口) */
  readonly #warn: (warning: SdkWarning) => void;
  readonly #hasWarningHandler: boolean;
  readonly #maxTurnsPerQuery: number;
  #compaction: QueryCompactionOptions | undefined;
  readonly #attachmentCheckpoints: boolean;
  #attachmentManager: ContextManager | undefined;
  #dormantAttachmentCheckpoint: AttachmentCheckpoint | undefined;
  #modelAlias: string | null;
  #activeModelState: SessionModelState | null = null;
  #activeBinding: ModelBinding | null = null;
  #activeThinking: { budget?: number } | undefined;
  #lastObserved: SessionContextState['lastObserved'] = null;
  #lastUsage: SessionContextState['lastUsage'] = null;
  #fallback: SessionContextState['fallback'] = null;
  readonly #contextListeners = new Set<(state: SessionContextState) => void>();
  #transition: SessionContextState['transition'] = { status: 'idle', target: null };
  #transitionController: AbortController | null = null;
  #transitionBackup: ModelTransitionBackup | null = null;
  readonly #registry: ProviderRegistry | null;
  readonly #onHistoryCommit:
    | ((history: readonly IRMessage[], meta: HistoryCommitMeta) => void | Promise<void>)
    | undefined;
  readonly #ownedResources: OwnedResources;
  readonly #lifecycle = new LifecycleLedger();
  readonly #querySettlements = new Set<Promise<void>>();
  readonly #managerSettlements = new Set<Promise<void>>();
  // 原 query/manager 收尾失败不可由 pending 集合清空洗掉；只记录既有结果，不追加 settle。
  #sourceSettlementFailed = false;
  #capture: SessionCaptureCoordinator | undefined;
  #contextMaterials: ContextMaterialReader | undefined;
  #captureController: AbortController | undefined;
  #archiveBindingController: AbortController | undefined;
  #captureRevision = 0;
  #archiveHistoryPort: ArchiveSessionHistoryPort | undefined;
  #archiveHistoryGeneration = 0;
  #archiveHistoryConfirmed: ArchiveSessionHistoryCommit | null = null;
  #archiveHistoryLatestCommit: ArchiveOriginalHistoryCommit | null = null;
  #archiveStoreObservationFailed = false;
  #archiveRuntime: ArchiveSessionLifecycleRuntime | undefined;
  #archiveFinishing = false;
  #archivePendingTurn: LifecycleTurn | undefined;
  #archivePendingRewrite: LifecycleRewriteWork | undefined;
  #archiveRetryTask: Promise<boolean> | undefined;
  #archiveFailures: SessionLifecycleFailure[] | undefined;
  #archiveUnresolvedFailures: Set<number> | undefined;
  #archiveFailureCount = 0;
  #archiveBufferFailureId: number | undefined;

  #captureSettlement: Promise<void> | undefined;
  readonly #resourceDiagnostics = new ResourceSettlementAccumulator();
  /** RV-3-08:违约收尾快照的屏障(缺省持住;drain({ force }) 释放) */
  readonly #settlementBarrier = createSettlementBarrier();
  #releaseQuerySettlement: (() => void) | undefined;
  #commitTail: Promise<void> = Promise.resolve();
  #commits = 0;
  /** 提交失败后，存储与回调各自要求下一全快照 rewritten:true；在执行时判定以覆盖晚失败。 */
  #storeRewritePending = false;
  #callbackRewritePending = false;
  #generation = 0;
  #retryGeneration = -1;
  #retryAttempt = 0;
  #retryPending: Promise<void> | undefined;
  #storeFlush: Promise<void> | undefined;
  #cleanupTask: Promise<void> | undefined;
  #finished = false;
  /** S-1:快照接线(null = 未接线,相关方法结构化拒绝) */
  readonly #checkpoints: ResolvedCheckpoints | null;
  readonly #onTurnStart: ((prompts: readonly string[]) => void) | undefined;
  readonly #onInputsConsumed: ((texts: readonly string[]) => void) | undefined;
  /** F-2:会话存储(fork() 落新会话记录;undefined = 未接线) */
  readonly #store: SessionStore | undefined;
  /** 平台直连面(令牌档真客户端 / 其余档 fail-fast 占位) */
  readonly #platform: PlatformAudioClient;
  /** 会话预算闸(DEC-RF-29;undefined = 未接闸):账本消费入流事件,闸门选项逐轮透传 runQuery */
  readonly #spendGate: SessionBudgetWiring | undefined;

  #binding: ModelBinding;
  #handle: QueryHandle | null = null;
  #history: IRMessage[];
  /** 本轮经注入屏障递交的 prompt(终态核对 queueInjections 结转用) */
  #injected: string[] = [];
  /** 会话全局单调 seq(入队时打戳,见文件头) */
  #seq = 0;
  #closed = false;
  /** RV-3-02:会话级 signal 的 abort 监听解除器(close 时调用一次;undefined = 未挂 / 已解除) */
  #releaseSignal: (() => void) | undefined;
  /** 当前泵任务(idle() 排空用;无运行中轮为 null) */
  #pumpTask: Promise<void> | null = null;
  /**
   * S-2:空闲期改写操作(compact/checkpoint/restore/deleteCheckpoint)的串行链——
   * 同会话并发调用按到达序依次执行,互不交叉;每个操作起跑时再核对空闲态。
   */
  #idleOps: Promise<void> = Promise.resolve();
  /** 在飞 + 待跑的空闲期操作数(>0 期间 send 缓冲、close 推迟收口、idle 等待) */
  #idleOpDepth = 0;
  /** 空闲期操作进行中抵达的输入(操作链排空后合并起一轮;输入永不丢失,TUI 同律) */
  #bufferedSends: string[] = [];

  constructor(init: AgentSessionInit) {
    this.#capture = init.sdkSessionRuntime?.capture;
    this.#contextMaterials = init.sdkSessionRuntime?.contextMaterials;
    this.#captureController = this.#capture === undefined ? undefined : new AbortController();
    this.#modelAlias = init.modelAlias ?? null;
    const { maxEvents, maxBytes } = init.eventBacklog;
    this.#queue = createEventQueue<KernelEvent>({
      maxEvents,
      maxBytes,
      // 溢出通报 = 一等 session.events_dropped(contract-v0.27,RFC-RF-1;取代 DEC-RF-03 借 turn.error 的过渡形)。
      // sdk 签发方实现规则:包络 seq 借首条被丢事件的 seq 占位(流内 seq 仍单调且唯一,契约只保证 first ≤ last);
      // 上界 Infinity 不可 JSON 化 → 键缺席 = 不限(QA-RFC-05);droppedBytes / maxBytes 为 JSON 文本长度近似度量。
      marker: ({ dropped, droppedBytes, reason, firstDropped, lastDropped }) => ({
        type: 'session.events_dropped',
        droppedCount: dropped,
        reason,
        scope: 'sdk.events',
        droppedBytes,
        firstDroppedSeq: firstDropped.seq,
        lastDroppedSeq: lastDropped.seq,
        ...(Number.isFinite(maxEvents) ? { maxEvents } : {}),
        ...(Number.isFinite(maxBytes) ? { maxBytes } : {}),
        sessionId: init.sessionId,
        seq: firstDropped.seq,
        ts: Date.now(),
        v: 'v0',
        source: 'sdk',
      }),
    });
    init.bindEmit?.((body, source) => this.#push(body, source));
    this.#binding = init.binding;
    this.#executor = init.executor;
    this.#system = init.system;
    this.#prepareApplicationPrompt = init.prepareApplicationPrompt;
    this.#applicationPrompt = Object.freeze({ ...(init.applicationPrompt ?? { policy: 'fallback', source: 'sdk' }) });
    this.#tools = init.tools;
    this.#maxTokens = init.maxTokens;
    this.#thinking = init.thinking;
    this.#sessionId = init.sessionId;
    this.#inputDelivery = new SessionInputDelivery(init.sessionId);
    this.#cwd = init.cwd;
    this.#maxTurnsPerQuery = init.maxTurnsPerQuery;
    this.#compaction = init.compaction;
    this.#registry = init.registry;
    this.#onHistoryCommit = init.onHistoryCommit;
    this.#ownedResources = init.ownedResources ?? new OwnedResources();
    if (init.ownedResources === undefined && init.onEnded !== undefined) this.#ownedResources.add(init.onEnded);
    this.#checkpoints = init.checkpoints;
    this.#onTurnStart = init.onTurnStart;
    this.#onInputsConsumed = init.onInputsConsumed;
    this.#rebindCwd = init.rebindCwd;
    this.#onCwdChanged = init.onCwdChanged;
    this.#warn = init.warn;
    this.#hasWarningHandler = init.hasWarningHandler ?? false;
    this.#store = init.store;
    this.#platform =
      init.platformContext !== undefined
        ? createPlatformAudioClient({
            baseUrl: init.platformContext.baseUrl,
            token: init.platformContext.token,
            ...(init.platformContext.fetchImpl !== undefined ? { fetchImpl: init.platformContext.fetchImpl } : {}),
          })
        : createUnavailablePlatformAudioClient();
    this.#spendGate = init.budget;
    this.#history = init.initialMessages.map((m) => structuredClone(m));
    this.#attachmentCheckpoints = init.store?.attachmentCheckpoints === true || init.restoredAttachmentCheckpoint !== undefined;
    if (init.restoredAttachmentCheckpoint !== undefined) {
      const input = { scope: this.#attachmentScope(init.restoredAttachmentCwd ?? this.#cwd), messages: this.#history,
        ...(init.restoredAttachmentRepaired === true ? { invalidateReferences: true } : {}) };
      let accepted: boolean;
      try {
        let checkpoint = rebindAttachmentCheckpoint(init.restoredAttachmentCheckpoint, input);
        if (init.restoredAttachmentCwd !== undefined && init.restoredAttachmentCwd !== this.#cwd) {
          checkpoint = rebindAttachmentCheckpoint(checkpoint, { ...input, invalidateReferences: true, nextScope: this.#attachmentScope() });
        }
        if (this.#compaction === undefined || this.#binding.contextWindowTokens === undefined) {
          this.#dormantAttachmentCheckpoint = checkpoint; accepted = true;
        } else {
          const manager = resolveCompactionManager(this.#compaction, this.#binding);
          accepted = manager.restoreAttachmentCheckpoint(checkpoint, { scope: this.#attachmentScope(), messages: this.#history });
          this.#attachmentManager = manager;
        }
      }
      catch { throw new TansrSdkError('session_store_corrupted', 'Invalid stored attachment checkpoint.'); }
      if (!accepted) throw new TansrSdkError('session_store_corrupted', 'Stored attachment checkpoint does not match this history or context policy.');
    }
    if (init.signal !== undefined && !init.signal.aborted) {
      const signal = init.signal;
      const onAbort = (): void => this.close();
      signal.addEventListener('abort', onAbort, { once: true });
      this.#releaseSignal = () => {
        this.#releaseSignal = undefined;
        signal.removeEventListener('abort', onAbort);
      };
    }
    this.#push({ type: 'session.created', cwd: init.cwd }, 'sdk');
  }

  get sessionId(): string {
    return this.#sessionId;
  }

  /**
   * 平台直连面(令牌档):`platform.transcribe({ audio, model?, language? })` /
   * `platform.speak({ input, voice?, model?, format? })`——纯 HTTP 客户端直打
   * `/t1/asr`、`/t1/tts`,**不进会话历史、不进事件流**(App 自决是否把转写作为下一条
   * 输入、是否播放合成音频);失败 throw PlatformRequestError(携网关错误码)。托管/
   * 注入档无平台连接:调用即 invalid_options 指引令牌档(fail-fast,恒不静默)。
   */
  get platform(): PlatformAudioClient {
    return this.#platform;
  }

  /**
   * 会话事件流(async iterable;**多消费者广播**,2026-09-02 起)。每次 for-await 即一个
   * 独立订阅者:SessionView 泵、Narrator、宿主自建循环可同时挂,各得全量事件、互不争抢;
   * 首订阅者收到会话创建以来的积压,晚订阅者自订阅点起。跨轮连续,close() 后以
   * session.ended 收尾并正常终结(各订阅者排空后 for-await 自然退出)。
   *
   * 积压有界:首订阅者到来前的积压受 `CreateSessionOptions.events` 的条数 / 近似字节
   * 上界约束(缺省 1000 条 / 2 MiB),超界丢最旧并以恰一条 `session.events_dropped{scope:'sdk.events',
   * droppedCount, reason, …}` 占队首通报(contract-v0.27);上界以内全量积压照旧。
   * 直播后订阅者全部离场期间推送的事件不保留(晚订阅者自当前起,既有语义)。
   */
  get events(): AsyncIterable<KernelEvent> {
    return this.#queue.iterable;
  }

  /** 下轮模型档快照；active=true 优先取本轮冻结绑定，空闲时取下轮档。 */
  currentModel(options: { active?: boolean } = {}): ResolvedModel {
    return publicModel(options.active ? (this.#activeBinding ?? this.#binding).model : this.#binding.model);
  }

  /** 即时快照：在飞执行/下轮选择与已观察模型分开，token占用恒非累计计费量。 */
  contextState(): SessionContextState {
    return structuredClone({
      selected: this.#modelState(this.#binding, this.#modelAlias), active: this.#activeModelState,
      lastObserved: this.#lastObserved, lastUsage: this.#lastUsage, fallback: this.#fallback,
      budget: this.#budget(this.#activeBinding ?? this.#binding, this.#handle?.messages() ?? this.#history,
        this.#compaction, (this.#activeBinding === null ? this.#thinking : this.#activeThinking) ?? null),
      sampledAt: Date.now(), history: this.#handle === null ? 'committed' : 'live', transition: this.#transition,
    });
  }

  /** 首次同步通知；返回函数解除订阅。观察器失败不影响会话。 */
  subscribeContext(listener: (state: SessionContextState) => void): () => void {
    this.#assertOpen('subscribeContext');
    this.#contextListeners.add(listener);
    try { listener(this.contextState()); } catch { /* 开发者观察器隔离 */ }
    return () => { this.#contextListeners.delete(listener); };
  }

  /** @internal 当前有效历史的纯同步观察；不安装 historyPort、不确认/重试 Store。 */
  withArchiveSourceObservation<T>(work: (lease: ArchiveSourceReadLease) => T): T {
    const history = this.#history, generation = this.#generation, binding = this.#binding;
    const captureRevision = this.#captureRevision;
    const assertIdle = (): void => {
      if (this.#closed || this.#finished || this.#handle !== null || this.#pumpTask !== null || this.#idleOpDepth > 0 ||
        this.#querySettlements.size > 0 || this.#managerSettlements.size > 0 || this.#commits > 0 || this.#lifecycle.failed || this.#sourceSettlementFailed ||
        this.#captureSettlement !== undefined || this.#retryPending !== undefined || this.#storeFlush !== undefined ||
        this.#archiveBindingController !== undefined || this.#archiveFinishing || this.#archivePendingTurn !== undefined ||
        this.#archivePendingRewrite !== undefined || this.#archiveRetryTask !== undefined || this.#archiveStoreObservationFailed ||
        this.#storeRewritePending || (this.#store === undefined && this.#callbackRewritePending) ||
        (this.#archiveUnresolvedFailures?.size ?? 0) > 0 || this.#history !== history || this.#generation !== generation ||
        this.#binding !== binding || this.#captureRevision !== captureRevision)
        throw new Error('Archive source observation requires an idle, settled session');
    };
    return withArchiveSourceReadLease(this, () => observeArchiveSession(this.#sessionId, this.#history, this.#generation), assertIdle, work);
  }

  /** @internal 新扩展首次晚绑定；共享原空闲操作队列，用户文本在等待期间正常排队。 */
  withArchiveBinding<T>(work: ArchiveSessionBindingWork<T>): Promise<T> {
    return this.#withArchiveBinding(work);
  }

  /** @internal 缓存宿主沿原队列确认历史；相同原史的确认不失效附件引用。 */
  withCacheHistoryBinding<T>(work: ArchiveSessionBindingWork<T>): Promise<T> {
    return this.#archiveRuntime === undefined
      ? this.#withArchiveBinding(work, undefined, false, true)
      : this.withArchiveCommittedRead(work);
  }

  /** @internal 删除前只观察真实终态，不在 Store 锁中反向等待会话。 */
  cacheHistoryDeletionReady(): boolean {
    const state = this.#drainResult();
    return this.#closed && this.#finished && state.status === 'completed' &&
      Object.values(state.pending).every(count => count === 0) && !this.#sourceSettlementFailed;
  }

  /** @internal 关闭介质只需真实排空；持久确认失败仍留在磁盘供下一宿主核实。 */
  cacheResourcesSettled(): boolean {
    return this.#closed && this.#finished && !this.#sourceSettlementFailed &&
      Object.values(this.#drainResult().pending).every(count => count === 0);
  }

  /** @internal 只读已确认历史；不等待或补写未决 Store，不安装候选，异步读取期间保留排他租约。 */
  withArchiveCommittedRead<T>(work: ArchiveSessionBindingWork<T>): Promise<T> {
    try {
      this.withArchiveSourceObservation(() => undefined);
      if (this.#archiveRuntime === undefined || this.#archiveHistoryPort === undefined) throw new Error('Archive committed read requires installed history');
      return this.#withArchiveBinding(work, undefined, true);
    } catch (error) { return Promise.reject(error); }
  }

  /** @internal 显式冷恢复；保留原提交事实，不重写历史或伪造热 Store Promise。 */
  withArchiveColdBinding<T>(confirmation: ArchiveColdHistoryConfirmation, work: ArchiveSessionBindingWork<T>): Promise<T> {
    if (confirmation === undefined) return Promise.reject(new Error('Archive cold binding requires a confirmation'));
    return this.#withArchiveBinding(work, confirmation);
  }

  #withArchiveBinding<T>(work: ArchiveSessionBindingWork<T>, confirmation?: ArchiveColdHistoryConfirmation, readOnly = false, preserveAttachments = false): Promise<T> {
    if (this.#closed || this.#handle !== null || this.#idleOpDepth > 0 || !this.#archiveReady()) return Promise.reject(new Error('Archive binding requires an idle session'));
    const controller = new AbortController(); this.#archiveBindingController = controller;
    const signal = controller.signal; let valid = true;
    let originalValue: T | undefined; let valueStarted = false;
    // 新扩展的预期拒绝不污染旧SDK的持久化失败账本。
    return this.#runIdleOp(async () => {
      try {
        if (!readOnly) {
          await raceAbort(Promise.all([...this.#querySettlements, ...this.#managerSettlements, this.#commitTail,
            ...(this.#captureSettlement === undefined ? [] : [this.#captureSettlement])]), signal);
          if (this.#capture !== undefined) await raceAbort(this.#capture.settle(), signal);
        }
        if (signal.aborted || this.#closed || this.#handle !== null) throw new Error('Archive binding cancelled');
        if (this.#storeRewritePending || (this.#store === undefined && this.#callbackRewritePending)) throw new Error('Archive binding requires confirmed legacy history');
        if (!readOnly && confirmation === undefined && this.#store !== undefined) await raceAbort(this.#prepareArchiveHistoryPort(preserveAttachments), signal);
        const bind = (witness?: ArchiveColdHistoryWitness): T | Promise<T> => {
          if (signal.aborted || this.#closed || this.#handle !== null) throw new Error('Archive binding cancelled');
          const history = this.#history; const generation = this.#captureRevision; const binding = this.#binding;
          const observation = observeArchiveSession(this.#sessionId, history, generation);
          let checkInstallation = false;
          let installedRuntime = this.#archiveRuntime, installedCapture = this.#capture, installedMaterials = this.#contextMaterials;
          const assertCurrent = (): void => {
            if ((checkInstallation && (this.#archiveRuntime !== installedRuntime || this.#capture !== installedCapture || this.#contextMaterials !== installedMaterials)) ||
              !valid || signal.aborted || this.#closed || this.#history !== history || this.#captureRevision !== generation ||
              this.#binding !== binding || this.#handle !== null ||
              observeArchiveSession(this.#sessionId, this.#history, generation).sourceSnapshotDigest !== observation.sourceSnapshotDigest) throw new Error('Archive binding source changed');
          };
          const lease = { observation, signal, assertCurrent,
            ...(this.#archiveHistoryPort === undefined ? {} : { historyPort: this.#archiveHistoryPort }) };
          const install = (): T | Promise<T> => {
            assertCurrent(); const candidate = snapshotArchiveBindingCandidate(work(lease));
            originalValue = candidate.value; valueStarted = true;
            if (readOnly) {
              // work可能在开始真实读取后同步关闭；先接住原Promise并持有到落定，再核lease。
              return (async () => {
                const value = await candidate.value;
                assertCurrent();
                if (Object.keys(candidate).some(key => key !== 'value')) throw new Error('Archive committed read cannot install state');
                return value;
              })();
            }
            assertCurrent();
            if (candidate.installation !== undefined) assertPreparedArchiveInstallation(candidate.installation, { lease, storeIdentity: this.#store!, contextMaterials: candidate.contextMaterials ?? this.#contextMaterials });
            const retryInstallation = candidate.installation !== undefined && this.#archiveRuntime !== undefined;
            if ((candidate.capture !== undefined && this.#capture !== undefined && candidate.capture !== this.#capture) ||
              (candidate.contextMaterials !== undefined && this.#contextMaterials !== undefined && candidate.contextMaterials !== this.#contextMaterials)) throw new Error('Archive binding cannot replace existing ownership');
            if (candidate.lifecycle !== undefined && ((!retryInstallation && (this.#archiveRuntime !== undefined || this.#capture !== undefined)) ||
              candidate.capture !== undefined || this.#store === undefined)) throw new Error('Complete lifecycle requires exclusive original Store ownership');
            assertArchiveBindingCaptureReady(candidate.capture); assertCurrent();
            if (candidate.lifecycle !== undefined) {
              if (retryInstallation) {
                checkInstallation = true;
                const runtime = assertArchiveSessionLifecycleInstalled(candidate.lifecycle, { lease, storeIdentity: this.#store! });
                if (runtime !== this.#archiveRuntime) throw new Error('Archive installation runtime ownership changed');
              } else {
                const runtime = claimArchiveSessionLifecycleBundle(candidate.lifecycle, { lease, storeIdentity: this.#store! });
                this.#archiveRuntime = runtime; this.#capture = runtime.capture; this.#captureController = new AbortController();
                this.#archiveFailures = []; this.#archiveUnresolvedFailures = new Set();
              }
            }
            if (candidate.capture !== undefined && this.#capture === undefined) {
              this.#capture = candidate.capture; this.#captureController = new AbortController();
            }
            if (candidate.contextMaterials !== undefined && !retryInstallation) this.#contextMaterials = candidate.contextMaterials;
            if (candidate.installation !== undefined) {
              installedRuntime = this.#archiveRuntime; installedCapture = this.#capture; installedMaterials = this.#contextMaterials; checkInstallation = true;
              markArchiveSessionLifecycleInstalled(candidate.lifecycle!, { lease, storeIdentity: this.#store!, runtime: this.#archiveRuntime! });
              confirmPreparedArchiveInstallation(candidate.installation, candidate.lifecycle!, lease, this.#contextMaterials);
              assertCurrent();
            }
            return candidate.value;
          };
          return witness === undefined ? install() : withArchiveColdHistoryLease(witness, lease, install);
        };
        const value = confirmation === undefined ? bind() : await raceAbort(withArchiveColdHistoryConfirmation(confirmation,
          { storeIdentity: this.#store!, sessionId: this.#sessionId }, (details, witness) => {
            if (signal.aborted || this.#closed || this.#handle !== null || this.#store === undefined ||
              this.#archiveHistoryLatestCommit !== null || this.#archiveStoreObservationFailed)
              throw new Error('Archive cold binding requires an unchanged, settled original Store');
            const actual = observeArchiveSession(this.#sessionId, this.#history, this.#archiveHistoryGeneration);
            if (actual.sourceSnapshotDigest !== details.history.observation.sourceSnapshotDigest)
              throw new Error('Archive cold binding source changed');
            this.#initializeArchiveHistoryPort();
            // 持久旧 N 留在共享冷映射；该端口只呈现新宿主的真实进程内代际。
            this.#archiveHistoryConfirmed = { commitId: details.history.commitId, observation: actual };
            return bind(witness);
          }), signal);
        // 回调候选必须同步；其 value 可以是异步工作，原排他区必须等实际完成。
        return { ok: true as const, value: await value };
      } catch (error) {
        // 同步close/取消可在候选返回后使校验失败；已启动原工作仍必须实际完成才释放排他区。
        if (valueStarted) { try { await originalValue; } catch { /* 保留首先失效/取消的原因。 */ } }
        return { ok: false as const, error };
      }
      finally { valid = false; if (this.#archiveBindingController === controller) this.#archiveBindingController = undefined; }
    }, true).then(result => { if (!result.ok) throw result.error; return result.value; });
  }

  /** @internal 原关闭排空后解除本安装；保留会话、历史与原输入，失败继续保门。 */
  withArchiveDetachment<T>(work: ArchiveSessionDetachmentWork<T>): Promise<T> {
    if (this.#closed || this.#handle !== null || this.#idleOpDepth > 0 || this.#archiveHistoryPort === undefined)
      return Promise.reject(new Error('Archive detachment requires an idle registered session'));
    stopArchiveInstallationAdmission(this.#store, this.#sessionId);
    const controller = new AbortController(); this.#archiveBindingController = controller;
    const signal = controller.signal; let valid = true;
    return this.#runIdleOp(async () => {
      try {
        await raceAbort(Promise.all([...this.#querySettlements, ...this.#managerSettlements, this.#commitTail,
          ...(this.#captureSettlement === undefined ? [] : [this.#captureSettlement])]), signal);
        if (this.#archiveRuntime !== undefined && (await raceAbort(this.#archiveRuntime.rewrite.settle(), signal)).status !== 'completed')
          throw new Error('Archive retirement requires settled resources');
        if (this.#capture !== undefined) { await raceAbort(this.#capture.settle(), signal); assertSessionCaptureSettled(this.#capture); }
        if (this.#contextMaterials !== undefined && (await raceAbort(this.#contextMaterials.settle(), signal)).status !== 'completed')
          throw new Error('Archive retirement requires settled materials');
        if (this.#sourceSettlementFailed || this.#storeRewritePending || this.#archiveStoreObservationFailed ||
          this.#archivePendingTurn !== undefined || this.#archivePendingRewrite !== undefined || this.#archiveRetryTask !== undefined)
          throw new Error('Archive retirement requires confirmed legacy history');
        const history = this.#history, generation = this.#captureRevision, binding = this.#binding;
        const runtime = this.#archiveRuntime, capture = this.#capture, reader = this.#contextMaterials, port = this.#archiveHistoryPort!;
        if (runtime === undefined ? capture !== undefined || reader !== undefined : capture !== runtime.capture)
          throw new Error('Archive detachment cannot remove foreign resources');
        const observation = observeArchiveSession(this.#sessionId, history, generation);
        const assertCurrent = (): void => {
          if (!valid || signal.aborted || this.#closed || this.#handle !== null || this.#history !== history ||
            this.#captureRevision !== generation || this.#binding !== binding || this.#archiveRuntime !== runtime ||
            this.#capture !== capture || this.#contextMaterials !== reader || this.#archiveHistoryPort !== port ||
            observeArchiveSession(this.#sessionId, this.#history, generation).sourceSnapshotDigest !== observation.sourceSnapshotDigest)
            throw new Error('Archive detachment source changed');
        };
        const lease = { observation, signal, assertCurrent, historyPort: port };
        const value = executeArchiveDetachment(lease, { runtime: runtime ?? null, contextMaterials: reader,
          prepareDetach: assertCurrent,
          detachOwnedState: () => {
            this.#archiveRuntime = undefined; this.#capture = undefined; this.#captureController = undefined;
            this.#contextMaterials = undefined; this.#archiveHistoryPort = undefined;
            this.#archiveHistoryConfirmed = null; this.#archiveHistoryLatestCommit = null;
          },
          assertDetached: () => {
            if (this.#archiveRuntime !== undefined || this.#capture !== undefined || this.#contextMaterials !== undefined || this.#archiveHistoryPort !== undefined)
              throw new Error('Archive detachment is incomplete');
          },
        }, work);
        return { ok: true as const, value };
      } catch (error) { return { ok: false as const, error }; }
      finally { valid = false; if (this.#archiveBindingController === controller) this.#archiveBindingController = undefined; }
    }, true, true).then(result => { if (!result.ok) throw result.error; return result.value; });
  }

  /** 仅内部晚绑定启用；create 元数据及 resume 原始输入都不是当前历史提交证明。 */
  async #prepareArchiveHistoryPort(preserveAttachments = false): Promise<void> {
    const store = this.#store!;
    assertArchiveRecoveryInstallation(store, this.#sessionId, this.#archiveRuntime, true);
    this.#initializeArchiveHistoryPort();
    const observation = this.#archiveHistoryObservation(this.#history)!;
    if (this.#archiveHistoryConfirmed?.observation.sourceSnapshotDigest === observation.sourceSnapshotDigest &&
      this.#archiveHistoryConfirmed.observation.localHistoryGeneration === observation.localHistoryGeneration) return;
    const history = this.messages(); const id = this.#lifecycle.operation();
    await this.#enqueueCommit(async () => {
      try {
        const prior = preserveAttachments ? await store.get(this.#sessionId) : null;
        const unchanged = prior !== null && observeArchiveSession(this.#sessionId, prior.messages, 0).sourceSnapshotDigest === observation.sourceSnapshotDigest;
        await this.#commitArchiveStore(history, { rewritten: !unchanged }, observation);
        this.#storeRewritePending = false;
        this.#lifecycle.recover('store_commit', id);
      } catch (cause) {
        this.#storeRewritePending = true;
        this.#lifecycle.fail(id, 'store_commit', cause);
        throw cause;
      }
    });
  }

  /** 端口创建没有 IO；热确认与品牌冷映射各自提供原事实。 */
  #initializeArchiveHistoryPort(): void {
    const store = this.#store!;
    if (this.#archiveHistoryPort === undefined) {
      this.#archiveHistoryPort = Object.freeze({ storeIdentity: store,
        latestCommit: () => this.#archiveHistoryLatestCommit, read: () => {
        const observation = this.#archiveHistoryObservation(this.#history)!;
        const confirmed = this.#archiveHistoryConfirmed;
        const current = confirmed !== null && confirmed.observation.sessionId === observation.sessionId &&
          confirmed.observation.sourceSnapshotDigest === observation.sourceSnapshotDigest &&
          confirmed.observation.localHistoryGeneration === observation.localHistoryGeneration;
        return { observation, confirmedCommit: current ? structuredClone(confirmed) : null };
      } });
    }
  }

  /** 默认路径不散列；配置代际与已发布历史代际保持独立。 */
  #archiveHistoryObservation(history: readonly IRMessage[]): ArchiveSessionObservation | undefined {
    if (this.#archiveHistoryPort === undefined) return undefined;
    const observation = observeArchiveSession(this.#sessionId, history, this.#archiveHistoryGeneration);
    if (history !== this.#history && observation.sourceSnapshotDigest !==
      observeArchiveSession(this.#sessionId, this.#history, this.#archiveHistoryGeneration).sourceSnapshotDigest) {
      return Object.freeze({ ...observation, localHistoryGeneration: this.#archiveHistoryGeneration + 1 });
    }
    return observation;
  }

  #replaceHistory(history: IRMessage[]): void {
    if (this.#archiveHistoryPort !== undefined) {
      this.#archiveHistoryGeneration = this.#archiveHistoryObservation(history)!.localHistoryGeneration;
    }
    this.#history = history;
  }

  #attachmentScope(cwd = this.#cwd): string {
    // 本地嵌入式 Store 是宿主权威；不消费可编辑 meta 或历史里自报的身份。
    return JSON.stringify(['sdk-attachments/1', this.#sessionId, cwd]);
  }

  #resolveAttachmentManager(compaction: QueryCompactionOptions, binding = this.#binding): ContextManager {
    const manager = resolveCompactionManager(compaction, binding);
    if (!this.#attachmentCheckpoints) return manager;
    const previous = this.#attachmentManager;
    if ((previous !== undefined && manager !== previous) || this.#dormantAttachmentCheckpoint !== undefined) {
      const input = { scope: this.#attachmentScope(), messages: this.#history };
      if (!manager.restoreAttachmentCheckpoint(this.#dormantAttachmentCheckpoint ?? previous!.exportAttachmentCheckpoint(input), input)) {
        throw new TansrSdkError('invalid_options', 'Attachment context policy changed without a compatible checkpoint.');
      }
    }
    this.#attachmentManager = manager;
    this.#dormantAttachmentCheckpoint = undefined;
    return manager;
  }

  #attachmentCommitMeta(history: readonly IRMessage[], meta: Parameters<SessionStore['commit']>[2]): Parameters<SessionStore['commit']>[2] {
    if (!this.#attachmentCheckpoints || Object.hasOwn(meta, 'attachmentCheckpoint')) return meta;
    if (this.#dormantAttachmentCheckpoint !== undefined) {
      this.#dormantAttachmentCheckpoint = rebindAttachmentCheckpoint(this.#dormantAttachmentCheckpoint,
        { scope: this.#attachmentScope(), messages: history, invalidateReferences: true });
      return { ...meta, attachmentCheckpoint: this.#dormantAttachmentCheckpoint };
    }
    if (this.#attachmentManager === undefined) return { ...meta, attachmentCheckpoint: undefined };
    // 任意历史重写保守失效引用，累计字节不变；不能给已删除的正文继续签引用。
    if (meta.rewritten) this.#attachmentManager.runPostCompactInvalidations();
    const attachmentCheckpoint: AttachmentCheckpoint = this.#attachmentManager.exportAttachmentCheckpoint(
      { scope: this.#attachmentScope(), messages: history });
    if (attachmentCheckpoint.injectedBytes === 0 && attachmentCheckpoint.rejectedCount === 0 && attachmentCheckpoint.entries.length === 0) return { ...meta, attachmentCheckpoint: undefined };
    return { ...meta, attachmentCheckpoint };
  }

  /** 只观察原 Store Promise；回调、commitTail 的吞错与 flush 均不能铸造成功。 */
  #commitArchiveStore(history: readonly IRMessage[], meta: Parameters<SessionStore['commit']>[2],
    observation: ArchiveSessionObservation | undefined, commitId?: string,
    onReceipt?: (receipt: ArchiveOriginalHistoryCommit) => void): Promise<void> {
    meta = this.#attachmentCommitMeta(history, meta);
    if (this.#archiveHistoryPort === undefined) return this.#store!.commit(this.#sessionId, history, meta);
    if (this.#archiveStoreObservationFailed) throw new Error('Archive Store completion remains unobservable');
    const commit = { commitId: commitId ?? randomUUID(), observation: observation! };
    this.#archiveHistoryConfirmed = null;
    this.#archiveHistoryLatestCommit = null;
    let original: Promise<void>;
    try { original = this.#store!.commit(this.#sessionId, history, meta); }
    catch (error) {
      // 同步抛错也可能已经启动写者；没有返回原完成Promise就不能再起写入。
      this.#archiveStoreObservationFailed = true;
      throw error;
    }
    let receipt: ArchiveOriginalHistoryCommit;
    try { receipt = observeArchiveHistoryCommit({ ...commit, storeIdentity: this.#store!, original }); }
    catch (error) {
      // 订阅失败不证明原写者结束；不能再起一次写入覆盖未知的旧写者。
      this.#archiveStoreObservationFailed = true;
      throw error;
    }
    this.#archiveHistoryLatestCommit = receipt;
    onReceipt?.(receipt);
    return receipt.completion.then(() => {
      this.#archiveHistoryConfirmed = structuredClone(commit);
    });
  }

  /** 最近一次迁移前原文深拷贝；摘要有损，原件可另行导出/恢复。 */
  modelTransitionBackup(): ModelTransitionBackup | null {
    return this.#transitionBackup === null ? null : structuredClone(this.#transitionBackup);
  }

  #modelState(binding: ModelBinding, alias: string | null): SessionModelState {
    return { model: publicModel(binding.model), alias, contextWindowTokens: binding.contextWindowTokens ?? null };
  }

  #budget(binding: ModelBinding, messages: readonly IRMessage[], compaction = this.#compaction, thinking = this.#thinking ?? null) {
    return measureContextBudget({ system: this.#system, tools: this.#tools, messages }, {
      contextWindowTokens: binding.contextWindowTokens, maxOutputTokens: binding.maxOutputTokens,
      requestedOutputTokens: this.#maxTokens, thinkingBudgetTokens: thinking?.budget,
      thresholds: compaction?.thresholds, resolvedThresholds: compaction?.manager?.thresholds,
      estimator: compaction?.manager?.requestEstimator ?? compaction?.estimator,
      settingsSource: compaction?.manager !== undefined ? 'external_manager' :
        compaction?.thresholds !== undefined ? 'configured' : binding.contextWindowTokens !== undefined ? 'model' : 'unknown',
    });
  }

  #notifyContext(): void {
    for (const listener of this.#contextListeners) {
      try { listener(this.contextState()); } catch { /* 观察器不拥有查询状态机 */ }
    }
  }

  #resolveNextBinding(next: string | SetModelBinding): ModelBinding {
    if (typeof next !== 'string') return { client: next.client, model: publicModel(next.model),
      contextWindowTokens: next.contextWindowTokens, maxOutputTokens: next.maxOutputTokens };
    if (this.#registry === null) throw new TansrSdkError('invalid_options', 'setModel(alias) requires a managed session; injected sessions must pass { client, model }.');
    return buildClientFromRegistry(this.#registry, next);
  }

  #managerCompatible(binding: ModelBinding, compaction: QueryCompactionOptions | undefined, explicit: boolean): boolean {
    const manager = compaction?.manager;
    if (manager === undefined) return true;
    if (binding.contextWindowTokens === undefined || manager.thresholds.contextWindow > binding.contextWindowTokens) return false;
    return explicit || (binding.client === this.#binding.client && binding.model.provider === this.#binding.model.provider && binding.model.model === this.#binding.model.model);
  }

  running(): boolean {
    return this.#handle !== null;
  }

  inputCapabilities(): SessionInputCapabilities { return this.#inputDelivery.inputCapabilities(); }
  getInputTarget(): SessionInputTarget | null { return this.#inputDelivery.getInputTarget(); }
  submitInput(input: SessionInputSubmission): Promise<SessionInputResult> { return this.#inputDelivery.submitInput(input); }
  getInputStatus(inputId: string, target: SessionInputTarget): SessionInputReceipt | null {
    return this.#inputDelivery.getInputStatus(inputId, target);
  }

  /** 会话历史快照(深拷贝;可直接作恢复/续跑的 initialMessages) */
  messages(): IRMessage[] {
    return structuredClone(this.#history);
  }

  /**
   * 历史分页读:消息索引口径的 [offset, offset+limit) 切片,
   * 与 messages() 同一投影口径(同一份历史快照、同为深拷贝;运行中亦可读,返回轮起
   * 时的历史)。切片恒**配对完整**:起点向前对齐到干净边界并回填 `offset`,终点向后
   * 对齐(limit 是下限,为凑齐工具轮可多返回几条)。缺省全量 = 与 messages() 字节等价;
   * `limit: 0` 只取 total;offset 越界 → 空页且 offset 回填 total。休眠会话(未起
   * AgentSession)请用 readHistoryPage(store, sessionId, page)。
   */
  async history(options: HistoryPageOptions = {}): Promise<HistoryPage> {
    const { offset, limit } = normalizeHistoryPageOptions(options, 'history()');
    return sliceHistoryPage(this.messages(), offset, limit);
  }

  /**
   * 提交输入:空闲起新轮;运行中经内核注入屏障(下一 assembling 前消费,
   * 自然终态未消费的旧注入结转为下一轮开场输入；中断/硬限制不结转)。close 后调用属编程错误,
   * 结构化 throw(不静默吞输入)。
   */
  send(prompt: string): void {
    assertArchiveRecoveryInstallation(this.#store, this.#sessionId, this.#archiveRuntime);
    assertArchiveInstallationAdmission(this.#store, this.#sessionId, this.#archiveRuntime);
    if (this.#closed) {
      throw new TansrSdkError('session_closed', 'send() called on a closed session.');
    }
    if (this.#archiveFinishing || (this.#archiveRuntime !== undefined && this.#handle === null && !this.#archiveReady())) {
      this.#bufferedSends.push(prompt); return;
    }
    if (this.#handle !== null) {
      this.#injected.push(prompt);
      this.#handle.enqueueUserInput(prompt);
      return;
    }
    if (this.#idleOpDepth > 0) {
      // S-2:compact()/restore() 进行中——历史即将被替换,缓冲到操作链排空后以
      // 新历史起轮(与 TUI 空闲直压期缓冲输入同律;不静默丢、不起用旧历史)
      this.#bufferedSends.push(prompt);
      return;
    }
    assertRequiredContextMaterialsReady(this.#contextMaterials);
    this.#startTurn([prompt]);
  }

  /** 图文仅从空闲起新轮；拒绝时零消费，宿主保留原草稿后重试。 */
  sendBlocks(input: readonly SessionUserBlock[]): SendBlocksResult {
    assertArchiveRecoveryInstallation(this.#store, this.#sessionId, this.#archiveRuntime);
    assertArchiveInstallationAdmission(this.#store, this.#sessionId, this.#archiveRuntime);
    this.#assertOpen('sendBlocks');
    if (this.#archiveFinishing || (this.#archiveRuntime !== undefined && this.#handle === null && !this.#archiveReady())) return { status: 'rejected', reason: 'operation_running' };
    if (this.#handle !== null) return { status: 'rejected', reason: 'turn_running' };
    if (this.#idleOpDepth > 0) return { status: 'rejected', reason: 'operation_running' };
    const parsed = validateUserBlocks(input);
    if ('status' in parsed) return parsed;
    // 已启用压缩时由查询环治理旧史；这里只拒绝新输入自身已放不下的情况。
    // 否则图文会在进入压缩环之前被旧史水位拒绝，与send文本行为分叉。
    const messages: IRMessage[] = [...(this.#compaction ? [] : this.#history), { role: 'user', blocks: parsed.blocks }];
    if (this.#budget(this.#binding, messages).fits === false) return { status: 'rejected', reason: 'context_budget_exceeded' };
    // 旧文本回调继续收到文本投影；模型与历史收到同一条完整图文消息。
    const text = parsed.blocks.filter(block => block.t === 'text').map(block => block.text).join('\n');
    assertRequiredContextMaterialsReady(this.#contextMaterials);
    this.#startTurn([text], parsed.blocks);
    return { status: 'started' };
  }

  /** 中断当前轮(优雅:事件流走到 turn.aborted);空闲为无害空操作;已关闭 → session_closed(全表同律) */
  interrupt(): void {
    this.#assertOpen('interrupt');
    this.#transitionController?.abort();
    this.#handle?.abort();
  }

  /**
   * 模型切换(对后续轮生效;运行中轮不受影响):
   * - 字符串(别名/ref):仅托管模式可用,经装配期 registry 重新解析构造
   *   (解析失败抛 TansrSdkError('assembly_failed'),会话状态不变);
   * - SetModelBinding:client 与 model 成对更换(跨 provider 时旧 client
   *   不可复用);contextWindowTokens 不传则清空(宁缺毋错)。
   * 已关闭会话上调用 → session_closed(先于别名解析:不构造任何新 client,currentModel() 不变)。
   */
  setModel(next: string | SetModelBinding): void {
    this.#assertOpen('setModel');
    if (this.#archiveRuntime !== undefined && !this.#archiveReady()) throw this.#turnRunning('setModel()');
    this.#capture?.assertReady();
    if (this.#idleOpDepth > 0) throw new TansrSdkError('turn_running', 'An idle operation is in progress; await session.idle() before changing models.');
    const binding = this.#resolveNextBinding(next);
    if (!this.#managerCompatible(binding, this.#compaction, false)) {
      throw new TansrSdkError('invalid_options', 'The external compaction manager does not match this model. Use switchModel() with a compatible compaction binding.');
    }
    const history = this.#handle?.messages() ?? this.#history;
    const shrink = binding.contextWindowTokens !== undefined &&
      (this.#binding.contextWindowTokens === undefined || binding.contextWindowTokens < this.#binding.contextWindowTokens);
    if ((shrink && this.#handle !== null) || (history.length > 0 &&
      (this.#budget(binding, history).fits === false ||
        (this.#binding.contextWindowTokens !== undefined && binding.contextWindowTokens === undefined)))) {
      throw new TansrSdkError('invalid_options', 'This model change requires a context transition. Await session.switchModel() while idle; the current model and history are unchanged.');
    }
    this.#binding = binding;
    this.#modelAlias = typeof next === 'string' ? next : null;
    if (this.#capture !== undefined) this.#captureRevision++;
    this.#notifyContext();
  }

  /**
   * 空闲期安全切模：完整备份→源窗/分段摘要→目标预算验证→持久化→原子提交。
   * 无store时保留内存原文；失败/取消不切模型、不换历史。新输入沿idle操作队列缓冲。
   */
  async switchModel(next: string | SetModelBinding, options: SwitchModelOptions = {}): Promise<SwitchModelResult> {
    this.#assertOpen('switchModel');
    return this.#runIdleOp(async (): Promise<SwitchModelResult> => {
      if (this.#handle !== null) return { status: 'rejected', reason: 'turn_running' };
      if (this.#closed || options.signal?.aborted) return { status: 'rejected', reason: 'cancelled' };
      const binding = this.#resolveNextBinding(next);
      const compaction = options.compaction ?? this.#compaction;
      if (!this.#managerCompatible(binding, compaction, options.compaction !== undefined)) {
        return { status: 'rejected', reason: 'manager_incompatible' };
      }
      const controller = new AbortController();
      this.#transitionController = controller;
      const signal = options.signal === undefined ? controller.signal : AbortSignal.any([controller.signal, options.signal]);
      const from = publicModel(this.#binding.model);
      let checkpointId: string | undefined;
      this.#transition = { status: 'preparing', target: publicModel(binding.model) };
      this.#notifyContext();
      const resources = new ResourceSettlementScope();
      let rewrite: LifecycleRewriteWork | undefined;
      try {
        await this.#commitTail;
        if (signal.aborted || this.#closed) return { status: 'rejected', reason: 'cancelled' };
        const original = this.messages();
        rewrite = this.#archiveRuntime === undefined ? undefined : this.#beginArchiveRewrite('model-transition', resources, signal);
        const publication = this.#capture === undefined ? undefined : await this.#prepareHistoryCapture(signal, rewrite?.ticket);
        const targetBudget = this.#budget(binding, original, compaction);
        if (targetBudget.fits !== true && original.length > 0) {
          this.#transitionBackup = { model: from, messages: structuredClone(original), createdAt: Date.now() };
          if (this.#checkpoints !== null) {
            try {
              const checkpoint = await this.#writeCheckpoint(this.#checkpoints.store, 'pre_compaction', original, 'Before model transition');
              checkpointId = checkpoint.checkpointId;
              this.#transitionBackup.checkpointId = checkpointId;
            } catch { return { status: 'rejected', reason: 'checkpoint_failed' }; }
          }
        }
        if (signal.aborted || this.#closed) return { status: 'rejected', reason: 'cancelled', ...(checkpointId ? { checkpointId } : {}) };
        const result = await prepareContextTransition({
          view: { system: this.#system, tools: this.#tools, messages: original },
          source: this.#binding,
          target: { contextWindowTokens: binding.contextWindowTokens, maxOutputTokens: binding.maxOutputTokens,
            requestedOutputTokens: this.#maxTokens, thinkingBudgetTokens: this.#thinking?.budget,
            thresholds: compaction?.thresholds, resolvedThresholds: compaction?.manager?.thresholds,
            estimator: compaction?.manager?.requestEstimator ?? compaction?.estimator },
          signal, timeoutMs: options.timeoutMs, maxCalls: options.maxSummaryCalls, resources,
          // 摘要成本与采纳独立；失败/取消亦报告已实际发生的用量。
          onUsage: usage => this.#push({ type: 'cost.usage.updated', model: from.model, provider: from.provider,
            purpose: 'compaction', usage,
            ...(this.#binding.contextWindowTokens !== undefined ? { contextWindowTokens: this.#binding.contextWindowTokens } : {}) }, 'sdk'),
        });
        if (result.status === 'rejected') return { status: 'rejected', reason: result.reason, ...(checkpointId ? { checkpointId } : {}) };
        if (signal.aborted || this.#closed) return { status: 'rejected', reason: 'cancelled', ...(checkpointId ? { checkpointId } : {}) };
        const compacted = result.summaryCalls > 0;
        const publish = (): void => {
          publication?.assertSourceCurrent();
          this.#replaceHistory(result.messages);
          this.#binding = binding;
          this.#modelAlias = typeof next === 'string' ? next : null;
          this.#compaction = compaction;
          if (publication !== undefined) { this.#captureRevision++; publication.accept(); }
          compaction?.manager?.bindRequestModel(binding.model, binding.client);
          compaction?.manager?.invalidateRequestBasis();
        };
        if (rewrite !== undefined) {
          const commit = await this.#persistArchiveRewrite(rewrite, result.messages, 'compaction', publish);
          commit();
          await this.#notifyArchiveHistoryCommit('compaction');
        } else if (compacted && (this.#store !== undefined || this.#onHistoryCommit !== undefined)) {
          this.#transition = { status: 'committing', target: publicModel(binding.model) };
          this.#notifyContext();
          const failed = await this.#persistTransition(result.messages, original, signal, publish);
          if (failed !== undefined) return { status: 'rejected', reason: failed, ...(checkpointId ? { checkpointId } : {}) };
        } else {
          if (signal.aborted || this.#closed) return { status: 'rejected', reason: 'cancelled', ...(checkpointId ? { checkpointId } : {}) };
          publish();
        }
        if (compacted && result.removedIndices.length > 0) {
          this.#push({ type: 'session.compacted', removedRange: [result.removedIndices[0]!, result.removedIndices.at(-1)!],
            removedIndices: result.removedIndices, mechanism: 'model_transition',
            ...(checkpointId ? { checkpointId } : {}) }, 'sdk');
        }
        return { status: 'changed', from, to: publicModel(binding.model), compacted, summaryCalls: result.summaryCalls,
          ...(checkpointId ? { checkpointId } : {}) };
      } finally {
        this.#transitionController = null;
        this.#transition = { status: 'idle', target: null };
        this.#trackManager(resources, rewrite?.settle);
        this.#notifyContext();
        if (rewrite !== undefined) await this.#endArchiveRewrite(rewrite);
      }
    });
  }

  /** 持久化先于内存发布。提交失败/期间取消时以完整原史补偿，不让候选晚到覆盖回滚。 */
  async #persistTransition(candidate: IRMessage[], original: IRMessage[], signal: AbortSignal, publish: () => void): Promise<
    'persistence_failed' | 'persistence_rollback_failed' | 'cancelled' | undefined> {
    const candidateObservation = this.#archiveHistoryObservation(candidate);
    const originalObservation = this.#archiveHistoryObservation(original);
    return this.#enqueueCommit(async () => {
      if (signal.aborted || this.#closed) return 'cancelled' as const;
      let storeAttempted = false;
      let callbackAttempted = false;
      try {
        if (this.#store !== undefined) {
          storeAttempted = true;
          await this.#commitArchiveStore(structuredClone(candidate), { rewritten: true, reason: 'compaction' }, candidateObservation);
        }
        signal.throwIfAborted();
        if (this.#onHistoryCommit !== undefined) {
          callbackAttempted = true;
          await this.#onHistoryCommit(structuredClone(candidate), { rewritten: true, reason: 'compaction' });
        }
        signal.throwIfAborted();
        // 最后取消检查与内存发布之间无await；本点之后到达的取消属于已完成切换。
        publish();
        this.#storeRewritePending = false;
        this.#callbackRewritePending = false;
        return undefined;
      } catch {
        let rollbackFailed = false;
        if (storeAttempted) {
          try { await this.#commitArchiveStore(structuredClone(original), { rewritten: true, reason: 'restore' }, originalObservation); }
          catch { rollbackFailed = true; this.#storeRewritePending = true; }
        }
        if (callbackAttempted) {
          try { await this.#onHistoryCommit?.(structuredClone(original), { rewritten: true, reason: 'restore' }); }
          catch { rollbackFailed = true; this.#callbackRewritePending = true; }
        }
        return rollbackFailed ? 'persistence_rollback_failed' as const : signal.aborted ? 'cancelled' as const : 'persistence_failed' as const;
      }
    });
  }

  /**
   * 动态切换思考生成(生成面旋钮,setModel 同形制):下一轮生效,运行中轮
   * 不受影响。undefined = 回到不注入(模型缺省);呈现面(思考显示与否)
   * 走 SessionView delivery,两旋钮正交互不推导。已关闭会话上调用 → session_closed。
   */
  setThinking(thinking: { budget?: number } | undefined): void {
    this.#assertOpen('setThinking');
    if (this.#transitionController !== null) throw new TansrSdkError('turn_running', 'A model transition is in progress; await session.idle() before changing thinking.');
    this.#thinking = thinking;
    if (this.#capture !== undefined) this.#captureRevision++;
    this.#notifyContext();
  }

  /**
   * 关闭会话(幂等):空闲立即收口(session.ended + 流终结);运行中先
   * abort,收口推迟到泵终态——中止轮的尾部事件(turn.aborted 等)不丢失。
   * 会话级 signal 的 abort 监听在此解除(逻辑 close 后 abort 已无事可做,不再为已关闭会话持有闭包)。
   */
  close(): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#archiveRuntime?.stopAdmission();
    this.#captureController?.abort();
    this.#archiveBindingController?.abort();
    this.#transitionController?.abort();
    this.#releaseSignal?.();
    if (this.#handle !== null) {
      this.#handle.abort();
      return;
    }
    // S-2:空闲期操作在飞——让其跑完(落盘提交/事件不丢),收口推迟到操作链排空
    if (this.#idleOpDepth > 0 || this.#archiveFinishing) return;
    this.#finish();
  }

  /** 排空当前轮(含结转连锁起的新轮)与空闲期操作链:close/断言前等待终态收口 */
  async idle(): Promise<void> {
    for (;;) {
      const task: Promise<unknown> | null =
        this.#pumpTask ?? this.#archiveRetryTask ?? (this.#idleOpDepth > 0 ? this.#idleOps : null);
      if (task === null) return;
      await task;
      if ((this.#pumpTask ?? this.#archiveRetryTask ?? (this.#idleOpDepth > 0 ? this.#idleOps : null)) === task) return;
    }
  }

  // ———— 手动压缩与上下文快照(全部空闲期语义,运行中一律拒绝不排队)————

  /**
   * 手动压缩:对当前历史执行一次 manual 压缩,成功即就地替换历史、
   * 推 session.compacted / cost.usage.updated 事件、以 { rewritten:true, reason:
   * 'compaction' } 提交落盘。压缩前缺省先落 pre_compaction 快照(可关;快照 id 随
   * 结果与事件体交付);压缩失败快照保留。
   * - 运行中 → rejected:'turn_running'(不排队;await idle() 后重试——显式优于隐式);
   * - 历史为空 → rejected:'empty_history';compaction:false 或窗口未知 → 'not_configured';
   * - 并发 compact()/restore()/checkpoint() 按到达序串行;期间 send() 缓冲到操作链排空。
   * close 后调用 throw session_closed(编程错误,与 send 同律)。
   */
  async compact(options: CompactOptions = {}): Promise<CompactResult> {
    this.#assertOpen('compact');
    const explicit = options.checkpoint;
    if (explicit !== undefined && explicit !== false && this.#checkpoints === null) {
      throw this.#notWired('compact({ checkpoint })');
    }
    return this.#runIdleOp(async (): Promise<CompactResult> => {
      if (this.#handle !== null) return { status: 'rejected', reason: 'turn_running' };
      if (this.#history.length === 0) return { status: 'rejected', reason: 'empty_history' };
      const binding = this.#binding;
      const compaction = this.#compaction;
      if (compaction === undefined || binding.contextWindowTokens === undefined) {
        return { status: 'rejected', reason: 'not_configured' };
      }
      // 压缩前钩子与查询环屏障点同一组合件(#beforeCompactHook):SDK 快照钩子按
      // 「显式 > autoBeforeCompact 缺省」决定是否落 pre_compaction,开发者自设的
      // compaction.beforeCompact 随后同调;checkpoint:false = 本次不调钩子(kernel
      // compactNow({ checkpoint:false }) 同义)
      const hook = this.#beforeCompactHook();
      const context: QueryBeforeCompactContext = {
        trigger: 'manual',
        ...(explicit !== undefined ? { checkpoint: explicit } : {}),
      };
      const beforeCompact =
        hook !== undefined && explicit !== false
          ? (view: ContextView): Promise<{ checkpointId?: string } | void> => hook(view, context)
          : undefined;
      // 与查询环同一构造逻辑(compaction.manager 在场即复用;否则按自建字段构造)
      const manager = this.#resolveAttachmentManager(compaction, binding);
      const rewrite = this.#archiveRuntime === undefined ? undefined : this.#beginArchiveRewrite('compaction', manager);
      try {
      const publication = this.#capture === undefined ? undefined : await this.#prepareHistoryCapture(undefined, rewrite?.ticket);
      let archivePublish: ((apply?: () => void) => void) | undefined;
      const input = {
        manager,
        compaction,
        view: { system: this.#system, messages: this.#history, tools: this.#tools },
        model: binding.model,
        contextWindowTokens: binding.contextWindowTokens,
        ...(options.instructions !== undefined ? { instructions: options.instructions } : {}),
        ...(beforeCompact !== undefined ? { beforeCompact } : {}),
      };
      const result = await (publication === undefined ? runIdleCompaction(input) : runCapturedIdleCompaction(input, {
        publication,
        signal: this.#captureController!.signal,
        publish: (messages) => { this.#replaceHistory(messages); this.#captureRevision++; },
        onUnpublishedEvents: (events) => { for (const body of events) this.#push(body, 'sdk'); },
        ...(rewrite === undefined ? {} : {
          beforePublish: async (messages: readonly IRMessage[]) => { archivePublish = await this.#persistArchiveRewrite(rewrite, messages, 'compaction'); },
          commitPrepared: <T>(_messages: readonly IRMessage[], commit: () => T): T => {
            let result!: T; archivePublish!(() => { result = commit(); }); return result;
          },
        }),
      })).finally(() => this.#trackManager(manager, rewrite?.settle));
      if (result.status === 'failed') {
        const { outcome } = result;
        for (const body of result.events) this.#push(body, 'sdk');
        // 与 kernel 降级铁律同通道:可观测失败信息,不中断会话
        this.#push(
          { type: 'turn.error', scope: 'compaction', message: outcome.message, recoverable: true },
          'sdk',
        );
        return {
          status: 'failed',
          reason: outcome.reason,
          ...(outcome.checkpointId !== undefined ? { checkpointId: outcome.checkpointId } : {}),
        };
      }
      const { outcome } = result;
      // 应用器产物与 outcome.messages 逐元素同引用;快照语义(structuredClone)不变
      if (publication === undefined) this.#replaceHistory(result.messages.map((m) => structuredClone(m)));
      for (const body of result.events) this.#push(body, 'sdk');
      // rewritten:历史前缀被折叠改写,落盘方必须整卷轮换(不得增量追加)
      if (rewrite === undefined) this.#commitHistory(true, 'compaction');
      else await this.#notifyArchiveHistoryCommit('compaction');
      const compactionId = outcome.compactionId ?? outcome.observation?.compactionId;
      if (compactionId === undefined) {
        throw new Error('AgentSession.compact: kernel CompactOutcomeSuccess 缺 compactionId(运行时不变量被破)');
      }
      return {
        status: 'compacted',
        compactionId,
        removedRange: [outcome.removedRange[0], outcome.removedRange[1]],
        ...(outcome.checkpointId !== undefined ? { checkpointId: outcome.checkpointId } : {}),
      };
      } finally { if (rewrite !== undefined) await this.#endArchiveRewrite(rewrite); }
    });
  }

  /**
   * 手动快照(trigger='manual'):把当前完整历史写成自包含只读拷贝,返回头部 meta;
   * 落盘后推 session.checkpointed 事件。运行中 throw turn_running(历史归内核所有,
   * 空闲期快照才是完整上下文);未接线 throw checkpoints_not_wired。
   */
  async checkpoint(options: CheckpointOptions = {}): Promise<CheckpointMeta> {
    this.#assertOpen('checkpoint');
    const store = this.#requireCheckpoints('checkpoint()');
    return this.#runIdleOp(async () => {
      if (this.#handle !== null) throw this.#turnRunning('checkpoint()');
      return this.#writeCheckpoint(store, 'manual', this.#history, options.label);
    });
  }

  /** 列举本会话全部快照头部(checkpointId 升序 = 时间线;只读,运行中亦可) */
  async listCheckpoints(): Promise<CheckpointMeta[]> {
    const store = this.#requireCheckpoints('listCheckpoints()');
    return store.list(this.#sessionId);
  }

  /**
   * 自快照恢复(同会话就地换史):空闲校验 → 读快照 → sessionId
   * 对账 → (可选)pre_restore 快照 → 以快照 messages 替换历史(与 resume 预载同径:
   * 配对治理 + 深拷贝)→ 推 session.restored → { rewritten:true, reason:'restore' } 提交
   * 落盘(整卷轮换)。同一快照连续恢复两次,第二次历史字节等价、仍换卷(不做无变化
   * 短路,可解释优先)。快照 cwd ≠ 会话 cwd 时不改 cwd,推 turn.error(scope=
   * 'sdk.notice',recoverable)提示 cwd_mismatch_on_restore。
   */
  async restore(checkpointId: string, options: RestoreOptions = {}): Promise<RestoreResult> {
    this.#assertOpen('restore');
    return this.#runIdleOp(async (): Promise<RestoreResult> => {
      if (this.#handle !== null) return { status: 'rejected', reason: 'turn_running' };
      const checkpoints = this.#checkpoints;
      if (checkpoints === null) return { status: 'rejected', reason: 'store_not_wired' };
      const cp = await checkpoints.store.get(this.#sessionId, checkpointId);
      if (cp === null) return { status: 'rejected', reason: 'not_found' };
      if (cp.sessionId !== this.#sessionId) return { status: 'rejected', reason: 'session_mismatch' };
      const rewrite = this.#archiveRuntime === undefined ? undefined : this.#beginArchiveRewrite('restore', new ResourceSettlementScope());
      try {
      const publication = this.#capture === undefined ? undefined : await this.#prepareHistoryCapture(undefined, rewrite?.ticket);
      let preRestoreCheckpointId: string | undefined;
      if (options.checkpoint !== false) {
        const pre = await this.#writeCheckpoint(checkpoints.store, 'pre_restore', this.#history);
        preRestoreCheckpointId = pre.checkpointId;
      }
      const fromMessages = this.#history.length;
      // 与 resume 预载同径(doc/91 拍板④):配对治理后整体替换(kernel 写快照前已治理,
      // 自建 store 的快照亦经此兜底);深拷贝隔离快照对象
      const restored = repairHistoryPairing(cp.messages).messages;
      const publish = (): void => {
        publication?.assertSourceCurrent();
        this.#replaceHistory(restored);
        if (publication !== undefined) { this.#captureRevision++; publication.accept(); }
      };
      if (rewrite === undefined) publish();
      else (await this.#persistArchiveRewrite(rewrite, restored, 'restore', publish))();
      this.#inputDelivery.reset();
      const toMessages = this.#history.length;
    this.#push({ type: 'session.restored', checkpointId, fromMessages, toMessages }, 'sdk');
    if (cp.cwd !== this.#cwd) {
      // W-3:经告警通道(onWarning 在场即结构化交付;缺席回落 turn.error sdk.notice,文案同前)
      this.#warn({
        source: 'sdk',
        code: 'cwd_mismatch_on_restore',
        message:
          `checkpoint "${checkpointId}" was taken in "${cp.cwd}" ` +
          `but this session runs in "${this.#cwd}"; the working directory was left unchanged.`,
        detail: { checkpointId, checkpointCwd: cp.cwd, sessionCwd: this.#cwd },
      });
    }
    if (rewrite === undefined) this.#commitHistory(true, 'restore');
    else await this.#notifyArchiveHistoryCommit('restore');
      return {
        status: 'restored',
        checkpointId,
        fromMessages,
        toMessages,
        ...(preRestoreCheckpointId !== undefined ? { preRestoreCheckpointId } : {}),
      };
      } finally { if (rewrite !== undefined) await this.#endArchiveRewrite(rewrite); }
    });
  }

  /** 删除本会话的一份快照(幂等);运行中 throw turn_running;未接线 throw checkpoints_not_wired */
  async deleteCheckpoint(checkpointId: string): Promise<void> {
    this.#assertOpen('deleteCheckpoint');
    const store = this.#requireCheckpoints('deleteCheckpoint()');
    return this.#runIdleOp(async () => {
      if (this.#handle !== null) throw this.#turnRunning('deleteCheckpoint()');
      await store.delete(this.#sessionId, checkpointId);
    });
  }

  // ———— F-2:fork 与快照导出 / 导入(doc/117 §十 F-2,D-2 B;三方法不触本会话历史,运行中亦可调用)————

  /**
   * 自本会话的一份快照 fork 出**新会话**(源会话零改动;不隐式打开新会话——显式优于隐式):
   * 读快照 → `store.fork`(createFileSessionStore:kernel 原语,新卷首记录 kind='fork' +
   * meta.forkedFrom + 附件按 sha 复制;自建 store 缺该法回落 create + commit 全量历史)→ 返回
   * `{ sessionId }`,开发者随后 `createSession({ resume: { sessionId, store } })` 打开(或一步到位
   * 用 `createSession({ fork: { sessionId, checkpointId }, store })`)。`label` = 新会话标题、`cwd` =
   * 新会话工作目录(缺省沿本会话)。不签发事件(`session.forked` 在新会话首次打开时由快捷形签发)。
   * 失败面(结构化 throw):未接线快照 `checkpoints_not_wired` / 未给会话 store
   * `session_store_not_wired` / 快照不存在 `checkpoint_not_found` / close 后 `session_closed`。
   */
  async fork(checkpointId: string, options: ForkOptions = {}): Promise<ForkResult> {
    this.#assertOpen('fork');
    const checkpoints = this.#requireCheckpoints('fork()');
    const store = this.#store;
    if (store === undefined) {
      throw new TansrSdkError(
        'session_store_not_wired',
        'fork() requires a session store to place the new session in: pass createSession({ store }) ' +
          '(the fork result is a new session record you then open with createSession({ resume: { sessionId, store } })).',
      );
    }
    const checkpoint = await this.#readOwnCheckpoint(checkpoints, checkpointId, 'fork()');
    return forkIntoStore(store, {
      sourceSessionId: this.#sessionId,
      checkpoint,
      cwd: options.cwd ?? this.#cwd,
      ...(options.label !== undefined ? { title: options.label } : {}),
    });
  }

  /**
   * 导出本会话的一份快照为自包含字节(UTF-8 JSON `{ format:'tansr-checkpoint/1', checkpoint,
   * attachments: { [sha256]: base64 } }`;image 字节内联进 attachments,快照体以 $ref 引用)。
   * 只读,运行中 / close 后皆可(与 listCheckpoints 同律)。快照不存在 throw `checkpoint_not_found`。
   */
  async exportCheckpoint(checkpointId: string): Promise<Uint8Array> {
    const checkpoints = this.#requireCheckpoints('exportCheckpoint()');
    const checkpoint = await this.#readOwnCheckpoint(checkpoints, checkpointId, 'exportCheckpoint()');
    return encodeCheckpointExport(checkpoint);
  }

  /**
   * 把 exportCheckpoint() 产物(本会话或他会话 / 他机导出)导入为**本会话**的一份新快照:
   * 校验 format / schema / 每附件 sha256 / 每 $ref 皆有附件 / 消息形(任一不过 throw
   * `checkpoint_import_invalid`,不落半成品;校验先于入队,零副作用)→ 身份改写(sessionId = 本会话;
   * checkpointId 重铸;trigger 'manual';createdAt 现时;label 覆写或沿用;cwd / model / tokens 沿用;
   * compactionId 剥除)→ `store.put` → 推 `session.checkpointed`(每次快照落盘皆签发)。
   * 不触本会话历史(导入的是快照,之后可 restore()/fork());close 后 throw `session_closed`。
   * put 入空闲期串行链——与 checkpoint / restore / deleteCheckpoint 同链按到达序执行(避免与删除的附件 GC
   * 并发造成共享附件误删);运行中的轮不受影响(本方法不触历史,链内不校验 turn_running)。
   */
  async importCheckpoint(bytes: Uint8Array, options: ImportCheckpointOptions = {}): Promise<CheckpointMeta> {
    this.#assertOpen('importCheckpoint');
    const store = this.#requireCheckpoints('importCheckpoint()');
    let input: CheckpointInput;
    try {
      input = decodeCheckpointImport(bytes, {
        sessionId: this.#sessionId,
        ...(options.label !== undefined ? { label: options.label } : {}),
      });
    } catch (err) {
      if (err instanceof JournalError && err.code === 'CHECKPOINT_IMPORT_INVALID') {
        throw new TansrSdkError('checkpoint_import_invalid', `importCheckpoint: ${err.message}`, { cause: err });
      }
      throw err;
    }
    return this.#runIdleOp(async () => {
      const meta = await store.put(input);
      this.#reportRetentionWarning(meta);
      this.#push(
        {
          type: 'session.checkpointed',
          checkpointId: meta.checkpointId,
          trigger: meta.trigger,
          ...(meta.label !== undefined ? { label: meta.label } : {}),
          messageCount: meta.messageCount,
        },
        'sdk',
      );
      return meta;
    });
  }

  /** 读本会话的快照(F-2 三方法共用):不存在 throw checkpoint_not_found;归属不符 = store 失信 */
  async #readOwnCheckpoint(store: CheckpointStore, checkpointId: string, what: string): Promise<Checkpoint> {
    const checkpoint = await store.get(this.#sessionId, checkpointId);
    if (checkpoint === null) {
      throw new TansrSdkError(
        'checkpoint_not_found',
        `${what}: checkpoint "${checkpointId}" was not found for session "${this.#sessionId}" (listCheckpoints() to see what exists).`,
      );
    }
    if (checkpoint.sessionId !== this.#sessionId) {
      throw new TansrSdkError(
        'checkpoint_store_corrupted',
        `${what}: the checkpoint store returned checkpoint "${checkpointId}" owned by session "${checkpoint.sessionId}" ` +
          `when asked for session "${this.#sessionId}".`,
      );
    }
    return checkpoint;
  }

  /**
   * @internal 装配层合成事件入流(hooks emit / provider 切换 / todo 台账)。0.14 起自公开 d.ts 剥离
   * (`@internal` + stripInternal),运行时保留一个版本周期供旧调用方过渡;close 后调用即
   * `session_closed`(RV-3-05)。类内一律走 `#push`;createSession 闭包经 `bindEmit` 回填。
   */
  pushBody(body: EventBody, source: string): void {
    this.#assertOpen('pushBody');
    this.#push(body, source);
  }

  /** 事件入流:包络本地补齐,seq 与内核事件共用同一会话计数器(close 后的收尾事件亦经此) */
  #push(body: EventBody, source: string): void {
    const event: KernelEvent = {
      ...body,
      sessionId: this.#sessionId,
      seq: this.#seq++,
      ts: Date.now(),
      v: 'v0',
      source,
    };
    this.#queue.push(event);
    // 预算账本旁路记账(合成的 cost.usage.updated,如上下文迁移摘要用量;非 cost 事件容忍忽略)
    this.#spendGate?.consume(event);
    this.#observeContext(body);
  }

  #observeContext(event: EventBody): void {
    if (event.type === 'cost.usage.updated' && event.agentId === undefined && event.toolUseId === undefined &&
      (event.purpose === undefined || event.purpose === 'main')) {
      const model = { provider: event.provider ?? this.#activeBinding?.model.provider ?? this.#binding.model.provider, model: event.model };
      this.#lastObserved = { model, observedAt: Date.now(), evidence: 'usage' };
      this.#lastUsage = { model, usage: structuredClone(event.usage), observedAt: Date.now() };
    } else if (event.type === 'cost.provider.switched') {
      this.#fallback = { from: event.from, to: event.to, observedAt: Date.now() };
    }
    if (event.type === 'cost.usage.updated' || event.type === 'cost.provider.switched' ||
      event.type === 'session.compacted' || event.type === 'session.microcompacted') this.#notifyContext();
  }

  /** 等查询生成器实际收尾与提交；不得从本会话 history callback 内调用并等待。 */
  async drain(options: SessionDrainOptions = {}): Promise<SessionDrainResult> {
    return this.#drain(options, new LifecycleWait(options));
  }

  /** 信号/超时仅约束观察，逻辑 close 仍即时生效；非法选项在 close 前拒绝。 */
  async closeAsync(options: SessionDrainOptions = {}): Promise<SessionDrainResult> {
    const wait = new LifecycleWait(options);
    this.close();
    return this.#drain(options, wait);
  }

  #drainResult(status?: SessionDrainResult['status']): SessionDrainResult {
    const local = this.#lifecycle.snapshot();
    const owned = this.#ownedResources.ledger.snapshot();
    const archiveFailures = this.#archiveFailures ?? [];
    return { status: status ?? (this.#lifecycle.failed || this.#ownedResources.ledger.failed || (this.#archiveUnresolvedFailures?.size ?? 0) > 0 ? 'failed' : 'completed'),
      closed: this.#closed,
      pending: { ...emptyPending(), query: Math.max(this.#querySettlements.size, this.#pumpTask === null ? 0 : 1),
        idleOperations: this.#idleOpDepth + this.#managerSettlements.size + (this.#captureSettlement === undefined ? 0 : 1), commits: this.#commits,
        cleanup: this.#cleanupTask === undefined ? 0 : Math.max(1, this.#ownedResources.pending),
        storeFlush: this.#storeFlush === undefined ? 0 : 1 },
      failures: [...local.failures, ...owned.failures, ...archiveFailures], failureCount: local.failureCount + owned.failureCount + this.#archiveFailureCount,
      omittedFailures: local.omittedFailures + owned.omittedFailures + this.#archiveFailureCount - archiveFailures.length, cleanupEvidence: this.#ownedResources.evidence };
  }

  async #drain(options: SessionDrainOptions, wait: LifecycleWait): Promise<SessionDrainResult> {
    let retried = this.#retryPending !== undefined;
    const retryAttempt = this.#retryAttempt;
    let flushed = false;
    // 仅观察真实在飞捕获；结果未知由下一准入按原键核实，drain 不代为重试。
    if (this.#capture !== undefined) this.#trackCaptureSettlement();
    if (this.#finished && this.#cleanupTask === undefined && wait.stopped === undefined) this.#startCleanup();
    while (true) {
      const pending = [...this.#querySettlements, ...this.#managerSettlements];
      if (this.#captureSettlement !== undefined) pending.push(this.#captureSettlement);
      if (this.#pumpTask !== null) pending.push(this.#pumpTask);
      if (this.#archiveRetryTask !== undefined) pending.push(this.#archiveRetryTask.then(() => {}));
      if (this.#idleOpDepth > 0) pending.push(this.#idleOps);
      if (this.#commits > 0) pending.push(this.#commitTail);
      if (this.#cleanupTask !== undefined) pending.push(this.#cleanupTask);
      if (this.#storeFlush !== undefined) { pending.push(this.#storeFlush); flushed = options.flushStore === true; }
      if (pending.length > 0) {
        const stopped = await wait.wait(Promise.allSettled(pending));
        if (stopped === 'timeout' && options.force === true && this.#closed) return this.#forceRelease(options);
        if (stopped !== undefined) return this.#drainResult(stopped);
        continue;
      }
      if (wait.stopped === 'cancelled') return this.#drainResult('cancelled');
      if (this.#retryAttempt !== retryAttempt) retried = true;
      if (!retried && options.retryPersistence && this.#archiveRuntime === undefined && this.#storeRewritePending && this.#store !== undefined) {
        if (wait.stopped !== undefined) return this.#drainResult(wait.stopped);
        retried = true;
        this.#retryPersistence();
        continue;
      }
      if (!flushed && options.flushStore && this.#store?.flush !== undefined) {
        if (wait.stopped !== undefined) return this.#drainResult(wait.stopped);
        flushed = true;
        const id = this.#lifecycle.operation();
        const task = flushBorrowedStore(this.#store).then((complete) => {
          if (complete) this.#lifecycle.recover('store_flush', id);
          else this.#lifecycle.fail(id, 'store_flush', undefined, 'store_flush_incomplete');
        }, (cause: unknown) => { this.#lifecycle.fail(id, 'store_flush', cause); });
        this.#storeFlush = task;
        void task.then(() => { this.#storeFlush = undefined; });
        continue;
      }
      if (this.#archiveRuntime !== undefined) {
        const archive = await this.#archiveRuntime.rewrite.settle();
        if (archive.status !== 'completed') return this.#drainResult('failed');
      }
      return this.#drainResult();
    }
  }

  /**
   * RV-3-08:`drain({ force: true })` 在已关闭会话的观察预算耗尽后强制释放——释放违约快照屏障(其
   * `*_settlement_incomplete` 已记),对仍在飞的收尾(settle 未返回,无法中断)记 `*_settlement_forced`,
   * 然后不再等它们、直接释放自有资源(幂等 dispose),并以同一预算等待释放完成。
   */
  async #forceRelease(options: SessionDrainOptions): Promise<SessionDrainResult> {
    this.#settlementBarrier.release();
    // 让被释放的收尾任务结束并自集合注销(微任务链;一个宏任务足够)
    await new Promise<void>((resolve) => setImmediate(resolve));
    for (const _task of this.#querySettlements) {
      this.#lifecycle.fail(this.#lifecycle.operation(), 'query', undefined, 'query_settlement_forced');
    }
    for (const _task of this.#managerSettlements) {
      this.#lifecycle.fail(this.#lifecycle.operation(), 'idle_operation', undefined, 'compaction_settlement_forced');
    }
    const release = new LifecycleWait(options);
    const stopped = await release.wait(this.#ownedResources.dispose().catch(() => {}));
    if (stopped !== undefined) return this.#drainResult(stopped);
    await new Promise<void>((resolve) => setImmediate(resolve));
    return this.#drainResult();
  }

  #retryPersistence(): void {
    if (this.#retryPending !== undefined && this.#retryGeneration === this.#generation) return;
    const store = this.#store;
    if (store === undefined) return;
    const history = this.messages();
    const observation = this.#archiveHistoryObservation(history);
    const id = this.#lifecycle.operation();
    this.#retryGeneration = this.#generation;
    this.#retryAttempt++;
    const task = this.#enqueueCommit(async () => {
      try {
        await this.#commitArchiveStore(history, { rewritten: true }, observation);
        this.#storeRewritePending = false;
        this.#lifecycle.recover('store_commit', id);
      } catch (cause) {
        this.#storeRewritePending = true;
        this.#lifecycle.fail(id, 'store_commit', cause);
      }
    });
    this.#retryPending = task;
    void task.then(() => { this.#retryPending = undefined; });
  }

  #finish(): void {
    if (this.#finished) return;
    this.#finished = true;
    this.#push({ type: 'session.ended' }, 'sdk');
    this.#queue.end();
    this.#notifyContext();
    this.#contextListeners.clear();
    // 逻辑终态即时发出；物理清理等本会话每轮查询的真实 settlement。
    this.#startCleanup();
  }

  #startCleanup(): void {
    if (this.#cleanupTask !== undefined) return;
    const task = (async () => {
      await Promise.all([...this.#querySettlements, ...this.#managerSettlements]);
      if (this.#capture !== undefined) await this.#capture.settle();
      if (this.#archiveRuntime !== undefined) {
        await this.#commitTail;
        const settled = await this.#archiveRuntime.rewrite.settle();
        if (settled.status !== 'completed') return;
      }
      await this.#ownedResources.dispose();
    })();
    this.#cleanupTask = task;
    void task.then(() => { this.#cleanupTask = undefined; }, () => {
      this.#cleanupTask = undefined;
      this.#warnAfterEnd(lifecycleSummary(this.#ownedResources.ledger.snapshot().failures));
    });
  }

  #warnAfterEnd(message: string): void {
    if (this.#hasWarningHandler) this.#warn({ source: 'sdk', code: 'lifecycle_failed', message });
    else console.warn(`[tansr-sdk] ${message}`);
  }

  // ———— S-2 内部件 ————

  /** 同一宿主历史的捕获与最终发布见证；纯内存会话不依赖持久化重试 generation。 */
  #prepareHistoryCapture(signal?: AbortSignal, ticket?: LifecycleRewrite) {
    const messages = this.#history;
    const revision = this.#captureRevision;
    const binding = this.#binding;
    const closeSignal = this.#captureController!.signal;
    return prepareCapturedGovernance({
      capture: ticket?.governanceCapture ?? this.#capture!, messages,
      signal: signal === undefined ? closeSignal : AbortSignal.any([signal, closeSignal]),
      isCurrent: () => !this.#closed && this.#history === messages && this.#captureRevision === revision && this.#binding === binding,
      onPending: (work) => {
        const task = work.then(() => undefined, () => undefined);
        this.#managerSettlements.add(task);
        void task.then(() => { this.#managerSettlements.delete(task); });
      },
    });
  }

  #trackCaptureSettlement(): void {
    if (this.#captureSettlement !== undefined || this.#capture === undefined) return;
    const task = this.#capture.settle();
    this.#captureSettlement = task;
    void task.then(() => { if (this.#captureSettlement === task) this.#captureSettlement = undefined; }, () => {
      if (this.#captureSettlement === task) this.#captureSettlement = undefined;
    });
  }

  #assertOpen(method: string): void {
    if (this.#closed) {
      throw new TansrSdkError('session_closed', `${method}() called on a closed session.`);
    }
  }

  #notWired(what: string): TansrSdkError {
    return new TansrSdkError(
      'checkpoints_not_wired',
      `${what} requires a checkpoint store: pass createSession({ checkpoints: { dir } }) or ` +
        '{ checkpoints: { store } }, or a session "store" that provides colocated checkpoints (createFileSessionStore).',
    );
  }

  #requireCheckpoints(what: string): CheckpointStore {
    if (this.#checkpoints === null) throw this.#notWired(what);
    return this.#checkpoints.store;
  }

  #turnRunning(what: string): TansrSdkError {
    return new TansrSdkError(
      'turn_running',
      `${what} is idle-only: a turn is running (await session.idle() and retry).`,
    );
  }

  /**
   * 空闲期操作串行链:按到达序执行;期间 send() 缓冲;链排空后——已 close 则收口
   * (session.ended),否则把缓冲输入合并起一轮(输入永不丢失)。
   */
  #runIdleOp<T>(op: () => Promise<T>, binding = false, detaching = false): Promise<T> {
    if (!binding) { assertArchiveRecoveryInstallation(this.#store, this.#sessionId, this.#archiveRuntime);
      assertArchiveInstallationAdmission(this.#store, this.#sessionId, this.#archiveRuntime); }
    this.#idleOpDepth += 1;
    const id = this.#lifecycle.operation();
    const run = this.#idleOps.then(this.#archiveRuntime === undefined ? op : async () => {
      if (this.#archiveRuntime !== undefined && this.#archiveFinishing && this.#pumpTask !== null) await this.#pumpTask;
      if (!detaching && this.#archiveRuntime !== undefined && this.#handle === null && !this.#archiveReady()) throw new Error('Archive lifecycle reconciliation required');
      return op();
    }).catch((cause: unknown) => {
      if (!(cause instanceof ArchiveRewriteReconciliationError)) this.#lifecycle.fail(id, 'idle_operation', cause);
      throw cause;
    });
    this.#idleOps = run.then(
      () => undefined,
      () => undefined,
    );
    return run.finally(() => {
      this.#idleOpDepth -= 1;
      if (this.#idleOpDepth > 0) return;
      if (isArchiveInstallationAdmissionBlocked(this.#store, this.#sessionId, this.#archiveRuntime)) {
        if (this.#closed && this.#handle === null) this.#finish();
        return; // 尚未确认安装时，不摘除已经排队的原输入。
      }
      if (this.#archiveRuntime !== undefined) { this.#releaseArchiveBuffered(); return; }
      const buffered = this.#bufferedSends;
      this.#bufferedSends = [];
      if (this.#closed) {
        if (this.#handle === null) this.#finish();
        return;
      }
      if (buffered.length > 0 && this.#handle === null) this.#startTurn(buffered);
    });
  }

  /**
   * 写快照(kernel 原语经 CheckpointStore):铸 ULID → put(配对治理 + durable)→
   * 推 session.checkpointed(落盘之后才入流,事件即事实)。messages 传入引用,
   * store 实现自行拷贝(kernel writeCheckpoint 不改输入)。
   */
  async #writeCheckpoint(
    store: CheckpointStore,
    trigger: CheckpointTrigger,
    messages: readonly IRMessage[],
    label?: string,
  ): Promise<CheckpointMeta> {
    const model = this.#binding.model;
    const meta = await store.put({
      checkpointId: newCheckpointId(),
      sessionId: this.#sessionId,
      createdAt: new Date().toISOString(),
      trigger,
      ...(label !== undefined ? { label } : {}),
      cwd: this.#cwd,
      model: { provider: model.provider, model: model.model },
      messages: [...messages],
    });
    this.#reportRetentionWarning(meta);
    this.#push(
      {
        type: 'session.checkpointed',
        checkpointId: meta.checkpointId,
        trigger: meta.trigger,
        ...(meta.label !== undefined ? { label: meta.label } : {}),
        messageCount: meta.messageCount,
      },
      'sdk',
    );
    return meta;
  }

  /**
   * kernel 文件 store 的写后淘汰失败以 `meta.retentionWarning` 携回(写本身已成功,不反噬);
   * 经告警通道上报(onWarning 在场结构化交付;缺席回落 turn.error sdk.notice),不打断调用方。
   */
  #reportRetentionWarning(meta: CheckpointMeta): void {
    const warning = (meta as { retentionWarning?: { checkpointId: string; code: string; message: string } }).retentionWarning;
    if (warning === undefined) return;
    this.#warn({
      source: 'sdk',
      code: 'checkpoint_retention_failed',
      message:
        `checkpoint "${meta.checkpointId}" was written, but retention could not evict "${warning.checkpointId}": ` +
        `${warning.message}. The stale checkpoint stays on disk and will be retried on the next write.`,
      detail: { checkpointId: meta.checkpointId, failed: warning.checkpointId, code: warning.code },
    });
  }

  /**
   * K-5 接线:压缩前钩子(查询环屏障点的 auto/reactive/compactNow 压缩与 compact()
   * 空闲直压共用)。SDK 快照钩子据语境决定是否落 pre_compaction(显式 checkpoint
   * true/{label} 恒落、false 不落、缺席按 checkpoints.autoBeforeCompact),随后同调
   * 开发者经 compaction.beforeCompact 自设的钩子(快照接线不静默顶替开发者钩子);
   * 返回的 checkpointId 以 SDK 快照为准,SDK 未落则沿用开发者钩子返回值。
   * 快照未接线 → 原样返回开发者钩子(缺席即 undefined,现状零漂移)。
   *
   * 快照是附属能力:auto / reactive 触发的 pre_compaction 快照失败
   * → 推 `turn.error{ scope:'checkpoint', recoverable:true }` 并**继续压缩**(不 throw = kernel 不落
   * before_compact_failed、不计自动压缩熔断);此前配了 max 的用户一处杂散文件即让自动压缩连败三次
   * 进熔断、会话不可压缩。manual(compact() / compactNow 显式意志)保持 fail-closed:快照失败即
   * 压缩失败(before_compact_failed),用户明确要快照就不能静默无快照压缩。
   */
  #beforeCompactHook(): QueryBeforeCompactHook | undefined {
    const checkpoints = this.#checkpoints;
    const developer = this.#compaction?.beforeCompact;
    if (checkpoints === null) return developer;
    return async (view, context) => {
      const explicit = context.checkpoint;
      const want = explicit === undefined ? checkpoints.autoBeforeCompact : explicit !== false;
      let checkpointId: string | undefined;
      if (want) {
        const label = typeof explicit === 'object' ? explicit.label : undefined;
        try {
          const meta = await this.#writeCheckpoint(checkpoints.store, 'pre_compaction', view.messages, label);
          checkpointId = meta.checkpointId;
        } catch (err) {
          if (context.trigger === 'manual') throw err;
          const detail = err instanceof Error ? err.message : String(err);
          this.#push(
            {
              type: 'turn.error',
              scope: 'checkpoint',
              message: `pre_compaction checkpoint failed (${context.trigger} compaction continues without a snapshot): ${detail}`,
              recoverable: true,
            },
            'sdk',
          );
        }
      }
      const fromDeveloper = await developer?.(view, context);
      const id = checkpointId ?? fromDeveloper?.checkpointId;
      return id !== undefined ? { checkpointId: id } : undefined;
    };
  }

  /** 内核事件入队:seq 覆写为会话全局值(ts/turnId/source 保留原值) */
  #pushKernelEvent(event: KernelEvent): void {
    const stamped: KernelEvent = { ...event, seq: this.#seq++ };
    this.#queue.push(stamped);
    // 预算账本旁路记账(cost.usage.updated 逐行入账,含子代理 / 压缩摘要上卷;闸门下一屏障读快照)
    this.#spendGate?.consume(stamped);
    this.#observeContext(event);
  }

  #startTurn(prompts: readonly string[], userBlocks?: readonly SessionUserBlock[]): void {
    assertArchiveRecoveryInstallation(this.#store, this.#sessionId, this.#archiveRuntime);
    assertArchiveInstallationAdmission(this.#store, this.#sessionId, this.#archiveRuntime);
    const captureInputStart = this.#capture === undefined ? undefined : this.#history.length;
    const initialMessages: IRMessage[] = [
      ...this.#history,
      ...(userBlocks === undefined ? prompts.map((text): IRMessage => ({ role: 'user', blocks: [{ t: 'text', text }] })) :
        [{ role: 'user' as const, blocks: structuredClone([...userBlocks]) }]),
    ];
    this.#injected = [];
    const binding = this.#binding;
    const thinking = this.#thinking === undefined ? undefined : { ...this.#thinking };
    this.#activeBinding = binding;
    this.#activeThinking = thinking;
    this.#activeModelState = this.#modelState(binding, this.#modelAlias);
    // S-2:压缩前快照钩子(快照接线在场时注入组合钩子;缺席 = 选项原样,现状零漂移)
    let compaction: QueryCompactionOptions | undefined =
      this.#compaction !== undefined && this.#checkpoints !== null
        ? { ...this.#compaction, beforeCompact: this.#beforeCompactHook() }
        : this.#compaction;
    if (this.#attachmentCheckpoints && compaction !== undefined && binding.contextWindowTokens !== undefined) {
      compaction = { ...compaction, manager: this.#resolveAttachmentManager(compaction, binding) };
    }
    let preparedInfo: ApplicationPromptInfo | undefined;
    let lifecycleTurn: LifecycleTurn | undefined;
    const start = (system: IRSystemSegment[]): QueryHandle => {
      if (preparedInfo !== undefined) {
        // 旧 usage 已包含旧 system；正文变化须丢测量锚点，保留历史与累计用量。
        const changed = system.length !== this.#system.length || system.some((segment, index) => {
          const previous = this.#system[index];
          return segment.text !== previous?.text || segment.cacheable !== previous?.cacheable || segment.label !== previous?.label;
        });
        if (changed) this.#compaction?.manager?.invalidateRequestBasis();
        // 预检确认成功且未取消后才提交；Task 捕获同一数组，主轮使用独立段快照。
        this.#system.splice(0, this.#system.length, ...system);
        this.#applicationPrompt = Object.freeze({ ...preparedInfo });
      }
      const ticket = this.#archiveRuntime?.run.planRun({ turnId: randomUUID(), onAdmitted: () => this.#onTurnStart?.(prompts) });
      if (ticket === undefined) this.#onTurnStart?.(prompts);
      const query = runQuery({
        ...(ticket === undefined ? {} : { turnId: ticket.turnId }),
        ...(this.#capture !== undefined ? { sessionCapture: this.#capture, sessionCaptureInputStart: captureInputStart } : {}),
        ...(this.#contextMaterials !== undefined ? { contextMaterials: this.#contextMaterials } : {}),
        client: binding.client,
        executor: this.#executor,
        system,
        tools: this.#tools,
        model: binding.model,
        maxTokens: this.#maxTokens,
        ...(thinking !== undefined ? { thinking } : {}),
        sessionId: this.#sessionId,
        initialMessages,
        ...(this.#onInputsConsumed !== undefined ? { onInputsConsumed: (inputs) => this.#onInputsConsumed?.(inputs.map((input) => input.text)) } : {}),
        maxTurns: this.#maxTurnsPerQuery,
        ...(binding.contextWindowTokens !== undefined
          ? { contextWindowTokens: binding.contextWindowTokens }
          : {}),
        // T-C12:capability.maxOutput 半边(查询环 IR 构造前事前钳制)
        ...(binding.maxOutputTokens !== undefined
          ? { maxOutputTokens: binding.maxOutputTokens }
          : {}),
        // 压缩只在窗口已知的轮启用(阈值代数依赖窗口;与 TUI/serve 同守卫)
        ...(compaction !== undefined && binding.contextWindowTokens !== undefined
          ? { compaction }
          : {}),
        // 会话预算闸(DEC-RF-29):kernel 屏障点比对会话累计消耗;缺席不落键 = 现状
        ...(this.#spendGate !== undefined ? { budget: this.#spendGate.option } : {}),
      });
      const receipt = this.#trackQuery(query);
      if (ticket !== undefined) {
        if (receipt === undefined) throw new Error('Archive lifecycle requires original query receipt');
        ticket.bindQuery({ handle: query, receipt });
        lifecycleTurn = { ticket, query, events: ticket.armUnadmittedInput({ capture: this.#capture!, inputStart: captureInputStart! }) };
      }
      return query;
    };
    const prepare = this.#prepareApplicationPrompt;
    let handle: QueryHandle;
    try {
      handle = prepare === undefined ? start(this.#system) : prepareApplicationPromptTurn({
        sessionId: this.#sessionId,
        history: this.#history,
        prepare: async (signal) => {
          const refreshed = await prepare(signal);
          signal.throwIfAborted();
          preparedInfo = refreshed.info;
          return refreshed.system;
        },
        start,
        ...(this.#archiveRuntime === undefined ? {} : { eventsFor: (query: QueryHandle) => lifecycleTurn?.query === query ? lifecycleTurn.events : query.events }),
        errorScope: 'sdk',
        errorMessage: applicationPromptFailureMessage,
      });
    } catch (error) {
      // 尚未创建轮句柄的同步拒绝不会进入pump收口，须撤销执行态并保留原错。
      this.#activeBinding = null;
      this.#activeThinking = undefined;
      this.#activeModelState = null;
      this.#notifyContext();
      throw error;
    }
    this.#handle = handle;
    this.#inputDelivery.bind(handle);
    this.#pumpTask = this.#pump(handle, () => lifecycleTurn);
    this.#notifyContext();
  }

  async #pump(handle: QueryHandle, readTurn: () => LifecycleTurn | undefined): Promise<void> {
    // S-B5:本轮是否发生历史就地改写(压缩折叠 / microcompact 存根化)
    // ——落盘方须整卷轮换而非增量追加(cli session-driver 同判据;
    // microcompacted 一并计入:SDK 无 journal 存根重演机制,旧消息 content
    // 被原位替换对落盘方同样是前缀失配)
    let rewritten = false;
    /** S-2:本轮是否发生压缩折叠(reason:'compaction';仅存根化时 reason 缺席) */
    let compacted = false;
    let allowCarry = false;
    try {
      for await (const event of (readTurn()?.query === handle ? readTurn()!.events : handle.events)) {
        if (this.#archiveRuntime !== undefined && (event.type === 'turn.completed' || event.type === 'turn.aborted')) this.#archiveFinishing = true;
        if (event.type === 'turn.completed') {
          allowCarry = event.reason === 'completed' || event.reason === 'structured_output';
        }
        if (event.type === 'session.compacted' || event.type === 'session.microcompacted') {
          rewritten = true;
          if (event.type === 'session.compacted') compacted = true;
        }
        this.#pushKernelEvent(event);
      }
    } finally {
      // settle 在未消费事件前只是空快照；轮泵退出后才启动真实收尾观察。
      this.#releaseQuerySettlement?.();
      this.#releaseQuerySettlement = undefined;
      // 终态收口:历史快照 + 未消费注入结转(messages() 已含被消费的注入,
      // 结转集只含未消费尾段,不会重复)
      const consumed = handle.counters().queueInjections;
      const carried = allowCarry ? this.#injected.slice(consumed) : [];
      if (this.#archiveRuntime !== undefined) {
        await this.#finishArchiveTurn(handle, readTurn(), carried, rewritten, compacted);
        return;
      }
      this.#replaceHistory(handle.messages());
      if (this.#capture !== undefined) this.#captureRevision++;
      this.#inputDelivery.finish(handle);
      this.#handle = null;
      this.#activeBinding = null;
      this.#activeThinking = undefined;
      this.#activeModelState = null;
      this.#injected = [];
      this.#pumpTask = null;
      // 持久化缝在 session.ended 收口之前提交(末轮历史不丢;缺席零开销)
      this.#commitHistory(rewritten, compacted ? 'compaction' : undefined);
      this.#notifyContext();
      if (this.#closed) {
        // S-2:空闲期操作链尚有在飞项时由其排空后收口(不重复签发 session.ended)
        if (this.#idleOpDepth === 0) this.#finish();
      } else if (carried.length > 0) {
        this.#startTurn(carried);
      }
    }
  }

  /**
   * S-B5 — 每轮终态的历史提交(缺席 = 纯内存零变化,不做任何快照开销)。
   * 快照为深拷贝(messages()),回调方可自由持有;同步抛错与异步 reject
   * 都不破会话主流程(结构化吞错,见 #reportCommitFailure)。
   * S-2:reason 为加法位(compaction / restore;缺席 = 非改写或仅存根化)。
   */
  #archiveReady(): boolean {
    return !this.#archiveFinishing && (this.#archiveRuntime === undefined || this.#archiveRuntime.rewrite.status().status === 'completed');
  }

  #releaseArchiveBuffered(): void {
    try {
      if (isArchiveInstallationAdmissionBlocked(this.#store, this.#sessionId, this.#archiveRuntime)) {
        if (this.#closed && this.#handle === null && this.#idleOpDepth === 0) this.#finish();
        return;
      }
      if (this.#idleOpDepth > 0 || this.#handle !== null || !this.#archiveReady()) return;
      if (this.#closed) { this.#finish(); return; }
      if (this.#bufferedSends.length === 0) return;
      const buffered = [...this.#bufferedSends];
      // 起轮同步拒绝时保留原文本；真实起轮成功才消费原缓冲。
      this.#startTurn(buffered);
      this.#bufferedSends.splice(0, buffered.length);
      if (this.#archiveBufferFailureId !== undefined) this.#recoverArchiveFailure(this.#archiveBufferFailureId);
      this.#archiveBufferFailureId = undefined;
    } catch (cause) {
      this.#archiveBufferFailureId ??= this.#recordArchiveFailure(cause, 'archive_buffer_admission_failed');
    }
  }

  #recordArchiveFailure(cause: unknown, code = 'archive_reconciliation_required'): number {
    const operationId = this.#lifecycle.operation();
    this.#archiveFailureCount++;
    this.#archiveUnresolvedFailures!.add(operationId);
    this.#archiveFailures!.push({ operationId, phase: 'query', code, cause, recovered: false });
    if (this.#archiveFailures!.length > 32) this.#archiveFailures!.shift();
    return operationId;
  }

  #recoverArchiveFailure(operationId: number): void {
    this.#archiveUnresolvedFailures!.delete(operationId);
    for (const failure of this.#archiveFailures!) if (failure.operationId === operationId) failure.recovered = true;
  }

  async #finishArchiveTurn(handle: QueryHandle, turn: LifecycleTurn | undefined,
    carried: string[], rewritten: boolean, compacted: boolean): Promise<void> {
    const runtime = this.#archiveRuntime!;
    this.#archiveFinishing = true;
    this.#archivePendingTurn = turn;
    this.#inputDelivery.finish(handle);
    this.#handle = null;
    this.#activeBinding = null; this.#activeThinking = undefined; this.#activeModelState = null;
    this.#injected = [];
    this.#bufferedSends.push(...carried);
    try {
      if (turn !== undefined) {
        // 原泵已退出；同一个 receipt 先确认真实 settle，然后才处理未受理输入。
        await runtime.run.settle();
        const history = structuredClone(handle.messages());
        turn.continuation = { history, source: this.#archiveHistoryPort!.read().observation,
          observation: this.#archiveHistoryObservation(history)!, commitId: randomUUID(), rewritten,
          ...(compacted ? { reason: 'compaction' } : {}), unadmitted: turn.ticket.status().stage === 'planned', failureOperationIds: [] };
        if (turn.continuation.unadmitted) await turn.ticket.finalizeUnadmittedInput();
        await this.#continueArchiveTurn(turn);
      }
      this.#archivePendingTurn = undefined;
    } catch (cause) {
      const operationId = this.#recordArchiveFailure(cause);
      turn?.continuation?.failureOperationIds.push(operationId);
    } finally {
      this.#archiveFinishing = false;
      this.#pumpTask = null;
      this.#notifyContext();
      this.#releaseArchiveBuffered();
    }
  }

  #continueArchiveTurn(turn: LifecycleTurn): Promise<void> {
    const continuation = turn.continuation!;
    if (continuation.work !== undefined) return continuation.work;
    if (turn.ticket.status().capture !== 'confirmed' || turn.ticket.status().query !== 'confirmed')
      return Promise.reject(new Error('Archive original capture/query remains unresolved'));
    const seen = this.#archiveHistoryPort!.read().observation;
    if (JSON.stringify(seen) !== JSON.stringify(continuation.source)) return Promise.reject(new Error('Archive original continuation source changed'));
    continuation.work = Promise.resolve().then(async () => {
      this.#replaceHistory(continuation.history); this.#captureRevision++;
      await this.#commitHistory(continuation.rewritten, continuation.reason, turn.ticket, continuation);
      const result = await this.#archiveRuntime!.run.settle();
      if (result.status !== 'completed') throw new Error('Archive run reconciliation required');
    });
    return continuation.work;
  }

  /** @internal 显式恢复同键事实；只可继续已固定但尚未开始的首次 Store，不重写已经开始的 Store。 */
  retryArchiveLifecycle(): Promise<boolean> {
    if (this.#archiveRetryTask !== undefined) return this.#archiveRetryTask;
    const runtime = this.#archiveRuntime;
    if (runtime === undefined || this.#archiveFinishing) return Promise.resolve(false);
    this.#archiveFinishing = true;
    const task = (async () => {
      const turn = this.#archivePendingTurn;
      if (turn !== undefined) {
        const continuation = turn.continuation;
        if (continuation === undefined) return false;
        if (continuation.work === undefined) {
          if (continuation.unadmitted) await turn.ticket.retryUnadmittedInput();
          else await runtime.capture.awaitReady();
          await this.#continueArchiveTurn(turn);
        } else {
          await continuation.work.catch(() => {});
          const receipt = continuation.receipt;
          if (receipt !== undefined && turn.ticket.status().stage !== 'settled') turn.ticket.commitHistory({
            commitId: receipt.commitId, storeIdentity: receipt.storeIdentity, target: runtime.targetFor(continuation.history, receipt),
            localHistoryGeneration: receipt.observation.localHistoryGeneration, outcome: receipt.outcome,
          });
        }
      }
      await runtime.run.retryOriginal();
      let result = await runtime.rewrite.retryOriginal();
      const rewrite = this.#archivePendingRewrite;
      if (rewrite !== undefined && result.status !== 'completed') {
        if (rewrite.commitInput !== undefined && rewrite.receipt === undefined) {
          // 原 store-intent 已固定；只继续此前尚未调用的同一 start，失败过的原 Store 不重写。
          await this.#enqueueCommit(async () => {
            rewrite.ticket.commitHistory(rewrite.commitInput!);
            await rewrite.completion;
          });
        }
        // 原 API 已失败返回；未发布候选统一按固定原史补偿，不重放原发布或摘要。
        await this.#endArchiveRewrite(rewrite);
        result = await runtime.rewrite.retryOriginal();
      }
      if (result.status !== 'completed') return false;
      for (const operationId of turn?.continuation?.failureOperationIds ?? []) this.#recoverArchiveFailure(operationId);
      for (const operationId of rewrite?.failureOperationIds ?? []) this.#recoverArchiveFailure(operationId);
      this.#archivePendingTurn = undefined;
      this.#archivePendingRewrite = undefined;
      return true;
    })().finally(() => { this.#archiveFinishing = false; this.#archiveRetryTask = undefined; this.#releaseArchiveBuffered(); });
    this.#archiveRetryTask = task;
    return task;
  }

  #beginArchiveRewrite(reason: 'compaction' | 'restore' | 'model-transition',
    resources: Pick<ContextManager, 'settle'>, signal = this.#captureController!.signal): LifecycleRewriteWork {
    const history = this.#history, revision = this.#captureRevision, binding = this.#binding;
    const sourceLease = { signal, assertCurrent: () => {
      signal.throwIfAborted();
      if (this.#closed || this.#history !== history || this.#captureRevision !== revision || this.#binding !== binding)
        throw new Error('Archive rewrite source changed');
    } };
    const ticket = this.#archiveRuntime!.rewrite.prepare({ rewriteId: randomUUID(), reason, sourceLease });
    let ended!: () => void;
    const end = new Promise<void>(resolve => { ended = resolve; });
    let settled: Promise<ResourceSettlementResult> | undefined;
    const settle = () => settled ??= resources.settle({ timeoutMs: Infinity });
    ticket.bindWork({ ended: end, settle });
    const work = { ticket, ended, signal, settle, original: this.messages(), source: this.#archiveHistoryObservation(this.#history)!, failureOperationIds: [] };
    this.#archivePendingRewrite = work;
    return work;
  }

  async #endArchiveRewrite(work: LifecycleRewriteWork): Promise<void> {
    try {
    work.ended();
    if (work.receipt !== undefined && work.completion !== undefined &&
      work.ticket.status().publication === 'not-started' && work.ticket.status().compensation !== 'confirmed') {
      await work.completion.catch(() => {});
      await this.#archiveRuntime!.rewrite.settle();
      work.compensationInput ??= { commitId: randomUUID(), storeIdentity: this.#store!,
        target: this.#archiveRuntime!.targetFor(work.original), localHistoryGeneration: work.source.localHistoryGeneration,
        start: () => {
          let receipt: ArchiveOriginalHistoryCommit | undefined;
          work.compensationCompletion = this.#commitArchiveStore(work.original, { rewritten: true, reason: 'restore' }, work.source,
            work.compensationInput!.commitId, value => { receipt = value; });
          if (receipt === undefined) throw new Error('Missing original compensation receipt');
          return receipt.outcome;
        } };
      work.ticket.compensateHistory(work.compensationInput);
      await work.compensationCompletion;
    }
    if (work.ticket.status().legacyStore === 'not-started' && work.ticket.status().publication === 'not-started')
      work.ticket.concludeUnchanged();
    const result = await this.#archiveRuntime!.rewrite.settle();
    if (result.status !== 'completed') throw new Error('Archive rewrite reconciliation required');
    if (this.#archivePendingRewrite === work) this.#archivePendingRewrite = undefined;
    } catch (cause) {
      if (work.failureOperationIds.length === 0) work.failureOperationIds.push(this.#recordArchiveFailure(cause, 'archive_rewrite_reconciliation_required'));
      throw new ArchiveRewriteReconciliationError('Archive rewrite reconciliation required', { cause });
    }
  }

  async #persistArchiveRewrite(work: LifecycleRewriteWork, candidate: readonly IRMessage[],
    reason: HistoryRewriteReason, publish?: () => void): Promise<(apply?: () => void) => void> {
    const runtime = this.#archiveRuntime!;
    const observation = this.#archiveHistoryObservation(candidate)!;
    const target = runtime.targetFor(candidate);
    // 原计算工作已完成。资源 ended 门不能依赖随后等待它的 rewrite finalizer。
    work.ended();
    work.ticket.stage({ target, localHistoryGeneration: observation.localHistoryGeneration });
    await this.#enqueueCommit(async () => {
      work.signal.throwIfAborted();
      const commitId = randomUUID();
      work.commitInput = { commitId, storeIdentity: this.#store!, target,
        localHistoryGeneration: observation.localHistoryGeneration, start: () => {
          work.completion = this.#commitArchiveStore(structuredClone(candidate), { rewritten: true, reason }, observation,
            commitId, receipt => { work.receipt = receipt; });
          if (work.receipt === undefined) throw new Error('Missing original rewrite Store receipt');
          return work.receipt.outcome;
        } };
      work.ticket.commitHistory(work.commitInput);
      await work.completion;
      await runtime.rewrite.settle();
      work.signal.throwIfAborted();
      if (this.#closed) throw new Error('Archive rewrite closed before publication');
      this.#storeRewritePending = false;
    });
    return (apply = publish) => {
      if (apply === undefined) throw new Error('Archive rewrite requires original publisher');
      work.ticket.publish({ apply, assertPublished: () => {
        const seen = this.#archiveHistoryPort!.read().observation;
        if (seen.sourceSnapshotDigest !== observation.sourceSnapshotDigest ||
          seen.localHistoryGeneration !== observation.localHistoryGeneration) throw new Error('Archive rewrite publication mismatch');
      } });
    };
  }

  async #notifyArchiveHistoryCommit(reason: HistoryRewriteReason): Promise<void> {
    if (this.#onHistoryCommit === undefined) return;
    const history = this.messages(), id = this.#lifecycle.operation();
    await this.#enqueueCommit(async () => {
      try {
        await this.#onHistoryCommit!(history, { rewritten: true, reason });
        this.#callbackRewritePending = false; this.#lifecycle.recover('history_callback', id);
      } catch (cause) {
        this.#callbackRewritePending = true; this.#lifecycle.fail(id, 'history_callback', cause);
      }
    });
  }

  #commitHistory(rewritten: boolean, reason?: HistoryRewriteReason, ticket?: LifecycleRun,
    fixed?: NonNullable<LifecycleTurn['continuation']>): Promise<void> | undefined {
    if (this.#store === undefined && this.#onHistoryCommit === undefined) return;
    const history = fixed?.history ?? this.messages();
    const observation = fixed?.observation ?? this.#archiveHistoryObservation(history);
    // 与轮末历史一起拍摄，不能等异步队列执行时读取下一轮准入状态。
    let attachmentMeta: Parameters<SessionStore['commit']>[2] | undefined;
    let attachmentFailure: unknown;
    try { attachmentMeta = this.#attachmentCommitMeta(history, { rewritten, ...(reason !== undefined ? { reason } : {}) }); }
    catch (error) { attachmentFailure = error; }
    this.#generation++;
    const id = this.#lifecycle.operation();
    return this.#enqueueCommit(async () => {
      const failed: SessionLifecycleFailure[] = [];
      if (this.#store !== undefined) {
        try {
          if (attachmentFailure !== undefined) throw attachmentFailure;
          await this.#commitArchiveStore(structuredClone(history), { ...attachmentMeta, rewritten: rewritten || this.#storeRewritePending,
            ...(reason !== undefined ? { reason } : {}) }, observation, fixed?.commitId,
            ticket === undefined ? undefined : receipt => {
              if (fixed !== undefined) fixed.receipt = receipt;
              try { ticket.commitHistory({ commitId: receipt.commitId,
                storeIdentity: receipt.storeIdentity, target: this.#archiveRuntime!.targetFor(history, receipt),
                localHistoryGeneration: receipt.observation.localHistoryGeneration, outcome: receipt.outcome }); }
              catch (cause) {
                const operationId = this.#recordArchiveFailure(cause, 'archive_store_metadata_unconfirmed');
                fixed?.failureOperationIds.push(operationId);
              }
            });
          this.#storeRewritePending = false;
          this.#lifecycle.recover('store_commit', id);
        } catch (cause) {
          this.#storeRewritePending = true;
          this.#lifecycle.fail(id, 'store_commit', cause);
          failed.push({ operationId: id, phase: 'store_commit', code: 'store_commit_failed', cause, recovered: false });
        }
      }
      if (this.#onHistoryCommit !== undefined) {
        try {
          await this.#onHistoryCommit(structuredClone(history), { rewritten: rewritten || this.#callbackRewritePending,
            ...(reason !== undefined ? { reason } : {}) });
          this.#callbackRewritePending = false;
          this.#lifecycle.recover('history_callback', id);
        } catch (cause) {
          this.#callbackRewritePending = true;
          this.#lifecycle.fail(id, 'history_callback', cause);
          failed.push({ operationId: id, phase: 'history_callback', code: 'history_callback_failed', cause, recovered: false });
        }
      }
      if (failed.length > 0) this.#reportCommitFailure(failed);
    });
  }

  #enqueueCommit<T>(work: () => Promise<T>): Promise<T> {
    this.#commits++;
    const task = this.#commitTail.then(work);
    this.#commitTail = task.then(() => { this.#commits--; }, () => { this.#commits--; });
    return task;
  }

  #trackQuery(handle: QueryHandle): OriginalQuerySettlementReceipt | undefined {
    const id = this.#lifecycle.operation();
    const registration = this.#archiveHistoryPort === undefined ? undefined : registerOriginalQuerySettlement(handle);
    if (registration === undefined && handle.settle === undefined) {
      this.#lifecycle.fail(id, 'query', undefined, 'query_settlement_unavailable');
      return;
    }
    const accept = async (result: QuerySettlement | undefined): Promise<void> => {
      if (result === undefined) return;
      if (result.status !== 'completed' || result.failureCount > 0) this.#sourceSettlementFailed = true;
      const added = this.#resourceDiagnostics.accept(result, handle);
      for (const failure of added.failures) this.#lifecycle.fail(id, 'query', failure.cause, `${failure.phase}_failed`);
      this.#lifecycle.omitFailures(added.omittedFailures);
      if (result.status !== 'completed' && result.failures.length === 0) this.#lifecycle.fail(id, 'query', undefined, `query_${result.status}`);
      if (result.status === 'timeout' || result.status === 'cancelled' ||
        result.pending.execution + result.pending.modelStreams + result.pending.toolExecutors > 0) {
        this.#lifecycle.fail(id, 'query', undefined, 'query_settlement_incomplete');
        // 违约快照无实际完成证据:持屏障(drain 观察超时不推进 MCP 关闭);仅 drain({ force }) 释放(RV-3-08)
        await this.#settlementBarrier.promise;
      }
    };
    let task: Promise<void>;
    if (registration === undefined) {
      const ready = new Promise<void>((resolve) => { this.#releaseQuerySettlement = resolve; });
      task = ready.then(() => handle.settle?.({ timeoutMs: Infinity })).then(accept,
        (cause: unknown) => { this.#lifecycle.fail(id, 'query', cause); });
    } else {
      this.#releaseQuerySettlement = registration.markEventsEnded;
      task = registration.receipt.outcome.then(async outcome => {
        if (outcome.state === 'fulfilled') { await accept(readQuerySettlementSnapshot(outcome.result.value)); return; }
        this.#lifecycle.fail(id, 'query', outcome.state === 'unavailable' ? undefined : outcome.reason,
          outcome.state === 'unavailable' ? 'query_settlement_unavailable' : 'query_settlement_unconfirmed');
        await this.#settlementBarrier.promise;
      }).catch(async cause => {
        this.#lifecycle.fail(id, 'query', cause);
        await this.#settlementBarrier.promise;
      });
    }
    this.#querySettlements.add(task);
    void task.then(() => { this.#querySettlements.delete(task); });
    return registration?.receipt;
  }
  /** 空闲直压可能使用临时manager；即使逻辑结果先到，也保留该实例真实收尾。 */
  #trackManager(manager: Pick<ContextManager, 'settle'>, settle?: () => Promise<ResourceSettlementResult>): void {
    const id = this.#lifecycle.operation();
    const task = (settle?.() ?? manager.settle({ timeoutMs: Infinity })).then(async (result) => {
      if (result.status !== 'completed' || result.failureCount > 0) this.#sourceSettlementFailed = true;
      const added = this.#resourceDiagnostics.accept(result, manager);
      for (const failure of added.failures) this.#lifecycle.fail(id, 'idle_operation', failure.cause, `compaction_${failure.phase}_failed`);
      this.#lifecycle.omitFailures(added.omittedFailures);
      if (result.status === 'timeout' || result.status === 'cancelled' ||
        result.pending.execution + result.pending.modelStreams + result.pending.toolExecutors > 0) {
        this.#lifecycle.fail(id, 'idle_operation', undefined, 'compaction_settlement_incomplete');
        await this.#settlementBarrier.promise;
      }
    }, (cause: unknown) => { this.#lifecycle.fail(id, 'idle_operation', cause, 'compaction_settlement_failed'); });
    this.#managerSettlements.add(task);
    void task.then(() => { this.#managerSettlements.delete(task); });
  }

  #reportCommitFailure(failures: readonly SessionLifecycleFailure[]): void {
    const message = lifecycleSummary(failures);
    if (this.#queue.ended) {
      this.#warnAfterEnd(message);
      return;
    }
    this.#push(
      { type: 'turn.error', scope: 'sdk.persistence', message, recoverable: true },
      'sdk',
    );
  }

  // ———— W-2:cwd 中途切换(doc/116 §3.7 D-7 / doc/117 W-2;空闲期语义,独立方法块)————

  /** 会话当前工作目录(session.created.cwd 起,随 setCwd 改写) */
  cwd(): string {
    return this.#cwd;
  }

  /**
   * 切换会话工作目录(空闲期;运行中 → rejected:'turn_running',不排队)。校验经 kernel
   * resolveSessionCwd(绝对 / 存在 / 目录;SDK 面由本机开发者亲手给出,恒等 resolve 策略——与
   * CLI `--cwd` 同律,不设白名单闸);通过即:按新 cwd 重建执行器 + 权限门 + PreToolUse hooks
   * (装配层 rebindCwd;registry / 已读文件表 / grep 聚合共享;权限引擎实例复用)→ 改写会话
   * cwd(快照 meta.cwd、恢复对账锚、Task 子代理 ToolContext.cwd 随之)→ 推 session.cwd_changed
   * {from,to} → 落盘 store.recordCwdChange(meta.cwd 改写 + cwdHistory 追加;缺席不落)。
   * 与 compact()/restore() 同一空闲期串行链(期间 send() 缓冲)。目标与当前相同:零副作用回
   * changed(不推事件、不落盘)。不重跑 loadConfig(项目层配置不随 cwd 变;已知限界,详见 SDK 技术手册)。
   */
  async setCwd(dir: string): Promise<SetCwdResult> {
    this.#assertOpen('setCwd');
    return this.#runIdleOp(async (): Promise<SetCwdResult> => {
      if (this.#handle !== null) return { status: 'rejected', reason: 'turn_running' };
      const from = this.#cwd;
      const resolved = await resolveSessionCwd({
        requested: dir,
        fallback: from,
        policy: { resolve: (candidate) => candidate },
      });
      if (!resolved.ok) return { status: 'rejected', reason: resolved.reason };
      const to = resolved.cwd;
      if (to === from) return { status: 'changed', from, to };
      this.#executor = this.#rebindCwd(to);
      this.#cwd = to;
      this.#attachmentManager?.runPostCompactInvalidations();
      if (this.#dormantAttachmentCheckpoint !== undefined) this.#dormantAttachmentCheckpoint = rebindAttachmentCheckpoint(
        this.#dormantAttachmentCheckpoint, { scope: this.#attachmentScope(from), nextScope: this.#attachmentScope(to),
          messages: this.#history, invalidateReferences: true });
      this.#push({ type: 'session.cwd_changed', from, to }, 'sdk');
      const change: SessionCwdChange = { at: new Date().toISOString(), from, to };
      if (this.#onCwdChanged !== undefined) {
        const id = this.#lifecycle.operation();
        await this.#enqueueCommit(async () => {
          try { await this.#onCwdChanged?.(change); }
          catch (error) {
            this.#lifecycle.fail(id, 'cwd_commit', error);
            this.#reportCwdChangeFailure(error);
          }
        });
      }
      if (this.#attachmentCheckpoints && this.#store !== undefined) await this.#commitHistory(false);
      return { status: 'changed', from, to };
    });
  }

  /** setCwd 落盘缝失败面(#reportCommitFailure 同律:入流 recoverable,流终结退化 console.warn) */
  #reportCwdChangeFailure(error: unknown): void {
    const message = lifecycleSummary([{ operationId: 0, phase: 'cwd_commit', code: 'cwd_commit_failed', cause: error, recovered: false }]);
    if (this.#queue.ended) {
      this.#warnAfterEnd(message);
      return;
    }
    this.#push(
      { type: 'turn.error', scope: 'sdk.persistence', message, recoverable: true },
      'sdk',
    );
  }
}

/**
 * 快照落点解析(优先级:显式 store > 显式 dir > 会话 store 同居缺省 > 未接线)。
 * 保留策略(`retention` / `max` 别名)对 dir 形态由 kernel 在写后施行;对 store /
 * 同居形态经 withCheckpointRetention 包装施行——三形态同一淘汰律(kernel planCheckpointRetention:
 * 先龄再数、刚写入者恒保留、不分 trigger)。
 */
function resolveCheckpoints(
  options: SessionCheckpointOptions | undefined,
  sessionStore: SessionStore | undefined,
): ResolvedCheckpoints | null {
  const opts = options ?? {};
  if (opts.store !== undefined && opts.dir !== undefined) {
    throw new TansrSdkError(
      'invalid_options',
      'createSession: "checkpoints.store" and "checkpoints.dir" are mutually exclusive; pass one of them.',
    );
  }
  if (opts.max !== undefined && (!Number.isFinite(opts.max) || opts.max < 0)) {
    throw new TansrSdkError(
      'invalid_options',
      `createSession: "checkpoints.max" must be a non-negative finite number (got ${String(opts.max)}).`,
    );
  }
  const retention = resolveCheckpointRetention(opts.max, opts.retention);
  if (retention !== undefined) assertValidCheckpointRetention(retention, 'createSession: "checkpoints.retention"');
  let store: CheckpointStore | undefined;
  if (opts.store !== undefined) {
    store = retention !== undefined ? withCheckpointRetention(opts.store, retention) : opts.store;
  } else if (opts.dir !== undefined) {
    store = createFileCheckpointStore({ dir: opts.dir, ...(retention !== undefined ? { retention } : {}) });
  } else if (sessionStore?.checkpoints !== undefined) {
    store =
      retention !== undefined ? withCheckpointRetention(sessionStore.checkpoints, retention) : sessionStore.checkpoints;
  }
  if (store === undefined) return null;
  return {
    store,
    autoBeforeCompact: opts.autoBeforeCompact ?? true,
    autoBeforeClear: opts.autoBeforeClear ?? true,
  };
}

/**
 * 创建多轮会话。托管模式(model 为别名字符串)含异步配置装载;注入模式
 * (client + model 对象)零 IO——统一返回 Promise,调用方一律 await。
 * 装配失败以 TansrSdkError 结构化抛出(不 process.exit)。
 */
export function createSession(options: CreateSessionOptions = {}): Promise<AgentSession> {
  return assembleSession(options);
}

/** @internal SDK 宿主依赖；不进入公开配置、包导出或 SDK1 协议。 */
export interface SdkSessionRuntimeDependencies {
  capture?: SessionCaptureCoordinator;
  contextMaterials?: ContextMaterialReader;
  gatewayCache?: import('./platform/cache-runtime.js').GatewayCacheAssembler;
  cacheSession?: {
    refreshConfiguration?(signal: AbortSignal): Promise<void>;
    configurationRefreshRequired?(): boolean;
    confirmConfiguration?(revision: string): void;
    /** 缓存宿主 fork 源授权在异步装配后、原 Store 写入前同步复验。 */
    beforeFork?(): void;
    selectRuntime(stored: string | undefined, resumed: boolean): Promise<string>;
    archived(previous: string): string;
    initialize(session: AgentSession, runtimeSessionId: string): Promise<void>;
    confirmRuntime(runtimeSessionId: string): void;
  };
}

/** @internal 供嵌入宿主装配共享协调器；不从 SDK 公共入口导出。 */
export function createSessionWithRuntime(options: CreateSessionOptions, runtime: SdkSessionRuntimeDependencies): Promise<AgentSession> {
  return assembleSession(options, runtime);
}

async function assembleSession(options: CreateSessionOptions, runtime?: SdkSessionRuntimeDependencies): Promise<AgentSession> {
  const cwd = options.cwd ?? process.cwd();
  // doc/91 拍板③:resume 在场时逻辑会话 id 锚定恢复目标(显式 sessionId
  // 同给必须一致——同一会话不允许双 id)
  if (options.resume !== undefined) {
    if (options.initialMessages !== undefined) {
      throw new TansrSdkError(
        'invalid_options',
        'createSession: "resume" and "initialMessages" are mutually exclusive; ' +
          '"resume" loads the history from the store ("initialMessages" is the low-level re-feed escape hatch).',
      );
    }
    if (options.sessionId !== undefined && options.sessionId !== options.resume.sessionId) {
      throw new TansrSdkError(
        'invalid_options',
        'createSession: "sessionId" conflicts with "resume.sessionId"; pass one of them (they must match).',
      );
    }
  }
  // F-2:fork 快捷形前置校验(fail-fast,先于任何 IO)
  if (options.fork !== undefined) {
    if (options.resume !== undefined) {
      throw new TansrSdkError(
        'invalid_options',
        'createSession: "fork" and "resume" are mutually exclusive; "fork" already opens the new session it creates ' +
          '(use session.fork() + a separate createSession({ resume }) for the two-step form).',
      );
    }
    if (options.initialMessages !== undefined) {
      throw new TansrSdkError(
        'invalid_options',
        'createSession: "fork" and "initialMessages" are mutually exclusive; the forked session starts from the checkpoint history.',
      );
    }
    if (options.store === undefined) {
      throw new TansrSdkError(
        'invalid_options',
        'createSession: "fork" requires "store" (the new session is created as a record in that store, then resumed from it).',
      );
    }
  }
  const injected = options.client !== undefined;
  const tokenTier = options.token !== undefined;
  if (runtime?.gatewayCache !== undefined && (!tokenTier || injected)) {
    throw new TansrSdkError('invalid_options', 'Gateway cache requires the platform token tier.');
  }
  if (injected && typeof options.model === 'string') {
    throw new TansrSdkError(
      'invalid_options',
      'createSession: "client" requires "model" to be a ResolvedModel object (injected mode); ' +
        'a model alias string implies managed assembly and conflicts with a caller-provided client.',
    );
  }
  if (!injected && typeof options.model === 'object') {
    throw new TansrSdkError(
      'invalid_options',
      'createSession: a ResolvedModel object requires a paired "client" (injected mode); ' +
        'pass a model alias string for managed assembly.',
    );
  }
  validateTokenTierOptions('createSession', options);
  // DEC-RF-29:预算段选项校验先于任何 IO(与 events / checkpoints 同律 fail-fast)
  validateSessionBudgetOptions('createSession', options.budget);
  // S-1:快照落点解析先于任何 IO(选项错误 fail-fast,不留半登记的会话)
  const checkpoints = resolveCheckpoints(options.checkpoints, options.store);
  // doc/113 G-5:裁决人前置裁决(同步零 IO;姿态推导 / A 无 askUser fail-fast / 非令牌档误用)
  const preparedAdjudication = prepareAdjudication({
    api: 'createSession',
    options: options.adjudication,
    tokenTier,
    askUserPresent: options.permission?.askUser !== undefined,
  });

  // F-2 — fork 快捷形(= fork + resume):读源会话快照 → (装配成功后)store.fork(或五方法回落)落新会话
  // 记录 → 按 resume 同径预载打开;新会话首次打开即签发 session.forked(构造后紧随 session.created)。
  // RV-3-03:落新记录挪到工装 / 模型装配之后、会话登记之前——装配失败(invalid_options / assembly_failed)
  // 不再留下孤儿记录;登记或构造失败则在 catch 里删除已落记录(store 列表零净增)。
  const resume = options.resume;
  let forkOrigin: { sourceSessionId: string; checkpointId: string } | undefined;
  /** 待 fork 的源快照(校验已过;装配成功后落 store) */
  let forkCheckpoint: Checkpoint | undefined;
  /** 已落 store 的 fork 新记录 id(catch 回滚用) */
  let forkedRecordId: string | undefined;
  if (options.fork !== undefined) {
    if (checkpoints === null) {
      throw new TansrSdkError(
        'checkpoints_not_wired',
        'createSession({ fork }) requires a checkpoint store to read the source checkpoint from: pass ' +
          '{ checkpoints: { dir } } / { checkpoints: { store } }, or a session "store" that provides colocated checkpoints (createFileSessionStore).',
      );
    }
    const checkpoint = await checkpoints.store.get(options.fork.sessionId, options.fork.checkpointId);
    if (checkpoint === null) {
      throw new TansrSdkError(
        'checkpoint_not_found',
        `createSession({ fork }): checkpoint "${options.fork.checkpointId}" was not found for session "${options.fork.sessionId}".`,
      );
    }
    if (checkpoint.sessionId !== options.fork.sessionId) {
      throw new TansrSdkError(
        'checkpoint_store_corrupted',
        `createSession({ fork }): the checkpoint store returned checkpoint "${options.fork.checkpointId}" owned by session ` +
          `"${checkpoint.sessionId}" when asked for session "${options.fork.sessionId}".`,
      );
    }
    forkCheckpoint = checkpoint;
    forkOrigin = { sourceSessionId: options.fork.sessionId, checkpointId: options.fork.checkpointId };
  }
  const sessionId = options.sessionId ?? resume?.sessionId ?? randomUUID();
  const eventBacklog = resolveEventBacklog(options.events);

  // doc/91 拍板③/④ — resume 预载:store 取回 → 配对治理 → initialMessages。
  // 治理结果静默采用(断尾补占位/孤儿截断是恢复语义本体,非错误);目标
  // 缺失与存储损坏则 fail-fast 结构化抛出,恒不静默起一个空会话。
  let initialMessages: readonly IRMessage[] = options.initialMessages ?? [];
  /** resume 记录已存的平台 ULID(旧记录无键 = undefined) */
  let storedPlatformSessionId: string | undefined;
  /** W-3:resume 记录已存的会话 cwd(对账锚;自建 store 无键 = 不对账) */
  let storedCwd: string | undefined;
  let storedAttachmentCheckpoint: unknown;
  let storedAttachmentRepaired = false;
  if (resume !== undefined) {
    const record = await resume.store.get(resume.sessionId);
    if (record === null) {
      throw new TansrSdkError(
        'session_not_found',
        `createSession: session "${resume.sessionId}" was not found in the given store; ` +
          'list() the store or create a fresh session instead.',
      );
    }
    const repaired = repairHistoryPairing(record.messages);
    initialMessages = repaired.messages;
    storedAttachmentRepaired = repaired.droppedMessages > 0 || repaired.synthesizedResults > 0 ||
      (resume.store.attachmentCheckpoints === true && record.attachmentReferencesInvalidated === true);
    storedPlatformSessionId = record.meta.platformSessionId;
    storedCwd = record.meta.cwd;
    if (resume.store.attachmentCheckpoints === true) storedAttachmentCheckpoint = record.attachmentCheckpoint;
  }

  // doc/91 拍板⑤ — 令牌档 per-AgentSession 铸平台会话 ULID(经 twp
  // meta.sessionId 缝出线;非令牌档无平台面,恒缺席)。
  // doc/110 B2(翻案拍板⑤实施语义,用户批准 2026-09-02):resume 沿用记录
  // 已存 ULID——prompt_cache_key/metadata.user_id/网关池粘性/会话归因四件跨
  // resume 延续;旧记录无键铸新(store 登记时补录,下次 resume 起稳定)。
  // M-07(doc/130 §五;十王修案 FX-B-28):`let`——平台对该 id 回 410 session_archived(归档 /
  // 租约过期,resume 旧记录时典型)即铸新 id:供给闭包 `() => platformSessionId` 读现值,下轮
  // exchange 以新 id 开新会话(首见幂等登记);对旧 id 恒不再心跳、不重试;store 登记以新值对账。
  let platformSessionId = tokenTier ? (runtime?.cacheSession === undefined ? storedPlatformSessionId ?? ulid()
    : await runtime.cacheSession.selectRuntime(storedPlatformSessionId, resume !== undefined)) : undefined;

  // 入流口后绑定解环(与 serve 装配同法):hooks emit / provider 切换 / todo onChange 三处闭包
  // 引用会话入流口,AgentSession 构造期经 bindEmit 回填(类内 #push,不再依赖公开 pushBody;RV-3-06)。
  // 首轮事件前不可达,无丢失窗口。
  let emitBound: ((body: EventBody, source: string) => void) | null = null;
  const emitBody = (body: EventBody, source: string): void => {
    emitBound?.(body, source);
  };
  // W-3:告警通道——平台源(令牌档 t.warn / app_platform_mismatch / 裁决人三码)与 sdk 源
  // (cwd 对账提示)统一汇入 onWarning;onPlatformWarning 保留为平台子集通道。sdk 源在
  // onWarning 缺席时回落 turn.error{scope:'sdk.notice'}:装配期(session 尚未构造)先缓冲,
  // 构造后冲刷(seq 紧随 session.created)。
  const pendingNotices: SdkWarning[] = [];
  const noticeOf = (warning: SdkWarning): EventBody => ({
    type: 'turn.error',
    scope: 'sdk.notice',
    message: `${warning.code}: ${warning.message}`,
    recoverable: true,
  });
  const warn = createSdkWarningChannel({
    ...(options.onWarning !== undefined ? { onWarning: options.onWarning } : {}),
    ...(options.onPlatformWarning !== undefined ? { onPlatformWarning: options.onPlatformWarning } : {}),
    notice: (warning) => {
      if (emitBound !== null) emitBound(noticeOf(warning), 'sdk');
      else pendingNotices.push(warning);
    },
  });
  const onWarn = (warning: PlatformWarning): void =>
    warn({ source: 'platform', code: warning.code, message: warning.message, ...(warning.detail !== undefined ? { detail: warning.detail } : {}) });
  // W-3(doc/116 §3.7 resume 对账,SDK 面;'current' 策:按本次 cwd 运行、差异恒提示不改目录):
  // 旧记录 meta.cwd 为建档时进程 cwd(W-2 前 file-store 恒写 process.cwd()),差异如实提示
  if (options.resume !== undefined && storedCwd !== undefined && storedCwd !== cwd) {
    warn({
      source: 'sdk',
      code: 'cwd_mismatch_on_resume',
      message:
        `session "${sessionId}" was last running in "${storedCwd}" but is resumed in "${cwd}"; ` +
        'the working directory was left unchanged (pass cwd to resume in the stored directory).',
      detail: { storedCwd, currentCwd: cwd },
    });
  }

  let binding: ModelBinding;
  let registry: ProviderRegistry | null = null;
  let loaded: LoadedConfig | null = null;
  let bundleCapabilities: AppCapabilities | undefined;
  /** 令牌档 bundle 计价行(会话预算账本的 USD 桶来源;其余档缺席 = 内置缺省簿) */
  let pricingUpdates: readonly PricingUpdate[] | undefined;
  // S-E1 系统工具的平台提供方材料:仅令牌档在场(baseUrl/token/fetchImpl 三件套)。
  let platformContext: PlatformToolContext | undefined;
  // doc/113 G-5:令牌档裁决人装配产物(bundle 任命 × 姿态);其余档恒缺席
  let adjudication: ReturnType<typeof assembleTokenTierAdjudicator>;
  let platformSystemPrompt: string | undefined;
  let systemPromptPolicy: SystemPromptPolicy | undefined;
  let cacheConfiguration: ReturnType<typeof readPlatformConfiguration>;
  if (injected) {
    binding = {
      client: options.client as ModelClient,
      model: options.model as ResolvedModel,
      ...(options.contextWindowTokens !== undefined
        ? { contextWindowTokens: options.contextWindowTokens }
        : {}),
      ...(options.maxOutputTokens !== undefined
        ? { maxOutputTokens: options.maxOutputTokens }
        : {}),
    };
  } else if (tokenTier) {
    // 令牌档:bundle 供给模型与能力位;恒零本地配置 IO(hooks 无配置来源不装);
    // 平台提示帧经 W-3 告警通道(onWarn 见上)汇入
    // RV-3-04:令牌 getter 形——authedFetch 作最内层 fetch 逐请求取现值覆写令牌头;字符串形零变化
    const tokenSource = options.token as AppTokenSource;
    const tokenSnapshot = readAppToken('createSession', tokenSource);
    const authedFetch = createAppTokenSourceFetch('createSession', tokenSource, options.fetchImpl);
    const assembled = await assemblePlatformModelWithRuntime({
      token: tokenSnapshot,
      baseUrl: options.baseUrl as string,
      ...(typeof options.model === 'string' ? { model: options.model } : {}),
      fetchImpl: authedFetch,
      // 拍板⑤:本会话全部 exchange(含 setModel 热切换与 Task 子代理,同
      // registry 同缝)恒携同一枚平台会话 ULID——会话维归因/亲和自此在场
      ...(platformSessionId !== undefined ? { sessionId: () => platformSessionId } : {}),
      // M-07:装配期心跳 410 session_archived → 停该 id 心跳(不重试)+ 铸新 id(下轮 exchange 开新会话)
      onSessionArchived: (archivedId) => {
        if (platformSessionId === undefined) return;
        if (archivedId !== undefined && archivedId !== platformSessionId) return;
        const previous = platformSessionId;
        platformSessionId = runtime?.cacheSession?.archived(previous) ?? ulid();
        warn({
          source: 'sdk',
          code: 'platform_session_archived',
          message:
            `platform session "${previous}" was archived by the platform (410 session_archived); ` +
            `a new platform session "${platformSessionId}" starts with the next exchange (no retry against the archived id).`,
          detail: { archivedSessionId: previous, platformSessionId },
        });
      },
      onProviderSelected: (selection) => {
        const body = providerSwitchedBody(selection);
        if (body !== null) emitBody(body, 'provider');
      },
      onWarn,
    }, runtime?.gatewayCache === undefined ? {} : { gatewayCache: runtime.gatewayCache });
    // doc/112 §5.5:会话服务拓扑的应用在进程内装配 → 提示不拒(位已按拓扑归一)
    warnEmbeddedPlatformMismatch(assembled.platform, options.baseUrl as string, onWarn);
    binding = {
      client: assembled.client,
      model: assembled.model,
      ...(assembled.contextWindowTokens !== undefined
        ? { contextWindowTokens: assembled.contextWindowTokens }
        : {}),
      ...(assembled.maxOutputTokens !== undefined
        ? { maxOutputTokens: assembled.maxOutputTokens }
        : {}),
    };
    registry = assembled.registry;
    bundleCapabilities = assembled.capabilities;
    pricingUpdates = assembled.pricingUpdates;
    platformSystemPrompt = assembled.systemPrompt;
    systemPromptPolicy = assembled.systemPromptPolicy;
    cacheConfiguration = readPlatformConfiguration(assembled);
    platformContext = {
      baseUrl: options.baseUrl as string,
      token: tokenSnapshot,
      // 媒体工具 / 直连客户端 / 提示词刷新器同经 authedFetch(getter 形每请求现值;字符串形等价于原 fetchImpl 包装)
      fetchImpl: authedFetch,
      // S-G1:bundle 授权媒体集随装配注入(媒体工具描述自列;集成方零手配)。
      platformModels: assembled.platformModels,
    };
    adjudication = assembleTokenTierAdjudicator(preparedAdjudication, assembled, onWarn);
  } else {
    const assembled = await assembleManagedModel({
      ...(typeof options.model === 'string' ? { model: options.model } : {}),
      cwd,
      ...(options.env !== undefined ? { env: options.env } : {}),
      ...(options.fetchImpl !== undefined ? { fetchImpl: options.fetchImpl } : {}),
      ...(options.config !== undefined ? { config: options.config } : {}),
      ...(options.loadOptions !== undefined ? { loadOptions: options.loadOptions } : {}),
      onProviderSelected: (selection) => {
        const body = providerSwitchedBody(selection);
        if (body !== null) emitBody(body, 'provider');
      },
    });
    binding = {
      client: assembled.client,
      model: assembled.model,
      ...(assembled.contextWindowTokens !== undefined
        ? { contextWindowTokens: assembled.contextWindowTokens }
        : {}),
      ...(assembled.maxOutputTokens !== undefined
        ? { maxOutputTokens: assembled.maxOutputTokens }
        : {}),
    };
    registry = assembled.registry;
    loaded = assembled.loaded;
  }

  // ———— 工装一站式(权限五层+附加规则 → 能力位裁剪 → 工具集 → 门 → 执行器)————
  // 能力位来源:令牌档恒 bundle(平台治理面,选项已在互斥校验拒绝);
  // 托管/注入档取 options.capabilities(缺省平台缺省档)
  const effectiveCapabilities = bundleCapabilities ?? options.capabilities;
  // Read能力属于正在执行的模型；运行中设置下轮模型不得反向改写本轮权限能力判定。
  const imageInputHolder: { session?: AgentSession } = {};
  const imageInput = (): InputModalityMode | undefined =>
    resolveImageInputMode(imageInputHolder.session?.currentModel({ active: true }) ?? binding.model, registry);
  const tooling = await assembleTooling({
    cwd,
    sessionId,
    loaded,
    imageInput,
    // RV-3-10:出网代理决策绑定注入的 options.env(缺席 = 进程级单例读 process.env)
    egress: sdkEgressClient(options.env),
    ...(options.tools !== undefined ? { tools: options.tools } : {}),
    ...(effectiveCapabilities !== undefined ? { capabilities: effectiveCapabilities } : {}),
    ...(platformContext !== undefined ? { platformContext } : {}),
    ...(options.skills !== undefined ? { skills: options.skills } : {}),
    ...(options.mcp !== undefined ? { mcp: options.mcp } : {}),
    ...(options.promptChannel !== undefined ? { promptChannel: options.promptChannel } : {}),
    ...(options.permission !== undefined ? { permission: options.permission } : {}),
    // 项目层 hooks 信任门(缺席 = fail-closed)与宿主 shell 方言(缺席 = 平台缺省)原样透传
    ...(options.trustProjectHooks !== undefined ? { trustProjectHooks: options.trustProjectHooks } : {}),
    ...(options.hostShell !== undefined ? { hostShell: options.hostShell } : {}),
    // FX-B-05:分类器配置 + 生效姿态 + 生效资格档三件同传,权限模式由 kernel modeUnderAdjudicator 单点推导;
    // FX-B-06:explicit(开发者显式给了 adjudication)供 Q-05 兼容径裁「真矛盾 fail-fast」还是「缺省注入让位 mode」
    ...(adjudication !== undefined && preparedAdjudication !== undefined
      ? {
          adjudication: {
            classifier: adjudication.config,
            posture: adjudication.posture,
            tier: adjudication.tier,
            explicit: preparedAdjudication.explicit,
          },
        }
      : {}),
    // FX-B-09:审计缝透传(缺省由 assembleTooling 对裁决人终局经 warn 通报 permission_decided)
    ...(options.onDecision !== undefined ? { onDecision: options.onDecision } : {}),
    emit: emitBody,
    warn,
  });

  // 宿主段在建会时私有复制；仅平台字段可逐轮变化。
  try {
  const hostSystem = options.system?.map((segment) => ({ ...segment }));
  const appendedSystem = [...(options.systemAppend ?? []).map((segment) => ({ ...segment })), ...tooling.systemSegments];
  const initialPrompt = { systemPrompt: platformSystemPrompt, systemPromptPolicy };
  const applicationPrompt = resolveApplicationSystem(initialPrompt, hostSystem);
  const system = [...applicationPrompt.system, ...appendedSystem];
  const refreshPrompt = platformContext === undefined ? undefined : createApplicationPromptRefresherWithRuntime({
    baseUrl: platformContext.baseUrl,
    token: platformContext.token,
    ...(platformContext.fetchImpl !== undefined ? { fetchImpl: platformContext.fetchImpl } : {}),
    initial: initialPrompt,
  }, cacheConfiguration && runtime?.cacheSession?.refreshConfiguration ? {
    initial: cacheConfiguration, refreshConfiguration: signal => runtime.cacheSession!.refreshConfiguration!(signal),
    refreshRequired: () => runtime.cacheSession!.configurationRefreshRequired?.() ?? false,
    confirmConfiguration: revision => runtime.cacheSession!.confirmConfiguration?.(revision),
  } : undefined);
  const maxTokens = options.maxTokens ?? DEFAULT_MAX_TOKENS;

  // W-2:会话当前权限门(setCwd 切目录时随执行器换新);Task 子代理经转发门取**当前**门,
  // 子代理 cwd 缺省取派生时 ToolContext.cwd(= 父执行器当前 cwd)——两者随切换自动跟随
  let currentGate = tooling.permissionGate;
  const rebindCwd = (nextCwd: string): ToolExecutor => {
    const rebound = tooling.rebindCwd(nextCwd);
    currentGate = rebound.permissionGate;
    return rebound.executor;
  };

  // S-B4:agent 位统管 Task 家族——位开装配(注册进同一 registry/toolDefs),
  // 位关恒不装;子代理沿用会话 client/system/权限门,三档降档经 registry 解析
  if (tooling.capabilities.tools.agent) {
    const resolveModel = subagentModelResolverOf(registry);
    attachSdkTaskTool(
      { registry: tooling.registry, toolDefs: tooling.toolDefs },
      {
        client: binding.client,
        system,
        parentModel: binding.model,
        maxTokens,
        sessionId,
        permissionGate: { decide: (call, hints) => currentGate.decide(call, hints) },
        ...(resolveModel !== undefined ? { resolveModel } : {}),
        // FX-B-19(T-16):子代理 McpDiscover 换装材料(懒目录在场才有;缺席 = kernel 摘除)
        ...(tooling.mcpDiscover !== undefined ? { mcpDiscover: tooling.mcpDiscover } : {}),
      },
    );
  }

  // doc/91 拍板③ — store 在场:会话登记(幂等;令牌档平台 ULID 映射随
  // 登记落 SessionRecordMeta,拍板⑤)+ 每轮落卷接管。登记失败 fail-fast
  // (createSession 拒绝——存储不可用时静默丢历史比报错更糟)。
  const store = options.store;
  if (forkCheckpoint !== undefined && store !== undefined && forkOrigin !== undefined) {
    runtime?.cacheSession?.beforeFork?.();
    // RV-3-03:装配已成功,此刻才落 fork 新记录;随后按 resume 同径自 store 取回预载(附件 / 配对治理同源)
    const forked = await forkIntoStore(store, { sourceSessionId: forkOrigin.sourceSessionId, checkpoint: forkCheckpoint, newSessionId: sessionId, cwd });
    forkedRecordId = forked.sessionId;
    const record = await store.get(forked.sessionId);
    if (record === null) {
      throw new TansrSdkError(
        'session_not_found',
        `createSession({ fork }): the forked session "${forked.sessionId}" was not found in the given store right after fork.`,
      );
    }
    initialMessages = repairHistoryPairing(record.messages).messages;
  }
  if (store !== undefined) {
    await store.create({
      sessionId,
      ...(platformSessionId !== undefined && runtime?.cacheSession === undefined ? { platformSessionId } : {}),
      // W-2:meta.cwd = 会话 cwd(此前 file-store 恒写 process.cwd();server 份 V-3 同笔)
      cwd,
    });
  }
  // W-2:切换落盘缝(store 提供 recordCwdChange 才接;自建五方法 store 缺席 = 不落盘)
  const onCwdChanged =
    store?.recordCwdChange !== undefined
      ? (change: SessionCwdChange): Promise<void> =>
          (store.recordCwdChange as NonNullable<SessionStore['recordCwdChange']>).call(store, sessionId, change)
      : undefined;

  // DEC-RF-29:会话预算闸(段形 = 配置 budget 段;两上限缺席 → 不接闸不建账本)
  const budget = createSessionBudget({
    budget: options.budget,
    sessionId,
    registry,
    ...(pricingUpdates !== undefined ? { pricingUpdates } : {}),
  });
  const session = new AgentSession({
    ...(storedAttachmentCheckpoint === undefined ? {} : { restoredAttachmentCheckpoint: storedAttachmentCheckpoint,
      ...(storedAttachmentRepaired ? { restoredAttachmentRepaired: true } : {}),
      ...(storedCwd === undefined ? {} : { restoredAttachmentCwd: storedCwd }) }),
    ...(runtime !== undefined ? { sdkSessionRuntime: runtime } : {}),
    binding,
    ...(typeof options.model === 'string' ? { modelAlias: options.model } : {}),
    ...(budget !== undefined ? { budget } : {}),
    executor: tooling.executor,
    system,
    applicationPrompt: applicationPrompt.info,
    ...(refreshPrompt !== undefined ? { prepareApplicationPrompt: async (signal: AbortSignal) => {
      const selected = resolveApplicationSystem(await refreshPrompt(signal), hostSystem);
      return { system: [...selected.system, ...appendedSystem], info: selected.info };
    } } : {}),
    tools: tooling.toolDefs,
    cwd,
    sessionId,
    maxTokens,
    thinking: options.thinking,
    maxTurnsPerQuery: options.maxTurnsPerQuery ?? DEFAULT_MAX_TURNS,
    compaction: options.compaction === false ? undefined : options.compaction ?? {},
    initialMessages,
    registry,
    onHistoryCommit: options.onHistoryCommit,
    ...(tooling.ownedResources !== undefined ? { ownedResources: tooling.ownedResources } : {}),
    ...(tooling.onEnded !== undefined ? { onEnded: tooling.onEnded } : {}),
    checkpoints,
    ...(adjudication !== undefined
      ? { onTurnStart: (prompts: readonly string[]) => adjudication?.transcript.noteTurn(prompts) }
      : {}),
    onInputsConsumed: (texts: readonly string[]) => {
      adjudication?.transcript.noteInput(texts);
      options.onInputsConsumed?.(texts);
    },
    rebindCwd,
    ...(onCwdChanged !== undefined ? { onCwdChanged } : {}),
    warn,
    hasWarningHandler: options.onWarning !== undefined,
    ...(store !== undefined ? { store } : {}),
    // doc/123 D-A9 ②:令牌档材料同源挂载 session.platform(工具与直连同一 baseUrl/token/fetchImpl)
    ...(platformContext !== undefined ? { platformContext } : {}),
    eventBacklog,
    bindEmit: (emit) => { emitBound = emit; },
    // RV-3-02:会话级 signal 的 abort 监听在 close 收尾时解除(50 会话共享 shutdown signal 不再挂 50 个闭包)
    ...(options.signal !== undefined ? { signal: options.signal } : {}),
  });
  imageInputHolder.session = session;
  if (runtime?.cacheSession !== undefined) {
    try {
      if (store === undefined || platformSessionId === undefined) throw new Error('Cache host requires original Store and platform runtime');
      await runtime.cacheSession.initialize(session, platformSessionId);
      const confirmed = await store.create({ sessionId, platformSessionId, cwd });
      if (confirmed.platformSessionId !== platformSessionId) throw new Error('Cache host Store runtime confirmation failed');
      runtime.cacheSession.confirmRuntime(platformSessionId);
    } catch (error) {
      session.close(); await session.drain(); throw error;
    }
  }
  // W-3:装配期缓冲的 sdk 源提示回落帧冲刷(seq 紧随 session.created)
  for (const pending of pendingNotices.splice(0)) emitBody(noticeOf(pending), 'sdk');
  // F-2:fork 快捷形 = 新会话首次打开——血缘事件紧随 session.created(seq 1;contract-v0.24)
  if (forkOrigin !== undefined) {
    emitBody({ type: 'session.forked', ...forkOrigin, sessionId }, 'sdk');
  }
  // 会话级中断信号:创建前已 aborted → 立即 close(created / forked 事件仍先入流);未 aborted 的监听
  // 已在构造期挂上并随 close 解除(RV-3-02)
  if (options.signal?.aborted === true) session.close();
  return session;
  } catch (cause) {
    // RV-3-03:fork 已落的新会话记录随装配失败一并回滚(store 列表不新增、无 meta.cwd=undefined 孤儿)
    if (forkedRecordId !== undefined && options.store !== undefined) {
      try {
        await options.store.delete(forkedRecordId);
      } catch (deleteCause) {
        if (tooling.ownedResources !== undefined) {
          const ledger = tooling.ownedResources.ledger;
          ledger.fail(ledger.operation(), 'assembly_rollback', deleteCause, 'fork_record_delete_failed');
        } else {
          throw new SdkLifecycleError('assembly_failed', [
            { operationId: 1, phase: 'assembly_rollback', code: 'fork_record_delete_failed', cause: deleteCause, recovered: false },
          ], { cause });
        }
      }
    }
    if (tooling.ownedResources !== undefined) return tooling.ownedResources.rollback(cause);
    throw cause;
  }
}
