using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

public class ScreenAsMemory : TriggerContraption
{
    public IList<Input> Address;
    public IList<Input> AddressClears;
    public Input EnableWrite;
    public IList<Input> Writes;
    public IList<IList<Input>> DirectWrites;
    public IList<IList<Input>> Clears;
    public Input DisableWrite;
    public Input ClearScreen;
    private MixedMemory mem;
    public ScreenAsMemory(uint rows, uint columns, int bits, double x, double y, double width, double height)
    {
        double addressible = Math.Pow(2, bits);
        if (rows*columns > addressible)
        {
            throw new ArgumentOutOfRangeException("Screen too large to be addressed by " + bits.ToString() + " bits!");
        }
        List<IMemoryRow> memRows = new();
        for (int pixelY = 0; pixelY < rows; pixelY++)
        {
            for (int pixelX = 0; pixelX < columns; pixelX++)
            {
            
                DisplayMemoryRow pixel = new(bits, x + pixelX*width, y + pixelY*height, width, height);
                memRows.Add(pixel);
            }
        }
        Panel background = new(x+width*(columns-1)/2,y+height*(rows-1)/2,width*columns,height*rows, onColor:0x0, hasOff:false, startDisabled:false, foreground:true);

        mem = new(memRows, x, y, width, height, visualized: false, visualizedAddress: false, clickable: false);
        Address = mem.Address;
        AddressClears = mem.AddressClears;
        EnableWrite = mem.EnableWrite;
        Writes = mem.Writes;
        DirectWrites = mem.DirectWrites;
        Clears = mem.Clears;
        DisableWrite = mem.DisableWrite;
        ClearScreen = new Input(Enumerable.Range(0, (int)(rows*columns))
            .Select(index => memRows[index].Clears[0])
            .SelectMany(input => input));
        Entities = background.Entities.Concat(mem.Entities);
    }

    public void SetValue(int index, uint value) { mem.SetValue(index, value); }
}