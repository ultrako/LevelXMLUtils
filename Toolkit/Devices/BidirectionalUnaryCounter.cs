using LevelXML;

namespace LevelXMLUtils.Devices;

/// <summary>
/// A unary counter that takes two triggers per state,
/// can be both incremented and decremented.
/// </summary>
public class BidirectionalUnaryCounter : TriggerContraption
{
    public Input Increment;
    public Input Decrement;
    public Input Reset => Set[0];
    public Input Probe;
    public IList<Input> Set;
    public IList<Output> Value;
    public IList<Output> OffSignals;
    public BidirectionalUnaryCounter(int n, double x, double y, double width, double height)
    {
        MultiRelay input = new(n, x, y, width, height, startDisabled:true);
        MultiRelay state = new(n, x, y+height, width, height, startDisabled:true);
        input.Relays[0].StartDisabled = false;
        state.Relays[0].StartDisabled = false;
        IEnumerable<(Relay, Relay)> pairs = input.Relays.Zip(state.Relays);
        for (int i = 1; i < n-1; i++)
        {
            input.Outputs[i].Connect(state.Enables[i-1]);
            input.Outputs[i].Connect(state.Enables[i+1]);
        }
        input.Outputs[0].Connect(state.Enables[0]);
        input.Outputs[0].Connect(state.Enables[1]);
        input.Outputs[n-1].Connect(state.Enables[n-1]);
        input.Outputs[n-1].Connect(state.Enables[n-2]);
        
        foreach((Relay inputRelay, Relay stateRelay) in pairs)
        {
            inputRelay.Output.Connect(inputRelay.Disable);
            stateRelay.Output.Connect(inputRelay.Enable);
        }
        foreach (Relay stateRelay in state.Relays)
        {
            foreach(Relay other in state.Relays)
            {
                if (stateRelay != other)
                {
                    stateRelay.Output.Connect(other.Disable);
                }
            }
        }
        // The logic behind incrementing and decrementing is that we trigger all the triggers in the input,
        // the one fires and enables its two left and right state triggers,
        // and then we go from left to right or right to left to move left or right respectively.
        Increment = new Input(input.Inputs.SelectMany(inp => inp)).Concat(new Input(state.Inputs.SelectMany(input => input).Reverse()));
        Decrement = new Input(input.Inputs.SelectMany(inp => inp)).Concat(new Input(state.Inputs.SelectMany(input => input)));
        Value = state.Outputs;
        // The input triggers only fire when the state moves, so off of the current trigger in any case.
        OffSignals = input.Outputs;
        Probe = new Input(state.Inputs.SelectMany(targets => targets));
        Set = Enumerable.Range(0, n).Select(
            x => new Input(new Input(input.Relays.Take(x).Select(relay => relay.Disable).SelectMany(input => input))
                .Concat(new Input(state.Relays.Take(x).Select(relay => relay.Disable).SelectMany(input => input))
                .Concat(input.Relays[x].Enable)
                .Concat(state.Relays[x].Enable)
                .Concat(new Input(input.Relays.Skip(x+1).Select(relay => relay.Disable).SelectMany(input => input)))
                .Concat(new Input(state.Relays.Skip(x+1).Select(relay => relay.Disable).SelectMany(input => input)))
            )
        )).ToList();
        Entities = input.Entities.Concat(state.Entities);
    }
}