using LevelXML;

namespace LevelXMLUtils;

/// <summary>One trigger fired by other triggers: <see cref="Input"/> fires it, <see cref="Enable"/> and <see cref="Disable"/> set
/// whether firing it does anything. The usual building block for stateful logic.</summary>
public class Relay : TriggerContraption {
	public Input Input { get; }
	public Output Output { get; }
	public Input Enable;
	public Input Disable;
	private RepeaterTrigger trigger;

	// Reminder that changing this property after calling
	// addVisualisation will not change the default panel color
	public bool StartDisabled 
	{
		get { return (bool)trigger.StartDisabled!; }
		set { trigger.StartDisabled = value; }
	}
	private double x,y,width,height;
	public Relay(double x=0, double y=0, double width=5, double height=5, bool startDisabled=false, double delay=0) {
		this.x = x;
		this.y = y;
		this.width = width;
		this.height = height;
		// This class basically maps a regular trigger to this interface I've defined
		trigger = new(x, y, width, height);
		trigger.Delay = delay;
		StartDisabled = startDisabled;
		Input = new Input() 
			{ new Target<Trigger>(trigger, new Activate()) };
		Enable = new Input()
			{ new Target<Trigger>(trigger, new Enable()) };
		Disable = new Input()
			{ new Target<Trigger>(trigger, new Disable()) };
		Output = new Output() { trigger };
		Entities = new List<Entity>() { trigger };
	}

	// I often do this manually, so I'm adding a method for it
	public void AddVisualization()
	{
		Panel panel = new(x,y,width,height, startDisabled:StartDisabled);
		Entities = Entities.Concat(panel.Entities);
		Enable = Enable.Concat(panel.Input);
		Disable = Disable.Concat(panel.Reset);
	}
}

/// <summary>A row of n relays, side by side from (x, y), with their inputs and outputs as lists in the same order.</summary>
public class MultiRelay : TriggerContraption
{
	public IList<Relay> Relays { get; }
	public IList<Input> Inputs;
	public IList<Output> Outputs;
	public IList<Input> Enables;
	public IList<Input> Disables;

	public MultiRelay(int n, double x=0, double y=0, double width=100, double height=100, bool startDisabled=false)
	{
		Relays = Enumerable.Range(0, n).Select(i => new Relay(x + width * i, y, width, height, startDisabled)).ToList();
		Inputs = Relays.Select(relay => relay.Input).ToList();
		Outputs = Relays.Select(relay => relay.Output).ToList();
		Enables = Relays.Select(relay => relay.Enable).ToList();
		Disables = Relays.Select(relay => relay.Disable).ToList();
		Entities = Relays.SelectMany(relay => relay.Entities).ToList();
	}

	/// <summary>Starts the relays disabled where val has a 1 bit, the lowest bit first.</summary>
	public void SetValue(uint val)
	{
		if (val >= Math.Pow(2, Relays.Count)) { throw new ArgumentOutOfRangeException(nameof(val), "val was higher than the row of relays could store!"); }
		for (int index = 0; index < Relays.Count; index++) { Relays[index].StartDisabled = ((val >> index) % 2) == 1; }
	}

	public void AddVisualization()
	{
		foreach (Relay relay in Relays) { relay.AddVisualization(); }
		Entities = Relays.SelectMany(relay => relay.Entities).ToList();
		Enables = Relays.Select(relay => relay.Enable).ToList();
		Disables = Relays.Select(relay => relay.Disable).ToList();
	}
}
