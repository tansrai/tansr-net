/**
 * @tansr/sdk — Tansr 对外 TypeScript SDK(MIT,对外发布面)。
 *
 * 形态:SDK 是内核协议的语言绑定,不重复实现 agent 逻辑——
 * async generator 流式事件 + 结构化终值 + protocol 类型纯导出。
 *
 * 三档 API:
 * - `query(options)`:单轮便捷面(prompt in → KernelEvent 流 out,return
 *   值携带最终消息与 usage);
 * - `createSession(options)`:多轮会话(send/events/interrupt/messages/
 *   setModel/close;托管装配或注入 client 两模式);
 * - `runAgent(options)`:低阶薄包装(kernel runQuery 直通,返回 QueryHandle)。
 *
 * 分层:@tansr/sdk → kernel/providers/protocol;禁止 import @tansr/cli 与
 * 任何 UI 依赖(本包纯 headless)。
 */

// ———————————— SDK API 面 ————————————
export { defineTool } from './define-tool.js';
export type { DefineToolOptions, DefinedTool, ToolParameterSpec } from './define-tool.js';
export { assembleSdkSkills, defineSkill } from './skills.js';
export type {
  AssembledSkills,
  DefineSkillOptions,
  DefinedSkill,
  SdkSkillsOptions,
} from './skills.js';
export { McpHost, attachSdkMcp, attachSdkMcpWithDiscover, createMcpHost } from './mcp.js';
export type { McpHostOptions, McpSessionOption, SdkMcpAttachment } from './mcp.js';
export { runAgent, resolveInitialMessages, DEFAULT_MAX_TOKENS } from './run-agent.js';
export type { RunAgentOptions } from './run-agent.js';
export { query, accumulateUsage } from './query.js';
export type { QueryOptions, ManagedQueryOptions, QueryResult } from './query.js';
export type { QueryLifecycleOptions } from './query-lifecycle.js';
export type {
  SessionDrainOptions,
  SessionDrainResult,
  SessionLifecyclePhase,
  SessionLifecycleFailure,
  SdkCleanupHandle,
} from './lifecycle.js';
export { createSession, AgentSession } from './session.js';
export type { SessionUserBlock, SendBlocksResult } from './sessions/user-blocks.js';
export type { SessionContextState, SessionModelState, SwitchModelOptions, SwitchModelResult, ModelTransitionBackup } from './sessions/context-state.js';
export type { ContextBudgetState } from '@tansr/kernel';
export { AppConfigRequestError, describeApplicationPromptFailure, applicationPromptFailureMessage } from './platform/config-error.js';
export type { ApplicationPromptFailure, ApplicationPromptFailureReason, ApplicationPromptRetry } from './platform/config-error.js';
export type {
  CreateSessionOptions,
  HistoryCommitMeta,
  HistoryRewriteReason,
  SetModelBinding,
  // S-2(doc/116 §4.3):手动压缩与上下文快照的选项/结果联合
  SessionCheckpointOptions,
  CompactOptions,
  CompactResult,
  CheckpointOptions,
  RestoreOptions,
  RestoreResult,
  // W-2(doc/116 §3.7 D-7):cwd 中途切换结果联合
  SetCwdResult,
  // F-2(doc/117 §十):fork 与快照导出/导入
  ForkOptions,
  ForkResult,
  ImportCheckpointOptions,
} from './session.js';
// W-3(doc/117 W-3 / D-S4):通用告警形(createSession({ onWarning }));PlatformWarning 为其平台子集
export type { SdkWarning } from './platform/client.js';
// DEC-RF-29:会话预算闸选项(createSession({ budget });与配置 budget 段同形,五形态同一闸门语义)
export type { SessionBudgetOptions } from './session-budget.js';
// ———— 会话存储(doc/91 拍板③/⑦):SessionStore 接口是唯一承诺面 ————
// kernel journal 原语(writer/reader/lock)经 FileSessionStore 内部消费,
// 恒不经 SDK 出口导出(拍板⑦);开发者自建存储实现 SessionStore 即可注入。
export { createFileSessionStore, LOCAL_END_USER_KEY } from './sessions/file-store.js';
export type { CreateFileSessionStoreOptions } from './sessions/file-store.js';
export type { SessionRecoveryPolicy, SessionRecoveryReport } from '@tansr/kernel';
export { SESSION_RECOVERY_VERSION } from '@tansr/kernel';
// ———— doc/119 / doc/120 IO-22:会话历史分层存储(冷层与 I/O 边界)——kernel 调用契约经 SDK 再出口 ————
// `createFileSessionStore({ dir, cold?, index?, policy?, metrics? })` 的冷层接口面:开发者实现一级 SegmentBlobStore
// (put/get/head/list/delete)或二级 SessionHistoryStore(store/load/listSessions/remove)即可挂载;kernel journal 原语
// 仍不经此出口(拍板⑦不变——这里出口的是**开发者要实现的接口**与测试替身,不是内核读写原语)。
// IO-14 已落地(2026-09-06 v0.11.1 补出口):createFsBlobStore(本地目录冷层参考实现)/ createChaosBlobStore
// (故障注入替身)/ runStorageConformance(第三方 SegmentBlobStore 适配器的一致性卷,examples/store-s3 消费)
// ——开发者经 npm 装 @tansr/sdk 即可,恒不需直装私有 @tansr/kernel。
export {
  StoreError,
  toStoreError,
  isSessionHistoryStore,
  SEGMENTED_STORE_DEFAULTS,
  createMemoryBlobStore,
  createFsBlobStore,
  createChaosBlobStore,
  runStorageConformance,
} from '@tansr/kernel';
export type {
  StoreErrorKind,
  StoreErrorOptions,
  BlobMeta,
  ByteRange,
  PutOptions,
  PutResult,
  BlobListEntry,
  BlobStoreCapabilities,
  SegmentBlobStore,
  StoredSessionMeta,
  // server 份因与 v2 读面同名异形而以 ColdStoredSessionMeta 出口;SDK 两名同出,便于两面对表
  StoredSessionMeta as ColdStoredSessionMeta,
  StoreInput,
  LoadQuery,
  LoadResult,
  SessionHistoryStore,
  SessionIndexStore,
  StreamTransform,
  SegmentedStorePolicy,
  MemoryBlobStoreOptions,
  FsBlobStoreOptions,
  ChaosFaults,
  ChaosStats,
  ChaosBlobStore,
  KeyPredicate,
  StorageConformanceOptions,
  StorageConformanceCase,
  StorageConformanceReport,
  ColdTierEvent,
  ColdTierEventType,
  ColdTierStats,
} from '@tansr/kernel';
export type {
  SessionCwdChange,
  SessionRecord,
  SessionRecordMeta,
  SessionStore,
  SessionStoreCommitMeta,
  SessionStoreCreateInit,
  // F-2:store 可选 fork 面的输入形(自建 store 实现 fork? 时的参数类型)
  SessionStoreForkInput,
} from './sessions/store.js';
// ———— 上下文快照存储(S-1,doc/116 §3.2·§4.3):CheckpointStore 四方法是唯一承诺面 ————
// kernel 快照原语(writeCheckpoint/readCheckpoint/…)经 createFileCheckpointStore 内部
// 消费,恒不经 SDK 出口导出;自建实现四方法即可注入 createSession({ checkpoints: { store } })。
export { createFileCheckpointStore } from './sessions/checkpoint-store.js';
export type {
  Checkpoint,
  CheckpointInput,
  CheckpointMeta,
  CheckpointRetention,
  CheckpointStore,
  CheckpointTrigger,
  CreateFileCheckpointStoreOptions,
} from './sessions/checkpoint-store.js';
// ———— 历史分页(S-3,doc/116 §3.5):活跃会话走 AgentSession.history(),休眠会话走 readHistoryPage ————
export { readHistoryPage } from './sessions/history-page.js';
export type { HistoryPage, HistoryPageOptions } from './sessions/history-page.js';
export { TansrSdkError, SdkLifecycleError } from './errors.js';
export type { SdkErrorCode } from './errors.js';

// ———————————— 渲染数据管道(doc/84 §5.6 三层喷口;γ 泳道 DX 登记①主线接线) ————————————
export {
  createSessionView,
  createNarrator,
  reduceSessionView,
  appendUserMessage,
  initialSessionViewState,
  markSourceFailure,
  setSessionViewDelivery,
  stripThinkingParts,
  viewStateFromHistory,
  OUTPUT_TAIL_MAX_CHARS,
  SESSION_NOTICES_MAX,
} from './view/index.js';
export type {
  SessionView,
  SessionViewSource,
  SessionEventSource,
  SessionViewState,
  SessionViewReducerState,
  SessionViewStatus,
  SessionViewOptions,
  SessionViewDeliveryOptions,
  TextDeliveryMode,
  ThinkingDeliveryMode,
  DeliveryConfig,
  UIMessage,
  UIMessagePart,
  UITextPart,
  UIThinkingPart,
  UIToolCallPart,
  ToolCallView,
  ToolCallPartStatus,
  ActiveToolStatus,
  TodoView,
  UsageView,
  ErrorView,
  SessionNotice,
  Narrator,
  NarratorOptions,
  NarratorVerbosity,
} from './view/index.js';

// ———————————— 高阶装配的可复用件(进阶用法) ————————————
export {
  assembleManagedModel,
  assembleTooling,
  buildClientFromRegistry,
  providerSwitchedBody,
  DEFAULT_MAX_TURNS,
} from './assembly.js';
export type {
  AssembleManagedModelOptions,
  AssembledManagedModel,
  AssembleToolingAdjudication,
  AssembleToolingOptions,
  AssembledTooling,
  PermissionOptions,
} from './assembly.js';
export {
  buildSdkToolSet,
  buildToolGuideSegment,
  resolveBuiltinSelection,
  resolvePlatformSelection,
  toToolDef,
  TOOL_GUIDE_LABEL,
  TOOLS_PLATFORM_DEPRECATED_CODE,
} from './toolset.js';
export type {
  BuildSdkToolSetOptions,
  BuiltinToolName,
  PlatformCapabilityName,
  PlatformToolContext,
  SdkToolSet,
  SdkToolsOptions,
} from './toolset.js';

// ———————————— 平台契约(S-0 冻结,doc/89)与令牌档装配 ————————————
export {
  APP_SDK_CONTRACT_VERSION,
  AppCapabilitiesSchema,
  AppTokenRequestSchema,
  AppTokenResponseSchema,
  CLIENT_FEATURES,
  DEFAULT_APP_CAPABILITIES,
  EndUserIdSchema,
  HEADER_APP_TOKEN,
  HEADER_CLIENT_FEATURES,
} from './platform/contract.js';
export type { AppCapabilities, AppTokenRequest, AppTokenResponse, ClientFeature } from './platform/contract.js';
// doc/125 契约 6(用户分级与定价,PL-22):bundle 顶层 `plan` 投影的 zod 镜份 + 套餐错误码八枚镜份
// (api contract/app-sdk.ts BundlePlanSchema / http/errors.ts 同构;S-0 对表卷钉死)
export {
  AppBundlePlanSchema,
  PLAN_ERROR_CODES,
  PLAN_ERROR_HTTP_STATUS,
  PLAN_STATUSES,
  PLAN_TIERS,
  PlanCapsSchema,
  PlanStatusSchema,
  PlanTierSchema,
  isPlanErrorCode,
  planErrorGuidance,
  planUpgradeUrlOf,
} from './platform/contract.js';
export type { AppBundlePlan, PlanCaps, PlanErrorCode, PlanStatus, PlanTier } from './platform/contract.js';
// 十王修案 M-13 / R-02(FX-C-15/片段):令牌径失效码集 + detail.reason 词表——由 api openapi 顶层
// `x-tansr-error-codes[]` 经 scripts/contract-error-codes.mjs 生成(vendored contract/openapi.json),
// serve `refreshingFetch` / `AppTokenMinter.invalidate` import 本导出,手写零;`pnpm contract:check` 再生零 diff 门
export {
  APP_TOKEN_REJECT_REASONS,
  APP_TOKEN_TRANSIENT_REASONS,
  TOKEN_INVALIDATING_CODES,
  isTokenInvalidatingCode,
  isTransientAppTokenReason,
} from './platform/error-codes.generated.js';
export type { AppTokenRejectReason, TokenInvalidatingCode } from './platform/error-codes.generated.js';
export {
  assemblePlatformModel,
  createAppTokenFetch,
  fetchAppBundle,
  fetchTwpFeatures,
  fetchTwpHeartbeat,
  parseCacheControlMaxAgeMs,
} from './platform/client.js';
// doc/112 契约 2.4:应用平台类型与能力拓扑注册表(api 仓 app-sdk.ts 同构镜份;
// serve 平台内置形按位装配与控制台差分同源消费)
// FX-B-07(十王修案 S2 N-11):能力位 → 工具名表 + hostedToolUniverse()(serve
// `mobile-default` 档唯一上界来源;C 路 `BUILTIN_CAPABILITY_PROFILES['mobile-default']` 改接)
export {
  APP_PLATFORMS,
  APP_PLATFORM_TOPOLOGY,
  AppPlatformSchema,
  CAPABILITY_BIT_TOOL_NAMES,
  CAPABILITY_PLATFORM_KEYS,
  CAPABILITY_TOOL_KEYS,
  CAPABILITY_TOPOLOGIES,
  defaultAppCapabilities,
  hostedToolUniverse,
  inapplicableTrueBits,
  normalizeAppCapabilitiesForPlatform,
  topologyOf,
  topologyToolUniverse,
  // FX-A-15 sdk 半边(V-A-43):capabilities 段整段坏形的回落档(tools 全 false;parseAppBundle 单点使用)
  zeroAppCapabilities,
} from './platform/contract.js';
export type {
  AppPlatform,
  CapabilityPlatformKey,
  CapabilityToolKey,
  CapabilityTopology,
  CapabilityTopologyProfile,
} from './platform/contract.js';
export type {
  AssemblePlatformModelOptions,
  AssembledPlatformModel,
  FetchedAppBundle,
  PlatformWarning,
  TwpHeartbeatResult,
} from './platform/client.js';
// SC-24 per-app bundle/registry 缓存(serve 平台内置形缺省接一枚;SDK 多会话同 app 可自接);
// SC-34b heartbeat bundleEtag 失效订阅(条目级 invalidate;normalizeEtagValue 比对归一)
// FX-C-16(十王修案 S-08):键四维 (base, appId, scope, featuresFingerprint);bundleKey / featuresFingerprintOf 导出供卷断言键形
export {
  DEFAULT_BUNDLE_CACHE_SCOPE,
  bundleKey,
  createBundleCache,
  DEFAULT_BUNDLE_CACHE_TTL_MS,
  featuresFingerprintOf,
  normalizeEtagValue,
} from './platform/bundle-cache.js';
export type {
  BundleCache,
  BundleCacheKey,
  BundleCacheResolveInput,
  BundleCacheScope,
  BundleCacheStats,
  CreateBundleCacheOptions,
  ResolvedBundle,
} from './platform/bundle-cache.js';
// S-E1 系统媒体工具平台提供方薄封装(imageGen;doc/128 S-1 起委托 kernel ImageGen 骨架——签名不变,
// schema / 常量 / Args 自 kernel、ImageGenData 自 protocol 重导;经 tools.builtin(或 tools.platform 别名)装配,
// 工厂导出供注入档/测试直用)
export { createImageGenTool, ImageGenArgsSchema, IMAGEGEN_TOOL_MAX_N, IMAGEGEN_TOOL_TIMEOUT_MS } from './platform/imagegen-tool.js';
export type { CreateImageGenToolOptions, ImageGenArgs, ImageGenData } from './platform/imagegen-tool.js';
// S-F 系统媒体工具平台提供方薄封装(videoGen;imageGen 同构镜像)
export { createVideoGenTool, VideoGenArgsSchema, VIDEOGEN_TOOL_MAX_DURATION, VIDEOGEN_TOOL_TIMEOUT_MS } from './platform/videogen-tool.js';
export type { CreateVideoGenToolOptions, VideoGenArgs, VideoGenData } from './platform/videogen-tool.js';
// 音频系统工具的平台提供方薄封装:SpeechToText(/t1/asr)/ TextToSpeech(/t1/tts)
export {
  createSpeechToTextTool,
  parseTranscriptData,
  SpeechToTextArgsSchema,
  SPEECH_TO_TEXT_TOOL_TIMEOUT_MS,
} from './platform/speech-to-text-tool.js';
export type {
  CreateSpeechToTextToolOptions,
  SpeechToTextArgs,
  TranscriptData,
  TranscriptSegment,
} from './platform/speech-to-text-tool.js';
export {
  createTextToSpeechTool,
  parseSpeechData,
  TextToSpeechArgsSchema,
  TEXT_TO_SPEECH_DEFAULT_MAX_CHARS,
  TEXT_TO_SPEECH_FORMATS,
  TEXT_TO_SPEECH_TOOL_TIMEOUT_MS,
} from './platform/text-to-speech-tool.js';
export type {
  CreateTextToSpeechToolOptions,
  SpeechAudio,
  SpeechData,
  TextToSpeechArgs,
} from './platform/text-to-speech-tool.js';
// doc/128 S-1 媒体能力内核化:四工具骨架收编 kernel 环2(tools/media),SDK 供四平台提供方(kernel
// 提供方缝的平台实现,name 'tansr';serve 平台会话工厂 / 注入档直构 kernel 工具时按面注入)
export { PLATFORM_MEDIA_PROVIDER_NAME } from './platform/media-provider.js';
export type { PlatformMediaProviderOptions } from './platform/media-provider.js';
// FX-B-26(十王修案 S8 M-02 / M-08 / M-07 残留):平台媒体错误 hint 单源表 + 受众档(app / user);词表外 undefined → kernel 通用指引
export { hintFor } from './platform/media-hints.js';
export type { MediaHintAudience, MediaHintFace } from './platform/media-hints.js';
export { createPlatformImageGenProvider } from './platform/imagegen-provider.js';
export type { CreatePlatformImageGenProviderOptions } from './platform/imagegen-provider.js';
export { createPlatformVideoGenProvider } from './platform/videogen-provider.js';
export type { CreatePlatformVideoGenProviderOptions } from './platform/videogen-provider.js';
export { createPlatformSpeechToTextProvider } from './platform/speech-to-text-provider.js';
export type { CreatePlatformSpeechToTextProviderOptions } from './platform/speech-to-text-provider.js';
export { createPlatformTextToSpeechProvider } from './platform/text-to-speech-provider.js';
export type { CreatePlatformTextToSpeechProviderOptions } from './platform/text-to-speech-provider.js';
// doc/123 D-A9 ②:令牌档平台直连方法(session.platform.transcribe / speak;serve /v2 代打径复用 requestPlatformAudio)
export {
  createPlatformAudioClient,
  createUnavailablePlatformAudioClient,
  platformErrorOf,
  PlatformRequestError,
  requestPlatformAudio,
} from './platform/audio-client.js';
export type {
  CreatePlatformAudioClientOptions,
  PlatformAudioCallOptions,
  PlatformAudioClient,
  PlatformAudioFace,
  PlatformAudioResponse,
  RequestPlatformAudioOptions,
  SpeakInput,
  TranscribeInput,
} from './platform/audio-client.js';
// S-WS2 平台联网搜索后端(用户拍板 2026-08-31:SDK 恒不 BYO,环2 kernel
// 'WebSearch' 工具直接骑平台通道;取代 S-WS 环3 独立工具 'PlatformWebSearch')
export { createPlatformSearchProvider, PLATFORM_SEARCH_PROVIDER_NAME } from './platform/websearch-provider.js';
export type { CreatePlatformSearchProviderOptions } from './platform/websearch-provider.js';
export {
  APP_TOKEN_PLACEHOLDER_ENV,
  APP_TOKEN_PLACEHOLDER_VALUE,
  TANSR_PROVIDER_ID,
  appBundlePricingUpdates,
  appBundleRegistryConfig,
  parseAppBundle,
} from './platform/bundle.js';
export type {
  AppBundle,
  // FX-B-30(R-18 N-10 α):bundle contextScheme 形状级透传型(真消费 = kernel contextSchemeToSchemeOptions)
  AppBundleContextScheme,
  // M-05(FX-A-17/片段):bundle governance.sessionRetentionDays 形状级透传型(真消费 = serve resolveSessionRetention)
  AppBundleGovernance,
  AppBundleImageModel,
  AppBundleMediaModel,
  AppBundleModel,
  AppBundleModelPricing,
  AppBundlePlatformModels,
  AppBundleVideoModel,
  ImageModelConstraints,
  VideoModelConstraints,
  // doc/123 音频两面(2026-09-05):bundle platformModels.speechToText / textToSpeech 行形与约束
  AppBundleSpeechToTextModel,
  AppBundleTextToSpeechModel,
  AudioAsrConstraints,
  AudioTtsConstraints,
} from './platform/bundle.js';
export { createSdkPermissionGate } from './permission.js';
export type { CreateSdkPermissionGateOptions } from './permission.js';
export { attachSdkTaskTool, subagentModelResolverOf } from './task.js';
export type { AttachSdkTaskToolConfig, SdkToolsetRef } from './task.js';
// S-2/S-3(doc/117 S 泳道冻结形,供 serve v2 与其它宿主去重消费):空闲直压公共件
// (与 cli tui/session-driver #runIdleCompact 同律)+ 历史分页边界对齐/全量切片
export { runIdleCompaction } from './sessions/compaction-idle.js';
export type { IdleCompactionInput, IdleCompactionResult } from './sessions/compaction-idle.js';
export { alignHistoryPage } from './sessions/pairing.js';
export type { AlignedHistoryPage } from './sessions/pairing.js';
export { sliceHistoryPage } from './sessions/history-page.js';

// ———————————— kernel 注入缝的引用面(类型 + 最小运行时件) ————————————
// PromptChannel 实现件与测试用顺序执行器:SDK 使用方实现交互/自定义工具
// 时的开箱件;其余 kernel 能力经 @tansr/kernel 自取(内部包)。
export { QueueChannel, UnavailableChannel, SequentialToolExecutor } from '@tansr/kernel';
// F-2:快照导出体格式判别('tansr-checkpoint/1';exportCheckpoint 产物 / importCheckpoint 输入的 format 值)
export { CHECKPOINT_EXPORT_FORMAT } from '@tansr/kernel';
export type {
  // 工具注入缝(kernel Tool 形态)
  Tool,
  ToolContext,
  ToolResult,
  ToolResultContent,
  SequentialToolHandler,
  SequentialToolResult,
  // 交互通道(AskUser 工具)
  PromptChannel,
  Question,
  QuestionOption,
  Answer,
  // 权限注入缝
  AskUserCallback,
  PermissionMode,
  PermissionRules,
  GateDecision,
  PermissionGate,
  // 查询环句柄与执行器契约(runAgent 低阶面)
  QueryHandle,
  QuerySettleOptions,
  QuerySettlement,
  QuerySettlementFailure,
  QueryCounters,
  QueryCompactionOptions,
  CompactNowOptions,
  CompactNowResult,
  // S-2/S-3:compaction.beforeCompact 钩子形 / compact() failed.reason 词表 / 封段选项
  QueryBeforeCompactContext,
  QueryBeforeCompactHook,
  CompactionFailureReason,
  SegmentationOptions,
  ToolExecutor,
  ToolExecutionContext,
  ToolExecutionOutcome,
  ToolExecutorYield,
  ProposedToolCall,
  IRToolResultBlock,
  KernelState,
  // 托管装配的配置面
  LoadedConfig,
  LoadConfigOptions,
  ConfigDiagnostic,
  // MCP 外接的配置与观测面(S-B10;servers 条目形态直采 kernel 契约)
  McpServerConfig,
  McpConnectFactory,
  McpConnection,
  McpEventObserver,
  McpClientEvent,
  // skills 注入缝(S-B9;hermetic 测试的 fs 假件形态)
  SkillsFileSystem,
} from '@tansr/kernel';

// ———————————— protocol 类型纯导出(零运行时依赖增量) ————————————
// 任务卡硬约束:纯 `export type` re-export,不改 @tansr/protocol、不引入
// 运行时绑定;schema(zod)等运行时值不在 SDK 面重导,消费方按需自取。
export type {
  // IR 消息模型
  IRBlock,
  IRToolContent,
  IRRole,
  IRMessage,
  IRSystemSegment,
  IRToolDef,
  ResolvedModel,
  IRRequest,
  IRUsage,
  IRErrorKind,
  IRStreamEvent,
  // 事件协议
  EventEnvelope,
  EventBody,
  KernelEvent,
  TerminalReason,
  ToolErrorType,
  PermissionDecision,
  DecisionSource,
  HookOutcomeStatus,
  // 模型客户端契约
  ModelClient,
  ModelCallOptions,
  // 配置与能力位
  ProtocolKind,
  PromptCachingMode,
  Capability,
  Pricing,
  ProviderProfile,
  TansrConfig,
  // journal(会话持久化记录形态)
  Visibility,
  Integrity,
  JournalKind,
  JournalRecord,
} from '@tansr/protocol';

// ———————————— 媒体产物形单源 + 三端判别参考实现(doc/128 D-M5 / D-M10,S-2) ————————————
// protocol media.ts 是四媒体工具 ToolResult.data 形的单源(sdk *Data 型为其别名);四 schema
// (zod .passthrough())与 parseMediaArtifact(doc/123 §3.7 端侧形状规则的 TS 参考实现,判序
// speech → video → image → transcript,errorCode 在场恒 null)作为运行时值重导——集成方渲染面
// 与 Android/iOS ToolCardArtifact 对表的可执行基准(A-12);protocol 随 SDK 构建打包,零运行时依赖增量。
export {
  ImageGenDataSchema,
  VideoGenDataSchema,
  TranscriptDataSchema,
  TranscriptSegmentSchema,
  SpeechAudioSchema,
  SpeechDataSchema,
  parseMediaArtifact,
  // 十王修案 FX-C-24:三端同名「产物可渲染」谓词(四金样三端逐字对表;parseMediaArtifact 前置门)
  isRenderableArtifact,
} from '@tansr/protocol';
export type {
  MediaArtifact,
  MediaModelDescriptor,
  MediaOutputHosting,
  ImageModelDescriptor,
  VideoModelDescriptor,
  SpeechToTextModelDescriptor,
  TextToSpeechModelDescriptor,
} from '@tansr/protocol';

// ———————————— 裁决人档:两阶段 block-list 分类器核心(G-4 下沉,doc/113 §4.3) ————————————
// 单源两入口:cli assembly/permission-classifier.ts 转出口 + SDK 令牌档(G-5)/
// serve /v2(G-6)装配同一实现;stage 句柄与资格解析闭包注入,治理旋钮归宿主。
export {
  createBlockListClassifier,
  createClassifierTranscriptRecorder,
  createSingleStageClassifier,
  createTwoStageClassifier,
  CLASSIFIER_PROTOCOL_FINGERPRINT,
  CLASSIFIER_TIMEOUT_POLICY,
  SINGLE_STAGE_PROTOCOL_FINGERPRINT,
  TWO_STAGE_TIMEOUT_MS,
  classifierProtocolFingerprintFor,
  parseStage1Output,
  parseStage2Output,
  renderClassifierStagePrompt,
  serializeTimeoutPolicy,
  singleStageProtocolFingerprintFor,
  // 应用表面协议 v1(doc/optimize/37):独立指纹与指令;协议 v4 事实段渲染
  APP_SINGLE_STAGE_INSTRUCTION,
  APP_SINGLE_STAGE_PROTOCOL_FINGERPRINT,
  appSingleStageProtocolFingerprintFor,
  renderAppContext,
  renderCallPayloadDetailed,
  renderEngineFacts,
} from './adjudication/index.js';
export type {
  BlockListClassifierConfig,
  ClassifierAssemblyMode,
  ClassifierLiveStages,
  ClassifierProtocol,
  ClassifierStageBudgetOverrides,
  ClassifierStageHandle,
  ClassifierTimeoutPolicy,
  ClassifierTranscriptRecorder,
  CreateBlockListClassifierOptions,
  RenderPayloadOptions,
} from './adjudication/index.js';
// G-5(doc/113 §4.4):令牌档裁决人装配——bundle 任命 × 姿态(人在环/无人环)→
// PermissionEngine({ mode:'auto', classifier });createSession/query 的 `adjudication`
// 选项形态;serve /v2 平台内置形(G-6)复用 assembleBundleAdjudicator 同一装配件。
// FX-B-08(十王修案 S2 J2-01(b) / J2-06):bundle 治理位真消费——resolveAdjudicatorGovernance
// 单点(escalation.mode:'reject' → headless;callBudget = min(bundle 键 / 金额同币折算 / 宿主))。
// FX-B-11(S3 J-06 / J2-05):serve 裁决人选项收口的接口常量——段缺席通报形(sdk / serve 同码同文案)与
// 开发者向码集(只走宿主通道,恒不下发终端设备);serve 侧 import 之即零本地字面。
export {
  ADJUDICATION_CONSULT_TOKEN_ESTIMATE,
  ADJUDICATION_POSTURES,
  ADJUDICATOR_UNAVAILABLE_WARNING,
  DEVELOPER_ONLY_ADJUDICATOR_WARNING_CODES,
  assembleBundleAdjudicator,
  assembleTokenTierAdjudicator,
  prepareAdjudication,
  resolveAdjudicationPosture,
  resolveAdjudicatorGovernance,
  resolveStageHandle,
  validateAdjudicationOptions,
} from './adjudication/index.js';
export type {
  AdjudicationOptions,
  AdjudicationPosture,
  AdjudicationWarning,
  AdjudicatorGovernance,
  AdjudicatorGovernanceInput,
  AdjudicatorMoneyFold,
  AssembleBundleAdjudicatorOptions,
  AssembledBundleAdjudicator,
  PreparedAdjudication,
} from './adjudication/index.js';
export { ADJUDICATOR_TIERS, AdjudicatorTierSchema } from './platform/contract.js';
export type { AdjudicatorTier } from './platform/contract.js';
export type { AppBundleAdjudicator } from './platform/bundle.js';

// AP-SP：业务提示词组合与只读来源，低阶宿主可显式复用相同规则。
export { resolveApplicationSystem } from './platform/system-prompt.js';
export type { SystemPromptPolicy, ApplicationPromptInfo } from './platform/system-prompt.js';

// SDK／serve 共用可取消的轮首预检与提示词条件核验。
export { prepareApplicationPromptTurn } from './platform/application-prompt-turn.js';
export { createApplicationPromptRefresher } from './platform/system-prompt-refresh.js';
export type { ApplicationPromptSnapshot } from './platform/system-prompt-refresh.js';
export type { ApplicationPromptTurnOptions } from './platform/application-prompt-turn.js';
export type {
  SessionInputTarget, SessionInputContent, SessionInputSubmission, SessionInputReceipt,
  SessionInputRejectCode, SessionInputResult, SessionInputCapabilities,
} from '@tansr/kernel';
export { countSpeechCharacters, planSpeechInput } from '@tansr/kernel';
export type { SpeechInputPlan, SpeechInputPlanResult } from '@tansr/kernel';
