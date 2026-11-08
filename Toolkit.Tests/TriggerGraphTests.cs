using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LevelXMLUtils;
using LevelXML;
using Xunit;

namespace LevelXMLUtils.Tests;

public class TriggerGraphTests
{
    [Fact]
    public void Pulse_FiresActivatedNodesInOrder()
    {
        var graph = new TriggerGraph(new[]
        {
            new TriggerNode { Id = "A", Enabled = true, Outgoing = { new TriggerEdge { TargetId = "B", Action = TriggerActionKind.Activate } } },
            new TriggerNode { Id = "B", Enabled = true },
        });

        graph.Pulse("A");

        Assert.True(graph.IsEnabled("B"));
    }

    [Fact]
    public void Pulse_RespectsDisabledStatesAndEnableDisableActions()
    {
        var graph = new TriggerGraph(new[]
        {
            new TriggerNode { Id = "A", Enabled = true, Outgoing = { new TriggerEdge { TargetId = "B", Action = TriggerActionKind.Disable } } },
            new TriggerNode { Id = "B", Enabled = true },
            new TriggerNode { Id = "C", Enabled = true, Outgoing = { new TriggerEdge { TargetId = "B", Action = TriggerActionKind.Enable } } },
        });

        graph.Pulse("A");
        Assert.False(graph.IsEnabled("B"));

        graph.Pulse("C");
        Assert.True(graph.IsEnabled("B"));
    }


    // A trigger that fires itself, each time stepping a 3-bit counter first, until the counter's overflow disables it:
    // it fires 8 times, each activation inside the one before.
    [Fact]
    public void ATriggerCanFireItself_UntilACounterDisablesIt()
    {
        var counter = new InstantToggleChain(3);
        var loop = new Relay();
        loop.Output.Connect(counter.Input);
        loop.Output.Connect(loop.Input);
        counter.Overflow.Connect(loop.Disable);
        var graph = TriggerGraph.FromTriggers(loop.Entities.Concat(counter.Entities).OfType<ActivateTrigger>());
        string id = TriggerGraph.IdFor(loop.Output.Single());

        graph.Pulse(id);

        Assert.Equal(8 * loop.Output.Single().Targets.Count, graph.ActionsBySource[id]);
        Assert.Contains(id, graph.Reentered);
        Assert.Empty(graph.Runaway);
        Assert.False(graph.IsEnabled(id));
    }

    // A trigger that fires itself with nothing to stop it is cut off at MaxDepth and reported
    [Fact]
    public void ALoopWithNoWayOut_IsCutOffAtMaxDepth()
    {
        var loop = new Relay();
        loop.Output.Connect(loop.Input);
        var graph = TriggerGraph.FromTriggers(loop.Entities.OfType<ActivateTrigger>());
        graph.MaxDepth = 1000;
        string id = TriggerGraph.IdFor(loop.Output.Single());

        graph.Pulse(id);

        Assert.Contains(id, graph.Runaway);
        Assert.Equal(1000, graph.ActionsBySource[id]);
    }
}
