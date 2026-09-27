// Original SDK1 host configuration for the native cwd controls. Bound-device sessions
// deliberately cannot change the Serve host cwd; this fixture never enables that plane.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export async function startNativeLegacyFixture({ source, directory, token, scope, fetchImpl, apiBaseUrl }) {
  assert.equal(scope.applicationScopeId, 'native-ui-app');
  assert.equal(scope.endUserId, 'native-ui-user');
  assert.equal(scope.authorizationRevision, '1');
  assert.equal(typeof token, 'string'); assert.ok(token.length > 0);
  assert.equal(typeof fetchImpl, 'function');
  directory = resolve(directory);
  const cwdRoot = join(directory, 'initial'), cwdTarget = join(directory, 'target');
  await mkdir(directory, { recursive: true });
  await Promise.all([cwdRoot, cwdTarget].map(path => mkdir(path)));
  const load = path => import(pathToFileURL(join(source, path)).href);
  const { createAgentSessionFactory, createServeAgentSessionStore, startServer } = await load('packages/server/src/index.ts');
  const routes = [], creations = [], storeErrors = [];
  let modelRequests = 0, closing;
  const controlledFetch = async (input, init) => {
    const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
    assert.equal(url.origin, new URL(apiBaseUrl).origin, 'Only the original synthetic platform is permitted.');
    if (url.pathname === '/t1/exchange') {
      modelRequests++;
      throw new Error('The native SDK1 cwd scenario must not start a model turn.');
    }
    const response = await fetchImpl(input, init);
    if (url.pathname !== '/t1/config' || response.status !== 200) return response;
    const bundle = await response.json();
    assert.ok(bundle.capabilities && typeof bundle.capabilities === 'object');
    // Only this synthetic application's advertised execution capability is omitted.
    // Real factory, public cwd policy, authentication and response contracts are reused.
    const capabilities = { ...bundle.capabilities }; delete capabilities.execution;
    const headers = new Headers(response.headers);
    headers.delete('content-length'); headers.delete('content-encoding');
    return Response.json({ ...bundle, capabilities }, { status: response.status, headers });
  };
  const store = createServeAgentSessionStore({ dir: join(directory, 'sessions'), ownership: {} });
  const build = createAgentSessionFactory({ cwd: cwdRoot, cwdPolicy: { allowedRoots: [directory] },
    store, checkpoints: { autoBeforeCompact: false }, configuration: {},
    onStoreError(sessionId, error) { storeErrors.push({ sessionId, code: error?.code ?? 'store_failed' }); },
    platform: { apiBaseUrl, appId: scope.applicationScopeId, appKey: 'synthetic-only', fetchImpl: controlledFetch } });
  const factory = { ...build.factory, async create(init) {
    const created = await build.factory.create(init);
    creations.push({ sessionId: created.handle.sessionId, resumed: created.resumed });
    return created;
  } };
  const server = await startServer({ host: '127.0.0.1', port: 0, token: 'unused-v1-synthetic', readyFrame: 'none', heartbeatMs: 0,
    createSession: { create() { throw new Error('The original SDK1 v2 routes are required.'); } },
    terminal: { contract: 'terminal-services-v1', scopeFor: reference => {
      assert.equal(reference.endUserId, scope.endUserId); return scope;
    } },
    v2: { createSession: factory, store: build.storeReader, governance: { sweepIntervalMs: 0 },
      authenticate(request) {
        routes.push({ method: request.method, path: new URL(request.url, 'http://127.0.0.1').pathname });
        return request.headers.authorization === `Bearer ${token}` ? { endUserId: scope.endUserId } : null;
      } } });
  assert.equal(new URL(server.url).hostname, '127.0.0.1');
  return { url: server.url, cwdRoot, cwdTarget, close() {
    return closing ??= (async () => {
      const errors = []; let report;
      try { report = await server.drain({ timeoutMs: 5000 }); } catch (error) { errors.push(error); }
      try { await server.settleResources(); } catch (error) { errors.push(error); }
      try { await build.flush(); } catch (error) { errors.push(error); }
      const result = { realKernel: true, realStore: true, executionEnabled: false,
        cwdRoot, cwdTarget, allowedRoots: [directory], creations, routes, modelRequests, storeErrors,
        report: report ?? null, cleanupErrors: errors.map(error => String(error?.message ?? error)) };
      await writeFile(join(directory, 'result.json'), JSON.stringify(result), { flag: 'wx' });
      if (errors.length) throw new AggregateError(errors, 'Native SDK1 cwd fixture cleanup failed.');
      assert.equal(modelRequests, 0); assert.deepEqual(storeErrors, []);
      return result;
    })();
  } };
}
