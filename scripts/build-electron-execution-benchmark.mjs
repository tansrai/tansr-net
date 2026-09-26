// Acceptance-only: bundle the current complete Electron app and SDK; old archives supply runtime only.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const [sourceArg, outputArg] = process.argv.slice(2);
assert.ok(sourceArg && outputArg, 'usage: node build-electron-execution-benchmark.mjs CLI_SOURCE ARCHIVE_CANDIDATE');
const source = resolve(sourceArg), output = resolve(outputArg);
assert.ok(!output.startsWith(source + '/'), 'Output must be in the task archive, never the source checkout.');
const example = join(source, 'examples/electron-chat');
const require = createRequire(join(source, 'package.json'));
const esbuild = require('esbuild');
await mkdir(join(output, 'dist'), { recursive: true });
const entry = resolve(dirname(fileURLToPath(import.meta.url)), '../tests/Tansr.Sdk.Windows.Tests/Execution/electron-execution-benchmark.mjs');
const common = { absWorkingDir: source, bundle: true, platform: 'node', target: 'node22', loader: { '.md': 'text', '.txt': 'text' },
  external: ['electron'], metafile: true, alias: { '@tansr/sdk': join(source, 'packages/sdk/src/index.ts'),
    '@tansr-benchmark/app': join(example, 'src/app.ts') } };
const built = await esbuild.build({ ...common, entryPoints: [entry], format: 'esm', outfile: join(output, 'dist/benchmark.mjs'),
  banner: { js: "import {createRequire as __createRequire} from 'node:module';const require=__createRequire(import.meta.url);import {fileURLToPath as __fileURLToPath} from 'node:url';import {dirname as __dirnameOf} from 'node:path';const __filename=__fileURLToPath(import.meta.url);const __dirname=__dirnameOf(__filename);" } });
const preload = await esbuild.build({ ...common, entryPoints: [join(example, 'src/preload.ts')], format: 'cjs', outfile: join(output, 'dist/preload.cjs') });
await cp(join(example, 'renderer'), join(output, 'renderer'), { recursive: true, errorOnExist: true });
await cp(join(example, 'assets'), join(output, 'assets'), { recursive: true, errorOnExist: true });
const inputs = [...new Set([...Object.keys(built.metafile.inputs), ...Object.keys(preload.metafile.inputs)])].sort();
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const files = await Promise.all(inputs.map(async path => ({ path: resolve(source, path), sha256: sha256(await readFile(resolve(source, path))) })));
for (const path of ['renderer/index.html', 'renderer/renderer.js', 'renderer/style.css']) files.push({ path: join(example, path), sha256: sha256(await readFile(join(example, path))) });
await writeFile(join(output, 'source-manifest.json'), JSON.stringify({ source, kind: 'original-complete-electron-sdk-ipc', files,
  entry: join(output, 'dist/benchmark.mjs'), entrySha256: sha256(await readFile(join(output, 'dist/benchmark.mjs'))) }, null, 2) + '\n');
console.log(join(output, 'dist/benchmark.mjs'));
