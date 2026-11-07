using LevelXMLUtils;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
namespace LevelXMLUtils.Tests;
using Xunit;

public class PixelFontTests
{
	// "A" set alone with no padding must be exactly the font's rows (the rows no character in the text uses are trimmed): bit b of a row is
	// pixel x = b, the lowest bit the leftmost
	[Fact]
	public void ASingleLetter_IsExactlyItsRowsInTheFontData()
	{
		PixelFont font = PixelFont.Monogram();
		PixelArt art = font.Render(new[] { "A" }, 1, 0, padding: 0);
		int[] rows = JsonSerializer.Deserialize<Dictionary<string, int[]>>(new StreamReader(typeof(PixelFont).Assembly.GetManifestResourceStream("LevelXMLUtils.Fonts.monogram-bitmap.json")!).ReadToEnd())!["A"];
		int[] used = rows.Where(r => r != 0).ToArray();
		Assert.Equal(PixelFont.GlyphWidth, art.Width);
		Assert.Equal(used.Length, art.Height);
		for (int y = 0; y < art.Height; y++)
		{
			for (int x = 0; x < art.Width; x++) { Assert.Equal((used[y] >> x & 1) != 0 ? 1 : 0, art.Pixels[x, y]); }
		}
	}

	[Fact]
	public void Lines_AreSpacedByTheAdvanceAndTheGap()
	{
		PixelArt art = PixelFont.Monogram().Render(new[] { "Ab", "c" }, 1, 0, padding: 2, lineGap: 1);
		Assert.Equal(2 * PixelFont.Advance - (PixelFont.Advance - PixelFont.GlyphWidth) + 4, art.Width);
		Assert.Throws<ArgumentException>(() => PixelFont.Monogram().Render(new[] { "\u0001" }, 1, 0));
	}



}
