import { prependToMethod } from './util.mjs';

// A test probe for levels driven by a script (tools/controls-probe.mjs). Everything is a no-op unless the page sets `window.__probe` to an array:
// the session puts itself on `window.__session` every frame (so a script can read `_iteration`, the game's own tick counter), and every trigger
// that runs its actions pushes [tick, x, y] (its position identifies it). Nothing else changes.
export const probeSession = prependToMethod({
  name: 'probe-session',
  description: 'session run: expose the session to a test script when window.__probe is set',
  method: 'run',
  mustContain: 'enableCameraMode',
  code: `if (window.__probe) { window.__session = this; }`,
});

export const probeTrigger = prependToMethod({
  name: 'probe-trigger',
  description: 'trigger singleAction: log [tick, x, y] when window.__probe is set',
  method: 'singleAction',
  mustContain: 'soundEffect',
  code: `if (window.__probe && this.session) { window.__probe.push([this.session._iteration, Math.round(this._x * 10) / 10, Math.round(this._y * 10) / 10]); }`,
});

// The keys the character sees as pressed, each tick: [tick, bits] with bits 1 left, 2 right, 4 up, 8 down, 16 space, 32 shift, 64 ctrl
export const probeKeys = prependToMethod({
  name: 'probe-keys',
  description: 'character checkKeyStates: log the pressed keys each tick when window.__probe is set',
  method: 'checkKeyStates',
  mustContain: 'this.dead',
  code: `if (window.__probe && this._session) { (window.__probeKeys || (window.__probeKeys = [])).push([this._session.iteration, (this.leftPressed ? 1 : 0) | (this.rightPressed ? 2 : 0) | (this.upPressed ? 4 : 0) | (this.downPressed ? 8 : 0) | (this.spacePressed ? 16 : 0) | (this.shiftPressed ? 32 : 0) | (this.ctrlPressed ? 64 : 0)]); }`,
});
