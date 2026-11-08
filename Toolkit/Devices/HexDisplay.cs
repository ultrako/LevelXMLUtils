using LevelXML;
namespace LevelXMLUtils.Devices;

/// <summary>Like <see cref="DecimalDisplay"/>, for 0 to 15.</summary>
public class HexDisplay : TriggerContraption
{
    public Input Reset;
    public Input One;
    public Input Two;
    public Input Four;
    public Input Eight;
    public List<Input> Sets;
    private List<Label> digits = new();
    public HexDisplay(double x=0, double y=0, double width=100, double height=100, int color=0xFFFFFF)
    {
        digits = Enumerable.Range(0, 16)
            .Select(index => new Label(index.ToString(), x,y, width,height, 1, 0x0, color))
            .ToList();
        Reset = new(digits.SelectMany(digit => digit.Show));
        // Setting a bit means hiding all the numbers that doesn't contain it as a sum
        // The One bit hides all the even numbers
        One = new(
            (new List<int>() {0,2,4,6,8,10,12,14})
            .SelectMany(index => digits[index].Hide)
        );
        
        Two = new(
            (new List<int>() {0,1,4,5,8,9,12,13})
            .SelectMany(index => digits[index].Hide)
        );
        Four = new(
            (new List<int>() {0,1,2,3,8,9,10,11})
            .SelectMany(index => digits[index].Hide)
        );
        Eight = new(
            (new List<int>() {0,1,2,3,4,5,6,7})
            .SelectMany(index => digits[index].Hide)
        );
        Sets = new() {One, Two, Four, Eight};
        List<Label> digitsReversed = new(digits);
        digitsReversed.Reverse();
        Entities = digitsReversed.SelectMany(digit => digit.Entities);
    }
}
public class HexDisplays : TriggerContraption
{
    private readonly List<HexDisplay> displays;
    public Input Reset;
    public List<Input> Ones;
    public List<Input> Twos;
    public List<Input> Fours;
    public List<Input> Eights;
    public List<Input> Sets;
    public HexDisplays(int n, double x=0, double y=0, double width=100, double height=100) 
    {
        displays = Enumerable.Range(0, n).Select(index => new HexDisplay(x + width*index, y, width, height)).ToList();
        Entities = displays.SelectMany(part => part.Entities).ToList();
        Reset = new Input(displays.SelectMany(display => display.Reset));
        Ones = displays.Select(display => display.One).ToList();
        Twos = displays.Select(display => display.Two).ToList();
        Fours = displays.Select(display => display.Four).ToList();
        Eights = displays.Select(display => display.Eight).ToList();
        Sets = displays.SelectMany(display => display.Sets).ToList();
    }
}