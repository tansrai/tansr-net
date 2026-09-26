// Reproducible acceptance fixture, never a product Serve launcher.
// Runs the real source HTTP/SSE router: legacy transport uses FakeAgentFactory; the
// public assembly uses the real kernel with a controlled synthetic upstream, never paid sampling.
import assert from 'node:assert/strict';
import { fork, spawn, execFileSync } from 'node:child_process';
import { randomBytes, timingSafeEqual, createHash } from 'node:crypto';
import { existsSync, readFileSync, realpathSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { verifyServeSourceSnapshot } from './serve-source-snapshot.mjs';

const script = fileURLToPath(import.meta.url);
const repository = resolve(dirname(script), '..');
const required = name => {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required; this integration gate never skips missing prerequisites.`);
  return value;
};
const source = realpathSync(required('TANSR_SERVE_SOURCE'));
const sourceFile = path => resolve(source, path);
const sha = path => createHash('sha256').update(readFileSync(path)).digest('hex');

function sourceEvidence() {
  for (const path of ['node_modules/tsx/dist/loader.mjs', 'packages/server/src/index.ts', 'packages/server/test/v2-helpers.ts']) {
    assert.ok(existsSync(sourceFile(path)), `Required Serve source prerequisite missing: ${path}`);
  }
  // An absolute source entry alone is insufficient if workspace dependencies resolve old dist.
  const packages = new Map(), pending = ['server', 'sdk', 'kernel', 'providers', 'protocol', 'i18n'];
  while (pending.length) {
    const name = pending.pop();
    if (packages.has(name)) continue;
    const pkg = JSON.parse(readFileSync(sourceFile(`packages/${name}/package.json`), 'utf8'));
    const entry = pkg.exports['.'];
    const target = typeof entry === 'string' ? entry : entry.default;
    assert.ok(target?.startsWith('./src/'), `${name} must resolve its source export, never dist.`);
    packages.set(name, pkg);
    for (const [dependency, version] of Object.entries({ ...pkg.dependencies, ...pkg.devDependencies })) {
      if (dependency.startsWith('@tansr/') && version.startsWith('workspace:')) pending.push(dependency.slice(7));
    }
  }
  // Verify actual node_modules resolution from each package, not just its declared source export.
  const resolutions = [];
  for (const [name, pkg] of packages) {
    const resolveFrom = createRequire(sourceFile(`packages/${name}/src/index.ts`));
    for (const [dependency, version] of Object.entries({ ...pkg.dependencies, ...pkg.devDependencies })) {
      if (!dependency.startsWith('@tansr/') || !version.startsWith('workspace:')) continue;
      const targetName = dependency.slice(7), targetPackage = packages.get(targetName);
      for (const [subpath, entry] of Object.entries(targetPackage.exports)) {
        const target = typeof entry === 'string' ? entry : entry.default;
        assert.ok(target?.startsWith('./src/'), `${dependency}${subpath.slice(1)} must resolve source.`);
        const actual = realpathSync(resolveFrom.resolve(dependency + subpath.slice(1)));
        const expected = realpathSync(sourceFile(`packages/${targetName}/${target}`));
        assert.equal(actual, expected, `${name} resolves ${dependency}${subpath.slice(1)} outside this source snapshot.`);
        resolutions.push(`${name}->${dependency}${subpath.slice(1)}:${target}`);
      }
    }
  }
  const revision = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: source, encoding: 'utf8', windowsHide: true }).trim();
  const paths = ['packages/server/test/v2-helpers.ts', 'packages/server/test/helpers.ts', 'packages/server/test/fake-platform-fetch.ts', 'doc/rfc/sdk2-ext-v1.schema.json',
    ...Array.from(packages.keys()).flatMap(name => [`packages/${name}/src`, `packages/${name}/package.json`])];
  const dirty = execFileSync('git', ['status', '--porcelain', '--', ...paths], { cwd: source, encoding: 'utf8', windowsHide: true }).trim();
  const candidate = process.env.TANSR_SERVE_SOURCE_SNAPSHOT
    ? verifyServeSourceSnapshot(source, process.env.TANSR_SERVE_SOURCE_SNAPSHOT) : null;
  if (!candidate) assert.equal(dirty, '', 'Use a clean source snapshot for reproducible Serve acceptance. Commit/choose that source separately.');
  return { revision, ...(candidate ?? { sourceCommitted: true }), sourceEntrySha256: sha(sourceFile('packages/server/src/index.ts')),
    sourceFixtureSha256: sha(sourceFile('packages/server/test/v2-helpers.ts')), sourceResolutions: resolutions.length,
    publicFixtureSha256: sha(resolve(repository, 'tests/Tansr.Sdk.IntegrationTests/ServePublicFixture.mjs')),
    syntheticPlatformSha256: sha(sourceFile('packages/server/test/fake-platform-fetch.ts')),
    archiveSchemaSha256: sha(sourceFile('doc/rfc/sdk2-ext-v1.schema.json')),
    sourceResolutionSha256: createHash('sha256').update(resolutions.sort().join('\n')).digest('hex') };
}

// Only children created by this runner are targets; no process-name enumeration/kill.
const ownedChildren = new Set();
function own(child) { ownedChildren.add(child); child.once('exit', () => ownedChildren.delete(child)); return child; }
function terminateTree(child) {
  if (!child.pid || child.exitCode !== null || child.signalCode !== null) return;
  if (process.platform === 'win32') {
    try { execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore', timeout: 10000 }); }
    catch { child.kill(); }
  } else {
    try { process.kill(-child.pid, 'SIGKILL'); } catch { child.kill('SIGKILL'); }
  }
}
if (process.argv[2] !== '--fixture') {
  for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, () => {
    for (const child of ownedChildren) terminateTree(child);
    process.exitCode = signal === 'SIGINT' ? 130 : 143;
  });
}

function isolatedEnvironment(extra) {
  const env = { ...process.env };
  // Do not make inherited real credentials available to the controlled source fixture.
  for (const name of Object.keys(env)) if (/(?:TOKEN|SECRET|PASSWORD|API.?KEY|APP.?KEY|CREDENTIAL)/i.test(name)) delete env[name];
  return { ...env, ...extra };
}

async function fixture() {
  assert.equal(typeof process.send, 'function', 'Fixture must be launched by the acceptance runner.');
  const token = required('TANSR_SERVE_TEST_TOKEN'), otherToken = required('TANSR_SERVE_TEST_OTHER_TOKEN');
  const expiresAt = Number(required('TANSR_SERVE_TEST_EXPIRES'));
  const matches = (actual, expected) => typeof actual === 'string' && actual.length === expected.length &&
    timingSafeEqual(Buffer.from(actual), Buffer.from(expected));
  // Extra regression guard for the fixture: source imports cannot call a paid fetch endpoint.
  const originalFetch = globalThis.fetch;
  globalThis.fetch = (input, init) => {
    const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
    assert.ok(['127.0.0.1', '[::1]', 'localhost'].includes(url.hostname), 'Outbound fetch forbidden in controlled Serve acceptance.');
    return originalFetch(input, init);
  };
  const { startServer } = await import(pathToFileURL(sourceFile('packages/server/src/index.ts')).href);
  const { FakeAgentFactory, FakeAgentHandle } = await import(pathToFileURL(sourceFile('packages/server/test/v2-helpers.ts')).href);
  const requests = [];
  class ControlledHandle extends FakeAgentHandle {
    history = [];
    send(prompt) {
      const outcome = super.send(prompt);
      if (outcome === 'ended') return outcome;
      // Deterministic fixture text, not an LLM response or real kernel turn.
      const response = `echo[${this.sends.length}]: ${prompt} · 汉字😀é`;
      this.history.push({ role: 'user', blocks: [{ t: 'text', text: prompt }] },
        { role: 'assistant', blocks: [{ t: 'text', text: response }] });
      this.emitText(response);
      return outcome;
    }
    historySnapshot() { return { lastSeq: this.lastSeq(), messages: structuredClone(this.history) }; }
  }
  const factory = new FakeAgentFactory({ makeHandle: (id, init, options) => new ControlledHandle(id, init.endUserId, options) });
  const server = await startServer({
    host: '127.0.0.1', port: 0, token: randomBytes(32).toString('hex'), readyFrame: 'none', heartbeatMs: 0,
    eventBufferSize: 4, eventBufferMaxBytes: 1048576,
    createSession: { create() { throw new Error('The integration fixture exposes only SDK1 /v2 agent sessions.'); } },
    logger: { info() {}, error(message) { process.stderr.write(`Serve fixture error: ${message}\n`); } },
    v2: {
      createSession: factory, governance: { sweepIntervalMs: 0 },
      authenticate(req) {
        requests.push({ method: req.method, path: new URL(req.url, 'http://127.0.0.1').pathname,
          lastEventId: req.headers['last-event-id'] ?? null });
        if (Date.now() > expiresAt) return null;
        const bearer = req.headers.authorization;
        if (matches(bearer, `Bearer ${token}`)) return { endUserId: 'net-integration-user' };
        if (matches(bearer, `Bearer ${otherToken}`)) return { endUserId: 'net-integration-other' };
        return null;
      }
    }
  });
  const authenticate = req => {
    if (Date.now() > expiresAt) return null;
    if (matches(req.headers.authorization, `Bearer ${token}`)) return { endUserId: 'net-integration-user' };
    if (matches(req.headers.authorization, `Bearer ${otherToken}`)) return { endUserId: 'net-integration-other' };
    return null;
  };
  const { startPublicFixture } = await import(pathToFileURL(resolve(repository, 'tests/Tansr.Sdk.IntegrationTests/ServePublicFixture.mjs')).href);
  let publicFixture;
  try {
    publicFixture = await startPublicFixture({ source, directory: required('TANSR_SERVE_TEST_DIRECTORY'), authenticate,
      candidate: Boolean(process.env.TANSR_SERVE_SOURCE_SNAPSHOT) });
  } catch (error) { await server.close(); throw error; }
  let closing;
  const close = () => closing ??= (async () => { await server.close(); return await publicFixture.close(); })();
  process.on('message', async message => {
    if (message?.type !== 'stop') return;
    try {
      const publicEvidence = await close();
      process.send({ type: 'closed', sessions: factory.handles.length, sends: factory.handles.reduce((n, h) => n + h.sends.length, 0),
        interrupts: factory.handles.reduce((n, h) => n + h.interrupts, 0), requests, publicEvidence });
      process.disconnect();
    } catch (error) { process.stderr.write(error.message + '\n'); process.exitCode = 1; process.disconnect(); }
  });
  process.on('disconnect', () => { void close(); });
  process.send({ type: 'ready', url: server.url, publicUrl: publicFixture.url });
}

function waitMessage(child, type, timeoutMs = 30000) {
  return new Promise((resolveMessage, reject) => {
    const clean = () => { clearTimeout(timer); child.off('message', onMessage); child.off('exit', onExit); child.off('error', onError); };
    const onMessage = message => { if (message?.type === type) { clean(); resolveMessage(message); } };
    const onExit = (code, signal) => { clean(); reject(new Error(`Serve fixture exited before ${type}: ${code ?? signal}`)); };
    const onError = error => { clean(); reject(error); };
    const timer = setTimeout(() => { clean(); reject(new Error(`Timed out waiting for Serve fixture ${type}.`)); }, timeoutMs);
    child.on('message', onMessage); child.once('exit', onExit); child.once('error', onError);
  });
}

async function run() {
  const evidence = sourceEvidence();
  const token = randomBytes(32).toString('base64url'), otherToken = randomBytes(32).toString('base64url');
  const artifactRoot = resolve(repository, 'artifacts/serve-integration'); mkdirSync(artifactRoot, { recursive: true });
  const directory = mkdtempSync(resolve(artifactRoot, 'run-'));
  writeFileSync(resolve(directory, 'owner.json'), JSON.stringify({ task: process.env.TANSR_SERVE_EVIDENCE_TASK ?? 'NET-Serve-integration', createdAt: new Date().toISOString(),
    purpose: 'Synthetic acceptance data and original SQLite receipts, retained for reconciliation; contains no access tokens.', ...evidence }, null, 2));
  const env = isolatedEnvironment({ TANSR_SERVE_SOURCE: source, TANSR_SERVE_SOURCE_SHA: evidence.revision,
    TANSR_SERVE_TEST_TOKEN: token, TANSR_SERVE_TEST_OTHER_TOKEN: otherToken, TANSR_SERVE_TEST_DIRECTORY: directory, TANSR_SERVE_TEST_EXPIRES: String(Date.now() + 300000) });
  const child = own(fork(script, ['--fixture'], { cwd: source, env, windowsHide: true, detached: process.platform !== 'win32',
    execArgv: ['--import', pathToFileURL(sourceFile('node_modules/tsx/dist/loader.mjs')).href],
    stdio: ['ignore', 'pipe', 'pipe', 'ipc'] }));
  child.stdout.pipe(process.stdout); child.stderr.pipe(process.stderr);
  let exitCode = 1;
  try {
    const ready = await waitMessage(child, 'ready');
    const url = new URL(ready.url);
    assert.equal(url.hostname, '127.0.0.1');
    console.log(JSON.stringify({ acceptance: 'real-Serve-source-HTTP-SSE', kernel: 'controlled-FakeAgentFactory',
      paidSampling: false, ...evidence }));
    const args = ['test', resolve(repository, 'tests/Tansr.Sdk.IntegrationTests/Tansr.Sdk.IntegrationTests.csproj'),
      '--no-build', '--no-restore', '--configuration', process.env.TANSR_INTEGRATION_CONFIGURATION ?? 'Release',
      '--filter', 'FullyQualifiedName~Tansr.Sdk.IntegrationTests.ServeSourceIntegrationTests|FullyQualifiedName~Tansr.Sdk.IntegrationTests.ServePublicHostIntegrationTests', '--logger', 'console;verbosity=normal'];
    const test = own(spawn(process.env.TANSR_DOTNET ?? 'dotnet', args, { cwd: repository, shell: false, windowsHide: true,
      detached: process.platform !== 'win32', env: { ...env, TANSR_SERVE_TEST_URL: url.origin, TANSR_SERVE_PUBLIC_URL: ready.publicUrl }, stdio: 'inherit' }));
    exitCode = await new Promise((resolveExit, reject) => {
      const timeout = setTimeout(() => { terminateTree(test); reject(new Error('Serve integration test deadline exceeded.')); }, 120000);
      test.once('error', error => { clearTimeout(timeout); reject(error); });
      test.once('exit', code => { clearTimeout(timeout); resolveExit(code ?? 1); });
    });
  } finally {
    if (child.connected) {
      const closed = waitMessage(child, 'closed', 10000);
      child.send({ type: 'stop' });
      try {
        const result = await closed;
        // Independent host evidence: real routes were used and observer cancellation never interrupted a turn.
        if (exitCode === 0) {
          assert.ok(result.sessions >= 3 && result.sends >= 9, 'No substantive integration tests ran.');
          assert.equal(result.interrupts, 0, 'Observation cancellation unexpectedly interrupted the controlled session.');
          assert.ok(result.requests.some(r => r.path.endsWith('/events') && r.lastEventId === '0'), 'Last-Event-ID did not reach Serve.');
          assert.ok(result.requests.every(r => r.path.startsWith('/v2/')), 'SDK1 default escaped the original session family.');
          assert.equal(result.publicEvidence.mainExchanges, process.env.TANSR_SERVE_SOURCE_SNAPSHOT ? 4 : 3, 'Real kernel/device/material flow did not run.');
          for (const suffix of ['/initialize', '/execution-bindings', '/receipts', '/archive/records', '/archive/acks', '/material-responses'])
            assert.ok(result.publicEvidence.routes.some(path => path.endsWith(suffix)), `Missing public product route: ${suffix}`);
        }
        console.log(JSON.stringify({ acceptance: 'Serve-route-evidence', sessions: result.sessions, sends: result.sends,
          interrupts: result.interrupts, requests: result.requests.length, passed: exitCode === 0 }));
        const routes = result.publicEvidence.routes;
        const routeCounts = Object.fromEntries([...new Set(routes.map(path => path.replace(/\/[0-9a-f]{8}-[0-9a-f-]{27,}/g, '/:id').replace(/\/jr-p-[a-f0-9]+/g, '/:artifact')))]
          .map(path => [path, routes.filter(actual => actual.replace(/\/[0-9a-f]{8}-[0-9a-f-]{27,}/g, '/:id').replace(/\/jr-p-[a-f0-9]+/g, '/:artifact') === path).length]));
        console.log(JSON.stringify({ acceptance: 'public-Serve-kernel-device-archive', model: result.publicEvidence.model,
          exchanges: result.publicEvidence.exchanges, mainExchanges: result.publicEvidence.mainExchanges,
          memoryExchanges: result.publicEvidence.memoryExchanges, realKernel: true, authorizationChecksByRoute: routeCounts, directory, passed: exitCode === 0 }));
        if (process.env.TANSR_SERVE_SOURCE_SNAPSHOT) verifyServeSourceSnapshot(source, process.env.TANSR_SERVE_SOURCE_SNAPSHOT);
        writeFileSync(resolve(directory, 'result.json'), JSON.stringify({ source: evidence, passed: exitCode === 0,
          legacy: { sessions: result.sessions, sends: result.sends, interrupts: result.interrupts, requests: result.requests.length },
          public: { ...result.publicEvidence, authorizationChecksByRoute: routeCounts } }, null, 2));
      } finally { terminateTree(child); }
    } else terminateTree(child);
  }
  process.exitCode = exitCode;
}

try {
  if (process.argv.length === 3 && process.argv[2] === '--fixture') await fixture();
  else { assert.equal(process.argv.length, 2, 'Usage: TANSR_SERVE_SOURCE=<clean tansr-cli> node scripts/serve-integration.mjs'); await run(); }
} catch (error) { console.error(error.message); process.exitCode = 1; }
