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
if (manifest.status !== 'schema-pinned-preview-opt-in' || manifest.revision !== '2026-09-26.candidate-7') throw new Error('Candidate status/revision changed.');
if (manifest.sourceCommitted !== false || manifest.sourceWorktreeSnapshot !== true || !/^[a-f0-9]{40}$/.test(manifest.sourceBaseCommit)) throw new Error('Candidate worktree provenance changed.');
for (const item of manifest.files) {
  for (const file of [inside(root, item.snapshot), ...(source ? [inside(source, item.source)] : [])]) {
    const bytes = readFileSync(file);
    if (bytes.length !== item.bytes || createHash('sha256').update(bytes).digest('hex') !== item.sha256) throw new Error('Candidate source/snapshot drift: ' + item.source);
  }
}
const code = readFileSync(resolve(here, '../TerminalCandidateContract.cs'), 'utf8');
if (!code.includes(manifest.files[0].sha256) || !code.includes(manifest.revision)) throw new Error('Consumer candidate fingerprint drift.');
const schemas = manifest.files.filter(item => item.source.endsWith('.schema.json'));
const original = JSON.parse(readFileSync(resolve(root, 'contract/sdk2-ext-v1.schema.json'), 'utf8')).definitions;
const copied = ['Id', 'LegacyId', 'Sequence', 'Digest', 'Scope', 'ExecutionBinding', 'ExecutionTarget', 'ExecutionInterpreter',
  'ExecutionStatus', 'ExecutionOperation', 'ResourceRequest', 'ExecutionReceiptRequest', 'ResourceResult'];
for (const item of schemas) {
  const definitions = JSON.parse(readFileSync(resolve(root, item.snapshot), 'utf8')).definitions;
  const selected = item.source.includes('archive-recovery') ? Object.keys(definitions).filter(name => !name.startsWith('AckRebase')) : copied;
  for (const name of selected) if (JSON.stringify(definitions[name]) !== JSON.stringify(original[name])) throw new Error('Copied SDK2 definition drift: ' + name);
}
const recovery = schemas.find(item => item.source.includes('archive-recovery'));
if (recovery?.sha256 !== manifest.archiveRecovery.schemaSha256) throw new Error('Recovery schema fingerprint drift.');
const golden = JSON.parse(readFileSync(resolve(root, 'tests/Tansr.Sdk.Tests/Terminal/Fixtures/sdk2-archive-recovery-v1.golden.json'), 'utf8'));
if (golden.contract !== manifest.archiveRecovery.contract || golden.schemaSha256 !== recovery.sha256) throw new Error('Recovery golden fingerprint drift.');
const generated = readFileSync(resolve(here, 'reference-terminal-schema-generated.ts.txt'), 'utf8');
for (const name of ['BackgroundToolName', 'BackgroundToolDefinitionSha256', 'MemoryPublicationToolName', 'MemoryPublicationToolDefinitionSha256']) {
  const value = code.match(new RegExp('const string ' + name + ' = "([^"]+)"'))?.[1];
  if (!value || !generated.includes('"' + value + '"')) throw new Error('Typed tool profile fingerprint drift: ' + name);
}
console.log(JSON.stringify({ candidate: manifest.revision, sourceCommitted: manifest.sourceCommitted, sourceBaseCommit: manifest.sourceBaseCommit, schemaSha256: manifest.files[0].sha256,
  recoverySha256: manifest.archiveRecovery.schemaSha256, files: manifest.files.length, publicPreview: true, stable: false }));
