using LevelXML;
namespace LevelXMLUtils;

/// <summary>A sound effect played by triggers: <see cref="Input"/> plays it, every time. The id is the index into the game's
/// sound list (see tools/audio-mosaic/sounds.py), and the volume 0 to 1 (louder takes more than one copy).</summary>
public class Sound : TriggerContraption
{
	public Input Input { get; }
	public SoundTrigger Trigger { get; }

	public Sound(int soundId, double x = 0, double y = 0, double volume = 1)
	{
		// Multiple: played every time it is activated (the default, Once, plays the first time only); and only by other
		// triggers: the default, the main character, would also make it a contact sensor in the level
		Trigger = new SoundTrigger { X = x, Y = y, Sound = soundId, IsLocal = false, Volume = volume, RepeatType = RepeatType.Multiple, TriggeredBy = TriggeredBy.Triggers };
		Input = new Input { new Target<Trigger>(Trigger, new Activate()) };
		Entities = new List<Entity> { Trigger };
	}
}
