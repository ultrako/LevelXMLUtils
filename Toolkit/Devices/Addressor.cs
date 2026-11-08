using LevelXML;
namespace LevelXMLUtils.Devices;

// This is a binary counter (a toggle chain) that additionally can output unary value.
public class Addressor : InstantToggleChain
{
    public Input ProbeUnary;
    public IList<Output> UnaryOff;
    /// <param name="quietCounting">A bit's "is 1" signal is its toggle's On output, which also fires whenever counting turns the bit on, so every
    /// increment fires the UnaryOff outputs of the bits it turns on. With quietCounting the signal goes through a gate relay per bit that is open
    /// only while ProbeUnary runs: counting costs one action per bit turned on instead of all of those outputs' targets.</param>
    public Addressor(int n=1, double x=0, double y=0, double width=5, double height=5, bool visualized=false, bool visualizedAddress=false, bool quietCounting=false) :
    base(n, x, y, width, height)
    {
        var addresses = (int)Math.Pow(2, n);
        MultiRelay notGates = new(n, x, y, width, height);
        Relay notGateReseter = new(x-width, y, width, height);
        notGateReseter.Output.Connect(notGates.Enables);
        IList<Output> isOne = Ons;
        Input probeOpen = new(), probeClose = new();
        if (quietCounting)
        {
            MultiRelay gates = new(n, x, y, width, height, startDisabled: true);
            for (int k = 0; k < n; k++) { Ons[k].Connect(gates.Inputs[k]); }
            Relay open = new(x, y, width, height), close = new(x, y, width, height);
            open.Output.Connect(gates.Enables);
            close.Output.Connect(gates.Disables);
            isOne = gates.Outputs;
            (probeOpen, probeClose) = (open.Input, close.Input);
            Entities = Entities.Concat(gates.Entities).Concat(open.Entities).Concat(close.Entities);
        }
        isOne.Connect(notGates.Disables);
        UnaryOff = Enumerable.Range(0, addresses).Select(
            i => new Output(Enumerable.Range(0, n).Select(
                k => ((i >> k) & 1) switch
                {
                    0 => isOne[k],
                    _ => notGates.Outputs[k]
                }
            ).SelectMany(output => output))
        ).ToList();
        ProbeUnary = new Input(
            (new [] {probeOpen, notGateReseter.Input})
            .Concat(Probes)
            .Concat(notGates.Inputs)
            .Append(probeClose)
            .SelectMany(input => input));
        Entities = Entities.Concat(notGates.Entities).Concat(notGateReseter.Entities);
        if (visualizedAddress)
        {
            PanelRow position = new(addresses, x, y + height, width, height);
            ProbeUnary = new Input(position.Inputs.SelectMany(input => input)).Concat(ProbeUnary);
            UnaryOff.Connect(position.Resets);
            Entities = Entities.Concat(position.Entities);
        }
    }
}