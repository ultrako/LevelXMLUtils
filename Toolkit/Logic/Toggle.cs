using System;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using LevelXML;
namespace LevelXMLUtils;
/// <summary>A row of bits that can be counted up, set, cleared and read, one Input or Output per bit.</summary>
public interface ICounter
{
    IList<Input> Inputs {get; protected set;}
    IList<Input> Sets {get; protected set;}
    IList<Input> Clears {get; protected set;}
    IList<Input> Probes {get; protected set;}
    IList<Output> Ons {get; protected set;}
}
// An instant toggle in 3 triggers
public abstract class Toggle : TriggerContraption {
	public virtual Input Input {get; protected set;} = new();
	public virtual Input Clear {get; protected set;} = new();
	public virtual Input Set {get; protected set;} = new();
	public virtual Output Off {get; protected set;} = new();
	public virtual Output On {get; protected set;} = new();
	
	protected double x,y,width,height;

	protected Toggle(double x, double y, double width, double height)
	{
		this.x = x;
		this.y = y;
		this.width = width;
		this.height = height;
	}
	public void AddVisualization()
	{
		Panel panel = new(x,y,width,height);
		Clear = Clear.Concat(panel.Reset);
		Set = Set.Concat(panel.Input);
		Off.Connect(panel.Reset);
		On.Connect(panel.Input);
		Entities = Entities.Concat(panel.Entities);
	}
}
public class InstantToggle : Toggle {
	public Input Probe;
	public Input ToggleIfOff; 
	public Output Used;	
	public InstantToggle(double x=0, double y=0, double width=5, double height=5) :
	base(x,y,width,height)
	{
		// A toggle in three triggers,
		Relay trigger = new(x,y,width,height, startDisabled: true);
		Relay enabler = new(x,y,width, height, startDisabled: true);
		Relay enablerenabler = new(x + width, y + height, 5, 5, startDisabled:false);
		// It disables itself when it fires, and prevents itself from being enabled
		trigger.Output.Connect(trigger.Disable);
		trigger.Output.Connect(enabler.Disable);

		// Because two triggers can't point to one with different actions,
		// there needs to be a second "relay" trigger that just enables the first trigger.
		enabler.Output.Connect(trigger.Enable);
		// We need to reactivate the enabler every fire, for the case that the output
		// disabled the enabler.
		enablerenabler.Output.Connect(enabler.Enable);
		// The input will enable the enabler, activate the trigger, and then activate the enabler.
		// The first time the input fires, the toggle will just get enabled.
		// The second time the input fires, the toggle will disable itself and
		// disable the enabler and do all of its output actions.
		Input = enablerenabler.Input.Concat(trigger.Input).Concat(enabler.Input);
		Off = trigger.Output;
		On = enabler.Output;
		// You can disable both triggers to clear the value,
		// and similary enable both to set.
		Clear = trigger.Disable.Concat(enabler.Disable);
		Set = trigger.Enable.Concat(enabler.Enable);
		// Trigger just the enabler and it won't change the state,
		// and it will tell you whether or not the toggle is set
		Probe = enabler.Input;
		// This one is good for saving shape count, if you want to enable a trigger
		// and then activate it, and you're using a toggle anyway,
		// you'd have to use a relay trigger, but
		// you can use the enablerenabler to enable it instead.
		Used = enablerenabler.Output;
		// As opposed to enabler, hitting this will send overflow and set it back to 0
		ToggleIfOff = trigger.Input;
		Entities = trigger.Entities.Concat(enabler.Entities).Concat(enablerenabler.Entities);
	}
}
// A toggle in 2 triggers; the catch is that half of the time it takes a frame to respond
public class DelayedToggle : Toggle
{
	public DelayedToggle (double x=0, double y=0, double width=100, double height=100) :
	base(x,y,width,height)
	{
		Relay trigger = new(x,y,width,height, startDisabled: true, delay:1d/30d);
		Relay enabler = new(x + width, y, width, height, startDisabled: false, delay:0);
		// Each trigger disables itself and enables the other one
		trigger.Output.Connect(trigger.Disable);
		enabler.Output.Connect(enabler.Disable);
		trigger.Output.Connect(enabler.Enable);
		enabler.Output.Connect(trigger.Enable);
		Input = trigger.Input.Concat(enabler.Input);
		Clear = trigger.Disable.Concat(enabler.Enable);
		Set = trigger.Enable.Concat(enabler.Disable);
		On = enabler.Output;
		Off = trigger.Output;
		Entities = trigger.Entities.Concat(enabler.Entities);
	}
}
/// <summary>
/// A binary counter: a row of InstantToggles, each one's Off (carry) feeding the next one's Input. Count up with
/// <see cref="Input"/>; <see cref="Overflow"/> fires when the last bit carries. The lists run from the lowest bit.
/// </summary>
public class InstantToggleChain : TriggerContraption, ICounter
{
	private readonly List<InstantToggle> toggles;
	public IReadOnlyList<InstantToggle> Toggles => toggles;
	public Input Input;
	public Input ShiftLeft;
	public IList<Input> Inputs {get; set;}
	public IList<Input> Clears {get; set;}
	public IList<Input> Sets {get; set;}
	public IList<Input> Probes {get; set;}
	public IList<Output> Ons {get; set;}
	public IList<Output> Offs;
	public IList<Output> Useds;
	public Output Overflow;
	public InstantToggleChain(int n=1, double x=0, double y=0, double width=5, double height=5)
	{
		toggles = Enumerable.Range(0, n).Reverse().Select(index => new InstantToggle(x+width*index, y, width, height)).ToList();
		foreach ((InstantToggle first, InstantToggle second) in toggles.Zip(toggles.Skip(1)))
		{
			first.Off.Connect(second.Input);
		}
		Input = toggles.First().Input;
		ShiftLeft = new(toggles.SelectMany(toggle => toggle.ToggleIfOff));
		ShiftLeft.Reverse();
		Overflow = toggles.Last().Off;
		Inputs = toggles.Select(toggle => toggle.Input).ToList();
		Clears = toggles.Select(toggle => toggle.Clear).ToList();
		Sets = toggles.Select(toggle => toggle.Set).ToList();
		Probes = toggles.Select(toggle => toggle.Probe).ToList();
		Offs = toggles.Select(toggle => toggle.Off).ToList();
		Ons = toggles.Select(toggle => toggle.On).ToList();
		Useds = toggles.Select(toggle => toggle.Used).ToList();
		Entities = toggles.SelectMany(toggle => toggle.Entities).ToList();
	}

	public void AddVisualization()
	{
		foreach (Toggle toggle in toggles)
		{
			toggle.AddVisualization();
		}
		Clears = toggles.Select(toggle => toggle.Clear).ToList();
		Sets = toggles.Select(toggle => toggle.Set).ToList();
		// keeps what a subclass added (Addressor's NOT relays), adding the toggles' panels
		Entities = Entities.Union<Entity>(toggles.SelectMany(toggle => toggle.Entities), ReferenceEqualityComparer.Instance).ToList();
	}
}

/// <summary>Like <see cref="InstantToggleChain"/>, from DelayedToggles: fewer triggers, but a carry can take a frame.</summary>
public class DelayedToggleChain : TriggerContraption
{
	private readonly List<DelayedToggle> toggles;
	public IReadOnlyList<DelayedToggle> Toggles => toggles;
	public Input Input;
	public List<Input> Inputs;
	public List<Input> Clears;
	public List<Input> Sets;
	public List<Output> Ons;
	public List<Output> Offs;
	public Output Overflow;
	public DelayedToggleChain(int n=1, double x=0, double y=0, double width=100, double height=100)
	{
		toggles = Enumerable.Range(0, n).Select(index => new DelayedToggle(x+width*index, y, width, height)).ToList();
		foreach ((Toggle first, Toggle second) in toggles.Zip(toggles.Skip(1)))
		{
			first.Off.Connect(second.Input);
		}
		Input = toggles.First().Input;
		Overflow = toggles.Last().Off;
		Inputs = toggles.Select(toggle => toggle.Input).ToList();
		Clears = toggles.Select(toggle => toggle.Clear).ToList();
		Sets = toggles.Select(toggle => toggle.Set).ToList();
		Offs = toggles.Select(toggle => toggle.Off).ToList();
		Ons = toggles.Select(toggle => toggle.On).ToList();
		Entities = toggles.SelectMany(toggle => toggle.Entities).ToList();
	}
}
