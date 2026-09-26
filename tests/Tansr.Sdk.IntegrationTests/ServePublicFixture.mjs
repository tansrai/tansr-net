// Acceptance-only public Serve assembly. The platform/model responses are deterministic and
// synthetic; HTTP, SDK/kernel loop, permissions, execution spool and archive host are real source.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdir, readFile, writeFile, rename } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startPublicFixture({ source, directory, authenticate, candidate = false }) {
  await mkdir(directory, { recursive: true });
  const load = path => import(pathToFileURL(join(source, path)).href);
  const extensions = await load('packages/server/src/extensions/index.ts');
  const { createServeArchiveHost, openSqliteArchiveSpool } = extensions;
  const { createServeAgentSessionStore } = await load('packages/server/src/index.ts');
  const { defaultAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const schema = JSON.parse(await readFile(join(source, 'doc/rfc/sdk2-ext-v1.schema.json'), 'utf8'));
  const limits = Object.fromEntries(Object.entries(schema.definitions.Limits.properties).map(([key, value]) => [key, value.default]));
  const scope = { applicationScopeId: 'net-integration-app', endUserId: 'net-integration-user', authorizationRevision: '1' };
  const limit = { bytes: 8388608, records: 1024 };
  const spool = await openSqliteArchiveSpool({ path: join(directory, 'execution.sqlite'), mode: 'create', storeId: 'net-execution',
    bindings: [{ bindingId: 'execution', applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId,
      limits: { binding: limit, application: limit, endUser: limit } }], globalLimit: limit,
    maxReservations: 64, maxEntries: 1024, maxOperations: 1024, maxDatabasePages: 8192 });
  const defaults = defaultAppCapabilities('desktop');
  const capabilities = { ...defaults, execution: { version: 'bound-device-v1', boundDevice: { tools: { read: true } } } };
  const platformPrompt = 'NET_P05_SYNTHETIC_PLATFORM_PRIVATE_ROLE';
  const hostPrompt = 'NET_P05_SYNTHETIC_HOST_PRIVATE_ROLE';
  const fake = createFakePlatform({ features: [], bundleExtra: { capabilities, app: { platform: 'desktop' },
    ...(candidate ? { systemPrompt: platformPrompt, systemPromptPolicy: 'prepend' } : {}) } });
  const exchanges = [], routes = [];
  let mainExchanges = 0;
  const memoryExchanges = { extraction: 0, consolidation: 0 }, memoryRequests = new Set();
  let memoryStore, memory, memoryHost;
  if (candidate) {
    memoryStore = await extensions.createSqliteMemoryPublicationStore(join(directory, 'managed-memory.sqlite'));
    memory = await extensions.MemoryManagementSource.open({ identity: { kind: 'server-managed', domain: 'net/app/中文', sourceId: 'net-memory/中文',
      sourceGeneration: '0', applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId }, mode: 'create', store: memoryStore, assertCurrent() {} });
    memoryHost = await memory.createHost({ rootDir: join(directory, 'memory-world'), memoryDir: join(directory, 'memory-world', 'memory') });
  }
  const expectedText = 'NET_DEVICE_NOTE_7319 · 中文 café 👩🏽‍💻';
  const fetchImpl = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    assert.equal(url.origin, FAKE_API_BASE, 'Only the injected synthetic platform is permitted.');
    if (url.pathname !== '/t1/exchange') return fake.fetchImpl(input, init);
    const request = JSON.parse(String(init?.body)); exchanges.push(request);
    const recoveryTurn = JSON.stringify(request.thread).includes('Produce recovery original answer once.');
    try {
      const frame = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
      const textResponse = text => new Response(frame('t.open', { exchangeId: `net-synthetic-${exchanges.length}`, model: request.model, protocol: 'twp/1' }) +
        frame('t.delta', { i: 0, t: 'text', v: text }) + frame('t.close', { stop: 'end_turn' }), { headers: { 'content-type': 'text/event-stream' } });
      if (request.meta?.purpose === 'memory') {
        assert.equal(candidate, true);
        assert.deepEqual((request.tools ?? []).map(tool => tool.name).sort(), ['Edit', 'Read', 'Write']);
        const last = request.thread.at(-1); assert.equal(last?.role, 'user');
        const instruction = (last.blocks ?? []).filter(block => block.t === 'text').map(block => block.v).join('\n');
        const kind = instruction.includes('<memory-extraction-task>') && instruction.includes('</memory-extraction-task>') ? 'extraction' :
          instruction.includes('<memory-consolidation-task>') && instruction.includes('</memory-consolidation-task>') ? 'consolidation' : null;
        assert.ok(kind, 'Only the explicit trusted memory extraction/consolidation profiles are controlled.');
        const fingerprint = JSON.stringify({ kind, thread: request.thread, tools: request.tools });
        assert.ok(!memoryRequests.has(fingerprint), 'Unexpected duplicate memory model request/retry.'); memoryRequests.add(fingerprint);
        assert.ok(++memoryExchanges[kind] <= (kind === 'extraction' ? 3 : 1), 'Unexpected background memory sampling.');
        // Both actual kernel instructions explicitly allow this no-write terminal response.
        return textResponse(kind === 'extraction' ? 'nothing to save' : 'nothing to consolidate');
      }
      ++mainExchanges;
      if (candidate) {
        // Observe the actual applied model system, independently of the metadata projection.
        assert.equal(request.thread[0]?.role, 'system');
        assert.deepEqual(request.thread[0].blocks.slice(0, 2).map(block => block.v), [platformPrompt, hostPrompt]);
      }
      // The public device host explicitly narrows RequestedTools to Read. Memory management is
      // still available through its trusted source, but SearchMemory must not enter this turn.
      // TWP omits tools entirely for the recovery session's explicit empty tool set.
      assert.deepEqual((request.tools ?? []).map(tool => tool.name).sort(), recoveryTurn ? [] : ['Read']);
      assert.ok(mainExchanges <= (candidate ? 4 : 3), 'Unexpected model sampling/retry.');
      if (mainExchanges > 1 && !recoveryTurn) assert.ok(JSON.stringify(request.thread).includes(expectedText), 'The real kernel must consume the .NET device result.');
      const tool = mainExchanges === 1;
      return new Response(frame('t.open', { exchangeId: `net-synthetic-${exchanges.length}`, model: request.model, protocol: 'twp/1' }) +
        (tool ? frame('t.delta', { i: 0, t: 'tool_use', id: 'net-device-read', name: 'Read', vJson: JSON.stringify({ file_path: '/workspace/work/note.txt' }) }) :
          frame('t.delta', { i: 0, t: 'text', v: recoveryTurn ? 'NET_RECOVERY_ORIGINAL' : mainExchanges === 2 ? expectedText : 'Original material returned through .NET.' })) +
        frame('t.close', { stop: tool ? 'tool_use' : 'end_turn' }), { headers: { 'content-type': 'text/event-stream' } });
    } catch (error) {
      // Surface synthetic-boundary failures before the real provider's normal retry policy can
      // make the downstream symptom an idle timeout. Never print credentials or request bodies.
      process.stderr.write(`Controlled upstream rejected synthetic exchange ${exchanges.length}: ${error.message}\n`);
      throw error;
    }
  };
  const cap = { bytes: 64 * 1048576, records: 4096 }, total = { bytes: 256 * 1048576, records: 16384 }, issuedAtMs = Date.now() - 1000;
  const sessionStore = createServeAgentSessionStore({ dir: join(directory, 'sessions'), ...(candidate ? { ownership: {} } : {}) });
  const commit = sessionStore.commit.bind(sessionStore);
  let barrier;
  const barrierCommit = async (...args) => {
    const result = await commit(...args);
    if (barrier && !barrier.entered && args[1] === barrier.sessionId && args[2].length) {
      barrier.entered = true; barrier.enter(); await barrier.proceed;
    }
    return result;
  };
  const host = createServeArchiveHost({ applicationScopeId: scope.applicationScopeId, security: 'trusted-single-application',
    agent: { cwd: directory, store: sessionStore, checkpoints: { autoBeforeCompact: false }, ...(candidate ? { configuration: {} } : {}),
      platform: { apiBaseUrl: FAKE_API_BASE, appId: scope.applicationScopeId, appKey: 'synthetic-only-no-real-key', fetchImpl,
        ...(candidate ? { system: [{ text: hostPrompt }] } : {}),
        ...(candidate ? { memoryFor: owner => owner.endUserId === scope.endUserId ? { kind: 'server-managed', domain: 'net/app/中文',
          rootDir: memoryHost.rootDir, memoryDir: memoryHost.memoryDir, host: memoryHost, management: memory,
          enabled: () => true, balance: () => null, recallSelector: false } : undefined } : {}) },
      execution: { applicationScopeId: scope.applicationScopeId,
        authorize: (request, user) => ({ controller: authenticate(request)?.endUserId === scope.endUserId && user === scope.endUserId,
          ...(authenticate(request)?.endUserId === scope.endUserId && user === scope.endUserId ? { executorId: 'net-pc' } : {}) }),
        readPolicy: async () => ({ authorizationRevision: scope.authorizationRevision, tools: ['Read'] }), spoolFor: () => ({ spool, bindingId: 'execution' }) } },
    archive: { directory: join(directory, 'archive'), mode: 'create', hostId: 'net-public-archive',
      quota: { host: total, application: total, endUser: total, capture: cap }, maxSessions: 2,
      commandBytes: 1048576, commandDatabasePages: 1024, spoolDatabasePages: 32768, limits,
      epoch: { id: 'net-public-epoch', issuedAtMs, expiresAtMs: issuedAtMs + limits.epochLifetimeMs } },
    events: { storeId: 'net-public-events', key: randomBytes(32), maxQueuedOperations: 32, maxSubscriptions: 4 },
    sourceFor: subject => ({ sourceId: `source-${subject.sessionId}`, sourceGeneration: 'net-source-generation' }),
    authorize: ({ subject }) => { assert.equal(subject.endUserId, scope.endUserId); return { authorizationRevision: scope.authorizationRevision }; }
  });
  let server;
  try {
    server = await host.start({ host: '127.0.0.1', port: 0, token: 'synthetic-unused-legacy', readyFrame: 'none', heartbeatMs: 0,
      ...(candidate ? { terminal: { contract: 'terminal-services-v1' } } : {}),
      v2: { authenticate(request) { routes.push(new URL(request.url, 'http://127.0.0.1').pathname); return authenticate(request); } } });
  } catch (error) { await host.dispose(); spool.close(); throw error; }
  // A bounded test-only file IPC drives trusted host lifecycle, not a new product endpoint.
  // This never proxies SSE/body delivery; .NET consumes actual Serve routes independently.
  let lastCommand = '', pending = false, failure;
  const timer = setInterval(async () => {
    if (pending || failure) return; pending = true;
    try {
      let raw;
      try { raw = await readFile(join(directory, 'host-command.json'), 'utf8'); } catch (error) { if (error.code === 'ENOENT') return; throw error; }
      assert.ok(Buffer.byteLength(raw) <= 4096); const command = JSON.parse(raw);
      if (command.id === lastCommand) return;
      assert.match(command.id, /^[a-z0-9-]{1,64}$/); assert.equal(typeof command.sessionId, 'string');
      const subject = { endUserId: scope.endUserId, sessionId: command.sessionId }; let value;
      if (command.action === 'materials') {
        assert.ok(Array.isArray(command.recordIds) && command.recordIds.length > 0 && command.recordIds.length <= 8);
        value = host.requestMaterials(subject, { materialRequestId: command.id, recordIds: command.recordIds, purpose: 'context-recall', lifetimeMs: 30000 });
      } else if (command.action === 'enqueue') {
        host.enqueueMaterials(subject, { materialRequestId: command.materialRequestId, leaseId: command.id, required: true }); value = { enqueued: true };
      } else if (candidate && command.action === 'arm-recovery-store') {
        assert.equal(barrier, undefined); let enter, release;
        const stopped = new Promise(resolve => { enter = resolve; }), proceed = new Promise(resolve => { release = resolve; });
        barrier = { sessionId: command.sessionId, entered: false, stopped, proceed, enter, release };
        // Install only after this original session's fresh-store identity was verified, matching
        // the producer's regression fixture; changing an attested store before creation is invalid.
        sessionStore.commit = barrierCommit; value = { armed: true };
      } else if (candidate && command.action === 'await-recovery-store') {
        assert.equal(barrier?.sessionId, command.sessionId); await barrier.stopped; value = { committedButReturnHeld: true };
      } else if (candidate && command.action === 'release-recovery-store') {
        assert.equal(barrier?.sessionId, command.sessionId); assert.equal(barrier.entered, true); barrier.release(); value = { released: true };
      } else throw new Error('Unknown trusted fixture command.');
      lastCommand = command.id;
      const temporary = join(directory, 'host-response.tmp');
      await writeFile(temporary, JSON.stringify({ id: command.id, value })); await rename(temporary, join(directory, 'host-response.json'));
    } catch (error) { failure = error; process.stderr.write(`Public fixture command failed: ${error.message}\n`); }
    finally { pending = false; }
  }, 30);
  timer.unref();
  return { url: server.url, scope, expectedText,
    async close() {
      barrier?.release();
      clearInterval(timer); while (pending) await new Promise(resolve => setTimeout(resolve, 10));
      await server.close(); await server.settleResources?.(); await host.dispose(); spool.close(); memoryStore?.close();
      if (failure) throw failure;
      return { model: 'controlled-synthetic-platform', exchanges: exchanges.length, mainExchanges, memoryExchanges, routes, realKernel: true };
    }
  };
}
