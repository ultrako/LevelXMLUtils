using LevelXML;
namespace LevelXMLUtils.Playback;

/// <summary>
/// A dictionary of sound chords: groups of sound triggers that many frames start together become one relay, so those
/// frames need one arm instead of several (Re-Pair on sets: the pair found together in the most frames becomes a chord,
/// which can itself be part of a later chord). Lossless: every frame still starts exactly the same sound triggers, and
/// the order of sounds within a frame does not matter.
/// </summary>
/// <remarks>
/// A frame's sounds are a set (Video gives a second copy of a sound its own trigger), so no chord targets a trigger
/// twice and no frame targets a chord and one of its members. A chord used by a single caller in the end is folded
/// back into it by LevelCruncher.OptimizeTriggers.
/// </remarks>
public static class SoundChords
{
	/// <param name="plays">each frame's (or tie relay's) output and the sound triggers it starts</param>
	/// <param name="minUses">a pair becomes a chord only while at least this many plays share it: each use saves an
	/// arm, and the chord costs a relay and its own two arms</param>
	/// <returns>the chord relays' entities; every play is connected, through chords or directly</returns>
	public static List<Entity> Connect(IReadOnlyList<(Output From, IReadOnlyList<Sound> Sounds)> plays, int minUses, double x, double y)
	{
		Dictionary<Sound, int> symbolOf = new(ReferenceEqualityComparer.Instance);
		List<Input> inputs = new();
		List<HashSet<int>> sets = new();
		Dictionary<int, HashSet<int>> where = new();
		foreach (var (_, sounds) in plays)
		{
			HashSet<int> set = new();
			foreach (Sound sound in sounds)
			{
				if (!symbolOf.TryGetValue(sound, out int s))
				{
					s = symbolOf[sound] = inputs.Count;
					inputs.Add(sound.Input);
				}
				set.Add(s);
			}
			int index = sets.Count;
			sets.Add(set);
			foreach (int s in set) { (where.TryGetValue(s, out var w) ? w : where[s] = new()).Add(index); }
		}

		static long Pair(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
		Dictionary<long, int> count = new();
		PriorityQueue<long, int> best = new();
		void Change(long pair, int by)
		{
			int now = count.GetValueOrDefault(pair) + by;
			if (now <= 0) { count.Remove(pair); return; }
			count[pair] = now;
			if (by > 0) { best.Enqueue(pair, -now); }
		}
		foreach (HashSet<int> set in sets)
		{
			int[] items = set.ToArray();
			for (int i = 0; i < items.Length; i++) { for (int j = i + 1; j < items.Length; j++) { Change(Pair(items[i], items[j]), 1); } }
		}

		List<Entity> entities = new();
		while (best.TryDequeue(out long pair, out int negative))
		{
			// Entries are left in the queue when a count drops: only one that still matches its pair's count is current.
			if (count.GetValueOrDefault(pair) != -negative) { continue; }
			if (-negative < minUses) { break; }
			int a = (int)(pair >> 32), b = (int)(pair & 0xFFFFFFFF);
			Relay chord = new(x, y, 5, 5, false, 0);
			chord.Output.Connect(inputs[a]);
			chord.Output.Connect(inputs[b]);
			entities.AddRange(chord.Entities);
			int c = inputs.Count;
			inputs.Add(chord.Input);
			HashSet<int> uses = new(where[a]);
			uses.IntersectWith(where[b]);
			where[c] = new();
			foreach (int index in uses)
			{
				HashSet<int> set = sets[index];
				set.Remove(a);
				set.Remove(b);
				where[a].Remove(index);
				where[b].Remove(index);
				foreach (int other in set)
				{
					Change(Pair(a, other), -1);
					Change(Pair(b, other), -1);
					Change(Pair(c, other), 1);
				}
				set.Add(c);
				where[c].Add(index);
			}
			count.Remove(pair);
		}
		for (int index = 0; index < plays.Count; index++)
		{
			foreach (int s in sets[index]) { plays[index].From.Connect(inputs[s]); }
		}
		return entities;
	}
}
