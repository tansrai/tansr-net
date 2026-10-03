#!/usr/bin/env node
// Single writer for src/Tansr.Sdk/Api/ApiRoutes.generated.cs.
// Source of truth: contract/api-manifest.json (vendored byte-for-byte from tansr-cli
// packages/server/contract/api-manifest.json and locked in contract/manifest.json).
//
//   node scripts/generate-api-routes.mjs                   # regenerate the C# module
//   node scripts/generate-api-routes.mjs --check           # verify lock + generated file, exit 1 on drift
//   node scripts/generate-api-routes.mjs --validate <file> # structural checks only, against any manifest file; no lock, no write
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const validateAt = process.argv.indexOf('--validate');
const validateOnly = validateAt >= 0;
const manifestPath = validateOnly ? resolve(process.argv[validateAt + 1] ?? '') : resolve(root, 'contract/api-manifest.json');
const lockPath = resolve(root, 'contract/manifest.json');
const outputPath = resolve(root, 'src/Tansr.Sdk/Api/ApiRoutes.generated.cs');
const check = process.argv.includes('--check');

const manifestBytes = readFileSync(manifestPath);
const manifestSha = createHash('sha256').update(manifestBytes).digest('hex');
const manifest = JSON.parse(manifestBytes.toString('utf8'));
if (!Array.isArray(manifest?.operations) || !Array.isArray(manifest?.families)) { console.error(`api-routes: ${manifestPath} is not a manifest (operations/families must be arrays)`); process.exit(1); }
const lock = JSON.parse(readFileSync(lockPath, 'utf8'));
// Vocabularies come from the vendored unified-v1 schema (same revision as the manifest), never from a copy kept here.
const unifiedSchema = JSON.parse(readFileSync(resolve(root, 'contract/unified-v1.schema.json'), 'utf8'));
const definition = (name) => unifiedSchema.definitions?.[name] ?? {};
const patternOf = (name) => new RegExp(definition(name).pattern ?? '(?!)');

const failures = [];
const fail = (message) => failures.push(message);

if (manifest.format !== 'tansr-api-manifest-v1') fail(`unexpected manifest format ${manifest.format}`);
if (manifest.contract !== 'unified-v1') fail(`unexpected manifest contract ${manifest.contract}`);
if (!Number.isInteger(manifest.revision) || manifest.revision < 1) fail('manifest revision must be a positive integer');
if (!/^[0-9a-f]{64}$/.test(manifest.schemaHash ?? '')) fail('manifest schemaHash must be 64 hex chars');

if (!validateOnly) {
  const lockEntry = (lock.files ?? []).find((file) => file.snapshot === 'contract/api-manifest.json');
  if (!lockEntry) fail('contract/manifest.json has no entry for contract/api-manifest.json');
  else {
    if (lockEntry.sha256 !== manifestSha) fail(`lock sha256 ${lockEntry.sha256} != actual ${manifestSha}`);
    if (lockEntry.bytes !== manifestBytes.length) fail(`lock bytes ${lockEntry.bytes} != actual ${manifestBytes.length}`);
  }
  if (lock.apiManifest) {
    if (lock.apiManifest.revision !== manifest.revision) fail(`lock apiManifest.revision ${lock.apiManifest.revision} != ${manifest.revision}`);
    if (lock.apiManifest.schemaHash !== manifest.schemaHash) fail(`lock apiManifest.schemaHash != manifest schemaHash`);
  } else fail('contract/manifest.json has no apiManifest block');
}

// Operation facts that end up in ApiRoutes are validated against the schema's own vocabulary and co-constraints
// (ManifestOperation.method / kind enums and its allOf rules): a drift fails here, before any C# is written.
const methods = definition('ManifestOperation').properties?.method?.enum ?? [];
const kinds = definition('ManifestOperation').properties?.kind?.enum ?? [];
if (methods.length === 0 || kinds.length === 0) fail('unified-v1.schema.json lacks ManifestOperation method/kind enums');
const operationName = patternOf('OperationName');
const familyId = patternOf('FamilyId');
const digest = patternOf('Digest');
const familyIds = new Set((manifest.families ?? []).map((family) => family.id));
// Placeholder vocabulary is closed; ApiOperation.Path exposes exactly these named parameters.
const allowedPlaceholders = ['id', 'targetId', 'uploadId', 'ticketId'];
const placeholdersOf = (template) => [...template.matchAll(/:([A-Za-z][A-Za-z0-9]*)/g)].map((m) => m[1]);
const pascal = (name) => name.split(/[.\-_]/).filter(Boolean).map((part) => part[0].toUpperCase() + part.slice(1)).join('');
const names = new Set();
for (const op of manifest.operations) {
  if (!operationName.test(op.name ?? '')) fail(`${op.name}: name is not an OperationName`);
  if (!op.apiPath.startsWith('/api/') && op.apiPath !== '/api') fail(`${op.name}: apiPath ${op.apiPath} is outside /api`);
  for (const template of [op.apiPath, ...op.aliases]) {
    for (const placeholder of placeholdersOf(template)) {
      if (!allowedPlaceholders.includes(placeholder)) fail(`${op.name}: placeholder :${placeholder} is not in the closed vocabulary`);
    }
  }
  const member = pascal(op.name);
  if (names.has(member)) fail(`${op.name}: duplicate C# member name ${member}`);
  names.add(member);
  if (!methods.includes(op.method)) fail(`${op.name}: method ${op.method} outside the schema enum`);
  if (!kinds.includes(op.kind)) fail(`${op.name}: kind ${op.kind} outside the schema enum`);
  if ((op.kind === 'stream') !== (op.sse === true)) fail(`${op.name}: kind/sse disagree`);
  // schema allOf: GET ⇒ read | stream, any other method ⇒ write.
  if (op.method === 'GET' ? op.kind === 'write' : op.kind !== 'write') fail(`${op.name}: ${op.method} cannot be ${op.kind}`);
  // schema allOf: discovery operations are facade-owned (family null); every other operation names a declared family.
  if ((op.domain === 'discovery') !== (op.family === null)) fail(`${op.name}: discovery operations have family null, others name their family`);
  if (op.family !== null && !familyIds.has(op.family)) fail(`${op.name}: family ${op.family} is not declared`);
  // revision 7 facts (U7-OPS §3): etagPath / expectedRevision are required keys (null when the resource has no version);
  // expectedRevision only on write operations; KeyPath = 1..8 object keys.
  if (!('etagPath' in op) || !('expectedRevision' in op)) fail(`${op.name}: revision 7 requires etagPath and expectedRevision keys`);
  if (op.etagPath !== null && !isKeyPath(op.etagPath)) fail(`${op.name}: etagPath is not a KeyPath`);
  if (op.expectedRevision !== null) {
    if (op.kind !== 'write') fail(`${op.name}: expectedRevision on a ${op.kind} operation`);
    if (!isKeyPath(op.expectedRevision?.path) || !['sequence', 'integer'].includes(op.expectedRevision?.kind)) fail(`${op.name}: expectedRevision shape`);
  }
}
for (const family of manifest.families) {
  if (!familyId.test(family.id ?? '')) fail(`${family.id}: id is not a FamilyId`);
  if (!digest.test(family.sha256 ?? '')) fail(`${family.id}: sha256 is not a Digest`);
  if (!('requestIdPath' in family)) fail(`${family.id}: revision 7 requires requestIdPath`);
  if (family.requestIdPath !== null && !isKeyPath(family.requestIdPath)) fail(`${family.id}: requestIdPath is not a KeyPath`);
}
function isKeyPath(value) {
  return Array.isArray(value) && value.length >= 1 && value.length <= 8 && value.every((key) => typeof key === 'string' && /^[A-Za-z][A-Za-z0-9]*$/.test(key) && key.length <= 64);
}

// Domain -> first family (manifest order) that declares the domain. This matches the facade's
// API_DOMAIN_FAMILY table: archive -> sdk2-ext-v1 (not recovery), cache -> sdk2-cache-v1 (not core).
const domainFamily = new Map();
for (const family of manifest.families) for (const domain of family.domains) if (!domainFamily.has(domain)) domainFamily.set(domain, family);
// Domain vocabulary = unified-v1 schema `Domain` enum (same set as route-table.ts ApiDomain); every operation domain must be in it.
const domains = definition('Domain').enum;
if (!Array.isArray(domains) || domains.length === 0) fail('unified-v1.schema.json lacks definitions.Domain.enum');
for (const op of manifest.operations) if (!domains?.includes(op.domain)) fail(`${op.name}: domain ${op.domain} outside the unified vocabulary`);
for (const domain of domains ?? []) if (domain !== 'discovery' && !domainFamily.has(domain)) fail(`domain ${domain} has no family`);

if (validateOnly) {
  if (failures.length) { for (const message of failures) console.error('api-routes: ' + message); process.exit(1); }
  console.log(`api-routes: valid (${manifest.operations.length} operations, revision ${manifest.revision}, sha256 ${manifestSha})`);
  process.exit(0);
}

const literal = (value) => value === null || value === undefined ? 'null' : JSON.stringify(value);
const array = (values) => values.length === 0 ? 'System.Array.Empty<string>()' : `new[] { ${values.map(literal).join(', ')} }`;

const lines = [];
lines.push('// <auto-generated>');
lines.push('// Generated by scripts/generate-api-routes.mjs from contract/api-manifest.json. Do not edit by hand.');
lines.push(`// Manifest: ${manifest.format} contract=${manifest.contract} revision=${manifest.revision}`);
lines.push(`// Manifest schemaHash: ${manifest.schemaHash}`);
lines.push(`// Manifest file sha256: ${manifestSha} (${manifestBytes.length} bytes)`);
lines.push(`// Source: tansr-cli packages/server/contract/api-manifest.json @ ${lock.apiManifest?.sourceRevision ?? 'unknown'}`);
lines.push('// </auto-generated>');
lines.push('#nullable enable');
lines.push('using System.Collections.Generic;');
lines.push('');
lines.push('namespace Tansr.Sdk.Api;');
lines.push('');
lines.push('public static partial class ApiRoutes');
lines.push('{');
lines.push(`    /// <summary>Unified contract identifier carried by every <c>/api</c> response header <c>tansr-contract</c>.</summary>`);
lines.push(`    public const string Contract = ${literal(manifest.contract)};`);
lines.push(`    /// <summary>Revision of the vendored api-manifest; compared against <c>tansr-manifest-revision</c> when the caller asks for strict revision pinning.</summary>`);
lines.push(`    public const int ManifestRevision = ${manifest.revision};`);
lines.push(`    /// <summary>Operation-table hash published by the manifest (<c>schemaHash</c>).</summary>`);
lines.push(`    public const string ManifestSchemaHash = ${literal(manifest.schemaHash)};`);
lines.push(`    /// <summary>SHA-256 of the vendored <c>contract/api-manifest.json</c> bytes.</summary>`);
lines.push(`    public const string ManifestSha256 = ${literal(manifestSha)};`);
lines.push(`    public const int OperationCount = ${manifest.operations.length};`);
lines.push('');
const keyPath = (values) => values === null ? 'null' : array(values);
for (const op of manifest.operations) {
  const member = pascal(op.name);
  const versioned = op.etagPath !== null || op.expectedRevision !== null;
  lines.push(`    /// <summary><c>${op.method} ${op.apiPath}</c> (${op.domain}${op.family ? ', ' + op.family : ''})${versioned ? `; versioned: ETag ← ${op.etagPath === null ? 'none' : op.etagPath.join('.')}, If-Match → ${op.expectedRevision === null ? 'none' : op.expectedRevision.path.join('.') + ' (' + op.expectedRevision.kind + ')'}` : ''}.</summary>`);
  lines.push(`    public static readonly ApiOperation ${member} = new ApiOperation(${literal(op.name)}, ${literal(op.domain)}, ${literal(op.family)}, ${literal(op.method)}, ${literal(op.apiPath)}, ${array(op.aliases)}, ${array(op.query)}, ${op.sse ? 'true' : 'false'}, ${literal(op.kind)}, ${keyPath(op.etagPath)}, ${keyPath(op.expectedRevision?.path ?? null)}, ${literal(op.expectedRevision?.kind ?? null)});`);
}
lines.push('');
lines.push('    /// <summary>All operations in manifest order.</summary>');
lines.push('    public static readonly IReadOnlyList<ApiOperation> All = new ApiOperation[]');
lines.push('    {');
for (const op of manifest.operations) lines.push(`        ${pascal(op.name)},`);
lines.push('    };');
lines.push('');
lines.push('    /// <summary>Domain vocabulary (unified-v1 schema Domain enum; discovery is facade-owned).</summary>');
lines.push(`    public static readonly IReadOnlyList<string> Domains = ${array(domains ?? [])};`);
lines.push('');
lines.push('    /// <summary>Family schema fingerprints as published by the manifest (<c>families[].sha256</c>).</summary>');
lines.push('    public static class Families');
lines.push('    {');
for (const family of manifest.families) {
  lines.push(`        /// <summary>${family.id}: ${family.status}, source ${family.source} (domains ${family.domains.join(', ')}).</summary>`);
  lines.push(`        public const string ${pascal(family.id)} = ${literal(family.sha256)};`);
}
lines.push('    }');
lines.push('');
// revision 7: per-family idempotency-key position (ManifestFamily.requestIdPath). null = the family body has no such
// position (agent-session-v1 / archive-sync-v1 dedupe by request fingerprint); unknown family → null as well.
lines.push('    /// <summary>Body key path the facade fills from <c>Idempotency-Key</c> for a family (<c>families[].requestIdPath</c>, revision 7),');
lines.push('    /// or null when the family body has no such position or the family is unknown.</summary>');
lines.push('    public static IReadOnlyList<string>? FamilyRequestIdPath(string family) => family switch');
lines.push('    {');
for (const family of manifest.families) if (family.requestIdPath !== null) lines.push(`        ${literal(family.id)} => ${array(family.requestIdPath)},`);
lines.push('        _ => null,');
lines.push('    };');
lines.push('');
lines.push('    /// <summary>Primary family for a domain (first manifest family declaring it), or null for unknown domains.</summary>');
lines.push('    public static string? DomainFamily(string domain) => domain switch');
lines.push('    {');
for (const [domain, family] of domainFamily) lines.push(`        ${literal(domain)} => ${literal(family.id)},`);
lines.push('        _ => null,');
lines.push('    };');
lines.push('');
// tansr-schema-hash is compared per domain (手册 §16.4; facade.ts schemaHashOf): discovery responses carry the manifest
// aggregate schemaHash, every other domain carries its primary family's source SHA. The unified-v1 family file SHA is
// still published through Families.UnifiedV1 but is never what the facade puts in the header.
lines.push('    /// <summary>Expected <c>tansr-schema-hash</c> value for a domain, or null when the domain is unknown. <c>discovery</c> is the');
lines.push('    /// manifest aggregate <see cref="ManifestSchemaHash"/>; other domains carry their primary family source SHA (手册 §16.4).</summary>');
lines.push('    public static string? DomainSchemaHash(string domain) => domain switch');
lines.push('    {');
for (const [domain, family] of domainFamily) lines.push(`        ${literal(domain)} => ${domain === 'discovery' ? '"sha256:" + ManifestSchemaHash' : literal('sha256:' + family.sha256)},`);
lines.push('        _ => null,');
lines.push('    };');
lines.push('}');
const output = lines.join('\n') + '\n';

if (check) {
  if (!existsSync(outputPath)) fail(`${outputPath} is missing`);
  else {
    const current = readFileSync(outputPath);
    if (current.length >= 3 && current[0] === 0xef && current[1] === 0xbb && current[2] === 0xbf) fail('generated file must not carry a UTF-8 BOM');
    if (current.toString('utf8') !== output) fail('src/Tansr.Sdk/Api/ApiRoutes.generated.cs differs from the manifest; rerun without --check');
  }
  if (failures.length) { for (const message of failures) console.error('api-routes: ' + message); process.exit(1); }
  console.log(`api-routes: ok (${manifest.operations.length} operations, revision ${manifest.revision}, sha256 ${manifestSha})`);
} else {
  if (failures.length) { for (const message of failures) console.error('api-routes: ' + message); process.exit(1); }
  writeFileSync(outputPath, output);
  console.log(`api-routes: wrote ${outputPath} (${manifest.operations.length} operations)`);
}
