# LevelXMLUtils.Toolkit

A C# library for building Happy Wheels levels in code, on top of [LevelXML](https://www.nuget.org/packages/LevelXML).
It covers trigger logic (gates, toggles, counters), a simulator to test that logic without the game, an optimizer and
minifier for big levels, and converters from pictures to art shapes.

## Trigger logic (`Logic/`)

Happy Wheels triggers are state machines: each one is enabled or disabled, and when one fires, its targets run in
list order, at once and recursively, within the frame. A trigger fired while disabled does nothing. Logic is built
from that, not from boolean values on wires:

- `Input` is a list of target actions (activate, enable or disable some trigger). `Output` is the triggers that fire
  when it is on. `output.Connect(input)` makes every trigger of the output perform the input's actions, so no extra
  trigger is spent in between.
- `Relay`: one trigger, with `Input` (fire it), `Enable` and `Disable`.
- `InstantToggle` (three triggers, flips within the frame), `DelayedToggle` (two triggers, sometimes a frame late) and
  `InstantToggleChain` (a binary counter: each bit's carry feeds the next).
- `Clickable` (fired by a click), `Panel` (a light that shows a signal), `EveryFrameTrigger`, `RepeaterTrigger`.

A trigger can't target the same trigger with two different actions: if it is listed twice, both entries take the
first one's action. So when one signal must both enable a trigger and fire it, route one of the two through a relay.

### AND, OR, NOT, XNOR and XOR

There are no gate classes, because in a real device the gate is nearly always a relay you are already wiring: a
value is "this relay is enabled", and computing is firing a pulse that only enabled relays let through. The recipes
below show the idea. In each, `a` and `b` are the signals, `start` and `finish` mark the beginning and end of a round
(often the same frame), and `result` is what the gate drives. They depend on order: each says which signals must come
first, either earlier in a trigger's target list or in an earlier frame.

**AND**: `a` stores itself by enabling a relay, and `b` fires it. `a` has to come before `b`.

```csharp
Relay and = new(startDisabled: true);
a.Output.Connect(and.Enable);
b.Output.Connect(and.Input);
finish.Output.Connect(and.Disable);   // forget a when the round is over
and.Output.Connect(result.Input);
```

An N-way AND or a lookup is the same idea: whatever enabled the relay is the condition, and one shared pulse reaches
only the enabled relays.

**OR**: when it doesn't matter if `result` fires twice, don't build anything: connect both `a` and `b` to `result`.
To get one firing however many inputs arrive, use a relay that disables itself, re-armed by `start`:

```csharp
Relay or = new();
or.Output.Connect(or.Disable);
start.Output.Connect(or.Enable);
a.Output.Connect(or.Input);
b.Output.Connect(or.Input);
or.Output.Connect(result.Input);
```

**NOT**: armed at `start`, disarmed by `a`, fired at `finish`. It fires only if `a` didn't arrive in between.

```csharp
Relay not = new();
start.Output.Connect(not.Enable);
a.Output.Connect(not.Disable);
finish.Output.Connect(not.Input);
not.Output.Connect(result.Input);
```

**XNOR** (neither or both): a NOT of `a` or `b` for "neither", and an AND for "both", driving the same `result`.
`a` has to come before `b`.

```csharp
Relay neither = new();
Relay both = new(startDisabled: true);
start.Output.Connect(neither.Enable);
a.Output.Connect(new Input(neither.Disable.Concat(both.Enable)));
b.Output.Connect(new Input(neither.Disable.Concat(both.Input)));
finish.Output.Connect(new Input(neither.Input.Concat(both.Disable)));
neither.Output.Connect(result.Input);
both.Output.Connect(result.Input);
```

**XOR** is parity: a toggle flipped by every input, read at `finish`. It doesn't care about order, and works for any
number of inputs. A toggle's `On` fires whenever it is set, by an input as well as by `Probe`, so it is read through
a relay that is only enabled at `finish` and disables itself after firing. `result` fires when an odd number of inputs
arrived. For an XNOR that doesn't care about order, use `read` as the `a` of a NOT.

```csharp
InstantToggle parity = new();
Relay read = new(startDisabled: true);
read.Output.Connect(read.Disable);
start.Output.Connect(new Input(parity.Clear.Concat(read.Disable)));
a.Output.Connect(parity.Input);
b.Output.Connect(parity.Input);
finish.Output.Connect(new Input(read.Enable.Concat(parity.Probe)));   // enable read first, then probe
parity.On.Connect(read.Input);
read.Output.Connect(result.Input);
```

`Toolkit.Tests/LogicRecipeTests.cs` builds each recipe exactly as written here and checks it in the simulator.

## Simulator (`Simulation/TriggerGraph.cs`)

`TriggerGraph.FromTriggers(triggers)` builds a model of the triggers' enabled state and targets. `Pulse(id)` fires a
trigger the way the game does, depth-first in target order, and records what fired (`LastFired`), what is enabled
(`IsEnabled`) and how many actions it took. Use it in unit tests for a device instead of loading the level. It only
models trigger-to-trigger actions; delays are honoured only with `honourDelays: true`.

## Optimizer and minifier (`Optimization/LevelCruncher.cs`)

- `OptimizeTriggers(entities)` removes triggers that can never fire or do nothing, and folds simple relays (one
  activation path, no enable or disable involved) into the triggers that fire them. It skips anything where that
  could change order or duplicate an action.
- `FactorCommonActivations` shares target lists that many triggers have in common through new relays (fewer arms
  in the file). The caller must make sure the order of those targets doesn't matter.
- `MinifyRelayTriggers(xml, ...)` writes the XML without the attributes the game fills in with defaults, without
  formatting, and optionally with numbers rounded, art outlines shared between shapes (`ShareArtVertices`) and
  straight-edged art turned into polygons. Each dropped field was checked in the game.
- `OrderTriggersByUse` renumbers the triggers so the most-targeted ones get the shortest numbers.

## Level to code (`CodeGen/LevelCodeGenerator.cs`)

Turns a level (usually imported from levelXML) into C# that rebuilds it, so a level made in the editor can be
brought into code and split into named parts. Only properties that differ from the defaults are written, and
trigger targets are wired after every trigger exists. Shapes that share another shape's outline get their own copy
of the vertices, which looks the same.

## Art (`Art/`)

- `PixelArt`: a pixel-art image as one art shape per colour (not one per pixel).
- `PixelFont`: text set in a pixel font as `PixelArt`; `PixelFont.Monogram()` is the built-in font.
- `SvgArt`: an SVG's paths, curves and arcs as art shapes.
- `VectorTrace`: any picture traced into a few smooth art shapes (palette in OKLab, Bézier fitting), and
  `VectorTrace.Render` to preview shapes the way the game draws them.

## First-person raycaster (`Raycasting/`)

`Raycaster` draws a first-person view of a grid map with triggers: walls in perspective, shaded by face and distance, a
fogged floor and ceiling, striped wall textures, and sprites (pictures standing in cells, hidden behind walls). It also
does walking, strafing and turning, with collision that slides along walls.

```csharp
RaycastMap map = RaycastMap.FromRows(rows, new Dictionary<char, int> { ['#'] = 0, ['o'] = 1 },
    new WallMaterial[] { new(0xD9B69B), new(0xDCD7CF, Texture: new[] { 1, 0.85, 0.85, 1, 1, 0.85, 0.85, 1 }) });
Raycaster view = new(new RaycastSettings(), map, startX: 2, startY: 4, startFacing: 8,
    sprites: new[] { new RaycastSprite(12, 4, new[] { leverOff, leverOn }) });
keys.UpOn.Connect(view.Forward);      // and Back, StrafeLeft, StrafeRight, TurnLeft, TurnRight
startTrigger.Connect(view.Start);     // once, when the level starts
view.OnCell(14, 8).Connect(win.Input); // fired during each redraw while the player is in that cell
```

- **The view:** `RaycastSettings` sets the screen columns, distance steps, facings, field of view, view range, wall
  height, screen position, shading and fog. Columns times distance steps sets most of the shape count and the frame
  time: 120 by 50 runs at full frame rate. The defaults fill the game's 900 by 500 window at the level's top left.
- **Building on it:** a level reacts to where the player is with `OnCell`, adds its own relays to the facing groups
  with `ForFacings` (enabled only while the player faces one of the given ways), and switches a sprite's picture with
  `ShowState` and `Hide`, then fires `Redraw`. A sprite made with `blocking:` stops the player walking into its cell
  until `Unblock` (and again after `Block`). Levers, doors and goals are built that way.
- **Sprites:** a sprite can have simpler drawings for further away (`farther:`, from a distance in cells on).
  Sprites cost physics bodies, about 26 per state at the defaults, and the game allows 1,024 in all.
  Sprites with the same pictures that are never on the screen together share their bodies.
- **Optimizing:** run `LevelCruncher.OptimizeTriggers` on the level, since much of the wiring is relays it folds away.
- **Example:** [Examples/RaycastDemo](../Examples/RaycastDemo) builds a playable level with the controls below: a
  lever that lowers the spikes blocking a doorway, and a finish line past them. Its map is 17 by 11 cells, and it
  saves at about 630 KB compressed.

## Keyboard controls (`Controls/`)

`NassingerControls` is a vehicle whose keys move parts into sensors: Up, Down, Left, Right, Space, Shift and Ctrl
each have an `On` and an `Off` output.

- **The vehicle:** Nassinger's. `ControlsTuning` changes it so each part travels a short, limited way and springs
  back, which makes taps reliable.
- **Held keys:** a repeat interval for the arrow keys turns a held key into steps, on a trigger clock that also
  catches one-tick taps.
- **Using it:** the player has to press Space first to take hold of the controls. They only work with the pogostick
  guy (`Character.PogostickMan`, forced, his vehicle hidden).
- **`SeatedControls`:** puts the controls out of earshot of their jets, with the player in a seat at the spawn (the
  start trigger deletes the joint that linked the two, and the keys stay linked).

## Command line (`LevelTool/`)

```sh
dotnet run --project LevelTool -- codegen level.xml MyLevel > MyLevel.cs
dotnet run --project LevelTool -- minify level.xml round=3 share-art > small.xml
dotnet run --project LevelTool -- trace logo.png width=300 preview=preview.png > logo.xml
dotnet run --project LevelTool -- svg drawing.svg width=300 > drawing.xml
dotnet run --project LevelTool -- pixelart sprite.png pixel=4 > sprite.xml
```

Run `LevelTool` with no arguments for every option.
