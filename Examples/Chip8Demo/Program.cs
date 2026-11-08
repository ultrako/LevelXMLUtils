// A CHIP-8 game compiled into triggers, played with the keyboard controls. The games in programs/ (CC0, from the CHIP-8 Archive) have
// presets; any other .ch8 file works with the options below.
//   dotnet run --project Examples/Chip8Demo -c Release -- br8kout br8kout.xml
//   dotnet run --project Examples/Chip8Demo -c Release -- game.ch8 game.xml speed=15 keys=left:7,right:9 colors=FFFFFF,000000
//   dotnet run --project Examples/Chip8Demo -c Release -- measure game.ch8 [seconds=10] [speed=11] [modern] [fuzz] [keys=KEY:FROM-TO,...]
// Level options (KEY=VALUE):
//   speed=N        instructions per 60 Hz frame (the archive's "tickrate")
//   keys=...       which control presses which CHIP-8 key (hex): left, right, up, down, space, shift, ctrl
//   colors=ON,OFF  the screen's colours (hex)
//   persistence=0  draw pixels as they are; by default a pixel turned off stays lit for a frame, which hides the flicker of games that
//                  erase and redraw their sprites
//   win=ADDRESS    the game's own victory screen when the program reaches this address (hex)
//   lock=red,blue,...  a colour-code lock (ColorLock) over the screen; the game starts once it's opened. Keep the code to yourself.
//   physics=0      no physics randomness (by default CXNN's random numbers also depend on when the player pressed what)
//   blackhole=0    keep the character (by default he is black-holed once he grabs the seat, so the screen can be plain shapes)
//   fused=0        Octo's exact timing (by default Danm8ku's bullet routine runs as one step; other games are unaffected)
using System.Globalization;
using LevelXML;
using LevelXMLUtils;
using LevelXMLUtils.Computer;
using LevelXMLUtils.Controls;
using LevelXMLUtils.Devices;

if (args.Length < 2) { Console.Error.WriteLine("usage: GAME OUTPUT.xml [options], or measure GAME [options]; see the top of Program.cs"); return 2; }
Dictionary<string, string> options = args.Skip(2).Where(a => a.Contains('=')).ToDictionary(a => a[..a.IndexOf('=')], a => a[(a.IndexOf('=') + 1)..]);
string? Option(string key) => options.TryGetValue(key, out string? value) ? value : null;

// The included games: speed, colours, keys, persistence, the address that means the player won
var presets = new Dictionary<string, (int speed, string colors, string keys, bool persistence, int? win)>
{
	// keys 7 and 9 slide the paddle
	["br8kout"] = (7, "FF84FE,CA2553", "left:7,right:9", true, null),
	// key 6 serves, 7 and 9 slide the paddle
	["superpong"] = (30, "43523D,C7F0D8", "left:7,right:9,up:6", true, null),
	// 5, 8, 7 and 9 move (and start the game); after the last wave it shows its "you win" screen at 0x711
	["danm8ku"] = (1000, "00FF00,000000", "up:5,down:8,left:7,right:9", false, 0x711),
};
string ProgramPath(string game) => File.Exists(game) ? game : Path.Combine(AppContext.BaseDirectory, "programs", game + ".ch8");

if (args[0] == "measure") { return Measure(ProgramPath(args[1])); }

string name = Path.GetFileNameWithoutExtension(args[0]);
var preset = presets.TryGetValue(name, out var p) ? p : (speed: 11, colors: "FFFFFF,000000", keys: "left:4,right:6,up:2,down:8,space:5", persistence: true, win: (int?)null);
byte[] program = File.ReadAllBytes(ProgramPath(args[0]));
int speed = int.Parse(Option("speed") ?? preset.speed.ToString());
string[] colors = (Option("colors") ?? preset.colors).Split(',');
Dictionary<string, int> keys = (Option("keys") ?? preset.keys).Split(',').Select(part => part.Split(':')).ToDictionary(kv => kv[0], kv => Convert.ToInt32(kv[1], 16));
int? win = Option("win") is string w ? Convert.ToInt32(w, 16) : preset.win;
Chip8Quirks quirks = Option("fused") == "0" ? Chip8Quirks.Octo with { FuseTimerWaits = true } : Chip8Quirks.Danm8kuFused;
bool blackHole = Option("blackhole") != "0";

// The player sits at the spawn; the controls themselves are far away, out of earshot of their jets
double spawnX = 180, spawnY = 140;
SeatedControls seated = new(spawnX, spawnY, tuning: new ControlsTuning());
// The screen: 64 by 32 pixels of 14 units from (2, 26), filling the game's window
Chip8Machine machine = new(program, quirks, speed, 2, 26, 14, onColor: Convert.ToInt32(colors[0], 16), offColor: Convert.ToInt32(colors[1], 16),
	ram: Chip8Machine.MeasureWrites(program, quirks, speed),
	persistence: Option("persistence") is string persist ? persist != "0" : preset.persistence,
	reached: win is int at ? new[] { at } : null,
	// Danm8ku: a frame whose first tick moved 24 bullets or more skips its second
	heavyBullets: name == "danm8ku" ? 24 : 0,
	foregroundScreen: !blackHole);
List<Entity> entities = new();
if (win is int won)
{
	VictoryTrigger victory = new() { X = spawnX, Y = spawnY, Width = 5, Height = 5, TriggeredBy = TriggeredBy.Triggers };
	machine.Reached[won].Connect(new Input { new Target<Trigger>(victory, new Activate()) });
	entities.Add(victory);
}
ColorLock? colorLock = Option("lock") is string code ? new ColorLock(ColorLock.Parse(code), 2 + 64 * 14 / 2.0, 26 + 32 * 14 / 2.0) : null;
if (colorLock is null) { new Output(new[] { seated.Start }).Connect(machine.Start); }
else { colorLock.Unlocked.Connect(machine.Start); }

NassingerControls c = seated.Controls;
var controls = new Dictionary<string, (Output on, Output off)>
{
	["up"] = (c.UpOn, c.UpOff), ["down"] = (c.DownOn, c.DownOff), ["left"] = (c.LeftOn, c.LeftOff), ["right"] = (c.RightOn, c.RightOff),
	["space"] = (c.SpaceOn, c.SpaceOff), ["shift"] = (c.ShiftOn, c.ShiftOff), ["ctrl"] = (c.CtrlOn, c.CtrlOff),
};
// CXNN's counter also steps when a ball of the physics stirrer crosses a sensor, and every press and release kicks a ball
PhysicsRandom? stirrer = Option("physics") == "0" ? null : new PhysicsRandom(6000, 3000);
if (stirrer is not null)
{
	stirrer.Hits[0].Connect(machine.RandomCounter.Inputs[0]);
	stirrer.Hits[1].Connect(machine.RandomCounter.Inputs[3]);
}
int kick = 0;
foreach ((string control, int key) in keys)
{
	controls[control].on.Connect(machine.KeyDown[key]);
	controls[control].off.Connect(machine.KeyUp[key]);
	if (stirrer is not null) { controls[control].on.Connect(stirrer.Kick(kick++)); controls[control].off.Connect(stirrer.Kick(kick++)); }
}
entities = seated.Entities.Concat(machine.Entities).Concat(entities).Concat(stirrer?.Entities ?? Enumerable.Empty<Entity>())
	.Concat(blackHole ? new[] { seated.BlackHoleOnGrab() } : Array.Empty<Entity>()).Concat(colorLock?.Entities ?? Enumerable.Empty<Entity>())
	// lifts the editor's shape limit
	.Append(new Chain { LinkCount = double.NaN }).ToList();
LevelCruncher.OptimizeTriggers(entities);
Level level = new(entities.ToArray())
{
	X = spawnX,
	Y = spawnY,
	Character = Character.PogostickMan,
	ForcedCharacter = true,
	VehicleHidden = true,
};
File.WriteAllText(args[1], LevelCruncher.MinifyRelayTriggers(level.ToXML()));
Console.WriteLine($"wrote {args[1]}: press Space to take hold of the controls, then play");
return 0;

// Runs a program in the reference interpreter and prints what it needs: how much of memory it writes (RAM; the rest can be ROM), its
// deepest call, how much it draws, and the instructions it uses most. speed is instructions per 60 Hz frame; fuzz holds a random key
// every quarter second, so a game gets past its title screen
int Measure(string path)
{
	double seconds = Option("seconds") is string s ? double.Parse(s, CultureInfo.InvariantCulture) : 10;
	int speed = int.Parse(Option("speed") ?? "11");
	var held = (Option("keys")?.Split(',') ?? Array.Empty<string>())
		.Select(part => { string[] kv = part.Split(':'), range = kv[1].Split('-'); return (key: Convert.ToInt32(kv[0], 16), from: int.Parse(range[0]), to: int.Parse(range[1])); }).ToList();
	Chip8 chip = new(File.ReadAllBytes(path), args.Contains("modern") ? Chip8Quirks.Modern : Chip8Quirks.Vip);
	Random fuzz = new(1);
	int fuzzKey = -1, frames = (int)(seconds * 60), framesDrawing = 0;
	long mostFlips = 0;
	for (int frame = 0; frame < frames && chip.Halted is null; frame++)
	{
		if (args.Contains("fuzz") && frame % 15 == 0) { fuzzKey = fuzz.Next(3) == 0 ? -1 : fuzz.Next(16); }
		for (int key = 0; key < 16; key++) { chip.Keys[key] = key == fuzzKey || held.Any(h => h.key == key && frame >= h.from && frame <= h.to); }
		long flipsBefore = chip.PixelsFlipped, spritesBefore = chip.SpritesDrawn;
		chip.RunFrame(speed);
		mostFlips = Math.Max(mostFlips, chip.PixelsFlipped - flipsBefore);
		if (chip.SpritesDrawn > spritesBefore) { framesDrawing++; }
	}
	Console.WriteLine($"{path}: {new FileInfo(path).Length} bytes, ran {chip.Instructions} instructions over {seconds} s at {speed} a 60 Hz frame");
	if (chip.Halted is not null) { Console.WriteLine($"HALTED: {chip.Halted}"); }
	Console.WriteLine($"written (needs RAM): {chip.Written.Count} bytes at {Chip8.Ranges(chip.Written)}");
	Console.WriteLine($"read as data: {chip.ReadAsData.Count} bytes at {Chip8.Ranges(chip.ReadAsData)}");
	Console.WriteLine($"executed: {chip.Executed.Count} bytes at {Chip8.Ranges(chip.Executed)}");
	Console.WriteLine($"deepest call stack: {chip.MaxStackDepth}; sprites drawn: {chip.SpritesDrawn} (in {framesDrawing} of {frames} frames), most pixels flipped in a frame: {mostFlips}");
	Console.WriteLine("instructions by kind:");
	foreach (var (kind, count) in chip.OpcodeCounts.OrderByDescending(kv => kv.Value)) { Console.WriteLine($"  {count,9}  {kind}"); }
	Console.Write(chip.Screen());
	return 0;
}
