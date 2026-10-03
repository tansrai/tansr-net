import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

// The generator is the only writer of ApiRoutes.generated.cs and the only place the vendored manifest is validated on the
// C# side. These tests pin (1) that the pristine repo artifact passes, and (2) that the structural rules the generator
// claims are real: for each golden Manifest negative accounted as "generator-validated" in UnifiedGoldenTests, the same
// mutation applied to the repo artifact must be rejected with the specific message. Golden patches target
// manifest-runtime-view (5 operations), so each mutation is re-targeted at a repo-artifact operation of the same shape.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const generator = join(root, 'scripts/generate-api-routes.mjs');
const artifact = () => JSON.parse(readFileSync(join(root, 'contract/api-manifest.json'), 'utf8'));
const golden = JSON.parse(readFileSync(join(root, 'contract/unified-v1.golden.json'), 'utf8'));

const run = (args) => {
  const result = spawnSync(process.execPath, [generator, ...args], { cwd: root, encoding: 'utf8' });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
};
const validate = (manifest) => {
  const dir = mkdtempSync(join(tmpdir(), 'tansr-api-routes-'));
  try {
    const file = join(dir, 'api-manifest.json');
    writeFileSync(file, JSON.stringify(manifest));
    return run(['--validate', file]);
  } finally { rmSync(dir, { recursive: true, force: true }); }
};
const first = (manifest, predicate) => {
  const op = manifest.operations.find(predicate);
  assert.ok(op, 'repo artifact lacks an operation of the required shape');
  return op;
};
const postWrite = (m) => first(m, (op) => op.method === 'POST' && op.kind === 'write' && op.family !== null);

// golden name → (mutate repo artifact in place, return the expected failure message pattern)
const covered = {
  'manifest-missing-schema-hash': (m) => { delete m.schemaHash; return /manifest schemaHash must be 64 hex chars/; },
  'manifest-revision-zero': (m) => { m.revision = 0; return /manifest revision must be a positive integer/; },
  'manifest-family-sha256-short': (m) => { m.families[0].sha256 = m.families[0].sha256.slice(1); return /: sha256 is not a Digest/; },
  'manifest-operation-name-case': (m) => { postWrite(m).name = 'Session.Message.Send'; return /: name is not an OperationName/; },
  'manifest-operation-method-put': (m) => { postWrite(m).method = 'PUT'; return /: method PUT outside the schema enum/; },
  'manifest-operation-stream-without-sse': (m) => { first(m, (op) => op.sse === true).sse = false; return /: kind\/sse disagree/; },
  'manifest-operation-post-read': (m) => { postWrite(m).kind = 'read'; return /: POST cannot be read/; },
  'manifest-operation-read-with-expected-revision': (m) => {
    first(m, (op) => op.kind === 'read').expectedRevision = { path: ['expectedRevision'], kind: 'sequence' };
    return /: expectedRevision on a read operation/;
  },
  'manifest-operation-etag-path-empty': (m) => { first(m, (op) => op.etagPath !== null).etagPath = []; return /: etagPath is not a KeyPath/; },
  'manifest-family-request-id-path-string': (m) => {
    m.families.find((family) => family.requestIdPath !== null).requestIdPath = 'request.requestId';
    return /: requestIdPath is not a KeyPath/;
  },
  'manifest-discovery-operation-with-family': (m) => {
    first(m, (op) => op.domain === 'discovery').family = m.families[0].id;
    return /: discovery operations have family null, others name their family/;
  },
  'manifest-operation-api-path-legacy': (m) => { postWrite(m).apiPath = '/v2/sessions/:id/messages'; return /: apiPath \/v2\/sessions\/:id\/messages is outside \/api/; },
};

test('the pristine repo artifact validates and the generated route table is current', () => {
  const validated = validate(artifact());
  assert.equal(validated.status, 0, validated.stderr);
  assert.match(validated.stdout, /^api-routes: valid \(81 operations, revision 7, sha256 [0-9a-f]{64}\)/);
  const checked = run(['--check']);
  assert.equal(checked.status, 0, checked.stderr);
  assert.match(checked.stdout, /^api-routes: ok \(81 operations, revision 7, /);
});

test('--validate never touches the lock or the generated file', () => {
  const before = readFileSync(join(root, 'src/Tansr.Sdk/Api/ApiRoutes.generated.cs'));
  const mutated = artifact(); mutated.revision += 1; // lock mismatch would fail --check; --validate must not care
  const result = validate(mutated);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(readFileSync(join(root, 'src/Tansr.Sdk/Api/ApiRoutes.generated.cs')), before);
});

test('a file that is not a manifest is a usage error, not a crash', () => {
  const result = validate({ definitions: {} });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /api-routes: .* is not a manifest/);
});

for (const [name, mutate] of Object.entries(covered)) {
  test(`golden ${name}: the same mutation on the repo artifact is rejected`, () => {
    const vector = golden.vectors.find((v) => v.name === name);
    assert.ok(vector, `golden vector ${name} is missing`);
    assert.equal(vector.definition, 'Manifest'); assert.equal(vector.expect, 'invalid');
    const manifest = artifact();
    const expected = mutate(manifest);
    const result = validate(manifest);
    assert.equal(result.status, 1, `expected rejection for ${name}; stdout: ${result.stdout}`);
    assert.match(result.stderr, expected);
    assert.ok(result.stderr.split('\n').every((line) => line === '' || line.startsWith('api-routes: ')), result.stderr);
  });
}

test('the C# accounting names exactly the vectors covered here', () => {
  const source = readFileSync(join(root, 'tests/Tansr.Sdk.Tests/Api/UnifiedGoldenTests.cs'), 'utf8');
  const declared = [...source.matchAll(/\["Manifest\/(manifest-[a-z0-9-]+)"\] = (?:GeneratorValidated|"generator-validated")\b/g)].map((m) => m[1]).sort();
  assert.deepEqual(declared, Object.keys(covered).sort());
  // Every other golden Manifest negative must be listed in the C# NotConsumed table with a reason.
  const negatives = golden.vectors.filter((v) => v.definition === 'Manifest' && v.expect === 'invalid').map((v) => v.name);
  for (const name of negatives) {
    if (name in covered) continue;
    assert.match(source, new RegExp(`\\["Manifest/${name}"\\] = "`), `${name} is neither generator-validated nor listed as not consumed`);
  }
});
