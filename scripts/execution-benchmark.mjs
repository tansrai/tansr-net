// NET-A09 owns an independent instance of the original execution fixture and spool.
// Build the .NET candidate once before this entry. This runner does not run a whole test pool.
import assert from 'node:assert/strict';
import { fork, spawn } from 'node:child_process';
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { verifyServeSourceSnapshot } from './serve-source-snapshot.mjs';

const script = fileURLToPath(import.meta.url), repository = resolve(dirname(script), '..');
const required = name => { assert.ok(process.env[name], `${name} is required.`); return process.env[name]; };
const source = realpathSync(required('TANSR_SERVE_SOURCE'));
const children = new Set();
const hash = path => createHash('sha256').update(readFileSync(path)).digest('hex');
function isolated(extra = {}) {
  const env = { ...process.env };
  for (const name of Object.keys(env)) if (/(?:TOKEN|SECRET|PASSWORD|API.?KEY|APP.?KEY|CREDENTIAL|ELECTRON_RUN_AS_NODE)/i.test(name)) delete env[name];
  return { ...env, ...extra };
}
function own(child) { children.add(child); child.once('exit', () => children.delete(child)); return child; }
async function terminate(child) {
  if (!child.pid || child.exitCode !== null || child.signalCode !== null) return;
  if (process.platform === 'win32') {
    const killer = spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { shell: false, windowsHide: true, stdio: 'ignore' });
    await new Promise(resolve => { killer.once('error', resolve); killer.once('exit', resolve); });
  } else child.kill('SIGTERM');
}
function message(child, type, timeoutMs) {
  return new Promise((resolve, reject) => {
    const clear = () => { clearTimeout(timer); child.removeListener('message', receive); child.removeListener('exit', exited); };
    const receive = value => { if (value?.type === 'failed') { clear(); reject(new Error(value.error)); } else if (value?.type === type) { clear(); resolve(value); } };
    const exited = code => { clear(); reject(new Error(`Fixture exited ${code} before ${type}.`)); };
    const timer = setTimeout(() => { clear(); reject(new Error(`Fixture ${type} timed out.`)); }, timeoutMs);
    child.on('message', receive); child.once('exit', exited);
  });
}
async function command(executable, args, env, timeoutMs) {
  const child = own(spawn(executable, args, { cwd: repository, env, stdio: 'inherit', shell: false, windowsHide: true }));
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { void terminate(child).then(() => reject(new Error('Benchmark child deadline exceeded.'))); }, timeoutMs);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', code => { clearTimeout(timer); resolve(code ?? 1); });
  });
}
async function fixture() {
  const token = required('TANSR_SERVE_TEST_TOKEN');
  // Refuse real upstream access even if a product regression ignores the injected fetch seam.
  const nativeFetch = globalThis.fetch;
  globalThis.fetch = (input, init) => {
    const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
    assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname)); return nativeFetch(input, init);
  };
  const { startExecutionPipelineFixture } = await import('../tests/Tansr.Sdk.IntegrationTests/ServeExecutionPipelineFixture.mjs');
  const origin = await startExecutionPipelineFixture({ source, directory: join(required('TANSR_SERVE_TEST_DIRECTORY'), 'execution-pipeline'), foregroundOnly: true,
    authenticate(request) {
      const actual = request.headers.authorization, expected = `Bearer ${token}`;
      if (typeof actual === 'string' && Buffer.byteLength(actual) === Buffer.byteLength(expected) && timingSafeEqual(Buffer.from(actual), Buffer.from(expected)))
        return { endUserId: 'net-integration-user' };
      return null;
    } });
  process.send({ type: 'ready', url: origin.url });
  process.once('message', () => { void origin.close().then(evidence => {
    process.send({ type: 'closed', evidence }, () => process.disconnect());
  }, error => { process.send({ type: 'failed', error: String(error.stack ?? error) }, () => process.disconnect()); process.exitCode = 1; }); });
}
async function run() {
  assert.equal(process.platform, 'win32', 'This comparison uses same-machine Windows QPC.');
  const snapshot = required('TANSR_SERVE_SOURCE_SNAPSHOT'), provenance = verifyServeSourceSnapshot(source, snapshot);
  const root = resolve(required('TANSR_SERVE_TEST_DIRECTORY')); mkdirSync(root, { recursive: true });
  const directory = mkdtempSync(join(root, 'execution-benchmark-'));
  const candidate = join(directory, 'electron-candidate');
  const runtime = realpathSync(required('TANSR_ELECTRON_RUNTIME'));
  const env = isolated({ TANSR_SERVE_SOURCE: source, TANSR_SERVE_TEST_TOKEN: randomBytes(32).toString('base64url'), TANSR_SERVE_TEST_DIRECTORY: directory,
    TANSR_ELECTRON_RUNTIME: runtime, TANSR_ELECTRON_BENCHMARK_ENTRY: join(candidate, 'dist/benchmark.mjs') });
  const receipt = { task: 'NET-A09', source, directory, runtime, runtimeSha256: hash(runtime), ...provenance,
    benchmarkSha256: hash(resolve(repository, 'tests/Tansr.Sdk.Windows.Tests/Execution/WindowsExecutionBenchmarkTests.cs')),
    fixtureSha256: hash(resolve(repository, 'tests/Tansr.Sdk.IntegrationTests/ServeExecutionPipelineFixture.mjs')), paidSampling: false };
  writeFileSync(join(directory, 'owner.json'), JSON.stringify(receipt, null, 2) + '\n');
  assert.equal(await command(process.execPath, [join(repository, 'scripts/build-electron-execution-benchmark.mjs'), source, candidate], env, 90000), 0, 'Original Electron SDK bundle failed.');
  const child = own(fork(script, ['--fixture'], { cwd: source, env, windowsHide: true,
    execArgv: ['--import', pathToFileURL(join(source, 'node_modules/tsx/dist/loader.mjs')).href], stdio: ['ignore', 'inherit', 'inherit', 'ipc'] }));
  let exitCode = 1;
  try {
    const ready = await message(child, 'ready', 45000); assert.equal(new URL(ready.url).hostname, '127.0.0.1');
    exitCode = await command(process.env.TANSR_DOTNET ?? 'dotnet', ['test', join(repository, 'tests/Tansr.Sdk.Windows.Tests/Tansr.Sdk.Windows.Tests.csproj'),
      '--no-build', '--no-restore', '--configuration', process.env.TANSR_INTEGRATION_CONFIGURATION ?? 'Release',
      '--filter', 'FullyQualifiedName~WindowsExecutionBenchmarkTests', '--logger', 'console;verbosity=normal'], { ...env, TANSR_SERVE_EXECUTION_URL: ready.url }, 260000);
  } finally {
    try {
      if (child.connected) {
        const closed = message(child, 'closed', 20000); child.send({ type: 'stop' }); const result = await closed;
        writeFileSync(join(directory, 'serve-evidence.json'), JSON.stringify(result.evidence, null, 2) + '\n');
        if (exitCode === 0) { assert.equal(result.evidence.operations.length, 2); assert.ok(result.evidence.acceptedBlocks >= 200); }
      }
      verifyServeSourceSnapshot(source, snapshot);
      const manifest = JSON.parse(readFileSync(join(candidate, 'source-manifest.json'), 'utf8'));
      for (const file of manifest.files) assert.equal(hash(file.path), file.sha256, 'Electron build input changed during benchmark: ' + file.path);
    } finally { await terminate(child); }
    console.log(JSON.stringify({ ...receipt, exitCode }));
  }
  process.exitCode = exitCode;
}
if (process.argv[2] === '--fixture') {
  fixture().catch(error => { process.send?.({ type: 'failed', error: String(error.stack ?? error) }); process.exitCode = 1; process.disconnect?.(); });
} else {
  for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, () => { void Promise.all([...children].map(terminate)).then(() => { process.exitCode = 1; }); });
  run().catch(async error => { console.error(error); await Promise.all([...children].map(terminate)); process.exitCode = 1; });
}
