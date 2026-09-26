// Original remote protocol client running inside Electron. Does not replace the integrated SDK app.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { app, BrowserWindow } from 'electron';
import { Sdk2SessionClient } from '@tansr-shared/session-client';

const index = process.argv.indexOf('--shared-config');
assert.ok(index > 0); const config = JSON.parse(readFileSync(process.argv[index + 1], 'utf8'));
app.setPath('userData', join(config.directory, 'electron-user-data'));
const abort = new AbortController(), events = [], denied = [], started = Date.now();
const guard = setTimeout(() => { abort.abort(); app.exit(3); }, 420000);
const client = new Sdk2SessionClient({ baseUrl: config.url, readToken: () => 'demo1.unified.electron',
  readContext: () => ({ applicationScopeId: 'unified-app', endUserId: 'u-electron', authorizationRevision: '1' }) });
let session, watch, window, failure;
async function state() { const r = await fetch(config.controlUrl + '/state'); assert.equal(r.status, 200); return r.json(); }
async function post(route, value) { const r = await fetch(config.controlUrl + route, { method: 'POST', headers: { authorization: 'Bearer fixture-control', 'content-type': 'application/json' }, body: JSON.stringify(value) }); assert.equal(r.status, 200); return r.json(); }
async function until(predicate) { while (!predicate(await state())) { assert.ok(Date.now() - started < 400000); await new Promise(resolve => setTimeout(resolve, 100)); } }
try {
  await app.whenReady(); window = new BrowserWindow({ show: false, webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true } });
  await window.loadURL('data:text/html,<title>Same Serve protocol acceptance</title><main>Original Electron runtime</main>');
  session = await client.create({ tools: [], budget: { maxTokens: 10000 } });
  watch = (async () => { for await (const event of client.events(session.sessionId, { signal: abort.signal })) events.push(event); })();
  void watch.catch(() => {});
  await post('/sessions', { electron: session.sessionId });
  await post('/timeline', { platform: 'electron', stage: 'ready', sessionId: session.sessionId, version: process.versions.electron });
  await until(s => s.barrier);
  await post('/timeline', { platform: 'electron', stage: 'active' });
  for (const id of [(await state()).sessions.windows, (await state()).sessions.csharp]) {
    for (const [kind, operation] of [['meta', () => client.meta(id)], ['history', () => client.history(id)]]) {
      await assert.rejects(operation, error => { assert.ok([401, 403, 404].includes(error.status)); denied.push({ sessionId: id, kind, status: error.status }); return true; });
    }
  }
  async function turn(id) {
    const before = await client.meta(session.sessionId), from = events.length;
    assert.equal(before.status, 'idle'); await client.send(session.sessionId, 'NET_SHARED:' + JSON.stringify({ id }));
    const end = Date.now() + 25000;
    while (!events.slice(from).some(e => e.type === 'turn.completed' && e.seq > before.lastSeq)) {
      assert.ok(Date.now() < end, 'Own Electron turn must complete.'); await new Promise(resolve => setTimeout(resolve, 40));
    }
    assert.ok(events.slice(from).some(e => e.type === 'msg.text.delta' && e.text === `SHARED_SETTLED:${id}`));
  }
  await turn('electron-before-faults');
  await until(s => s.sessions.csharpFaultsComplete === true);
  await turn('electron-after-other-user-revocation');
  await post('/timeline', { platform: 'electron', stage: 'survived-faults' });
  await until(s => s.sessions.mobileStage === 'done');
  const history = await client.history(session.sessionId);
  assert.ok(!JSON.stringify(history).includes('native-mobile-') && !JSON.stringify(history).includes('CSHARP_PRIVATE_WORKSPACE'));
  await post('/timeline', { platform: 'electron', stage: 'finish', events: events.length });
  writeFileSync(join(config.directory, 'electron-evidence.json'), JSON.stringify({ passed: true, versions: process.versions, session, denied, events,
    role: 'Original Sdk2SessionClient inside Electron; complete integrated SDK/IPC compatibility is a separate original-app regression.' }, null, 2));
} catch (error) { failure = error; writeFileSync(join(config.directory, 'electron-failure.json'), JSON.stringify({ error: error?.stack ?? String(error), events }, null, 2)); }
finally {
  abort.abort(); await watch?.catch(error => { if (!abort.signal.aborted) failure ??= error; });
  if (session) await client.close(session.sessionId).catch(error => { failure ??= error; });
  window?.destroy(); clearTimeout(guard); app.exit(failure ? 1 : 0);
}
