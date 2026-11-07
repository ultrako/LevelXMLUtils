"""Renders frames as a level's screen shows them (816 by 464, each cell a rectangle) to a video, to judge by eye.

  python3 render.py FRAMES.raw W H LEVELS OUT.mp4 [AUDIO.wav]

FRAMES.raw: one byte a cell, 0 .. LEVELS-1 (2 for black and white), frame by frame, row by row (frames.py write and
writegrey make them). AUDIO: a mosaic.py render, to hear it with the picture.
"""
import subprocess, sys
import numpy as np

SW, SH = 816, 464


def main():
    path, w, h, k, out = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
    audio = sys.argv[6] if len(sys.argv) > 6 else None
    frames = np.fromfile(path, np.uint8).reshape(-1, h, w)
    cell = min(SW / w, SH / h)
    # Past the last cell (the screen is cell * w by cell * h) the level shows its black background.
    ys, xs = (np.arange(SH) / cell).astype(int), (np.arange(SW) / cell).astype(int)
    outside = (ys[:, None] >= h) | (xs[None, :] >= w)
    ys, xs = np.minimum(ys, h - 1), np.minimum(xs, w - 1)
    grey = np.round(np.linspace(0, 255, k)).astype(np.uint8)
    cmd = ["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "gray", "-s", f"{SW}x{SH}", "-r", "30", "-i", "-"]
    if audio:
        cmd += ["-i", audio, "-c:a", "aac", "-b:a", "192k", "-shortest"]
    cmd += ["-c:v", "libx264", "-crf", "16", "-pix_fmt", "yuv420p", out]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    for f in frames:
        picture = grey[f[np.ix_(ys, xs)]]
        picture[outside] = 0
        proc.stdin.write(picture.tobytes())
    proc.stdin.close()
    proc.wait()
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
