// Original HTTP routes, persistent session/checkpoint stores and kernel. Only the upstream
// model/audio provider is synthetic. No fake checkpoint receipt, projection or cleanup route.
import assert from 'node:assert/strict';
import { mkdir, readdir, readFile, rename, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startSessionApiFixture({ source, directory, authenticate }) {
  await mkdir(directory, { recursive: true });
  const commands = join(directory, 'host-commands'), responses = join(directory, 'host-responses');
  const cwd = join(directory, 'workspace'), alternate = join(directory, 'alternate');
  await Promise.all([commands, responses, cwd, alternate].map(path => mkdir(path)));
  const load = path => import(pathToFileURL(join(source, path)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const { zeroAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { SUMMARY_SECTION_TITLES, budgetSpendOf } = await load('packages/kernel/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const { setEnglish } = await load('packages/server/test/helpers.ts'); setEnglish();
  const caps = zeroAppCapabilities('desktop');
  const fake = createFakePlatform({ features: ['image-input'], bundleExtra: {
    models: [{ handle: 'fake-main', modelId: 'fake-main-id', displayName: 'API synthetic', protocol: 'twp', contextWindow: 128000,
      capabilities: { inputModalities: { image: 'supported' } } }], capabilities: { ...caps,
    platform: { ...caps.platform, speechToText: true, textToSpeech: true } }, app: { platform: 'desktop' } } });
  const exchanges = [], audio = [], routes = [], profiles = [], handles = new Map(), storeErrors = [], resourceGates = new Map();
  let expectedResourceFailures = 0;
  const summary = SUMMARY_SECTION_TITLES.map((title, index) => `${index + 1}. ${title}: NET retained decision.`).join('\n') + '\n' + 'Recorded synthetic detail. '.repeat(30);
  const frame = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  const filler = 'Synthetic record 中文 preserved historical detail. '.repeat(100);
  let failure, busy = false, closing = false;
  const fetchImpl = async (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    assert.equal(url.origin, FAKE_API_BASE);
    if (url.pathname === '/v1/my-usage') {
      assert.equal(url.search, '?window=1d'); assert.equal(init?.method, 'GET');
      const headers = new Headers(init?.headers), token = headers.get('x-tansr-app-token');
      const match = /^tok\.(.+)\.[0-9]+$/.exec(token ?? ''); assert.ok(match); assert.equal(headers.get('x-tansr-app-key'), null);
      profiles.push({ path: url.pathname, endUserId: match[1] });
      return new Response(JSON.stringify({ window: '1d', endUserId: match[1], requests: 7, inTokens: 1234, outTokens: 56, cacheRTokens: 78, cacheWTokens: 9 }), { headers: { 'content-type': 'application/json' } });
    }
    if (url.pathname === '/t1/asr' || url.pathname === '/t1/tts') {
      const request = JSON.parse(String(init?.body)); audio.push({ path: url.pathname, request });
      if (request.input === 'NET-AUDIO-QUOTA') return new Response(JSON.stringify({ error: { code: 'tts_quota_exceeded', message: 'Synthetic quota' } }), { status: 429 });
      return new Response(JSON.stringify(url.pathname.endsWith('/asr') ? { model: 'synthetic-asr', text: 'NET transcribed draft 中文', language: 'zh', durationMs: 1000 }
        : { model: 'synthetic-tts', billedChars: request.input.length, audio: { b64: 'UklGRg==', mime: 'audio/wav', format: 'wav' } }), { headers: { 'content-type': 'application/json' } });
    }
    if (url.pathname !== '/t1/exchange') return fake.fetchImpl(input, init);
    const request = JSON.parse(String(init?.body)); exchanges.push(request); assert.ok(exchanges.length <= 32);
    assert.equal((request.tools ?? []).length, 0);
    const compacting = request.meta?.purpose === 'compaction' || request.thread.some(item => item.role === 'system' && JSON.stringify(item.blocks).includes('compaction'));
    const responseText = compacting ? summary : `NET answer ${exchanges.length} ${filler}`;
    return new Response(frame('t.open', { exchangeId: `api-${exchanges.length}`, model: request.model, protocol: 'twp/1' }) +
      frame('t.delta', { i: 0, t: 'text', v: responseText }) +
      frame('t.usage', { inTokens: 120, outTokens: 35, cacheRTokens: 0, cacheWTokens: 0 }) + frame('t.close', { stop: 'end_turn' }), { headers: { 'content-type': 'text/event-stream' } });
  };
  const store = createServeAgentSessionStore({ dir: join(directory, 'sessions'), ownership: {} });
  const build = createAgentSessionFactory({ cwd, cwdPolicy: { allowedRoots: [directory] }, store, checkpoints: { autoBeforeCompact: false },
    onStoreError(sessionId, error) { storeErrors.push({ sessionId, code: error?.code ?? 'store_failed' }); },
    platform: { apiBaseUrl: FAKE_API_BASE, appId: 'net-integration-app', appKey: 'synthetic-only', fetchImpl, maxOutputTokens: 8192 } });
  const factory = { ...build.factory, async create(init) {
    const result = await build.factory.create(init); const handle = result.handle; handles.set(handle.sessionId, handle);
    const mode = init.labels?.resource;
    if (mode === 'delayed' || mode === 'failed') {
      const settle = handle.settleResources.bind(handle); let release;
      const pending = new Promise(resolve => { release = resolve; }); resourceGates.set(handle.sessionId, { release, mode, entered: false });
      // Inject an owned host resource, after real kernel/store settlement. HTTP still reads
      // the original registry's actual resource promise and safe failure translation.
      let injected = false;
      handle.settleResources = async () => {
        await settle(); resourceGates.get(handle.sessionId).entered = true;
        if (mode === 'delayed') await pending;
        else { if (!injected) { injected = true; expectedResourceFailures++; } throw new Error('NET_API_RESOURCE_FAILURE'); }
      };
    }
    return result;
  } };
  const server = await startServer({ host: '127.0.0.1', port: 0, token: 'synthetic-unused', readyFrame: 'none', heartbeatMs: 0,
    createSession: { create() { throw new Error('No v1 factory'); } },
    terminal: { contract: 'terminal-services-v1', scopeFor: reference => ({ applicationScopeId: 'net-integration-app', endUserId: reference.endUserId, authorizationRevision: '1' }) },
    v2: { createSession: factory, store: build.storeReader, authenticate(request) { routes.push({ method: request.method, path: new URL(request.url, 'http://local').pathname }); return authenticate(request); }, governance: { sweepIntervalMs: 0 } } });
  const processed = new Set();
  const timer = setInterval(async () => {
    if (busy || failure || closing) return; busy = true;
    try {
      const files = (await readdir(commands)).filter(file => file.endsWith('.json')).sort(); assert.ok(files.length <= 128);
      const file = files.find(item => !processed.has(item)); if (!file) return;
      assert.match(file, /^[a-z0-9-]{1,64}\.json$/); processed.add(file);
      const value = JSON.parse(await readFile(join(commands, file), 'utf8')); assert.equal(file, `${value.id}.json`);
      let result;
      if (value.action === 'inspect') result = { cwd, alternate, exchanges: exchanges.length, audio, routes, profiles, storeErrors };
      else if (value.action === 'usage') {
        await build.flush(); const record = await store.get('net-integration-user', value.sessionId); assert.ok(record);
        const snapshot = record.usageSnapshot?.payload.snapshot;
        result = { snapshot: snapshot ?? null, totals: snapshot === undefined ? null : budgetSpendOf(snapshot) };
      }
      else if (value.action === 'settle') { const handle = handles.get(value.sessionId); assert.ok(handle); await handle.settleResources(); await build.flush(); result = { status: handle.status(), settled: true }; }
      else if (value.action === 'release-resource') { const gate = resourceGates.get(value.sessionId); assert.ok(gate); assert.equal(gate.mode, 'delayed'); assert.equal(gate.entered, true); gate.release(); result = { released: true }; }
      else throw new Error('Unknown trusted session API fixture command.');
      await writeFile(join(responses, `${value.id}.tmp`), JSON.stringify({ id: value.id, value: result }), { flag: 'wx' });
      await rename(join(responses, `${value.id}.tmp`), join(responses, `${value.id}.json`));
    } catch (error) { failure = { message: String(error?.message ?? error).slice(0, 2048) }; await writeFile(join(directory, 'host-failure.json'), JSON.stringify(failure)); }
    finally { busy = false; }
  }, 10);
  return { url: server.url, async close() {
    closing = true; clearInterval(timer); while (busy) await new Promise(resolve => setTimeout(resolve, 10));
    for (const gate of resourceGates.values()) gate.release();
    const cleanupErrors = []; let report;
    try { report = await server.drain({ timeoutMs: 2000 }); } catch (error) { cleanupErrors.push(error); }
    try { await server.settleResources(); } catch (error) { cleanupErrors.push(error); }
    await build.flush();
    const leaves = error => error instanceof AggregateError ? error.errors.flatMap(leaves) : [error];
    const leafErrors = cleanupErrors.flatMap(leaves);
    if (expectedResourceFailures > 0) { assert.ok(leafErrors.length >= 1); assert.ok(leafErrors.every(error => error?.message === 'NET_API_RESOURCE_FAILURE')); }
    else assert.deepEqual(leafErrors, []);
    const evidence = { realKernel: true, realStore: true, routes, profiles, exchanges: exchanges.length, audioCalls: audio.length, storeErrors, report: report ?? null,
      expectedResourceFailures, observedCleanupFailureLeaves: leafErrors.length, failure: failure ?? null };
    await writeFile(join(directory, 'result.json'), JSON.stringify(evidence)); assert.deepEqual(storeErrors, []); assert.equal(failure, undefined); return evidence;
  } };
}
