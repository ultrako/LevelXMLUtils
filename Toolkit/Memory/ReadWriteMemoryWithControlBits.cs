using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

// Made to test memoryRow-like display
public class ReadWriteMemoryWithControlBits : TriggerContraption, IMemoryGrid
{
    private MixedMemory mem;
    public IList<Input> Address => mem.Address;
    public IList<Input> AddressClears => mem.AddressClears;
    public IList<Output> AddressOns => mem.AddressOns;
    public IList<Output> AddressOffs => mem.AddressOffs;
    public Input Read => mem.Read;
    public Input EnableWrite => mem.EnableWrite;
    public IList<Input> Writes => mem.Writes;
    public Input DisableWrite => mem.DisableWrite;
    public IList<IList<Input>> Clears => mem.Clears;
    public IList<IList<Input>> DirectWrites => mem.DirectWrites;
    public IList<Output> Values => mem.Values;
    private uint lastAddress;
    public uint LastAddress => lastAddress;
    public IReadOnlyList<IMemoryRow> Rows => mem.Rows;
    public ReadWriteMemoryWithControlBits(int numRows, int bits, double x, double y, double width, double height, bool visualized=false, bool visualizedAddress=false, bool clickable=false)
    {
        int controlBits = 4;

        List<IMemoryRow> rows = Enumerable.Range(0, numRows-controlBits)
            .Select(index => new ReadWriteMemoryRow(bits, x+width*index, y, width, height))
            .Cast<IMemoryRow>()
            .Concat(Enumerable.Range(numRows-controlBits, controlBits)
            .Select(index => new SingleCellMemoryRow(bits, x+width*index, y, width, height)))
            .Cast<IMemoryRow>()
            .ToList();

        lastAddress = (uint)rows.Count - 1;

        mem = new(rows, x, y, width, height, visualized, visualizedAddress, clickable);
        Entities = mem.Entities;
    }

    void IMemoryGrid.SetValue(int index, uint value) { mem.SetValue(index, value); }
}