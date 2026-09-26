import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { check, extractExports, extractMembers } from './check-parity.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const load = () => JSON.parse(readFileSync(join(root, 'doc/compatibility/public-api-map.json'), 'utf8'));

test('all fixed public entries are inventoried without claiming behavior completion', () => {
  const map = load();
  assert.deepEqual(check(map), { entries: 681, enumerated: 681, groups: 16, sourceFiles: 11, acceptedBehavior: 0, referencedEvidence: map.behaviorEvidence.length });
});

test('dropping a difficult original class member fails the same inventory gate', () => {
  const map = load();
  map.entries = map.entries.filter(entry => entry.symbol !== 'AgentSession.withArchiveDetachment');
  assert.throws(() => check(map), /New public member has no mapping/);
});

test('host properties cannot silently disappear from the behavior mapping', () => {
  const map = load();
  map.entries = map.entries.filter(entry => entry.symbol !== 'ChatApp.paths');
  assert.throws(() => check(map), /New public member has no mapping/);
});

test('advanced query and runAgent invocations cannot be omitted', () => {
  const map = load();
  map.entries = map.entries.filter(entry => entry.inventory !== 'advanced-entry' || entry.symbol !== 'runAgent');
  assert.throws(() => check(map), /Advanced SDK invocation has no mapping/);
});

test('new upstream exports fail before a stale source can be approved', () => {
  const map = load(), directory = mkdtempSync(join(tmpdir(), 'tansr-net-parity-'));
  try {
    for (const source of map.sources) {
      const target = join(directory, source.path);
      mkdirSync(dirname(target), { recursive: true });
      let bytes = readFileSync(join(root, source.snapshot));
      if (source.path === 'packages/sdk/src/index.ts') bytes = Buffer.concat([bytes, Buffer.from('\nexport const UnmappedBusinessCapability = 1;\n')]);
      writeFileSync(target, bytes);
    }
    assert.throws(() => check(map, directory), /New public entry has no mapping.*UnmappedBusinessCapability/);
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

test('new public class members are collected but private fields and bodies are not', () => {
  assert.deepEqual(extractMembers('export class Example {\n  readonly #secret = 1;\n  private hidden(): void {}\n  public read(): void { const nested = { pretend: 1 }; }\n  get state(): string { return "public fake() {}"; }\n  readonly path: string;\n}', 'Example').map(item => item.name), ['Example.read', 'Example.state', 'Example.path']);
  assert.deepEqual(extractExports('// export const invented = 1;\nexport { actual as alias } from "module";').map(item => item.name), ['alias']);
});

test('an individual reviewed behavior can progress without forcing all groups green', () => {
  const map = load(), entry = map.entries.find(item => item.symbol === 'AgentSession.submitInput');
  assert.ok(entry);
  Object.assign(entry, { status: 'verified', remaining: '', evidence: ['session-input'], acceptance: ['NET-A05: original target and inputId, once-only consumption and draft retention'] });
  assert.equal(check(map).acceptedBehavior, 0);
});

test('unrun evidence or an absent receipt cannot approve behavior', () => {
  const map = load(), entry = map.entries.find(item => item.group === 'P01');
  map.behaviorEvidence.find(item => item.id === 'session-version-selection').status = 'ready-not-run';
  Object.assign(entry, { status: 'verified', remaining: '', evidence: ['session-version-selection'], acceptance: ['NET-A04'] });
  assert.throws(() => check(map), /Cannot close with unrun evidence/);
  const second = load(); delete second.behaviorEvidence[0].receipt;
  assert.throws(() => check(second), /Passed behavior lacks source\/receipt/);
});

test('a complete group cannot hide open entries or remaining acceptance conditions', () => {
  const map = load(), group = map.groups.find(item => item.id === 'P03');
  Object.assign(group, { complete: true, remaining: '', acceptance: ['complete original behavior comparison'] });
  assert.throws(() => check(map), /Group has open behavior entries/);
});

test('behavior evidence must still point to a real executable entry', () => {
  const map = load(); map.behaviorEvidence[0].files[0].anchor = 'PretendTestThatDoesNotExist';
  assert.throws(() => check(map), /Behavior entry missing/);
  const second = load(); second.behaviorEvidence[0].files[0].path = '../outside.cs';
  assert.throws(() => check(second), /Source escapes root/);
});
