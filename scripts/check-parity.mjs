import { existsSync, readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, dirname, relative, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const mappingPath = resolve(repository, 'doc/compatibility/public-api-map.json');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const need = (condition, message) => { if (!condition) throw new Error(message); };

// 足够覆盖锁定入口语法的词法读取，字符串/注释不会生成伪出口。
// 新的export语法须明确扩展校验器；不能静默跳过export * / default。
export function tokenize(text) {
  const tokens = []; let index = 0, line = 1;
  while (index < text.length) {
    const c = text[index], next = text[index + 1];
    if (/\s/.test(c)) { if (c === '\n') line++; index++; continue; }
    if (c === '/' && next === '/') { while (index < text.length && text[index] !== '\n') index++; continue; }
    if (c === '/' && next === '*') {
      index += 2;
      while (index < text.length && !(text[index] === '*' && text[index + 1] === '/')) { if (text[index] === '\n') line++; index++; }
      need(index < text.length, 'Unclosed source comment'); index += 2; continue;
    }
    const startLine = line;
    if (c === '"' || c === "'" || c === '`') {
      let value = ''; index++;
      while (index < text.length && text[index] !== c) {
        if (text[index] === '\\') { value += text[index++]; need(index < text.length, 'Unclosed source escape'); }
        if (text[index] === '\n') line++;
        value += text[index++];
      }
      need(index < text.length, 'Unclosed source literal'); index++;
      tokens.push({ value, kind: 'literal', line: startLine }); continue;
    }
    if (/[A-Za-z_$]/.test(c)) {
      const start = index++;
      while (index < text.length && /[A-Za-z0-9_$]/.test(text[index])) index++;
      tokens.push({ value: text.slice(start, index), kind: 'name', line: startLine }); continue;
    }
    tokens.push({ value: c, kind: 'punctuation', line: startLine }); index++;
  }
  return tokens;
}

export function extractExports(text) {
  const tokens = tokenize(text), result = [];
  for (let i = 0; i < tokens.length; i++) {
    if (tokens[i].kind !== 'name' || tokens[i].value !== 'export') continue;
    const line = tokens[i].line; let j = i + 1, type = false;
    if (tokens[j]?.value === 'type') { type = true; j++; }
    if (tokens[j]?.value === '{') {
      j++; const entries = [];
      while (tokens[j]?.value !== '}') {
        if (tokens[j]?.value === ',') { j++; continue; }
        let entryType = type;
        if (tokens[j]?.value === 'type') { entryType = true; j++; }
        need(tokens[j]?.kind === 'name', 'Unsupported named export');
        const imported = tokens[j++].value; let name = imported;
        if (tokens[j]?.value === 'as') { j++; need(tokens[j]?.kind === 'name', 'Unsupported export alias'); name = tokens[j++].value; }
        entries.push({ name, imported, kind: entryType ? 'type' : 'value', line });
        need(tokens[j]?.value === ',' || tokens[j]?.value === '}', 'Unsupported export separator');
      }
      j++; let module = null;
      if (tokens[j]?.value === 'from') { j++; need(tokens[j]?.kind === 'literal', 'Unsupported module specifier'); module = tokens[j++].value; }
      for (const entry of entries) result.push({ ...entry, module });
      i = j - 1;
    } else {
      if (tokens[j]?.value === 'async' || tokens[j]?.value === 'declare') j++;
      const declaration = type ? 'type' : tokens[j++]?.value;
      need(['function', 'class', 'interface', 'const', 'let', 'enum', 'type'].includes(declaration), 'Unaccounted export syntax at line ' + line);
      need(tokens[j]?.kind === 'name', 'Missing export name at line ' + line);
      const name = tokens[j].value;
      result.push({ name, imported: name, kind: type || declaration === 'interface' ? 'type' : 'value', line, module: null });
    }
  }
  need(new Set(result.map(x => x.name)).size === result.length, 'Duplicate public export');
  return result;
}

export function extractPreload(text) {
  const tokens = tokenize(text), result = [];
  let start = tokens.findIndex(x => x.kind === 'name' && x.value === 'exposeInMainWorld');
  need(start >= 0, 'Electron public bridge missing');
  while (tokens[start]?.value !== '{') { need(start < tokens.length, 'Bridge object missing'); start++; }
  let level = 1;
  for (let i = start + 1; i < tokens.length && level; i++) {
    const token = tokens[i];
    if (level === 1 && token.kind === 'name' && tokens[i + 1]?.value === ':') result.push({ name: token.value, line: token.line, kind: 'electron-bridge', module: null });
    if (['{', '(', '['].includes(token.value) && token.kind === 'punctuation') level++;
    if (['}', ')', ']'].includes(token.value) && token.kind === 'punctuation') level--;
  }
  need(new Set(result.map(x => x.name)).size === result.length, 'Duplicate Electron bridge');
  return result;
}

export function extractCapabilityRows(text) {
  const lines = text.split(/\r?\n/), result = []; let table = false;
  for (let i = 0; i < lines.length; i++) {
    if (lines[i] === '| 能力 | 装配缝 | UI 入口 / 可见面 |') { table = true; continue; }
    if (!table) continue;
    if (!lines[i].startsWith('|')) break;
    if (/^\|[-| ]+\|$/.test(lines[i])) continue;
    const name = lines[i].split('|')[1].trim();
    need(name, 'Empty capability row'); result.push({ name, line: i + 1, kind: 'electron-capability', module: null });
  }
  need(result.length > 0, 'Electron capability baseline missing'); return result;
}

// The frozen SDK class/interface declarations use one public member per two-space
// source line. Braces/parameters/literals are tokenized so method bodies and nested
// object types do not invent public members. Source hashes still fail closed on
// any upstream syntax change before a reviewed inventory can be accepted.
export function extractMembers(text, name, kind = 'class') {
  const tokens = tokenize(text), lines = text.split(/\r?\n/), result = [];
  let start = tokens.findIndex((token, index) => token.value === kind && tokens[index + 1]?.value === name);
  need(start >= 0, 'Public declaration missing: ' + name);
  while (tokens[start]?.value !== '{') { need(start < tokens.length, 'Declaration body missing: ' + name); start++; }
  let braces = 1, parentheses = 0, brackets = 0, lastLine = -1;
  for (let i = start + 1; i < tokens.length && braces; i++) {
    const token = tokens[i];
    if (braces === 1 && parentheses === 0 && brackets === 0 && token.line !== lastLine && /^  \S/.test(lines[token.line - 1])) {
      lastLine = token.line;
      let next = i;
      while (['public', 'readonly', 'async', 'static', 'override', 'declare', 'abstract', 'get', 'set'].includes(tokens[next]?.value)) next++;
      const member = tokens[next];
      if (member?.kind === 'name' && !['private', 'protected', 'constructor'].includes(member.value) &&
          ['(', '<', ':', '?', '=', ';'].includes(tokens[next + 1]?.value))
        result.push({ name: name + '.' + member.value, line: token.line });
    }
    if (token.kind !== 'punctuation') continue;
    if (token.value === '{') braces++; else if (token.value === '}') braces--;
    else if (token.value === '(') parentheses++; else if (token.value === ')') parentheses--;
    else if (token.value === '[') brackets++; else if (token.value === ']') brackets--;
  }
  need(result.length > 0, 'Empty member inventory: ' + name);
  need(new Set(result.map(item => item.name)).size === result.length, 'Duplicate member inventory: ' + name);
  return result;
}

export function extractInvocations(text, names) {
  const tokens = tokenize(text), result = [];
  for (const name of names) {
    const index = tokens.findIndex((token, offset) => token.kind === 'name' && token.value === name && tokens[offset + 1]?.value === '(');
    need(index >= 0, 'Advanced SDK invocation missing: ' + name);
    result.push({ name, line: tokens[index].line });
  }
  return result;
}

function inside(root, path) {
  const value = resolve(root, path), rel = relative(root, value);
  need(!isAbsolute(rel) && rel !== '..' && !rel.startsWith('../') && !rel.startsWith('..\\'), 'Source escapes root');
  return value;
}

export function check(mapping, sourceRoot) {
  need(mapping.format === 'tansr-net-public-api-map-v1', 'Unknown mapping format');
  need(['coverage-only-not-behavior-acceptance', 'reviewed-behavior-evidence'].includes(mapping.evidenceStatus), 'Unknown evidence status');
  need(mapping.groups.length === 16 && new Set(mapping.groups.map(g => g.id)).size === 16, 'The fixed 16 groups must remain intact');
  need(mapping.groups.every((g, i) => g.id === 'P' + String(i + 1).padStart(2, '0') && typeof g.complete === 'boolean'), 'The fixed group identities must remain intact');
  const groups = new Set(mapping.groups.map(g => g.id)), sources = new Map(mapping.sources.map(s => [s.path, s]));
  const evidence = new Map();
  for (const item of mapping.behaviorEvidence ?? []) {
    need(typeof item.id === 'string' && !evidence.has(item.id), 'Duplicate behavior evidence');
    need(['passed', 'ready-not-run'].includes(item.status) && item.behavior?.length && item.command?.length && item.assertions?.length && item.files?.length, 'Incomplete behavior evidence: ' + item.id);
    for (const file of item.files) {
      const content = readFileSync(inside(repository, file.path), 'utf8');
      need(file.anchor?.length && content.includes(file.anchor), 'Behavior entry missing: ' + file.path + '#' + file.anchor);
    }
    if (item.status === 'passed') need(item.receipt?.length && item.sourceRevision?.length, 'Passed behavior lacks source/receipt: ' + item.id);
    evidence.set(item.id, item);
  }
  const verifyEvidence = item => {
    need(Array.isArray(item.evidence), 'Missing evidence list: ' + item.id);
    for (const id of item.evidence) need(evidence.has(id), 'Unknown behavior evidence: ' + id);
    if (item.complete || item.status === 'verified') {
      need(item.evidence.length > 0 && item.evidence.every(id => evidence.get(id).status === 'passed'), 'Cannot close with unrun evidence: ' + item.id);
      need(item.remaining === '' && item.acceptance?.length, 'Closed item lacks complete acceptance conditions: ' + item.id);
    } else need(item.remaining?.length, 'Open item needs a concrete remaining condition: ' + item.id);
  };
  for (const group of mapping.groups) {
    verifyEvidence(group);
    for (const file of group.netSources) need(existsSync(inside(repository, file)), 'Mapped implementation missing: ' + file);
  }
  const ids = new Set();
  for (const entry of mapping.entries) {
    need(!ids.has(entry.id), 'Duplicate mapping: ' + entry.id); ids.add(entry.id);
    need(groups.has(entry.group) && sources.has(entry.source), 'Unknown group/source: ' + entry.id);
    need(['pending', 'partial-unverified', 'serve-dependency', 'verified'].includes(entry.status), 'Unknown behavior status: ' + entry.id);
    need(entry.net?.length && Array.isArray(entry.serveDependencies), 'Missing behavior/gap: ' + entry.id);
    verifyEvidence(entry);
  }
  for (const group of mapping.groups) {
    const entries = mapping.entries.filter(e => e.group === group.id);
    need(entries.length > 0, 'Empty capability group: ' + group.id);
    if (group.complete) need(entries.every(entry => entry.status === 'verified'), 'Group has open behavior entries: ' + group.id);
  }
  let enumerated = 0;
  for (const source of mapping.sources) {
    const frozen = readFileSync(inside(repository, source.snapshot));
    need(sha(frozen) === source.sha256 && frozen.length === source.bytes, 'Frozen source changed: ' + source.path);
    const bytes = sourceRoot ? readFileSync(inside(sourceRoot, source.path)) : frozen;
    const extract = { exports: extractExports, 'electron-bridge': extractPreload, 'electron-capabilities': extractCapabilityRows }[source.extract];
    if (extract) {
      const entries = extract(bytes.toString('utf8'));
      const expected = mapping.entries.filter(e => e.source === source.path && e.inventory === source.extract);
      const actualNames = new Set(entries.map(e => e.name));
      for (const entry of entries) need(expected.some(e => e.symbol === entry.name), 'New public entry has no mapping: ' + source.path + '::' + entry.name);
      for (const entry of expected) need(actualNames.has(entry.symbol), 'Mapped public entry vanished: ' + entry.id);
      need(expected.length === entries.length, 'Public entry count differs: ' + source.path);
      enumerated += entries.length;
    }
    for (const spec of source.members ?? []) {
      const actual = extractMembers(bytes.toString('utf8'), spec.name, spec.kind);
      const expected = mapping.entries.filter(e => e.source === source.path && e.inventory === spec.inventory);
      for (const member of actual) need(expected.some(entry => entry.symbol === member.name), 'New public member has no mapping: ' + member.name);
      need(expected.length === actual.length && expected.every(entry => actual.some(member => member.name === entry.symbol)), 'Mapped public member vanished: ' + source.path);
      enumerated += actual.length;
    }
    if (source.invocations) {
      const actual = extractInvocations(bytes.toString('utf8'), source.invocations);
      const expected = mapping.entries.filter(e => e.source === source.path && e.inventory === 'advanced-entry');
      need(expected.length === actual.length && actual.every(item => expected.some(entry => entry.symbol === item.name)), 'Advanced SDK invocation has no mapping: ' + source.path);
      enumerated += actual.length;
    }
    need(sha(bytes) === source.sha256, 'Upstream source changed; refresh reviewed mapping/evidence: ' + source.path);
  }
  const pkgSource = sources.get('packages/sdk/package.json');
  const pkg = JSON.parse(readFileSync(inside(repository, pkgSource.snapshot), 'utf8'));
  need(JSON.stringify(Object.keys(pkg.publishConfig.exports).sort()) === JSON.stringify(mapping.publishedEntrypoints.slice().sort()), 'Published SDK entrypoint changed');
  return { entries: mapping.entries.length, enumerated, groups: mapping.groups.length, sourceFiles: mapping.sources.length,
    acceptedBehavior: mapping.groups.filter(group => group.complete).length, referencedEvidence: evidence.size };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2); let sourceRoot;
    if (args.length) { need(args.length === 2 && args[0] === '--source-root', 'Usage: node scripts/check-parity.mjs [--source-root <tansr-cli>]'); sourceRoot = resolve(args[1]); }
    const mapping = JSON.parse(readFileSync(mappingPath, 'utf8'));
    console.log(JSON.stringify(check(mapping, sourceRoot)));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
