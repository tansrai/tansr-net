// Synthetic upstream only. Session ownership, loop, input admission, configuration CAS,
// context assembly, history and HTTP/SSE are the actual public Serve implementation.
import assert from 'node:assert/strict';
import { mkdir, readdir, readFile, rename, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startSessionControlsFixture({ source, directory, authenticate }) {
  await mkdir(directory, { recursive: true });
  const commands = join(directory, 'host-commands'), responses = join(directory, 'host-responses');
  await mkdir(commands); await mkdir(responses);
  const load = path => import(pathToFileURL(join(source, path)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const { zeroAppCapabilities } = await load('packages/sdk/src/index.ts');
  const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
  const { setEnglish } = await load('packages/server/test/helpers.ts'); setEnglish();
  const profiles = new Map(), handles = new Map(), exchanges = [], gates = new Map();
  const frame = (name, data) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`;
  let failure, busy = false, closing = false;
  async function publish(name, value) {
    const text = JSON.stringify(value); assert.ok(Buffer.byteLength(text) <= 1048576);
    await writeFile(join(responses, `${name}.tmp`), text, { flag: 'wx' });
    await rename(join(responses, `${name}.tmp`), join(responses, `${name}.json`));
  }
  async function failed(error, stage) {
    if (failure) return;
    failure = { type: String(error?.name ?? 'Error').slice(0, 128), message: String(error?.message ?? error).slice(0, 2048), stage };
    await writeFile(join(directory, 'host-failure.tmp'), JSON.stringify(failure), { flag: 'wx' });
    await rename(join(directory, 'host-failure.tmp'), join(directory, 'host-failure.json'));
  }
  async function makeProfile(name) {
    const root = join(directory, name); await mkdir(root);
    const state = { prompt: 'NET_PLATFORM_P1', policy: 'prepend', revision: 1 };
    const fake = createFakePlatform({ features: ['image-input', 'reasoning-content', 'reasoning-off'],
      bundleExtra: { app: { platform: 'desktop' }, capabilities: zeroAppCapabilities('desktop'),
        models: [
          { handle: 'controls-main', modelId: 'controls-main-id', displayName: 'Main', protocol: 'twp', capabilities: { inputModalities: { image: 'supported' } }, contextWindow: 32768 },
          { handle: 'controls-large', modelId: 'controls-large-id', displayName: 'Large', protocol: 'twp', capabilities: { inputModalities: { image: 'supported' } }, contextWindow: 65536 },
          { handle: 'controls-tiny', modelId: 'controls-tiny-id', displayName: 'Tiny', protocol: 'twp', capabilities: {}, contextWindow: 64 },
        ], aliases: { main: 'controls-main' } } });
    const fetchImpl = async (input, init) => {
      const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
      assert.equal(url.origin, FAKE_API_BASE);
      if (url.pathname !== '/t1/exchange') {
        const response = await fake.fetchImpl(input, init);
        if (url.pathname !== '/t1/config' || response.status !== 200) return response;
        return new Response(JSON.stringify({ ...await response.json(), systemPrompt: state.prompt, systemPromptPolicy: state.policy }),
          { status: 200, headers: response.headers });
      }
      try {
        const request = JSON.parse(String(init?.body));
        assert.ok(exchanges.length < 64, 'Synthetic model request bound exceeded.');
        assert.ok(request.meta?.purpose !== 'memory', 'This fixture does not enable memory.');
        assert.deepEqual(request.tools ?? [], [], 'No host tools are authorized.');
        const text = request.thread.filter(item => item.role === 'user').flatMap(item => item.blocks.filter(block => block.t === 'text').map(block => block.v));
        const marker = text.findLast(value => value.startsWith('NET-CONTROLS/'));
        assert.ok(marker, 'Every model request belongs to an explicit synthetic scenario.');
        const match = /^NET-CONTROLS\/([a-z0-9-]+)\/(hold|next|normal)$/.exec(marker); assert.ok(match);
        const scenario = match[1], ordinal = exchanges.filter(item => item.scenario === scenario).length + 1;
        exchanges.push({ profile: name, scenario, ordinal, request });
        const encoder = new TextEncoder();
        let release;
        const held = match[2] === 'hold' && ordinal === 1;
        const gate = held ? new Promise(resolve => { release = resolve; }) : Promise.resolve();
        if (held) { assert.ok(!gates.has(scenario)); gates.set(scenario, { release, aborted: false }); }
        const signal = init?.signal;
        const abort = () => { const current = gates.get(scenario); if (current) { current.aborted = true; current.release(); } };
        signal?.addEventListener('abort', abort, { once: true });
        if (signal?.aborted) abort();
        const stream = new ReadableStream({
          async start(controller) {
            try {
              controller.enqueue(encoder.encode(frame('t.open', { exchangeId: `controls-${exchanges.length}`, model: request.model, protocol: 'twp/1' }) +
                frame('t.delta', { i: 0, t: 'text', v: `NET_ANSWER_${scenario}_${ordinal}` })));
              await gate;
              if (!signal?.aborted) controller.enqueue(encoder.encode(frame('t.close', { stop: 'end_turn' })));
              controller.close();
            } catch (error) { if (!signal?.aborted) controller.error(error); }
            finally { signal?.removeEventListener('abort', abort); }
          },
          cancel() { release?.(); }
        });
        return new Response(stream, { headers: { 'content-type': 'text/event-stream' } });
      } catch (error) { await failed(error, 'synthetic-upstream'); throw error; }
    };
    const store = createServeAgentSessionStore({ dir: join(root, 'sessions'), ownership: {} });
    const build = createAgentSessionFactory({ cwd: root, store, configuration: {},
      // Keep enough declared output capacity for the real kernel's minimum 1024-token
      // thinking budget. A 128 cap legitimately squeezes thinking off before TWP encoding.
      // The synthetic upstream still returns only the fixed short text above; no paid call.
      platform: { apiBaseUrl: FAKE_API_BASE, appId: 'net-integration-app', appKey: 'synthetic-only', fetchImpl, maxOutputTokens: 8192,
        ...(name === 'absent' ? {} : { system: name === 'empty' ? [] : [{ text: 'NET_HOST_S' }] }), systemAppend: [{ text: 'NET_APPEND_A' }] } });
    const factory = { ...build.factory, async create(init) {
      const result = await build.factory.create(init); handles.set(result.handle.sessionId, { handle: result.handle, profile: name }); return result;
    } };
    const server = await startServer({ host: '127.0.0.1', port: 0, token: 'synthetic-unused-v1', readyFrame: 'none', heartbeatMs: 0,
      createSession: { create() { throw new Error('No legacy v1 factory'); } },
      terminal: { contract: 'terminal-services-v1', scopeFor: reference => ({ applicationScopeId: 'net-integration-app', endUserId: reference.endUserId, authorizationRevision: '1' }) },
      v2: { createSession: factory, store: build.storeReader, authenticate, governance: { sweepIntervalMs: 0 } } });
    const profile = { server, build, state, fake }; profiles.set(name, profile); return profile;
  }
  try { for (const name of ['explicit', 'absent', 'empty']) await makeProfile(name); }
  catch (error) { await Promise.allSettled([...profiles.values()].map(async p => { await p.server.close(); await p.server.settleResources?.(); await p.build.flush(); })); throw error; }
  const processed = new Set();
  async function command(value) {
    if (value.action === 'origins') return Object.fromEntries([...profiles].map(([name, p]) => [name, p.server.url]));
    if (value.action === 'platform') {
      const p = profiles.get(value.profile); assert.ok(p); assert.ok(['fallback', 'prepend'].includes(value.policy));
      assert.match(value.prompt, /^NET_PLATFORM_P[12]$/); p.state.prompt = value.prompt; p.state.policy = value.policy;
      p.fake.setBundleEtag(`controls-${++p.state.revision}`); return { updated: true };
    }
    if (value.action === 'release') { const gate = gates.get(value.scenario); assert.ok(gate); gate.release(); return { released: true }; }
    if (value.action === 'wait-held') {
      const end = Date.now() + 10000;
      while (!gates.has(value.scenario)) { if (failure) throw new Error(failure.message); assert.ok(Date.now() < end, 'Actual model request did not enter.'); await new Promise(resolve => setTimeout(resolve, 10)); }
      return { held: true };
    }
    if (value.action === 'settle') {
      const current = handles.get(value.sessionId); assert.ok(current); assert.equal(current.handle.status(), 'idle');
      await current.handle.settleResources?.(); await profiles.get(current.profile).build.flush(); return { settled: true };
    }
    if (value.action === 'inspect') return { exchanges: exchanges.filter(item => item.scenario === value.scenario), aborted: gates.get(value.scenario)?.aborted ?? false };
    throw new Error('Unknown trusted test command.');
  }
  const timer = setInterval(async () => {
    if (busy || failure || closing) return; busy = true;
    let id;
    try {
      const files = (await readdir(commands)).filter(name => name.endsWith('.json')).sort(); assert.ok(files.length <= 128);
      const file = files.find(name => !processed.has(name)); if (!file) return;
      assert.match(file, /^[a-z0-9-]{1,64}\.json$/); processed.add(file);
      const text = await readFile(join(commands, file), 'utf8'); assert.ok(Buffer.byteLength(text) <= 4096);
      const value = JSON.parse(text); id = value.id; assert.equal(file, `${id}.json`);
      await publish(id, { id, value: await command(value) });
    } catch (error) { await failed(error, `command:${id ?? 'read'}`); }
    finally { busy = false; }
  }, 10);
  return { url: profiles.get('explicit').server.url,
    async close() {
      closing = true; clearInterval(timer); for (const gate of gates.values()) gate.release();
      const reports = [];
      for (const [profile, p] of profiles) {
        const report = await p.server.drain({ timeoutMs: 2000 }); await p.server.settleResources?.(); await p.build.flush(); reports.push({ profile, ...report });
      }
      const evidence = { exchanges: exchanges.length, reports, failure: failure ?? null };
      await writeFile(join(directory, 'result.json'), JSON.stringify(evidence));
      if (failure) throw new Error(`${failure.stage}: ${failure.message}`);
      return evidence;
    } };
}
