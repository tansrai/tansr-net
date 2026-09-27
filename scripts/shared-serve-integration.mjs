// NET-A23 same-candidate acceptance. Reuses the three original native test entry points unchanged.
// Run with the pinned Serve tree's tsx loader; build/pin client artifacts before this concentrated gate.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { basename, dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { verifyServeSourceSnapshot } from './serve-source-snapshot.mjs';
import { startSharedClientsFixture } from '../tests/Tansr.Sdk.IntegrationTests/ServeSharedClientsFixture.mjs';

assert.equal(process.platform, 'win32', 'The C# device lane executes the real Windows backend.');
const configPath = resolve(process.argv[2] ?? '');
const c = JSON.parse(readFileSync(configPath, 'utf8'));
const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const hash = path => createHash('sha256').update(readFileSync(path)).digest('hex');
assert.ok(c.source && c.sourceSnapshot && c.output && c.electron && c.android && c.harmony && c.ios);
assert.equal(c.packages?.length, 3, 'Pin the exact Serve, API-client and complete SDK packages.');
assert.ok(!existsSync(c.output), 'Evidence directory must be new.'); mkdirSync(c.output, { recursive: true });
const source = verifyServeSourceSnapshot(c.source, c.sourceSnapshot);
const implementation = ['scripts/shared-serve-integration.mjs', 'tests/Tansr.Sdk.IntegrationTests/ServeSharedClientsFixture.mjs',
  'tests/Tansr.Sdk.IntegrationTests/ServeSharedClientsTests.cs', 'tests/Tansr.Sdk.IntegrationTests/electron-shared-serve.mjs',
  ...['Tansr.Sdk.IntegrationTests.dll', 'Tansr.Sdk.dll', 'Tansr.Sdk.Windows.dll'].map(file => 'tests/Tansr.Sdk.IntegrationTests/bin/Release/net10.0-windows/' + file)]
  .map(file => ({ path: join(repository, file), sha256: hash(join(repository, file)) }));
const electronRuntime = { path: c.electron, sha256: hash(c.electron) };
const pins = [];
for (const item of [...c.packages, c.android, c.harmony]) {
  assert.match(item.sha256, /^[a-f0-9]{64}$/); assert.equal(hash(item.artifact), item.sha256, 'Artifact changed: ' + item.artifact);
  pins.push({ path: item.artifact, sha256: item.sha256 });
}
for (const item of [c.android, c.harmony, c.ios]) {
  assert.ok(item.sourceRoot && item.sourceRevision, 'Native candidate requires actual source provenance.');
  assert.equal(execFileSync('git', ['rev-parse', 'HEAD'], { cwd: item.sourceRoot, encoding: 'utf8', windowsHide: true }).trim(), item.sourceRevision);
}
assert.ok(c.android.serial && c.harmony.serial && c.ios.simulator && c.ios.xctestrun && c.ios.xctestrunSha256 && c.ios.evidence);
assert.ok(c.ios.evidence.startsWith('/Users/mac/tansr/archive/'), 'Use the authorized Mac archive only.');
assert.match(basename(c.output), /^[a-zA-Z0-9_-]+$/);
const remoteTestName = `net-shared-${basename(c.output)}.xctestrun`;
const env = { ...process.env };
for (const key of Object.keys(env)) if (/(?:TOKEN|SECRET|PASSWORD|API.?KEY|APP.?KEY|CREDENTIAL)/i.test(key)) delete env[key];
const originalFetch = globalThis.fetch;
globalThis.fetch = (input, init) => { const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
  assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname), 'Paid/external fetch forbidden.'); return originalFetch(input, init); };
const children = new Set(), commands = [], ownedForwards = [];
let firstChildFailure, terminating = false;
function launch(name, tool, args, options = {}) {
  const child = spawn(tool, args, { windowsHide: true, cwd: repository, env: { ...env, ...options.env }, stdio: ['pipe', 'pipe', 'pipe'] }); children.add(child);
  let text = ''; const append = bytes => {
    if (text.length + bytes.length > 24 * 1048576) { firstChildFailure ??= new Error(name + ' exceeded bounded output'); stop(child); return; }
    text += bytes; writeFileSync(join(c.output, name + '.log'), text);
  };
  child.stdout.on('data', append); child.stderr.on('data', append);
  const entry = { name, tool, args, started: Date.now() }; commands.push(entry);
  const done = new Promise((resolveDone, reject) => { child.once('error', failure => { children.delete(child); if (!terminating) firstChildFailure ??= failure; reject(failure); }); child.once('exit', (code, signal) => {
    children.delete(child); Object.assign(entry, { code, signal, finished: Date.now() });
    if (code === 0 && (!options.success || options.success.test(text))) resolveDone(text);
    else { const failure = new Error(`${name} failed (${code}/${signal}${code === 0 ? '; required test result absent' : ''}); see its log.`); if (!terminating) firstChildFailure ??= failure; reject(failure); }
  }); }); void done.catch(() => {});
  if (options.input) child.stdin.end(options.input); else child.stdin.end();
  return { child, done, text: () => text };
}
async function command(name, tool, args, options) { return launch(name, tool, args, options).done; }
function stop(child) { if (!child.pid || child.exitCode !== null) return; try { execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore', timeout: 10000 }); } catch { child.kill(); } }
const sshArgs = ['-F', 'none', '-i', c.ios.key, '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
  '-o', `UserKnownHostsFile=${c.ios.knownHosts}`, '-o', 'ConnectTimeout=10'];
const quote = value => "'" + value.replaceAll("'", "'\\''") + "'";
let fixture, error, report, tunnel, mac;
const end = Date.now() + 500000;
async function until(read, predicate, description) {
  while (true) { if (firstChildFailure) throw firstChildFailure; assert.ok(Date.now() < end, description + ' timed out'); const value = await read(); if (predicate(value)) return value; await new Promise(resolve => setTimeout(resolve, 200)); }
}
async function state() { const response = await fetch('http://127.0.0.1:18890/state'); assert.equal(response.status, 200); return response.json(); }
async function post(route, value) { const response = await fetch('http://127.0.0.1:18890' + route, { method: 'POST', headers: { authorization: 'Bearer fixture-control', 'content-type': 'application/json' }, body: JSON.stringify(value) }); assert.equal(response.status, 200); }
try {
  // Source imports and the packages are attested together. No package is described as published.
  fixture = await startSharedClientsFixture({ source: c.source, directory: c.output });
  const native = launch('csharp', 'dotnet', ['test', 'tests/Tansr.Sdk.IntegrationTests/Tansr.Sdk.IntegrationTests.csproj', '-c', 'Release', '--no-build', '--no-restore',
    '--filter', 'FullyQualifiedName~ServeSharedClientsTests', '--logger', 'trx;LogFileName=shared.trx', '--results-directory', join(c.output, 'results')],
  { env: { TANSR_SHARED_DIRECTORY: c.output, TANSR_SHARED_URL: fixture.url } });
  const prepared = await until(state, s => Boolean(s.sessions.windows && s.sessions.csharp), 'Native session binding');
  const mobileId = prepared.sessions.windows;
  const esbuild = createRequire(join(c.source, 'package.json'))('esbuild');
  const electronEntry = join(c.output, 'electron-shared-serve.mjs');
  const bundle = await esbuild.build({ absWorkingDir: c.source, entryPoints: [join(repository, 'tests/Tansr.Sdk.IntegrationTests/electron-shared-serve.mjs')],
    outfile: electronEntry, bundle: true, platform: 'node', format: 'esm', target: 'node22', external: ['electron'], metafile: true,
    alias: { '@tansr-shared/session-client': join(c.source, 'packages/api-client/src/sdk2/session-client.ts') } });
  writeFileSync(join(c.output, 'electron-inputs.json'), JSON.stringify(bundle.metafile, null, 2));
  const electronConfig = join(c.output, 'electron-config.json'); writeFileSync(electronConfig, JSON.stringify({ directory: c.output, url: fixture.url, controlUrl: fixture.controlUrl }));
  const electron = launch('electron', c.electron, [electronEntry, '--shared-config', electronConfig]);
  // Existing emulator identities are explicit. Never select the first ADB/hdc device or touch phones.
  const androidAbi = await command('android-identity', c.android.tool, ['-s', c.android.serial, 'shell', 'getprop', 'ro.product.cpu.abi']);
  assert.match(androidAbi, /x86_64/);
  const harmonyModel = await command('harmony-identity', c.harmony.tool, ['-t', c.harmony.serial, 'shell', 'param', 'get', 'const.product.model']); assert.match(harmonyModel, /emulator/);
  assert.match(await command('android-install', c.android.tool, ['-s', c.android.serial, 'install', '-r', c.android.artifact]), /Success/);
  assert.match(await command('harmony-install', c.harmony.tool, ['-t', c.harmony.serial, 'install', '-r', c.harmony.artifact]), /install bundle successfully/);
  const previousAndroid = await command('android-forwards', c.android.tool, ['-s', c.android.serial, 'reverse', '--list']);
  const previousHarmony = await command('harmony-forwards', c.harmony.tool, ['-t', c.harmony.serial, 'fport', 'ls']);
  for (const port of [18789, 18890]) {
    const pair = `tcp:${port} tcp:${port}`;
    assert.ok(!previousAndroid.includes(`tcp:${port}`) || previousAndroid.includes(pair), 'Refuse conflicting Android port mapping.');
    if (!previousAndroid.includes(pair)) { await command('android-forward-' + port, c.android.tool, ['-s', c.android.serial, 'reverse', `tcp:${port}`, `tcp:${port}`]); ownedForwards.push(['android', port]); }
    assert.ok(!previousHarmony.includes(`tcp:${port}`) || previousHarmony.includes(pair), 'Refuse conflicting Harmony port mapping.');
    if (!previousHarmony.includes(pair)) { await command('harmony-forward-' + port, c.harmony.tool, ['-t', c.harmony.serial, 'rport', `tcp:${port}`, `tcp:${port}`]); ownedForwards.push(['harmony', port]); }
  }
  // Original iOS test still uses these two loopback ports. Only this runner's SSH process is owned.
  tunnel = launch('mac-tunnel', c.ios.ssh, [...sshArgs, '-o', 'ExitOnForwardFailure=yes', '-N', '-R', '127.0.0.1:18789:127.0.0.1:18789', '-R', '127.0.0.1:18890:127.0.0.1:18890', c.ios.host]);
  const preparation = `import pathlib,plistlib,hashlib,json\nsrc=pathlib.Path(${JSON.stringify(c.ios.xctestrun)})\nraw=src.read_bytes()\nassert hashlib.sha256(raw).hexdigest()==${JSON.stringify(c.ios.xctestrunSha256)}\nd=plistlib.loads(raw)\ndef walk(v):\n if isinstance(v,dict):\n  if 'TestBundlePath' in v:v.setdefault('EnvironmentVariables',{})['UNIFIED_CONSUMPTION']='1'\n  for x in v.values():walk(x)\n elif isinstance(v,list):\n  for x in v:walk(x)\nwalk(d)\nout=pathlib.Path(${JSON.stringify(c.ios.evidence)})\nout.mkdir(parents=True,exist_ok=False)\np=src.parent/${JSON.stringify(remoteTestName)}\nassert not p.exists()\np.write_bytes(plistlib.dumps(d))\nprint(json.dumps({'path':str(p),'sourceSha256':hashlib.sha256(raw).hexdigest(),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()}))\n`;
  mac = JSON.parse((await command('ios-prepare', c.ios.ssh, [...sshArgs, c.ios.host, 'python3 -'], { input: preparation })).trim());
  const android = launch('android-test', c.android.tool, ['-s', c.android.serial, 'shell', 'am', 'instrument', '-w', '-r', '-e', 'class',
    'com.tansr.sdk.receiver.android.AgentSessionHttpTest#testSharedServeAttachInputApprovalAndUiReconnect', '-e', 'unifiedUrl', fixture.url,
    '-e', 'unifiedSession', mobileId, '-e', 'unifiedPrompt', 'USDK:windows:Write:mobile-android', 'com.tansr.sdk.receiver.android.test/android.test.InstrumentationTestRunner'], { success: /OK \(1 test\)/ });
  const harmony = launch('harmony-test', c.harmony.tool, ['-t', c.harmony.serial, 'shell', 'aa', 'test', '-b', 'com.tansr.harmony.demo', '-m', 'entry_test', '-s', 'unittest', 'OpenHarmonyTestRunner',
    '-s', 'class', 'UnifiedMobileTransport', '-s', 'unifiedUrl', fixture.url, '-s', 'unifiedSession', mobileId, '-s', 'unifiedPrompt', 'USDK:windows:Write:mobile-harmony', '-s', 'timeout', '360000', '-w', '420'], { success: /Tests run: 1, Failure: 0, Error: 0, Pass: 1, Ignore: 0/ });
  const iosCommand = `xcodebuild test-without-building -xctestrun ${quote(mac.path)} -destination ${quote('platform=iOS Simulator,id=' + c.ios.simulator)} -parallel-testing-enabled NO -only-testing:TansrClientTests/AgentSessionHttpTests/testSharedServeAttachInputApprovalAndUiReconnect -resultBundlePath ${quote(c.ios.evidence + '/shared.xcresult')} CODE_SIGNING_ALLOWED=NO`;
  const ios = launch('ios-test', c.ios.ssh, [...sshArgs, c.ios.host, `printf '%s\\n' $$ > ${quote(c.ios.evidence + '/owned-xcode.pid')}; exec ${iosCommand}`], { success: /Executed 1 test, with 0 failures/ });
  // A real readiness barrier, not just simultaneous process starts. The original tests log their
  // two live subscriptions before waiting for their assigned stage. Match this unique session ID.
  await until(async () => {
    for (const lane of [native, electron, android, harmony, ios]) if (lane.child.exitCode !== null) await lane.done;
    const a = execFileSync(c.android.tool, ['-s', c.android.serial, 'logcat', '-d', '-s', 'UnifiedMobile:I', '*:S'], { encoding: 'utf8', windowsHide: true, timeout: 10000, maxBuffer: 2 * 1048576 });
    const h = execFileSync(c.harmony.tool, ['-t', c.harmony.serial, 'shell', 'hilog', '-x'], { encoding: 'utf8', windowsHide: true, timeout: 10000, maxBuffer: 16 * 1048576 });
    writeFileSync(join(c.output, 'mobile-ready-observations.json'), JSON.stringify({ android: a.split(/\r?\n/).filter(x => x.includes(mobileId)), harmony: h.split(/\r?\n/).filter(x => x.includes(mobileId)) }));
    return { android: a.includes(`READY android session=${mobileId}`), harmony: h.includes(`READY harmony session=${mobileId}`), ios: ios.text().includes(`READY ios session=${mobileId}`), state: await state() };
  }, value => value.android && value.harmony && value.ios && value.state.timeline.some(t => t.platform === 'electron' && t.stage === 'ready'), 'All five live clients');
  for (const platform of ['android', 'harmony', 'ios']) await post('/timeline', { platform, stage: 'ready', sessionId: mobileId });
  await post('/barrier', {});
  for (const platform of ['android', 'harmony', 'ios']) {
    await post('/sessions', { mobileStage: platform }); await post('/timeline', { platform, stage: 'active' });
    await until(state, s => s.results.some(result => result.id === `USDK:windows:Write:mobile-${platform}`), platform + ' original tool result');
    // The native test must finish its consumed-input/readback assertions before changing stage.
    const marker = `PASS ${platform} session=${mobileId}`;
    await until(async () => platform === 'ios' ? ios.text() : execFileSync(platform === 'android' ? c.android.tool : c.harmony.tool,
      platform === 'android' ? ['-s', c.android.serial, 'logcat', '-d', '-s', 'UnifiedMobile:I', '*:S'] : ['-t', c.harmony.serial, 'shell', 'hilog', '-x'],
      { encoding: 'utf8', windowsHide: true, timeout: 10000, maxBuffer: 16 * 1048576 }), text => text.includes(marker), platform + ' native assertions');
  }
  // Keep all original mobile observers subscribed throughout the selected C# fault interval.
  await until(state, s => s.sessions.csharpFaultsComplete === 'done' && s.timeline.some(item => item.platform === 'electron' && item.stage === 'survived-faults'), 'Other subjects survive selected C# faults');
  await post('/sessions', { mobileStage: 'done' });
  const outputs = await Promise.all([native.done, electron.done, android.done, harmony.done, ios.done]);
  assert.match(outputs[2], /OK \(1 test\)/); assert.match(outputs[3], /Tests run: 1, Failure: 0, Error: 0, Pass: 1, Ignore: 0/);
  assert.match(outputs[4], /Executed 1 test, with 0 failures/); assert.ok(!outputs[4].includes('Test skipped'));
  report = await state();
  assert.ok(report.revoked && report.sessions.csharpFaultsComplete === 'done');
  assert.equal(report.results.filter(item => item.id.startsWith('USDK:')).length, 3, 'Three mobile subscriptions do not triple side effects.');
  verifyServeSourceSnapshot(c.source, c.sourceSnapshot);
  for (const item of implementation) assert.equal(hash(item.path), item.sha256, 'Acceptance code/binary drift during the run.');
} catch (caught) { error = caught; }
finally {
  terminating = true;
  for (const child of children) stop(child);
  if (mac) {
    const cleanup = `import pathlib,subprocess,signal,os,json\nf=pathlib.Path(${JSON.stringify(c.ios.evidence + '/owned-xcode.pid')})\npid=int(f.read_text()) if f.exists() else None\nr=subprocess.run(['ps','-p',str(pid),'-o','command='],capture_output=True,text=True) if pid else None\nactive=bool(r and r.returncode==0 and r.stdout.strip())\nif active:\n assert 'xcodebuild' in r.stdout and ${JSON.stringify(mac?.path)} in r.stdout\n os.kill(pid,signal.SIGINT)\nprint(json.dumps({'ownedPid':pid,'activeBeforeCleanup':active}))\n`;
    try { await command('ios-owned-cleanup', c.ios.ssh, [...sshArgs, c.ios.host, 'python3 -'], { input: cleanup }); } catch (caught) { error ??= caught; }
  }
  for (const [platform, port] of ownedForwards) {
    try { await command(`${platform}-forward-cleanup-${port}`, platform === 'android' ? c.android.tool : c.harmony.tool,
      platform === 'android' ? ['-s', c.android.serial, 'reverse', '--remove', `tcp:${port}`] : ['-t', c.harmony.serial, 'fport', 'rm', `tcp:${port}`, `tcp:${port}`],
      platform === 'harmony' ? { success: /Remove forward ruler success/ } : undefined); }
    catch (cleanup) { error ??= cleanup; }
  }
  try { await fixture?.close(); } catch (cleanup) { error ??= cleanup; }
  writeFileSync(join(c.output, 'shared-manifest.json'), JSON.stringify({ passed: !error, source, pins, implementation, electronRuntime, configSha256: hash(configPath), commands, report,
    failure: error?.stack ?? null, boundary: 'Original native shared-session tests; three distinct subjects across mobile, C#, Electron. Faults selected on C#; not a five-by-four Cartesian matrix. Electron original full SDK/IPC regression is separate.' }, null, 2));
}
if (error) throw error;
