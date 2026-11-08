using LevelXML;
using Xunit;
using Xunit.Abstractions;
using LevelXMLUtils.Memory;
namespace LevelXMLUtils.Tests;

// MixedMemory selects a row through blocks instead of disabling every row that doesn't match (see its header). This drives it with random
// operations, the way the processor uses memory: set the address and read; set the address, enable writes, write the bits, disable writes;
// stray writes while writing is disabled (every probe of the processor's source operand fires memory.Writes); direct writes and clears of
// single cells; addresses past the last row. It must read back what a plain array says.
public class MixedMemoryTests
{
	readonly ITestOutputHelper output;
	public MixedMemoryTests(ITestOutputHelper output) { this.output = output; }

	enum Kind { ReadWrite, SingleCell, Rom }

	// A memory grid with relays to drive it, and a relay per bit that its values fire
	sealed class Rig
	{
		public readonly List<ActivateTrigger> Triggers = new();
		public readonly Relay ClearAddress, Read, EnableWrite, DisableWrite;
		public Relay? ReadNext, EnableWriteNext;
		public readonly List<Relay> SetAddressBit, WriteBit, ValueBit;
		public readonly List<List<Relay>> DirectWrite, Clear;
		public TriggerGraph Graph = null!;

		public Rig(int bits, Kind[] kinds, uint[] initial, int? addressBits = null, int baseAddress = 0)
		{
			List<IMemoryRow> rows = kinds.Select((kind, r) => kind switch
			{
				Kind.ReadWrite => (IMemoryRow)new ReadWriteMemoryRow(bits, 0, 0, 5, 5),
				Kind.SingleCell => new SingleCellMemoryRow(bits, 0, 0, 5, 5),
				_ => new ROMRow(bits, 0, 0, 5, 5),
			}).ToList();
			for (int r = 0; r < rows.Count; r++) { rows[r].SetValue(initial[r]); }
			memory = new MixedMemory(rows, 0, 0, 5, 5, addressBits: addressBits, baseAddress: baseAddress);
			(IList<Input> address, IList<Input> addressClears, Input read, Input enableWrite, IList<Input> writes, Input disableWrite,
				IList<IList<Input>> directWrites, IList<IList<Input>> clears, IList<Output> values, IEnumerable<Entity> entities) = From(memory);
			Relay Driver(Input input) { Relay relay = new(0, 0, 5, 5); relay.Output.Connect(input); Triggers.AddRange(relay.Entities.OfType<ActivateTrigger>()); return relay; }
			ClearAddress = Driver(new Input(addressClears.SelectMany(i => i)));
			SetAddressBit = address.Select(Driver).ToList();
			Read = Driver(read);
			EnableWrite = Driver(enableWrite);
			WriteBit = writes.Select(Driver).ToList();
			DisableWrite = Driver(disableWrite);
			DirectWrite = directWrites.Select(row => row.Select(Driver).ToList()).ToList();
			Clear = clears.Select(row => row.Select(Driver).ToList()).ToList();
			ValueBit = values.Select(value => { Relay sink = new(0, 0, 5, 5); value.Connect(sink.Input); Triggers.AddRange(sink.Entities.OfType<ActivateTrigger>()); return sink; }).ToList();
			ReadNext = Driver(memory.ReadNext);
			EnableWriteNext = Driver(memory.EnableWriteNext);
			Triggers.AddRange(entities.OfType<ActivateTrigger>());
			Graph = TriggerGraph.FromTriggers(Triggers.Distinct());
		}

		static (IList<Input>, IList<Input>, Input, Input, IList<Input>, Input, IList<IList<Input>>, IList<IList<Input>>, IList<Output>, IEnumerable<Entity>) From(MixedMemory m) =>
			(m.Address, m.AddressClears, m.Read, m.EnableWrite, m.Writes, m.DisableWrite, m.DirectWrites, m.Clears, m.Values, m.Entities);

		readonly MixedMemory memory;
		public int Actions;
		void Pulse(Relay relay) { Graph.Pulse(TriggerGraph.IdFor(relay.Output.Single())); Actions += Graph.ActionsCarriedOut; }

		void SetAddress(uint address)
		{
			Pulse(ClearAddress);
			for (int k = 0; k < SetAddressBit.Count; k++) { if (((address >> k) & 1) == 1) { Pulse(SetAddressBit[k]); } }
		}

		public uint ReadAt(uint address)
		{
			SetAddress(address);
			Graph.Pulse(TriggerGraph.IdFor(Read.Output.Single()));
			Actions += Graph.ActionsCarriedOut;
			uint value = 0;
			for (int i = 0; i < ValueBit.Count; i++)
			{
				if (Graph.LastFired.Contains(TriggerGraph.IdFor(ValueBit[i].Output.Single()))) { value |= 1u << i; }
			}
			return value;
		}

		public void WriteAt(uint address, uint value)
		{
			SetAddress(address);
			Pulse(EnableWrite);
			for (int i = 0; i < WriteBit.Count; i++) { if (((value >> i) & 1) == 1) { Pulse(WriteBit[i]); } }
			Pulse(DisableWrite);
		}

		// The next address after the last read or write
		public uint ReadNextValue()
		{
			Graph.Pulse(TriggerGraph.IdFor(ReadNext!.Output.Single()));
			Actions += Graph.ActionsCarriedOut;
			uint value = 0;
			for (int i = 0; i < ValueBit.Count; i++) { if (Graph.LastFired.Contains(TriggerGraph.IdFor(ValueBit[i].Output.Single()))) { value |= 1u << i; } }
			return value;
		}

		public void WriteNext(uint value)
		{
			Pulse(EnableWriteNext!);
			for (int i = 0; i < WriteBit.Count; i++) { if (((value >> i) & 1) == 1) { Pulse(WriteBit[i]); } }
			Pulse(DisableWrite);
		}

		public void StrayWrite(uint value)
		{
			for (int i = 0; i < WriteBit.Count; i++) { if (((value >> i) & 1) == 1) { Pulse(WriteBit[i]); } }
		}

		public void DirectWriteAt(int row, int bit) => Pulse(DirectWrite[row][bit]);
		public void ClearAt(int row, int bit) => Pulse(Clear[row][bit]);
	}

	// The plain array: what each row kind holds after each operation
	sealed class Model
	{
		readonly Kind[] kinds; readonly uint[] values; readonly int bits;
		public Model(int bits, Kind[] kinds, uint[] initial) { this.bits = bits; this.kinds = kinds; values = initial.ToArray(); }
		uint Mask(int r) => kinds[r] switch { Kind.ReadWrite => (1u << bits) - 1, Kind.SingleCell => 1u, _ => 0u };
		public uint Read(uint address) => address < values.Length ? values[address] : 0;
		public void Write(uint address, uint value)
		{
			if (address >= values.Length) { return; }
			uint mask = Mask((int)address);
			values[address] = (values[address] & ~mask) | (value & mask);
		}
		public void DirectWrite(int row, int bit) { values[row] |= (1u << bit) & Mask(row); }
		public void Clear(int row, int bit) { values[row] &= ~((1u << bit) & Mask(row)); }
	}

	// A CHIP-8's memory: 8-bit rows behind a 12-bit address
	[Fact]
	public void MixedMemory_WithAWiderAddressThanItsRows_ReadsAndWritesLikeAnArray()
	{
		const int bits = 8, addressBits = 12, rowCount = 1107;
		Random random = new(7);
		Kind[] kinds = Enumerable.Range(0, rowCount).Select(_ => random.Next(4) == 0 ? Kind.ReadWrite : Kind.Rom).ToArray();
		uint[] initial = kinds.Select(_ => (uint)random.Next(1 << bits)).ToArray();
		Rig memory = new(bits, kinds, initial, addressBits: addressBits);
		Model model = new(bits, kinds, initial);
		uint RandomAddress() => random.Next(8) == 0 ? (uint)random.Next(1 << addressBits) : (uint)random.Next(rowCount);
		for (int step = 0; step < 400; step++)
		{
			uint address = RandomAddress();
			if (random.Next(2) == 0) { Assert.True(model.Read(address) == memory.ReadAt(address), $"step {step}: reading {address}"); }
			else { uint value = (uint)random.Next(1 << bits); memory.WriteAt(address, value); model.Write(address, value); }
		}
		for (uint address = 0; address < rowCount; address++) { Assert.Equal(model.Read(address), memory.ReadAt(address)); }
	}

	// A bank: rows standing for addresses from a base on. Addresses below the base or past the last row read and write nothing.
	[Theory]
	[InlineData(0xB00, 400, 21)]
	[InlineData(0x100, 13, 22)]
	public void MixedMemory_WithABaseAddress_ReadsAndWritesLikeAnArray(int baseAddress, int rowCount, int seed)
	{
		Random random = new(seed);
		Kind[] kinds = Enumerable.Range(0, rowCount).Select(_ => random.Next(4) == 0 ? Kind.Rom : Kind.ReadWrite).ToArray();
		uint[] initial = kinds.Select(_ => (uint)random.Next(256)).ToArray();
		Rig memory = new(8, kinds, initial, addressBits: 12, baseAddress: baseAddress);
		Model model = new(8, kinds, initial);
		uint ModelRead(uint address) => address >= baseAddress && address < baseAddress + rowCount ? model.Read(address - (uint)baseAddress) : 0;
		uint current = 0;
		for (int step = 0; step < 500; step++)
		{
			int op = random.Next(10);
			// (ReadNext can't step into the bank from below: a full read there)
			if (op < 4 || step == 0 || current >= 4095 || current + 1 == baseAddress)
			{
				current = (uint)(random.Next(3) == 0 ? random.Next(4096) : baseAddress - 4 + random.Next(rowCount + 8));
				if (random.Next(2) == 0) { Assert.True(ModelRead(current) == memory.ReadAt(current), $"step {step}: reading {current:X3}"); }
				else { uint value = (uint)random.Next(256); memory.WriteAt(current, value); if (current >= baseAddress && current < baseAddress + rowCount) { model.Write(current - (uint)baseAddress, value); } }
			}
			else
			{
				current++;
				Assert.True(ModelRead(current) == memory.ReadNextValue(), $"step {step}: reading the next row, {current:X3}");
			}
		}
	}

	// ReadNext and EnableWriteNext (the address one past the last access, without decoding it) mixed with ordinary reads and writes, through
	// place and block boundaries and past the last row (which reads nothing)
	[Theory]
	[InlineData(8, 12, 1107, 11)]
	[InlineData(6, 6, 13, 12)]
	[InlineData(6, 6, 64, 13)]
	[InlineData(4, 4, 1, 14)]
	[InlineData(8, 12, 100, 15)]
	public void MixedMemory_StepsToTheNextRow_LikeAnArray(int bits, int addressBits, int rowCount, int seed)
	{
		Random random = new(seed);
		Kind[] kinds = Enumerable.Range(0, rowCount).Select(_ => random.Next(3) == 0 ? Kind.Rom : Kind.ReadWrite).ToArray();
		uint[] initial = kinds.Select(_ => (uint)random.Next(1 << bits)).ToArray();
		Rig memory = new(bits, kinds, initial, addressBits: addressBits);
		Model model = new(bits, kinds, initial);
		uint current = 0, limit = (uint)Math.Min((1 << addressBits) - 1, rowCount + 8);
		int reads = 0, actions = 0, nexts = 0;
		for (int step = 0; step < 500; step++)
		{
			int op = random.Next(10);
			bool canStep = step > 0 && current < limit;
			if (op < 3 || !canStep)
			{
				current = (uint)random.Next((int)limit);
				if (random.Next(2) == 0) { Assert.True(model.Read(current) == memory.ReadAt(current), $"step {step}: reading {current}"); reads++; }
				else { uint value = (uint)random.Next(1 << bits); memory.WriteAt(current, value); model.Write(current, value); }
			}
			else if (op < 8)
			{
				current++;
				int before = memory.Actions;
				uint got = memory.ReadNextValue();
				actions += memory.Actions - before; nexts++;
				Assert.True(model.Read(current) == got, $"step {step}: reading the next row, {current}, gave {got}, the array says {model.Read(current)}");
			}
			else
			{
				current++;
				uint value = (uint)random.Next(1 << bits);
				memory.WriteNext(value); model.Write(current, value);
			}
		}
		for (uint address = 0; address < rowCount; address++) { Assert.Equal(model.Read(address), memory.ReadAt(address)); }
		output.WriteLine($"{rowCount} rows: {nexts} next-row reads, {(nexts == 0 ? 0 : actions / nexts)} actions each");
	}

	[Theory]
	[InlineData(6, 13, 1)]
	[InlineData(6, 64, 2)]
	[InlineData(7, 100, 3)]
	[InlineData(9, 512, 4)]
	[InlineData(9, 13, 5)]
	[InlineData(3, 1, 6)]
	public void MixedMemory_ReadsAndWritesLikeAnArray(int bits, int rowCount, int seed)
	{
		Random random = new(seed);
		// Mostly read-write rows, with some single-bit rows (the processor's control registers) and ROM rows mixed in
		Kind[] kinds = Enumerable.Range(0, rowCount).Select(_ => random.Next(10) switch { 0 => Kind.SingleCell, 1 => Kind.Rom, _ => Kind.ReadWrite }).ToArray();
		uint[] initial = kinds.Select(kind => kind == Kind.SingleCell ? (uint)random.Next(2) : (uint)random.Next(1 << bits)).ToArray();
		Rig fast = new(bits, kinds, initial);
		Model model = new(bits, kinds, initial);
		uint RandomAddress() => random.Next(8) == 0 ? (uint)random.Next(1 << bits) : (uint)random.Next(rowCount);
		int reads = 0;
		for (int step = 0; step < 400; step++)
		{
			switch (random.Next(10))
			{
				case < 4:
				{
					uint address = RandomAddress();
					uint expected = model.Read(address), got = fast.ReadAt(address);
					Assert.True(expected == got, $"step {step}: reading {address} gave {got}, the array says {expected}");
					reads++;
					break;
				}
				case < 8:
				{
					uint address = RandomAddress(), value = (uint)random.Next(1 << bits);
					fast.WriteAt(address, value); model.Write(address, value);
					break;
				}
				case 8:
				{
					uint value = (uint)random.Next(1 << bits);
					fast.StrayWrite(value);
					break;
				}
				default:
				{
					int row = random.Next(rowCount), bit = random.Next(bits);
					if (random.Next(2) == 0) { fast.DirectWriteAt(row, bit); model.DirectWrite(row, bit); }
					else { fast.ClearAt(row, bit); model.Clear(row, bit); }
					break;
				}
			}
		}
		for (uint address = 0; address < rowCount; address++) { Assert.Equal(model.Read(address), fast.ReadAt(address)); }
		output.WriteLine($"{bits} bits, {rowCount} rows: {reads} random reads; actions {fast.Actions}");
	}
}
