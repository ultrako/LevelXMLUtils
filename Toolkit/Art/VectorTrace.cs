using LevelXML;
using SkiaSharp;
namespace LevelXMLUtils;

/// <summary>
/// Turns an image into a few smooth art shapes, the way it would be traced by hand: the
/// underlying shapes with few vertices per curve, not every pixel. The steps:
/// <list type="number">
/// <item>A palette, chosen automatically by k-means in OKLab (where distances follow what the eye sees), then near-identical colours merged and rare
/// ones (mostly anti-aliasing fringes) dropped. Colours can be pinned (always in the palette, never moved) when the automatic choice gets one wrong.</item>
/// <item>A colour map: every pixel to its nearest palette colour, smoothed by a majority filter, and specks smaller than MinRegionShare of the picture
/// given to the neighbour they share the most border with.</item>
/// <item>Layers stacked so that no gap can open between two colours: a colour's shape covers its own pixels and every colour drawn above it, so
/// neighbours overlap instead of meeting edge to edge. The order is chosen greedily to keep the outlines short.</item>
/// <item>Each layer's outline traced at sub-pixel accuracy (marching squares over the layer slightly blurred, so the pixel steps disappear), then
/// fitted with cubic Béziers to within Tolerance pixels (Schneider's algorithm, "An Algorithm for Automatically Fitting Digitized Curves",
/// Graphics Gems 1990), keeping sharp turns as corners.</item>
/// </list>
/// The game draws an art shape's vertices joined by cubic curves: in the level file a vertex is "x_y_inX_inY_outX_outY", the incoming and outgoing
/// handles relative to the vertex, and it scales the vertices (handles with them) so that the box of
/// the vertex positions, measured from the shape's origin, fills the shape's width and height. Each layer is one shape; several loops (pieces and
/// holes) are joined through the shape's middle and back, which cancels under the non-zero fill rule, as PixelArt does.
/// </summary>
public class VectorTrace
{
	public record Options
	{
		/// <summary>The most colours the palette starts with, before merging and dropping.</summary>
		public int MaxColors { get; init; } = 12;
		/// <summary>Palette colours closer than this in OKLab are merged (0.04 is about a just-noticeable difference for flat colours).</summary>
		public double MergeDistance { get; init; } = 0.05;
		/// <summary>A colour covering less of the picture than this is dropped as noise (its pixels go to the nearest kept colour). Small but distinct
		/// colours (eyes, dashes) are kept: rarity is not what makes a colour unimportant.</summary>
		public double MinColorShare { get; init; } = 0.0002;
		/// <summary>An anti-aliasing fringe is dropped: a colour within FringeDistance (OKLab) of the line between two other colours of the palette, a blend
		/// of them, covering less than this share of the picture.</summary>
		public double FringeMaxShare { get; init; } = 0.03;
		public double FringeDistance { get; init; } = 0.04;
		/// <summary>A connected patch of one colour smaller than this share of the picture is given to its neighbour.</summary>
		public double MinRegionShare { get; init; } = 0.00003;
		/// <summary>How far the curves may stray outward from the traced outline, in pixels of the (possibly reduced) image.</summary>
		public double Tolerance { get; init; } = 1.2;
		/// <summary>How far a curve may cut inside its colour's pixels (pixels). Small, so simplifying makes shapes overlap their neighbours instead of
		/// leaving a hairline of whatever is behind (overlap is the lesser fault).</summary>
		public double InwardTolerance { get; init; } = 0.35;
		/// <summary>How far (pixels) a layer reaches under the colours drawn above it, at most halfway to a colour drawn below.</summary>
		public int Reach { get; init; } = 16;
		/// <summary>The most an edge hidden under an upper colour may move (pixels), however much room there is.</summary>
		public double MaxHiddenSlack { get; init; } = 16;
		/// <summary>A turn sharper than this (degrees) is kept as a corner instead of being rounded.</summary>
		public double CornerAngle { get; init; } = 60;
		/// <summary>A blur (Gaussian sigma, pixels) of a layer before it is traced. Off by default: the outline is smoothed along its length instead, which
		/// removes the pixel steps without wiping out lines a pixel or two wide (a blur of 1 erased them).</summary>
		public double Blur { get; init; } = 0;
		/// <summary>Only the circle inscribed in the picture is kept (a Discord profile picture is shown as a circle).</summary>
		public bool Circle { get; init; }
		/// <summary>A bigger picture is scaled down to this many pixels across first (more pixels only cost time; the curves don't need them).</summary>
		public int MaxSize { get; init; } = 640;
		/// <summary>A smaller picture is scaled up to this many pixels across first, so thin lines get pixels of their own colour.</summary>
		public int MinSize { get; init; } = 640;
		/// <summary>An edge-preserving smoothing before the palette (the colour sigma in OKLab; 0 is off), which evens out gradients and compression noise
		/// in a painted picture.</summary>
		public double Denoise { get; init; } = 0;
		/// <summary>Colours (0xRRGGBB) always in the palette and never moved by the clustering.</summary>
		public int[] Pinned { get; init; } = Array.Empty<int>();
		// Make the bottom layer the colour most common along the edge of the opaque area. The layers above may stop a fraction of a pixel short of that edge,
		// so the bottom colour can show there as a thin rim; with 8 colours one test picture got a yellow rim (an eye ring's colour came out at the bottom).
		public bool BottomFromRim { get; init; }
		// Use only this square of the source (in its pixels), and put this colour behind its transparent pixels before tracing (for a picture cut out of a
		// larger drawing with a transparent background)
		public (int X, int Y, int Size)? Crop { get; init; }
		public int? Backdrop { get; init; }
	}

	// The source cropped and set on the backdrop (Options.Crop, Options.Backdrop), or a copy of it
	static SKBitmap Prepare(SKBitmap source, Options options)
	{
		SKRectI from = options.Crop is var (x, y, size) ? new SKRectI(x, y, x + size, y + size) : new SKRectI(0, 0, source.Width, source.Height);
		SKBitmap result = new(from.Width, from.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		using SKCanvas canvas = new(result);
		canvas.Clear(options.Backdrop is int color ? new SKColor((byte)(color >> 16), (byte)(color >> 8), (byte)color) : SKColors.Transparent);
		canvas.DrawBitmap(source, from, new SKRect(0, 0, from.Width, from.Height));
		return result;
	}

	/// <summary>A point of a closed curve: its position and its handles, relative to it (In shapes the curve arriving, Out the one leaving).</summary>
	public record struct Node(double X, double Y, double InX, double InY, double OutX, double OutY);

	public readonly int Width, Height;
	public readonly List<int> Palette = new();
	/// <summary>Bottom first: each layer's colour and its closed curves, in image pixels.</summary>
	public readonly List<(int color, List<List<Node>> loops)> Layers = new();
	/// <summary>The cleaned colour map (an index into Palette, or -1 for transparent), what the curves are fitted to.</summary>
	public readonly int[,] Labels;

	public VectorTrace(string path, Options? options = null) : this(SKBitmap.Decode(path) ?? throw new FileNotFoundException($"can't read image {path}"), options) { }

	public VectorTrace(SKBitmap source, Options? options = null)
	{
		options ??= new Options();
		using SKBitmap prepared = Prepare(source, options);
		using SKBitmap bitmap = Reduce(prepared, options.MaxSize, options.MinSize);
		Width = bitmap.Width;
		Height = bitmap.Height;
		// the opaque pixels in OKLab
		var lab = new (double L, double a, double b)[Width, Height];
		bool[,] opaque = new bool[Width, Height];
		double cx = Width / 2.0, cy = Height / 2.0, radius = Math.Min(Width, Height) / 2.0;
		for (int x = 0; x < Width; x++)
		{
			for (int y = 0; y < Height; y++)
			{
				SKColor c = bitmap.GetPixel(x, y);
				bool inCircle = !options.Circle || Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2) <= radius * radius;
				opaque[x, y] = c.Alpha >= 128 && inCircle;
				lab[x, y] = ToLab((c.Red << 16) | (c.Green << 8) | c.Blue);
			}
		}
		if (options.Denoise > 0) { lab = Bilateral(lab, options.Denoise); }
		List<(double L, double a, double b)> centres = ChoosePalette(lab, opaque, options);
		Palette.AddRange(centres.Select(FromLab));
		Labels = Label(lab, opaque, centres);
		Smooth(Labels);
		RemoveSpecks(Labels, (int)Math.Ceiling(options.MinRegionShare * Width * Height));
		// drop palette entries nothing uses any more, keeping the indexes compact
		int[] used = Enumerable.Range(0, Palette.Count).Where(i => Labels.Cast<int>().Contains(i)).ToArray();
		int[] remap = Enumerable.Repeat(-1, Palette.Count).ToArray();
		for (int i = 0; i < used.Length; i++) { remap[used[i]] = i; }
		for (int x = 0; x < Width; x++) { for (int y = 0; y < Height; y++) { if (Labels[x, y] >= 0) { Labels[x, y] = remap[Labels[x, y]]; } } }
		List<int> palette = used.Select(i => Palette[i]).ToList();
		Palette.Clear();
		Palette.AddRange(palette);

		HashSet<int> below = new();
		foreach (int color in StackingOrder(options.BottomFromRim))
		{
			// this layer covers its own colour and every colour above it (all not yet placed below)
			// the bottom layer is everything opaque: nothing behind the picture can show through anywhere
			bool[,] mask = Layers.Count == 0 ? Opaque() : LayerMask(color, below, options.Reach);
			Func<double, double, (double outward, double inward)> limits = Limits(color, below, options);
			List<List<Node>> loops = Trace(mask, options).Select(loop => Fit(loop, options, limits)).Where(loop => loop.Count >= 2).ToList();
			Layers.Add((Palette[color], loops));
			below.Add(color);
		}
	}

	/// <summary>
	/// How far a layer's outline may move outward and inward at a point. Where the edge is hidden under colours drawn above the layer it may move
	/// anywhere it stays hidden: outward to just short of a colour drawn below (or transparency), inward to just short of the layer's own colour, which
	/// is how an edge under line art is traced by hand (anywhere inside the line). Where it is exposed one of those is right next to it, so the limits
	/// are the tolerances. Distances go through the upper colours in 8-neighbour steps, which never overstate the real distance, less a pixel to spare,
	/// and are capped at MaxHiddenSlack.
	/// </summary>
	Func<double, double, (double outward, double inward)> Limits(int color, HashSet<int> below, Options options)
	{
		bool Upper(int x, int y) => Labels[x, y] >= 0 && Labels[x, y] != color && !below.Contains(Labels[x, y]);
		int[,] Distances(Func<int, int, bool> source)
		{
			int[,] distance = new int[Width, Height];
			Queue<(int x, int y)> queue = new();
			for (int x = 0; x < Width; x++) { for (int y = 0; y < Height; y++) { distance[x, y] = int.MaxValue; if (source(x, y)) { distance[x, y] = 0; queue.Enqueue((x, y)); } } }
			while (queue.Count > 0)
			{
				var (px, py) = queue.Dequeue();
				for (int dx = -1; dx <= 1; dx++)
				{
					for (int dy = -1; dy <= 1; dy++)
					{
						int nx = px + dx, ny = py + dy;
						if (nx < 0 || ny < 0 || nx >= Width || ny >= Height || distance[nx, ny] != int.MaxValue || !Upper(nx, ny)) { continue; }
						distance[nx, ny] = distance[px, py] + 1;
						queue.Enqueue((nx, ny));
					}
				}
			}
			return distance;
		}
		int[,] toOwn = Distances((x, y) => Labels[x, y] == color);
		// the picture's edge counts as exposed (beyond it is whatever the picture is drawn on)
		int[,] toOther = Distances((x, y) => Labels[x, y] < 0 || (Labels[x, y] != color && below.Contains(Labels[x, y])) || x == 0 || y == 0 || x == Width - 1 || y == Height - 1);
		return (x, y) =>
		{
			// the pixels round the point (an outline point lies on the edge between pixels)
			int own = int.MaxValue, other = int.MaxValue;
			for (int px = (int)Math.Floor(x - 1); px <= (int)Math.Floor(x + 1); px++)
			{
				for (int py = (int)Math.Floor(y - 1); py <= (int)Math.Floor(y + 1); py++)
				{
					if (px < 0 || py < 0 || px >= Width || py >= Height) { other = 0; continue; }
					own = Math.Min(own, toOwn[px, py]);
					other = Math.Min(other, toOther[px, py]);
				}
			}
			double Slack(int distance) => distance == int.MaxValue ? options.MaxHiddenSlack : Math.Min(options.MaxHiddenSlack, distance - 1.0);
			return (Math.Max(options.Tolerance, Slack(other)), Math.Max(options.InwardTolerance, Slack(own)));
		};
	}

	bool[,] Opaque()
	{
		bool[,] mask = new bool[Width, Height];
		for (int x = 0; x < Width; x++) { for (int y = 0; y < Height; y++) { mask[x, y] = Labels[x, y] >= 0; } }
		return mask;
	}

	/// <summary>
	/// A layer's pixels: its own colour, and the colours drawn above it where that closes a gap and stays hidden. It reaches under an upper colour up to
	/// Reach pixels from its own, and only where it is nearer its own colour than a colour drawn below it (or transparency), so its edge stays under the
	/// upper colour by about half its width, with room on both sides for a hidden edge to be traced loosely (see Limits; reaching only 2 pixels left the
	/// edge right beside the layer's colour, and it still had to follow every wiggle of the line art): reaching to the upper colour's far edge put two smoothed outlines in the same place, and the lower layer
	/// showed through as a hairline (the eye-white under the line art, along the hair's top). Upper colours it surrounds completely are filled solid.
	/// </summary>
	bool[,] LayerMask(int color, HashSet<int> below, int reach)
	{
		bool Upper(int x, int y) => Labels[x, y] >= 0 && Labels[x, y] != color && !below.Contains(Labels[x, y]);
		// distances through upper colours (4-neighbour steps) from the layer's own colour and from everything else
		int[,] Distances(Func<int, int, bool> source)
		{
			int[,] distance = new int[Width, Height];
			Queue<(int x, int y)> queue = new();
			for (int x = 0; x < Width; x++) { for (int y = 0; y < Height; y++) { distance[x, y] = int.MaxValue; if (source(x, y)) { distance[x, y] = 0; queue.Enqueue((x, y)); } } }
			while (queue.Count > 0)
			{
				var (px, py) = queue.Dequeue();
				foreach (var (nx, ny) in new[] { (px + 1, py), (px - 1, py), (px, py + 1), (px, py - 1) })
				{
					if (nx < 0 || ny < 0 || nx >= Width || ny >= Height || distance[nx, ny] != int.MaxValue || !Upper(nx, ny)) { continue; }
					distance[nx, ny] = distance[px, py] + 1;
					queue.Enqueue((nx, ny));
				}
			}
			return distance;
		}
		int[,] fromOwn = Distances((x, y) => Labels[x, y] == color);
		// "everything else" includes the outside of the picture: an upper colour touching the edge is not surrounded
		int[,] fromOther = Distances((x, y) => (Labels[x, y] < 0 || (Labels[x, y] != color && below.Contains(Labels[x, y]))) || (Upper(x, y) && (x == 0 || y == 0 || x == Width - 1 || y == Height - 1)));
		bool[,] mask = new bool[Width, Height];
		for (int x = 0; x < Width; x++)
		{
			for (int y = 0; y < Height; y++)
			{
				if (Labels[x, y] == color) { mask[x, y] = true; continue; }
				if (!Upper(x, y)) { continue; }
				bool surrounded = fromOther[x, y] == int.MaxValue && fromOwn[x, y] != int.MaxValue;
				mask[x, y] = surrounded || (fromOwn[x, y] <= reach && fromOwn[x, y] < fromOther[x, y]);
			}
		}
		return mask;
	}

	/// <summary>The vertex count of every layer together, what the level file pays for.</summary>
	public int VertexCount => Layers.Sum(l => l.loops.Sum(loop => loop.Count + 1) + (l.loops.Count > 1 ? l.loops.Count + 1 : 0));

	/// <summary>
	/// The layers as art shapes, bottom first: the picture <paramref name="width"/> units wide (the height in proportion), its middle at
	/// (<paramref name="x"/>, <paramref name="y"/>).
	/// </summary>
	public List<Art> ToShapes(double width, double x, double y)
	{
		double scale = width / Width;
		List<Art> shapes = new();
		foreach ((int color, List<List<Node>> loops) in Layers)
		{
			if (loops.Count == 0) { continue; }
			// in units, relative to the picture's middle
			List<Node> Scaled(List<Node> loop) => loop.Select(n => new Node((n.X - Width / 2.0) * scale, (n.Y - Height / 2.0) * scale, n.InX * scale, n.InY * scale, n.OutX * scale, n.OutY * scale)).ToList();
			List<List<Node>> scaled = loops.Select(Scaled).ToList();
			double minX = scaled.SelectMany(l => l).Min(n => n.X), maxX = scaled.SelectMany(l => l).Max(n => n.X);
			double minY = scaled.SelectMany(l => l).Min(n => n.Y), maxY = scaled.SelectMany(l => l).Max(n => n.Y);
			double midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;
			Art art = new() { X = x + midX, Y = y + midY, Width = maxX - minX, Height = maxY - minY, FillColor = color, OutlineColor = -1, Opacity = 100 };
			Vertex V(double px, double py, double inX, double inY, double outX, double outY) =>
				inX == 0 && inY == 0 && outX == 0 && outY == 0 ? new Vertex(new Point(px - midX, py - midY)) : new Vertex(new Point(px - midX, py - midY), new Point(inX, inY), new Point(outX, outY));
			if (scaled.Count == 1)
			{
				foreach (Node n in scaled[0]) { art.Vertices.Add(V(n.X, n.Y, n.InX, n.InY, n.OutX, n.OutY)); }
			}
			else
			{
				// out from the middle to each loop's first point (a straight line: no handles at the middle, none into the first point), round the loop, back
				// to its first point (arriving along the curve) and straight back to the middle
				art.Vertices.Add(new Vertex(new Point(0, 0)));
				foreach (List<Node> loop in scaled)
				{
					Node first = loop[0];
					art.Vertices.Add(V(first.X, first.Y, 0, 0, first.OutX, first.OutY));
					foreach (Node n in loop.Skip(1)) { art.Vertices.Add(V(n.X, n.Y, n.InX, n.InY, n.OutX, n.OutY)); }
					art.Vertices.Add(V(first.X, first.Y, first.InX, first.InY, 0, 0));
					art.Vertices.Add(new Vertex(new Point(0, 0)));
				}
			}
			shapes.Add(art);
		}
		return shapes;
	}

	/// <summary>
	/// Draws art shapes the way the game does (vertices joined by cubic curves from their handles, scaled so the box of the vertex positions, measured
	/// from the origin, fills the width and height; non-zero fill), for previews and tests. The view's top left is (left, top) in the level.
	/// </summary>
	/// <remarks>
	/// What the game does with an art shape: its width and height are a box centred on its position, and its vertices are scaled to fill the box, each
	/// axis clamped to 0.1 to 10 times the vertices' span measured from 0 (with a span of 100, drawn between 10 and 1,000 units). An unset (NaN) width
	/// or height makes it invisible. A vertex is written x_y_inX_inY_outX_outY, its handles relative to it, and every edge is a cubic curve: only
	/// handles at 0, 0 make it straight. The game triangulates a shape the first time it is drawn, cutting each curved edge into at least 8 pieces, so
	/// complex art costs on first sight.
	/// </remarks>
	public static SKBitmap Render(IEnumerable<Art> shapes, int width, int height, double left = 0, double top = 0, double zoom = 1, SKColor? background = null)
	{
		SKBitmap bitmap = new(width, height);
		using SKCanvas canvas = new(bitmap);
		canvas.Clear(background ?? SKColors.White);
		foreach (Art art in shapes)
		{
			var v = art.Vertices.ToList();
			if (v.Count < 2) { continue; }
			double spanX = Math.Max(0, v.Max(p => p.Position.X)) - Math.Min(0, v.Min(p => p.Position.X));
			double spanY = Math.Max(0, v.Max(p => p.Position.Y)) - Math.Min(0, v.Min(p => p.Position.Y));
			double sx = spanX > 0 ? art.Width / spanX : 1, sy = spanY > 0 ? art.Height / spanY : 1;
			// scaled to its box, then turned by its rotation (clockwise degrees) about its centre
			double rc = Math.Cos(art.Rotation * Math.PI / 180), rs = Math.Sin(art.Rotation * Math.PI / 180);
			SKPoint P(double px, double py) { double x = px * sx, y = py * sy; return new((float)((art.X + x * rc - y * rs - left) * zoom), (float)((art.Y + x * rs + y * rc - top) * zoom)); }
			using SKPath path = new() { FillType = SKPathFillType.Winding };
			path.MoveTo(P(v[0].Position.X, v[0].Position.Y));
			// an open shape (SvgArt's lines) has no closing edge and no fill
			bool open = !art.Closed;
			for (int i = 1; i <= (open ? v.Count - 1 : v.Count); i++)
			{
				Vertex from = v[i - 1], to = v[i % v.Count];
				Point o = from.HandleTwo ?? new Point(0, 0), n = to.HandleOne ?? new Point(0, 0);
				path.CubicTo(P(from.Position.X + o.X, from.Position.Y + o.Y), P(to.Position.X + n.X, to.Position.Y + n.Y), P(to.Position.X, to.Position.Y));
			}
			if (!open) { path.Close(); }
			byte alpha = (byte)Math.Round(Math.Clamp((double)art.Opacity, 0, 100) * 2.55);
			SKColor Colour(int c) => new((byte)(c >> 16), (byte)(c >> 8), (byte)c, alpha);
			int color = (int)art.FillColor;
			if (!open && color >= 0)
			{
				using SKPaint paint = new() { Color = Colour(color), IsAntialias = true, Style = SKPaintStyle.Fill };
				canvas.DrawPath(path, paint);
			}
			// the game's outline is a hairline, one screen pixel wide whatever the zoom
			if (art.OutlineColor is double outline && outline >= 0)
			{
				using SKPaint stroke = new() { Color = Colour((int)outline), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
				canvas.DrawPath(path, stroke);
			}
		}
		return bitmap;
	}

	// ---- the palette ----

	/// <summary>The picture scaled to between minSize and maxSize pixels across (a small one up, so a line a pixel wide gets pixels of its own colour).</summary>
	static SKBitmap Reduce(SKBitmap source, int maxSize, int minSize)
	{
		int size = Math.Max(source.Width, source.Height);
		if (size <= maxSize && size >= minSize) { return source.Copy(); }
		double k = (double)Math.Clamp(size, minSize, maxSize) / size;
		return source.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * k)), Math.Max(1, (int)Math.Round(source.Height * k))), SKFilterQuality.High);
	}

	/// <summary>
	/// An edge-preserving smoothing (a bilateral filter, radius 2): each pixel becomes the average of its neighbours weighted by nearness and by how close
	/// their colour is (OKLab, sigma <paramref name="range"/>), so gradients and compression noise even out while edges between colours stay sharp.
	/// </summary>
	(double L, double a, double b)[,] Bilateral((double L, double a, double b)[,] lab, double range)
	{
		var result = new (double L, double a, double b)[Width, Height];
		for (int x = 0; x < Width; x++)
		{
			for (int y = 0; y < Height; y++)
			{
				double sl = 0, sa = 0, sb = 0, sw = 0;
				for (int dx = -2; dx <= 2; dx++)
				{
					for (int dy = -2; dy <= 2; dy++)
					{
						int nx = x + dx, ny = y + dy;
						if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) { continue; }
						double d = Distance(lab[x, y], lab[nx, ny]);
						double w = Math.Exp(-(dx * dx + dy * dy) / (2 * 1.5 * 1.5) - d * d / (2 * range * range));
						sl += lab[nx, ny].L * w; sa += lab[nx, ny].a * w; sb += lab[nx, ny].b * w; sw += w;
					}
				}
				result[x, y] = (sl / sw, sa / sw, sb / sw);
			}
		}
		return result;
	}

	static double Distance((double L, double a, double b) p, (double L, double a, double b) q) => Math.Sqrt((p.L - q.L) * (p.L - q.L) + (p.a - q.a) * (p.a - q.a) + (p.b - q.b) * (p.b - q.b));

	List<(double L, double a, double b)> ChoosePalette((double L, double a, double b)[,] lab, bool[,] opaque, Options options)
	{
		var points = new List<(double L, double a, double b)>();
		for (int x = 0; x < Width; x++) { for (int y = 0; y < Height; y++) { if (opaque[x, y]) { points.Add(lab[x, y]); } } }
		if (points.Count == 0) { return new(); }
		var pinned = options.Pinned.Select(ToLab).ToList();
		// k-means++ seeding, deterministic (a fixed seed), then Lloyd's iterations; pinned centres stay where they are
		Random random = new(12345);
		var centres = new List<(double L, double a, double b)>(pinned);
		if (centres.Count == 0) { centres.Add(points[random.Next(points.Count)]); }
		double[] nearest = points.Select(p => centres.Min(c => Distance(p, c))).Select(d => d * d).ToArray();
		while (centres.Count < Math.Max(options.MaxColors, pinned.Count) && nearest.Sum() > 0)
		{
			double pick = random.NextDouble() * nearest.Sum(), run = 0;
			int index = 0;
			for (; index < points.Count - 1; index++) { run += nearest[index]; if (run >= pick) { break; } }
			centres.Add(points[index]);
			for (int i = 0; i < points.Count; i++) { double d = Distance(points[i], points[index]); nearest[i] = Math.Min(nearest[i], d * d); }
		}
		int[] assignment = new int[points.Count];
		for (int iteration = 0; iteration < 30; iteration++)
		{
			for (int i = 0; i < points.Count; i++) { assignment[i] = Nearest(points[i], centres); }
			bool moved = false;
			for (int c = pinned.Count; c < centres.Count; c++)
			{
				var members = Enumerable.Range(0, points.Count).Where(i => assignment[i] == c).Select(i => points[i]).ToList();
				if (members.Count == 0) { continue; }
				(double L, double a, double b) mean = (members.Average(p => p.L), members.Average(p => p.a), members.Average(p => p.b));
				if (Distance(mean, centres[c]) > 1e-5) { moved = true; }
				centres[c] = mean;
			}
			if (!moved) { break; }
		}
		// merge near-identical colours (into the bigger one, or into a pinned one), then drop the rare ones
		int[] counts = new int[centres.Count];
		for (int i = 0; i < points.Count; i++) { counts[Nearest(points[i], centres)]++; }
		var kept = Enumerable.Range(0, centres.Count).ToList();
		bool merging = true;
		while (merging)
		{
			merging = false;
			foreach (int i in kept)
			{
				int j = kept.Where(o => o != i && (Distance(centres[o], centres[i]) < options.MergeDistance || NearlySame(centres[o], centres[i]))).DefaultIfEmpty(-1).First();
				if (j < 0) { continue; }
				(int into, int from) = (i < pinned.Count || (j >= pinned.Count && counts[i] >= counts[j])) ? (i, j) : (j, i);
				if (from < pinned.Count) { continue; } // two pinned colours stay apart
				if (into >= pinned.Count)
				{
					double total = counts[into] + counts[from];
					centres[into] = ((centres[into].L * counts[into] + centres[from].L * counts[from]) / total, (centres[into].a * counts[into] + centres[from].a * counts[from]) / total, (centres[into].b * counts[into] + centres[from].b * counts[from]) / total);
				}
				counts[into] += counts[from];
				kept.Remove(from);
				merging = true;
				break;
			}
		}
		// drop noise, then fringes (a blend of two other colours), smallest first, so a fringe is judged against the colours that stay
		kept.RemoveAll(i => i >= pinned.Count && counts[i] < options.MinColorShare * points.Count && kept.Count > 1);
		foreach (int c in kept.OrderBy(i => counts[i]).ToList())
		{
			if (c < pinned.Count || counts[c] >= options.FringeMaxShare * points.Count || kept.Count <= 2) { continue; }
			var others = kept.Where(o => o != c).ToList();
			bool blend = others.Any(a => others.Any(b => b != a && SegmentDistance(centres[c], centres[a], centres[b]) < options.FringeDistance));
			if (blend) { kept.Remove(c); }
		}
		return kept.Select(i => centres[i]).ToList();
	}

	/// <summary>How far a colour is from the straight line between two others (in OKLab): how close it is to being a blend of them.</summary>
	static double SegmentDistance((double L, double a, double b) p, (double L, double a, double b) a, (double L, double a, double b) b)
	{
		(double L, double a, double b) ab = (b.L - a.L, b.a - a.a, b.b - a.b), ap = (p.L - a.L, p.a - a.a, p.b - a.b);
		double lengthSquared = ab.L * ab.L + ab.a * ab.a + ab.b * ab.b;
		double t = lengthSquared < 1e-12 ? 0 : Math.Clamp((ap.L * ab.L + ap.a * ab.a + ap.b * ab.b) / lengthSquared, 0, 1);
		return Distance(p, (a.L + t * ab.L, a.a + t * ab.a, a.b + t * ab.b));
	}

	/// <summary>Within 12 of each other on every channel in sRGB: OKLab's cube root makes very dark colours (000000 and 010101) look far apart.</summary>
	static bool NearlySame((double L, double a, double b) p, (double L, double a, double b) q)
	{
		int x = FromLab(p), y = FromLab(q);
		return Enumerable.Range(0, 3).All(k => Math.Abs(((x >> (8 * k)) & 0xFF) - ((y >> (8 * k)) & 0xFF)) <= 12);
	}

	static int Nearest((double L, double a, double b) p, List<(double L, double a, double b)> centres)
	{
		int best = 0;
		double bestDistance = double.MaxValue;
		for (int c = 0; c < centres.Count; c++) { double d = Distance(p, centres[c]); if (d < bestDistance) { bestDistance = d; best = c; } }
		return best;
	}

	int[,] Label((double L, double a, double b)[,] lab, bool[,] opaque, List<(double L, double a, double b)> centres)
	{
		int[,] labels = new int[Width, Height];
		for (int x = 0; x < Width; x++) { for (int y = 0; y < Height; y++) { labels[x, y] = opaque[x, y] && centres.Count > 0 ? Nearest(lab[x, y], centres) : -1; } }
		return labels;
	}

	/// <summary>One pass of a 3 by 3 majority filter (a pixel takes the colour at least 6 of the 9 have), which cleans up stray pixels along edges without
	/// erasing a line one pixel wide; transparency stays.</summary>
	void Smooth(int[,] labels)
	{
		for (int pass = 0; pass < 1; pass++)
		{
			int[,] copy = (int[,])labels.Clone();
			for (int x = 0; x < Width; x++)
			{
				for (int y = 0; y < Height; y++)
				{
					if (copy[x, y] < 0) { continue; }
					Dictionary<int, int> votes = new();
					for (int dx = -1; dx <= 1; dx++) { for (int dy = -1; dy <= 1; dy++) { int nx = x + dx, ny = y + dy; if (nx >= 0 && ny >= 0 && nx < Width && ny < Height && copy[nx, ny] >= 0) { votes[copy[nx, ny]] = votes.GetValueOrDefault(copy[nx, ny]) + 1; } } }
					var (winner, count) = votes.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).First();
					if (count >= 6) { labels[x, y] = winner; }
				}
			}
		}
	}

	/// <summary>Connected patches (4-neighbour) of one colour smaller than minimum pixels are given to the colour they share the most border with, smallest first.</summary>
	void RemoveSpecks(int[,] labels, int minimum)
	{
		for (bool changed = true; changed;)
		{
			changed = false;
			int[,] region = new int[Width, Height];
			for (int x = 0; x < Width; x++) { for (int y = 0; y < Height; y++) { region[x, y] = -1; } }
			List<List<(int x, int y)>> regions = new();
			for (int x = 0; x < Width; x++)
			{
				for (int y = 0; y < Height; y++)
				{
					if (labels[x, y] < 0 || region[x, y] >= 0) { continue; }
					List<(int, int)> cells = new();
					Stack<(int x, int y)> stack = new();
					stack.Push((x, y));
					region[x, y] = regions.Count;
					while (stack.Count > 0)
					{
						var (px, py) = stack.Pop();
						cells.Add((px, py));
						foreach (var (nx, ny) in new[] { (px + 1, py), (px - 1, py), (px, py + 1), (px, py - 1) })
						{
							if (nx >= 0 && ny >= 0 && nx < Width && ny < Height && region[nx, ny] < 0 && labels[nx, ny] == labels[x, y]) { region[nx, ny] = regions.Count; stack.Push((nx, ny)); }
						}
					}
					regions.Add(cells);
				}
			}
			foreach (var cells in regions.Where(r => r.Count < minimum).OrderBy(r => r.Count))
			{
				int own = labels[cells[0].x, cells[0].y];
				Dictionary<int, int> border = new();
				foreach (var (px, py) in cells)
				{
					foreach (var (nx, ny) in new[] { (px + 1, py), (px - 1, py), (px, py + 1), (px, py - 1) })
					{
						if (nx >= 0 && ny >= 0 && nx < Width && ny < Height && labels[nx, ny] >= 0 && labels[nx, ny] != own) { border[labels[nx, ny]] = border.GetValueOrDefault(labels[nx, ny]) + 1; }
					}
				}
				if (border.Count == 0) { continue; }
				int to = border.OrderByDescending(kv => kv.Value).First().Key;
				foreach (var (px, py) in cells) { labels[px, py] = to; }
				changed = true;
			}
		}
	}

	/// <summary>
	/// Palette indexes, bottom first. The bottom layer is everything opaque whatever comes first; each next layer is the union of the colours still to
	/// place, so the colour placed now decides the next layer's outline: the one leaving the shortest outline is placed.
	/// </summary>
	List<int> StackingOrder(bool bottomFromRim = false)
	{
		List<int> remaining = Enumerable.Range(0, Palette.Count).Where(i => Labels.Cast<int>().Contains(i)).ToList(), order = new();
		if (bottomFromRim)
		{
			bool Transparent(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height || Labels[x, y] < 0;
			var rim = new Dictionary<int, int>();
			for (int x = 0; x < Width; x++)
			{
				for (int y = 0; y < Height; y++)
				{
					if (!Transparent(x, y) && (Transparent(x + 1, y) || Transparent(x - 1, y) || Transparent(x, y + 1) || Transparent(x, y - 1))) { rim[Labels[x, y]] = rim.GetValueOrDefault(Labels[x, y]) + 1; }
				}
			}
			int bottom = rim.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
			order.Add(bottom);
			remaining.Remove(bottom);
		}
		while (remaining.Count > 0)
		{
			int best = remaining.OrderBy(c => Perimeter(remaining.Where(o => o != c).ToHashSet())).ThenBy(c => c).First();
			order.Add(best);
			remaining.Remove(best);
		}
		return order;
	}

	int Perimeter(HashSet<int> colors)
	{
		bool In(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Labels[x, y] >= 0 && colors.Contains(Labels[x, y]);
		int edges = 0;
		for (int x = -1; x < Width; x++) { for (int y = -1; y < Height; y++) { if (In(x, y) != In(x + 1, y)) { edges++; } if (In(x, y) != In(x, y + 1)) { edges++; } } }
		return edges;
	}

	// ---- tracing ----

	/// <summary>The closed outlines of a mask, at sub-pixel accuracy, in image pixels; every loop has the inside on the same side, so holes run the other way.</summary>
	List<List<(double x, double y)>> Trace(bool[,] mask, Options options)
	{
		// a field on the pixel centres with a border of zeros, blurred (separable Gaussian)
		int w = Width + 2, h = Height + 2;
		double[,] field = new double[w, h];
		for (int x = 0; x < Width; x++) { for (int y = 0; y < Height; y++) { field[x + 1, y + 1] = mask[x, y] ? 1 : 0; } }
		if (options.Blur > 0)
		{
			int r = (int)Math.Ceiling(3 * options.Blur);
			double[] kernel = Enumerable.Range(-r, 2 * r + 1).Select(i => Math.Exp(-i * i / (2 * options.Blur * options.Blur))).ToArray();
			double sum = kernel.Sum();
			for (int i = 0; i < kernel.Length; i++) { kernel[i] /= sum; }
			double[,] pass = new double[w, h];
			for (int x = 0; x < w; x++) { for (int y = 0; y < h; y++) { double s = 0; for (int k = -r; k <= r; k++) { int sx = x + k; if (sx >= 0 && sx < w) { s += field[sx, y] * kernel[k + r]; } } pass[x, y] = s; } }
			for (int x = 0; x < w; x++) { for (int y = 0; y < h; y++) { double s = 0; for (int k = -r; k <= r; k++) { int sy = y + k; if (sy >= 0 && sy < h) { s += pass[x, sy] * kernel[k + r]; } } field[x, y] = s; } }
		}
		// marching squares: a cell between nodes (i, j) and (i + 1, j + 1); its edges are keyed (i, j, 0) for the top and (i, j, 1) for the left
		bool Inside(int i, int j) => field[i, j] > 0.5;
		(double x, double y) Crossing((int i, int j, int dir) e)
		{
			(int i2, int j2) = e.dir == 0 ? (e.i + 1, e.j) : (e.i, e.j + 1);
			double a = field[e.i, e.j], b = field[i2, j2], t = Math.Abs(b - a) < 1e-12 ? 0.5 : (0.5 - a) / (b - a);
			// node (i, j) is the centre of pixel (i - 1, j - 1), at (i - 0.5, j - 0.5) in image coordinates
			return (e.i + t * (i2 - e.i) - 0.5, e.j + t * (j2 - e.j) - 0.5);
		}
		Dictionary<(int, int, int), (int, int, int)> next = new();
		for (int i = 0; i < w - 1; i++)
		{
			for (int j = 0; j < h - 1; j++)
			{
				bool a = Inside(i, j), b = Inside(i + 1, j), c = Inside(i + 1, j + 1), d = Inside(i, j + 1);
				var top = (i, j, 0); var bottom = (i, j + 1, 0); var leftEdge = (i, j, 1); var right = (i + 1, j, 1);
				// corners in order round the cell, with the edge after each: a -top- b -right- c -bottom- d -left- a. Walking round the boundary with
				// the inside on the right (y down: clockwise round a filled region), a segment goes from the edge where inside starts to the edge where
				// it ends, read anticlockwise; done per run of inside corners
				bool[] inside = { a, b, c, d };
				(int, int, int)[] edgeAfter = { top, right, bottom, leftEdge };
				int count = inside.Count(v => v);
				if (count == 0 || count == 4) { continue; }
				bool saddle = count == 2 && inside[0] == inside[2];
				if (saddle)
				{
					bool centre = (field[i, j] + field[i + 1, j] + field[i + 1, j + 1] + field[i, j + 1]) / 4 > 0.5;
					// each inside corner is cut off on its own unless the centre joins them, when each outside corner is cut off on its own
					for (int k = 0; k < 4; k++)
					{
						if (inside[k] == centre) { continue; }
						// corner k alone: the edges before and after it
						var before = edgeAfter[(k + 3) % 4]; var after = edgeAfter[k];
						if (inside[k]) { next[before] = after; } else { next[after] = before; }
					}
					continue;
				}
				// one run of inside corners: it is entered across the edge before its first corner and left across the edge after its last
				int first = Enumerable.Range(0, 4).First(k => inside[k] && !inside[(k + 3) % 4]);
				int last = first;
				while (inside[(last + 1) % 4]) { last = (last + 1) % 4; }
				next[edgeAfter[(first + 3) % 4]] = edgeAfter[last];
			}
		}
		// link the segments into loops
		List<List<(double x, double y)>> loops = new();
		HashSet<(int, int, int)> done = new();
		foreach (var start in next.Keys)
		{
			if (done.Contains(start)) { continue; }
			List<(double, double)> loop = new();
			var e = start;
			while (done.Add(e)) { loop.Add(Crossing(e)); if (!next.TryGetValue(e, out e)) { break; } }
			if (loop.Count >= 3) { loops.Add(loop); }
		}
		return loops;
	}

	// ---- curve fitting ----

	/// <summary>A closed outline fitted with cubic curves: split at its corners (or, with none, at two opposite points), each piece fitted on its own.</summary>
	static List<Node> Fit(List<(double x, double y)> outline, Options options, Func<double, double, (double outward, double inward)> limits)
	{
		// evenly spaced points, a pixel apart, so a number of points is a distance along the outline (marching squares bunches them round a corner)
		var d = Resample(Dedupe(outline), 1.0);
		int n = d.Count;
		if (n < 4) { return d.Select(p => new Node(p.x, p.y, 0, 0, 0, 0)).ToList(); }
		// corners: the turn between the points k pixels before and after is sharper than CornerAngle, and the sharpest nearby
		int k = Math.Clamp(n / 16, 2, 4);
		double Turn(int i)
		{
			var (px, py) = d[(i - k + n) % n]; var (qx, qy) = d[i]; var (rx, ry) = d[(i + k) % n];
			double ax = qx - px, ay = qy - py, bx = rx - qx, by = ry - qy;
			double cos = (ax * bx + ay * by) / Math.Max(1e-12, Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by)));
			return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
		}
		double[] turns = Enumerable.Range(0, n).Select(Turn).ToArray();
		// the limits at each point; a turn where the edge is hidden is not kept as a corner (nobody sees it)
		// a small loop (a dot, a catch-light) may only be cut into by a tenth of its size, or a loose inward limit shrinks it to nothing (a colon lost a dot)
		double extent = Math.Min(d.Max(q => q.x) - d.Min(q => q.x), d.Max(q => q.y) - d.Min(q => q.y));
		double inwardCap = Math.Max(Math.Min(options.InwardTolerance, 0.35), 0.1 * extent);
		var allowed = d.Select(q => limits(q.x, q.y)).Select(a => a.inward <= options.InwardTolerance ? (outward: a.outward, inward: Math.Min(a.inward, inwardCap)) : a).ToList();
		bool Hidden(int i) => allowed[i].outward > options.Tolerance && allowed[i].inward > options.InwardTolerance;
		List<int> corners = Enumerable.Range(0, n).Where(i => !Hidden(i) && turns[i] > options.CornerAngle && Enumerable.Range(-k, 2 * k + 1).All(o => o == 0 || turns[(i + o + n) % n] < turns[i] || (turns[(i + o + n) % n] == turns[i] && o > 0))).ToList();
		bool smooth = corners.Count == 0;
		if (smooth) { corners = new() { 0, n / 2 }; }
		else
		{
			// a corner sits where the straight lines of its two sides meet (the blur before tracing rounds it off), if that is near
			var snapped = new List<(double x, double y)>(d);
			foreach (int c in corners)
			{
				int Wrap(int i) => ((i % n) + n) % n;
				var (ax, ay) = d[Wrap(c - 2)]; var (bx, by) = d[Wrap(c - 2 * k - 2)];
				var (cx2, cy2) = d[Wrap(c + 2)]; var (dx2, dy2) = d[Wrap(c + 2 * k + 2)];
				double r1x = ax - bx, r1y = ay - by, r2x = cx2 - dx2, r2y = cy2 - dy2, den = r1x * r2y - r1y * r2x;
				if (Math.Abs(den) < 1e-9) { continue; }
				double t = ((cx2 - ax) * r2y - (cy2 - ay) * r2x) / den;
				(double x, double y) meet = (ax + t * r1x, ay + t * r1y);
				if (Length(Sub(meet, d[c])) < 3) { snapped[c] = meet; }
			}
			d = snapped;
		}
		// the outline smoothed along its length (pixel steps are noise, not shape), the corners held still; the curves may only cut inside it by
		// InwardTolerance (measured against the unsmoothed outline, the curves had to follow every pixel step: 3.4 times the vertices)
		d = SmoothOutline(d, smooth ? new List<int>() : corners);
		var raw = d;
		// outward normals: every outline runs with its inside on the same side, so outward is the direction of travel turned a quarter (test: a disc)
		var normals = Enumerable.Range(0, n).Select(i => { var t = CentreTangent(d, i); return (x: -t.y, y: t.x); }).ToList();
		List<Node> nodes = new();
		for (int c = 0; c < corners.Count; c++)
		{
			int from = corners[c], to = corners[(c + 1) % corners.Count];
			int length = (to - from + n) % n;
			if (length == 0) { length = n; }
			var piece = Enumerable.Range(0, length + 1).Select(o => d[(from + o) % n]).ToList();
			List<T> Along<T>(List<T> values) => Enumerable.Range(0, length + 1).Select(o => values[(from + o) % n]).ToList();
			Piece fit = new(piece, Along(raw), Along(normals), Along(allowed).Select(a => a.outward).ToList(), Along(allowed).Select(a => a.inward).ToList());
			// at a corner, the side's direction from a few points out (right next to it the outline still bends round the rounded-off corner)
			int reach = Math.Min(5, piece.Count - 1);
			(double x, double y) startTangent = smooth ? CentreTangent(d, from) : Normalize(Sub(piece[reach], piece[0]));
			(double x, double y) endTangent = smooth ? Neg(CentreTangent(d, to)) : Normalize(Sub(piece[piece.Count - 1 - reach], piece[^1]));
			List<(double x, double y)[]> curves = new();
			FitCubic(fit, 0, piece.Count - 1, startTangent, endTangent, curves);
			foreach (var bez in curves)
			{
				// the curve leaves bez[0] (its out handle) and arrives at bez[3] (the next node's in handle)
				if (nodes.Count > 0) { nodes[^1] = nodes[^1] with { OutX = bez[1].x - bez[0].x, OutY = bez[1].y - bez[0].y }; }
				else { nodes.Add(new Node(bez[0].x, bez[0].y, 0, 0, bez[1].x - bez[0].x, bez[1].y - bez[0].y)); }
				nodes.Add(new Node(bez[3].x, bez[3].y, bez[2].x - bez[3].x, bez[2].y - bez[3].y, 0, 0));
			}
		}
		// the last node is the first again: give the first its in handle and drop the repeat
		if (nodes.Count > 1)
		{
			Node lastNode = nodes[^1];
			nodes.RemoveAt(nodes.Count - 1);
			nodes[0] = nodes[0] with { InX = lastNode.InX, InY = lastNode.InY };
		}
		return nodes;
	}

	/// <summary>A Gaussian (sigma 2 points, about 1.5 pixels) along a closed outline, the given points held still and not smoothed across.</summary>
	static List<(double x, double y)> SmoothOutline(List<(double x, double y)> d, List<int> fixedPoints)
	{
		int n = d.Count, r = 5;
		double[] w = Enumerable.Range(-r, 2 * r + 1).Select(i => Math.Exp(-i * i / 8.0)).ToArray();
		bool[] isFixed = new bool[n];
		foreach (int i in fixedPoints) { isFixed[i] = true; }
		var result = new List<(double x, double y)>(d);
		for (int i = 0; i < n; i++)
		{
			if (isFixed[i]) { continue; }
			double sx = 0, sy = 0, sw = 0;
			// neighbours up to the nearest fixed point on each side (a fixed point counts, beyond it doesn't)
			for (int side = -1; side <= 1; side += 2)
			{
				for (int o = 1; o <= r; o++)
				{
					int j = ((i + side * o) % n + n) % n;
					sx += d[j].x * w[r + o]; sy += d[j].y * w[r + o]; sw += w[r + o];
					if (isFixed[j]) { break; }
				}
			}
			sx += d[i].x * w[r]; sy += d[i].y * w[r]; sw += w[r];
			result[i] = (sx / sw, sy / sw);
		}
		return result;
	}

	/// <summary>A closed outline resampled to points step apart along it.</summary>
	static List<(double x, double y)> Resample(List<(double x, double y)> d, double step)
	{
		int n = d.Count;
		if (n < 3) { return d; }
		double total = Enumerable.Range(0, n).Sum(i => Length(Sub(d[(i + 1) % n], d[i])));
		int count = Math.Max(3, (int)Math.Round(total / step));
		double spacing = total / count;
		List<(double x, double y)> result = new();
		int segment = 0;
		double segmentStart = 0, segmentLength = Length(Sub(d[1 % n], d[0]));
		for (int i = 0; i < count; i++)
		{
			double at = i * spacing;
			while (segmentStart + segmentLength < at && segment < n - 1) { segmentStart += segmentLength; segment++; segmentLength = Length(Sub(d[(segment + 1) % n], d[segment])); }
			double t = segmentLength < 1e-12 ? 0 : (at - segmentStart) / segmentLength;
			result.Add(Add(d[segment], Mul(Sub(d[(segment + 1) % n], d[segment]), Math.Clamp(t, 0, 1))));
		}
		return result;
	}

	static List<(double x, double y)> Dedupe(List<(double x, double y)> points)
	{
		List<(double x, double y)> result = new();
		foreach (var p in points) { if (result.Count == 0 || Math.Abs(p.x - result[^1].x) + Math.Abs(p.y - result[^1].y) > 1e-6) { result.Add(p); } }
		while (result.Count > 1 && Math.Abs(result[0].x - result[^1].x) + Math.Abs(result[0].y - result[^1].y) <= 1e-6) { result.RemoveAt(result.Count - 1); }
		return result;
	}

	static (double x, double y) Sub((double x, double y) a, (double x, double y) b) => (a.x - b.x, a.y - b.y);
	static (double x, double y) Add((double x, double y) a, (double x, double y) b) => (a.x + b.x, a.y + b.y);
	static (double x, double y) Mul((double x, double y) a, double s) => (a.x * s, a.y * s);
	static (double x, double y) Neg((double x, double y) a) => (-a.x, -a.y);
	static double Dot((double x, double y) a, (double x, double y) b) => a.x * b.x + a.y * b.y;
	static double Length((double x, double y) a) => Math.Sqrt(Dot(a, a));
	static (double x, double y) Normalize((double x, double y) a) { double l = Length(a); return l < 1e-12 ? (1, 0) : (a.x / l, a.y / l); }

	/// <summary>The direction of a closed outline at a point, from the points two either side.</summary>
	static (double x, double y) CentreTangent(List<(double x, double y)> d, int i)
	{
		int n = d.Count;
		return Normalize(Sub(d[(i + 2) % n], d[(i - 2 + n) % n]));
	}

	static (double x, double y) Bezier((double x, double y)[] b, double t)
	{
		double s = 1 - t;
		return (s * s * s * b[0].x + 3 * s * s * t * b[1].x + 3 * s * t * t * b[2].x + t * t * t * b[3].x, s * s * s * b[0].y + 3 * s * s * t * b[1].y + 3 * s * t * t * b[2].y + t * t * t * b[3].y);
	}

	/// <summary>A piece of outline to fit: the points the curves follow, the ones they mustn't cut inside (the same smoothed points, kept apart so the
	/// two can differ), the outward normals, and the two tolerances.</summary>
	record Piece(List<(double x, double y)> Points, List<(double x, double y)> Raw, List<(double x, double y)> Normals, List<double> Outward, List<double> Inward);

	/// <summary>Schneider's fit of points first to last with cubics, the end tangents given (the start one pointing along the curve, the end one back along it).</summary>
	static void FitCubic(Piece piece, int first, int last, (double x, double y) tHat1, (double x, double y) tHat2, List<(double x, double y)[]> output)
	{
		var d = piece.Points;
		if (last - first == 1)
		{
			double dist = Length(Sub(d[last], d[first])) / 3;
			output.Add(new[] { d[first], Add(d[first], Mul(tHat1, dist)), Add(d[last], Mul(tHat2, dist)), d[last] });
			return;
		}
		// a piece that is straight to within half the tolerance (and doesn't cut in) is a straight line: handles on the ends (written as plain points)
		double[] u = ChordLengths(d, first, last);
		(double x, double y)[] line = { d[first], d[first], d[last], d[last] };
		if (MaxError(piece, first, last, line, u, linear: true).maxError < 0.5)
		{
			output.Add(line);
			return;
		}
		var bez = GenerateBezier(d, first, last, u, tHat1, tHat2);
		var (maxError, split) = MaxError(piece, first, last, bez, u);
		if (maxError < 1) { output.Add(bez); return; }
		if (maxError < 2)
		{
			for (int i = 0; i < 6; i++)
			{
				u = Reparameterize(d, first, last, u, bez);
				bez = GenerateBezier(d, first, last, u, tHat1, tHat2);
				(maxError, split) = MaxError(piece, first, last, bez, u);
				if (maxError < 1) { output.Add(bez); return; }
			}
		}
		split = Math.Clamp(split, first + 1, last - 1);
		var centre = Normalize(Sub(d[Math.Max(first, split - 1)], d[Math.Min(last, split + 1)]));
		FitCubic(piece, first, split, tHat1, centre, output);
		FitCubic(piece, split, last, Neg(centre), tHat2, output);
	}

	static double[] ChordLengths(List<(double x, double y)> d, int first, int last)
	{
		double[] u = new double[last - first + 1];
		for (int i = first + 1; i <= last; i++) { u[i - first] = u[i - first - 1] + Length(Sub(d[i], d[i - 1])); }
		for (int i = 1; i < u.Length; i++) { u[i] /= u[^1]; }
		return u;
	}

	static (double x, double y)[] GenerateBezier(List<(double x, double y)> d, int first, int last, double[] u, (double x, double y) tHat1, (double x, double y) tHat2)
	{
		double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
		var p0 = d[first]; var p3 = d[last];
		for (int i = 0; i < u.Length; i++)
		{
			double t = u[i], s = 1 - t, b0 = s * s * s, b1 = 3 * s * s * t, b2 = 3 * s * t * t, b3 = t * t * t;
			var a1 = Mul(tHat1, b1); var a2 = Mul(tHat2, b2);
			c00 += Dot(a1, a1); c01 += Dot(a1, a2); c11 += Dot(a2, a2);
			var tmp = Sub(d[first + i], Add(Mul(p0, b0 + b1), Mul(p3, b2 + b3)));
			x0 += Dot(a1, tmp); x1 += Dot(a2, tmp);
		}
		double det = c00 * c11 - c01 * c01;
		double alpha1 = Math.Abs(det) < 1e-12 ? 0 : (x0 * c11 - x1 * c01) / det;
		double alpha2 = Math.Abs(det) < 1e-12 ? 0 : (c00 * x1 - c01 * x0) / det;
		double segment = Length(Sub(p3, p0)), epsilon = 1e-6 * segment;
		if (alpha1 < epsilon || alpha2 < epsilon) { alpha1 = alpha2 = segment / 3; }
		return new[] { p0, Add(p0, Mul(tHat1, alpha1)), Add(p3, Mul(tHat2, alpha2)), p3 };
	}

	/// <summary>
	/// The worst point of a fit, as a share of what is allowed (under 1 fits): the distance from the smoothed point over the outward tolerance, or how far
	/// the curve is inside the unsmoothed point (along the outward normal) over the inward tolerance. A straight line is measured by distance to the line
	/// (its parameters don't follow the points).
	/// </summary>
	static (double maxError, int split) MaxError(Piece piece, int first, int last, (double x, double y)[] bez, double[] u, bool linear = false)
	{
		double max = 0;
		int split = (first + last) / 2;
		var chord = Sub(bez[3], bez[0]);
		double chordLength = Length(chord);
		for (int i = first + 1; i < last; i++)
		{
			(double x, double y) at = Bezier(bez, u[i - first]);
			if (linear && chordLength > 1e-9)
			{
				// the foot of the point on the line
				double t = Math.Clamp(Dot(Sub(piece.Points[i], bez[0]), chord) / (chordLength * chordLength), 0, 1);
				at = Add(bez[0], Mul(chord, t));
			}
			double outward = Length(Sub(at, piece.Points[i])) / piece.Outward[i];
			double inside = -Dot(Sub(at, piece.Raw[i]), piece.Normals[i]);
			double e = Math.Max(outward, inside > 0 ? inside / piece.Inward[i] : 0);
			if (e >= max) { max = e; split = i; }
		}
		return (max, split);
	}

	static double[] Reparameterize(List<(double x, double y)> d, int first, int last, double[] u, (double x, double y)[] bez)
	{
		double[] result = new double[u.Length];
		for (int i = 0; i < u.Length; i++)
		{
			// one Newton step towards the curve point nearest the data point
			double t = u[i];
			var q = Bezier(bez, t);
			(double x, double y)[] d1 = { Mul(Sub(bez[1], bez[0]), 3), Mul(Sub(bez[2], bez[1]), 3), Mul(Sub(bez[3], bez[2]), 3) };
			(double x, double y)[] d2 = { Mul(Sub(d1[1], d1[0]), 2), Mul(Sub(d1[2], d1[1]), 2) };
			double s = 1 - t;
			var q1 = Add(Add(Mul(d1[0], s * s), Mul(d1[1], 2 * s * t)), Mul(d1[2], t * t));
			var q2 = Add(Mul(d2[0], s), Mul(d2[1], t));
			var diff = Sub(q, d[first + i]);
			double numerator = Dot(diff, q1), denominator = Dot(q1, q1) + Dot(diff, q2);
			result[i] = Math.Abs(denominator) < 1e-12 ? t : Math.Clamp(t - numerator / denominator, 0, 1);
		}
		return result;
	}

	// ---- OKLab ----

	static double ToLinear(double c) { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
	static int FromLinear(double c) { c = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(Math.Max(c, 0), 1 / 2.4) - 0.055; return Math.Clamp((int)Math.Round(c * 255), 0, 255); }

	public static (double L, double a, double b) ToLab(int rgb)
	{
		double r = ToLinear((rgb >> 16) & 0xFF), g = ToLinear((rgb >> 8) & 0xFF), b = ToLinear(rgb & 0xFF);
		double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
		double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
		double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
		return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
	}

	public static int FromLab((double L, double a, double b) c)
	{
		double l = c.L + 0.3963377774 * c.a + 0.2158037573 * c.b, m = c.L - 0.1055613458 * c.a - 0.0638541728 * c.b, s = c.L - 0.0894841775 * c.a - 1.2914855480 * c.b;
		l = l * l * l; m = m * m * m; s = s * s * s;
		int r = FromLinear(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s), g = FromLinear(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s), b = FromLinear(-0.0041960863 * l - 0.7034186147 * m + 1.7076412211 * s);
		return (r << 16) | (g << 8) | b;
	}
}
