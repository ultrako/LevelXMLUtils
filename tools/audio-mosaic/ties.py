"""Find stretches of a song that repeat earlier ones, and write the frame map mosaic.py --tie uses (a tied frame
replays the onsets of the frame it copies, so a repeat costs the level almost nothing).

  1. a 30 Hz log spectrogram of the audio (64 log-spaced bands, 80 dB range);
  2. for every 2 s window (60 frames), its closest earlier window at least 2 s back: mean |dB| difference and lag;
  3. greedy: the longest runs (at least 4 s of window starts) where one lag keeps the window within THRESHOLD dB,
     each taking only frames no longer run has; each run copies [t, u + 59) from lag frames earlier. A copy is then
     checked against the frames it really plays (a copy of a copy can drift), and frames that drift past the threshold
     are left untied.

    python3 ties.py SONG THRESHOLD --out ties.json

Use the part that repeats most exactly (with stems, the instrumental). Steps 1 and 2 are cached next to --cache, by
the audio file's name, size and time (step 2 takes a few minutes on a whole song). Lower thresholds tie less and
sound closer to the song; 3.5 dB was inaudible on Bad Apple.
"""
import argparse
import json
import os
import subprocess

import numpy as np

W, MIN_LAG, MIN = 60, 60, 120


def logmel(path):
    sr = 22050
    x = np.frombuffer(subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-ac", "1", "-ar", str(sr), "-f", "f32le", "-"],
                                     capture_output=True, check=True).stdout, np.float32)
    hop, win = sr // 30 * 1.0, 2048
    n = int(len(x) / hop) - 2
    idx = (np.arange(n) * hop).astype(int)
    frames = np.stack([x[i:i + win] if i + win <= len(x) else np.pad(x[i:], (0, win - (len(x) - i))) for i in idx])
    P = np.abs(np.fft.rfft(frames * np.hanning(win), axis=1)) ** 2
    edges = np.unique(np.geomspace(2, win // 2, 65).astype(int))
    L = 10 * np.log10(np.stack([P[:, a:b].sum(1) for a, b in zip(edges[:-1], edges[1:])], 1) + 1e-10)
    return np.maximum(L - L.max(), -80)


def closest_earlier(L):
    n = len(L)
    best = np.full(n, np.inf, np.float32)
    bestlag = np.zeros(n, int)
    k = np.ones(W, np.float32) / W
    for lag in range(MIN_LAG, n - W):
        s = np.convolve(np.abs(L[lag:] - L[:-lag]).mean(1), k, "valid")  # the window starting at t vs t - lag
        t = np.arange(lag, lag + len(s))
        better = s < best[t]
        best[t[better]] = s[better]
        bestlag[t[better]] = lag
    return best, bestlag


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("song", help="the audio to find repeats in (with stems, the instrumental)")
    ap.add_argument("threshold", type=float, help="mean dB difference under which a 2 s window counts as a repeat")
    ap.add_argument("--out", required=True)
    ap.add_argument("--untied-before", type=float, default=0, help="seconds at the start never tied (a quiet intro "
                    "can pass the threshold but sound different by ear)")
    ap.add_argument("--max-plays", type=int, default=0, help="most places one frame may stand for (0: any number)")
    ap.add_argument("--cache", default=os.path.expanduser("~/.cache/audio-mosaic/ties"))
    args = ap.parse_args()
    os.makedirs(args.cache, exist_ok=True)
    st = os.stat(args.song)
    key = f"{os.path.basename(args.song)}-{st.st_size}-{int(st.st_mtime)}"
    cache = os.path.join(args.cache, f"{key}-logmel.npy")
    if not os.path.exists(cache):
        np.save(cache, logmel(args.song))
    L = np.load(cache).astype(np.float32)
    cache_best, cache_lag = os.path.join(args.cache, f"{key}-best.npy"), os.path.join(args.cache, f"{key}-bestlag.npy")
    if not os.path.exists(cache_best):
        b, g = closest_earlier(L)
        np.save(cache_best, b)
        np.save(cache_lag, g)
    best, lag = np.load(cache_best), np.load(cache_lag)
    n, TH = len(best), args.threshold

    runs = []
    for g in sorted(set(lag[np.isfinite(best) & (best < TH)].tolist())):
        s = np.convolve(np.abs(L[g:] - L[:-g]).mean(1), np.ones(W) / W, "valid")
        ok = np.zeros(n, bool)
        ok[g:g + len(s)] = s < TH
        t = 0
        while t < n:
            if ok[t]:
                u = t
                while u < n and ok[u]:
                    u += 1
                if u - t >= MIN:
                    runs.append((u - t, t, u, g))
                t = u
            else:
                t += 1
    runs.sort(reverse=True)
    used = np.zeros(n, bool)
    chosen = []
    for length, t, u, g in runs:
        if (~used[t:u]).sum() >= MIN:
            used[t:u] = True
            chosen.append((t, u, g))
    fmt = lambda f: f"{int(f / 30) // 60}:{f / 30 % 60:04.1f}"
    for t, u, g in sorted(chosen):
        print(f"  {fmt(t)}-{fmt(u + W - 1)} repeats {fmt(t - g)}-{fmt(u + W - 1 - g)} (lag {g / 30:.2f} s)")

    # Each repeat [t, u + 59) copies the frames lag earlier; overlaps go to the earlier stretch.
    n_frames = n + W - 1
    canon = np.arange(n_frames)
    owned = np.zeros(n_frames, bool)
    stretches = []
    for t, u, g in sorted(chosen):
        a, b = t, min(u + W - 1, n_frames)
        while a < b and owned[a]:
            a += 1
        if b - a < W:
            continue
        owned[a:b] = True
        stretches.append((int(a), int(b), int(g)))
    def window_distance(frames, roots):
        """Each frame's 2 s window against its root's, mean |dB| (windows past the end are cut short)."""
        return np.array([np.abs(L[f:f + W] - L[r:r + len(L[f:f + W])]).mean() for f, r in zip(frames, roots)])

    kept, dropped = [], 0
    plays = np.ones(n_frames, int)
    for a, b, g in stretches:
        roots = canon[np.arange(a, b) - g]
        # A chained copy plays its root's onsets: check it against those, and leave untied the frames that drift past the
        # threshold or whose root already stands for --max-plays places.
        ok = window_distance(np.arange(a, b), roots) < TH
        ok &= np.arange(a, b) >= args.untied_before * 30
        if args.max_plays:
            ok &= plays[roots] < args.max_plays
        # Kept as stretches of at least 2 s, so a repeat stays a run of the song rather than scattered frames.
        f = a
        while f < b:
            if ok[f - a]:
                e = f
                while e < b and ok[e - a]:
                    e += 1
                if e - f >= W:
                    canon[f:e] = canon[np.arange(f, e) - g]
                    np.add.at(plays, canon[f:e], 1)
                    kept.append((int(f), int(e), int(g)))
                else:
                    dropped += e - f
                f = e
            else:
                dropped += 1
                f += 1
    stretches = kept
    print(f"{dropped} frames left untied (too far from the frames they would play, or their root used up)")
    print(f"most places one frame stands for: {plays.max()}")
    out = args.out
    json.dump({"stretches": stretches, "canon": canon.tolist()}, open(out, "w"))
    print(f"threshold {TH} dB: {len(stretches)} stretches, {np.mean(canon != np.arange(n_frames)):.1%} of frames copy "
          f"earlier ones -> {out}")


if __name__ == "__main__":
    main()
