#!/usr/bin/env node
// Single writer for src/Tansr.Sdk/Api/ApiRoutes.generated.cs.
// Source of truth: contract/api-manifest.json (vendored byte-for-byte from tansr-cli
// packages/server/contract/api-manifest.json and locked in contract/manifest.json).
//
//   node scripts/generate-api-routes.mjs          # regenerate the C# module
//   node scripts/generate-api-routes.mjs --check  # verify lock + generated file, exit 1 on drift
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const manifestPath = resolve(root, 'contract/api-manifest.json');
const lockPath = resolve(root, 'contract/manifest.json');
const outputPath = resolve(root, 'src/Tansr.Sdk/Api/ApiRoutes.generated.cs');
const check = process.argv.includes('--check');

const manifestBytes = readFileSync(manifestPath);
const manifestSha = createHash('sha256').update(manifestBytes).digest('hex');
const manifest = JSON.parse(manifestBytes.toString('utf8'));
const lock = JSON.parse(readFileSync(lockPath, 'utf8'));

const failures = [];
const fail = (message) => failures.push(message);

if (manifest.format !== 'tansr-api-manifest-v1') fail(`unexpected manifest format ${manifest.format}`);
if (manifest.contract !== 'unified-v1') fail(`unexpected manifest contract ${manifest.contract}`);
if (!Number.isInteger(manifest.revision) || manifest.revision < 1) fail('manifest revision must be a positive integer');
if (!/^[0-9a-f]{64}$/.test(manifest.schemaHash ?? '')) fail('manifest schemaHash must be 64 hex chars');

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

// Placeholder vocabulary is closed; ApiOperation.Path exposes exactly these named parameters.
const allowedPlaceholders = ['id', 'targetId', 'uploadId', 'ticketId'];
const placeholdersOf = (template) => [...template.matchAll(/:([A-Za-z][A-Za-z0-9]*)/g)].map((m) => m[1]);
const pascal = (name) => name.split(/[.\-_]/).filter(Boolean).map((part) => part[0].toUpperCase() + part.slice(1)).join('');
const names = new Set();
for (const op of manifest.operations) {
  if (!op.apiPath.startsWith('/api/') && op.apiPath !== '/api') fail(`${op.name}: apiPath ${op.apiPath} is outside /api`);
  for (const template of [op.apiPath, ...op.aliases]) {
    for (const placeholder of placeholdersOf(template)) {
      if (!allowedPlaceholders.includes(placeholder)) fail(`${op.name}: placeholder :${placeholder} is not in the closed vocabulary`);
    }
  }
  const member = pascal(op.name);
  if (names.has(member)) fail(`${op.name}: duplicate C# member name ${member}`);
  names.add(member);
  if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].includes(op.method)) fail(`${op.name}: method ${op.method}`);
  if (!['read', 'write', 'delete', 'stream'].includes(op.kind)) fail(`${op.name}: kind ${op.kind}`);
  if ((op.kind === 'stream') !== (op.sse === true)) fail(`${op.name}: kind/sse disagree`);
}

// Domain -> first family (manifest order) that declares the domain. This matches the facade's
// API_DOMAIN_FAMILY table: archive -> sdk2-ext-v1 (not recovery), cache -> sdk2-cache-v1 (not core).
const domainFamily = new Map();
for (const family of manifest.families) for (const domain of family.domains) if (!domainFamily.has(domain)) domainFamily.set(domain, family);
// Domain vocabulary = unified-v1 schema `Domain` enum (same set as route-table.ts ApiDomain); every operation domain must be in it.
const unifiedSchema = JSON.parse(readFileSync(resolve(root, 'contract/unified-v1.schema.json'), 'utf8'));
const domains = unifiedSchema.definitions?.Domain?.enum;
if (!Array.isArray(domains) || domains.length === 0) fail('unified-v1.schema.json lacks definitions.Domain.enum');
for (const op of manifest.operations) if (!domains?.includes(op.domain)) fail(`${op.name}: domain ${op.domain} outside the unified vocabulary`);
for (const domain of domains ?? []) if (domain !== 'discovery' && !domainFamily.has(domain)) fail(`domain ${domain} has no family`);

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
for (const op of manifest.operations) {
  const member = pascal(op.name);
  lines.push(`    /// <summary><c>${op.method} ${op.apiPath}</c> (${op.domain}${op.family ? ', ' + op.family : ''}).</summary>`);
  lines.push(`    public static readonly ApiOperation ${member} = new ApiOperation(${literal(op.name)}, ${literal(op.domain)}, ${literal(op.family)}, ${literal(op.method)}, ${literal(op.apiPath)}, ${array(op.aliases)}, ${array(op.query)}, ${op.sse ? 'true' : 'false'}, ${literal(op.kind)});`);
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
