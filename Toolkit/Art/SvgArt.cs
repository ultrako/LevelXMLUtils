using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LevelXML;

namespace LevelXMLUtils;

/// <summary>
/// Vector drawings (SVG, for example from Inkscape) as art shapes: each filled path is one art shape, in document order (the order they are drawn). The
/// game joins an art shape's vertices with cubic curves, a vertex carrying its incoming and outgoing handles relative to itself (see VectorTrace), so a
/// path's curves go in as they are: a cubic segment P0 C1 C2 P3 gives P0 the outgoing handle C1 - P0 and P3 the incoming handle C2 - P3, a straight segment
/// no handles, a quadratic one is raised to a cubic, and an arc becomes cubics of at most 90 degrees each (A2C, after fontello's svgpath, MIT licensed). Transforms (matrix, translate, scale) on paths and groups are applied. Also ellipses, circles, rectangles (rounded too), lines, polylines and polygons. A stroke becomes the shape's outline colour, which the game always draws one
/// screen pixel wide (its stroke width is not kept); an unfilled shape is outline only, and an unfilled open path a line (written open, f="f"). What it
/// doesn't take: gradients, filters, masks, clipping, text; images (a reference sketch) are skipped. A path of
/// several pieces is joined through its middle and back, as VectorTrace does.
/// </summary>
public class SvgArt
{
	/// <summary>A path: its fill (-1 for none), its outline (-1 for none; the game draws an outline one screen pixel wide, whatever the stroke width), and its
	/// pieces, each closed or open (an open piece is drawn as a line, never filled).</summary>
	public record Piece(int Color, int Outline, double Opacity, List<List<VectorTrace.Node>> Loops, List<bool> Closed, string Id);
	public readonly List<Piece> Pieces = new();
	public readonly double Width, Height;

	public SvgArt(string path)
	{
		XDocument doc = XDocument.Load(path);
		XElement svg = doc.Root!;
		string[] box = (svg.Attribute("viewBox")?.Value ?? $"0 0 {svg.Attribute("width")!.Value} {svg.Attribute("height")!.Value}")
			.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
		Width = Num(box[2]); Height = Num(box[3]);
		Walk(svg, Matrix.Identity, 1);
	}

	void Walk(XElement element, Matrix parent, double parentOpacity)
	{
		foreach (XElement child in element.Elements())
		{
			string tag = child.Name.LocalName;
			Matrix m = parent * Transform(child.Attribute("transform")?.Value);
			Dictionary<string, string> style = Style(child);
			double opacity = parentOpacity * (style.TryGetValue("opacity", out string? o) ? Num(o) : 1);
			if (style.TryGetValue("display", out string? display) && display == "none") { continue; }
			if (tag == "g") { Walk(child, m, opacity); continue; }
			if (PathData(child) is not string d) { continue; } // images (a reference sketch), text and the rest
			// SVG's default fill is black; a fill of "none" (or a gradient, which the game can't draw) leaves only the outline
			string fill = style.TryGetValue("fill", out string? f) ? f : "#000000";
			string stroke = style.TryGetValue("stroke", out string? s) ? s : "none";
			int color = fill.StartsWith('#') ? ParseColor(fill) : -1, outline = stroke.StartsWith('#') ? ParseColor(stroke) : -1;
			if (color < 0 && outline < 0) { continue; }
			double alpha = color >= 0 ? (style.TryGetValue("fill-opacity", out string? fo) ? Num(fo) : 1) : (style.TryGetValue("stroke-opacity", out string? so) ? Num(so) : 1);
			var closed = new List<bool>();
			List<List<VectorTrace.Node>> loops = ParsePath(d, closed).Select(loop => loop.Select(n => m.Apply(n)).ToList()).ToList();
			// an open piece of a filled path is filled as if closed (SVG fills it so), but an unfilled one stays a line
			if (color >= 0) { closed = closed.Select(_ => true).ToList(); }
			Pieces.Add(new Piece(color, outline, opacity * alpha * 100, loops, closed, child.Attribute("id")?.Value ?? ""));
		}
	}

	/// <summary>
	/// Fewer points for the same outline, to within <paramref name="tolerance"/> units of the original everywhere (always measured against the original
	/// path, so errors can't pile up): a curve that stays that close to its chord becomes a straight segment (no handles: a vertex with no handles is two
	/// numbers in the level instead of six); a point whose handles can both be dropped becomes a corner; and then, repeatedly, the point whose two curves one
	/// curve replaces most closely is removed. The replacement keeps the directions its ends leave in (so smooth joins stay smooth) and finds the handle
	/// lengths by least squares on points of the original (Schneider, as VectorTrace fits), improved by a few Newton steps on the samples' positions along it.
	/// </summary>
	public static List<VectorTrace.Node> Simplify(List<VectorTrace.Node> loop, bool closed, double tolerance)
	{
		var nodes = loop.ToList();
		int Segs() => closed ? nodes.Count : nodes.Count - 1;
		// the original outline's samples, per current segment
		const int PerSegment = 24;
		var samples = new List<List<(double x, double y)>>();
		for (int i = 0; i < Segs(); i++) { var c = Controls(nodes, i); samples.Add(Enumerable.Range(1, PerSegment - 1).Select(s => At(c, s / (double)PerSegment)).ToList()); }
		double Error((double x, double y)[] c, IEnumerable<(double x, double y)> points) => points.Max(pt => DistanceToCurve(c, pt));

		// 1. nearly straight segments become straight
		for (int i = 0; i < Segs(); i++)
		{
			var c = Controls(nodes, i);
			(double x, double y)[] line = { c[0], c[0], c[3], c[3] };
			if (Error(line, samples[i]) <= tolerance) { SetSegment(nodes, i, line); }
		}
		// 2. points whose handles can both go
		for (int k = 0; k < nodes.Count; k++)
		{
			var n = nodes[k];
			if (n.InX == 0 && n.InY == 0 && n.OutX == 0 && n.OutY == 0) { continue; }
			int before = (k - 1 + nodes.Count) % nodes.Count; bool hasBefore = closed || k > 0, hasAfter = closed || k < nodes.Count - 1;
			var trial = nodes.ToList(); trial[k] = n with { InX = 0, InY = 0, OutX = 0, OutY = 0 };
			bool ok = (!hasBefore || Error(Controls(trial, before), samples[before]) <= tolerance) && (!hasAfter || Error(Controls(trial, k), samples[k]) <= tolerance);
			if (ok) { nodes = trial; }
		}
		// 3. remove points while some two neighbouring curves can become one
		while (nodes.Count > (closed ? 3 : 2))
		{
			double best = double.MaxValue; int bestK = -1; (double x, double y)[]? bestCurve = null;
			for (int k = closed ? 0 : 1; k < (closed ? nodes.Count : nodes.Count - 1); k++)
			{
				int a = (k - 1 + nodes.Count) % nodes.Count, b = (k + 1) % nodes.Count;
				// the original points this merged curve must follow: the two segments' samples and the point itself
				var pts = samples[a].Append((nodes[k].X, nodes[k].Y)).Concat(samples[k]).ToList();
				var curve = Merge(nodes[a], nodes[b], pts);
				double error = Error(curve, pts);
				if (error < best) { best = error; bestK = k; bestCurve = curve; }
			}
			if (best > tolerance) { break; }
			int ka = (bestK - 1 + nodes.Count) % nodes.Count;
			var merged = samples[ka].Append((nodes[bestK].X, nodes[bestK].Y)).Concat(samples[bestK]).ToList();
			nodes.RemoveAt(bestK);
			samples.RemoveAt(bestK);
			int seg = bestK > 0 ? bestK - 1 : (closed ? nodes.Count - 1 : 0);
			samples[seg] = merged;
			SetSegment(nodes, seg, bestCurve!);
		}
		return nodes;
	}

	// segment i's four control points (from node i to the next)
	static (double x, double y)[] Controls(List<VectorTrace.Node> nodes, int i)
	{
		var p = nodes[i]; var q = nodes[(i + 1) % nodes.Count];
		return new[] { (p.X, p.Y), (p.X + p.OutX, p.Y + p.OutY), (q.X + q.InX, q.Y + q.InY), (q.X, q.Y) };
	}

	static void SetSegment(List<VectorTrace.Node> nodes, int i, (double x, double y)[] c)
	{
		int j = (i + 1) % nodes.Count;
		nodes[i] = nodes[i] with { OutX = c[1].x - c[0].x, OutY = c[1].y - c[0].y };
		nodes[j] = nodes[j] with { InX = c[2].x - c[3].x, InY = c[2].y - c[3].y };
	}

	static (double x, double y) At((double x, double y)[] c, double t)
	{
		double u = 1 - t, a = u * u * u, b = 3 * u * u * t, d = 3 * u * t * t, e = t * t * t;
		return (a * c[0].x + b * c[1].x + d * c[2].x + e * c[3].x, a * c[0].y + b * c[1].y + d * c[2].y + e * c[3].y);
	}

	static double DistanceToCurve((double x, double y)[] c, (double x, double y) pt)
	{
		// coarse search, then refine around the best
		double bestT = 0, best = double.MaxValue;
		for (int s = 0; s <= 64; s++) { var q = At(c, s / 64.0); double d = (q.x - pt.x) * (q.x - pt.x) + (q.y - pt.y) * (q.y - pt.y); if (d < best) { best = d; bestT = s / 64.0; } }
		double lo = Math.Max(0, bestT - 1 / 64.0), hi = Math.Min(1, bestT + 1 / 64.0);
		for (int s = 0; s <= 32; s++) { double t = lo + (hi - lo) * s / 32; var q = At(c, t); double d = (q.x - pt.x) * (q.x - pt.x) + (q.y - pt.y) * (q.y - pt.y); if (d < best) { best = d; } }
		return Math.Sqrt(best);
	}

	// One cubic from p to q through the points (in order), leaving p and arriving at q in the directions their current handles have (or towards the
	// nearest point where a handle is zero)
	static (double x, double y)[] Merge(VectorTrace.Node p, VectorTrace.Node q, List<(double x, double y)> pts)
	{
		(double x, double y) P0 = (p.X, p.Y), P3 = (q.X, q.Y);
		(double x, double y) Unit(double x, double y) { double l = Math.Sqrt(x * x + y * y); return l < 1e-12 ? (0, 0) : (x / l, y / l); }
		var t1 = Unit(p.OutX, p.OutY); if (t1 == (0, 0)) { t1 = Unit(pts[0].x - P0.x, pts[0].y - P0.y); }
		var t2 = Unit(q.InX, q.InY); if (t2 == (0, 0)) { t2 = Unit(pts[^1].x - P3.x, pts[^1].y - P3.y); }
		var all = new List<(double x, double y)> { P0 }; all.AddRange(pts); all.Add(P3);
		double[] u = new double[all.Count];
		for (int i = 1; i < all.Count; i++) { u[i] = u[i - 1] + Math.Sqrt(Math.Pow(all[i].x - all[i - 1].x, 2) + Math.Pow(all[i].y - all[i - 1].y, 2)); }
		double total = u[^1]; if (total <= 0) { return new[] { P0, P0, P3, P3 }; }
		for (int i = 0; i < u.Length; i++) { u[i] /= total; }
		(double x, double y)[] curve = Fit(all, u, P0, P3, t1, t2, total);
		for (int iteration = 0; iteration < 4; iteration++)
		{
			// Newton step on each sample's parameter
			for (int i = 1; i < all.Count - 1; i++)
			{
				double t = u[i], v = 1 - t;
				var q0 = At(curve, t);
				(double x, double y) d1 = (3 * (v * v * (curve[1].x - curve[0].x) + 2 * v * t * (curve[2].x - curve[1].x) + t * t * (curve[3].x - curve[2].x)),
					3 * (v * v * (curve[1].y - curve[0].y) + 2 * v * t * (curve[2].y - curve[1].y) + t * t * (curve[3].y - curve[2].y)));
				(double x, double y) d2 = (6 * (v * (curve[2].x - 2 * curve[1].x + curve[0].x) + t * (curve[3].x - 2 * curve[2].x + curve[1].x)),
					6 * (v * (curve[2].y - 2 * curve[1].y + curve[0].y) + t * (curve[3].y - 2 * curve[2].y + curve[1].y)));
				double num = (q0.x - all[i].x) * d1.x + (q0.y - all[i].y) * d1.y;
				double den = d1.x * d1.x + d1.y * d1.y + (q0.x - all[i].x) * d2.x + (q0.y - all[i].y) * d2.y;
				if (Math.Abs(den) > 1e-12) { u[i] = Math.Clamp(t - num / den, 0, 1); }
			}
			curve = Fit(all, u, P0, P3, t1, t2, total);
		}
		return curve;
	}

	// least squares handle lengths along fixed directions (Schneider's GenerateBezier); a bad solve falls back to a third of the chord
	static (double x, double y)[] Fit(List<(double x, double y)> d, double[] u, (double x, double y) P0, (double x, double y) P3,
		(double x, double y) t1, (double x, double y) t2, double total)
	{
		double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
		for (int i = 0; i < d.Count; i++)
		{
			double t = u[i], v = 1 - t, b0 = v * v * v, b1 = 3 * v * v * t, b2 = 3 * v * t * t, b3 = t * t * t;
			(double x, double y) a1 = (t1.x * b1, t1.y * b1), a2 = (t2.x * b2, t2.y * b2);
			c00 += a1.x * a1.x + a1.y * a1.y; c01 += a1.x * a2.x + a1.y * a2.y; c11 += a2.x * a2.x + a2.y * a2.y;
			(double x, double y) tmp = (d[i].x - (P0.x * (b0 + b1) + P3.x * (b2 + b3)), d[i].y - (P0.y * (b0 + b1) + P3.y * (b2 + b3)));
			x0 += a1.x * tmp.x + a1.y * tmp.y; x1 += a2.x * tmp.x + a2.y * tmp.y;
		}
		double det = c00 * c11 - c01 * c01;
		double alpha1 = Math.Abs(det) < 1e-12 ? 0 : (x0 * c11 - x1 * c01) / det, alpha2 = Math.Abs(det) < 1e-12 ? 0 : (c00 * x1 - c01 * x0) / det;
		double chord = Math.Sqrt(Math.Pow(P3.x - P0.x, 2) + Math.Pow(P3.y - P0.y, 2));
		if (alpha1 < 1e-6 * total || alpha2 < 1e-6 * total || alpha1 > 3 * total || alpha2 > 3 * total) { alpha1 = alpha2 = chord / 3; }
		return new[] { P0, (P0.x + t1.x * alpha1, P0.y + t1.y * alpha1), (P3.x + t2.x * alpha2, P3.y + t2.y * alpha2), P3 };
	}

	// A shape element as path data
	static string? PathData(XElement e)
	{
		string N(string name, string fallback = "0") => e.Attribute(name)?.Value ?? fallback;
		double V(string name) => Num(N(name));
		string F(double x) => x.ToString("R", CultureInfo.InvariantCulture);
		switch (e.Name.LocalName)
		{
			case "path": return N("d", "");
			case "line": return $"M {N("x1")} {N("y1")} L {N("x2")} {N("y2")}";
			case "polyline": return "M " + N("points", "");
			case "polygon": return "M " + N("points", "") + " Z";
			case "circle": case "ellipse":
			{
				double cx = V("cx"), cy = V("cy"), rx = e.Name.LocalName == "circle" ? V("r") : V("rx"), ry = e.Name.LocalName == "circle" ? V("r") : V("ry");
				// two half arcs
				return $"M {F(cx - rx)} {F(cy)} A {F(rx)} {F(ry)} 0 0 1 {F(cx + rx)} {F(cy)} A {F(rx)} {F(ry)} 0 0 1 {F(cx - rx)} {F(cy)} Z";
			}
			case "rect":
			{
				double x = V("x"), y = V("y"), w = V("width"), h = V("height");
				double rx = e.Attribute("rx") is null ? (e.Attribute("ry") is null ? 0 : V("ry")) : V("rx"), ry = e.Attribute("ry") is null ? rx : V("ry");
				rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
				if (rx <= 0 || ry <= 0) { return $"M {F(x)} {F(y)} H {F(x + w)} V {F(y + h)} H {F(x)} Z"; }
				string A(double tx, double ty) => $"A {F(rx)} {F(ry)} 0 0 1 {F(tx)} {F(ty)}";
				return $"M {F(x + rx)} {F(y)} H {F(x + w - rx)} {A(x + w, y + ry)} V {F(y + h - ry)} {A(x + w - rx, y + h)} H {F(x + rx)} {A(x, y + h - ry)} V {F(y + ry)} {A(x + rx, y)} Z";
			}
			default: return null;
		}
	}

	/// <summary>Simplification never strays further than this share of a loop's size, whatever tolerance is asked for.</summary>
	public const double RelativeTolerance = 0.05;

	/// <summary>The pieces as art shapes, the drawing <paramref name="width"/> units wide, its top left at (<paramref name="left"/>, <paramref name="top"/>).</summary>
	/// <param name="precision">Each vertex and handle is rounded to this share of its shape's box (the level stores them as a share of the box times 100,
	/// so 0.001 leaves one decimal: about 1.6 KB less for the bonus art, and at most half a pixel on its biggest pieces).</param>
	/// <param name="tolerance">If above 0, each piece is simplified (Simplify) to within this many units of the original outline.</param>
	public List<Art> ToShapes(double width, double left, double top, double precision = 0.001, double tolerance = 0)
	{
		double scale = width / Width;
		List<Art> shapes = new();
		foreach (Piece piece in Pieces)
		{
			List<List<VectorTrace.Node>> loops = piece.Loops.Select(loop => loop.Select(n =>
				new VectorTrace.Node(left + n.X * scale, top + n.Y * scale, n.InX * scale, n.InY * scale, n.OutX * scale, n.OutY * scale)).ToList()).ToList();
			if (tolerance > 0)
			{
				// a small shape keeps its shape: at most RelativeTolerance of its smaller side (a fixed pixel tolerance turned the eye highlights' circles into
				// rounded triangles)
				double Size(List<VectorTrace.Node> l) => Math.Min(l.Max(n => n.X) - l.Min(n => n.X), l.Max(n => n.Y) - l.Min(n => n.Y));
				loops = loops.Select((loop, k) => Simplify(loop, piece.Closed[k], Math.Min(tolerance, RelativeTolerance * Math.Max(Size(loop), 1e-9)))).ToList();
			}
			// closed pieces together in one shape (joined through its middle); each open piece a shape of its own, since the joins would be drawn
			var groups = new List<(List<List<VectorTrace.Node>> loops, bool closed)>();
			var closedLoops = loops.Where((_, k) => piece.Closed[k]).ToList();
			if (closedLoops.Count > 0) { groups.Add((closedLoops, true)); }
			groups.AddRange(loops.Where((_, k) => !piece.Closed[k]).Select(l => (new List<List<VectorTrace.Node>> { l }, false)));
			foreach (var (group, closed) in groups) { shapes.Add(Shape(group, closed, piece, precision)); }
		}
		return shapes;
	}

	static Art Shape(List<List<VectorTrace.Node>> loops, bool closed, Piece piece, double precision)
	{
		var all = loops.SelectMany(l => l).ToList();
		double minX = all.Min(n => n.X), maxX = all.Max(n => n.X), minY = all.Min(n => n.Y), maxY = all.Max(n => n.Y);
		double midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;
		// the game fits the vertices' span to the box, so a box of no width or height (a straight horizontal or vertical line) can't be drawn: give it a
		// sliver, out of the first vertex
		const double Sliver = 0.05;
		bool flatX = maxX - minX < Sliver, flatY = maxY - minY < Sliver;
		Art art = new() { X = midX, Y = midY, Width = Math.Max(maxX - minX, Sliver), Height = Math.Max(maxY - minY, Sliver), FillColor = closed ? piece.Color : -1, OutlineColor = piece.Outline,
			Opacity = Math.Round(piece.Opacity) };
		bool firstVertex = true;
		Vertex V(VectorTrace.Node n, bool keepIn = true, bool keepOut = true)
		{
			double inX = keepIn ? n.InX : 0, inY = keepIn ? n.InY : 0, outX = keepOut ? n.OutX : 0, outY = keepOut ? n.OutY : 0;
			double px = n.X - midX, py = n.Y - midY;
			if (firstVertex) { if (flatX) { px -= Sliver / 2; } if (flatY) { py -= Sliver / 2; } }
			firstVertex = false;
			if (flatX && px == 0) { px = Sliver / 2; }
			if (flatY && py == 0) { py = Sliver / 2; }
			double stepX = art.Width * precision, stepY = art.Height * precision;
			double RX(double v) => Math.Round(v / stepX) * stepX;
			double RY(double v) => Math.Round(v / stepY) * stepY;
			(px, py, inX, inY, outX, outY) = (RX(px), RY(py), RX(inX), RY(inY), RX(outX), RY(outY));
			Point p = new(px, py);
			return inX == 0 && inY == 0 && outX == 0 && outY == 0 ? new Vertex(p) : new Vertex(p, new Point(inX, inY), new Point(outX, outY));
		}
		if (loops.Count == 1) { foreach (var n in loops[0]) { art.Vertices.Add(V(n)); } }
		else
		{
			firstVertex = false;
			art.Vertices.Add(new Vertex(new Point(0, 0)));
			foreach (var loop in loops)
			{
				art.Vertices.Add(V(loop[0], keepIn: false));
				foreach (var n in loop.Skip(1)) { art.Vertices.Add(V(n)); }
				art.Vertices.Add(V(loop[0], keepOut: false));
				art.Vertices.Add(new Vertex(new Point(0, 0)));
			}
		}
		if (!closed) { art.Closed = false; }
		return art;
	}

	/// <summary>A path's pieces as nodes (handles relative to their node). A piece ended by Z has its last point merged with its first when they meet.</summary>
	public static List<List<VectorTrace.Node>> ParsePath(string d) => ParsePath(d, new List<bool>());

	/// <summary>The same, with whether each piece was closed (Z) in <paramref name="closed"/>.</summary>
	public static List<List<VectorTrace.Node>> ParsePath(string d, List<bool> closed)
	{
		var tokens = Regex.Matches(d, @"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Select(t => t.Value).ToList();
		var loops = new List<List<VectorTrace.Node>>();
		List<VectorTrace.Node>? loop = null;
		double cx = 0, cy = 0, startX = 0, startY = 0, lastC2X = 0, lastC2Y = 0;
		char command = ' ', previous = ' ';
		int i = 0;
		double Next() => Num(tokens[i++]);
		// an arc's flag is one digit, and compact SVG writes them packed into the next number ("a5 5 0 011 1" or "0110,10")
		int Flag()
		{
			string t = tokens[i];
			if (t.Length > 1 && (t[0] == '0' || t[0] == '1')) { tokens[i] = t[1..]; return t[0] - '0'; }
			i++;
			return t == "0" ? 0 : 1;
		}
		bool HasNumber() => i < tokens.Count && !char.IsLetter(tokens[i][0]);
		void SetOut(double ox, double oy) { var last = loop![^1]; loop[^1] = last with { OutX = ox - last.X, OutY = oy - last.Y }; }
		void Add(double x, double y, double inX, double inY) => loop!.Add(new VectorTrace.Node(x, y, inX - x, inY - y, 0, 0));
		void Close(bool byZ)
		{
			if (loop is null || loop.Count == 0) { return; }
			if (loop.Count == 1) { loop = null; return; } // a lone point draws nothing
			closed.Add(byZ);
			// the last point back on the first: merge them (the first keeps its outgoing handle and takes the last's incoming one)
			if (byZ && loop.Count > 1 && Math.Abs(loop[^1].X - loop[0].X) < 1e-9 && Math.Abs(loop[^1].Y - loop[0].Y) < 1e-9)
			{
				loop[0] = loop[0] with { InX = loop[^1].InX, InY = loop[^1].InY };
				loop.RemoveAt(loop.Count - 1);
			}
			loops.Add(loop);
			loop = null;
		}
		while (i < tokens.Count)
		{
			if (char.IsLetter(tokens[i][0])) { command = tokens[i++][0]; }
			bool rel = char.IsLower(command);
			double ox = rel ? cx : 0, oy = rel ? cy : 0;
			switch (char.ToUpper(command))
			{
				case 'M':
					Close(false);
					cx = ox + Next(); cy = oy + Next(); startX = cx; startY = cy;
					loop = new() { new VectorTrace.Node(cx, cy, 0, 0, 0, 0) };
					command = rel ? 'l' : 'L'; // further pairs are lines
					previous = 'M';
					continue;
				case 'L': { double x = ox + Next(), y = oy + Next(); Add(x, y, x, y); cx = x; cy = y; break; }
				case 'H': { double x = ox + Next(); Add(x, cy, x, cy); cx = x; break; }
				case 'V': { double y = (rel ? cy : 0) + Next(); Add(cx, y, cx, y); cy = y; break; }
				case 'C':
				{
					double c1x = ox + Next(), c1y = oy + Next(), c2x = ox + Next(), c2y = oy + Next(), x = ox + Next(), y = oy + Next();
					SetOut(c1x, c1y); Add(x, y, c2x, c2y); lastC2X = c2x; lastC2Y = c2y; cx = x; cy = y; break;
				}
				case 'S':
				{
					bool smooth = char.ToUpper(previous) is 'C' or 'S';
					double c1x = smooth ? 2 * cx - lastC2X : cx, c1y = smooth ? 2 * cy - lastC2Y : cy;
					double c2x = ox + Next(), c2y = oy + Next(), x = ox + Next(), y = oy + Next();
					SetOut(c1x, c1y); Add(x, y, c2x, c2y); lastC2X = c2x; lastC2Y = c2y; cx = x; cy = y; break;
				}
				case 'Q':
				{
					double qx = ox + Next(), qy = oy + Next(), x = ox + Next(), y = oy + Next();
					SetOut(cx + 2.0 / 3 * (qx - cx), cy + 2.0 / 3 * (qy - cy)); Add(x, y, x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y)); cx = x; cy = y; break;
				}
				case 'Z':
					Close(true); cx = startX; cy = startY; previous = 'Z';
					// a new piece may start without an M: from the start point
					if (HasNumber()) { loop = new() { new VectorTrace.Node(cx, cy, 0, 0, 0, 0) }; }
					continue;
				case 'A':
				{
					double rx = Next(), ry = Next(), phi = Next();
					int largeArc = Flag(), sweep = Flag();
					double x = ox + Next(), y = oy + Next();
					var curves = A2C(cx, cy, x, y, largeArc, sweep, rx, ry, phi);
					if (curves.Count == 0) { Add(x, y, x, y); } // a zero radius or no length: a straight line, as SVG draws it
					foreach (double[] c in curves) { SetOut(c[2], c[3]); Add(c[6], c[7], c[4], c[5]); }
					// the last point exactly the arc's end
					if (curves.Count > 0) { loop![^1] = loop[^1] with { X = x, Y = y, InX = loop[^1].InX + loop[^1].X - x, InY = loop[^1].InY + loop[^1].Y - y }; }
					cx = x; cy = y; break;
				}
				default: throw new NotSupportedException($"SVG path command {command}");
			}
			previous = command;
		}
		Close(false);
		return loops;
	}

	/// <summary>
	/// An SVG arc from (x1, y1) to (x2, y2) as cubic Béziers (each [x0, y0, c1x, c1y, c2x, c2y, x3, y3]), none over 90 degrees: the endpoint form turned into
	/// the centre form (SVG 1.1 implementation notes, F.6.5), radii too small for the endpoints grown to fit, then each piece of a unit circle approximated with
	/// handles 4/3 tan(angle / 4) long and mapped back onto the ellipse. Ported from the a2c of fontello's svgpath (https://github.com/fontello/svgpath, MIT licence).
	/// </summary>
	public static List<double[]> A2C(double x1, double y1, double x2, double y2, int largeArc, int sweep, double rx, double ry, double phiDegrees)
	{
		var result = new List<double[]>();
		double sinPhi = Math.Sin(phiDegrees * Math.PI / 180), cosPhi = Math.Cos(phiDegrees * Math.PI / 180);
		double x1p = cosPhi * (x1 - x2) / 2 + sinPhi * (y1 - y2) / 2, y1p = -sinPhi * (x1 - x2) / 2 + cosPhi * (y1 - y2) / 2;
		if ((x1p == 0 && y1p == 0) || rx == 0 || ry == 0) { return result; }
		rx = Math.Abs(rx); ry = Math.Abs(ry);
		double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
		if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
		// the centre
		double rxSq = rx * rx, rySq = ry * ry, x1pSq = x1p * x1p, y1pSq = y1p * y1p;
		double radicant = Math.Max(0, rxSq * rySq - rxSq * y1pSq - rySq * x1pSq) / (rxSq * y1pSq + rySq * x1pSq);
		radicant = Math.Sqrt(radicant) * (largeArc == sweep ? -1 : 1);
		double cxp = radicant * rx / ry * y1p, cyp = radicant * -ry / rx * x1p;
		double centreX = cosPhi * cxp - sinPhi * cyp + (x1 + x2) / 2, centreY = sinPhi * cxp + cosPhi * cyp + (y1 + y2) / 2;
		static double Angle(double ux, double uy, double vx, double vy) =>
			(ux * vy - uy * vx < 0 ? -1 : 1) * Math.Acos(Math.Clamp(ux * vx + uy * vy, -1, 1));
		double v1x = (x1p - cxp) / rx, v1y = (y1p - cyp) / ry, v2x = (-x1p - cxp) / rx, v2y = (-y1p - cyp) / ry;
		double theta = Angle(1, 0, v1x, v1y), delta = Angle(v1x, v1y, v2x, v2y);
		if (sweep == 0 && delta > 0) { delta -= 2 * Math.PI; }
		if (sweep == 1 && delta < 0) { delta += 2 * Math.PI; }
		int segments = Math.Max((int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2)), 1);
		delta /= segments;
		for (int s = 0; s < segments; s++)
		{
			double alpha = 4.0 / 3 * Math.Tan(delta / 4);
			double ax = Math.Cos(theta), ay = Math.Sin(theta), bx = Math.Cos(theta + delta), by = Math.Sin(theta + delta);
			double[] unit = { ax, ay, ax - ay * alpha, ay + ax * alpha, bx + by * alpha, by - bx * alpha, bx, by };
			double[] mapped = new double[8];
			for (int k = 0; k < 8; k += 2)
			{
				double px = unit[k] * rx, py = unit[k + 1] * ry;
				mapped[k] = cosPhi * px - sinPhi * py + centreX;
				mapped[k + 1] = sinPhi * px + cosPhi * py + centreY;
			}
			result.Add(mapped);
			theta += delta;
		}
		return result;
	}

	static Dictionary<string, string> Style(XElement e)
	{
		var result = new Dictionary<string, string>();
		foreach (string name in new[] { "fill", "fill-opacity", "stroke", "stroke-opacity", "opacity", "display" }) { if (e.Attribute(name) is XAttribute a) { result[name] = a.Value.Trim(); } }
		foreach (string part in (e.Attribute("style")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			int colon = part.IndexOf(':');
			if (colon > 0) { result[part[..colon].Trim()] = part[(colon + 1)..].Trim(); }
		}
		return result;
	}

	static int ParseColor(string hex)
	{
		hex = hex.TrimStart('#');
		if (hex.Length == 3) { hex = string.Concat(hex.Select(c => $"{c}{c}")); }
		return int.Parse(hex, NumberStyles.HexNumber);
	}

	static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

	static Matrix Transform(string? t)
	{
		Matrix m = Matrix.Identity;
		if (string.IsNullOrWhiteSpace(t)) { return m; }
		foreach (Match f in Regex.Matches(t, @"(\w+)\s*\(([^)]*)\)"))
		{
			double[] a = Regex.Matches(f.Groups[2].Value, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Select(x => Num(x.Value)).ToArray();
			m = m * (f.Groups[1].Value switch
			{
				"matrix" => new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]),
				"translate" => new Matrix(1, 0, 0, 1, a[0], a.Length > 1 ? a[1] : 0),
				"scale" => new Matrix(a[0], 0, 0, a.Length > 1 ? a[1] : a[0], 0, 0),
				// rotate(a) about the origin, rotate(a, x, y) about (x, y): translate(x, y) rotate(a) translate(-x, -y)
				"rotate" => Rotate(a[0], a.Length > 2 ? a[1] : 0, a.Length > 2 ? a[2] : 0),
				"skewX" => new Matrix(1, 0, Math.Tan(a[0] * Math.PI / 180), 1, 0, 0),
				"skewY" => new Matrix(1, Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0),
				_ => throw new NotSupportedException($"SVG transform {f.Groups[1].Value}"),
			});
		}
		return m;
	}

	static Matrix Rotate(double degrees, double x, double y)
	{
		double c = Math.Cos(degrees * Math.PI / 180), s = Math.Sin(degrees * Math.PI / 180);
		return new Matrix(1, 0, 0, 1, x, y) * new Matrix(c, s, -s, c, 0, 0) * new Matrix(1, 0, 0, 1, -x, -y);
	}

	// x' = a x + c y + e, y' = b x + d y + f (SVG's matrix(a, b, c, d, e, f))
	readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
	{
		public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
		public static Matrix operator *(Matrix p, Matrix q) => new(
			p.A * q.A + p.C * q.B, p.B * q.A + p.D * q.B, p.A * q.C + p.C * q.D, p.B * q.C + p.D * q.D, p.A * q.E + p.C * q.F + p.E, p.B * q.E + p.D * q.F + p.F);
		public VectorTrace.Node Apply(VectorTrace.Node n) => new(A * n.X + C * n.Y + E, B * n.X + D * n.Y + F,
			A * n.InX + C * n.InY, B * n.InX + D * n.InY, A * n.OutX + C * n.OutY, B * n.OutX + D * n.OutY);
	}
}
