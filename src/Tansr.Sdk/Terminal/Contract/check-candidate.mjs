// Read-only candidate provenance gate, independent from the original contract/ SDK2 lock.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, resolve, relative, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';
const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../../..');
const manifest = JSON.parse(readFileSync(resolve(here, 'manifest.json'), 'utf8'));
const args = process.argv.slice(2);
if (args.length && (args.length !== 2 || args[0] !== '--source-root')) throw new Error('Usage: check-candidate.mjs [--source-root <Serve source tree>]');
const source = args.length ? resolve(args[1]) : null;
const inside = (base, name) => {
  const file = resolve(base, name), rel = relative(base, file);
  if (isAbsolute(rel) || rel === '..' || rel.startsWith('../') || rel.startsWith('..\\')) throw new Error('Manifest path escapes its root.');
  return file;
};
if (manifest.status !== 'internal-candidate-not-frozen-not-public' || manifest.revision !== '2026-09-26.candidate-4') throw new Error('Candidate status/revision changed.');
if (manifest.sourceCommitted !== false || manifest.sourceWorktreeSnapshot !== true || !/^[a-f0-9]{40}$/.test(manifest.sourceBaseCommit)) throw new Error('Candidate worktree provenance changed.');
for (const item of manifest.files) {
  for (const file of [inside(root, item.snapshot), ...(source ? [inside(source, item.source)] : [])]) {
    const bytes = readFileSync(file);
    if (bytes.length !== item.bytes || createHash('sha256').update(bytes).digest('hex') !== item.sha256) throw new Error('Candidate source/snapshot drift: ' + item.source);
  }
}
const code = readFileSync(resolve(here, '../TerminalCandidateContract.cs'), 'utf8');
if (!code.includes(manifest.files[0].sha256) || !code.includes(manifest.revision)) throw new Error('Consumer candidate fingerprint drift.');
console.log(JSON.stringify({ candidate: manifest.revision, sourceCommitted: manifest.sourceCommitted, sourceBaseCommit: manifest.sourceBaseCommit, schemaSha256: manifest.files[0].sha256, files: manifest.files.length, public: false }));
