"""Choose the frames a trigger video plays: per pixel, when to change it, trading how much of each frame's area the
screen gets right against how many changes there are (the level's size grows with them).

Coverage: each screen pixel's share of white in the source, from ffmpeg's area scaling (for a black-and-white source its
grey average is the white share). A pixel shown white gets its white share right, a black one its black share; the
score is the mean over pixels and frames (1 = every frame's area right). The video is resampled to the game's 30 frames
a second.

  python3 frames.py VIDEO table W H [W H ...]       accuracy and changes at each size (majority, and a plain threshold)
  python3 frames.py VIDEO sweep W H LAMBDA...       the same with the per-pixel trade-off (see choose)
  python3 frames.py VIDEO write W H LAMBDA OUT.raw [--budget FLIPS] [--actions ACTIONS]
                                                    the chosen frames (one byte a pixel, 0 or 1, frame by frame, row by
                                                    row) for Video.FromRawFrames; --budget: at most that many flips a
                                                    frame (see budget); --actions: at most that many trigger actions (see
                                                    budget_actions)
  python3 frames.py VIDEO grey W H LEVELS LAMBDA... LEVELS evenly spaced greys (2: black and white), the same trade-off;
                                                    a grey g shown where the white share is c gets 1 - |g - c| right
  python3 frames.py VIDEO writegrey W H LEVELS LAMBDA OUT.raw   grey frames (one byte a pixel, 0 to LEVELS - 1)

--filter adds an ffmpeg filter before the scaling (a crop, more contrast for a grey film: "eq=contrast=1.6"); --start
and --seconds take an excerpt. Lambda is in pixel-areas a change: a change has to win back that much area over the frames
it lasts. Small lambdas take out flicker nearly free; larger ones lose to a lower resolution at the same size.
"""
import argparse
import hashlib
import os
import subprocess

import numpy as np

CACHE = os.path.expanduser("~/.cache/video-frames")
args = None


def grey(w, h, flags):
    os.makedirs(CACHE, exist_ok=True)
    st = os.stat(args.video)
    key = hashlib.sha1(f"{os.path.abspath(args.video)}|{st.st_size}|{st.st_mtime}|{args.filter}|{args.start}|{args.seconds}".encode()).hexdigest()[:12]
    path = f"{CACHE}/{key}-{w}x{h}-{flags}.npy"
    if not os.path.exists(path):
        cmd = ["ffmpeg", "-v", "error"]
        if args.start:
            cmd += ["-ss", str(args.start)]
        if args.seconds:
            cmd += ["-t", str(args.seconds)]
        filters = ",".join(f for f in [args.filter, "fps=30", f"scale={w}:{h}:flags={flags}", "format=gray"] if f)
        cmd += ["-i", args.video, "-vf", filters, "-f", "rawvideo", "-"]
        raw = subprocess.run(cmd, capture_output=True, check=True).stdout
        np.save(path, np.frombuffer(raw, np.uint8).reshape(-1, h, w))
    return np.load(path)


def coverage(w, h):
    return grey(w, h, "area").astype(np.float32) / 255


def score(states, cov):
    return float(np.where(states, cov, 1 - cov).mean())


def changes(states):
    prev = np.concatenate([np.zeros_like(states[:1]), states[:-1]])
    return int((states != prev).sum())


def choose(cov, lam):
    """Per pixel, the white/black sequence minimising (area wrong) + lam * (changes), exactly (two states a frame, a
    dynamic program). Starts black, as the screen does. lam is in pixel-areas a change: a change has to win back that
    much area over the frames it lasts. lam may also be one value a frame (a change at frame t costs lam[t])."""
    n = len(cov)
    lams = np.broadcast_to(np.asarray(lam, np.float32), (n,))
    d0 = np.zeros(cov.shape[1:], np.float32)
    d1 = np.full(cov.shape[1:], lams[0], np.float32)
    back0 = np.zeros(cov.shape, bool)  # came from white
    back1 = np.zeros(cov.shape, bool)  # came from black
    for t in range(n):
        c = cov[t]
        if t > 0:
            stay0, switch0 = d0, d1 + lams[t]
            stay1, switch1 = d1, d0 + lams[t]
            back0[t] = switch0 < stay0
            back1[t] = switch1 < stay1
            d0, d1 = np.minimum(stay0, switch0), np.minimum(stay1, switch1)
        d0 = d0 + c
        d1 = d1 + (1 - c)
    states = np.zeros(cov.shape, bool)
    s = d1 < d0
    for t in range(n - 1, -1, -1):
        states[t] = s
        s = np.where(s, ~back1[t], back0[t]) if t > 0 else s
    return states


def choose_levels(cov, k, lam):
    """choose() for k evenly spaced levels (0 .. k-1, level j showing j / (k - 1)): per pixel the sequence minimising
    sum |level - c| + lam * (changes), starting black. Returns level indices."""
    g = np.linspace(0, 1, k).astype(np.float32)[:, None, None]
    d = np.full((k,) + cov.shape[1:], lam, np.float32)
    d[0] = 0
    back = np.zeros((len(cov),) + (k,) + cov.shape[1:], np.uint8)
    for t in range(len(cov)):
        if t > 0:
            best = d.argmin(0)
            low = d.min(0) + lam
            switch = low[None] < d
            back[t] = np.where(switch, best[None], np.arange(k)[:, None, None])
            d = np.minimum(d, low[None])
        else:
            back[t] = np.arange(k)[:, None, None]
        d = d + np.abs(g - cov[t][None])
    out = np.zeros(cov.shape, np.uint8)
    s = d.argmin(0)
    for t in range(len(cov) - 1, -1, -1):
        out[t] = s
        s = np.take_along_axis(back[t], s[None], 0)[0]
    return out


def budget(states, cov, most):
    """At most `most` flips a frame: a frame that wants more makes the flips that matter most (pixels whose white share
    is furthest from a half) and leaves the rest to the next frames, so a scene cut is spread over two or three frames
    (700 at 70x40 on Bad Apple: not noticeable by eye, and the most trigger work in a frame went from 53,000 to 41,000
    actions)."""
    shown = np.zeros(states.shape[1:], bool)
    out = np.empty_like(states)
    for i, wanted in enumerate(states):
        flips = np.argwhere(wanted != shown)
        if len(flips) > most:
            gain = np.abs(2 * cov[i][flips[:, 0], flips[:, 1]] - 1)
            flips = flips[np.argsort(-gain, kind="stable")[:most]]
        shown = shown.copy()
        shown[flips[:, 0], flips[:, 1]] ^= True
        out[i] = shown
    return out


def budget_actions(states, cov, most, flips_most=None, row=287.0, column=14.0, toggle=6.6, frame=278.0):
    """At most `most` trigger actions a frame, estimated (`row` a selected row, `column` a column command, `toggle` a
    toggled pixel, `frame` the frame's own; fitted to TriggerGraph's counts for VideoDecoder at 80x45 with bands and
    row steps, within 122 actions on average), and at most `flips_most` flips: a frame that wants more makes the flips
    that matter most (white share furthest from a half), skipping any that would go over, and leaves the rest to the
    next frames. Selecting a row costs far more than a flip, so a frame over the cap first drops lone flips in rows of
    their own."""
    shown = np.zeros(states.shape[1:], bool)
    out = np.empty_like(states)
    for i, wanted in enumerate(states):
        flips = np.argwhere(wanted != shown)
        rows = set(flips[:, 0].tolist())
        cost = frame + row * len(rows) + (column + toggle) * len(flips)
        if cost > most or (flips_most and len(flips) > flips_most):
            gain = np.abs(2 * cov[i][flips[:, 0], flips[:, 1]] - 1)
            chosen, used, cost = [], set(), frame
            for k in np.argsort(-gain, kind="stable"):
                y = int(flips[k, 0])
                extra = column + toggle + (0 if y in used else row)
                if cost + extra > most or (flips_most and len(chosen) >= flips_most):
                    continue
                chosen.append(k); used.add(y); cost += extra
            flips = flips[chosen]
        shown = shown.copy()
        shown[flips[:, 0], flips[:, 1]] ^= True
        out[i] = shown
    return out


def main():
    global args
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("video")
    ap.add_argument("command", choices=["table", "sweep", "write", "grey", "writegrey"])
    ap.add_argument("values", nargs="+")
    ap.add_argument("--filter", default="", help="an ffmpeg filter applied before the scaling")
    ap.add_argument("--start", type=float, default=None)
    ap.add_argument("--seconds", type=float, default=None)
    ap.add_argument("--budget", type=int, default=None, help="write: at most this many flips a frame")
    ap.add_argument("--actions", type=int, default=None, help="write: at most this many trigger actions a frame")
    args = ap.parse_args()
    cmd, v = args.command, args.values
    counts = {"write": 4, "writegrey": 5}
    if cmd in counts and len(v) != counts[cmd] or cmd == "table" and len(v) % 2:
        ap.error(f"wrong number of values for {cmd}: {' '.join(v)}")
    if cmd == "table":
        for w, h in zip(map(int, v[::2]), map(int, v[1::2])):
            cov = coverage(w, h)
            major = cov > 0.5
            plain = grey(w, h, "bicubic") > 127
            print(f"{w}x{h}: majority {score(major, cov):.5f} with {changes(major)} changes; bicubic over 127 {score(plain, cov):.5f} with {changes(plain)}", flush=True)
    elif cmd == "sweep":
        w, h = int(v[0]), int(v[1])
        cov = coverage(w, h)
        for lam in map(float, v[2:]):
            s = choose(cov, lam)
            print(f"{w}x{h} lambda {lam}: {score(s, cov):.5f} with {changes(s)} changes", flush=True)
    elif cmd == "grey":
        w, h, k = int(v[0]), int(v[1]), int(v[2])
        cov = coverage(w, h)
        g = np.linspace(0, 1, k).astype(np.float32)
        for lam in map(float, v[3:]):
            levels = choose_levels(cov, k, lam) if lam > 0 else np.rint(cov * (k - 1)).astype(np.uint8)
            print(f"{w}x{h} {k} levels, lambda {lam}: {1 - float(np.abs(g[levels] - cov).mean()):.5f} with {changes(levels)} changes", flush=True)
    elif cmd == "writegrey":
        w, h, k, lam, out = int(v[0]), int(v[1]), int(v[2]), float(v[3]), v[4]
        cov = coverage(w, h)
        g = np.linspace(0, 1, k).astype(np.float32)
        levels = choose_levels(cov, k, lam) if lam > 0 else np.rint(cov * (k - 1)).astype(np.uint8)
        levels.tofile(out)
        print(f"{w}x{h} {k} levels, lambda {lam}: {1 - float(np.abs(g[levels] - cov).mean()):.5f} with {changes(levels)} changes -> {out}")
    elif cmd == "write":
        w, h, lam, out = int(v[0]), int(v[1]), float(v[2]), v[3]
        cov = coverage(w, h)
        s = choose(cov, lam) if lam > 0 else cov > 0.5
        if args.actions:
            s = budget_actions(s, cov, args.actions, args.budget)
        elif args.budget:
            s = budget(s, cov, args.budget)
        s.astype(np.uint8).tofile(out)
        print(f"{w}x{h} lambda {lam}: {score(s, cov):.5f} with {changes(s)} changes -> {out}")


if __name__ == "__main__":
    main()
