// Acceptance-only assembly. Original Serve/kernel/execution/spool; only model/platform are synthetic.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { randomBytes } from 'node:crypto';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startSharedClientsFixture({ source, directory }) {
  const load = file => import(pathToFileURL(join(source, file)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const { openSqliteArchiveSpool } = await load('packages/server/src/extensions/index.ts');
  const { defaultAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const scope = { applicationScopeId: 'unified-app', endUserId: 'u-unified', authorizationRevision: '1' };
  const users = ['u-unified', 'u-csharp', 'u-electron'];
  const roles = { ui: 'u-unified', observer: 'u-unified', device: 'u-unified', csharp: 'u-csharp', 'csharp-device': 'u-csharp', electron: 'u-electron' };
  const identity = req => Object.keys(roles).find(role => req.headers.authorization === `Bearer demo1.unified.${role}`);
  const sessions = { mobileStage: 'waiting' }, requests = [], results = [], timeline = [], operations = [];
  const calls = new Map(), cap = { bytes: 16 * 1048576, records: 1024 }, spools = new Map();
  let revoked = false, barrier = false, failure, memoryCalls = 0, slowStream;
  await mkdir(join(directory, 'serve-private'), { recursive: true });
  const sentinel = randomBytes(32).toString('hex');
  const sentinelPath = join(directory, 'serve-private', 'sentinel.txt');
  await writeFile(sentinelPath, sentinel, { flag: 'wx' });
  for (const user of users) spools.set(user, await openSqliteArchiveSpool({ path: join(directory, `${user}.sqlite`), mode: 'create', storeId: user,
    bindings: [{ bindingId: user, applicationScopeId: scope.applicationScopeId, endUserId: user,
      limits: { binding: cap, application: cap, endUser: cap } }], globalLimit: cap,
    maxReservations: 128, maxEntries: 1024, maxOperations: 2048, maxDatabasePages: 8192 }));
  const base = defaultAppCapabilities('desktop');
  const fake = createFakePlatform({ features: [], bundleExtra: { app: { platform: 'desktop' }, capabilities: { ...base,
    execution: { version: 'bound-device-v1', boundDevice: { tools: { read: true, write: true } } } } } });
  const frame = (type, data) => `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`;
  const fetchImpl = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    assert.equal(url.origin, FAKE_API_BASE, 'Synthetic upstream only.');
    if (url.pathname !== '/t1/exchange') return fake.fetchImpl(input, init);
    try {
      const body = JSON.parse(String(init?.body)), text = JSON.stringify(body.thread);
      assert.ok(!text.includes(sentinel), 'Serve private content must not reach a model.');
      let delta, stop = 'end_turn', marker = 'auxiliary';
      if (body.meta?.purpose === 'memory') { assert.ok(++memoryCalls <= 32); delta = { i: 0, t: 'text', v: 'nothing to save' }; }
      else if (text.includes('PERMISSION ADJUDICATION')) delta = { i: 0, t: 'text', v: '{"decision":"endorse","reason":"authorized synthetic workspace"}' };
      else {
        const prompts = body.thread.filter(item => item.role === 'user').flatMap(item => item.blocks ?? []).filter(block => block.t === 'text').map(block => block.v).join('\n');
        const mobile = [...prompts.matchAll(/USDK:windows:Write:mobile-(android|harmony|ios)/g)].at(-1);
        const native = [...prompts.matchAll(/NET_SHARED:([^\n]+)/g)].at(-1);
        assert.ok(mobile || native, 'Only fixed same-Serve synthetic scenarios are accepted.');
        const action = mobile ? { id: mobile[0], name: 'Write', args: { file_path: `/workspace/work/mobile-${mobile[1]}.txt`, contents: `native-mobile-${mobile[1]}` } } : JSON.parse(native[1]);
        marker = action.id; assert.match(marker, /^[a-zA-Z0-9:_-]{1,160}$/);
        const count = (calls.get(marker) ?? 0) + 1; calls.set(marker, count);
        assert.ok(calls.size <= 48 && count <= 2, 'No repeated model/side-effect attempt.');
        if (action.stream === true) {
          assert.equal(marker, 'csharp-slow-stream'); assert.equal(count, 1);
          const before = server.sseStats().slowDisconnects;
          slowStream = { frames: 0, maxQueued: 0, sawPaused: false, disconnected: false };
          const encoder = new TextEncoder();
          const stream = new ReadableStream({ async start(controller) {
            try {
              controller.enqueue(encoder.encode(frame('t.open', { exchangeId: marker, model: body.model, protocol: 'twp/1' })));
              const until = Date.now() + 30000;
              while (server.sseStats().slowDisconnects === before) {
                assert.ok(slowStream.frames < 12000 && Date.now() < until, 'Real paused SSE must hit its original bounded queue.');
                for (let i = 0; i < 50; i++) controller.enqueue(encoder.encode(frame('t.delta', { i: 0, t: 'text', v: `${++slowStream.frames}|` + 'z'.repeat(2000) })));
                await new Promise(resolve => setTimeout(resolve, 3));
                const stats = server.sseStats(); slowStream.maxQueued = Math.max(slowStream.maxQueued, stats.queuedBytes);
                slowStream.sawPaused ||= stats.pausedSubscribers > 0;
              }
              slowStream.disconnected = true;
              assert.ok(slowStream.sawPaused && slowStream.maxQueued <= 2 * 256 * 1024 + 50 * 2300, 'Both real subscriber queues stay bounded.');
              controller.enqueue(encoder.encode(frame('t.usage', { inTokens: 11, outTokens: 3 }) + frame('t.close', { stop: 'end_turn' }))); controller.close();
            } catch (error) { failure = error; controller.error(error); }
          } });
          return new Response(stream, { headers: { 'content-type': 'text/event-stream' } });
        }
        if (count === 1 && action.name) { delta = { i: 0, t: 'tool_use', id: marker, name: action.name, vJson: JSON.stringify(action.args) }; stop = 'tool_use'; }
        else {
          if (action.name) {
            const result = body.thread.flatMap(item => item.blocks ?? []).findLast(block => block.t === 'tool_result' && block.toolUseId === marker);
            assert.ok(result, 'The actual kernel tool result must return.'); results.push({ id: marker, result });
          }
          delta = { i: 0, t: 'text', v: `SHARED_SETTLED:${marker}` };
        }
      }
      return new Response(frame('t.open', { exchangeId: marker, model: body.model, protocol: 'twp/1' }) + frame('t.delta', delta) +
        frame('t.usage', { inTokens: 11, outTokens: 3 }) + frame('t.close', { stop }), { headers: { 'content-type': 'text/event-stream' } });
    } catch (error) { failure = error; throw error; }
  };
  const sessionStore = createServeAgentSessionStore({ dir: join(directory, 'sessions') });
  const build = createAgentSessionFactory({ cwd: join(directory, 'serve-private'), store: sessionStore,
    checkpoints: { autoBeforeCompact: false }, platform: { apiBaseUrl: FAKE_API_BASE, appId: scope.applicationScopeId, appKey: 'synthetic-no-key', fetchImpl },
    execution: { applicationScopeId: scope.applicationScopeId,
      authorize(req, user) { const role = identity(req); const allowed = roles[role] === user && !(revoked && user === 'u-csharp');
        return { controller: allowed && ['ui', 'csharp', 'electron'].includes(role),
          ...(allowed && ['device', 'csharp-device'].includes(role) ? { executorId: role === 'device' ? 'shared-mobile-pc' : 'shared-csharp-pc' } : {}) }; },
      readPolicy: async user => ({ authorizationRevision: revoked && user === 'u-csharp' ? '2' : '1', tools: revoked && user === 'u-csharp' ? [] : ['Read', 'Write'] }),
      spoolFor: ({ endUserId }) => ({ spool: spools.get(endUserId), bindingId: endUserId }) } });
  const unsubscribe = build.factory.execution.observeChanges(change => {
    if (change.operation && !operations.some(item => item.operationId === change.operation.operationId)) operations.push(change.operation);
  });
  let server;
  try { server = await startServer({ host: '127.0.0.1', port: 18789, token: 'unused-synthetic', readyFrame: 'none', heartbeatMs: 0,
    sse: { maxBufferBytes: 256 * 1024, slowGraceMs: 0 },
    createSession: { create() { throw new Error('Legacy transport factory unused.'); } },
    v2: { createSession: build.factory, store: build.storeReader, governance: { sweepIntervalMs: 0 }, authenticate(req) {
      const role = identity(req), user = roles[role];
      assert.ok(requests.length < 16384); requests.push({ at: Date.now(), role, method: req.method, path: new URL(req.url, server?.url ?? 'http://127.0.0.1').pathname });
      return user && !(revoked && user === 'u-csharp') ? { endUserId: user } : null;
    } } }); }
  catch (error) { unsubscribe(); try { await build.flush(); } finally { for (const spool of spools.values()) spool.close(); } throw error; }
  async function state() { return { sessions, barrier, revoked, results, operations, calls: Object.fromEntries(calls), timeline, slowStream, sse: server.sseStats(),
    hostSentinelUnchanged: await readFile(sentinelPath, 'utf8') === sentinel,
    serviceFiles: await readdir(join(directory, 'serve-private')),
    usage: Object.fromEntries([...spools].map(([user, spool]) => [user, spool.snapshot().usage])) }; }
  const control = createServer((req, res) => { void (async () => {
    const url = new URL(req.url, 'http://127.0.0.1');
    const reply = (status, value) => { res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' }); res.end(JSON.stringify(value)); };
    if (req.method === 'GET' && url.pathname === '/state') return reply(200, await state());
    if (req.method === 'GET' && url.pathname === '/mobile-ready') {
      const platform = url.searchParams.get('platform'); assert.ok(['android', 'harmony', 'ios'].includes(platform));
      return sessions.windows ? reply(200, { sessionId: sessions.windows, prompt: `USDK:windows:Write:mobile-${platform}` }) : reply(409, { ready: false });
    }
    if (req.method !== 'POST' || req.headers.authorization !== 'Bearer fixture-control') return reply(403, {});
    let bytes = ''; for await (const part of req) { bytes += part; assert.ok(Buffer.byteLength(bytes) <= 8192); }
    const input = JSON.parse(bytes);
    if (url.pathname === '/sessions') Object.assign(sessions, input);
    else if (url.pathname === '/barrier') barrier = true;
    else if (url.pathname === '/revoke') revoked = true;
    else if (url.pathname === '/pressure') {
      const spool = spools.get('u-csharp'), used = spool.snapshot().usage.global;
      spool.reserve({ bindingId: 'u-csharp', operationKey: 'test-pressure', intentDigest: 'a'.repeat(64),
        originalMax: { bytes: cap.bytes - used.bytes, records: 1 }, terminalReserve: { bytes: 0, records: 0 } });
      assert.equal(spool.snapshot().usage.global.bytes, cap.bytes);
    } else if (url.pathname === '/timeline') { assert.ok(timeline.length < 64); timeline.push({ ...input, at: Date.now() }); }
    else return reply(404, {});
    reply(200, await state());
  })().catch(error => { failure = error; res.writeHead(500).end(JSON.stringify({ error: String(error) })); }); });
  try { await new Promise((resolve, reject) => { control.once('error', reject); control.listen(18890, '127.0.0.1', resolve); }); }
  catch (error) { try { await server.close(); await server.settleResources?.(); await build.flush(); } finally { unsubscribe(); for (const spool of spools.values()) spool.close(); } throw error; }
  return { url: server.url, controlUrl: 'http://127.0.0.1:18890', state,
    async close() {
      try {
      control.closeAllConnections(); await new Promise(resolve => control.close(resolve));
      await server.close(); await server.settleResources?.(); await build.flush(); unsubscribe();
      const billing = [];
      for (const [key, user] of [['windows', 'u-unified'], ['csharp', 'u-csharp'], ['electron', 'u-electron']]) {
        if (!sessions[key]) continue;
        const stored = await sessionStore.get(user, sessions[key]);
        assert.ok(stored?.usageSnapshot, 'Actual persisted usage snapshot required: ' + key);
        for (const other of users.filter(value => value !== user)) assert.equal(await sessionStore.get(other, sessions[key]), null, 'Stored history and usage must be scoped to the original subject.');
        billing.push({ user, sessionId: sessions[key], usageSnapshot: stored.usageSnapshot });
      }
      const value = { ...(await state()), requests, memoryCalls, billing, realKernel: true, controlledModel: true, failure: failure?.stack ?? null };
      await writeFile(join(directory, 'serve-shared-evidence.json'), JSON.stringify(value, null, 2));
      if (failure) throw failure; assert.equal(value.hostSentinelUnchanged, true); assert.deepEqual(value.serviceFiles, ['sentinel.txt']);
      return value;
      } finally { unsubscribe(); for (const spool of spools.values()) spool.close(); }
    } };
}
