"""Make sounds.json, the list of the game's sounds mosaic.py fits with, from the game's dependencies script.

Each sound's id is its index in the game's sound list (what a sound trigger's `Sound` is), with its display name, the
file it plays, and where that file is in the audio sprite ([start ms, length ms]). The script also names the audio
sprite's folder; with --download, the sprite is fetched from the game's site next to sounds.json.

    python3 sounds.py --fetch [--cache ~/.cache/audio-mosaic]
    python3 sounds.py dependencies.<hash>.js [--download]

--fetch gets the current dependencies script from the game's site (the page reads its name from
happy-wheels-js/version.json; it changes with game updates) and the audio sprite: three requests. Or give a copy of
the script, and --download for the sprite.
"""
import argparse
import json
import os
import re
import urllib.request

SITE = "https://totaljerkface.com/happy-wheels-js/"
# Python's default user agent is refused; this one says what is asking
AGENT = {"User-Agent": "LevelXMLUtils sounds.py (fetches the sound list for audio-mosaic)"}


def get(url):
    return urllib.request.urlopen(urllib.request.Request(url, headers=AGENT)).read()


def parse(script):
    # The sprite map: JSON.parse('{"_":{"ArrowFire1":[0,293.4],...}}') (more keys may follow "_")
    m = re.search(r"""JSON\.parse\('(\{"_":\{.*?\}[^']*)'\)""", script)
    if not m:
        raise SystemExit("no audio sprite map in the script")
    spans = json.loads(m.group(1))["_"]
    names = dict(re.findall(r'_sfxDictionary\.set\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\)', script))
    # the list is declared empty first and filled in later: the longest assignment is the real one
    lists = [re.findall(r'"([^"]+)"', body) for body in re.findall(r"_sfxLookup\s*=\s*\[([^\]]*)\]", script)]
    lookup = max(lists, key=len, default=[])
    if not names or not lookup:
        raise SystemExit("no sound list in the script")
    src = re.search(r'"(assets-[^"/]+/audiosprite\.small\.ogg)"', script)
    sounds = []
    for i, name in enumerate(lookup):
        file = names.get(name)
        if file is None or file not in spans:
            raise SystemExit(f"sound {i} ({name}) is not in the sprite map")
        sounds.append({"id": i, "name": name, "file": file, "span": spans[file]})
    return sounds, src.group(1) if src else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("script", nargs="?", help="the game's dependencies.<hash>.js (or --fetch)")
    ap.add_argument("--fetch", action="store_true", help="get the script and the audio sprite from the game's site")
    ap.add_argument("--cache", default=os.path.expanduser("~/.cache/audio-mosaic"))
    ap.add_argument("--download", action="store_true", help="also fetch the audio sprite from the game's site")
    args = ap.parse_args()
    if args.fetch:
        version = json.loads(get(SITE + "version.json"))
        script = get(f"{SITE}js/dependencies.{version['dependencies']}.js").decode("utf-8")
        args.download = True
    elif args.script:
        script = open(args.script, encoding="utf-8").read()
    else:
        ap.error("give the script or --fetch")
    sounds, sprite = parse(script)
    os.makedirs(args.cache, exist_ok=True)
    out = os.path.join(args.cache, "sounds.json")
    json.dump(sounds, open(out, "w"))
    print(f"{len(sounds)} sounds -> {out}")
    if sprite:
        print(f"audio sprite: {SITE}{sprite}")
    if args.download:
        if not sprite:
            raise SystemExit("the script doesn't name the audio sprite")
        path = os.path.join(args.cache, "audiosprite.small.ogg")
        with open(path, "wb") as f:
            f.write(get(SITE + sprite))
        # the mixes are rendered from a 48 kHz copy, made again from the new sprite
        stale = os.path.join(args.cache, "sprite48k.f32")
        if os.path.exists(stale):
            os.remove(stale)
        print(f"-> {path}")


if __name__ == "__main__":
    main()
