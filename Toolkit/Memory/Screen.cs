using LevelXML;

using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;
public class Screen : TriggerContraption
{
    private readonly List<PanelRow> rows;
    public IList<IList<Input>> Ons => rows.Select(row => row.Inputs).ToList();
    public IList<IList<Input>> Offs => rows.Select(row => row.Resets).ToList();
    public Screen(double x, double y, double pixelWidth, double pixelHeight, int width, int height){
        rows = Enumerable.Range(0, height).Select(index => new PanelRow(width, x, y + pixelHeight*index, pixelWidth, pixelHeight, hasOff: false, onColor: 0xFFFFFF)).ToList();
        Entities = rows.SelectMany(part => part.Entities).ToList();
        Panel background = new(x+pixelWidth*(width-1)/2,y+pixelHeight*(height-1)/2,pixelWidth*width,pixelHeight*height, onColor:0x0, hasOff:false, startDisabled:false);
        Entities = background.Entities.Concat(Entities);
    }
}