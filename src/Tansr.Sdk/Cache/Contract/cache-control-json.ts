/** 仅新控制 JSON。来源 tansr-api src/sdk2/cache/control-json.ts，原件 SHA256 f10be8d619c120664e2315fbc3fd3d10ca0feaf59f889d2b80b69fef243bcfbc；不得用来重编码原 TWP。 */
import { types } from 'node:util';

export const CACHE_CONTROL_MAX_BYTES = 65536;
const CONTROL_BYTES = CACHE_CONTROL_MAX_BYTES;
const typedArrayPrototype = Object.getPrototypeOf(Uint8Array.prototype) as object;
const byteLengthOf = Object.getOwnPropertyDescriptor(typedArrayPrototype, 'byteLength')!.get!;
const bufferOf = Object.getOwnPropertyDescriptor(typedArrayPrototype, 'buffer')!.get!;
export interface CacheControlOptions { readonly maxBytes?: number }

function invalid(reason: string): never { throw new TypeError(`SDK2 cache control: ${reason}`); }
function limit(options: CacheControlOptions): number {
  const input = object(options); keys(input, [], ['maxBytes']);
  const value = input.maxBytes ?? CONTROL_BYTES;
  if (typeof value !== 'number') invalid('invalid byte limit');
  if (!Number.isSafeInteger(value) || value < 1 || value > CONTROL_BYTES) invalid('invalid byte limit');
  return value;
}
function unicode(value: string): void {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (!(next >= 0xdc00 && next <= 0xdfff)) invalid('unpaired surrogate');
    } else if (code >= 0xdc00 && code <= 0xdfff) invalid('unpaired surrogate');
  }
}
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || types.isProxy(value) || Array.isArray(value) ||
    ![Object.prototype, null].includes(Object.getPrototypeOf(value))) invalid('expected plain object');
  const snapshot: Record<string, unknown> = Object.create(null);
  for (const key of Reflect.ownKeys(value)) {
    if (typeof key !== 'string') invalid('symbol key');
    const descriptor = Object.getOwnPropertyDescriptor(value, key)!;
    if (!Object.hasOwn(descriptor, 'value') || !descriptor.enumerable) invalid('accessor or hidden property');
    snapshot[key] = descriptor.value;
  }
  return snapshot;
}
function array(value: unknown, maximum: number): unknown[] {
  if (!value || typeof value !== 'object' || types.isProxy(value) || !Array.isArray(value)) invalid('expected dense array');
  const length = Object.getOwnPropertyDescriptor(value, 'length')!.value as number;
  if (length > maximum) invalid('array limit');
  const keys = Reflect.ownKeys(value);
  if (keys.length !== length + 1 || keys.some(key => typeof key !== 'string')) invalid('extra or missing array property');
  const result: unknown[] = [];
  for (let index = 0; index < length; index++) {
    const descriptor = Object.getOwnPropertyDescriptor(value, String(index));
    if (!descriptor || !Object.hasOwn(descriptor, 'value') || !descriptor.enumerable) invalid('sparse array or accessor');
    result.push(descriptor.value);
  }
  return result;
}
export function snapshotCacheBytes(value: unknown, maximum: number): Uint8Array {
  cacheByteLength(value, maximum);
  // TypedArray 构造复制内部元素，不调用子类迭代器或自报 byteLength。
  return new Uint8Array(value as Uint8Array);
}
export function cacheByteLength(value: unknown, maximum: number): number {
  if (!value || typeof value !== 'object' || types.isProxy(value) || !types.isUint8Array(value)) invalid('expected bytes');
  if (types.isSharedArrayBuffer(bufferOf.call(value))) invalid('shared byte buffer');
  const length = byteLengthOf.call(value) as number;
  if (length > maximum) invalid('byte limit');
  return length;
}

/** 独立扩展的控制元数据编码。原 IR/附件正文不得进入此规范化器。 */
export function encodeCacheControl(value: unknown, options: CacheControlOptions = {}): Uint8Array {
  const maximum = limit(options);
  const parts: string[] = [];
  const ancestors = new Set<object>();
  let used = 0; let nodes = 0;
  const append = (part: string): void => {
    used += Buffer.byteLength(part, 'utf8');
    if (used > maximum) invalid('metadata byte limit');
    parts.push(part);
  };
  const quote = (text: string): string => {
    if (text.length > maximum) invalid('metadata byte limit');
    unicode(text); return JSON.stringify(text);
  };
  const write = (item: unknown, depth: number): void => {
    if (depth > 32 || ++nodes > 100000) invalid('metadata complexity');
    if (item === null) { append('null'); return; }
    if (typeof item === 'string') { append(quote(item)); return; }
    if (typeof item === 'boolean') { append(item ? 'true' : 'false'); return; }
    if (typeof item === 'number') {
      if (!Number.isSafeInteger(item) || item < 0 || Object.is(item, -0)) invalid('metadata number');
      append(String(item)); return;
    }
    if (!item || typeof item !== 'object' || types.isProxy(item)) invalid('metadata value');
    if (ancestors.has(item)) invalid('cyclic metadata');
    ancestors.add(item);
    if (Array.isArray(item)) {
      const values = array(item, 100000); append('[');
      values.forEach((entry, index) => { if (index) append(','); write(entry, depth + 1); });
      append(']');
    } else {
      const values = object(item); const keys = Object.keys(values).sort(); append('{');
      keys.forEach((key, index) => {
        if (!/^[\x21-\x7e]+$/.test(key)) invalid('metadata key');
        if (index) append(','); append(quote(key)); append(':'); write(values[key], depth + 1);
      });
      append('}');
    }
    ancestors.delete(item);
  };
  write(value, 0);
  return new Uint8Array(Buffer.from(parts.join(''), 'utf8'));
}

/** 严格检查 UTF-8、重复解码键和数字原词法，不能先 JSON.parse 抹去输入证据。 */
export function decodeCacheControl(input: Uint8Array, options: CacheControlOptions = {}): unknown {
  return decodeMetadata(input, limit(options));
}
/** 新Core只读查询响应包含原有界回执正文；旧控制入口的64KiB合同不变。 */
export function decodeCacheLookupControl(input: Uint8Array): unknown {
  return decodeMetadata(input, 1048576);
}
function decodeMetadata(input: Uint8Array, maximum: number): unknown {
  const raw = snapshotCacheBytes(input, maximum);
  let text: string;
  try { text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(raw); }
  catch { invalid('invalid UTF-8'); }
  let offset = 0; let nodes = 0;
  const whitespace = (): void => { while (/[ \t\r\n]/.test(text[offset] ?? '\0')) offset++; };
  const string = (): string => {
    if (text[offset++] !== '"') invalid('string token');
    const start = offset - 1;
    while (offset < text.length) {
      const char = text[offset++];
      if (char === '"') {
        const result: unknown = JSON.parse(text.slice(start, offset));
        if (typeof result !== 'string') invalid('string token');
        unicode(result); return result;
      }
      if (char === '\\') {
        const escaped = text[offset++];
        if (!'"\\/bfnrtu'.includes(escaped ?? '\0')) invalid('string escape');
        if (escaped === 'u') {
          if (!/^[a-fA-F0-9]{4}$/.test(text.slice(offset, offset + 4))) invalid('unicode escape');
          offset += 4;
        }
      } else if (char!.charCodeAt(0) < 0x20) invalid('unescaped control');
    }
    invalid('unterminated string');
  };
  const parse = (depth: number): unknown => {
    whitespace();
    if (depth > 32 || ++nodes > 100000) invalid('metadata complexity');
    const token = text[offset];
    if (token === '"') return string();
    if (token === '{') {
      offset++; whitespace(); const result: Record<string, unknown> = Object.create(null);
      if (text[offset] === '}') { offset++; return result; }
      for (;;) {
        whitespace(); const key = string();
        if (!/^[\x21-\x7e]+$/.test(key) || Object.hasOwn(result, key)) invalid('invalid or duplicate key');
        whitespace(); if (text[offset++] !== ':') invalid('missing colon');
        result[key] = parse(depth + 1); whitespace();
        const separator = text[offset++];
        if (separator === '}') return result;
        if (separator !== ',') invalid('object separator');
      }
    }
    if (token === '[') {
      offset++; whitespace(); const result: unknown[] = [];
      if (text[offset] === ']') { offset++; return result; }
      for (;;) {
        result.push(parse(depth + 1)); whitespace();
        const separator = text[offset++];
        if (separator === ']') return result;
        if (separator !== ',') invalid('array separator');
      }
    }
    for (const [literal, result] of [['true', true], ['false', false], ['null', null]] as const) {
      if (text.startsWith(literal, offset)) { offset += literal.length; return result; }
    }
    const match = /^(0|[1-9][0-9]*)/.exec(text.slice(offset));
    if (!match) invalid('number or literal');
    offset += match[0].length;
    if (offset < text.length && !/[ \t\r\n,\]}]/.test(text[offset]!)) invalid('number token');
    const result = Number(match[0]);
    if (!Number.isSafeInteger(result)) invalid('unsafe number');
    return result;
  };
  const result = parse(0); whitespace();
  if (offset !== text.length) invalid('trailing metadata');
  return result;
}

function keys(value: Record<string, unknown>, required: readonly string[], optional: readonly string[] = []): void {
  if (required.some(key => !Object.hasOwn(value, key)) || Object.keys(value).some(key => !required.includes(key) && !optional.includes(key))) invalid('record fields');
}
