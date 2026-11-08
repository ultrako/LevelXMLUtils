namespace LevelXMLUtils.Raycasting;

/// <summary>What a wall is drawn with: its colour at full brightness, and optionally a texture of stripes across each face.</summary>
/// <param name="Texture">The brightness (0 to 1) of each stripe across a face, left to right as the viewer sees it, one per
/// <see cref="RaycastSettings.TextureStripes"/>; null for a plain wall. A texture costs no shapes (one black overlay per column, its opacity set by
/// the stripe the column's ray lands on), and few distinct brightnesses cost less wiring than many.</param>
public record WallMaterial(int Color, double[]? Texture = null);

/// <summary>
/// A grid map for a <see cref="Raycaster"/>: every cell is open floor or a wall of some material. x runs east and y south, from the top left. The
/// map's edge must be walls. Open cells cost wiring whether or not the player can reach them, and walls cost nothing, so fill unused space with wall.
/// </summary>
public class RaycastMap
{
	public int Width { get; }
	public int Height { get; }
	public IReadOnlyList<WallMaterial> Materials { get; }
	readonly int[,] cells;
	readonly Func<int, int, int, int>? faceMaterial;

	/// <param name="cells">[x, y]: 0 for open floor, k for a wall of material k - 1.</param>
	/// <param name="materials">The wall materials.</param>
	/// <param name="faceMaterial">Optional: the material a wall cell's face shows, given (x, y, face) with faces 0 north, 1 east, 2 south, 3 west; by
	/// default every face shows the cell's own material. For example a wall can take the colour of the room it faces.</param>
	public RaycastMap(int[,] cells, IReadOnlyList<WallMaterial> materials, Func<int, int, int, int>? faceMaterial = null)
	{
		this.cells = cells;
		Width = cells.GetLength(0);
		Height = cells.GetLength(1);
		Materials = materials;
		this.faceMaterial = faceMaterial;
		if (materials.Count == 0) { throw new ArgumentException("a map needs at least one wall material"); }
		for (int x = 0; x < Width; x++)
		{
			for (int y = 0; y < Height; y++)
			{
				if (cells[x, y] < 0 || cells[x, y] > materials.Count) { throw new ArgumentException($"cell ({x}, {y}) is {cells[x, y]}: no such material"); }
				if ((x == 0 || y == 0 || x == Width - 1 || y == Height - 1) && cells[x, y] == 0) { throw new ArgumentException($"the map's edge must be walls: ({x}, {y}) is open"); }
			}
		}
	}

	/// <summary>A map from rows of text: each character is a cell, <paramref name="open"/> characters are floor and every other character is the wall
	/// material <paramref name="legend"/> gives it.</summary>
	public static RaycastMap FromRows(IReadOnlyList<string> rows, IReadOnlyDictionary<char, int> legend, IReadOnlyList<WallMaterial> materials, string open = ". ",
		Func<int, int, int, int>? faceMaterial = null)
	{
		int width = rows.Max(r => r.Length), height = rows.Count;
		int[,] cells = new int[width, height];
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				char ch = x < rows[y].Length ? rows[y][x] : ' ';
				if (open.Contains(ch)) { continue; }
				if (!legend.TryGetValue(ch, out int material)) { throw new ArgumentException($"'{ch}' at ({x}, {y}) is not in the legend"); }
				cells[x, y] = material + 1;
			}
		}
		return new RaycastMap(cells, materials, faceMaterial);
	}

	public bool InMap(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
	/// <summary>Whether the cell is a wall; outside the map counts as wall.</summary>
	public bool WallAt(int x, int y) => !InMap(x, y) || cells[x, y] != 0;
	public bool OpenAt(int x, int y) => !WallAt(x, y);
	/// <summary>The material a wall cell's face shows (faces 0 north, 1 east, 2 south, 3 west).</summary>
	public int MaterialOf(int x, int y, int face) => faceMaterial?.Invoke(x, y, face) ?? cells[x, y] - 1;

	/// <summary>Whether a wall cell has open floor beside it: only those can ever be seen.</summary>
	public bool TouchesFloor(int x, int y) => OpenAt(x + 1, y) || OpenAt(x - 1, y) || OpenAt(x, y + 1) || OpenAt(x, y - 1);

	/// <summary>The materials some face that can be seen shows: the screen has a colour panel per column only for these.</summary>
	public HashSet<int> SeenMaterials()
	{
		var seen = new HashSet<int>();
		(int dx, int dy)[] outward = { (0, -1), (1, 0), (0, 1), (-1, 0) }; // the open cell a face looks out on: N, E, S, W
		for (int x = 0; x < Width; x++)
		{
			for (int y = 0; y < Height; y++)
			{
				if (!WallAt(x, y)) { continue; }
				for (int face = 0; face < 4; face++) { if (OpenAt(x + outward[face].dx, y + outward[face].dy)) { seen.Add(MaterialOf(x, y, face)); } }
			}
		}
		return seen;
	}

	/// <summary>
	/// True if walking from the middle of the open cell (px, py) by the step passes through the inside of a solid cell or lands in one (touching a
	/// corner doesn't count).
	/// </summary>
	public static bool StepCrosses(int px, int py, (int dx, int dy) step, Func<int, int, bool> solid)
	{
		double startX = px + 0.5, startY = py + 0.5;
		List<double> crossings = new() { 0, 1 };
		for (int x = Math.Min(px, px + step.dx); x <= Math.Max(px, px + step.dx) + 1; x++)
		{
			double t = step.dx == 0 ? -1 : (x - startX) / step.dx;
			if (t > 0 && t < 1) { crossings.Add(t); }
		}
		for (int y = Math.Min(py, py + step.dy); y <= Math.Max(py, py + step.dy) + 1; y++)
		{
			double t = step.dy == 0 ? -1 : (y - startY) / step.dy;
			if (t > 0 && t < 1) { crossings.Add(t); }
		}
		crossings.Sort();
		for (int i = 0; i < crossings.Count - 1; i++)
		{
			// crossing an x line and a y line at once is passing through a corner, which goes through no cell
			if (crossings[i + 1] - crossings[i] < 1e-9) { continue; }
			double t = (crossings[i] + crossings[i + 1]) / 2;
			if (solid((int)Math.Floor(startX + step.dx * t), (int)Math.Floor(startY + step.dy * t))) { return true; }
		}
		return false;
	}

	/// <summary>True if the segment between the two points crosses no wall cell (touching a corner is fine).</summary>
	public bool SegmentClear(double x0, double y0, double x1, double y1)
	{
		double dx = x1 - x0, dy = y1 - y0;
		List<double> crossings = new() { 0, 1 };
		if (dx != 0) { for (int x = (int)Math.Floor(Math.Min(x0, x1)); x <= (int)Math.Ceiling(Math.Max(x0, x1)); x++) { double t = (x - x0) / dx; if (t > 0 && t < 1) { crossings.Add(t); } } }
		if (dy != 0) { for (int y = (int)Math.Floor(Math.Min(y0, y1)); y <= (int)Math.Ceiling(Math.Max(y0, y1)); y++) { double t = (y - y0) / dy; if (t > 0 && t < 1) { crossings.Add(t); } } }
		crossings.Sort();
		for (int i = 0; i < crossings.Count - 1; i++)
		{
			if (crossings[i + 1] - crossings[i] < 1e-9) { continue; }
			double t = (crossings[i] + crossings[i + 1]) / 2;
			if (WallAt((int)Math.Floor(x0 + dx * t), (int)Math.Floor(y0 + dy * t))) { return false; }
		}
		return true;
	}
}
