// Acceptance provenance only. Explicitly snapshots an active candidate; the default clean-source
// integration gate is unchanged. Every runtime source file is checked before and after execution.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
function inventory(source) {
  const paths = [];
  function visit(relative) {
    if (!existsSync(resolve(source, relative))) return;
    for (const entry of readdirSync(resolve(source, relative), { withFileTypes: true })) {
      assert.ok(!entry.isSymbolicLink(), 'Source snapshot cannot contain a symlink: ' + relative + '/' + entry.name);
      const path = relative + '/' + entry.name;
      if (entry.isDirectory()) visit(path); else if (entry.isFile()) paths.push(path);
    }
  }
  for (const pkg of readdirSync(resolve(source, 'packages'), { withFileTypes: true })) {
    if (!pkg.isDirectory()) continue;
    for (const directory of ['src', 'test']) visit('packages/' + pkg.name + '/' + directory);
    const file = 'packages/' + pkg.name + '/package.json'; if (existsSync(resolve(source, file))) paths.push(file);
  }
  for (const file of ['package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml',
    'doc/rfc/sdk2-ext-v1.schema.json', 'doc/rfc/terminal-services-v1.schema.json', 'doc/rfc/sdk2-archive-recovery-v1.schema.json']) {
    assert.ok(existsSync(resolve(source, file)), 'Required source snapshot entry missing: ' + file); paths.push(file);
  }
  return paths.sort().map(path => { const bytes = readFileSync(resolve(source, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; });
}
function approvedSchemas(files) {
  const pins = {
    'doc/rfc/sdk2-ext-v1.schema.json': '969273844ca9196f19dd71b292b65a49307d63be0d20a0caf557e105ba6d8605',
    'doc/rfc/terminal-services-v1.schema.json': '8cd8c7c55a84c5700373aed75d5653a0737d718bfe0546641be367bda1a11896',
    'doc/rfc/sdk2-archive-recovery-v1.schema.json': 'f530de1096b4f5d56ea688b7f2cec9ae66deb7d3719db85ab1f48287d3bd7ad4'
  };
  for (const [path, expected] of Object.entries(pins)) assert.equal(files.find(entry => entry.path === path)?.sha256, expected, 'Approved candidate schema drift: ' + path);
}
export function verifyServeSourceSnapshot(source, filename) {
  const bytes = readFileSync(filename), value = JSON.parse(bytes);
  assert.equal(value.format, 'tansr-serve-source-snapshot-v1'); assert.equal(value.sourceCommitted, false);
  assert.equal(realpathSync(source), value.sourceRoot);
  assert.equal(execFileSync('git', ['rev-parse', 'HEAD'], { cwd: source, encoding: 'utf8', windowsHide: true }).trim(), value.sourceBaseCommit);
  const current = inventory(source); approvedSchemas(current);
  assert.deepEqual(current, value.files, 'Active Serve source changed since its explicit snapshot; freeze a new reviewed snapshot, never refresh it during a run.');
  return { sourceSnapshotSha256: hash(bytes), sourceCommitted: false, sourceBaseCommit: value.sourceBaseCommit,
    candidateRevision: '2026-09-26.candidate-7', sourceSnapshotFiles: current.length };
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  assert.equal(process.argv.length, 4, 'Usage: TANSR_SERVE_SOURCE=<candidate> node scripts/serve-source-snapshot.mjs --write <new-manifest-path>');
  assert.equal(process.argv[2], '--write'); assert.ok(process.env.TANSR_SERVE_SOURCE, 'TANSR_SERVE_SOURCE is required.');
  const source = realpathSync(process.env.TANSR_SERVE_SOURCE), files = inventory(source); approvedSchemas(files);
  const value = { format: 'tansr-serve-source-snapshot-v1', task: 'NET-03-native-services', createdAt: new Date().toISOString(),
    sourceRoot: source, sourceCommitted: false, sourceBaseCommit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: source, encoding: 'utf8', windowsHide: true }).trim(), files };
  writeFileSync(resolve(process.argv[3]), JSON.stringify(value, null, 2) + '\n', { flag: 'wx' });
  console.log(JSON.stringify(verifyServeSourceSnapshot(source, resolve(process.argv[3]))));
}
