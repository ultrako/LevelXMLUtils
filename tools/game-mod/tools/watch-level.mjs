// Usage: node tools/watch-level.mjs --level NAME.xml [--level-dir ~/levels] [--port 8080] --eval EXPR --script "STEP; STEP; ..."
//
// Plays a level in headless Chromium (like controls-probe.mjs) and follows a script, printing EXPR (evaluated in the game; window.__session is the
// session) as JSON with the tick whenever the script says so. Steps:
//   wait N            wait N ticks
//   key NAME HOLD     hold a key for HOLD ticks (Up Down Left Right Space Shift Ctrl Z C)
//   eval              print EXPR
//   every N COUNT     print EXPR every N ticks, COUNT times
//   click X Y         click at game pixel (X, Y) (the game is 900 by 500)
//   shot FILE         screenshot of the game
//   profile start [US] start the CPU profiler (sampling every US microseconds, default 100)
//   profile stop FILE  stop it and write a .cpuprofile (Chrome DevTools can open it)
// --record EXPR also evaluates EXPR on every game tick from the very start (inside the page, so no tick is missed) and prints that log at the end.
// Kills the headless Chromium it started; check `pgrep -af chromium` for leftovers if it is interrupted: a second run on the same debugging port fights the first.
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir, homedir } from 'node:os';
import { join } from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []));
const levelName = args.level ?? 'watch.xml';
const levelDir = (args['level-dir'] ?? join(homedir(), 'levels')).replace(/^~/, homedir());
const port = Number(args.port ?? 8080);
const debugPort = 9334;
const script = (args.script ?? 'wait 30; eval').split(';').map((s) => s.trim().split(/\s+/)).filter((s) => s[0]);
const expr = args.eval ?? 'window.__session._iteration';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const t0 = Date.now();
const log = (...m) => console.error(`[${((Date.now() - t0) / 1000).toFixed(1)}s]`, ...m);
const keys = { Up: [38, 'ArrowUp'], Down: [40, 'ArrowDown'], Left: [37, 'ArrowLeft'], Right: [39, 'ArrowRight'], Space: [32, ' ', 'Space'], Shift: [16, 'Shift', 'ShiftLeft'],
  Ctrl: [17, 'Control', 'ControlLeft'], Z: [90, 'z', 'KeyZ'], C: [67, 'c', 'KeyC'] };

let server = null;
async function ensureServer() {
  try { await fetch(`http://127.0.0.1:${port}/__mod/`); return; } catch {}
  server = spawn(process.execPath, [new URL('./serve.mjs', import.meta.url).pathname, '--port', String(port), '--level-dir', levelDir], { stdio: 'ignore' });
  for (let i = 0; i < 50; i++) { await sleep(200); try { await fetch(`http://127.0.0.1:${port}/__mod/`); return; } catch {} }
  throw new Error('serve.mjs did not start');
}
const profile = mkdtempSync(join(tmpdir(), 'watch-level-'));
const chrome = spawn('chromium', ['--headless=new', `--remote-debugging-port=${debugPort}`, `--user-data-dir=${profile}`, '--use-gl=angle', '--use-angle=swiftshader',
  '--enable-unsafe-swiftshader', '--window-size=1000,1100', '--mute-audio', '--autoplay-policy=no-user-gesture-required', '--disable-background-timer-throttling',
  '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows', 'about:blank'], { stdio: 'ignore' });
function cleanup() { try { chrome.kill('SIGKILL'); } catch {} try { server?.kill(); } catch {} try { rmSync(profile, { recursive: true, force: true }); } catch {} }
process.on('exit', cleanup);
process.on('SIGINT', () => { cleanup(); process.exit(130); });

let ws, nextId = 1;
const pending = new Map();
function send(method, params = {}) {
  const id = nextId++;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject, method });
    setTimeout(() => { if (pending.has(id)) { pending.delete(id); reject(new Error(`${method}: no reply in 120 s`)); } }, 120000);
  });
}
const contexts = [];
let gameContext = null;
async function connect() {
  for (let i = 0; i < 50; i++) {
    try {
      const page = (await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json()).find((t) => t.type === 'page');
      if (page) {
        ws = new WebSocket(page.webSocketDebuggerUrl);
        await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
        ws.onmessage = (e) => { const m = JSON.parse(e.data);
          if (m.method === 'Page.javascriptDialogOpening') { ws.send(JSON.stringify({ id: nextId++, method: 'Page.handleJavaScriptDialog', params: { accept: true } })); }
          if (m.method === 'Runtime.exceptionThrown') { log('exception:', (m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text).slice(0, 400)); }
          if (m.method === 'Runtime.executionContextCreated') { contexts.push(m.params.context.id); }
          if (m.method === 'Runtime.executionContextsCleared') { contexts.length = 0; gameContext = null; }
          if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(new Error(`${p.method}: ${m.error.message}`)) : p.resolve(m.result); } };
        return;
      }
    } catch {}
    await sleep(200);
  }
  throw new Error('could not reach Chromium');
}
async function evaluate(expression) {
  if (gameContext === null) {
    for (const id of [...contexts].reverse()) {
      try { if ((await send('Runtime.evaluate', { expression: '!!window.__session', returnByValue: true, contextId: id })).result.value) { gameContext = id; break; } } catch {}
    }
  }
  const r = await send('Runtime.evaluate', gameContext === null ? { expression, returnByValue: true } : { expression, returnByValue: true, contextId: gameContext });
  return r.exceptionDetails ? `ERROR ${r.exceptionDetails.exception?.description ?? r.exceptionDetails.text}` : r.result.value;
}
const tick = () => evaluate('window.__session ? window.__session._iteration : -1');
async function waitTick(target) { const start = Date.now(); for (;;) { const t = await tick(); if (t >= target) return t; if (Date.now() - start > 60000) throw new Error(`tick ${target} not reached`); await sleep(2); } }
async function key(name, down) {
  const [code, keyName, codeName] = keys[name];
  await send('Input.dispatchKeyEvent', { type: down ? 'rawKeyDown' : 'keyUp', windowsVirtualKeyCode: code, nativeVirtualKeyCode: code, key: keyName, code: codeName ?? keyName });
  return (await tick()) + 1;
}
const print = async () => console.log(`${await tick()}: ${JSON.stringify(await evaluate(expr))}`);

async function main() {
  await ensureServer();
  await connect();
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Page.addScriptToEvaluateOnNewDocument', { source: 'window.__probe = [];' });
  if (args.record) {
    await send('Page.addScriptToEvaluateOnNewDocument', { source: `window.__record = []; (function poll() { try { const s = window.__session; if (s && s._iteration !== window.__recordLast) { window.__recordLast = s._iteration; window.__record.push([s._iteration, (() => (${args.record}))()]); } } catch (e) { window.__record.push(['error', String(e)]); } requestAnimationFrame(poll); })();` });
  }
  await send('Page.navigate', { url: `http://localhost:${port}/happy_wheels.tjf?local=${encodeURIComponent(levelName)}` });
  const started = Date.now();
  while ((await tick()) < 1) { if (Date.now() - started > 400000) throw new Error('the level never started'); await sleep(250); }
  const [fx, fy, fw, fh] = (await send('Runtime.evaluate', { expression: `(() => { const f = [...document.querySelectorAll('iframe')].find((f) => /happy-wheels-js/.test(f.src)); f.focus(); const r = f.getBoundingClientRect(); return [r.left, r.top, r.width, r.height]; })()`, returnByValue: true })).result.value;
  // the game canvas is 900 by 500, centred in the iframe
  const gx = fx + (fw - 900) / 2, gy = fy + (fh - 500) / 2;
  const click = async (x, y) => { for (const type of ['mousePressed', 'mouseReleased']) { await send('Input.dispatchMouseEvent', { type, x: gx + x, y: gy + y, button: 'left', clickCount: 1 }); } };
  await click(450, 250);
  await evaluate('window.focus(); true');
  log('level running at tick', await tick());
  for (const [op, a, b] of script) {
    if (op === 'wait') await waitTick((await tick()) + Number(a));
    else if (op === 'eval') await print();
    else if (op === 'every') { for (let i = 0; i < Number(b); i++) { await print(); await waitTick((await tick()) + Number(a)); } }
    else if (op === 'key') { const at = await key(a, true); await waitTick(at - 1 + Number(b ?? 1)); await key(a, false); }
    else if (op === 'click') await click(Number(a), Number(b));
    else if (op === 'profile' && a === 'start') { await send('Profiler.enable'); await send('Profiler.setSamplingInterval', { interval: Number(b ?? 100) }); await send('Profiler.start'); }
    else if (op === 'profile' && a === 'stop') { const { profile } = await send('Profiler.stop'); writeFileSync(b, JSON.stringify(profile)); log('wrote', b); }
    else if (op === 'shot') { const shot = await send('Page.captureScreenshot', { format: 'png', clip: { x: gx, y: gy, width: 900, height: 500, scale: 1 } }); writeFileSync(a, Buffer.from(shot.data, 'base64')); log('wrote', a); }
    else throw new Error(`unknown step ${op}`);
  }
}
async function finish() { if (args.record) { for (const [t, v] of (await evaluate('window.__record')) ?? []) console.log(`rec ${t}: ${JSON.stringify(v)}`); } }
main().then(finish).then(() => { cleanup(); process.exit(0); }, (e) => { console.error(e); cleanup(); process.exit(1); });
