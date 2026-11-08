using LevelXML;
using Xunit;
namespace LevelXMLUtils.Tests;

// The AND / OR / NOT / XNOR / XOR recipes in Toolkit/README.md, wired exactly as written there. The relays a, b, start
// and finish stand for whatever fires those signals in a level (buttons, sensors, other devices), and result for
// whatever the gate drives. Each Pulse is one firing; several ids in one Pulse fire in that order, the way triggers
// fire in their target list's order within a frame.
public class LogicRecipeTests
{
	private readonly Relay a = new(), b = new(), start = new(), finish = new(), result = new();

	private TriggerGraph Graph(params TriggerContraption[] devices) =>
		TriggerGraph.FromTriggers(new[] { a, b, start, finish, result }.Concat(devices).SelectMany(d => d.Entities).OfType<ActivateTrigger>());

	private static string Id(Relay relay) => TriggerGraph.IdFor(relay.Output.Single());

	private bool Fired(TriggerGraph graph) => graph.LastFired.Contains(Id(result));

	[Fact]
	public void And_FiresWhenBArrivesWhileAIsSet()
	{
		Relay and = new(startDisabled: true);
		a.Output.Connect(and.Enable);
		b.Output.Connect(and.Input);
		finish.Output.Connect(and.Disable);
		and.Output.Connect(result.Input);
		TriggerGraph graph = Graph(and);

		graph.Pulse(Id(b));
		Assert.False(Fired(graph));
		graph.Pulse(Id(a), Id(b), Id(finish));
		Assert.True(Fired(graph));
		// b before a in the frame: too early, a is stored only after b has gone through
		graph.Pulse(Id(b), Id(a), Id(finish));
		Assert.False(Fired(graph));
		// finish cleared it
		graph.Pulse(Id(b));
		Assert.False(Fired(graph));
	}

	[Fact]
	public void Or_FiresOnceHoweverManyInputsArrive()
	{
		Relay or = new();
		or.Output.Connect(or.Disable);
		start.Output.Connect(or.Enable);
		a.Output.Connect(or.Input);
		b.Output.Connect(or.Input);
		or.Output.Connect(result.Input);
		TriggerGraph graph = Graph(or);

		graph.Pulse(Id(start), Id(a), Id(b));
		Assert.True(Fired(graph));
		// it ran its two arms (disable itself, fire result) once: b found it disabled
		Assert.Equal(2, graph.ActionsBySource[Id(or)]);

		graph.Pulse(Id(start), Id(b));
		Assert.True(Fired(graph));
		graph.Pulse(Id(start));
		Assert.False(Fired(graph));
	}

	[Fact]
	public void Not_FiresAtFinishOnlyIfAStayedAway()
	{
		Relay not = new();
		start.Output.Connect(not.Enable);
		a.Output.Connect(not.Disable);
		finish.Output.Connect(not.Input);
		not.Output.Connect(result.Input);
		TriggerGraph graph = Graph(not);

		graph.Pulse(Id(start), Id(finish));
		Assert.True(Fired(graph));
		graph.Pulse(Id(start), Id(a), Id(finish));
		Assert.False(Fired(graph));
		// a after finish is too late for this round
		graph.Pulse(Id(start), Id(finish), Id(a));
		Assert.True(Fired(graph));
	}

	[Theory]
	[InlineData(false, false, true)]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(true, true, true)]
	public void Xnor_FiresForNeitherOrBoth(bool sendA, bool sendB, bool expected)
	{
		Relay neither = new();
		Relay both = new(startDisabled: true);
		start.Output.Connect(neither.Enable);
		a.Output.Connect(new Input(neither.Disable.Concat(both.Enable)));
		b.Output.Connect(new Input(neither.Disable.Concat(both.Input)));
		finish.Output.Connect(new Input(neither.Input.Concat(both.Disable)));
		neither.Output.Connect(result.Input);
		both.Output.Connect(result.Input);
		TriggerGraph graph = Graph(neither, both);

		graph.Pulse(new[] { Id(start) }.Concat(sendA ? new[] { Id(a) } : Array.Empty<string>()).Concat(sendB ? new[] { Id(b) } : Array.Empty<string>()).Append(Id(finish)).ToArray());
		Assert.Equal(expected, Fired(graph));
	}

	[Fact]
	public void Xnor_NeedsAToArriveBeforeB()
	{
		Relay neither = new();
		Relay both = new(startDisabled: true);
		start.Output.Connect(neither.Enable);
		a.Output.Connect(new Input(neither.Disable.Concat(both.Enable)));
		b.Output.Connect(new Input(neither.Disable.Concat(both.Input)));
		finish.Output.Connect(new Input(neither.Input.Concat(both.Disable)));
		neither.Output.Connect(result.Input);
		both.Output.Connect(result.Input);
		TriggerGraph graph = Graph(neither, both);

		graph.Pulse(Id(start), Id(b), Id(a), Id(finish));
		Assert.False(Fired(graph));
	}

	[Theory]
	[InlineData(false, false, false)]
	[InlineData(true, false, true)]
	[InlineData(false, true, true)]
	[InlineData(true, true, false)]
	public void Xor_IsParity_InEitherOrder(bool sendA, bool sendB, bool expected)
	{
		InstantToggle parity = new();
		Relay read = new(startDisabled: true);
		read.Output.Connect(read.Disable);
		start.Output.Connect(new Input(parity.Clear.Concat(read.Disable)));
		a.Output.Connect(parity.Input);
		b.Output.Connect(parity.Input);
		finish.Output.Connect(new Input(read.Enable.Concat(parity.Probe)));
		parity.On.Connect(read.Input);
		read.Output.Connect(result.Input);
		TriggerGraph graph = Graph(parity, read);

		foreach (bool bFirst in new[] { false, true })
		{
			string[] inputs = (sendA ? new[] { Id(a) } : Array.Empty<string>()).Concat(sendB ? new[] { Id(b) } : Array.Empty<string>()).ToArray();
			// a whole round in one pulse: result must fire for the probe at finish, not when an input sets the toggle
			graph.Pulse(inputs.Length == 0 ? new[] { Id(start) } : new[] { Id(start) }.Concat(bFirst ? inputs.Reverse() : inputs).ToArray());
			Assert.False(Fired(graph));
			graph.Pulse(Id(finish));
			Assert.Equal(expected, Fired(graph));
		}
	}
}
