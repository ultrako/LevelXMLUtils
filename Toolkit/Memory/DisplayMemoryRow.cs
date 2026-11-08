using LevelXML;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

// public class ROMCell : TriggerContraption
// {
//     public Input EnableRead;
//     public Input DisableRead;
//     public Input EnableWrite = new Input();
//     public Input DisableWrite = new Input();
//     public Input Clear = new Input();
//     public Input Read;
//     public Input Write = new Input();
//     public Output Value;
//     //private Relay writable;
//     private double x, y, width, height;
//     private Panel panel;
//     private Relay cell;
//     public ROMCell(double x, double y, double width, double height)
//     {
//         Relay readable = new(x, y, width, height, startDisabled:true);
//         // writable = new(x, y, width, height, startDisabled:true);
//         cell = new(x, y, width, height, startDisabled:true);
//         panel = new(x, y, width, height, startDisabled: InitialValue);
//         cell.Output.Connect(readable.Input);
//         // writable.Output.Connect(cell.Enable);
//         EnableRead = readable.Enable;
//         DisableRead = readable.Disable;
//         // EnableWrite = writable.Enable;
//         // DisableWrite = writable.Disable;
//         // Clear = cell.Disable;
//         Read = cell.Input;
//         // Write = writable.Input;
//         Value = readable.Output;
//         Entities = readable.Entities
//         // .Concat(writable.Entities)
//         .Concat(cell.Entities);
//         this.x = x;
//         this.y = y;
//         this.width = width;
//         this.height = height;
//     }

//     public void AddVisualization()
//     {
//         // writable.Output.Connect(panel.Input);
//         // Clear = Clear.Concat(panel.PowerFirst);
//         Entities = Entities.Concat(panel.Entities);
//     }

//     public bool InitialValue 
//     { 
//         get { return cell.StartDisabled; }
//         set 
//         { 
//             cell.StartDisabled = value;
//             panel.StartDisabled = value;
//         }
//     }
// }

// Called "memoryRow" because it implements IMemoryRow
// Is write-only, and is just a panel.
public class DisplayMemoryRow : TriggerContraption, IMemoryRow
{
    private Panel panel;
    private IList<Output> listOfEmptyOutputs;
    public Input EnableRead => new Input();
    public Input DisableRead => new Input();
    private List<Input> enableWrites;
    public IList<Input> EnableWrites => enableWrites;
    private List<Input> disableWrites;
    public IList<Input> DisableWrites => disableWrites;
    private List<Input> clears;
    public IList<Input> Clears => clears;
    public Input Read => new Input();
    private List<Input> writes;
    public IList<Input> Writes => writes;
    private List<Input> directWrites;
    public IList<Input> DirectWrites => directWrites;
    public IList<Output> Values => listOfEmptyOutputs;
    public DisplayMemoryRow(int n, double x, double y, double width, double height) 
    {
        Relay conditionalWrite = new(x,y,width,height, startDisabled:true);
        panel = new(x,y,width,height,onColor:0xFFFFFF,offColor:0x0,foreground:true,hasOff:false);
        conditionalWrite.Output.Connect(panel.Input);
        listOfEmptyOutputs = Enumerable.Range(0, n).Select(index => new Output()).ToList();
        // We'll set these to be lists of empty inputs,
        // but then replace only the first input
        enableWrites = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        disableWrites = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        clears = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        writes = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        directWrites = Enumerable.Range(0, n).Select(index => new Input()).ToList();
        enableWrites[0] = conditionalWrite.Enable;
        disableWrites[0] = conditionalWrite.Disable;
        clears[0] = panel.Reset;
        writes[0] = conditionalWrite.Input;
        directWrites[0] = panel.Input;
        Entities = conditionalWrite.Entities.Concat(panel.Entities);
    }
    
    // This is always visualized, so no need to do anything
    public void AddVisualization(){}
    public void AddClickTriggers(){}

    public void SetValue(uint val)
	{
        panel.StartDisabled = val switch
        {
            0 => true,
            1 => false,
            _ => throw new ArgumentOutOfRangeException("Tried to set a value other than 0 or 1 to a displayRow!")
        };
	}
}
