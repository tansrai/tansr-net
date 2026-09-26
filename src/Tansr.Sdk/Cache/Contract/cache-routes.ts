/** 新 /v3 面独立鉴权及严格 JSON；未显式装配时旧服务没有这些路由。 */
import type { IncomingMessage, ServerResponse } from 'node:http';
import { randomUUID } from 'node:crypto';
import { ZodError } from 'zod';
import { bodyReservationBytes, type AdmissionController } from '../v2/admission.js';
import { GatewayCacheError } from '@tansr/providers';
import { CACHE_PROTOCOL, CacheErrorSchema, decodeCacheControl, encodeCacheControl } from '@tansr/providers/internal/cache-wire';
import type { AgentSessionsOptions } from '../v2/types.js';
import { AuthenticationUnavailableError, type RequestAuthenticator } from '../auth.js';
import type { ServeCacheLink } from './cache-link.js';

function reply(res: ServerResponse, status: number, value: unknown): void {
  if (res.destroyed || res.writableEnded) return;
  const body = encodeCacheControl(value);
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'content-length': body.length });
  res.end(body);
}
async function body(req: IncomingMessage): Promise<unknown> {
  if (req.headers['content-type']?.split(';')[0]?.trim().toLowerCase() !== 'application/json') throw new GatewayCacheError('invalid_request', undefined, 400);
  const chunks: Buffer[] = []; let bytes = 0;
  for await (const part of req) {
    const chunk = Buffer.isBuffer(part) ? part : Buffer.from(part as Uint8Array); bytes += chunk.length;
    if (bytes > 65536) throw new GatewayCacheError('payload_too_large', undefined, 413);
    chunks.push(chunk);
  }
  try { return decodeCacheControl(Buffer.concat(chunks)); } catch { throw new GatewayCacheError('invalid_request', undefined, 400); }
}
function query(url: URL, required: readonly string[], optional: readonly string[] = []): URLSearchParams {
  const params = url.searchParams;
  for (const key of params.keys()) if (![...required, ...optional].includes(key) || params.getAll(key).length !== 1) throw new GatewayCacheError('invalid_request', undefined, 400);
  if (required.some(key => !params.has(key)) || params.get('protocol') !== CACHE_PROTOCOL) throw new GatewayCacheError('invalid_request', undefined, 400);
  return params;
}
export function createCacheRoutes(options: { link: ServeCacheLink; admission: AdmissionController; authenticate: RequestAuthenticator['authenticate']; track: (work: Promise<void>) => void }) {
  let inflight = 0;
  return (req: IncomingMessage, res: ServerResponse, pathname: string, method: string): boolean => {
    if (!pathname.startsWith('/v3/sdk2/cache/')) return false;
    let reservation = 0, occupied = false;
    const work = (async () => {
      if (inflight >= 128 || options.admission.overloaded()) throw new GatewayCacheError('capacity_exceeded', undefined, 503);
      inflight++; occupied = true;
      let identity: Awaited<ReturnType<AgentSessionsOptions['authenticate']>>;
      try { identity = await options.authenticate(req, res); } catch (error) {
        if (error instanceof AuthenticationUnavailableError) throw new GatewayCacheError('capacity_exceeded', undefined, 503, 'same-request');
        identity = null;
      }
      if (req.aborted || res.destroyed || res.writableEnded) return;
      if (!identity || !identity.endUserId) throw new GatewayCacheError('unauthorized', undefined, 401);
      const admitted = await options.admission.rateLimit(req, identity.endUserId);
      if (!admitted.admitted) throw new GatewayCacheError('capacity_exceeded', undefined, 429);
      if (method === 'POST') {
        const bytes = bodyReservationBytes(req, 65536);
        if (!options.admission.reserveBody(bytes).admitted) throw new GatewayCacheError('capacity_exceeded', undefined, 503);
        reservation = bytes;
      }
      const eu = identity.endUserId, url = new URL(req.url ?? '/', 'http://serve.invalid');
      const path = pathname.slice('/v3/sdk2/cache'.length);
      let result: unknown;
      const coreDiagnostic = /^\/core\/v1\/bindings\/([A-Za-z0-9][A-Za-z0-9._~-]{0,127})\/diagnostics$/.exec(path);
      if (method === 'GET' && coreDiagnostic) {
        const p = url.searchParams;
        if ([...p.keys()].some(key => !['protocol', 'limit', 'after'].includes(key) || p.getAll(key).length !== 1) ||
          p.get('protocol') !== 'sdk2-cache-core-v1') throw new GatewayCacheError('invalid_request', undefined, 400);
        const raw = p.get('limit') ?? '100';
        if (!/^[1-9][0-9]{0,2}$/.test(raw) || Number(raw) > 100) throw new GatewayCacheError('invalid_request', undefined, 400);
        result = await options.link.coreDiagnostics(eu, coreDiagnostic[1]!, p.get('after'), Number(raw));
      } else if (method === 'GET' && path === '/capabilities') {
        if (url.search) throw new GatewayCacheError('invalid_request', undefined, 400);
        result = await options.link.capabilities(eu);
      } else if (method === 'POST' && path === '/bindings') {
        if (url.search) throw new GatewayCacheError('invalid_request', undefined, 400);
        result = await options.link.execute(eu, 'open', await body(req));
      } else if (method === 'GET' && path === '/operations') {
        const p = query(url, ['protocol', 'operation', 'operationEpoch', 'requestId'], ['bindingId']);
        const operation = p.get('operation');
        if (operation !== 'open' && operation !== 'renew' && operation !== 'rebind' && operation !== 'rotate' && operation !== 'close') throw new GatewayCacheError('invalid_request', undefined, 400);
        result = await options.link.query(eu, operation, { operationEpoch: p.get('operationEpoch')!, requestId: p.get('requestId')! }, p.get('bindingId'));
      } else {
        const match = /^\/bindings\/([A-Za-z0-9][A-Za-z0-9._~-]{0,127})(?:\/(renew|rebind|rotate|close|diagnostics))?$/.exec(path);
        if (!match) throw new GatewayCacheError('mapping_unavailable', undefined, 404);
        const id = match[1]!, action = match[2];
        if (method === 'GET' && action === undefined) {
          query(url, ['protocol']); result = await options.link.read(eu, id);
        } else if (method === 'GET' && action === 'diagnostics') {
          const p = query(url, ['protocol'], ['after', 'limit']), raw = p.get('limit') ?? '100';
          if (!/^[1-9][0-9]{0,2}$/.test(raw) || Number(raw) > 100) throw new GatewayCacheError('invalid_request', undefined, 400);
          result = await options.link.diagnostics(eu, id, p.get('after'), Number(raw));
        } else if (method === 'POST' && (action === 'renew' || action === 'rebind' || action === 'rotate' || action === 'close')) {
          if (url.search) throw new GatewayCacheError('invalid_request', undefined, 400);
          const input = await body(req);
          if (!input || typeof input !== 'object' || (input as { bindingId?: unknown }).bindingId !== id) throw new GatewayCacheError('invalid_request', undefined, 400);
          result = await options.link.execute(eu, action, input);
        } else throw new GatewayCacheError('invalid_request', undefined, 400);
      }
      reply(res, 200, result);
    })().catch(error => {
      if (res.destroyed || res.writableEnded) return;
      const known = error instanceof GatewayCacheError;
      const status = known ? error.status ?? 503 : error instanceof ZodError ? 400 : 503;
      const code = known ? error.code : error instanceof ZodError ? 'invalid_request' : 'result_unknown';
      const envelope = { protocol: CACHE_PROTOCOL, requestId: randomUUID(), code, status,
        retryAction: known ? error.retryAction : code === 'result_unknown' ? 'query-status' : 'none', fallback: 'none', message: code };
      const parsed = CacheErrorSchema.safeParse(envelope);
      if (status === 429 || status === 503) res.setHeader('retry-after', '5');
      reply(res, parsed.success ? status : 503, parsed.success ? parsed.data : { ...envelope, code: 'result_unknown', message: 'result_unknown', status: 503, retryAction: 'query-status' });
    }).finally(() => { if (occupied) inflight--; if (reservation > 0) options.admission.releaseBody(reservation); });
    options.track(work); return true;
  };
}
