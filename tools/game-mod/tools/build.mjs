#!/usr/bin/env node
// Builds the patched game script for a version already in versions/<hash>/index.js:
// unpack (the outer base64 + zstd packer) -> webcrack (the obfuscator) -> patches -> versions/<hash>/patched.js.
//   node tools/build.mjs HASH              the whole pipeline (webcrack takes a few minutes)
//   node tools/build.mjs HASH --repatch    only re-apply patches/ to the deobfuscated stage (after editing a patch)
// update.mjs downloads a new version and runs this; run it yourself after changing patches.
import { readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { unpack } from './unpack.mjs';
import { applyPatches } from './patch.mjs';
import { patches } from '../patches/index.mjs';
import { versionDir } from './versions.mjs';

export async function build(index, { repatch = false } = {}) {
  const dir = versionDir(index);
  const path = (name) => join(dir, name);
  const original = readFileSync(path('index.js'), 'utf8');
  let deobfuscated;
  if (repatch) {
    deobfuscated = readFileSync(path('deobfuscated.js'), 'utf8');
  } else {
    console.log(`[1/3] unpacking ${index}`);
    const { code: unpacked, packed } = unpack(original);
    console.log(packed ? `      ${unpacked.length} bytes of inner script` : '      no packer found, using the file as it is');
    writeFileSync(path('unpacked.js'), unpacked);
    console.log('[2/3] deobfuscating with webcrack (a few minutes)');
    const { webcrack } = await import('webcrack');
    // unpack: false keeps the webpack bundle as one file; jsx: false keeps the output plain JS
    ({ code: deobfuscated } = await webcrack(unpacked, { unpack: false, jsx: false }));
    writeFileSync(path('deobfuscated.js'), deobfuscated);
  }
  console.log('[3/3] applying patches');
  const sha256 = createHash('sha256').update(original).digest('hex');
  const { code: patched, report } = applyPatches(deobfuscated, patches, { sourceName: `index.${index}.js`, sha256 });
  for (const r of report) console.log(`      ${r.name}: ${r.edits} edit(s)`);
  writeFileSync(path('patched.js'), patched);
  writeFileSync(path('build-info.json'), JSON.stringify({ index, sha256, patches: report }, null, 2) + '\n');
  console.log(`done: ${path('patched.js')}`);
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const [index, flag] = process.argv.slice(2);
  if (!index) { console.error('usage: node tools/build.mjs HASH [--repatch]'); process.exit(2); }
  await build(index, { repatch: flag === '--repatch' });
}
