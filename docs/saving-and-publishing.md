# Saving and publishing

What the game's server accepts, measured against the live server between 2026-10 and 2026-11
 (`tools/game-mod/tools/save-level.mjs`). The server answers most refusals with an empty HTTP 200 and stores
nothing, so a save that "worked" may not have: read the level back to be sure (`save-level.mjs --verify`).

## What a saved level is

A save sends the level XML compressed with zlib, encrypted with Blowfish in CBC mode (the key is `eatshit` followed by
the account's user id, the IV `abcd1234`, PKCS#5 padding), then base64-encoded, as a form POST to `set_level.hw`. That
encrypted blob is the level's record. Loading a level fetches the record (`get_level.hw`, action `get_record`, which
answers the raw encrypted bytes) and reverses the steps.

## The size cap: 900,000 bytes of record

- The server stores a record of at most **900,000 bytes** (1,200,000 base64 characters): 900,000 was stored, 900,001
  wasn't (2026-10-05, by bisecting with raw records on a private test level).
- The cap is on the compressed, encrypted record, not the XML. XML that compresses well can be huge: 8, 27 and 60 MB
  levels saved fine. A 25.8 MB level of varied triggers compressed to 2.0 MB and didn't.
- It is a check on the record itself: padding the request with a 1.4 MB unused field didn't matter, a record of
  900,000 zero bytes was stored, and an upload slowed to 59 seconds was stored too.
- An oversized save gets the same empty reply as any other refused save.

## Making the record smaller

The game always compresses with zlib, but loading only inflates whatever stream it gets. So any encoder that writes a
standard zlib stream gives a smaller record that loads identically:

| Encoder | A 7 MB level | Notes |
| --- | --- | --- |
| zlib 6 (what the game uses) | 604,560 bytes | |
| zlib 9 | 585,548 bytes | |
| Zopfli, 15 iterations | 392,951 bytes | |
| Zopfli, 50 iterations | 385,897 bytes | 20 seconds natively |
| Zopfli, 200 iterations | 385,642 bytes | gains flatten out |

On a level right at the cap, ECT (Efficient Compression Tool, level 10300) was another half a percent smaller than
Zopfli at 50 iterations (896,416 against 902,075 bytes). Levels this repetitive gain far more from a better encoder
than the usual few percent. `save-level.mjs --zopfli` and `--ect` use them. Making the XML itself smaller helps too:
`LevelCruncher.MinifyRelayTriggers` leaves out fields the game doesn't need.

## Other rules for a save

- **The level's name can be at most 20 characters.** A longer one is refused with the empty reply.
- **Saving again within about a minute** is refused with `failure:time_lockout` (45-second waits were sometimes not
  enough).
- A new save (`action=create`) answers `success:` and the new level's id; an update (`action=update`) answers
  `success:true`.
- Saved levels are private until published.
- A save always sends the whole record. There is no append or chunked upload, and the record format always uses
  zlib, so there is no other algorithm to ask for.
- The level's forced character (`playable_character`) is sent with the save, not read from the XML: the game sends
  the character from the level's `<info c=".." f="t">`, or 0 for none.

## Publishing

- Publishing (`action=publish`) makes a level public, and **can't be undone or updated**. The editor doesn't offer to
  overwrite a published level, and the server refuses it too: an update of a published level answers
  `failure:app_error` and leaves it unchanged (2026-10-28). A fixed version is a new level, without the old one's
  plays, ratings and replays.
- An account can publish about **one level a day**. The limit counts the account's recent published levels that still
  exist: a publish 21.5 hours after the last one was refused with `failure:time_lockout`, and was accepted straight
  away after deleting that earlier level (2026-10-30).
- The description (`user_comment`) is public once the level is published.

## The request quota

The server blocks an IP address for 24 hours (HTTP 429) after roughly 500 requests in a day, and a request sent during
a block starts the 24 hours again. Playing the game counts too (every level loaded, every list in the level browser).
The tools in `tools/game-mod` record their requests and refuse to go past a safe share of the quota.
