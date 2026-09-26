/** SDK2 缓存网关具名合同；来源 doc/rfc/sdk2-cache-v1.schema.json，不扩展冻结 L0。 */
import { createHash } from 'node:crypto';
import { z } from 'zod';
import { encodeCacheControl } from './cache-control-json.js';

export const CACHE_PROTOCOL = 'sdk2-cache-v1' as const;
export const CACHE_FEATURE = 'private-logical-cache-v1' as const;
export const CACHE_EXCHANGE_CONTENT_TYPE = 'application/vnd.tansr.cache-exchange.v1';
export const CACHE_TWP_MAX_BYTES = 33554432;
const id = z.string().min(1).max(128).regex(/^[A-Za-z0-9][A-Za-z0-9._~-]*$/);
const legacyId = z.string().refine(value => [...value].length > 0 && [...value].length <= 512);
const sequence = z.string().regex(/^(0|[1-9][0-9]{0,18})$/).refine(value => BigInt(value) <= 9223372036854775807n);
const ticket = z.string().regex(/^[A-Za-z0-9_-]{43}$/).refine(value => Buffer.from(value, 'base64url').toString('base64url') === value);
const date = z.string().datetime({ offset: true });
const protocol = z.literal(CACHE_PROTOCOL);
export const CacheRequestIdentitySchema = z.object({ operationEpoch: id, requestId: id }).strict();
export const CacheOpenIntentSchema = z.discriminatedUnion('kind', [
  z.object({ kind: z.literal('new') }).strict(), z.object({ kind: z.literal('import') }).strict(),
  z.object({ kind: z.literal('resume'), ticket }).strict(), z.object({ kind: z.literal('fork'), parentTicket: ticket }).strict(),
]);
const projection = z.object({ projectionRef: id, revision: sequence,
  status: z.enum(['current', 'rebuild-required', 'missing']),
  reason: z.enum(['unchanged', 'policy-changed', 'generation-changed', 'provider-changed', 'source-missing', 'legacy-missing', 'key-rotated']),
}).strict();
export const CacheBindingSchema = z.object({ protocol, audience: z.literal('gateway-cache'), bindingId: id, logicalRef: id, revision: sequence,
  state: z.enum(['active', 'closed', 'revoked', 'expired']), mode: z.literal('private-logical-v1'), groupGeneration: sequence,
  projection: projection.nullable(), expiresAt: date, availability: z.literal('legacy-complete'),
}).strict();
export const CacheOpenResponseSchema = z.object({ protocol, request: CacheRequestIdentitySchema, binding: CacheBindingSchema,
  ticket, ticketExpiresAt: date, relation: z.enum(['new', 'resumed', 'forked', 'imported']),
}).strict();
export const CacheMutationResponseSchema = z.object({ protocol, request: CacheRequestIdentitySchema,
  operation: z.enum(['open', 'renew', 'rotate', 'close', 'rebind']),
  semanticDigest: z.object({ algorithm: z.literal('hmac-sha256'), keyId: id, digest: z.string().regex(/^[a-f0-9]{64}$/) }).strict(),
  state: z.literal('completed'), binding: CacheBindingSchema, ticket: ticket.nullable(), ticketExpiresAt: date.nullable(),
}).strict();
const limitBounds = {
  controlBytes: [4096, 65536], exchangeBytes: [1024, 33554432], projectionBytes: [1024, 262144], projectionSegments: [1, 64],
  ticketTtlMs: [60000, 604800000], mappingIdleMs: [60000, 604800000], mappingLifetimeMs: [60000, 2592000000], bindingsPerLogical: [1, 16],
  mappingsPerUser: [1, 1000], mappingsPerApplication: [1, 10000], mappingsGlobal: [1, 100000], mappingBytes: [1024, 16384],
  receiptsGlobal: [1, 100000], receiptBytes: [1024, 32768], receiptRetentionMs: [60000, 604800000], epochLifetimeMs: [60000, 86400000],
  diagnosticRowsPerUser: [1, 1000], diagnosticRowsPerApplication: [1, 10000], diagnosticRowsGlobal: [1, 100000], diagnosticBytes: [1024, 4096],
  diagnosticRetentionMs: [60000, 86400000], diagnosticSamplePerMillion: [0, 1000000], diagnosticsPageRows: [1, 100], negotiatedTtlMs: [1000, 60000],
  retainedRowsPerUser: [1, 10000], retainedRowsPerApplication: [1, 100000], retainedRowsGlobal: [1, 1000000],
  retainedBytesPerUser: [1, 16777216], retainedBytesPerApplication: [1, 268435456], retainedBytesGlobal: [1, 2147483648],
} as const;
const limits = z.object(Object.fromEntries(Object.entries(limitBounds).map(([key, [min, max]]) => [key, z.number().int().min(min).max(max)]))).strict();
export const CacheCapabilitiesSchema = z.object({ protocol, audience: z.literal('gateway-cache'), features: z.array(z.literal(CACHE_FEATURE)).max(1),
  gateway: z.literal('not-applicable'), availability: z.literal('legacy-complete'), revision: sequence, limits,
  operationEpoch: z.object({ id, issuedAt: date, expiresAt: date, state: z.literal('active') }).strict().nullable(),
}).strict().refine(value => value.features.length === 0 || value.operationEpoch !== null);
export const CacheErrorSchema = z.object({ protocol, requestId: id,
  code: z.enum(['invalid_request', 'protocol_version_mismatch', 'unauthorized', 'forbidden', 'stale_revision', 'stale_generation',
    'request_id_conflict', 'projection_stale', 'ticket_expired', 'mapping_expired', 'receipt_expired', 'payload_too_large',
    'unsupported_capability', 'capacity_exceeded', 'epoch_unavailable', 'mapping_unavailable', 'result_unknown']),
  status: z.number().int().min(400).max(503), retryAction: z.enum(['none', 'same-request', 'query-status', 'refresh-projection']),
  fallback: z.enum(['none', 'legacy-cold']), message: z.string().min(1).max(256),
}).strict();
export type GatewayCacheRequestIdentity = z.infer<typeof CacheRequestIdentitySchema>;
export type GatewayCacheOpenIntent = z.infer<typeof CacheOpenIntentSchema>;
export type GatewayCacheOpenResponse = z.infer<typeof CacheOpenResponseSchema>;
export type GatewayCacheMutationResponse = z.infer<typeof CacheMutationResponseSchema>;
export type GatewayCacheBinding = z.infer<typeof CacheBindingSchema>;
export type GatewayCacheCapabilities = z.infer<typeof CacheCapabilitiesSchema>;
export type GatewayCacheMutation = 'renew' | 'rebind' | 'rotate' | 'close';

export function cacheId(value: string): string { return id.parse(value); }
export function cacheRuntimeId(value: string): string { return legacyId.parse(value); }
export function cacheRevision(value: string): string { return sequence.parse(value); }
export function cacheTicket(value: string): string { return ticket.parse(value); }

/** 原 TWP 只为核对身份解析，封套携带的正文始终是调用方首次序列化的原字节。 */
export function encodeGatewayCacheExchange(fields: { bindingId: string; ticket: string; runtimeSessionId: string; projectionRef: string }, requestId: string, body: string): Buffer {
  const bytes = Buffer.from(body, 'utf8');
  if (bytes.length < 1 || bytes.length > CACHE_TWP_MAX_BYTES || bytes.toString('utf8') !== body) throw new TypeError('invalid_request');
  const parsed = JSON.parse(body) as { meta?: { requestId?: unknown; sessionId?: unknown } };
  if (!/^[A-Za-z0-9_-]{8,40}$/.test(requestId) || parsed?.meta?.requestId !== requestId || parsed.meta.sessionId !== fields.runtimeSessionId) throw new TypeError('request_id_conflict');
  const metadata = encodeCacheControl({ protocol: CACHE_PROTOCOL, bindingId: cacheId(fields.bindingId), ticket: cacheTicket(fields.ticket),
    runtimeSessionId: cacheRuntimeId(fields.runtimeSessionId), projectionRef: cacheId(fields.projectionRef),
    payloadBytes: bytes.length, payloadSha256: createHash('sha256').update(bytes).digest('hex'),
  });
  const frame = Buffer.allocUnsafe(4 + metadata.length + bytes.length);
  frame.writeUInt32BE(metadata.length); frame.set(metadata, 4); frame.set(bytes, 4 + metadata.length);
  return frame;
}
