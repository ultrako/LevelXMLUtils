using LevelXMLUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LevelXML;
namespace LevelXMLUtils.Tests;
using Xunit;

// PixelArt traces each colour of a pixel-art image into one art shape. These tests draw the resulting shapes back onto a
// pixel grid (even-odd scanline fill at pixel centres, in the shapes' drawing order) and require exactly the source image.
public class PixelArtTests
{
	private static PixelArt Parse(params string[] rows)
	{
		Dictionary<char, int> palette = new() { ['a'] = 0x000000, ['b'] = 0xFF0000, ['c'] = 0x00FF00, ['d'] = 0x0000FF };
		int?[,] pixels = new int?[rows[0].Length, rows.Length];
		for (int y = 0; y < rows.Length; y++) { for (int x = 0; x < rows[y].Length; x++) { if (rows[y][x] != '.') { pixels[x, y] = palette[rows[y][x]]; } } }
		return new PixelArt(pixels);
	}

	private static int?[,] Render(PixelArt art, double pixelSize = 1)
	{
		List<Art> shapes = art.ToShapes(pixelSize, 0, 0);
		int?[,] result = new int?[art.Width, art.Height];
		foreach (Art shape in shapes)
		{
			var v = shape.Vertices;
			for (int y = 0; y < art.Height; y++)
			{
				for (int x = 0; x < art.Width; x++)
				{
					double px = (x + 0.5) * pixelSize, py = (y + 0.5) * pixelSize;
					bool inside = false;
					for (int i = 0; i < v.Count; i++)
					{
						Point a = new(shape.X + v[i].Position.X, shape.Y + v[i].Position.Y), b = new(shape.X + v[(i + 1) % v.Count].Position.X, shape.Y + v[(i + 1) % v.Count].Position.Y);
						if ((a.Y > py) != (b.Y > py) && px < (b.X - a.X) * (py - a.Y) / (b.Y - a.Y) + a.X) { inside = !inside; }
					}
					if (inside) { result[x, y] = (int)shape.FillColor; }
				}
			}
		}
		return result;
	}

	private static void AssertSame(PixelArt art, double pixelSize = 1)
	{
		int?[,] drawn = Render(art, pixelSize);
		for (int y = 0; y < art.Height; y++) { for (int x = 0; x < art.Width; x++) { Assert.True(drawn[x, y] == art.Pixels[x, y], $"pixel ({x},{y}): drew {drawn[x, y]:X}, the image has {art.Pixels[x, y]:X}"); } }
	}

	[Fact]
	public void ARing_WithADotInside_IsDrawnSolidUnderTheDot() => AssertSame(Parse("bbbbb", "bcccb", "bcdcb", "bcccb", "bbbbb"));

	[Fact]
	public void SeparatePieces_OfOneColour_AreOneShape()
	{
		PixelArt art = Parse("b.b.b", "aaaaa", "b.b.b");
		AssertSame(art);
		Assert.Equal(2, art.ToShapes(1, 0, 0).Count); // the silhouette (b, the most common) and a
	}

	[Fact]
	public void PiecesThatOnlyTouchAtACorner_AreDrawnCorrectly() => AssertSame(Parse("bb..", "bbcc", "..cc", "..cc"));

	// A transparent area completely enclosed by the drawing (the gap between two outlines) is a hole: drawn as a loop the other way round, it stays empty.
	// A colour enclosing pixels of the silhouette colour (the most common one, drawn underneath as the whole picture with no shape of its own): the inside of an
	// o in text on a board. Filling across it covered the board there, so every letter with a counter came out solid in the game.
	[Fact]
	public void AColourEnclosingTheSilhouetteColour_LeavesItShowing() => AssertSame(Parse("bbbbbbb", "bcccccb", "bcbbbcb", "bcccccb", "bbbbbbb", "bbbbbbb"));

	[Fact]
	public void TextOnABoard_ComesBackAsExactlyTheSamePixels() => AssertSame(PixelFont.Monogram().Render(new[] { "Space: Ao e8 D", "@ R" }, 0x1A1A1A, 0xD8C8A0));

	[Fact]
	public void ATransparentHole_StaysEmpty() => AssertSame(Parse("aaaaa", "a...a", "abcba", "aaaaa"));

	[Fact]
	public void AHole_InsideAColourThatEnclosesAnotherColour_StaysEmptyToo() => AssertSame(Parse("bbbbbbb", "b.....b", "b.cdc.b", "b.....b", "bbbbbbb"));

	// Two colours that each enclose the other (a checkerboard) are both drawn as exactly their own pixels: filling either across the other covered it
	[Fact]
	public void ACheckerboard_ComesBackAsACheckerboard() => AssertSame(Parse("ccccccc", "cbdbdbc", "cdbdbdc", "cbdbdbc", "ccccccc"));

	[Fact]
	public void EveryShape_HasARealWidthAndHeight_NotNaN()
	{
		foreach (Art shape in Parse("bbb", "bcb", "bbb").ToShapes(5, 0, 0))
		{
			Assert.False(double.IsNaN(shape.Width) || double.IsNaN(shape.Height));
			Assert.True(shape.Width > 0 && shape.Height > 0);
			Assert.DoesNotContain("NaN", new Level(shape).ToXML());
		}
	}

	[Fact]
	public void ScaleAndAnchor_PlaceTheShapesInWorldUnits()
	{
		PixelArt art = Parse("bc", "cb");
		List<Art> shapes = art.ToShapes(5, 1, 2);
		// the pixel grid corner (0, 0) is 1 pixel left of and 2 above the anchor, at 5 units a pixel
		Assert.Contains(shapes.SelectMany(s => s.Vertices.Select(v => (x: s.X + v.Position.X, y: s.Y + v.Position.Y))), p => p.x == -5 && p.y == -10);
		AssertSame(art, 5);
	}

	// The game scales a shape's vertices to its width and height around its position, so the vertices must be centred on the box
	// (seen in the game: shapes far from their origin were drawn small and near it).
	[Fact]
	public void EveryShape_IsCentredOnItsOwnBoundingBox()
	{
		foreach (Art shape in Parse("bbbb", "bccb", "bbbb", "....").ToShapes(7, 3, 3).Concat(PixelFont.Monogram().Render(new[] { "Hi" }, 0x1A1A1A, 0xD8C8A0).ToShapes(6, 13.5, 20)))
		{
			double minX = shape.Vertices.Min(v => v.Position.X), maxX = shape.Vertices.Max(v => v.Position.X);
			double minY = shape.Vertices.Min(v => v.Position.Y), maxY = shape.Vertices.Max(v => v.Position.Y);
			Assert.Equal(0, minX + maxX, 6);
			Assert.Equal(0, minY + maxY, 6);
			Assert.Equal(maxX - minX, shape.Width, 6);
			Assert.Equal(maxY - minY, shape.Height, 6);
		}
	}

}
