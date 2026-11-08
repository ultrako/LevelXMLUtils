using LevelXML;

namespace LevelXMLUtils;

public class Not : TriggerContraption {
	public Input Reset { get; }
	public Input Input { get; }
	public Input PowerLast { get; }
	public Output Output { get; }
	// Fire Reset, then the input, then PowerLast, in that order: the output fires at PowerLast unless the input came. Reset and
	// PowerLast are two signals because one trigger can't both enable this relay and fire it. In a rotated order it answers a frame
	// late; mixed up, it doesn't work.

	public Not(double x=0, double y=0, double width=5, double height=5) {
		Relay relay = new(x, y, width, height);
		Reset = relay.Enable;
		Input = relay.Disable;
		PowerLast = relay.Input;
		Output = relay.Output;
		Entities = relay.Entities;
	}
}
