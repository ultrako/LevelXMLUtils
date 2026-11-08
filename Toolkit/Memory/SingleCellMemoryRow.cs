using LevelXML;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

public class SingleCellMemoryRow : TriggerContraption, IMemoryRow
{
    private ReadWriteMemoryCell cell;
    private Relay readRelay;
    public Input EnableRead => readRelay.Enable;
    public Input DisableRead => readRelay.Disable;
    private List<Input> enableWrites;
    public IList<Input> EnableWrites => enableWrites;
    private List<Input> disableWrites;
    public IList<Input> DisableWrites => disableWrites;
    private List<Input> clears;
    public IList<Input> Clears => clears;
    public Input Read => readRelay.Input;
    private List<Input> writes;
    public IList<Input> Writes => writes;
    private List<Input> directWrites;
    public IList<Input> DirectWrites => directWrites;
    private List<Output> values;
    public IList<Output> Values => values;
    public IReadOnlyList<ActivateTrigger>? BitTriggers => new[] { cell.BitTrigger };
    private double x,y,width,height;
    public SingleCellMemoryRow(int n, double x, double y, double width, double height) 
    {
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
        cell = new(x,y,width,height);
        readRelay = new(x,y,width,height,startDisabled:true);
        readRelay.Output.Connect(cell.Read);
        // We'll set these to be lists of empty inputs,
        // but then replace only the first input
        enableWrites = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        disableWrites = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        clears = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        writes = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        directWrites = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        values = Enumerable.Range(0, n).Select(index => new Output()).ToList();
        enableWrites[0] = cell.EnableWrite;
        disableWrites[0] = cell.DisableWrite;
        clears[0] = cell.Clear;
        writes[0] = cell.Write;
        directWrites[0] = cell.DirectWrite;
        values[0] = cell.Value;
        
        Entities = cell.Entities.Concat(readRelay.Entities);
    }

    public void AddVisualization()
    {
        cell.AddVisualization();
        Entities = Entities.Union(cell.Entities);
    }

    public void AddClickTriggers()
    {
        Clickable set = new(x,y,width,height);
        Clickable clear = new(x,y-height,width,height);
        set.Output.Connect(cell.Write);
        clear.Output.Connect(cell.Clear);
        Entities = Entities
            .Concat(set.Entities)
            .Concat(clear.Entities);
    }

    public void SetValue(uint val)
	{
        bool startEnabled = val switch
        {
            1 => true,
            0 => false,
            _ => throw new ArgumentOutOfRangeException("Tried to set a value other than 0 or 1 to a singleCellRow!")
        };
        cell.StartsAtOne = startEnabled;
	}
}