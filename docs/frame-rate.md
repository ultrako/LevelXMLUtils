# Frame rate

What costs time in a frame, measured in the game (2026-10). The game runs at 30 frames a second, so a frame has about
33 ms, part of which the game's own physics and drawing take.

## Trigger actions

Measured 2026-10-25 with a level that runs 20,000 actions of one kind a frame from a frame clock, timing the level's
trigger work per tick in headless Chromium:

| Action | Cost |
| --- | --- |
| activate a disabled trigger | 0.20 µs |
| activate an enabled trigger with no targets | 0.21 µs |
| enable a trigger | 0.20 µs |
| disable a disabled trigger | 0.20 µs |
| disable an enabled trigger | about 0.28 µs |
| **change opacity** | **5.3 µs** (about 25 times the rest, run from the queue the next tick) |

So count trigger-to-trigger actions as equal and opacity changes at about 25 each. Roughly 75,000 to 100,000 trigger
actions, or 3,000 opacity changes, fit in a frame. 100,000 extra activations of disabled triggers a press still ran at
30 frames a second. `TriggerGraph` counts actions per pulse (`ActionsCarriedOut`, `ActionsBySource`, `ActionsByKind`),
which tracked the real cost well.

## Display objects

- An idle frame's cost is the display objects over the camera, hidden or not: about 2.2 µs per group and 0.5 µs per
  shape a frame (the renderer's transform update).
- The game's culling sets every foreground object's visibility from whether it overlaps the camera, every frame. So an
  opacity change to 0 doesn't save anything: only moving an object away from the camera, or deleting it, removes its
  cost. Groups loaded at opacity 0 are no exception.
- The exception: a shape at opacity 0 that no trigger targets is never culled and never drawn.
- Every piece a trigger shows or hides has to be its own group (or its own shape), so a screen of trigger-driven pixels
  is mostly groups. 9,000 one-shape foreground groups cost about three times what the same 9,000 shapes did as
  top-level shapes. A screen made of plain shapes needs the character out of the way: black-hole him once
  he holds the keyboard controls (`SeatedControls.BlackHoleOnGrab`).

## Physics

- Box2D wakes a body and everything pinned to it when it moves: a carrier with 300 objects pinned to it cost 15 to
  20 ms a frame for half a second.
- A body moved by a joint's limits travels about 119 units a tick (Box2D's 2 metres a step): 900 units take 8 ticks.

## Drawing

- The game triangulates a shape the first time it is drawn, and draws art-shape edges as curves cut into pieces, so
  complex art costs on first sight. `LevelCruncher.MinifyRelayTriggers` can write straight-edged art as polygons.

## Measuring

- Headless Chromium with software rendering underweights all of this (a tick took about 210 ms there). Profile a
  frame in a real browser for drawing costs; headless is fine for trigger work.
- With the game-mod build, `tools/game-mod/tools/watch-level.mjs` can profile frames and evaluate expressions in the
  game (`window.__session` is the session).
