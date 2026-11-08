using LevelXML;
using System;
using System.Linq;
using LevelXMLUtils;
using Xunit;

namespace LevelXMLUtils.Tests;

public class SvgArtTests
{
	// A point of a cubic curve at t
	static (double x, double y) At(double[] c, double t)
	{
		double u = 1 - t;
		return (u * u * u * c[0] + 3 * u * u * t * c[2] + 3 * u * t * t * c[4] + t * t * t * c[6], u * u * u * c[1] + 3 * u * u * t * c[3] + 3 * u * t * t * c[5] + t * t * t * c[7]);
	}

	// The curves of an arc stay on its circle (the cubic approximation of a quarter circle is off by at most 0.03% of the radius), start and end where
	// asked, and go the way the flags say
	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 1)]
	[InlineData(0, 0)]
	[InlineData(1, 0)]
	public void AnArcsCurves_LieOnItsCircle_AndFollowItsFlags(int largeArc, int sweep)
	{
		const double r = 50;
		// from (0, 0) to (50, 50) on a circle of radius 50: the two centres are (50, 0) and (0, 50)
		var curves = SvgArt.A2C(0, 0, 50, 50, largeArc, sweep, r, r, 0);
		Assert.Equal(0, curves[0][0], 9); Assert.Equal(0, curves[0][1], 9);
		Assert.Equal(50, curves[^1][6], 9); Assert.Equal(50, curves[^1][7], 9);
		Assert.Equal(largeArc == 1 ? 3 : 1, curves.Count); // 270 degrees in three pieces, 90 in one
		// the large arc and the small one round different centres: from (0, 0), straight up from (0, 50), turning in the positive direction (sweep 1, which
		// with y down is clockwise on the screen) reaches (50, 50) after 90 degrees, so the small arc with sweep 1 goes round (0, 50)
		(double cx, double cy) = (largeArc == sweep) ? (50.0, 0.0) : (0.0, 50.0);
		foreach (double[] c in curves)
		{
			for (double t = 0; t <= 1; t += 0.05)
			{
				(double x, double y) = At(c, t);
				double off = Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r);
				Assert.True(off < r * 0.0003, $"t={t}: {off} off the circle");
			}
		}
	}

	[Fact]
	public void PackedArcFlags_ReadLikeSpacedOnes()
	{
		var packed = SvgArt.ParsePath("M20 90a15 8 0 1010 10l20-30z");
		var spaced = SvgArt.ParsePath("M 20 90 a 15 8 0 1 0 10 10 l 20 -30 z");
		Assert.Equal(spaced.Single().Count, packed.Single().Count);
		foreach (var (p, s) in packed.Single().Zip(spaced.Single())) { Assert.Equal(s.X, p.X, 9); Assert.Equal(s.Y, p.Y, 9); Assert.Equal(s.InX, p.InX, 9); Assert.Equal(s.OutY, p.OutY, 9); }
	}

	[Fact]
	public void AStrokeGivenAsAnAttribute_IsAnOpenLine()
	{
		string path = Path.Combine(Path.GetTempPath(), $"svgart-{Guid.NewGuid()}.svg");
		File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><path d=\"M10 10 L90 10 L50 90\" fill=\"none\" stroke=\"#ff0000\"/></svg>");
		try
		{
			Art line = Assert.Single(new SvgArt(path).ToShapes(100, 0, 0));
			Assert.False(line.Closed);
			Assert.Equal(0xff0000, (int)line.OutlineColor);
		}
		finally { File.Delete(path); }
	}
}
