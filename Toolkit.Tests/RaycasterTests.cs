using LevelXML;
using LevelXMLUtils.Raycasting;
using Xunit;
namespace LevelXMLUtils.Tests;

// The raycaster run in the trigger simulator, against a plain raycast of the same map: what each column shows is read from the ray walk's column relays
// that fired in the redraw (DepthColumn, HueColumn, FaceColumn).
public class RaycasterTests
{
	// small enough to build and simulate quickly
	static readonly RaycastSettings Small = new() { Columns = 24, Depths = 12, Facings = 16, MaxRange = 8, WallHeightCells = 1.5 };
	static readonly WallMaterial[] Materials = { new(0xC0B0A0), new(0x90A0C0, new double[] { 1, 0.85, 0.85, 1, 1, 0.85, 0.85, 1 }), new(0xA0C090) };
	static readonly string[] Rows =
	{
		"#########",
		"#.......#",
		"#..p....#",
		"#.......=",
		"#....p..#",
		"#.......#",
		"####=####",
	};
	static RaycastMap Map() => RaycastMap.FromRows(Rows, new Dictionary<char, int> { ['#'] = 0, ['p'] = 1, ['='] = 2 }, Materials);

	static string Id(Input input) => TriggerGraph.IdFor((ActivateTrigger)input.First().Targeted);
	static string Id(Relay relay) => TriggerGraph.IdFor(relay.Output.Single());
	static TriggerGraph Graph(Raycaster raycaster) => TriggerGraph.FromTriggers(raycaster.Entities.OfType<ActivateTrigger>());

	// What each column shows after the last pulse: (depth step, material, face), or null for nothing in range
	static (int depth, int material, int face)?[] Screen(Raycaster r, TriggerGraph g)
	{
		var shown = new (int, int, int)?[r.Settings.Columns];
		for (int c = 0; c < r.Settings.Columns; c++)
		{
			int depth = Enumerable.Range(0, r.Settings.Depths).SingleOrDefault(d => g.LastFired.Contains(Id(r.Walk.DepthColumn[c, d])), -1);
			if (depth < 0) { continue; }
			int material = Enumerable.Range(0, r.Map.Materials.Count).Single(m => g.LastFired.Contains(Id(r.Walk.HueColumn[c, m])));
			int face = Enumerable.Range(0, 4).Single(f => g.LastFired.Contains(Id(r.Walk.FaceColumn[c, f])));
			shown[c] = (depth + 1, material, face);
		}
		return shown;
	}

	static (int depth, int material, int face)?[] Expected(RaycastSettings s, RaycastMap map, int px, int py, int f) =>
		Enumerable.Range(0, s.Columns).Select(c =>
		{
			foreach (var (dx, dy, face, depth, _) in s.RayPath(f, c))
			{
				if (map.WallAt(px + dx, py + dy)) { return ((int, int, int)?)(depth, map.MaterialOf(px + dx, py + dy, face), face); }
			}
			return null;
		}).ToArray();

	static (int x, int y) Cell(Raycaster r, TriggerGraph g)
	{
		var cells = Enumerable.Range(0, r.Player.Cell.Length).Where(i => r.Player.Cell[i] is Relay relay && g.LastFired.Contains(Id(relay))).ToList();
		Assert.Single(cells);
		return (cells[0] % r.Map.Width, cells[0] / r.Map.Width);
	}

	[Fact]
	public void TheFirstView_IsTheRaycastOfTheMap()
	{
		Raycaster r = new(Small, Map(), 1, 3, startFacing: 4);
		TriggerGraph g = Graph(r);
		g.Pulse(Id(r.Start));
		Assert.Empty(g.Runaway);
		Assert.Equal(Expected(Small, r.Map, 1, 3, 4), Screen(r, g));
		Assert.Equal((1, 3), Cell(r, g));
	}

	[Fact]
	public void EveryFacing_FromSeveralCells_ShowsTheRaycast()
	{
		RaycastMap map = Map();
		foreach ((int x, int y) in new[] { (1, 1), (4, 3), (7, 5) })
		{
			Raycaster r = new(Small, map, x, y);
			TriggerGraph g = Graph(r);
			g.Pulse(Id(r.Start));
			for (int f = 0; f < Small.Facings; f++)
			{
				Assert.Equal(Expected(Small, map, x, y, f), Screen(r, g));
				Assert.Empty(g.Runaway);
				g.Pulse(Id(r.TurnRight));
			}
			// all the way round, and back the other way
			Assert.Equal(Expected(Small, map, x, y, 0), Screen(r, g));
			g.Pulse(Id(r.TurnLeft));
			Assert.Equal(Expected(Small, map, x, y, Small.Facings - 1), Screen(r, g));
		}
	}

	[Fact]
	public void WalkingStraight_StopsAtTheWall()
	{
		Raycaster r = new(Small, Map(), 1, 3, startFacing: 4); // east
		TriggerGraph g = Graph(r);
		g.Pulse(Id(r.Start));
		for (int i = 0; i < 10; i++) { g.Pulse(Id(r.Forward)); }
		Assert.Equal((7, 3), Cell(r, g));
		Assert.Equal(Expected(Small, r.Map, 7, 3, 4), Screen(r, g));
		g.Pulse(Id(r.Back));
		Assert.Equal((6, 3), Cell(r, g));
		g.Pulse(Id(r.StrafeRight)); // south
		Assert.Equal((6, 4), Cell(r, g));
		g.Pulse(Id(r.StrafeLeft));
		g.Pulse(Id(r.StrafeLeft));
		Assert.Equal((6, 2), Cell(r, g));
	}

	[Fact]
	public void APillar_BlocksTheWay()
	{
		Raycaster r = new(Small, Map(), 1, 2, startFacing: 4);
		TriggerGraph g = Graph(r);
		g.Pulse(Id(r.Start));
		for (int i = 0; i < 5; i++) { g.Pulse(Id(r.Forward)); }
		Assert.Equal((2, 2), Cell(r, g)); // the pillar is at (3, 2)
	}

	[Fact]
	public void WalkingAtASlant_NeverEntersAWall_AndBackRetracesIt()
	{
		RaycastMap map = Map();
		for (int f = 1; f < Small.Facings; f += 3)
		{
			Raycaster r = new(Small, map, 4, 3, startFacing: f);
			TriggerGraph g = Graph(r);
			g.Pulse(Id(r.Start));
			var path = new List<(int, int)> { Cell(r, g) };
			for (int i = 0; i < 6; i++)
			{
				g.Pulse(Id(r.Forward));
				Assert.Empty(g.Runaway);
				(int x, int y) at = Cell(r, g);
				Assert.True(map.OpenAt(at.x, at.y));
				Assert.Equal(Expected(Small, map, at.x, at.y, f), Screen(r, g));
				path.Add(at);
			}
			// in open space (no slides), walking back retraces the steps: the sub-cell counters are shared
			if (path.Distinct().Count() == path.Count)
			{
				for (int i = path.Count - 2; i >= 0; i--) { g.Pulse(Id(r.Back)); Assert.Equal(path[i], Cell(r, g)); }
			}
		}
	}

	[Fact]
	public void ATexturedWall_SetsItsStripes_AndAPlainWallDoesNot()
	{
		Raycaster r = new(Small, Map(), 2, 3, startFacing: 0); // north: the pillar at (3, 2) is ahead and to the right
		TriggerGraph g = Graph(r);
		g.Pulse(Id(r.Start));
		var screen = Screen(r, g);
		for (int c = 0; c < Small.Columns; c++)
		{
			bool stripeFired = r.Walk.StripeColumn[c].Any(s => g.LastFired.Contains(Id(s.relay)));
			if (screen[c] is not (_, 1, _)) { Assert.False(stripeFired, $"column {c} is not the pillar but set a stripe"); }
		}
		Assert.Contains(Enumerable.Range(0, Small.Columns), c => r.Walk.StripeColumn[c].Any(s => g.LastFired.Contains(Id(s.relay))));
	}

	[Fact]
	public void OnCell_FiresForThePlayersCell_InEveryRedraw()
	{
		Raycaster r = new(Small, Map(), 1, 3, startFacing: 4);
		Relay goal = new();
		r.OnCell(3, 3).Connect(goal.Input);
		TriggerGraph g = TriggerGraph.FromTriggers(r.Entities.Concat(goal.Entities).OfType<ActivateTrigger>());
		g.Pulse(Id(r.Start));
		Assert.DoesNotContain(Id(goal), g.LastFired);
		g.Pulse(Id(r.Forward));
		g.Pulse(Id(r.Forward));
		Assert.Contains(Id(goal), g.LastFired);
		g.Pulse(Id(r.TurnRight));
		Assert.Contains(Id(goal), g.LastFired);
		g.Pulse(Id(r.Forward));
		Assert.DoesNotContain(Id(goal), g.LastFired);
	}

	[Fact]
	public void ForFacings_EnablesARelayOnlyWhileFacingThatWay()
	{
		Raycaster r = new(Small, Map(), 4, 3, startFacing: 2);
		Relay north = new();
		r.ForFacings(north, new[] { 0 });
		TriggerGraph g = TriggerGraph.FromTriggers(r.Entities.Concat(north.Entities).OfType<ActivateTrigger>());
		g.Pulse(Id(r.Start));
		Assert.False(g.IsEnabled(Id(north)));
		g.Pulse(Id(r.TurnLeft));
		g.Pulse(Id(r.TurnLeft));
		Assert.True(g.IsEnabled(Id(north)));
		g.Pulse(Id(r.TurnLeft));
		Assert.False(g.IsEnabled(Id(north)));
	}

	// A plain rectangle stands in for a sprite's picture (real ones are the level author's art)
	static PixelArt Placeholder(int color)
	{
		int?[,] pixels = new int?[4, 8];
		for (int x = 0; x < 4; x++) { for (int y = 0; y < 8; y++) { pixels[x, y] = color; } }
		return new PixelArt(pixels);
	}

	[Fact]
	public void ASprite_IsSeenWhereNoWallIsInTheWay_AndSwitchesState()
	{
		var states = new[] { Placeholder(0xFF0000), Placeholder(0x00FF00) };
		RaycastSprite sprite = new(6, 2, states);
		Raycaster r = new(Small, Map(), 1, 2, startFacing: 4, sprites: new[] { sprite });
		TriggerGraph g = Graph(r);
		// the size relays the walk fires for the sprite
		bool Seen() => r.Walk.SpriteSize[0].Values.Any(relay => g.LastFired.Contains(Id(relay)));
		g.Pulse(Id(r.Start));
		Assert.Empty(g.Runaway);
		Assert.False(Seen()); // the pillar at (3, 2) is in the way
		g.Pulse(Id(r.StrafeRight));
		Assert.True(Seen());
		Assert.True(sprite.Views.ContainsKey((3 * r.Map.Width + 1, 4)));
		// the shown object is the current state's: its show relay for that size fires, the other state's is disabled
		int size = r.Walk.SpriteSize[0].Single(kv => g.LastFired.Contains(Id(kv.Value))).Key;
		Assert.Contains(Id(sprite.ShowRelays[size][0]), g.LastFired);
		Assert.False(g.IsEnabled(Id(sprite.ShowRelays[size][1])));
		g.Pulse(Id(sprite.ShowState[1]));
		g.Pulse(Id(r.Redraw));
		Assert.Contains(Id(sprite.ShowRelays[size][1]), g.LastFired);
		g.Pulse(Id(sprite.Hide));
		g.Pulse(Id(r.Redraw));
		Assert.False(sprite.ShowRelays[size].Any(s => g.LastFired.Contains(Id(s))));
	}

	[Fact]
	public void ABlockingSprite_StopsTheWalk_UntilItIsUnblocked()
	{
		RaycastSprite door = new(5, 3, new[] { Placeholder(0x808080) }, blocking: true);
		Raycaster r = new(Small, Map(), 1, 3, startFacing: 4, sprites: new[] { door });
		TriggerGraph g = Graph(r);
		g.Pulse(Id(r.Start));
		for (int i = 0; i < 6; i++) { g.Pulse(Id(r.Forward)); }
		Assert.Equal((4, 3), Cell(r, g));
		g.Pulse(Id(door.Unblock));
		for (int i = 0; i < 6; i++) { g.Pulse(Id(r.Forward)); }
		Assert.Equal((7, 3), Cell(r, g));
		// blocked again behind the player: walking back stops in front of it
		g.Pulse(Id(door.Block));
		for (int i = 0; i < 6; i++) { g.Pulse(Id(r.Back)); }
		Assert.Equal((6, 3), Cell(r, g));
		// a slanted walk can't cut past it either
		Raycaster slant = new(Small, Map(), 4, 2, startFacing: 6, sprites: new[] { new RaycastSprite(5, 3, new[] { Placeholder(0x808080) }, blocking: true) });
		TriggerGraph sg = Graph(slant);
		sg.Pulse(Id(slant.Start));
		for (int i = 0; i < 4; i++) { sg.Pulse(Id(slant.Forward)); Assert.NotEqual((5, 3), Cell(slant, sg)); }
	}

	[Fact]
	public void FartherPictures_AreUsedFromTheirDistance()
	{
		PixelArt near = Placeholder(0xFF0000), medium = Placeholder(0x00FF00), far = Placeholder(0x0000FF);
		RaycastSprite sprite = new(6, 2, new[] { near }, farther: new (double, IReadOnlyList<PixelArt>)[] { (3, new[] { medium }), (7, new[] { far }) });
		Assert.Same(near, sprite.StatesAt(1)[0]);
		Assert.Same(medium, sprite.StatesAt(3)[0]);
		Assert.Same(medium, sprite.StatesAt(6.9)[0]);
		Assert.Same(far, sprite.StatesAt(12)[0]);
		Assert.Throws<ArgumentException>(() => new RaycastSprite(6, 2, new[] { near, medium }, farther: new (double, IReadOnlyList<PixelArt>)[] { (3, new[] { far }) }));
		Assert.Throws<ArgumentException>(() => new RaycastSprite(6, 2, new[] { near }, farther: new (double, IReadOnlyList<PixelArt>)[] { (7, new[] { far }), (3, new[] { medium }) }));
		// it builds and is seen like any other sprite
		Raycaster r = new(Small, Map(), 1, 3, startFacing: 4, sprites: new[] { sprite });
		TriggerGraph g = Graph(r);
		g.Pulse(Id(r.Start));
		Assert.Contains(r.Walk.SpriteSize[0].Values, relay => g.LastFired.Contains(Id(relay)));
	}

	[Fact]
	public void ABadMap_IsRefused()
	{
		Assert.Throws<ArgumentException>(() => RaycastMap.FromRows(new[] { "###", "#..", "###" }, new Dictionary<char, int> { ['#'] = 0 }, Materials));
		Assert.Throws<ArgumentException>(() => new Raycaster(Small with { Facings = 10 }, Map(), 1, 1));
		Assert.Throws<ArgumentException>(() => new Raycaster(Small, Map(), 0, 0));
	}
}
