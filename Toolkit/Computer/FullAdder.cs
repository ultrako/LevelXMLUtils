using LevelXML;

using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
using LevelXMLUtils.Controls;
namespace LevelXMLUtils.Computer;

// Ordered FullAdder in 4 triggers
public class FullAdder : TriggerContraption {
	public Input InputA {get; set;}
	public Input InputB {get; set;}
	public Input Carry_in {get; set;}
	public Input Clear {get; set;}
	public Output Sum {get; set;}
	public Output Carry_out {get; set;}
	public Input Reset {get; set;}
	public Input PowerLast {get; set;}
	public FullAdder(double x=0, double y=0, double width=5, double height=5) {
		InstantToggle toggle = new(x,y,width,height);
		// We want a trigger that fires once if 2 or 3 of our inputs fire.
		Relay sumRelay = new(x+width*2, y, width, height, startDisabled: true);
		// sumRelay needs to start disabled.
		Reset = sumRelay.Disable;
		// If you want the adder to not remember what you just added,
		// then have your powerFirst also Clear
		Clear = toggle.Clear;
		//InputA = toggle.Set;
		InputA = toggle.Input;
		InputB = toggle.Input;
		Carry_in = toggle.Input;
		Carry_out = toggle.Off;
		PowerLast = sumRelay.Enable.Concat(toggle.Probe);
		toggle.On.Connect(sumRelay.Input);
		Sum = sumRelay.Output;
		Entities = toggle.Entities.Concat(sumRelay.Entities);
	}
}
