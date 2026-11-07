#!/usr/bin/env node
// Takes a game update, when you choose to: the local server keeps playing the pinned version until you run this.
//   node tools/update.mjs            pin the site's current version, downloading and building it if it's new
//   node tools/update.mjs --check    only say whether the site has a version newer than the pinned one
//   node tools/update.mjs --pin HASH go back to a version built before (with the dependencies it was pinned with)
// A game update often changes only the dependencies script (shared code and the asset list): then nothing is rebuilt.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { build } from './build.mjs';
import { AGENT, SITE, patchedPath, readPinned, siteVersion, versionDir, writePinned } from './versions.mjs';

const args = process.argv.slice(2);
const pinned = readPinned();

if (args[0] === '--pin') {
  const index = args[1];
  const info = join(versionDir(index ?? ''), 'pair.json');
  if (!index || !existsSync(patchedPath(index)) || !existsSync(info)) { console.error(`no built version ${index}`); process.exit(1); }
  writePinned(JSON.parse(readFileSync(info, 'utf8')));
  console.log(`pinned ${index}`);
  process.exit(0);
}

const site = await siteVersion();
const same = pinned && pinned.index === site.index && pinned.dependencies === site.dependencies;
console.log(`pinned: ${pinned ? `index ${pinned.index}, dependencies ${pinned.dependencies}` : 'nothing yet'}`);
console.log(`site:   index ${site.index}, dependencies ${site.dependencies}${same ? ' (the same)' : ''}`);
if (args[0] === '--check' || same) process.exit(0);

if (!existsSync(patchedPath(site.index))) {
  mkdirSync(versionDir(site.index), { recursive: true });
  const res = await fetch(`${SITE}/happy-wheels-js/js/index.${site.index}.js`, { headers: AGENT });
  if (!res.ok) throw new Error(`index.${site.index}.js: HTTP ${res.status}`);
  writeFileSync(join(versionDir(site.index), 'index.js'), Buffer.from(await res.arrayBuffer()));
  await build(site.index);
} else {
  console.log(`index ${site.index} is built already; only the dependencies script changed`);
}
writeFileSync(join(versionDir(site.index), 'pair.json'), JSON.stringify(site, null, 2) + '\n');
writePinned(site);
console.log(`pinned index ${site.index}, dependencies ${site.dependencies}`);
