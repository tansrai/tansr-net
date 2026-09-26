// C# calls the original public Serve endpoints. Hooks, skills and child-agent policies are
// trusted deployment configuration; no HTTP field uploads code or selects a host path.
import assert from 'node:assert/strict';
import { mkdir, readFile, readdir, rename, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createServer } from 'node:http';

export async function startTrustedExtensionsFixture({ source, directory, authenticate }) {
  await mkdir(directory, { recursive: true });
  const commands = join(directory, 'host-commands'), responses = join(directory, 'host-responses');
  await mkdir(commands); await mkdir(responses);
  const load = file => import(pathToFileURL(join(source, file)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const { openSqliteArchiveSpool } = await load('packages/server/src/extensions/index.ts');
  const { SkillRegistry, budgetSpendOf } = await load('packages/kernel/src/index.ts');
  const { defineTool, defaultAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const scope = { applicationScopeId: 'net-trusted-app', endUserId: 'net-integration-user', authorizationRevision: '1' };
  const names = ['TrustedOrder', 'Skill', 'Read', 'Write', 'Task', 'SpawnAgent', 'AgentFollowup', 'NativeMcp'];
  const cap = { bytes: 16 * 1048576, records: 1024 };
  const spool = await openSqliteArchiveSpool({ path: join(directory, 'executions.sqlite'), mode: 'create', storeId: 'net-trusted-store',
    bindings: [{ bindingId: 'trusted', applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId,
      limits: { binding: cap, application: cap, endUser: cap } }], globalLimit: cap,
    maxReservations: 128, maxEntries: 1024, maxOperations: 1024, maxDatabasePages: 8192 });
  // Defaults are shared by the SDK; each synthetic application owns its mutable policy.
  const capabilities = structuredClone(defaultAppCapabilities('desktop'));
  capabilities.tools.agent = true; capabilities.tools.customTools = true; capabilities.tools.skills = true;
  capabilities.execution = { version: 'bound-device-v1', boundDevice: { tools: { read: true, write: true, skills: true, customTools: true } } };
  const fake = createFakePlatform({ features: [], bundleExtra: { app: { platform: 'desktop' }, capabilities } });
  const events = [], notices = [], requests = [], subjects = [], mcpCalls = [], decisions = [], handles = new Map();
  let next = null, hookMode = 'allow', promptBlocked = false, executeCount = 0, hookCalls = 0, closes = 0;
  let childId = '', childCalls = 0, failure, busy = false, closing = false;
  async function recordFailure(stage, error, details = {}) {
    if (failure) return;
    failure = { stage, message: String(error.message), stack: String(error.stack), ...details };
    await writeFile(join(directory, 'host-failure.tmp'), JSON.stringify(failure));
    await rename(join(directory, 'host-failure.tmp'), join(directory, 'host-failure.json'));
  }
  const governance = { enabled: false, sessionId: null, mainCalls: 0, childCalls: 0, adjudicationCalls: 0 };
  const frame = (name, data) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`;
  const response = (request, answer, usage = { inTokens: 0, outTokens: 0, cacheRTokens: 0, cacheWTokens: 0 }) => new Response(frame('t.open', { exchangeId: `trusted-${requests.length}`, model: request.model, protocol: 'twp/1' }) +
    (typeof answer === 'string' ? frame('t.delta', { i: 0, t: 'text', v: answer }) :
      (Array.isArray(answer) ? answer : [answer]).map((tool, i) => frame('t.delta', { i, t: 'tool_use', id: tool.id ?? `trusted-call-${requests.length}-${i}`, name: tool.name, vJson: JSON.stringify(tool.args) })).join('')) +
    frame('t.usage', usage) +
    frame('t.close', { stop: typeof answer === 'string' ? 'end_turn' : 'tool_use' }), { headers: { 'content-type': 'text/event-stream' } });
  const fetchImpl = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    assert.equal(url.origin, FAKE_API_BASE);
    if (url.pathname !== '/t1/exchange') return fake.fetchImpl(input, init);
    let request;
    try {
      request = JSON.parse(String(init?.body)); assert.ok(requests.length < 48);
      if (failure) throw new Error(`The controlled upstream already failed: ${failure.message}`);
      if (request.meta?.purpose === 'memory') return response(request, 'nothing to save');
      const tools = (request.tools ?? []).map(tool => tool.name);
      // Tool availability is the subject under test, not a reliable request identity.
      // Only the real child prompt names this lane; empty main tools must fail as main.
      const textBlocks = (request.thread ?? []).filter(message => message.role === 'user')
        .flatMap(message => message.blocks ?? []).filter(block => block.t === 'text').map(block => block.v);
      const child = textBlocks.some(text => typeof text === 'string' &&
        /^(?:Synthetic foreground task|Synthetic resident task|Continue the synthetic task|Consider two synthetic writes\.)$/.test(text));
      // A permission adjudicator is a separate original-kernel request, not a child.
      if (JSON.stringify(request.thread).includes('PERMISSION ADJUDICATION')) {
        if (governance.enabled) {
          governance.adjudicationCalls++; assert.equal(governance.adjudicationCalls, 1, 'Spent budget must prevent a second real adjudication.');
          return response(request, '{"verdict":"reject","reason":"bounded synthetic write denied"}', { inTokens: 10, outTokens: 20, cacheRTokens: 30, cacheWTokens: 10 });
        }
        return response(request, '{"verdict":"endorse","reason":"bounded synthetic task"}');
      }
      requests.push({ child, tools, thread: request.thread });
      if (governance.enabled) {
        if (tools.includes('Task')) {
          governance.mainCalls++; assert.equal(governance.mainCalls, 1, 'Budget terminal and restored budget must prevent another main request.');
          return response(request, { name: 'Task', args: { description: 'bounded governance child', prompt: 'Consider two synthetic writes.', tools: ['Write'] } });
        }
        governance.childCalls++; assert.equal(governance.childCalls, 1); assert.deepEqual(tools, ['Write']);
        return response(request, [0, 1].map(i => ({ id: `net-governance-write-${i}`, name: 'Write', args: { file_path: `/workspace/work/denied-${i}.txt`, contents: 'synthetic' } })));
      }
      if (child) {
        childCalls++; assert.ok(tools.includes('Read'), 'Children inherit only the selected device read capability.');
        if (childCalls % 2 === 1) return response(request, { name: 'Read', args: { file_path: '/workspace/work/child-sentinel.txt' } });
        assert.ok(JSON.stringify(request.thread).includes('CHILD_DEVICE_SENTINEL'), 'The actual child must consume its original C# device read.');
        return response(request, 'TRUSTED_CHILD_DONE');
      }
      const answer = next;
      if (answer) assert.ok(tools.includes(answer.name), `Planned main tool ${answer.name} must be visible; actual tools: ${tools.join(', ') || '(none)'}`);
      next = null;
      if (answer?.name === 'AgentFollowup') answer.args.agent_id = childId;
      return response(request, answer ?? 'TRUSTED_PARENT_DONE');
    } catch (error) {
      await recordFailure('upstream', error, { request, executions: [...handles.keys()].map(sessionId => {
        try { return build.factory.execution.session(scope.endUserId, sessionId).executionBoundary(); }
        catch { return { sessionId, released: true }; }
      }) });
      throw error;
    }
  };
  const order = defineTool({ name: 'TrustedOrder', description: 'Read one synthetic business order',
    parameters: { id: { type: 'string' } }, readOnly: true, handler: async (args, context) => {
      assert.equal(args.id, 'synthetic'); assert.equal(context.executionTarget?.kind, 'controlled-service');
      executeCount++; return 'TRUSTED_ORDER_READY';
    } });
  const store = createServeAgentSessionStore({ dir: join(directory, 'sessions'), ownership: { leaseMs: 30000 } });
  const build = createAgentSessionFactory({ cwd: directory, store,
    platform: { apiBaseUrl: FAKE_API_BASE, appId: scope.applicationScopeId, appKey: 'synthetic-only', fetchImpl, maxOutputTokens: 256,
      onDecision(record) { decisions.push(structuredClone(record)); assert.ok(decisions.length <= 128); },
      trustedExtensionsFor(subject) { subjects.push(subject); return { timeoutMs: 2000, onClosed() { closes++; },
        queryHooks: () => ({ userPromptSubmit() { if (promptBlocked) throw new Error('synthetic trusted policy revoked'); return {}; } }),
        beforeTool() { hookCalls++; if (hookMode === 'throw') throw new Error('synthetic trusted hook failed'); return { blocked: hookMode === 'deny' }; } }; } },
    execution: { applicationScopeId: scope.applicationScopeId,
      authorize: (request, user) => ({ controller: authenticate(request)?.endUserId === user,
        ...(authenticate(request)?.endUserId === user ? { executorId: 'net-trusted-pc' } : {}) }),
      readPolicy: async () => ({ authorizationRevision: scope.authorizationRevision, tools: names }),
      spoolFor: () => ({ spool, bindingId: 'trusted' }), toolsFor: () => [order],
      skillsFor: () => ({ execution: 'device', registry: new SkillRegistry([
        { name: 'inline-guide', description: 'Trusted inline synthetic guide', metadata: {}, source: 'builtin', inlineContent: 'INLINE_GUIDE_BODY' },
        { name: 'device-guide', description: 'Read guide from the bound C# device', metadata: {}, source: 'project', file: '/workspace/work/SKILL.md' }
      ]) }),
      subagentsFor(subject) { subjects.push(subject); return { continuableAgents: true,
        onNotification(notice) { notices.push(notice); childId = notice.agentId; } }; } } });
  const factory = { ...build.factory, async create(init) {
    const result = await build.factory.create(init); handles.set(result.handle.sessionId, result.handle); return result;
  } };
  const unsubscribe = build.factory.execution.observeChanges(change => events.push({ kind: change.kind, sessionId: change.sessionId,
    operation: change.operation?.request.operation, toolName: change.operation?.toolName }));
  let server;
  try { server = await startServer({ host: '127.0.0.1', port: 0, token: 'unused', readyFrame: 'none', heartbeatMs: 0,
    createSession: { create() { throw new Error('Legacy v1 unused'); } },
    v2: { createSession: factory, authenticate, governance: { sweepIntervalMs: 0 } } }); }
  catch (error) { unsubscribe(); await build.flush(); spool.close(); throw error; }
  const mcpServer = createServer(async (request, response) => {
    try {
      if (request.method === 'DELETE') { response.writeHead(204); response.end(); return; }
      assert.equal(request.method, 'POST');
      const chunks = []; let size = 0;
      for await (const bytes of request) { chunks.push(bytes); size += bytes.length; assert.ok(size <= 32768); }
      const message = JSON.parse(Buffer.concat(chunks).toString('utf8')); mcpCalls.push(message.method); assert.ok(mcpCalls.length < 32);
      if (message.id === undefined) { assert.ok(message.method.startsWith('notifications/')); response.writeHead(202); response.end(); return; }
      let result;
      if (message.method === 'initialize') result = { protocolVersion: '2025-11-25', capabilities: { tools: {} }, serverInfo: { name: 'native-http-fixture', version: '1' } };
      else if (message.method === 'tools/list') result = { tools: [{ name: 'echo', inputSchema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] } },
        { name: 'unapproved', inputSchema: { type: 'object' } }] };
      else {
        assert.equal(message.method, 'tools/call'); assert.equal(message.params.name, 'echo');
        assert.equal(message.params.arguments.text, 'NATIVE_HTTP_MCP_中文🙂');
        result = { content: [{ type: 'text', text: message.params.arguments.text },
          { type: 'resource_link', uri: 'mem://synthetic', name: 'synthetic reference' },
          { type: 'audio', mimeType: 'audio/wav', data: 'omitted-synthetic' }], structuredContent: { answer: 42 } };
      }
      response.writeHead(200, { 'content-type': 'application/json', 'mcp-session-id': 'trusted-http-session' });
      response.end(JSON.stringify({ jsonrpc: '2.0', id: message.id, result }));
    } catch (error) { await recordFailure('native-http-mcp', error); response.writeHead(500); response.end(); }
  });
  await new Promise((resolve, reject) => { mcpServer.once('error', reject); mcpServer.listen(0, '127.0.0.1', resolve); });
  const mcpUrl = `http://127.0.0.1:${mcpServer.address().port}/mcp`;
  async function command(input) {
    if (input.action === 'mcp-origin') return { url: mcpUrl };
    if (input.action === 'plan') {
      governance.enabled = false;
      assert.ok(['allow', 'deny', 'throw'].includes(input.hookMode ?? 'allow'));
      hookMode = input.hookMode ?? 'allow'; promptBlocked = input.promptBlocked === true;
      if (input.tool !== undefined) assert.ok(names.includes(input.tool));
      next = input.tool === undefined ? null : { name: input.tool, args: input.args ?? {} }; return { planned: true };
    }
    if (input.action === 'governance') {
      assert.ok(handles.has(input.sessionId)); governance.enabled = true; governance.sessionId = input.sessionId;
      hookMode = 'allow'; promptBlocked = false; next = null; return { planned: true };
    }
    if (input.action === 'revoke') { scope.authorizationRevision = '2'; return { revised: true }; }
    if (input.action === 'settle') { const handle = handles.get(input.sessionId); assert.ok(handle); await handle.settleResources?.(); await build.flush(); return { settled: true }; }
    if (input.action === 'inspect') {
      const saved = governance.sessionId === null ? null : await store.get(scope.endUserId, governance.sessionId);
      const snapshot = saved?.usageSnapshot?.payload.snapshot;
      return { executeCount, hookCalls, closes, childCalls, childId, notices, requests, subjects, events, mcpCalls, decisions,
        governance: { ...governance, spend: snapshot === undefined ? null : budgetSpendOf(snapshot), snapshot: snapshot ?? null } };
    }
    throw new Error('Unknown trusted host command');
  }
  const seen = new Set();
  const timer = setInterval(async () => {
    if (closing || busy) return; busy = true;
    try {
      const files = (await readdir(commands)).filter(file => file.endsWith('.json')).sort(); assert.ok(files.length <= 192);
      const file = files.find(file => !seen.has(file)); if (!file) return; seen.add(file);
      assert.match(file, /^[a-z0-9-]{1,64}\.json$/);
      const bytes = await readFile(join(commands, file)); assert.ok(bytes.length <= 4096);
      const input = JSON.parse(bytes.toString('utf8')); assert.equal(file, `${input.id}.json`);
      const result = { id: input.id, value: await command(input) };
      await writeFile(join(responses, `${input.id}.tmp`), JSON.stringify(result), { flag: 'wx' });
      await rename(join(responses, `${input.id}.tmp`), join(responses, file));
    } catch (error) { await recordFailure('host-command', error); }
    finally { busy = false; }
  }, 10);
  return { url: server.url, async close() {
    closing = true; clearInterval(timer);
    try { await server.close(); await server.settleResources?.(); await build.flush(); await build.factory.execution.settle(); }
    finally { unsubscribe(); spool.close(); await new Promise((resolve, reject) => mcpServer.close(error => error ? reject(error) : resolve())); }
    const result = { realKernel: true, realExecutionSpool: true, controlledModel: true, executeCount, hookCalls, closes, childCalls,
      notices, requests: requests.length, requestEvidence: requests, subjects, events, mcpCalls, decisions, governance, failure: failure ?? null };
    await writeFile(join(directory, 'result.json'), JSON.stringify(result));
    if (failure) throw new Error(`${failure.stage}: ${failure.message}`); return result;
  } };
}
