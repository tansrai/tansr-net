// No SDK, kernel, permission, IPC, preload or renderer implementation is replaced.
// Only platform model responses are synthetic; observation goes back to the C# QPC sampler.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { createServer } from 'node:http';
import { createConnection } from 'node:net';
import { join } from 'node:path';
import { app } from 'electron';
import { zeroAppCapabilities } from '@tansr/sdk';
import { startChatApp } from '@tansr-benchmark/app';

// Let Electron finish evaluating the entry module before awaiting app readiness.
// A module-level await can otherwise prevent the ready event from being emitted.
async function run() {
  const configAt = process.argv.indexOf('--benchmark-config');
  assert.ok(configAt >= 0, 'A private benchmark config is required.');
  const config = JSON.parse(readFileSync(process.argv[configAt + 1], 'utf8'));
  mkdirSync(config.directory, { recursive: true });
  app.setPath('userData', config.directory);
  const watchdog = setTimeout(() => app.exit(3), 85000);
  const pipe = createConnection(config.pipe);
  await new Promise((resolve, reject) => { pipe.once('connect', resolve); pipe.once('error', reject); });
  const samples = new Set(), faults = [], exchanges = [];
  let chat, callCount = 0;
  const send = value => pipe.write(JSON.stringify(value) + '\n');
  const caps = zeroAppCapabilities('desktop'); caps.tools.shell = true;
  const frame = (type, body) => `event: ${type}\ndata: ${JSON.stringify(body)}\n\n`;
  const command = `& '${config.executable.replaceAll("'", "''")}' benchmark '${config.releaseName.replaceAll("'", "''")}'`;
  const server = createServer((request, response) => { void (async () => {
    const json = value => response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(value));
    if (request.url === '/t1/config') return json({ app: { id: 'net-electron-benchmark', platform: 'desktop' },
      models: [{ handle: 'main', displayName: 'Synthetic only', protocol: 'twp', contextWindow: 128000 }], aliases: { primary: 'main' },
      capabilities: caps, systemPrompt: 'Execute the one requested synthetic benchmark.',
      platformModels: { imageGen: [], videoGen: [], speechToText: [], textToSpeech: [] } });
    if (request.url === '/t1/heartbeat') return json({ features: [] });
    if (request.url?.startsWith('/v1/my-usage')) return response.writeHead(404).end();
    assert.equal(request.url, '/t1/exchange');
    const chunks = []; for await (const chunk of request) chunks.push(Buffer.from(chunk));
    const body = JSON.parse(Buffer.concat(chunks).toString('utf8')); exchanges.push(body);
    const serialized = JSON.stringify(body);
    let delta, stop = 'end_turn';
    if (body.meta?.purpose === 'memory') delta = { i: 0, t: 'text', v: 'nothing to save' };
    else if (serialized.includes('PERMISSION ADJUDICATION')) delta = { i: 0, t: 'text', v: '{"verdict":"endorse","reason":"synthetic benchmark explicitly authorized"}' };
    else {
      callCount++; assert.ok(callCount <= 2, 'The original Shell executes exactly once.');
      if (callCount === 1) {
        assert.ok(body.tools.some(tool => tool.name === 'Shell'));
        delta = { i: 0, t: 'tool_use', id: 'electron-benchmark', name: 'Shell', vJson: JSON.stringify({ command, timeout_ms: 60000 }) }; stop = 'tool_use';
      } else {
        assert.ok(serialized.includes('BENCHMARK_DONE'), 'The original real process must return through the kernel.');
        delta = { i: 0, t: 'text', v: 'BENCHMARK_SETTLED' };
      }
    }
    response.writeHead(200, { 'content-type': 'text/event-stream' });
    response.end(frame('t.open', { exchangeId: 'net-electron-benchmark', model: body.model, protocol: 'twp/1' }) + frame('t.delta', delta) + frame('t.close', { stop }));
  })().catch(error => { faults.push(String(error)); response.writeHead(500).end(); }); });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    await app.whenReady();
    const base = `http://127.0.0.1:${server.address().port}`;
    chat = startChatApp({ apiBase: base, tokenServer: base, appToken: 'synthetic-fixture-token', show: false });
    await chat.whenReady(); const win = chat.getWin(); assert.ok(win);
    win.webContents.on('console-message', (_event, ...values) => {
      const message = values.find(value => typeof value === 'object' && typeof value?.message === 'string')?.message ?? values.find(value => typeof value === 'string');
      if (!message?.startsWith('NET_QPC_DOM:')) return;
      const sample = message.slice('NET_QPC_DOM:'.length);
      if (!samples.has(sample)) { samples.add(sample); send({ stage: 'electron.dom', line: sample }); }
    });
    await win.webContents.executeJavaScript(`(() => {
      const seen = new Set();
      const collect = () => {
        const text = document.getElementById('transcript').textContent;
        for (const match of text.matchAll(/QPC_SAMPLE\\|\\d+\\|\\d+\\|\\d+\\|\\d+\\|中文🙂/g)) {
          if (!seen.has(match[0])) { seen.add(match[0]); console.log('NET_QPC_DOM:' + match[0]); }
        }
      };
      new MutationObserver(collect).observe(document.getElementById('transcript'), {subtree:true,childList:true,characterData:true});
      window.tansrChat.onPermissionAsk(request => window.tansrChat.answerPermission(request.id, true));
      document.getElementById('input').value = 'NET_ELECTRON_BENCHMARK';
      document.getElementById('composer').requestSubmit();
    })()`);
    const until = Date.now() + 70000;
    while (callCount !== 2) { assert.ok(Date.now() < until, 'Original Electron Shell did not settle.'); assert.deepEqual(faults, []); await new Promise(resolve => setTimeout(resolve, 20)); }
    await chat.getSession().idle();
    assert.equal(samples.size, 100, 'Every flushed line must reach the original DOM before release.');
    const launches = readFileSync(join(chat.paths.sandbox, 'launches.txt'), 'utf8').trim().split(/\r?\n/);
    assert.equal(launches.length, 1);
    writeFileSync(join(config.directory, 'electron-evidence.json'), JSON.stringify({ command, callCount, sampleCount: samples.size, launches,
      boundary: 'Original renderer DOM mutation; report traverses Electron console IPC and a named pipe to the common C# QPC collector.',
      processLiveCheck: 'The C# collector checks the source PID at the first and 100th DOM notice before setting the release event.', originalSdk: true, originalApp: true, originalPreload: true, originalRenderer: true,
      versions: process.versions, syntheticExchangeCount: exchanges.length }, null, 2));
    send({ stage: 'electron.complete' });
  } catch (error) { send({ stage: 'electron.failed', error: String(error?.stack ?? error) }); process.exitCode = 1; }
  finally {
    try { await chat?.dispose(); } catch (error) { send({ stage: 'electron.cleanup.failed', error: String(error) }); process.exitCode = 1; }
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
    await new Promise(resolve => pipe.end(resolve)); clearTimeout(watchdog); app.exit(process.exitCode ?? 0);
  }
}

void run().catch(error => { console.error(error); app.exit(1); });
