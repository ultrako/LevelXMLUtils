using LevelXML;
namespace LevelXMLUtils;
///<summary>
/// A trigger that is likely to be used often in trigger contraptions
/// Activates every single frame once it's activated, triggered by anything
///</summary>
public class EveryFrameTrigger : ActivateTrigger
{
	public EveryFrameTrigger(double x=0, double y=0, double width=5, double height=5) : base()
	{
		this.X = x;
		this.Y = y;
		this.Width=width;
		this.Height=height;
		this.TriggeredBy = 3;
		this.RepeatType = 4;
		this.Interval = float.NaN;
		this.StartDisabled = false;
	}
}
/// <summary>A small falling square: it touches an EveryFrameTrigger (triggered by anything) placed where it falls, so that one starts
/// on the first frame.</summary>
public class TinyFallingSquare : Rectangle
{
	public TinyFallingSquare(double x=0, double y=0) : base()
	{
		this.X = x;
		this.Y = y;
		this.Width = 5;
		this.Height = 5;
		this.Fixed = false;
		this.Density = 0.1;
	}
}
