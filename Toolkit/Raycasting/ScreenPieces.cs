using LevelXML;
namespace LevelXMLUtils.Raycasting;

/// <summary>
/// The floor and ceiling: on a flat floor the distance of a point depends only on its screen row, so their fog is one fixed picture, dark at the
/// horizon and lighter toward the player, in bands. A band's colour is the darkening a wall gets at the distance the floor is at in that band, so the
/// base of a wall and the floor beside it match. The mask bars (<see cref="MaskBars"/>) are cut from the same picture. Offsets are distances from the
/// horizon in screen units.
/// </summary>
public class FloorFog
{
	readonly RaycastSettings settings;
	public FloorFog(RaycastSettings settings) { this.settings = settings; }

	public double PanelOffset => settings.MaxWallHeight / 2;
	public double FarOffset => settings.MinWallHeight / 2;

	/// <summary>Band edges from the far end (half the shortest wall) to the panels' edge (half the tallest), evenly spaced in the distance the floor is at,
	/// so every band is the same step of shade.</summary>
	public double[] Boundaries()
	{
		double far = 1 / FarOffset, near = 1 / PanelOffset;
		return Enumerable.Range(0, settings.FloorBands + 1).Select(i => 1 / (far - (far - near) * i / settings.FloorBands)).ToArray();
	}

	/// <summary>The colour at an offset: the near colour darkened like a wall at the distance the floor is there.</summary>
	public int Fog(int color, double offset)
	{
		double shade = settings.FarShadeOpacity / 100.0 * Math.Clamp((settings.PerspectiveScale / Math.Max(2 * offset, 1e-9) - 1) / (settings.MaxRange - 1), 0, 1);
		int Channel(int shift) => (int)Math.Round(((color >> shift) & 0xFF) * (1 - shade)) << shift;
		return Channel(16) | Channel(8) | Channel(0);
	}

	/// <summary>The pieces of the fog picture between two offsets, as (from, to, colour): a band cut by the limits keeps the whole band's colour, so the
	/// pieces of one band in different bars match.</summary>
	public List<(double from, double to, int color)> Pieces(int nearColor, double innerOffset, double outerOffset)
	{
		double[] edges = Boundaries();
		List<(double lo, double hi)> all = new() { (0, edges[0]) };
		for (int i = 0; i + 1 < edges.Length; i++) { all.Add((edges[i], edges[i + 1])); }
		all.Add((edges[^1], double.PositiveInfinity));
		List<(double, double, int)> pieces = new();
		foreach ((double lo, double hi) in all)
		{
			double from = Math.Max(lo, innerOffset), to = Math.Min(hi, outerOffset);
			if (to - from < 1e-9) { continue; }
			double middle = double.IsInfinity(hi) ? PanelOffset : (lo + hi) / 2;
			pieces.Add((from, to, Fog(nearColor, middle)));
		}
		return pieces;
	}
}

/// <summary>
/// The two bars of one screen column at one distance step, above and below the band a wall at that step fills, as ONE foreground group, so a single
/// opacity change shows or hides both. A shape can't be resized by a trigger, so a wall's height on the screen comes from covering the full-height
/// colour panel with these. Each bar is the fog picture's pieces from the wall's band out, so it looks like the floor or ceiling around it.
/// </summary>
public class MaskBars
{
	public readonly Group Group;
	public readonly Input Input, Reset;
	public IEnumerable<Entity> Entities => new[] { Group };

	/// <param name="outerOffset">Where the bars end, from the horizon (the panels' edge unless given): the raycaster splits a step's bars into the band its
	/// wall's edge is in and the bands beyond, which many steps share.</param>
	public MaskBars(FloorFog fog, int ceilingColor, int floorColor, double x, double horizonY, double bandOffset, double width, double? outerOffset = null)
	{
		List<Entity> shapes = new();
		var top = fog.Pieces(ceilingColor, bandOffset, outerOffset ?? fog.PanelOffset);
		var bottom = fog.Pieces(floorColor, bandOffset, outerOffset ?? fog.PanelOffset);
		for (int i = 0; i < top.Count; i++)
		{
			(double from, double to, int color) = top[i];
			if (color == bottom[i].color) { shapes.Add(Pair(x, horizonY, width, from, to, color)); continue; }
			shapes.Add(new Rectangle { X = x, Y = horizonY - (from + to) / 2, Width = width, Height = to - from, FillColor = color, OutlineColor = -1, Opacity = 100, Interactive = false });
			(from, to, color) = bottom[i];
			shapes.Add(new Rectangle { X = x, Y = horizonY + (from + to) / 2, Width = width, Height = to - from, FillColor = color, OutlineColor = -1, Opacity = 100, Interactive = false });
		}
		Group = new Group(shapes.ToArray()) { Foreground = true, Opacity = 0 };
		Input = new() { new Target<Group>(Group, new ChangeOpacity<Group>(Opacity: 100, Duration: 0)) };
		Reset = new() { new Target<Group>(Group, new ChangeOpacity<Group>(Opacity: 0, Duration: 0)) };
	}

	/// <summary>A top and a bottom piece of one colour as one shape: the top piece, down the column's left edge to the bottom piece, round it and back up
	/// the same edge. The edge travelled both ways encloses nothing, so the wall between the pieces shows.</summary>
	public static Art Pair(double x, double horizonY, double width, double from, double to, int color)
	{
		var shape = new RectangleArtShape(x, horizonY, width, 2 * to, color: color, outline: -1);
		shape.Vertices.Clear();
		double w = width / 2;
		foreach ((double vx, double vy) in new[] { (-w, -to), (w, -to), (w, -from), (-w, -from), (-w, from), (w, from), (w, to), (-w, to) }) { shape.Vertices.Add(new(new(vx, vy))); }
		return shape;
	}
}

/// <summary>
/// A piece of the screen (a colour panel, or a face overlay at one opacity) that a redraw leaves alone when it wants it again, instead of hiding it at
/// the start of the redraw and showing it again: opacity changes are the costliest trigger action (about 25 times an activation), and most columns
/// show the same colour and face from one redraw to the next. <see cref="Want"/> shows it only if it isn't showing and cancels its pending hide; the
/// redraw's reset fires <see cref="Shown"/>, which arms <see cref="Pending"/> if the piece is showing; the commit after the ray walk fires Pending, which
/// hides what nobody wanted.
/// </summary>
public class KeptPiece
{
	public readonly Relay Shown = new(startDisabled: true);
	public readonly Relay Pending = new(startDisabled: true);
	public readonly Relay Gate = new();
	public Input Want => new(Pending.Disable.Concat(Gate.Input));
	public IEnumerable<Entity> Entities => Shown.Entities.Concat(Pending.Entities).Concat(Gate.Entities);

	public KeptPiece(Input show, Input hide)
	{
		Gate.Output.Connect(new Input(show.Concat(Gate.Disable)));
		Shown.Output.Connect(Pending.Enable);
		Pending.Output.Connect(new Input(hide.Concat(Shown.Disable).Concat(Gate.Enable).Concat(Pending.Disable)));
	}

	/// <summary>Pieces that are the same shape at another opacity: showing this one replaces them, so it forgets that they were showing.</summary>
	public void Replaces(IEnumerable<KeptPiece> others)
	{
		foreach (KeptPiece other in others) { Gate.Output.Connect(new Input(other.Pending.Disable.Concat(other.Shown.Disable).Concat(other.Gate.Enable))); }
	}
}
