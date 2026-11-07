#!/usr/bin/env node
// Finds published levels the game's level browser can't filter for: lists levels the way the browser does (get_level.hw
// get_all / search_by_name / search_by_user / get_featured), drops the ones whose name or description matches a blacklist,
// downloads the rest (once; they are cached), and counts each one the way the level editor does, so you can sort and
// filter on trigger count, on being over the editor's shape (900) or art (10000) limit, and on looking like a controls level.
//
//   node --openssl-legacy-provider tools/level-search.mjs [list options] [filters] [--out FILE.tsv]
//   node tools/level-search.mjs --analyze A.xml B.xml ...     count level XML files, no network
//   node tools/level-search.mjs --dump ID                     print a cached level's XML
//   node tools/level-search.mjs --sqlite happywheels.sqlite3 [--limit N] [--sort rating|plays|newest|oldest|id] [filters]
//       search the offline archive (every level up to 2018-11-26; see searchArchive) instead of the server: no requests,
//       no quota. --sort rating counts only levels with 60+ votes unless --min-votes says otherwise.
//       --interesting keeps over-limit, controls, or 200+ trigger levels (--min-triggers to change); --resume (with --out)
//       goes in id order, appends matches as found and can be stopped and rerun to carry on (OUT.checkpoint).
//
// List (one of; default --sort rating --uploaded week):
//   --sort newest|oldest|plays|rating  --uploaded today|week|month|anytime  --pages N (500 levels a page, default 1)
//   --search NAME     levels whose NAME STARTS with this (the server matches the start of the whole name, not words)
//   --author NAME     levels by this author          --featured   the featured list
//   --authors "A,B,C"  every level by these authors (one list request each, cached a day)    --since YYYY-MM-DD  only newer levels
// Before downloading (from the list data alone):
//   --exclude REGEX   drop levels whose name or description matches (case-insensitive; repeat for more)
//   --exclude-name REGEX   the same, on the name only
//   --list-only       print the list entries that pass these filters (with descriptions) and download nothing
//   --append          with --list-only and --out: add only levels not already in the file (build one table over many runs)
//   --fresh           refetch list pages (they are cached for a day)
//   --daily-budget N  requests allowed in any 24 hours, counted across all tools and your play through serve.mjs (default
//                     300, or HW_DAILY_BUDGET; see hw-quota.mjs). `node tools/hw-quota.mjs` shows what is left.
//   --min-plays N  --min-votes N  --min-rating X  --max-downloads N (default 150: the server allows a few hundred requests a day)
// After downloading:
//   --over-limit      only levels the editor would refuse to save (over 900 shapes or 10000 art, or a NaN count)
//   --controls        only levels that look like controls levels (2+ jets and 4+ tiny contact sensors: a heuristic)
//   --min-triggers N
// Politeness: one request at a time, --wait SECONDS between them (default 4; at 1.5 the server answered HTTP 429 after
// about 185 downloads), waits out a 429 (Retry-After, else 1 minute doubling to 15), stops after 3 other failures in a row,
// downloads ask the server NOT to count a play (the game's ip_tracking ends in an odd digit for a counted play, even
// for not), and every level is cached in --cache DIR (default ~/.cache/hw-levels) so a rerun downloads nothing twice.
//
// The editor's count (from the game script): every trigger 1; an interactive shape 1, a non-interactive one (and every
// art shape) 1 art; specials cost their parts (an NPC 24, a glass panel 16, a chain 1 a link, ...) when interactive,
// and art when not; a group the sum of what it holds; joints nothing. A NaN chain makes the editor's count NaN, which
// never compares as over the limit: the known bypass.
import https from 'node:https';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createDecipheriv, createHash } from 'node:crypto';
import { inflateSync } from 'node:zlib';
import { spawnSync } from 'node:child_process';
import { guard, noteResponse, QuotaError, describe, status, DEFAULT_BUDGET } from './hw-quota.mjs';

const argv = process.argv.slice(2);
const flags = { exclude: [], excludeName: [], analyze: [] };
for (let i = 0; i < argv.length; i++) {
  const a = argv[i];
  if (!a.startsWith('--')) { flags.analyze.push(a); continue; }
  const key = a.slice(2), next = argv[i + 1];
  if (key === 'analyze') continue; // the files that follow are collected above
  if (['over-limit', 'controls', 'featured', 'list-only', 'interesting', 'resume', 'append'].includes(key)) { flags[key] = true; continue; }
  if (key === 'exclude') { flags.exclude.push(new RegExp(next, 'i')); i++; continue; }
  if (key === 'exclude-name') { flags.excludeName.push(new RegExp(next, 'i')); i++; continue; }
  flags[key] = next; i++;
}

// Blowfish is a legacy cipher in OpenSSL 3; rerun ourselves with the legacy provider (as save-level.mjs does).
if (!flags.analyze.length && !process.execArgv.includes('--openssl-legacy-provider') && !process.env.LEVEL_SEARCH_CHILD) {
  const r = spawnSync(process.execPath, ['--openssl-legacy-provider', '--disable-warning=ExperimentalWarning', ...process.argv.slice(1)], { stdio: 'inherit', env: { ...process.env, LEVEL_SEARCH_CHILD: '1' } });
  process.exit(r.status ?? 1);
}

const SITE = 'https://totaljerkface.com';
const cacheDir = flags.cache ?? path.join(os.homedir(), '.cache', 'hw-levels');
const wait = Number(flags.wait ?? 4) * 1000;
const budget = Number(flags['daily-budget'] ?? DEFAULT_BUDGET);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- counting a level the way the editor does ----

// Special type (the list index the game uses) -> [shapes when interactive, the p attribute that says interactive (null:
// always interactive), art when not]. From the game's ...Ref classes and their _attributes lists.
const SPECIALS = {
  0: [3, 4], 1: [4, 4], 2: [2], 3: [1], 4: [2], 5: [4], 6: [2], 7: [3], 8: [3], 9: [1], 10: [1], 11: [1], 12: [1], 13: [1], 14: [1],
  16: [0], 18: [16], 19: [4, 5], 20: [1, 5], 21: [3, 4], 22: [2, 4], 24: [5, 5], 25: [4], 26: [13, 4], 27: [2], 28: [1], 29: [23],
  31: [1], 32: [3, 4], 33: [0], 34: [2, 5], 35: [3],
};

export function analyze(xml) {
  const text = xml.replace(/<!\[CDATA\[[\s\S]*?\]\]>/g, '');
  const attr = (s, k) => { const m = new RegExp(`(?:^|\\s)${k}="([^"]*)"`).exec(s); return m ? m[1] : undefined; };
  const info = /<info\b([^>]*)>/.exec(text)?.[1] ?? '';
  const r = {
    version: attr(info, 'v') ?? '', character: Number(attr(info, 'c') ?? 0), forced: attr(info, 'f') === 't', hiddenVehicle: attr(info, 'h') === 't',
    shapes: 0, art: 0, triggers: 0, interactiveShapes: 0, artShapes: 0, specials: 0, groups: 0, joints: 0, jets: 0, smallSensors: 0,
    npcs: 0, chainLinks: 0, nanChain: false, xmlBytes: Buffer.byteLength(xml),
  };
  const stack = [];
  for (const m of text.matchAll(/<(\/?)([A-Za-z]+)([^>]*?)(\/?)>/g)) {
    const [, close, name, rest, selfClose] = m;
    if (close) { stack.pop(); continue; }
    const parent = stack[stack.length - 1];
    if (name === 'sh' && (parent === 'shapes' || parent === 'g')) {
      const t = attr(rest, 't'), interactive = attr(rest, 'i') !== 'f' && t !== '4';
      if (interactive) { r.shapes++; r.interactiveShapes++; } else { r.art++; r.artShapes++; }
    } else if (name === 'sp' && (parent === 'specials' || parent === 'g')) {
      r.specials++;
      const t = Number(attr(rest, 't')), p = (k) => attr(rest, `p${k}`);
      if (t === 30) {
        const links = Number(p(5) ?? 20);
        if (!Number.isFinite(links)) r.nanChain = true;
        if (p(4) === 'f') r.art += links; else r.shapes += links;
        r.chainLinks += Number.isFinite(links) ? links : 0;
      } else if (t === 17) {
        r.npcs++;
        if (p(7) === 'f') r.art += 10; else r.shapes += 24;
      } else if (t === 15) {
        r.shapes += p(3) === 'f' ? 4 : 11;
      } else if (t === 23) {
        r.art += 1;
      } else if (SPECIALS[t]) {
        const [cost, interactiveAttr] = SPECIALS[t];
        if (interactiveAttr !== undefined && p(interactiveAttr) === 'f') r.art += 1; else r.shapes += cost;
        if (t === 28) r.jets++;
      }
    } else if (name === 'g' && (parent === 'groups')) {
      r.groups++;
    } else if (name === 'j' && parent === 'joints') {
      r.joints++;
    } else if (name === 't' && parent === 'triggers') {
      r.triggers++; r.shapes++;
      const b = attr(rest, 'b'), w = Number(attr(rest, 'w') ?? 0), h = Number(attr(rest, 'h') ?? 0);
      // a contact sensor (not triggered by other triggers) no bigger than 10 by 10: what controls levels read keys with
      if (b !== '5' && w <= 10 && h <= 10) r.smallSensors++;
    }
    if (!selfClose) stack.push(name);
  }
  r.overShapes = Number.isNaN(r.shapes) || r.shapes > 900;
  r.overArt = r.art > 10000;
  r.overLimit = r.nanChain || r.shapes > 900 || r.art > 10000;
  // Controls levels in the NassingerControls style read keys with the vehicle's own jets (Space, Shift, Ctrl) and paddles
  // touching tiny contact sensors; the vehicle may be hidden by the level (h="t") or by the level's own art (SeatedControls).
  r.controls = r.jets >= 2 && r.smallSensors >= 4;
  return r;
}

// ---- talking to the server (the requests the game's level browser makes) ----

function post(endpoint, body, binary = false) {
  return new Promise((resolve, reject) => {
    // The shared daily ledger (hw-quota.mjs) refuses a request over the budget or during a block, before anything is sent.
    try { guard('level-search', `${endpoint} ${body.slice(0, 60)}`, budget); } catch (err) { return reject(err); }
    const buf = Buffer.from(body);
    const req = https.request(`${SITE}/${endpoint}`, {
      method: 'POST', timeout: 120000,
      headers: { 'content-type': 'application/x-www-form-urlencoded', 'content-length': buf.length, origin: SITE, referer: `${SITE}/happy_wheels.tjf`, 'user-agent': 'hw-level-search (a personal level finder; one request at a time)' },
    }, (res) => {
      const chunks = []; res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const data = Buffer.concat(chunks);
        noteResponse(res.statusCode, res.headers['retry-after']);
        if (res.statusCode !== 200) {
          const err = new Error(`HTTP ${res.statusCode}`);
          err.status = res.statusCode;
          err.retryAfter = Number(res.headers['retry-after']) || undefined;
          return reject(err);
        }
        resolve(binary ? data : data.toString('latin1'));
      });
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
    req.end(buf);
  });
}

// Form encoding, as the game sends it: a space must be '+' (with %20 the server's name and author search find nothing).
const field = (s) => encodeURIComponent(s).replace(/%20/g, '+');
function parseList(text) {
  return [...text.matchAll(/<lv ([^>]*?)(?:\/>|>([\s\S]*?)<\/lv>)/g)].map((m) => {
    const a = Object.fromEntries([...m[1].matchAll(/(\w+)="([^"]*)"/g)].map((x) => [x[1], x[2]]));
    const desc = /<!\[CDATA\[([\s\S]*?)\]\]>/.exec(m[2] ?? '')?.[1] ?? '';
    const un = (s) => s.replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'");
    return { id: a.id, name: un(a.ln ?? ''), author: un(a.un ?? ''), authorId: a.ui, published: a.dp, featured: a.df, rating: Number(a.rg ?? 0), votes: Number(a.vs ?? 0), plays: Number(a.ps ?? 0), character: Number(a.pc ?? 0), description: desc };
  });
}

// List pages are cached for a day (--fresh to refetch): the server allows only a few hundred requests a day per IP
// (it answered 429 with Retry-After: 86336 after about 500 in one day), so a rerun shouldn't spend them on lists.
async function listPage(body) {
  const file = path.join(cacheDir, `list-${createHash('sha1').update(body).digest('hex').slice(0, 16)}.xml`);
  if (!flags.fresh && fs.existsSync(file) && Date.now() - fs.statSync(file).mtimeMs < 86400000) return { text: fs.readFileSync(file, 'latin1'), cached: true };
  const text = await post('get_level.hw', body);
  fs.writeFileSync(file, text, 'latin1');
  return { text, cached: false };
}

async function listLevels() {
  if (flags.featured) return parseList((await listPage('action=get_featured')).text);
  if (flags.authors) {
    // --authors "A,B,C": every level by each author (search_by_user matches the start of the name, so keep exact matches only).
    const levels = [];
    for (const author of flags.authors.split(',').map((a) => a.trim()).filter(Boolean)) {
      for (let page = 1; ; page++) {
        const { text, cached } = await listPage(`action=search_by_user&sterm=${field(author)}&page=${page}&sortby=newest`);
        const got = parseList(text);
        const exact = got.filter((lv) => lv.author.toLowerCase() === author.toLowerCase());
        levels.push(...exact);
        process.stderr.write(`${author}: ${exact.length} levels${got.length > exact.length ? ` (and ${got.length - exact.length} by other authors whose names start the same)` : ''}${cached ? ' (cached)' : ''}\n`);
        if (!cached) await sleep(wait);
        if (got.length < 500) break;
      }
    }
    return levels;
  }
  const pages = Number(flags.pages ?? 1), sort = flags.sort ?? 'rating', levels = [];
  for (let page = 1; page <= pages; page++) {
    let body;
    if (flags.search) body = `action=search_by_name&sterm=${field(flags.search)}&page=${page}&sortby=${sort}`;
    else if (flags.author) body = `action=search_by_user&sterm=${field(flags.author)}&page=${page}&sortby=${sort}`;
    else body = `action=get_all&page=${page}&sortby=${sort}&uploaded=${flags.uploaded ?? 'week'}`;
    const { text, cached } = await listPage(body);
    const got = parseList(text);
    levels.push(...got);
    process.stderr.write(`list page ${page}: ${got.length} levels${cached ? ' (cached)' : ''}\n`);
    if (got.length < 500) break;
    if (!cached) await sleep(wait);
  }
  return levels;
}

const blockedMessage = (err) => `the server is refusing requests (HTTP 429)${err.retryAfter ? ` for another ${(err.retryAfter / 3600).toFixed(1)} hours (until about ${new Date(Date.now() + err.retryAfter * 1000).toLocaleString()})` : ''}; stopping. Rerun after that: the cache keeps everything done so far.`;

// An even last digit: the server doesn't count a play (the game sends an odd one when it does; randomNumString).
const ipTracking = () => String(1000 + Math.floor(Math.random() * 4500) * 2);

async function fetchLevel(lv) {
  const file = path.join(cacheDir, `${lv.id}.bin`);
  let data, cached = fs.existsSync(file);
  if (cached) data = fs.readFileSync(file);
  else {
    data = await post('get_level.hw', `level_id=${lv.id}&action=get_record&ip_tracking=${ipTracking()}`, true);
    if (data.length === 0 || /^(failure|<html|<!DOCTYPE)/i.test(data.subarray(0, 20).toString('latin1'))) throw new Error(`no record (${data.subarray(0, 30).toString('latin1')})`);
    fs.writeFileSync(file, data);
  }
  // The record can only be decrypted with its author's id, so keep the list entry next to it (--dump reads it).
  const meta = path.join(cacheDir, `${lv.id}.json`);
  if (!fs.existsSync(meta)) fs.writeFileSync(meta, JSON.stringify({ id: lv.id, name: lv.name, author: lv.author, authorId: lv.authorId, published: lv.published }) + '\n');
  const d = createDecipheriv('bf-cbc', Buffer.from('eatshit' + lv.authorId), Buffer.from('abcd1234'));
  d.setAutoPadding(false);
  const xml = inflateSync(Buffer.concat([d.update(data), d.final()]), { finishFlush: 2 }).toString('utf8');
  return { xml, recordBytes: data.length, cached };
}

// ---- the offline archive (no server at all) ----

// "Happy Wheels Offline Backup" v1.1 by DarkMoe and XXLuigiMario (archive.org item Happy_Wheels_Offline, "Happy Wheels
// 1.1.zip", 9.4 GB): every published level up to 2018-11-26 in Data/htdocs/swf.totaljerkface.com/happywheels.sqlite3
// (17.6 GB). Its own get_level.hw (PHP) shows the layout: `levels` holds the list fields the server returns (id, ln, un,
// ui, dp, rg, vs, ps, pc, uc) and `levels_data` (id, data) the record exactly as get_record serves it.
async function searchArchive(results) {
  const { DatabaseSync } = await import('node:sqlite');
  const db = new DatabaseSync(flags.sqlite, { readOnly: true });
  // --resume: go through the archive in id order, append each match to --out as it is found, and keep the last id done in
  // OUT.checkpoint, so the same command run again (after Ctrl-C, a kill or a reboot) carries on where it stopped.
  const resume = !!flags.resume;
  if (resume && !flags.out) throw new Error('--resume needs --out FILE (matches are appended to it as they are found)');
  const checkpoint = resume ? `${flags.out}.checkpoint` : null;
  let startId = 0;
  if (resume && fs.existsSync(checkpoint)) {
    const saved = fs.readFileSync(checkpoint, 'utf8').trim();
    if (saved === 'done') { process.stderr.write(`${flags.out} is complete (${checkpoint} says done); delete both to start over\n`); return true; }
    startId = Number(saved) || 0;
  }
  if (resume && (!fs.existsSync(flags.out) || startId === 0)) fs.writeFileSync(flags.out, COLUMNS.join('\t') + '\n');
  // Matches found after the last checkpoint were written before the stop; drop them, the rerun finds them again.
  else if (resume) fs.writeFileSync(flags.out, fs.readFileSync(flags.out, 'utf8').split('\n').filter((line, i) => i === 0 || (line && Number(line.split('\t')[0]) <= startId)).join('\n') + '\n');
  if (resume) process.stderr.write(startId ? `resuming after level id ${startId}\n` : 'starting from the first level\n');
  const sort = resume ? 'id' : flags.sort ?? 'rating';
  const order = { oldest: 'date(l.dp) ASC', newest: 'date(l.dp) DESC', plays: 'l.ps DESC', rating: 'l.rg DESC', id: 'l.id ASC' }[sort];
  if (!order) throw new Error(`--sort must be one of oldest, newest, plays, rating, id`);
  // The live server only ranks levels with about 60 votes or more by rating; the archive's script has no such floor.
  const minVotes = Number(flags['min-votes'] ?? (sort === 'rating' ? 60 : 0));
  const minPlays = Number(flags['min-plays'] ?? 0), minRating = Number(flags['min-rating'] ?? 0), minTriggers = Number(flags['min-triggers'] ?? (flags.interesting ? 200 : 0));
  const limit = flags.limit ? Number(flags.limit) : -1;
  // --ids 1,2,3: only these levels.
  const idList = flags.ids ? String(flags.ids).split(',').map(Number).filter(Number.isInteger).join(',') : '';
  const stmt = db.prepare(`SELECT l.id, l.ln, l.un, l.ui, l.dp, l.rg, l.vs, l.ps, l.pc, l.uc, d.data FROM levels l JOIN levels_data d ON d.id = l.id
    WHERE l.vs >= ? AND l.ps >= ? AND l.rg >= ? AND l.id > ?${idList ? ` AND l.id IN (${idList})` : ''} ORDER BY ${order} LIMIT ?`);
  let seen = 0, analysed = 0, broken = 0, kept = 0, lastId = startId;
  const t0 = Date.now();
  for (const r of stmt.iterate(minVotes, minPlays, minRating, startId, limit)) {
    seen++;
    lastId = Number(r.id);
    // Every 5,000 levels, after their matches are in the file: the point a rerun carries on from.
    if (resume && seen % 5000 === 0) fs.writeFileSync(checkpoint, String(lastId));
    const lv = { id: String(r.id), name: String(r.ln ?? ''), author: String(r.un ?? ''), authorId: String(r.ui), published: String(r.dp ?? ''), rating: Number(r.rg ?? 0), votes: Number(r.vs ?? 0), plays: Number(r.ps ?? 0), character: Number(r.pc ?? 0), description: String(r.uc ?? '') };
    if (flags.exclude.some((re) => re.test(lv.name) || re.test(lv.description)) || flags.excludeName.some((re) => re.test(lv.name))) continue;
    let a;
    try {
      const data = Buffer.from(r.data);
      const d = createDecipheriv('bf-cbc', Buffer.from('eatshit' + lv.authorId), Buffer.from('abcd1234'));
      d.setAutoPadding(false);
      a = analyze(inflateSync(Buffer.concat([d.update(data), d.final()]), { finishFlush: 2 }).toString('utf8'));
      lv.recordBytes = data.length;
    } catch { broken++; continue; }
    analysed++;
    // Keep only what will be shown, so a pass over millions of levels stays small in memory.
    // --interesting keeps a level that is any of: over a limit (or a NaN chain), a controls level, or has min-triggers (default
    // 200) triggers; otherwise every filter given must hold.
    const keep = flags.interesting ? a.overLimit || a.controls || a.triggers >= minTriggers
      : (!flags['over-limit'] || a.overLimit) && (!flags.controls || a.controls) && a.triggers >= minTriggers;
    if (keep) {
      kept++;
      if (resume) fs.appendFileSync(flags.out, row(lv, a).map(clean).join('\t') + '\n'); else results.push({ lv, a });
    }
    if (seen % 50000 === 0) process.stderr.write(`${new Date().toLocaleTimeString()}: ${seen} levels read (up to id ${lastId}), ${analysed} analysed, ${kept} kept, ${broken} unreadable (${Math.round((Date.now() - t0) / 1000)} s)\n`);
  }
  if (resume) fs.writeFileSync(checkpoint, limit === -1 ? 'done' : String(lastId));
  process.stderr.write(`done: ${seen} levels read, ${analysed} analysed, ${kept} kept, ${broken} unreadable, in ${Math.round((Date.now() - t0) / 1000)} s\n`);
  return resume; // true: the matches are already in --out
}

// ---- output ----

const COLUMNS = ['id', 'name', 'author', 'published', 'rating', 'votes', 'plays', 'shapes', 'art', 'triggers', 'over', 'controls', 'jets', 'sensors', 'npcs', 'groups', 'joints', 'xmlKB', 'recordKB', 'version', 'url'];
function row(lv, a) {
  const over = [a.nanChain && 'NaN chain', a.shapes > 900 && 'shapes', a.art > 10000 && 'art'].filter(Boolean).join('+');
  return [lv.id, lv.name, lv.author, lv.published, lv.rating.toFixed(2), lv.votes, lv.plays, Number.isNaN(a.shapes) ? 'NaN' : a.shapes, a.art, a.triggers, over, a.controls ? 'yes' : '',
    a.jets, a.smallSensors, a.npcs, a.groups, a.joints, (a.xmlBytes / 1024).toFixed(0), lv.recordBytes ? (lv.recordBytes / 1024).toFixed(0) : '', a.version,
    lv.id ? `${SITE}/happy_wheels.tjf?level_id=${lv.id}` : ''];
}
const clean = (v) => String(v).replace(/[\t\r\n]+/g, ' ');

async function main() {
  if (flags.dump) {
    const meta = JSON.parse(fs.readFileSync(path.join(cacheDir, `${flags.dump}.json`), 'utf8'));
    process.stdout.write((await fetchLevel({ ...meta, id: flags.dump })).xml);
    return;
  }
  const results = [];
  if (flags.analyze.length) {
    for (const f of flags.analyze) results.push({ lv: { id: '', name: path.basename(f), author: '', published: '', rating: 0, votes: 0, plays: 0 }, a: analyze(fs.readFileSync(f, 'utf8')) });
  } else if (flags.sqlite) {
    if (await searchArchive(results)) return;
  } else {
    fs.mkdirSync(cacheDir, { recursive: true });
    let all;
    process.stderr.write(`request budget: ${describe(status(budget))}\n`);
    try { all = await listLevels(); } catch (err) {
      if (err instanceof QuotaError) { process.stderr.write(`not sending: ${err.message}\n`); process.exit(2); }
      if (err.status === 429) { process.stderr.write(blockedMessage(err) + '\n'); process.exit(2); }
      throw err;
    }
    const minPlays = Number(flags['min-plays'] ?? 0), minVotes = Number(flags['min-votes'] ?? 0), minRating = Number(flags['min-rating'] ?? 0);
    // --since DATE: only levels published after it (the offline archive has everything up to 2018-11-26).
    if (flags.since) all = all.filter((lv) => lv.published > flags.since);
    const candidates = all.filter((lv) => !flags.exclude.some((re) => re.test(lv.name) || re.test(lv.description))
      && !flags.excludeName.some((re) => re.test(lv.name)) && lv.plays >= minPlays && lv.votes >= minVotes && lv.rating >= minRating);
    if (flags['list-only']) {
      const header = 'id\tname\tauthor\tauthorId\tpublished\trating\tvotes\tplays\tcharacter\tdescription';
      const line = (lv) => [lv.id, lv.name, lv.author, lv.authorId, lv.published, lv.rating.toFixed(2), lv.votes, lv.plays, lv.character, lv.description].map(clean).join('\t');
      if (flags.append) {
        // --append: add only levels not already in --out, so repeated runs (new uploads each week, other sorts) build one table.
        if (!flags.out) throw new Error('--append needs --out FILE');
        const have = new Set(fs.existsSync(flags.out) ? fs.readFileSync(flags.out, 'utf8').split('\n').slice(1).map((l) => l.split('\t')[0]).filter(Boolean) : []);
        if (!fs.existsSync(flags.out)) fs.writeFileSync(flags.out, header + '\n');
        const added = [...new Map(candidates.filter((lv) => !have.has(lv.id)).map((lv) => [lv.id, lv])).values()];
        if (added.length) fs.appendFileSync(flags.out, added.map(line).join('\n') + '\n');
        process.stderr.write(`${all.length} listed, ${candidates.length} after the list filters, ${added.length} new added to ${flags.out} (now ${have.size + added.length}; nothing downloaded)\n`);
        return;
      }
      const tsv = [header, ...candidates.map(line)].join('\n') + '\n';
      if (flags.out) fs.writeFileSync(flags.out, tsv); else process.stdout.write(tsv);
      process.stderr.write(`${all.length} listed, ${candidates.length} after the list filters (nothing downloaded)\n`);
      return;
    }
    const cap = Number(flags['max-downloads'] ?? 150);
    process.stderr.write(`${all.length} listed, ${candidates.length} left after the list filters${candidates.length > cap ? `, analysing the first ${cap} (--max-downloads)` : ''}\n`);
    let failures = 0, downloads = 0, slowDowns = 0;
    const todo = candidates.slice(0, cap);
    for (let i = 0; i < todo.length; i++) {
      const lv = todo[i];
      try {
        const { xml, recordBytes, cached } = await fetchLevel(lv);
        lv.recordBytes = recordBytes;
        results.push({ lv, a: analyze(xml) });
        failures = 0; slowDowns = 0;
        if (!cached) {
          downloads++;
          if (downloads % 50 === 0) process.stderr.write(`${downloads} downloaded, ${i + 1} of ${todo.length} done\n`);
          await sleep(wait);
        }
      } catch (err) {
        if (err instanceof QuotaError) { process.stderr.write(`stopping: ${err.message}. Rerun later: the cache keeps what is done.\n`); break; }
        if (err.status === 429) {
          // Too many requests: wait as long as the server asks (Retry-After), else 1 minute doubling to 15, then retry
          // the same level. Give up after 6 in a row, or at once if the server asks for more than 15 minutes (it has
          // asked for 24 hours: a daily quota, not a rate).
          if (err.retryAfter > 900) { process.stderr.write(blockedMessage(err) + '\n'); break; }
          if (++slowDowns > 6) { process.stderr.write('still rate-limited after 6 waits; stopping (rerun later: the cache keeps what is done)\n'); break; }
          const pause = err.retryAfter ? err.retryAfter * 1000 : Math.min(60000 * 2 ** (slowDowns - 1), 900000);
          process.stderr.write(`rate-limited (HTTP 429) at level ${lv.id}; waiting ${Math.round(pause / 1000)} s\n`);
          await sleep(pause);
          i--;
          continue;
        }
        process.stderr.write(`level ${lv.id} (${lv.name}): ${err.message}\n`);
        if (++failures >= 3) { process.stderr.write('3 failures in a row; stopping so as not to hammer the server\n'); break; }
        await sleep(wait * 4);
      }
    }
    process.stderr.write(`${results.length} analysed (${downloads} downloaded, the rest from ${cacheDir}); ${describe(status(budget))}\n`);
  }
  const minTriggers = Number(flags['min-triggers'] ?? 0);
  const shown = results.filter(({ a }) => (!flags['over-limit'] || a.overLimit) && (!flags.controls || a.controls) && a.triggers >= minTriggers);
  // Over-limit levels first, then by how far over the shape limit they are, then by trigger count.
  shown.sort((x, y) => (y.a.overLimit - x.a.overLimit) || ((Number.isNaN(y.a.shapes) ? 1e9 : y.a.shapes) - (Number.isNaN(x.a.shapes) ? 1e9 : x.a.shapes)) || (y.a.triggers - x.a.triggers));
  const tsv = [COLUMNS.join('\t'), ...shown.map(({ lv, a }) => row(lv, a).map(clean).join('\t'))].join('\n') + '\n';
  if (flags.out) { fs.writeFileSync(flags.out, tsv); process.stderr.write(`${shown.length} levels written to ${flags.out}\n`); } else process.stdout.write(tsv);
}

main().catch((err) => { console.error(err); process.exit(1); });
