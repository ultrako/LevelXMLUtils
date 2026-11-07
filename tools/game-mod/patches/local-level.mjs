import { prependToMethod } from './util.mjs';

// Play a level from a local XML file exactly like a published one (skipping the level editor).
// Everything here is a no-op unless `window.xml` is set (tools/serve.mjs --level sets it), so the
// same build still behaves like the stock game otherwise.
//
// A published level goes: enterSession -> loadLevelByID -> [get_level.hw: metadata]
//   -> openSessionController -> [get_level.hw get_record: encrypted XML] -> buildLevelSourceObject.
// The game already contains a dead debug path, loadXML(), that reads `window.xml` instead of the
// network; these three patches route the normal flow through it.

const LOCAL_LEVEL_ID = -2; // truthy (the loader skips falsy ids), != 1 (rejected as unsupported), and matches no real level

// 1. Take the "open level by id" path even though the URL has no level_id.
export const localLevelEnter = prependToMethod({
  name: 'local-level-enter',
  description: 'enterSession: use a fake level id when window.xml is set',
  method: 'enterSession',
  code: `if (window.xml) { this._levelID = ${LOCAL_LEVEL_ID}; }`,
});

// 2. Level metadata. Reuses the game's own parser by faking the get_level.hw response; the shape
//    below is what the real server returns for a public level.
export const localLevelMeta = prependToMethod({
  name: 'local-level-meta',
  description: 'level metadata loader: answer locally instead of POSTing get_level.hw',
  method: 'load',
  mustContain: '"get_level"',
  code: `if (window.xml && arguments[0] == ${LOCAL_LEVEL_ID}) {
  var __hwName = String(window.xmlName || "Local level").replace(/[&<>"]/g, function (c) { return "&#" + c.charCodeAt(0) + ";"; });
  // A published level's "pc" is its forced character (0 = the player picks one at character select), and the game
  // forces a character whenever pc > 0. Take it from the XML's own <info c="N" f="t">, like the level editor does.
  var __hwInfo = String(window.xml).match(/<info[^>]*>/);
  var __hwChar = 0;
  if (__hwInfo && / f="t"/.test(__hwInfo[0])) {
    var __hwC = __hwInfo[0].match(/ c="([0-9]+)"/);
    if (__hwC) { __hwChar = parseInt(__hwC[1], 10) || 0; }
  }
  this.loader = {
    data: '<?xml version="1.0" encoding="UTF-8"?><lvs><lv dp="2000-01-01" id="${LOCAL_LEVEL_ID}" ln="' + __hwName + '" pc="' + __hwChar + '" ps="0" rg="0" ui="0" un="local file" vs="0"><uc><![CDATA[Loaded from a local XML file.]]></uc></lv></lvs>',
    removeEventListener: function () {},
  };
  this.levelDataLoaded();
  return;
}`,
});

// 3. Level content. loadXML() already exists in the game: it parses window.xml and fires COMPLETE.
//    The method that requests the level record is the only one that mentions "get_record". It was
//    loadData() up to game build e779b9df; from cfbedf93 on, loadData() just calls loadFromServer()
//    (next to a new Steam-offline loadDownloaded()), so the request moved there. Accept either.
export const localLevelData = prependToMethod({
  name: 'local-level-data',
  description: 'level data loader: parse window.xml via the existing loadXML()',
  method: ['loadFromServer', 'loadData'],
  mustContain: '"get_record"',
  // A level from the level browser's local list (patches/local-browser.mjs) has an id of -1000 or less, and
  // window.__modLocalLevel(id) reads its file: loadXML() is given it through window.xml for the one call.
  code: `var __hwLocal = window.__modLocalLevel && window.__modLocalLevel(this.levelId);
if (__hwLocal) {
  var __hwSelf = this;
  __hwLocal.then(function (__hwText) {
    var __hwKept = window.xml;
    window.xml = __hwText;
    try { __hwSelf.loadXML(); } finally { window.xml = __hwKept; }
  }, function (__hwError) { console.error("[MOD] cannot read the level file: " + __hwError); });
  return;
}
if (window.xml && this.levelId == ${LOCAL_LEVEL_ID}) { return this.loadXML(); }`,
});
