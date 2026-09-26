// Acceptance adapter only: load the exact original Node implementation, never dist or a port.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, realpathSync } from 'node:fs';
import { dirname, join, resolve, toNamespacedPath } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { DatabaseSync } from 'node:sqlite';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const manifestBytes = readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'Fixtures', 'memory-publication-source-manifest.json'));
const manifest = JSON.parse(manifestBytes);
assert.equal(manifest.format, 'tansr-node-memory-publication-source-v1');
assert.equal(manifest.sourceCommitted, true);
let input = '';
for await (const chunk of process.stdin) { input += chunk; assert.ok(Buffer.byteLength(input) <= 2 * 1024 * 1024); }
const data = JSON.parse(input), root = realpathSync(data.cliRoot);
function verifySources() {
  for (const entry of manifest.files) {
    const path = resolve(root, entry.path), bytes = readFileSync(path);
    assert.equal(bytes.length, entry.bytes, 'Frozen Node source size changed: ' + entry.path);
    assert.equal(hash(bytes), entry.sha256, 'Frozen Node source changed: ' + entry.path);
  }
}
verifySources();
const runtimeCommit = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true }).trim();
// The current checkout may be a later mainline; exact source bytes, not branch naming, bind this test.
const load = path => import(pathToFileURL(join(root, path)).href);
const { openMemoryPublicationSqlite, MemoryPublicationAdapterError } = await load(manifest.entry);
const { encodeControl } = await load('packages/api-client/src/sdk2/wire-codec.ts');
const { validateTerminalNamed } = await load('packages/api-client/src/terminal/wire-codec.ts');
assert.equal(data.owner, encodeControl(JSON.parse(data.owner)));
assert.ok(['create', 'reopen'].includes(data.mode));
assert.ok(Array.isArray(data.operations) && data.operations.length > 0 && data.operations.length <= 128);
const store = await openMemoryPublicationSqlite({ path: data.path, mode: data.mode, identity: data.identity,
  readContext: () => data.scope, maxTransfers: data.maxTransfers, maxStagingBytes: data.maxStagingBytes, maxPages: data.maxPages });
const responses = [];
let capacity;
try {
  assert.equal(store.atomicDurablePublication, true);
  assert.equal(encodeControl(store.identity), encodeControl(data.identity));
  for (const operation of data.operations) {
    if (operation.expectedError !== null) {
      let rejected = false;
      try { await store.execute(operation.request, operation.owner ?? data.owner); }
      catch (error) {
        assert.ok(error instanceof MemoryPublicationAdapterError); assert.equal(error.code, operation.expectedError);
        rejected = true; responses.push({ error: error.code });
      }
      assert.equal(rejected, true, 'Original Node accepted a request expected to be rejected.');
    } else {
      const response = await store.execute(operation.request, operation.owner ?? data.owner);
      validateTerminalNamed('MemoryPublicationResponse', response); responses.push(response);
    }
  }
  capacity = await store.capacity();
} finally { await store.close(); }

// Read only after the production owner is closed. Never repair its metadata or database layout.
const db = new DatabaseSync(process.platform === 'win32' ? toNamespacedPath(data.path) : data.path, { readOnly: true });
let metadata;
try {
  const ddl = db.prepare("SELECT sql FROM sqlite_master WHERE substr(name,1,7)<>'sqlite_' ORDER BY sql").all().map(row => row.sql);
  assert.deepEqual(ddl, [...manifest.ddl].sort());
  metadata = db.prepare('SELECT json FROM metadata WHERE id=1').get().json;
  assert.equal(metadata, encodeControl(JSON.parse(metadata), 1048576));
  assert.equal(db.prepare('PRAGMA quick_check').get().quick_check, 'ok');
  assert.equal(db.prepare('PRAGMA journal_mode').get().journal_mode, 'wal');
  assert.equal(db.prepare('PRAGMA page_size').get().page_size, 4096);
} finally { db.close(); }
verifySources();
process.stdout.write(JSON.stringify({ responses, capacity, metadata,
  provenance: { sourceCommit: manifest.sourceCommit, runtimeCommit, sourceCommitted: true,
    manifestSha256: hash(manifestBytes), sourceFiles: manifest.files.length, entrySha256: manifest.files.find(file => file.path === manifest.entry).sha256 } }) + '\n');
