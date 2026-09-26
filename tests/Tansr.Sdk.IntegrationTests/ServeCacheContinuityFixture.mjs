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
  const api = continuityApi(), routes = [], snapshots = [];
  let host, server, agentStore, enabled = true, restarts = 0, runtimePolicy = 'preserve';
  async function start(mode) {
    host = await createServeCacheHost({ security: 'trusted-single-application', directory: join(directory, 'cache'), mode,
      storeId: 'net-cache-public', applicationScopeId: 'synthetic-app', apiBaseUrl: BASE, key: hostKey,
      continuity: { ...continuityOptions, runtimePolicy } });
    agentStore = createServeAgentSessionStore({ dir: join(directory, 'history') });
    const agent = { cwd: directory, store: agentStore,
      platform: { apiBaseUrl: BASE, appId: 'synthetic-app', appKey: 'synthetic-key', fetchImpl: api.fetchImpl },
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
        latestSource: selected, continuityReceipts: [...api.receipts.values()].map(item => ({ operation: item.operation, sourceId: item.source.sourceId,
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
      continuityOperations: api.intents.map(item => item.operation), runtimeSnapshots: snapshots.map(item => item.runtimes.length), routes };
  })() };
}
