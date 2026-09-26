import type { AgentSession, CreateSessionOptions, SessionStore } from '../index.js';

declare const cacheContinuityAuthority: unique symbol;
/** 受信 Node 宿主能力；普通终端不能自行构造，不含发行者私钥。 */
export interface NodeCacheContinuityAuthority { readonly [cacheContinuityAuthority]: true }
/** 正式受信后端的短期凭据桥；不能为任意 Electron 本地历史提供盲签服务。 */
export interface NodeCacheContinuityBridge {
  readonly issuerId: string; readonly sourceNamespace: string;
  readonly runtimePolicy?: 'preserve' | 'renew-on-restore';
  /** 必须由持有此原 Store 来源权威的后端签发；字节已耐久，凭据过期只刷新同一意图。 */
  readonly credential: (input: { readonly sessionId: string; readonly intentBytes: Uint8Array; readonly signal?: AbortSignal;
    readonly assertCurrent: () => void }) => Promise<string>;
}
/** 显式缓存宿主；本地加密 key 仅保护缓存介质，不是平台发行者签名密钥。 */
export interface NodeCacheHostOptions {
  /** 此显式扩展要求原 createFileSessionStore 的队列/锁和删除保护；普通 SDK 五方法自建 Store 仍沿 createSession 使用。 */
  readonly store: SessionStore;
  readonly applicationScopeId: string;
  readonly endUserId: string;
  readonly baseUrl: string;
  readonly cache: {
    readonly directory: string; readonly mode: 'create' | 'recover'; readonly storeId: string;
    readonly key: { readonly id: string; readonly bytes: Uint8Array };
    readonly maxRuntimes?: number;
  };
  /** 每次操作同步核当前权限；抛错拒绝，授权变化使正在等待的见证失效。 */
  readonly authorize: (input: { readonly sessionId: string; readonly operation: 'open' | 'resume' | 'cold-new' | 'run' | 'delete' }) => { readonly authorizationRevision: string };
  /** 仅受信宿主；正式终端跨运行仍须对接受信后端，不能内嵌 issuer key。 */
  readonly continuity?: NodeCacheContinuityAuthority | NodeCacheContinuityBridge;
}
/** 缓存开启只适用于原令牌档；原工具、权限、计费和模型配置保持。 */
export type NodeCacheSessionOptions = Omit<CreateSessionOptions, 'store' | 'sessionId' | 'resume'>;
/** 新建或恢复原缓存会话；恢复路径沿原 Store 与平台控制意图继续。 */
export interface NodeCacheOpenRequest {
  readonly mode: 'fresh' | 'resume'; readonly sessionId: string; readonly session: NodeCacheSessionOptions;
  // fresh 可沿原 SDK fork/initialMessages 初始化；源材料不继承缓存关系、执行身份或 Core 品牌。
  /** 410 是归档终态；只有显式冷新建授权才建立独立新关系，绝不重发旧 unknown。 */
  readonly archived?: 'reject' | 'cold-new';
}
/** 冷删除或原未决删除续办；只提供当前 app_user 平台认证，不创建模型会话。 */
export interface NodeCacheDeleteRequest {
  readonly sessionId: string;
  readonly authorization: Required<Pick<CreateSessionOptions, 'token' | 'baseUrl'>> & Pick<CreateSessionOptions, 'fetchImpl'>;
}
/** 同一应用/用户的冷维护查询；不创建模型会话或迁移未知请求。 */
export interface NodeCacheLookupRequest extends NodeCacheDeleteRequest { readonly requestId: string; readonly signal?: AbortSignal }
/** 原请求受理状态及尚被保留的响应；查询本身不清除未决请求或授权再次执行。 */
export interface NodeCacheLookupResult {
  readonly protocol: 'sdk2-cache-core-v1';
  readonly state: 'not-found' | 'accepted' | 'result-unknown' | 'completed' | 'receipt-expired';
  readonly result: { readonly status: number; readonly contentType: string; readonly body: string;
    readonly headers?: { readonly cost?: string; readonly balance?: string; readonly priceTable?: string } } | null;
}
/**
 * 受信缓存宿主的只读连续性观察。
 *
 * `logicalGroup.id` 只在同一宿主缓存密钥域内稳定，用于跨 runtime 对齐逻辑会话；
 * 它不是供应商 cache key，也不授予读取历史、执行工具或恢复旧租约的权限。
 * provider 三项证据在当前 SDK2 cache-host 合同中没有上游实报，必须保持 unknown。
 */
export interface NodeCacheContinuityView {
  readonly format: 'sdk2-cache-continuity-view-v1';
  readonly sessionId: string;
  readonly state: 'absent' | 'ready' | 'pending' | 'deleting' | 'deleted';
  readonly continuity: 'disabled' | 'unknown' | 'enrolled' | 'deleted';
  readonly logicalGroup: {
    readonly id: string;
    readonly lineageCount: number;
    /** 当前宿主内的逻辑血缘代数；不等同供应商 groupGeneration。 */
    readonly lineageGeneration: number;
  } | null;
  /** 最近接纳的本地网关投影，仅含版本/状态/原因；未加载、非 ready 或控制未决为 null，不代表实时上游状态。 */
  readonly projection?: {
    readonly revision: string;
    readonly status: 'current' | 'rebuild-required' | 'missing';
    readonly reason: 'unchanged' | 'policy-changed' | 'generation-changed' | 'provider-changed' | 'source-missing' | 'legacy-missing' | 'key-rotated';
  } | null;
  readonly provider: {
    readonly hit: 'unknown';
    readonly usage: 'unknown';
    readonly billing: 'unknown';
    readonly basis: 'provider-evidence-unavailable';
  };
}
/** 受信 Node 缓存宿主；显式管理原 Store、加密介质与 continuity 生命周期。 */
export interface NodeCacheHost {
  /** 在原历史确认、原控制意图与 Store 运行号对账后交付同一 AgentSession。 */
  open(input: NodeCacheOpenRequest): Promise<AgentSession>;
  /**
   * 读取当前宿主掌握的逻辑缓存连续性状态；纯本地、无出网、不会读取正文。
   * provider 命中、usage 和 billing 没有上游实报时恒为 unknown。
   */
  readCacheContinuity(sessionId: string): NodeCacheContinuityView;
  /** 查原Core受理/已存响应；包括completed在内均不清pending，也不把原结果作为新用户轮执行。 */
  lookup(input: NodeCacheLookupRequest): Promise<NodeCacheLookupResult>;
  /** 所有宿主会话已 close/drain 后，经原 Store 排他锁确认远端墓碑再删史；未知保留原键供重试。 */
  delete(input: NodeCacheDeleteRequest): Promise<void>;
  /** 仅全部会话已关闭并实际排空后关闭本地介质，保留未决请求与永久身份。 */
  dispose(): Promise<void>;
}
