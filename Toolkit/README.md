# LevelXMLUtils.Toolkit

A C# library for building Happy Wheels levels in code, on top of [LevelXML](https://www.nuget.org/packages/LevelXML).
It covers trigger logic (gates, toggles, counters), a simulator to test that logic without the game, an optimizer and
minifier for big levels, and converters from pictures to art shapes.

How the game runs triggers, and its other rules for levels, are in [../docs](../docs/README.md).

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

The same gates come as classes, `And`, `Or`, `Not`, `Xnor` and `Xor`, with `start` as `Reset` and `finish` as
`PowerLast` where a gate needs them. `MultiRelay` and `PanelRow` are rows of n relays or panels, with their inputs
and outputs as lists. `Toolkit.Tests/DeviceTests.cs` checks the gate classes.

## Devices (`Devices/`)

Small devices, each checked in the simulator by `Toolkit.Tests/DeviceTests.cs` (and `ColorLockTests.cs`):

- `ColorLock`: coloured buttons that gate a level behind a code, for publishing a level only some people should play.
  An attempt is judged only once it's complete, so wrong clicks don't give the code away. `ColorLock.Parse("red,blue,...")`
  reads a code. It keeps out casual players, not someone who reads the level file. More in
  [Devices/color-lock.md](Devices/color-lock.md).
- `PhysicsRandom`: randomness, which triggers can't make: balls batted around a closed box by a motor, with sensors
  that only the balls set off (`Hits`). `Kick(k)` pushes a ball; connect it to the player's key presses so the
  outcome depends on their timing. Place it far from the camera; it takes about a dozen physics proxies.
- `ClickableToggle`: on and off by clicking. `Label`: text on a box that can be shown and hidden. `Numpad`: ten
  clickable keys that send a digit's bits.
- `DecimalDisplay` and `HexDisplay` (and `DecimalDisplays`/`HexDisplays` for several): a digit from four bits.
- `BCDCounter` (and `BCDCounters`): a decimal digit that counts and carries, with its nines' complement.
  `BidirectionalUnaryCounter`: a one-hot counter that steps both ways. `Addressor`: a binary counter whose
  `ProbeUnary` fires every address output except the current value's (the processor's instruction decoder).
- `ALU`, `Multiplier`, `HalfAdder`, `FullAdder`, `MultiAdder` (in `Computer/`): arithmetic on toggle chains.

## Memory and the processor (`Memory/`, `Computer/`)

More in [Computer/README.md](Computer/README.md).

- `MixedMemory`: rows of bits behind one address, any mix of read-write rows (`ReadWriteMemoryRow`, two triggers a bit),
  ROM rows (`ROMRow`, nearly free once the optimizer folds each cell into its row) and single-bit rows. An access
  switches only the current block and row, so it costs a few hundred actions however many rows there are. An address
  past the last row reads and writes nothing. `ReadWriteMemory` and `ReadOnlyMemory` are the plain kinds.
- `Processor`: a programmable computer in triggers. Word and address are the same width (`bits`); an instruction is
  an opcode word and up to two operand words, (source, destination). There are 50 opcodes: moves, add, subtract,
  compare, jumps on the carry and zero flags, multiply by a constant, push and pop, drawing on a small screen, sound,
  and two `Auxillary` outputs for the level to use. Registers 9 to 12 hold the Shift, Ctrl, Up and Down keys
  (`NassingerControls`). `Start` runs instructions back to back in the frame until `StopStepping`, or until
  `ResumeSteppingAfterFrame` waits a few frames.
- `Assembler.Assemble(words)` turns `InstructionWord`s, with labels for jumps, into the memory image for
  `SetInitialMemory`.
- Cost, measured in the simulator: about 3,000 to 3,500 actions an instruction at 9 bits (an add between registers,
  a load, a store), a little more at 12 and 16 bits, so roughly 20 instructions a frame. Full read-write memory at 9
  bits is about 12,700 triggers, at 11 bits 55,000; 12 bits is over the game's limit. ROM is limited by file size.
- Reading a run in the simulator: a memory row's `BitTriggers` are the triggers holding its bits, each enabled while
  its bit is 1, and `row.ValueIn(graph)` reads the row's value. The processor exposes `Registers` and `MemoryRows`.
- `Examples/ProcessorDemo` builds a level to try it by hand. `Toolkit.Tests/ProcessorTests.cs` runs programs in the
  simulator, and `MixedMemoryTests.cs` checks memory against a plain array.

## CHIP-8 (`Computer/Chip8/`)

More in [Computer/Chip8/README.md](Computer/Chip8/README.md).

- `Chip8Machine` compiles a CHIP-8 program into triggers. The program is fixed when the level is built, so there is
  no fetch or decode: every instruction the program can reach (followed from 0x200 through its jumps, calls and
  skips, with what is known about the registers at each) is its own short trigger program, and a one-hot relay per
  instruction says which runs next. A frame clock runs `tickrate` instructions a game frame. Memory is a
  `MixedMemory`: RAM only where the program can store (worked out from the program, and measured by
  `Chip8Machine.MeasureWrites` for stores it can't bound), ROM everywhere else.
- `Chip8` is a plain C# interpreter, the reference the machine is checked against (`Chip8MachineTests` runs both
  side by side, register for register), and a way to measure what a game needs. `Chip8Quirks` holds the behaviours
  interpreters disagree on: `Vip` (the original COSMAC VIP), `Modern`, and `Octo` (what the CHIP-8 Archive's games
  run on). The lockstep runs of every Danm8ku wave and the cost measurements take about half an hour, so they
  only run with `SLOW_TESTS=1 dotnet test Toolkit.Tests`; a plain `dotnet test` keeps a few minutes of them.
- `Examples/Chip8Demo` builds a playable level from a `.ch8` file, with the keyboard controls, optional physics
  randomness for `CXNN` and an optional colour lock; its `measure` command runs a program in the interpreter. Three
  CC0 games from the CHIP-8 Archive are included with presets.

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

## Video and sound (`Playback/`)

`Video` plays a video with triggers at the game's 30 frames a second. A clock steps a `UnaryCounter`, and each frame's
relay sends its pixel changes and starts its sounds.

- **Decoders:** `VideoDecoder` plays black and white with row and column commands instead of one arm per pixel
  change. `GreyVideoDecoder` sets pixels to grey levels.
- **The decoder's commands:**
  - **Bands:** rows are grouped in bands, so a column command reaches only the selected band.
  - **Row steps:** "down d" selects a row a few below the last one, so the frames repeat a few commands that compress
    well.
  - **Inversion:** a row where most pixels change is inverted first.
  - A frame lists a command once for each row it changes in.
- **Sounds:** onsets come from tools/audio-mosaic. `SoundChords` shares groups of sounds that many frames start
  together; tied onsets (a repeated stretch of the song) play through one relay.
- **Inputs:** `Video.ReadFrames` and `ReadLevels` read what tools/video-frames writes, `ReadOnsets` what the mosaic
  writes, and `ReadVideo` takes frames straight from a video file with ffmpeg.
- **Optimizing:** run `LevelCruncher.OptimizeTriggers` with `DecoderTriggers` kept, then `MinifyRelayTriggers` and,
  last, `OrderTriggersByUse`.
- **Example:** [Examples/VideoDemo](../Examples/VideoDemo) builds a Bad Apple!! level with sound from the video file.

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
- **Using it:** the player has to press Space first to take hold of the controls. A player holder (`PlayerHolder`)
  around the character makes sure he can grab them; the toolkit's is shaped for the pogostick guy
  (`Character.PogostickMan`, forced, his vehicle hidden), and other characters need a holder shaped for them.
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
