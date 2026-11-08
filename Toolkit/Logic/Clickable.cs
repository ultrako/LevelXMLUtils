using LevelXML;
namespace LevelXMLUtils;

/// <summary>A trigger the player fires by clicking it.</summary>
public class Clickable : TriggerContraption {
	public Output Output { get; }
	public Input Input;
	public Input Enable;
	public Input Disable;
	public Clickable(double x=0, double y=0, double width=100, double height=100, bool startDisabled=false, bool continuous=false) {
		// Only an output for this contraption
		RepeaterTrigger trigger = new(x,y,width, height);
		trigger.StartDisabled = startDisabled;
		if (continuous) {
			trigger.RepeatType = 4;
			trigger.Interval = float.NaN;
		}
		// Triggered by: click
		trigger.TriggeredBy = 6;
		Input = new() { new Target<Trigger>(trigger, new Activate())};
		Enable = new() { new Target<Trigger>(trigger, new Enable())};
		Disable = new() { new Target<Trigger>(trigger, new Disable())};
		Output = new() { trigger };
		Entities = new List<Entity>() { trigger };
	}
}
