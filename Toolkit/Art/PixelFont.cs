using System.Text.Json;
namespace LevelXMLUtils;

// Text set in a pixel font, as PixelArt (one shape per colour once it goes through PixelArt.ToShapes). The built-in font is monogram by Vinícius
// Menézio (datagoblin), CC0 (Fonts/credits.txt; from https://gitlab.com/datagoblin/monogram, source/bitmap/monogram-bitmap.json), embedded in this
// library. A font file is a JSON object from each character to its 12 rows, each row a number whose bits are its pixels, the lowest bit the leftmost;
// glyphs are 5 pixels wide and the font is monospace at Advance.
public class PixelFont
{
	public const int Advance = 6, Rows = 12, GlyphWidth = 5;
	readonly Dictionary<string, int[]> glyphs;

	/// <summary>Reads a font in monogram's bitmap JSON format from a file.</summary>
	public PixelFont(string jsonPath) : this(File.OpenRead(jsonPath)) { }

	/// <summary>Reads a font in monogram's bitmap JSON format from a stream (which it closes).</summary>
	public PixelFont(Stream json)
	{
		using (json) { glyphs = JsonSerializer.Deserialize<Dictionary<string, int[]>>(json)!; }
	}

	/// <summary>The monogram font, embedded in this library.</summary>
	public static PixelFont Monogram() =>
		new(typeof(PixelFont).Assembly.GetManifestResourceStream("LevelXMLUtils.Fonts.monogram-bitmap.json")
			?? throw new FileNotFoundException("the embedded monogram font is missing from the assembly"));

	public bool Has(char c) => c == ' ' || glyphs.ContainsKey(c.ToString());

	// Lines of text in textColor on a board of boardColor, padding pixels round it and lineGap empty rows between lines. Rows no character uses
	// (above the tallest and below the lowest) are left out, so lines are as close as the font allows.
	public PixelArt Render(IReadOnlyList<string> lines, int textColor, int boardColor, int padding = 2, int lineGap = 1)
	{
		foreach (char c in lines.SelectMany(l => l)) { if (!Has(c)) { throw new ArgumentException($"the font has no '{c}'"); } }
		int[] Rows(char c) => c == ' ' ? new int[PixelFont.Rows] : glyphs[c.ToString()];
		var used = Enumerable.Range(0, PixelFont.Rows).Where(r => lines.SelectMany(l => l).Any(c => Rows(c)[r] != 0)).ToList();
		int top = used.Count > 0 ? used.Min() : 0, bottom = used.Count > 0 ? used.Max() : 0, lineHeight = bottom - top + 1;
		int width = lines.Max(l => l.Length) * Advance - (Advance - GlyphWidth) + 2 * padding;
		int height = lines.Count * lineHeight + (lines.Count - 1) * lineGap + 2 * padding;
		int?[,] pixels = new int?[width, height];
		for (int x = 0; x < width; x++) { for (int y = 0; y < height; y++) { pixels[x, y] = boardColor; } }
		for (int l = 0; l < lines.Count; l++)
		{
			for (int i = 0; i < lines[l].Length; i++)
			{
				int[] rows = Rows(lines[l][i]);
				for (int r = top; r <= bottom; r++)
				{
					for (int b = 0; b < GlyphWidth; b++)
					{
						if ((rows[r] >> b & 1) != 0) { pixels[padding + i * Advance + b, padding + l * (lineHeight + lineGap) + r - top] = textColor; }
					}
				}
			}
		}
		return new PixelArt(pixels);
	}
}
