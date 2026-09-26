import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

let input = '';
for await (const chunk of process.stdin) { input += chunk; assert.ok(input.length <= 2 * 1024 * 1024); }
const data = JSON.parse(input), directory = join(data.cliRoot, 'packages', 'api-client', 'src', 'sdk2');
// 原 Serve 冻结实现及独立恢复 schema；没有在测试里重实现接收器或修改原库 metadata。
assert.equal(createHash('sha256').update(readFileSync(join(directory, 'receiver-sqlite.ts'))).digest('hex'),
  'de45e05f3baccff36c381bdc9275011f53fb72bd759f5f08771d71747fca644b');
assert.ok(readFileSync(join(directory, 'archive-recovery-types-generated.ts'), 'utf8').includes('f530de1096b4f5d56ea688b7f2cec9ae66deb7d3719db85ab1f48287d3bd7ad4'));
const source = await import(pathToFileURL(join(directory, 'receiver-sqlite.ts')).href);
const wire = await import(pathToFileURL(join(directory, 'wire-codec.ts')).href);
const options = { path: data.path, mode: 'reopen', identity: data.identity, replica: data.replica, syncRole: 'source', limits: data.limits,
  maxPages: data.maxPages, readContext: () => data.scope, readRetentionRevision: () => '0', authorizeRetention: () => { throw new Error('not authorized in this fixture'); } };
const body = Uint8Array.from(Buffer.from(data.bodyBase64, 'base64'));
const batch = { ...data.input, artifacts: [{ artifactId: data.artifact.artifactId, body }] };
if (data.action === 'prepare' && data.migrate) {
  const old = await source.openSdk2SqliteSyncArchiveStore({ ...options, mode: 'create' });
  try { await old.receive(batch); } finally { await old.close(); }
}
const store = await source.openSdk2RecoverableSqliteSyncArchiveStore({ ...options,
  mode: data.action === 'prepare' ? data.migrate ? 'migrate-v1' : 'create' : 'reopen' });
try {
  if (data.action === 'prepare') {
    if (!data.migrate) await store.receive(batch);
    const intent = await store.prepareAckRebase(data.request); assert.deepEqual(intent, await store.pendingAckRebase());
  } else {
    assert.equal(Buffer.from(await store.body(data.artifact)).toString('base64'), data.bodyBase64);
    if (data.action === 'read-completed') {
      assert.equal(await store.pending(), null); assert.equal(await store.pendingAckRebase(), null);
      const page = await store.syncPage(null); assert.deepEqual(page.ack.request, data.request); assert.deepEqual(page.checkpoint.page, data.input.page);
    } else {
      const intent = await store.pendingAckRebase(); assert.ok(intent); assert.deepEqual(intent.request, data.request);
      const next = { ...intent.previous, request: intent.request, expectedRevision: '6' }, { request, ...semantic } = next;
      const receipt = { protocol: 'sdk2-ext-v1', bindingId: next.bindingId, request, operation: 'archive-ack',
        semanticDigest: createHash('sha256').update('tansr.sdk2.operation.v1\0' + wire.encodeControl({
          scope: [data.scope.applicationScopeId, data.scope.endUserId], operation: 'archive-ack', semantic }, 1048576)).digest('hex'),
        state: 'completed', revision: '7', outcomeRef: next.bindingId };
      await store.confirmAckRebase({ ...intent, next, receipt });
    }
  }
} finally { await store.close(); }
await assert.rejects(source.openSdk2SqliteSyncArchiveStore(options));
process.stdout.write('original-node-recovery-ok\n');
