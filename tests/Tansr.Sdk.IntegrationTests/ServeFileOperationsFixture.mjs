// Real kernel, HTTP, permissions and execution spool. Only the synthetic upstream is controlled.
import assert from 'node:assert/strict';
import { randomBytes, createHash } from 'node:crypto';
import { mkdir, readFile, readdir, rename, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startFileOperationsFixture({ source, directory, authenticate }) {
  await mkdir(directory, { recursive: true });
  const commands = join(directory, 'host-commands'), responses = join(directory, 'host-responses');
  await mkdir(commands); await mkdir(responses);
  const load = file => import(pathToFileURL(join(source, file)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const { openSqliteArchiveSpool, clientToolDefinitionDigest } = await load('packages/server/src/extensions/index.ts');
  const { defaultAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const names = ['Read', 'Write', 'Edit', 'List', 'Glob', 'Grep', 'BusinessLookup'];
  const scope = { applicationScopeId: 'net-files-app', endUserId: 'net-integration-user', authorizationRevision: '1' };
  const declaration = { name: 'BusinessLookup', description: 'Return a synthetic order from the native application', parameters: { id: { type: 'string' } }, readOnly: true };
  const privateDirectory = join(directory, 'serve-private'); await mkdir(privateDirectory);
  const privatePath = join(privateDirectory, 'sentinel.txt');
  const privateValue = 'SERVE_PRIVATE_' + randomBytes(24).toString('hex'); await writeFile(privatePath, privateValue, { flag: 'wx' });
  await writeFile(join(directory, 'fixture.json'), JSON.stringify({ declaration, definitionDigest: clientToolDefinitionDigest(declaration), privateDirectory, privatePath }));
  const cap = { bytes: 16 * 1048576, records: 2048 };
  const spool = await openSqliteArchiveSpool({ path: join(directory, 'execution.sqlite'), mode: 'create', storeId: 'net-files-spool',
    bindings: [{ bindingId: 'files', applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId,
      limits: { binding: cap, application: cap, endUser: cap } }], globalLimit: cap,
    maxReservations: 256, maxEntries: 2048, maxOperations: 2048, maxDatabasePages: 8192 });
  const capabilities = structuredClone(defaultAppCapabilities('desktop')); capabilities.tools.customTools = true;
  capabilities.execution = { version: 'bound-device-v1', boundDevice: { tools: { read: true, write: true, edit: true, list: true, glob: true, grep: true, customTools: true } } };
  const fake = createFakePlatform({ features: [], bundleExtra: { app: { platform: 'desktop' }, capabilities } });
  const requests = new Map(), results = [], operations = [], routes = [];
  let enabled = true, failure, pending = false, closing = false, memoryCalls = 0;
  const frame = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  const response = (request, text, tool) => new Response(frame('t.open', { exchangeId: `files-${requests.size}-${results.length}`, model: request.model, protocol: 'twp/1' }) +
    (tool ? frame('t.delta', { i: 0, t: 'tool_use', id: tool.id, name: tool.name, vJson: JSON.stringify(tool.args) }) : frame('t.delta', { i: 0, t: 'text', v: text })) +
    frame('t.close', { stop: tool ? 'tool_use' : 'end_turn' }), { headers: { 'content-type': 'text/event-stream' } });
  const fetchImpl = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    assert.equal(url.origin, FAKE_API_BASE);
    if (url.pathname !== '/t1/exchange') return fake.fetchImpl(input, init);
    try {
      const request = JSON.parse(String(init?.body));
      assert.ok(!JSON.stringify(request.thread).includes(privateValue), 'Serve private content must never reach the model.');
      if (request.meta?.purpose === 'memory') { assert.ok(++memoryCalls <= 40); return response(request, 'nothing to save'); }
      if (JSON.stringify(request.thread).includes('PERMISSION ADJUDICATION')) return response(request, '{"verdict":"endorse","reason":"bounded synthetic device task"}');
      const prompt = request.thread.filter(item => item.role === 'user').flatMap(item => item.blocks ?? []).filter(block => block.t === 'text').map(block => block.v).findLast(text => text.startsWith('NET_FILES:'));
      assert.ok(prompt, 'Only the named synthetic fixture protocol may drive this upstream.');
      const action = JSON.parse(prompt.slice('NET_FILES:'.length)); assert.ok(names.includes(action.name)); assert.match(action.id, /^[a-z0-9-]+$/);
      const count = (requests.get(action.id) ?? 0) + 1; requests.set(action.id, count); assert.ok(count <= 2 && requests.size <= 32);
      if (count === 1) return response(request, null, action);
      const original = request.thread.flatMap(item => item.blocks ?? []).findLast(block => block.t === 'tool_result' && block.toolUseId === action.id);
      assert.ok(original, 'The result must return through the actual kernel, not a fixture-generated receipt.');
      results.push({ id: action.id, name: action.name, result: original });
      return response(request, `FILES_SETTLED:${action.id}`);
    } catch (error) { failure = { stage: 'upstream', message: String(error.message) }; throw error; }
  };
  const build = createAgentSessionFactory({ cwd: privateDirectory,
    store: createServeAgentSessionStore({ dir: join(directory, 'sessions'), ownership: {} }),
    platform: { apiBaseUrl: FAKE_API_BASE, appId: scope.applicationScopeId, appKey: 'synthetic-only', fetchImpl, maxOutputTokens: 256 },
    execution: { applicationScopeId: scope.applicationScopeId,
      authorize(request, user) {
        const allowed = authenticate(request)?.endUserId === scope.endUserId && user === scope.endUserId;
        return { controller: allowed && request.headers['x-net-role'] === 'controller',
          ...(allowed && request.headers['x-net-role'] === 'executor' ? { executorId: 'net-files-pc' } : {}) };
      },
      readPolicy: async () => ({ authorizationRevision: scope.authorizationRevision, tools: enabled ? names : [] }),
      spoolFor: () => ({ spool, bindingId: 'files' }) } });
  const unsubscribe = build.factory.execution.observeChanges(change => {
    if (change.operation && !operations.some(item => item.operationId === change.operation.operationId)) operations.push(change.operation);
  });
  let server;
  try { server = await startServer({ host: '127.0.0.1', port: 0, token: 'unused', readyFrame: 'none', heartbeatMs: 0,
    createSession: { create() { throw new Error('Legacy v1 unused'); } },
    v2: { createSession: build.factory, governance: { sweepIntervalMs: 0 }, authenticate(request) {
      routes.push({ method: request.method, path: new URL(request.url, 'http://127.0.0.1').pathname }); return authenticate(request);
    } } }); }
  catch (error) { unsubscribe(); await build.flush(); spool.close(); throw error; }
  const inspect = async () => ({ requests: Object.fromEntries(requests), results, operations, memoryCalls,
    hostSentinelUnchanged: await readFile(privatePath, 'utf8') === privateValue, hostSentinelSha256: createHash('sha256').update(privateValue).digest('hex') });
  const seen = new Set();
  const timer = setInterval(async () => {
    if (closing || pending || failure) return; pending = true;
    try {
      const files = (await readdir(commands)).filter(file => file.endsWith('.json')).sort(); assert.ok(files.length <= 32);
      const file = files.find(file => !seen.has(file)); if (!file) return; seen.add(file);
      assert.match(file, /^[a-z0-9-]{1,64}\.json$/);
      const bytes = await readFile(join(commands, file)); assert.ok(bytes.length <= 4096);
      const command = JSON.parse(bytes.toString('utf8')); assert.equal(file, `${command.id}.json`);
      let value;
      if (command.action === 'inspect') value = await inspect();
      else if (command.action === 'revoke') { enabled = false; scope.authorizationRevision = '2'; value = { revoked: true }; }
      else throw new Error('Unknown trusted fixture command.');
      await writeFile(join(responses, `${command.id}.tmp`), JSON.stringify({ id: command.id, value }), { flag: 'wx' });
      await rename(join(responses, `${command.id}.tmp`), join(responses, file));
    } catch (error) { failure = { stage: 'host-command', message: String(error.message) }; await writeFile(join(directory, 'host-failure.json'), JSON.stringify(failure)); }
    finally { pending = false; }
  }, 10);
  return { url: server.url, async close() {
    closing = true; clearInterval(timer); while (pending) await new Promise(resolve => setTimeout(resolve, 10));
    try { await server.close(); await server.settleResources?.(); await build.flush(); }
    finally { unsubscribe(); spool.close(); }
    const result = { realKernel: true, realExecutionSpool: true, controlledModel: true, ...await inspect(), routes, failure: failure ?? null };
    await writeFile(join(directory, 'result.json'), JSON.stringify(result));
    if (failure) throw new Error(`${failure.stage}: ${failure.message}`); assert.equal(result.hostSentinelUnchanged, true);
    return result;
  } };
}
