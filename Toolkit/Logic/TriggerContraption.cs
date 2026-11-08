using LevelXML;
namespace LevelXMLUtils;
// Devices ("trigger contraptions") are wired together with Inputs and Outputs.
// An Output is the triggers that fire when it is "on". An Input is not a trigger: it is a list of target actions
// (usually activate, enable or disable a trigger) that something else performs to drive the device.
// Connecting an Output to an Input adds the Input's actions to every trigger of the Output, so no relay trigger is
// spent in between.
///<summary>
/// The things you need to target with your trigger in order to "input" to the machine
/// This will be mostly triggers
///</summary>
public class Input : List<Target> 
{
	public Input() { }
	public Input(IEnumerable<Target> lst) : this() { 
		foreach (Target target in lst) { Add(target); }
	}
	public Input Concat(Input other) {
		Input input = new(this);
		foreach (Target target in other) { input.Add(target); }
		return input;
	}
	public Input(Output lst) : 
		this(lst.Select(trigger => new Target<ActivateTrigger>(trigger, new Activate()))) {}
}
///<summary>
/// The triggers that fire when the output is considered "on"
///</summary>
public class Output : List<ActivateTrigger>
{ 
	public void Connect(Input input, bool prepend=false) 
	{
		foreach (ActivateTrigger trigger in this) {
			foreach (Target target in input) {
				if (prepend) {
					trigger.InsertTarget(0,target);
				} else {
					trigger.AddTarget(target);
				}
			}
		}
	}
	public void Connect(IList<Input> inputs, bool prepend=false)
	{
		foreach (Input input in inputs)
		{
			Connect(input, prepend);
		}
	}
	public Output() : base() {}
	public Output(IEnumerable<ActivateTrigger> lst) : this() {
		foreach (ActivateTrigger trigger in lst) { Add(trigger); }
	}
	public Output Concat(Output other) {
		Output output = new(this);
		foreach (ActivateTrigger trigger in other) { output.Add(trigger); }
		return output;
	}
}
/// <summary>
/// A device made of triggers: its <see cref="Entities"/> go in the level, and it exposes whatever named
/// <see cref="Input"/>s and <see cref="Output"/>s it needs as fields or properties.
/// </summary>
public abstract class TriggerContraption
{
	/// <summary>
	/// All of the entities (mainly triggers) in the contraption
	/// </summary>
	public IEnumerable<Entity> Entities {get; protected set;} = default!;
}
public static class Extensions
{
	// An extension method to make working with lists of outputs and inputs easier,
	// for multi bit gates.
	// Connecting a list of outputs to a single input
	public static void Connect(this IList<Output> outputs, Input input)
	{
		foreach (Output output in outputs) { output.Connect(input); }
	}
	// Connecting a list of outputs to a list of inputs (pairwise)
	public static void Connect(this IList<Output> outputs, IList<Input> inputs)
	{
		foreach ((Output output, Input input) in outputs.Zip(inputs))
		{
			output.Connect(input);
		}
	}
}
