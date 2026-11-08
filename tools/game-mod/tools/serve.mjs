#!/usr/bin/env node
// Local reverse proxy for totaljerkface.com. Plays the pinned game version (versions/pinned.json, see update.mjs) with
// its patched script, and forwards everything else to the real site, so the modded game runs at http://localhost:<port>/.
// The game's static files (scripts, sounds, pictures) are kept in cache/ the first time they are fetched, so a pinned
// version keeps working after the site moves on, as long as the server's API (levels, accounts) still accepts it.
// Usage: node tools/serve.mjs [--port 8080] [--upstream https://totaljerkface.com]
//        [--version HASH]  play this built version instead of the pinned one
//        [--script FILE]   serve exactly this file as the game script (for bisecting build stages)
// Play at http://localhost:<port>/play : only the game, with a bar under it to open a level file or a folder of them
// (Browse). A folder's levels are listed in the game's level browser. The site's own page,
// http://localhost:<port>/happy_wheels.tjf, works too (without the folder in the browser).
//        [--level-dir DIR]  the folder open in the play page's level browser; also plays DIR/NAME.xml directly at
//                          /play?local=NAME.xml. Files are read again every time they're played.
//        [--level FILE.xml]  level to play when the URL has no ?local=; also sets --level-dir to its folder.
import { createServer } from 'node:http';
import { closeSync, existsSync, mkdirSync, openSync, readFileSync, readSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { basename, dirname, extname, join, resolve } from 'node:path';
import { htmlTransforms } from '../patches/html.mjs';
import { CACHE, patchedPath, readPinned, siteVersion, versionDir } from './versions.mjs';
import { note, noteResponse } from './hw-quota.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), [])
);
const port = Number(args.port ?? 8080);
const upstream = new URL(args.upstream ?? 'https://totaljerkface.com');
const origin = `http://localhost:${port}`;
const levelFile = args.level;
const levelDir = args['level-dir'] ?? (levelFile && dirname(levelFile));
const levelsEnabled = Boolean(levelDir);

const pinned = args.version ? JSON.parse(readFileSync(join(versionDir(args.version), 'pair.json'), 'utf8')) : readPinned();
if (!pinned || !existsSync(patchedPath(pinned.index))) {
  console.error('No game version is built yet: run  node tools/update.mjs');
  process.exit(1);
}
siteVersion().then((site) => {
  if (site.index !== pinned.index || site.dependencies !== pinned.dependencies) {
    console.log(`The site has a newer game version (index ${site.index}); still playing the pinned one. node tools/update.mjs takes it.`);
  }
}).catch(() => {});
// Static game files kept locally: the version-specific scripts and the asset folders
const CACHED_RE = /^\/happy-wheels-js\/(js\/dependencies\.[0-9a-f]{32}\.js|assets-[^/]+\/.+)$/;
const CACHED_TYPES = {
  '.js': 'text/javascript; charset=utf-8', '.json': 'application/json', '.css': 'text/css; charset=utf-8', '.txt': 'text/plain; charset=utf-8',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.ogg': 'audio/ogg', '.mp3': 'audio/mpeg', '.otf': 'font/otf', '.ttf': 'font/ttf', '.woff': 'font/woff',
  '.woff2': 'font/woff2',
};
const SCRIPT_RE = /^\/happy-wheels-js\/js\/(index\.[0-9a-f]{32})\.js$/;
// Headers that describe the upstream connection/encoding, not the body we send back.
const DROP_RESPONSE = new Set(['content-encoding', 'content-length', 'transfer-encoding', 'connection', 'strict-transport-security']);
const DROP_REQUEST = new Set(['host', 'connection', 'content-length', 'accept-encoding']);

const toUpstream = (url) => url.replace(origin, upstream.origin);

// Cookies from the real site are scoped to totaljerkface.com and Secure; make them valid on http://localhost.
const rewriteCookie = (c) =>
  c
    .replace(/;\s*domain=[^;]*/gi, '')
    .replace(/;\s*secure/gi, '')
    .replace(/;\s*samesite=none/gi, '; SameSite=Lax');

function log(req, status, how) {
  console.log(`${req.method} ${req.url} -> ${status} (${how})`);
}

// The play page: the game's frame (900 by 500, as on the site), and under it a bar to open a level file or a folder. A
// file plays at once; a folder's levels are listed in the game's own level browser (patches/html.mjs). Both stay in this
// page (window.__modLevel, window.__modFolder), where the game's frame reads them: no upload, no size limit. With
// --level-dir, that folder is open when the page loads.
function playPage(current) {
  return `<!doctype html><meta charset="utf-8"><title>Happy Wheels</title>
<style>
  html, body { margin: 0; height: 100%; background: #222; color: #ccc; font: 13px sans-serif; }
  body { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 6px; }
  #game { width: 900px; height: 500px; border: 0; display: block; background: #000; }
  #bar { width: 900px; display: flex; gap: 10px; align-items: center; position: relative; }
  #menu { position: absolute; bottom: 100%; left: 0; margin-bottom: 2px; background: #333; border: 1px solid #555; display: none; }
  #menu.open { display: block; }
  #menu button { display: block; width: 100%; text-align: left; background: none; color: #ddd; border: 0; padding: 6px 14px; }
  #menu button:hover { background: #444; }
  #folder button { margin-left: 4px; }
  #name { flex: 1; text-align: right; color: #888; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
</style>
<iframe id="game" src="/happy-wheels-js/index.tjf" allow="autoplay; fullscreen"></iframe>
<div id="bar">
  <div id="menu"><button id="pick-file">A level file...</button><button id="pick-folder">A folder of levels...</button></div>
  <button id="browse">Browse...</button>
  <input id="file" type="file" accept=".xml,.levelxml,.txt" hidden><input id="dir" type="file" webkitdirectory hidden>
  <button id="restart" title="load the page's level again">Restart</button>
  <span id="folder"></span>
  <span id="name"></span>
</div>
<script>
  var frame = document.getElementById('game'), label = document.getElementById('name'), menu = document.getElementById('menu');
  var opened = null; // the file opened with Browse
  function play() { frame.src = '/happy-wheels-js/index.tjf'; }
  function base(n) { return n.replace(/\\.[^.]*$/, ''); }
  function show(xml, text) {
    window.__modLevel = { xml: xml, name: base(opened.name) };
    label.textContent = text + ' (' + Math.round(xml.length / 1024) + ' KB)';
    history.replaceState(null, '', '/play');
    play();
  }
  // a level's forced character, from its <info c="N" f="t"> near the start of the file
  function character(head) {
    var info = head.match(/<info[^>]*>/);
    if (!info || !/ f="t"/.test(info[0])) return 0;
    var c = info[0].match(/ c="([0-9]+)"/);
    return c ? parseInt(c[1], 10) || 0 : 0;
  }
  function showFolder(open) {
    window.__modFolder = open;
    var el = document.getElementById('folder');
    el.textContent = '';
    if (!open) return;
    el.textContent = 'In the level browser: ' + open.name + ' (' + open.levels.length + ' levels)';
    var close = document.createElement('button');
    close.textContent = 'Close';
    close.title = 'list the site\\'s levels again';
    close.onclick = function () { showFolder(null); };
    el.appendChild(close);
  }
  document.getElementById('browse').onclick = function (e) { e.stopPropagation(); menu.classList.toggle('open'); };
  document.addEventListener('click', function () { menu.classList.remove('open'); });
  document.getElementById('pick-file').onclick = function () { document.getElementById('file').click(); };
  document.getElementById('pick-folder').onclick = function () { document.getElementById('dir').click(); };
  document.getElementById('file').onchange = function () {
    if (!this.files[0]) return;
    opened = this.files[0];
    opened.text().then(function (xml) { opened.xml = xml; show(xml, opened.name); });
    this.value = '';
  };
  document.getElementById('dir').onchange = function () {
    var files = Array.prototype.filter.call(this.files, function (f) { return /\\.(xml|levelxml)$/i.test(f.name); });
    var root = this.files.length ? this.files[0].webkitRelativePath.split('/')[0] : '';
    this.value = '';
    Promise.all(files.map(function (f) {
      return f.slice(0, 65536).text().then(function (head) {
        return { name: base(f.name), path: f.webkitRelativePath, size: f.size, date: f.lastModified, character: character(head),
          read: function () { return f.text(); } };
      });
    })).then(function (levels) { showFolder({ name: root, levels: levels }); });
  };
  // Restart: the opened file read again (it may have been edited), or the level the address names
  document.getElementById('restart').onclick = function () {
    if (!opened) return play();
    opened.text().then(function (xml) { show(xml, opened.name); }, function () { show(opened.xml, opened.name + ', as first opened'); });
  };
  fetch('/__mod/levels.json').then(function (r) { return r.json(); }).then(function (dir) {
    if (!dir || window.__modFolder !== undefined) return;
    showFolder({ name: dir.name, levels: dir.levels.map(function (l) {
      l.read = function () {
        return fetch('/__mod/level.xml?f=' + encodeURIComponent(l.file)).then(function (r) {
          if (!r.ok) throw new Error(l.file + ': ' + r.status);
          return r.text();
        });
      };
      return l;
    }) });
  });
  ${current ? `label.textContent = ${JSON.stringify(current)};` : ''}
</script>`;
}

// The --level-dir folder's *.xml files, for the play page's level browser
function folderLevels() {
  return readdirSync(levelDir).filter((n) => /\.xml$/i.test(n)).map((file) => {
    const path = join(levelDir, file);
    const fd = openSync(path, 'r');
    const head = Buffer.alloc(65536);
    const got = readSync(fd, head, 0, head.length, 0);
    closeSync(fd);
    const info = head.toString('utf8', 0, got).match(/<info[^>]*>/)?.[0] ?? '';
    const character = / f="t"/.test(info) ? Number(info.match(/ c="([0-9]+)"/)?.[1] ?? 0) : 0;
    const { size, mtimeMs } = statSync(path);
    return { file, name: file.replace(/\.xml$/i, ''), size, date: mtimeMs, character };
  });
}

// Only bare *.xml names inside levelDir: these endpoints must not be able to read arbitrary files, since any web page
// you visit can make your browser request localhost.
function levelPath(wanted) {
  if (wanted !== basename(wanted) || !/\.xml$/i.test(wanted)) throw new Error('level name must be a plain *.xml file name');
  return join(levelDir, wanted);
}

createServer(async (req, res) => {
  try {
    const url = new URL(req.url, origin);
    const scriptName = url.pathname.match(SCRIPT_RE)?.[1];

    if (url.pathname === '/play') {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      res.end(playPage(url.searchParams.get('local')));
      return log(req, 200, 'play page');
    }

    if (url.pathname === '/__mod/levels.json') {
      const dir = levelsEnabled ? { name: basename(resolve(levelDir)), levels: folderLevels() } : null;
      res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
      res.end(JSON.stringify(dir));
      return log(req, 200, dir ? `level list (${dir.levels.length} files)` : 'no --level-dir');
    }

    if (url.pathname === '/__mod/level.xml') {
      try {
        if (!levelsEnabled) throw new Error('no --level-dir');
        const xml = readFileSync(levelPath(url.searchParams.get('f') ?? ''));
        res.writeHead(200, { 'content-type': 'text/xml; charset=utf-8', 'cache-control': 'no-store' });
        res.end(xml);
        return log(req, 200, `level file (${xml.length} bytes)`);
      } catch (err) {
        res.writeHead(404, { 'content-type': 'text/plain' });
        res.end(`${err.message}\n`);
        return log(req, 404, err.message);
      }
    }

    if (url.pathname === '/__mod/level.js') {
      const headers = { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-store' };
      const wanted = url.searchParams.get('f');
      if (!levelsEnabled || (!wanted && !levelFile)) {
        res.writeHead(200, headers);
        res.end('');
        return log(req, 200, 'no level requested');
      }
      // Only bare *.xml names inside levelDir: this endpoint must not be able to read arbitrary
      // files, since any web page you visit can make your browser request localhost.
      try {
        const path = wanted ? levelPath(wanted) : levelFile;
        const xml = readFileSync(path, 'utf8');
        const name = basename(path).replace(/\.xml$/i, '');
        res.writeHead(200, headers);
        res.end(`window.xml = ${JSON.stringify(xml)};\nwindow.xmlName = ${JSON.stringify(name)};\n`);
        return log(req, 200, `local level ${path} (${xml.length} chars)`);
      } catch (err) {
        console.error(`ERROR: cannot load level ${wanted ?? levelFile}: ${err.message}`);
        res.writeHead(200, headers); // 200 + console.error so the page still loads and shows the stock menu
        res.end(`console.error(${JSON.stringify(`[MOD] cannot load level: ${err.message}`)});\n`);
        return;
      }
    }

    // The page asks which scripts to load: the pinned pair, not the site's latest
    if (url.pathname === '/happy-wheels-js/version.json') {
      res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
      res.end(JSON.stringify(pinned));
      return log(req, 200, 'pinned version');
    }

    if (scriptName === `index.${pinned.index}`) {
      res.writeHead(200, { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-store' });
      res.end(readFileSync(args.script ?? patchedPath(pinned.index)));
      return log(req, 200, 'patched game script');
    }

    const cached = req.method === 'GET' && CACHED_RE.test(url.pathname) ? join(CACHE, url.pathname) : null;
    if (cached && existsSync(cached)) {
      // the right type matters: a browser ignores a stylesheet sent as anything but text/css, and the game waits for its fonts forever
      res.writeHead(200, { 'content-type': CACHED_TYPES[extname(cached).toLowerCase()] ?? 'application/octet-stream' });
      res.end(readFileSync(cached));
      return log(req, 200, 'cached');
    }

    const headers = {};
    for (const [k, v] of Object.entries(req.headers)) if (!DROP_REQUEST.has(k)) headers[k] = v;
    if (headers.origin) headers.origin = toUpstream(headers.origin);
    if (headers.referer) headers.referer = toUpstream(headers.referer);
    // Pages we rewrite must never be answered "304 Not Modified": the browser would keep serving
    // a stale rewritten copy (e.g. from before --level was passed).
    if (url.pathname.endsWith('.tjf')) {
      delete headers['if-none-match'];
      delete headers['if-modified-since'];
    }

    const body = ['GET', 'HEAD'].includes(req.method) ? undefined : Buffer.concat(await req.toArray());
    const up = await fetch(new URL(url.pathname + url.search, upstream), {
      method: req.method,
      headers,
      body,
      redirect: 'manual',
    });

    // The game's own requests to the server's API spend the same daily per-IP quota as the tools (hw-quota.mjs): record
    // them, and a 429 block, so level-search.mjs leaves room for playing. Never refused here.
    if (url.pathname.endsWith('.hw')) {
      const action = /(?:^|&)action=([^&]*)/.exec(body?.toString('latin1') ?? '')?.[1] ?? '';
      try { note('game (serve.mjs)', `${url.pathname} ${action}`); noteResponse(up.status, up.headers.get('retry-after')); } catch {}
    }

    const out = {};
    for (const [k, v] of up.headers) if (!DROP_RESPONSE.has(k) && k !== 'set-cookie') out[k] = v;
    if (out.location) out.location = out.location.replace(/^https?:\/\/(www\.)?totaljerkface\.com/i, origin);
    const cookies = up.headers.getSetCookie().map(rewriteCookie);
    if (cookies.length) out['set-cookie'] = cookies;

    let payload = Buffer.from(await up.arrayBuffer());
    let how = 'proxied';
    if (cached && up.status === 200) {
      mkdirSync(dirname(cached), { recursive: true });
      writeFileSync(cached, payload);
      how += ', kept in cache';
    }
    if (/^text\/html/i.test(up.headers.get('content-type') ?? '')) {
      let text = payload.toString('utf8');
      const ctx = { origin, levelsEnabled };
      if (url.pathname.endsWith('.tjf')) out['cache-control'] = 'no-store';
      for (const t of htmlTransforms.filter((t) => t.match(url.pathname) && (t.enabled?.(ctx) ?? true))) {
        const next = t.apply(text, ctx);
        if (next !== text) how += ` +${t.name}`;
        else if (t.name !== 'absolute-urls') console.warn(`WARNING: html transform "${t.name}" changed nothing on ${url.pathname}`);
        text = next;
      }
      payload = Buffer.from(text, 'utf8');
    }
    res.writeHead(up.status, out);
    res.end(req.method === 'HEAD' ? undefined : payload);
    log(req, up.status, how);
  } catch (err) {
    console.error(`${req.method} ${req.url} -> 502 ${err.message}`);
    res.writeHead(502, { 'content-type': 'text/plain' });
    res.end(`proxy error: ${err.message}\n`);
  }
}).listen(port, '127.0.0.1', () => console.log(`proxying ${upstream.origin}: play at ${origin}/play  (game index ${pinned.index})`));
