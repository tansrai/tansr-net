// Real Serve/kernel for native UI acceptance. Only the platform/model is synthetic.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile, rename } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyServeSourceSnapshot } from '../../scripts/serve-source-snapshot.mjs';

const source = process.env.TANSR_SERVE_SOURCE;
const directory = process.env.TANSR_NATIVE_UI_DIRECTORY;
const token = process.env.TANSR_NATIVE_UI_TOKEN;
assert.ok(source && directory && token && process.env.TANSR_SERVE_SOURCE_SNAPSHOT);
const sourceBefore = verifyServeSourceSnapshot(source, process.env.TANSR_SERVE_SOURCE_SNAPSHOT);
await mkdir(directory, { recursive: true });
const load = path => import(pathToFileURL(join(source, path)).href);
const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
const { zeroAppCapabilities } = await load('packages/sdk/src/index.ts');
const { createFakePlatform, FAKE_API_BASE } = await load('packages/server/test/fake-platform-fetch.ts');
const { setEnglish } = await load('packages/server/test/helpers.ts'); setEnglish();
const records = [], issued = new Map();
const capability = zeroAppCapabilities('desktop');
capability.tools.customTools = capability.tools.askUser = capability.tools.todoWrite = true;
const { nativeMediaPlatform } = await import('./ServeNativeMediaFixture.mjs');
const media = await nativeMediaPlatform({ directory });
const { nativeMemoryPlatform } = await import('./ServeNativeMemoryFixture.mjs');
const memory = await nativeMemoryPlatform({ source, directory,
  scope: { applicationScopeId: 'native-ui-app', endUserId: 'native-ui-user', authorizationRevision: '1' },
  authenticate: request => request.headers.authorization === `Bearer ${token}` ? { endUserId: 'native-ui-user' } : null,
  allowedTools: ['SearchMemory', 'Read', 'List', 'Write', 'Edit', 'AskUser', 'TodoWrite', 'ImageGen', 'VideoGen', 'TextToSpeech', 'SpeechToText', 'set_window_title', 'native_skill', 'mcp_echo'] });
capability.platform.imageGen = capability.platform.videoGen = capability.platform.speechToText = capability.platform.textToSpeech = true;
const fake = createFakePlatform({ cacheControl: 'private, max-age=0', features: ['image-input', 'reasoning-content', 'reasoning-off'], bundleExtra: {
  ...media?.bundleExtra, app: { platform: 'desktop' }, capabilities: {
    ...capability, ...memory.capabilityExtra, ...(media?.bundleExtra.capabilities ?? {}),
    tools: { ...capability.tools, ...(media?.bundleExtra.capabilities?.tools ?? {}) },
  }, models: [
    { handle: 'ui-main', modelId: 'ui-main', displayName: 'UI Main', protocol: 'twp', capabilities: { inputModalities: { image: 'supported' } }, contextWindow: 32768 },
    { handle: 'ui-large', modelId: 'ui-large', displayName: 'UI Large', protocol: 'twp', capabilities: { inputModalities: { image: 'supported' } }, contextWindow: 65536 },
  ], aliases: { main: 'ui-main' },
} });
const frame = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
let recordWrite = Promise.resolve();
const saveRecords = () => { const bytes = JSON.stringify(records); recordWrite = recordWrite.then(async () => {
  await writeFile(join(directory, 'native-ui-records.tmp'), bytes); await rename(join(directory, 'native-ui-records.tmp'), join(directory, 'native-ui-records.json'));
}); return recordWrite; };
const fetchImpl = async (input, init) => {
  const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
  assert.equal(url.origin, FAKE_API_BASE, 'No paid or external platform is permitted.');
  if (url.pathname === '/v1/my-usage') {
    assert.equal(url.searchParams.get('window'), '1d');
    const appToken = new Headers(init?.headers).get('x-tansr-app-token');
    assert.ok(appToken?.startsWith('tok.native-ui-user.'), 'Usage must retain the authenticated end-user app token.');
    return Response.json({ window: '1d', endUserId: 'native-ui-user', requests: 7, inTokens: 1234, outTokens: 56, cacheRTokens: 78, cacheWTokens: 9 });
  }
  const response = await media?.fetch(input, init); if (response) return response;
  if (url.pathname !== '/t1/exchange') {
    let disabled = false;
    if (url.pathname === '/t1/config') {
      try { disabled = JSON.parse(await readFile(join(directory, 'native-media-control.json'), 'utf8')).enabled === false; }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
      fake.setBundleEtag(disabled ? 'ui-media-disabled' : 'ui-media-enabled');
    }
    const result = await fake.fetchImpl(input, init);
    if (url.pathname !== '/t1/config' || result.status !== 200 || !disabled) return result;
    const body = await result.json();
    for (const name of ['imageGen', 'videoGen', 'speechToText', 'textToSpeech']) body.capabilities.platform[name] = false;
    return Response.json(body, { status: result.status, headers: result.headers });
  }
  const request = JSON.parse(String(init.body));
  const memoryResponse = memory.tryModel(request); if (memoryResponse) return memoryResponse;
  const marker = request.thread.filter(x => x.role === 'user').flatMap(x => x.blocks.filter(b => b.t === 'text').map(b => b.v)).findLast(x => /^UI_[A-Z_]+/.test(x));
  assert.ok(marker); assert.ok(records.filter(x => x.kind === 'model').length < 150);
  const match = /^UI_([A-Z_]+)_(WPF|WINFORMS)$/.exec(marker); assert.ok(match, 'Only named synthetic UI scenarios are accepted.');
  const kind = match[1], host = match[2]; records.push({ kind: 'model', marker, model: request.model, at: Date.now() });
  if (kind === 'IMAGE_INPUT') assert.ok(request.thread.findLast(message => message.role === 'user').blocks.some(block => block.t === 'image'), 'Original user image block must reach the real model boundary.');
  await saveRecords();
  let tool;
  if (!issued.has(marker)) {
    if (['ALLOW', 'DENY', 'EXPIRE', 'RESUMED'].includes(kind)) tool = { name: 'set_window_title', args: { title: `Approved ${host} ${kind}` } };
    else if (kind === 'QUESTION') tool = { name: 'AskUser', args: { questions: [{ id: 'q-ui', prompt: 'Choose a UI answer', options: [{ id: 'a', label: 'Alpha' }, { id: 'b', label: 'Beta' }] }] } };
    else if (kind === 'SKILL') tool = { name: 'native_skill', args: { name: 'inline-guide' } };
    else if (kind === 'MCP') tool = { name: 'mcp_echo', args: { text: 'native ui mcp echo' } };
    else if (kind === 'TODO') tool = { name: 'TodoWrite', args: { todos: [{ id: 'ui-todo', content: 'Native UI todo', status: 'pending' }], merge: false } };
    else if (kind.startsWith('MEDIA_')) tool = media.toolRequests[`UI_${kind}`];
    if (tool) { assert.ok(request.tools.some(x => x.name === tool.name), `Required UI tool unavailable: ${tool.name}`); issued.set(marker, `ui-tool-${records.length}`); }
  }
  if (!tool && issued.has(marker) && !['DENY', 'EXPIRE'].includes(kind)) {
    const result = request.thread.flatMap(message => message.blocks ?? []).findLast(block => block.t === 'tool_result' && block.toolUseId === issued.get(marker));
    assert.ok(result && result.isError !== true, `The real ${kind} tool must succeed before UI_DONE is emitted.`);
    records.push({ kind: 'tool-result', marker, toolUseId: result.toolUseId, isError: result.isError === true });
  }
  const open = frame('t.open', { exchangeId: `ui-${records.length}`, model: request.model, protocol: 'twp/1' });
  if (kind === 'HOLD') {
    const encoder = new TextEncoder();
    return new Response(new ReadableStream({
      start(controller) {
        controller.enqueue(encoder.encode(open + frame('t.delta', { i: 0, t: 'text', v: `UI_HOLDING_${host}` })));
        const close = () => { try { controller.close(); } catch {} };
        if (init.signal?.aborted) close(); else init.signal?.addEventListener('abort', close, { once: true });
      },
    }), { headers: { 'content-type': 'text/event-stream' } });
  }
  return new Response(open + (kind.startsWith('THINK_') ? frame('t.delta', { i: 0, t: 'thinking', v: `UI_REASONING_${kind.slice(6)}_${host}` }) : '') + (tool
    ? frame('t.delta', { i: 0, t: 'tool_use', id: issued.get(marker), name: tool.name, vJson: JSON.stringify(tool.args) })
    : frame('t.delta', { i: kind.startsWith('THINK_') ? 1 : 0, t: 'text', v: `UI_DONE_${kind}_${host} · 中文🙂` })) + frame('t.close', { stop: tool ? 'tool_use' : 'end_turn' }),
  { headers: { 'content-type': 'text/event-stream' } });
};
const originalFetch = globalThis.fetch;
globalThis.fetch = (input, init) => {
  const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
  assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname), 'External fetch forbidden.'); return originalFetch(input, init);
};
const store = createServeAgentSessionStore({ dir: join(directory, 'sessions'), ownership: {} });
const build = createAgentSessionFactory({ cwd: directory, store, configuration: {}, permissionTimeoutMs: 10000,
  execution: memory.execution,
  platform: { apiBaseUrl: FAKE_API_BASE, appId: 'native-ui-app', appKey: 'synthetic', fetchImpl, maxOutputTokens: 8192, memoryPublicationFor: memory.memoryPublicationFor } });
const createSession = build.factory.create.bind(build.factory);
build.factory.create = async init => { const created = await createSession(init); memory.trackHandle?.(created.handle); return created; };
const server = await startServer({ host: '127.0.0.1', port: 0, token: 'unused-v1-synthetic', readyFrame: 'none', heartbeatMs: 0,
  createSession: { create() { throw new Error('v1 factory disabled'); } },
  terminal: { contract: 'terminal-services-v1', scopeFor: reference => ({ applicationScopeId: 'native-ui-app', endUserId: reference.endUserId, authorizationRevision: '1' }) },
  v2: { createSession: build.factory, store: build.storeReader, governance: { sweepIntervalMs: 0 },
    authenticate(request) { const url = new URL(request.url, 'http://localhost'); records.push({ kind: 'http', method: request.method, path: url.pathname, at: Date.now() }); void saveRecords(); return request.headers.authorization === `Bearer ${token}` ? { endUserId: 'native-ui-user' } : null; } } });
await writeFile(join(directory, 'ready.json'), JSON.stringify({ url: server.url, source: sourceBefore, memory: memory.configuration }), { flag: 'wx' });
let stopped = false;
async function close() {
  if (stopped) return; stopped = true;
  try { const report = await server.drain({ timeoutMs: 5000 }); await server.settleResources?.(); await build.flush(); await saveRecords(); await memory.close();
    const sourceAfter = verifyServeSourceSnapshot(source, process.env.TANSR_SERVE_SOURCE_SNAPSHOT);
    await writeFile(join(directory, 'result.json'), JSON.stringify({ records, report, media: media?.mediaRecords ?? [], sourceBefore, sourceAfter }), { flag: 'wx' });
  } catch (error) { await writeFile(join(directory, 'failure.txt'), error.stack ?? String(error), { flag: 'wx' }); process.exitCode = 1; }
  finally { process.exit(process.exitCode ?? 0); }
}
process.on('SIGINT', close); process.on('SIGTERM', close);
const check = setInterval(async () => { try { await readFile(join(directory, 'stop')); clearInterval(check); await close(); } catch (error) { if (error.code !== 'ENOENT') throw error; } }, 100);
