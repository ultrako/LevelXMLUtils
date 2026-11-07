# LevelXMLUtils
A collection of programs that parse Happy Wheels LevelXML strings and transform them in some way.

- `Toolkit`: a library for building levels in code: trigger logic, a trigger simulator, an optimizer and minifier,
  level-to-C# code generation, picture-to-art converters, video with sound, a first-person raycaster and keyboard
  controls.
  See [Toolkit/README.md](Toolkit/README.md).
- `LevelTool`: the toolkit's command line (codegen, minify, trace, svg, pixelart).
- `tools/video-frames`: chooses a video's frames for a trigger screen ([README](tools/video-frames/README.md)).
- `tools/audio-mosaic`: makes a song out of the game's sound effects ([README](tools/audio-mosaic/README.md)).

## Examples

Small programs that show the basics of the LevelXML library: read a level from stdin, change its entities, write
it to stdout.

- `Examples/ReverseLevel`: mirrors a level left to right around a vertical line (x = 2000, or the first argument):
  `dotnet run --project Examples/ReverseLevel -- 2000 < level.xml > mirrored.xml`
- `Examples/Paperify`: puts a red art-shape copy over every triangle and circle (circles drawn as polygons with a
  side about every 20 units). The output has only those shapes and their copies.
- `Examples/RaycastDemo`: a playable first-person level made with the toolkit's raycaster and controls, a lever
  that lowers spikes and a finish line: `dotnet run --project Examples/RaycastDemo -- demo.xml`
- `Examples/VideoDemo`: Bad Apple!! with sound, from the video file you give it (not included):
  `Examples/VideoDemo/make.sh badapple.webm out.xml`
