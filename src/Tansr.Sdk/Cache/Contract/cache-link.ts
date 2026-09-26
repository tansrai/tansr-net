/** SDK2 显式启用的两跳控制账本。原请求先落盘，API 受理与本地发布不伪装为同一事务。 */
import { createHash, createHmac, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { z } from 'zod';
import { endUserKeyOf } from '../v2/agent-session-store.js';
import { isVaultReservation, readVaultValue, writeVault, unusedVaultReservations, type VaultReservation } from './cache-vault-reserve.js';
import { GatewayCacheError, type GatewayCacheClient, type GatewayCacheTransport } from '@tansr/providers';
import { CACHE_PROTOCOL, CACHE_FEATURE, CacheRequestIdentitySchema, CacheOpenIntentSchema, CacheBindingSchema,
  cacheId, cacheRevision, cacheRuntimeId, cacheTicket, decodeCacheControl, encodeCacheControl,
  type GatewayCacheMutation, type GatewayCacheOpenResponse, type GatewayCacheMutationResponse,
  type GatewayCacheRequestIdentity } from '@tansr/providers/internal/cache-wire';
import type { ContinuityReceipt } from '@tansr/providers/internal/runtime-continuity';
import type { CacheCoreAuthority, CacheCoreRequest } from '@tansr/providers/internal/cache-core';
import { gatewayCacheRetirement, type SqliteCacheRecordStore } from '@tansr/providers/internal/cache-store';

const bindingSchema = CacheBindingSchema.extend({ audience: z.literal('serve-cache') });
type Binding = z.infer<typeof bindingSchema>;
type UpstreamReceipt = GatewayCacheOpenResponse | GatewayCacheMutationResponse;
type Operation = 'open' | GatewayCacheMutation;
const requestSchema = z.object({ protocol: z.literal(CACHE_PROTOCOL), request: CacheRequestIdentitySchema });
const openSchema = requestSchema.extend({ sessionId: z.string(), intent: CacheOpenIntentSchema, requiredFeature: z.literal(CACHE_FEATURE) }).strict();
const mutationSchema = requestSchema.extend({ bindingId: z.string(), expectedRevision: z.string(), ticket: z.string().optional(),
  sessionId: z.string().optional(), reason: z.literal('manual').optional() }).strict();

/** 只由工厂的真实 Store/空闲租约构造，HTTP 输入不能提供历史见证。 */
export interface CacheLinkWitness { readonly digest: string; readonly signal: AbortSignal; assertCurrent(): void }
export interface CacheLinkSession {
  readonly runtimeSessionId: string;
  readonly client: GatewayCacheClient;
  /** 同一已注册驱动的私有身份；仅起轮元数据刷新使用，不能由 HTTP 指定。 */
  readonly configurationIdentity?: object;
  withWitness<T>(work: (witness: CacheLinkWitness) => Promise<T>): Promise<T>;
}
export interface CacheLinkOptions {
  readonly store: SqliteCacheRecordStore;
  readonly key: { id: string; bytes: Uint8Array };
  readonly session: (endUserId: string, sessionId: string) => CacheLinkSession;
  /** 每次调用都经当前用户的鉴权令牌到 API；GET 不隐式创建运行或账本。 */
  readonly client: (endUserId: string, runtimeSessionId: string) => Promise<GatewayCacheClient>;
  readonly capabilities: (endUserId: string) => ReturnType<GatewayCacheClient['capabilities']>;
  readonly continuityReservation?: (endUserId: string, sessionId: string) => string | undefined;
  readonly assertContinuityOpen?: (endUserId: string, sessionId: string) => void;
  readonly assertContinuityAvailable?: (endUserId: string, sessionId: string) => void;
  readonly confirmContinuity?: (endUserId: string, sessionId: string, session: CacheLinkSession, witness: CacheLinkWitness, binding: UpstreamReceipt['binding']) => Promise<void>;
  readonly now?: () => number;
}
interface Link {
  endUserId: string; sessionId: string; runtimeSessionId: string; binding: Binding;
  upstreamBindingId: string; upstreamTicket: string | null; localTicket: string | null;
}
interface SavedOperation {
  endUserId: string; sessionId: string; runtimeSessionId: string; operation: Operation;
  original: string; upstream: string; digest: string; localBindingId: string; logicalRef: string; localTicket: string;
  compensationRequest: GatewayCacheRequestIdentity; state: 'pending' | 'compensating' | 'cancelled' | 'completed';
  receipt: Record<string, unknown> | null; compensation: string | null; reservedKeys?: readonly string[];
  /** 宿主提示词准备阶段的同运行元数据刷新；不读取或改写历史来源。 */
  configurationRefresh?: true;
}
interface RetiredOperation {
  state: 'retired'; endUserId: string; runtimeSessionId: string; localBindingId: string;
  originalDigest: string;
}
type StoredOperation = SavedOperation | RetiredOperation;
interface Epoch { id: string; issuedAt: string; expiresAt: string; state: 'active' }
const randomTicket = (): string => randomBytes(32).toString('base64url');
const fail = (code: string, status = code === 'result_unknown' ? 503 : 409): never => { throw new GatewayCacheError(code, undefined, status, code === 'result_unknown' ? 'query-status' : 'none'); };

export class ServeCacheLink {
  readonly #options: CacheLinkOptions;
  #epoch: Epoch;
  readonly #key: Buffer;
  readonly #now: () => number;
  readonly #inflight = new Set<string>();
  #poisoned = false;
  constructor(options: CacheLinkOptions) {
    if (options.key.bytes.byteLength !== 32) throw new TypeError('Invalid cache link key');
    cacheId(options.key.id); this.#key = Buffer.from(options.key.bytes); this.#options = options; this.#now = options.now ?? Date.now;
    const saved = this.#read<{ format: string; keyId: string; epoch: Epoch }>('metadata');
    if (saved) {
      if (!['serve-cache-link-v1', 'serve-cache-link-v2'].includes(saved.format) || saved.keyId !== options.key.id || !Number.isFinite(Date.parse(saved.epoch.expiresAt))) fail('result_unknown');
      this.#epoch = saved.epoch;
      if (saved.format === 'serve-cache-link-v1') this.#write([['metadata', { ...saved, format: 'serve-cache-link-v2' }]]);
    } else {
      if (options.store.inventory().rows !== 0) fail('result_unknown');
      this.#epoch = { id: randomUUID(), issuedAt: new Date(this.#now()).toISOString(), expiresAt: new Date(this.#now() + 86400000).toISOString(), state: 'active' };
      this.#write([['metadata', { format: 'serve-cache-link-v2', keyId: options.key.id, epoch: this.#epoch }]]);
    }
  }
  /** 运维显式轮换；旧操作和拒重锚完整保留，GET 永不铸新 epoch。 */
  rotateEpoch(): void {
    const next: Epoch = { id: randomUUID(), issuedAt: new Date(this.#now()).toISOString(), expiresAt: new Date(this.#now() + 86400000).toISOString(), state: 'active' };
    this.#write([['metadata', { format: 'serve-cache-link-v2', keyId: this.#options.key.id, epoch: next }]]); this.#epoch = next;
  }
  /** 宿主显式冷新关系沿原C1请求；身份先由宿主持久化，重入不另造键。 */
  coldNewRequest(): GatewayCacheRequestIdentity { return { operationEpoch: this.#epoch.id, requestId: randomUUID() }; }
  openColdNew(eu: string, sid: string, request: GatewayCacheRequestIdentity): Promise<unknown> {
    return this.execute(eu, 'open', { protocol: CACHE_PROTOCOL, request, sessionId: sid, intent: { kind: 'new' }, requiredFeature: CACHE_FEATURE });
  }
  /** 仅显式冷建的下一次受理可换请求；先以原补偿字节确认旧C1已关闭，unknown绝不当终态。 */
  async confirmColdNewCancellation(eu: string, sid: string, runtime: string, request: GatewayCacheRequestIdentity): Promise<boolean> {
    const key = this.#operationKey(eu, 'open', request, null), saved = this.#read<StoredOperation>(key);
    if (!saved || saved.state === 'pending' || saved.state === 'completed') return false;
    if (saved.state === 'retired') return fail('receipt_expired', 410);
    const expected = Buffer.from(encodeCacheControl({ protocol: CACHE_PROTOCOL, request, sessionId: sid, intent: { kind: 'new' }, requiredFeature: CACHE_FEATURE })).toString('base64');
    if (saved.original !== expected || saved.endUserId !== eu || saved.sessionId !== sid || saved.runtimeSessionId !== runtime || saved.compensation === null)
      return fail('result_unknown');
    if (this.#inflight.has(key)) return fail('result_unknown');
    this.#inflight.add(key);
    try {
      const client = await this.#options.client(eu, runtime);
      const bytes = Buffer.from(saved.compensation, 'base64');
      const closed = await client.prepareControlBytes('close', bytes).execute();
      if (closed.binding.state !== 'closed') return fail('result_unknown');
      if (saved.state !== 'cancelled') this.#write([[key, { ...saved, state: 'cancelled' }]], [this.#name('guard', eu, sid),
        ...unusedVaultReservations(this.#options.store, key, saved.reservedKeys!)]);
      return true;
    } finally { this.#inflight.delete(key); }
  }
  close(): void { this.#poisoned = true; this.#key.fill(0); }
  #hash(domain: string, ...parts: string[]): string {
    return createHmac('sha256', this.#key).update(encodeCacheControl([domain, ...parts])).digest('hex');
  }
  #name(domain: string, endUserId: string, ...parts: string[]): string { return `${domain}/${this.#hash(domain, endUserId, ...parts)}`; }
  #read<T>(key: string): T | undefined {
    if (this.#poisoned) return fail('result_unknown');
    try { const raw = this.#options.store.read(key); if (raw === undefined) return undefined;
      const value = readVaultValue(key, raw); return isVaultReservation(value) ? undefined : value as T; }
    catch { this.#poisoned = true; return fail('result_unknown'); }
  }
  #write(puts: readonly (readonly [string, unknown])[], deletes: readonly string[] = [], reserve: readonly VaultReservation[] = []): void {
    if (this.#poisoned) fail('result_unknown');
    try {
      writeVault(this.#options.store, puts, deletes, reserve);
    } catch (error) { if (error instanceof GatewayCacheError && error.code === 'capacity_exceeded') throw error;
      this.#poisoned = true; fail('result_unknown'); }
  }
  #operationKey(eu: string, operation: Operation, request: GatewayCacheRequestIdentity, bindingId: string | null): string {
    return this.#name('operation', eu, operation, request.operationEpoch, request.requestId, ...(bindingId === null ? [] : [bindingId]));
  }
  #link(eu: string, id: string): Link {
    cacheId(id); const link = this.#read<Link>(this.#name('binding', eu, id));
    if (!link || link.endUserId !== eu || link.binding.bindingId !== id) return fail('mapping_unavailable', 404);
    return link;
  }
  #ticket(eu: string, ticket: string): Link {
    cacheTicket(ticket); const id = this.#read<string>(this.#name('ticket', eu, ticket));
    if (!id) return fail('mapping_unavailable', 404);
    const link = this.#link(eu, id);
    if (link.localTicket === null || !timingSafeEqual(Buffer.from(link.localTicket), Buffer.from(ticket))) return fail('mapping_unavailable', 404);
    return link;
  }
  #semantic(operation: Operation, original: string): string {
    const { request: _request, ...meaning } = decodeCacheControl(Buffer.from(original, 'base64')) as Record<string, unknown>;
    const derived = createHmac('sha256', this.#key).update('tansr.sdk2.cache-operation.key.v1').digest();
    try { return createHmac('sha256', derived).update('tansr.sdk2.cache-operation.v1\0').update(encodeCacheControl({ operation, ...meaning })).digest('hex'); }
    finally { derived.fill(0); }
  }
  #publicBinding(receipt: UpstreamReceipt['binding'], bindingId: string, logicalRef: string): Binding {
    return bindingSchema.parse({ ...receipt, audience: 'serve-cache', bindingId, logicalRef,
      projection: receipt.projection === null ? null : { ...receipt.projection, projectionRef: this.#hash('projection', bindingId, receipt.projection.projectionRef) } });
  }
  async capabilities(eu: string): Promise<unknown> {
    const upstream = await this.#options.capabilities(eu);
    const enabled = upstream.features.includes(CACHE_FEATURE) && Date.parse(this.#epoch.expiresAt) > this.#now();
    return { ...upstream, audience: 'serve-cache', gateway: enabled ? 'confirmed' : 'unsupported', features: enabled ? [CACHE_FEATURE] : [],
      operationEpoch: enabled ? this.#epoch : null };
  }
  /** 在首次模型装配时返回代理；未完成两跳发布、控制未决或运行身份变化均拒绝模型请求。 */
  transport(eu: string, sessionId: string, runtimeSessionId: string, client: GatewayCacheClient, coreAuthority?: CacheCoreAuthority): GatewayCacheTransport {
    const ready = (): GatewayCacheTransport => {
      if (gatewayCacheRetirement(client)) fail('receipt_expired', 410);
      this.#options.assertContinuityAvailable?.(eu, sessionId);
      if (this.#read(this.#name('guard', eu, sessionId))) fail('result_unknown');
      const id = this.#read<string>(this.#name('session', eu, sessionId));
      const link = id ? this.#link(eu, id) : undefined;
      if (!link || link.runtimeSessionId !== runtimeSessionId || link.binding.state !== 'active') fail('mapping_unavailable');
      return client.transport(coreAuthority);
    };
    return Object.freeze({ runtimeSessionId, ...(coreAuthority ? { coreProjection: true as const } : {}), originalBody: (id: string) => client.transport().originalBody(id),
      prepare: (id: string, body: string, core?: CacheCoreRequest) => { const prepared = ready().prepare(id, body, core); return { requestId: id,
        send: (signal: AbortSignal, streaming: boolean) => { ready(); return prepared.send(signal, streaming); } }; },
      finish: (id: string) => client.transport().finish(id), uncertain: (id: string) => client.transport().uncertain(id) });
  }
  async execute(eu: string, operation: Operation, input: unknown): Promise<unknown> {
    const parsed = operation === 'open' ? openSchema.parse(input) : mutationSchema.parse(input);
    const original = Buffer.from(encodeCacheControl(parsed)).toString('base64');
    const key = this.#operationKey(eu, operation, parsed.request, 'bindingId' in parsed ? cacheId(parsed.bindingId) : null);
    const stored = this.#read<StoredOperation>(key);
    if (stored?.state === 'retired') {
      if (stored.endUserId !== eu || stored.originalDigest !== createHash('sha256').update(original).digest('hex')) return fail('request_id_conflict');
      return fail('receipt_expired', 410);
    }
    let saved = stored;
    if (saved && saved.original !== original) return fail('request_id_conflict');
    if (this.#inflight.has(key)) return fail('result_unknown');
    this.#inflight.add(key);
    try {
      if (!saved) saved = await this.#prepare(eu, operation, parsed, original, key);
      return await this.#drive(key, saved, false);
    } finally { this.#inflight.delete(key); }
  }
  /** 内部起轮钩子；恢复原两跳guard，不改变任何已冻结exchange。 */
  async refreshConfiguration(eu: string, sid: string, signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    const session = this.#options.session(eu, sid), epoch = this.#epoch.id;
    const assertBoundary = () => {
      signal.throwIfAborted(); const current = this.#options.session(eu, sid);
      if (!session.configurationIdentity || current.configurationIdentity !== session.configurationIdentity ||
        current.runtimeSessionId !== session.runtimeSessionId || current.client !== session.client) fail('identity_conflict');
      if (this.#epoch.id !== epoch) fail('epoch_unavailable');
    };
    assertBoundary();
    const guard = this.#read<string>(this.#name('guard', eu, sid));
    if (guard) {
      const saved = this.#read<StoredOperation>(guard);
      if (!saved || saved.state === 'retired' || !saved.configurationRefresh || saved.operation !== 'rebind' || this.#inflight.has(guard)) return fail('result_unknown');
      this.#inflight.add(guard);
      try { await this.#drive(guard, saved, true, signal, assertBoundary); } finally { this.#inflight.delete(guard); }
      // 原回执可能属于更早配置；结清后再以当前 revision 刷新，不拿旧回执确认新 ETag。
    }
    const id = this.#read<string>(this.#name('session', eu, sid));
    if (!id) return fail('mapping_unavailable');
    const binding = await this.read(eu, id) as Binding;
    assertBoundary();
    const parsed = mutationSchema.parse({ protocol: CACHE_PROTOCOL, request: this.coldNewRequest(), bindingId: id, expectedRevision: binding.revision, sessionId: sid });
    const original = Buffer.from(encodeCacheControl(parsed)).toString('base64');
    const key = this.#operationKey(eu, 'rebind', parsed.request, id);
    this.#inflight.add(key);
    try { await this.#drive(key, await this.#prepare(eu, 'rebind', parsed, original, key, signal, assertBoundary), false, signal, assertBoundary); }
    finally { this.#inflight.delete(key); }
  }
  /** 启动恢复只消费已有配置标记，不伪造历史见证、不创建新C1，且仍经当前API认证。 */
  async recoverConfiguration(eu: string, sid: string, runtimeSessionId: string, assertOwner: () => void): Promise<void> {
    const key = this.#read<string>(this.#name('guard', eu, sid));
    if (!key) return;
    const saved = this.#read<StoredOperation>(key);
    if (!saved || saved.state === 'retired') return fail('result_unknown');
    if (!saved.configurationRefresh) return; // 普通控制留给原查询/补偿入口。
    if (saved.operation !== 'rebind' || saved.state !== 'pending' || saved.endUserId !== eu || saved.sessionId !== sid || saved.runtimeSessionId !== runtimeSessionId || this.#inflight.has(key))
      return fail('result_unknown');
    const epoch = this.#epoch.id;
    const controller = new AbortController(), timer = setTimeout(() => controller.abort(new Error('Cache configuration recovery timeout')), 30000);
    timer.unref();
    this.#inflight.add(key);
    try {
      assertOwner();
      const client = await this.#options.client(eu, runtimeSessionId);
      const assertCurrent = () => {
        controller.signal.throwIfAborted(); assertOwner();
        if (this.#epoch.id !== epoch || this.#read(this.#name('guard', eu, sid)) !== key) fail('result_unknown');
        if (client.pendingExchanges().length > 0) fail('result_unknown');
      };
      assertCurrent();
      const witness: CacheLinkWitness = { digest: this.#hash('configuration-refresh', eu, sid, runtimeSessionId),
        signal: controller.signal, assertCurrent };
      await this.#drive(key, saved, true, undefined, undefined, { client, runtimeSessionId, witness });
    } finally { clearTimeout(timer); this.#inflight.delete(key); }
  }
  #configurationWitness<T>(eu: string, sid: string, session: CacheLinkSession, signal: AbortSignal, work: (witness: CacheLinkWitness) => Promise<T>, assertBoundary?: () => void): Promise<T> {
    const epoch = this.#epoch.id;
    const assertCurrent = () => {
      signal.throwIfAborted(); this.#options.assertContinuityAvailable?.(eu, sid);
      assertBoundary?.();
      const current = this.#options.session(eu, sid);
      if (!session.configurationIdentity || current.configurationIdentity !== session.configurationIdentity ||
        current.runtimeSessionId !== session.runtimeSessionId || current.client !== session.client) fail('identity_conflict');
      if (this.#epoch.id !== epoch) fail('epoch_unavailable');
      if (current.client.pendingExchanges().length > 0) fail('result_unknown');
    };
    assertCurrent();
    return work({ digest: this.#hash('configuration-refresh', eu, sid, session.runtimeSessionId), signal, assertCurrent });
  }
  async #prepare(eu: string, operation: Operation, parsed: z.infer<typeof openSchema> | z.infer<typeof mutationSchema>, original: string, key: string, configurationSignal?: AbortSignal, assertBoundary?: () => void): Promise<SavedOperation> {
    if (parsed.request.operationEpoch !== this.#epoch.id || Date.parse(this.#epoch.expiresAt) <= this.#now()) return fail('epoch_unavailable');
    let current: Link | undefined;
    let sessionId: string;
    if ('intent' in parsed) {
      sessionId = cacheRuntimeId(parsed.sessionId);
      if (parsed.intent.kind === 'resume' || parsed.intent.kind === 'fork') current = this.#ticket(eu, parsed.intent.kind === 'resume' ? parsed.intent.ticket : parsed.intent.parentTicket);
    } else {
      current = this.#link(eu, parsed.bindingId); cacheRevision(parsed.expectedRevision);
      if (current.binding.revision !== parsed.expectedRevision) return fail('stale_revision');
      if (operation === 'rebind') {
        if (parsed.sessionId === undefined || parsed.ticket !== undefined || parsed.reason !== undefined) return fail('invalid_request', 400);
        sessionId = cacheRuntimeId(parsed.sessionId);
      } else {
        if (parsed.ticket === undefined || parsed.sessionId !== undefined || (operation === 'rotate' ? parsed.reason !== 'manual' : parsed.reason !== undefined)) return fail('invalid_request', 400);
        if (this.#ticket(eu, parsed.ticket).binding.bindingId !== current.binding.bindingId) return fail('mapping_unavailable', 404);
        sessionId = current.sessionId;
      }
    }
    this.#options.assertContinuityAvailable?.(eu, sessionId);
    if (operation === 'open') this.#options.assertContinuityOpen?.(eu, sessionId);
    const session = this.#options.session(eu, sessionId);
    // C1 不授予跨运行身份；只有独立可信来源协议可以完成此迁移。
    if (current && current.runtimeSessionId !== session.runtimeSessionId) return fail('unsupported_capability');
    const guardKey = this.#name('guard', eu, sessionId);
    if (this.#read(guardKey)) return fail('result_unknown');
    const prepare = async (witness: CacheLinkWitness) => {
      if (configurationSignal !== undefined && session.client.pendingControl()) fail('result_unknown');
      const caps = await session.client.capabilities(); witness.assertCurrent();
      if (configurationSignal !== undefined && session.client.pendingControl()) fail('result_unknown');
      if (!caps.features.includes(CACHE_FEATURE) || !caps.operationEpoch) return fail('unsupported_capability');
      // 能力读的 await 期间其它请求可能占用同一运行，落盘前再次检查。
      if (this.#read(guardKey)) return fail('result_unknown');
      const request = { operationEpoch: caps.operationEpoch.id, requestId: randomUUID() };
      const body = 'intent' in parsed
        ? { protocol: CACHE_PROTOCOL, request, runtimeSessionId: session.runtimeSessionId,
          intent: parsed.intent.kind === 'resume' ? { kind: 'resume', ticket: current!.upstreamTicket } : parsed.intent.kind === 'fork' ? { kind: 'fork', parentTicket: current!.upstreamTicket } : parsed.intent,
          requiredFeature: CACHE_FEATURE }
        : { protocol: CACHE_PROTOCOL, request, bindingId: current!.upstreamBindingId, expectedRevision: parsed.expectedRevision,
          ...(operation === 'rebind' ? { sessionId: session.runtimeSessionId } : { ticket: current!.upstreamTicket }), ...(operation === 'rotate' ? { reason: 'manual' } : {}) };
      const saved: SavedOperation = { endUserId: eu, sessionId, runtimeSessionId: session.runtimeSessionId, operation, original,
        upstream: Buffer.from(encodeCacheControl(body)).toString('base64'), digest: witness.digest,
        localBindingId: operation === 'open' ? randomUUID() : current!.binding.bindingId,
        logicalRef: 'intent' in parsed && parsed.intent.kind !== 'resume' ? randomUUID() : current!.binding.logicalRef,
        localTicket: randomTicket(), compensationRequest: { operationEpoch: request.operationEpoch, requestId: randomUUID() }, state: 'pending', receipt: null, compensation: null,
        ...(configurationSignal === undefined ? {} : { configurationRefresh: true as const }) };
      return this.#reserveOperation(key, saved, guardKey);
    };
    return configurationSignal === undefined ? session.withWitness(prepare) : this.#configurationWitness(eu, sessionId, session, configurationSignal, prepare, assertBoundary);
  }
  #reserveOperation(key: string, saved: SavedOperation, guardKey?: string): SavedOperation {
    const eu = saved.endUserId;
    const keys = [this.#name('binding', eu, saved.localBindingId), this.#name('session', eu, saved.sessionId), this.#name('ticket', eu, saved.localTicket)];
    const source = this.#options.continuityReservation?.(eu, saved.sessionId); if (source) keys.push(source);
    if (saved.reservedKeys && JSON.stringify(saved.reservedKeys) !== JSON.stringify(keys)) fail('result_unknown');
    const next = { ...saved, reservedKeys: keys };
    this.#write([[key, next], ...(guardKey ? [[guardKey, key] as const] : [])], [], keys.map(reservedKey => ({ key: reservedKey, owner: key, required: saved.reservedKeys !== undefined })));
    return next;
  }
  async #drive(key: string, saved: SavedOperation, query: boolean, configurationSignal?: AbortSignal, assertBoundary?: () => void,
    recovery?: { client: GatewayCacheClient; runtimeSessionId: string; witness: CacheLinkWitness }): Promise<unknown> {
    const original = decodeCacheControl(Buffer.from(saved.upstream, 'base64')) as { request: GatewayCacheRequestIdentity; bindingId?: string };
    if (saved.state === 'completed' || saved.state === 'cancelled') {
      // 旧回执重放仍经当前 API 认证；不能用本地历史票据绕过应用撤销。
      const client = await this.#options.client(saved.endUserId, saved.runtimeSessionId);
      if (gatewayCacheRetirement(client)) return fail('receipt_expired', 410);
      await client.queryOperation({ operation: saved.operation, request: original.request, bindingId: original.bindingId ?? null });
      if (saved.state === 'cancelled') return fail('mapping_unavailable');
      return saved.receipt;
    }
    // 旧未决记录先补足本地收尾空间，不能先调用API再发现回执无处持久化。
    saved = this.#reserveOperation(key, saved);
    const session = recovery ?? this.#options.session(saved.endUserId, saved.sessionId);
    if (session.runtimeSessionId !== saved.runtimeSessionId) return fail('result_unknown');
    const drive = async (witness: CacheLinkWitness) => {
      const remote = saved.state === 'compensating'
        ? await session.client.queryOperation({ operation: saved.operation, request: original.request, bindingId: original.bindingId ?? null }, witness.signal)
        : await (async () => {
          const prepared = session.client.prepareControlBytes(saved.operation, Buffer.from(saved.upstream, 'base64'));
          if (!query) return prepared.execute(witness.signal);
          try { return await prepared.query(witness.signal); }
          catch (error) {
            // 原键无回执不等于已受理；仅元数据 C1 用原字节幂等重放，绝不换身份或改前提。
            if (!saved.configurationRefresh || !(error instanceof GatewayCacheError) || error.code !== 'result_unknown' || error.status !== 503) throw error;
            witness.assertCurrent(); return prepared.execute(witness.signal);
          }
        })();
      // 源发生变化后不能发布旧绑定。补偿使用出网前固定的第二个请求身份，失联仍保留。
      // 配置刷新不改原来源；取消只保留原guard待查同回执，不补偿关闭现有会话。
      if (saved.configurationRefresh) witness.assertCurrent();
      let cancelled = saved.state === 'compensating' || witness.digest !== saved.digest;
      try { witness.assertCurrent(); } catch { cancelled = true; }
      if (cancelled) {
        if (remote.binding.state === 'active') {
          if (saved.compensation === null) {
            if (!remote.ticket) return fail('result_unknown');
            const caps = await session.client.capabilities();
            if (!caps.operationEpoch) return fail('epoch_unavailable');
            saved = { ...saved, compensationRequest: { ...saved.compensationRequest, operationEpoch: caps.operationEpoch.id } };
            saved = { ...saved, state: 'compensating', compensation: Buffer.from(encodeCacheControl({ protocol: CACHE_PROTOCOL,
              request: saved.compensationRequest, bindingId: remote.binding.bindingId, expectedRevision: remote.binding.revision, ticket: remote.ticket })).toString('base64') };
            this.#write([[key, saved]]);
          }
          await session.client.prepareControlBytes('close', Buffer.from(saved.compensation!, 'base64')).execute();
        }
        this.#write([[key, { ...saved, state: 'cancelled' }]], [this.#name('guard', saved.endUserId, saved.sessionId),
          ...unusedVaultReservations(this.#options.store, key, saved.reservedKeys!)]);
        return fail('mapping_unavailable');
      }
      if (remote.binding.state === 'active' && !saved.configurationRefresh) {
        if (recovery) return fail('result_unknown');
        await this.#options.confirmContinuity?.(saved.endUserId, saved.sessionId, session as CacheLinkSession, witness, remote.binding);
        witness.assertCurrent();
      }
      const request = (decodeCacheControl(Buffer.from(saved.original, 'base64')) as { request: GatewayCacheRequestIdentity }).request;
      const binding = this.#publicBinding(remote.binding, saved.localBindingId, saved.logicalRef);
      const ticket = remote.ticket === null ? null : saved.localTicket;
      const receipt: Record<string, unknown> = saved.operation === 'open'
        ? { protocol: CACHE_PROTOCOL, request, binding, ticket, ticketExpiresAt: remote.ticketExpiresAt, relation: (remote as GatewayCacheOpenResponse).relation }
        : { protocol: CACHE_PROTOCOL, request, operation: saved.operation, semanticDigest: { algorithm: 'hmac-sha256', keyId: this.#options.key.id,
          digest: this.#semantic(saved.operation, saved.original) }, state: 'completed', binding, ticket, ticketExpiresAt: remote.ticketExpiresAt };
      const link: Link = { endUserId: saved.endUserId, sessionId: saved.sessionId, runtimeSessionId: saved.runtimeSessionId, binding,
        upstreamBindingId: remote.binding.bindingId, upstreamTicket: remote.ticket, localTicket: ticket };
      const writes: [string, unknown][] = [[key, { ...saved, state: 'completed', receipt }],
        [this.#name('binding', saved.endUserId, saved.localBindingId), link], [this.#name('session', saved.endUserId, saved.sessionId), saved.localBindingId]];
      if (ticket !== null) writes.push([this.#name('ticket', saved.endUserId, ticket), saved.localBindingId]);
      this.#write(writes, [this.#name('guard', saved.endUserId, saved.sessionId),
        ...unusedVaultReservations(this.#options.store, key, saved.reservedKeys!)]); return receipt;
    };
    if (recovery) {
      if (!saved.configurationRefresh) return fail('result_unknown');
      return drive(recovery.witness);
    }
    const current = session as CacheLinkSession;
    return saved.configurationRefresh ? this.#configurationWitness(saved.endUserId, saved.sessionId, current,
      configurationSignal ?? AbortSignal.timeout(30000), drive, assertBoundary) : current.withWitness(drive);
  }
  async query(eu: string, operation: Operation, request: GatewayCacheRequestIdentity, bindingId: string | null): Promise<unknown> {
    if (bindingId !== null) cacheId(bindingId);
    const key = this.#operationKey(eu, operation, CacheRequestIdentitySchema.parse(request), bindingId);
    const saved = this.#read<StoredOperation>(key);
    if (saved?.state === 'retired') return fail('receipt_expired', 410);
    if (operation === 'open' ? bindingId !== null : bindingId === null) return fail('invalid_request', 400);
    if (!saved) return request.operationEpoch === this.#epoch.id && Date.parse(this.#epoch.expiresAt) > this.#now() ? fail('result_unknown', 503) : fail('receipt_expired', 410);
    if (operation !== 'open' && bindingId !== saved.localBindingId) return fail('receipt_expired', 410);
    if (this.#inflight.has(key)) return fail('result_unknown');
    this.#inflight.add(key); try { return await this.#drive(key, saved, true); } finally { this.#inflight.delete(key); }
  }
  async read(eu: string, id: string): Promise<unknown> {
    const link = this.#link(eu, id), client = await this.#options.client(eu, link.runtimeSessionId);
    if (gatewayCacheRetirement(client)) return fail('receipt_expired', 410);
    const remote = await client.readBinding(link.upstreamBindingId);
    const binding = this.#publicBinding(remote, id, link.binding.logicalRef);
    if (!this.#read(this.#name('guard', eu, link.sessionId))) {
      const current = this.#link(eu, id);
      if (current.binding.revision === link.binding.revision && BigInt(binding.revision) >= BigInt(link.binding.revision) &&
        Buffer.compare(Buffer.from(encodeCacheControl(binding)), Buffer.from(encodeCacheControl(current.binding))) !== 0)
        this.#write([[this.#name('binding', eu, id), { ...current, binding }]]);
    }
    return binding;
  }
  /** 宿主私有来源协调读，绝不作为终端自报的授权入口。 */
  continuityBinding(eu: string, sid: string): { bindingId: string; runtimeSessionId: string } | undefined {
    const id = this.#read<string>(this.#name('session', eu, sid));
    if (!id) return undefined;
    const link = this.#link(eu, id); return { bindingId: link.upstreamBindingId, runtimeSessionId: link.runtimeSessionId };
  }
  /** 尚未登记来源也可能已受理C1；删除不得越过首次open的原未决guard。 */
  assertHistoryIdle(endUserKey: string, sid: string): void {
    for (const record of this.#options.store.snapshot().records) {
      if (!record.key.startsWith('guard/')) continue;
      const operationKey = readVaultValue(record.key, record.value);
      if (typeof operationKey !== 'string') fail('result_unknown');
      const saved = this.#read<SavedOperation>(operationKey as string);
      if (!saved) fail('result_unknown');
      if (saved!.sessionId === sid && endUserKeyOf(saved!.endUserId) === endUserKey) fail('result_unknown');
    }
  }
  assertContinuityIdle(eu: string, sid: string): void {
    if (this.#read(this.#name('guard', eu, sid))) fail('result_unknown');
  }
  /** 一次扫描建立按运行索引的压缩计划；真实inflight/poison仍在每次提交前核查。 */
  retirementCensus(records: readonly (readonly [string, unknown])[]): (eu: string, runtime: string) => readonly (readonly [string, unknown])[] | null {
    const identity = (eu: string, runtime: string) => JSON.stringify([eu, runtime]);
    const blocked = new Set<string>(), writes = new Map<string, (readonly [string, unknown])[]>();
    const guards = new Set(records.filter(([key]) => key.startsWith('guard/')).map(([key]) => key));
    for (const [key, raw] of records) {
      if (isVaultReservation(raw)) continue;
      if (key.startsWith('operation/')) {
        const saved = raw as StoredOperation, id = identity(saved.endUserId, saved.runtimeSessionId);
        if (saved.state === 'retired') continue;
        if ((saved.state !== 'completed' && saved.state !== 'cancelled') || guards.has(this.#name('guard', saved.endUserId, saved.sessionId))) {
          blocked.add(id); continue;
        }
        const rows = writes.get(id) ?? [];
        rows.push([key, { state: 'retired', endUserId: saved.endUserId, runtimeSessionId: saved.runtimeSessionId,
          localBindingId: saved.localBindingId, originalDigest: createHash('sha256').update(saved.original).digest('hex') } satisfies RetiredOperation]);
        writes.set(id, rows);
      } else if (key.startsWith('binding/')) {
        const link = raw as Link;
        if (link.binding.state !== 'closed') blocked.add(identity(link.endUserId, link.runtimeSessionId));
      }
    }
    return (eu, runtime) => this.#poisoned || this.#inflight.size > 0 || blocked.has(identity(eu, runtime)) ? null : writes.get(identity(eu, runtime)) ?? [];
  }
  /** 接续在出网前固定新本地身份，并占住实际未来binding/ticket两行。 */
  continuitySlots(eu: string, owner: string, alias: { bindingId: string; ticket: string }): readonly VaultReservation[] {
    cacheId(alias.bindingId); cacheTicket(alias.ticket);
    return [this.#name('binding', eu, alias.bindingId), this.#name('ticket', eu, alias.ticket)].map(key => ({ key, owner }));
  }
  /** 新API结果与来源状态在同一宿主库发布；原别名关闭，新运行领取独立本地票据。 */
  publishContinuity(eu: string, sid: string, receipt: ContinuityReceipt, sourceWrites: readonly (readonly [string, unknown])[], alias?: { bindingId: string; ticket: string }): void {
    this.assertContinuityIdle(eu, sid);
    let id = this.#read<string>(this.#name('session', eu, sid));
    if (receipt.operation === 'delete') {
      // 同一档案的历代冷关系都要删；不能把旧来源墓碑应用到当前新绑定。
      const matches = this.#options.store.snapshot().records.filter(row => row.key.startsWith('binding/')).flatMap(row => {
        const value = readVaultValue(row.key, row.value);
        if (isVaultReservation(value)) return [];
        const link = value as Link;
        return link.endUserId === eu && link.sessionId === sid && link.upstreamBindingId === receipt.binding.bindingId ? [link.binding.bindingId] : [];
      });
      if (matches.length !== 1) return fail('mapping_unavailable');
      id = matches[0];
    }
    if (!id) return fail('mapping_unavailable');
    const old = this.#link(eu, id);
    const closed: Link = { ...old, binding: { ...old.binding, revision: String(BigInt(old.binding.revision) + 1n), state: 'closed' }, localTicket: null, upstreamTicket: null };
    const writes: (readonly [string, unknown])[] = [...sourceWrites, [this.#name('binding', eu, id), closed]];
    if (receipt.operation === 'resume') {
      if (!receipt.ticket || receipt.binding.state !== 'active' || receipt.binding.bindingId === old.upstreamBindingId) fail('identity_conflict');
      if (!alias) fail('result_unknown');
      const newId = cacheId(alias!.bindingId), ticket = cacheTicket(alias!.ticket);
      const next: Link = { endUserId: eu, sessionId: sid, runtimeSessionId: receipt.source.runtimeSessionId,
        binding: this.#publicBinding(receipt.binding, newId, old.binding.logicalRef), upstreamBindingId: receipt.binding.bindingId,
        upstreamTicket: receipt.ticket, localTicket: ticket };
      writes.push([this.#name('binding', eu, newId), next], [this.#name('session', eu, sid), newId], [this.#name('ticket', eu, ticket), newId]);
    } else if (receipt.operation !== 'delete' || receipt.source.state !== 'deleted' || receipt.binding.bindingId !== old.upstreamBindingId) fail('identity_conflict');
    this.#write(writes);
  }
  async diagnostics(eu: string, id: string, after: string | null, limit: number): Promise<unknown> {
    const link = this.#link(eu, id), client = await this.#options.client(eu, link.runtimeSessionId);
    if (gatewayCacheRetirement(client)) return fail('receipt_expired', 410);
    return client.diagnostics(link.upstreamBindingId, after, limit);
  }
  /** 独立新诊断入口；本地alias按当前用户解析，API绑定/票据不返给终端。 */
  async coreDiagnostics(eu: string, id: string, after: string | null, limit: number): Promise<unknown> {
    const link = this.#link(eu, id), client = await this.#options.client(eu, link.runtimeSessionId);
    if (gatewayCacheRetirement(client)) return fail('receipt_expired', 410);
    return client.coreDiagnostics(link.upstreamBindingId, after, limit);
  }
}
