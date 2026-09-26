/** 私有 L1 接入面：可选宿主耐久屏障；serve 两跳关系由独立宿主协调。 */
import { createHash, randomUUID } from 'node:crypto';
import { z } from 'zod';
import { assertCacheCoreRequest, type CacheCoreRequest, type CacheCoreAuthority } from '../internal/cache-core.js';
import { CACHE_CORE_PATH, CACHE_CORE_HEADER, CACHE_CORE_PROTOCOL, CacheCoreCapabilitiesSchema, CacheCoreDiagnosticsSchema, CacheCoreLookupSchema,
  cacheCoreIntentBytes, decodeCacheCoreIntent, type CacheCoreIntent, type CacheCoreDiagnostics, type CacheCoreLookup } from './cache-core-wire.js';
import { decodeCacheControl, decodeCacheLookupControl, encodeCacheControl } from './cache-control-json.js';
import { registerContinuityTarget, type ContinuityAdoption } from './continuity-target.js';
import { registerGatewayCacheRetirement, type GatewayCacheRetirement, type GatewayCacheTerminalProof } from './cache-retirement.js';
import type { CacheRecordChange, CacheStoredRecord, GatewayCachePersistence } from './cache-persistence.js';
import { CACHE_EXCHANGE_CONTENT_TYPE, CACHE_FEATURE, CACHE_PROTOCOL, CacheBindingSchema, CacheCapabilitiesSchema,
  CacheErrorSchema, CacheMutationResponseSchema, CacheOpenIntentSchema, CacheOpenResponseSchema, CacheRequestIdentitySchema,
  cacheId, cacheRevision, cacheRuntimeId, cacheTicket, encodeGatewayCacheExchange,
  type GatewayCacheBinding, type GatewayCacheCapabilities, type GatewayCacheMutation, type GatewayCacheMutationResponse,
  type GatewayCacheOpenIntent, type GatewayCacheOpenResponse, type GatewayCacheRequestIdentity,
} from './cache-wire.js';

export class GatewayCacheError extends Error {
  readonly fallback = 'none';
  constructor(readonly code: string, readonly requestId?: string, readonly status?: number, readonly retryAction = 'none') {
    super(`SDK2 cache: ${/^[a-z][a-z0-9_]{0,63}$/.test(code) ? code : 'invalid_response'}`);
    this.name = 'GatewayCacheError';
  }
}
export interface GatewayCacheClientOptions {
  /** 明确的 /t1 基址；不从平台配置猜测另一网关。 */
  baseUrl: string;
  token: () => string | Promise<string>;
  runtimeSessionId: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
  /** 仅可信宿主显式装配；发送前提交原意图，默认缺席零新增持久 IO。 */
  persistence?: GatewayCachePersistence;
  /** @internal 仅可信宿主指定；普通终端不能提供接续归属。 */
  continuityScope?: { readonly applicationScopeId: string; readonly endUserId: string };
}
export interface GatewayCachePreparedControl<T> {
  readonly operation: 'open' | GatewayCacheMutation;
  readonly request: Readonly<GatewayCacheRequestIdentity>;
  /** 返回副本；缺省仅内存，显式 persistence 时由客户端先提交原字节再发送。 */
  bytes(): Uint8Array;
  execute(signal?: AbortSignal): Promise<T>;
  query(signal?: AbortSignal): Promise<T>;
}
export interface GatewayCachePreparedExchange {
  readonly requestId: string;
  send(signal: AbortSignal, streaming: boolean): Promise<Response>;
}
/**
 * 本地缓存绑定的非敏感观察视图。
 *
 * 只返回绑定状态和前缀投影版本，不返回 bindingId、logicalRef、ticket 或任何
 * 可用于跨用户关联的标识。该视图来自本地已接纳元数据，不会触发 HTTP 请求，
 * 也不把网关投影误报成供应商命中。
 */
export interface GatewayCacheLocalBindingView {
  readonly revision: string;
  readonly state: GatewayCacheBinding['state'];
  readonly mode: GatewayCacheBinding['mode'];
  readonly groupGeneration: string;
  readonly projection: Readonly<Pick<NonNullable<GatewayCacheBinding['projection']>, 'revision' | 'status' | 'reason'>> | null;
  readonly expiresAt: string;
  readonly availability: GatewayCacheBinding['availability'];
}
export interface GatewayCacheTransport {
  readonly runtimeSessionId: string;
  readonly coreProjection?: true;
  originalBody(requestId: string): string | undefined;
  prepare(requestId: string, originalBody: string, coreRequest?: CacheCoreRequest): GatewayCachePreparedExchange;
  finish(requestId: string): void;
  uncertain(requestId: string): void;
}
type ControlResponse = GatewayCacheOpenResponse | GatewayCacheMutationResponse;
type Pending = { operation: 'open' | GatewayCacheMutation; identity: GatewayCacheRequestIdentity; body: Uint8Array; path: string;
  bindingId: string | null; handle: GatewayCachePreparedControl<ControlResponse>; sending: boolean; started: boolean };
type Exchange = { body: string; frame: Buffer; uncertain: boolean; active: boolean; core?: CacheCoreIntent };
type LocalBinding = { view: GatewayCacheBinding; ticket: string; ticketExpiresAt: string };
type SavedControl = { operation: Pending['operation']; body: Uint8Array; result: ControlResponse | null };
/** 完成后只保留拒重索引；完整意图和回执仍在原耐久记录，重放恒查当前鉴权的原回执。 */
type CompletedControl = { operation: Pending['operation']; request: GatewayCacheRequestIdentity; bindingId: string | null; completed: true };
type ResidentControl = SavedControl | CompletedControl;
const controlKey = (operation: string, body: Uint8Array): string => `control/${createHash('sha256').update(operation).update('\0').update(body).digest('hex')}`;
function completedControl(control: SavedControl): CompletedControl {
  const original = decodeCacheControl(control.body) as { request: unknown; bindingId?: string };
  return { operation: control.operation, request: CacheRequestIdentitySchema.parse(original.request),
    bindingId: control.operation === 'open' ? null : cacheId(original.bindingId ?? ''), completed: true };
}
const adoptionSchema = z.object({ applicationScopeId: z.string(), endUserId: z.string(), sourceNamespace: z.string(),
  intentDigest: z.string().regex(/^[a-f0-9]{64}$/), receiptDigest: z.string().regex(/^[a-f0-9]{64}$/), bindingId: z.string() }).strict();
type LocalAdoption = z.infer<typeof adoptionSchema>;
const terminalProofSchema = z.object({ bindingId: z.string().refine(value => { try { return cacheId(value) === value; } catch { return false; } }),
  kind: z.enum(['close', 'continuity-resume', 'continuity-delete']), digest: z.string().regex(/^[a-f0-9]{64}$/) }).strict();
const retirementSchema = z.object({ format: z.literal('sdk2-cache-client-retired-v1'), baseUrl: z.string(), runtimeSessionId: z.string(),
  proof: terminalProofSchema, compactedControls: z.number().int().min(0).max(128), compactedExchanges: z.number().int().min(0).max(4096),
  continuityScope: z.object({ applicationScopeId: z.string(), endUserId: z.string() }).strict().optional() }).strict();
const originalId = (id: string): boolean => /^[A-Za-z0-9_-]{8,40}$/.test(id);
const emptyDiagnostic = z.object({ protocol: z.literal(CACHE_PROTOCOL), rows: z.array(z.never()).max(0), next: z.null() }).strict();

/** 原整段请求时限；读取正文结束即释放计时器，不把已完成控制请求留到30秒后。 */
function controlDeadline(timeoutMs: number, signal?: AbortSignal) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(new DOMException('The operation was aborted due to timeout', 'TimeoutError')), timeoutMs);
  timer.unref();
  return { signal: signal ? AbortSignal.any([signal, controller.signal]) : controller.signal, close: () => clearTimeout(timer) };
}

async function readControl(response: Response, maxBytes = 65536): Promise<unknown> {
  const type = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase();
  if (type !== 'application/json' || response.body === null) { await response.body?.cancel(); throw new GatewayCacheError('invalid_response'); }
  const reader = response.body.getReader();
  const parts: Uint8Array[] = []; let size = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read(); if (done) break;
      size += value.byteLength;
      if (size > maxBytes) { await reader.cancel(); throw new GatewayCacheError('payload_too_large'); }
      parts.push(value);
    }
    return maxBytes === 1048576 ? decodeCacheLookupControl(Buffer.concat(parts)) : decodeCacheControl(Buffer.concat(parts));
  } finally { reader.releaseLock(); }
}

/** 新旧错误信封都终止当前调用；绝不继承旧 409 烧键、503 自动重发或冷回退。 */
export async function gatewayCacheResponseError(response: Response, requestId?: string): Promise<GatewayCacheError> {
  try {
    const raw = await readControl(response);
    const result = CacheErrorSchema.safeParse(raw);
    if (result.success && result.data.status === response.status) return new GatewayCacheError(result.data.code, requestId, response.status, result.data.retryAction);
    const legacy = z.object({ error: z.object({ code: z.string().regex(/^[a-z][a-z0-9_]{0,63}$/) }).passthrough() }).passthrough().safeParse(raw);
    if (legacy.success) return new GatewayCacheError(legacy.data.error.code, requestId, response.status);
  } catch { /* 网络／不合法响应不提供未受理证明。 */ }
  return new GatewayCacheError('result_unknown', requestId, response.status, 'query-status');
}

export class GatewayCacheClient {
  readonly #options: GatewayCacheClientOptions;
  readonly #baseUrl: string;
  readonly #fetch: typeof fetch;
  readonly runtimeSessionId: string;
  #capabilities: { value: GatewayCacheCapabilities; expires: number } | undefined;
  #heartbeat = false;
  #pending: Pending | undefined;
  #binding: LocalBinding | undefined;
  #opened: GatewayCacheOpenResponse | undefined;
  #openedIntent: string | undefined;
  readonly #exchanges = new Map<string, Exchange>();
  readonly #completed = new Set<string>();
  readonly #transport: GatewayCacheTransport;
  #storageUnknown = false;
  #adoption: LocalAdoption | undefined;
  #restoring = false;
  readonly #controls = new Map<string, ResidentControl>();
  #retirement: GatewayCacheRetirement | undefined;
  #terminalProof: GatewayCacheTerminalProof | undefined;
  #coreInventory = false;
  #highWaterPendingControlBytes = 0;
  #highWaterPendingExchangeBytes = 0;

  constructor(options: GatewayCacheClientOptions) {
    const url = new URL(options.baseUrl);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash || !/\/t1\/?$/.test(url.pathname)) throw new GatewayCacheError('invalid_request');
    if (options.timeoutMs !== undefined && (!Number.isSafeInteger(options.timeoutMs) || options.timeoutMs < 1 || options.timeoutMs > 300000)) throw new GatewayCacheError('invalid_request');
    this.#options = { ...options, ...(options.continuityScope ? { continuityScope: Object.freeze({ applicationScopeId: cacheId(options.continuityScope.applicationScopeId), endUserId: cacheRuntimeId(options.continuityScope.endUserId) }) } : {}) }; this.#baseUrl = options.baseUrl.replace(/\/+$/, ''); this.#fetch = options.fetchImpl ?? fetch;
    this.runtimeSessionId = cacheRuntimeId(options.runtimeSessionId);
    this.#transport = Object.freeze({ runtimeSessionId: this.runtimeSessionId,
      originalBody: (requestId: string) => { this.#storageReady(); return this.#exchanges.get(requestId)?.body; },
      prepare: (requestId: string, body: string) => this.#prepareExchange(requestId, body),
      finish: (requestId: string) => {
        this.#storageReady();
        const saved = this.#exchanges.get(requestId);
        if (!saved?.active) throw new GatewayCacheError('result_unknown', requestId, undefined, 'query-status');
        this.#commit({ put: [{ key: `completed/${requestId}`, value: new Uint8Array() }], delete: [`exchange/${requestId}`, `flight/${requestId}`, ...(saved.core ? [`core-exchange/${requestId}`] : [])] });
        this.#exchanges.delete(requestId); this.#completed.add(requestId);
      },
      uncertain: (requestId: string) => {
        this.#storageReady();
        const saved = this.#exchanges.get(requestId);
        // The adapter calls uncertain from its outer finally block even when
        // prepare/send failed before the provider request was handed to fetch.
        // Only an active exchange can have an unknown provider-side outcome;
        // keeping a deterministic preflight failure replayable preserves the
        // capability, size and token gates for the next attempt.
        if (saved?.active) { saved.uncertain = true; saved.active = false; }
      },
    });
    if (options.persistence) {
      const records = options.persistence.load({ baseUrl: this.#baseUrl, runtimeSessionId: this.runtimeSessionId });
      if (records.length === 0) this.#commit({ put: [{ key: 'meta', value: this.#metadata() }], delete: [] });
      else this.#restore(records);
    }
    this.#highWaterPendingExchangeBytes = this.#exchangeBytes();
    if (this.#options.continuityScope && options.persistence) registerContinuityTarget(this, {
      adopt: value => this.#adoptContinuity(value), heartbeat: signal => this.#registerRuntime(signal),
      retire: value => this.#retireContinuity(value),
    });
    registerGatewayCacheRetirement(this, {
      status: () => {
        this.#storageKnown();
        return this.#retirement ? structuredClone(this.#retirement) : undefined;
      },
      retireClosed: () => {
        this.#storageKnown();
        if (!this.#options.persistence) throw new GatewayCacheError('mapping_unavailable');
        const proof = this.#retirement?.proof ?? this.#terminalProof;
        if (!proof || proof.kind !== 'close') throw new GatewayCacheError('mapping_unavailable');
        return this.#retire(proof);
      },
    });
  }

  #storageKnown(): void { if (this.#storageUnknown) throw new GatewayCacheError('storage_unknown', undefined, undefined, 'query-status'); }
  #storageReady(): void {
    this.#storageKnown();
    if (this.#retirement) throw new GatewayCacheError('runtime_retired');
  }
  #commit(change: CacheRecordChange): void {
    this.#storageReady();
    if (!this.#options.persistence || this.#restoring) return;
    try { this.#options.persistence.commit(change); }
    catch { this.#storageUnknown = true; throw new GatewayCacheError('storage_unknown', undefined, undefined, 'query-status'); }
  }
  #metadata(binding: LocalBinding | null | undefined = this.#binding, opened = this.#opened, openedIntent = this.#openedIntent, adoption = this.#adoption,
    terminalProof: GatewayCacheTerminalProof | null = this.#terminalProof ?? null): Uint8Array {
    return encodeCacheControl({ format: this.#coreInventory ? 'sdk2-cache-client-core-v1' : 'sdk2-cache-client-v1', baseUrl: this.#baseUrl, runtimeSessionId: this.runtimeSessionId,
      binding: binding ?? null, opened: opened ?? null, openedIntent: openedIntent ?? null, ...(adoption ? { continuity: adoption } : {}),
      ...(terminalProof ? { terminalProof } : {}) });
  }
  #controlRecord(control: SavedControl): Uint8Array {
    return encodeCacheControl({ operation: control.operation, body: Buffer.from(control.body).toString('base64'), result: control.result });
  }

  /** 只读恢复入口；不重新分配身份，不向终端发送票据或正文。 */
  pendingControl(): GatewayCachePreparedControl<ControlResponse> | undefined { this.#storageReady(); return this.#pending?.handle; }
  pendingExchanges(): readonly { requestId: string; uncertain: boolean; active: boolean }[] {
    this.#storageReady(); return [...this.#exchanges].map(([requestId, value]) => ({ requestId, uncertain: value.uncertain, active: value.active }));
  }
  #exchangeBytes(): number {
    return [...this.#exchanges.values()].reduce((sum, value) => sum + value.frame.length + Buffer.byteLength(value.body)
      + (value.core ? cacheCoreIntentBytes(value.core).byteLength : 0), 0);
  }
  /** 宿主内部数字诊断；控制与交换互斥，票据、原帧和请求号均不外传。 */
  readInventory() {
    this.#storageKnown();
    const pendingControlBytes = this.#pending?.body.byteLength ?? 0, pendingExchangeBytes = this.#exchangeBytes();
    return { retired: this.#retirement !== undefined, pendingControlBytes, pendingExchangeBytes, pendingBytes: pendingControlBytes + pendingExchangeBytes,
      pendingControls: Number(this.#pending !== undefined), pendingExchanges: this.#exchanges.size,
      highWaterPendingControlBytes: this.#highWaterPendingControlBytes, highWaterPendingExchangeBytes: this.#highWaterPendingExchangeBytes,
      highWaterPendingBytes: Math.max(this.#highWaterPendingControlBytes, this.#highWaterPendingExchangeBytes),
      pendingControlCapBytes: 65536, pendingExchangeCapBytes: 67108864, pendingCapBytes: 67108864 };
  }
  /** 原unknown只查同键/原帧；任何状态均不删除pending，不调用prepare或首次派发。 */
  async lookupPending(requestId: string, authority: CacheCoreAuthority, signal?: AbortSignal): Promise<CacheCoreLookup> {
    this.#storageReady();
    const entry = this.#exchanges.get(requestId);
    if (!entry?.core || !authority.lookupCredential) throw new GatewayCacheError('core_source_missing', requestId);
    if (entry.active || this.#pending) throw new GatewayCacheError('pending_execution', requestId);
    const deadline = controlDeadline(this.#options.timeoutMs ?? 30000, signal), combined = deadline.signal;
    try {
      authority.assertCurrent(entry.core);
      const token = await this.#options.token();
      if (typeof token !== 'string' || !token || token.trim() !== token || /[\r\n]/.test(token)) throw new GatewayCacheError('unauthorized', requestId);
      const proof = await authority.lookupCredential(structuredClone(entry.core), combined);
      authority.assertCurrent(entry.core); this.#storageReady(); combined.throwIfAborted();
      if (this.#exchanges.get(requestId) !== entry || entry.active || this.#pending) throw new GatewayCacheError('pending_execution', requestId);
      if (typeof proof !== 'string' || proof.length > 8192 || !/^[A-Za-z0-9_-]+$/.test(proof)) throw new GatewayCacheError('invalid_request', requestId);
      const response = await this.#fetch(`${this.#baseUrl}${CACHE_CORE_PATH}/exchange/lookup`, { method: 'POST', redirect: 'error',
        headers: { 'content-type': CACHE_EXCHANGE_CONTENT_TYPE, accept: 'application/json', 'x-tansr-app-token': token, [CACHE_CORE_HEADER]: proof },
        body: Buffer.from(entry.frame), signal: combined });
      if (response.status !== 200) throw await gatewayCacheResponseError(response, requestId);
      const result = CacheCoreLookupSchema.parse(await readControl(response, 1048576));
      this.#storageReady(); authority.assertCurrent(entry.core);
      return result;
    } finally { deadline.close(); }
  }
  /**
   * 读取当前运行最近接纳的本地绑定观察值；显式 persistence 时由原提交屏障持久化。
   *
   * 这是纯本地、无出网的诊断入口；它不查询或猜测供应商状态，也不泄露
   * bindingId、logicalRef、projectionRef、ticket 等身份与授权材料。
   * 尚未 open/adopt 或控制操作未决时返回 null；不把旧快照当成当前已确认绑定。
   */
  readLocalBinding(): GatewayCacheLocalBindingView | null {
    this.#storageReady();
    if (this.#pending) return null;
    const view = this.#binding?.view;
    if (!view) return null;
    return { revision: view.revision, state: view.state, mode: view.mode, groupGeneration: view.groupGeneration,
      projection: view.projection ? { revision: view.projection.revision, status: view.projection.status, reason: view.projection.reason } : null,
      expiresAt: view.expiresAt, availability: view.availability };
  }
  async resumePendingControl(mode: 'query' | 'replay', signal?: AbortSignal): Promise<ControlResponse> {
    this.#storageReady();
    if (!this.#pending || !['query', 'replay'].includes(mode)) throw new GatewayCacheError('invalid_request');
    return mode === 'query' ? this.#pending.handle.query(signal) : this.#pending.handle.execute(signal);
  }
  /** 宿主只能交回本客户端生成的规范原控制字节；不接受改票据/改身份的恢复。 */
  prepareControlBytes(operation: Pending['operation'], bytes: Uint8Array): GatewayCachePreparedControl<ControlResponse> {
    this.#storageReady();
    const raw = decodeCacheControl(bytes) as Record<string, unknown>;
    if (Buffer.compare(Buffer.from(encodeCacheControl(raw)), Buffer.from(bytes)) !== 0) throw new GatewayCacheError('request_id_conflict');
    let result: GatewayCachePreparedControl<ControlResponse>;
    if (operation === 'open') {
      const input = z.object({ protocol: z.literal(CACHE_PROTOCOL), request: CacheRequestIdentitySchema, runtimeSessionId: z.literal(this.runtimeSessionId),
        intent: CacheOpenIntentSchema, requiredFeature: z.literal(CACHE_FEATURE) }).strict().parse(raw);
      result = this.prepareOpen(input);
    } else {
      const input = z.object({ protocol: z.literal(CACHE_PROTOCOL), request: CacheRequestIdentitySchema, bindingId: z.string(), expectedRevision: z.string(),
        ticket: z.string().optional(), sessionId: z.string().optional(), reason: z.literal('manual').optional() }).strict().parse(raw);
      result = this.prepareMutation(operation, input);
    }
    if (Buffer.compare(Buffer.from(result.bytes()), Buffer.from(bytes)) !== 0) throw new GatewayCacheError('request_id_conflict');
    return result;
  }

  #restore(records: readonly CacheStoredRecord[]): void {
    this.#restoring = true;
    try {
      const map = new Map(records.map(row => [row.key, row.value]));
      if (map.size !== records.length || !map.has('meta')) throw new Error('inventory');
      const rawMeta = decodeCacheControl(map.get('meta')!) as Record<string, unknown>;
      if (rawMeta['format'] === 'sdk2-cache-client-retired-v1') {
        const retired = retirementSchema.parse(rawMeta);
        if (records.length !== 1 || retired.baseUrl !== this.#baseUrl || retired.runtimeSessionId !== this.runtimeSessionId ||
          retired.continuityScope?.applicationScopeId !== this.#options.continuityScope?.applicationScopeId ||
          retired.continuityScope?.endUserId !== this.#options.continuityScope?.endUserId) throw new Error('retirement inventory');
        this.#retirement = retired;
        return;
      }
      const meta = z.object({ format: z.enum(['sdk2-cache-client-v1', 'sdk2-cache-client-core-v1']), baseUrl: z.literal(this.#baseUrl), runtimeSessionId: z.literal(this.runtimeSessionId),
        binding: z.object({ view: CacheBindingSchema, ticket: z.string(), ticketExpiresAt: z.string() }).strict().nullable(),
        opened: CacheOpenResponseSchema.nullable(), openedIntent: z.string().nullable(), continuity: adoptionSchema.optional(),
        terminalProof: terminalProofSchema.optional() }).strict().parse(rawMeta);
      this.#coreInventory = meta.format === 'sdk2-cache-client-core-v1';
      this.#adoption = meta.continuity;
      if (this.#adoption && (!this.#options.continuityScope || this.#adoption.applicationScopeId !== this.#options.continuityScope.applicationScopeId ||
        this.#adoption.endUserId !== this.#options.continuityScope.endUserId || (meta.binding && meta.binding.view.bindingId !== this.#adoption.bindingId) || meta.opened !== null || meta.openedIntent !== null)) throw new Error('continuity inventory');
      this.#binding = meta.binding ?? undefined; this.#opened = meta.opened ?? undefined; this.#openedIntent = meta.openedIntent ?? undefined;
      this.#terminalProof = meta.terminalProof;
      if (this.#terminalProof && (this.#terminalProof.kind !== 'close' || this.#binding ||
        this.#terminalProof.bindingId !== (this.#adoption?.bindingId ?? this.#opened?.binding.bindingId))) throw new Error('terminal proof inventory');
      if (this.#binding) { cacheTicket(this.#binding.ticket); z.string().datetime({ offset: true }).parse(this.#binding.ticketExpiresAt); }
      const flights = new Set<string>();
      const coreIntents = new Map<string, CacheCoreIntent>();
      for (const row of records) {
        if (row.key === 'meta' || row.key === 'pending-control') continue;
        if (row.key.startsWith('control/')) {
          const saved = z.object({ operation: z.enum(['open', 'renew', 'rebind', 'rotate', 'close']), body: z.string(),
            result: z.union([CacheOpenResponseSchema, CacheMutationResponseSchema]).nullable() }).strict().parse(decodeCacheControl(row.value));
          const body = Buffer.from(saved.body, 'base64');
          if (body.toString('base64') !== saved.body || controlKey(saved.operation, body) !== row.key) throw new Error('inventory');
          this.#controls.set(row.key, { operation: saved.operation, body, result: saved.result });
        } else if (row.key.startsWith('exchange/')) {
          const requestId = row.key.slice(9), frame = Buffer.from(row.value);
          if (!originalId(requestId) || frame.length < 5 || frame.length > 33619972) throw new Error('inventory');
          const size = frame.readUInt32BE(0); if (size > 65536 || size < 1 || frame.length <= 4 + size) throw new Error('inventory');
          const metadata = decodeCacheControl(frame.subarray(4, 4 + size)) as { bindingId: string; ticket: string; runtimeSessionId: string; projectionRef: string };
          if (metadata.runtimeSessionId !== this.runtimeSessionId) throw new Error('inventory');
          const body = frame.subarray(4 + size).toString('utf8');
          if (Buffer.compare(encodeGatewayCacheExchange(metadata, requestId, body), frame) !== 0) throw new Error('inventory');
          this.#exchanges.set(requestId, { body, frame, uncertain: true, active: false });
        } else if (row.key.startsWith('core-exchange/')) {
          if (!this.#coreInventory) throw new Error('Core inventory version');
          const id = row.key.slice(14), intent = decodeCacheCoreIntent(row.value);
          if (!originalId(id) || intent.requestId !== id || intent.runtimeSessionId !== this.runtimeSessionId) throw new Error('Core inventory');
          coreIntents.set(id, intent);
        } else if (row.key.startsWith('completed/')) {
          const id = row.key.slice(10); if (!originalId(id) || row.value.length !== 0) throw new Error('inventory'); this.#completed.add(id);
        } else if (row.key.startsWith('flight/')) {
          if (!originalId(row.key.slice(7)) || row.value.length !== 0) throw new Error('inventory'); flights.add(row.key.slice(7));
        } else throw new Error('inventory');
      }
      for (const [id, intent] of coreIntents) {
        const entry = this.#exchanges.get(id);
        if (!entry || createHash('sha256').update(entry.body).digest('hex') !== intent.payloadSha256) throw new Error('Core inventory');
        const frame = decodeCacheControl(entry.frame.subarray(4, 4 + entry.frame.readUInt32BE(0))) as Record<string, unknown>;
        if (frame['bindingId'] !== intent.bindingId || frame['projectionRef'] !== intent.projectionRef) throw new Error('Core inventory');
        entry.core = intent;
      }
      if (this.#controls.size > 128 || this.#exchanges.size > 16 || this.#completed.size > 4096 ||
        [...this.#exchanges].some(([id]) => this.#completed.has(id)) || [...flights].some(id => !this.#exchanges.has(id)) ||
        [...this.#exchanges.values()].reduce((sum, value) => sum + value.frame.length + Buffer.byteLength(value.body) + (value.core ? cacheCoreIntentBytes(value.core).byteLength : 0), 0) > 67108864) throw new Error('inventory');
      const pendingKey = map.get('pending-control');
      const unfinished = [...this.#controls].filter(([, value]) => !('completed' in value) && value.result === null);
      if (pendingKey) {
        const key = Buffer.from(pendingKey).toString('utf8'), saved = this.#controls.get(key);
        if (!saved || 'completed' in saved || saved.result || unfinished.length !== 1 || this.#exchanges.size > 0) throw new Error('inventory');
        this.prepareControlBytes(saved.operation, saved.body); this.#pending!.started = true;
      } else if (unfinished.length !== 0) throw new Error('inventory');
      if (this.#terminalProof && ![...this.#controls.values()].some(saved => !('completed' in saved) && saved.operation === 'close' && saved.result &&
        saved.result.binding.state === 'closed' && saved.result.binding.bindingId === this.#terminalProof!.bindingId &&
        this.#closeProof(saved.body, saved.result).digest === this.#terminalProof!.digest)) throw new Error('terminal proof receipt');
      // 全量校验（含原close证明）成功后才释放完成正文；未决控制和exchange原字节不变。
      for (const [key, saved] of this.#controls) if (!('completed' in saved) && saved.result !== null) this.#controls.set(key, completedControl(saved));
    } catch { this.#storageUnknown = true; throw new GatewayCacheError('storage_unknown', undefined, undefined, 'query-status'); }
    finally { this.#restoring = false; }
  }

  #closeProof(body: Uint8Array, result: ControlResponse): GatewayCacheTerminalProof {
    return { kind: 'close', bindingId: result.binding.bindingId,
      digest: createHash('sha256').update('tansr.sdk2.cache-retirement.close.v1\0').update(body).update('\0').update(encodeCacheControl(result)).digest('hex') };
  }

  /** 墓碑与旧锚清理在同一耐久事务内；任何未知结果必须重开原库，不允许另建运行身份。 */
  #retire(proof: GatewayCacheTerminalProof): GatewayCacheRetirement {
    this.#storageKnown();
    if (this.#retirement) {
      if (!Buffer.from(encodeCacheControl(this.#retirement.proof)).equals(Buffer.from(encodeCacheControl(proof)))) throw new GatewayCacheError('request_id_conflict');
      return structuredClone(this.#retirement);
    }
    if (!this.#options.persistence) throw new GatewayCacheError('mapping_unavailable');
    if (this.#pending || this.#exchanges.size > 0 || [...this.#controls.values()].some(value => !('completed' in value) && value.result === null)) throw new GatewayCacheError('result_unknown', undefined, undefined, 'query-status');
    const retired: GatewayCacheRetirement = { format: 'sdk2-cache-client-retired-v1', baseUrl: this.#baseUrl,
      runtimeSessionId: this.runtimeSessionId, proof: { ...proof }, compactedControls: this.#controls.size,
      compactedExchanges: this.#completed.size, ...(this.#options.continuityScope ? { continuityScope: { ...this.#options.continuityScope } } : {}) };
    this.#commit({ put: [{ key: 'meta', value: encodeCacheControl(retired) }],
      delete: [...this.#controls.keys(), ...[...this.#completed].map(id => `completed/${id}`)] });
    this.#retirement = retired;
    this.#controls.clear(); this.#completed.clear(); this.#binding = undefined; this.#opened = undefined;
    this.#openedIntent = undefined; this.#adoption = undefined; this.#terminalProof = undefined;
    this.#heartbeat = false; this.#capabilities = undefined;
    return structuredClone(retired);
  }

  #retireContinuity(value: ContinuityAdoption): GatewayCacheRetirement {
    this.#storageKnown();
    const configured = this.#options.continuityScope, { intent, receipt } = value;
    if (!configured || !this.#options.persistence || value.baseUrl !== this.#baseUrl ||
      value.scope.applicationScopeId !== configured.applicationScopeId || value.scope.endUserId !== configured.endUserId ||
      !['resume', 'delete'].includes(intent.operation) || intent.previousRuntimeSessionId !== this.runtimeSessionId ||
      (intent.operation === 'resume' ? intent.runtimeSessionId === this.runtimeSessionId : intent.runtimeSessionId !== this.runtimeSessionId)) throw new GatewayCacheError('identity_conflict', intent.requestId);
    const bindingId = this.#retirement?.proof.bindingId ?? this.#binding?.view.bindingId ?? this.#adoption?.bindingId ?? this.#opened?.binding.bindingId;
    if (bindingId !== intent.bindingId) throw new GatewayCacheError('identity_conflict', intent.requestId);
    const proof: GatewayCacheTerminalProof = { kind: intent.operation === 'resume' ? 'continuity-resume' : 'continuity-delete', bindingId,
      digest: createHash('sha256').update('tansr.sdk2.cache-retirement.continuity.v1\0').update(encodeCacheControl({ baseUrl: value.baseUrl, scope: value.scope, intent, receipt })).digest('hex') };
    return this.#retire(proof);
  }


  /** 只由已签名通道的模块能力调用；不能接受终端响应对象代替可信回执。 */
  #adoptContinuity(value: ContinuityAdoption): void {
    this.#storageReady();
    const configured = this.#options.continuityScope, { intent, receipt } = value;
    if (!configured || !this.#options.persistence || value.baseUrl !== this.#baseUrl ||
      value.scope.applicationScopeId !== configured.applicationScopeId || value.scope.endUserId !== configured.endUserId ||
      intent.runtimeSessionId !== this.runtimeSessionId || receipt.source.runtimeSessionId !== this.runtimeSessionId ||
      intent.operation !== 'resume' || !receipt.ticket || !receipt.ticketExpiresAt) throw new GatewayCacheError('identity_conflict');
    if (this.#pending || this.#exchanges.size > 0) throw new GatewayCacheError('result_unknown', intent.requestId, undefined, 'query-status');
    const adoption: LocalAdoption = { applicationScopeId: configured.applicationScopeId, endUserId: configured.endUserId, sourceNamespace: value.scope.sourceNamespace,
      intentDigest: createHash('sha256').update(encodeCacheControl(intent)).digest('hex'), receiptDigest: createHash('sha256').update(encodeCacheControl(receipt)).digest('hex'), bindingId: receipt.binding.bindingId };
    if (this.#adoption) {
      if (Buffer.from(encodeCacheControl(this.#adoption)).equals(Buffer.from(encodeCacheControl(adoption)))) return;
      throw new GatewayCacheError('request_id_conflict', intent.requestId);
    }
    if (this.#binding || this.#opened || this.#controls.size > 0 || this.#completed.size > 0) throw new GatewayCacheError('identity_conflict', intent.requestId);
    if (!this.#heartbeat || Date.parse(receipt.ticketExpiresAt) <= Date.now() || Date.parse(receipt.binding.expiresAt) <= Date.now()) throw new GatewayCacheError('mapping_unavailable', intent.requestId);
    const binding = { view: structuredClone(receipt.binding), ticket: receipt.ticket, ticketExpiresAt: receipt.ticketExpiresAt };
    this.#commit({ put: [{ key: 'meta', value: this.#metadata(binding, undefined, undefined, adoption) }], delete: [] });
    this.#binding = binding; this.#adoption = adoption;
  }

  async #request<T>(path: string, body: Uint8Array | undefined, signal: AbortSignal | undefined, consume: (response: Response) => Promise<T>): Promise<T> {
    this.#storageReady();
    const token = await this.#options.token();
    this.#storageReady();
    if (typeof token !== 'string' || token.trim() !== token || token.length === 0 || /[\r\n]/.test(token)) throw new GatewayCacheError('unauthorized');
    const deadline = controlDeadline(this.#options.timeoutMs ?? 30000, signal);
    try {
      if (body && path.startsWith('/cache/') && this.#pending) this.#commit({ put: [{ key: 'pending-control', value: Buffer.from(controlKey(this.#pending.operation, this.#pending.body)) }], delete: [] });
      const response = await this.#fetch(`${this.#baseUrl}${path}`, { method: body === undefined ? 'GET' : 'POST', redirect: 'error',
        headers: { accept: 'application/json', 'x-tansr-app-token': token, ...(body ? { 'content-type': 'application/json' } : {}) },
        ...(body ? { body: Buffer.from(body) } : {}), signal: deadline.signal,
      });
      return await consume(response);
    } finally { deadline.close(); }
  }

  async #json<T>(path: string, body: Uint8Array | undefined, schema: z.ZodType<T>, signal?: AbortSignal): Promise<T> {
    try {
      return await this.#request(path, body, signal, async response => {
        if (response.status !== 200) throw await gatewayCacheResponseError(response);
        const raw = await readControl(response);
        this.#storageReady();
        return schema.parse(raw);
      });
    } catch (error) {
      if (error instanceof GatewayCacheError) throw error;
      throw new GatewayCacheError('result_unknown', undefined, undefined, 'query-status');
    }
  }

  async capabilities(signal?: AbortSignal): Promise<GatewayCacheCapabilities> {
    const value = await this.#json('/cache/v1/capabilities', undefined, CacheCapabilitiesSchema, signal);
    this.#storageReady();
    this.#capabilities = { value, expires: Date.now() + value.limits['negotiatedTtlMs']! };
    return structuredClone(value);
  }
  async #ready(signal?: AbortSignal): Promise<GatewayCacheCapabilities> {
    this.#storageReady();
    const caps = this.#capabilities && this.#capabilities.expires > Date.now() ? this.#capabilities.value : await this.capabilities(signal);
    this.#storageReady();
    if (!caps.features.includes(CACHE_FEATURE) || !caps.operationEpoch || Date.parse(caps.operationEpoch.expiresAt) <= Date.now()) throw new GatewayCacheError('unsupported_capability');
    return caps;
  }
  async #registerRuntime(signal?: AbortSignal): Promise<void> {
    this.#storageReady();
    if (this.#heartbeat) return;
    const heartbeat = await this.#json('/heartbeat', encodeCacheControl({ client: 'sdk', sessionId: this.runtimeSessionId }),
      z.object({ sessionId: z.literal(this.runtimeSessionId) }).passthrough(), signal);
    this.#storageReady();
    this.#heartbeat = heartbeat.sessionId === this.runtimeSessionId;
  }

  async open(intent: GatewayCacheOpenIntent = { kind: 'new' }, signal?: AbortSignal): Promise<GatewayCacheOpenResponse> {
    this.#storageReady();
    if (this.#adoption) throw new GatewayCacheError('mapping_unavailable');
    const savedIntent = CacheOpenIntentSchema.parse(decodeCacheControl(encodeCacheControl(intent)));
    if (this.#pending) {
      const raw = decodeCacheControl(this.#pending.body) as { intent?: unknown };
      if (this.#pending.operation !== 'open' || Buffer.compare(Buffer.from(encodeCacheControl(raw.intent)), Buffer.from(encodeCacheControl(savedIntent))) !== 0) throw new GatewayCacheError('request_id_conflict');
      return this.#pending.handle.execute(signal) as Promise<GatewayCacheOpenResponse>;
    }
    if (this.#opened) {
      if (this.#openedIntent !== Buffer.from(encodeCacheControl(savedIntent)).toString('utf8')) throw new GatewayCacheError('request_id_conflict');
      if (!this.#binding || this.#binding.view.state !== 'active') throw new GatewayCacheError('mapping_unavailable');
      return structuredClone(this.#opened);
    }
    await this.#registerRuntime(signal);
    const caps = await this.#ready(signal);
    return this.prepareOpen({ request: { operationEpoch: caps.operationEpoch!.id, requestId: randomUUID() }, intent: savedIntent }).execute(signal);
  }

  prepareOpen(input: { request: GatewayCacheRequestIdentity; intent: GatewayCacheOpenIntent }): GatewayCachePreparedControl<GatewayCacheOpenResponse> {
    this.#storageReady();
    if (this.#adoption) throw new GatewayCacheError('mapping_unavailable');
    const request = CacheRequestIdentitySchema.parse(input.request), intent = CacheOpenIntentSchema.parse(input.intent);
    return this.#prepare('open', '/cache/v1/bindings', null, { protocol: CACHE_PROTOCOL, request, runtimeSessionId: this.runtimeSessionId, intent, requiredFeature: CACHE_FEATURE }) as GatewayCachePreparedControl<GatewayCacheOpenResponse>;
  }

  prepareMutation(operation: GatewayCacheMutation, input: { request: GatewayCacheRequestIdentity; bindingId: string; expectedRevision: string; ticket?: string; sessionId?: string; reason?: 'manual' }): GatewayCachePreparedControl<GatewayCacheMutationResponse> {
    this.#storageReady();
    if (!['renew', 'rebind', 'rotate', 'close'].includes(operation)) throw new GatewayCacheError('invalid_request');
    const bindingId = cacheId(input.bindingId), request = CacheRequestIdentitySchema.parse(input.request), expectedRevision = cacheRevision(input.expectedRevision);
    if (this.#adoption && bindingId !== this.#adoption.bindingId) throw new GatewayCacheError('identity_conflict', request.requestId);
    const body: Record<string, unknown> = { protocol: CACHE_PROTOCOL, request, bindingId, expectedRevision };
    if (operation === 'rebind') {
      if (input.sessionId !== this.runtimeSessionId || input.ticket !== undefined || input.reason !== undefined) throw new GatewayCacheError('invalid_request');
      body['sessionId'] = this.runtimeSessionId;
    } else {
      body['ticket'] = cacheTicket(input.ticket ?? '');
      if (input.sessionId !== undefined || (operation !== 'rotate' && input.reason !== undefined)) throw new GatewayCacheError('invalid_request');
      if (operation === 'rotate') { if (input.reason !== 'manual') throw new GatewayCacheError('invalid_request'); body['reason'] = 'manual'; }
    }
    return this.#prepare(operation, `/cache/v1/bindings/${encodeURIComponent(bindingId)}/${operation}`, bindingId, body) as GatewayCachePreparedControl<GatewayCacheMutationResponse>;
  }

  #prepare(operation: Pending['operation'], path: string, bindingId: string | null, body: Record<string, unknown>): GatewayCachePreparedControl<ControlResponse> {
    this.#storageReady();
    const bytes = encodeCacheControl(body), identity = CacheRequestIdentitySchema.parse(body['request']);
    const key = controlKey(operation, bytes);
    for (const [savedKey, saved] of this.#controls) {
      if (saved.operation !== operation) continue;
      const original = 'completed' in saved ? saved : decodeCacheControl(saved.body) as { request: GatewayCacheRequestIdentity; bindingId?: string };
      if (original.request.operationEpoch === identity.operationEpoch && original.request.requestId === identity.requestId &&
        (original.bindingId ?? null) === bindingId && savedKey !== key) throw new GatewayCacheError('request_id_conflict', identity.requestId);
    }
    const previous = this.#controls.get(key);
    if (previous && previous.operation !== operation) throw new GatewayCacheError('request_id_conflict');
    if (previous && ('completed' in previous || previous.result)) {
      const query = (signal?: AbortSignal) => this.queryOperation({ operation, request: identity, bindingId }, signal);
      return Object.freeze({ operation, request: Object.freeze({ ...identity }), bytes: () => new Uint8Array(bytes), execute: query, query });
    }
    if (this.#pending) {
      if (this.#pending.operation !== operation || Buffer.compare(Buffer.from(this.#pending.body), Buffer.from(bytes)) !== 0) throw new GatewayCacheError('request_id_conflict');
      return this.#pending.handle;
    }
    if (this.#exchanges.size > 0) throw new GatewayCacheError('result_unknown', undefined, undefined, 'query-status');
    if (this.#options.persistence && !previous && this.#controls.size >= 128) throw new GatewayCacheError('capacity_exceeded', identity.requestId);
    const run = async (query: boolean, signal?: AbortSignal): Promise<ControlResponse> => {
      this.#storageReady();
      if (pending.sending) throw new GatewayCacheError('result_unknown', identity.requestId, undefined, 'query-status');
      if (this.#pending !== pending) throw new GatewayCacheError('request_id_conflict', identity.requestId);
      pending.sending = true;
      try {
        if (!query && operation === 'open') await this.#registerRuntime(signal);
        if (!query && !pending.started) await this.#ready(signal);
        pending.started = true;
        const result = query ? await this.queryOperation({ operation, request: identity, bindingId }, signal) : await this.#json(path, bytes,
          z.union([CacheOpenResponseSchema, CacheMutationResponseSchema]), signal);
        if (result.request.operationEpoch !== identity.operationEpoch || result.request.requestId !== identity.requestId ||
          (bindingId !== null && result.binding.bindingId !== bindingId) ||
          (operation === 'open' ? !('relation' in result) : !('operation' in result) || result.operation !== operation)) throw new GatewayCacheError('invalid_response');
        const binding = result.ticket && result.ticketExpiresAt ? { view: result.binding, ticket: result.ticket, ticketExpiresAt: result.ticketExpiresAt } : undefined;
        const opened = 'relation' in result ? result : this.#opened;
        const openedIntent = 'relation' in result ? Buffer.from(encodeCacheControl(body['intent'])).toString('utf8') : this.#openedIntent;
        const completed = { operation, body: bytes, result };
        const resident = this.#options.persistence ? completedControl(completed) : undefined;
        const terminalProof = operation === 'close' && result.binding.state === 'closed' && this.#binding?.view.bindingId === result.binding.bindingId
          ? this.#closeProof(bytes, result) : null;
        this.#commit({ put: [{ key: 'meta', value: this.#metadata(binding ?? null, opened, openedIntent, this.#adoption, terminalProof) }, { key, value: this.#controlRecord(completed) }], delete: ['pending-control'] });
        this.#binding = binding; this.#opened = opened; this.#openedIntent = openedIntent;
        this.#terminalProof = terminalProof ?? undefined;
        if (resident) this.#controls.set(key, resident);
        this.#pending = undefined;
        return structuredClone(result);
      } finally { pending.sending = false; }
    };
    const handle: GatewayCachePreparedControl<ControlResponse> = Object.freeze({ operation, request: Object.freeze({ ...identity }), bytes: () => new Uint8Array(bytes), execute: (signal?: AbortSignal) => run(false, signal), query: (signal?: AbortSignal) => run(true, signal) });
    const pending: Pending = { operation, identity, body: bytes, path, bindingId, handle, sending: false, started: false };
    const saved: SavedControl = { operation, body: bytes, result: null };
    this.#commit({ put: [{ key, value: this.#controlRecord(saved) }, { key: 'pending-control', value: Buffer.from(key) }], delete: [] });
    if (this.#options.persistence) this.#controls.set(key, saved);
    this.#pending = pending; this.#highWaterPendingControlBytes = Math.max(this.#highWaterPendingControlBytes, bytes.byteLength); return handle;
  }

  async queryOperation(input: { operation: Pending['operation']; request: GatewayCacheRequestIdentity; bindingId: string | null }, signal?: AbortSignal): Promise<ControlResponse> {
    const request = CacheRequestIdentitySchema.parse(input.request);
    if (!['open', 'renew', 'rebind', 'rotate', 'close'].includes(input.operation) || (input.operation === 'open' ? input.bindingId !== null : input.bindingId === null)) throw new GatewayCacheError('invalid_request');
    const query = new URLSearchParams({ protocol: CACHE_PROTOCOL, operation: input.operation, operationEpoch: request.operationEpoch, requestId: request.requestId,
      ...(input.bindingId === null ? {} : { bindingId: cacheId(input.bindingId) }) });
    const result = await this.#json(`/cache/v1/operations?${query}`, undefined, z.union([CacheOpenResponseSchema, CacheMutationResponseSchema]), signal);
    if (result.request.operationEpoch !== request.operationEpoch || result.request.requestId !== request.requestId ||
      (input.operation === 'open' ? !('relation' in result) : !('operation' in result) || result.operation !== input.operation || result.binding.bindingId !== input.bindingId)) throw new GatewayCacheError('invalid_response');
    return result;
  }
  async readBinding(bindingId: string, signal?: AbortSignal): Promise<GatewayCacheBinding> {
    const result = await this.#json(`/cache/v1/bindings/${encodeURIComponent(cacheId(bindingId))}?protocol=${CACHE_PROTOCOL}`, undefined, CacheBindingSchema, signal);
    if (result.bindingId !== bindingId) throw new GatewayCacheError('invalid_response');
    return result;
  }
  async diagnostics(bindingId: string, after: string | null = null, limit = 100, signal?: AbortSignal): Promise<z.infer<typeof emptyDiagnostic>> {
    if (!Number.isInteger(limit) || limit < 1 || limit > 100) throw new GatewayCacheError('invalid_request');
    const query = new URLSearchParams({ protocol: CACHE_PROTOCOL, limit: String(limit), ...(after === null ? {} : { after: cacheId(after) }) });
    return this.#json(`/cache/v1/bindings/${encodeURIComponent(cacheId(bindingId))}/diagnostics?${query}`, undefined, emptyDiagnostic, signal);
  }
  async coreCapabilities(signal?: AbortSignal): Promise<z.infer<typeof CacheCoreCapabilitiesSchema>> {
    const value = await this.#json(`${CACHE_CORE_PATH}/capabilities`, undefined, CacheCoreCapabilitiesSchema, signal);
    if (!value.features.includes('trusted-core-projection-v1')) throw new GatewayCacheError('unsupported_capability');
    return value;
  }
  /** 原diagnostics继续返回旧空视图；显式新API保留已知/unknown与采样边界。 */
  async coreDiagnostics(bindingId: string, after: string | null = null, limit = 100, signal?: AbortSignal): Promise<CacheCoreDiagnostics> {
    if (!Number.isInteger(limit) || limit < 1 || limit > 100 || (after !== null && (after.length > 150 || !/^[0-9]+\.[A-Za-z0-9][A-Za-z0-9._~-]*$/.test(after)))) throw new GatewayCacheError('invalid_request');
    const query = new URLSearchParams({ protocol: CACHE_CORE_PROTOCOL, limit: String(limit), ...(after === null ? {} : { after }) });
    return this.#request(`${CACHE_CORE_PATH}/bindings/${encodeURIComponent(cacheId(bindingId))}/diagnostics?${query}`, undefined, signal, async response => {
      if (response.status !== 200) throw await gatewayCacheResponseError(response);
      const result = CacheCoreDiagnosticsSchema.parse(await readControl(response));
      this.#storageReady();
      if (result.rows.length > limit) throw new GatewayCacheError('invalid_response');
      return result;
    });
  }
  transport(coreAuthority?: CacheCoreAuthority): GatewayCacheTransport {
    this.#storageReady();
    if (!this.#binding || this.#pending) throw new GatewayCacheError('mapping_unavailable');
    return coreAuthority === undefined ? this.#transport : Object.freeze({ ...this.#transport, coreProjection: true as const,
      prepare: (id: string, body: string, core?: CacheCoreRequest) => this.#prepareExchange(id, body, core, coreAuthority) });
  }

  #prepareExchange(requestId: string, body: string, coreRequest?: CacheCoreRequest, coreAuthority?: CacheCoreAuthority): GatewayCachePreparedExchange {
    this.#storageReady();
    if (this.#completed.has(requestId)) throw new GatewayCacheError('receipt_expired', requestId);
    let entry = this.#exchanges.get(requestId);
    if (!entry) {
      if (this.#pending || [...this.#exchanges.values()].some(value => value.uncertain)) throw new GatewayCacheError('result_unknown', requestId, undefined, 'query-status');
      const migrateCore = coreAuthority !== undefined && !this.#coreInventory;
      if (migrateCore && this.#exchanges.size !== 0) throw new GatewayCacheError('pending_execution', requestId);
      const binding = this.#binding;
      if (!binding || binding.view.state !== 'active' || !binding.view.projection || binding.view.projection.status === 'rebuild-required' || Date.parse(binding.ticketExpiresAt) <= Date.now()) throw new GatewayCacheError('mapping_unavailable', requestId);
      if (this.#exchanges.size >= 16 || this.#completed.size + this.#exchanges.size >= 4096) throw new GatewayCacheError('capacity_exceeded', requestId);
      const frame = encodeGatewayCacheExchange({ bindingId: binding.view.bindingId, ticket: binding.ticket, runtimeSessionId: this.runtimeSessionId, projectionRef: binding.view.projection.projectionRef }, requestId, body);
      let core: CacheCoreIntent | undefined;
      if (coreAuthority !== undefined) {
        if (!coreRequest) throw new GatewayCacheError('core_source_missing', requestId);
        assertCacheCoreRequest(coreRequest);
        core = coreAuthority.prepare({ runtimeSessionId: this.runtimeSessionId, bindingId: binding.view.bindingId, bindingRevision: binding.view.revision,
          projectionRef: binding.view.projection.projectionRef, requestId, payloadSha256: createHash('sha256').update(body).digest('hex') }, coreRequest);
      }
      const bytes = frame.length + Buffer.byteLength(body) + (core ? cacheCoreIntentBytes(core).byteLength : 0) +
        [...this.#exchanges.values()].reduce((sum, value) => sum + value.frame.length + Buffer.byteLength(value.body) + (value.core ? cacheCoreIntentBytes(value.core).byteLength : 0), 0);
      if (bytes > 67108864) throw new GatewayCacheError('capacity_exceeded', requestId);
      if (migrateCore) this.#coreInventory = true;
      this.#commit({ put: [{ key: `exchange/${requestId}`, value: frame }, ...(core ? [{ key: `core-exchange/${requestId}`, value: cacheCoreIntentBytes(core) }] : []),
        ...(migrateCore ? [{ key: 'meta', value: this.#metadata() }] : [])], delete: [] });
      entry = { body, frame, uncertain: false, active: false, ...(core ? { core } : {}) }; this.#exchanges.set(requestId, entry);
      this.#highWaterPendingExchangeBytes = Math.max(this.#highWaterPendingExchangeBytes, bytes);
    } else if (entry.body !== body) throw new GatewayCacheError('request_id_conflict', requestId);
    if (entry.core) {
      if (!coreAuthority || !coreRequest) throw new GatewayCacheError('core_source_missing', requestId);
      assertCacheCoreRequest(coreRequest);
      if (!Buffer.from(encodeCacheControl(coreRequest.facts)).equals(Buffer.from(encodeCacheControl(entry.core.core)))) throw new GatewayCacheError('request_id_conflict', requestId);
    } else if (coreAuthority) throw new GatewayCacheError('request_id_conflict', requestId);
    const fixed = entry;
    return Object.freeze({ requestId, send: async (signal: AbortSignal, streaming: boolean): Promise<Response> => {
      this.#storageReady();
      if (fixed.active) throw new GatewayCacheError('result_unknown', requestId, undefined, 'query-status');
      fixed.active = true;
      // Only an actual exchange fetch can leave the provider-side result unknown.
      // Capability, size, token and local durability checks all happen before the
      // request is handed to fetch; poisoning the durable exchange in those paths
      // would let the next retry skip #ready() and bypass the capability/size gate.
      let exchangeStarted = false;
      try {
        if (!fixed.uncertain) {
          const caps = await this.#ready(signal);
          if (Buffer.byteLength(fixed.body) > caps.limits['exchangeBytes']! || fixed.frame.readUInt32BE(0) > caps.limits['controlBytes']!) throw new GatewayCacheError('payload_too_large', requestId);
          if (fixed.core) await this.coreCapabilities(signal);
        }
        const token = await this.#options.token();
        this.#storageReady();
        if (typeof token !== 'string' || !token || token.trim() !== token || /[\r\n]/.test(token)) throw new GatewayCacheError('unauthorized', requestId);
        const proof = fixed.core ? await coreAuthority!.credential(structuredClone(fixed.core), signal) : undefined;
        if (fixed.core) {
          coreRequest?.assertCurrent(); coreAuthority!.assertCurrent(fixed.core);
          if (typeof proof !== 'string' || proof.length > 8192 || !/^[A-Za-z0-9_-]+$/.test(proof)) throw new GatewayCacheError('invalid_request', requestId);
        }
        this.#storageReady();
        this.#commit({ put: [{ key: `flight/${requestId}`, value: new Uint8Array() }], delete: [] });
        exchangeStarted = true;
        return await this.#fetch(`${this.#baseUrl}${fixed.core ? CACHE_CORE_PATH : '/cache/v1'}/exchange`, { method: 'POST', redirect: 'error',
          headers: { 'content-type': CACHE_EXCHANGE_CONTENT_TYPE, accept: streaming ? 'text/event-stream' : 'application/json', 'x-tansr-app-token': token,
            ...(proof ? { [CACHE_CORE_HEADER]: proof } : {}) },
          body: Buffer.from(fixed.frame), signal,
        });
      } catch (error) {
        if (exchangeStarted) {
          this.#transport.uncertain(requestId);
          throw new GatewayCacheError('result_unknown', requestId, undefined, 'query-status');
        }
        // No provider request was started. Preserve deterministic local errors
        // and leave the durable entry replayable after the caller fixes the gate.
        fixed.active = false;
        if (error instanceof GatewayCacheError) throw error;
        throw error;
      }
    } });
  }
}

export function createGatewayCacheClient(options: GatewayCacheClientOptions): GatewayCacheClient { return new GatewayCacheClient(options); }
