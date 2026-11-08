using LevelXML;

namespace LevelXMLUtils;

/// <summary>XOR in three triggers (a NOT of an XNOR): Reset, the inputs (A before B), then PowerLast.</summary>
public class Xor : TriggerContraption {
	public Input Reset { get; }
	public Input InputA { get; }
	public Input InputB { get; }
	public Input PowerLast { get; }
	public Output Output { get; }
	public Xor(double x=0, double y=0, double width=5, double height=5) {
		Xnor xnor = new(x, y, width, height);
		Not not = new(x+width, y, width, height);
		Reset = xnor.Reset.Concat(not.Reset);
		InputA = xnor.InputA;
		InputB = xnor.InputB;
		PowerLast = xnor.PowerLast.Concat(not.PowerLast);
		xnor.Output.Connect(not.Input);
		Output = not.Output;
		Entities = xnor.Entities.Concat(not.Entities);
	}
}
