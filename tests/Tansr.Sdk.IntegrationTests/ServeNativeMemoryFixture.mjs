// Composition only: real Serve memoryPublicationFor + original execution spool. The
// caller owns its one server, authentication, main model loop, native UI and shutdown.
// The helper touches only its explicit synthetic fixture directory, never user memory.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, renameSync } from 'node:fs';
import { mkdir } from 'node:fs/promises';
import { join, dirname, basename } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function nativeMemoryPlatform({ source, directory, scope, authenticate,
  executorId = 'native-ui-pc', allowedTools = ['SearchMemory', 'Read', 'List', 'Write', 'Edit'] }) {
  assert.ok(scope?.applicationScopeId && scope?.endUserId && scope?.authorizationRevision);
  assert.equal(typeof authenticate, 'function');
  assert.ok(allowedTools.length > 0 && allowedTools.length <= 16);
  const { openSqliteArchiveSpool, memoryPublicationKey } = await import(pathToFileURL(join(source, 'packages/server/src/extensions/index.ts')).href);
  const { getPlatformMemoryLifecycle } = await import(pathToFileURL(join(source, 'packages/server/src/v2/memory-lifecycle.ts')).href);
  await mkdir(directory, { recursive: true });
  const controlFile = join(directory, 'native-memory-control.json');
  const identityFile = join(directory, 'native-memory-identities.json');
  const topic = 'native-ui-preference.md';
  const fact = 'NATIVE_UI_MEMORY_FACT: the synthetic user prefers violet report headings.';
  const observations = [], identities = new Map(), extracted = new Set(), extractedSessions = new Set(), handles = new Map();
  let modelCalls = 0, closed = false, statusWork, closeWork, lastStatus;
  const limit = { bytes: 64 * 1048576, records: 4096 };
  const spool = await openSqliteArchiveSpool({ path: join(directory, 'native-memory-execution.sqlite'), mode: 'create', storeId: 'native-ui-device-memory',
    bindings: [{ bindingId: 'native-memory', applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId,
      limits: { binding: limit, application: limit, endUser: limit } }], globalLimit: limit,
    maxReservations: 1024, maxEntries: 4096, maxOperations: 4096, maxDatabasePages: 32768 });
  function control() {
    try {
      const bytes = readFileSync(controlFile); assert.ok(bytes.length <= 4096);
      const value = JSON.parse(bytes.toString('utf8')); assert.equal(typeof value.enabled, 'boolean');
      if (value.minimumDeletionGeneration !== undefined) assert.match(value.minimumDeletionGeneration, /^(0|[1-9][0-9]*)$/);
      return value;
    } catch (error) { if (error.code === 'ENOENT') return { enabled: false }; throw error; }
  }
  function persist(name, value) {
    const file = join(directory, name), temporary = file + '.tmp';
    writeFileSync(temporary, JSON.stringify(value)); renameSync(temporary, file);
  }
  function observe(sessionId, kind, value) {
    assert.ok(observations.length < 1024, 'Bounded native memory evidence exceeded.');
    observations.push({ sessionId, kind, value, synthetic: true });
    persist('native-memory-observations.json', observations);
  }
  const quiescent = (handle, lifecycle) => {
    const state = lifecycle?.status();
    return handle.status() === 'idle' && state !== undefined && !state.closing &&
      !state.extractionInFlight && !state.consolidationInFlight && !state.promotionInFlight;
  };
  function writeStatus() {
    if (closed || statusWork) return statusWork ?? Promise.resolve();
    statusWork = (async () => {
      const rows = [];
      for (const [sessionId, handle] of handles) {
        const lifecycle = getPlatformMemoryLifecycle(handle);
        let idle = false;
        if (quiescent(handle, lifecycle) && handle.owner) {
          try { await handle.owner.assertIdle(); idle = quiescent(handle, lifecycle); }
          catch { idle = false; } // Busy, reconciliation or failed ownership is never a successful idle observation.
        }
        rows.push({ sessionId, idle, extracted: extractedSessions.has(sessionId) });
      }
      const encoded = JSON.stringify(rows);
      if (!closed && encoded !== lastStatus) { persist('native-memory-status.json', rows); lastStatus = encoded; }
    })().finally(() => { statusWork = undefined; });
    return statusWork;
  }
  const statusTimer = setInterval(() => {
    void writeStatus().catch(error => {
      // Keep the original failure visible; the UI cannot mistake an absent/stale status for a completed seed.
      if (!closed) persist('native-memory-status-failure.json', { code: error.code ?? error.name, message: String(error.message).slice(0, 1024) });
    });
  }, 100);
  statusTimer.unref();
  function trackHandle(handle) {
    assert.equal(typeof handle?.sessionId, 'string');
    if (!identities.has(handle.sessionId)) return; // Ordinary UI sessions have no memory source.
    handles.set(handle.sessionId, handle);
  }
  function memoryPublicationFor(selected) {
    assert.equal(selected.applicationScopeId, scope.applicationScopeId); assert.equal(selected.endUserId, scope.endUserId);
    assert.match(selected.sessionId, /^[A-Za-z0-9_-]{1,128}$/);
    if (!control().enabled) return undefined;
    let entry = identities.get(selected.sessionId);
    if (!entry) {
      assert.ok(identities.size < 8, 'Only explicit native memory test sessions are permitted.');
      const identity = { kind: 'client-managed', domain: 'native-ui/session/' + selected.sessionId,
        sourceId: 'native-ui-memory-' + selected.sessionId, sourceGeneration: '1',
        applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId };
      const deviceIdentity = { scope: { applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId },
        sourceId: identity.sourceId, sourceGeneration: identity.sourceGeneration, domainKey: memoryPublicationKey(identity) };
      entry = { sessionId: selected.sessionId, identity, deviceIdentity, executorId }; identities.set(selected.sessionId, entry);
      persist('native-memory-identities.json', [...identities.values()]);
    }
    return { identity: entry.identity, mode: 'create', enabled: () => !closed && control().enabled,
      balance: () => null, minimumDeletionGeneration: () => control().minimumDeletionGeneration ?? '0',
      provenance: () => ({ deletionGeneration: control().minimumDeletionGeneration ?? '0', origins: ['synthetic:native-ui-memory-seed'] }),
      promotion: { projectFile: 'PROJECT.md', userFile: 'USER.md' }, extraction: { inviteGate: 'off' }, recallSelector: false,
      onObservation: (kind, value) => observe(selected.sessionId, kind, value) };
  }
  const execution = { applicationScopeId: scope.applicationScopeId,
    authorize: (request, user) => {
      const accepted = authenticate(request)?.endUserId === scope.endUserId && user === scope.endUserId;
      return { controller: accepted, ...(accepted ? { executorId } : {}) };
    }, readPolicy: async () => ({ authorizationRevision: scope.authorizationRevision, tools: [...allowedTools] }),
    spoolFor: () => ({ spool, bindingId: 'native-memory' }) };
  const frame = (event, value) => `event: ${event}\ndata: ${JSON.stringify(value)}\n\n`;
  function answer(request, text, tool) {
    return new Response(frame('t.open', { exchangeId: 'native-ui-memory-' + modelCalls, model: request.model, protocol: 'twp/1' }) +
      (text ? frame('t.delta', { i: 0, t: 'text', v: text }) : '') +
      (tool ? frame('t.delta', { i: text ? 1 : 0, t: 'tool_use', id: tool.id, name: tool.name, vJson: JSON.stringify(tool.args) }) : '') +
      frame('t.close', { stop: tool ? 'tool_use' : 'end_turn' }), { headers: { 'content-type': 'text/event-stream' } });
  }
  // This handles only the original kernel memory fork. It neither implements the
  // agent loop nor changes its authority, publication, extraction or promotion code.
  function tryModel(request) {
    if (request.meta?.purpose !== 'memory') return undefined;
    assert.ok(++modelCalls <= 48, 'Unexpected synthetic native memory workload.');
    assert.deepEqual((request.tools ?? []).map(tool => tool.name).sort(), ['Edit', 'Read', 'Write']);
    const blocks = request.thread.flatMap(message => message.blocks ?? []);
    const instructions = blocks.filter(block => block.t === 'text').map(block => block.v).join('\n');
    const result = id => blocks.findLast(block => block.t === 'tool_result' && block.toolUseId === id);
    const memoryDirectory = /Auto-memory directory: ([^\r\n]+)/.exec(instructions)?.[1];
    if (instructions.includes('<memory-consolidation-task>')) return answer(request, 'No additional synthetic consolidation required.');
    assert.ok(memoryDirectory, 'Original fork must supply its authorized virtual memory directory.');
    if (instructions.includes('<memory-promotion-task>')) {
      const target = /- Project instructions \(team\/project conventions\): ([^\r\n]+)/.exec(instructions)?.[1];
      assert.ok(target && basename(target) === 'PROJECT.md' && dirname(target) === dirname(memoryDirectory));
      const read = result('native-memory-promotion-read'), write = result('native-memory-promotion-write');
      if (!read) return answer(request, null, { id: 'native-memory-promotion-read', name: 'Read', args: { file_path: join(memoryDirectory, 'MEMORY.md') } });
      assert.notEqual(read.isError, true, 'Real memory promotion read failed.');
      if (!write) return answer(request, 'Propose the reviewed synthetic report preference in PROJECT.md; the following write requires original user approval.',
        { id: 'native-memory-promotion-write', name: 'Write', args: { file_path: target, contents: '# Synthetic UI instructions\n\n' + fact + '\n' } });
      assert.notEqual(write.isError, true, 'Original promotion permission or publication refused the write.');
      return answer(request, 'The approved synthetic preference was promoted through the original memory write plane.');
    }
    assert.ok(instructions.includes('<memory-extraction-task>'), 'No other synthetic memory model purpose is allowed.');
    if (!instructions.includes('UI_MEMORY_SEED_') || extracted.has(memoryDirectory)) return answer(request, 'No additional synthetic memory to save.');
    const read = result('native-memory-index-read'), topicWrite = result('native-memory-topic-write'), indexWrite = result('native-memory-index-write');
    if (!read) return answer(request, null, { id: 'native-memory-index-read', name: 'Read', args: { file_path: join(memoryDirectory, 'MEMORY.md') } });
    if (!topicWrite) return answer(request, null, { id: 'native-memory-topic-write', name: 'Write', args: { file_path: join(memoryDirectory, topic), contents: '# Synthetic preference\n\n' + fact + '\n' } });
    assert.notEqual(topicWrite.isError, true, 'Real memory extraction topic write failed.');
    if (!indexWrite) return answer(request, null, { id: 'native-memory-index-write', name: 'Write', args: { file_path: join(memoryDirectory, 'MEMORY.md'), contents: '# Synthetic memory\n\n- [Report preference](' + topic + ')\n' } });
    assert.notEqual(indexWrite.isError, true, 'Real memory extraction index write failed.'); extracted.add(memoryDirectory);
    const entry = [...identities.values()].find(value => value.deviceIdentity.domainKey === basename(dirname(memoryDirectory)));
    assert.ok(entry, 'The completed extraction must belong to an explicitly tracked synthetic source.');
    extractedSessions.add(entry.sessionId);
    return answer(request, 'Saved the synthetic preference through the original extraction lifecycle.');
  }
  return { execution, memoryPublicationFor, tryModel, trackHandle, observations,
    capabilityExtra: { execution: { version: 'bound-device-v1', boundDevice: { tools: { read: true, list: true, write: true, edit: true, shell: false } } } },
    configuration: { executorId, identityFile, controlFile, topic, fact, allowedTools: [...allowedTools] },
    identityFor: sessionId => identities.get(sessionId)?.deviceIdentity,
    close() { return closeWork ??= (async () => { closed = true; clearInterval(statusTimer); try { await statusWork; } finally { spool.close(); } })(); } };
}
