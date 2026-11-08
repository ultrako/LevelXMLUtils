using LevelXML;
namespace LevelXMLUtils.Devices;

/// <summary>A digit 0 to 9 shown by labels stacked with 0 on top: Reset shows them all, and each bit's input (One, Two, Four, Eight)
/// hides the digits without that bit, so the smallest still shown is the value. SetUnary[d] shows d directly.</summary>
public class DecimalDisplay : TriggerContraption
{
    public Input Reset;
    public Input One;
    public Input Two;
    public Input Four;
    public Input Eight;
    public Input SetAll;
    public List<Input> SetUnary;
    public List<Input> Sets;
    private List<Label> digits = new();
    public DecimalDisplay(double x=0, double y=0, double width=100, double height=100, int color=0xFFFFFF)
    {
        digits = Enumerable.Range(0, 10)
            .Select(index => new Label(index.ToString(), x,y, width,height, 1, 0x0, color, startHidden:false))
            .ToList();
        Reset = new(digits.SelectMany(digit => digit.Show));
        // Setting a bit means hiding all the numbers that doesn't contain it as a sum
        // The One bit hides all the even numbers
        One = new(
            (new List<int>() {0,2,4,6,8})
            .SelectMany(index => digits[index].Hide)
        );
        
        Two = new(
            (new List<int>() {0,1,4,5,8,9})
            .SelectMany(index => digits[index].Hide)
        );
        Four = new(
            (new List<int>() {0,1,2,3,8,9})
            .SelectMany(index => digits[index].Hide)
        );
        Eight = new(
            (new List<int>() {0,1,2,3,4,5,6,7})
            .SelectMany(index => digits[index].Hide)
        );
        SetAll = new(
            (new List<int>() {0,1,2,3,4,5,6,7,8,9})
            .SelectMany(index => digits[index].Hide)
        );
        SetUnary = new(
            Enumerable.Range(0, 10)
            .Select(index => 
            new Input(Enumerable.Range(0, 10)
            .Where(otherIndex => otherIndex != index)
            .SelectMany(otherIndex => digits[otherIndex].Hide)))
        );
        Sets = new() {One, Two, Four, Eight};
        List<Label> digitsReversed = new(digits);
        digitsReversed.Reverse();
        Entities = digitsReversed.SelectMany(digit => digit.Entities);
    }
}
public class DecimalDisplays : TriggerContraption
{
    private readonly List<DecimalDisplay> displays;
    public Input Reset;
    public List<Input> Resets;
    public List<Input> Ones;
    public List<Input> Twos;
    public List<Input> Fours;
    public List<Input> Eights;
    public Input Set;
    public List<Input> Sets;
    public DecimalDisplays(int n, double x=0, double y=0, double width=100, double height=100) 
    {
        displays = Enumerable.Range(0, n).Select(index => new DecimalDisplay(x + width*index, y, width, height)).ToList();
        Entities = displays.SelectMany(part => part.Entities).ToList();
        Reset = new Input(displays.SelectMany(display => display.Reset));
        Resets = displays.Select(display => display.Reset).ToList();
        Ones = displays.Select(display => display.One).ToList();
        Twos = displays.Select(display => display.Two).ToList();
        Fours = displays.Select(display => display.Four).ToList();
        Eights = displays.Select(display => display.Eight).ToList();
        Set = new Input(displays.SelectMany(display => display.SetAll));
        Sets = displays.SelectMany(display => display.Sets).ToList();
        double centerOffset = ((n-1) * width)/2;
        Art background = new RectangleArtShape(x + centerOffset,y,width*n+2,height+2,0,0xEEEEEE,0,100);
        Entities = Entities.Concat(new List<Entity>{background});
    }
}