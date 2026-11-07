using LevelXMLUtils;
using LevelXML;
namespace LevelXMLUtils.Tests;
using Xunit;

using System.Collections.Generic;
using System.Linq;
using System;

// LevelCruncher used to look up "who targets this" by scanning every trigger, which took minutes on a level with
// tens of thousands of triggers. It now keeps an index instead. These tests run the original algorithm
// (SlowLevelCruncherReference) and the fast one on the same random trigger networks and require the exact same
// level out, so the fast one keeps doing all the same optimizations.
public class TestLevelCruncherEquivalence
{
    // A random network of relays, clickables (sources that are not relays) and panels, with Activate, Enable
    // and Disable connections, some starting disabled, some pointing back at themselves or loops.
    private static IList<Entity> RandomNetwork(int seed)
    {
        Random rng = new(seed);
        int relayCount = rng.Next(2, 30);
        List<Relay> relays = Enumerable.Range(0, relayCount).Select(_ => new Relay(0, 0, 5, 5, startDisabled: rng.Next(4) == 0)).ToList();
        List<Clickable> clickables = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => new Clickable(0, 0, 5, 5)).ToList();
        List<Panel> panels = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => new Panel(0, 0, 5, 5)).ToList();
        int edges = rng.Next(relayCount, relayCount * 3);
        // A trigger can't target the same entity twice (the engine rejects a second action on one target)
        HashSet<(string, string)> used = new();
        for (int i = 0; i < edges; i++)
        {
            bool fromClickable = rng.Next(5) == 0;
            int sourceIndex = fromClickable ? rng.Next(clickables.Count) : rng.Next(relays.Count);
            Output source = fromClickable ? clickables[sourceIndex].Output : relays[sourceIndex].Output;
            string sourceKey = (fromClickable ? "c" : "r") + sourceIndex;
            int kind = rng.Next(10);
            int targetIndex = kind < 2 ? rng.Next(panels.Count) : rng.Next(relays.Count);
            string targetKey = (kind < 2 ? "p" : "r") + targetIndex;
            if (!used.Add((sourceKey, targetKey))) { continue; }
            switch (kind)
            {
                case 0: source.Connect(panels[targetIndex].Input); break;
                case 1: source.Connect(panels[targetIndex].Reset); break;
                case 2: source.Connect(relays[targetIndex].Enable); break;
                case 3: source.Connect(relays[targetIndex].Disable); break;
                default: source.Connect(relays[targetIndex].Input); break;
            }
        }
        return clickables.SelectMany(c => c.Entities)
            .Concat(relays.SelectMany(r => r.Entities))
            .Concat(panels.SelectMany(p => p.Entities))
            .ToList();
    }

    private static string Xml(IList<Entity> entities) => new Level(entities.ToArray()).ToXML();

    private static void AssertSame(Action<IList<Entity>> slow, Action<IList<Entity>> fast)
    {
        int changed = 0;
        for (int seed = 0; seed < 400; seed++)
        {
            IList<Entity> expected = RandomNetwork(seed);
            IList<Entity> actual = RandomNetwork(seed);
            string before = Xml(actual);
            slow(expected);
            fast(actual);
            Assert.True(Xml(expected) == Xml(actual), $"seed {seed}: the fast cruncher's result differs from the original's");
            if (Xml(actual) != before) { changed++; }
        }
        // Guards against a generator that produces networks nothing can be done to
        Assert.True(changed > 50, $"only {changed} of 400 random networks were changed at all");
    }

    [Fact]
    public void OptimizeTriggers_MatchesTheOriginal() =>
        AssertSame(SlowLevelCruncherReference.OptimizeTriggers, LevelCruncher.OptimizeTriggers);

    [Fact]
    public void SimplifyRelayTriggers_MatchesTheOriginal() =>
        AssertSame(SlowLevelCruncherReference.SimplifyRelayTriggers, LevelCruncher.SimplifyRelayTriggers);

    [Fact]
    public void RemoveDisabledTriggersThatStayDisabled_MatchesTheOriginal() =>
        AssertSame(SlowLevelCruncherReference.RemoveDisabledTriggersThatStayDisabled, LevelCruncher.RemoveDisabledTriggersThatStayDisabled);

    [Fact]
    public void RemoveRelayTriggersThatAreNeverActivated_MatchesTheOriginal() =>
        AssertSame(SlowLevelCruncherReference.RemoveRelayTriggersThatAreNeverActivated, LevelCruncher.RemoveRelayTriggersThatAreNeverActivated);
}
