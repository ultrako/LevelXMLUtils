using LevelXML;
namespace LevelXMLUtils;
///<summary>
/// A trigger that is likely to be used often in trigger contraptions
///</summary>
public class RepeaterTrigger : ActivateTrigger
{
	public RepeaterTrigger(double x=0, double y=0, double width=5, double height=5) : base()
	{
		this.X = x;
		this.Y = y;
		this.Width=width;
		this.Height=height;
		this.TriggeredBy = 5;
		this.RepeatType = 2;
		this.StartDisabled = false;
	}
}
