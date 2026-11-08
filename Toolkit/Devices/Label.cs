using LevelXML;
namespace LevelXMLUtils.Devices;

/// <summary>Text on a coloured box, in a group that Show and Hide make visible or not.</summary>
public class Label : TriggerContraption
{
    public Input Show;
    public Input Hide;
    public Label(string content, double x=0, double y=0, double width=100, double height=100, double textSize=1, int color=0x0, int backgroundColor=0x999999, int outlineColor=-1, bool startHidden=false, double textOffsetX=0, double textOffsetY=0)
    {
        Group group = new();
        group.X = x-width/2;
        group.Y = y-height/2;
        TextBox textBox = new();
        textBox.X = (width*textSize)/3.5 + textOffsetX;
        textBox.Y = (height*(textSize))/8 + (1.0-textSize)*height*(1.0/2.0) + textOffsetY;
        textBox.FontSize = (width * 2.0/3.0) * textSize;
        textBox.Content = content;
        textBox.Color = color;
        RectangleArtShape background = new(0,0, width, height, color:backgroundColor, outline:outlineColor);
        group.Items.Add(background);
        group.Items.Add(textBox);
        if (startHidden)
        {
            group.Opacity = 0;
        }
        Entities = new List<Entity>{group};
        Show = new(new List<Target> {new Target<Group>(group, new ChangeOpacity<Group>(100, 0))});
        Hide = new(new List<Target> {new Target<Group>(group, new ChangeOpacity<Group>(0, 0))});
    }
}