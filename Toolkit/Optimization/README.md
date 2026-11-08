# Optimization

`LevelCruncher` makes big trigger levels smaller and faster: `OptimizeTriggers` removes and folds triggers,
`FactorCommonActivations` shares targets between triggers, `OrderTriggersByUse` renumbers triggers, and
`MinifyRelayTriggers` writes the XML with as little as the game needs. The Toolkit README says how to use them;
these notes are for working on them.

## Fields the game lets you leave out

Each of these was tested in the game-mod build with one small level per variant, and the defaults were read from the
game's level loader (2026-10). `MinifyRelayTriggers` drops the safe ones.

Safe to drop:

- `<a i="0"/>` (activate) on a trigger target: `<t i="N"><a i="0"/></t>` can be `<t i="N"/>`, because a trigger
  target with no action is an activate. On a big trigger level this took the XML from 25.8 to 15.8 MB.
- `p0="100"` (the opacity) on a group target's change opacity (`<g i="N"><a i="1" .../>`; on a shape, change opacity
  is action 3): 100 is the default, as it already is for shapes.
- `x`, `y`, `w`, `h` and `a` (position, size, angle) on triggers triggered by other triggers (`b="5"`), which have no
  sensor.
- `sd="f"` (starts disabled: false), and `p0="100"` on shape targets.

Not safe to drop:

- `b` (what triggers it): a missing `b` reads as 1, triggered by the character, and every trigger except `b="5"`
  creates a physics sensor. A level of 1,100 relays without `b` failed to load; with `b="5"` it loaded.
- `t` (the trigger's type): "TRIGGER TYPE NOT IN TYPE ARRAY" at load.
- `d` (the delay): a missing delay is `NaN`, and the trigger never fires.
- `p1` (an opacity change's duration): without it the game fades slowly.
- `p0="0"` on an opacity change: a change with `p0` dropped doesn't go to 0.
- `x` and `y` on a sound trigger placed in the world (`l` other than 1): without them its volume is `NaN`.

`r` (repeat type) defaults to 1 (once), so `r="1"` could be dropped.
