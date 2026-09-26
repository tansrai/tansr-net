/** Staged package-internal SDK2 transport validation; no kernel, filesystem or authority. */
import { SDK2_LIMIT_RANGES } from './wire-limits-generated.js';

export const SDK2_EVENT_JSON_BYTES = 262144;
export const SDK2_EVENT_SSE_BYTES = 262317;
export const SDK2_EVENT_TYPES = ['archive.records-available', 'archive.status', 'material.request', 'material.status', 'binding.status'] as const;
export type Sdk2EventType = typeof SDK2_EVENT_TYPES[number];
export interface Sdk2EventContext {
  readonly bindingId: string;
  readonly applicationScopeId: string;
  readonly endUserId: string;
  readonly generations: { readonly historyEpoch: string; readonly deletionGeneration: string; readonly projectionRevision: string };
}
export interface Sdk2EventFrame {
  readonly protocol: 'sdk2-ext-v1'; readonly bindingId: string; readonly eventId: string; readonly cursor: string;
  readonly revision: string; readonly generations: Sdk2EventContext['generations']; readonly eventType: Sdk2EventType;
  readonly payload: Readonly<Record<string, unknown>>;
}
/** Local transport failure only; these codes are not new wire/API error values. */
export class Sdk2StreamDecodeError extends Error {
  constructor(readonly code: 'invalid_encoding' | 'invalid_frame' | 'invalid_json' | 'invalid_payload' | 'frame_too_large' |
    'incomplete_frame' | 'context_mismatch' | 'closed' | 'reentrant') {
    super(`SDK2 event stream: ${code}`); this.name = 'Sdk2StreamDecodeError';
  }
}
export function streamFail(code: Sdk2StreamDecodeError['code'] = 'invalid_payload'): never { throw new Sdk2StreamDecodeError(code); }
const encoder = new TextEncoder();
const MAX_SEQUENCE = 9223372036854775807n;
const ID = /^[A-Za-z0-9][A-Za-z0-9._~-]{0,127}$/;
const HASH = /^[a-f0-9]{64}$/;
const STATES = ['active', 'backpressured', 'closing', 'closed'];
const CAPS = ['archive-transfer-v1', 'context-materials-v1'];
type ObjectValue = Record<string, unknown>;
function object(value: unknown): ObjectValue {
  if (!value || typeof value !== 'object' || Array.isArray(value)) streamFail();
  return value as ObjectValue;
}
function fields(value: unknown, required: readonly string[], optional: readonly string[] = []): ObjectValue {
  const v = object(value);
  if (required.some(key => !Object.hasOwn(v, key)) || Object.keys(v).some(key => !required.includes(key) && !optional.includes(key))) streamFail();
  return v;
}
function id(value: unknown): string { if (typeof value !== 'string' || !ID.test(value)) streamFail(); return value; }
function legacy(value: unknown): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > 1024 || [...value].length > 512) streamFail();
  return value;
}
function seq(value: unknown): bigint {
  if (typeof value !== 'string' || !/^(0|[1-9][0-9]{0,18})$/.test(value) || BigInt(value) > MAX_SEQUENCE) streamFail();
  return BigInt(value);
}
function integer(value: unknown, min = 0, max = Number.MAX_SAFE_INTEGER): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || Object.is(value, -0) || value < min || value > max) streamFail();
  return value;
}
function digest(value: unknown): void { if (typeof value !== 'string' || !HASH.test(value)) streamFail(); }
function choice(value: unknown, options: readonly string[]): string {
  if (typeof value !== 'string' || !options.includes(value)) streamFail(); return value;
}
function array(value: unknown, min: number, max: number): unknown[] {
  if (!Array.isArray(value) || value.length < min || value.length > max) streamFail(); return value;
}
function generations(value: unknown): ObjectValue {
  const v = fields(value, ['historyEpoch', 'deletionGeneration', 'projectionRevision']);
  legacy(v.historyEpoch); seq(v.deletionGeneration); seq(v.projectionRevision); return v;
}
function sameGenerations(a: unknown, b: unknown): void {
  const x = generations(a), y = generations(b);
  if (Object.keys(x).some(key => x[key] !== y[key])) streamFail('context_mismatch');
}
function target(value: unknown): ObjectValue {
  const v = fields(value, ['sessionId', 'generations', 'sourceSnapshotDigest']);
  legacy(v.sessionId); generations(v.generations); digest(v.sourceSnapshotDigest); return v;
}
function unicode(value: string): void {
  for (let at = 0; at < value.length; at++) {
    const c = value.charCodeAt(at);
    if (c >= 0xd800 && c <= 0xdbff) { const next = value.charCodeAt(++at); if (!(next >= 0xdc00 && next <= 0xdfff)) streamFail('invalid_json'); }
    else if (c >= 0xdc00 && c <= 0xdfff) streamFail('invalid_json');
  }
}
function canonical(value: unknown): string {
  let nodes = 0;
  function visit(v: unknown, depth: number): string {
    if (depth > 32 || ++nodes > 100000) streamFail('invalid_json');
    if (v === null || typeof v === 'boolean') return String(v);
    if (typeof v === 'string') { unicode(v); return JSON.stringify(v); }
    if (typeof v === 'number') return String(integer(v));
    if (Array.isArray(v)) return '[' + v.map(item => visit(item, depth + 1)).join(',') + ']';
    return '{' + Object.keys(object(v)).sort().map(key => {
      if (!/^[\x21-\x7e]+$/.test(key)) streamFail('invalid_json');
      return JSON.stringify(key) + ':' + visit((v as ObjectValue)[key], depth + 1);
    }).join(',') + '}';
  }
  return visit(value, 0);
}
function frozen<T>(value: T): T {
  if (value && typeof value === 'object') { for (const child of Object.values(value)) frozen(child); Object.freeze(value); }
  return value;
}
function coverage(value: unknown): ObjectValue {
  const v = fields(value, ['fromSequence', 'throughSequence', 'headDigest']);
  if (seq(v.fromSequence) < 1n || seq(v.throughSequence) < seq(v.fromSequence)) streamFail(); digest(v.headDigest); return v;
}
function artifact(value: unknown): ObjectValue {
  const v = fields(value, ['artifactId', 'sourceId', 'bytes', 'sha256', 'mediaType']);
  id(v.artifactId); id(v.sourceId); integer(v.bytes, 1, 33554432); digest(v.sha256);
  if (typeof v.mediaType !== 'string' || v.mediaType.length > 128 || !/^[A-Za-z0-9][A-Za-z0-9!#$&^_.+/-]*$/.test(v.mediaType)) streamFail();
  return v;
}
function archiveStatus(p: ObjectValue): void {
  fields(p, ['protocol', 'bindingId', 'revision', 'generations', 'sourceId', 'sourceGeneration', 'publishedThroughSequence',
    'acknowledgedCoverage', 'releasableThroughSequence', 'pendingBytes', 'pendingRecords', 'sessionPersistence', 'state']);
  seq(p.revision); generations(p.generations); id(p.sourceId); id(p.sourceGeneration); choice(p.state, STATES);
  if (p.sessionPersistence !== 'unchanged') streamFail(); integer(p.pendingBytes, 0, 67108864); integer(p.pendingRecords, 0, 4096);
  const published = p.publishedThroughSequence === null ? null : seq(p.publishedThroughSequence);
  const released = p.releasableThroughSequence === null ? null : seq(p.releasableThroughSequence);
  if (published === 0n || released === 0n) streamFail();
  if (p.acknowledgedCoverage !== null) {
    const ack = coverage(p.acknowledgedCoverage), last = seq(ack.throughSequence);
    if (published === null || last > published || (released !== null && released > last)) streamFail();
  } else if (released !== null) streamFail();
}
function materialRequest(p: ObjectValue): void {
  fields(p, ['protocol', 'bindingId', 'materialRequestId', 'target', 'sourceId', 'sourceGeneration', 'requestedRecords', 'purpose',
    'maxBytes', 'chunkBytes', 'remainingTtlMs']);
  id(p.materialRequestId); target(p.target); id(p.sourceId); id(p.sourceGeneration); choice(p.purpose, ['context-recall', 'projection-verification']);
  integer(p.remainingTtlMs, 1, 30000); const max = integer(p.maxBytes, 1024, 1048576), chunk = integer(p.chunkBytes, 1, 65536);
  const records = new Set<string>(), objects = new Map<string, ObjectValue>(); let total = 0;
  for (const item of array(p.requestedRecords, 1, 32)) {
    const r = fields(item, ['recordId', 'digest', 'payload', 'attachments']), rid = id(r.recordId); digest(r.digest);
    if (records.has(rid)) streamFail(); records.add(rid); const unique = new Set<string>();
    for (const item of [r.payload, ...array(r.attachments, 0, 32)]) {
      const ref = artifact(item), key = id(ref.artifactId), bytes = integer(ref.bytes, 1, 33554432);
      if (ref.sourceId !== p.sourceId || Math.ceil(bytes / chunk) > 16 || unique.has(key)) streamFail(); unique.add(key);
      const previous = objects.get(key); if (previous && canonical(previous) !== canonical(ref)) streamFail();
      if (!previous) { objects.set(key, ref); total += bytes; if (total > max) streamFail(); }
    }
  }
}
function materialStatus(p: ObjectValue): void {
  fields(p, ['protocol', 'bindingId', 'materialRequestId', 'state', 'revision', 'acceptedRecordIds'], ['reason']);
  id(p.materialRequestId); seq(p.revision); choice(p.state, ['pending', 'received', 'verified', 'core-consumed', 'rejected']);
  const ids = array(p.acceptedRecordIds, 0, 32).map(id); if (new Set(ids).size !== ids.length) streamFail();
  if (p.state === 'rejected') choice(p.reason, ['source_unavailable', 'stale_generation', 'invalid_coverage', 'request_expired', 'material_rejected', 'forbidden', 'binding_closed']);
  else if (Object.hasOwn(p, 'reason')) streamFail();
  if (p.state === 'pending' && (p.revision !== '0' || ids.length)) streamFail();
}
function dateTime(value: unknown): number {
  if (typeof value !== 'string') streamFail();
  const m = /^(\d{4})-(\d{2})-(\d{2})[Tt](\d{2}):(\d{2}):(\d{2})(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$/.exec(value);
  if (!m) streamFail();
  const y = Number(m[1]), mo = Number(m[2]), day = Number(m[3]), zone = m[8]!;
  const days = [31, y % 4 === 0 && (y % 100 !== 0 || y % 400 === 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (mo < 1 || mo > 12 || day < 1 || day > days[mo - 1]! || Number(m[4]) > 23 || Number(m[5]) > 59 || Number(m[6]) > 60 ||
    (zone.length > 1 && (Number(zone.slice(1, 3)) > 23 || Number(zone.slice(4, 6)) > 59))) streamFail();
  const result = Date.parse(`${m[1]}-${m[2]}-${m[3]}T${m[4]}:${m[5]}:${m[6] === '60' ? '59' : m[6]}${m[7] ?? ''}${zone.toUpperCase()}`);
  if (!Number.isFinite(result)) streamFail(); return result + (m[6] === '60' ? 1000 : 0);
}
function bindingStatus(p: ObjectValue, context: Sdk2EventContext): void {
  fields(p, ['protocol', 'bindingId', 'scope', 'target', 'revision', 'state', 'sourceId', 'acceptedCapabilities', 'rejectedCapabilities',
    'availability', 'operationEpoch', 'limits', 'archiveAckFormat']);
  seq(p.revision); choice(p.state, STATES); id(p.sourceId); target(p.target);
  const scope = fields(p.scope, ['applicationScopeId', 'endUserId', 'authorizationRevision']);
  id(scope.applicationScopeId); legacy(scope.endUserId); seq(scope.authorizationRevision);
  if (scope.applicationScopeId !== context.applicationScopeId || scope.endUserId !== context.endUserId) streamFail('context_mismatch');
  if (p.availability !== 'legacy-complete') streamFail();
  const accepted = array(p.acceptedCapabilities, 1, 2).map(value => choice(value, CAPS));
  if (new Set(accepted).size !== accepted.length) streamFail(); const rejected = new Set<string>();
  for (const item of array(p.rejectedCapabilities, 0, 2)) {
    const r = fields(item, ['capability', 'reason']), cap = choice(r.capability, CAPS);
    choice(r.reason, ['unsupported_capability', 'not_authorized']);
    if (accepted.includes(cap) || rejected.has(cap)) streamFail(); rejected.add(cap);
  }
  if (p.archiveAckFormat !== (accepted.includes('archive-transfer-v1') ? 'split-receipts-v1' : null)) streamFail();
  const limits = fields(p.limits, Object.keys(SDK2_LIMIT_RANGES));
  for (const [key, [min, max]] of Object.entries(SDK2_LIMIT_RANGES)) integer(limits[key], min, max);
  if (Number(limits.inflightReserveBytes) > Number(limits.pendingBytes)) streamFail();
  if (encoder.encode(canonical(p)).byteLength > Number(limits.controlBytes)) streamFail('frame_too_large');
  if (p.operationEpoch !== null) {
    const epoch = fields(p.operationEpoch, ['id', 'issuedAt', 'expiresAt', 'state']); id(epoch.id);
    const duration = dateTime(epoch.expiresAt) - dateTime(epoch.issuedAt);
    if (epoch.state !== 'active' || duration <= 0 || duration > Number(limits.epochLifetimeMs)) streamFail();
  }
}
export function copyEventContext(context: Sdk2EventContext): Sdk2EventContext {
  id(context.bindingId); id(context.applicationScopeId); legacy(context.endUserId); unicode(context.endUserId); generations(context.generations);
  const value = { bindingId: context.bindingId, applicationScopeId: context.applicationScopeId, endUserId: context.endUserId,
    generations: { ...context.generations } }; unicode(value.generations.historyEpoch); return frozen(value);
}
/** Canonical wire bytes make duplicates/alternate numeric tokens visible before delivery. No MAC claim. */
export function decodeSdk2EventFrame(text: string, headerId: string, headerEvent: string, context: Sdk2EventContext): Sdk2EventFrame {
  if (encoder.encode(text).byteLength > SDK2_EVENT_JSON_BYTES) streamFail('frame_too_large');
  let decoded: unknown;
  try { decoded = JSON.parse(text); } catch { streamFail('invalid_json'); }
  if (canonical(decoded) !== text) streamFail('invalid_json');
  const f = fields(decoded, ['protocol', 'bindingId', 'eventId', 'cursor', 'revision', 'generations', 'eventType', 'payload']);
  if (f.protocol !== 'sdk2-ext-v1') streamFail(); id(f.bindingId); seq(f.revision); generations(f.generations);
  if (f.bindingId !== context.bindingId) streamFail('context_mismatch'); sameGenerations(f.generations, context.generations);
  choice(f.eventType, SDK2_EVENT_TYPES);
  if (f.cursor !== headerId || f.eventType !== headerEvent) streamFail('invalid_frame');
  if (typeof f.cursor !== 'string' || !/^e1\.k1\.[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\.[0-7][0-9a-f]{15}\.[0-7][0-9a-f]{15}\.[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/.test(f.cursor)) streamFail('invalid_frame');
  const parts = f.cursor.split('.');
  if (BigInt('0x' + parts[3]!) === 0n || BigInt('0x' + parts[4]!) === 0n || f.eventId !== `e1.${parts[2]}.${parts[3]}.${parts[4]}`) streamFail('invalid_frame');
  const p = object(f.payload);
  if (f.eventType === 'archive.records-available') { fields(p, ['publishedThroughSequence']); if (seq(p.publishedThroughSequence) < 1n) streamFail(); }
  else {
    if (p.protocol !== 'sdk2-ext-v1' || p.bindingId !== f.bindingId) streamFail('context_mismatch');
    if (f.eventType === 'archive.status') archiveStatus(p);
    else if (f.eventType === 'material.request') materialRequest(p);
    else if (f.eventType === 'material.status') materialStatus(p);
    else bindingStatus(p, context);
    if ((f.eventType === 'archive.status' || f.eventType === 'binding.status') && p.revision !== f.revision) streamFail('invalid_payload');
    if (Object.hasOwn(p, 'generations')) sameGenerations(f.generations, p.generations);
    if (Object.hasOwn(p, 'target')) sameGenerations(f.generations, object(p.target).generations);
  }
  return frozen(f) as unknown as Sdk2EventFrame;
}
