using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
using LevelXMLUtils.Controls;
namespace LevelXMLUtils.Computer;

public class ALU : TriggerContraption
{
	public Input Multiply;
	public Input Add;
	public IList<Input> SetAs;
	public IList<Input> ClearAs;
	public IList<Input> SetBs;
	public IList<Input> ClearBs;
	public IList<Input> ClearCs;
	public IList<Output> Ons;
	public IList<Output> Offs;
	public Output Overflow;
	public ALU(int n, double x=0, double y=0, double width=100, double height=100, bool visualized=true)
	{
		MultiRelay counterA = new(n:n, x,y, width,height, startDisabled: true);
		MultiRelay counterB = new(n:n, x, y+height, width,height, startDisabled: true);
		InstantToggleChain adder = new(n:n, x,y+(height*2), width,height);
		Relay addCounterB = new(x-width, y, width, height, startDisabled: true);
		MultiRelay addA = new(n:n, x,y,width,height, startDisabled:true);
		// As one input can't do two things to the same trigger,
		// I am adding a relay that disables AddA,
		// and the same for addCounterB
		Relay disableAddA = new(x,y,width,height, startDisabled:false);
		Relay disableAddCounterB = new(x-width, y, width, height, startDisabled: false);
		disableAddA.Output.Connect(addA.Disables);
		disableAddCounterB.Output.Connect(addCounterB.Disable);
		counterA.Outputs.Connect(addA.Inputs);
		addA.Outputs.Connect(adder.Inputs);
		// The reason this row exists is because one trigger can't do the same action more than once
		MultiRelay shifts = new(n:n, x,y-height, 5, 5, startDisabled: false);
		shifts.Outputs.Connect(adder.ShiftLeft);
		// PowerLast should, starting from the MSB of leftProduct and going to the less significant bits,
		// Add B to the result if that bit is set,
		// and in either case shift the result left.
		// So we need to interleave rightProduct.Inputs with shifts.Inputs;
		Multiply = new Input(counterA.Inputs
			.Zip(shifts.Inputs, (lhs, rhs) => new[] {lhs, rhs}).SelectMany(l => l).SelectMany(l => l).ToList()
		);
		Multiply.Reverse();
		Multiply = addCounterB.Enable.Concat(Multiply).Concat(disableAddCounterB.Input);
		Add = new Input(addA.Enables.Concat(counterA.Inputs).Concat(counterB.Inputs).Concat(new List<Input> {disableAddA.Input}).SelectMany(l => l));
		counterA.Outputs.Connect(addCounterB.Input);
		addCounterB.Output.Connect(counterB.Inputs);
		counterB.Outputs.Connect(adder.Inputs);
		SetAs = counterA.Enables;
		ClearAs = counterA.Disables;
		SetBs = counterB.Enables;
		ClearBs = counterB.Disables;
		ClearCs = adder.Clears;
		Ons = adder.Ons;
		Offs = adder.Offs;
		Overflow = adder.Overflow;
		Entities = adder.Entities.Concat(counterA.Entities).Concat(counterB.Entities).Concat(addA.Entities).Concat(addCounterB.Entities).Concat(shifts.Entities).Concat(disableAddA.Entities).Concat(disableAddCounterB.Entities);
		if (visualized)
		{
			PanelRow panelsA = new(n, x,y, width,height);
			SetAs = SetAs.Zip(panelsA.Inputs, (a, b) => a.Concat(b)).ToList();
			ClearAs = ClearAs.Zip(panelsA.Resets, (a, b) => a.Concat(b)).ToList();
			Entities = Entities.Concat(panelsA.Entities);
			PanelRow panelsB = new(n, x,y+height, width,height);
			SetBs = SetBs.Zip(panelsB.Inputs, (a, b) => a.Concat(b)).ToList();
			ClearBs = ClearBs.Zip(panelsB.Resets, (a, b) => a.Concat(b)).ToList();
			Entities = Entities.Concat(panelsB.Entities);
		}
	}
}
