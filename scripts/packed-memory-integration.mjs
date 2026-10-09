// Runs only the dedicated encrypted memory consumer against a hash-pinned public package host.
// Each case owns a fresh host and OS-temp directory; no current CLI source or paid model is loaded.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdtemp, mkdir, rm, realpath } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, dirname, join, relative, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
assert.equal(process.platform, 'win32', 'The dedicated consumer requires actual Windows DPAPI.');
assert.equal(process.argv.length, 4, 'Usage: node scripts/packed-memory-integration.mjs shared-host.json NEW_EVIDENCE_DIRECTORY');
const manifestPath = resolve(process.argv[2]), manifestBytes = await readFile(manifestPath);
const host = JSON.parse(manifestBytes), evidence = resolve(process.argv[3]);
await mkdir(evidence); // Do not overwrite evidence from any previous run.
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
assert.equal(sha(await readFile(host.fixture)), host.fixtureSha256);
for (const item of host.packages) assert.equal(sha(await readFile(item.path)), item.sha256);
const demo = join(repository, 'examples/ConsoleAssistant/bin/Release/net10.0-windows/ConsoleAssistant.exe');
const artifacts = await Promise.all([
  'tests/Tansr.Sdk.IntegrationTests/ServeEncryptedMemoryPublicationTests.cs', 'scripts/packed-memory-integration.mjs',
  'tests/Tansr.Sdk.IntegrationTests/bin/Release/net10.0-windows/Tansr.Sdk.IntegrationTests.dll',
  'tests/Tansr.Sdk.IntegrationTests/bin/Release/net10.0-windows/Tansr.Sdk.dll',
  'tests/Tansr.Sdk.IntegrationTests/bin/Release/net10.0-windows/Tansr.Sdk.Windows.dll',
  'examples/ConsoleAssistant/bin/Release/net10.0-windows/ConsoleAssistant.exe',
  'examples/ConsoleAssistant/bin/Release/net10.0-windows/ConsoleAssistant.dll',
  'examples/ConsoleAssistant/bin/Release/net10.0-windows/Tansr.Sdk.dll',
  'examples/ConsoleAssistant/bin/Release/net10.0-windows/Tansr.Sdk.Windows.dll',
].map(async name => ({ path: join(repository, name), sha256: sha(await readFile(join(repository, name))) })));
const cases = [
  'EncryptedDemoKeepsOriginalReceiptThroughResponseLossAndProcessRestart',
  'OriginalChunkCommitLossKeepsUnknownWithoutReexecution',
  'InFlightStopKeepsOriginalUnknownWithoutReexecution',
];
const results = [];
function launch(executable, args, env) {
  const child = spawn(executable, args, { cwd: repository, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let output = '', errors = '', info; const ready = Promise.withResolvers();
  child.stdout.on('data', data => {
    output += data; if (output.length > 8 * 1048576) { child.kill(); ready.reject(new Error('Bounded child output exceeded.')); }
    if (!info) { const line = output.split(/\r?\n/).find(line => line.startsWith('TANSR_GO_FIXTURE '));
      if (line) { try { info = JSON.parse(line.slice('TANSR_GO_FIXTURE '.length)); ready.resolve(info); } catch {} } }
  });
  child.stderr.on('data', data => { errors += data; if (errors.length > 8 * 1048576) child.kill(); });
  const done = new Promise((accept, reject) => { child.once('error', reject); child.once('exit', (code, signal) => {
    if (!info) ready.reject(new Error('Host exited before ready: ' + errors)); accept({ code, signal });
  }); });
  void ready.promise.catch(() => {}); void done.catch(() => {});
  return { child, done, ready: ready.promise, text: () => output, errors: () => errors };
}
const environment = { ...process.env };
for (const name of Object.keys(environment)) if (/(?:TOKEN|SECRET|PASSWORD|API.?KEY|APP.?KEY|CREDENTIAL)/i.test(name)) delete environment[name];
let failure;
try {
  for (const name of cases) {
    const directory = await mkdtemp(join(tmpdir(), 'tansr-net-pst-memory-'));
    const owner = { name, directory, clean: false }; results.push(owner);
    let fixture, test;
    try {
      fixture = launch(process.execPath, [host.fixture, '.', join(directory, 'host'), 'publication'], environment);
      const info = await Promise.race([fixture.ready, new Promise((_, reject) => setTimeout(() => reject(new Error('Host ready timeout')), 30000).unref())]);
      const configuration = join(directory, 'host-config.json'); await writeFile(configuration, JSON.stringify(info), { flag: 'wx' });
      const args = ['test', 'tests/Tansr.Sdk.IntegrationTests/Tansr.Sdk.IntegrationTests.csproj', '-c', 'Release', '--no-build', '--no-restore',
        '--filter', 'FullyQualifiedName~ServeEncryptedMemoryPublicationTests.' + name,
        '--logger', 'trx;LogFileName=' + name + '.trx', '--results-directory', evidence];
      owner.command = ['dotnet', ...args];
      test = launch('dotnet', args, { ...environment, TANSR_PST_HOST_CONFIG: configuration, TANSR_PST_TEST_DIRECTORY: directory, TANSR_PST_DEMO_EXE: demo });
      const timeout = setTimeout(() => test.child.kill(), 135000); timeout.unref();
      owner.test = await test.done; clearTimeout(timeout);
      await writeFile(join(evidence, name + '.log'), test.text() + test.errors());
      assert.equal(owner.test.code, 0, name + ' failed; see preserved log.');
      const trx = await readFile(join(evidence, name + '.trx'), 'utf8');
      assert.match(trx, /total="1" executed="1" passed="1" failed="0"/);
    } finally {
      if (test && test.child.exitCode === null) { test.child.kill(); await test.done; }
      if (fixture) {
        fixture.child.stdin.end('stop\n');
        const timeout = setTimeout(() => fixture.child.kill(), 20000); timeout.unref();
        owner.host = await fixture.done; clearTimeout(timeout);
        // Config/ready contains synthetic credentials. Only preserve non-ready diagnostics.
        await writeFile(join(evidence, name + '-host.log'), fixture.text().split(/\r?\n/).filter(line => !line.startsWith('TANSR_GO_FIXTURE ')).join('\n') + fixture.errors());
      }
      const temp = await realpath(tmpdir()), actual = await realpath(directory), rel = relative(temp, actual);
      assert.ok(rel && !rel.startsWith('..') && !isAbsolute(rel) && actual.includes('tansr-net-pst-memory-'));
      await rm(actual, { recursive: true }); owner.clean = true;
    }
    assert.equal(owner.host.code, 0, 'Host did not close its resources.');
  }
} catch (error) { failure = error; }
finally {
  for (const artifact of artifacts) assert.equal(sha(await readFile(artifact.path)), artifact.sha256, 'Candidate changed during run.');
  await writeFile(join(evidence, 'manifest.json'), JSON.stringify({ passed: !failure, sharedHostManifest: manifestPath,
    sharedHostManifestSha256: sha(manifestBytes), fixtureSha256: host.fixtureSha256, packages: host.packages,
    artifacts, results, failure: failure?.stack ?? null,
    boundary: 'Windows DPAPI + actual encrypted publication/journal and original public Serve HTTP; synthetic upstream/identity and intentional result loss. No cloud, physical power failure, CLR4/UI or 35-platform full matrix claim.' }, null, 2));
}
if (failure) throw failure;
console.log('Dedicated encrypted memory consumer: 3/3; owned hosts and OS-temp roots closed.');
