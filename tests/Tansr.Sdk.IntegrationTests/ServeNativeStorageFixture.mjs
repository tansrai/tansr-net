// Native UI acceptance: original public offload/archive/cache hosts and real HTTP/SQLite.
// Only the platform peer is synthetic. Configuration is issued by this trusted fixture,
// using actual binding/status responses; neither the UI nor this helper fabricates ACKs.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdir, readFile, writeFile, rename, readdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startNativeStorageFixture({ source, directory, scope, authenticate, token, fetchImpl, apiBaseUrl }) {
  directory = resolve(directory);
  assert.equal(scope.applicationScopeId, 'native-ui-app'); assert.equal(scope.endUserId, 'native-ui-user');
  assert.equal(scope.authorizationRevision, '1'); assert.equal(typeof token, 'string');
  const load = file => import(pathToFileURL(join(source, file)).href);
  const { createServeOffloadArchiveHost, createServeCacheHost } = await load('packages/server/src/extensions/index.ts');
  const { createFsBlobStore } = await load('packages/kernel/src/journal/segmented/fs-blob-store.ts');
  const { continuityApi, continuityOptions, hostKey } = await load('packages/server/test/cache-continuity.fixture.ts');
  const { BASE } = await load('packages/sdk/test/cache-gateway-assembly.fixture.ts');
  const schema = JSON.parse(await readFile(join(source, 'doc/rfc/sdk2-ext-v1.schema.json'), 'utf8'));
  const limits = Object.fromEntries(Object.entries(schema.definitions.Limits.properties).map(([key, value]) => [key, value.default]));
  await mkdir(directory, { recursive: true });
  const commandDirectory = join(directory, 'host-commands'), responseDirectory = join(directory, 'host-responses');
  const configurationFile = join(directory, 'configurations.json'), scopeFile = join(directory, 'trusted-scope.json');
  await mkdir(commandDirectory); await mkdir(responseDirectory); await mkdir(join(directory, 'cache'));
  const jsonFile = async (path, value) => {
    const temporary = path + '.tmp'; await writeFile(temporary, JSON.stringify(value), { flag: 'wx' }); await rename(temporary, path);
  };
  // A bounded original local permission, issued by the trusted test host, never renewed by reads.
  await jsonFile(scopeFile, { principal: `${scope.applicationScopeId}/${scope.endUserId}`, scope,
    offlineRead: { allowed: true, authorizationRevision: scope.authorizationRevision, expiresAt: new Date(Date.now() + 3600000).toISOString() } });
  const api = continuityApi(), platformTokens = new Set(), subjects = new Map(), configurations = new Map(), observations = [];
  const controlledFetch = async (input, init) => {
    const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
    assert.equal(url.origin, new URL(apiBaseUrl).origin, 'Only the injected synthetic UI platform is permitted.');
    const isCache = url.pathname.startsWith('/t1/cache/v1/') || url.pathname.startsWith('/t1/runtime-continuity/v1/');
    if (!isCache) {
      const result = await fetchImpl(input, init);
      if (url.pathname === '/v1/app-tokens' && result.status === 200) {
        const requested = JSON.parse(String(init?.body)); assert.equal(requested.endUserId, scope.endUserId);
        platformTokens.add((await result.clone().json()).token);
      }
      if (url.pathname === '/t1/heartbeat' && result.status === 200) {
        const value = await result.json(); return Response.json({ ...value, features: [...new Set([...(value.features ?? []), 'prompt-cache'])] }, { headers: result.headers });
      }
      return result;
    }
    const headers = new Headers(init?.headers);
    assert.ok(platformTokens.has(headers.get('x-tansr-app-token')), 'Cache operations retain the actual same-user minted token boundary.');
    observations.push({ kind: 'platform', path: url.pathname });
    if (url.pathname === '/t1/cache/v1/exchange') {
      const bytes = Buffer.from(init?.body ?? []); assert.ok(bytes.length >= 4 && bytes.length <= 8 * 1048576);
      const controlBytes = bytes.readUInt32BE(0); assert.ok(controlBytes > 0 && controlBytes <= 262144 && 4 + controlBytes < bytes.length);
      const control = JSON.parse(bytes.subarray(4, 4 + controlBytes).toString('utf8'));
      assert.equal(control.protocol, 'sdk2-cache-v1');
      // Decode only the original C2 framing at the synthetic upstream boundary. The real
      // public Serve/provider performs both cache admission and the original kernel turn.
      headers.set('content-type', 'application/json');
      return fetchImpl(`${apiBaseUrl}/t1/exchange`, { ...init, headers, body: bytes.subarray(4 + controlBytes).toString('utf8') });
    }
    // The reused original synthetic peer has a fixed token fixture. Translate only after
    // checking the UI-issued token; production headers, credentials and wire are unchanged.
    headers.set('x-tansr-app-token', 'synthetic-app-user-token');
    const routed = `${BASE}${url.pathname}${url.search}`;
    const diagnostic = /^\/t1\/cache\/v1\/bindings\/([^/]+)\/diagnostics$/.exec(url.pathname);
    if (diagnostic) {
      assert.equal(init?.method ?? 'GET', 'GET'); const bindingId = decodeURIComponent(diagnostic[1]);
      const actual = await api.fetchImpl(`${BASE}/t1/cache/v1/bindings/${encodeURIComponent(bindingId)}`, { ...init, headers });
      assert.equal(actual.status, 200); assert.equal((await actual.json()).bindingId, bindingId);
      const limit = Number(url.searchParams.get('limit')); assert.ok(Number.isInteger(limit) && limit >= 1 && limit <= 100);
      return Response.json({ protocol: 'sdk2-cache-v1', rows: [], next: null });
    }
    return api.fetchImpl(routed, { ...init, headers });
  };
  const cache = await createServeCacheHost({ security: 'trusted-single-application', directory: join(directory, 'cache'), mode: 'create',
    storeId: 'native-storage-cache', applicationScopeId: scope.applicationScopeId, apiBaseUrl, key: hostKey,
    continuity: { ...continuityOptions, runtimePolicy: 'preserve' } });
  const cap = { bytes: 64 * 1048576, records: 4096 }, total = { bytes: 256 * 1048576, records: 16384 }, issuedAtMs = Date.now() - 1000;
  const selectedSource = { sourceId: 'native-ui-archive-source', sourceGeneration: 'native-ui-source-generation' };
  const host = createServeOffloadArchiveHost({ applicationScopeId: scope.applicationScopeId, security: 'trusted-single-application', cache,
    agent: { cwd: directory, configuration: {}, checkpoints: { autoBeforeCompact: false },
      platform: { apiBaseUrl, appId: scope.applicationScopeId, appKey: 'synthetic-only', fetchImpl: controlledFetch, maxOutputTokens: 8192 },
      logger: { info() {}, error() {} } },
    persistence: { directory: join(directory, 'effective-history'), mode: 'create', ...selectedSource,
      cold: createFsBlobStore({ dir: join(directory, 'developer-history') }),
      policy: { codec: 'jsonl', hotRetention: { maxBytes: 65536, keepRecentSegments: 0 }, upload: { mode: 'sync' } },
      segmentation: { maxBytes: 4096, maxRecords: 1 }, maxRollbackBytes: 1048576 },
    archive: { directory: join(directory, 'capture'), mode: 'create', hostId: 'native-ui-archive',
      quota: { host: total, application: total, endUser: total, capture: cap }, maxSessions: 8,
      commandBytes: 1048576, commandDatabasePages: 1024, spoolDatabasePages: 32768, limits,
      epoch: { id: 'native-ui-archive-epoch', issuedAtMs, expiresAtMs: issuedAtMs + limits.epochLifetimeMs } },
    events: { storeId: 'native-ui-archive-events', key: randomBytes(32), maxQueuedOperations: 32, maxSubscriptions: 8 },
    sourceFor(subject) {
      assert.equal(subject.endUserId, scope.endUserId);
      if (!subjects.has(subject.sessionId)) subjects.set(subject.sessionId, { ...subject, observedAt: Date.now() });
      return selectedSource;
    },
    authorize({ subject }) { assert.equal(subject.endUserId, scope.endUserId); return { authorizationRevision: scope.authorizationRevision }; }
  });
  let server;
  try {
    server = await host.start({ host: '127.0.0.1', port: 0, token: 'unused-v1-synthetic', readyFrame: 'none', heartbeatMs: 0,
      terminal: { contract: 'terminal-services-v1', scopeFor: reference => { assert.equal(reference.endUserId, scope.endUserId); return scope; } },
      v2: { governance: { sweepIntervalMs: 0 }, authenticate(request) {
        const identity = authenticate(request); if (identity) assert.equal(identity.endUserId, scope.endUserId);
        observations.push({ kind: 'http', method: request.method, path: new URL(request.url, 'http://127.0.0.1').pathname }); return identity;
      } } });
  } catch (error) { await host.dispose(); throw error; }
  const origin = new URL(server.url); assert.equal(origin.hostname, '127.0.0.1');
  const get = async path => {
    const response = await fetch(new URL(path, origin), { headers: { authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(5000), redirect: 'error' });
    if (response.status === 404) return null;
    assert.equal(response.status, 200, `Actual archive read ${path}: ${response.status}`); return response.json();
  };
  const saveConfigurations = () => jsonFile(configurationFile, { format: 'native-ui-storage-configurations-v1', sessions: [...configurations.values()] });
  await saveConfigurations();
  async function prepare(sessionId) {
    if (configurations.has(sessionId)) return configurations.get(sessionId);
    const subject = subjects.get(sessionId); assert.ok(subject, 'Only a real same-scope host session can receive configuration.');
    const target = await get(`/v3/sdk2/sessions/${encodeURIComponent(sessionId)}/binding-target?protocol=sdk2-ext-v1`);
    if (!target?.bindingId) return null;
    const bindingId = target.bindingId;
    const binding = await get(`/v3/sdk2/bindings/${encodeURIComponent(bindingId)}?protocol=sdk2-ext-v1`);
    const status = await get(`/v3/sdk2/bindings/${encodeURIComponent(bindingId)}/archive/status?protocol=sdk2-ext-v1`);
    if (!binding || !status) return null;
    assert.deepEqual(binding.scope, scope); assert.equal(binding.target.sessionId, sessionId);
    assert.equal(status.sourceId, selectedSource.sourceId); assert.equal(status.sourceGeneration, selectedSource.sourceGeneration);
    const identity = { scope: { applicationScopeId: scope.applicationScopeId, endUserId: scope.endUserId }, bindingId,
      sourceId: status.sourceId, sourceGeneration: status.sourceGeneration, target: { sessionId, generations: status.generations } };
    const originalSubject = { endUserId: scope.endUserId, sessionId };
    const revision = host.readRetentionRevision(originalSubject); assert.equal(revision, '0');
    const approvedRetention = host.readRetentionPage(originalSubject, revision); assert.equal(approvedRetention, null);
    const sessionDirectory = join(directory, 'clients', sessionId); await mkdir(sessionDirectory, { recursive: true });
    const retentionAuthorityFile = join(sessionDirectory, 'retention-authority.json');
    await jsonFile(retentionAuthorityFile, { identity, revision, approvedRetention });
    const paths = { sessionId, archiveConfiguration: join(sessionDirectory, 'archive-create.json'),
      archiveReopenConfiguration: join(sessionDirectory, 'archive-reopen.json'), cacheConfiguration: join(sessionDirectory, 'cache-create.json'),
      cacheReopenConfiguration: join(sessionDirectory, 'cache-reopen.json'), retentionAuthorityFile };
    const storage = { archivePath: join(sessionDirectory, 'archive.sqlite'), outboxPath: join(sessionDirectory, 'material-outbox.sqlite'),
      cursorPath: join(sessionDirectory, 'cursor.dpapi'), keyPath: join(sessionDirectory, 'archive-key.dpapi'), keyId: 'native-ui-user-key', replicationId: 'native-ui-primary' };
    for (const mode of ['create', 'reopen']) {
      await jsonFile(mode === 'create' ? paths.archiveConfiguration : paths.archiveReopenConfiguration,
        { format: 'tansr-example-archive-v1', sessionId, serveUrl: server.url, allowInsecureLoopback: true, trustedScopeFile: scopeFile,
          tokenEnvironment: 'TANSR_NATIVE_UI_TOKEN', retentionAuthorityFile, storage: { mode, ...storage } });
      await jsonFile(mode === 'create' ? paths.cacheConfiguration : paths.cacheReopenConfiguration,
        { format: 'tansr-example-cache-continuity-v1', enablePreview: true, sessionId, serveUrl: server.url, trustedScopeFile: scopeFile,
          statePath: join(sessionDirectory, 'cache-state.dpapi'), mode });
    }
    configurations.set(sessionId, paths); await saveConfigurations(); return paths;
  }
  const processed = new Set(); let active = false, failure, closing;
  const timer = setInterval(async () => {
    if (active || failure) return; active = true;
    try {
      for (const subject of subjects.values()) if (!configurations.has(subject.sessionId)) {
        assert.ok(Date.now() - subject.observedAt < 30000, 'Actual archive binding did not become available within the UI deadline.');
        await prepare(subject.sessionId);
      }
      const commands = (await readdir(commandDirectory)).filter(name => name.endsWith('.json')).sort(); assert.ok(commands.length <= 32);
      const filename = commands.find(name => !processed.has(name)); if (!filename) return;
      assert.match(filename, /^[a-z0-9-]{1,64}\.json$/);
      const bytes = await readFile(join(commandDirectory, filename)); assert.ok(bytes.length <= 4096);
      const command = JSON.parse(bytes.toString('utf8')); assert.equal(filename, command.id + '.json');
      assert.equal(command.action, 'configure'); assert.equal(typeof command.sessionId, 'string');
      const paths = await prepare(command.sessionId); assert.ok(paths); let value = paths;
      if (command.cacheFromSessionId !== undefined) {
        const original = configurations.get(command.cacheFromSessionId); assert.ok(original, 'Original cache state must belong to this same trusted host.');
        const originalConfig = JSON.parse(await readFile(original.cacheReopenConfiguration, 'utf8'));
        const resumedPath = join(directory, 'clients', command.sessionId, `cache-resume-${command.id}.json`);
        await jsonFile(resumedPath, { ...originalConfig, sessionId: command.sessionId }); value = { ...paths, cacheResumeConfiguration: resumedPath };
      }
      processed.add(filename); await jsonFile(join(responseDirectory, filename), { id: command.id, value });
    } catch (error) {
      failure = error; await jsonFile(join(directory, 'host-failure.json'), { type: error.name, message: String(error.message).slice(0, 2048) });
    } finally { active = false; }
  }, 50); timer.unref();
  return { url: server.url, configuration: { url: server.url, contract: 'sdk2-offload-v1', configurationFile, commandDirectory, responseDirectory, scopeFile },
    close: () => closing ??= (async () => {
      clearInterval(timer); while (active) await new Promise(resolve => setTimeout(resolve, 10));
      await server.close(); await server.settleResources?.(); await host.dispose();
      const evidence = { realPublicOffloadArchiveAndCache: true, platform: 'controlled-synthetic', scope,
        configurations: [...configurations.values()], observations, continuity: api.intents.map(item => ({ operation: item.operation, requestId: item.requestId })) };
      await jsonFile(join(directory, 'storage-result.json'), evidence); if (failure) throw failure; return evidence;
    })() };
}
