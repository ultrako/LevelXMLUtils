# LevelXMLUtils
A collection of programs that parse Happy Wheels LevelXML strings and transform them in some way.

- `Toolkit`: a library for building levels in code: trigger logic and small devices (a colour-code lock, physics
  randomness, counters, displays), a programmable processor, a CHIP-8 compiler, a trigger simulator, an optimizer
  and minifier, level-to-C# code generation, picture-to-art converters, video with sound, a first-person raycaster
  and keyboard controls.
  See [Toolkit/README.md](Toolkit/README.md).
- `LevelTool`: the toolkit's command line (codegen, minify, trace, svg, pixelart).
  It, `ReverseLevel` and `Paperify` (below) can be downloaded as single programs for Windows, Linux and Mac from
  the [releases](https://github.com/ultrako/LevelXMLUtils/releases), with no .NET to install.
- `tools/video-frames`: chooses a video's frames for a trigger screen ([README](tools/video-frames/README.md)).
- `tools/audio-mosaic`: makes a song out of the game's sound effects ([README](tools/audio-mosaic/README.md)).
- `tools/game-mod`: a modded build of the web game served locally, to play a level XML file like a published level
  ([README](tools/game-mod/README.md)).

## How the game works

[docs/](docs/README.md) collects what was learned about the game building big trigger levels: how triggers run, the
limits on a level's size, saving and publishing, the frame rate, physics, sound and keyboard controls, and notes on the
processor and CHIP-8 devices.

## Examples

Small programs that show the basics of the LevelXML library: read a level from stdin, change its entities, write
it to stdout.

- `Examples/ReverseLevel`: mirrors a level left to right around a vertical line (x = 2000, or the first argument):
  `dotnet run --project Examples/ReverseLevel -- 2000 < level.xml > mirrored.xml`
- `Examples/Paperify`: puts a red art-shape copy over every triangle and circle (circles drawn as polygons with a
  side about every 20 units). The output has only those shapes and their copies.
- `Examples/RaycastDemo`: a playable first-person level made with the toolkit's raycaster and controls, a lever
  that lowers spikes and a finish line: `dotnet run --project Examples/RaycastDemo -- demo.xml`
- `Examples/ProcessorDemo`: a programmable trigger computer to try by hand, with clickable memory and the
  instruction reference written in the level: `dotnet run --project Examples/ProcessorDemo -- processor.xml`
- `Examples/Chip8Demo`: a CHIP-8 game compiled into triggers, playable with the keyboard; three CC0 games from the
  CHIP-8 Archive are included: `dotnet run --project Examples/Chip8Demo -c Release -- br8kout br8kout.xml`
- `Examples/VideoDemo`: Bad Apple!! with sound, from the video file you give it (not included):
  `Examples/VideoDemo/make.sh badapple.webm out.xml`
