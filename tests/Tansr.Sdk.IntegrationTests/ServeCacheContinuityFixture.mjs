// Original public Serve cache assembly and durable SQLite ledger. The platform peer is synthetic.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile, rename, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startCacheContinuityFixture({ source, directory, authenticate }) {
  await mkdir(directory, { recursive: true });
  const commands = join(directory, 'host-commands'), responses = join(directory, 'host-responses');
  await mkdir(commands); await mkdir(responses); await mkdir(join(directory, 'cache'));
  const load = file => import(pathToFileURL(join(source, file)).href);
  const { createServeAgentSessionStore } = await load('packages/server/src/index.ts');
  const { createServeCacheHost } = await load('packages/server/src/extensions/index.ts');
  const { continuityApi, continuityOptions, hostKey } = await load('packages/server/test/cache-continuity.fixture.ts');
  const { BASE } = await load('packages/sdk/test/cache-gateway-assembly.fixture.ts');
  const api = continuityApi(), routes = [], snapshots = [], diagnosticReads = [], expiryRejections = [];
  const shortTickets = new Map(), shortReceipts = new Map(); let issueShortTicket = false;
  // 原continuityApi夹具没有诊断路由；独立补齐原已支持的空诊断页，不伪造供应商命中或费用。
  const fetchImpl = async (input, init) => {
    const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
    assert.equal(url.origin, new URL(BASE).origin, 'Only the explicit synthetic cache platform is permitted.');
    // The authoritative synthetic peer issues one short-lived ticket. Only wall-clock
    // time expires it: runtime, binding, authentication and both operation epochs remain valid.
    const renewal = /^\/t1\/cache\/v1\/bindings\/([^/]+)\/renew$/.exec(url.pathname);
    if (renewal && shortTickets.has(decodeURIComponent(renewal[1]))) {
      assert.equal(init?.method, 'POST');
      assert.equal(new Headers(init?.headers).get('x-tansr-app-token'), 'synthetic-app-user-token');
      const bindingId = decodeURIComponent(renewal[1]), ticket = shortTickets.get(bindingId);
      const body = JSON.parse(Buffer.from(init?.body).toString('utf8'));
      assert.equal(body.bindingId, bindingId); assert.equal(body.ticket, ticket.value);
      assert.equal(body.request.operationEpoch, ticket.operationEpoch);
      const current = await api.fetchImpl(`${BASE}/t1/cache/v1/bindings/${encodeURIComponent(bindingId)}`, { headers: init.headers });
      const binding = await current.json(); assert.equal(binding.state, 'active'); assert.equal(body.expectedRevision, binding.revision);
      assert.ok(Date.parse(binding.expiresAt) > Date.now(), 'Mapping must not be the expired authority.');
      assert.ok(Date.now() >= ticket.expiresAtMs, 'This scenario must wait for natural ticket expiration.');
      expiryRejections.push({ requestId: body.request.requestId, operationEpoch: body.request.operationEpoch, bindingId,
        expiredAt: new Date(ticket.expiresAtMs).toISOString(), observedAt: new Date().toISOString(),
        mappingState: binding.state, mappingExpiresAt: binding.expiresAt });
      assert.equal(expiryRejections.length, 1, 'The SDK must not automatically replay the rejected original operation.');
      // Exact original API cache/http-errors.ts envelope, independently exercised by its
      // lockAndValidateGatewayControl natural-expiry test; no C2/model is invoked here.
      return Response.json({ protocol: 'sdk2-cache-v1', requestId: body.request.requestId, code: 'ticket_expired', status: 410,
        retryAction: 'none', fallback: 'none', message: 'ticket expired' }, { status: 410 });
    }
    if (url.pathname === '/t1/cache/v1/operations' && shortReceipts.has(url.searchParams.get('requestId'))) {
      assert.equal(new Headers(init?.headers).get('x-tansr-app-token'), 'synthetic-app-user-token');
      return Response.json(shortReceipts.get(url.searchParams.get('requestId')));
    }
    if (url.pathname === '/t1/cache/v1/bindings' && issueShortTicket) {
      assert.equal(init?.method, 'POST'); assert.equal(shortTickets.size, 0);
      const body = JSON.parse(Buffer.from(init?.body).toString('utf8')); assert.equal(body.intent.kind, 'new');
      const response = await api.fetchImpl(input, init); assert.equal(response.status, 200);
      const receipt = await response.json(), issuedAtMs = Date.now(), expiresAtMs = issuedAtMs + 1500;
      receipt.ticketExpiresAt = new Date(expiresAtMs).toISOString();
      shortTickets.set(receipt.binding.bindingId, { value: receipt.ticket, issuedAtMs, expiresAtMs, operationEpoch: body.request.operationEpoch });
      shortReceipts.set(body.request.requestId, receipt); issueShortTicket = false; return Response.json(receipt);
    }
    const diagnostic = /^\/t1\/cache\/v1\/bindings\/([^/]+)\/diagnostics$/.exec(url.pathname);
    if (!diagnostic) return api.fetchImpl(input, init);
    assert.equal(init?.method ?? 'GET', 'GET');
    assert.equal(new Headers(init?.headers).get('x-tansr-app-token'), 'synthetic-app-user-token');
    const bindingId = decodeURIComponent(diagnostic[1]);
    const response = await api.fetchImpl(`${BASE}/t1/cache/v1/bindings/${encodeURIComponent(bindingId)}`, init);
    assert.equal(response.status, 200); assert.equal((await response.json()).bindingId, bindingId);
    const limit = Number(url.searchParams.get('limit')); assert.ok(Number.isInteger(limit) && limit >= 1 && limit <= 100);
    diagnosticReads.push({ bindingId, after: url.searchParams.get('after'), limit });
    assert.ok(diagnosticReads.length <= 8);
    return Response.json({ protocol: 'sdk2-cache-v1', rows: [], next: null });
  };
  let host, server, agentStore, enabled = true, restarts = 0, runtimePolicy = 'preserve';
  async function start(mode) {
    host = await createServeCacheHost({ security: 'trusted-single-application', directory: join(directory, 'cache'), mode,
      storeId: 'net-cache-public', applicationScopeId: 'synthetic-app', apiBaseUrl: BASE, key: hostKey,
      continuity: { ...continuityOptions, runtimePolicy } });
    agentStore = createServeAgentSessionStore({ dir: join(directory, 'history') });
    const agent = { cwd: directory, store: agentStore,
      platform: { apiBaseUrl: BASE, appId: 'synthetic-app', appKey: 'synthetic-key', fetchImpl },
      logger: { info() {}, error() {} } };
    server = await host.start({ port: 0, host: '127.0.0.1', token: 'unused-synthetic-v1', readyFrame: 'none', agent,
      v2: { authenticate(req) {
        routes.push({ method: req.method, path: new URL(req.url, 'http://127.0.0.1').pathname });
        return enabled ? authenticate(req) : null;
      } } });
  }
  async function stop() {
    if (server) { await server.close(); await server.settleResources?.(); server = undefined; }
    if (host) { snapshots.push(host.readInventory()); await host.dispose(); host = undefined; }
  }
  try { await start('create'); } catch (error) { await stop(); throw error; }
  const processed = new Set(); let pending = false, failure, closing;
  const timer = setInterval(async () => {
    if (pending) return; pending = true;
    try {
      const names = (await readdir(commands)).filter(name => name.endsWith('.json')).sort();
      assert.ok(names.length <= 24, 'Bounded synthetic cache command set.');
      const name = names.find(item => !processed.has(item)); if (!name) return;
      assert.match(name, /^[a-z0-9-]{1,64}\.json$/);
      const raw = await readFile(join(commands, name), 'utf8'); assert.ok(Buffer.byteLength(raw) <= 4096);
      const command = JSON.parse(raw); assert.equal(name, command.id + '.json'); processed.add(name);
      assert.equal(failure, undefined); let value;
      if (command.action === 'restart') {
        assert.ok(restarts < 2); await stop();
        if (command.runtimePolicy !== undefined) { assert.equal(command.runtimePolicy, 'renew-on-restore'); runtimePolicy = command.runtimePolicy; }
        await start('recover'); restarts++;
        value = { url: server.url, restarts };
      } else if (command.action === 'enabled') {
        assert.equal(typeof command.enabled, 'boolean'); enabled = command.enabled; value = { enabled };
      } else if (command.action === 'arm-ticket-expiry') {
        assert.equal(issueShortTicket, false); assert.equal(shortTickets.size, 0); issueShortTicket = true;
        value = { nextIssuedTicketTtlMs: 1500, realWallClock: true };
      } else if (command.action === 'delete-history') {
        assert.equal(command.user, 'net-integration-user'); assert.equal(typeof command.sessionId, 'string');
        await server.settleResources?.(); await agentStore.delete(command.user, command.sessionId);
        value = { absent: (await agentStore.get(command.user, command.sessionId)) === null,
          deletedSources: api.sources().filter(item => item.state === 'deleted').length };
      } else if (command.action === 'inspect') {
        let selected = api.source();
        if (command.sessionId !== undefined) {
          assert.equal(typeof command.sessionId, 'string'); const meta = await agentStore.getMeta('net-integration-user', command.sessionId);
          assert.ok(meta); selected = api.sources().find(item => item.runtimeSessionId === meta.platformSessionId);
          assert.ok(selected, 'The original Store and trusted source must agree on runtime identity.');
        }
        value = { restarts, intents: api.intents.map(item => ({ operation: item.operation, requestId: item.requestId })),
        modelExchanges: api.requests.filter(item => item.path.endsWith('/exchange')).length, runtimeCount: host.readInventory().runtimes.length,
        latestSource: selected, expiryRejections, shortTickets: [...shortTickets].map(([bindingId, row]) => ({ bindingId,
          issuedAt: new Date(row.issuedAtMs).toISOString(), expiresAt: new Date(row.expiresAtMs).toISOString(), operationEpoch: row.operationEpoch })),
        continuityReceipts: [...api.receipts.values()].map(item => ({ operation: item.operation, sourceId: item.source.sourceId,
          runtimeSessionId: item.source.runtimeSessionId, logicalReference: item.binding.logicalRef, state: item.source.state })) };
      }
      else throw new Error('Unknown cache fixture action.');
      const temporary = join(responses, command.id + '.tmp');
      await writeFile(temporary, JSON.stringify({ id: command.id, value }), { flag: 'wx' }); await rename(temporary, join(responses, name));
    } catch (error) {
      if (!failure) { failure = error; await writeFile(join(directory, 'host-failure.json'), JSON.stringify({ type: error.name, message: String(error.message).slice(0, 1024) }), { flag: 'wx' }); }
    } finally { pending = false; }
  }, 20); timer.unref();
  return { url: server.url, close: () => closing ??= (async () => {
    clearInterval(timer); while (pending) await new Promise(resolve => setTimeout(resolve, 10));
    await stop(); if (failure) throw failure;
    return { realPublicCacheHost: true, durableSqlite: true, platform: 'controlled-synthetic', restarts,
      modelExchanges: api.requests.filter(item => item.path.endsWith('/exchange')).length,
      continuityOperations: api.intents.map(item => item.operation), runtimeSnapshots: snapshots.map(item => item.runtimes.length), diagnosticReads, expiryRejections, routes };
  })() };
}
