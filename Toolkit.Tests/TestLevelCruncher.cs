using LevelXMLUtils;
using LevelXML;
namespace LevelXMLUtils.Tests;
using Xunit;

using System.Collections.Generic;
using System.Linq;
using System;

public class TestLevelCruncher
{

    [Fact]
    public void TestSimplifyRelayTriggers_SimplifyRelay()
    {
        List<Clickable> buttons = new() { new(0,0,0,0), new(0,0,0,0) };
        Relay relay = new(0,0,0,0);
        Panel panel = new(0,0,0,0);
        buttons.Select(button => button.Output).ToList().Connect(relay.Input);
        relay.Output.Connect(panel.Input);
        IList<Entity> entities = buttons.SelectMany(button => button.Entities)
            .Concat(relay.Entities)
            .Concat(panel.Entities)
            .ToList();
        LevelCruncher.SimplifyRelayTriggers(entities);
        // Console.WriteLine(new Level(entities.ToArray()).ToXML());
        Assert.DoesNotContain(relay.Entities.First(), entities);
        foreach(ActivateTrigger trigger in buttons.SelectMany(button => button.Entities).OfType<ActivateTrigger>())
        {
            IEnumerable<Entity> targetedEntities = trigger.Targets.Select(target => target.Targeted);
            Assert.NotEmpty(panel.Entities.Intersect(targetedEntities));
        }
    }

    [Fact]
    public void TestSimplifyRelayTriggers_InboundSharesTargets()
    {
        Relay conditional = new(0,0,0,0, startDisabled:true);
        Relay disableConditional = new(0,0,0,0);
        Clickable doThingConditionally = new(0,0,0,0);
        Panel sink = new(0,0,0,0);
        doThingConditionally.Output.Connect(conditional.Input);
        doThingConditionally.Output.Connect(disableConditional.Input);
        disableConditional.Output.Connect(conditional.Disable);
        conditional.Output.Connect(sink.Input);
        IList<Entity> entities = conditional.Entities
            .Concat(disableConditional.Entities)
            .Concat(doThingConditionally.Entities)
            .Concat(sink.Entities)
            .ToList();
        // If you try to add a second action to a trigger,
        // an underlying library throws.
        // and the test is to make sure it doesn't do that
        LevelCruncher.SimplifyRelayTriggers(entities);
    }

    [Fact]
    public void TestSimplifyRelayTriggers_RelaySelfLoops()
    {
        List<Clickable> buttons = new() { new(0,0,0,0), new(0,0,0,0) };
        Relay relay = new(0,0,0,0);
        Panel panel = new(0,0,0,0);
        buttons.Select(button => button.Output).ToList().Connect(relay.Input);
        relay.Output.Connect(panel.Input);
        relay.Output.Connect(relay.Input);
        IList<Entity> entities = buttons.SelectMany(button => button.Entities)
            .Concat(relay.Entities)
            .Concat(panel.Entities)
            .ToList();
        LevelCruncher.SimplifyRelayTriggers(entities);
        // Console.WriteLine(new Level(entities.ToArray()).ToXML());
        Assert.Contains(relay.Entities.First(), entities);
    }

    [Fact]
    public void TestOptimizeTriggers_RelayChain()
    {
        List<Clickable> buttons = new() { new(0,0,0,0) };
        List<Relay> relays = Enumerable.Range(0, 10).Select(i => new Relay(20 + 20*i, 0, 20, 20)).ToList();
        Panel sink = new(220, 0, 20, 20);
        buttons.Select(button => button.Output).ToList().Connect(relays[0].Input);
        foreach((Relay first, Relay second) in relays.Zip(relays.Skip(1)))
        {
            first.Output.Connect(second.Input);
        }
        relays[^1].Output.Connect(sink.Input);
        IList<Entity> entities = buttons.SelectMany(button => button.Entities)
            .Concat(relays.SelectMany(relay => relay.Entities))
            .Concat(sink.Entities)
            .ToList();
        LevelCruncher.OptimizeTriggers(entities);
        Assert.Empty(relays.SelectMany(relay => relay.Entities).Intersect(entities));
        foreach(ActivateTrigger trigger in buttons.SelectMany(button => button.Entities).OfType<ActivateTrigger>())
        {
            IEnumerable<Entity> targetedEntities = trigger.Targets.Select(target => target.Targeted);
            Assert.NotEmpty(sink.Entities.Intersect(targetedEntities));
        }
    }

    [Fact]
    public void TestMinifyRelayTriggers_DropsOnlyALoneActivateActionOnTriggerTargets()
    {
        Relay source = new(0, 0, 5, 5);
        Relay activated = new(0, 0, 5, 5);
        Relay disabled = new(0, 0, 5, 5);
        Relay enabled = new(0, 0, 5, 5);
        Panel panel = new(0, 0, 5, 5);
        source.Output.Connect(activated.Input);
        source.Output.Connect(disabled.Disable);
        source.Output.Connect(enabled.Enable);
        source.Output.Connect(panel.Input);
        Clickable button = new(0, 0, 5, 5);
        button.Output.Connect(source.Input);
        Entity[] entities = button.Entities.Concat(source.Entities).Concat(activated.Entities).Concat(disabled.Entities)
            .Concat(enabled.Entities).Concat(panel.Entities).ToArray();
        string xml = new Level(entities).ToXML();

        System.Xml.Linq.XElement Triggers(string levelXml) => System.Xml.Linq.XElement.Parse(levelXml).Element("triggers")!;
        List<System.Xml.Linq.XElement> TriggerTargets(string levelXml) =>
            Triggers(levelXml).Elements().SelectMany(trigger => trigger.Elements().Where(element => element.Name == "t")).ToList();
        List<System.Xml.Linq.XElement> before = TriggerTargets(xml);
        List<System.Xml.Linq.XElement> after = TriggerTargets(LevelCruncher.MinifyRelayTriggers(xml));

        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(target => target.Attribute("i")!.Value), after.Select(target => target.Attribute("i")!.Value));
        // every activate action on a trigger target is gone, and disable and enable are kept
        int activates = before.Count(target => target.Elements().Any(action => action.Attribute("i")!.Value == "0"));
        Assert.True(activates >= 2, "the test level should have activate targets to collapse");
        Assert.DoesNotContain(after, target => target.Elements().Any(action => action.Attribute("i")!.Value == "0"));
        Assert.Equal(before.Count(target => target.Elements().Any(action => action.Attribute("i")!.Value == "1")),
            after.Count(target => target.Elements().Any(action => action.Attribute("i")!.Value == "1")));
        Assert.Equal(before.Count(target => target.Elements().Any(action => action.Attribute("i")!.Value == "2")),
            after.Count(target => target.Elements().Any(action => action.Attribute("i")!.Value == "2")));
        // the panel's shape target keeps its change opacity action
        Assert.Equal(Triggers(xml).Descendants("sh").Count(), Triggers(LevelCruncher.MinifyRelayTriggers(xml)).Descendants("sh").Count());
        Assert.Contains(Triggers(LevelCruncher.MinifyRelayTriggers(xml)).Descendants("sh"), target => target.Elements().Any(action => action.Attribute("i")?.Value == "3"));
    }

    [Fact]
    public void TestMinifyRelayTriggers_DropsP0OnlyWhenItIs100_AndNeverP1_OnGroupOpacityTargets()
    {
        Panel panel = new(0, 0, 5, 5, foreground: true);
        Relay showIt = new(0, 0, 5, 5);
        Relay hideIt = new(0, 0, 5, 5);
        showIt.Output.Connect(panel.Input);
        hideIt.Output.Connect(panel.Reset);
        Clickable button = new(0, 0, 5, 5);
        button.Output.Connect(showIt.Input);
        button.Output.Connect(hideIt.Input);
        Entity[] entities = button.Entities.Concat(showIt.Entities).Concat(hideIt.Entities).Concat(panel.Entities).ToArray();
        string xml = new Level(entities).ToXML();
        System.Xml.Linq.XElement[] Actions(string levelXml) => System.Xml.Linq.XElement.Parse(levelXml).Element("triggers")!
            .Descendants("g").SelectMany(target => target.Elements("a")).ToArray();
        System.Xml.Linq.XElement[] before = Actions(xml), after = Actions(LevelCruncher.MinifyRelayTriggers(xml));

        Assert.Equal(2, before.Length);
        Assert.Contains(before, action => action.Attribute("p0")?.Value == "100");
        Assert.Contains(before, action => action.Attribute("p0")?.Value == "0");
        // p0 = 100 is gone, p0 = 0 (the hide) stays, and every action keeps its p1 duration
        Assert.DoesNotContain(after, action => action.Attribute("p0")?.Value == "100");
        Assert.Contains(after, action => action.Attribute("p0")?.Value == "0");
        Assert.All(after, action => Assert.NotNull(action.Attribute("p1")));
    }

    // Three sources activating overlapping targets: s1 and s2 both activate A, B and C (three in common, worth a relay), s3 activates
    // A, B and D (only two in common with either, so it is left as it is). One relay is added, s1 and s2 reach A, B, C through it,
    // and every source still fires exactly the same targets.
    [Fact]
    public void TestFactorCommonActivations_SharesOverlappingActivations_WithoutChangingWhatFires()
    {
        Relay s1 = new(0, 0, 5, 5), s2 = new(0, 0, 5, 5), s3 = new(0, 0, 5, 5);
        Relay a = new(0, 0, 5, 5), b = new(0, 0, 5, 5), c = new(0, 0, 5, 5), d = new(0, 0, 5, 5);
        Panel panel = new(0, 0, 5, 5);
        foreach (Relay source in new[] { s1, s2 }) { source.Output.Connect(a.Input); source.Output.Connect(b.Input); source.Output.Connect(c.Input); }
        s3.Output.Connect(a.Input); s3.Output.Connect(b.Input); s3.Output.Connect(d.Input);
        a.Output.Connect(panel.Input);
        List<Entity> entities = new[] { s1, s2, s3, a, b, c, d }.SelectMany(relay => relay.Entities).Concat(panel.Entities).ToList();
        ActivateTrigger[] all = entities.OfType<ActivateTrigger>().ToArray();
        ActivateTrigger[] sources = new[] { s1, s2, s3 }.Select(relay => relay.Output[0]).ToArray();
        int armsBefore = all.Sum(trigger => trigger.TargetCount);

        var beforeGraph = TriggerGraph.FromTriggers(all);
        HashSet<string> Fired(TriggerGraph graph, ActivateTrigger source, IEnumerable<ActivateTrigger> among)
        {
            graph.Pulse(TriggerGraph.IdFor(source));
            return among.Select(TriggerGraph.IdFor).Where(graph.LastFired.Contains).ToHashSet();
        }
        List<HashSet<string>> before = sources.Select(source => Fired(beforeGraph, source, all)).ToList();

        int added = LevelCruncher.FactorCommonActivations(entities, sources);
        ActivateTrigger[] afterAll = entities.OfType<ActivateTrigger>().ToArray();
        var afterGraph = TriggerGraph.FromTriggers(afterAll);
        List<HashSet<string>> after = sources.Select(source => Fired(afterGraph, source, all)).ToList();

        Assert.Equal(1, added);
        Assert.Equal(all.Length + 1, afterAll.Length);
        for (int i = 0; i < sources.Length; i++) { Assert.Equal(before[i], after[i]); }
        Assert.Equal(armsBefore - 1, afterAll.Sum(trigger => trigger.TargetCount)); // 3 + 3 + 3 = 9 arms became 2 + 3 + 3 = 8
        Assert.DoesNotContain(new[] { s1, s2 }.SelectMany(relay => relay.Output[0].Targets), target => target.Targeted == a.Output[0] || target.Targeted == b.Output[0] || target.Targeted == c.Output[0]);
        Assert.Empty(afterGraph.Runaway);
    }

    // Random networks of sources and targets: after sharing, every source must fire exactly the same original triggers, no more
    // work than before must be done per pulse (each target reached once, so nothing fires twice), and there must be fewer arms.
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(true, 20)]
    
    public void TestFactorCommonActivations_OnRandomNetworks_KeepsWhatFires_AndReducesArms(bool exactSetsFirst, int minSharedTargets)
    {
        int improved = 0;
        for (int seed = 0; seed < 60; seed++)
        {
            Random rng = new(seed);
            int sourceCount = rng.Next(3, 14), targetCount = rng.Next(8, 60);
            double density = 0.3 + rng.NextDouble() * 0.5;
            List<Relay> sourceRelays = Enumerable.Range(0, sourceCount).Select(_ => new Relay(0, 0, 5, 5)).ToList();
            List<Relay> targetRelays = Enumerable.Range(0, targetCount).Select(_ => new Relay(0, 0, 5, 5)).ToList();
            foreach (Relay source in sourceRelays) { foreach (Relay target in targetRelays.Where(_ => rng.NextDouble() < density)) { source.Output.Connect(target.Input); } }
            List<Entity> entities = sourceRelays.Concat(targetRelays).SelectMany(relay => relay.Entities).ToList();
            ActivateTrigger[] originals = entities.OfType<ActivateTrigger>().ToArray();
            ActivateTrigger[] sources = sourceRelays.Select(relay => relay.Output[0]).ToArray();
            int armsBefore = originals.Sum(trigger => trigger.TargetCount);
            var beforeGraph = TriggerGraph.FromTriggers(originals);
            List<int> actionsBefore = new();
            List<HashSet<string>> before = sources.Select(source => { beforeGraph.Pulse(TriggerGraph.IdFor(source)); actionsBefore.Add(beforeGraph.ActionsCarriedOut); return originals.Select(TriggerGraph.IdFor).Where(beforeGraph.LastFired.Contains).ToHashSet(); }).ToList();

            LevelCruncher.FactorCommonActivations(entities, sources, minSharedTargets, exactSetsFirst);
            ActivateTrigger[] afterAll = entities.OfType<ActivateTrigger>().ToArray();
            var afterGraph = TriggerGraph.FromTriggers(afterAll);
            for (int i = 0; i < sources.Length; i++)
            {
                afterGraph.Pulse(TriggerGraph.IdFor(sources[i]));
                Assert.True(before[i].SetEquals(originals.Select(TriggerGraph.IdFor).Where(afterGraph.LastFired.Contains)), $"seed {seed} source {i}: a different set of triggers fired");
                Assert.Empty(afterGraph.Runaway);
                // each relay a pulse goes through costs one extra action (the arm into it), and is reached at most once: nothing fires twice
                Assert.True(afterGraph.ActionsCarriedOut <= actionsBefore[i] + (afterAll.Length - originals.Length), $"seed {seed} source {i}: {actionsBefore[i]} actions became {afterGraph.ActionsCarriedOut}");
            }
            int armsAfter = afterAll.Sum(trigger => trigger.TargetCount);
            Assert.True(armsAfter <= armsBefore, $"seed {seed}: {armsBefore} arms became {armsAfter}");
            if (armsAfter < armsBefore) { improved++; }
        }
        if (minSharedTargets == 3) { Assert.True(improved > 30, $"only {improved} of 60 random networks got smaller"); }
    }

    // Targets activated by exactly the same sources go behind ONE relay however many sources there are (the pairwise pass would need about one relay
    // per source): 5 sources x 4 targets = 20 arms become 5 + 4 = 9 with a single relay, and the other target, whose sources differ, is left alone.
    [Fact]
    public void TestFactorCommonActivations_ExactSetsFirst_GivesASharedSetOneRelay()
    {
        List<Relay> sources = Enumerable.Range(0, 5).Select(_ => new Relay(0, 0, 5, 5)).ToList();
        List<Relay> shared = Enumerable.Range(0, 4).Select(_ => new Relay(0, 0, 5, 5)).ToList();
        Relay lone = new(0, 0, 5, 5);
        foreach (Relay source in sources) { foreach (Relay target in shared) { source.Output.Connect(target.Input); } }
        sources[0].Output.Connect(lone.Input); sources[1].Output.Connect(lone.Input);
        List<Entity> entities = sources.Concat(shared).Append(lone).SelectMany(relay => relay.Entities).ToList();
        ActivateTrigger[] originals = entities.OfType<ActivateTrigger>().ToArray();
        var beforeGraph = TriggerGraph.FromTriggers(originals);
        List<HashSet<string>> before = sources.Select(source => { beforeGraph.Pulse(TriggerGraph.IdFor(source.Output[0])); return originals.Select(TriggerGraph.IdFor).Where(beforeGraph.LastFired.Contains).ToHashSet(); }).ToList();

        int added = LevelCruncher.FactorCommonActivations(entities, sources.Select(relay => relay.Output[0]), minSharedTargets: 1000, exactSetsFirst: true);

        Assert.Equal(1, added);
        ActivateTrigger[] afterAll = entities.OfType<ActivateTrigger>().ToArray();
        Assert.Equal(22 - 20 + 9, afterAll.Sum(trigger => trigger.TargetCount)); // the lone target's 2 arms stay
        var afterGraph = TriggerGraph.FromTriggers(afterAll);
        for (int i = 0; i < sources.Count; i++)
        {
            afterGraph.Pulse(TriggerGraph.IdFor(sources[i].Output[0]));
            Assert.True(before[i].SetEquals(originals.Select(TriggerGraph.IdFor).Where(afterGraph.LastFired.Contains)));
        }
    }

    [Fact]
    public void TestFactorCommonActivations_LeavesAlone_WhatIsNotWorthSharing()
    {
        Relay s1 = new(0, 0, 5, 5), s2 = new(0, 0, 5, 5);
        Relay a = new(0, 0, 5, 5), b = new(0, 0, 5, 5), e = new(0, 0, 5, 5);
        // two sources x two targets is 4 arms either way (2 + 2), so no relay is added
        foreach (Relay source in new[] { s1, s2 }) { source.Output.Connect(a.Input); source.Output.Connect(b.Input); }
        // an enable on a source is never moved
        s1.Output.Connect(e.Enable);
        List<Entity> entities = new[] { s1, s2, a, b, e }.SelectMany(relay => relay.Entities).ToList();
        int armsBefore = entities.OfType<ActivateTrigger>().Sum(trigger => trigger.TargetCount);
        Assert.Equal(0, LevelCruncher.FactorCommonActivations(entities, new[] { s1, s2 }.Select(relay => relay.Output[0])));
        Assert.Equal(armsBefore, entities.OfType<ActivateTrigger>().Sum(trigger => trigger.TargetCount));
    }

    [Fact]
    public void TestRoundNumber_RoundsLongDecimals_AndLeavesEverythingElseAlone()
    {
        Assert.Equal("59.4", LevelCruncher.RoundNumber("59.399999999999636", 3));
        Assert.Equal("129.051", LevelCruncher.RoundNumber("129.05052631578948", 3));
        Assert.Equal("-0.333", LevelCruncher.RoundNumber("-0.33333333", 3));
        Assert.Equal("0", LevelCruncher.RoundNumber("-0.0000004", 3));
        Assert.Equal("15.5", LevelCruncher.RoundNumber("15.5", 3));
        Assert.Equal("100", LevelCruncher.RoundNumber("100", 3));
        Assert.Equal("t", LevelCruncher.RoundNumber("t", 3));
        Assert.Equal("1e-05", LevelCruncher.RoundNumber("1e-05", 3));
        Assert.Equal("NaN", LevelCruncher.RoundNumber("NaN", 3));
    }

    [Fact]
    public void TestMinifyRelayTriggers_RoundsNumbersAndVertexListsOnlyWhenAsked()
    {
        Rectangle box = new() { X = 59.399999999999636, Y = 10, Width = 15.123456789, Height = 5 };
        RectangleArtShape art = new(100.123456789, 200, 30, 40);
        string xml = new Level(new Entity[] { box, art }).ToXML();
        string untouched = LevelCruncher.MinifyRelayTriggers(xml);
        string rounded = LevelCruncher.MinifyRelayTriggers(xml, roundDecimals: 3);
        Assert.Contains("59.399999999999636", untouched);
        Assert.DoesNotContain("59.399999999999636", rounded);
        Assert.Contains("p0=\"59.4\"", rounded);
        Assert.Contains("p2=\"15.123\"", rounded);
        Assert.Contains("p0=\"100.123\"", rounded);
        // vertices: "-15_-20" style lists survive, and the level still reads back
        Level roundTrip = new(rounded);
        Assert.Equal(2, roundTrip.Entities.Count());
        Assert.Equal(59.4, roundTrip.Entities.First().X, 3);
    }
}