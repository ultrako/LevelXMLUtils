using LevelXML;

namespace LevelXMLUtils;

/// <summary>AND in one trigger: InputA enables it and InputB fires it, so A has to come before B in the frame. While A is set, every
/// InputB fires the output; PowerLast (after the inputs) forgets A.</summary>
public class And : TriggerContraption {
	public Input InputA { get; }
	public Input InputB { get; }
	public Output Output { get; }
	public Input PowerLast { get; }
	public And(double x=0, double y=0, double width=5, double height=5) {
		Relay relay = new(x, y, width, height, startDisabled: true);
		InputA = relay.Enable;
		InputB = relay.Input;
		Output = relay.Output;
		PowerLast = relay.Disable;
		Entities = relay.Entities;
	}
}
