const LOCAL_LEVEL = `(function () {
  var opened = null;
  try { opened = window.parent.__modLevel; } catch (e) {}
  if (opened) { window.xml = opened.xml; window.xmlName = opened.name; return; }
  // with no ?local=, the server answers with --level FILE if it was given one, or with nothing
  var local = new URLSearchParams(window.parent.location.search).get('local') || '';
  document.write('<script src="/__mod/level.js?f=' + encodeURIComponent(local) + '"><\\/script>');
})();`;

// The level browser's list from the play page's open folder (window.parent.__modFolder: { name, levels: [{ name, size,
// date, character, read() }] }), for patches/local-browser.mjs and local-level.mjs. The browser's list requests are
// answered the way the site answers them; other requests (favourites, an author's levels) still go to the site, and
// with no folder open everything does. A listed level's id is -1000 - its place in the folder.
const LOCAL_FOLDER = `(function () {
  var SET = 500; // levels in a set, as the browser requests them
  var listed = {};
  function folder() { try { return window.parent.__modFolder || null; } catch (e) { return null; } }
  function esc(t) { return String(t).replace(/[&<>"]/g, function (c) { return '&#' + c.charCodeAt(0) + ';'; }); }
  function day(ms) { var d = new Date(ms); return d.getFullYear() + '-' + ('0' + (d.getMonth() + 1)).slice(-2) + '-' + ('0' + d.getDate()).slice(-2); }
  window.__modBrowse = function (browser, request) {
    var open = folder(), v = request.data || {};
    if (!open || (v.action !== 'get_all' && v.action !== 'search_by_name')) return browser.loader.load(request);
    var term = v.action === 'search_by_name' ? String(v.sterm || '').toLowerCase() : '';
    var levels = open.levels.map(function (l, i) { return { level: l, id: -1000 - i }; })
      .filter(function (e) { return e.level.name.toLowerCase().indexOf(term) > -1; });
    // newest and oldest by the file's date; the other orders (plays, rating) by name
    var by = v.sortby === 'newest' ? function (a, b) { return b.level.date - a.level.date; }
      : v.sortby === 'oldest' ? function (a, b) { return a.level.date - b.level.date; }
      : function (a, b) { return a.level.name.localeCompare(b.level.name); };
    var page = Math.max(1, Number(v.page) || 1);
    levels = levels.sort(by).slice((page - 1) * SET, page * SET);
    var xml = '<?xml version="1.0" encoding="UTF-8"?><lvs>' + levels.map(function (e) {
      listed[e.id] = e.level;
      return '<lv id="' + e.id + '" ln="' + esc(e.level.name) + '" ui="0" un="' + esc(open.name) + '" rg="0" vs="0" ps="0" dp="' +
        day(e.level.date) + '" pc="' + (e.level.character || 0) + '"><uc><![CDATA[' +
        (e.level.path || e.level.name).replace(/]]>/g, '') + ', ' + Math.round(e.level.size / 1024) + ' KB]]></uc></lv>';
    }).join('') + '</lvs>';
    setTimeout(function () { browser.loader = { data: xml, removeEventListener: function () {} }; browser.levelDataLoaded(); }, 0);
  };
  // the file of a level listed above, as a promise of its text, or null for any other id
  window.__modLocalLevel = function (id) { return listed[id] ? listed[id].read() : null; };
})();`;

// Text transforms the local proxy applies to HTML from the real site.
// Each: { name, match(pathname) -> bool, apply(text, { origin }) -> text }.
// Like the JS patches, these anchor on stable strings, and the proxy warns when one stops matching.
export const htmlTransforms = [
  {
    // The loader page refuses to run (and loops on `debugger`) unless it and its parent are on a
    // *.totaljerkface.com host. endsWith("") is true for any host, including localhost.
    name: 'loader-domain-lock',
    match: (pathname) => pathname.endsWith('/happy-wheels-js/index.tjf'),
    apply: (text) => text.replace(/\.endsWith\((["'])totaljerkface\.com\1\)/g, '.endsWith("")'),
  },
  {
    // The game sends its requests (levels, logging in, ratings) to HW_SETTINGS.siteURL, which the loader page builds from
    // the hostname alone. On localhost:8080 that is port 80, where nothing answers ("Sorry, there was an IO Error" in the
    // level browser): use the host, which keeps the port.
    name: 'site-url-port',
    match: (pathname) => pathname.endsWith('/happy-wheels-js/index.tjf'),
    apply: (text) => text.replace('"//" + document.location.hostname + "/"', '"//" + document.location.host + "/"'),
  },
  {
    // Load the level into window.xml before the game script runs (the patched loaders read it; see
    // patches/local-level.mjs): a file opened with the play page's Browse button (the page keeps it, and this frame is on
    // the same origin, so it reads it from there), or else ?local=NAME.xml on the page's address, from --level-dir
    // or --level FILE (the way the game itself reads ?level_id=).
    name: 'inject-local-level',
    match: (pathname) => pathname.endsWith('/happy-wheels-js/index.tjf'),
    apply: (text) => text.replace('</head>', `<script>${LOCAL_LEVEL}</script><script>${LOCAL_FOLDER}</script></head>`),
  },
  {
    // Since the Steam release the game page shows an "Open Happy Wheels in Steam?" banner and, if the
    // banner's "Remember" box was ever ticked (localStorage open_in_steam=1), navigates to steam://
    // instead of loading the web game. Always load the web game, and hide the banner.
    name: 'skip-steam-prompt',
    match: (pathname) => pathname.endsWith('/happy_wheels.tjf'),
    apply: (text) => {
      const edits = [
        ['localStorage.getItem("open_in_steam") === "1"', 'false'],
        ['banner.style.display = "";', 'banner.style.display = "none";'],
      ];
      for (const [from, to] of edits) {
        if (!text.includes(from)) console.warn(`WARNING: skip-steam-prompt: anchor not found: ${from}`);
        text = text.replace(from, to);
      }
      return text;
    },
  },
  {
    // Keep navigation on the proxy instead of bouncing to the real site.
    name: 'absolute-urls',
    match: () => true,
    apply: (text, { origin }) => text.replace(/https?:\/\/(?:www\.)?totaljerkface\.com(?![\w.-])/g, origin),
  },
];
