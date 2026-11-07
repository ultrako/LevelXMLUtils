using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using LevelXMLUtils;
using Xunit;

namespace LevelXMLUtils.Tests;

// LevelCruncher.ShareArtVertices writes each outline once, and the game draws a shape by scaling its (shared) vertex list to the shape's width and height,
// the scale clamped to 0.1 to 10 times the list's span (measured from 0). These read the shared lists back the way the game does.
public class ShareArtVerticesTests
{
	static XElement Shape(double x, double y, double w, double h) =>
		new("sh", new XAttribute("t", "4"), new XAttribute("p0", x), new XAttribute("p1", y), new XAttribute("p2", w), new XAttribute("p3", h),
			new XElement("v", new XAttribute("f", "t"), new XAttribute("v0", $"{-w / 2}_{-h / 2}"), new XAttribute("v1", $"{w / 2}_{-h / 2}"), new XAttribute("v2", $"{w / 4}_{h / 2}"), new XAttribute("n", 3)));

	// The vertices the game draws for each shape: the list (its own or the one its id first had), scaled by width / span and height / span, clamped
	static List<List<(double x, double y)>> AsDrawn(XElement level)
	{
		Dictionary<string, List<(double x, double y)>> lists = new();
		var drawn = new List<List<(double x, double y)>>();
		foreach (XElement sh in level.Descendants("sh"))
		{
			XElement v = sh.Element("v")!;
			string id = v.Attribute("id")!.Value;
			if (v.Attribute("n") is XAttribute n)
			{
				lists[id] = Enumerable.Range(0, int.Parse(n.Value)).Select(i => v.Attribute("v" + i)!.Value.Split('_').Select(double.Parse).ToArray()).Select(p => (p[0], p[1])).ToList();
			}
			var list = lists[id];
			double spanX = Math.Max(0, list.Max(p => p.x)) - Math.Min(0, list.Min(p => p.x)), spanY = Math.Max(0, list.Max(p => p.y)) - Math.Min(0, list.Min(p => p.y));
			double sx = Math.Clamp(double.Parse(sh.Attribute("p2")!.Value) / spanX, 0.1, 10), sy = Math.Clamp(double.Parse(sh.Attribute("p3")!.Value) / spanY, 0.1, 10);
			drawn.Add(list.Select(p => (p.x * sx, p.y * sy)).ToList());
		}
		return drawn;
	}

	[Fact]
	public void SharedOutlines_AreDrawnAtTheirOwnSize_EvenWhenSmallOrLarge()
	{
		// the same outline at 5, 40 and 250 units wide (and 0.6 as tall, so the sides run from 3 to 250, under 100 times apart): one list, and each drawn exactly its own size (at the old span of 100 the 5-unit one came out 10)
		double[] sizes = { 5, 40, 250 };
		XElement level = new("levelXML", new XElement("shapes", sizes.Select(w => Shape(0, 0, w, w * 0.6))));
		LevelCruncher.ShareArtVertices(level);
		Assert.Single(level.Descendants("v").Where(v => v.Attribute("n") is not null));
		var drawn = AsDrawn(level);
		for (int i = 0; i < sizes.Length; i++)
		{
			double w = sizes[i], h = w * 0.6;
			var expected = new[] { (-w / 2, -h / 2), (w / 2, -h / 2), (w / 4, h / 2) };
			for (int k = 0; k < 3; k++)
			{
				Assert.Equal(expected[k].Item1, drawn[i][k].x, 2);
				Assert.Equal(expected[k].Item2, drawn[i][k].y, 2);
			}
		}
	}

	[Fact]
	public void SharedOutlines_MoreThanAHundredTimesApart_AreSplit()
	{
		XElement level = new("levelXML", new XElement("shapes", new[] { 2.0, 20, 2000 }.Select(w => Shape(0, 0, w, w))));
		LevelCruncher.ShareArtVertices(level);
		Assert.Equal(2, level.Descendants("v").Count(v => v.Attribute("n") is not null));
		var drawn = AsDrawn(level);
		Assert.Equal(2000, drawn[2].Max(p => p.x) - drawn[2].Min(p => p.x), 2);
		Assert.Equal(2, drawn[0].Max(p => p.x) - drawn[0].Min(p => p.x), 2);
	}

	// The level file keeps three decimals of every vertex, so a tiny copy of an outline doesn't come out exactly a scaled copy of a large one; they still share
	// one list (within half a percent of the box), the most precise one
	[Fact]
	public void TinyCopies_RoundedByTheFile_StillShareTheLargeCopysOutline()
	{
		static XElement Rounded(double w)
		{
			double[][] outline = { new[] { -0.5, -0.5 }, new[] { 0.5, -0.5 }, new[] { 0.1234, 0.5 } };
			return new XElement("sh", new XAttribute("t", "4"), new XAttribute("p0", 0), new XAttribute("p1", 0), new XAttribute("p2", w), new XAttribute("p3", w),
				new XElement("v", new XAttribute("f", "t"), outline.Select((p, i) => new XAttribute("v" + i, $"{Math.Round(p[0] * w, 3)}_{Math.Round(p[1] * w, 3)}")), new XAttribute("n", 3)));
		}
		XElement level = new("levelXML", new XElement("shapes", new[] { 300, 20, 10.0 }.Select(Rounded)));
		LevelCruncher.ShareArtVertices(level);
		Assert.Single(level.Descendants("v").Where(v => v.Attribute("n") is not null));
		// every copy is drawn from the large copy's precise vertex, to within half a percent of its box
		var drawn = AsDrawn(level);
		double[] sizes = { 300, 20, 10 };
		for (int i = 0; i < 3; i++) { Assert.InRange(drawn[i][2].x, 0.1234 * sizes[i] - 0.005 * sizes[i], 0.1234 * sizes[i] + 0.005 * sizes[i]); }
	}

	// A shared list must come before the shapes that refer to it (seen in the game): it is written at its first shape in the file, however the shapes are sized
	[Fact]
	public void ASharedList_IsWrittenAtItsFirstShapeInTheFile()
	{
		XElement level = new("levelXML", new XElement("shapes", new[] { 40.0, 5, 200 }.Select(w => Shape(0, 0, w, w))));
		LevelCruncher.ShareArtVertices(level);
		var lists = level.Descendants("v").ToList();
		Assert.NotNull(lists[0].Attribute("n"));
		Assert.All(lists.Skip(1), v => Assert.Null(v.Attribute("n")));
	}

	// A shape far smaller than the largest copy of its outline is drawn at least a tenth of the list's span: it shares the list as long as that is at most a
	// quarter of a unit too big (under a screen pixel), else it gets a list of its own
	[Fact]
	public void ATinyCopy_SharesTheList_OnlyIfItIsDrawnAtMostAUnitTooBig()
	{
		XElement shared = new("levelXML", new XElement("shapes", new[] { 300.0, 30 }.Select(w => Shape(0, 0, w, w))));
		LevelCruncher.ShareArtVertices(shared);
		Assert.Single(shared.Descendants("v").Where(v => v.Attribute("n") is not null));
		var drawn = AsDrawn(shared);
		Assert.InRange(drawn[1].Max(p => p.x) - drawn[1].Min(p => p.x), 30, 30.25);
		XElement split = new("levelXML", new XElement("shapes", new[] { 300.0, 1 }.Select(w => Shape(0, 0, w, w))));
		LevelCruncher.ShareArtVertices(split);
		Assert.Equal(2, split.Descendants("v").Count(v => v.Attribute("n") is not null));
		Assert.InRange(AsDrawn(split)[1].Max(p => p.x) - AsDrawn(split)[1].Min(p => p.x), 1, 1.25);
	}
}
