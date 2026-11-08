using LevelXML;
namespace LevelXMLUtils.Devices;
///<summary>
/// A decimal digit in binary (BCD): Increment counts up, and Check, after it, turns a 10 into 0 and fires Overflow. Shift fires the
/// Value outputs of the bits that are set, GetComplement the Complement outputs of 9 minus the value. The options leave out the
/// triggers of the parts a use doesn't need.
///</summary>
public class BCDCounter : TriggerContraption
{
    public Input Increment = new();
    public Input Check = new();
    // Shift fires Value[k] for each bit k that is set
    public Input Shift = new();
    // These lists are 4 bits long
    public IList<Input> Adds = new List<Input>();
    public IList<Input> Sets = new List<Input>();
    public Input ClearAll = new();
    public IList<Input> Clears = new List<Input>();
    public IList<Input> Probes = new List<Input>();
    public IList<Output> Ons = new List<Output>();
    public IList<Output> Offs = new List<Output>();
    public Output Overflow = new();
    public IList<Output> Value = new List<Output>();
    public Input GetComplement = new();
    public List<Output> Complement = new();
    // Enabling certain functionalities requires more triggers per digit, and is only necessary in certain situations

    public BCDCounter(double x=0, double y=0, double width=100, double height=100, bool shiftable=true, bool invertible=true, bool checkable=true, bool incrementable=true)
    {
        ICounter bits;
        if (incrementable)
        {
            InstantToggleChain toggles = new(4, x,y,width,height);
            bits = toggles;
            Increment = toggles.Input;
            Offs = toggles.Offs;
            Entities = toggles.Entities;
            // the check (10 to 0, with Overflow) only exists for the incrementable counter
            if (checkable)
            {
                Relay eightAndTwoOrFour = new(x, y-height*2, width, height, startDisabled: true);
                Relay twoOrFour = new(x, y-height, width, height, startDisabled: true);
                Or addSix = new(x+width, y-height, width, height);
                Relay finishCheck = new(x+width*2, y-height, width, height);
                toggles.Ons[2].Connect(twoOrFour.Input);
                toggles.Ons[1].Connect(twoOrFour.Input);
                twoOrFour.Output.Connect(eightAndTwoOrFour.Enable);
                toggles.Ons[3].Connect(eightAndTwoOrFour.Input);
                Check = 
                twoOrFour.Enable
                .Concat(toggles.Probes[1])
                .Concat(toggles.Probes[2])
                .Concat(toggles.Probes[3])
                .Concat(finishCheck.Input);
                toggles.Overflow.Connect(addSix.Input);
                eightAndTwoOrFour.Output.Connect(addSix.Input);
                finishCheck.Output.Connect(twoOrFour.Disable);
                finishCheck.Output.Connect(eightAndTwoOrFour.Disable);
                finishCheck.Output.Connect(addSix.Reset);
                // To ensure there isn't an infinite loop, we need to reset the decimal overflow triggers
                addSix.Output.Connect(eightAndTwoOrFour.Disable);
                addSix.Output.Connect(twoOrFour.Disable);
                addSix.Output.Connect(toggles.Inputs[2]);
                addSix.Output.Connect(toggles.Inputs[1]);
                Overflow = Overflow.Concat(addSix.Output);
                Entities = Entities
                    .Concat(eightAndTwoOrFour.Entities)
                    .Concat(twoOrFour.Entities)
                    .Concat(addSix.Entities)
                    .Concat(finishCheck.Entities);
            }
        } else
        {
            MultiRelayAsCounter relays = new(1,x,y, width, height);
            bits = relays;
            Entities = relays.Entities;
        }
        Adds = bits.Inputs;
        Probes = bits.Probes;
        Sets = bits.Sets;
        Clears = bits.Clears;
        Ons = bits.Ons;
        if (shiftable)
        {
            Relay probeAll = new(x-width, y, width, height);
            probeAll.Output.Connect(bits.Probes);
            Relay one = new(x,y+height, 5,5, startDisabled: true);
            Relay two = new(x,y+height+5, 5,5, startDisabled: true);
            Relay four = new(x,y+height+10, 5,5, startDisabled: true);
            Relay eight = new(x,y+height+15,5,5, startDisabled: true);
            Relay disableNRelays = new(x,y+height+20,5,5);
            Relay clearAllBits = new(x,y+height+25,5,5);
            clearAllBits.Output.Connect(bits.Clears[0].Concat(bits.Clears[1]).Concat(bits.Clears[2]).Concat(bits.Clears[3]));
            bits.Ons[0].Connect(one.Input);
            bits.Ons[1].Connect(two.Input);
            bits.Ons[2].Connect(four.Input);
            bits.Ons[3].Connect(eight.Input);
            disableNRelays.Output.Connect(one.Disable);
            disableNRelays.Output.Connect(two.Disable);
            disableNRelays.Output.Connect(four.Disable);
            disableNRelays.Output.Connect(eight.Disable);
            ClearAll = clearAllBits.Input;
            Shift =
                one.Enable
                .Concat(two.Enable)
                .Concat(four.Enable)
                .Concat(eight.Enable)
                .Concat(probeAll.Input)
                .Concat(disableNRelays.Input);
            Value = new List<Output>
            {
                one.Output,
                two.Output,
                four.Output,
                eight.Output
            };
            Entities = Entities
                .Concat(one.Entities)
                .Concat(two.Entities)
                .Concat(four.Entities)
                .Concat(eight.Entities)
                .Concat(disableNRelays.Entities)
                .Concat(clearAllBits.Entities)
                .Concat(probeAll.Entities);
        }
        if (invertible)
        {
            // These triggers will send the 9's complement of this counter to GetComplement
            // A' = notA and notB and notC
            // B' = B xor C
            // C' = C
            // D' = notD
            Relay probeGates = new(x-width, y-height*3, width, height);
            Relay notABC = new(x, y-height*3, width, height, startDisabled:true);
            Relay notBOrC = new(x+width, y-height, width, height);
            Relay BAndC = new(x+width*2, y-height*3, width, height);
            Relay notD = new(x+width*3, y-height*3, width, height);
            Relay BXorC = new(x+width*4, y, width, height, startDisabled:true);
            Relay c = new(x+width*5, y-height*3, width, height, startDisabled: true);
            Relay resetComplementGates = new(x+width*6, y-height*3, width, height);
            // GetComplement will ready all of the gates,
            // and then fire probeGates, which will also disable all of the gates
            GetComplement = new Input(
                notABC.Enable
                .Concat(BAndC.Disable)
                .Concat(notBOrC.Enable)
                .Concat(BXorC.Enable)
                .Concat(c.Disable)
                .Concat(notD.Enable)
                .Concat(probeGates.Input)
            );
            bits.Ons[3].Connect(notABC.Disable);
            bits.Ons[2].Connect(notABC.Disable);
            bits.Ons[1].Connect(notABC.Disable);
            probeGates.Output.Connect(bits.Probes);
            // If neither A, nor B, nor C disabled this, then this sends a signal to A
            probeGates.Output.Connect(notABC.Input);
            // BXorC only fires when both notBOrC and BAndC never fired.
            bits.Ons[1].Connect(notBOrC.Disable);
            bits.Ons[2].Connect(notBOrC.Disable);
            bits.Ons[1].Connect(BAndC.Enable);
            bits.Ons[2].Connect(BAndC.Input);
            notBOrC.Output.Connect(BXorC.Disable);
            BAndC.Output.Connect(BXorC.Disable);
            // This will send B xor C to B'
            probeGates.Output.Connect(notBOrC.Input);
            probeGates.Output.Connect(BXorC.Input);
            // This will send C to C'
            bits.Ons[1].Connect(c.Enable);
            probeGates.Output.Connect(c.Input);
            // This will send notD to D'
            bits.Ons[0].Connect(notD.Disable);
            probeGates.Output.Connect(notD.Input);
            // This will make sure no sending happens when we aren't trying to get the complement
            probeGates.Output.Connect(resetComplementGates.Input);
            resetComplementGates.Output.Connect(notABC.Disable);
            resetComplementGates.Output.Connect(BXorC.Disable);
            // A'
            Complement.Add(notABC.Output);
            // B'
            Complement.Add(BXorC.Output);
            // C'
            Complement.Add(c.Output);
            // D'
            Complement.Add(notD.Output);
            Complement.Reverse();
            Entities = Entities
                .Concat(probeGates.Entities)
                .Concat(notABC.Entities)
                .Concat(notBOrC.Entities)
                .Concat(BAndC.Entities)
                .Concat(BXorC.Entities)
                .Concat(c.Entities)
                .Concat(notD.Entities)
                .Concat(resetComplementGates.Entities);
        }
    }
}
public class BCDCounters : TriggerContraption
{
    public IList<Input> Increments;
    public Input Check;
    // This shift also clears
    public Input Shift;
    // These shifts will not do any clears
    public IList<Input> Shifts;
    // The following 6 lists are n * 4 long, because in BCD there are 4 bits per digit
    public IList<Input> Adds;
    public IList<Input> Sets;
    public IList<Input> Clears;
    public IList<Input> SetLSB;
    public IList<Input> Probes;
    public IList<Output> Ons;
    public IList<Output> Offs;
    // Overflow for each digit (there are n of these)
    public IList<Output> Overflows;
    // Each value is 4 wide, and we have n values
    public IList<IList<Output>> Values;
    public Output Overflow;
    public IList<Input> GetComplements;
    public IList<Output> Complements;
    private IList<BCDCounter> lst;
    public BCDCounters(int n, double x=0, double y=0, double width=100, double height=100, bool shiftable=true, bool invertible=true, bool checkable=true, bool incrementable=true)
    {
        lst = 
            Enumerable.Range(0, n)
		    .Select(index => new BCDCounter(x+(index*(width + 300)),y,width, height, shiftable, invertible, checkable, incrementable))
            .ToList();
        // I'm connecting these such that the MSB is on the left and LSB is on the right
        foreach ((BCDCounter first, BCDCounter second) in lst.Take(lst.Count - 1).Zip(lst.Skip(1)))
		{
			second.Overflow.Connect(first.Increment);
            second.Value.Connect(first.Sets);
		}
        Increments = lst.Select(counter => counter.Increment).ToList();
        Overflow = lst.First().Overflow;
        Adds = lst.SelectMany(counter => counter.Adds).ToList();
        Sets = lst.SelectMany(counter => counter.Sets).ToList();
        Clears = lst.SelectMany(counter => counter.Clears).ToList();
        SetLSB = lst.Last().Sets;
        Probes = lst.SelectMany(counter => counter.Probes).ToList();
        Ons = lst.SelectMany(counter => counter.Ons).ToList();
        Offs = lst.SelectMany(counter => counter.Offs).ToList();
        Overflows = lst.Select(counter => counter.Overflow).ToList();
        Values = lst.Select(counter => counter.Value).ToList();
        GetComplements = lst.Select(counter => counter.GetComplement).ToList();
        Complements = lst.SelectMany(counter => counter.Complement).ToList();
        
        Shift = new Input(
            lst.Select(counter => counter.Shift)
            .Zip(lst.Select(counter => counter.ClearAll))
            .Select(a => new[] {a.First, a.Second})
            .SelectMany(l => l)
            .SelectMany(l => l));
        Shifts = lst.Select(counter => counter.Shift).ToList();
        //lst.Reverse();
        // Check needs to go in order within the counters and in reverse order between the counters.
        List<Input> reversedCheck = lst.Select(counter => counter.Check).ToList();
        reversedCheck.Reverse();
        Check = new Input(reversedCheck.SelectMany(l => l));
        Entities = lst.SelectMany(counter => counter.Entities);
    }
}
public class MultiRelayAsCounter : TriggerContraption, ICounter
{
    public IList<Input> Inputs {get; set;}
    public IList<Input> Sets {get; set;}
    public IList<Input> Clears {get; set;}
    public IList<Input> Probes {get; set;}
    public IList<Output> Ons {get; set;}
    public MultiRelayAsCounter(int n=1,double x=0, double y=0, double height=100, double width=100)
    {
        MultiRelay bits = new(n*4, x,y, width,height, startDisabled:true);
        Sets = bits.Enables;
        Clears = bits.Disables;
        Inputs = bits.Inputs;
        // Because this counter is stateless, probing is the same as setting the input
        Probes = bits.Inputs;
        Ons = bits.Outputs;
        Entities = bits.Entities;
    }
}
