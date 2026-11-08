namespace LevelXMLUtils.Devices;
using LevelXML;
/// <summary>Ten clickable keys, 0 to 9: a click fires Clicked and the Value outputs of the digit's bits (BCD, lowest bit first).</summary>
public class Numpad : TriggerContraption
{
    public Output Clicked;
    public IList<Output> Value;
    public Numpad (double x=0, double y=0, double width=100, double height=100, int color=0x0, int backgroundColor=0x999999, int outlineColor=-1)
    {
        Clickable one = new(x,y,width,height);
        Clickable two = new(x+width,y,width,height);
        Clickable three = new(x+width*2,y,width,height);
        Clickable four = new(x,y+height,width,height);
        Clickable five = new(x+width,y+height,width,height);
        Clickable six = new(x+width*2,y+height,width,height);
        Clickable seven = new(x,y+height*2,width,height);
        Clickable eight = new(x+width,y+height*2,width,height);
        Clickable nine = new(x+width*2,y+height*2,width,height);
        Clickable zero = new(x+width, y+height*3, width, height);
        Label oneLabel = new("1", x,y,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label twoLabel = new("2", x+width,y,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label threeLabel = new("3", x+width*2,y,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label fourLabel = new("4", x,y+height,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label fiveLabel = new("5", x+width,y+height,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label sixLabel = new("6", x+width*2,y+height,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label sevenLabel = new("7", x,y+height*2,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label eightLabel = new("8", x+width,y+height*2,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label nineLabel = new("9", x+width*2,y+height*2,width,height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Label zeroLabel = new("0", x+width, y+height*3, width, height, color:color, backgroundColor:backgroundColor, outlineColor:outlineColor);
        Value = new List<Output> 
        {
            new Output((new List<Output>{one.Output, three.Output, five.Output, seven.Output, nine.Output}).SelectMany(output => output)),
            new Output((new List<Output>{two.Output, three.Output, six.Output, seven.Output}).SelectMany(output => output)),
            new Output((new List<Output>{four.Output, five.Output, six.Output, seven.Output}).SelectMany(output => output)),
            new Output((new List<Output>{eight.Output, nine.Output}).SelectMany(output => output))
        };
        Clicked = new Output((new List<Output>{zero.Output, one.Output, two.Output, three.Output, four.Output, five.Output, six.Output, seven.Output, eight.Output, nine.Output}).SelectMany(output => output));
        Entities =
            one.Entities
            .Concat(two.Entities)
            .Concat(three.Entities)
            .Concat(four.Entities)
            .Concat(five.Entities)
            .Concat(six.Entities)
            .Concat(seven.Entities)
            .Concat(eight.Entities)
            .Concat(nine.Entities)
            .Concat(zero.Entities)
            .Concat(oneLabel.Entities)
            .Concat(twoLabel.Entities)
            .Concat(threeLabel.Entities)
            .Concat(fourLabel.Entities)
            .Concat(fiveLabel.Entities)
            .Concat(sixLabel.Entities)
            .Concat(sevenLabel.Entities)
            .Concat(eightLabel.Entities)
            .Concat(nineLabel.Entities)
            .Concat(zeroLabel.Entities);
    }
}