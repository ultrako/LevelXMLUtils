// A small first-person level made with the raycaster: a lever in the first room lowers the spikes in its doorway, and a finish line past them wins.
// Press Space first to take hold of the controls; then the arrow keys walk and turn, and Space pulls the lever when it's within reach.
//   dotnet run --project Examples/RaycastDemo -- demo.xml
using LevelXML;
using LevelXMLUtils;
using LevelXMLUtils.Controls;
using LevelXMLUtils.Raycasting;

string output = args.Length > 0 ? args[0] : "raycast-demo.xml";
PixelArt Art(string name) => PixelArt.Load(Path.Combine(AppContext.BaseDirectory, "art", name), mergeWithin: 8);

// '#' and '+' are walls of two colours, 'o' a striped pillar, '.' floor. The first room's only way out is the doorway at (8, 3).
string[] rows =
{
	"#################",
	"#.......#.......#",
	"#..o....#....o..#",
	"#...............#",
	"#.......+.......#",
	"#.......#.......#",
	"###+#####...#####",
	"#.......#.......#",
	"#...o.......o...#",
	"#.......#.......#",
	"#################",
};
WallMaterial[] materials =
{
	new(0xD9B69B),
	new(0x97C7D6),
	new(0xDCD7CF, Texture: new[] { 1, 0.85, 0.85, 1, 1, 0.85, 0.85, 1 }),
};
RaycastMap map = RaycastMap.FromRows(rows, new Dictionary<char, int> { ['#'] = 0, ['+'] = 1, ['o'] = 2 }, materials);

// The sprites: the lever (off, on), the spikes in the doorway (raised and blocking, lowered), and the finish line. Each state's picture stands on its
// floor point in the middle of its cell; the spikes are drawn about as wide as a cell, with simpler drawings from 3 and from 7 cells away.
(int x, int y) leverCell = (5, 4), spikeCell = (8, 3), finishCell = (2, 8);
RaycastSprite lever = new(leverCell.x, leverCell.y, new[] { Art("lever_off.png"), Art("lever_on.png") });
PixelArt[] Spikes(string distance) => new[] { Art($"spikes_up_{distance}.png"), Art($"spikes_down_{distance}.png") };
RaycastSprite spikes = new(spikeCell.x, spikeCell.y, Spikes("near"), heightFraction: 0.14, blocking: true,
	farther: new (double, IReadOnlyList<PixelArt>)[] { (3, Spikes("medium")), (7, Spikes("far")) });
RaycastSprite finish = new(finishCell.x, finishCell.y, new[] { Art("finishline.png") }, heightFraction: 0.84);

RaycastSettings settings = new();
Raycaster raycaster = new(settings, map, startX: 2, startY: 4, startFacing: settings.Facings / 4, sprites: new[] { lever, spikes, finish });
List<Entity> logic = new();

// The player sits in a seat at the top left of the level, where the camera stays on the screen; the controls are far to the right, out of earshot of
// their jets. The pogostick guy, forced and without his vehicle, is the character they work with.
const double spawnX = 250, spawnY = 70;
SeatedControls seated = new(spawnX, spawnY, turnRepeatSeconds: 0.10, moveRepeatSeconds: 0.15, tuning: new ControlsTuning());
NassingerControls keys = seated.Controls;
keys.UpOn.Connect(raycaster.Forward);
keys.DownOn.Connect(raycaster.Back);
keys.LeftOn.Connect(raycaster.TurnLeft);
keys.RightOn.Connect(raycaster.TurnRight);
new Output(new[] { seated.Start }).Connect(raycaster.Start);

// The lever is within reach from the cells near it with nothing in between: every redraw, the player's cell enables or disables inReach
Relay inReach = new(startDisabled: true);
for (int y = 0; y < map.Height; y++)
{
	for (int x = 0; x < map.Width; x++)
	{
		if (map.WallAt(x, y)) { continue; }
		double dx = leverCell.x - x, dy = leverCell.y - y;
		bool near = dx * dx + dy * dy <= 1.5 * 1.5 && map.SegmentClear(x + 0.5, y + 0.5, leverCell.x + 0.5, leverCell.y + 0.5);
		raycaster.OnCell(x, y).Connect(near ? inReach.Enable : inReach.Disable);
	}
}
// Space in reach pulls it: on lowers the spikes, off raises them again. The pictures change at the next redraw, a frame later (not inside the press's
// own handling: a redraw must not start while another runs).
InstantToggle pulled = new();
Relay redrawSoon = new(delay: 0.04);
redrawSoon.Output.Connect(raycaster.Redraw);
keys.SpaceOn.Connect(inReach.Input);
inReach.Output.Connect(new Input(pulled.Input.Concat(redrawSoon.Input)));
pulled.On.Connect(new Input(lever.ShowState[1].Concat(spikes.ShowState[1]).Concat(spikes.Unblock)));
pulled.Off.Connect(new Input(lever.ShowState[0].Concat(spikes.ShowState[0]).Concat(spikes.Block)));
logic.AddRange(inReach.Entities.Concat(pulled.Entities).Concat(redrawSoon.Entities));

// Walking onto the finish line wins
VictoryTrigger win = new() { TriggeredBy = TriggeredBy.Triggers };
raycaster.OnCell(finishCell.x, finishCell.y).Connect(new Input { new Target<Trigger>(win, new Activate()) });
logic.Add(win);

// The controls first, so the screen is drawn over them
List<Entity> entities = seated.Entities.Concat(logic).Concat(raycaster.Entities).ToList();
LevelCruncher.OptimizeTriggers(entities);
Level level = new(entities.ToArray()) { Character = Character.PogostickMan, ForcedCharacter = true, VehicleHidden = true, X = spawnX, Y = spawnY };
string xml = LevelCruncher.MinifyRelayTriggers(level.ToXML(), roundDecimals: 3);
File.WriteAllText(output, xml);
Console.WriteLine($"{output}: {entities.OfType<Trigger>().Count():N0} triggers, {xml.Length / 1024.0:N0} KB");
