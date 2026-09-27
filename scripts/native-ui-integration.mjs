// Runs only this repository's two selected example executables against an isolated real Serve.
import assert from 'node:assert/strict';
import { randomBytes, createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { openSync, closeSync } from 'node:fs';
import { resolve, join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { verifyServeSourceSnapshot } from './serve-source-snapshot.mjs';

assert.equal(process.platform, 'win32', 'Native UI gate requires Windows.');
const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const required = name => { assert.ok(process.env[name], `${name} is required.`); return process.env[name]; };
const directory = resolve(required('TANSR_NATIVE_UI_DIRECTORY'));
const source = resolve(required('TANSR_SERVE_SOURCE'));
const snapshot = resolve(required('TANSR_SERVE_SOURCE_SNAPSHOT'));
const sourceBefore = verifyServeSourceSnapshot(source, snapshot);
const select = (name, defaults, allowed = defaults) => {
  const values = process.env[name]?.split(',') ?? defaults;
  assert.ok(values.length && new Set(values).size === values.length && values.every(value => allowed.includes(value)), `Invalid ${name}.`);
  return values;
};
const hosts = select('TANSR_NATIVE_UI_HOSTS', ['WPF', 'WINFORMS']);
const groups = select('TANSR_NATIVE_UI_GROUPS', ['primary', 'storage', 'legacy'], ['primary', 'media', 'media-tail', 'speech', 'continuity', 'memory', 'storage', 'legacy']);
await mkdir(directory); // preserve all prior red and green evidence
const token = randomBytes(32).toString('hex');
const originDirectory = join(directory, 'serve'); await mkdir(originDirectory);
const wpf = resolve(process.env.TANSR_NATIVE_UI_WPF ?? join(repository, 'examples/WpfAssistant/bin/Release/net10.0-windows/WpfAssistant.exe'));
const winforms = resolve(process.env.TANSR_NATIVE_UI_WINFORMS ?? join(repository, 'examples/WinFormsAssistant/bin/Release/net48/WinFormsAssistant.exe'));
const sha = async path => createHash('sha256').update(await readFile(path)).digest('hex');
async function candidate(path) {
  const files = [{ path, sha256: await sha(path) }];
  for (const dependency of [path.replace(/\.exe$/i, '.dll'), join(dirname(path), 'Tansr.Sdk.dll'), join(dirname(path), 'Tansr.Sdk.Windows.dll')]) {
    try { files.push({ path: dependency, sha256: await sha(dependency) }); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
  return { path, files };
}
const executables = { wpf: await candidate(wpf), winforms: await candidate(winforms) };
const scripts = await Promise.all(['scripts/test-native-ui.ps1', 'scripts/native-media-ui.ps1', 'scripts/native-storage-ui.ps1',
  'tests/Tansr.Sdk.IntegrationTests/ServeNativeUiFixture.mjs', 'tests/Tansr.Sdk.IntegrationTests/ServeNativeMediaFixture.mjs',
  'tests/Tansr.Sdk.IntegrationTests/ServeNativeMemoryFixture.mjs', 'tests/Tansr.Sdk.IntegrationTests/ServeNativeStorageFixture.mjs',
  'tests/Tansr.Sdk.IntegrationTests/ServeNativeLegacyFixture.mjs'].map(async name => ({ path: join(repository, name), sha256: await sha(join(repository, name)) })));
const env = { ...process.env };
for (const name of Object.keys(env)) if (/(TOKEN|SECRET|PASSWORD|API.?KEY|APP.?KEY|CREDENTIAL)/i.test(name) || /^TANSR_/.test(name)) delete env[name];
Object.assign(env, { TANSR_SERVE_SOURCE: source, TANSR_SERVE_SOURCE_SNAPSHOT: snapshot, TANSR_NATIVE_UI_DIRECTORY: originDirectory, TANSR_NATIVE_UI_TOKEN: token });
env.TANSR_NATIVE_UI_MEDIA_DIRECTORY = join(repository, 'tests/Tansr.Sdk.Windows.Tests/Hosting/Fixtures/Media');
env.TANSR_NATIVE_UI_MEDIA_SCRIPT = join(repository, 'scripts/native-media-ui.ps1');
env.TANSR_NATIVE_UI_STORAGE_SCRIPT = join(repository, 'scripts/native-storage-ui.ps1');
const fixtureLog = openSync(join(directory, 'serve.log'), 'wx');
const fixture = spawn(process.execPath, ['--import', pathToFile(source, 'node_modules/tsx/dist/loader.mjs'), join(repository, 'tests/Tansr.Sdk.IntegrationTests/ServeNativeUiFixture.mjs')],
  { cwd: repository, env, windowsHide: true, stdio: ['ignore', fixtureLog, fixtureLog] });
const fixtureExit = new Promise((accept, reject) => { fixture.once('error', reject); fixture.once('exit', code => accept(code)); });
function pathToFile(root, path) { return pathToFileURL(join(root, path)).href; }
const delay = ms => new Promise(resolve => { const timer = setTimeout(resolve, ms); timer.unref(); });
let uiCode, failure, readyWaitMilliseconds;
try {
  let ready;
  const waitingSince = Date.now();
  for (let i = 0; i < 900; i++) {
    assert.equal(fixture.exitCode, null, 'Real Serve fixture exited before ready.');
    try { ready = JSON.parse(await readFile(join(originDirectory, 'ready.json'), 'utf8')); break; }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    await delay(100);
  }
  readyWaitMilliseconds = Date.now() - waitingSince;
  assert.ok(ready, 'Real Serve fixture startup timed out.');
  const quote = value => `'${value.replaceAll("'", "''")}'`;
  const command = `$taskUiScript = [ScriptBlock]::Create([IO.File]::ReadAllText(${quote(join(repository, 'scripts/test-native-ui.ps1'))}, [Text.Encoding]::UTF8)); & $taskUiScript -WpfExecutable ${quote(wpf)} -WinFormsExecutable ${quote(winforms)} -OutputDirectory ${quote(join(directory, 'ui'))} -ServeUrl ${quote(ready.url)} -Hosts @(${hosts.map(quote).join(',')}) -Groups @(${groups.map(quote).join(',')}); if (-not $?) { exit 1 }`;
  const uiLog = openSync(join(directory, 'ui.log'), 'wx');
  try {
    const ui = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(command, 'utf16le').toString('base64')],
      { cwd: repository, env, windowsHide: true, stdio: ['ignore', uiLog, uiLog] });
    uiCode = await new Promise((accept, reject) => { ui.once('error', reject); ui.once('exit', code => accept(code)); });
  } finally { closeSync(uiLog); }
  assert.equal(uiCode, 0, 'Native UI assertions failed; inspect ui.log and ui/partial.json.');
} catch (error) { failure = error; }
finally {
  await writeFile(join(originDirectory, 'stop'), 'stop', { flag: 'wx' });
  const fixtureCode = await Promise.race([fixtureExit, delay(15000).then(() => 'timeout')]);
  if (fixtureCode === 'timeout') fixture.kill();
  closeSync(fixtureLog);
  if (fixtureCode !== 0 && !failure) failure = new Error(`Serve cleanup failed: ${fixtureCode}`);
  let sourceAfter;
  try { sourceAfter = verifyServeSourceSnapshot(source, snapshot); } catch (error) { failure ??= error; }
  await writeFile(join(directory, 'manifest.json'), JSON.stringify({ hosts, groups, executables, scripts, sourceBefore, sourceAfter, fixturePid: fixture.pid, readyWaitMilliseconds, uiCode, fixtureCode, failure: failure?.message ?? null }, null, 2));
}
if (failure) throw failure;
console.log(JSON.stringify({ directory, uiCode, result: 'passed', paidModelCalls: 0 }));
