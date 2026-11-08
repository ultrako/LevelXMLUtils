using LevelXML;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

// A grid of memory rows behind one address. The address is a toggle chain as wide as a row; Read and EnableWrite first select the row it
// names, then act on that row only.
//
// Selecting is done in two levels so that an access costs about the square root of the row count, not the row count. The low L bits of the
// address are the row's place in its block (lo, R = 2^L places) and the rest are the block (hi). One block is current at a time: every row of
// it has its own read and write gates enabled, every other row's are disabled. One place is current too: its read, write and clear relays,
// which reach that place in every block, are enabled. So a read goes through the R place relays (one passes) and then the place's rows in
// every block (only the current block's row passes). Changing the current block switches 2R rows' gates; changing the place switches a few
// relays; and an access to the same block or place as the last one switches nothing. The address is decoded each time by candidate relays per
// block and per place (enabled, then disabled by every address bit that doesn't match, then fired: the one left switches to itself).
//
// The decoder this replaced disabled every row that didn't match on every access, which was about (bits / 2) * rows * (bits + 2) actions: 87%
// of what a 9-bit processor did per instruction. An address past the last row selects nothing, as before.
public class MixedMemory : TriggerContraption
{
    public IList<Input> Address;
    public IList<Input> AddressClears;
    // Add 2^k to the address (ripple carry), for addressing a row relative to one already set
    public IList<Input> AddressIncrements;
    public IList<Output> AddressOns;
    public IList<Output> AddressOffs;
    public Input Read;
    public Input EnableWrite;
    // Read or enable writes at the address one past the last Read or EnableWrite (or the last of these), without decoding the address again:
    // the current place moves up one (wrapping to the next block). A small fraction of a Read's actions. It can't step into the rows from
    // below the base address (nothing is current there): only step where the last access was inside the rows or past them.
    public Input ReadNext;
    // Step to the next address without reading or writing (to skip a byte between two ReadNext)
    public Input Advance;
    public Input EnableWriteNext;
    public IList<Input> Writes;
    public Input DisableWrite;
    // Make the row at this index current directly (what decoding its address would do), and enable writes there afterwards
    public Func<int, Input> SelectRow = null!;
    public Input EnableWriteSelected = null!;
    // Read the row at this index straight from its cells onto Values, without selecting it (the current row stays as it was)
    public Func<int, Input> RowCellRead = null!;
    // The row's cells' outputs (a cell fires when it is read and holds a 1), to wire a row to something of its own
    public Func<int, IList<Output>> RowValues = null!;
    public IList<IList<Input>> Clears;
    public IList<IList<Input>> DirectWrites;
    public IList<Output> Values;
    private IList<IMemoryRow> memoryRows;
    public IReadOnlyList<IMemoryRow> Rows => memoryRows.ToList();
    // Which part of the memory each of its triggers is (for profiling): "rows", "decode", "block select", "place select", "read", "step", "write"
    public readonly Dictionary<Trigger, string> Parts = new(ReferenceEqualityComparer.Instance);
    // addressBits: how wide the address is, when it isn't the row's width (a CHIP-8 has 12-bit addresses and 8-bit rows)
    // baseAddress: the address of the first row (a bank of a larger memory); it must be a multiple of the rows per block, which is at most
    // 2^(addressBits / 2), so a multiple of that is always fine. Addresses outside the rows read and write nothing.
    // lazyWrites: a row's and a place's write gates are only enabled between EnableWrite and DisableWrite, for the one row being written, so
    // changing the current row (which every read does) switches 1 relay per row and 2 per place instead of about 10 each; a write then costs
    // about 2 * (places + blocks) more. For a memory read far more often than written.
    // stepsCountAddress: false when the address is always set again before the next Read or EnableWrite (stepping then doesn't count it up)
    public MixedMemory(IList<IMemoryRow> memoryRows, double x, double y, double width, double height, bool visualized=false, bool visualizedAddress=false, bool clickable=false, int? addressBits = null, int baseAddress = 0, bool lazyWrites = false, bool stepsCountAddress = true)
    {
        if (!stepsCountAddress && visualizedAddress) { throw new ArgumentException("a drawn address must follow the steps"); }
        this.memoryRows = memoryRows;
        int n = memoryRows[0].Writes.Count;
        int a = addressBits ?? n;
        foreach (IMemoryRow row in memoryRows)
        {
            if (row.Writes.Count != n)
            {
                throw new ArgumentException("Memory row has an incorrect width of " + row.Writes.Count
                + " when we expect a width of " + n);
            }
        }
        if (visualized)
        {
            foreach (IMemoryRow row in memoryRows)
            {
                row.AddVisualization();
            }
        }
        if (clickable)
        {
            foreach (IMemoryRow row in memoryRows)
            {
                row.AddClickTriggers();
            }
        }
        List<Entity> entities = memoryRows.SelectMany(row => row.Entities).ToList();
        foreach (Trigger trigger in entities.OfType<Trigger>()) { Parts[trigger] = "rows"; }
        string part = "decode";
        Relay NewRelay(bool startDisabled)
        {
            Relay relay = new(x, y, width, height, startDisabled: startDisabled);
            entities.AddRange(relay.Entities);
            foreach (Trigger trigger in relay.Entities.OfType<Trigger>()) { Parts[trigger] = part; }
            return relay;
        }
        List<Relay> NewRelays(int count, bool startDisabled) => Enumerable.Range(0, count).Select(_ => NewRelay(startDisabled)).ToList();

        int rows = memoryRows.Count;
        // Half of the bits the rows need, not half of the word: a wide word with few rows would otherwise get far too many places per block.
        // The address bits above the rows' are still checked, by the block candidates.
        int loBits = Math.Min(a, (int)Math.Ceiling(Math.Log2(rows))) / 2;
        int places = 1 << loBits;
        if (baseAddress % places != 0) { throw new ArgumentException($"the base address {baseAddress} isn't a multiple of the {places} rows per block"); }
        int blocks = (rows + places - 1) / places;
        IEnumerable<int> RowsInBlock(int block) => Enumerable.Range(block * places, places).Where(r => r < rows);
        IEnumerable<int> RowsAtPlace(int place) => Enumerable.Range(0, blocks).Select(block => block * places + place).Where(r => r < rows);

        InstantToggleChain address = new(a, x, y - height * 2, width, height * 2.0 / 3.0);
        entities.AddRange(address.Entities);
        foreach (Trigger trigger in address.Entities.OfType<Trigger>()) { Parts[trigger] = "address"; }
        // A bit's "is 1" is its On output, fired by its probe; "is 0" is a not gate that the On output disables, fired after the probes.
        List<Relay> notGates = NewRelays(a, startDisabled: false);
        Relay notGateReset = NewRelay(startDisabled: false);

        notGateReset.Output.Connect(new Input(notGates.SelectMany(gate => gate.Enable)));
        for (int k = 0; k < a; k++) { address.Ons[k].Connect(notGates[k].Disable); }
        // Decoding `count` values (value i is i + offset) from the address bits firstBit to endBit - 1, in two stages: the high bits pick a group (a
        // group candidate is knocked out by every bit that doesn't match; the one left enables its group's final relays) and the low bits a
        // member (likewise; the one left fires that member's final in every group, and only the enabled group's passes, disabling its group
        // again). About 2 * (groups + members) actions instead of a candidate per value and half the candidates per bit. finals[i] fires when
        // the address is value i.
        (List<Relay> finals, Input reset, Input fire) TwoStage(int count, int firstBit, int endBit, int offset)
        {
            int memberBits = Math.Min(endBit - firstBit, (int)Math.Ceiling(Math.Log2(Math.Max(1, count))) / 2);
            int members = 1 << memberBits;
            List<int> groupValues = Enumerable.Range(0, count).Select(i => (i + offset) >> memberBits).Distinct().ToList();
            List<Relay> groupCandidates = NewRelays(groupValues.Count, startDisabled: true), memberCandidates = NewRelays(members, startDisabled: true);
            var finals = new Relay[count];
            var groupFinals = new List<List<Relay>>();
            for (int g = 0; g < groupValues.Count; g++)
            {
                var row = new List<Relay>();
                for (int m = 0; m < members; m++)
                {
                    Relay final = NewRelay(startDisabled: true);
                    int i = ((groupValues[g] << memberBits) | m) - offset;
                    if (i >= 0 && i < count) { finals[i] = final; }
                    row.Add(final);
                }
                groupFinals.Add(row);
                groupCandidates[g].Output.Connect(new Input(row.SelectMany(final => final.Enable)));
                foreach (Relay final in row) { final.Output.Connect(new Input(row.SelectMany(other => other.Disable))); }
            }
            for (int m = 0; m < members; m++) { memberCandidates[m].Output.Connect(new Input(groupFinals.SelectMany(row => row[m].Input))); }
            for (int k = firstBit; k < endBit; k++)
            {
                int bit = k - firstBit;
                (List<Relay> candidates, Func<int, int> valueOf, int at) = bit < memberBits
                    ? (memberCandidates, (Func<int, int>)(m => m), bit) : (groupCandidates, g => groupValues[g], bit - memberBits);
                address.Ons[k].Connect(new Input(candidates.Where((_, c) => ((valueOf(c) >> at) & 1) == 0).SelectMany(candidate => candidate.Disable)));
                notGates[k].Output.Connect(new Input(candidates.Where((_, c) => ((valueOf(c) >> at) & 1) == 1).SelectMany(candidate => candidate.Disable)));
            }
            Relay fireGroups = NewRelay(startDisabled: false), fireMembers = NewRelay(startDisabled: false);
            fireGroups.Output.Connect(new Input(groupCandidates.SelectMany(candidate => candidate.Input)));
            fireMembers.Output.Connect(new Input(memberCandidates.SelectMany(candidate => candidate.Input)));
            return (finals.ToList(), new Input(groupCandidates.Concat(memberCandidates).SelectMany(candidate => candidate.Enable)), fireGroups.Input.Concat(fireMembers.Input));
        }
        (List<Relay> placeCandidates, Input placeReset, Input firePlaceCandidates) = TwoStage(places, 0, loBits, 0);
        (List<Relay> blockCandidates, Input blockReset, Input fireBlockCandidates) = TwoStage(blocks, loBits, a, baseAddress >> loBits);
        Relay noBlock = NewRelay(startDisabled: true);
        Relay candidateReset = NewRelay(startDisabled: false);
        candidateReset.Output.Connect(placeReset.Concat(blockReset).Concat(noBlock.Enable));

        part = "rows";
        // Every row's own gates: read and write are the row's (enabled while its block is current), the clear relay is one per row here.
        List<Relay> rowClears = NewRelays(lazyWrites ? 0 : rows, startDisabled: true);
        for (int r = 0; r < rowClears.Count; r++)
        {
            rowClears[r].Output.Connect(new Input(memoryRows[r].Clears.SelectMany(clear => clear)));
        }
        // Lazy writes: per writable row, an arm relay (enabled while the row is current; it enables the row's write gates and clears it) and a
        // disarm relay (enabled with it; it disables them)
        bool Writable(int r) => memoryRows[r].EnableWrites.Any(gate => gate.Any());
        var rowArms = new Dictionary<int, Relay>(); var rowDisarms = new Dictionary<int, Relay>();
        if (lazyWrites)
        {
            for (int r = 0; r < rows; r++)
            {
                if (!Writable(r)) { continue; }
                rowArms[r] = NewRelay(startDisabled: true); rowDisarms[r] = NewRelay(startDisabled: true);
                rowArms[r].Output.Connect(new Input(memoryRows[r].EnableWrites.SelectMany(gate => gate)).Concat(new Input(memoryRows[r].Clears.SelectMany(clear => clear))));
                rowDisarms[r].Output.Connect(new Input(memoryRows[r].DisableWrites.SelectMany(gate => gate)));
            }
        }
        PanelRow? positions = null;
        List<Relay> rowShows = new();
        if (visualizedAddress)
        {
            positions = new(rows, x, y - height * 2 + height * 2.0 / 3.0, width, height * 2.0 / 3.0);
            entities.AddRange(positions.Entities);
            rowShows = NewRelays(rows, startDisabled: true);
            for (int r = 0; r < rows; r++) { rowShows[r].Output.Connect(positions.Inputs[r]); }
        }
        Input RowGates(int r, bool enable)
        {
            IMemoryRow row = memoryRows[r];
            Input gates;
            if (lazyWrites)
            {
                gates = enable ? row.EnableRead : row.DisableRead;
                if (rowArms.ContainsKey(r)) { gates = gates.Concat(enable ? rowArms[r].Enable.Concat(rowDisarms[r].Enable) : rowArms[r].Disable.Concat(rowDisarms[r].Disable)); }
            }
            else
            {
                gates = (enable ? row.EnableRead : row.DisableRead)
                    .Concat(new Input((enable ? row.EnableWrites : row.DisableWrites).SelectMany(gate => gate)))
                    .Concat(enable ? rowClears[r].Enable : rowClears[r].Disable);
            }
            if (visualizedAddress)
            {
                gates = gates.Concat(enable ? rowShows[r].Enable : rowShows[r].Disable.Concat(positions!.Resets[r]));
            }
            return gates;
        }

        // The current block: Switch[b] is enabled unless b is current; it unselects the current block (Current[b] is enabled only for it) and
        // selects b. The two can't be one relay: Switch activates every Current and enables its own, which is two actions on one trigger.
        part = "block select";
        List<Relay> blockSwitches = NewRelays(blocks, startDisabled: false), blockCurrents = NewRelays(blocks, startDisabled: true);
        Relay unselectBlock = NewRelay(startDisabled: false);
        unselectBlock.Output.Connect(new Input(blockCurrents.SelectMany(current => current.Input)));
        for (int b = 0; b < blocks; b++)
        {
            blockCandidates[b].Output.Connect(noBlock.Disable.Concat(blockSwitches[b].Input));
            blockSwitches[b].Output.Connect(unselectBlock.Input
                .Concat(new Input(RowsInBlock(b).SelectMany(r => RowGates(r, enable: true))))
                .Concat(blockCurrents[b].Enable)
                .Concat(blockSwitches[b].Disable));
            blockCurrents[b].Output.Connect(new Input(RowsInBlock(b).SelectMany(r => RowGates(r, enable: false)))
                .Concat(blockSwitches[b].Enable)
                .Concat(blockCurrents[b].Disable));
        }
        // An address whose block has no rows unselects the current block, so it reads and writes nothing.
        noBlock.Output.Connect(unselectBlock.Input);

        part = "place select";
        // The current place, the same way. Its relays reach that place in every block.
        List<Relay> placeReads = NewRelays(places, startDisabled: true), placeClears = NewRelays(places, startDisabled: true);
        List<List<Relay>> placeWrites = Enumerable.Range(0, places).Select(_ => NewRelays(n, startDisabled: true)).ToList();
        List<Relay> placeShows = visualizedAddress ? NewRelays(places, startDisabled: true) : new();
        List<Relay> placeSwitches = NewRelays(places, startDisabled: false), placeCurrents = NewRelays(places, startDisabled: true);
        Relay unselectPlace = NewRelay(startDisabled: false);
        unselectPlace.Output.Connect(new Input(placeCurrents.SelectMany(current => current.Input)));
        // Lazy writes: a place's arm relay enables its write relays and arms its rows (only the current row's arm is enabled); its disarm relay
        // undoes both
        List<Relay> placeArms = NewRelays(lazyWrites ? places : 0, startDisabled: true), placeDisarms = NewRelays(lazyWrites ? places : 0, startDisabled: true);
        Input PlaceRelays(int l, bool enable)
        {
            IEnumerable<Relay> relays = lazyWrites ? new[] { placeReads[l], placeArms[l], placeDisarms[l] }
                : placeWrites[l].Append(placeReads[l]).Append(placeClears[l]);
            relays = relays.Concat(visualizedAddress ? new[] { placeShows[l] } : Array.Empty<Relay>());
            return new Input(relays.SelectMany(relay => enable ? relay.Enable : relay.Disable));
        }
        for (int l = 0; l < places; l++)
        {
            placeReads[l].Output.Connect(new Input(RowsAtPlace(l).SelectMany(r => memoryRows[r].Read)));
            if (lazyWrites)
            {
                List<int> writable = RowsAtPlace(l).Where(rowArms.ContainsKey).ToList();
                placeArms[l].Output.Connect(new Input(placeWrites[l].SelectMany(w => w.Enable)).Concat(new Input(writable.SelectMany(r => rowArms[r].Input))));
                placeDisarms[l].Output.Connect(new Input(placeWrites[l].SelectMany(w => w.Disable)).Concat(new Input(writable.SelectMany(r => rowDisarms[r].Input))));
            }
            else { placeClears[l].Output.Connect(new Input(RowsAtPlace(l).SelectMany(r => rowClears[r].Input))); }
            for (int i = 0; i < n; i++)
            {
                int bit = i;
                placeWrites[l][i].Output.Connect(new Input(RowsAtPlace(l).SelectMany(r => memoryRows[r].Writes[bit])));
            }
            Input hide = new();
            if (visualizedAddress)
            {
                placeShows[l].Output.Connect(new Input(RowsAtPlace(l).SelectMany(r => rowShows[r].Input)));
                hide = new Input(RowsAtPlace(l).SelectMany(r => positions!.Resets[r]));
            }
            placeCandidates[l].Output.Connect(placeSwitches[l].Input);
            placeSwitches[l].Output.Connect(unselectPlace.Input
                .Concat(PlaceRelays(l, enable: true))
                .Concat(placeCurrents[l].Enable)
                .Concat(placeSwitches[l].Disable));
            placeCurrents[l].Output.Connect(PlaceRelays(l, enable: false)
                .Concat(hide)
                .Concat(placeSwitches[l].Enable)
                .Concat(placeCurrents[l].Disable));
        }

        Input select = notGateReset.Input
            .Concat(candidateReset.Input)
            .Concat(new Input(address.Probes.SelectMany(probe => probe)))
            .Concat(new Input(notGates.SelectMany(gate => gate.Input)))
            .Concat(fireBlockCandidates)
            .Concat(noBlock.Input)
            .Concat(firePlaceCandidates);
        if (visualizedAddress)
        {
            Relay show = NewRelay(startDisabled: false);
            show.Output.Connect(new Input(placeShows.SelectMany(relay => relay.Input)));
            select = select.Concat(show.Input);
        }

        // Stepping to the next row: a step relay per place and per block, enabled while that one is current (they follow the switches), moves
        // the current one up by one; they fire from the top down, so the one just made current isn't stepped again
        part = "step";
        List<Relay> placeSteps = NewRelays(places, startDisabled: true), blockSteps = NewRelays(blocks, startDisabled: true);
        Relay advancePlace = NewRelay(startDisabled: false), advanceBlock = NewRelay(startDisabled: false);
        // Wrapping from the last place to place 0 can't enable place 0's step directly: the sweep reaches place 0 after the last one and would
        // step it again. It enables this, which the end of the sweep turns into place 0's step.
        Relay wrapToFirst = NewRelay(startDisabled: true);
        wrapToFirst.Output.Connect(placeSteps[0].Enable.Concat(wrapToFirst.Disable));
        advancePlace.Output.Connect(new Input(Enumerable.Range(0, places).Reverse().SelectMany(l => placeSteps[l].Input)).Concat(wrapToFirst.Input));
        advanceBlock.Output.Connect(new Input(Enumerable.Range(0, blocks).Reverse().SelectMany(b => blockSteps[b].Input)));
        for (int l = 0; l < places; l++)
        {
            placeSwitches[l].Output.Connect(placeSteps[l].Enable);
            placeCurrents[l].Output.Connect(placeSteps[l].Disable);
            int next = (l + 1) % places;
            Input step = next == l ? new Input() : PlaceRelays(l, enable: false).Concat(PlaceRelays(next, enable: true))
                .Concat(placeCurrents[l].Disable).Concat(placeCurrents[next].Enable)
                .Concat(placeSwitches[l].Enable).Concat(placeSwitches[next].Disable)
                .Concat(placeSteps[l].Disable).Concat(next == 0 ? wrapToFirst.Enable : placeSteps[next].Enable);
            placeSteps[l].Output.Connect(next == 0 ? step.Concat(advanceBlock.Input) : step);
        }
        for (int b = 0; b < blocks; b++)
        {
            blockSwitches[b].Output.Connect(blockSteps[b].Enable);
            blockCurrents[b].Output.Connect(blockSteps[b].Disable);
            Input step = new Input(RowsInBlock(b).SelectMany(r => RowGates(r, enable: false)))
                .Concat(blockCurrents[b].Disable).Concat(blockSwitches[b].Enable).Concat(blockSteps[b].Disable);
            if (b + 1 < blocks)
            {
                step = step.Concat(new Input(RowsInBlock(b + 1).SelectMany(r => RowGates(r, enable: true))))
                    .Concat(blockCurrents[b + 1].Enable).Concat(blockSwitches[b + 1].Disable).Concat(blockSteps[b + 1].Enable);
            }
            blockSteps[b].Output.Connect(step);
        }
        // (With the address drawn, stepping decodes the address again, so the highlight follows)
        Input advance = (stepsCountAddress ? new Input(address.Inputs[0]) : new Input()).Concat(visualizedAddress ? select : advancePlace.Input);

        part = "read";
        Relay readPlaces = NewRelay(startDisabled: false);
        readPlaces.Output.Connect(new Input(placeReads.SelectMany(relay => relay.Input)));
        Read = select.Concat(readPlaces.Input);
        ReadNext = advance.Concat(readPlaces.Input);
        Advance = advance;

        // Writes only get through between EnableWrite and DisableWrite (the row's own write gates stay enabled while its block is current).
        // Like before, enabling writes clears the selected row first.
        part = "write";
        List<Relay> writeGates = NewRelays(n, startDisabled: true);
        Relay clearPlaces = NewRelay(startDisabled: false), writeDisabler = NewRelay(startDisabled: false);
        // (with lazy writes, arming the current row clears it)
        clearPlaces.Output.Connect(new Input((lazyWrites ? placeArms : placeClears).SelectMany(relay => relay.Input)));
        writeDisabler.Output.Connect(new Input(writeGates.SelectMany(gate => gate.Disable)));
        if (lazyWrites)
        {
            Relay disarmPlaces = NewRelay(startDisabled: false);
            disarmPlaces.Output.Connect(new Input(placeDisarms.SelectMany(relay => relay.Input)));
            writeDisabler.Output.Connect(disarmPlaces.Input);
        }
        for (int i = 0; i < n; i++)
        {
            writeGates[i].Output.Connect(new Input(placeWrites.SelectMany(place => place[i].Input)));
        }
        EnableWrite = select
            .Concat(new Input(writeGates.SelectMany(gate => gate.Enable)))
            .Concat(clearPlaces.Input);
        EnableWriteNext = advance
            .Concat(new Input(writeGates.SelectMany(gate => gate.Enable)))
            .Concat(clearPlaces.Input);
        Writes = writeGates.Select(gate => gate.Input).ToList();
        EnableWriteSelected = new Input(writeGates.SelectMany(gate => gate.Enable)).Concat(clearPlaces.Input);
        SelectRow = row => blockSwitches[row / places].Input.Concat(placeSwitches[row % places].Input);
        RowValues = row => memoryRows[row].Values;
        RowCellRead = row => new Input((memoryRows[row].CellReads ?? throw new InvalidOperationException("this row's cells can't be read directly")).SelectMany(read => read));
        DisableWrite = writeDisabler.Input;
        DirectWrites = memoryRows.Select(row => row.DirectWrites).ToList();
        Clears = memoryRows.Select(row => row.Clears).ToList();

        Address = address.Sets;
        AddressOns = address.Ons;
        AddressOffs = address.Offs;
        AddressClears = address.Clears;
        AddressIncrements = address.Inputs;

        Entities = entities;
        Values = Enumerable.Range(0, n).Select(
            i => new Output(memoryRows.Select(memoryRow =>
                memoryRow.Values[i]).SelectMany(trigger => trigger)
            )
        ).ToList();
    }
    /// <summary>
    /// Sets an initial value for a particular memory row
    /// </summary>
    /// <param name="index">The index of the memory row to set</param>
    /// <param name="value">
    /// The unsigned integer that would represent the bit pattern to set.
    /// </param>
    public void SetValue(int index, uint value)
    {
        if (index >= memoryRows.Count())
        {
            throw new ArgumentOutOfRangeException("Tried to set a memory row that isn't in this memory grid!");
        }
        memoryRows[index].SetValue(value);
    }
}
