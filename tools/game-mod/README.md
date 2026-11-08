# Playing levels locally

A modded build of the Happy Wheels web game, served from your own machine, that plays a level XML file like a
published level. It skips the level editor, whose import is slow and can't open very large levels.

```sh
cd tools/game-mod
npm install                     # Node 22 or later
node tools/update.mjs           # download and build the current game version
node tools/serve.mjs --level-dir ~/levels
# then open http://localhost:8080/play
```

- **The play page:** `/play` shows only the game, with a bar under it:
  - **Browse...** opens a level file, which plays at once, or a folder of them. A folder's levels (`*.xml` and
    `*.levelxml`, subfolders included) are listed in the game's own level browser ("Browse Levels"), in place of the
    site's: sort by newest or oldest goes by the file's date, the other orders by name, and "search by name"
    searches them. The page reads the files in the browser, so nothing is uploaded and there's no size limit.
  - **Close** lists the site's levels in the level browser again. Favourites and an author's levels always come from
    the site.
  - **Restart** reloads the game and plays the opened file again, read afresh if you've edited it.
- **A folder on the server:** `--level-dir DIR` is the folder open when the page loads, read from disk each time a
  level is played. `/play?local=NAME.xml` plays one of its levels directly. Without it, Browse still works. `--level
  FILE.xml` sets a level to play when none is chosen.
- **The character:** a level whose `<info>` has `f="t"` starts as its character `c`; otherwise you get character
  select.
- **Reloading:** once a level starts, the game rewrites the address to `?level_id=-2` (the fake id the local level
  gets), and reloading that address doesn't load the file again. Use Restart, or go back to the `?local=` address.
- **The site's own page:** `http://localhost:8080/happy_wheels.tjf?local=NAME.xml` works too, without the folder in the
  level browser.
- **Safety:** only plain `*.xml` names inside the folder are served. Any web page you visit can make your browser
  request localhost, so the server must not read arbitrary files.

`serve.mjs` is a reverse proxy for totaljerkface.com: it serves the patched game script, rewrites addresses and cookies
to work on localhost, and forwards everything else, logging each request.

## Game versions

The game changes when you choose, not when the site updates.

- **`update.mjs`:** downloads the site's current game script and builds it into `versions/<hash>/`. It then pins it
  in `versions/pinned.json`, the pair of scripts (`index` and `dependencies`) that `serve.mjs` tells the page to load.
  An update that changes only the dependencies script is pinned without a rebuild.
- **Checking and going back:** `node tools/update.mjs --check` says whether the site has moved on, and
  `node tools/update.mjs --pin HASH` goes back to a version built before. `serve.mjs` says at startup when the site
  has a newer version.
- **The cache:** the game's static files (the dependencies script, sounds, pictures) are kept in `cache/` the first
  time they are fetched. So a pinned version keeps working after the site deletes them, for as long as the server's
  API (levels, accounts) still accepts it.

## How the build works

1. **`tools/unpack.mjs`:** the game script hides the real code as a base64, zstd-compressed blob run through
   `new Function`. This finds it by its shape (a huge base64 run and the zstd magic `28 b5 2f fd`) and unpacks it.
2. **[webcrack](https://github.com/j4k0xb/webcrack):** undoes the obfuscator.io layer, which moves every string into
   a shuffled array.
3. **`patches/*.mjs`:** small edits found with an AST and spliced in by text offset, so the patched file differs from
   the deobfuscated one only where the patches are.

`node tools/build.mjs HASH --repatch` re-applies the patches to a version's deobfuscated stage, after you edit a
patch.

If a patch's anchor stops matching after a game update, the build stops and names it. Fix the anchor in
`patches/<name>.mjs`. Anchor on string literals and structure, never on minified names (`V`, `w0`, ...), which change
with every build. If unpacking fails, the packer changed; if webcrack's output is still unreadable, the obfuscator
did; try a newer webcrack.

## Patches

| Patch | What it does |
| --- | --- |
| `local-level-enter`, `local-level-meta`, `local-level-data` | play `window.xml` like a published level: a fake level id, metadata answered locally (with the forced character from the XML's `<info>`), and the level read by the game's own leftover `loadXML()`. They do nothing unless `window.xml` is set, or the level is one of a local folder's |
| `local-browser` | the level browser's list requests go through `window.__modBrowse`, which lists a local folder's levels when the play page has one open (`patches/html.mjs`), and asks the site otherwise |
| `hostname-hash-check` | anti-tamper: the game md5-hashes `location.hostname` against an allow-list and, on a mismatch, deletes every key on `window` (it hangs at "Loading... 100%"). Makes the check pass |
| `enter-session-gate` | anti-tamper: the main menu only opens when `location.hostname` is `totaljerkface.com` (a blank canvas otherwise, with no errors) |
| `session-host-bomb` | anti-tamper: when the hostname isn't `totaljerkface.com`, starting a level sets off timers that double a string every 35 to 50 ms. That grows the heap by about 40 MB a second and freezes the browser, and it looks like a leak in the level. The check never runs |
| `editor-domain-lock` | the level editor only works on `totaljerkface.com`; makes it pass |
| `editor-limits` | removes the level editor's shape limit (900) and art limit (10,000): it no longer refuses to test or save. The game's own load limits (physics proxies, the pair table) still apply |
| `probe-session`, `probe-trigger`, `probe-keys` | for the test harnesses: expose the session and log every trigger's activation and the keys each tick. Inert unless the page sets `window.__probe` |

`patches/html.mjs` changes the loader pages:
- **Addresses:** absolute addresses become localhost.
- **The server's address:** the game's requests go to the page's host with its port (the site's page drops the port).
- **Local levels:** it adds the scripts that load a level and list a folder's.
- **The Steam prompt:** it hides the "Open Happy Wheels in Steam?" banner and ignores a remembered "open in Steam"
  choice.

Restart `serve.mjs` after changing it. The game script is read again on every request.

If a future version renders blank on localhost, there's another hostname check. To bisect, serve the game to Chromium
under the real hostname (`--host-resolver-rules="MAP totaljerkface.com 127.0.0.1"`, `http://totaljerkface.com:<port>/`)
with `serve.mjs --script FILE`, trying the unpacked, deobfuscated and patched stages in turn. Then search the built file
for `location`, `hostname`, `enterSession` and 32-hex-digit md5 strings.

## Server tools

These talk to the game's server for your account.

- **The quota:** the server blocks an IP address for 24 hours (HTTP 429) after roughly 500 requests in a day, and a
  request sent during a block starts it again. So every tool here, and the game played through `serve.mjs`, records
  its requests in `~/.cache/hw-server/requests.jsonl` (`hw-quota.mjs`). The tools refuse to go past a safe share of
  that, or past a block.
- **`save-level.mjs`:** saves a level XML to your account, the same request the editor's Save sends, with
  `--file LEVEL.xml [--name NAME] [--id N]`. The name defaults to the file's, and can be at most 20 characters.
  `--dry-run` checks the level and its size without sending anything.
  - **Privacy:** saved levels are private. Publishing needs `--publish N`, which makes the level public and can't be
    undone. You can publish one level a day.
  - **Credentials:** it reads the Cookie header of a logged-in request and your user id from `~/.hw_cookie` and
    `~/.hw_user_id` (or the `HW_COOKIE` and `HW_USER_ID` environment variables). It never prints them.
  - **Size:** the server keeps at most 900,000 bytes of record (the compressed, encrypted level) and silently drops a
    bigger one, so the script refuses to send it.
  - **Compression:** `--zopfli` compresses the record about a third smaller than the game does (Zopfli, installed by
    `npm install`; about 10 seconds per MB of XML). `--ect` is another half a percent smaller, for a level just over
    the cap: build [ECT](https://github.com/fhanau/Efficient-Compression-Tool) and put `ect` on your PATH, or set
    `ECT` to its path.
  - **Other uses:** `--list` (your levels and their sizes), `--readback N`, `--delete N`, and `--verify` after a save.
- **`level-search.mjs`:** finds published levels the game's browser can't filter for. It lists levels the way the
  browser does, downloads them once (cached), and counts triggers, shapes and art the way the editor does. It also
  searches an offline SQLite archive with `--sqlite FILE`, and counts local files with `--analyze`.

## Test harnesses

Both play a level in headless Chromium through `serve.mjs`, starting it if nothing is listening on the port, and need
`chromium` on the PATH (they run it muted). If one is interrupted, check
`pgrep -af chromium` for a leftover browser: a second run on the same debugging port fights the first.

- **`watch-level.mjs`:** `--level NAME.xml --eval EXPR --script "wait 30; key Up 5; eval; shot a.png"` follows a script
  of waits, key presses, clicks, screenshots and CPU profiles, printing EXPR (evaluated in the game; `window.__session`
  is the session) with the tick.
- **`controls-probe.mjs`:** plays a level of keyboard controls (`NassingerControls` from `Toolkit/Controls`), presses
  keys at exact game ticks, and reports per press the ticks to each key's "on" and "off" triggers, and other keys'
  triggers firing.
