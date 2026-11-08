using LevelXML;
using Xunit;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Tests;

public class ColorLockTests
{
	// Clicks the lock's buttons (by color name) in the trigger graph and says whether it unlocked
	sealed class Clicker
	{
		readonly TriggerGraph graph;
		readonly Dictionary<string, string> buttons;
		readonly string marker;
		public Clicker(int[] code)
		{
			ColorLock lockBox = new(code, 450, 250);
			Relay seen = new(0, 0, 5, 5);
			seen.Output.Connect(seen.Disable);
			lockBox.Unlocked.Connect(seen.Input);
			var triggers = lockBox.Entities.Concat(seen.Entities).OfType<ActivateTrigger>().ToList();
			graph = TriggerGraph.FromTriggers(triggers);
			var clicks = triggers.Where(t => t.TriggeredBy == TriggeredBy.Click).ToList();
			buttons = ColorLock.Colors.Select((c, i) => (c.name, id: TriggerGraph.IdFor(clicks[i]))).ToDictionary(t => t.name, t => t.id);
			marker = TriggerGraph.IdFor(seen.Output.Single());
		}
		public void Click(string colors) { foreach (string name in colors.Split(',')) { graph.Pulse(buttons[name.Trim()]); } }
		public bool Unlocked => !graph.IsEnabled(marker);
		public bool Responds(string name) => graph.IsEnabled(buttons[name]);
	}

	static readonly int[] Code = ColorLock.Parse("blue,yellow,red,blue,green,red");

	[Fact]
	public void TheCode_Unlocks()
	{
		Clicker lockBox = new(Code);
		lockBox.Click("blue,yellow,red,blue,green");
		Assert.False(lockBox.Unlocked);
		lockBox.Click("red");
		Assert.True(lockBox.Unlocked);
		// unlocked: the buttons are switched off
		Assert.False(lockBox.Responds("red"));
	}

	[Theory]
	[InlineData("green,yellow,red,blue,green,red")]
	[InlineData("blue,yellow,red,blue,green,green")]
	[InlineData("blue,yellow,green,blue,green,red")]
	public void AWrongClickAnywhere_FailsTheAttempt(string clicks)
	{
		Clicker lockBox = new(Code);
		lockBox.Click(clicks);
		Assert.False(lockBox.Unlocked);
		// the attempt is over: the whole code, from scratch, unlocks
		lockBox.Click("blue,yellow,red,blue,green,red");
		Assert.True(lockBox.Unlocked);
	}

	[Fact]
	public void AWrongClick_IsNotRevealedBeforeTheAttemptEnds()
	{
		// a wrong second click doesn't start over at once: the code clicked from the third click on doesn't unlock (the attempt ends after six)
		Clicker lockBox = new(Code);
		lockBox.Click("blue,green");
		lockBox.Click("blue,yellow,red,blue,green,red");
		Assert.False(lockBox.Unlocked);
	}

	[Fact]
	public void TheClickThatEndsAFailedAttempt_DoesntStartTheNext()
	{
		// a failed attempt ending in blue (the code's first color), then the rest of the code: not unlocked, the blue didn't count
		Clicker lockBox = new(Code);
		lockBox.Click("red,red,red,red,red,blue");
		lockBox.Click("yellow,red,blue,green,red");
		Assert.False(lockBox.Unlocked);
	}
}
