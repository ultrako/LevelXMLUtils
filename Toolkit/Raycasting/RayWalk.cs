using LevelXML;
namespace LevelXMLUtils.Raycasting;

/// <summary>
/// The walls (and sprites) worked out while the level runs. A ray from a cell's middle visits the same cells, relative to where it starts, wherever the
/// player stands; only whether those cells are walls depends on the position. So the rays are baked as relative paths and the walls looked up at run
/// time:
/// <list type="bullet">
/// <item>Every ray of one facing goes into one tree of relative cells (neighbouring columns share their first cells). A node holds the rays that reach it,
/// with the distance step each would show if this cell were its wall, and the face it would hit.</item>
/// <item>Facing f plus a quarter turn sees exactly facing f's rays turned a quarter, so there are Facings / 4 trees. Moves and faces go through a router
/// of the current quarter.</item>
/// <item>A token holds a position (which relays are enabled for its column and row). Each redraw it starts on the player's cell and the current facing's
/// tree is walked depth first: a node moves the token one cell and checks for a wall there; on a hit it shows its rays' columns (distance, face shade,
/// material, texture stripe) and skips its subtree, otherwise it walks its children and moves the token back. A node is only reached through open
/// cells, so its hit is the first hit of every ray through it.</item>
/// </list>
/// Most of the structure is there to keep the number of trigger actions a redraw costs small: searches go through blocks of about the square root of
/// what they search, and a wall of the same material as the last one sets nothing.
/// </summary>
public class RayWalk
{
	sealed class Node
	{
		public int X, Y, Dir, Face, Level;
		public List<(int column, int depth, int stripe)> Rays = new();
		public Dictionary<(int x, int y), Node> Children = new();
		public Relay Hit = null!, Continue = null!, Repair = null!;
		public IEnumerable<Target> Visit = null!;
		public List<(int kind, int x, int size)> SpriteViews = new();
		public Relay? SpriteHit;
	}

	/// <summary>A sprite standing in a cell, and where it is drawn (screen position, size, and the column whose ray decides whether it is seen, or -1 for
	/// any ray) when it is (ox, oy) cells from the player facing f, ignoring walls; null when it is not drawn from there.</summary>
	public record SpriteSpec(int CellX, int CellY, Func<int, int, int, (int x, int size, int anchor)?> View);

	static readonly (int x, int y)[] Step = { (0, -1), (1, 0), (0, 1), (-1, 0) }; // N E S W, also the face order
	static (int x, int y) Rotate((int x, int y) v, int quarters) { for (int i = 0; i < quarters; i++) { v = (-v.y, v.x); } return v; }

	public readonly List<Entity> Entities = new();
	/// <summary>Walks the current facing's tree (the token must be on the player's cell, where it always is between redraws).</summary>
	public readonly Relay March;
	public int NodeCount { get; private set; }
	/// <summary>Per (column, distance step - 1), (column, material) and (column, face): fired for a column that shows that (wired to the screen by
	/// <see cref="ConnectScreen"/>).</summary>
	public readonly Relay[,] DepthColumn, HueColumn, FaceColumn;
	/// <summary>Per column, the texture overlay opacities the column can show and the relay that sets each.</summary>
	public readonly List<(double opacity, Relay relay)>[] StripeColumn;
	/// <summary>Per sprite (in the order given): a relay per screen position and per size it can be shown at, fired when the walk finds it there.</summary>
	public readonly List<Dictionary<int, Relay>> SpriteX = new(), SpriteSize = new();
	/// <summary>The token's moves north, east, south and west (the player's moves fire them too, so the token follows the player).</summary>
	public readonly Relay[] Move;
	/// <summary>For looking at cells near the player outside the walk: AbsMove[d] moves the token one cell in direction d (N E S W) turned by the current
	/// quarter, like a node's move, and <see cref="Check"/> looks at its cell. <see cref="WallProbe"/> fires for a wall there (it starts disabled:
	/// whoever asks enables it while asking), and <see cref="SpriteCell"/>[i] for sprite i's cell.</summary>
	public readonly Relay[] AbsMove;
	public readonly Relay Check;
	public readonly Relay WallProbe;
	public readonly Relay[] SpriteCell;

	readonly RaycastSettings settings;
	readonly Relay[] select, deselect;
	readonly Dictionary<(int c, int s), Relay> selectStripe = new();
	readonly double[][] textures;
	readonly int[] textureOf; // material -> index in textures, or -1
	readonly Dictionary<(int c, int t, double opacity), Relay> stripeRelay = new();

	internal Relay NewRelay(bool disabled = false) { Relay r = new(startDisabled: disabled); Entities.AddRange(r.Entities); return r; }

	// One axis of the token searched in three levels: per direction, a relay per block and per superblock, enabled while the token is in it, each firing its
	// children in the direction's passing order.
	class Level3
	{
		readonly int n, s1, s2, blocks, supers, dirs;
		readonly List<Relay[]> b1 = new(), b2 = new();
		readonly bool[] descendingOf = new bool[3];
		public Level3(int n, int s1, int s2, int dirs, RayWalk walk)
		{
			this.n = n; this.s1 = s1; this.s2 = s2; this.dirs = dirs;
			blocks = (n + s1 - 1) / s1; supers = (blocks + s2 - 1) / s2;
			for (int d = 0; d < dirs; d++)
			{
				b1.Add(Enumerable.Range(0, blocks).Select(_ => walk.NewRelay(true)).ToArray());
				b2.Add(Enumerable.Range(0, supers).Select(_ => walk.NewRelay(true)).ToArray());
			}
		}
		static IEnumerable<int> Order(int from, int count, bool descending) { var r = Enumerable.Range(from, count); return descending ? r.Reverse() : r; }
		public void Wire(int d, Func<int, Input> cell, bool descending)
		{
			for (int k = 0; k < blocks; k++) { b1[d][k].Output.Connect(new Input(Order(k * s1, Math.Min(s1, n - k * s1), descending).SelectMany(cell))); }
			for (int k = 0; k < supers; k++) { b2[d][k].Output.Connect(new Input(Order(k * s2, Math.Min(s2, blocks - k * s2), descending).SelectMany(b => b1[d][b].Input))); }
			descendingOf[d] = descending;
		}
		public Input Top(int d) => new(Order(0, supers, descendingOf[d]).SelectMany(k => b2[d][k].Input));
		public IEnumerable<Target> Cross(int from, int to)
		{
			IEnumerable<Target> Switch(List<Relay[]> level, int a, int b) => a == b ? Enumerable.Empty<Target>()
				: Enumerable.Range(0, dirs).SelectMany(d => level[d][a].Disable).Concat(Enumerable.Range(0, dirs).SelectMany(d => level[d][b].Enable));
			return Switch(b1, from / s1, to / s1).Concat(Switch(b2, from / s1 / s2, to / s1 / s2));
		}
		public void Start(int at) { for (int d = 0; d < dirs; d++) { b1[d][at / s1].StartDisabled = false; b2[d][at / s1 / s2].StartDisabled = false; } }
	}

	// A hit's column and the texture stripe its ray lands on, armed by one relay per (column, stripe)
	Relay SelectStripe(int c, int s)
	{
		if (!selectStripe.TryGetValue((c, s), out Relay? r))
		{
			selectStripe[(c, s)] = r = NewRelay();
			IEnumerable<Target> stripes = Enumerable.Range(0, textures.Length).Select(t => StripeOpacity(t, s)).Select((o, t) => (o, t)).Where(p => p.o > 0)
				.SelectMany(p => stripeRelay[(c, p.t, p.o)].Enable);
			r.Output.Connect(new Input(select[c].Input.Concat(stripes)));
		}
		return r;
	}
	double StripeOpacity(int t, int s) => Math.Round((1 - textures[t][s]) * 100, 1);

	/// <param name="gateForFacings">Registers a relay as enabled only while the player faces one of the given facings (the raycaster's facing groups).</param>
	public RayWalk(RaycastSettings settings, RaycastMap map, int startX, int startY, Action<Relay, IEnumerable<int>> gateForFacings, IReadOnlyList<SpriteSpec>? sprites = null)
	{
		this.settings = settings;
		sprites ??= Array.Empty<SpriteSpec>();
		int W = map.Width, H = map.Height, columns = settings.Columns, depths = settings.Depths, materials = map.Materials.Count;
		int quarters = settings.Quarters, classes = settings.FacingsPerQuarter;
		March = NewRelay(); WallProbe = NewRelay(true);

		// The textures: one per distinct stripe pattern among the materials
		var textureList = new List<double[]>();
		textureOf = new int[materials];
		for (int m = 0; m < materials; m++)
		{
			double[]? texture = map.Materials[m].Texture;
			if (texture is null) { textureOf[m] = -1; continue; }
			if (texture.Length != settings.TextureStripes) { throw new ArgumentException($"material {m}'s texture has {texture.Length} stripes, not {settings.TextureStripes}"); }
			int t = textureList.FindIndex(other => other.SequenceEqual(texture));
			if (t < 0) { t = textureList.Count; textureList.Add(texture); }
			textureOf[m] = t;
		}
		textures = textureList.ToArray();

		// The trees, and a check that each turned facing really gives the same cells and steps as its own rays
		var roots = new Node[classes];
		for (int t = 0; t < classes; t++)
		{
			roots[t] = new Node();
			for (int c = 0; c < columns; c++)
			{
				var path = settings.RayPath(t, c);
				for (int q = 1; q < quarters; q++)
				{
					var turned = settings.RayPath(t + classes * q, c);
					var expected = path.Select(p => { var v = Rotate((p.x, p.y), q); return (v.x, v.y, (p.face + q) % 4, p.depth, p.stripe); }).ToList();
					if (!turned.SequenceEqual(expected)) { throw new InvalidOperationException($"facing {t + classes * q} column {c} is not facing {t} turned {q} quarters"); }
				}
				Node node = roots[t]; (int x, int y) at = (0, 0);
				foreach (var (x, y, face, depth, stripe) in path)
				{
					int dir = Array.IndexOf(Step, (x - at.x, y - at.y));
					if (!node.Children.TryGetValue((x, y), out Node? child)) { node.Children[(x, y)] = child = new Node { X = x, Y = y, Dir = dir, Face = face, Level = node.Level + 1 }; }
					child.Rays.Add((c, depth, stripe));
					node = child; at = (x, y);
				}
			}
		}
		static int Depth(Node n) => n.Children.Count == 0 ? n.Level : n.Children.Values.Max(Depth);
		int levels = roots.Max(r => Depth(r));

		// The token: E[x] and Wt[x] are enabled while it is in column x, S[y] and N[y] while it is in row y, Row[y] likewise (the check's first level), and
		// a wall cell's relay while the token is in its column. A move fires the token's relays of that direction in the order that passes each before it can
		// be enabled, so it moves exactly once.
		var E = Enumerable.Range(0, W).Select(_ => NewRelay(true)).ToArray();
		var Wt = Enumerable.Range(0, W).Select(_ => NewRelay(true)).ToArray();
		var S = Enumerable.Range(0, H).Select(_ => NewRelay(true)).ToArray();
		var N = Enumerable.Range(0, H).Select(_ => NewRelay(true)).ToArray();
		var Row = Enumerable.Range(0, H).Select(_ => NewRelay(true)).ToArray();
		var wall = new Dictionary<(int x, int y), Relay>();
		// only walls beside open floor: a ray stops at the first wall it steps into, which it entered from an open neighbour
		for (int x = 0; x < W; x++) { for (int y = 0; y < H; y++) { if (map.WallAt(x, y) && map.TouchesFloor(x, y)) { wall[(x, y)] = NewRelay(true); } } }
		var spriteAt = new Dictionary<(int x, int y), Relay>();
		for (int i = 0; i < sprites.Count; i++)
		{
			(int x, int y) cell = (sprites[i].CellX, sprites[i].CellY);
			if (map.WallAt(cell.x, cell.y) || spriteAt.ContainsKey(cell)) { throw new InvalidOperationException($"sprite {i} at {cell} is in a wall or shares its cell with another sprite"); }
			spriteAt[cell] = NewRelay(true);
		}
		IEnumerable<Target> WallColumn(int x, bool enable) => Enumerable.Range(0, H).SelectMany(y => new[] { wall.GetValueOrDefault((x, y)), spriteAt.GetValueOrDefault((x, y)) })
			.OfType<Relay>().SelectMany(r => enable ? r.Enable : r.Disable);
		// columns and rows searched in three levels (superblocks, blocks, cells), each move switching the blocks it crosses into
		Level3 X = new(W, 5, 5, 2, this), Y = new(H, 4, 4, 3, this);
		for (int x = 0; x < W; x++)
		{
			if (x + 1 < W) { E[x].Output.Connect(new Input(E[x].Disable.Concat(Wt[x].Disable).Concat(E[x + 1].Enable).Concat(Wt[x + 1].Enable).Concat(WallColumn(x, false)).Concat(WallColumn(x + 1, true)).Concat(X.Cross(x, x + 1)))); }
			if (x > 0) { Wt[x].Output.Connect(new Input(Wt[x].Disable.Concat(E[x].Disable).Concat(Wt[x - 1].Enable).Concat(E[x - 1].Enable).Concat(WallColumn(x, false)).Concat(WallColumn(x - 1, true)).Concat(X.Cross(x, x - 1)))); }
		}
		for (int y = 0; y < H; y++)
		{
			if (y + 1 < H) { S[y].Output.Connect(new Input(S[y].Disable.Concat(N[y].Disable).Concat(Row[y].Disable).Concat(S[y + 1].Enable).Concat(N[y + 1].Enable).Concat(Row[y + 1].Enable).Concat(Y.Cross(y, y + 1)))); }
			if (y > 0) { N[y].Output.Connect(new Input(N[y].Disable.Concat(S[y].Disable).Concat(Row[y].Disable).Concat(N[y - 1].Enable).Concat(S[y - 1].Enable).Concat(Row[y - 1].Enable).Concat(Y.Cross(y, y - 1)))); }
		}
		X.Wire(0, x => x + 1 < W ? E[x].Input : new Input(), descending: true); X.Wire(1, x => x > 0 ? Wt[x].Input : new Input(), descending: false);
		Y.Wire(0, y => y + 1 < H ? S[y].Input : new Input(), descending: true); Y.Wire(1, y => y > 0 ? N[y].Input : new Input(), descending: false);
		Y.Wire(2, y => Row[y].Input, descending: false);
		Input[] moveTop = { Y.Top(1), X.Top(0), Y.Top(0), X.Top(1) };
		Move = new Relay[4];
		for (int d = 0; d < 4; d++) { Move[d] = NewRelay(); Move[d].Output.Connect(moveTop[d]); }
		// the token starts on the player's cell and follows every step; the walk and the collision checks always bring it back
		foreach (Relay r in new[] { E[startX], Wt[startX], S[startY], N[startY], Row[startY] }) { r.StartDisabled = false; }
		X.Start(startX); Y.Start(startY);
		for (int y = 0; y < H; y++)
		{
			if (wall.TryGetValue((startX, y), out Relay? w)) { w.StartDisabled = false; }
			if (spriteAt.TryGetValue((startX, y), out Relay? s)) { s.StartDisabled = false; }
		}

		// Materials: a wall that is hit sets, for each of its faces, which material that face shows (materialOf[face, m] enabled for exactly that m); the hit
		// node then fires its face (turned by the quarter) and the material relays send it to its columns. A setter turns itself off once its material is
		// current, so a wall of the same material as the last costs one action per face.
		var materialOf = new Relay[4, materials];
		var setMaterial = new Relay[4, materials];
		for (int face = 0; face < 4; face++)
		{
			for (int m = 0; m < materials; m++) { materialOf[face, m] = NewRelay(m != 0); setMaterial[face, m] = NewRelay(m == 0); }
			for (int m = 0; m < materials; m++)
			{
				int face1 = face, m1 = m;
				setMaterial[face, m].Output.Connect(new Input(Enumerable.Range(0, materials).SelectMany(k => k == m1 ? materialOf[face1, k].Enable : materialOf[face1, k].Disable)
					.Concat(Enumerable.Range(0, materials).SelectMany(k => k == m1 ? setMaterial[face1, k].Disable : setMaterial[face1, k].Enable))));
			}
		}
		// A hit goes to the armed node of the current level (level[L] is enabled while the walk is at level L), through groups of levels
		Relay hitRouter = NewRelay();
		var level = Enumerable.Range(0, levels + 1).Select(_ => NewRelay(true)).ToArray();
		int levelGroupSize = (int)Math.Ceiling(Math.Sqrt(levels));
		int GroupOf(int l) => (l - 1) / levelGroupSize;
		var levelGroup = Enumerable.Range(0, GroupOf(levels) + 1).Select(_ => NewRelay(true)).ToArray();
		for (int k = 0; k < levelGroup.Length; k++) { int k1 = k; levelGroup[k].Output.Connect(new Input(Enumerable.Range(1, levels).Where(l => GroupOf(l) == k1).SelectMany(l => level[l].Input))); }
		hitRouter.Output.Connect(new Input(levelGroup.SelectMany(g => g.Input)));
		var enter = new Relay[levels + 1]; var exit = new Relay[levels + 1];
		for (int l = 1; l <= levels; l++)
		{
			enter[l] = NewRelay(); exit[l] = NewRelay();
			IEnumerable<Target> into = l == 1 ? levelGroup[0].Enable : GroupOf(l) != GroupOf(l - 1) ? levelGroup[GroupOf(l - 1)].Disable.Concat(levelGroup[GroupOf(l)].Enable) : Enumerable.Empty<Target>();
			IEnumerable<Target> back = l == 1 ? levelGroup[0].Disable : GroupOf(l) != GroupOf(l - 1) ? levelGroup[GroupOf(l)].Disable.Concat(levelGroup[GroupOf(l - 1)].Enable) : Enumerable.Empty<Target>();
			enter[l].Output.Connect(new Input((l > 1 ? level[l - 1].Disable : Enumerable.Empty<Target>()).Concat(level[l].Enable).Concat(into)));
			exit[l].Output.Connect(new Input(level[l].Disable.Concat(l > 1 ? level[l - 1].Enable : Enumerable.Empty<Target>()).Concat(back)));
		}
		Check = NewRelay();
		Check.Output.Connect(Y.Top(2));
		for (int y = 0; y < H; y++) { int y1 = y; Row[y].Output.Connect(new Input(Enumerable.Range(0, W).SelectMany(x => new[] { wall.GetValueOrDefault((x, y1)), spriteAt.GetValueOrDefault((x, y1)) }).OfType<Relay>().SelectMany(r => r.Input))); }
		foreach (((int x, int y), Relay relay) in wall)
		{
			relay.Output.Connect(new Input(Enumerable.Range(0, 4).SelectMany(face => setMaterial[face, map.MaterialOf(x, y, face)].Input)));
			relay.Output.Connect(WallProbe.Input);
			relay.Output.Connect(hitRouter.Input);
		}

		// The quarter-turn routers: a move or face as the tree sees it, turned by the current quarter
		AbsMove = new Relay[4]; var absFace = new Relay[4]; var fireFace = new Relay[4];
		for (int d = 0; d < 4; d++) { AbsMove[d] = NewRelay(); absFace[d] = NewRelay(); fireFace[d] = NewRelay(); }
		SpriteCell = sprites.Select(sp => spriteAt[(sp.CellX, sp.CellY)]).ToArray();
		for (int q = 0; q < quarters; q++)
		{
			var facings = Enumerable.Range(classes * q, classes).ToList();
			for (int d = 0; d < 4; d++)
			{
				Relay moveTurned = NewRelay(q != 0), faceTurned = NewRelay(q != 0);
				gateForFacings(moveTurned, facings); gateForFacings(faceTurned, facings);
				moveTurned.Output.Connect(moveTop[(d + q) % 4]);
				faceTurned.Output.Connect(fireFace[(d + q) % 4].Input);
				AbsMove[d].Output.Connect(moveTurned.Input);
				absFace[d].Output.Connect(faceTurned.Input);
			}
		}

		// Per column: the material panels and face shades are reached through relays enabled only while the hit node selects that column, in blocks of
		// about the square root of the column count
		DepthColumn = new Relay[columns, depths]; HueColumn = new Relay[columns, materials]; FaceColumn = new Relay[columns, 4];
		StripeColumn = new List<(double, Relay)>[columns];
		select = new Relay[columns]; deselect = new Relay[columns];
		int colBlockSize = (int)Math.Ceiling(Math.Sqrt(columns)), colBlocks = (columns + colBlockSize - 1) / colBlockSize;
		var hueBlock = new Relay[colBlocks, materials]; var faceBlock = new Relay[colBlocks, 4];
		var selectBlock = new Relay[colBlocks]; var deselectBlock = new Relay[colBlocks];
		for (int b = 0; b < colBlocks; b++)
		{
			for (int m = 0; m < materials; m++) { hueBlock[b, m] = NewRelay(true); }
			for (int face = 0; face < 4; face++) { faceBlock[b, face] = NewRelay(true); }
			int b1 = b;
			var gated = Enumerable.Range(0, materials).Select(m => hueBlock[b1, m]).Concat(Enumerable.Range(0, 4).Select(f => faceBlock[b1, f])).ToList();
			// each selected column selects its block; the block's select then turns itself off until the block is deselected
			selectBlock[b] = NewRelay(); deselectBlock[b] = NewRelay();
			selectBlock[b].Output.Connect(new Input(gated.SelectMany(r => r.Enable).Concat(selectBlock[b].Disable)));
			deselectBlock[b].Output.Connect(new Input(gated.SelectMany(r => r.Disable).Concat(selectBlock[b].Enable)));
		}
		for (int c = 0; c < columns; c++)
		{
			for (int d = 0; d < depths; d++) { DepthColumn[c, d] = NewRelay(); }
			for (int m = 0; m < materials; m++) { HueColumn[c, m] = NewRelay(true); }
			for (int face = 0; face < 4; face++) { FaceColumn[c, face] = NewRelay(true); }
			select[c] = NewRelay(); deselect[c] = NewRelay();
			int c1 = c;
			var gated = Enumerable.Range(0, materials).Select(m => HueColumn[c1, m]).Concat(Enumerable.Range(0, 4).Select(f => FaceColumn[c1, f])).ToList();
			select[c].Output.Connect(new Input(gated.SelectMany(r => r.Enable).Concat(selectBlock[c / colBlockSize].Input)));
			// A texture's stripes: one relay per (column, texture, overlay opacity), armed by the hit for the stripe its ray lands on, fired by the column's
			// material relay when the material has that texture, and disarmed with the rest of the column
			StripeColumn[c] = new();
			for (int t = 0; t < textures.Length; t++)
			{
				foreach (double opacity in Enumerable.Range(0, settings.TextureStripes).Select(s => StripeOpacity(t, s)).Where(o => o > 0).Distinct())
				{
					Relay r = NewRelay(true);
					stripeRelay[(c, t, opacity)] = r;
					StripeColumn[c].Add((opacity, r));
				}
			}
			deselect[c].Output.Connect(new Input(gated.SelectMany(r => r.Disable).Concat(StripeColumn[c].SelectMany(s => s.relay.Disable))));
			for (int m = 0; m < materials; m++)
			{
				int t = textureOf[m];
				if (t >= 0) { HueColumn[c, m].Output.Connect(new Input(stripeRelay.Where(kv => kv.Key.c == c1 && kv.Key.t == t).SelectMany(kv => kv.Value.Input))); }
			}
		}
		for (int b = 0; b < colBlocks; b++)
		{
			var mine = Enumerable.Range(b * colBlockSize, Math.Min(colBlockSize, columns - b * colBlockSize)).ToList();
			for (int m = 0; m < materials; m++) { int m1 = m; hueBlock[b, m].Output.Connect(new Input(mine.SelectMany(c => HueColumn[c, m1].Input))); }
			for (int face = 0; face < 4; face++) { int f1 = face; faceBlock[b, face].Output.Connect(new Input(mine.SelectMany(c => FaceColumn[c, f1].Input))); }
		}
		for (int face = 0; face < 4; face++)
		{
			for (int m = 0; m < materials; m++) { int m1 = m; materialOf[face, m].Output.Connect(new Input(Enumerable.Range(0, colBlocks).SelectMany(b => hueBlock[b, m1].Input))); }
			int face1 = face;
			// the face goes to a selected block's columns after the materials, so the block is done after it
			for (int b = 0; b < colBlocks; b++) { faceBlock[b, face].Output.Connect(deselectBlock[b].Input); }
			fireFace[face].Output.Connect(new Input(Enumerable.Range(0, materials).SelectMany(m => materialOf[face1, m].Input).Concat(Enumerable.Range(0, colBlocks).SelectMany(b => faceBlock[b, face1].Input))));
		}

		// Sprites. A node shows a sprite found in its cell at the screen position and size the sprite has from there, which depend only on the node's offset
		// and facing; sprites drawn the same from every offset are one kind. A node counts for a kind when its rays include the column under the sprite's
		// floor point (or, when that is off the screen, always). When the check finds a sprite it arms that sprite, goes to the armed node like a hit does,
		// and the node fires the kind's position and size relays, which reach only the armed sprite's.
		var allNodes = new List<(Node n, int t)>();
		void Collect(Node n, int t) { allNodes.Add((n, t)); foreach (Node child in n.Children.Values) { Collect(child, t); } }
		for (int t = 0; t < classes; t++) { foreach (Node child in roots[t].Children.Values) { Collect(child, t); } }
		var kindViews = new List<List<(int x, int size, int anchor)?>>();
		int KindOf(List<(int x, int size, int anchor)?> views)
		{
			int k = kindViews.FindIndex(v => v.SequenceEqual(views));
			if (k < 0) { k = kindViews.Count; kindViews.Add(views); }
			return k;
		}
		var kindOf = sprites.Select(sp => KindOf(allNodes.Select(nt => sp.View(nt.n.X, nt.n.Y, nt.t)).ToList())).ToArray();
		for (int a = 0; a < allNodes.Count; a++)
		{
			Node n = allNodes[a].n;
			for (int k = 0; k < kindViews.Count; k++)
			{
				if (kindViews[k][a] is (int x, int size, int anchor) && (anchor < 0 || n.Rays.Any(r => r.column == anchor))) { n.SpriteViews.Add((k, x, size)); }
			}
		}
		var seenX = new Dictionary<(int kind, int x), Relay>(); var seenSize = new Dictionary<(int kind, int size), Relay>();
		var who = new Relay[sprites.Count]; var whoOff = new Relay[sprites.Count];
		Relay dispatchOn = NewRelay(), dispatchOff = NewRelay(), spriteRouter = NewRelay();
		for (int i = 0; i < sprites.Count; i++)
		{
			int mine = kindOf[i];
			var views = allNodes.SelectMany(nt => nt.n.SpriteViews).Where(v => v.kind == mine).ToList();
			SpriteX.Add(views.Select(v => v.x).Distinct().OrderBy(x => x).ToDictionary(x => x, _ => NewRelay(true)));
			SpriteSize.Add(views.Select(v => v.size).Distinct().OrderBy(z => z).ToDictionary(z => z, _ => NewRelay(true)));
			foreach (int x in SpriteX[i].Keys)
			{
				if (!seenX.ContainsKey((mine, x))) { seenX[(mine, x)] = NewRelay(); }
				seenX[(mine, x)].Output.Connect(SpriteX[i][x].Input);
			}
			foreach (int size in SpriteSize[i].Keys)
			{
				if (!seenSize.ContainsKey((mine, size))) { seenSize[(mine, size)] = NewRelay(); }
				seenSize[(mine, size)].Output.Connect(SpriteSize[i][size].Input);
			}
			var gates = SpriteX[i].Values.Concat(SpriteSize[i].Values).ToList();
			who[i] = NewRelay(true); whoOff[i] = NewRelay(true);
			who[i].Output.Connect(new Input(gates.SelectMany(g => g.Enable)));
			whoOff[i].Output.Connect(new Input(gates.SelectMany(g => g.Disable)));
			Relay arm = NewRelay(), disarm = NewRelay();
			arm.Output.Connect(new Input(who[i].Enable.Concat(whoOff[i].Enable)));
			disarm.Output.Connect(new Input(who[i].Disable.Concat(whoOff[i].Disable)));
			// the found sprite's position and size relays are open only while the armed node fires its views
			spriteAt[(sprites[i].CellX, sprites[i].CellY)].Output.Connect(new Input(arm.Input.Concat(dispatchOn.Input).Concat(spriteRouter.Input).Concat(dispatchOff.Input).Concat(disarm.Input)));
		}
		dispatchOn.Output.Connect(new Input(who.SelectMany(r => r.Input)));
		dispatchOff.Output.Connect(new Input(whoOff.SelectMany(r => r.Input)));
		var spriteLevel = Enumerable.Range(0, levels + 1).Select(_ => NewRelay(true)).ToArray();
		for (int l = 1; l <= levels; l++)
		{
			enter[l].Output.Connect(new Input((l > 1 ? spriteLevel[l - 1].Disable : Enumerable.Empty<Target>()).Concat(spriteLevel[l].Enable)));
			exit[l].Output.Connect(new Input(spriteLevel[l].Disable.Concat(l > 1 ? spriteLevel[l - 1].Enable : Enumerable.Empty<Target>())));
		}
		var spriteBlocks = Enumerable.Range(0, levels + 1).Select(_ => new List<(Relay block, List<Relay> hits)>()).ToArray();
		var spriteBlockOf = new Dictionary<Relay, (Relay block, List<Relay> hits)>(ReferenceEqualityComparer.Instance as IEqualityComparer<Relay>);

		// The nodes, in blocks per level and per direction of the step into them (about the square root of their count): the node being visited enables its
		// block (through the block's shared enter and exit relays, which also take the step there and back, switch the level and run the check), and a hit
		// fires the level's blocks, of which only the live one fires its nodes.
		var perKey = new Dictionary<(int l, int dir), int>();
		void Count(Node n) { perKey[(n.Level, n.Dir)] = perKey.GetValueOrDefault((n.Level, n.Dir)) + 1; foreach (Node child in n.Children.Values) { Count(child); } }
		foreach (Node root in roots) { foreach (Node child in root.Children.Values) { Count(child); } }
		var blockSize = perKey.ToDictionary(kv => kv.Key, kv => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(kv.Value))));
		var blocks = Enumerable.Range(0, levels + 1).Select(l => new List<(Relay block, Relay enter, Relay exit, List<Relay> hits)>()).ToArray();
		var blocksOf = new Dictionary<(int l, int dir), List<int>>();
		var placed = new Dictionary<(int l, int dir), int>();
		var repairOf = new Dictionary<Relay, Relay>(ReferenceEqualityComparer.Instance as IEqualityComparer<Relay>);
		void Build(Node n)
		{
			n.Hit = NewRelay(true); n.Continue = NewRelay();
			NodeCount++;
			int l = n.Level;
			var key = (l, n.Dir);
			int k = placed.GetValueOrDefault(key) / blockSize[key];
			placed[key] = placed.GetValueOrDefault(key) + 1;
			if (!blocksOf.TryGetValue(key, out var mineList)) { blocksOf[key] = mineList = new List<int>(); }
			if (k == mineList.Count)
			{
				Relay block = NewRelay(true), enterBlock = NewRelay(), exitBlock = NewRelay();
				enterBlock.Output.Connect(new Input(AbsMove[n.Dir].Input.Concat(block.Enable)));
				exitBlock.Output.Connect(new Input(AbsMove[(n.Dir + 2) % 4].Input.Concat(block.Disable)));
				mineList.Add(blocks[l].Count);
				blocks[l].Add((block, enterBlock, exitBlock, new List<Relay>()));
			}
			var mine = blocks[l][mineList[k]];
			// A hit disables the node's continue (skipping its subtree), and the block's repair, armed by the hit and fired by the block's exit, enables
			// every continue of the block again, so the parent can list the visit's steps itself
			if (!repairOf.TryGetValue(mine.enter, out Relay? repair)) { repairOf[mine.enter] = repair = NewRelay(true); repair.Output.Connect(repair.Disable); }
			repair.Output.Connect(n.Continue.Enable);
			n.Repair = repair;
			mine.hits.Add(n.Hit);
			IEnumerable<Target> spriteArm = Enumerable.Empty<Target>();
			if (n.SpriteViews.Count > 0)
			{
				n.SpriteHit = NewRelay(true);
				if (!spriteBlockOf.TryGetValue(mine.enter, out var spriteBlock))
				{
					spriteBlockOf[mine.enter] = spriteBlock = (NewRelay(true), new List<Relay>());
					mine.enter.Output.Connect(spriteBlock.block.Enable);
					mine.exit.Output.Connect(spriteBlock.block.Disable);
					spriteBlocks[l].Add(spriteBlock);
				}
				spriteBlock.hits.Add(n.SpriteHit);
				spriteArm = n.SpriteHit.Enable;
				mine.exit.Output.Connect(n.SpriteHit.Disable);
				n.SpriteHit.Output.Connect(new Input(n.SpriteViews.SelectMany(v => seenX[(v.kind, v.x)].Input.Concat(seenSize[(v.kind, v.size)].Input)).Concat(n.SpriteHit.Disable)));
			}
			foreach (Node child in n.Children.Values) { Build(child); }
			n.Visit = n.Hit.Enable.Concat(spriteArm).Concat(mine.enter.Input).Concat(n.Continue.Input).Concat(mine.exit.Input).ToList();
			// select (with the stripe) each column, send the face and material to the selected columns, then the depths
			n.Hit.Output.Connect(new Input(n.Rays.SelectMany(r => SelectStripe(r.column, r.stripe).Input).Concat(absFace[n.Face].Input)
				.Concat(n.Rays.SelectMany(r => DepthColumn[r.column, r.depth - 1].Input)).Concat(n.Continue.Disable).Concat(n.Repair.Enable).Concat(n.Hit.Disable)));
			n.Continue.Output.Connect(new Input(n.Hit.Disable.Concat(n.Children.Values.SelectMany(child => child.Visit))));
		}
		var rootRelays = new Relay[classes];
		for (int t = 0; t < classes; t++)
		{
			foreach (Node child in roots[t].Children.Values) { Build(child); }
			rootRelays[t] = NewRelay(t != 0);
			gateForFacings(rootRelays[t], Enumerable.Range(0, quarters).Select(q => t + classes * q));
			rootRelays[t].Output.Connect(new Input(roots[t].Children.Values.SelectMany(child => child.Visit)));
		}
		for (int l = 1; l <= levels; l++)
		{
			foreach (var b in blocks[l]) { b.enter.Output.Connect(new Input(enter[l].Input.Concat(Check.Input))); b.exit.Output.Connect(new Input(exit[l].Input.Concat(repairOf[b.enter].Input))); }
			level[l].Output.Connect(new Input(blocks[l].SelectMany(b => b.block.Input)));
			foreach (var b in blocks[l]) { b.block.Output.Connect(new Input(b.hits.SelectMany(h => h.Input))); }
		}
		for (int l = 1; l <= levels; l++)
		{
			if (spriteBlocks[l].Count == 0) { continue; }
			spriteLevel[l].Output.Connect(new Input(spriteBlocks[l].SelectMany(b => b.block.Input)));
			foreach (var b in spriteBlocks[l]) { b.block.Output.Connect(new Input(b.hits.SelectMany(h => h.Input))); }
			spriteRouter.Output.Connect(spriteLevel[l].Input);
		}
		March.Output.Connect(new Input(rootRelays.SelectMany(r => r.Input)));
	}

	/// <summary>A column deselects itself once it has its face shade (the face goes after the material). Call after everything else is wired to the face
	/// relays, so this comes last.</summary>
	public void FinishColumns()
	{
		for (int c = 0; c < settings.Columns; c++) { for (int face = 0; face < 4; face++) { FaceColumn[c, face].Output.Connect(deselect[c].Input); } }
	}

	/// <summary>Wires the column relays to what they show: depthOutputs(column, step - 1), hueOutputs(column, material), faceOutputs(column, face) (null or
	/// empty: nothing), and each column's texture overlay.</summary>
	public void ConnectScreen(Func<int, int, Input> depthOutputs, Func<int, int, Input?> hueOutputs, Func<int, int, Input?> faceOutputs, Group?[] textureOverlays)
	{
		for (int c = 0; c < settings.Columns; c++)
		{
			if (textureOverlays[c] is Group overlay)
			{
				foreach ((double opacity, Relay relay) in StripeColumn[c]) { relay.Output.Connect(new Input { new Target<Group>(overlay, new ChangeOpacity<Group>(Opacity: opacity, Duration: 0)) }); }
			}
			for (int d = 0; d < settings.Depths; d++) { Input outputs = depthOutputs(c, d); if (outputs.Count > 0) { DepthColumn[c, d].Output.Connect(outputs); } }
			for (int m = 0; m < HueColumn.GetLength(1); m++) { if (hueOutputs(c, m) is { Count: > 0 } hue) { HueColumn[c, m].Output.Connect(hue); } }
			for (int face = 0; face < 4; face++) { if (faceOutputs(c, face) is { Count: > 0 } faceOut) { FaceColumn[c, face].Output.Connect(faceOut); } }
		}
	}
}
