using LevelXML;
namespace LevelXMLUtils.Raycasting;

/// <summary>
/// A picture standing in a cell of the map, drawn facing the player at the size and place its distance and angle give, and hidden behind walls. It has
/// one or more states (pictures with the same floor point, for example a lever off and on), and a level switches between them with
/// <see cref="ShowState"/> or takes it away with <see cref="Hide"/>; either shows from the next redraw.
/// </summary>
/// <remarks>
/// The pictures are drawn by moving them, not by keeping a hidden copy for every place they can be seen: one sliding object per (size, state), all
/// pinned to one carrier that a trigger slides to the right place. Every object is a physics body (the game allows 1,024 in all), so a sprite costs
/// about (Depths / SpriteSizeStep + 1) bodies per state. Sprites with the same pictures that are never on the screen together share their objects.
/// </remarks>
public class RaycastSprite
{
	public int CellX { get; }
	public int CellY { get; }
	public IReadOnlyList<PixelArt> States { get; }
	/// <summary>How tall it is drawn, as a share of a wall's height at the same distance.</summary>
	public double HeightFraction { get; }
	/// <summary>The state it shows at the start, or -1 for none.</summary>
	public int StartState { get; }

	/// <summary>Per state: shows this state from the next redraw (and not the others). Built by the <see cref="Raycaster"/>.</summary>
	public IReadOnlyList<Input> ShowState { get; internal set; } = Array.Empty<Input>();
	/// <summary>Shows no state from the next redraw.</summary>
	public Input Hide { get; internal set; } = new();

	/// <summary>Whether it can stop the player walking into or through its cell (null: never), and if so whether it does at the start.</summary>
	public bool? Blocking { get; }
	/// <summary>For a sprite that can block: from now on it stops the player walking into or through its cell, or lets them (a door's spikes raised or
	/// lowered). Takes effect at once, not at the next redraw.</summary>
	public Input Block { get; internal set; } = new();
	public Input Unblock { get; internal set; } = new();

	/// <param name="states">The pictures, one per state. They are drawn from their floor point (the middle of their lowest row of pixels), which stands
	/// on the floor in the middle of the cell.</param>
	/// <summary>Pictures for further away, nearest first: from each distance (in cells) on, the states are drawn with these instead (a simpler
	/// drawing for when the sprite is small on the screen).</summary>
	public IReadOnlyList<(double fromDistance, IReadOnlyList<PixelArt> states)> Farther { get; }

	/// <param name="blocking">Null for a sprite the player walks through; true or false for one that can block (see <see cref="Block"/>), and whether it
	/// does at the start.</param>
	/// <param name="farther">See <see cref="Farther"/>: each entry has a picture per state, in the same order as <paramref name="states"/>.</param>
	public RaycastSprite(int cellX, int cellY, IReadOnlyList<PixelArt> states, double heightFraction = 0.42, int startState = 0, bool? blocking = null,
		IReadOnlyList<(double fromDistance, IReadOnlyList<PixelArt> states)>? farther = null)
	{
		if (states.Count == 0) { throw new ArgumentException("a sprite needs at least one picture"); }
		if (startState < -1 || startState >= states.Count) { throw new ArgumentException($"no state {startState}"); }
		farther ??= Array.Empty<(double, IReadOnlyList<PixelArt>)>();
		if (farther.Any(f => f.states.Count != states.Count)) { throw new ArgumentException("every distance needs a picture for each state"); }
		if (farther.Zip(farther.Skip(1)).Any(p => p.Second.fromDistance <= p.First.fromDistance)) { throw new ArgumentException("the farther pictures must be nearest first"); }
		CellX = cellX; CellY = cellY; States = states; HeightFraction = heightFraction; StartState = startState; Blocking = blocking; Farther = farther;
	}

	/// <summary>The pictures (one per state) drawn at a distance in cells.</summary>
	public IReadOnlyList<PixelArt> StatesAt(double distance) => Farther.LastOrDefault(f => distance >= f.fromDistance).states ?? States;
	internal bool SamePictures(RaycastSprite other) => ReferenceEquals(States, other.States) && HeightFraction == other.HeightFraction
		&& Farther.Count == other.Farther.Count && Farther.Zip(other.Farther).All(p => p.First.fromDistance == p.Second.fromDistance && ReferenceEquals(p.First.states, p.Second.states));

	// Filled in by the raycaster: [cell index y * width + x, facing] -> the screen position (in SpriteXStep columns) and size it is drawn at from there
	/// <summary>[cell index y * width + x, facing]: the screen position (in SpriteXStep columns) and size it is drawn at from there.</summary>
	public IReadOnlyDictionary<(int cell, int facing), (int x, int size)> Views => views;
	internal Dictionary<(int cell, int facing), (int x, int size)> views = new();
	internal SpritePool Pool = null!;
	internal int WalkIndex;
	/// <summary>Per size it can be drawn at, a relay per state that shows that state's object (only the current state's are enabled).</summary>
	public IReadOnlyDictionary<int, Relay[]> ShowRelays => showRelays;
	internal Dictionary<int, Relay[]> showRelays = new();
}

/// <summary>Where and how big sprites are drawn, from the settings.</summary>
internal class SpriteGeometry
{
	readonly RaycastSettings s;
	readonly RaycastMap map;
	public SpriteGeometry(RaycastSettings settings, RaycastMap map) { s = settings; this.map = map; }

	// Nearer than this a sprite is under the player and not drawn (except from its own cell, underfoot)
	public const double MinDepth = 0.5;
	// From its own cell a sprite is drawn in an extra size at the bottom middle of the screen: a little bigger than one step away, a third of it cut off
	public const double UnderfootScale = 1.15, UnderfootCutOff = 1.0 / 3;
	public int UnderfootSize => (s.Depths - 1) / s.SpriteSizeStep + 1;
	public int UnderfootX => s.Columns / s.SpriteXStep / 2;
	double StripWidth => s.ScreenWidth / s.Columns;
	double ScreenLeft => -s.ScreenWidth / 2;

	/// <summary>The local (screen-centred) x of the floor point, the floor point's y below the horizon, and the height of a wall at that size.</summary>
	public (double x, double floorY, double wallHeight) Placement(int xIndex, int sizeIndex)
	{
		double x = ScreenLeft + StripWidth * s.SpriteXStep * (xIndex + 0.5);
		if (sizeIndex == UnderfootSize)
		{
			double near = Placement(xIndex, 0).wallHeight * UnderfootScale;
			return (x, double.NaN, near);
		}
		double step = Math.Min(s.Depths, sizeIndex * s.SpriteSizeStep + (s.SpriteSizeStep + 1) / 2.0);
		double wallHeight = s.MaxWallHeight - (step - 1) / (s.Depths - 1) * (s.MaxWallHeight - s.MinWallHeight);
		return (x, wallHeight / 2, wallHeight);
	}

	/// <summary>The distance in cells a size stands for (the underfoot size: one step away).</summary>
	public double DistanceOf(int sizeIndex) => s.PerspectiveScale / Placement(0, sizeIndex == UnderfootSize ? 0 : sizeIndex).wallHeight;

	/// <summary>Where the floor point is below the horizon and how tall the picture is, for a sprite of the given height fraction at a size.</summary>
	public (double floorY, double height) Drawn(int sizeIndex, double heightFraction)
	{
		(_, double floorY, double wallHeight) = Placement(0, sizeIndex);
		double height = wallHeight * heightFraction;
		if (sizeIndex == UnderfootSize) { floorY = s.ScreenHeight / 2 + height * UnderfootCutOff; }
		return (floorY, height);
	}

	/// <summary>Where a sprite (ox, oy) cells from the player, facing f, is drawn: its screen position and size, the screen column under its floor point (-1
	/// when that is off the screen); null when it is not drawn (too near, too far, or wholly off the screen). Depends only on the offset and the facing's
	/// angle, so a quarter turn of both gives the same answer.</summary>
	public (int x, int size, int anchor)? View(double ox, double oy, int f, Func<int, IReadOnlyList<(PixelArt art, double[] mass, double anchor)>> picturesAt, double heightFraction)
	{
		double plane = Math.Tan(s.FieldOfViewDegrees * Math.PI / 360);
		double angle = f * 2.0 * Math.PI / s.Facings;
		double dirX = Math.Sin(angle), dirY = -Math.Cos(angle), rightX = Math.Cos(angle), rightY = Math.Sin(angle);
		double depth = ox * dirX + oy * dirY;
		if (depth < MinDepth || depth > s.MaxRange + 1e-9) { return null; }
		double cameraX = (ox * rightX + oy * rightY) / depth / plane;
		int column = (int)Math.Floor((cameraX + 1) / 2 * s.Columns);
		int xIndex = (int)Math.Floor((double)column / s.SpriteXStep), sizeIndex = (s.DepthStep(depth) - 1) / s.SpriteSizeStep;
		(double drawX, _, double wallHeight) = Placement(xIndex, sizeIndex);
		double height = wallHeight * heightFraction;
		// drawn while any of its pixels are on the screen
		bool onScreen = picturesAt(sizeIndex).Any(p =>
		{
			double scale = height / p.art.Height;
			for (int i = 0; i < p.mass.Length; i++)
			{
				if (p.mass[i] <= 0) { continue; }
				double x0 = drawX + (i - p.anchor) * scale, x1 = x0 + scale;
				if (Math.Min(x1, -ScreenLeft) > Math.Max(x0, ScreenLeft)) { return true; }
			}
			return false;
		});
		if (!onScreen) { return null; }
		int anchorColumn = (int)Math.Floor((drawX - ScreenLeft) / StripWidth);
		return (xIndex, sizeIndex, anchorColumn >= 0 && anchorColumn < s.Columns ? anchorColumn : -1);
	}

	/// <summary>Whether the sprite at (tx, ty) is seen from (px, py) facing f: the ray of column <paramref name="anchor"/> (or any column, for -1) reaches its
	/// cell before a wall.</summary>
	public bool Reaches(int px, int py, int f, int anchor, int tx, int ty)
	{
		bool RayReaches(int c)
		{
			foreach ((int dx, int dy, _, _, _) in s.RayPath(f, c))
			{
				(int x, int y) = (px + dx, py + dy);
				if ((x, y) == (tx, ty)) { return true; }
				if (map.WallAt(x, y)) { return false; }
			}
			return false;
		}
		return anchor >= 0 ? RayReaches(anchor) : Enumerable.Range(0, s.Columns).Any(RayReaches);
	}

	public static List<(PixelArt art, double[] mass, double anchor)> Masses(IEnumerable<PixelArt> pictures) =>
		pictures.Select(p => (p, p.ColumnMass(), p.FloorAnchor().x)).ToList();
}

/// <summary>
/// The one sliding body a pool of sprite objects rides on (the objects are pinned to it): moving it moves them all with one joint action. An invisible
/// circle in a row of its own, much heavier than the objects, so the pins move the objects rather than drag it back.
/// </summary>
internal class SpriteCarrier
{
	public readonly Group Group;
	public readonly SlidingJoint Joint;
	public const double Density = 100, ObjectDensity = 0.1;

	public SpriteCarrier(double worldX, double rowY)
	{
		double x = worldX + SlidingSprite.CarrierOffsetX;
		Group = new Group(new Circle { X = 0, Y = 0, Width = 4, Height = 4, Fixed = false, Density = Density, Collision = Collision.Nothing, Opacity = 0 }) { X = x, Y = rowY, FixedRotation = true, Opacity = 0 };
		Joint = new SlidingJoint(Group) { X = x, Y = rowY, Angle = 0, Limit = true, UpperLimit = 0, LowerLimit = 0, Motorized = true, Speed = 0, Force = 100000, CollideConnected = false };
	}

	/// <summary>Puts every object of the pool <paramref name="offset"/> units right of where it was built. The limits are negative for a move right: the
	/// joint measures the world relative to the group.</summary>
	public Input MoveTo(double offset) => new() { new Target<Joint>(Joint, new ChangeLimits(-offset - 1, -offset)) };
}

/// <summary>One picture at one size: a foreground group with fixed rotation, carrying a tiny circle that collides with nothing so a joint has a body to
/// hold, pinned to its pool's carrier. It shows and hides by group opacity.</summary>
internal class SlidingSprite
{
	public readonly Group Group;
	public readonly Joint Joint;
	public readonly Input Show, Hide;
	// The circle is this far right of the floor point: a body that leaves the physics world (the map plus 250 units all round) is lost, and the picture
	// has to be able to slide past the screen's left edge
	public const double CarrierOffsetX = 400;

	public SlidingSprite(IEnumerable<Art> shapes, double worldX, double worldFloorY, double rowY, SpriteCarrier carrier)
	{
		double circleY = rowY - worldFloorY;
		List<Entity> children = new() { new Circle { X = CarrierOffsetX, Y = circleY, Width = 4, Height = 4, Fixed = false, Density = SpriteCarrier.ObjectDensity, Collision = Collision.Nothing, Opacity = 0 } };
		children.AddRange(shapes);
		Group = new Group(children.ToArray()) { X = worldX, Y = worldFloorY, Foreground = true, FixedRotation = true, Opacity = 0 };
		Joint = new PinJoint(Group, carrier.Group) { X = worldX + CarrierOffsetX, Y = rowY, CollideConnected = false };
		Show = new() { new Target<Group>(Group, new ChangeOpacity<Group>(Opacity: 100, Duration: 0)) };
		Hide = new() { new Target<Group>(Group, new ChangeOpacity<Group>(Opacity: 0, Duration: 0)) };
	}
}

/// <summary>
/// The objects one or more sprites are drawn with (one per size and state, on one carrier), and the latches that show the wanted one each redraw.
/// </summary>
/// <remarks>
/// Each object has a Wanted latch the sprite's show relays enable during a redraw, cleared at its start. Showing waits a couple of frames for an object
/// that wasn't showing (Settle, delayed), so it appears after the carrier has moved it rather than at the last place it was shown; when the pool was
/// already showing something (Active) the new object shows at once. Hiding goes through HideCheck, armed only for objects that are showing (Shown) and
/// disarmed by being wanted again, and committed at the end of the redraw: an object that stays wanted is never touched, and one that stopped being
/// wanted is hidden in the same frame.
/// </remarks>
internal class SpritePool
{
	public readonly List<RaycastSprite> Members = new();
	public SpriteCarrier Carrier = null!;
	public readonly Dictionary<(int size, int state), SlidingSprite> Objects = new();
	public readonly Dictionary<int, Relay> Positions = new();
	public readonly Dictionary<(int size, int state), Relay> Wanted = new(), HideCheck = new(), Shown = new();
	public readonly Relay Active = new(startDisabled: true), ClaimActive = new(startDisabled: true), Expire = new();
	public readonly Relay Live = new(startDisabled: true), Commit = new(startDisabled: true), Settle = new(startDisabled: true);
	public IEnumerable<Entity> RelayEntities => new[] { Active, ClaimActive, Expire, Live, Commit, Settle }.SelectMany(r => r.Entities)
		.Concat(Wanted.Values.Concat(HideCheck.Values).Concat(Shown.Values).Concat(Positions.Values).SelectMany(r => r.Entities));

	/// <summary>What one sprite's show relay for an object fires: want it, keep it from being hidden, and claim the pool.</summary>
	public Input Claim(int size, int state) => new(Wanted[(size, state)].Enable.Concat(HideCheck[(size, state)].Disable).Concat(Expire.Disable).Concat(Active.Input)
		.Concat(ClaimActive.Enable).Concat(Live.Enable).Concat(Settle.Enable));

	public void WireLatches()
	{
		foreach (((int size, int state) key, SlidingSprite sprite) in Objects)
		{
			Relay wanted = new(startDisabled: true), shown = new(startDisabled: true), hideCheck = new(startDisabled: true);
			wanted.Output.Connect(sprite.Show);
			wanted.Output.Connect(shown.Enable);
			hideCheck.Output.Connect(sprite.Hide);
			hideCheck.Output.Connect(shown.Disable);
			hideCheck.Output.Connect(hideCheck.Disable);
			shown.Output.Connect(hideCheck.Enable);
			Wanted[key] = wanted; HideCheck[key] = hideCheck; Shown[key] = shown;
		}
		Active.Output.Connect(new Input(Wanted.Values.SelectMany(w => w.Input)));
		ClaimActive.Output.Connect(new Input(Active.Enable.Concat(ClaimActive.Disable)));
		Expire.Output.Connect(Active.Disable);
		Expire.Output.Connect(Live.Disable);
		Live.Output.Connect(new Input(Wanted.Values.SelectMany(w => w.Disable).Concat(Shown.Values.SelectMany(s => s.Input)).Concat(Commit.Enable)));
		Commit.Output.Connect(new Input(HideCheck.Values.SelectMany(h => h.Input).Concat(Commit.Disable)));
		Settle.Output.Connect(new Input(Wanted.Values.SelectMany(w => w.Input)));
	}
}
