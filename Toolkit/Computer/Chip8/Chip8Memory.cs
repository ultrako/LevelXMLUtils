using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// Chip8Machine's memory: reading and writing through the banks, and loads and stores at known addresses
public partial class Chip8Machine
{
	// I, memory and the run gate
	Relay clearI = null!, digitReset = null!, runGate = null!;
	Relay[][] digits = null!;
	DecoderRelays fontDecoder = null!, decimalDecoder = null!;
	readonly Dictionary<int, Relay> waitArms = new();
	readonly Dictionary<int, Relay[]> waitReleases = new();
	// Point the banks' addresses at I + offset
	void AddressFromI(Steps steps, int offset, List<Bank> banks)
	{
		foreach (Bank bank in banks)
		{
			steps.Add(new Input(bank.Memory.AddressClears.SelectMany(c => c)));
			steps.Add(bank.Open);
		}
		steps.Add(new Input(IRegister.Probes.SelectMany(p => p)));
		foreach (Bank bank in banks)
		{
			steps.Add(bank.Close);
			if (offset > 0) { steps.Add(new Input(BitsOf(offset, 12).SelectMany(k => bank.Memory.AddressIncrements[k]))); }
		}
	}
	// Temp = memory[I + offset]. With one bank, a read of the byte after the last access (offset one more than the last ReadMemory or
	// WriteMemory of the same instruction) steps the bank to its next row instead of decoding the address again: much cheaper. With several,
	// each read decodes the address (a bank can't step into its rows from below).
	void ReadMemory(Steps steps, int offset, List<Bank> banks, bool next = false)
	{
		if (next && banks.Count == 1) { steps.Add(tempReset); steps.Add(banks[0].Memory.ReadNext); return; }
		AddressFromI(steps, offset, banks);
		steps.Add(tempReset);
		foreach (Bank bank in banks) { steps.Add(bank.Memory.Read); }
	}
	// memory[I + offset] = whatever `write` sends to the banks' Writes
	void WriteMemory(Steps steps, int offset, List<Bank> banks, Action write, bool next = false)
	{
		if (next && banks.Count == 1) { steps.Add(banks[0].Memory.EnableWriteNext); }
		else { AddressFromI(steps, offset, banks); foreach (Bank bank in banks) { steps.Add(bank.Memory.EnableWrite); } }
		write();
		foreach (Bank bank in banks) { steps.Add(bank.Memory.DisableWrite); }
	}
	// Load the registers in `mask` from I + their index, in order. `last` is the offset of the bank's last access in this instruction (-1:
	// none): with one bank, a byte a little after it is reached by stepping (ReadNext), anything else decodes the address again.
	void LoadRegisters(Steps steps, List<Bank> banks, int mask, ref int last)
	{
		for (int r = 0; r < 16; r++)
		{
			if (((mask >> r) & 1) == 0) { continue; }
			bool step = banks.Count == 1 && last >= 0 && r > last && r - last <= 4;
			if (step) { for (int skip = last + 1; skip < r; skip++) { steps.Add(banks[0].Memory.Advance); } }
			ReadMemory(steps, r, banks, next: step);
			steps.Add(new Input(V[r].Clears.SelectMany(c => c)));
			InstantToggleChain register = V[r];
			Apply(steps, Sink($"set{r}", t: k => register.Sets[k]), t: AllBits);
			last = r;
		}
	}
	Input AllBankWrites(int k) => new(Banks.SelectMany(bank => bank.Memory.Writes[k]));
	// The byte at a known address onto the bus: fire its row's cells directly (no address, no decode); past every bank it reads 0
	// Rows whose byte is a toggle chain (Danm8ku's bullet table, when its loops are fused): reads, loads and stores at a known address use the
	// chain directly
	readonly Dictionary<int, ToggleMemoryRow> toggleRows = new();
	void ReadConstant(Steps steps, int address)
	{
		if (toggleRows.TryGetValue(address, out ToggleMemoryRow? toggled)) { ReadOnto(steps, toggled.Byte); return; }
		Bank? bank = Banks.FirstOrDefault(b => address >= b.Start && address < b.End);
		if (bank is not null) { steps.Add(bank.Memory.RowCellRead(address - bank.Start)); }
	}
	// Load a register from a known address: gates from that row's cells to the register's bits, open while the row's cells are read (no
	// Temp, no sink; a row gets one set of gates per register loaded from it)
	readonly Dictionary<(int address, InstantToggleChain register), (Relay open, Relay close)> rowGates = new();
	void LoadConstant(Steps steps, int address, InstantToggleChain register)
	{
		if (toggleRows.TryGetValue(address, out ToggleMemoryRow? toggled)) { Copy(steps, toggled.Byte, register); return; }
		steps.Add(new Input(register.Clears.SelectMany(c => c)));
		Bank? bank = Banks.FirstOrDefault(b => address >= b.Start && address < b.End);
		if (bank is null) { return; }
		if (!rowGates.TryGetValue((address, register), out var gates))
		{
			string outer = tag; tag = "direct paths";
			Relay[] bits = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			IList<Output> cells = bank.Memory.RowValues(address - bank.Start);
			for (int k = 0; k < 8; k++) { cells[k].Connect(bits[k].Input); bits[k].Output.Connect(register.Sets[k]); }
			Relay open = NewRelay(), close = NewRelay();
			open.Output.Connect(new Input(bits.SelectMany(g => g.Enable)));
			close.Output.Connect(new Input(bits.SelectMany(g => g.Disable)));
			rowGates[(address, register)] = gates = (open, close);
			tag = outer;
		}
		steps.Add(gates.open);
		steps.Add(bank.Memory.RowCellRead(address - bank.Start));
		steps.Add(gates.close);
	}
	// Store a register at a known address straight into the row's cells: the cells are cleared, then the register's 1s set them, through a
	// store hub of the register (a relay per bit, open only for its stores, so its other probes don't pay for the stores' fan-out) and groups
	// of 8 rows (a group relay enabled with its row's gates). A known value just sets the cells.
	readonly Dictionary<InstantToggleChain, (Relay[] bits, List<List<(int address, Relay[] gates)>> groups)> storeHubs = new();
	readonly Dictionary<(int address, InstantToggleChain register), (Relay open, Relay close)> storeGates = new();
	void StoreConstant(Steps steps, int address, InstantToggleChain register, int? value)
	{
		if (toggleRows.TryGetValue(address, out ToggleMemoryRow? toggled))
		{
			steps.Add(toggled.MarkWritten);
			if (value is int v) { steps.Add(new Input(toggled.Byte.Clears.SelectMany(c => c))); steps.Add(new Input(BitsOf(v).SelectMany(k => toggled.Byte.Sets[k]))); }
			else { Copy(steps, register, toggled.Byte); }
			return;
		}
		Bank? bank = Banks.FirstOrDefault(b => address >= b.Start && address < b.End);
		if (bank is null) { return; }
		int row = address - bank.Start;
		steps.Add(new Input(bank.Memory.Clears[row].SelectMany(c => c)));
		if (value is int known) { steps.Add(new Input(BitsOf(known).SelectMany(k => bank.Memory.DirectWrites[row][k]))); return; }
		if (!storeGates.TryGetValue((address, register), out var gates))
		{
			string outer = tag; tag = "direct paths";
			if (!storeHubs.TryGetValue(register, out var hub))
			{
				hub = (Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray(), new());
				for (int k = 0; k < 8; k++) { register.Ons[k].Connect(hub.bits[k].Input); }
				storeHubs[register] = hub;
			}
			if (hub.groups.Count == 0 || hub.groups[^1].Count == 8) { hub.groups.Add(new()); }
			int g = hub.groups.Count - 1;
			// the group's relays per bit: one per group and bit, fired by the hub, firing the group's rows' gates
			Relay[] bits = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			for (int k = 0; k < 8; k++) { bits[k].Output.Connect(bank.Memory.DirectWrites[row][k]); }
			hub.groups[g].Add((address, bits));
			Relay groupOpen = NewRelay(), groupClose = NewRelay();
			Relay open = NewRelay(), close = NewRelay();
			Relay[] groupBits = groupRelays.TryGetValue((register, g), out var existing) ? existing : groupRelays[(register, g)] = Enumerable.Range(0, 8).Select(k =>
			{
				Relay groupBit = NewRelay(startDisabled: true);
				hub.bits[k].Output.Connect(groupBit.Input);
				return groupBit;
			}).ToArray();
			for (int k = 0; k < 8; k++) { groupBits[k].Output.Connect(bits[k].Input); }
			open.Output.Connect(new Input(hub.bits.SelectMany(b => b.Enable)).Concat(new Input(groupBits.SelectMany(b => b.Enable))).Concat(new Input(bits.SelectMany(b => b.Enable))));
			close.Output.Connect(new Input(hub.bits.SelectMany(b => b.Disable)).Concat(new Input(groupBits.SelectMany(b => b.Disable))).Concat(new Input(bits.SelectMany(b => b.Disable))));
			storeGates[(address, register)] = gates = (open, close);
			tag = outer;
		}
		steps.Add(gates.open);
		steps.Add(new Input(register.Probes.SelectMany(p => p)));
		steps.Add(gates.close);
	}
	readonly Dictionary<(InstantToggleChain, int), Relay[]> groupRelays = new();
	// Write the byte at a known address: its row made current directly (no decode), then the write as usual
	void WriteConstant(Steps steps, int address, Action write)
	{
		Bank? bank = Banks.FirstOrDefault(b => address >= b.Start && address < b.End);
		if (bank is null) { return; }
		steps.Add(bank.Memory.SelectRow(address - bank.Start));
		steps.Add(bank.Memory.EnableWriteSelected);
		write();
		steps.Add(bank.Memory.DisableWrite);
	}
	void AddToI(Steps steps, int amount) => steps.Add(new Input(BitsOf(amount, 12).SelectMany(k => IRegister.Inputs[k])));
}
