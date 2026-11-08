using LevelXML;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

public class ReadWriteMemoryCell : TriggerContraption
{
    public Input EnableWrite;
    public Input DisableWrite;
    public Input Clear;
    public Input Read;
    public Input Write;
    public Input DirectWrite;
    public Output Value;
    private Relay writable;
    private double x, y, width, height;
    private Panel panel;
    private Relay cell;
    private bool visualized;
    /// <summary>The trigger that holds the bit: enabled while it is 1 (read it in a TriggerGraph with IsEnabled).</summary>
    public ActivateTrigger BitTrigger => cell.Output.Single();
    public ReadWriteMemoryCell(double x, double y, double width, double height)
    {
        writable = new(x, y, width, height, startDisabled:true);
        cell = new(x, y, width, height, startDisabled:true);
        panel = new(x, y, width, height, startDisabled: !StartsAtOne, outlineColor:0x0);
        writable.Output.Connect(cell.Enable);
        EnableWrite = writable.Enable;
        DisableWrite = writable.Disable;
        Clear = cell.Disable;
        Read = cell.Input;
        Write = writable.Input;
        DirectWrite = cell.Enable;
        Value = cell.Output;
        Entities = writable.Entities
        .Concat(cell.Entities);
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }

    public void AddVisualization()
    {
        writable.Output.Connect(panel.Input);
        Clear = Clear.Concat(panel.Reset);
        DirectWrite = DirectWrite.Concat(panel.Input);
        Entities = Entities.Concat(panel.Entities);
        visualized = true;
    }

    public void AddClickTrigger()
    {
        Clickable clickable = new(x,y,width,height);
        if (visualized)
        {
            clickable.Output.Connect(panel.Input);
        }
        clickable.Output.Connect(cell.Enable);
        Entities = Entities.Concat(clickable.Entities);
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

public class ReadWriteMemoryRow : TriggerContraption, IMemoryRow
{
    private readonly List<ReadWriteMemoryCell> cells;
    private Input enableRead;
    public Input EnableRead => enableRead;
    private Input disableRead;
    public Input DisableRead => disableRead;
    public IList<Input> EnableWrites => cells.Select(cell => cell.EnableWrite).ToList();
    public IList<Input> DisableWrites => cells.Select(cell => cell.DisableWrite).ToList();
    public IList<Input> Clears => cells.Select(cell => cell.Clear).ToList();
    private Input read;
    public Input Read => read;
    public IList<Input> Writes => cells.Select(cell => cell.Write).ToList();
    public IList<Input> DirectWrites => cells.Select(cell => cell.DirectWrite).ToList();
    public IList<Output> Values => cells.Select(cell => cell.Value).ToList();
    public IList<Input>? CellReads => cells.Select(cell => cell.Read).ToList();
    public IReadOnlyList<ActivateTrigger>? BitTriggers => cells.Select(cell => cell.BitTrigger).ToList();
    private double x,y,width,height;
    private Relay enableReadRelay;
    private Relay disableReadRelay;
    private Relay readRelay;
    public ReadWriteMemoryRow(int n, double x, double y, double width, double height) {
        cells = // one cell per bit, the lowest at the bottom
        Enumerable.Range(0, n).Reverse().Select(index => new ReadWriteMemoryCell(x, y + height*index, width, height)).ToList();
        Entities = cells.SelectMany(part => part.Entities).ToList();
        enableReadRelay = new(x,y,width,height);
        disableReadRelay = new(x,y,width,height);
        readRelay = new(x,y,width,height,startDisabled:true);
        enableReadRelay.Output.Connect(readRelay.Enable);
        disableReadRelay.Output.Connect(readRelay.Disable);
        readRelay.Output.Connect(cells.Select(cell => cell.Read).ToList());
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
        enableRead = enableReadRelay.Input;
        disableRead = disableReadRelay.Input;
        read = readRelay.Input;
        Entities = Entities
            .Concat(enableReadRelay.Entities)
            .Concat(disableReadRelay.Entities)
            .Concat(readRelay.Entities)
            ;
    }
    
    // Shows each bit as a panel (MixedMemory calls it when the memory is visualized)
    public void AddVisualization()
    {
        foreach (ReadWriteMemoryCell cell in cells)
        {
            cell.AddVisualization();
        }
        Entities = cells.SelectMany(cell => cell.Entities)
            .Concat(enableReadRelay.Entities)
            .Concat(disableReadRelay.Entities)
            .Concat(readRelay.Entities)
            .ToList();
    }

    public void AddClickTriggers()
    {
        foreach (ReadWriteMemoryCell cell in cells)
        {
            cell.AddClickTrigger();
        }
        Clickable clearRow = new(x,y-height,width,height);
        clearRow.Output.Connect(Clears);
        Entities = clearRow.Entities
            .Concat(cells.SelectMany(cell => cell.Entities)
            .Concat(enableReadRelay.Entities)
            .Concat(disableReadRelay.Entities)
            .Concat(readRelay.Entities)
            .ToList());
    }

    public void SetValue(uint val)
	{
		if (val >= Math.Pow(2, cells.Count))
		{
			throw new ArgumentOutOfRangeException("Tried to set the memory row to a value higher than it can store!");
		}
		foreach ((ReadWriteMemoryCell cell, int index) in cells.Zip(Enumerable.Range(0, cells.Count)))
		{
			cell.StartsAtOne = ((val >> index) % 2) == 1;
		}
	}
}

public class ReadWriteMemory : TriggerContraption, IMemoryGrid
{
    private readonly List<ReadWriteMemoryRow> rows;
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
    public ReadWriteMemory(int n, int bits, double x, double y, double width, double height, bool visualized=false, bool visualizedAddress=false, bool clickable=false) 
    {
        rows = Enumerable.Range(0, n).Select(index => new ReadWriteMemoryRow(bits, x + width*index, y+height, width, height)).ToList();
        Entities = rows.SelectMany(part => part.Entities).ToList();
        mem = new(rows.Cast<IMemoryRow>().ToList(), x, y, width, height, visualized, visualizedAddress, clickable);
        lastAddress = (uint)rows.Count - 1;
        Entities = mem.Entities;
    }

    public void SetValue(int index, uint value) { mem.SetValue(index, value); }
}