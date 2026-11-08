using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
using LevelXMLUtils.Controls;
namespace LevelXMLUtils.Computer;

public class Multiplier : TriggerContraption
{
	public IList<Input> SetAs;
	public IList<Input> ClearAs;
	public IList<Input> SetBs;
	public IList<Input> ClearBs;
	public IList<Output> Ons;
	public IList<Output> Offs;
	public Input PowerLast;
	public Output Overflow;
	public Multiplier(int n, double x=0, double y=0, double width=100, double height=100, bool visualized=true)
	{
		MultiRelay leftProduct = new(n:n, x,y, width,height, startDisabled: true);
		MultiRelay rightProduct = new(n:n, x, y+height, width,height, startDisabled: true);
		InstantToggleChain adder = new(n:n, x,y+(height*2), width,height);
		Relay AddRightProduct = new(x-width, y, width, height);
		// The reason this row exists is because one trigger can't do the same action more than once
		MultiRelay shifts = new(n:n, x,y-height, 5, 5, startDisabled: false);
		shifts.Outputs.Connect(adder.ShiftLeft);
		// PowerLast should, starting from the MSB of leftProduct and going to the less significant bits:
		// Add rightProduct to the result if that bit is set,
		// and in either case shift the result left.
		// So we need to interleave rightProduct.Inputs with shifts.Inputs;
		PowerLast = new Input(leftProduct.Inputs
			.Zip(shifts.Inputs, (lhs, rhs) => new[] {lhs, rhs}).SelectMany(l => l).SelectMany(l => l).ToList()
		);
		PowerLast.Reverse();
		leftProduct.Outputs.Connect(AddRightProduct.Input);
		AddRightProduct.Output.Connect(rightProduct.Inputs);
		rightProduct.Outputs.Connect(adder.Inputs);
		SetAs = leftProduct.Enables;
		ClearAs = leftProduct.Disables;
		SetBs = rightProduct.Enables;
		ClearBs = rightProduct.Disables;
		Ons = adder.Ons;
		Offs = adder.Offs;
		Overflow = adder.Overflow;
		Entities = adder.Entities.Concat(leftProduct.Entities).Concat(rightProduct.Entities).Concat(AddRightProduct.Entities).Concat(shifts.Entities);
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
