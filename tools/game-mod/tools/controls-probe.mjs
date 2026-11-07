// Usage: node tools/controls-probe.mjs [--level controls-harness.xml] [--level-dir ~/levels] [--port 8080] [--out /tmp/controls-probe.json] [--plan basic|full|taps] [--eval EXPR]
//
// Plays a level made of keyboard controls (NassingerControls from Toolkit/Controls, with the pogostick guy in its seat as
// Examples/RaycastDemo has it) in headless Chromium, presses keys at exact game ticks and logs,
// tick by tick, which of the controls' triggers ran their actions (patches/probe.mjs). Reports, for every press: the ticks from the press to its
// "on" trigger and from the release to its "off" trigger, any "on" firing after the release, and any other key's triggers firing (interference).
// A key sent between two ticks is read by the game at the start of the next tick, so "press tick" here is that tick.
//
// Kills any headless Chromium it started; check `pgrep -af chromium` for leftovers if it is interrupted: a second run on the same debugging port fights the first.
import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir, homedir } from 'node:os';
import { join } from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []));
const levelName = args.level ?? 'controls-harness.xml';
const levelDir = (args['level-dir'] ?? join(homedir(), 'levels')).replace(/^~/, homedir());
const port = Number(args.port ?? 8080);
const out = args.out ?? '/tmp/controls-probe.json';
const plan = args.plan ?? 'basic';
const verbose = 'verbose' in args;
const debugPort = 9333;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const t0 = Date.now();
const log = (...m) => console.error(`[${((Date.now() - t0) / 1000).toFixed(1)}s]`, ...m);

// The controls' 14 triggers are the level's first 14, in NassingerControls' order
const names = ['Up', 'Down', 'Left', 'Right', 'Space', 'Shift', 'Ctrl'];
const xml = readFileSync(join(levelDir, levelName), 'utf8');
const triggerXml = xml.slice(xml.indexOf('<triggers>'));
const positions = [...triggerXml.matchAll(/<t x="([-0-9.e]+)" y="([-0-9.e]+)"/g)].slice(0, 14).map((m) => [Number(m[1]), Number(m[2])]);
if (positions.length < 14) throw new Error('could not find the 14 controls triggers in the level');
// A key with a trigger clock (NassingerControls' TriggerClock: a Permanent trigger, r="4", 3 units right of its key's "on") reports the clock's
// pulses as its "on", since those are what a level gets, and its own fire-once "on" as "Press"
const clockAt = [...triggerXml.matchAll(/<t x="([-0-9.e]+)" y="([-0-9.e]+)"[^>]* r="4"/g)].map((m) => [Number(m[1]) - 3, Number(m[2])]);
const clocked = new Set(clockAt.map(([x, y]) => positions.findIndex(([px, py]) => Math.hypot(px - x, py - y) < 0.01)).filter((i) => i >= 0));
const labelAt = (x, y) => {
  if (clockAt.some(([cx, cy]) => Math.hypot(cx + 3 - x, cy - y) < 0.2)) {
    const i = positions.findIndex(([px, py]) => Math.hypot(px - (x - 3), py - y) < 0.2);
    return `${names[i >> 1]}On`;
  }
  let best = null, bestD = Infinity;
  positions.forEach(([px, py], i) => { const d = Math.hypot(px - x, py - y); if (d < bestD) { bestD = d; best = i; } });
  if (bestD >= 2) return null;
  return `${names[best >> 1]}${best % 2 ? 'Off' : clocked.has(best) ? 'Press' : 'On'}`;
};
const keys = { Up: [38, 'ArrowUp'], Down: [40, 'ArrowDown'], Left: [37, 'ArrowLeft'], Right: [39, 'ArrowRight'], Space: [32, ' ', 'Space'], Shift: [16, 'Shift', 'ShiftLeft'], Ctrl: [17, 'Control', 'ControlLeft'] };

// --- processes
let server = null;
async function ensureServer() {
  try { await fetch(`http://127.0.0.1:${port}/__mod/`); return; } catch {}
  server = spawn(process.execPath, [new URL('./serve.mjs', import.meta.url).pathname, '--port', String(port), '--level-dir', levelDir], { stdio: 'ignore' });
  for (let i = 0; i < 50; i++) { await sleep(200); try { await fetch(`http://127.0.0.1:${port}/__mod/`); return; } catch {} }
  throw new Error('serve.mjs did not start');
}
const profile = mkdtempSync(join(tmpdir(), 'controls-probe-'));
const chrome = spawn('chromium', ['--headless=new', `--remote-debugging-port=${debugPort}`, `--user-data-dir=${profile}`, '--use-gl=angle', '--use-angle=swiftshader',
  '--enable-unsafe-swiftshader', '--window-size=1000,1100', '--mute-audio', '--autoplay-policy=no-user-gesture-required', '--disable-background-timer-throttling',
  '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows', 'about:blank'], { stdio: 'ignore' });
function cleanup() { try { chrome.kill('SIGKILL'); } catch {} try { server?.kill(); } catch {} try { rmSync(profile, { recursive: true, force: true }); } catch {} }
process.on('exit', cleanup);
process.on('SIGINT', () => { cleanup(); process.exit(130); });

// --- DevTools protocol over the built-in WebSocket
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
async function connect() {
  for (let i = 0; i < 50; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json();
      const page = targets.find((t) => t.type === 'page');
      if (page) {
        ws = new WebSocket(page.webSocketDebuggerUrl);
        await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
        ws.onmessage = (e) => { const m = JSON.parse(e.data);
          if (m.method === 'Page.javascriptDialogOpening') { log('dialog:', m.params.type, m.params.message); ws.send(JSON.stringify({ id: nextId++, method: 'Page.handleJavaScriptDialog', params: { accept: true } })); }
          if (m.method === 'Runtime.consoleAPICalled' && verbose) { log('console:', m.params.type, m.params.args.map((a) => a.value ?? a.description).join(' ').slice(0, 300)); }
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
// The game may run in a frame of its own: find the execution context that has the session, and evaluate there
const contexts = [];
let gameContext = null;
async function evaluate(expression) {
  if (gameContext === null) {
    for (const id of [...contexts].reverse()) {
      try { if ((await send('Runtime.evaluate', { expression: '!!window.__session', returnByValue: true, contextId: id })).result.value) { gameContext = id; log('game is in context', id); break; } } catch {}
    }
  }
  return (await send('Runtime.evaluate', gameContext === null ? { expression, returnByValue: true } : { expression, returnByValue: true, contextId: gameContext })).result.value;
}
const tick = () => evaluate('window.__session ? window.__session._iteration : -1');
async function waitTick(target, timeoutMs = 20000) {
  const start = Date.now();
  for (;;) { const t = await tick(); if (t >= target) return t; if (Date.now() - start > timeoutMs) throw new Error(`tick ${target} not reached (at ${t})`); await sleep(2); }
}
async function key(name, down) {
  const [code, keyName, codeName] = keys[name];
  await send('Input.dispatchKeyEvent', { type: down ? 'rawKeyDown' : 'keyUp', windowsVirtualKeyCode: code, nativeVirtualKeyCode: code, key: keyName, code: codeName ?? keyName });
  return (await tick()) + 1; // read by the game at the start of the next tick
}

// --- the plan: [{ press: [names], hold: ticks, gap: ticks }]
function makePlan() {
  const steps = [];
  if (plan === 'taps') {
    // short taps, many times each, to measure how often they are missed
    for (let round = 0; round < 8; round++) { for (const name of ['Up', 'Down', 'Left', 'Right']) { for (const hold of [1, 2, 3]) { steps.push({ press: [name], hold, gap: 15 }); } } }
    return steps;
  }
  const holds = plan === 'full' ? [1, 2, 3, 5, 10, 20] : [1, 3, 10];
  for (const name of names) { for (const hold of holds) { steps.push({ press: [name], hold, gap: 25 }); } }
  // two keys at once, to look for one key's motion reaching another key's triggers
  for (const pair of [['Up', 'Left'], ['Up', 'Right'], ['Down', 'Left'], ['Down', 'Right'], ['Left', 'Right'], ['Up', 'Down'], ['Space', 'Up'], ['Ctrl', 'Left']]) { steps.push({ press: pair, hold: 10, gap: 25 }); }
  // rapid taps: release and press again after a short gap
  for (const name of ['Up', 'Left']) { for (const gap of [1, 2, 3]) { steps.push({ press: [name], hold: 3, gap, again: true }); } }
  return steps;
}

async function main() {
  await ensureServer();
  log('server up');
  await connect();
  log('connected to Chromium');
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Page.addScriptToEvaluateOnNewDocument', { source: 'window.__probe = [];' });
  await send('Page.navigate', { url: `http://localhost:${port}/happy_wheels.tjf?local=${encodeURIComponent(levelName)}` });
  // wait for the level to run, then a second for the controls to settle
  log('navigated');
  const started = Date.now();
  let lastLog = 0;
  while ((await tick()) < 1) { if (Date.now() - started > 400000) throw new Error('the level never started (is the patched build current? is the network up?)'); if (Date.now() - lastLog > 10000) { lastLog = Date.now(); log('waiting for the level; page at', await evaluate('location.href + " xml=" + (!!window.xml) + " probe=" + (!!window.__probe)')); } await sleep(250); }
  log('level running at tick', await tick(), 'holding the handle:', await evaluate('!!(window.__session._character && window.__session._character.userVehicle)'));
  // keys go to the focused frame: focus the game's own window, and click in it (the middle of the screen, away from the menu button)
  // The game is in an iframe of the loader page: focus that iframe from the top page and click inside it, then focus the game's own window
  const rect = (await send('Runtime.evaluate', { expression: `(() => { const f = [...document.querySelectorAll('iframe')].find((f) => /happy-wheels-js/.test(f.src)); if (!f) return null; f.focus(); const r = f.getBoundingClientRect(); return [r.left, r.top, r.width, r.height]; })()`, returnByValue: true })).result.value;
  if (!rect) throw new Error('could not find the game iframe');
  const [fx, fy, fw, fh] = rect;
  for (const type of ['mousePressed', 'mouseReleased']) { await send('Input.dispatchMouseEvent', { type, x: fx + fw / 2, y: fy + fh / 2, button: 'left', clickCount: 1 }); }
  await evaluate('window.focus(); true');
  log('game iframe at', JSON.stringify(rect), 'focused:', await evaluate('document.hasFocus()'));
  if ('keycheck' in args) {
    await evaluate('window.__keys = []; for (const t of [window, document]) { t.addEventListener("keydown", (e) => window.__keys.push([t === window ? "window" : "document", e.keyCode, e.key, document.hasFocus()]), true); } true');
    const at = await key('Up', true); await waitTick(at + 5); await key('Up', false);
    log('keydown seen by the game frame:', JSON.stringify(await evaluate('window.__keys')), 'focus:', await evaluate('document.hasFocus()'), 'active:', await evaluate('document.activeElement && document.activeElement.tagName'));
    const frames = await send('Page.getFrameTree'); log('frames:', JSON.stringify(frames.frameTree.childFrames?.map((f) => f.frame.url) ?? []));
    return;
  }
  await waitTick((await tick()) + 30);
  // the character only holds the controls' handle once something jolts its hands onto it; a Space tap does (until
  // then the arrow keys drive the character, not the controls)
  if ('grab-test' in args) {
    for (let i = 0; i < 10; i++) { await waitTick((await tick()) + 30); log('tick', await tick(), 'holding:', await evaluate('!!window.__session._character.userVehicle')); }
    const grabKey = args['grab-test'];
    if (grabKey !== '1') { const at = await key(grabKey, true); await waitTick(at); await key(grabKey, false); await waitTick(at + 40); log('after', grabKey, 'holding:', await evaluate('!!window.__session._character.userVehicle')); }
    return;
  }
  if (!('no-grab' in args)) { const at = await key('Space', true); await waitTick(at); await key('Space', false); await waitTick(at + 40); log('after a Space tap, holding the handle:', await evaluate('!!window.__session._character.userVehicle')); }
  if (args.screenshot) {
    // the controls sit right of the character, near the top of the game: zoom on them (args.clip = "x,y,w,h" in the game's own pixels)
    const [cx, cy, cw, ch] = (args.clip ?? '330,0,250,170').split(',').map(Number);
    const shot = await send('Page.captureScreenshot', { format: 'png', clip: { x: fx + cx, y: fy + cy, width: cw, height: ch, scale: 3 } });
    writeFileSync(args.screenshot, Buffer.from(shot.data, 'base64'));
    log('wrote', args.screenshot);
    return;
  }
  const settleEvents = (await evaluate('window.__probe.length'));
  const presses = [];
  for (const step of makePlan()) {
    await waitTick((await tick()) + 1);
    let pressTick = 0;
    for (const name of step.press) { pressTick = await key(name, true); }
    await waitTick(pressTick - 1 + step.hold);
    let releaseTick = 0;
    for (const name of step.press) { releaseTick = await key(name, false); }
    const record = { keys: step.press, hold: step.hold, pressTick, releaseTick };
    if (step.again) {
      await waitTick(releaseTick - 1 + step.gap);
      record.againTick = 0;
      for (const name of step.press) { record.againTick = await key(name, true); }
      await waitTick(record.againTick - 1 + step.hold);
      for (const name of step.press) { record.againReleaseTick = await key(name, false); }
      await waitTick(record.againReleaseTick + 25);
    } else {
      await waitTick(releaseTick + step.gap);
    }
    record.endTick = await tick();
    record.holding = await evaluate('!!(window.__session._character && window.__session._character.userVehicle)');
    presses.push(record);
  }
  // --eval EXPR: an expression evaluated in the game after the plan, printed as JSON (window.__session is the game session)
  if (args.eval) console.log('eval:', JSON.stringify(await evaluate(args.eval)));
  const raw = await evaluate('window.__probe');
  const events = raw.slice(settleEvents).map(([t, x, y]) => ({ tick: t, label: labelAt(x, y) })).filter((e) => e.label);
  const keyLog = (await evaluate('window.__probeKeys || []'));
  const bit = { Left: 1, Right: 2, Up: 4, Down: 8, Space: 16, Shift: 32, Ctrl: 64 };
  for (const p of presses) {
    p.events = events.filter((e) => e.tick >= p.pressTick - 1 && e.tick <= p.endTick).map((e) => [e.tick - p.pressTick, e.label]);
    // the ticks (relative to the press) in which the game saw each of this step's keys as pressed
    p.seen = Object.fromEntries(p.keys.map((k) => [k, keyLog.filter(([t, b]) => t >= p.pressTick - 2 && t <= p.endTick && (b & bit[k])).map(([t]) => t - p.pressTick)]));
  }
  log('probe events:', raw.length, 'first:', JSON.stringify(raw.slice(0, 12)), 'distinct positions:', JSON.stringify([...new Set(raw.map(([, x, y]) => `${x},${y}`))].slice(0, 30)));
  writeFileSync(out, JSON.stringify({ level: levelName, plan, presses }, null, 1));
  // --- summary
  for (const p of presses) {
    const own = (label) => p.keys.some((k) => label.startsWith(k));
    const rel = p.releaseTick - p.pressTick;
    const on = p.events.filter(([, l]) => l.endsWith('On') && own(l));
    const off = p.events.filter(([, l]) => l.endsWith('Off') && own(l));
    const foreign = p.events.filter(([, l]) => !own(l));
    const firstOn = on.length ? on[0][0] : '-';
    const offAfter = off.filter(([t]) => t >= rel);
    const firstOff = offAfter.length ? offAfter[0][0] - rel : '-';
    const lateOn = on.filter(([t]) => t >= rel && (p.againTick === undefined || t < p.againTick - p.pressTick)).map(([t]) => t - rel);
    const earlyOff = off.filter(([t]) => t < rel).map(([t]) => t);
    console.log(`${p.holding ? ' ' : '!'}${p.keys.join('+').padEnd(11)} hold ${String(p.hold).padStart(2)}${p.againTick !== undefined ? ` again after ${p.againTick - p.releaseTick}` : ''}: on after ${firstOn}, ${on.length} on, off ${firstOff} after release`
      + `${lateOn.length ? `, ON AFTER RELEASE at +${lateOn.join(',+')}` : ''}${earlyOff.length ? `, OFF WHILE HELD at ${earlyOff.join(',')}` : ''}${foreign.length ? `, OTHER KEYS: ${foreign.map(([t, l]) => `${l}@${t}`).join(' ')}` : ''}`);
  }
  // per key, over its single-key presses: on and off delays (mean, max), presses with no "on", presses with an "on" after the release, presses that moved another key's triggers
  const singles = presses.filter((p) => p.keys.length === 1 && p.againTick === undefined);
  const lines = [];
  for (const name of names) {
    const mine = singles.filter((p) => p.keys[0] === name);
    if (!mine.length) continue;
    const ons = [], offs = []; let missing = 0, late = 0, foreign = 0;
    for (const p of mine) {
      const rel = p.releaseTick - p.pressTick;
      const on = p.events.filter(([, l]) => l === `${name}On`);
      const off = p.events.filter(([t, l]) => l === `${name}Off` && t >= rel);
      if (on.length) { ons.push(on[0][0]); } else { missing++; }
      if (off.length) { offs.push(off[0][0] - rel); }
      if (on.some(([t]) => t > rel)) { late++; }
      if (p.events.some(([, l]) => !l.startsWith(name))) { foreign++; }
    }
    const stat = (a) => (a.length ? `${(a.reduce((x, y) => x + y, 0) / a.length).toFixed(1)}/${Math.max(...a)}` : '-');
    lines.push(`${name.padEnd(6)} on ${stat(ons).padEnd(7)} off ${stat(offs).padEnd(7)} missed ${missing}  late-on ${late}  other-keys ${foreign}  (of ${mine.length})`);
  }
  console.log('summary (mean/max ticks):\n' + lines.join('\n'));
  if (plan === 'taps') {
    const rows = [];
    for (const name of ['Up', 'Down', 'Left', 'Right']) {
      rows.push(`${name.padEnd(6)} ` + [1, 2, 3].map((hold) => {
        const mine = singles.filter((p) => p.keys[0] === name && p.hold === hold);
        const missed = mine.filter((p) => !p.events.some(([, l]) => l === `${name}On`)).length;
        const extra = mine.filter((p) => p.events.filter(([, l]) => l === `${name}On`).length > 1 || p.events.some(([, l]) => !l.startsWith(name))).length;
        return `${hold}-tick taps missed ${missed}/${mine.length}${extra ? ` (${extra} with extra pulses)` : ''}`;
      }).join(', '));
    }
    console.log('taps:\n' + rows.join('\n'));
  }
  console.log(`wrote ${out}`);
}

main().then(() => { cleanup(); process.exit(0); }, (e) => { console.error(e); cleanup(); process.exit(1); });
