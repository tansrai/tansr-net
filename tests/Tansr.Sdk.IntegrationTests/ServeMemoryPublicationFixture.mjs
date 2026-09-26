// Real Serve/kernel publication over the original execution channel. Only the model is synthetic.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile, rename, readdir } from 'node:fs/promises';
import { appendFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startMemoryPublicationFixture({ source, directory, authenticate }) {
  await mkdir(directory, { recursive: true });
  const commandDirectory = join(directory, 'host-commands'), responseDirectory = join(directory, 'host-responses');
  await mkdir(commandDirectory); await mkdir(responseDirectory);
  const started = Date.now(); let diagnosticCount = 0, lastDiagnosticStage = 'fixture.start';
  const diagnostic = (stage, details = {}) => {
    assert.ok(++diagnosticCount <= 4096, 'Synthetic diagnostic record limit exceeded.');
    lastDiagnosticStage = stage;
    appendFileSync(join(directory, 'serve-stages.jsonl'), JSON.stringify({ at: new Date().toISOString(), elapsedMs: Date.now() - started, stage, ...details }) + '\n');
  };
  diagnostic('fixture.start');
  const load = file => import(pathToFileURL(join(source, file)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const { openSqliteArchiveSpool, memoryPublicationKey } = await load('packages/server/src/extensions/index.ts');
  const { getPlatformMemoryLifecycle } = await load('packages/server/src/v2/memory-lifecycle.ts');
  const { defaultAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const scope = { applicationScopeId: 'net-integration-app', endUserId: 'net-integration-user', authorizationRevision: '1' };
  const identity = { kind: 'client-managed', domain: 'net/device-memory', sourceId: 'net-device-memory', sourceGeneration: '1',
    applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId };
  const deviceIdentity = { scope: { applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId },
    sourceId: identity.sourceId, sourceGeneration: identity.sourceGeneration, domainKey: memoryPublicationKey(identity) };
  const fact = 'NET_MEMORY_FACT_8271: the synthetic user prefers violet report headings.';
  const topic = 'net-preference.md', index = '# Synthetic memory\n- [Report preference](net-preference.md)\n';
  const topicText = '# Synthetic report preference\n- [2026-09-26] ' + fact + '\n';
  const limit = { bytes: 64 * 1048576, records: 4096 };
  const spool = await openSqliteArchiveSpool({ path: join(directory, 'serve-execution.sqlite'), mode: 'create', storeId: 'net-device-memory',
    bindings: [{ bindingId: 'memory', applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId,
      limits: { binding: limit, application: limit, endUser: limit } }], globalLimit: limit,
    maxReservations: 1024, maxEntries: 4096, maxOperations: 4096, maxDatabasePages: 32768 });
  const base = defaultAppCapabilities('desktop');
  const fake = createFakePlatform({ features: [], bundleExtra: { app: { platform: 'desktop' }, capabilities: { ...base,
    tools: { ...base.tools, shell: false }, execution: { version: 'bound-device-v1', boundDevice: { tools: { read: true } } } } } });
  let enabled = true, minimumDeletionGeneration = '0', handle, virtualMemoryDir, mainCalls = 0, extractionCalls = 0;
  let initialExtractionDone = false, recallAdopted = false, forgottenAbsent = false, closedSession = false;
  let oldWriter, lateWriteRejected = false, backupReplayRejected = false;
  const observations = [], routes = [], exchanges = [];
  const frame = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  function answer(request, text, tool) {
    return new Response(frame('t.open', { exchangeId: `net-memory-${exchanges.length}`, model: request.model, protocol: 'twp/1' }) +
      (tool ? frame('t.delta', { i: 0, t: 'tool_use', id: tool.id, name: tool.name, vJson: JSON.stringify(tool.args) }) :
        frame('t.delta', { i: 0, t: 'text', v: text })) + frame('t.close', { stop: tool ? 'tool_use' : 'end_turn' }),
    { headers: { 'content-type': 'text/event-stream' } });
  }
  function result(request, id) {
    return request.thread.flatMap(message => message.blocks ?? []).findLast(block => block.t === 'tool_result' && block.toolUseId === id);
  }
  const fetchImpl = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    assert.equal(url.origin, FAKE_API_BASE, 'Only the explicit synthetic platform is permitted.');
    if (url.pathname !== '/t1/exchange') return fake.fetchImpl(input, init);
    const request = JSON.parse(String(init?.body)); exchanges.push({ purpose: request.meta?.purpose ?? 'main' });
    diagnostic('model.request', { call: exchanges.length, purpose: request.meta?.purpose ?? 'main',
      toolResults: request.thread.flatMap(message => message.blocks ?? []).filter(block => block.t === 'tool_result').map(block => ({ id: block.toolUseId, failed: block.isError === true })) });
    try {
      assert.ok(exchanges.length <= 24, 'Unexpected retry or extra model workload.');
      if (request.meta?.purpose === 'memory') {
        assert.deepEqual((request.tools ?? []).map(tool => tool.name).sort(), ['Edit', 'Read', 'Write']);
        const instructions = request.thread.flatMap(message => message.blocks ?? []).filter(block => block.t === 'text').map(block => block.v).join('\n');
        if (instructions.includes('<memory-consolidation-task>')) return answer(request, 'nothing to consolidate');
        assert.ok(instructions.includes('<memory-extraction-task>'), 'Only the original extraction profile may write synthetic memory.');
        extractionCalls++;
        if (initialExtractionDone) return answer(request, 'nothing to save');
        assert.ok(instructions.includes(fact), 'Extraction must consume the original completed turn.');
        const match = /Auto-memory directory: ([^\r\n]+)/.exec(instructions); assert.ok(match); virtualMemoryDir = match[1];
        const first = result(request, 'memory-index-read'), second = result(request, 'memory-topic-write'), third = result(request, 'memory-index-write');
        if (!first) return answer(request, null, { id: 'memory-index-read', name: 'Read', args: { file_path: join(virtualMemoryDir, 'MEMORY.md') } });
        if (!second) return answer(request, null, { id: 'memory-topic-write', name: 'Write', args: { file_path: join(virtualMemoryDir, topic), contents: topicText } });
        assert.notEqual(second.isError, true, 'The real extraction topic write was refused.');
        if (!third) return answer(request, null, { id: 'memory-index-write', name: 'Write', args: { file_path: join(virtualMemoryDir, 'MEMORY.md'), contents: index } });
        assert.notEqual(third.isError, true, 'The real extraction index write was refused.');
        initialExtractionDone = true; diagnostic('extraction.writes.accepted'); return answer(request, 'Saved synthetic preference to the authorized memory source.');
      }
      mainCalls++;
      assert.deepEqual((request.tools ?? []).map(tool => tool.name).sort(), ['SearchMemory']);
      const lastUser = [...request.thread].reverse().find(message => message.role === 'user' &&
        message.blocks?.some(block => block.t === 'text' && block.v.includes('NET_MEMORY_')));
      const prompt = lastUser?.blocks.filter(block => block.t === 'text').map(block => block.v).join('\n') ?? '';
      if (prompt.includes('NET_MEMORY_SEED')) return answer(request, 'Acknowledged the synthetic report preference.');
      const afterDelete = prompt.includes('NET_MEMORY_AFTER_DELETE');
      assert.ok(afterDelete || prompt.includes('NET_MEMORY_RECALL'), 'Unexpected business turn.');
      const id = afterDelete ? 'memory-search-after-delete' : 'memory-search-recall';
      const found = result(request, id);
      if (!found) return answer(request, null, { id, name: 'SearchMemory', args: { query: 'NET_MEMORY_FACT_8271', kind: 'memory', limit: 5 } });
      assert.notEqual(found.isError, true, 'Actual SearchMemory failed.');
      if (afterDelete) {
        assert.ok(!found.v.includes(fact), 'Forgotten source leaked through SearchMemory.'); forgottenAbsent = true;
        return answer(request, 'NET_MEMORY_ABSENT_CONFIRMED');
      }
      assert.ok(found.v.includes(fact), 'The subsequent model request must consume the real SearchMemory result.');
      recallAdopted = true; diagnostic('recall.result.adopted'); return answer(request, 'NET_MEMORY_ADOPTED: violet report headings.');
    } catch (error) { process.stderr.write(`Synthetic memory exchange rejected: ${error.message}\n`); throw error; }
  };
  const store = createServeAgentSessionStore({ dir: join(directory, 'sessions'), ownership: {} });
  const build = createAgentSessionFactory({ cwd: directory, store, platform: { apiBaseUrl: FAKE_API_BASE, appId: scope.applicationScopeId,
    appKey: 'synthetic-no-real-key', fetchImpl, memoryPublicationFor(selected) {
      assert.equal(selected.applicationScopeId, scope.applicationScopeId); assert.equal(selected.endUserId, scope.endUserId);
      return { identity, mode: 'create', enabled: () => enabled, balance: () => null, minimumDeletionGeneration: () => minimumDeletionGeneration,
        // Trusted synthetic host provenance, never supplied by the model or inferred from memory text.
        provenance: () => ({ deletionGeneration: minimumDeletionGeneration, origins: ['synthetic:net-memory-seed'] }),
        extraction: { inviteGate: 'off' }, recallSelector: false, onObservation: (kind, value) => observations.push({ kind, value }) };
    } }, execution: { applicationScopeId: scope.applicationScopeId,
    authorize: (request, user) => ({ controller: authenticate(request)?.endUserId === scope.endUserId && user === scope.endUserId,
      ...(authenticate(request)?.endUserId === scope.endUserId && user === scope.endUserId ? { executorId: 'net-memory-pc' } : {}) }),
    readPolicy: async () => ({ authorizationRevision: scope.authorizationRevision, tools: ['SearchMemory'] }),
    spoolFor: () => ({ spool, bindingId: 'memory' }) } });
  const create = build.factory.create.bind(build.factory);
  build.factory.create = async init => { assert.equal(handle, undefined, 'One explicit memory session per fixture.'); const value = await create(init); handle = value.handle; return value; };
  const server = await startServer({ host: '127.0.0.1', port: 0, token: 'synthetic-unused-v1', readyFrame: 'none', heartbeatMs: 0,
    terminal: { contract: 'terminal-services-v1' }, createSession: { create() { throw new Error('Legacy v1 unused'); } },
    v2: { createSession: build.factory, governance: { sweepIntervalMs: 0 }, authenticate(request) {
      routes.push({ method: request.method, path: new URL(request.url, 'http://127.0.0.1').pathname }); return authenticate(request);
    } } });
  async function idle() {
    assert.ok(handle); const deadline = Date.now() + 30000;
    for (;;) {
      const lifecycle = getPlatformMemoryLifecycle(handle); assert.ok(lifecycle);
      const status = lifecycle.status();
      if (handle.status() === 'idle' && !status.extractionInFlight && !status.consolidationInFlight && !status.promotionInFlight) {
        try { await handle.owner.assertIdle(); return lifecycle; }
        catch (error) { assert.equal(error.code, 'reconciliation_required'); }
      }
      assert.ok(Date.now() < deadline, 'Original memory resources did not become idle.'); await new Promise(resolve => setTimeout(resolve, 10));
    }
  }
  const processedCommands = new Set();
  let pending = false, failure;
  const timer = setInterval(async () => {
    if (pending) return; pending = true;
    let activeCommand;
    try {
      const files = (await readdir(commandDirectory)).filter(name => name.endsWith('.json')).sort();
      assert.ok(files.length <= 64, 'Trusted fixture command count exceeded.');
      // The first failed assertion stops business commands but never prevents original-session cleanup.
      const filename = files.find(name => !processedCommands.has(name) && (!failure ||
        name === 'close-memory-after-failure.json' || name === 'close-memory-session.json')); if (!filename) return;
      assert.match(filename, /^[a-z0-9-]{1,64}\.json$/);
      const raw = await readFile(join(commandDirectory, filename), 'utf8');
      assert.ok(Buffer.byteLength(raw) <= 4096); const command = JSON.parse(raw);
      assert.match(command.id, /^[a-z0-9-]{1,64}$/); assert.equal(filename, `${command.id}.json`); processedCommands.add(filename);
      activeCommand = command;
      if (failure) assert.equal(command.action, 'close-session');
      diagnostic('command.begin', { id: command.id, action: command.action });
      let value;
      if (command.action === 'identity') value = deviceIdentity;
      else if (command.action === 'idle') {
        await idle(); value = { idle: true, initialExtractionDone, recallAdopted, forgottenAbsent };
      } else if (command.action === 'inspect') {
        const lifecycle = await idle(), authority = lifecycle.management; assert.ok(authority);
        diagnostic('inspect.idle', { id: command.id });
        const managed = await authority.createHost(); diagnostic('inspect.host.created', { id: command.id });
        const memory = await authority.read(); diagnostic('inspect.state.read', { id: command.id });
        const files = {};
        for (const name of ['MEMORY.md', topic, 'pending-anchors.md']) {
          try { files[name] = (await managed.fs.readFile(join(managed.memoryDir, name))).toString('utf8'); }
          catch (error) { if (error.code !== 'ENOENT') throw error; files[name] = null; }
          diagnostic('inspect.file.read', { id: command.id, name, present: files[name] !== null });
        }
        value = { memory, files, initialExtractionDone, recallAdopted, forgottenAbsent };
      } else if (command.action === 'capture-writer') {
        const lifecycle = await idle(), generation = minimumDeletionGeneration;
        oldWriter = await lifecycle.management.createHost({ provenance: () => ({ deletionGeneration: generation, origins: ['synthetic:net-memory-seed'] }) });
        const originalBody = await oldWriter.fs.readFile(join(oldWriter.memoryDir, topic));
        await oldWriter.fs.writeFile(join(oldWriter.memoryDir, topic), originalBody);
        value = { captured: true, writeAcceptedBeforeDeletion: true };
      } else if (command.action === 'late-write') {
        assert.ok(oldWriter, 'The old writer must have been captured before deletion.');
        await assert.rejects(async () => oldWriter.fs.writeFile(join(oldWriter.memoryDir, topic), Buffer.from(topicText)), { code: 'stale_generation' });
        lateWriteRejected = true;
        const lifecycle = await idle();
        const backup = await lifecycle.management.createHost({ provenance: () => ({ deletionGeneration: minimumDeletionGeneration,
          origins: ['memory/' + topic] }) });
        await assert.rejects(async () => backup.fs.writeFile(join(backup.memoryDir, 'backup-alias.md'), Buffer.from(topicText)), { code: 'stale_generation' });
        backupReplayRejected = true; value = { lateWriteRejected, backupReplayRejected };
      } else if (command.action === 'enabled') { assert.equal(typeof command.enabled, 'boolean'); enabled = command.enabled; value = { enabled }; }
      else if (command.action === 'deletion-floor') {
        assert.match(command.generation, /^(0|[1-9][0-9]*)$/); assert.ok(BigInt(command.generation) >= BigInt(minimumDeletionGeneration));
        minimumDeletionGeneration = command.generation; value = { minimumDeletionGeneration };
      } else if (command.action === 'close-session') {
        // Closing owns cancellation/draining even after a failed assertion; polling must still be alive.
        assert.ok(handle); assert.equal(typeof handle.settleResources, 'function');
        const lifecycle = getPlatformMemoryLifecycle(handle); assert.ok(lifecycle);
        handle.close();
        let closeTimer;
        try {
          // The public handle resolves void after cleanup; only the lifecycle returns settlement rows.
          await Promise.race([handle.settleResources(), new Promise((_, reject) => {
            closeTimer = setTimeout(() => reject(new Error('Original Serve handle cleanup timed out.')), 10000);
          })]);
        } finally { clearTimeout(closeTimer); }
        const settled = await lifecycle.settleResources({ timeoutMs: 0 });
        diagnostic('session.close.settled', { results: settled });
        assert.ok(settled?.every(item => item.status === 'completed')); closedSession = true; value = { closedSession };
      } else throw new Error('Unknown trusted memory fixture command.');
      const temporary = join(responseDirectory, `${command.id}.tmp`);
      await writeFile(temporary, JSON.stringify({ id: command.id, value }), { flag: 'wx' });
      await rename(temporary, join(responseDirectory, `${command.id}.json`));
      diagnostic('command.completed', { id: command.id, action: command.action });
    } catch (error) {
      const report = { type: String(error?.name ?? 'Error').slice(0, 128),
        message: String(error?.message ?? 'Synthetic fixture command failed.').slice(0, 2048),
        stage: `${activeCommand?.action ?? 'ipc'}:${lastDiagnosticStage}`.slice(0, 256) };
      try {
        if (!failure) {
          failure = error;
          const temporary = join(directory, 'host-failure.tmp');
          const bytes = JSON.stringify(report); assert.ok(Buffer.byteLength(bytes) <= 16384);
          await writeFile(temporary, bytes, { flag: 'wx' }); await rename(temporary, join(directory, 'host-failure.json'));
        }
        if (activeCommand?.action === 'close-session') {
          const temporary = join(responseDirectory, `${activeCommand.id}.failure.tmp`);
          await writeFile(temporary, JSON.stringify({ id: activeCommand.id, failure: report }), { flag: 'wx' });
          await rename(temporary, join(responseDirectory, `${activeCommand.id}.json`));
        }
      } catch (persistenceError) { process.stderr.write(`Memory fixture failure persistence rejected: ${persistenceError.name}\n`); }
      process.stderr.write(`Memory fixture command rejected: ${report.message}\n`);
    }
    finally { pending = false; }
  }, 20); timer.unref();
  let closing;
  return { url: server.url, scope, identity: deviceIdentity,
    close: () => closing ??= (async () => {
      clearInterval(timer); while (pending) await new Promise(resolve => setTimeout(resolve, 10));
      await server.close(); await build.flush(); spool.close(); if (failure) throw failure;
      return { model: 'controlled-synthetic-platform', realKernel: true, mainCalls, extractionCalls, initialExtractionDone,
        recallAdopted, forgottenAbsent, lateWriteRejected, backupReplayRejected, closedSession, observations, routes };
    })() };
}
