using LevelXML;
namespace LevelXMLUtils.Playback;

/// <summary>
/// A counter with one relay per value: only the current value's relay is enabled, and <see cref="Increment"/> fires it
/// (Value[i] fires on the i-th increment) and moves on to the next. It counts up once to n and then stops.
/// </summary>
public class UnaryCounter : TriggerContraption
{
	public Input Increment { get; }
	/// <summary>Set[i]: makes i the current value.</summary>
	public IReadOnlyList<Input> Set { get; }
	public Input Reset => Set[0];
	public IReadOnlyList<Output> Value { get; }

	public UnaryCounter(int n)
	{
		List<Relay> relays = Enumerable.Range(0, n).Select(i => new Relay(startDisabled: i != 0)).ToList();
		foreach (Relay relay in relays) { relay.Output.Connect(relay.Disable); }
		foreach ((Relay first, Relay second) in relays.Zip(relays.Skip(1))) { first.Output.Connect(second.Enable); }
		Value = relays.Select(relay => relay.Output).ToList();
		// last first, so the relay a firing enables has already been passed
		Increment = new Input(relays.SelectMany(relay => relay.Input).Reverse());
		Set = Enumerable.Range(0, n).Select(i => new Input(relays.Take(i).SelectMany(r => r.Disable).Concat(relays[i].Enable).Concat(relays.Skip(i + 1).SelectMany(r => r.Disable)))).ToList();
		Entities = relays.SelectMany(r => r.Entities).ToList();
	}
}
