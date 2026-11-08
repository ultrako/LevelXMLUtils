using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

// A memory row whose byte is a toggle chain, like a CHIP-8 register, instead of relay cells: it reads, writes and clears through the same
// interface as a ReadWriteMemoryRow (a MixedMemory can hold it), and code that knows the row can count in its byte directly (Chip8Machine
// moves Danm8ku's bullets in their own table rows). Reading probes the toggles through gates that are open only while it reads, since a
// toggle's "is 1" also fires when counting turns the bit on.
public class ToggleMemoryRow : TriggerContraption, IMemoryRow
{
	public readonly InstantToggleChain Byte;
	readonly Relay readRelay, directRead, written;
	readonly Relay[] valueGates, writeGates;
	public Input EnableRead => readRelay.Enable;
	public Input DisableRead => readRelay.Disable;
	public IList<Input> EnableWrites => writeGates.Select(g => g.Enable).ToList();
	public IList<Input> DisableWrites => writeGates.Select(g => g.Disable).ToList();
	// Every write through the row's interface clears it first, so clearing marks it written (a store at a known address marks it itself)
	public IList<Input> Clears => Byte.Clears.Select((c, k) => k == 0 ? c.Concat(written.Input) : c).ToList();
	public Input MarkWritten => written.Input;
	public Output Written => written.Output;
	public Input Read => readRelay.Input;
	public IList<Input> Writes => writeGates.Select(g => g.Input).ToList();
	public IList<Input> DirectWrites => Byte.Sets.ToList();
	public IList<Output> Values => valueGates.Select(g => g.Output).ToList();
	public IList<Input>? CellReads => new List<Input> { directRead.Input };

	public ToggleMemoryRow(int bits, double x, double y, double width, double height)
	{
		Byte = new InstantToggleChain(bits, x, y, width, height);
		var entities = Byte.Entities.ToList();
		Relay NewRelay(bool startDisabled)
		{
			Relay relay = new(x, y, width, height, startDisabled: startDisabled);
			entities.AddRange(relay.Entities);
			return relay;
		}
		valueGates = Enumerable.Range(0, bits).Select(_ => NewRelay(true)).ToArray();
		writeGates = Enumerable.Range(0, bits).Select(_ => NewRelay(true)).ToArray();
		for (int k = 0; k < bits; k++)
		{
			Byte.Ons[k].Connect(valueGates[k].Input);
			writeGates[k].Output.Connect(Byte.Sets[k]);
		}
		Relay open = NewRelay(false), close = NewRelay(false);
		open.Output.Connect(new Input(valueGates.SelectMany(g => g.Enable)));
		close.Output.Connect(new Input(valueGates.SelectMany(g => g.Disable)));
		Input probe = open.Input.Concat(new Input(Byte.Probes.SelectMany(p => p))).Concat(close.Input);
		readRelay = NewRelay(true);
		readRelay.Output.Connect(probe);
		written = NewRelay(false);
		directRead = NewRelay(false);
		directRead.Output.Connect(probe);
		Entities = entities;
	}

	public void AddVisualization() => throw new NotSupportedException("a toggle row isn't drawn");
	public void AddClickTriggers() => throw new NotSupportedException("a toggle row isn't clickable");
	// Toggles start at 0
	public void SetValue(uint value) { if (value != 0) { throw new NotSupportedException("a toggle row starts at 0"); } }
}
