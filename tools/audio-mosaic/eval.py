"""Score renders against the song they aim at, the same way for every setting (the fit's own loss changes with its band
count, weighting and divergence, so it can't compare runs).

The target is the song, or stems summed with their gains. Both are cut into 64 log-mel bands every 10 ms; the render is
shifted to the target's overall level (median difference over the louder bins), and the score is the mean absolute
difference in dB over bins where either is within 60 dB of the target's loudest. Also: the same for attacks (the rise
in dB over 10 ms, where it rises) and the pitch error (L1 distance of the shares of the 12 pitch classes, on steps with
tonal energy). With --vocals, also the vocal range (300 Hz to 4 kHz), overall and while the vocals are loud.

    python3 eval.py --song song.wav --start 20 --seconds 30 render1.wav render2.wav ...
    python3 eval.py --stem instrumental.wav --stem vocals.wav:2 --vocals vocals.wav:2 render.wav

The renders start where the excerpt does (mosaic.py's --start and --seconds).
"""
import argparse
import os
import subprocess

import numpy as np

SR = 22050
HOP = 220


def load(path, start, seconds):
    cmd = ["ffmpeg", "-v", "error"]
    if start:
        cmd += ["-ss", str(start)]
    if seconds:
        cmd += ["-t", str(seconds)]
    cmd += ["-i", path, "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"]
    return np.frombuffer(subprocess.run(cmd, capture_output=True, check=True).stdout, dtype=np.float32)


def summed(specs, start, seconds):
    """PATH[:GAIN] specs, summed."""
    parts = []
    for spec in specs:
        path, _, gain = spec.partition(":")
        parts.append(load(path, start, seconds) * (float(gain) if gain else 1.0))
    n = min(len(p) for p in parts)
    return sum(p[:n] for p in parts)


def logmel(x, bands=64):
    n = 1024
    frames = np.lib.stride_tricks.sliding_window_view(x, n)[::HOP] * np.hanning(n)
    spec = np.abs(np.fft.rfft(frames, axis=1)) ** 2
    freqs = np.fft.rfftfreq(n, 1 / SR)
    mel = lambda f: 2595 * np.log10(1 + f / 700)
    imel = lambda m: 700 * (10 ** (m / 2595) - 1)
    edges = imel(np.linspace(0, mel(SR / 2), bands + 1))
    centres = (edges[:-1] + edges[1:]) / 2
    p = np.stack([spec[:, (freqs >= lo) & (freqs < hi)].sum(1) + 1e-12 for lo, hi in zip(edges[:-1], edges[1:])], 1)
    return 10 * np.log10(p), centres


def chroma(x, tuning, n=4096, hop=1102):
    frames = np.lib.stride_tricks.sliding_window_view(x, n)[::hop] * np.hanning(n)
    spec = np.abs(np.fft.rfft(frames, axis=1)) ** 2
    f = np.fft.rfftfreq(n, 1 / SR)
    ok = (f >= 80) & (f <= 4000)
    pc = np.round(12 * np.log2(f[ok] / tuning)).astype(int) % 12
    c = np.zeros((len(spec), 12))
    for k in range(12):
        c[:, k] = spec[:, ok][:, pc == k].sum(1)
    return c


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("renders", nargs="+")
    ap.add_argument("--song", help="the target (or give --stem)")
    ap.add_argument("--stem", action="append", default=[], help="PATH[:GAIN], summed into the target")
    ap.add_argument("--vocals", help="PATH[:GAIN]: also score the vocal range, overall and while the vocals are loud")
    ap.add_argument("--start", type=float, default=0)
    ap.add_argument("--seconds", type=float, default=None)
    ap.add_argument("--tuning", type=float, default=440.0, help="A4 in Hz for the pitch classes")
    args = ap.parse_args()
    if not args.song and not args.stem:
        ap.error("give --song or --stem")
    song = summed([args.song] if args.song else args.stem, args.start, args.seconds)
    target, centres = logmel(song)
    vband = (centres >= 300) & (centres <= 4000)
    singing = None
    if args.vocals:
        vocals, _ = logmel(summed([args.vocals], args.start, args.seconds)[: len(song)])
        singing = vocals[:, vband].max(1) > target.max() - 30

    def diff(t, r):
        m = min(len(r), len(t))
        t, r = t[:m], r[:m]
        r = r - np.median((r - t)[t > t.max() - 30])
        return np.abs(r - t), np.maximum(t, r) > t.max() - 60

    def attacks(x):
        return np.maximum(np.diff(x, axis=0), 0)

    t_att = attacks(target)
    tc = chroma(song, args.tuning)
    t_tot = tc.sum(1)
    t_loud = t_tot > t_tot.max() * 10 ** -3
    tc = tc / (t_tot[:, None] + 1e-12)
    columns = ["overall"] + (["vocal range", "while singing"] if singing is not None else []) + ["attacks", "pitch"]
    print(f"{'':40}" + "".join(f"{c:>15}" for c in columns))
    for path in args.renders:
        x = load(path, 0, args.seconds)
        r = logmel(x)[0]
        d, active = diff(target, r)
        k = min(len(r), len(target)) - 1
        att = np.abs(attacks(r)[:k] - t_att[:k])[active[1:k + 1]].mean()
        rc = chroma(x, args.tuning)
        kk = min(len(rc), len(tc))
        rc = rc[:kk] / (rc[:kk].sum(1, keepdims=True) + 1e-12)
        pitch = np.abs(rc - tc[:kk]).sum(1)[t_loud[:kk]].mean()
        values = [d[active].mean()]
        if singing is not None:
            sing = singing[: len(d)]
            values += [d[:, vband][active[:, vband]].mean(), d[sing][:, vband][active[sing][:, vband]].mean()]
        values += [att, pitch]
        print(f"{os.path.basename(path)[-40:]:40}" + "".join(f"{v:15.3f}" for v in values))


if __name__ == "__main__":
    main()
