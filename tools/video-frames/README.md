# Video frames

`frames.py` chooses what a trigger video shows: per screen pixel, when to change it. Fewer changes make a smaller level
(each change is a command in a frame), so it trades how much of each frame's area the screen gets right against the
number of changes. Its output is read by `Video.ReadFrames` and `Video.ReadLevels` (`Toolkit/Playback`).

Needs Python 3 with numpy, and ffmpeg.

```sh
python3 frames.py video.webm table 64 36 80 45 84 48       # accuracy and changes at each size
python3 frames.py video.webm sweep 84 48 0.1 0.3 0.5       # the same at several lambdas
python3 frames.py video.webm write 84 48 0.42 frames.raw --budget 700
python3 render.py frames.raw 84 48 2 preview.mp4 mosaic.wav   # look at it (and hear it) before building a level
```

- **Score:** each pixel's share of white in the source, from ffmpeg's area scaling. A pixel shown white gets its
  white share right, a black one its black share.
- **Lambda:** the cost of a change, in pixel areas. Each pixel's states are chosen exactly (a dynamic program) to
  minimise the area wrong plus lambda per change. Small lambdas remove flicker nearly free; past that, a lower
  resolution gives more for the size.
- **`--budget`:** caps the changes in a frame. A scene cut then spreads over a few frames, which keeps a frame's
  trigger work down (at 70x40, 700 changes took the busiest frame from 53,000 trigger actions to 41,000 and wasn't
  noticeable).
- **`grey` and `writegrey`:** pixels at evenly spaced grey levels (`Video` with levels plays them). Each change costs
  about twice what a black-and-white toggle does.
- **`--filter`:** an ffmpeg filter applied before scaling (a crop, more contrast).

The score rewards blur and grey, so judge the previews by eye. On Bad Apple, black and white beat greys by eye, and
sizes whose pixels cover a whole number of source pixels scored clearly better than their neighbours (from 480x360:
80x45, 84x48). The published level is 84x48, lambda 0.42, with a 700-change budget.
