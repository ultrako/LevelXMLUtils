using LevelXML;
using SkiaSharp;
namespace LevelXMLUtils;

/// <summary>
/// Turns a pixel-art image into Happy Wheels art shapes, one shape per colour (not one per pixel).
/// A colour's pixels are traced into outline polygons on the pixel grid; a colour whose pixels are in several
/// separate pieces still becomes ONE art shape, its pieces joined into one outline the way SvgArt
/// does subpaths (each piece is entered from and left back to the origin, which draws a zero-width spike).
///
/// Overlap instead of holes: where a colour encloses pixels of other colours (a ring around a dot), its polygon is
/// filled solid across them and the shapes are ordered so the enclosed colour is drawn later, on top. That avoids
/// holes for those. An enclosed area of TRANSPARENT pixels is a real hole: it is traced as a loop wound the other way round (inside the same shape), which
/// leaves it empty under either fill rule.
///
/// The most common colour is drawn as the whole silhouette underneath, and has no shape of its own, so any seam between
/// two other colours shows that colour instead of the background.
/// </summary>
public class PixelArt
{
	public readonly int Width, Height;
	// Colour of each pixel as 0xRRGGBB, or null where transparent.
	public readonly int?[,] Pixels;

	public PixelArt(int?[,] pixels)
	{
		Pixels = pixels;
		Width = pixels.GetLength(0);
		Height = pixels.GetLength(1);
	}

	/// <summary>The part of the image from (left, top), width by height pixels.</summary>
	public PixelArt Crop(int left, int top, int width, int height)
	{
		int?[,] pixels = new int?[width, height];
		for (int x = 0; x < width; x++) { for (int y = 0; y < height; y++) { pixels[x, y] = Pixels[left + x, top + y]; } }
		return new PixelArt(pixels);
	}

	/// <param name="mergeWithin">See <see cref="MergeSimilarColors"/>; 0 leaves every colour as it is.</param>
	/// <param name="backgroundColor">
	/// A colour that means "nothing" (an image saved with a white background): the pixels of it connected to the image's edge
	/// become transparent. The same colour inside the drawing is kept.
	/// </param>
	/// <param name="translucent">
	/// Allow partly transparent pixels: such a colour keeps its alpha in the top byte of the colour (so it is a colour of its own), and its shape is drawn at
	/// that opacity.
	/// </param>
	public static PixelArt Load(string path, int? backgroundColor = null, int mergeWithin = 0, bool translucent = false)
	{
		using SKBitmap bitmap = SKBitmap.Decode(path) ?? throw new FileNotFoundException($"can't read image {path}");
		int?[,] pixels = new int?[bitmap.Width, bitmap.Height];
		for (int y = 0; y < bitmap.Height; y++)
		{
			for (int x = 0; x < bitmap.Width; x++)
			{
				SKColor c = bitmap.GetPixel(x, y);
				if (c.Alpha == 0) { continue; }
				if (c.Alpha != 255 && !translucent) { throw new NotSupportedException($"{path}: pixel ({x},{y}) is partly transparent (alpha {c.Alpha}); use only fully opaque or fully transparent pixels"); }
				pixels[x, y] = (c.Alpha == 255 ? 0 : c.Alpha << 24) | (c.Red << 16) | (c.Green << 8) | c.Blue;
			}
		}
		PixelArt art = new(pixels);
		if (backgroundColor is int background) { art.ClearBackground(background); }
		if (mergeWithin > 0) { art.MergeSimilarColors(mergeWithin); }
		return art;
	}

	/// <summary>
	/// Makes colours that differ by at most <paramref name="maxDifference"/> in every channel the same colour, the most common
	/// one of the group (the image file is not touched). Every distinct colour becomes its own shape, so near-identical
	/// colours (a black and a "3,3,3" black) cost shapes for nothing.
	/// </summary>
	public void MergeSimilarColors(int maxDifference)
	{
		static int Channel(int color, int shift) => (color >> shift) & 0xFF;
		Dictionary<int, int> counts = Colors.ToDictionary(c => c, c => Pixels.Cast<int?>().Count(p => p == c));
		List<int> representatives = new();
		Dictionary<int, int> replacement = new();
		foreach (int color in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key))
		{
			int near = representatives.FirstOrDefault(r => Channel(r, 24) == Channel(color, 24) && new[] { 16, 8, 0 }.All(shift => Math.Abs(Channel(r, shift) - Channel(color, shift)) <= maxDifference), -1);
			if (near >= 0) { replacement[color] = near; } else { representatives.Add(color); }
		}
		for (int y = 0; y < Height; y++) { for (int x = 0; x < Width; x++) { if (Pixels[x, y] is int c && replacement.TryGetValue(c, out int merged)) { Pixels[x, y] = merged; } } }
	}

	private void ClearBackground(int background)
	{
		Stack<(int x, int y)> pending = new();
		void Push(int x, int y) { if (x >= 0 && y >= 0 && x < Width && y < Height && Pixels[x, y] == background) { Pixels[x, y] = null; pending.Push((x, y)); } }
		for (int x = 0; x < Width; x++) { Push(x, 0); Push(x, Height - 1); }
		for (int y = 0; y < Height; y++) { Push(0, y); Push(Width - 1, y); }
		while (pending.Count > 0)
		{
			(int x, int y) = pending.Pop();
			Push(x + 1, y); Push(x - 1, y); Push(x, y + 1); Push(x, y - 1);
		}
	}

	/// <summary>The point a picture stands on: the middle of its lowest row of drawn pixels, at that row's bottom edge (in pixels from the top left).</summary>
	public (double x, double y) FloorAnchor()
	{
		for (int y = Height - 1; y >= 0; y--)
		{
			int min = -1, max = -1;
			for (int x = 0; x < Width; x++) { if (Pixels[x, y] is not null) { if (min < 0) { min = x; } max = x; } }
			if (min >= 0) { return ((min + max + 1) / 2.0, y + 1); }
		}
		throw new InvalidOperationException("the picture has no pixels");
	}

	/// <summary>For each pixel column, the fraction of the picture's drawn pixels that are in it (they add up to 1).</summary>
	public double[] ColumnMass()
	{
		double[] mass = new double[Width];
		double total = 0;
		for (int y = 0; y < Height; y++) { for (int x = 0; x < Width; x++) { if (Pixels[x, y] is not null) { mass[x]++; total++; } } }
		if (total > 0) { for (int x = 0; x < Width; x++) { mass[x] /= total; } }
		return mass;
	}

	public IEnumerable<int> Colors => Pixels.Cast<int?>().OfType<int>().Distinct();

	/// <summary>The pixels of a colour, with anything it encloses that is entirely opaque filled in (see the class comment), except pixels of
	/// <paramref name="underneath"/>: the silhouette colour is drawn below every other shape, so where a colour encloses it (the inside of an o in text on a
	/// board), that must stay a hole in this colour's shape or the board never shows there.</summary>
	public bool[,] FilledMask(int color, int? underneath = null)
	{
		bool[,] filled = new bool[Width, Height];
		for (int y = 0; y < Height; y++) { for (int x = 0; x < Width; x++) { filled[x, y] = Pixels[x, y] == color; } }
		return FillEnclosed(filled, $"colour {color:X6}", underneath);
	}

	// Fills the OPAQUE pixels a mask encloses (another colour inside it). A transparent pixel it encloses is a real hole and is left unfilled, so the outline
	// traces it as a loop wound the other way round, which both the non-zero and the even-odd fill rules leave empty (the spikes' gaps between two outlines).
	private bool[,] FillEnclosed(bool[,] filled, string what, int? underneath = null)
	{
		// Everything not this colour that can be reached from outside the image without crossing this colour is outside;
		// what cannot be reached is enclosed.
		bool[,] outside = new bool[Width, Height];
		Stack<(int x, int y)> pending = new();
		void Push(int x, int y) { if (x >= 0 && y >= 0 && x < Width && y < Height && !filled[x, y] && !outside[x, y]) { outside[x, y] = true; pending.Push((x, y)); } }
		for (int x = 0; x < Width; x++) { Push(x, 0); Push(x, Height - 1); }
		for (int y = 0; y < Height; y++) { Push(0, y); Push(Width - 1, y); }
		while (pending.Count > 0)
		{
			(int x, int y) = pending.Pop();
			Push(x + 1, y); Push(x - 1, y); Push(x, y + 1); Push(x, y - 1);
		}
		for (int y = 0; y < Height; y++)
		{
			for (int x = 0; x < Width; x++)
			{
				if (filled[x, y] || outside[x, y]) { continue; }
				if (Pixels[x, y] is null || Pixels[x, y] == underneath) { continue; }
				filled[x, y] = true;
			}
		}
		return filled;
	}

	/// <summary>
	/// The outline of a mask as closed loops of pixel-grid corners (x to the right, y down), with the interior on the
	/// right of every edge (clockwise on screen) and collinear points removed.
	/// </summary>
	public static List<List<(int x, int y)>> Outline(bool[,] mask)
	{
		int w = mask.GetLength(0), h = mask.GetLength(1);
		bool At(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && mask[x, y];
		// Directed edges between grid corners, interior on the right. From a corner there can be two ways on where
		// corners of two pixels touch; turning right first (into the interior side) splits such a pinch into separate loops.
		Dictionary<(int x, int y), List<(int x, int y)>> next = new();
		void Edge((int x, int y) from, (int x, int y) to)
		{
			if (!next.TryGetValue(from, out var list)) { next[from] = list = new(); }
			list.Add(to);
		}
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if (!mask[x, y]) { continue; }
				if (!At(x, y - 1)) { Edge((x, y), (x + 1, y)); }         // top edge, going right
				if (!At(x + 1, y)) { Edge((x + 1, y), (x + 1, y + 1)); } // right edge, going down
				if (!At(x, y + 1)) { Edge((x + 1, y + 1), (x, y + 1)); } // bottom edge, going left
				if (!At(x - 1, y)) { Edge((x, y + 1), (x, y)); }         // left edge, going up
			}
		}
		List<List<(int x, int y)>> loops = new();
		while (next.Count > 0)
		{
			(int x, int y) start = next.Keys.OrderBy(p => p.y).ThenBy(p => p.x).First();
			List<(int x, int y)> loop = new() { start };
			(int x, int y) current = start, previous = start;
			bool first = true;
			while (true)
			{
				List<(int x, int y)> options = next[current];
				(int x, int y) target = options[0];
				if (options.Count > 1)
				{
					// at a pinch: prefer the sharpest right turn relative to the way we came
					(int dx, int dy) heading = first ? (1, 0) : (current.x - previous.x, current.y - previous.y);
					target = options.OrderBy(o => TurnRank(heading, (o.x - current.x, o.y - current.y))).First();
				}
				options.Remove(target);
				if (options.Count == 0) { next.Remove(current); }
				first = false;
				previous = current;
				current = target;
				if (current == start) { break; }
				loop.Add(current);
			}
			loops.Add(RemoveCollinear(loop));
		}
		return loops;
	}

	// Lower is a sharper right turn (right, straight, left, back). Screen coordinates: y grows downward.
	private static int TurnRank((int dx, int dy) heading, (int dx, int dy) direction)
	{
		int cross = heading.dx * direction.dy - heading.dy * direction.dx; // > 0 turns clockwise on screen (right)
		int dot = heading.dx * direction.dx + heading.dy * direction.dy;
		if (cross > 0) { return 0; }
		if (dot > 0) { return 1; }
		if (cross < 0) { return 2; }
		return 3;
	}

	private static List<(int x, int y)> RemoveCollinear(List<(int x, int y)> loop)
	{
		List<(int x, int y)> result = new();
		for (int i = 0; i < loop.Count; i++)
		{
			(int x, int y) before = loop[(i + loop.Count - 1) % loop.Count], here = loop[i], after = loop[(i + 1) % loop.Count];
			int cross = (here.x - before.x) * (after.y - here.y) - (here.y - before.y) * (after.x - here.x);
			if (cross != 0) { result.Add(here); }
		}
		return result;
	}

	/// <summary>
	/// The art shapes of the image with every vertex sent through <paramref name="map"/>, from the image's pixel coordinates (plus the offset) to the
	/// level's. Exact for any map that keeps straight lines straight, such as a perspective view of a flat wall: an outline's edges stay edges (the
	/// joins through a shape's middle go out and back along one line, so they stay zero-width too). Each shape is placed at the middle of its mapped
	/// outline with its box as its width and height, as <see cref="ToShapes"/> places them.
	/// </summary>
	public List<Art> ToMappedShapes(Func<double, double, (double x, double y)> map, double offsetX = 0, double offsetY = 0, double opacity = 100)
	{
		List<Art> result = new();
		foreach (Art flat in ToShapes(1, 0, 0))
		{
			var points = flat.Vertices.Select(v => map(offsetX + flat.X + v.Position.X, offsetY + flat.Y + v.Position.Y)).ToList();
			double minX = points.Min(p => p.x), maxX = points.Max(p => p.x), minY = points.Min(p => p.y), maxY = points.Max(p => p.y);
			double centerX = (minX + maxX) / 2, centerY = (minY + maxY) / 2;
			Art art = new() { X = centerX, Y = centerY, Width = maxX - minX, Height = maxY - minY, FillColor = flat.FillColor, OutlineColor = -1, Opacity = opacity };
			foreach ((double x, double y) in points) { art.Vertices.Add(new Vertex(new Point(x - centerX, y - centerY))); }
			result.Add(art);
		}
		return result;
	}

	/// <summary>
	/// The art shapes of the image, in drawing order, for a sprite standing at (0, 0): the pixel (anchorX, anchorY) of the image
	/// is at the origin, and a pixel is <paramref name="pixelSize"/> units wide (and <paramref name="pixelHeight"/> tall when given, else the same). Each shape sits at the middle of its own outline, with the
	/// vertices relative to that, so the caller adds where the sprite stands to each shape's X and Y, and a copy of the sprite
	/// somewhere else is the same vertex text with only X and Y different.
	/// </summary>
	public List<Art> ToShapes(double pixelSize, double anchorX, double anchorY, double? pixelHeight = null)
	{
		List<int> colors = Colors.ToList();
		if (colors.Count == 0) { return new(); }
		Dictionary<int, int> counts = colors.ToDictionary(c => c, c => Pixels.Cast<int?>().Count(p => p == c));
		int silhouetteColor = colors.OrderByDescending(c => counts[c]).ThenBy(c => c).First();
		Dictionary<int, bool[,]> filled = colors.Where(c => c != silhouetteColor).ToDictionary(c => c, c => FilledMask(c, silhouetteColor));
		// Two colours that each enclose pixels of the other (a checkerboard: every cell has the other colour on all four sides) can't both be filled across
		// the other, or whichever is drawn last covers both (the finish line's flag came out white with black notches). Such colours are drawn as exactly
		// their own pixels instead: the other colour fills the gaps anyway.
		for (bool changed = true; changed;)
		{
			changed = false;
			foreach (int a in filled.Keys.ToList())
			{
				foreach (int b in filled.Keys.ToList())
				{
					if (a >= b || !Covers(filled[a], b) || !Covers(filled[b], a)) { continue; }
					filled[a] = ExactMask(a); filled[b] = ExactMask(b); changed = true;
				}
			}
		}

		bool[,] silhouette = new bool[Width, Height];
		for (int y = 0; y < Height; y++) { for (int x = 0; x < Width; x++) { silhouette[x, y] = Pixels[x, y] is not null; } }

		// A colour that encloses another is drawn first
		List<int> remaining = filled.Keys.OrderBy(c => c).ToList();
		List<int> order = new();
		while (remaining.Count > 0)
		{
			int? pick = remaining.Where(c => !remaining.Any(other => other != c && Covers(filled[other], c) && !Covers(filled[c], other))).Cast<int?>().FirstOrDefault();
			if (pick is not int chosen) { throw new NotSupportedException("colours enclose each other in a cycle: " + string.Join(", ", remaining.Select(c => c.ToString("X6")))); }
			order.Add(chosen);
			remaining.Remove(chosen);
		}

		List<Art> shapes = new() { MakeShape(Outline(silhouette), silhouetteColor, pixelSize, anchorX, anchorY, pixelHeight ?? pixelSize) };
		foreach (int color in order) { shapes.Add(MakeShape(Outline(filled[color]), color, pixelSize, anchorX, anchorY, pixelHeight ?? pixelSize)); }
		return shapes;
	}

	// The pixels of exactly that colour, nothing filled in
	private bool[,] ExactMask(int color)
	{
		bool[,] mask = new bool[Width, Height];
		for (int y = 0; y < Height; y++) { for (int x = 0; x < Width; x++) { mask[x, y] = Pixels[x, y] == color; } }
		return mask;
	}

	// Whether the mask covers any pixel of the colour
	private bool Covers(bool[,] mask, int color)
	{
		for (int y = 0; y < Height; y++) { for (int x = 0; x < Width; x++) { if (mask[x, y] && Pixels[x, y] == color) { return true; } } }
		return false;
	}

	private static Art MakeShape(List<List<(int x, int y)>> loops, int color, double pixelSize, double anchorX, double anchorY, double pixelHeight)
	{
		Point P((int x, int y) corner) => new((corner.x - anchorX) * pixelSize, (corner.y - anchorY) * pixelHeight);
		// The game treats a shape's width and height as its box centred on its position, and scales the vertices to fit it, so
		// the vertices must be relative to the middle of the box (as RectangleArtShape's are), not to some other origin: shapes
		// that were not centred on their origin (a small one far from the floor point) were drawn shrunk and pulled toward it.
		// So the shape sits at the middle of its own outline, and it is the caller that adds where the sprite stands.
		double minX = loops.SelectMany(l => l).Min(c => P(c).X), maxX = loops.SelectMany(l => l).Max(c => P(c).X);
		double minY = loops.SelectMany(l => l).Min(c => P(c).Y), maxY = loops.SelectMany(l => l).Max(c => P(c).Y);
		double centerX = (minX + maxX) / 2, centerY = (minY + maxY) / 2;
		// a translucent colour keeps its alpha in the top byte (see Load)
		int alpha = (color >> 24) & 0xFF;
		Art art = new() { X = centerX, Y = centerY, Width = maxX - minX, Height = maxY - minY, FillColor = color & 0xFFFFFF, OutlineColor = -1, Opacity = alpha == 0 ? 100 : Math.Round(alpha * 100.0 / 255, 1) };
		Point Relative((int x, int y) corner) => new(P(corner).X - centerX, P(corner).Y - centerY);
		// Several loops are joined into one outline by short bridges: round a loop back to its start, across to the nearest corner of the nearest loop
		// not yet visited, round that one, and so on; at the end back along the same bridges to the first loop. A bridge walked there and back draws
		// nothing (an edge drawn twice, there and back, encloses nothing). They used to be joined through the middle, a spoke out to every loop: the
		// spokes crossed many other loops, and an outline that crosses itself that much was slow the first time each size or angle of it was drawn
		// (measured at about 50 ms per object in headless Chromium, a visible stutter).
		List<List<(int x, int y)>> left = loops.Select(l => l).ToList();
		List<(int x, int y)> starts = new();
		(int x, int y) at = left[0][0];
		while (left.Count > 0)
		{
			long D((int x, int y) c) => (long)(c.x - at.x) * (c.x - at.x) + (long)(c.y - at.y) * (c.y - at.y);
			var (loop, k) = left.Select(l => (l, l.Select((c, i) => (c, i)).MinBy(ci => D(ci.c)).i)).MinBy(li => D(li.l[li.Item2]));
			left.Remove(loop);
			for (int i = 0; i < loop.Count; i++) { art.Vertices.Add(new Vertex(Relative(loop[(k + i) % loop.Count]))); }
			at = loop[k];
			art.Vertices.Add(new Vertex(Relative(at)));
			starts.Add(at);
		}
		for (int i = starts.Count - 2; i >= 0; i--) { art.Vertices.Add(new Vertex(Relative(starts[i]))); }
		return art;
	}
}
