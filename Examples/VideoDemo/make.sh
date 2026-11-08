#!/bin/bash
# Makes a Bad Apple!! level with sound: the video's frames at 84x48, and its song made of the game's sounds.
#
#   Examples/VideoDemo/make.sh badapple.webm out.xml
#
# The video isn't included: use the original shadow-art PV (480x360, 30 frames a second, 3:39). Needs ffmpeg, Python
# with numpy and PyTorch (a CUDA GPU makes the audio fit minutes instead of most of an hour; DEVICE=cpu otherwise), and
# the .NET 8 SDK. Work files go in ${WORK:-~/.cache/video-demo}.
set -euo pipefail
VIDEO=$(realpath "$1"); OUT=$(realpath -m "$2")
HERE=$(cd "$(dirname "$0")" && pwd); TOOLS="$HERE/../../tools"
PY=${PYTHON:-python3}; DEVICE=${DEVICE:-cuda:0}
WORK=${WORK:-$HOME/.cache/video-demo}; mkdir -p "$WORK"; cd "$WORK"

# The game's sounds: the list and the audio sprite, from the game's site
if [ ! -f "$HOME/.cache/audio-mosaic/sounds.json" ]; then $PY "$TOOLS/audio-mosaic/sounds.py" --fetch; fi

# The song from the game's sounds. Bad Apple is tuned to A = 432 Hz. The flagged sounds are ones the ear picks out
# (pings, stabs), the excluded ones stuck out by ear (beeps, button presses, snaps).
ffmpeg -v error -y -i "$VIDEO" -vn -ac 2 -ar 44100 song.wav
$PY "$TOOLS/audio-mosaic/mosaic.py" --device "$DEVICE" --song song.wav \
  --per-second 880 --max-per-frame 20 --refine 400 --explore 8 --relax 200 \
  --chroma-weight 1 --chroma-target 0.3 --chroma-tuning 432 \
  --flag-weight 4 --flag 207,208,209,269,270,271,272 --exclude 264,282,279,266,267,236,237,238,232 \
  --events sounds.json --out mosaic.wav

# The frames: flicker taken out (lambda 0.42) and at most 700 pixel changes a frame (a scene cut spreads over a few frames)
$PY "$TOOLS/video-frames/frames.py" "$VIDEO" write 84 48 0.42 frames.raw --budget 700

dotnet run --project "$HERE" -c Release -- frames.raw 84 48 2 sounds.json "$OUT"
# A preview of what the level shows and plays
$PY "$TOOLS/video-frames/render.py" frames.raw 84 48 2 preview.mp4 mosaic.wav
echo "preview: $WORK/preview.mp4"
