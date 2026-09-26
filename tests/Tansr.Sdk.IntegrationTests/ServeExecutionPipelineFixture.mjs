// Real Serve/kernel + durable execution spool. Only upstream model responses and test commands
// are controlled. Windows executes the actual child process through its public SDK backend.
import assert from 'node:assert/strict';
import { appendFileSync } from 'node:fs';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startExecutionPipelineFixture({ source, directory, authenticate, shellSandbox = false }) {
  await mkdir(directory, { recursive: true });
  const load = file => import(pathToFileURL(join(source, file)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const { openSqliteArchiveSpool } = await load('packages/server/src/extensions/index.ts');
  const { TerminalExecutionService } = await load('packages/server/src/v2/terminal-execution.ts');
  const { defaultAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const scope = { applicationScopeId: 'net-execution-app', endUserId: 'net-integration-user', authorizationRevision: '1' };
  const cap = { bytes: 16 * 1048576, records: 1024 };
  const spool = await openSqliteArchiveSpool({ path: join(directory, 'execution.sqlite'), mode: 'create', storeId: 'net-native-execution',
    bindings: [{ bindingId: 'native', applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId,
      limits: { binding: cap, application: cap, endUser: cap } }], globalLimit: cap,
    maxReservations: 128, maxEntries: 1024, maxOperations: 1024, maxDatabasePages: 8192 });
  const base = defaultAppCapabilities('desktop');
  const fake = createFakePlatform({ features: [], bundleExtra: { app: { platform: 'desktop' }, capabilities: { ...base,
    tools: { ...base.tools, shell: true }, execution: { version: 'bound-device-v1', boundDevice: { tools: { shell: true, process: true, read: true } } } } } });
  const modelCalls = new Map(), operations = new Map(), routes = [], accepted = new Set(), backgroundActions = [];
  let records = 0, memoryCalls = 0, failure;
  function record(stage, facts = {}) {
    assert.ok(++records <= 8192, 'Native pipeline evidence is bounded.');
    appendFileSync(join(directory, 'serve-pipeline.jsonl'), JSON.stringify({ stage, utcMs: Date.now(), monotonicNs: String(process.hrtime.bigint()), ...facts }) + '\n');
  }
  const frame = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  const response = (request, text, tool) => new Response(frame('t.open', { exchangeId: `net-pipeline-${records}`, model: request.model, protocol: 'twp/1' }) +
    (tool ? frame('t.delta', { i: 0, t: 'tool_use', id: tool.id, name: tool.name ?? 'Shell', vJson: JSON.stringify(tool.args) }) :
      frame('t.delta', { i: 0, t: 'text', v: text })) + frame('t.close', { stop: tool ? 'tool_use' : 'end_turn' }),
    { headers: { 'content-type': 'text/event-stream' } });
  const fetchImpl = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    assert.equal(url.origin, FAKE_API_BASE, 'Only the injected synthetic platform is permitted.');
    if (url.pathname !== '/t1/exchange') return fake.fetchImpl(input, init);
    const request = JSON.parse(String(init?.body));
    try {
      if (request.meta?.purpose === 'memory') {
        assert.ok(++memoryCalls <= 8); return response(request, 'nothing to save');
      }
      const prompts = request.thread.filter(item => item.role === 'user').flatMap(item => item.blocks ?? []).filter(block => block.t === 'text').map(block => block.v).join('\n');
      const sandboxPlan = /NET_PIPELINE_SHELLSANDBOX_(normal|approved|denied|required|none)/.exec(prompts);
      if (sandboxPlan) {
        assert.equal(shellSandbox, true);
        const scenario = sandboxPlan[1], key = `SHELLSANDBOX_${scenario}`;
        const count = (modelCalls.get(key) ?? 0) + 1; modelCalls.set(key, count);
        assert.ok(count <= (scenario === 'normal' ? 2 : 3), 'Only normal and expressly approved escalation attempts are permitted.');
        if (count === 1) return response(request, null, { id: `native-sandbox-${scenario}-normal`, args: { command: 'native-large-unicode', cwd: '/workspace/work' } });
        if (scenario !== 'normal' && count === 2) {
          assert.ok(JSON.stringify(request).includes('isolation_denied'), 'Original structural denial must reach the same turn first.');
          return response(request, null, { id: `native-sandbox-${scenario}-escalate`, args: { command: 'native-large-unicode', cwd: '/workspace/work', escalate: true } });
        }
        const id = `native-sandbox-${scenario}-${scenario === 'normal' ? 'normal' : 'escalate'}`;
        const result = request.thread.flatMap(item => item.blocks ?? []).findLast(block => block.t === 'tool_result' && block.toolUseId === id);
        assert.ok(result, 'Real sandbox result must reach the model exactly once.');
        const text = JSON.stringify(result);
        if (['normal', 'approved', 'none'].includes(scenario)) {
          assert.notEqual(result.isError, true);
          assert.ok(text.includes('中'.repeat(6000) + '🙂�') && text.includes('错'.repeat(2000) + '🙂'), 'Core must consume full native output beyond the receipt preview.');
        }
        return response(request, 'NET_PIPELINE_SHELLSANDBOX_DONE');
      }
      // The real context manager may append another user block after the original prompt.
      // Select the latest explicitly marked synthetic action, not the tail of assembled context.
      const planned = [...prompts.matchAll(/NET_BACKGROUND:(\{[^\r\n]+\})/g)].at(-1);
      if (planned) {
        const action = JSON.parse(planned[1]);
        assert.ok(['Shell', 'ShellTask', 'ShellOutput'].includes(action.name));
        assert.deepEqual((request.tools ?? []).map(tool => tool.name).sort(), ['Shell', 'ShellOutput', 'ShellTask']);
        const count = (modelCalls.get(action.id) ?? 0) + 1; modelCalls.set(action.id, count);
        assert.ok(count <= 2, 'Each background business action has one proposal and one original result.');
        if (count === 1) { backgroundActions.push(action); return response(request, null, action); }
        const original = request.thread.flatMap(item => item.blocks ?? []).findLast(block => block.t === 'tool_result' && block.toolUseId === action.id);
        assert.ok(original, 'Background results must be returned by the real kernel.');
        return response(request, `${action.id}:settled`);
      }
      assert.deepEqual((request.tools ?? []).map(tool => tool.name).sort(), ['Shell']);
      const scenario = /NET_PIPELINE_(STREAM|CANCEL|LOSS)/.exec(prompts)?.[1];
      assert.ok(scenario, 'Only explicit native pipeline scenarios are allowed.');
      const count = (modelCalls.get(scenario) ?? 0) + 1; modelCalls.set(scenario, count);
      assert.ok(count <= 2, 'The original Shell must never be sampled or started twice.');
      record('model.request', { scenario, count });
      if (count === 1) return response(request, null, { id: `native-${scenario.toLowerCase()}`,
        args: { command: `net-pipeline-${scenario.toLowerCase()}`, cwd: '/workspace/work' } });
      assert.notEqual(scenario, 'CANCEL', 'Interrupt must stop the original model loop.');
      const result = request.thread.flatMap(item => item.blocks ?? []).findLast(block => block.t === 'tool_result' && block.toolUseId === `native-${scenario.toLowerCase()}`);
      assert.ok(result && result.isError !== true, 'The real native process result must reach the model exactly once.');
      assert.ok(JSON.stringify(result).includes('PIPE_OUT_中文🙂') && JSON.stringify(result).includes('PIPE_ERR_中文🙂'));
      return response(request, `NET_PIPELINE_${scenario}_DONE`);
    } catch (error) { failure = error; record('fixture.failure', { message: error.message }); throw error; }
  };
  const build = createAgentSessionFactory({ cwd: directory, store: createServeAgentSessionStore({ dir: join(directory, 'sessions') }),
    checkpoints: { autoBeforeCompact: false },
    platform: { apiBaseUrl: FAKE_API_BASE, appId: scope.applicationScopeId, appKey: 'synthetic-no-real-key', fetchImpl },
    execution: { applicationScopeId: scope.applicationScopeId, shellSandbox,
      authorize: (request, user) => {
        const allowed = authenticate(request)?.endUserId === scope.endUserId && user === scope.endUserId;
        return { controller: allowed && request.headers['x-net-role'] === 'controller',
          ...(allowed && request.headers['x-net-role'] === 'executor' ? { executorId: 'net-native-pc' } : {}) };
      },
      readPolicy: async () => ({ authorizationRevision: scope.authorizationRevision, tools: ['Shell'] }),
      confirmInterpreter: ({ interpreter }) => interpreter.id === 'net-native-fixture' && interpreter.revision === '1' && interpreter.hostShell === 'powershell',
      spoolFor: () => ({ spool, bindingId: 'native' }) } });
  const unsubscribe = build.factory.execution.observeChanges(change => {
    if (change.operation?.request.operation !== 'process.exec' && !(shellSandbox && change.operation?.request.operation === 'tool.invoke' && change.operation.request.args.name === 'TansrTerminalShellSandbox')) return;
    const original = change.operation;
    operations.set(original.operationId, { sessionId: original.sessionId, digest: original.digest });
    record('execution.change', { kind: change.kind, operationId: original.operationId });
  });
  // Read-only instrumentation of the real accepted-output listener, same boundary used by the
  // Serve resource-budget acceptance. It neither supplies blocks nor bypasses their validation.
  const subscribeOutput = TerminalExecutionService.prototype.subscribeOutput;
  TerminalExecutionService.prototype.subscribeOutput = function (...args) {
    const listener = args[5];
    args[5] = { ...listener, event(event) {
      if (event.type === 'output.block' && operations.has(event.operation.operationId)) {
        const key = `${event.operation.operationId}:${event.block.seq}`;
        if (!accepted.has(key)) { accepted.add(key); record('serve.output.accepted', { operationId: event.operation.operationId,
          seq: event.block.seq, channel: event.block.channel, byteLength: event.block.byteLength }); }
      }
      listener.event(event);
    } };
    return subscribeOutput.apply(this, args);
  };
  let server;
  try {
    server = await startServer({ host: '127.0.0.1', port: 0, token: 'unused-legacy', readyFrame: 'none', heartbeatMs: 0,
      terminal: { contract: 'terminal-services-v1', revalidateMs: 50 }, createSession: { create() { throw new Error('Legacy v1 unused.'); } },
      v2: { createSession: build.factory, governance: { sweepIntervalMs: 0 }, authenticate(request) {
        const path = new URL(request.url, 'http://127.0.0.1').pathname;
        routes.push({ method: request.method, path, role: request.headers['x-net-role'] ?? null });
        if (path.endsWith('/output-batches') || path.endsWith('/interrupt')) record('serve.http.received', { path });
        return authenticate(request);
      } } });
  } catch (error) { TerminalExecutionService.prototype.subscribeOutput = subscribeOutput; unsubscribe(); await build.flush(); spool.close(); throw error; }
  return { url: server.url,
    async close() {
      await server.close(); await server.settleResources?.(); await build.flush(); unsubscribe(); spool.close();
      TerminalExecutionService.prototype.subscribeOutput = subscribeOutput;
      if (failure) throw failure;
      return { realKernel: true, realExecutionSpool: true, controlledModel: true, modelCalls: Object.fromEntries(modelCalls),
        memoryCalls, operations: [...operations.entries()], acceptedBlocks: accepted.size, routes, backgroundActions, timingFile: join(directory, 'serve-pipeline.jsonl'),
        performanceBaseline: 'Not an Electron comparison or a performance threshold claim.' };
    }
  };
}
