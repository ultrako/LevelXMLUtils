using LevelXML;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

public class ROMCell : TriggerContraption
{
    public Input EnableRead => new Input();
    public Input DisableRead => new Input();
    public Input EnableWrite = new Input();
    public Input DisableWrite = new Input();
    public Input Clear = new Input();
    public Input Read;
    public Input Write = new Input();
    public Input DirectWrite = new Input();
    public Output Value;
    //private Relay writable;
    private double x, y, width, height;
    private Panel panel;
    private Relay cell;
    /// <summary>The trigger that holds the bit: enabled while it is 1 (read it in a TriggerGraph with IsEnabled).</summary>
    public ActivateTrigger BitTrigger => cell.Output.Single();
    public ROMCell(double x, double y, double width, double height)
    {
        // writable = new(x, y, width, height, startDisabled:true);
        cell = new(x, y, width, height, startDisabled:true);
        panel = new(x, y, width, height, startDisabled: !StartsAtOne);
        // writable.Output.Connect(cell.Enable);
        // EnableWrite = writable.Enable;
        // DisableWrite = writable.Disable;
        // The ability to clear a ROM row interferes with the level cruncher being able to simplify
        // the rom cells into nothing
        // Clear = cell.Disable;
        Read = cell.Input;
        // Write = writable.Input;
        Value = cell.Output;
        Entities = cell.Entities;
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }

    public void AddVisualization()
    {
        // writable.Output.Connect(panel.Input);
        // Clear = Clear.Concat(panel.PowerFirst);
        Entities = Entities.Concat(panel.Entities);
    }

    /// <summary>Whether the bit is 1 when the level starts (its trigger starts enabled).</summary>
    public bool StartsAtOne
    {
        get { return !cell.StartDisabled; }
        set
        {
            cell.StartDisabled = !value;
            panel.StartDisabled = !value;
        }
    }
}

public class ROMRow : TriggerContraption, IMemoryRow
{
    private readonly List<ROMCell> cells;
    public IList<Input> EnableReads => cells.Select(cell => cell.EnableRead).ToList();
    private Input enableRead;
    public Input EnableRead => enableRead;
    public IList<Input> DisableReads => cells.Select(cell => cell.DisableRead).ToList();
    private Input disableRead;
    public Input DisableRead => disableRead;
    public IList<Input> EnableWrites => cells.Select(cell => cell.EnableWrite).ToList();
    public IList<Input> DisableWrites => cells.Select(cell => cell.DisableWrite).ToList();
    public IList<Input> Clears => cells.Select(cell => cell.Clear).ToList();
    public IList<Input> Reads => cells.Select(cell => cell.Read).ToList();
    public IList<Input>? CellReads => Reads;
    private Input read;
    public Input Read => read;
    public IList<Input> Writes => cells.Select(cell => cell.Write).ToList();
    public IList<Input> DirectWrites => cells.Select(cell => cell.DirectWrite).ToList();
    public IList<Output> Values => cells.Select(cell => cell.Value).ToList();
    public IReadOnlyList<ActivateTrigger>? BitTriggers => cells.Select(cell => cell.BitTrigger).ToList();
    private Relay readRelay;
    public ROMRow(int n, double x, double y, double width, double height) {
        cells = // one cell per bit, the lowest at the top
        Enumerable.Range(0, n).Select(index => new ROMCell(x, y + height*index, width, height)).ToList();
        Entities = cells.SelectMany(part => part.Entities).ToList();
        readRelay = new(x,y,width,height,startDisabled:true);
        enableRead = readRelay.Enable;
        disableRead = readRelay.Disable;
        read = readRelay.Input;
        readRelay.Output.Connect(Reads);
        Entities = Entities
            .Concat(readRelay.Entities)
            ;
    }
    
    // Shows each bit as a panel (MixedMemory calls it when the memory is visualized)
    public void AddVisualization()
    {
        foreach (ROMCell cell in cells)
        {
            cell.AddVisualization();
        }
        Entities = cells.SelectMany(cell => cell.Entities)
            .Concat(readRelay.Entities)
            .ToList();
    }

    public void AddClickTriggers()
    {
        
    }

    public void SetValue(uint val)
	{
		if (val >= Math.Pow(2, cells.Count))
		{
			throw new ArgumentOutOfRangeException("Tried to set the memory row to a value higher than it can store!");
		}
		foreach ((ROMCell cell, int index) in cells.Zip(Enumerable.Range(0, cells.Count)))
		{
			cell.StartsAtOne = ((val >> index) % 2) == 1;
		}
	}
}

public class ReadOnlyMemory : TriggerContraption, IMemoryGrid
{
    private readonly List<ROMRow> rows;
    private MixedMemory mem;
    public IList<Input> Address => mem.Address;
    public IList<Input> AddressClears => mem.AddressClears;
    public IList<Output> AddressOns => mem.AddressOns;
    public IList<Output> AddressOffs => mem.AddressOffs;
    public Input Read => mem.Read;
    public Input EnableWrite => mem.EnableWrite;
    public IList<Input> Writes => mem.Writes;
    public Input DisableWrite => mem.DisableWrite;
    public IList<IList<Input>> DirectWrites => mem.DirectWrites;
    public IList<IList<Input>> Clears => mem.Clears;
    public IList<Output> Values => mem.Values;
    private uint lastAddress;
    public uint LastAddress => lastAddress;
    public IReadOnlyList<IMemoryRow> Rows => mem.Rows;
    public ReadOnlyMemory(int n, int bits, double x, double y, double width, double height, bool visualized=false, bool visualizedAddress=false, bool clickable=false) 
    {
        rows = Enumerable.Range(0, n).Select(index => new ROMRow(bits, x + width*index, y+height, width, height)).ToList();
        Entities = rows.SelectMany(part => part.Entities).ToList();
        mem = new(rows.Cast<IMemoryRow>().ToList(), x, y, width, height, visualized, visualizedAddress, clickable);
        lastAddress = (uint)rows.Count - 1;
        Entities = mem.Entities;
    }

    public void SetValue(int index, uint value) { mem.SetValue(index, value); }
}