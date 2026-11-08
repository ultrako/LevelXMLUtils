using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LevelXMLUtils;
using LevelXML;
using Xunit;

namespace LevelXMLUtils.Tests;

public class LevelCodeGeneratorTests
{
	private static string Canonical(string xml)
	{
		XElement root = XElement.Parse(xml);
		foreach (XElement element in root.DescendantsAndSelf())
		{
			var attributes = element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList();
			element.RemoveAttributes();
			element.Add(attributes);
		}
		return root.ToString();
	}



	[Fact]
	public void ImportedVehicleJoint_IsGeneratedWithVehicleControlledOff()
	{
		Vehicle vehicle = new(new Rectangle());
		PinJoint joint = new(vehicle) { VehicleControlled = false };
		string code = LevelCodeGenerator.FromXml(new Level(vehicle, joint).ToXML());
		Assert.Contains("VehicleControlled = false", code);
	}

	[Fact]
	public void TriggerTargets_AreWiredAfterAllTriggersExist()
	{
		Group group = new(new Rectangle { Width = 30 });
		ActivateTrigger first = new();
		ActivateTrigger second = new();
		first.AddTarget(new Target<Group>(group, new Impulse<Group>(1, 2, 3)));
		first.AddTarget(new Target<Trigger>(second, new Enable()));
		second.AddTarget(new Target<Trigger>(first, new Disable()));

		string code = LevelCodeGenerator.FromXml(new Level(group, first, second).ToXML());

		int declaredLast = code.IndexOf("ActivateTrigger activateTrigger1 =", StringComparison.Ordinal);
		int firstWiring = code.IndexOf("activateTrigger0.AddTarget", StringComparison.Ordinal);
		Assert.True(declaredLast >= 0 && firstWiring > declaredLast);
		Assert.Contains("activateTrigger0.AddTarget(new Target<Group>(group0, new Impulse<Group>() { X = 1, Y = 2, Spin = 3 }));", code);
		Assert.Contains("activateTrigger0.AddTarget(new Target<Trigger>(activateTrigger1, new Enable()));", code);
		Assert.Contains("activateTrigger1.AddTarget(new Target<Trigger>(activateTrigger0, new Disable()));", code);
	}

	[Fact]
	public void OnlyPropertiesThatDifferFromTheDefaultAreWritten()
	{
		string code = LevelCodeGenerator.FromXml(new Level(new Rectangle { X = 5, Width = 30 }).ToXML());
		Assert.Contains("new() { X = 5, Width = 30 }", code);
	}

	[Fact]
	public void CustomNames_ReplaceTheGeneratedOnes()
	{
		Rectangle wall = new();
		Circle wheel = new();
		Level level = new(wall, wheel);
		LevelCodeGenerator generator = new();
		generator.Names[wall] = "leftWall";
		generator.Names[wheel] = "wheel";

		string code = generator.GenerateMethod(level);

		Assert.Contains("Rectangle leftWall =", code);
		Assert.Contains("Circle wheel =", code);
		Assert.Contains("new Level(", code);
		Assert.DoesNotContain("rectangle0", code);
	}

	[Fact]
	public void AnOpenOutline_IsGeneratedOpen()
	{
		const string xml = "<levelXML><info v=\"1.95\" x=\"0\" y=\"0\" c=\"1\" f=\"f\" h=\"f\" bg=\"0\" bgc=\"16777215\" e=\"1\"/><shapes>"
			+ "<sh t=\"3\" p0=\"0\" p1=\"0\" p2=\"10\" p3=\"10\" p4=\"0\" p5=\"t\" p6=\"f\" p7=\"1\" p8=\"1\" p9=\"-1\" p10=\"100\" p11=\"1\">"
			+ "<v f=\"f\" id=\"0\" n=\"3\" v0=\"0_0\" v1=\"10_0\" v2=\"0_10\"/></sh></shapes></levelXML>";

		string code = new LevelCodeGenerator().GenerateMethod(new Level(xml));

		Assert.Contains("Closed = false", code);
		Assert.DoesNotContain("WARNING", code);
	}

	[Fact]
	public void Parts_MustCoverEveryEntityExactlyOnce()
	{
		Rectangle wall = new();
		Circle wheel = new();
		Level level = new(wall, wheel);

		Assert.Throws<InvalidOperationException>(() => new LevelCodeGenerator().GenerateClass(level, "Test",
			new[] { new LevelCodeGenerator.LevelPart("Walls", "", new Entity[] { wall }) }));
		Assert.Throws<InvalidOperationException>(() => new LevelCodeGenerator().GenerateClass(level, "Test",
			new[]
			{
				new LevelCodeGenerator.LevelPart("Walls", "", new Entity[] { wall, wheel }),
				new LevelCodeGenerator.LevelPart("Wheels", "", new Entity[] { wheel }),
			}));
	}

	[Fact]
	public void Parts_MustBeOrderedSoJointsComeAfterWhatTheyJoin()
	{
		Rectangle wall = new();
		PinJoint joint = new(wall);
		Level level = new(wall, joint);

		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new LevelCodeGenerator().GenerateClass(level, "Test",
			new[]
			{
				new LevelCodeGenerator.LevelPart("Joints", "", new Entity[] { joint }),
				new LevelCodeGenerator.LevelPart("Walls", "", new Entity[] { wall }),
			}));
		Assert.Contains("later part Walls", error.Message);
	}

	[Fact]
	public void Parts_KeepTheLevelsEntityOrder()
	{
		Rectangle first = new();
		Rectangle second = new();
		Level level = new(first, second);
		LevelCodeGenerator generator = new();
		generator.Names[first] = "first";
		generator.Names[second] = "second";

		// Second is in the earlier part, but the level must still list first before second
		string code = generator.GenerateClass(level, "Test", new[]
		{
			new LevelCodeGenerator.LevelPart("B", "", new Entity[] { second }),
			new LevelCodeGenerator.LevelPart("A", "", new Entity[] { first }),
		});

		Assert.Contains("new Level(\n\t\t\tFirst, Second)", code.Replace("\r\n", "\n"));
		Assert.True(code.IndexOf("BuildB();", StringComparison.Ordinal) < code.IndexOf("BuildA();", StringComparison.Ordinal));
	}
}
