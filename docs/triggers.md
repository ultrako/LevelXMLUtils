# Triggers

How the game runs triggers, as far as building logic with them goes. Each fact here was measured in the game, read
from its script, or both.

## The model

A trigger is a state machine. It is enabled or disabled, and it has a list of targets, each with an action. When an
activate trigger fires, it runs its target list **in order**. Firing another trigger runs that trigger's list at once,
inside the first one's (depth first), before the first list goes on. All of this happens within the frame. A
disabled trigger that is fired does nothing.

The actions one trigger can take on another are activate (fire it), enable and disable. Logic is built from these:
a trigger's enabled state is a stored bit, and firing a pulse through triggers that only pass it when enabled is a
computation. Because lists run in order, **the order of targets is part of the logic**: the same triggers wired in
another order can answer a frame late, or not at all. `Toolkit/README.md` has recipes for the basic gates, and
`TriggerGraph` (in the toolkit) simulates all of this for tests.

## A target listed more than once

Measured 2026-11-02 and 2026-11-06 with fixture levels, and read from the game's level loader:

- **Every entry runs, in order.** A trigger listed three times with "activate" fires three times. So a trigger can
  fire another several times in one list.
- **A trigger listed more than once performs its first listed action every time.** Activate-then-disable activated
  twice; enable-then-activate on a disabled trigger enabled twice and never fired. So one trigger can't both enable
  and fire another: route one of the two through a relay (a trigger that only passes the pulse on).
- **A shape (or any other non-trigger) listed more than once runs every action listed for it at each entry, and the
  later entries' actions lose their parameters and take the editor's defaults** (an impulse becomes 10, -10, 0; an
  opacity change 100 over 1 second). A cube listed twice with a spin each got both spins plus the default impulse
  twice, and jumped. Give a shape one entry with all its actions instead.
- The editor's save writes each entry with the whole action list of its entity (three activate entries came back as
  three `[activate, activate, activate]`); the level still plays the same.
- LevelXML 4.3.0 and later keep a trigger repeated with the same action as its own entry, and throw for a different
  action. Other entities are merged into their first entry.

## How deep activations can nest

Measured 2026-11-07 in Chrome, with a trigger looping through itself and a counter to stop it: 6,268 nested
activations ran, 6,518 threw "Maximum call stack size exceeded". The limit is the browser's call stack, so it differs
between browsers and machines. What happens past it:

- The game doesn't crash. The exception aborts that frame's trigger work.
- The trigger that started it is still queued, so it runs again from its first target the next frame: its earlier
  targets happen again.
- State the deep loop had already changed is kept. So a long loop finishes after several frames, and a freeze.

A device that needs that much depth is broken in the game even if it finishes in the end. `TriggerGraph.MaxDepth`
defaults to 6,000 and reports what went past it in `Runaway`.

## What runs at once and what waits

- Trigger-to-trigger actions (activate, enable, disable) run immediately and recursively, as above.
- Of the actions on other things, delete, impulse, change collision and change limits also run immediately. **Change
  opacity** (shapes and groups), **change motor speed** (joints) and **slide** are queued and run on the next tick: the
  queue's loop takes its length when it starts, so an action queued during a tick runs in the next one. Read from the
  game's trigger code (`singleAction` of an activate trigger) and seen in the game (2026-10-20): a keyframe that
  deleted one picture and showed the next showed what was underneath for a frame. Show the new picture first and
  delete the old one a couple of frames later.

## Delays

A trigger's delay is a whole number of frames: `floor(delay * 30)`. A delay of 0 frames fires at once. Keep delays
clear of a frame boundary and of rounding: 1/30 s written as `0.033` is `floor(0.99) = 0` frames, so it fires at once.
Use 0.04 for one frame. A delayed trigger that is disabled before its delay is over doesn't fire.

## Repeating triggers

- The repeat interval is also counted in whole frames, and the trigger fires every `floor(interval * 30) + 1` frames.
- The interval's setter clamps anything under 0.1 s up to 0.1 s. Only `NaN` gets under it: a trigger with a `NaN`
  interval fires every frame.
- A repeating trigger's counter is refilled only when the trigger is disabled. A repeat that was interrupted part-way
  starts its next run part-way too; disable and re-enable it to start clean.
- A `RepeatType.Permanent` (4) trigger with `TriggeredBy.Triggers` is a clock with no body and no physics sensor:
  activating it starts it, disabling it stops it. Its first pulse lands in the same tick it is started.

## Triggers and physics

- A trigger that isn't triggered by other triggers (by the character, by click, by its targets touching it) gets a
  physics sensor at load, which counts toward the physics limit (see [limits.md](limits.md)). One with no `b`
  attribute in the XML reads as triggered by the character and gets a sensor too: relays must keep `b="5"`.
- A trigger triggered by its targets fires when one of its target shapes enters it, which lets a trigger react to
  particular shapes (`PhysicsRandom` uses this).
