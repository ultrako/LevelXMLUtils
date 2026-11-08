// A video level with sound: frames chosen by tools/video-frames/frames.py, a song made of the game's sounds by
// tools/audio-mosaic/mosaic.py, played by triggers. make.sh runs the whole thing on Bad Apple!!.
//   dotnet run --project Examples/VideoDemo -- FRAMES.raw W H LEVELS EVENTS.json OUT.xml
// LEVELS: 2 for black and white (frames.py write), more for grey (frames.py writegrey). EVENTS: mosaic.py --events, or - for none.
using LevelXML;
using LevelXMLUtils;
using LevelXMLUtils.Playback;

if (args.Length < 6) { Console.Error.WriteLine("usage: FRAMES.raw W H LEVELS EVENTS.json OUT.xml"); return 1; }
string framesPath = args[0], eventsPath = args[4], output = args[5];
int width = int.Parse(args[1]), height = int.Parse(args[2]), levels = int.Parse(args[3]);
List<SoundOnset>? sounds = eventsPath == "-" ? null : Video.ReadOnsets(eventsPath);

// Square pixels filling as much of an 816 by 464 area as the shape allows, inside the game's 900 by 500 window at the
// level's top left (the camera stops there, so it frames the screen)
double pixel = Math.Min(816.0 / width, 464.0 / height);
double x = (900 - pixel * width) / 2 + pixel / 2, y = (500 - pixel * height) / 2 + pixel / 2;
Video video = levels == 2
	? new(x, y, pixel, pixel, Video.ReadFrames(framesPath, width, height), sounds)
	: new(x, y, pixel, pixel, Video.ReadLevels(framesPath, width, height), levels, sounds);

// The game's own win screen with the last frame
VictoryTrigger win = new() { TriggeredBy = TriggeredBy.Triggers };
video.End.Connect(new Input { new Target<Trigger>(win, new Activate()) });

List<Entity> entities = video.Entities.Append(win).ToList();
LevelCruncher.OptimizeTriggers(entities, video.DecoderTriggers);
// The helicopter man with a spawn of NaN, as the published Bad Apple level has it: nothing is drawn over the screen
Level level = new(entities.ToArray()) { BackgroundColor = 0x888888, Character = Character.HelicopterMan, X = double.NaN };
string xml = LevelCruncher.OrderTriggersByUse(LevelCruncher.MinifyRelayTriggers(level.ToXML()));
File.WriteAllText(output, xml);
Console.WriteLine($"{output}: {video.Counter.Value.Count:N0} frames, {sounds?.Count ?? 0:N0} sounds, {xml.Length / 1024.0:N0} KB of XML");
return 0;
