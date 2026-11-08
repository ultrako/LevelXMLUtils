using LevelXML;

namespace LevelXMLUtils;

/// <summary>XNOR in two triggers: a NOR ("neither", in any order) and an AND ("both", A before B) driving one output. Reset, the
/// inputs, then PowerLast.</summary>
public class Xnor : TriggerContraption {
	public Input Reset { get; }
	public Input InputA { get; }
	public Input InputB { get; }
	public Input PowerLast { get; }
	public Output Output { get; }
	public Xnor(double x=0, double y=0, double width=5, double height=5) {
		And and = new(x+width, y, width, height);
		Not nor = new(x, y, width, height);
		InputA = and.InputA.Concat(nor.Input);
		InputB = and.InputB.Concat(nor.Input);
		Reset = nor.Reset;
		PowerLast = nor.PowerLast.Concat(and.PowerLast);
		Output = nor.Output.Concat(and.Output);
		Entities = and.Entities.Concat(nor.Entities);
	}
}
