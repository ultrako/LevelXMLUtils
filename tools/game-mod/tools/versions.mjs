// Where game versions are kept. A version is the game's index script, named by its hash; versions/<hash>/ holds the
// original (index.js), the unpacked and deobfuscated stages, the patched script the server serves, and build-info.json.
// versions/pinned.json is the pair of scripts the local server plays ({ index, dependencies }, as the site's
// version.json writes them): the game only changes when you run update.mjs.
import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

export const SITE = 'https://totaljerkface.com';
export const VERSIONS = new URL('../versions/', import.meta.url).pathname;
export const CACHE = new URL('../cache/', import.meta.url).pathname;
// Node's default user agent is fine with the site, but say what is asking
export const AGENT = { 'user-agent': 'LevelXMLUtils game-mod (local level server)' };

export const versionDir = (index) => join(VERSIONS, index);
export const patchedPath = (index) => join(versionDir(index), 'patched.js');

export function readPinned() {
  const path = join(VERSIONS, 'pinned.json');
  return existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : null;
}

export function writePinned(pair) {
  mkdirSync(VERSIONS, { recursive: true });
  writeFileSync(join(VERSIONS, 'pinned.json'), JSON.stringify(pair, null, 2) + '\n');
}

export async function siteVersion() {
  const res = await fetch(`${SITE}/happy-wheels-js/version.json`, { headers: AGENT });
  if (!res.ok) throw new Error(`version.json: HTTP ${res.status}`);
  return res.json();
}
