/** SDK2 包内独立事件连接；不改 SDK1、不重试、不保存游标，也不发送档案 ACK。 */
import { copyEventContext, Sdk2StreamDecodeError, type Sdk2EventContext, type Sdk2EventFrame } from './event-frame.js';
import { StrictSdk2EventStream } from './strict-event-stream.js';

export interface Sdk2EventConnectionOptions {
  /** 可信宿主根地址；无默认值，不把旧 /v1 网关路径当作扩展地址。 */
  readonly baseUrl: string;
  /** 沿宿主现有 Bearer 认证，仅用于当前请求。 */
  readonly apiKey: string;
  /** 必须同步；宿主可在撤权时抛错。返回上下文不授予权限。 */
  readonly readContext: () => Sdk2EventContext;
  readonly controlBytes: number;
  readonly lastEventId?: string;
  readonly signal?: AbortSignal;
  readonly fetchImpl?: typeof fetch;
}
type LocalCode = 'invalid_options' | 'scope_unavailable' | 'context_changed' | 'reentrant' | 'aborted' |
  'network_error' | 'invalid_response' | 'consumer_failed';
/** 本地传输错误，不扩充 wire ErrorResponse 枚举，不保存配置、原因对象或响应正文。 */
export class Sdk2EventConnectionError extends Error {
  constructor(readonly code: LocalCode) { super(`SDK2 event connection: ${code}`); this.name = 'Sdk2EventConnectionError'; }
}
type RetryAction = 'none' | 'same-request' | 'query-status' | 'rebind';
/** 仅携经具名合同验证的非敏感诊断；永不回显服务端 message。 */
export class Sdk2EventHttpError extends Error {
  constructor(readonly status: number, readonly code: string, readonly retryAction: RetryAction,
    readonly requestId: string | null, readonly retryAfterMs: number | null) {
    super(`SDK2 event HTTP ${status}: ${code}`); this.name = 'Sdk2EventHttpError';
  }
}
function fail(code: LocalCode): never { throw new Sdk2EventConnectionError(code); }
type ByteRead = Awaited<ReturnType<ReadableStreamDefaultReader<Uint8Array>['read']>>;
const ID = /^[A-Za-z0-9][A-Za-z0-9._~-]{0,127}$/;
const encoder = new TextEncoder();
const ERROR_RULES: Readonly<Record<string, readonly [number, readonly RetryAction[]]>> = {
  invalid_request: [400, ['none']], protocol_version_mismatch: [400, ['none']], unauthorized: [401, ['none']], forbidden: [403, ['none']],
  unsupported_capability: [422, ['none']], capability_unconfirmed: [503, ['query-status']], binding_conflict: [409, ['none', 'rebind']],
  stale_generation: [409, ['none', 'rebind']], archive_gap: [409, ['none']], source_unavailable: [503, ['same-request', 'query-status']],
  invalid_coverage: [422, ['none']], capacity_exceeded: [429, ['same-request', 'query-status']], request_id_conflict: [409, ['none']],
  request_expired: [410, ['none']], receipt_expired: [410, ['none']], payload_too_large: [413, ['none']], material_rejected: [422, ['none']],
  binding_closed: [409, ['none']], internal_error: [500, ['query-status']], epoch_unavailable: [503, ['same-request']],
  stream_cursor_unknown: [409, ['none']], stream_cursor_expired: [410, ['none']], stream_gap: [409, ['none']],
};
interface ActiveConnection { readonly controller: AbortController; poisoned: boolean }
const active = new WeakMap<object, ActiveConnection>();
async function cancellable<T>(work: Promise<T>, signal: AbortSignal): Promise<T> {
  let listener: (() => void) | undefined;
  const interrupted = new Promise<never>((_resolve, reject) => {
    listener = () => reject(new Sdk2EventConnectionError('aborted'));
    signal.addEventListener('abort', listener, { once: true });
    if (signal.aborted) listener();
  });
  try { return await Promise.race([work, interrupted]); }
  finally { if (listener) signal.removeEventListener('abort', listener); }
}
function discardResponse(response: Response | undefined): void {
  try { void response?.body?.cancel().catch(() => undefined); }
  catch { /* 未接管的迟到响应只清理本次资源，不覆盖取消结果。 */ }
}
const NativePromise = Promise;
const nativeThen = Promise.prototype.then;
const nativeSpecies = Object.getOwnPropertyDescriptor(Promise, Symbol.species)!;

/** 不执行实例 then、constructor 访问器或自定义 species；不可安全观察时由原宿主持有拒绝责任。 */
function observeRejectedPromiseIfSafe(value: object): void {
  try {
    if (Object.getPrototypeOf(value) !== NativePromise.prototype) return;
    const constructor = Object.getOwnPropertyDescriptor(value, 'constructor') ??
      Object.getOwnPropertyDescriptor(NativePromise.prototype, 'constructor');
    const species = Object.getOwnPropertyDescriptor(NativePromise, Symbol.species);
    if (!constructor || !Object.hasOwn(constructor, 'value') || constructor.value !== NativePromise ||
      !species || Object.hasOwn(species, 'value') || species.get !== nativeSpecies.get || species.set !== nativeSpecies.set) return;
    // 原生 then 自行校验真实 Promise 品牌；不借 DOM 层引入 Node/kernel。
    Reflect.apply(nativeThen, value, [undefined, () => undefined]);
  } catch { /* 非原生品牌或不可安全观察，不赋予任何完成事实。 */ }
}

/** 描述符快照拒绝访问器、未知字段与异步返回，避免读取一次校验、再读取另一个值。 */
function data(value: unknown, required: readonly string[], optional: readonly string[] = []): Record<string, unknown> {
  if (!value || typeof value !== 'object') fail('invalid_options');
  const prototype = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null) {
    // 错把 async 函数当同步回调时，仅观察原生 Promise 的拒绝；不执行任意 thenable。
    observeRejectedPromiseIfSafe(value);
    fail('invalid_options');
  }
  const descriptors = Object.getOwnPropertyDescriptors(value), result: Record<string, unknown> = Object.create(null);
  for (const key of Reflect.ownKeys(descriptors)) {
    if (typeof key !== 'string' || (!required.includes(key) && !optional.includes(key))) fail('invalid_options');
    const descriptor = descriptors[key]!;
    if (!descriptor.enumerable || !('value' in descriptor)) fail('invalid_options');
    result[key] = descriptor.value;
  }
  if (required.some(key => !Object.hasOwn(result, key))) fail('invalid_options');
  return result;
}
function current(read: () => Sdk2EventContext): Sdk2EventContext {
  try {
    const raw = data(read(), ['bindingId', 'applicationScopeId', 'endUserId', 'generations']);
    const generations = data(raw.generations, ['historyEpoch', 'deletionGeneration', 'projectionRevision']);
    return copyEventContext({ ...raw, generations } as unknown as Sdk2EventContext);
  } catch { return fail('scope_unavailable'); }
}
function sameContext(left: Sdk2EventContext, right: Sdk2EventContext): boolean {
  return left.bindingId === right.bindingId && left.applicationScopeId === right.applicationScopeId && left.endUserId === right.endUserId &&
    left.generations.historyEpoch === right.generations.historyEpoch && left.generations.deletionGeneration === right.generations.deletionGeneration &&
    left.generations.projectionRevision === right.generations.projectionRevision;
}
function endpoint(value: unknown): string {
  if (typeof value !== 'string' || value !== value.trim() || /[?#@\\]/.test(value)) fail('invalid_options');
  try {
    const url = new URL(value as string);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash || url.pathname !== '/') fail('invalid_options');
    return url.origin;
  } catch { return fail('invalid_options'); }
}
function contentType(value: string | null, type: 'json' | 'sse'): boolean {
  return value !== null && (type === 'json' ? /^application\/json(?:\s*;\s*charset\s*=\s*(?:utf-8|"utf-8"))?\s*$/i :
    /^text\/event-stream\s*;\s*charset\s*=\s*(?:utf-8|"utf-8")\s*$/i).test(value);
}
/** 有界独立 ErrorResponse；不使用旧 SDK1 的尽力解析或自动 GET 重试。 */
function errorResponse(bytes: Uint8Array, status: number, apiKey: string): Sdk2EventHttpError {
  try {
    const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    const parsed: unknown = JSON.parse(text);
    // 本信封只有标量；排序重编码保留重复键／替代表达的反证，无任意嵌套遍历。
    const item = data(parsed, ['protocol', 'requestId', 'code', 'status', 'message', 'retryAction'], ['retryAfterMs']);
    if (Object.values(item).some(value => !['string', 'number'].includes(typeof value))) fail('invalid_response');
    const canonical = '{' + Object.keys(item).sort().map(key => `${JSON.stringify(key)}:${JSON.stringify(item[key])}`).join(',') + '}';
    if (canonical !== text || item.protocol !== 'sdk2-ext-v1' || typeof item.code !== 'string' || !Object.hasOwn(ERROR_RULES, item.code) ||
      typeof item.requestId !== 'string' || !ID.test(item.requestId) || typeof item.message !== 'string' || [...item.message].length < 1 ||
      [...item.message].length > 1024 || new TextDecoder('utf-8', { fatal: true }).decode(encoder.encode(item.message)) !== item.message) fail('invalid_response');
    const [expected, retries] = ERROR_RULES[item.code as string]!;
    if (item.status !== status || status !== expected || typeof item.retryAction !== 'string' || !retries.includes(item.retryAction as RetryAction)) fail('invalid_response');
    const after = item.retryAfterMs;
    if (after !== undefined && (typeof after !== 'number' || !Number.isSafeInteger(after) || after < 1 || after > 60000)) fail('invalid_response');
    return new Sdk2EventHttpError(status, item.code as string, item.retryAction as RetryAction,
      (item.requestId as string).includes(apiKey) ? null : item.requestId as string, after === undefined ? null : after as number);
  } catch { return fail('invalid_response'); }
}

/**
 * 单次连接：一次 GET、一个 reader、逐帧等待消费。返回仅表示流已完整结束，不是 ACK。
 * 调用者控制重连及已消费游标。同一 options 在连接存续期间不能再次发起连接。
 * 取消可停止本函数资源；已开始的业务消费仍须自行响应 signal，其晚完成不再推进此连接。
 */
export async function consumeSdk2EventConnection(options: Sdk2EventConnectionOptions,
  consume: (frame: Sdk2EventFrame) => void | Promise<void>): Promise<void> {
  if (!options || typeof options !== 'object') fail('invalid_options');
  const previous = active.get(options);
  if (previous) { previous.poisoned = true; previous.controller.abort(); return fail('reentrant'); }
  const run: ActiveConnection = { controller: new AbortController(), poisoned: false };
  active.set(options, run);
  let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  let unclaimedResponse: Response | undefined;
  let signal: AbortSignal | undefined;
  let onAbort: (() => void) | undefined;
  try {
    const raw = data(options, ['baseUrl', 'apiKey', 'readContext', 'controlBytes'], ['lastEventId', 'signal', 'fetchImpl']);
    const origin = endpoint(raw.baseUrl);
    if (typeof raw.apiKey !== 'string' || !/^[\x21-\x7e]+$/.test(raw.apiKey) || typeof raw.readContext !== 'function' || typeof consume !== 'function' ||
      typeof raw.controlBytes !== 'number' || !Number.isSafeInteger(raw.controlBytes) || raw.controlBytes < 1024 || raw.controlBytes > 262144) fail('invalid_options');
    const apiKey = raw.apiKey as string, readContext = raw.readContext as () => Sdk2EventContext, controlBytes = raw.controlBytes as number;
    if (raw.signal !== undefined && !(raw.signal instanceof AbortSignal)) fail('invalid_options');
    signal = raw.signal as AbortSignal | undefined;
    onAbort = () => run.controller.abort();
    signal?.addEventListener('abort', onAbort, { once: true });
    if (signal?.aborted) run.controller.abort();
    const checkState = () => { if (run.poisoned) fail('reentrant'); if (run.controller.signal.aborted) fail('aborted'); };
    checkState();
    const initial = current(readContext);
    const check = () => {
      checkState(); const observed = current(readContext); checkState();
      if (!sameContext(initial, observed)) fail('context_changed');
    };
    check();
    const headers = new Headers({ accept: 'text/event-stream', authorization: `Bearer ${apiKey}` });
    if (raw.lastEventId !== undefined) {
      if (typeof raw.lastEventId !== 'string' || !ID.test(raw.lastEventId)) fail('invalid_options');
      headers.set('Last-Event-ID', raw.lastEventId as string);
    }
    const fetchImpl = raw.fetchImpl === undefined ? globalThis.fetch : raw.fetchImpl;
    if (typeof fetchImpl !== 'function') fail('invalid_options');
    const path = `/v3/sdk2/bindings/${encodeURIComponent(initial.bindingId)}/events?protocol=sdk2-ext-v1`;
    let response: Response;
    try {
      const pending = (fetchImpl as typeof fetch)(origin + path, { method: 'GET', headers, signal: run.controller.signal,
        redirect: 'error', credentials: 'omit', cache: 'no-store' });
      void pending.then(value => {
        if (run.controller.signal.aborted) discardResponse(value);
        else unclaimedResponse = value;
      }, () => undefined);
      response = await cancellable(pending, run.controller.signal);
    } catch { checkState(); return fail('network_error'); }
    // 取得资源后先持有其 reader，再验当前范围；失败路径负责关闭本函数拿到的流。
    if (response.body) reader = response.body.getReader();
    unclaimedResponse = undefined;
    check();
    if (response.redirected || !reader) fail('invalid_response');
    if (response.status !== 200) {
      if (!contentType(response.headers.get('content-type'), 'json')) fail('invalid_response');
      const bytes = new Uint8Array(controlBytes); let used = 0;
      for (;;) {
        check(); let part: ByteRead;
        try { part = await cancellable(reader.read(), run.controller.signal); } catch { checkState(); return fail('network_error'); }
        check(); if (part.done) break;
        if (part.value.byteLength > bytes.length - used) fail('invalid_response');
        bytes.set(part.value, used); used += part.value.byteLength;
      }
      throw errorResponse(bytes.subarray(0, used), response.status, apiKey);
    }
    if (!contentType(response.headers.get('content-type'), 'sse') || response.headers.get('cache-control')?.toLowerCase() !== 'no-store') fail('invalid_response');
    const parser = new StrictSdk2EventStream(initial);
    for (;;) {
      check(); let part: ByteRead;
      try { part = await cancellable(reader.read(), run.controller.signal); } catch { checkState(); return fail('network_error'); }
      check();
      if (part.done) { parser.finish(); check(); return; }
      let abortListener: (() => void) | undefined;
      const aborted = new Promise<never>((_resolve, reject) => {
        abortListener = () => reject(new Sdk2EventConnectionError(run.poisoned ? 'reentrant' : 'aborted'));
        run.controller.signal.addEventListener('abort', abortListener, { once: true });
        if (run.controller.signal.aborted) abortListener();
      });
      try {
        await Promise.race([parser.feed(part.value, async frame => {
          check();
          try { await consume(frame); } catch { return fail('consumer_failed'); }
          check();
        }), aborted]);
      } finally { if (abortListener) run.controller.signal.removeEventListener('abort', abortListener); }
      check();
    }
  } catch (error) {
    if (run.poisoned) throw new Sdk2EventConnectionError('reentrant');
    if (error instanceof Sdk2EventConnectionError || error instanceof Sdk2EventHttpError || error instanceof Sdk2StreamDecodeError) throw error;
    throw new Sdk2EventConnectionError('invalid_response');
  } finally {
    run.controller.abort();
    if (signal && onAbort) signal.removeEventListener('abort', onAbort);
    // cancel可能由自定义传输提供且永不返回；触发关闭并观察拒绝，不让其阻塞取消完成。
    try { void reader?.cancel().catch(() => undefined); } catch { /* 已被本连接中止的 reader 不改写原错误。 */ }
    try { reader?.releaseLock(); } catch { /* 仅释放本函数取得的 reader。 */ }
    discardResponse(unclaimedResponse);
    active.delete(options);
  }
}
