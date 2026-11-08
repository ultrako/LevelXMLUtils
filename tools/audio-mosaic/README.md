# Audio mosaic

Approximates a song with the game's sound effects, the way a trigger level can play them: each sound started on a game
frame (30 a second), at a fixed volume, and left to play to its end. `mosaic.py` writes the onsets for
`Video.ReadOnsets` (`Toolkit/Playback`) and a WAV of the result to listen to.

Needs Python 3 with numpy and PyTorch, and ffmpeg. A CUDA GPU makes a whole song take minutes; `--device cpu` works
but is about ten times slower.

## The game's sounds

```sh
python3 sounds.py --fetch
```

writes `~/.cache/audio-mosaic/sounds.json` (each sound's id, the number a sound trigger takes, with its name and where
it is in the game's audio sprite) and downloads the audio sprite next to it. It reads the game's current script from
the game's site (three requests). `python3 sounds.py dependencies.<hash>.js` works from a saved copy of the script.

## Fitting a song

```sh
python3 mosaic.py --song song.wav --per-second 880 --max-per-frame 20 --refine 400 --explore 8 --relax 200 \
  --chroma-weight 1 --chroma-target 0.3 --events onsets.json --out mosaic.wav
```

1. **Stage 1:** a power fit of the song's mel spectrogram as a sum of the sounds' spectrograms (a convolutive NMF with
   the sounds as fixed templates), thinned to `--per-second` onsets a second, at most `--max-per-frame` in a frame.
2. **Refine:** gradient descent on the difference in dB between log-mel spectrograms of the mix and the song (the
   power fit leaves quiet parts too loud and loud parts too quiet, which a dB difference punishes as the ear does).
   - `--relax N` starts this from every sound on every frame and thins it by halves to stage 1's count, choosing
     the onsets by the loss itself.
   - `--explore R` runs rounds that add the onsets the gradient says help most and drop as many of the weakest.
3. **Pitch:** `--chroma-weight` adds a loss on the mix's spread over the 12 pitch classes, and `--chroma-target` lets
   steps already that close stop pulling. Set `--chroma-tuning` to the song's A.
4. **Mixing:** the volumes are scaled so the mix peaks at `--peak`, spikes over it are limited, and the volumes are
   rounded to `--step-db` steps. Each distinct (sound, volume) is a trigger in the level.

These are the settings that sounded best on Bad Apple!!. By the fit's own measure the search there was at the floor of
what the sounds can do on a 30 Hz grid: more onsets, restarts and other search methods (tried, and left out of this
script) didn't lower it, or didn't sound better. Judge by ear: the scores and the ear disagreed more than once.

- **Sounds that stick out:** `--exclude` leaves sounds out. On Bad Apple the ear picked out the beeps, the button
  presses and the snaps (`264,282,279,266,267,236,237,238,232`). `--flag` with `--flag-weight` penalises the mix being
  louder than the song where some sounds make it (`207,208,209,269,270,271,272`: pings and stabs).
- **Stems:** `--stem PATH:POOL:PER_SECOND[:GAIN]`, repeated, fits stems from a source separator (Demucs) each with
  their own pool (`voice` for the characters' voice lines, `other`, `all`) and budget. With `--tie`, the stem with
  pool `other` is fitted first and the rest on top of it.
- **Repeats:** `ties.py song.wav 3.5 --out ties.json` finds stretches that repeat earlier ones (within 3.5 dB). Pass it
  as `--tie ties.json`, and a repeat replays the first time's onsets through one relay, which costs the level almost
  nothing. On Bad Apple, 3.5 dB tied a third of the song and wasn't audible.
- `eval.py` scores renders against the song: mean dB difference, attacks and pitch error.

## Size

Each onset is an arm in the level (a few bytes compressed) and each distinct (sound, volume) a trigger. Bad Apple with
880 onsets a second came to about 80,000 onsets. To fit the save cap (900 KB compressed), fewer onsets a second, ties,
coarser volume steps (`--step-db 4` cost nothing by ear) and `--min-db` all help.
