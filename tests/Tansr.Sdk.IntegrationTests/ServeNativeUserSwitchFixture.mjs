// Native identity acceptance uses the original SDK1 factory, ownership, history and SSE.
// Only authentication tickets and the upstream platform/model are synthetic.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyServeSourceSnapshot } from '../../scripts/serve-source-snapshot.mjs';

export async function startNativeUserSwitchFixture({ source, directory }) {
  directory = resolve(directory);
  await mkdir(directory, { recursive: true });
  const load = path => import(pathToFileURL(join(source, path)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const { zeroAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const { setEnglish } = await load('packages/server/test/helpers.ts'); setEnglish();
  const users = ['native-user-a', 'native-user-b'].map(endUserId => ({
    endUserId, principal: endUserId, token: randomBytes(32).toString('hex'),
    scope: { applicationScopeId: 'native-ui-app', endUserId, authorizationRevision: '1' },
  }));
  const routes = [], creations = [], exchanges = [], storeErrors = [], modelErrors = [], issued = new Set();
  const capability = structuredClone(zeroAppCapabilities('desktop'));
  capability.tools.customTools = true;
  const fake = createFakePlatform({ bundleExtra: { app: { platform: 'desktop' }, capabilities: capability,
    models: [{ handle: 'ui-main', modelId: 'ui-main', displayName: 'Identity synthetic', protocol: 'twp',
      capabilities: {}, contextWindow: 32768 }], aliases: { main: 'ui-main' } } });
  const frame = (name, data) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`;
  const controlledFetch = async (input, init) => {
    const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
    assert.equal(url.origin, FAKE_API_BASE, 'Only the synthetic platform may be accessed.');
    if (url.pathname !== '/t1/exchange') return fake.fetchImpl(input, init);
    const applicationToken = new Headers(init?.headers).get('x-tansr-app-token');
    const tokenMatch = /^tok\.(native-user-[ab])\.[0-9]+$/.exec(applicationToken ?? '');
    assert.ok(tokenMatch, 'The original platform mint must preserve the authenticated end user.');
    const endUserId = tokenMatch[1], request = JSON.parse(String(init?.body));
    assert.notEqual(request.meta?.purpose, 'memory', 'This identity fixture has no configured memory source.');
    assert.ok(exchanges.length < 12, 'The original bounded native identity scenario must not loop.');
    const texts = request.thread.filter(message => message.role === 'user')
      .flatMap(message => message.blocks.filter(block => block.t === 'text').map(block => block.v));
    const marker = texts.findLast(text => /^UI_USER_(?:A|B|PENDING_A)_(?:WPF|WINFORMS)$/.test(text));
    assert.ok(marker, 'Only the original named native identity scenarios are accepted.');
    const [, scenario, host] = /^UI_USER_(A|B|PENDING_A)_(WPF|WINFORMS)$/.exec(marker);
    assert.equal(endUserId, scenario === 'B' ? users[1].endUserId : users[0].endUserId);
    if (endUserId === users[1].endUserId) {
      const body = JSON.stringify(request.thread);
      for (const privateMarker of ['UI_USER_A_', 'UI_USER_PENDING_A_', 'UI_DONE_USER_A_', 'UI_DONE_USER_PENDING_A_', 'NATIVE_PRIVATE_DRAFT_A'])
        assert.ok(!body.includes(privateMarker), 'User B must not submit or receive user A conversation or unsent draft.');
    }
    exchanges.push({ endUserId, marker, thread: request.thread, model: request.model, at: Date.now() });
    let tool;
    if (scenario === 'PENDING_A' && !issued.has(marker)) {
      assert.ok(request.tools.some(value => value.name === 'set_window_title'), 'The actual native business declaration is required.');
      issued.add(marker);
      tool = { id: `native-user-a-pending-title-${host.toLowerCase()}`, name: 'set_window_title', args: { title: `User A pending title ${host}` } };
    }
    return new Response(frame('t.open', { exchangeId: `identity-${exchanges.length}`, model: request.model, protocol: 'twp/1' }) +
      (tool ? frame('t.delta', { i: 0, t: 'tool_use', id: tool.id, name: tool.name, vJson: JSON.stringify(tool.args) }) :
        frame('t.delta', { i: 0, t: 'text', v: marker.replace('UI_USER_', 'UI_DONE_USER_') })) +
      frame('t.usage', { inTokens: 11, outTokens: 3, cacheRTokens: 0, cacheWTokens: 0 }) +
      frame('t.close', { stop: tool ? 'tool_use' : 'end' }), { headers: { 'content-type': 'text/event-stream' } });
  };
  const fetchImpl = async (input, init) => {
    try { return await controlledFetch(input, init); }
    catch (error) { modelErrors.push({ name: error.name, message: String(error.message) }); throw error; }
  };
  const store = createServeAgentSessionStore({ dir: join(directory, 'sessions'), ownership: {} });
  const build = createAgentSessionFactory({ cwd: directory, store, configuration: {}, permissionTimeoutMs: 60000,
    onStoreError(sessionId, error) { storeErrors.push({ sessionId, code: error?.code ?? 'store_failed' }); },
    platform: { apiBaseUrl: FAKE_API_BASE, appId: 'native-ui-app', appKey: 'synthetic-only', fetchImpl } });
  const factory = { ...build.factory, async create(init) {
    const created = await build.factory.create(init);
    creations.push({ endUserId: created.handle.endUserId, sessionId: created.handle.sessionId, resumed: created.resumed });
    return created;
  } };
  const server = await startServer({ host: '127.0.0.1', port: 0, token: 'synthetic-unused-v1', readyFrame: 'none', heartbeatMs: 0,
    createSession: { create() { throw new Error('The native example must consume original SDK1 v2 routes.'); } },
    terminal: { contract: 'terminal-services-v1', scopeFor: reference => {
      const user = users.find(value => value.endUserId === reference.endUserId); assert.ok(user); return user.scope;
    } },
    v2: { createSession: factory, store: build.storeReader, governance: { sweepIntervalMs: 0 }, authenticate(request) {
      const user = users.find(value => request.headers.authorization === `Bearer ${value.token}`);
      routes.push({ endUserId: user?.endUserId ?? null, method: request.method, path: new URL(request.url, 'http://127.0.0.1').pathname, at: Date.now() });
      return user ? { endUserId: user.endUserId } : null;
    } } });
  let closing;
  return { url: server.url, users, close() {
    return closing ??= (async () => {
      const errors = []; let report;
      try { report = await server.drain({ timeoutMs: 5000 }); } catch (error) { errors.push(error); }
      try { await server.settleResources(); } catch (error) { errors.push(error); }
      try { await build.flush(); } catch (error) { errors.push(error); }
      const result = { realKernel: true, realStore: true, executionEnabled: false, memoryConfigured: false,
        users: users.map(({ endUserId, principal, scope }) => ({ endUserId, principal, scope })), routes, creations, exchanges, storeErrors, modelErrors,
        report: report ?? null, sse: server.sseStats(), cleanupErrors: errors.map(error => String(error?.message ?? error)) };
      await writeFile(join(directory, 'result.json'), JSON.stringify(result), { flag: 'wx' });
      if (errors.length) throw new AggregateError(errors, 'Native identity fixture cleanup failed.');
      assert.deepEqual(storeErrors, []);
      assert.deepEqual(modelErrors, []);
      assert.equal(result.sse.activeSubscribers, 0); assert.equal(result.sse.queuedBytes, 0);
      assert.equal(result.sse.heartbeatTimerActive, false);
      return result;
    })();
  } };
}

// Standalone entry avoids starting unrelated media, archive and memory fixtures.
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const source = process.env.TANSR_SERVE_SOURCE, directory = process.env.TANSR_NATIVE_IDENTITY_DIRECTORY;
  const snapshot = process.env.TANSR_SERVE_SOURCE_SNAPSHOT;
  assert.ok(source && directory && snapshot);
  const sourceBefore = verifyServeSourceSnapshot(source, snapshot);
  const fixture = await startNativeUserSwitchFixture({ source, directory });
  // These are generated synthetic tickets in the local acceptance directory, never production credentials.
  await writeFile(join(directory, 'ready.json'), JSON.stringify({ url: fixture.url, users: fixture.users, source: sourceBefore }), { flag: 'wx' });
  let stopping = false;
  const stop = async () => {
    if (stopping) return; stopping = true; clearInterval(timer);
    try {
      await fixture.close();
      await writeFile(join(directory, 'source-final.json'), JSON.stringify(verifyServeSourceSnapshot(source, snapshot)), { flag: 'wx' });
    } catch (error) {
      process.exitCode = 1;
      await writeFile(join(directory, 'failure.txt'), error.stack ?? String(error), { flag: 'wx' });
    }
  };
  const timer = setInterval(async () => {
    try { await readFile(join(directory, 'stop')); await stop(); }
    catch (error) { if (error.code !== 'ENOENT') { process.exitCode = 1; clearInterval(timer); throw error; } }
  }, 100);
  process.on('SIGINT', stop); process.on('SIGTERM', stop);
}
