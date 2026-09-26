/** SDK2 有界控制字节与具名 schema 验证；无宿主权限、文件系统或 Node 依赖。 */
import { SDK2_WIRE_SCHEMA } from './wire-schema-generated.js';

export interface WireSchema {
  readonly $ref?: string; readonly type?: string; readonly const?: unknown; readonly enum?: readonly unknown[];
  readonly properties?: Readonly<Record<string, WireSchema>>; readonly required?: readonly string[]; readonly additionalProperties?: boolean;
  readonly allOf?: readonly WireSchema[]; readonly anyOf?: readonly WireSchema[]; readonly oneOf?: readonly WireSchema[];
  readonly not?: WireSchema; readonly if?: WireSchema; readonly then?: WireSchema; readonly else?: WireSchema;
  readonly items?: WireSchema; readonly contains?: WireSchema; readonly uniqueItems?: boolean;
  readonly minItems?: number; readonly maxItems?: number; readonly minLength?: number; readonly maxLength?: number;
  readonly minimum?: number; readonly maximum?: number; readonly multipleOf?: number; readonly pattern?: string; readonly format?: string;
}
export type Sdk2ClientErrorCode = 'invalid_options' | 'invalid_request' | 'invalid_response' | 'scope_unavailable' |
  'context_changed' | 'reentrant' | 'aborted' | 'network_error' | 'payload_too_large' | 'integrity_mismatch';
/** 本地失败；不会新增服务端 wire 错误值，不保存正文、配置或原异常。 */
export class Sdk2ClientError extends Error {
  constructor(readonly code: Sdk2ClientErrorCode) { super(`SDK2 client: ${code}`); this.name = 'Sdk2ClientError'; }
}
export function clientFail(code: Sdk2ClientErrorCode = 'invalid_response'): never { throw new Sdk2ClientError(code); }
const encoder = new TextEncoder();
export const MAX_CONTROL = 262144;
export const MAX_ARTIFACT_RESPONSE = 227 + 3 * 128 + 512 * 6 + 2 * 19 + 8 + 6 + 8 + 2 * 64 + 4 * Math.ceil(262144 / 3);

export function dataFields(value: unknown, required: readonly string[], optional: readonly string[] = []): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) clientFail('invalid_options');
  const descriptors = Object.getOwnPropertyDescriptors(value);
  if (Reflect.ownKeys(descriptors).some(key => typeof key !== 'string' || (!required.includes(key) && !optional.includes(key))) ||
    required.some(key => !Object.hasOwn(descriptors, key))) clientFail('invalid_options');
  const result: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
  for (const [key, entry] of Object.entries(descriptors)) {
    if (!Object.hasOwn(entry, 'value') || !entry.enumerable) clientFail('invalid_options');
    result[key] = entry.value;
  }
  return result;
}
function unicode(value: string): void {
  for (let i = 0; i < value.length; i++) {
    const c = value.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) { const next = value.charCodeAt(++i); if (!(next >= 0xdc00 && next <= 0xdfff)) clientFail('invalid_request'); }
    else if (c >= 0xdc00 && c <= 0xdfff) clientFail('invalid_request');
  }
}
/** 编码前逐描述符读取；拒绝 getter/toJSON/稀疏数组。原 IR 正文不走此编码器。 */
export function encodeControl(value: unknown, maximum = MAX_CONTROL): string {
  let nodes = 0, size = 0; const active = new Set<object>();
  const add = (part: string): string => { size += encoder.encode(part).byteLength; if (size > maximum) clientFail('payload_too_large'); return part; };
  function visit(v: unknown, depth: number): string {
    if (depth > 32 || ++nodes > 100000) clientFail('invalid_request');
    if (v === null || typeof v === 'boolean') return add(String(v));
    if (typeof v === 'string') { unicode(v); return add(JSON.stringify(v)); }
    if (typeof v === 'number') {
      if (!Number.isSafeInteger(v) || v < 0 || Object.is(v, -0)) clientFail('invalid_request'); return add(String(v));
    }
    if (!v || typeof v !== 'object' || active.has(v)) clientFail('invalid_request');
    active.add(v);
    try {
      const descriptors = Object.getOwnPropertyDescriptors(v);
      if (Array.isArray(v)) {
        const length = descriptors.length?.value as unknown;
        if (typeof length !== 'number' || length > 100000 || Reflect.ownKeys(descriptors).length !== length + 1) clientFail('invalid_request');
        const parts = [add('[')];
        for (let i = 0; i < length; i++) {
          const entry = descriptors[String(i)]; if (!entry || !Object.hasOwn(entry, 'value') || !entry.enumerable) clientFail('invalid_request');
          if (i) parts.push(add(',')); parts.push(visit(entry.value, depth + 1));
        }
        parts.push(add(']')); return parts.join('');
      }
      const prototype = Object.getPrototypeOf(v);
      if (prototype !== null && prototype !== Object.prototype) clientFail('invalid_request');
      if (Reflect.ownKeys(descriptors).some(key => typeof key !== 'string')) clientFail('invalid_request');
      const parts = [add('{')]; let first = true;
      for (const key of Object.keys(descriptors).sort()) {
        const entry = descriptors[key]!;
        if (!/^[\x21-\x7e]+$/.test(key) || !Object.hasOwn(entry, 'value') || !entry.enumerable) clientFail('invalid_request');
        if (!first) parts.push(add(',')); first = false;
        parts.push(add(JSON.stringify(key) + ':'), visit(entry.value, depth + 1));
      }
      parts.push(add('}')); return parts.join('');
    } finally { active.delete(v); }
  }
  return visit(value, 0);
}
export function decodeControl(bytes: Uint8Array, maximum: number): unknown {
  if (bytes.byteLength > maximum) clientFail('payload_too_large');
  try {
    const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    const value: unknown = JSON.parse(text);
    // 原 wire 要求规范控制字节；此比较同时拒绝重复解码键、替代数字和 BOM。
    if (encodeControl(value, maximum) !== text) clientFail();
    return value;
  } catch { return clientFail(); }
}
export function wireDateTime(value: string): number {
  const match = /^(\d{4})-(\d{2})-(\d{2})[Tt](\d{2}):(\d{2}):(\d{2})(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) return Number.NaN;
  const y = Number(match[1]), month = Number(match[2]), day = Number(match[3]), zone = match[8]!;
  const days = [31, y % 4 === 0 && (y % 100 !== 0 || y % 400 === 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (!(month >= 1 && month <= 12 && day >= 1 && day <= days[month - 1]! && Number(match[4]) <= 23 && Number(match[5]) <= 59 &&
    Number(match[6]) <= 60 && (zone.length === 1 || (Number(zone.slice(1, 3)) <= 23 && Number(zone.slice(4)) <= 59)))) return Number.NaN;
  return Date.parse(`${match[1]}-${match[2]}-${match[3]}T${match[4]}:${match[5]}:${match[6] === '60' ? '59' : match[6]}${match[7] ?? ''}${zone.toUpperCase()}`)
    + (match[6] === '60' ? 1000 : 0);
}
function matches(value: unknown, schema: WireSchema, depth = 0): boolean {
  if (depth > 64) return false;
  const sub = (child: WireSchema) => matches(value, child, depth + 1);
  if (schema.$ref) {
    const name = schema.$ref.slice('#/definitions/'.length), definition = SDK2_WIRE_SCHEMA[name];
    if (!definition || !sub(definition)) return false;
    if (['Sequence', 'RecordSequence'].includes(name) && BigInt(value as string) > 9223372036854775807n) return false;
  }
  if (Object.hasOwn(schema, 'const') && value !== schema.const) return false;
  if (schema.enum && !schema.enum.includes(value)) return false;
  if (schema.allOf && !schema.allOf.every(sub)) return false;
  if (schema.anyOf && !schema.anyOf.some(sub)) return false;
  if (schema.oneOf && schema.oneOf.filter(sub).length !== 1) return false;
  if (schema.not && sub(schema.not)) return false;
  if (schema.if && !(sub(schema.if) ? !schema.then || sub(schema.then) : !schema.else || sub(schema.else))) return false;
  if (schema.type) {
    const type = schema.type;
    if (type === 'null' ? value !== null : type === 'array' ? !Array.isArray(value) : type === 'object' ?
      !value || typeof value !== 'object' || Array.isArray(value) : type === 'integer' ?
      typeof value !== 'number' || !Number.isSafeInteger(value) || Object.is(value, -0) : typeof value !== type) return false;
  }
  if (typeof value === 'string') {
    const length = [...value].length;
    if ((schema.minLength !== undefined && length < schema.minLength) || (schema.maxLength !== undefined && length > schema.maxLength) ||
      (schema.pattern && !new RegExp(schema.pattern, 'u').test(value)) || (schema.format === 'date-time' && !Number.isFinite(wireDateTime(value)))) return false;
  }
  if (typeof value === 'number' && ((schema.minimum !== undefined && value < schema.minimum) ||
    (schema.maximum !== undefined && value > schema.maximum) || (schema.multipleOf !== undefined && value % schema.multipleOf !== 0))) return false;
  if (Array.isArray(value)) {
    if ((schema.minItems !== undefined && value.length < schema.minItems) || (schema.maxItems !== undefined && value.length > schema.maxItems) ||
      (schema.items && !value.every(item => matches(item, schema.items!, depth + 1))) ||
      (schema.contains && !value.some(item => matches(item, schema.contains!, depth + 1)))) return false;
    if (schema.uniqueItems && new Set(value.map(item => encodeControl(item, 1048576))).size !== value.length) return false;
  } else if (value && typeof value === 'object') {
    const object = value as Record<string, unknown>;
    if (schema.required?.some(key => !Object.hasOwn(object, key))) return false;
    if (schema.additionalProperties === false && Object.keys(object).some(key => !schema.properties || !Object.hasOwn(schema.properties, key))) return false;
    for (const [key, child] of Object.entries(schema.properties ?? {})) if (Object.hasOwn(object, key) && !matches(object[key], child, depth + 1)) return false;
  }
  return true;
}
export function validateNamed<T>(name: string, value: unknown): T {
  const schema = SDK2_WIRE_SCHEMA[name]; if (!schema || !matches(value, schema)) clientFail();
  return value as T;
}
export function freezeControl<T>(value: T): T {
  if (value && typeof value === 'object') { for (const item of Object.values(value)) freezeControl(item); Object.freeze(value); }
  return value;
}
export function equalControl(left: unknown, right: unknown): boolean { return encodeControl(left, 1048576) === encodeControl(right, 1048576); }
export function decodeBase64(text: string): Uint8Array {
  try {
    const raw = atob(text); if (btoa(raw) !== text) clientFail('integrity_mismatch');
    return Uint8Array.from(raw, character => character.charCodeAt(0));
  } catch { return clientFail('integrity_mismatch'); }
}
export async function sha256(bytes: Uint8Array): Promise<string> {
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes.slice().buffer as ArrayBuffer);
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}
