using LevelXML;
namespace LevelXMLUtils.Controls;

public interface IControls
{
    public Output UpOn {get;}
    public Output UpOff {get;}
    public Output DownOn {get;}
    public Output DownOff {get;}
    public Output LeftOn {get;}
    public Output LeftOff {get;}
    public Output RightOn {get;}
    public Output RightOff {get;}
    public Output SpaceOn {get;}
    public Output SpaceOff {get;}
    public Output ShiftOn {get;}
    public Output ShiftOff {get;}
    public Output CtrlOn {get;}
    public Output CtrlOff {get;}
    public Vehicle Handle {get;}
    public IEnumerable<Entity> Entities {get;}
}