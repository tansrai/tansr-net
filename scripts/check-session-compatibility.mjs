import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Execute the original Node client against the exact replies also consumed by the C# tests.
// This is a transport behavior comparison, not a replacement for real Serve integration.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = process.argv[2];
assert.ok(source, 'Pass the original tansr-cli source root (run with its tsx loader).');
const fixture = JSON.parse(readFileSync(resolve(root, 'contract/session-compatibility.json'), 'utf8'));
assert.equal(fixture.format, 'tansr-net-session-behavior-v1');
const revision = execFileSync('git', ['-C', source, 'rev-parse', 'HEAD'], { encoding: 'utf8', windowsHide: true }).trim();
assert.equal(revision, fixture.sourceRevision, 'The comparison must use the reviewed original client revision.');
assert.equal(execFileSync('git', ['-C', source, 'status', '--porcelain', '--', 'packages/api-client/src/sdk2'], { encoding: 'utf8', windowsHide: true }).trim(), '');
const { Sdk2SessionClient } = await import(pathToFileURL(resolve(source, 'packages/api-client/src/sdk2/session-client.ts')).href);
const discoveryPath = '/v3/sdk2/session-capabilities?protocol=sdk2-ext-v1';
const scope = { applicationScopeId: 'app', endUserId: 'user', authorizationRevision: '0' };
const results = [];
for (const vector of fixture.vectors) {
  const requests = [];
  const client = new Sdk2SessionClient({ baseUrl: 'https://serve.test', sessionContract: vector.contract,
    readToken: () => 'synthetic-ticket', readContext: () => scope,
    fetchImpl: async (url, init) => {
      const path = new URL(url).pathname + new URL(url).search;
      requests.push(init.method + ' ' + path);
      const reply = path === discoveryPath ? vector.discovery : vector.created;
      if (reply.networkFailure) throw new Error('synthetic-private-detail');
      return new Response(reply.body, { status: reply.status, headers: { 'content-type': 'application/json' } });
    } });
  let error = null;
  try { await client.create(vector.contract === 'sdk1' ? {} : { requestId: 'create-original' }); }
  catch (failure) { error = failure.code; assert.ok(!String(failure).includes('synthetic-private-detail')); }
  assert.equal(error, vector.nodeError, vector.id);
  assert.deepEqual(requests, vector.requests, vector.id);
  results.push({ id: vector.id, requests, error });
}

// Failed explicit refresh must invalidate the earlier same-ticket capability decision.
for (const failure of ['revoked', 'unauthorized']) {
  const requests = []; let discovery = 0;
  const client = new Sdk2SessionClient({ baseUrl: 'https://serve.test', sessionContract: 'sdk2-offload-v1',
    readToken: () => 'synthetic-ticket', readContext: () => scope,
    fetchImpl: async (url, init) => {
      const path = new URL(url).pathname + new URL(url).search;
      requests.push(init.method + ' ' + path);
      const positive = fixture.vectors.find(item => item.id === 'sdk2-new-server');
      const rejected = fixture.vectors.find(item => item.id === (failure === 'revoked' ? 'sdk2-unavailable' : 'sdk2-unauthorized'));
      const reply = path !== discoveryPath ? positive.created : ++discovery === 1 ? positive.discovery : rejected.discovery;
      return new Response(reply.body, { status: reply.status, headers: { 'content-type': 'application/json' } });
    } });
  await client.create({ requestId: 'first' });
  if (failure === 'unauthorized') await assert.rejects(client.sessionCapabilities());
  else assert.equal((await client.sessionCapabilities()).contracts.some(item => item.contract === 'sdk2-offload-v1'), false);
  await assert.rejects(client.create({ requestId: 'second' }));
  assert.equal(requests.filter(item => item.startsWith('POST ')).length, 1);
  results.push({ id: 'explicit-refresh-' + failure, requests });
}
console.log(JSON.stringify({ sourceRevision: revision, vectors: fixture.vectors.length, refreshCases: 2, failed: 0, skipped: 0, results }, null, 2));
