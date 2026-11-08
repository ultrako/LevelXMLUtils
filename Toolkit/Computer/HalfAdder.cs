using LevelXML;

using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
using LevelXMLUtils.Controls;
namespace LevelXMLUtils.Computer;

// Ordered HalfAdder in 3 triggers
public class HalfAdder : TriggerContraption {
	public Input InputA {get; set;}
	public Input InputB {get; set;}
	public Output Sum {get; set;}
	public Output Carry {get; set;}
	public Input Reset {get; set;}
	public Input PowerLast {get; set;}
	public HalfAdder(double x=0, double y=0, double width=5, double height=5) {
		Not nor = new(x, y, width, height);
		And and = new(x+width, y, width, height);
		Not not = new(x+width*2, y, width, height);
		Reset = nor.Reset.Concat(not.Reset);
		InputA = nor.Input.Concat(and.InputA);
		InputB = nor.Input.Concat(and.InputB);
		PowerLast = nor.PowerLast.Concat(and.PowerLast).Concat(not.PowerLast);
		nor.Output.Connect(not.Input);
		and.Output.Connect(not.Input);
		Sum = not.Output;
		Carry = and.Output;
		Entities = nor.Entities.Concat(and.Entities).Concat(not.Entities);
	}
}
