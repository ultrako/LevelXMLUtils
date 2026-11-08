using LevelXML;

namespace LevelXMLUtils;

/// <summary>OR that fires once however many inputs arrive: the relay disables itself when it fires, and Reset (before the inputs) sets
/// it up again. Only needed when the output mustn't fire twice; otherwise connect the inputs straight to what they drive.</summary>
public class Or : TriggerContraption 
{
	public Input Reset { get; }
	public Input Input { get; }
	public Output Output { get; }
	public Or(double x=0, double y=0, double width=5, double height=5) {
		Relay relay = new(x, y, width, height);
		relay.Output.Connect(relay.Disable);
		Reset = relay.Enable;
		Input = relay.Input;
		Output = relay.Output;
		Entities = relay.Entities;
	}
}
