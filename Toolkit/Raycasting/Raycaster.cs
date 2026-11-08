using LevelXML;
namespace LevelXMLUtils.Raycasting;

/// <summary>
/// A first-person view of a grid map drawn with triggers: walls in perspective with face and distance shading and fogged floor and ceiling, sprites
/// standing in cells, and walking, strafing and turning with collision that slides along walls. Connect controls to the inputs (<see cref="Forward"/>,
/// <see cref="TurnLeft"/> and the rest, each a whole step: a held key needs a repeating clock), fire <see cref="Start"/> once when the level starts,
/// and put <see cref="Entities"/> in the level.
/// </summary>
/// <remarks>
/// <para>The screen is a stack of foreground groups per column: the backdrop (floor and ceiling fog), a colour panel per material, a face overlay, a
/// texture overlay (when a material is textured), a shade overlay and mask bars per distance step, which cover the panel above and below the wall's
/// height. A redraw resets what the last one showed, finds the player's cell, walks the rays (<see cref="RayWalk"/>) and shows what they hit; pieces
/// shown again are left alone, since opacity changes are what a redraw can least afford.</para>
/// <para>The player is a cell and a facing (<see cref="RaycastSettings.Facings"/> directions, 0 north and clockwise), kept as which relays are enabled.
/// Everything that works for only one facing is in that facing's group, switched on a turn; a level can add its own relays to the groups with
/// <see cref="ForFacings"/>. <see cref="OnCell"/> fires during each redraw for the cell the player is in, which is how a level reacts to where the
/// player is (a goal, a trap, a lever in reach).</para>
/// <para>Run <see cref="LevelCruncher"/> on the level: much of the wiring is relays it folds away.</para>
/// </remarks>
public class Raycaster : TriggerContraption
{
	public RaycastSettings Settings { get; }
	public RaycastMap Map { get; }
	public IReadOnlyList<RaycastSprite> Sprites { get; }

	/// <summary>Draws the first view (turning to the start facing first). Fire it once, from a trigger the level starts with.</summary>
	public Input Start => start.Input;
	/// <summary>A step, then a redraw. Strafing walks as the facing a quarter turn to the side.</summary>
	public Input Forward => forward.Input;
	public Input Back => back.Input;
	public Input StrafeLeft => strafeLeft.Input;
	public Input StrafeRight => strafeRight.Input;
	/// <summary>One facing clockwise or anticlockwise, then a redraw.</summary>
	public Input TurnRight => turnRight.Input;
	public Input TurnLeft => turnLeft.Input;
	/// <summary>Redraws the view, for example after a sprite's state changed. Don't fire it while a redraw is running (from <see cref="OnCell"/>, say): use
	/// a relay with a delay of a frame or two.</summary>
	public Input Redraw => redraw.Input;

	/// <summary>Fired during every redraw while the player is in the open cell (x, y), after the screen's reset and before the ray walk.</summary>
	public Output OnCell(int x, int y) => player.Cell[y * Map.Width + x]?.Output ?? throw new ArgumentException($"({x}, {y}) is a wall");

	/// <summary>Puts a relay in the facing groups: from now on it is enabled while the player faces one of the given facings and disabled otherwise. Its
	/// start state is set for the start facing.</summary>
	public void ForFacings(Relay relay, IEnumerable<int> facings)
	{
		var set = facings.ToHashSet();
		relay.StartDisabled = !set.Contains(0); // the facing ring starts at 0; Start turns to the start facing, switching the groups
		foreach (int f in set)
		{
			enableGroup[f].Output.Connect(relay.Enable);
			disableGroup[f].Output.Connect(relay.Disable);
		}
	}

	/// <summary>The ray walk: its column relays (DepthColumn, HueColumn, FaceColumn) fire for what each column shows, which is how tests read the screen.</summary>
	public RayWalk Walk { get; }
	/// <summary>The player's cell.</summary>
	public PlayerCell Player => player;

	readonly Relay start = new(), forward, back, strafeLeft, strafeRight, turnRight = new(), turnLeft = new(), redraw = new();
	readonly PlayerCell player;
	readonly Relay[] enableGroup, disableGroup;
	readonly List<Entity> relays = new(), screen = new();
	Relay NewRelay(bool disabled = false, double delay = 0) { Relay r = new(startDisabled: disabled, delay: delay); relays.AddRange(r.Entities); return r; }

	/// <param name="startX">The player's starting cell (open floor).</param>
	/// <param name="startFacing">The starting facing: 0 north, clockwise in <see cref="RaycastSettings.Facings"/> steps.</param>
	public Raycaster(RaycastSettings settings, RaycastMap map, int startX, int startY, int startFacing = 0, IReadOnlyList<RaycastSprite>? sprites = null)
	{
		settings.Validate();
		if (map.WallAt(startX, startY)) { throw new ArgumentException($"the start ({startX}, {startY}) is a wall"); }
		if (startFacing < 0 || startFacing >= settings.Facings) { throw new ArgumentException($"no facing {startFacing}"); }
		Settings = settings; Map = map; Sprites = sprites ?? Array.Empty<RaycastSprite>();
		int W = map.Width, H = map.Height, facings = settings.Facings, columns = settings.Columns, depths = settings.Depths;
		relays.AddRange(new[] { start, turnRight, turnLeft, redraw }.SelectMany(r => r.Entities));

		// The facing groups: one master per facing that switches its relays on and one that switches them off (ForFacings)
		enableGroup = Enumerable.Range(0, facings).Select(_ => NewRelay()).ToArray();
		disableGroup = Enumerable.Range(0, facings).Select(_ => NewRelay()).ToArray();
		void Gate(Relay relay, IEnumerable<int> fs) => ForFacings(relay, fs);

		player = new PlayerCell(W, H, map.OpenAt, startX, startY);
		relays.AddRange(player.Entities);

		// The facing is a ring of relays like the cell: right[f] and left[f] are enabled while the player faces f. A turn fires the ring in the order that
		// passes each relay before it can be enabled, so it moves once; the step across the wrap goes through a relay fired at the end of the pass.
		var right = Enumerable.Range(0, facings).Select(f => NewRelay(f != 0)).ToArray();
		var left = Enumerable.Range(0, facings).Select(f => NewRelay(f != 0)).ToArray();
		Relay wrapRight = NewRelay(true), wrapLeft = NewRelay(true);
		IEnumerable<Target> FaceTo(int g) => right[g].Enable.Concat(left[g].Enable);
		for (int f = 0; f < facings; f++)
		{
			int r = (f + 1) % facings, l = (f + facings - 1) % facings;
			IEnumerable<Target> leave = right[f].Disable.Concat(left[f].Disable).Concat(disableGroup[f].Input);
			right[f].Output.Connect(new Input(leave.Concat(enableGroup[r].Input).Concat(r == 0 ? wrapRight.Enable : FaceTo(r))));
			left[f].Output.Connect(new Input(leave.Concat(enableGroup[l].Input).Concat(l == facings - 1 ? wrapLeft.Enable : FaceTo(l))));
		}
		wrapRight.Output.Connect(new Input(FaceTo(0).Concat(wrapRight.Disable)));
		wrapLeft.Output.Connect(new Input(FaceTo(facings - 1).Concat(wrapLeft.Disable)));
		Relay turnRightRaw = NewRelay(), turnLeftRaw = NewRelay();
		turnRightRaw.Output.Connect(new Input(right.Reverse().SelectMany(r => r.Input).Concat(wrapRight.Input)));
		turnLeftRaw.Output.Connect(new Input(left.SelectMany(r => r.Input).Concat(wrapLeft.Input)));

		// The sprites: where each is seen from, and the pools that draw them
		var geometry = new SpriteGeometry(settings, map);
		List<RayWalk.SpriteSpec> specs = new();
		foreach (RaycastSprite sprite in Sprites)
		{
			// the pictures drawn at each size, with what share of their pixels is in each column (for whether any of them is on the screen)
			var massesAt = new Dictionary<int, IReadOnlyList<(PixelArt, double[], double)>>();
			IReadOnlyList<(PixelArt, double[], double)> MassesAt(int size) =>
				massesAt.TryGetValue(size, out var m) ? m : massesAt[size] = SpriteGeometry.Masses(sprite.StatesAt(geometry.DistanceOf(size)));
			for (int py = 0; py < H; py++)
			{
				for (int px = 0; px < W; px++)
				{
					if (map.WallAt(px, py)) { continue; }
					for (int f = 0; f < facings; f++)
					{
						if ((px, py) == (sprite.CellX, sprite.CellY)) { sprite.views[(py * W + px, f)] = (geometry.UnderfootX, geometry.UnderfootSize); }
						else if (geometry.View(sprite.CellX - px, sprite.CellY - py, f, MassesAt, sprite.HeightFraction) is (int x, int size, int anchor)
							&& geometry.Reaches(px, py, f, anchor, sprite.CellX, sprite.CellY)) { sprite.views[(py * W + px, f)] = (x, size); }
					}
				}
			}
			sprite.WalkIndex = specs.Count;
			specs.Add(new RayWalk.SpriteSpec(sprite.CellX, sprite.CellY, (ox, oy, f) => geometry.View(ox, oy, f, MassesAt, sprite.HeightFraction)));
		}
		// Sprites with the same pictures that are never on the screen together share a pool
		List<SpritePool> pools = new();
		foreach (RaycastSprite sprite in Sprites)
		{
			SpritePool? pool = pools.FirstOrDefault(p => p.Members.All(o => o.SamePictures(sprite) && !o.Views.Keys.Intersect(sprite.Views.Keys).Any()));
			if (pool is null) { pools.Add(pool = new SpritePool()); }
			pool.Members.Add(sprite);
			sprite.Pool = pool;
		}

		RayWalk walk = new(settings, map, startX, startY, Gate, specs);
		Walk = walk;
		player.MoveNorth.Output.Connect(walk.Move[0].Input); player.MoveEast.Output.Connect(walk.Move[1].Input);
		player.MoveSouth.Output.Connect(walk.Move[2].Input); player.MoveWest.Output.Connect(walk.Move[3].Input);

		// Walking (see RaycastSettings.MajorStep): a step is one cell along the facing's main axis and sometimes one along the other, kept in a sub-cell
		// counter per axis shared by every walking key, so walking back retraces walking forward
		int bits = (int)Math.Round(Math.Log2(settings.SubSteps));
		InstantToggleChain fracX = new(bits), fracY = new(bits);
		relays.AddRange(fracX.Entities); relays.AddRange(fracY.Entities);
		fracX.Overflow.Connect(player.MoveEast.Input);
		fracY.Overflow.Connect(player.MoveSouth.Input);
		Relay? XMove(int dx) => dx switch { 1 => player.MoveEast, -1 => player.MoveWest, _ => null };
		Relay? YMove(int dy) => dy switch { 1 => player.MoveSouth, -1 => player.MoveNorth, _ => null };
		Input Hops(int hops, Input target)
		{
			Relay first = NewRelay(), current = first;
			for (int i = 0; i < hops; i++)
			{
				current.Output.Connect(target);
				if (i < hops - 1) { Relay next = NewRelay(); current.Output.Connect(next.Input); current = next; }
			}
			return first.Input;
		}
		// Per facing, the walk as facing f + shift in direction dir: the major step, and the minor step's bookkeeping (a positive minor step adds MinorK to its
		// axis's counter, whose wrap takes the step; a negative one takes the step at once and adds SubSteps - MinorK, whose wrap gives it back)
		(List<Relay> allowed, List<Input> minorOnly, List<Input> majorOnly) BuildWalk(int shift, int dir)
		{
			List<Relay> allowed = new(); List<Input> minors = new(), majors = new();
			for (int f = 0; f < facings; f++)
			{
				int g = (f + shift) % facings;
				(int x, int y) major = (dir * settings.MajorStep(g).x, dir * settings.MajorStep(g).y), minor = (dir * settings.MinorStep(g).x, dir * settings.MinorStep(g).y);
				Input majorOnly = new();
				if (XMove(major.x) is Relay mx) { majorOnly = new Input(majorOnly.Concat(mx.Input)); }
				if (YMove(major.y) is Relay my) { majorOnly = new Input(majorOnly.Concat(my.Input)); }
				Input minorOnly = new();
				int k = settings.MinorK(g);
				if (k == settings.SubSteps)
				{
					if (XMove(minor.x) is Relay nx) { minorOnly = new Input(minorOnly.Concat(nx.Input)); }
					if (YMove(minor.y) is Relay ny) { minorOnly = new Input(minorOnly.Concat(ny.Input)); }
				}
				else if (k > 0)
				{
					InstantToggleChain counter = minor.x != 0 ? fracX : fracY;
					int sign = minor.x != 0 ? minor.x : minor.y;
					if (sign < 0)
					{
						if (XMove(minor.x) is Relay bx) { minorOnly = new Input(minorOnly.Concat(bx.Input)); }
						if (YMove(minor.y) is Relay by) { minorOnly = new Input(minorOnly.Concat(by.Input)); }
					}
					minorOnly = new Input(minorOnly.Concat(Hops(sign > 0 ? k : settings.SubSteps - k, counter.Input)));
				}
				Relay gate = NewRelay();
				Gate(gate, new[] { f });
				gate.Output.Connect(new Input(majorOnly.Concat(minorOnly)));
				allowed.Add(gate); minors.Add(minorOnly); majors.Add(majorOnly);
			}
			return (allowed, minors, majors);
		}
		// Collision: a press looks at the cells its step goes through with the ray walk's token (on the player's cell between redraws): M, one step along the
		// major axis, then (for a facing with a minor axis) D, diagonally past it, and N, the minor step from the player. A wall in M or D blocks the step; a
		// wall in M with N open slides along it (the minor step alone); M open and D a wall cuts the corner, so the major step alone is taken.
		// A sprite that blocks is a wall to walking only: its cell relay (fired when the token's check finds it) fires its blocking relay, enabled while it
		// blocks, which answers the queries the way a wall does (but never slides: the player stops at it)
		List<Relay> wallQueries = new(), solidQueries = new();
		Relay solidProbe = NewRelay();
		foreach (RaycastSprite sprite in Sprites.Where(sp => sp.Blocking is not null))
		{
			Relay blocks = NewRelay(sprite.Blocking != true);
			walk.SpriteCell[sprite.WalkIndex].Output.Connect(blocks.Input);
			blocks.Output.Connect(solidProbe.Input);
			Relay block = NewRelay(), unblock = NewRelay();
			block.Output.Connect(blocks.Enable);
			unblock.Output.Connect(blocks.Disable);
			sprite.Block = block.Input;
			sprite.Unblock = unblock.Input;
		}
		(int x, int y)[] steps = { (0, -1), (1, 0), (0, 1), (-1, 0) }; // RayWalk.AbsMove's order
		int D((int x, int y) v) => Array.IndexOf(steps, v);
		Relay BuildKey(int shift, int dir)
		{
			(List<Relay> allowed, List<Input> minorOnly, List<Input> majorOnly) = BuildWalk(shift, dir);
			Relay go = NewRelay(), slide = NewRelay(true), corner = NewRelay(true), cornerCandidate = NewRelay(), reset = NewRelay();
			reset.Output.Connect(new Input(go.Enable.Concat(slide.Disable).Concat(corner.Disable).Concat(cornerCandidate.Enable)));
			Relay mWall = NewRelay(true), dWall = NewRelay(true), nWall = NewRelay(true);
			mWall.Output.Connect(new Input(go.Disable.Concat(slide.Enable).Concat(cornerCandidate.Disable)));
			dWall.Output.Connect(new Input(go.Disable.Concat(cornerCandidate.Input)));
			nWall.Output.Connect(slide.Disable);
			cornerCandidate.Output.Connect(corner.Enable);
			Relay mSolid = NewRelay(true), dSolid = NewRelay(true);
			mSolid.Output.Connect(new Input(go.Disable.Concat(cornerCandidate.Disable)));
			dSolid.Output.Connect(go.Disable);
			wallQueries.AddRange(new[] { mWall, dWall, nWall });
			solidQueries.AddRange(new[] { mSolid, dSolid });
			(Relay arm, Relay disarm) Arming(params Relay[] queries)
			{
				Relay arm = NewRelay(), disarm = NewRelay();
				arm.Output.Connect(new Input(queries.SelectMany(q => q.Enable).Concat(walk.WallProbe.Enable)));
				disarm.Output.Connect(new Input(queries.SelectMany(q => q.Disable).Concat(walk.WallProbe.Disable)));
				return (arm, disarm);
			}
			var (armM, disarmM) = Arming(mWall, mSolid);
			var (armD, disarmD) = Arming(dWall, dSolid);
			var (armN, disarmN) = Arming(nWall);
			// one program per facing class (facings a quarter turn apart walk the same steps, turned, and the token's moves are turned by the quarter)
			List<Relay> entries = new();
			for (int t = 0; t < settings.FacingsPerQuarter; t++)
			{
				int g = (t + shift) % facings;
				(int x, int y) major = (dir * settings.MajorStep(g).x, dir * settings.MajorStep(g).y), minor = (dir * settings.MinorStep(g).x, dir * settings.MinorStep(g).y);
				Relay entry = NewRelay();
				Gate(entry, Enumerable.Range(0, 4).Select(q => t + settings.FacingsPerQuarter * q));
				Relay toM = NewRelay(), back = NewRelay();
				entry.Output.Connect(toM.Input);
				toM.Output.Connect(new Input(walk.AbsMove[D(major)].Input.Concat(armM.Input).Concat(walk.Check.Input).Concat(disarmM.Input)));
				if (settings.MinorK(g) > 0)
				{
					Relay toD = NewRelay(), toN = NewRelay();
					toM.Output.Connect(toD.Input);
					toD.Output.Connect(new Input(walk.AbsMove[D(minor)].Input.Concat(armD.Input).Concat(walk.Check.Input).Concat(disarmD.Input).Concat(toN.Input)));
					toN.Output.Connect(new Input(walk.AbsMove[D((-major.x, -major.y))].Input.Concat(armN.Input).Concat(walk.Check.Input).Concat(disarmN.Input).Concat(back.Input)));
					back.Output.Connect(walk.AbsMove[D((-minor.x, -minor.y))].Input);
				}
				else
				{
					toM.Output.Connect(back.Input);
					back.Output.Connect(walk.AbsMove[D((-major.x, -major.y))].Input);
				}
				entries.Add(entry);
			}
			List<Relay> slideAllowed = new(), cornerAllowed = new();
			for (int f = 0; f < facings; f++)
			{
				Relay s = NewRelay(), c = NewRelay();
				Gate(s, new[] { f }); Gate(c, new[] { f });
				s.Output.Connect(minorOnly[f]);
				c.Output.Connect(majorOnly[f]);
				slideAllowed.Add(s); cornerAllowed.Add(c);
			}
			go.Output.Connect(new Input(allowed.SelectMany(a => a.Input)));
			slide.Output.Connect(new Input(slideAllowed.SelectMany(a => a.Input)));
			corner.Output.Connect(new Input(cornerAllowed.SelectMany(a => a.Input)));
			Relay finish = NewRelay(), key = NewRelay();
			finish.Output.Connect(new Input(go.Input.Concat(slide.Input).Concat(corner.Input)));
			key.Output.Connect(new Input(reset.Input.Concat(entries.SelectMany(e => e.Input)).Concat(finish.Input).Concat(redraw.Input)));
			return key;
		}
		forward = BuildKey(0, +1);
		back = BuildKey(0, -1);
		strafeRight = BuildKey(settings.FacingsPerQuarter, +1);
		strafeLeft = BuildKey(settings.FacingsPerQuarter, -1);
		walk.WallProbe.Output.Connect(new Input(wallQueries.SelectMany(q => q.Input)));
		solidProbe.Output.Connect(new Input(solidQueries.SelectMany(q => q.Input)));

		// The screen, in screen-centred coordinates moved to the screen's centre (cx, cy)
		double cx = settings.CenterX, cy = settings.CenterY;
		double screenLeft = -settings.ScreenWidth / 2, stripWidth = settings.ScreenWidth / columns, maxWall = settings.MaxWallHeight;
		var fog = new FloorFog(settings);
		Panel Layer(double x, double height, int color) => new(x + cx, cy, stripWidth, height, offColor: color, onColor: color, outlineColor: -1, hasOff: false, startDisabled: true, foreground: true);
		Group GroupOf(Panel panel) => (Group)panel.Entities.First();
		double ColumnX(int c) => screenLeft + stripWidth * (c + 0.5);
		List<Entity> backdrop = new();
		foreach ((int nearColor, int direction) in new[] { (settings.CeilingColor, -1), (settings.FloorColor, +1) })
		{
			foreach ((double from, double to, int color) in fog.Pieces(nearColor, 0, settings.ScreenHeight / 2))
			{
				backdrop.Add(new RectangleArtShape(cx, cy + direction * (from + to) / 2, settings.ScreenWidth, to - from, color: color, outline: -1));
			}
		}
		screen.Add(new Group(backdrop.ToArray()) { Foreground = true });
		var seenMaterials = map.SeenMaterials();
		int materials = map.Materials.Count;
		var hue = new Panel?[columns, materials];
		for (int c = 0; c < columns; c++)
		{
			foreach (int m in Enumerable.Range(0, materials).Where(seenMaterials.Contains))
			{
				hue[c, m] = Layer(ColumnX(c), maxWall, map.Materials[m].Color);
				screen.AddRange(hue[c, m]!.Entities);
			}
		}
		var faceOverlay = Enumerable.Range(0, columns).Select(c => Layer(ColumnX(c), maxWall, 0x000000)).ToArray();
		screen.AddRange(faceOverlay.SelectMany(p => p.Entities));
		bool textured = Enumerable.Range(0, columns).Any(c => walk.StripeColumn[c].Count > 0);
		var textureOverlay = textured ? Enumerable.Range(0, columns).Select(c => Layer(ColumnX(c), maxWall, 0x000000)).ToArray() : null;
		if (textureOverlay is not null) { screen.AddRange(textureOverlay.SelectMany(p => p.Entities)); }
		var shadeOverlay = Enumerable.Range(0, columns).Select(c => Layer(ColumnX(c), maxWall, 0x000000)).ToArray();
		screen.AddRange(shadeOverlay.SelectMany(p => p.Entities));
		// The mask bars: a depth's bars are split into the band its wall's edge is in (Masks) and the bands beyond it, which many depths share (MaskOuter)
		double[] bandEdges = fog.Boundaries();
		var maskOuter = new MaskBars?[columns, bandEdges.Length];
		var maskOuterOf = new int[depths];
		for (int d = 1; d <= depths; d++)
		{
			double h = settings.StepHeight(d);
			int k = Array.FindIndex(bandEdges, e => e > h / 2 + 1e-9);
			maskOuterOf[d - 1] = h / 2 < fog.PanelOffset - 1e-9 && k >= 0 && bandEdges[k] < fog.PanelOffset - 1e-9 ? k : -1;
		}
		foreach (int k in maskOuterOf.Where(k => k >= 0).Distinct().OrderBy(k => k))
		{
			for (int c = 0; c < columns; c++)
			{
				maskOuter[c, k] = new MaskBars(fog, settings.CeilingColor, settings.FloorColor, ColumnX(c) + cx, cy, bandEdges[k], stripWidth);
				screen.AddRange(maskOuter[c, k]!.Entities);
			}
		}
		// depth by depth, so the bars of one depth (the same text in every column) sit close together in the file and compress well
		var masks = new MaskBars?[columns, depths];
		for (int d = 1; d <= depths; d++)
		{
			double h = settings.StepHeight(d);
			if (maxWall - h <= 0) { continue; }
			int outer = maskOuterOf[d - 1];
			for (int c = 0; c < columns; c++)
			{
				masks[c, d - 1] = new MaskBars(fog, settings.CeilingColor, settings.FloorColor, ColumnX(c) + cx, cy, h / 2, stripWidth, outer >= 0 ? bandEdges[outer] : null);
				screen.AddRange(masks[c, d - 1]!.Entities);
			}
		}

		// The sprites' objects, one per (pool, size, state), over the walls
		double rowsTop = settings.SpriteRowsTop ?? settings.ScreenTop + settings.ScreenHeight + 200;
		int row = 0;
		double RowY() => rowsTop + 10 * row++;
		Relay settle = NewRelay(delay: settings.SpriteShowDelaySeconds);
		foreach (SpritePool pool in pools)
		{
			pool.Carrier = new SpriteCarrier(cx, RowY());
			screen.Add(pool.Carrier.Group);
			relays.Add(pool.Carrier.Joint);
			RaycastSprite first = pool.Members[0];
			foreach (int size in pool.Members.SelectMany(m => m.Views.Values).Select(v => v.size).Distinct().OrderBy(z => z))
			{
				(double floorY, double height) = geometry.Drawn(size, first.HeightFraction);
				IReadOnlyList<PixelArt> pictures = first.StatesAt(geometry.DistanceOf(size));
				for (int state = 0; state < pictures.Count; state++)
				{
					PixelArt art = pictures[state];
					(double ax, double ay) = art.FloorAnchor();
					var obj = new SlidingSprite(art.ToShapes(height / art.Height, ax, ay), cx, cy + floorY, RowY(), pool.Carrier);
					screen.Add(obj.Group);
					relays.Add(obj.Joint);
					pool.Objects[(size, state)] = obj;
				}
			}
			pool.WireLatches();
			foreach (int x in pool.Members.SelectMany(m => m.Views.Values).Select(v => v.x).Distinct().OrderBy(x => x))
			{
				Relay position = new();
				position.Output.Connect(pool.Carrier.MoveTo(geometry.Placement(x, 0).x));
				pool.Positions[x] = position;
			}
			relays.AddRange(pool.RelayEntities);
			settle.Output.Connect(pool.Settle.Input);
		}
		foreach (RaycastSprite sprite in Sprites)
		{
			SpritePool pool = sprite.Pool;
			// per size, a show relay per state; only the current state's are enabled
			foreach (int size in sprite.Views.Values.Select(v => v.size).Distinct().OrderBy(z => z))
			{
				sprite.showRelays[size] = Enumerable.Range(0, sprite.States.Count).Select(state =>
				{
					Relay show = NewRelay(state != sprite.StartState);
					show.Output.Connect(pool.Claim(size, state));
					return show;
				}).ToArray();
			}
			List<Input> showState = new();
			for (int state = 0; state < sprite.States.Count; state++)
			{
				Relay set = NewRelay();
				int s1 = state;
				set.Output.Connect(new Input(sprite.ShowRelays.Values.SelectMany(byState => byState.Select((r, k) => k == s1 ? r.Enable : r.Disable)).SelectMany(t => t)));
				showState.Add(set.Input);
			}
			sprite.ShowState = showState;
			Relay hide = NewRelay();
			hide.Output.Connect(new Input(sprite.ShowRelays.Values.SelectMany(byState => byState).SelectMany(r => r.Disable)));
			sprite.Hide = hide.Input;
			// what the ray walk found: the position (moves the pool's carrier) and then the size (wants the current state's object)
			foreach ((int x, Relay found) in walk.SpriteX[sprite.WalkIndex]) { if (pool.Positions.TryGetValue(x, out Relay? position)) { found.Output.Connect(position.Input); } }
			foreach ((int size, Relay found) in walk.SpriteSize[sprite.WalkIndex])
			{
				if (sprite.ShowRelays.TryGetValue(size, out Relay[]? shows)) { found.Output.Connect(new Input(shows.SelectMany(r => r.Input))); }
			}
			// standing on its cell the walk never reaches it: the cell shows it underfoot
			Output own = player.Cell[sprite.CellY * W + sprite.CellX]!.Output;
			own.Connect(pool.Positions[geometry.UnderfootX].Input);
			own.Connect(new Input(sprite.ShowRelays[geometry.UnderfootSize].SelectMany(r => r.Input)));
		}

		// Hooking the walk's column relays to the screen. The colour panels and face overlays are kept when a redraw wants them again (KeptPiece); the
		// mask bars, shade and texture overlays are hidden at the reset (most of them change with the distance anyway).
		var kept = new Dictionary<object, KeptPiece>(ReferenceEqualityComparer.Instance);
		KeptPiece Kept(object key, Func<Input> show, Func<Input> hide)
		{
			if (!kept.TryGetValue(key, out KeptPiece? piece)) { kept[key] = piece = new KeptPiece(show(), hide()); relays.AddRange(piece.Entities); }
			return piece;
		}
		var facePieces = new Dictionary<(int c, int face), KeptPiece>();
		KeptPiece? FacePiece(int c, int face)
		{
			if (settings.FaceOpacity(face) <= 0) { return null; }
			if (!facePieces.TryGetValue((c, face), out KeptPiece? piece))
			{
				facePieces[(c, face)] = piece = new KeptPiece(new Input { new Target<Group>(GroupOf(faceOverlay[c]), new ChangeOpacity<Group>(Opacity: settings.FaceOpacity(face), Duration: 0)) }, faceOverlay[c].Reset);
				relays.AddRange(piece.Entities);
			}
			return piece;
		}
		for (int c = 0; c < columns; c++)
		{
			// the face overlay is one shape at an opacity per face: showing a face replaces the last one, cancelling its pending hide
			var faces = Enumerable.Range(0, 4).Select(face => FacePiece(c, face)).OfType<KeptPiece>().ToList();
			foreach (KeptPiece face in faces) { face.Replaces(faces.Where(other => !ReferenceEquals(other, face))); }
		}
		Input MaskInput(int c, int d) => new((masks[c, d]?.Input ?? new Input()).Concat(maskOuterOf[d] >= 0 ? maskOuter[c, maskOuterOf[d]]!.Input : new Input()));
		walk.ConnectScreen((c, d) =>
		{
			Input outputs = MaskInput(c, d);
			double shade = settings.ShadeOpacity(d + 1);
			if (shade > 0) { outputs = new Input(outputs.Concat(new Input { new Target<Group>(GroupOf(shadeOverlay[c]), new ChangeOpacity<Group>(Opacity: shade, Duration: 0)) })); }
			return outputs;
		}, (c, m) => hue[c, m] is Panel panel ? Kept(panel, () => panel.Input, () => panel.Reset).Want : null,
			(c, face) => FacePiece(c, face)?.Want,
			Enumerable.Range(0, columns).Select(c => textureOverlay is null ? null : GroupOf(textureOverlay[c])).ToArray());

		// Hide only what was shown: each piece the walk can show has a latch, armed when it is shown; the reset fires the latches (in blocks of about the
		// square root of a column's count), and only an armed one hides its piece. A kept piece's latch (Shown) arms its pending hide instead, which the walk
		// cancels by wanting it again and the commit after the walk carries out.
		Relay screenReset = NewRelay(), screenCommit = NewRelay();
		List<Relay> latchBlocks = new(), pendingBlocks = new();
		for (int c = 0; c < columns; c++)
		{
			List<Relay> latches = new();
			Relay Latch(Input hide)
			{
				Relay latch = NewRelay(true);
				latch.Output.Connect(hide);
				latch.Output.Connect(latch.Disable);
				latches.Add(latch);
				return latch;
			}
			List<KeptPiece> columnKept = new();
			Relay KeptLatch(KeptPiece piece)
			{
				if (!columnKept.Contains(piece)) { columnKept.Add(piece); latches.Add(piece.Shown); }
				return piece.Shown;
			}
			List<(Output source, Relay latch)> arms = new();
			Relay shadeLatch = Latch(shadeOverlay[c].Reset);
			Dictionary<int, Relay> outerLatches = new();
			for (int m = 0; m < materials; m++) { if (hue[c, m] is not null) { arms.Add((walk.HueColumn[c, m].Output, KeptLatch(kept[hue[c, m]!]))); } }
			for (int face = 0; face < 4; face++) { if (FacePiece(c, face) is KeptPiece facePiece) { arms.Add((walk.FaceColumn[c, face].Output, KeptLatch(facePiece))); } }
			if (textureOverlay is not null && walk.StripeColumn[c].Count > 0)
			{
				Relay textureLatch = Latch(textureOverlay[c].Reset);
				foreach ((_, Relay stripe) in walk.StripeColumn[c]) { arms.Add((stripe.Output, textureLatch)); }
			}
			for (int d = 0; d < depths; d++)
			{
				if (masks[c, d] is MaskBars bars) { arms.Add((walk.DepthColumn[c, d].Output, Latch(bars.Reset))); }
				if (maskOuterOf[d] >= 0)
				{
					if (!outerLatches.TryGetValue(maskOuterOf[d], out Relay? outerLatch)) { outerLatches[maskOuterOf[d]] = outerLatch = Latch(maskOuter[c, maskOuterOf[d]]!.Reset); }
					arms.Add((walk.DepthColumn[c, d].Output, outerLatch));
				}
				if (settings.ShadeOpacity(d + 1) > 0) { arms.Add((walk.DepthColumn[c, d].Output, shadeLatch)); }
			}
			int size = (int)Math.Ceiling(Math.Sqrt(latches.Count));
			Dictionary<Relay, Relay> blockOf = new(ReferenceEqualityComparer.Instance);
			for (int k = 0; k * size < latches.Count; k++)
			{
				Relay block = NewRelay(true);
				List<Relay> mine = latches.Skip(k * size).Take(size).ToList();
				block.Output.Connect(new Input(mine.SelectMany(latch => latch.Input)));
				block.Output.Connect(block.Disable);
				foreach (Relay latch in mine) { blockOf[latch] = block; }
				latchBlocks.Add(block);
			}
			int pendingSize = (int)Math.Ceiling(Math.Sqrt(columnKept.Count));
			for (int k = 0; k * pendingSize < columnKept.Count; k++)
			{
				Relay block = NewRelay(true);
				List<KeptPiece> mine = columnKept.Skip(k * pendingSize).Take(pendingSize).ToList();
				block.Output.Connect(new Input(mine.SelectMany(piece => piece.Pending.Input)));
				block.Output.Connect(block.Disable);
				foreach (KeptPiece piece in mine) { piece.Shown.Output.Connect(block.Enable); }
				pendingBlocks.Add(block);
			}
			foreach (var bySource in arms.GroupBy(arm => arm.source, ReferenceEqualityComparer.Instance as IEqualityComparer<Output>))
			{
				List<Relay> armed = bySource.Select(arm => arm.latch).Distinct(ReferenceEqualityComparer.Instance as IEqualityComparer<Relay>).ToList();
				bySource.Key!.Connect(new Input(armed.SelectMany(latch => latch.Enable)
					.Concat(armed.Select(latch => blockOf[latch]).Distinct(ReferenceEqualityComparer.Instance as IEqualityComparer<Relay>).SelectMany(block => block.Enable))));
			}
		}
		screenReset.Output.Connect(new Input(latchBlocks.SelectMany(block => block.Input)));
		screenCommit.Output.Connect(new Input(pendingBlocks.SelectMany(block => block.Input)));
		walk.FinishColumns();
		if (pools.Count > 0)
		{
			// clear the wanted latches and arm the hides of what is showing, arm every pool's expiry, and start the delay before newly wanted objects show
			screenReset.Output.Connect(new Input(pools.SelectMany(p => p.Live.Input).Concat(pools.SelectMany(p => p.Settle.Disable)).Concat(pools.SelectMany(p => p.Expire.Enable))));
			screenReset.Output.Connect(settle.Input);
		}

		// A redraw: reset the screen, fire the player's cell (OnCell), walk the rays, hide the kept pieces nobody wanted, then hide the sprite objects that
		// weren't wanted again and expire the pools nothing claimed
		redraw.Output.Connect(new Input(screenReset.Input.Concat(player.Broadcast.Input).Concat(walk.March.Input).Concat(screenCommit.Input)
			.Concat(pools.SelectMany(p => p.Commit.Input)).Concat(pools.SelectMany(p => p.Expire.Input)).Concat(pools.SelectMany(p => p.ClaimActive.Input))));
		turnRight.Output.Connect(new Input(turnRightRaw.Input.Concat(redraw.Input)));
		turnLeft.Output.Connect(new Input(turnLeftRaw.Input.Concat(redraw.Input)));
		// the facing ring starts at 0: turn to the start facing (the shorter way round), then draw
		int turns = startFacing <= facings / 2 ? startFacing : facings - startFacing;
		Relay turnRaw = startFacing <= facings / 2 ? turnRightRaw : turnLeftRaw;
		start.Output.Connect(new Input(Enumerable.Range(0, turns).SelectMany(_ => turnRaw.Input).Concat(redraw.Input)));

		relays.AddRange(walk.Entities);
		Entities = relays.Concat(screen).ToList();
	}

	/// <summary>The screen's foreground groups and shapes, without the triggers and joints (for counting what a frame draws).</summary>
	public IReadOnlyList<Entity> ScreenEntities => screen;
}
