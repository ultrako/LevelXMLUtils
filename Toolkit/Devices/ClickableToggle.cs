using LevelXML;
namespace LevelXMLUtils.Devices;

/// <summary>A toggle the player clicks: On on the first click, Off on the next, and so on. Two click triggers on top of each other,
/// one enabled at a time.</summary>
public class ClickableToggle : Toggle
{
	public ClickableToggle(double x=0, double y=0, double width=100, double height=100) :
	base(x,y,width,height)
	{
		// For a toggle, we'll have a clickable trigger overlayed on top of another
		// and the bottom clickable will be of a nan interval
		// So 3 triggers and no need for power.
		Clickable top = new(x,y,width,height, startDisabled: false);
		Clickable bottom = new(x,y,width,height, startDisabled: true);
		// They both enable eachother
		top.Output.Connect(bottom.Enable);
		bottom.Output.Connect(top.Enable);
		// They both disable themselves
		top.Output.Connect(top.Disable);
		bottom.Output.Connect(bottom.Disable);
		On = top.Output;
		Off = bottom.Output;
		Entities = bottom.Entities.Concat(top.Entities);
	}
}
