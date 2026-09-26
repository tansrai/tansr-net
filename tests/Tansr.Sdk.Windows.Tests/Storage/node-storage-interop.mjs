import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

let input = '';
for await (const chunk of process.stdin) { input += chunk; assert.ok(input.length <= 2 * 1024 * 1024); }
const data = JSON.parse(input);
// 实际导入项目锁定实现；这里没有重实现接收器、ACK 或数据库格式。
const revision = execFileSync('git', ['-C', data.cliRoot, 'rev-parse', 'HEAD'], { encoding: 'utf8', windowsHide: true }).trim();
assert.equal(revision, data.sourceRevision);
assert.equal(execFileSync('git', ['-C', data.cliRoot, 'status', '--porcelain', '--', 'packages/api-client/src/sdk2'], { encoding: 'utf8', windowsHide: true }).trim(), '');
const referenceDirectory = join(data.cliRoot, 'packages', 'api-client', 'src', 'sdk2');
const source = await import(pathToFileURL(join(referenceDirectory, 'receiver-sqlite.ts')).href);
const wire = await import(pathToFileURL(join(referenceDirectory, 'wire-codec.ts')).href);
const open = data.encrypted ? source.openSdk2EncryptedSqliteSyncArchiveStore : source.openSdk2SqliteSyncArchiveStore;
const store = await open({
  path: data.path, mode: data.action === 'create-pending' ? 'create' : 'reopen',
  identity: data.identity, replica: data.replica, syncRole: 'source', limits: data.limits, maxPages: data.maxPages,
  readContext: () => data.scope, readRetentionRevision: () => '0', authorizeRetention: () => { throw new Error('not authorized in this fixture'); },
  ...(data.encrypted ? { key: { id: 'key', read: () => Uint8Array.from({ length: 32 }, (_, index) => index) } } : {}),
});
try {
  if (data.action === 'create-pending') {
    const body = Uint8Array.from(Buffer.from(data.bodyBase64, 'base64'));
    await store.receive({ ...data.input, artifacts: [{ artifactId: data.artifact.artifactId, body }] });
  } else {
    assert.equal(Buffer.from(await store.body(data.artifact)).toString('base64'), data.bodyBase64);
    const ack = await store.pending();
    if (data.action === 'reopen-confirmed') assert.equal(ack, null);
    else {
      assert.ok(ack); const { request, ...semantic } = ack;
      const canonical = wire.encodeControl({ scope: [data.scope.applicationScopeId, data.scope.endUserId], operation: 'archive-ack', semantic }, 1048576);
      await store.confirm({ protocol: 'sdk2-ext-v1', bindingId: ack.bindingId, request, operation: 'archive-ack',
        semanticDigest: createHash('sha256').update('tansr.sdk2.operation.v1\0' + canonical).digest('hex'),
        state: 'completed', revision: '3', outcomeRef: ack.bindingId });
    }
  }
} finally { await store.close(); }
process.stdout.write('original-node-consumer-ok\n');
