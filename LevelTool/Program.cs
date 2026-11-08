using System.Globalization;
using LevelXML;
using LevelXMLUtils;
using SkiaSharp;

// Command-line front end to the toolkit. Every command writes level XML (or C#) to stdout and messages to stderr, so
// redirect stdout to a file. Options are NAME=VALUE pairs or bare flags after the positional arguments.
const string Usage = """
Usage: LevelTool <command> ...

  codegen <levelXML file> <ClassName> [Namespace]
      C# source that rebuilds the level with LevelXML.

  minify <levelXML file> [round=DECIMALS] [share-art] [polygons] [picture-round=DECIMALS]
      Smaller level XML: default values dropped, no formatting. round= rounds every number, share-art writes each
      art outline once, polygons turns straight-edged art shapes into polygons.

  trace <image> [width=UNITS] [colors=N] [tolerance=PIXELS] [corner=DEGREES] [blur=SIGMA] [specks=SHARE]
        [merge=OKLAB] [denoise=OKLAB] [pin=RRGGBB,...] [circle] [preview=FILE.png]
      An image (photo, logo, painting) traced into a few smooth art shapes.

  svg <file.svg> [width=UNITS]
      An SVG drawing's paths as art shapes.

  pixelart <image> [pixel=UNITS] [merge=COLOUR DISTANCE]
      Pixel art as one art shape per colour.

The art commands centre the picture at (450, 250), the middle of the game's window when the camera sits at the
level's top left, and put the spawn under it.
""";

if (args.Length == 0) { Console.Error.Write(Usage); return 1; }
string[] positional = args.Skip(1).Where(a => !a.Contains('=')).ToArray();
Dictionary<string, string> named = args.Skip(1).Where(a => a.Contains('=')).ToDictionary(a => a[..a.IndexOf('=')], a => a[(a.IndexOf('=') + 1)..]);
double Number(string key, double fallback) => named.TryGetValue(key, out string? v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
int? Integer(string key) => named.TryGetValue(key, out string? v) ? int.Parse(v, CultureInfo.InvariantCulture) : null;
bool Flag(string name) => positional.Contains(name);
const double CentreX = 450, CentreY = 250;

void WriteArt(List<Art> shapes)
{
	Console.Error.WriteLine($"{shapes.Count} shapes, {shapes.Sum(s => s.Vertices.Count())} vertices");
	Console.Write(new Level(shapes.Cast<Entity>().ToArray()) { X = CentreX, Y = 600 }.ToXML());
}

switch (args[0].ToLowerInvariant())
{
	case "codegen":
	{
		if (positional.Length < 2) { Console.Error.Write(Usage); return 1; }
		Level level = new(File.ReadAllText(positional[0]));
		LevelCodeGenerator generator = new();
		Console.Write(generator.GenerateClass(level, positional[1], @namespace: positional.Length > 2 ? positional[2] : "Levels"));
		return 0;
	}
	case "minify":
	{
		if (positional.Length < 1) { Console.Error.Write(Usage); return 1; }
		Console.Write(LevelCruncher.MinifyRelayTriggers(File.ReadAllText(positional[0]), roundDecimals: Integer("round"),
			shareArtVertices: Flag("share-art"), straightArtToPolygons: Flag("polygons"), pictureDecimals: Integer("picture-round")));
		return 0;
	}
	case "trace":
	{
		if (positional.Length < 1) { Console.Error.Write(Usage); return 1; }
		VectorTrace.Options defaults = new();
		VectorTrace.Options options = defaults with
		{
			Circle = Flag("circle"),
			MaxColors = (int)Number("colors", defaults.MaxColors),
			Tolerance = Number("tolerance", defaults.Tolerance),
			CornerAngle = Number("corner", defaults.CornerAngle),
			Blur = Number("blur", defaults.Blur),
			MinRegionShare = Number("specks", defaults.MinRegionShare),
			MergeDistance = Number("merge", defaults.MergeDistance),
			Denoise = Number("denoise", defaults.Denoise),
			Pinned = named.TryGetValue("pin", out string? pins) ? pins.Split(',').Select(s => Convert.ToInt32(s, 16)).ToArray() : Array.Empty<int>(),
		};
		VectorTrace trace = new(positional[0], options);
		double width = Number("width", 400);
		List<Art> shapes = trace.ToShapes(width, CentreX, CentreY);
		Console.Error.WriteLine($"{trace.Width}x{trace.Height} pixels, {trace.Palette.Count} colours ({string.Join(" ", trace.Palette.Select(c => c.ToString("X6")))})");
		if (named.TryGetValue("preview", out string? preview))
		{
			// the source on the left, the shapes drawn the way the game draws them on the right, both width units wide
			using SKBitmap source = SKBitmap.Decode(positional[0]);
			int height = (int)Math.Ceiling(width * trace.Height / trace.Width);
			using SKBitmap traced = VectorTrace.Render(shapes, (int)width, height, CentreX - width / 2, CentreY - width * trace.Height / trace.Width / 2);
			using SKBitmap sheet = new((int)width * 2 + 10, height);
			using (SKCanvas canvas = new(sheet))
			{
				canvas.Clear(SKColors.White);
				canvas.DrawBitmap(source, new SKRect(0, 0, (float)width, height));
				canvas.DrawBitmap(traced, (float)width + 10, 0);
			}
			using FileStream stream = File.Create(preview);
			sheet.Encode(stream, SKEncodedImageFormat.Png, 100);
			Console.Error.WriteLine($"preview: {preview}");
		}
		WriteArt(shapes);
		return 0;
	}
	case "svg":
	{
		if (positional.Length < 1) { Console.Error.Write(Usage); return 1; }
		double width = Number("width", 400);
		WriteArt(new SvgArt(positional[0]).ToShapes(width, CentreX - width / 2, CentreY - width / 2));
		return 0;
	}
	case "pixelart":
	{
		if (positional.Length < 1) { Console.Error.Write(Usage); return 1; }
		PixelArt art = PixelArt.Load(positional[0], mergeWithin: Integer("merge") ?? 0);
		double pixel = Number("pixel", 4);
		List<Art> shapes = art.ToShapes(pixel, art.Width / 2.0, art.Height / 2.0);
		foreach (Art shape in shapes) { shape.X += CentreX; shape.Y += CentreY; }
		WriteArt(shapes);
		return 0;
	}
	default:
		Console.Error.Write(Usage);
		return 1;
}
