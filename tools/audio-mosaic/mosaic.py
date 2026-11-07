"""Audio mosaicing: approximate a song with the game's sound effects.

Each sound can only be started on a game frame (30 a second), at a fixed volume, and always plays to its end.
So the song's spectrogram is modelled as a sum of the sounds' spectrograms, each shifted to the frame it starts
on and scaled by its volume squared (powers add for unrelated sounds): a convolutive NMF whose templates are the
fixed sounds, solved for the activations only. The activations are then thinned to a sparse set of onsets,
refitted on that support, and rounded to volume steps, and the real samples are mixed into a WAV to listen to.

The song can be one file, or stems (from a source separator such as Demucs), each fitted on its own with its own
pool of sounds, onset budget and gain: the vocals with the characters' voice lines and a boost, so the singing is
not drowned by the bass and drums, which hold most of the power.

Inputs (see README.md): sounds.json (id -> span in the audio sprite) and audiosprite.small.ogg from the game,
and the song.

    python3 mosaic.py --song badapple.webm --out /tmp/mosaic.wav [--start 0 --seconds 30]
    python3 mosaic.py --stem vocals.wav:voice:15:2 --stem drums.wav:all:15 ... --out /tmp/mosaic.wav
"""
import argparse
import json
import os
import subprocess
import sys
import wave

import numpy as np

FPS = 30
SR = 22050
HOP = SR // FPS  # 735
WIN = 2 * HOP
N_FFT = 2048
HOP_HI = 48000 // FPS


def decode(path, sr, start=None, seconds=None, channels=1):
    cmd = ["ffmpeg", "-v", "error"]
    if start is not None:
        cmd += ["-ss", str(start)]
    if seconds is not None:
        cmd += ["-t", str(seconds)]
    cmd += ["-i", path, "-ac", str(channels), "-ar", str(sr), "-f", "f32le", "-"]
    x = np.frombuffer(subprocess.run(cmd, check=True, capture_output=True).stdout, dtype=np.float32).copy()
    return x if channels == 1 else x.reshape(-1, channels).T.copy()


def mel_filterbank(sr, n_fft, n_mels, fmin=0.0, fmax=None):
    fmax = fmax or sr / 2
    mel = lambda f: 2595 * np.log10(1 + f / 700)
    imel = lambda m: 700 * (10 ** (m / 2595) - 1)
    pts = imel(np.linspace(mel(fmin), mel(fmax), n_mels + 2))
    freqs = np.fft.rfftfreq(n_fft, 1 / sr)
    fb = np.zeros((n_mels, len(freqs)), dtype=np.float32)
    for i in range(n_mels):
        lo, c, hi = pts[i], pts[i + 1], pts[i + 2]
        fb[i] = np.clip(np.minimum((freqs - lo) / (c - lo), (hi - freqs) / (hi - c)), 0, None)
    return fb


def frames_power(x, fb, n_frames=None, hop=HOP):
    """Mel power per step of hop samples (a game frame, or a fraction of one); step t covers [t*hop, t*hop + WIN):
    the window stays two frames long at any step, so finer steps keep the frequency resolution."""
    win = WIN
    if n_frames is None:
        n_frames = max(1, int(np.ceil(len(x) / hop)))
    pad = np.zeros(n_frames * hop + win, dtype=np.float32)
    pad[: min(len(x), len(pad))] = x[: len(pad)]
    idx = np.arange(win)[None, :] + hop * np.arange(n_frames)[:, None]
    seg = pad[idx] * np.hanning(win).astype(np.float32)
    spec = np.abs(np.fft.rfft(seg, N_FFT)) ** 2
    return (spec @ fb.T).T.astype(np.float64)  # (bands, frames)


def load_sounds(cache, fb, excluded, hop=HOP):
    """One template per distinct file (several ids share one): ids, names, 48 kHz clips, mel templates."""
    sounds = json.load(open(os.path.join(cache, "sounds.json")))
    sprite_path = os.path.join(cache, "audiosprite.small.ogg")
    sprite48 = os.path.join(cache, "sprite48k.f32")
    if not os.path.exists(sprite48):
        decode(sprite_path, 48000).tofile(sprite48)
    sprite_hi = np.fromfile(sprite48, dtype=np.float32)
    sprite_lo = decode(sprite_path, SR)
    seen = set()
    ids, names, clips, templates = [], [], [], []
    for s in sounds:
        if s["id"] in excluded or s["file"] in seen:
            continue
        seen.add(s["file"])
        start_ms, dur_ms = s["span"]
        lo = sprite_lo[int(start_ms * SR / 1000): int((start_ms + dur_ms) * SR / 1000)]
        hi = sprite_hi[int(start_ms * 48): int((start_ms + dur_ms) * 48)]
        if len(lo) < 16 or np.max(np.abs(lo)) < 1e-4:
            continue
        ids.append(s["id"])
        names.append(s["name"])
        clips.append(hi)
        templates.append(frames_power(lo, fb, hop=hop))
    return ids, names, clips, templates


def keep_strongest(values, frame_of, cap, count):
    """Mask of the count largest values with no more than cap in one frame: what taking values from the largest down
    and skipping full frames gives, since a value is only skipped for larger ones in its own frame."""
    import torch
    flat = values.flatten()
    frames = frame_of.flatten()
    order = torch.argsort(flat, descending=True)
    # Rank within each frame, in descending value order: sort the descending order stably by frame.
    by_frame = order[torch.argsort(frames[order], stable=True)]
    f_sorted = frames[by_frame]
    group_start = torch.searchsorted(f_sorted, f_sorted, right=False)
    rank = torch.empty_like(flat, dtype=torch.long)
    rank[by_frame] = torch.arange(len(flat), device=flat.device) - group_start
    allowed = (rank < cap) & (flat > 0)
    candidates = torch.nonzero(allowed).flatten()
    chosen = candidates[torch.argsort(flat[candidates], descending=True)[:count]]
    mask = torch.zeros_like(flat)
    mask[chosen] = 1
    return mask.reshape(values.shape)


def fit_onsets(V, templates, args, label):
    """Fit V (bands x frames, real power) with the templates; returns {(template index, frame): volume}, where the
    volume plays the sound at the loudness that reproduces V. Runs in PyTorch on args.device (a GPU is many times faster)."""
    import torch
    dev = torch.device(args.device)
    T = V.shape[1]
    S = len(templates)
    L = max(t.shape[1] for t in templates)
    # Band weights: scaling a band of the target and of every template alike scales its share of the error.
    weight = V.mean(axis=1, keepdims=True) ** -args.whiten
    weight /= weight.mean()
    V = V * weight
    templates = [t * weight for t in templates]
    # Fit in units of the target's mean power (powers like Lam ** -1.5 overflow float32 otherwise), with templates
    # normalised to unit energy (quiet sounds are otherwise lost in float32 rounding next to loud ones): the real
    # power gain of an onset is H * unit / energy.
    unit = V.mean()
    eps = args.floor
    energy_of = np.array([t.sum() for t in templates])

    N = 1
    while N < T + L:
        N *= 2
    W = np.zeros((S, V.shape[0], L), dtype=np.float32)
    for i, t in enumerate(templates):
        W[i, :, : t.shape[1]] = t / energy_of[i]
    FW = torch.fft.rfft(torch.tensor(W, device=dev), n=N, dim=2)
    del W
    Vf = torch.tensor(V / unit + eps, dtype=torch.float32, device=dev)
    on_frame = torch.ones(1, T, device=dev)
    frame_of = torch.arange(T, device=dev)[None, :].expand(S, T)

    def reconstruct(H):
        FH = torch.fft.rfft(H, n=N, dim=1)
        return torch.fft.irfft(torch.einsum("sfk,sk->fk", FW, FH), n=N, dim=1)[:, :T].clamp(min=0) + eps

    def correlate(R):
        FR = torch.fft.rfft(R, n=N, dim=1)
        return torch.fft.irfft(torch.einsum("sfk,fk->sk", FW.conj(), FR), n=N, dim=1)[:, :T]

    # Beta-divergence: 1 is KL (dominated by the loudest bands), 0 is Itakura-Saito (every band by its ratio).
    beta = args.beta

    def fit(H, iters, mask):
        for it in range(iters):
            Lam = reconstruct(H)
            den = correlate(Lam ** (beta - 1))
            num = correlate(Vf * Lam ** (beta - 2))
            floor = 1e-4 * den.max(dim=1, keepdim=True).values
            H = H * (num.clamp(min=0) / torch.maximum(den, floor)).clamp(0, 4) * mask
        return H

    def loss(H):
        Lam = reconstruct(H)
        if beta == 0:
            r = Vf / Lam
            d = r - torch.log(r) - 1
        elif beta == 1:
            d = Vf * torch.log(Vf / Lam) - Vf + Lam
        else:
            d = (Vf ** beta + (beta - 1) * Lam ** beta - beta * Vf * Lam ** (beta - 1)) / (beta * (beta - 1))
        return float(d.mean())

    def result(H):
        h = H.cpu().numpy()
        return {(int(s), int(t)): float(np.sqrt(h[s, t] * unit / energy_of[s])) for s, t in zip(*np.nonzero(h))}

    H = torch.full((S, T), float(Vf.sum()) / (S * T), device=dev) * on_frame
    H = fit(H, args.iters, on_frame)
    dense = loss(H)

    # Thin gradually: refit, then drop the weakest onsets (by energy), until the budget remains and no frame starts
    # more than max-per-frame. Dropping all at once from the dense fit loses too much.
    budget = int(args.per_second * T / FPS)
    while True:
        support = int((H > 0).sum())
        if support <= budget:
            break
        mask = keep_strongest(H, frame_of, args.max_per_frame, max(budget, int(support * args.keep)))
        H = fit(H * mask, args.refit, mask)
    print(f"{label}: {S} sounds, loss dense {dense:.4f}, {int((H > 0).sum())} onsets {loss(H):.4f}", file=sys.stderr)
    return result(H)


def chroma_filterbank(sr, n, tuning=432.0, fmin=80.0, fmax=4000.0):
    """(12, n // 2 + 1): each FFT bin between fmin and fmax shared between the two nearest pitch classes (A = 0),
    pitch measured from tuning (A4) in semitones."""
    freqs = np.fft.rfftfreq(n, 1 / sr)
    fb = np.zeros((12, len(freqs)), dtype=np.float32)
    ok = (freqs >= fmin) & (freqs <= fmax)
    semis = 12 * np.log2(freqs[ok] / tuning)
    lo = np.floor(semis)
    frac = semis - lo
    idx = np.nonzero(ok)[0]
    np.add.at(fb, ((lo.astype(int)) % 12, idx), 1 - frac)
    np.add.at(fb, ((lo.astype(int) + 1) % 12, idx), frac)
    return fb


def pan_gains(p, lib=np):
    """Amplitude gains (left, right) of a sound at pan p in the game: Howler's stereo() uses the browser's
    StereoPannerNode, and the sprite's clips are two identical channels, for which the node sends at p <= 0, with
    x = (p + 1) * pi / 2, left = in * (1 + cos x) and right = in * sin x, and at p > 0, with x = p * pi / 2,
    left = in * cos x and right = in * (1 + sin x): unchanged at 0, both channels into one (+6 dB) at -1 or 1.
    (Pan 0 skips the panner altogether, the same thing.)"""
    neg = (p + 1) * np.pi / 2
    pos = p * np.pi / 2
    left = lib.where(p <= 0, 1 + lib.cos(neg), lib.cos(pos))
    right = lib.where(p <= 0, lib.sin(neg), 1 + lib.sin(pos))
    return left, right


def refine_log(raw, targets, lo_clips, args, background=None, canon=None):
    """Refit the chosen onsets (raw: [(sound index, frame, volume)]) by gradient descent on the difference in dB
    between log-mel spectrograms of the target and of the mix, at 3 steps a frame with 46 ms windows: the power fit
    leaves the quiet parts too loud and the loud parts too quiet, which a difference in dB punishes as the ear does.
    targets is [mono] or [left, right]; with two, every onset also gets a pan. With --explore, rounds of swaps keep the onset count: add the (sound, frame)
    pairs whose gradient says they would help most, refit, drop as many of the weakest.
    background: [(sound index, frame, volume, pan)] that play under the fit and stay as they are (mono only).
    canon: for each frame, the frame it repeats (itself if none, see --tie): an onset is chosen on a frame that repeats
    nothing and also plays at every frame repeating it, at the same volume, so a repeated stretch of the song is the
    same onsets each time (one chain of triggers in the level). Its volume is fitted against every copy, and an onset
    worth adding is judged by its gradient summed over them.
    Returns [(sound index, frame, volume, pan)], every copy listed."""
    import torch
    torch.set_num_threads(max(1, (os.cpu_count() or 2) // 2))
    dev = torch.device(args.device)
    hop, n = HOP // 3, 1024
    fb = mel_filterbank(SR, n, args.refine_bands)

    def spec(x, steps=None):
        if steps is None:
            steps = int(np.ceil(len(x) / hop))
        pad = np.zeros(steps * hop + n, dtype=np.float32)
        pad[: min(len(x), len(pad))] = x[: len(pad)]
        idx = np.arange(n)[None, :] + hop * np.arange(steps)[:, None]
        return ((np.abs(np.fft.rfft(pad[idx] * np.hanning(n).astype(np.float32), n)) ** 2) @ fb.T).T

    n_c = 4096
    cfb = chroma_filterbank(SR, n_c, args.chroma_tuning)

    def chroma(x, steps=None):
        """12 pitch classes per step, 186 ms windows centred like spec's."""
        if steps is None:
            steps = int(np.ceil(len(x) / hop))
        pad = np.zeros(steps * hop + n_c, dtype=np.float32)
        off = (n_c - n) // 2
        pad[off: off + min(len(x), len(pad) - off)] = x[: len(pad) - off]
        out = np.zeros((12, steps), dtype=np.float32)
        win = np.hanning(n_c).astype(np.float32)
        for a in range(0, steps, 2048):
            b = min(steps, a + 2048)
            idx = np.arange(n_c)[None, :] + hop * np.arange(a, b)[:, None]
            out[:, a:b] = ((np.abs(np.fft.rfft(pad[idx] * win, n_c)) ** 2) @ cfb.T).T
        return out

    Vs = [spec(x) for x in targets]
    steps = min(v.shape[1] for v in Vs)
    Vs = [v[:, :steps] for v in Vs]
    stereo = len(Vs) == 2
    S = len(lo_clips)
    W = [spec(c) for c in lo_clips]
    L = max(w.shape[1] for w in W)
    Wt = np.zeros((S, Vs[0].shape[0], L), dtype=np.float32)
    for k, w in enumerate(W):
        Wt[k, :, : w.shape[1]] = w
    energy = Wt.sum(axis=(1, 2))
    frames = steps // 3 + 1
    # Ties: copies[ptr[c]:ptr[c + 1]] are the frames playing what an onset on frame c plays.
    tied = canon is not None
    canon_f = np.arange(frames)
    if tied:
        m = np.asarray(canon)
        canon_f[: min(len(m), frames)] = m[:frames]
    is_canon = canon_f == np.arange(frames)
    order_f = np.argsort(canon_f, kind="stable")
    ptr = np.searchsorted(canon_f[order_f], np.arange(frames + 1))

    plays = ptr[1:] - ptr[:-1]  # how many frames an onset on each frame plays on (copies included)

    def plays_of(cols):
        cols = np.asarray(cols, dtype=np.int64)
        return plays[cols] if tied else np.ones(len(cols), dtype=np.int64)

    def expand(cols):
        """For onsets on cols, (onset index, frame) of every copy."""
        cols = np.asarray(cols, dtype=np.int64)
        if not tied:
            return np.arange(len(cols)), cols
        counts = ptr[cols + 1] - ptr[cols]
        k = np.repeat(np.arange(len(cols)), counts)
        offset = np.arange(len(k)) - np.repeat(np.cumsum(counts) - counts, counts)
        return k, order_f[np.repeat(ptr[cols], counts) + offset]
    unit = float(np.mean(Vs))
    eps = float(np.max(Vs)) * 10 ** (-args.refine_range / 10) / unit
    Wt = torch.tensor(Wt / unit, dtype=torch.float32, device=dev)
    bg = torch.zeros(1, S, frames, device=dev)
    # --flag: sounds the ear picks out (pings, stabs, footsteps). --flag-weight adds a loss on the mix being louder than
    # the target, weighted by the flagged sounds' share of the mix there.
    flagged = np.zeros(S, dtype=bool)
    flagged[list(getattr(args, "flag_rows", []))] = True
    flag_mask = torch.tensor(flagged, dtype=torch.float32, device=dev)[None, :, None]
    if background:
        assert not stereo, "a background is mono only"
        b = np.zeros((1, S, frames), dtype=np.float32)
        for i, t, v, _ in background:
            if t < frames:
                b[0, i, t] += v * v
        bg = torch.tensor(b, device=dev)
    if args.chroma_weight > 0:
        Ct = np.zeros((S, 12, L), dtype=np.float32)
        for k, c in enumerate(lo_clips):
            cc = chroma(c, W[k].shape[1])
            Ct[k, :, : cc.shape[1]] = cc
        Cs = [chroma(x, steps)[:, :steps] for x in targets]
        c_unit = float(np.mean(Cs))
        Ct = torch.tensor(Ct / c_unit, dtype=torch.float32, device=dev)
        c_targets, c_weights = [], []
        for C in Cs:
            C = C / c_unit
            tot = C.sum(0)
            c_targets.append(torch.tensor(C / (tot + 1e-9), dtype=torch.float32, device=dev))
            # Steps where the target has tonal energy count, the louder the more (up to 1).
            w = np.clip(10 * np.log10(tot / tot.max() + 1e-12) / 30 + 1, 0, 1)
            c_weights.append(torch.tensor(w, dtype=torch.float32, device=dev))
    floors, target_db = [], []
    for V in Vs:
        V = V / unit
        floor = torch.tensor(np.full_like(V, eps), dtype=torch.float32, device=dev)
        floors.append(floor)
        target_db.append(10 * torch.log10(torch.tensor(V, dtype=torch.float32, device=dev) + floor))

    def error(Hs):
        """Hs: one (1, S, frames) activation per channel."""
        total, diffs = 0, []
        for H, floor, tdb in zip(Hs, floors, target_db):
            m_lin = torch.nn.functional.conv_transpose1d(H, Wt, stride=3)[0, :, :steps]
            mdb = 10 * torch.log10(m_lin + floor)
            diff = mdb - tdb
            total = total + torch.nn.functional.smooth_l1_loss(diff, torch.zeros_like(diff), beta=1.0)
            if args.flag_weight > 0 and flagged.any():
                share = torch.nn.functional.conv_transpose1d(H * flag_mask, Wt, stride=3)[0, :, :steps] / (m_lin + floor)
                total = total + args.flag_weight * (share * diff.clamp(min=0)).mean()
            if args.chroma_weight > 0:
                k = len(diffs)
                cm = torch.nn.functional.conv_transpose1d(H, Ct, stride=3)[0, :, :steps]
                pm = cm / (cm.sum(0, keepdim=True) + 1e-6)
                d = (pm - c_targets[k]).abs().sum(0)
                if args.chroma_target > 0:
                    # A hinge: steps already within the target pull no further, so the search stops trading the dB
                    # match for pitch error too small to hear.
                    d = (d - args.chroma_target).clamp(min=0)
                total = total + args.chroma_weight * (d * c_weights[k]).sum() / (c_weights[k].sum() + 1e-9)
            diffs.append(diff)
        return total / len(Hs), torch.cat(diffs)

    # Support as parallel lists; the same (sound, frame) chosen twice is merged (powers add).
    merged = {}
    for i, t, v in raw:
        merged[(i, t)] = merged.get((i, t), 0) + v * v
    start_rows = [i for i, _ in merged]
    start_cols = [t for _, t in merged]
    start_logv = [0.5 * np.log(max(p, 1e-12)) for p in merged.values()]

    def optimise(rows, cols, logv, pans, steps_, free=None):
        """free: which onsets may change (None: all)."""
        a = torch.tensor(logv, dtype=torch.float32, device=dev, requires_grad=True)
        q = torch.tensor(pans, dtype=torch.float32, device=dev, requires_grad=stereo)
        k_np, f_np = expand(cols)
        kk = torch.tensor(k_np, device=dev)
        r, c = torch.tensor(np.asarray(rows)[k_np], device=dev), torch.tensor(f_np, device=dev)
        opt = torch.optim.Adam([a, q] if stereo else [a], lr=args.refine_lr)
        index = (torch.zeros_like(r), r, c)
        free_t = None if free is None else torch.tensor(free, device=dev)
        for _ in range(steps_):
            power = torch.exp(2 * a)[kk]
            if stereo:
                gl, gr = pan_gains(torch.tanh(q[kk]), torch)
                Hs = [torch.zeros(1, S, frames, device=dev).index_put(index, power * g * g, accumulate=True) for g in (gl, gr)]
            else:
                Hs = [bg + torch.zeros(1, S, frames, device=dev).index_put(index, power, accumulate=True)]
            loss, diff = error(Hs)
            opt.zero_grad()
            loss.backward()
            if free is not None:
                a.grad[~free_t] = 0
                if stereo:
                    q.grad[~free_t] = 0
            opt.step()
            with torch.no_grad():
                a.clamp_(max=float(np.log(8.0)))
                q.clamp_(-3, 3)
        return a.detach().cpu().numpy().tolist(), q.detach().cpu().numpy().tolist(), diff.abs().mean().item(), loss.item()

    def search(rows, cols, logv, pans):
        """Refine, then the explore rounds; returns the onsets, their volumes and pans, and the final errors."""
        logv, pans, err, final = optimise(rows, cols, logv, pans, args.refine)
        print(f"refine: {len(rows)} onsets, mean dB error {err:.2f}", file=sys.stderr)
        for round_ in range(args.explore):
            # Gradient of the error for every (sound, frame), for a new onset in the middle (both channels alike): the
            # most negative are the onsets worth adding.
            hs = [np.zeros((1, S, frames), dtype=np.float32) for _ in Vs]
            power = np.exp(2 * np.array(logv))
            gains = pan_gains(np.tanh(np.array(pans))) if stereo else [np.ones(len(rows))]
            k_np, f_np = expand(cols)
            for h, g in zip(hs, gains):
                np.add.at(h, (0, np.asarray(rows)[k_np], f_np), (power * g * g)[k_np])
            H0 = torch.zeros(1, S, frames, device=dev, requires_grad=True)
            Hs = [torch.tensor(h, device=dev) + bg + H0 for h in hs]
            loss, _ = error(Hs)
            loss.backward()
            g = H0.grad[0].cpu().numpy().copy()
            if tied:
                # An onset on frame c plays on every copy of c: its gradient is the sum over them, and only frames that
                # repeat nothing can take one.
                gc = np.zeros_like(g)
                np.add.at(gc.T, canon_f, g.T)
                gc[:, ~is_canon] = 0
                g = gc
            g[rows, cols] = 0
            if args.explore_allowed is not None:
                g[~args.explore_allowed] = 0
            m = max(1, int(len(rows) * args.explore_frac))
            best = np.argsort(g, axis=None)[:m]
            start_power = float(np.median(power * energy[rows])) * 0.3
            for flat in best:
                i, t = divmod(int(flat), frames)
                if g[i, t] >= 0:
                    break
                rows.append(i)
                cols.append(t)
                logv.append(0.5 * np.log(start_power / energy[i]))
                pans.append(0.0)
            logv, pans, err, final = optimise(rows, cols, logv, pans, args.explore_steps)
            # Drop the weakest by the energy they add (every copy), back to the budget.
            contribution = np.exp(2 * np.array(logv)) * energy[rows] * plays_of(cols)
            keep = np.sort(np.argsort(contribution)[::-1][: len(rows) - m])
            rows = [rows[k] for k in keep]
            cols = [cols[k] for k in keep]
            logv = [logv[k] for k in keep]
            pans = [pans[k] for k in keep]
            logv, pans, err, final = optimise(rows, cols, logv, pans, args.explore_steps)
            print(f"explore round {round_ + 1}: {len(rows)} onsets, mean dB error {err:.2f}", file=sys.stderr)
        return rows, cols, logv, pans, err, final


    # Relaxation (--relax N): start from every allowed sound on every frame, at volumes that together carry the
    # target's energy, and choose the onsets by the loss itself: N gradient steps, drop the weakest by the energy they
    # add down to --relax-keep of them (the per-frame cap tightening as the count falls), repeat until stage 1's onset
    # count is left. Replaces stage 1's onsets as the start of the search.
    if args.relax:
        budget = getattr(args, "relax_budget", None) or len(start_rows)
        allowed_s = np.nonzero(args.explore_allowed)[0] if args.explore_allowed is not None else np.arange(S)
        rr, cc = np.meshgrid(allowed_s, np.nonzero(is_canon)[0], indexing="ij")
        rows, cols = rr.ravel().tolist(), cc.ravel().tolist()
        total = float(np.mean([V.sum() for V in Vs]))
        logv = (0.5 * np.log(total / len(rows) / energy[rows])).tolist()
        pans = [0.0] * len(rows)
        frame_of = torch.arange(frames, device=dev)[None, :].expand(S, frames)
        while True:
            logv, pans, err, final = optimise(rows, cols, logv, pans, args.relax)
            print(f"relax: {len(rows)} onsets, loss {final:.5f}, mean dB error {err:.2f}", file=sys.stderr)
            if len(rows) <= budget:
                break
            target = max(budget, int(len(rows) * args.relax_keep))
            cap = args.max_per_frame if target == budget else max(args.max_per_frame, int(np.ceil(2 * target / frames)))
            vals = torch.zeros(S, frames, device=dev)
            vals[rows, cols] = torch.tensor(np.exp(2 * np.array(logv)) * energy[rows] * plays[cols], dtype=torch.float32, device=dev)
            kept = keep_strongest(vals, frame_of, cap, target)[rows, cols].cpu().numpy() > 0
            rows, cols, logv, pans = ([x[k] for k in np.nonzero(kept)[0]] for x in (rows, cols, logv, pans))
        start_rows, start_cols, start_logv = rows, cols, logv

    pans = [0.0] * len(start_rows)  # stored as the pre-tanh parameter
    rows, cols, logv, pans, err, final = search(list(start_rows), list(start_cols), list(start_logv), pans)
    k_np, f_np = expand(cols)
    return [(rows[k], int(f), float(np.exp(logv[k])), float(np.tanh(pans[k]))) for k, f in zip(k_np, f_np)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.expanduser("~/.cache/audio-mosaic"))
    ap.add_argument("--song", help="the whole song, fitted with every sound (or use --stem)")
    ap.add_argument("--stem", action="append", default=[],
                    help="PATH:POOL:PER_SECOND[:GAIN[:WHITEN]], POOL 'all' or 'voice' (the characters' voice lines) or "
                         "'other' (everything else); GAIN multiplies the stem's loudness (2 = +6 dB); WHITEN overrides --whiten")
    ap.add_argument("--start", type=float, default=None)
    ap.add_argument("--seconds", type=float, default=None)
    ap.add_argument("--out", required=True)
    ap.add_argument("--bands", type=int, default=48)
    ap.add_argument("--iters", type=int, default=150)
    ap.add_argument("--device", default="cuda:0", help="PyTorch device: cuda:0 (the fastest GPU), cuda:1, or cpu")
    ap.add_argument("--refine", type=int, default=0, help="gradient steps refitting the volumes on a log-mel loss")
    ap.add_argument("--refine-lr", type=float, default=0.05)
    ap.add_argument("--explore", type=int, default=0, help="rounds of swapping onsets in the log-mel refit")
    ap.add_argument("--relax", type=int, default=0, help="relaxation: gradient steps per thinning round from every sound on every frame (0: start from stage 1)")
    ap.add_argument("--relax-keep", type=float, default=0.5, help="share of the onsets kept each thinning round")
    ap.add_argument("--explore-frac", type=float, default=0.1, help="share of the onsets swapped each round")
    ap.add_argument("--explore-pool", default="all", help="sounds the swap search may add: all, voice or other")
    ap.add_argument("--explore-steps", type=int, default=60, help="gradient steps after each add and each drop")
    ap.add_argument("--refine-bands", type=int, default=64)
    ap.add_argument("--chroma-weight", type=float, default=0.0,
                    help="weight of a loss on pitch content (normalised 12-class chroma, 186 ms windows) in the refit")
    ap.add_argument("--chroma-target", type=float, default=0, help="per-step pitch error (L1 of pitch-class shares) below which it no longer counts; 0: always counts")
    ap.add_argument("--chroma-tuning", type=float, default=440.0, help="A4 in Hz for the chroma (the song's tuning)")
    ap.add_argument("--stereo", action="store_true", help="fit left and right in the log-mel refit, with a pan per onset")
    ap.add_argument("--pan-step", type=float, default=0.25, help="pans are rounded to this (distinct triggers)")
    ap.add_argument("--refine-range", type=float, default=60, help="dB below the target's loudest that still count")
    ap.add_argument("--per-second", type=float, default=8.0, help="onsets a second kept after thinning (--song)")
    ap.add_argument("--max-per-frame", type=int, default=3, help="most onsets started in one frame, per stem")
    ap.add_argument("--keep", type=float, default=0.5, help="share of onsets kept at each thinning step")
    ap.add_argument("--refit", type=int, default=40, help="iterations after each thinning step")
    ap.add_argument("--whiten", type=float, default=0.0, help="band weighting, 0 none .. 1 every band alike")
    ap.add_argument("--beta", type=float, default=0.5, help="beta-divergence: 1 KL, 0 Itakura-Saito")
    ap.add_argument("--floor", type=float, default=1e-3, help="power floor, relative to the target's mean")
    ap.add_argument("--peak", type=float, default=0.9, help="peak of the in-game mix")
    ap.add_argument("--loudness-percentile", type=float, default=99.99,
                    help="the mix is scaled so this percentile of its samples is at --peak; spikes above are limited")
    ap.add_argument("--limiter-steps", type=int, default=50000)
    ap.add_argument("--step-db", type=float, default=2.0, help="volume rounding step in dB (0: no rounding, 4 decimals)")
    ap.add_argument("--min-db", type=float, default=-40.0, help="onsets quieter than this are dropped")
    ap.add_argument("--exclude", default="", help="comma-separated sound ids to leave out")
    ap.add_argument("--events", default=None, help="write the onsets as JSON here")
    ap.add_argument("--tie", default=None, help="JSON with 'canon', each frame's earlier frame it repeats: fit the instrumental "
                    "stem as a layer whose repeats are the same onsets, then the rest on top (see refine_log)")
    ap.add_argument("--tie-budget", type=float, default=1.0, help="with --tie, the onset budgets times this")
    ap.add_argument("--flag", default="", help="comma-separated sound ids the ear picks out (see README.md)")
    ap.add_argument("--flag-weight", type=float, default=0.0, help="weight of a loss on the mix being louder than the target where flagged sounds make it")
    args = ap.parse_args()

    # From 0 Hz: the impacts carry rumble under 40 Hz that the song lacks, and a fit that cannot see it plays it.
    fb = mel_filterbank(SR, N_FFT, args.bands)
    excluded = {int(s) for s in args.exclude.split(",") if s}
    ids, names, clips, templates = load_sounds(args.cache, fb, excluded)
    is_voice = ["voice" in n for n in names]
    flag_ids = {int(x) for x in args.flag.split(",") if x}
    args.flag_rows = [k for k, sid in enumerate(ids) if sid in flag_ids]
    args.explore_allowed = None if args.explore_pool == "all" else np.array(
        [(args.explore_pool == "voice") == v for v in is_voice])

    stems = []
    if args.song:
        stems.append((args.song, "all", args.per_second, 1.0, args.whiten))
    for spec in args.stem:
        parts = spec.split(":")
        stems.append((parts[0], parts[1], float(parts[2]), float(parts[3]) if len(parts) > 3 else 1.0,
                      float(parts[4]) if len(parts) > 4 else args.whiten))
    if not stems:
        ap.error("give --song or --stem")

    def stage1(allowed):
        """Raw onsets from every stem, at the volumes that reproduce each stem times its gain, using only the sounds in
        allowed; also each onset's origin (the stem whose fit chose it), the stems' summed power and waveforms."""
        raw, origin = [], {}
        steps, mix, mix_waves = None, 0, None
        args.stem_raws = []
        for path, pool, per_second, gain, whiten in stems:
            members = [i for i in range(len(ids)) if allowed[i] and (pool == "all" or (pool == "voice") == is_voice[i])]
            x = decode(path, SR, args.start, args.seconds)
            if args.refine:
                channels = decode(path, SR, args.start, args.seconds, 2) if args.stereo else x[None, :]
                mix_waves = channels * gain if mix_waves is None else mix_waves[:, : channels.shape[1]] + channels[:, : mix_waves.shape[1]] * gain
            if steps is None:
                steps = int(np.ceil(len(x) / HOP))
            V = frames_power(x, fb, steps)
            mix = mix + V * gain * gain
            sub = argparse.Namespace(**{**vars(args), "per_second": per_second, "whiten": whiten})
            found = fit_onsets(V, [templates[i] for i in members], sub, f"{os.path.basename(path)} ({pool})")
            raw += [(members[s], t, v * gain) for (s, t), v in found.items()]
            args.stem_raws.append([(members[s], t, v * gain) for (s, t), v in found.items()])
            name = os.path.splitext(os.path.basename(path))[0]
            for s_, t in found:
                key = (members[s_], t)
                origin[key] = name if origin.get(key, name) == name else "both"
        return raw, origin, steps, mix, mix_waves

    allowed = np.ones(len(ids), dtype=bool)
    raw, origin, steps, mix, mix_waves = stage1(allowed)
    args.explore_allowed = allowed if args.explore_allowed is None else (args.explore_allowed & allowed)
    T = steps

    if args.refine and args.tie:
        # Two layers: the instrumental (the stem with pool 'other') with its repeats tied (--tie: each repeated stretch the
        # same onsets as the first time), then the other stems' sounds fitted to the whole mix on top of it, which stays.
        lo_clips = [np.interp(np.arange(0, len(c), 48000 / SR), np.arange(len(c)), c).astype(np.float32) for c in clips]
        canon = json.load(open(args.tie))["canon"]
        inst = next(k for k, st in enumerate(stems) if st[1] == "other")
        repeats_nothing = float(np.mean(np.asarray(canon[:T]) == np.arange(min(T, len(canon)))))
        raws = args.stem_raws
        total = len({(i, t) for r in raws for i, t, _ in r})
        share = len(raws[inst]) / sum(len(r) for r in raws)
        a1 = argparse.Namespace(**{**vars(args), "explore_allowed": args.explore_allowed & ~np.array(is_voice),
                                   "relax_budget": max(1, int(total * share * repeats_nothing * args.tie_budget))})
        path, _, _, gain, _ = stems[inst]
        inst_wave = decode(path, SR, args.start, args.seconds) * gain
        print(f"tied instrumental layer: {a1.relax_budget} onsets on the {repeats_nothing:.0%} of frames that repeat nothing", file=sys.stderr)
        layer1 = refine_log(raws[inst], [inst_wave], lo_clips, a1, canon=canon)
        rest = [x for k, r in enumerate(raws) if k != inst for x in r]
        a2 = argparse.Namespace(**{**vars(args), "relax_budget": max(1, int(total * (1 - share) * args.tie_budget))})
        print(f"layer on top: {a2.relax_budget} onsets", file=sys.stderr)
        layer2 = refine_log(rest, list(mix_waves), lo_clips, a2, background=layer1)
        raw = layer1 + layer2
        tie_of = [int(canon[t]) if t < len(canon) else t for _, t, _, _ in layer1] + [None] * len(layer2)
    elif args.refine:
        lo_clips = [np.interp(np.arange(0, len(c), 48000 / SR), np.arange(len(c)), c).astype(np.float32) for c in clips]
        raw = refine_log(raw, list(mix_waves), lo_clips, args)
    else:
        raw = [(i, t, v, 0.0) for i, t, v in raw]
    if not (args.refine and args.tie):
        tie_of = [None] * len(raw)

    # In the game a sound at volume v plays its clip times v (the sfx channel and the sprite are at 1, and the master
    # gain is the player's volume setting), the mix is a plain sum and nothing limits it, so the sum must stay under
    # 1 or it clips. A few spikes (sharp sounds landing together) would set the level of the whole song, so: scale
    # every volume so the --loudness-percentile of the mix is at --peak, then limit the spikes over --peak by turning
    # down, at each, the onset that contributes most there, just enough. Then round each volume to --step-db steps
    # (any volume can be written; steps only bound the number of distinct triggers).
    length = T * HOP_HI + 48000
    channels = 2 if args.stereo else 1

    def gains_of(p):
        return np.array(pan_gains(np.float64(p))) if args.stereo else np.ones(1)

    def render(events):
        """events: [(sound index, frame, volume, pan)] -> (samples, channels)."""
        out = np.zeros((T * HOP_HI + max(len(c) for c in clips), channels), dtype=np.float64)
        for i, t, v, p in events:
            out[t * HOP_HI: t * HOP_HI + len(clips[i])] += clips[i][:, None] * (v * gains_of(p))[None, :]
        return out[:length]

    def limit(out, events, vol, threshold, adjust, max_steps):
        """Turn down, at the loudest sample over threshold, the onset contributing most there, until none is over:
        adjust(j, contribution, excess) gives onset j's new volume. Changes out and vol in place; returns onsets touched."""
        starts = np.array([t * HOP_HI for _, t, _, _ in events])
        ends = starts + np.array([len(clips[i]) for i, _, _, _ in events])
        gain = np.array([gains_of(p) for _, _, _, p in events])
        touched = set()
        for _ in range(max_steps):
            k, ch = np.unravel_index(int(np.argmax(np.abs(out))), out.shape)
            excess = abs(out[k, ch]) - threshold
            if excess <= 0:
                break
            live = np.nonzero((starts <= k) & (k < ends) & (vol > 0))[0]
            contrib = np.array([clips[events[j][0]][k - starts[j]] * vol[j] * gain[j, ch] for j in live]) * np.sign(out[k, ch])
            j = live[int(np.argmax(contrib))]
            new = adjust(j, contrib.max(), excess)
            hi = min(ends[j], length)
            out[starts[j]: hi] += clips[events[j][0]][: hi - starts[j], None] * ((new - vol[j]) * gain[j])[None, :]
            vol[j] = new
            touched.add(int(j))
        return touched

    out = render(raw)
    scale = args.peak / max(np.percentile(np.abs(out), args.loudness_percentile), 1e-12)
    out *= scale
    vol = np.array([v * scale for _, _, v, _ in raw])
    limited = limit(out, raw, vol, args.peak, lambda j, c, excess: vol[j] * max(0.0, 1 - excess / c) if c > 0 else vol[j],
                    args.limiter_steps)
    print(f"loudness: scaled {20 * np.log10(scale):+.1f} dB, {len(limited)} onsets turned down at spikes", file=sys.stderr)
    if args.tie:
        # A tied onset's copies have to be one trigger in the level: every copy at the quietest of their volumes (the
        # limiter turns onsets down one by one).
        quietest = {}
        for (i, _, _, _), v, c in zip(raw, vol, tie_of):
            if c is not None:
                quietest[(i, c)] = min(quietest.get((i, c), v), v)
        vol = np.array([quietest[(i, c)] if c is not None else v for (i, _, _, _), v, c in zip(raw, vol, tie_of)])
    events = []
    for (i, t, _, p), v, c in zip(raw, vol, tie_of):
        if v <= 0:
            continue
        copies = max(1, int(np.ceil(v - 1e-9)))
        db = 20 * np.log10(v / copies)
        if db < args.min_db:
            continue
        if args.step_db > 0:
            db = np.round(db / args.step_db) * args.step_db
        q = min(1.0, round(float(10 ** (db / 20)), 4))
        pan = float(np.clip(np.round(p / args.pan_step) * args.pan_step, -1, 1)) if args.stereo else 0.0
        events.append({"frame": t, "id": ids[i], "volume": q, "copies": copies, "pan": round(pan, 4),
                       "from": ("tied" if c is not None else "on top") if args.tie else origin.get((i, t), "added")})
        if c is not None:
            events[-1]["tie"] = c
    events.sort(key=lambda e: (e["frame"], e["id"]))
    index = {sid: i for i, sid in enumerate(ids)}
    as_tuples = [(index[e["id"]], e["frame"], e["volume"] * e["copies"], e["pan"]) for e in events]
    out = render(as_tuples)
    # Rounding can push a limited spike back over 1: turn the onset contributing most there down a step, until none is.
    step = 10 ** (-(args.step_db if args.step_db > 0 else 0.5) / 20)
    vol = np.array([v for _, _, v, _ in as_tuples])
    stepped = limit(out, as_tuples, vol, 1.0, lambda j, c, excess: vol[j] * step, 100000)
    for e, v in zip(events, vol):
        e["volume"] = round(v / e["copies"], 4)
    if args.tie:
        quietest = {}
        for e in events:
            if "tie" in e:
                k = (e["id"], e["tie"])
                quietest[k] = min(quietest.get(k, (9e9, 0, 0)), (e["volume"] * e["copies"], e["volume"], e["copies"]))
        for e in events:
            if "tie" in e:
                _, e["volume"], e["copies"] = quietest[(e["id"], e["tie"])]
        out = render([(index[e["id"]], e["frame"], e["volume"] * e["copies"], e["pan"]) for e in events])
    if stepped:
        print(f"after rounding: {len(stepped)} onsets stepped down to keep the mix under 1", file=sys.stderr)
    peak = float(np.max(np.abs(out)))
    vols = np.array([e["volume"] for e in events])
    voice_onsets = sum(1 for e in events if is_voice[index[e["id"]]])
    print(f"{len(events)} onsets ({voice_onsets} voice lines), {sum(e['copies'] for e in events)} sound triggers fired, "
          f"{len({(e['id'], e['volume'], e['pan']) for e in events})} distinct (sound, volume, pan) triggers", file=sys.stderr)
    print(f"in-game mix at volume setting 1: peak {peak:.2f} ({np.mean(np.abs(out) > 1) * 100:.3f}% of samples clip), "
          f"rms {np.sqrt(np.mean(out ** 2)):.3f}; volumes median {np.median(vols):.3f}, max {vols.max():.3f}", file=sys.stderr)
    out = np.clip(out, -1, 1)
    with wave.open(args.out, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(48000)
        w.writeframes((out * 32767).astype(np.int16).tobytes())
    if args.events:
        json.dump(events, open(args.events, "w"))
    print(f"wrote {args.out}", file=sys.stderr)


if __name__ == "__main__":
    main()
