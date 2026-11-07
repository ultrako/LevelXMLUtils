using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using LevelXML;

namespace LevelXMLUtils;

public enum TriggerActionKind
{
    Activate,
    Enable,
    Disable,
}

public sealed class TriggerEdge
{
    public required string TargetId { get; init; }
    public TriggerActionKind Action { get; init; }
}

public sealed class TriggerNode
{
    public required string Id { get; init; }
    public bool Enabled { get; set; } = true;
    /// <summary>The trigger's delay in seconds: when the graph honours delays, its actions run after the rest of the Pulse.</summary>
    public double Delay { get; init; }
    public List<TriggerEdge> Outgoing { get; } = new();
}

public sealed class TriggerGraph
{
    private static readonly ConditionalWeakTable<ActivateTrigger, TriggerIdToken> _ids = new();
    private static int _nextId;

    private sealed class TriggerIdToken
    {
        public required string Id { get; init; }
    }

    private sealed class TriggerReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private readonly Dictionary<string, TriggerNode> _nodes;
    private HashSet<string> _lastFired = new();

    public TriggerGraph(IEnumerable<TriggerNode> nodes)
    {
        _nodes = nodes.ToDictionary(node => node.Id);
    }

    public IReadOnlyCollection<string> NodeIds => _nodes.Keys;
    public IReadOnlyCollection<string> LastFired => _lastFired;

    /// <summary>
    /// Trigger ids that were activated again while an earlier activation of theirs was still running: a loop, directly or
    /// through other triggers. Allowed, as in the game (a trigger that fires itself until a counter disables it is a
    /// normal device); this is only for looking at what a pulse did. Cleared by every Pulse.
    /// </summary>
    public IReadOnlyCollection<string> Reentered => _reentered;
    private HashSet<string> _reentered = new();

    /// <summary>
    /// Trigger ids whose activation was dropped because the activations already running were <see cref="MaxDepth"/>
    /// deep. In the game that depth overflows the browser's call stack, which aborts the frame's trigger work; the
    /// trigger that started it is still queued, so it runs again from its first target the next frame. A device that
    /// needs it is broken in the game even if it finishes in the end. Empty for a working device. Cleared by every Pulse.
    /// </summary>
    public IReadOnlyCollection<string> Runaway => _runaway;
    private HashSet<string> _runaway = new();

    /// <summary>
    /// The most activations that can be running inside one another (a trigger activating a trigger that activates a
    /// trigger...) before more are dropped and reported in <see cref="Runaway"/>. The default is a little under what
    /// Chrome managed: a trigger looping through itself ran 6,268 activations deep in this count, and 6,518 overflowed
    /// (measured 2026-11-07). The limit is the browser's stack size, so it differs between browsers and machines.
    /// </summary>
    public int MaxDepth { get; set; } = 6_000;

    /// <summary>How many target actions (activations, enables and disables) the last Pulse carried out: a rough measure of the work the game does for it.</summary>
    public int ActionsCarriedOut { get; private set; }
    // How many of the last pulse's actions each trigger carried out (for finding what a press spends its work on)
    public Dictionary<string, int> ActionsBySource { get; } = new();
    // How many of the last pulse's actions were of each kind, split by the state of the trigger they acted on. Measured in the game, they cost
    // about the same (about 0.2 microseconds each, disabling an enabled trigger about 0.28); an opacity change costs about 25 times as much.
    public Dictionary<string, int> ActionsByKind { get; } = new();

    /// <summary>
    /// When true, a trigger with a delay does not run its actions where it is activated but after everything else in the Pulse has happened, the way the game runs
    /// them a frame or more later, if it is still enabled then (a disabled trigger cancels its pending delayed action). Off by default: a graph without it treats
    /// every delay as zero, which is what the tests written before delays mattered expect.
    /// </summary>
    public bool HonourDelays { get; set; }
    private readonly List<string> _deferred = new();

    public bool IsEnabled(string nodeId)
    {
        return _nodes.TryGetValue(nodeId, out var node) && node.Enabled;
    }

    /// <summary>Raised at the end of every Pulse with what it fired (for readers that follow state across pulses, like a test's screen reader).</summary>
    public event Action<IReadOnlyCollection<string>>? Pulsed;

    public void Pulse(params string[] sourceIds)
    {
        _lastFired = new HashSet<string>();
        _reentered = new HashSet<string>();
        _runaway = new HashSet<string>();
        ActionsCarriedOut = 0;
        ActionsBySource.Clear();
        ActionsByKind.Clear();
        _deferred.Clear();
        foreach (var sourceId in sourceIds)
        {
            Fire(sourceId);
        }
        // Delayed triggers run once the frame's own work is done; what they trigger may be delayed again, so go round until nothing is left
        for (int round = 0; _deferred.Count > 0 && round < 1000; round++)
        {
            var batch = _deferred.ToList();
            _deferred.Clear();
            foreach (var id in batch) { Fire(id, afterDelay: true); }
        }
        Pulsed?.Invoke(_lastFired);
    }

    // Fires a trigger and, depth first, everything it activates, the way the game runs a trigger's targets in order and an activated
    // trigger's targets before the next one. An explicit stack instead of recursion, so a long loop can't overflow the call stack.
    private void Fire(string startId, bool afterDelay = false)
    {
        var stack = new Stack<(TriggerNode node, int next)>();
        var running = new Dictionary<string, int>();

        void Begin(string id, bool delayed)
        {
            if (!_nodes.TryGetValue(id, out var node) || !node.Enabled) { return; }
            if (HonourDelays && !delayed && node.Delay > 0) { _deferred.Add(id); return; }
            if (stack.Count >= MaxDepth) { _runaway.Add(id); return; }
            if (running.GetValueOrDefault(id) > 0) { _reentered.Add(id); }
            running[id] = running.GetValueOrDefault(id) + 1;
            _lastFired.Add(id);
            stack.Push((node, 0));
        }

        Begin(startId, afterDelay);
        while (stack.Count > 0)
        {
            var (node, next) = stack.Pop();
            if (next == node.Outgoing.Count)
            {
                running[node.Id]--;
                continue;
            }
            stack.Push((node, next + 1));
            var edge = node.Outgoing[next];
            if (!_nodes.TryGetValue(edge.TargetId, out var target))
            {
                continue;
            }

            ActionsCarriedOut++;
            ActionsBySource[node.Id] = ActionsBySource.GetValueOrDefault(node.Id) + 1;
            string kind = edge.Action switch
            {
                TriggerActionKind.Activate => target.Enabled ? "activate enabled" : "activate disabled",
                TriggerActionKind.Enable => target.Enabled ? "enable enabled" : "enable disabled",
                _ => target.Enabled ? "disable enabled" : "disable disabled",
            };
            ActionsByKind[kind] = ActionsByKind.GetValueOrDefault(kind) + 1;
            switch (edge.Action)
            {
                case TriggerActionKind.Activate:
                    Begin(edge.TargetId, false);
                    break;
                case TriggerActionKind.Enable:
                    target.Enabled = true;
                    break;
                case TriggerActionKind.Disable:
                    target.Enabled = false;
                    break;
            }
        }
    }

    public static string IdFor(ActivateTrigger trigger)
    {
        var token = _ids.GetValue(trigger, _ => new TriggerIdToken
        {
            Id = $"trigger_{Interlocked.Increment(ref _nextId)}",
        });
        return token.Id;
    }

    public static TriggerGraph FromTriggers(IEnumerable<ActivateTrigger> triggers, bool honourDelays = false)
    {
        var triggerList = triggers
            .Distinct(new TriggerReferenceComparer<ActivateTrigger>())
            .ToList();
        var ids = new Dictionary<ActivateTrigger, string>(new TriggerReferenceComparer<ActivateTrigger>());
        var nodes = new List<TriggerNode>();

        foreach (var trigger in triggerList)
        {
            var id = IdFor(trigger);
            ids[trigger] = id;
            nodes.Add(new TriggerNode
            {
                Id = id,
                Enabled = trigger.StartDisabled == false,
                Delay = trigger.Delay,
            });
        }

        var nodesById = nodes.ToDictionary(node => node.Id);
        foreach (var trigger in triggerList)
        {
            var sourceId = ids[trigger];
            var sourceNode = nodesById[sourceId];

            foreach (var target in trigger.Targets)
            {
                if (target.Targeted is not ActivateTrigger targetTrigger)
                {
                    continue;
                }

                if (!ids.TryGetValue(targetTrigger, out var targetId))
                {
                    continue;
                }

                var action = target.Actions.FirstOrDefault();
                TriggerActionKind kind = action switch
                {
                    Activate => TriggerActionKind.Activate,
                    Enable => TriggerActionKind.Enable,
                    Disable => TriggerActionKind.Disable,
                    _ => TriggerActionKind.Activate,
                };

                sourceNode.Outgoing.Add(new TriggerEdge
                {
                    TargetId = targetId,
                    Action = kind,
                });
            }
        }

        return new TriggerGraph(nodes) { HonourDelays = honourDelays };
    }
}
