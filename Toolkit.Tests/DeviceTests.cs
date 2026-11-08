using LevelXML;
using Xunit;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Tests;

// The gate classes and the small devices, run in the trigger simulator. A Bench wires a driver relay to each Input a test fires and a
// watcher relay to each Output it reads, so a step is "fire these drivers, in this order, in one frame" and a reading is "did this
// watcher fire, and how many times".
public class DeviceTests
{
	sealed class Bench
	{
		readonly List<TriggerContraption> parts = new();
		readonly Dictionary<Relay, Relay> sinks = new();
		TriggerGraph? graph;
		public Bench(params TriggerContraption[] devices) { parts.AddRange(devices); }
		public Relay Driver(Input input)
		{
			Relay driver = new();
			driver.Output.Connect(input);
			parts.Add(driver);
			return driver;
		}
		public Relay Watch(Output output)
		{
			// the watcher's one action (firing the sink) counts its firings in ActionsBySource
			Relay watcher = new(), sink = new();
			watcher.Output.Connect(sink.Input);
			output.Connect(watcher.Input);
			parts.Add(watcher);
			parts.Add(sink);
			sinks[watcher] = sink;
			return watcher;
		}
		public TriggerGraph Graph => graph ??= TriggerGraph.FromTriggers(parts.SelectMany(p => p.Entities).OfType<ActivateTrigger>());
		public void Fire(params Relay[] drivers) => Graph.Pulse(drivers.Select(Id).ToArray());
		public void Fire(ActivateTrigger trigger) => Graph.Pulse(TriggerGraph.IdFor(trigger));
		public bool Fired(Relay watcher) => Graph.LastFired.Contains(Id(watcher));
		public int Times(Relay watcher) => Graph.ActionsBySource.GetValueOrDefault(Id(watcher));
		public static string Id(Relay relay) => TriggerGraph.IdFor(relay.Output.Single());
	}

	[Theory]
	[InlineData(false, false, false)]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(true, true, true)]
	public void And_FiresWhenBothArrive_AFirst(bool a, bool b, bool expected)
	{
		And and = new();
		Bench bench = new(and);
		Relay inA = bench.Driver(and.InputA), inB = bench.Driver(and.InputB), last = bench.Driver(and.PowerLast);
		Relay output = bench.Watch(and.Output);
		bench.Fire(new[] { a ? inA : null, b ? inB : null, last }.OfType<Relay>().ToArray());
		Assert.Equal(expected, bench.Fired(output));
		// PowerLast forgot A
		bench.Fire(inB);
		Assert.False(bench.Fired(output));
	}

	[Fact]
	public void Or_FiresOnce_HoweverManyInputsArrive()
	{
		Or or = new();
		Bench bench = new(or);
		Relay reset = bench.Driver(or.Reset), input = bench.Driver(or.Input);
		Relay output = bench.Watch(or.Output);
		bench.Fire(reset, input, input, input);
		Assert.Equal(1, bench.Times(output));
		bench.Fire(reset);
		Assert.False(bench.Fired(output));
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void Not_FiresAtPowerLast_UnlessTheInputCame(bool input, bool expected)
	{
		Not not = new();
		Bench bench = new(not);
		Relay reset = bench.Driver(not.Reset), into = bench.Driver(not.Input), last = bench.Driver(not.PowerLast);
		Relay output = bench.Watch(not.Output);
		bench.Fire(new[] { reset, input ? into : null, last }.OfType<Relay>().ToArray());
		Assert.Equal(expected, bench.Fired(output));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void Xnor_AndXor_CompareTheirInputs(bool a, bool b)
	{
		Xnor xnor = new();
		Xor xor = new(20, 0);
		Bench bench = new(xnor, xor);
		Relay reset = bench.Driver(xnor.Reset.Concat(xor.Reset)), last = bench.Driver(xnor.PowerLast.Concat(xor.PowerLast));
		Relay inA = bench.Driver(xnor.InputA.Concat(xor.InputA)), inB = bench.Driver(xnor.InputB.Concat(xor.InputB));
		Relay same = bench.Watch(xnor.Output), different = bench.Watch(xor.Output);
		bench.Fire(new[] { reset, a ? inA : null, b ? inB : null, last }.OfType<Relay>().ToArray());
		Assert.Equal(a == b, bench.Fired(same));
		Assert.Equal(a != b, bench.Fired(different));
	}

	[Fact]
	public void ClickableToggle_AlternatesOnAndOff()
	{
		ClickableToggle toggle = new();
		Bench bench = new(toggle);
		Relay on = bench.Watch(toggle.On), off = bench.Watch(toggle.Off);
		var clicks = toggle.Entities.OfType<ActivateTrigger>().ToList();
		// a click fires whichever of the two click triggers is enabled
		void Click() => bench.Fire(clicks.Single(c => bench.Graph.IsEnabled(TriggerGraph.IdFor(c))));
		Click();
		Assert.True(bench.Fired(on));
		Click();
		Assert.True(bench.Fired(off));
		Click();
		Assert.True(bench.Fired(on));
	}

	[Fact]
	public void Numpad_SendsTheDigitsBits()
	{
		Numpad pad = new();
		Bench bench = new(pad);
		Relay[] bits = pad.Value.Select(bench.Watch).ToArray();
		Relay clicked = bench.Watch(pad.Clicked);
		// the click triggers, in the order the numpad makes them: 1 to 9, then 0
		var keys = pad.Entities.OfType<ActivateTrigger>().ToList();
		int[] digits = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0 };
		for (int i = 0; i < 10; i++)
		{
			bench.Fire(keys[i]);
			Assert.True(bench.Fired(clicked));
			Assert.Equal(digits[i], Enumerable.Range(0, 4).Sum(k => bench.Fired(bits[k]) ? 1 << k : 0));
		}
	}

	// The digit a display shows after a reset and the given inputs: its labels are stacked, 0 drawn last (on top); the reset shows them
	// all and each input hides some, so the one seen is the smallest still shown.
	static int Seen(TriggerContraption display, Input reset, params Input[] inputs)
	{
		List<Group> labels = display.Entities.OfType<Group>().ToList();
		var shown = labels.ToDictionary(g => g, g => true);
		void Apply(Input input)
		{
			foreach (Target<Group> target in input.OfType<Target<Group>>()) { shown[(Group)target.Targeted] = (target.Actions.OfType<ChangeOpacity<Group>>().Single().Opacity ?? 0) > 0; }
		}
		Apply(reset);
		foreach (Input input in inputs) { Apply(input); }
		Group top = labels.Last(g => shown[g]);
		return int.Parse(top.Items.OfType<TextBox>().Single().Content);
	}

	[Fact]
	public void DecimalDisplay_ShowsTheValueOfItsBits()
	{
		DecimalDisplay display = new();
		for (int v = 0; v < 10; v++)
		{
			Input[] bits = Enumerable.Range(0, 4).Where(k => (v >> k & 1) == 1).Select(k => display.Sets[k]).ToArray();
			Assert.Equal(v, Seen(display, display.Reset, bits));
			Assert.Equal(v, Seen(display, display.Reset, display.SetUnary[v]));
		}
	}

	[Fact]
	public void HexDisplay_ShowsTheValueOfItsBits()
	{
		HexDisplay display = new();
		for (int v = 0; v < 16; v++)
		{
			Input[] bits = Enumerable.Range(0, 4).Where(k => (v >> k & 1) == 1).Select(k => display.Sets[k]).ToArray();
			Assert.Equal(v, Seen(display, display.Reset, bits));
		}
	}

	[Fact]
	public void BCDCounter_CountsToNine_ThenOverflows()
	{
		BCDCounter counter = new();
		Bench bench = new(counter);
		Relay increment = bench.Driver(counter.Increment), check = bench.Driver(counter.Check), shift = bench.Driver(counter.Shift);
		Relay overflow = bench.Watch(counter.Overflow);
		Relay[] value = counter.Value.Select(bench.Watch).ToArray();
		int Read() { bench.Fire(shift); return Enumerable.Range(0, 4).Sum(k => bench.Fired(value[k]) ? 1 << k : 0); }
		for (int v = 1; v <= 9; v++)
		{
			bench.Fire(increment, check);
			Assert.False(bench.Fired(overflow));
			Assert.Equal(v, Read());
		}
		bench.Fire(increment, check);
		Assert.True(bench.Fired(overflow));
		Assert.Equal(0, Read());
		// reading doesn't change it
		Assert.Equal(0, Read());
	}

	[Fact]
	public void BCDCounter_GivesTheNinesComplement()
	{
		BCDCounter counter = new();
		Bench bench = new(counter);
		Relay increment = bench.Driver(counter.Increment), check = bench.Driver(counter.Check), complement = bench.Driver(counter.GetComplement);
		Relay[] bits = counter.Complement.Select(bench.Watch).ToArray();
		for (int v = 0; v <= 9; v++)
		{
			bench.Fire(complement);
			Assert.Equal(9 - v, Enumerable.Range(0, 4).Sum(k => bench.Fired(bits[k]) ? 1 << k : 0));
			bench.Fire(increment, check);
		}
	}

	[Fact]
	public void BidirectionalUnaryCounter_StepsBothWays()
	{
		BidirectionalUnaryCounter counter = new(5, 0, 0, 5, 5);
		Bench bench = new(counter);
		Relay up = bench.Driver(counter.Increment), down = bench.Driver(counter.Decrement), probe = bench.Driver(counter.Probe);
		Relay[] value = counter.Value.Select(bench.Watch).ToArray();
		int Read() { bench.Fire(probe); return Assert.Single(Enumerable.Range(0, 5), i => bench.Fired(value[i])); }
		Assert.Equal(0, Read());
		bench.Fire(up); bench.Fire(up); bench.Fire(up);
		Assert.Equal(3, Read());
		bench.Fire(down);
		Assert.Equal(2, Read());
		bench.Fire(up); bench.Fire(up);
		Assert.Equal(4, Read());
		bench.Fire(down); bench.Fire(down); bench.Fire(down); bench.Fire(down);
		Assert.Equal(0, Read());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Addressor_ProbeUnary_LeavesOnlyTheValuesOutputQuiet(bool quietCounting)
	{
		Addressor address = new(3, quietCounting: quietCounting);
		Bench bench = new(address);
		Relay increment = bench.Driver(address.Input), probe = bench.Driver(address.ProbeUnary);
		Relay[] offs = address.UnaryOff.Select(bench.Watch).ToArray();
		for (int v = 0; v < 8; v++)
		{
			bench.Fire(probe);
			for (int i = 0; i < 8; i++) { Assert.Equal(i != v, bench.Fired(offs[i])); }
			bench.Fire(increment);
			// counting fires no address output with quietCounting
			if (quietCounting) { Assert.DoesNotContain(offs, bench.Fired); }
		}
	}

	// Every entity a device's triggers target is among its entities (or the level would fail to save)
	internal static void AssertSelfContained(TriggerContraption device)
	{
		HashSet<Entity> entities = new(device.Entities, ReferenceEqualityComparer.Instance);
		foreach (Trigger trigger in entities.OfType<Trigger>())
		{
			Assert.All(trigger.Targets, target => Assert.Contains(target.Targeted, entities));
		}
	}

	[Fact]
	public void Addressor_Visualized_KeepsItsNotRelays()
	{
		Addressor address = new(3);
		address.AddVisualization();
		AssertSelfContained(address);
	}

	[Fact]
	public void Label_ShowAndHide_ChangeItsGroupsOpacity()
	{
		Label label = new("7", startHidden: true);
		Group group = Assert.Single(label.Entities.OfType<Group>());
		Assert.Equal(0, group.Opacity);
		double? Opacity(Input input) => Assert.IsType<ChangeOpacity<Group>>(Assert.Single(Assert.Single(input).Actions)).Opacity;
		Assert.Same(group, Assert.Single(label.Show).Targeted);
		Assert.Equal(100, Opacity(label.Show));
		Assert.Equal(0, Opacity(label.Hide));
	}

	[Fact]
	public void PhysicsRandom_SensorsAreSetOffOnlyByTheBalls_AndKicksPushTheFirstBall()
	{
		PhysicsRandom random = new(5000, 5000, balls: 3);
		var balls = random.Entities.OfType<Circle>().ToList();
		Assert.Equal(3, balls.Count);
		Assert.Equal(2, random.Hits.Length);
		foreach (Output hit in random.Hits)
		{
			ActivateTrigger sensor = Assert.Single(hit);
			Assert.Equal(TriggeredBy.Targets, sensor.TriggeredBy);
			Assert.Equal(balls, sensor.Targets.Select(t => ((Target<Shape>)t).Targeted));
		}
		for (int k = 0; k < 4; k++)
		{
			Bench bench = new(random);
			Relay kick = bench.Driver(random.Kick(k));
			Assert.Equal(balls[0], ((Target<Shape>)Assert.Single(kick.Output.Single().Targets.SelectMany(t => ((ActivateTrigger)((Target<Trigger>)t).Targeted).Targets))).Targeted);
		}
	}
}
