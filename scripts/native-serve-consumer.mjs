// A20: the unchanged Console example consumes the final standalone Serve twice:
// owned LocalServeHost and a separately managed URL. Only the upstream platform is synthetic.
import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { spawn, execFile, execFileSync } from 'node:child_process';
import { promisify } from 'node:util';
import { createServer } from 'node:http';
import { createServer as createTcpServer } from 'node:net';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

assert.equal(process.platform, 'win32');
const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const required = name => { assert.ok(process.env[name], name + ' is required'); return process.env[name]; };
const directory = resolve(required('TANSR_NATIVE_CONSUMER_DIRECTORY'));
const source = resolve(required('TANSR_SERVE_SOURCE'));
const snapshot = resolve(required('TANSR_SERVE_SOURCE_SNAPSHOT'));
const sea = resolve(required('TANSR_NATIVE_CONSUMER_SEA'));
const expectedSea = required('TANSR_NATIVE_CONSUMER_SEA_SHA256');
const consoleEncoding = process.env.TANSR_NATIVE_CONSUMER_ENCODING ?? 'gb18030';
const example = resolve(process.env.TANSR_NATIVE_CONSUMER_EXAMPLE ?? join(repository, 'examples/ConsoleAssistant/bin/Release/net10.0-windows/ConsoleAssistant.exe'));
const modulePath = join(directory, 'trusted-host.cjs');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const fileSha = async file => sha(await readFile(file));
const optionalFileSha = async file => { try { return await fileSha(file); } catch (error) { if (error.code === 'ENOENT') return null; throw error; } };
const consoleProduct = { path: example, sha256: await fileSha(example),
  dllSha256: await optionalFileSha(example.replace(/\.exe$/i, '.dll')), coreSha256: await optionalFileSha(join(dirname(example), 'Tansr.Sdk.dll')),
  windowsSha256: await optionalFileSha(join(dirname(example), 'Tansr.Sdk.Windows.dll')) };
assert.ok([consoleProduct.dllSha256, consoleProduct.coreSha256, consoleProduct.windowsSha256].every(Boolean) ||
  [consoleProduct.dllSha256, consoleProduct.coreSha256, consoleProduct.windowsSha256].every(value => value === null), 'Consume a complete build output or the published single-file product.');
consoleProduct.deployment = consoleProduct.dllSha256 ? 'build-output' : 'published-single-file';
const snapshotBytes = await readFile(snapshot), sourceRecord = JSON.parse(snapshotBytes);
assert.equal(sourceRecord.format, 'tansr-serve-source-snapshot-v1');
const sourceBefore = { sourceSnapshotSha256: sha(snapshotBytes), sourceBaseCommit: sourceRecord.sourceBaseCommit,
  sourceCommitted: sourceRecord.sourceCommitted, mode: 'historical immutable SEA; active source is not executed' };
assert.equal(await fileSha(sea), expectedSea);
assert.match(sourceRecord.sourceBaseCommit, /^[a-f0-9]{40}$/);
const moduleBytes = execFileSync('git', ['show', sourceRecord.sourceBaseCommit + ':examples/serve-demo/trusted-host.cjs'], { cwd: source, windowsHide: true });
const moduleSha = sha(moduleBytes);
await mkdir(directory); // Never overwrite a previous run's evidence.
await writeFile(modulePath, moduleBytes, { flag: 'wx' });
// Exact public platform bundle, with only the existing client-business-tool bit enabled.
// No SDK/Serve package is loaded by this provider process.
const capabilities = {
  tools: Object.fromEntries(['read', 'write', 'edit', 'glob', 'grep', 'list', 'shell', 'process', 'webFetch', 'webSearch', 'http', 'todoWrite', 'askUser', 'agent', 'skills', 'mcp', 'customTools'].map(key => [key, key === 'customTools'])),
  platform: { imageGen: false, videoGen: false, webSearch: false, speechToText: false, textToSpeech: false }
};
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const exec = promisify(execFile);
const owned = [], runs = [], requests = [], faults = [];
let failure, upstream, activeScope;
const identities = { local: { applicationScopeId: 'net-a20-local', endUserId: 'native-consumer', authorizationRevision: '1' },
  remote: { applicationScopeId: 'net-a20-remote', endUserId: 'native-consumer', authorizationRevision: '1' } };
const environment = {};
for (const name of ['SystemRoot', 'WINDIR', 'TEMP', 'TMP', 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'DOTNET_ROOT', 'DOTNET_ROOT_X64'])
  if (process.env[name]) environment[name] = process.env[name];
environment.PATH = join(process.env.SystemRoot ?? 'C:/Windows', 'System32');
environment.DOTNET_CLI_TELEMETRY_OPTOUT = '1';
const frame = (name, data) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`;
const until = async (check, description, timeout = 45000) => {
  const end = Date.now() + timeout;
  while (Date.now() < end) { const found = await check(); if (found) return found; await delay(50); }
  throw new Error('Timed out: ' + description);
};
async function port() {
  const server = createTcpServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const value = server.address().port; await new Promise(resolve => server.close(resolve)); return value;
}
function alive(pid) { try { process.kill(pid, 0); return true; } catch (error) { if (error.code === 'ESRCH') return false; throw error; } }
async function listeningPid(port) {
  const { stdout } = await exec(join(environment.SystemRoot, 'System32/netstat.exe'), ['-ano', '-p', 'tcp'], { windowsHide: true, timeout: 10000 });
  const row = stdout.split(/\r?\n/).map(line => line.trim().split(/\s+/)).find(parts => parts[0] === 'TCP' && parts[1] === `127.0.0.1:${port}` && parts[3] === 'LISTENING');
  return row ? Number(row[4]) : null;
}
function child(name, executable, args, env, cwd) {
  const process = spawn(executable, args, { cwd, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let text = '', errors = '', completed = false, exitCode;
  const stdoutDecoder = new TextDecoder(executable === example ? consoleEncoding : 'utf-8');
  const stderrDecoder = new TextDecoder(executable === example ? consoleEncoding : 'utf-8');
  process.stdout.on('data', chunk => { text += stdoutDecoder.decode(chunk, { stream: true }); if (text.length > 8 * 1048576) process.kill(); });
  process.stderr.on('data', chunk => { errors += stderrDecoder.decode(chunk, { stream: true }); if (errors.length > 1048576) process.kill(); });
  const exited = new Promise((accept, reject) => { process.once('error', reject); process.once('exit', code => { completed = true; exitCode = code; accept(code); }); });
  void exited.catch(() => {});
  const result = { name, process, exited, text: () => text, errors: () => errors, completed: () => completed, exitCode: () => exitCode,
    send: value => process.stdin.write(value + '\n'),
    async wait(marker) { return until(() => { if (completed) throw new Error(name + ' exited before ' + marker + ': ' + text + errors); return text.includes(marker); }, name + ': ' + marker); },
    async save() { await writeFile(join(directory, name + '.stdout.log'), text); await writeFile(join(directory, name + '.stderr.log'), errors); } };
  owned.push(result); return result;
}
async function shutdown(candidate) {
  if (!candidate.completed()) candidate.process.kill();
  await Promise.race([candidate.exited.catch(() => {}), delay(10000)]); await candidate.save();
}
async function consoleRun(mode, phase, config, resume) {
  const env = { ...environment, ...config.clientEnvironment, TANSR_EXAMPLE_STATE_FILE: join(config.root, `presentation-${phase}.json`),
    ...(resume ? { TANSR_RESUME_SESSION: resume } : {}) };
  const app = child(`${mode}-${phase}`, example, [], env, config.workspace);
  await app.wait('session=');
  const sessionId = /session=([^\r\n]+)/.exec(app.text())[1];
  if (resume) assert.equal(sessionId, resume, 'Resume must retain the original logical session.');
  const serverPid = await listeningPid(config.port); assert.ok(serverPid && serverPid !== app.process.pid);
  if (config.serverPid) assert.equal(serverPid, config.serverPid);
  app.send('/workspace 0'); await app.wait(mode === 'local' ? '受控本地 Serve' : '远端 Serve');
  if (!resume) {
    app.send(`A20:${mode}:FIRST`); await app.wait('A20_FIRST_DONE'); await app.wait('turn.completed');
    const previous = app.text().split('turn.completed').length;
    app.send(`A20:${mode}:SECOND`); await app.wait('A20_SECOND_DONE');
    await until(() => app.text().split('turn.completed').length > previous, 'Second original turn settles');
  } else {
    app.send(`A20:${mode}:RESUME`); await app.wait('A20_RESUME_DONE'); await app.wait('turn.completed');
  }
  app.send('/history'); await app.wait('A20:' + mode + ':FIRST');
  app.send('/quit'); assert.equal(await app.exited, 0); await app.save();
  assert.match(app.text(), /remote_resources=Completed/);
  assert.doesNotMatch(app.text(), /(?:close|cleanup|storage|event_source|local_state_save_failed)_unconfirmed|error=|turn\.error|turn\.aborted/);
  if (mode === 'local') { await until(() => !alive(serverPid), 'Owned Serve PID must exit'); assert.equal(await listeningPid(config.port), null); }
  else { assert.equal(alive(serverPid), true, 'A remote client must not terminate the Serve process.'); assert.equal(await listeningPid(config.port), serverPid); }
  const state = JSON.parse(await readFile(env.TANSR_EXAMPLE_STATE_FILE, 'utf8'));
  assert.equal(state.sessionId, sessionId); assert.ok(state.history.includes('A20_FIRST_DONE'));
  runs.push({ mode, phase, sessionId, clientPid: app.process.pid, servePid: serverPid, port: config.port, scope: identities[mode],
    retainedHistory: true, businessTool: 'application_info', remoteResources: 'Completed', localServeExited: mode === 'local',
    inheritedPath: env.PATH, source: 'unchanged ConsoleAssistant / ExampleConnection / original trusted-host.cjs' });
  return sessionId;
}
try {
  upstream = createServer((req, res) => { void (async () => {
    const chunks = []; let bytes = 0;
    for await (const chunk of req) { bytes += chunk.length; assert.ok(bytes < 4 * 1048576); chunks.push(chunk); }
    const body = bytes ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : {};
    const path = new URL(req.url, 'http://synthetic').pathname;
    const json = value => res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(value));
    if (path === '/v1/app-tokens') { assert.equal(body.endUserId, 'native-consumer'); return json({ token: 'synthetic.' + body.endUserId,
      expiresAt: new Date(Date.now() + 3600000).toISOString(), appId: activeScope.applicationScopeId, endUserId: body.endUserId }); }
    if (path === '/t1/config') return json({ app: { platform: 'desktop' }, capabilities,
      models: [{ handle: 'main', modelId: 'synthetic-main', displayName: 'Synthetic only', protocol: 'twp', capabilities: {}, contextWindow: 128000 }],
      aliases: { main: 'main' }, configVersion: 'a20-1', platformModels: { imageGen: [], videoGen: [], speechToText: [], textToSpeech: [] } });
    if (path === '/t1/heartbeat') return json({ features: [], configVersion: 'a20-1', configStale: false });
    if (path === '/v1/my-usage') return json({ window: '1d', endUserId: 'native-consumer', requests: requests.length, inTokens: 0, outTokens: 0, cacheRTokens: 0, cacheWTokens: 0 });
    assert.equal(path, '/t1/exchange'); assert.ok(requests.length < 32, 'Synthetic exchanges are bounded.');
    requests.push(body);
    const serialized = JSON.stringify(body), prompts = body.thread.filter(item => item.role === 'user').flatMap(item => item.blocks ?? []).filter(block => block.t === 'text').map(block => block.v);
    const last = prompts.findLast(text => /^A20:(local|remote):(FIRST|SECOND|RESUME)$/.test(text)), results = body.thread.flatMap(item => item.blocks ?? []).filter(block => block.t === 'tool_result');
    let delta, stop = 'end_turn';
    if (body.purpose === 'memory' || body.meta?.purpose === 'memory') delta = { i: 0, t: 'text', v: 'No new durable memory.' };
    else if (serialized.includes('PERMISSION ADJUDICATION')) delta = { i: 0, t: 'text', v: '{"verdict":"endorse","reason":"synthetic read-only application metadata"}' };
    else if (/A20:(local|remote):FIRST/.test(last)) {
      const mode = /A20:(local|remote):FIRST/.exec(last)[1], callId = 'application-info-' + mode;
      const result = results.find(block => block.toolUseId === callId);
      if (!result) { assert.ok(body.tools.some(tool => tool.name === 'application_info'));
        delta = { i: 0, t: 'tool_use', id: callId, name: 'application_info', vJson: '{}' }; stop = 'tool_use'; }
      else { assert.notEqual(result.isError, true); assert.match(JSON.stringify(result), /Tansr\.Console/); delta = { i: 0, t: 'text', v: 'A20_FIRST_DONE' }; }
    } else if (/A20:(local|remote):(SECOND|RESUME)/.test(last)) {
      assert.match(serialized, /A20_FIRST_DONE/); assert.match(serialized, /Tansr\.Console/);
      if (last.endsWith('RESUME')) assert.match(serialized, /A20_SECOND_DONE/);
      delta = { i: 0, t: 'text', v: last.endsWith('RESUME') ? 'A20_RESUME_DONE' : 'A20_SECOND_DONE' };
    } else throw new Error('Unexpected synthetic prompt');
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(frame('t.open', { exchangeId: 'a20-' + requests.length, model: body.model, protocol: 'twp/1' }) + frame('t.delta', delta) + frame('t.close', { stop }));
  })().catch(error => { faults.push(String(error.stack ?? error)); res.writeHead(500).end('{"error":{"code":"fixture_failed"}}'); }); });
  await new Promise(resolve => upstream.listen(0, '127.0.0.1', resolve));
  for (const mode of ['local', 'remote']) {
    const root = join(directory, mode), workspace = join(root, 'workspace'); await mkdir(workspace, { recursive: true });
    const userToken = randomBytes(32).toString('hex'), bearer = randomBytes(32).toString('hex'), listenPort = await port();
    const scope = identities[mode], policy = join(root, 'users.json'), scopeFile = join(root, 'scope.json'); activeScope = scope;
    await writeFile(scopeFile, JSON.stringify({ principal: 'local-binding/' + mode, scope }));
    await writeFile(policy, JSON.stringify([{ endUserId: scope.endUserId, tokenSha256: sha(userToken), executorId: 'native-' + mode,
      authorizationRevision: '1', tools: [], memory: { sourceId: 'native-memory-' + mode, sourceGeneration: '1', mode: 'create' } }]));
    const hostEnvironment = { TANSR_APP_KEY_ID: scope.applicationScopeId, TANSR_APP_KEY: 'synthetic-platform-only', TANSR_API_BASE: `http://127.0.0.1:${upstream.address().port}`,
      DEMO_STORE_DIR: join(root, 'store'), DEMO_HOST_USERS_PATH: policy, TANSR_DEMO_SYSTEM: 'Read-only application metadata and deterministic synthetic acceptance.' };
    const clientEnvironment = { TANSR_MODEL: 'main', TANSR_TRUSTED_SCOPE_FILE: scopeFile, TANSR_TERMINAL_PREVIEW: '1', TANSR_SERVE_USER_TOKEN: userToken, TANSR_SESSION_CONTRACT: 'sdk1' };
    const config = { root, workspace, port: listenPort, clientEnvironment };
    if (mode === 'local') Object.assign(clientEnvironment, hostEnvironment, { TANSR_LOCAL_SERVE_EXE: sea, TANSR_LOCAL_SERVE_SHA256: expectedSea,
      TANSR_LOCAL_WORKSPACE: workspace, TANSR_LOCAL_SERVE_PORT: String(listenPort), TANSR_LOCAL_SERVE_HOST_MODULE: modulePath,
      TANSR_LOCAL_SERVE_HOST_MODULE_SHA256: moduleSha, TANSR_LOCAL_ENV_NAMES: Object.keys(hostEnvironment).join(',') });
    else {
      const server = child('remote-serve', sea, ['serve', '--v2', '--host', '127.0.0.1', '--port', String(listenPort), '--cwd', workspace,
        '--host-module', modulePath, '--host-module-sha256', moduleSha], { ...environment, ...hostEnvironment, TANSR_SERVE_TOKEN: bearer }, workspace);
      config.serverPid = server.process.pid; config.server = server;
      Object.assign(clientEnvironment, { TANSR_SERVE_URL: `http://127.0.0.1:${listenPort}`, TANSR_SESSION_TOKEN: bearer, TANSR_ALLOW_HTTP_LOOPBACK: '1' });
      await until(async () => { assert.equal(server.completed(), false, server.errors()); try { return (await fetch(clientEnvironment.TANSR_SERVE_URL + '/v2/sessions?limit=1&offset=0',
        { headers: { authorization: 'Bearer ' + bearer, 'x-tansr-demo-user-token': userToken }, signal: AbortSignal.timeout(1000) })).ok; } catch { return false; } }, 'Remote final SEA ready');
    }
    const sessionId = await consoleRun(mode, 'initial', config);
    await consoleRun(mode, 'resumed', config, sessionId);
    if (config.server) { await shutdown(config.server); await until(() => !alive(config.serverPid), 'Owned remote fixture cleanup'); assert.equal(await listeningPid(listenPort), null); }
  }
  assert.deepEqual(faults, []); assert.equal(runs.length, 4);
} catch (error) { failure = error; }
finally {
  for (const candidate of owned.toReversed()) await shutdown(candidate);
  if (upstream) { upstream.closeAllConnections(); await new Promise(resolve => upstream.close(resolve)); }
  let sourceAfter;
  try {
    assert.equal(await fileSha(snapshot), sourceBefore.sourceSnapshotSha256);
    assert.equal(await fileSha(modulePath), moduleSha); assert.equal(await fileSha(sea), expectedSea);
    assert.equal(await fileSha(example), consoleProduct.sha256);
    assert.equal(await optionalFileSha(example.replace(/\.exe$/i, '.dll')), consoleProduct.dllSha256);
    assert.equal(await optionalFileSha(join(dirname(example), 'Tansr.Sdk.dll')), consoleProduct.coreSha256);
    assert.equal(await optionalFileSha(join(dirname(example), 'Tansr.Sdk.Windows.dll')), consoleProduct.windowsSha256);
    sourceAfter = sourceBefore;
  } catch (error) { failure ??= error; }
  await writeFile(join(directory, 'exchanges.json'), JSON.stringify(requests, null, 2));
  await writeFile(join(directory, 'manifest.json'), JSON.stringify({ finalSea: { path: sea, sha256: await fileSha(sea) },
    trustedModule: { path: modulePath, sha256: moduleSha }, console: consoleProduct,
    sourceBefore, sourceAfter, consoleEncoding, runs, syntheticExchanges: requests.length, faults, success: !failure,
    boundary: 'Node is the acceptance orchestrator/upstream only; the unchanged product children receive a PATH without Node. No model service is contacted.',
    failure: failure ? String(failure.stack ?? failure) : null }, null, 2));
}
if (failure) throw failure;
console.log(JSON.stringify({ success: true, runs: runs.length, syntheticExchanges: requests.length, directory }));
