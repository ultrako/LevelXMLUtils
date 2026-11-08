using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
using LevelXMLUtils.Controls;
namespace LevelXMLUtils.Computer;
public class MultiAdder : TriggerContraption
{
    private readonly List<FullAdder> adders;
    public IList<Input> InputAs;
    public IList<Input> InputBs;
	public IList<Input> Clears;
    public IList<Output> Sums;
    public Output Overflow;
    public MultiAdder(int n, double x=0, double y=0, double width=100, double height=100) 
    {
        adders = Enumerable.Range(0, n).Select(index => new FullAdder(x+width*index, y, width, height)).ToList();
        Entities = adders.SelectMany(part => part.Entities).ToList();
        foreach ((FullAdder first, FullAdder second) in adders.Take(adders.Count - 1).Zip(adders.Skip(1)))
        {
            first.Carry_out.Connect(second.Carry_in);
        }
        Overflow = adders.Last().Carry_out;
		Clears = adders.Select(adder => adder.Clear).ToList();
		InputAs = adders.Select(adder => adder.InputA).ToList();
		InputBs = adders.Select(adder => adder.InputB).ToList();
		Sums = adders.Select(adder => adder.Sum).ToList();
    }
}
