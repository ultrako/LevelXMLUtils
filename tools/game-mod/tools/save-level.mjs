#!/usr/bin/env node
// Saves level XML to your Happy Wheels account (set_level.hw, action create / update), the same request the level
// editor's Save button sends. Saved levels are private; only --publish makes one public.
//
//   HW_COOKIE   the Cookie header of a logged-in totaljerkface.com request (browser DevTools > Network > any request)
//   HW_USER_ID  your numeric user id (window.HW_SETTINGS.user_id on the game page; the number in /profile.tjf?uid=N)
// Each is taken from the environment variable if set, otherwise from a file: ~/.hw_cookie and ~/.hw_user_id (or the
// paths in HW_COOKIE_FILE / HW_USER_ID_FILE). Neither value is ever printed or logged. Keep the files mode 600.
//
// Usage:
//   node tools/save-level.mjs --dry-run --file LEVEL.xml            build + check the request, no network
//   node tools/save-level.mjs --file LEVEL.xml [--name NAME] [--id N]   save (create), or update level N
//                                                                   (NAME defaults to the file's name; at most 20 characters)
//   node tools/save-level.mjs --readback N                          fetch saved level N (get_level.hw get_record), decrypt it,
//                                                                   print its size and a sha256, and check it is valid XML
//   node tools/save-level.mjs --list                                list your saved + published levels (get_cmb_by_user), read each one back, and
//                                                                   print its stored (encrypted) size against the save cap
//   node tools/save-level.mjs --publish N                           PUBLISH saved level N (action=publish): it becomes visible in the
//                                                                   public level browser, and can't be changed after. The only command here that does.
//   node tools/save-level.mjs --delete N [--private]                DELETE level N (action=del_level, or del_priv_level for a private one),
//                                                                   printing the server's raw reply
//   A save sends the level's forced character (playable_character) from the XML's <info c f>, as the game does; --character N overrides it
//   add --comment TEXT to set the level's description (public once published; empty by default)
//   add --zopfli [ITERATIONS] to compress with Zopfli (default 50 iterations) for a much smaller record
//   add --ect [LEVEL] to compress with ECT, which you build and put on your PATH (or in ECT); default level 10300: 300 iterations
//   with a second pass. Half a percent smaller than --zopfli
//   add --verify to any save to read the level back afterwards and compare it with what was sent
//   options: --wait SECONDS (before retrying a save, default 60; the server answers failure:time_lockout if you save
//            too often), --timeout SECONDS (default 900), --log FILE (JSON lines, default save-level.log.jsonl)
//
// The save cap: the server stores a record (the compressed, encrypted level) of at most 900,000 bytes, and silently drops
// a bigger one (HTTP 200, an empty reply). Measured exactly: 900,000 bytes stored, 900,001 not.
// The level is sent the way the game sends it: XML -> zlib -> Blowfish CBC (key "eatshit"+user id, IV "abcd1234",
// PKCS#5) -> base64 -> encodeURIComponent. Blowfish needs Node's legacy OpenSSL provider, so the script re-runs
// itself with --openssl-legacy-provider.
import { spawnSync } from 'node:child_process';
import { createCipheriv, createDecipheriv } from 'node:crypto';
import { readFileSync, appendFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { homedir } from 'node:os';
import { basename, join } from 'node:path';
import https from 'node:https';
import { deflateSync, inflateSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { guard, noteResponse } from './hw-quota.mjs';

if (!process.execArgv.includes('--openssl-legacy-provider')) {
  const r = spawnSync(process.execPath, ['--openssl-legacy-provider', ...process.argv.slice(1)], { stdio: 'inherit' });
  process.exit(r.status ?? 1);
}

const args = {};
for (let i = 2; i < process.argv.length; i++) {
  const a = process.argv[i];
  if (!a.startsWith('--')) throw new Error(`unexpected argument ${a}`);
  const next = process.argv[i + 1];
  if (next === undefined || next.startsWith('--')) args[a.slice(2)] = true;
  else { args[a.slice(2)] = next; i++; }
}
const dryRun = !!args['dry-run'];
const SITE = 'https://totaljerkface.com';
const CAP = 900000; // the most bytes of record the server stores
function secret(envName, fileEnvName, defaultFile) {
  if (process.env[envName]) return process.env[envName].trim();
  try { return readFileSync(process.env[fileEnvName] ?? join(homedir(), defaultFile), 'utf8').trim(); } catch { return ''; }
}
const cookie = secret('HW_COOKIE', 'HW_COOKIE_FILE', '.hw_cookie').replace(/^cookie:\s*/i, '');
const userId = secret('HW_USER_ID', 'HW_USER_ID_FILE', '.hw_user_id');
if (!dryRun && (!cookie || !/^\d+$/.test(userId))) {
  console.error('No credentials: put the Cookie header value in ~/.hw_cookie and your numeric user id in ~/.hw_user_id (or set HW_COOKIE / HW_USER_ID). Use --dry-run to test without them.');
  process.exit(2);
}
const keyId = userId || '12345'; // dry runs use a made-up id

const key = Buffer.from('eatshit' + keyId);
const iv = Buffer.from('abcd1234');
// A zlib stream is a header plus a plain DEFLATE stream plus a checksum, and the game (and any inflate) replays whatever
// the encoder chose, so a smarter encoder gives a smaller record that loads identically.
// --zopfli compresses with Zopfli (@gfx/zopfli, Google's Zopfli built to WebAssembly): on the 48x24 raycast level 585,548 B
// (zlib 9) became 385,897 B. It takes about 10 s per MB of XML at the default 50 iterations.
// --ect compresses with ECT (github.com/fhanau/Efficient-Compression-Tool), which you build yourself: the program in the ECT
// environment variable, else `ect` on the PATH. It writes a gzip file, whose DEFLATE stream is given a zlib header and
// checksum. Half a percent smaller than Zopfli: on the raycast level with the easter egg (2026-10-22) Zopfli at 50 iterations
// gave 902,075 B and ECT -10100 896,913 B in about a minute. 10300 was about the best (896,416 B).
function compressEct(data) {
  const level = args.ect === true ? '10300' : String(args.ect);
  const ect = process.env.ECT ?? 'ect';
  const dir = mkdtempSync(join(tmpdir(), 'ect-'));
  try {
    const file = join(dir, 'level.xml');
    writeFileSync(file, data);
    const r = spawnSync(ect, [`-${level}`, '-gzip', '--mt-deflate', '-quiet', file], { stdio: ['ignore', 'pipe', 'pipe'] });
    if (r.error?.code === 'ENOENT') throw new Error(`--ect: no "${ect}" program; build ECT and put it on your PATH, or set ECT to its path`);
    if (r.status !== 0) throw new Error(`ect failed: ${r.error?.message ?? r.stderr?.toString().slice(0, 200)}`);
    const gz = readFileSync(file + '.gz');
    // gzip: a 10-byte header, optional fields by its flags, the DEFLATE stream, then CRC32 and size (8 bytes)
    const flags = gz[3]; let i = 10;
    if (flags & 4) i += 2 + gz.readUInt16LE(i);
    if (flags & 8) i = gz.indexOf(0, i) + 1;
    if (flags & 16) i = gz.indexOf(0, i) + 1;
    if (flags & 2) i += 2;
    const deflate = gz.subarray(i, gz.length - 8);
    const adler = adler32(data);
    const out = Buffer.concat([Buffer.from([0x78, 0x9c]), deflate, Buffer.from([adler >>> 24, (adler >>> 16) & 255, (adler >>> 8) & 255, adler & 255])]);
    if (!inflateSync(out).equals(data)) throw new Error('the ECT stream does not inflate back to the same bytes');
    return out;
  } finally { rmSync(dir, { recursive: true, force: true }); }
}
function adler32(data) {
  let a = 1, b = 0;
  for (let i = 0; i < data.length; i++) { a = (a + data[i]) % 65521; b = (b + a) % 65521; }
  return ((b << 16) | a) >>> 0;
}
async function compress(data) {
  if (args.ect) return compressEct(data);
  if (!args.zopfli) return deflateSync(data);
  const iterations = args.zopfli === true ? 50 : Number(args.zopfli);
  // loaded only here: the package (an Emscripten build) installs process-wide handlers for uncaught errors
  const { zlibAsync } = await import('@gfx/zopfli');
  const out = Buffer.from(await zlibAsync(data, { numiterations: iterations }));
  if (!inflateSync(out).equals(data)) throw new Error('the Zopfli stream does not inflate back to the same bytes');
  return out;
}
export async function buildRecord(xml) {
  let compressed;
  try { compressed = await compress(Buffer.from(xml, 'utf8')); } catch (err) { console.error(err.message); process.exit(1); }
  const c = createCipheriv('bf-cbc', key, iv);
  const encrypted = Buffer.concat([c.update(compressed), c.final()]);
  return { compressedAndEncrypted: encrypted.length, record: encodeURIComponent(encrypted.toString('base64')) };
}
export function readRecord(record) { // the inverse, for checking
  const d = createDecipheriv('bf-cbc', key, iv);
  const data = Buffer.from(decodeURIComponent(record), 'base64');
  return inflateSync(Buffer.concat([d.update(data), d.final()])).toString('utf8');
}

const field = (v) => encodeURIComponent(String(v)); // the game sends these raw; encoded is what the server ends up with
async function body({ action, id, name, xml, character }) {
  const { record, compressedAndEncrypted } = await buildRecord(xml);
  const parts = [`action=${action}`];
  if (action === 'update') parts.push(`level_id=${id}`);
  parts.push(`level_name=${field(name)}`, `user_comment=${field(args.comment ?? '')}`, `playable_character=${character}`, `level_record=${record}`);
  return { text: parts.join('&'), compressedAndEncrypted };
}

function post(text, timeoutMs) {
  return new Promise((resolve) => {
    const t0 = Date.now(); const marks = {};
    // The shared daily ledger (hw-quota.mjs): refuses before sending when over the budget or during a 429 block.
    try { guard('save-level', text.slice(0, 40)); } catch (err) { return resolve({ error: err.message, marks: {} }); }
    const buf = Buffer.from(text);
    const req = https.request(`${SITE}/set_level.hw`, {
      method: 'POST', timeout: timeoutMs,
      headers: {
        'content-type': 'application/x-www-form-urlencoded', 'content-length': buf.length,
        cookie, origin: SITE, referer: `${SITE}/happy_wheels.tjf`,
        'user-agent': 'Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0',
      },
    }, (res) => {
      marks.firstByte = Date.now() - t0;
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => noteResponse(res.statusCode, res.headers['retry-after']) || resolve({ status: res.statusCode, headers: Object.fromEntries(['server', 'content-type', 'content-length', 'x-powered-by', 'connection'].filter((h) => res.headers[h]).map((h) => [h, res.headers[h]])), text: Buffer.concat(chunks).toString('utf8').slice(0, 300), marks: { ...marks, done: Date.now() - t0 } }));
    });
    req.on('socket', (s) => s.on('connect', () => { marks.connected = Date.now() - t0; }));
    req.on('finish', () => { marks.uploaded = Date.now() - t0; });
    req.on('timeout', () => { req.destroy(new Error(`no progress for ${timeoutMs / 1000}s`)); });
    req.on('error', (err) => resolve({ error: `${err.code ?? ''} ${err.message}`.trim(), marks: { ...marks, done: Date.now() - t0 } }));
    req.end(buf);
  });
}


// Fetches a saved level the way the game's loadLevel does: POST get_level.hw, action get_record; the reply is the raw
// encrypted bytes (or text starting "failure:"). Returns { xml } or { error }.
function readBack(levelId) {
  return new Promise((resolve) => {
    const t0 = Date.now();
    try { guard('save-level', `get_record ${levelId}`); } catch (err) { return resolve({ error: err.message, ms: 0 }); }
    // ip_tracking's last digit tells the server whether to count a play (odd: yes, the game's randomNumString), so keep it even.
    const buf = Buffer.from(`level_id=${levelId}&action=get_record&ip_tracking=${1000 + Math.floor(Math.random() * 4500) * 2}`);
    const req = https.request(`${SITE}/get_level.hw`, {
      method: 'POST', timeout: 300000,
      headers: { 'content-type': 'application/x-www-form-urlencoded', 'content-length': buf.length, cookie, origin: SITE, referer: `${SITE}/happy_wheels.tjf`, 'user-agent': 'Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0' },
    }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const data = Buffer.concat(chunks);
        noteResponse(res.statusCode, res.headers['retry-after']);
        const head = data.subarray(0, 40).toString('latin1');
        if (res.statusCode !== 200 || data.length === 0 || /^(failure|<html|<!DOCTYPE)/i.test(head)) return resolve({ error: `status ${res.statusCode}, ${data.length} bytes, starts ${JSON.stringify(head.slice(0, 30))}`, ms: Date.now() - t0 });
        try {
          const d = createDecipheriv('bf-cbc', key, iv);
          const xml = inflateSync(Buffer.concat([d.update(data), d.final()])).toString('utf8');
          resolve({ xml, encryptedBytes: data.length, ms: Date.now() - t0 });
        } catch (err) { resolve({ error: `could not decrypt/inflate ${data.length} bytes: ${err.message}`, ms: Date.now() - t0 }); }
      });
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', (err) => resolve({ error: err.message, ms: Date.now() - t0 }));
    req.end(buf);
  });
}
const sha = (x) => createHash('sha256').update(x).digest('hex').slice(0, 12);
async function report(levelId, sentXml) {
  const r = await readBack(levelId);
  if (r.error) { console.log(`  read back level ${levelId}: ${r.error}`); return; }
  const same = sentXml === undefined ? '' : r.xml === sentXml ? ', IDENTICAL to what was just sent' : ', DIFFERENT from what was just sent';
  const valid = /^<levelXML>[\s\S]*<\/levelXML>\s*$/.test(r.xml) ? 'well-formed <levelXML>' : 'NOT a <levelXML> document';
  console.log(`  read back level ${levelId}: ${mb(Buffer.byteLength(r.xml))} xml (stored record ${mb(r.encryptedBytes)}), sha256 ${sha(r.xml)}, ${valid}${same}  (${r.ms} ms)`);
}

// The editor's "load level" list: POST get_level.hw action=get_cmb_by_user -> <combined_data><published>/<private><lvs><lv id ln .../>
function listLevels() {
  return new Promise((resolve) => {
    try { guard('save-level', 'get_cmb_by_user'); } catch (err) { return resolve({ error: err.message }); }
    const buf = Buffer.from('action=get_cmb_by_user');
    const req = https.request(`${SITE}/get_level.hw`, { method: 'POST', timeout: 120000, headers: { 'content-type': 'application/x-www-form-urlencoded', 'content-length': buf.length, cookie, origin: SITE, referer: `${SITE}/happy_wheels.tjf` } }, (res) => {
      const chunks = []; res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8');
        noteResponse(res.statusCode, res.headers['retry-after']);
        if (!text.includes('<combined_data>')) return resolve({ error: `status ${res.statusCode}: ${JSON.stringify(text.slice(0, 60))}` });
        const section = (name) => (text.match(new RegExp(`<${name}>[\\s\\S]*?</${name}>`)) ?? [''])[0];
        const levels = [];
        for (const [name, published] of [['private', false], ['published', true]]) {
          for (const m of section(name).matchAll(/<lv ([^>]*?)\/?>/g)) {
            const attr = (k) => new RegExp(`\\b${k}="([^"]*)"`).exec(m[1])?.[1] ?? '';
            levels.push({ id: Number(attr('id')), name: attr('ln').replace(/&amp;/g, '&'), published });
          }
        }
        resolve({ levels });
      });
    });
    req.on('error', (err) => resolve({ error: err.message })); req.end(buf);
  });
}

const logPath = typeof args.log === 'string' ? args.log : 'save-level.log.jsonl';
const wait = Number(args.wait ?? 60), timeoutMs = Number(args.timeout ?? 900) * 1000;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const mb = (n) => (n / 1048576).toFixed(2) + ' MB';

async function save({ label, xml, name, id }) {
  // The server silently rejects (HTTP 200, empty body, nothing stored) a level name longer than 20 characters: names of
  // 20 saved fine, 23 and 25 to 26 were rejected, whatever the compression or size. Nothing else says why.
  if (String(name).length > 20) {
    console.error(`level name "${name}" is ${String(name).length} characters: the server drops a level whose name is over 20 without saying so. Give a shorter one with --name.`);
    process.exit(2);
  }
  const action = id ? 'update' : 'create';
  // The forced character, as the game's own save sends it (playable_character: the character's number if the level forces one, else 0), read from the
  // level's <info c=".." f="t">; the published level takes it from here, not from the XML. --character overrides it.
  const infoTag = /<info\b[^>]*>/.exec(xml)?.[0] ?? '';
  const forced = / f="t"/.test(infoTag) ? Number(/ c="(\d+)"/.exec(infoTag)?.[1] ?? 0) : 0;
  const { text, compressedAndEncrypted } = await body({ action, id, name, xml, character: args.character ?? forced });
  const info = { label, action, xmlBytes: Buffer.byteLength(xml), encryptedBytes: compressedAndEncrypted, postBytes: text.length };
  if (dryRun) {
    const roundTrip = readRecord(new URLSearchParams(text).get('level_record') !== null ? text.split('level_record=')[1] : '') === xml;
    console.log(`[dry run] ${label}: ${action}, xml ${mb(info.xmlBytes)}, encrypted ${mb(info.encryptedBytes)} (${info.encryptedBytes} B of the ${CAP.toLocaleString('en')} B save cap${info.encryptedBytes > CAP ? ': TOO BIG, the server would drop it' : ''}), POST ${mb(info.postBytes)}, decrypts back to the same xml: ${roundTrip}`);
    return { ok: roundTrip };
  }
  if (info.encryptedBytes > CAP) {
    console.error(`${label}: the record is ${info.encryptedBytes.toLocaleString('en')} bytes, over the ${CAP.toLocaleString('en')}-byte save cap; the server would drop it without saying so. Not sent (try --ect or --zopfli).`);
    return { ok: false };
  }
  for (let attempt = 1; attempt <= 5; attempt++) {
    const r = await post(text, timeoutMs);
    const line = { time: new Date().toISOString(), ...info, attempt, ...r };
    appendFileSync(logPath, JSON.stringify(line) + '\n');
    const summary = r.error ? `ERROR ${r.error}` : `${r.status} ${JSON.stringify(r.text)}`;
    console.log(`${label}: ${action}, POST ${mb(info.postBytes)} -> ${summary}  (uploaded ${r.marks.uploaded ?? '?'} ms, first byte ${r.marks.firstByte ?? '?'} ms, total ${r.marks.done} ms)`);
    if (!r.error && /failure:time_lockout/.test(r.text) && attempt < 5) { console.log(`  saved too recently, waiting ${wait}s`); await sleep(wait * 1000); continue; }
    const m = !r.error && /^success:(\d+|true)/.exec(r.text); // create answers success:<new id>, update answers success:true
    const savedId = m && /^\d+$/.test(m[1]) ? Number(m[1]) : id;
    if (args.verify && savedId) await report(savedId, xml);
    return { ok: !!m, id: savedId, result: r };
  }
  return { ok: false };
}

if (args.publish) {
  // The game's publish request: POST set_level.hw with action=publish and the level id. Answers success:..., or failure:time_lockout
  // ("You may only publish one level per day.") and other failure:<reason> texts.
  const levelId = Number(args.publish);
  if (!Number.isInteger(levelId) || levelId <= 0) { console.error('--publish needs a level id'); process.exit(2); }
  if (dryRun) { console.log(`[dry run] would publish level ${levelId}`); process.exit(0); }
  const r = await post(`action=publish&level_id=${levelId}`, 120000);
  appendFileSync(logPath, JSON.stringify({ time: new Date().toISOString(), action: 'publish', levelId, ...r }) + '\n');
  console.log(`publish level ${levelId} -> ${r.error ? 'ERROR ' + r.error : `${r.status} ${JSON.stringify(r.text)}`}`);
  process.exit(r.error || !/^success/.test(r.text) ? 1 : 0);
}
if (args.delete) {
  // The game's delete request (the editor's load window): POST set_level.hw with action=del_level for a published level, del_priv_level for a private
  // one (--private), and the level id. Answers success:..., or failure:<reason> (invalid_action, bad_param, app_error, not_logged_in, ...); the raw
  // reply is printed, since the game only shows its own message for it.
  const levelId = Number(args.delete);
  if (!Number.isInteger(levelId) || levelId <= 0) { console.error('--delete needs a level id'); process.exit(2); }
  const action = args.private ? 'del_priv_level' : 'del_level';
  if (dryRun) { console.log(`[dry run] would delete level ${levelId} (${action})`); process.exit(0); }
  const r = await post(`action=${action}&level_id=${levelId}`, 120000);
  appendFileSync(logPath, JSON.stringify({ time: new Date().toISOString(), action, levelId, ...r }) + '\n');
  console.log(`${action} level ${levelId} -> ${r.error ? 'ERROR ' + r.error : `${r.status} ${JSON.stringify(r.text)}`}`);
  process.exit(r.error || !/^success/.test(r.text) ? 1 : 0);
}
if (args.list) {
  const l = await listLevels();
  if (l.error) { console.error('could not list levels:', l.error); process.exit(1); }
  console.log(`${l.levels.length} levels (${l.levels.filter((x) => !x.published).length} private, ${l.levels.filter((x) => x.published).length} published)`);
  console.log('id         pub  stored bytes   xml bytes   vs cap (900,000 bytes)                         name');
  const rows = [];
  for (const lv of l.levels) {
    const r = await readBack(lv.id);
    const stored = r.encryptedBytes, xml = r.xml ? Buffer.byteLength(r.xml) : 0;
    const verdict = r.error ? `read failed: ${r.error.slice(0, 50)}` : stored <= CAP ? 'fits' : 'OVER';
    rows.push({ ...lv, stored, xml, verdict });
    console.log(`${String(lv.id).padEnd(10)} ${lv.published ? 'yes' : 'no '}  ${String(stored ?? '-').padStart(12)}  ${String(xml).padStart(10)}   ${verdict.padEnd(45)} ${lv.name.slice(0, 40)}`);
    await sleep(400);
  }
  const ok = rows.filter((r) => r.verdict === 'fits').length, over = rows.filter((r) => r.verdict === 'OVER').length;
  console.log(`\n${ok} fit, ${over} over the cap, ${rows.length - ok - over} other`);
  const biggest = rows.filter((r) => r.stored).sort((a, b) => b.stored - a.stored)[0];
  if (biggest) console.log(`largest stored record: ${biggest.stored} bytes (level ${biggest.id}, ${JSON.stringify(biggest.name)})`);
} else if (args.readback) {
  await report(Number(args.readback));
} else if (args.file) {
  const xml = readFileSync(String(args.file), 'utf8');
  const r = await save({ label: String(args.file), xml, name: String(args.name ?? basename(String(args.file)).replace(/\.[^.]*$/, '')), id: args.id ? Number(args.id) : undefined });
  if (r.id) console.log(`level id: ${r.id}`);
} else {
  console.error('give --file LEVEL.xml, --list, --readback N, --publish N or --delete N  (see the top of this file)');
  process.exit(2);
}
