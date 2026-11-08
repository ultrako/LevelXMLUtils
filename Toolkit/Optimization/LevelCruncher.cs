using System.Text.RegularExpressions;
using System.Xml.Linq;
using LevelXML;
namespace LevelXMLUtils;

public class LevelCruncher
{
    private sealed class Index
    {
        private readonly Dictionary<Entity, HashSet<ActivateTrigger>> inbound = new(ReferenceEqualityComparer.Instance);
        // Entities some trigger enables / activates, and triggers with any Enable or Disable action anywhere
        // in their targets. Only read for the state a pass starts with, before it changes anything.
        public readonly HashSet<Entity> EnabledByAnyone = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<Entity> ActivatedByAnyone = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<ActivateTrigger> HasEnableOrDisable = new(ReferenceEqualityComparer.Instance);
        public readonly List<ActivateTrigger> Triggers;
        // Triggers taken out of the level so far, in the order it happened, and as a set for lookups
        public readonly List<ActivateTrigger> Removed = new();
        private readonly HashSet<ActivateTrigger> removedSet = new(ReferenceEqualityComparer.Instance);

        public Index(IEnumerable<Entity> entities)
        {
            Triggers = entities.OfType<ActivateTrigger>().ToList();
            foreach (ActivateTrigger trigger in Triggers)
            {
                foreach (Target target in trigger.Targets)
                {
                    AddInbound(target.Targeted, trigger);
                    foreach (ITriggerAction action in target.Actions)
                    {
                        if (action is Enable) { EnabledByAnyone.Add(target.Targeted); }
                        if (action is Activate) { ActivatedByAnyone.Add(target.Targeted); }
                        if (action is Enable || action is Disable) { HasEnableOrDisable.Add(trigger); }
                    }
                }
            }
        }

        public void AddInbound(Entity entity, ActivateTrigger trigger)
        {
            if (!inbound.TryGetValue(entity, out HashSet<ActivateTrigger>? set))
            {
                inbound[entity] = set = new HashSet<ActivateTrigger>(ReferenceEqualityComparer.Instance);
            }
            set.Add(trigger);
        }

        public ActivateTrigger[] InboundOf(Entity entity) =>
            inbound.TryGetValue(entity, out HashSet<ActivateTrigger>? set) ? set.ToArray() : Array.Empty<ActivateTrigger>();

        public int InboundCount(Entity entity) => inbound.TryGetValue(entity, out HashSet<ActivateTrigger>? set) ? set.Count : 0;

        public bool AnyInboundIn(Entity entity, ISet<ActivateTrigger> triggers) =>
            inbound.TryGetValue(entity, out HashSet<ActivateTrigger>? set) && set.Any(triggers.Contains);

        // Takes a trigger out of the level: every target anywhere that pointed at it goes, and so does it
        public void Remove(ActivateTrigger toRemove)
        {
            foreach (ActivateTrigger other in InboundOf(toRemove))
            {
                foreach (Target target in other.Targets.Where(target => target.Targeted == toRemove).ToList())
                {
                    other.RemoveTarget(target);
                }
            }
            inbound.Remove(toRemove);
            foreach (Target target in toRemove.Targets)
            {
                if (inbound.TryGetValue(target.Targeted, out HashSet<ActivateTrigger>? set)) { set.Remove(toRemove); }
            }
            if (removedSet.Add(toRemove)) { Removed.Add(toRemove); }
        }

        public void ApplyRemovalsTo(IList<Entity> entities)
        {
            foreach (ActivateTrigger trigger in Removed) { entities.Remove(trigger); }
            Removed.Clear();
            removedSet.Clear();
        }
    }

    private static List<ActivateTrigger> findDisabledTriggersThatStayDisabled(Index index) =>
        index.Triggers.Where(trigger => (bool)trigger.StartDisabled && !index.EnabledByAnyone.Contains(trigger)).ToList();

    private static List<ActivateTrigger> findRelayTriggersThatAreNeverActivated(Index index) =>
        index.Triggers.Where(trigger => trigger.TriggeredBy == TriggeredBy.Triggers && !index.ActivatedByAnyone.Contains(trigger)).ToList();

    private static List<ActivateTrigger> findActivateTriggersThatHaveNoTargets(Index index) =>
        index.Triggers.Where(trigger => !trigger.Targets.Any()).ToList();

    // We want to simplify relays, as long as they're not N to N and as long as their inbound triggers
    // don't share targets with it
    // and as long as they aren't being used as conditional triggers;
    // make sure their only inputs are Activates, not Enables or Disables
    private static List<ActivateTrigger> findUnipolarSimpleRelayTriggers(Index index) =>
        index.Triggers.Where(trigger => isUnipolarSimpleRelayTrigger(trigger, index)).ToList();

    private static bool isUnipolarSimpleRelayTrigger(ActivateTrigger trigger, Index index)
    {
        // Triggers that can be activated by physics or the player should act as "sources" for our system
        if (trigger.StartDisabled) { return false; }
        if (trigger.TriggeredBy != TriggeredBy.Triggers) { return false; }
        if (trigger.RepeatType != RepeatType.Multiple) { return false; }
        if (trigger.Delay != 0) { return false; }
        ActivateTrigger[] inbounds = index.InboundOf(trigger);
        // If this trigger is enabled by other triggers or disabled by other triggers,
        // this might be being used as a conditional trigger, in which case we can't simplify it
        if (inbounds.Any(index.HasEnableOrDisable.Contains)) { return false; }
        if (inbounds.Length > 1 && trigger.Targets.Count > 1) // It's bad to simplify N to N relays
        {
            return false;
        }
        return true;
    }

    private static bool distributeOutboundConnections(ActivateTrigger trigger, Index index)
    {
        ActivateTrigger[] inbounds = index.InboundOf(trigger);
        IReadOnlyList<Target> targets = trigger.Targets;
        // No self looping triggers either
        if (targets.Any(target => target.Targeted == trigger)) { return false; }
        // If the inbounds share a target, that might be a case of this trigger being used to add a second action
        // to the same trigger, and so it should not be simplified away. Checked from whichever side is cheaper:
        // the inbounds' targets, or the inbounds of this trigger's targets.
        HashSet<ActivateTrigger> inboundSet = new(inbounds, ReferenceEqualityComparer.Instance);
        HashSet<Entity> outbounds = new(targets.Select(target => target.Targeted), ReferenceEqualityComparer.Instance);
        long costFromInbounds = inbounds.Sum(inbound => (long)inbound.Targets.Count);
        long costFromOutbounds = outbounds.Sum(outbound => (long)index.InboundCount(outbound));
        bool shared = costFromInbounds <= costFromOutbounds
            ? inbounds.Any(inbound => inbound.Targets.Any(target => outbounds.Contains(target.Targeted)))
            : outbounds.Any(outbound => index.AnyInboundIn(outbound, inboundSet));
        if (shared) { return false; }
        foreach (ActivateTrigger inbound in inbounds)
        {
            int position = 0;
            while (inbound.Targets[position].Targeted != trigger) { position++; }
            foreach (Target target in targets.Reverse())
            {
                inbound.InsertTarget(position, target);
                index.AddInbound(target.Targeted, inbound);
            }
        }
        index.Remove(trigger);
        return true;
    }

    public static void RemoveDisabledTriggersThatStayDisabled(IList<Entity> entities)
    {
        Index index = new(entities);
        foreach (ActivateTrigger trigger in findDisabledTriggersThatStayDisabled(index)) { index.Remove(trigger); }
        index.ApplyRemovalsTo(entities);
    }

    public static void SimplifyRelayTriggers(IList<Entity> entities)
    {
        Index index = new(entities);
        foreach (ActivateTrigger trigger in findUnipolarSimpleRelayTriggers(index)) { distributeOutboundConnections(trigger, index); }
        index.ApplyRemovalsTo(entities);
    }

    /// <summary>
    /// Shares out activation lists that overlap across triggers, by repeatedly extracting common subexpressions. Many
    /// sources activate largely the same targets (for example, a grid where each cell relay activates about 900 gates and
    /// neighbouring cells activate mostly the same ones). Each round finds pairs of nodes (a source, or a relay made by an
    /// earlier round) that both directly activate at least <paramref name="minSharedTargets"/> of the same targets, and for
    /// the pairs with the most in common (each node used once a round) makes one new relay: both nodes now activate the
    /// relay and the relay activates the shared targets. That is one arm fewer than it costs for every shared target beyond
    /// two, so <paramref name="minSharedTargets"/> of 3 or more is a saving. Relays can share out relays, so a chain of
    /// several relays can lie between a source and a target, all of it within the frame. Every target is still reached from
    /// every source exactly once (a target moves from the nodes to the relay), so nothing fires twice.
    /// On a large level of that kind it took one gate family from 503,000 arms (sharing only identical sets) to 60,000, and
    /// another from 127,000 to 56,000.
    ///
    /// The CALLER must guarantee that the order of each source's targets does not matter (they are independent), because
    /// moved targets now fire when a relay does. Only plain Activate targets that are triggers and are not themselves in
    /// <paramref name="sources"/> are moved; every other target (enable, disable, shapes, groups) stays where it is. Each
    /// trigger's targets end up sorted by their position in <paramref name="entities"/>, so runs of neighbouring ids repeat
    /// and compress well; the new relays are added to the end of it. Returns how many relays were added.
    /// </summary>
    public static int FactorCommonActivations(IList<Entity> entities, IEnumerable<ActivateTrigger> sources, int minSharedTargets = 3, bool exactSetsFirst = false)
    {
        List<ActivateTrigger> sourceList = sources.Distinct().ToList();
        HashSet<ActivateTrigger> sourceSet = new(sourceList, ReferenceEqualityComparer.Instance);
        bool IsPlainActivate(Target target) => target.Actions.Count == 1 && target.Actions[0] is Activate;
        int exactRelays = exactSetsFirst ? FactorIdenticalSourceSets(entities, sourceList, sourceSet, IsPlainActivate) : 0;

        // The targets that can be moved, numbered; a node's set of them is a bitset
        Dictionary<ActivateTrigger, int> targetIndex = new(ReferenceEqualityComparer.Instance);
        List<ActivateTrigger> targetList = new();
        foreach (ActivateTrigger source in sourceList)
        {
            foreach (Target target in source.Targets)
            {
                if (target.Targeted is ActivateTrigger targeted && IsPlainActivate(target) && !sourceSet.Contains(targeted) && !targetIndex.ContainsKey(targeted))
                {
                    targetIndex[targeted] = targetList.Count;
                    targetList.Add(targeted);
                }
            }
        }
        int words = (targetList.Count + 63) / 64;
        if (words == 0) { return exactRelays; }

        List<ulong[]> bits = new();         // node -> the targets it still activates directly
        List<List<int>> links = new();      // node -> the relay nodes it activates
        List<ActivateTrigger?> trigger = new(); // node -> its trigger (the source, or the relay made for it)
        List<Relay?> relays = new();
        foreach (ActivateTrigger source in sourceList)
        {
            ulong[] mine = new ulong[words];
            foreach (Target target in source.Targets)
            {
                if (target.Targeted is ActivateTrigger targeted && IsPlainActivate(target) && targetIndex.TryGetValue(targeted, out int at)) { mine[at >> 6] |= 1UL << (at & 63); }
            }
            bits.Add(mine); links.Add(new List<int>()); trigger.Add(source); relays.Add(null);
        }

        int Count(ulong[] set) { int total = 0; foreach (ulong word in set) { total += System.Numerics.BitOperations.PopCount(word); } return total; }
        while (true)
        {
            List<int> active = Enumerable.Range(0, bits.Count).Where(node => Count(bits[node]) >= minSharedTargets).ToList();
            List<(int shared, int a, int b)>[] found = new List<(int, int, int)>[active.Count];
            Parallel.For(0, active.Count, i =>
            {
                List<(int, int, int)> mine = new();
                ulong[] first = bits[active[i]];
                for (int j = i + 1; j < active.Count; j++)
                {
                    ulong[] second = bits[active[j]];
                    int shared = 0;
                    for (int w = 0; w < words; w++) { shared += System.Numerics.BitOperations.PopCount(first[w] & second[w]); }
                    if (shared >= minSharedTargets) { mine.Add((shared, active[i], active[j])); }
                }
                found[i] = mine;
            });
            List<(int shared, int a, int b)> pairs = found.SelectMany(list => list).OrderByDescending(p => p.shared).ThenBy(p => p.a).ThenBy(p => p.b).ToList();
            if (pairs.Count == 0) { break; }
            HashSet<int> used = new();
            foreach ((int _, int a, int b) in pairs)
            {
                if (used.Contains(a) || used.Contains(b)) { continue; }
                ulong[] common = new ulong[words];
                int shared = 0;
                for (int w = 0; w < words; w++) { common[w] = bits[a][w] & bits[b][w]; shared += System.Numerics.BitOperations.PopCount(common[w]); }
                if (shared < minSharedTargets) { continue; }
                for (int w = 0; w < words; w++) { bits[a][w] &= ~common[w]; bits[b][w] &= ~common[w]; }
                Relay relay = new(0, 0, 5, 5);
                int node = bits.Count;
                bits.Add(common); links.Add(new List<int>()); trigger.Add(relay.Output[0]); relays.Add(relay);
                links[a].Add(node); links[b].Add(node);
                used.Add(a); used.Add(b);
            }
        }

        // Write it back: the relays go at the end of the entity list, then every trigger's targets are its untouched
        // ones plus its remaining direct targets and its relay links, all in entity order
        Dictionary<Entity, int> position = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < entities.Count; i++) { position[entities[i]] = i; }
        int added = 0;
        for (int node = sourceList.Count; node < bits.Count; node++)
        {
            entities.Add(trigger[node]!);
            position[trigger[node]!] = entities.Count - 1;
            added++;
        }
        int PositionOf(Target target) => position.TryGetValue(target.Targeted, out int at) ? at : int.MaxValue;
        for (int node = 0; node < bits.Count; node++)
        {
            ActivateTrigger owner = trigger[node]!;
            List<Target> arms = new();
            if (node < sourceList.Count)
            {
                arms.AddRange(owner.Targets.Where(target => !(target.Targeted is ActivateTrigger targeted && IsPlainActivate(target) && targetIndex.ContainsKey(targeted))));
            }
            for (int at = 0; at < targetList.Count; at++)
            {
                if ((bits[node][at >> 6] >> (at & 63) & 1) != 0) { arms.Add(new Input(new Output(new[] { targetList[at] }))[0]); }
            }
            foreach (int link in links[node]) { arms.Add(new Input(new Output(new[] { trigger[link]! }))[0]); }
            owner.ClearTargets();
            foreach (Target target in arms.OrderBy(PositionOf)) { owner.AddTarget(target); }
        }
        return added + exactRelays;
    }

    /// <summary>
    /// Groups the targets that exactly the same sources activate (two or more of each), and puts each group behind one relay when that saves arms
    /// (sources x targets arms become sources + targets). One relay serves the whole group however many sources share it, where the pairwise sharing
    /// in <see cref="FactorCommonActivations"/> needs about one relay per source for a set many sources share, so this is the cheap pass in triggers
    /// and the pairwise one mops up the rest. Each source's arm to the relay goes where its first moved target was; the relay's targets keep their
    /// order. Returns how many relays were added (appended to <paramref name="entities"/>).
    /// </summary>
    static int FactorIdenticalSourceSets(IList<Entity> entities, List<ActivateTrigger> sourceList, HashSet<ActivateTrigger> sourceSet, Func<Target, bool> isPlainActivate)
    {
        Dictionary<ActivateTrigger, List<int>> sourcesOf = new(ReferenceEqualityComparer.Instance);
        List<ActivateTrigger> order = new();
        for (int i = 0; i < sourceList.Count; i++)
        {
            foreach (Target target in sourceList[i].Targets)
            {
                if (target.Targeted is ActivateTrigger targeted && isPlainActivate(target) && !sourceSet.Contains(targeted))
                {
                    if (!sourcesOf.TryGetValue(targeted, out List<int>? list)) { sourcesOf[targeted] = list = new(); order.Add(targeted); }
                    if (list.Count == 0 || list[^1] != i) { list.Add(i); }
                }
            }
        }
        Dictionary<ActivateTrigger, ActivateTrigger> relayOf = new(ReferenceEqualityComparer.Instance);
        int added = 0;
        foreach (IGrouping<string, ActivateTrigger> group in order.GroupBy(target => string.Join(",", sourcesOf[target])))
        {
            int sources = sourcesOf[group.First()].Count, targets = group.Count();
            if (sources < 2 || targets < 2 || sources * targets <= sources + targets) { continue; }
            Relay relay = new(0, 0, 5, 5);
            ActivateTrigger relayTrigger = relay.Output[0];
            foreach (ActivateTrigger target in group)
            {
                relayOf[target] = relayTrigger;
                relayTrigger.AddTarget(new Input(new Output(new[] { target }))[0]);
            }
            entities.Add(relayTrigger);
            added++;
        }
        if (added == 0) { return 0; }
        foreach (ActivateTrigger source in sourceList)
        {
            List<Target> arms = new();
            HashSet<ActivateTrigger> linked = new(ReferenceEqualityComparer.Instance);
            foreach (Target target in source.Targets)
            {
                if (target.Targeted is ActivateTrigger targeted && isPlainActivate(target) && relayOf.TryGetValue(targeted, out ActivateTrigger? relayTrigger))
                {
                    if (linked.Add(relayTrigger)) { arms.Add(new Input(new Output(new[] { relayTrigger }))[0]); }
                    continue;
                }
                arms.Add(target);
            }
            source.ClearTargets();
            foreach (Target arm in arms) { source.AddTarget(arm); }
        }
        return added;
    }

    // While the previous method pushes back any relay triggers that do nothing,
    // we still need to snip off any useless chains at the start
    public static void RemoveRelayTriggersThatAreNeverActivated(IList<Entity> entities)
    {
        Index index = new(entities);
        foreach (ActivateTrigger trigger in findRelayTriggersThatAreNeverActivated(index)) { index.Remove(trigger); }
        index.ApplyRemovalsTo(entities);
    }

    /// <summary>
    /// Removes any disabled triggers that stay disabled,
    /// and simplifies any relay triggers that are not n to n,
    /// by pushing them back into their inbound triggers
    /// </summary>
    public static void OptimizeTriggers(IList<Entity> entities) => OptimizeTriggers(entities, null);

    /// <param name="keep">relays not to fold into their inbounds (for example relays that each change a shape's opacity, kept because an
    /// arm to a trigger is shorter in the level than an arm with an opacity change)</param>
    public static void OptimizeTriggers(IList<Entity> entities, IEnumerable<ActivateTrigger>? keep)
    {
        HashSet<ActivateTrigger> kept = new(keep ?? Enumerable.Empty<ActivateTrigger>(), ReferenceEqualityComparer.Instance);
        while (true)
        {
            Index index = new(entities);
            IList<ActivateTrigger> toRemove =
                findDisabledTriggersThatStayDisabled(index)
                .Union(findRelayTriggersThatAreNeverActivated(index))
                .Union(findActivateTriggersThatHaveNoTargets(index))
                .ToList();
            IList<ActivateTrigger> simpleRelays = findUnipolarSimpleRelayTriggers(index).Where(t => !kept.Contains(t)).ToList();
            if (!toRemove.Any() && !simpleRelays.Any()) { return; }
            bool removedAny = toRemove.Any();
            foreach (ActivateTrigger trigger in simpleRelays)
            {
                if (distributeOutboundConnections(trigger, index))
                {
                    removedAny = true;
                }
            }
            foreach (ActivateTrigger trigger in toRemove) { index.Remove(trigger); }
            index.ApplyRemovalsTo(entities);
            if (!removedAny) { return; }
        }
    }

    // LevelXML isn't made for minimising character count: it is meant to do exactly what Happy Wheels does
    // when you paste a level into the import box and then copy from the
    // save level box; this means that empty parameters that get default
    // values will always be expanded by my library.
    // Also, it pretty-prints the xml, when newlines aren't really needed.
    // roundDecimals: when set, every number in the level with more decimals than that is rounded to that many (trailing zeros
    // dropped). Positions and vertices computed in floating point come out like 59.399999999999636, which costs 17 characters
    // each and deflate can't make anything of the noise; rounded to 3 decimals (0.0005 of a unit) a large art-heavy level was about 2%
    // smaller. Opt-in, because a physics level is not necessarily as forgiving of its shapes moving.
    // A plain decimal number with more than `decimals` decimals, rounded; anything else is returned as it was.
    public static string RoundNumber(string text, int decimals)
    {
        int point = text.IndexOf('.');
        if (point < 0 || text.Length - point - 1 <= decimals) { return text; }
        if (!text.All(character => char.IsDigit(character) || character == '.' || character == '-')) { return text; }
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)) { return text; }
        string rounded = Math.Round(value, decimals).ToString("0." + new string('#', decimals), System.Globalization.CultureInfo.InvariantCulture);
        return rounded == "-0" ? "0" : rounded;
    }

    /// <summary>
    /// Writes each distinct art shape outline once (art shapes only: only art shapes were seen to be fitted to their box). A shape's vertex list
    /// (<c>&lt;v id=".." n=".." v0=".." .../&gt;</c>) has an id, and a later shape whose <c>&lt;v&gt;</c> has only that id (no vertices) is drawn with
    /// the same list; the game fits the vertices to each shape's own width and height (p2, p3). So copies of a picture at different sizes, whose vertices differ only by a scale, can share
    /// one list once the vertices are written at a common scale: each shape's vertices are divided by its own width and height, which keeps
    /// them centred where they were, identical outlines (compared at <paramref name="scale"/>) get one id, and each list is written at a span
    /// its shapes can all be drawn at (see below). Every shape's box is untouched. A level with many copies of a few sprites at different
    /// distances had 1,216 art shapes but only 25 outlines up to scale. The game reads the lists in document order, keeping them by id (polygons and
    /// art shapes share one id table), so a list is written at the first shape in the file that uses it.
    /// </summary>
    public static void ShareArtVertices(XElement level, double scale = 100)
    {
        // each type is shared on its own (a polygon's outline never stands in for an art shape's), but the ids must not repeat across the two: polygons
        // and art shapes share one numbering, and a polygon numbered like an art shape was drawn with the art shape's vertices (seen in the game: a bike
        // frame came out as a blob, until the polygons were numbered after the art shapes)
        int next = ShareVertices(level, "4", scale, 0);
        ShareVertices(level, "3", scale, next);
    }

    /// <summary>
    /// Turns every art shape whose edges are all straight (no vertex has curve handles: pixel art, flat rectangles) into a polygon, non-interactive like
    /// the art shape was. The game fits both to their box the same way, but an art shape is drawn with curved edges even where its vertices have no
    /// handles, and the first time each size or angle of a curved shape is drawn is slow: measured in headless Chromium at about 50 ms per object, a
    /// visible stutter. A polygon's edges are straight lines.
    /// </summary>
    public static void StraightArtToPolygons(XElement level)
    {
        foreach (XElement shape in level.Descendants("sh").Where(element => element.Attribute("t")?.Value == "4" && element.Element("v") is XElement v && v.Attribute("n") is not null))
        {
            XElement v = shape.Element("v")!;
            if (v.Attribute("f")?.Value == "f") { continue; } // an open line: a polygon is always closed
            int count = int.Parse(v.Attribute("n")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Enumerable.Range(0, count).All(i => v.Attribute("v" + i)!.Value.Split('_').Length == 2)) { shape.SetAttributeValue("t", "3"); }
        }
    }

    static int ShareVertices(XElement level, string type, double scale, int firstId)
    {
        // First the outlines up to scale (each shape's vertices over its own box), grouped by identical text at the nominal scale
        var groups = new Dictionary<string, List<(XElement vertices, string fill, List<double[]> points, double width, double height)>>();
        var documentOrder = new Dictionary<XElement, int>(ReferenceEqualityComparer.Instance);
        var order = new List<string>();
        foreach (XElement shape in level.Descendants("sh").Where(element => element.Attribute("t")?.Value == type && element.Element("v") is not null).ToList())
        {
            XElement vertices = shape.Element("v")!;
            documentOrder[vertices] = documentOrder.Count;
            if (!double.TryParse(shape.Attribute("p2")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double width) || width <= 0
                || !double.TryParse(shape.Attribute("p3")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double height) || height <= 0
                || vertices.Attribute("n") is null)
            {
                continue; // no box to scale by, or already a reference: leave it as it is
            }
            int count = int.Parse(vertices.Attribute("n")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            List<double[]> points = new();
            for (int i = 0; i < count; i++)
            {
                // a vertex is "x_y", or for a curve "x_y_handle1x_handle1y_handle2x_handle2y": even parts scale with the width, odd with the height
                string[] parts = vertices.Attribute("v" + i)!.Value.Split('_');
                points.Add(parts.Select((part, k) => double.Parse(part, System.Globalization.CultureInfo.InvariantCulture) / (k % 2 == 0 ? width : height)).ToArray());
            }
            string fill = vertices.Attribute("f")?.Value ?? "";
            string key = fill + "|" + string.Join(" ", points.Select(p => string.Join("_", p.Select(v => Math.Round(v * scale, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))));
            if (!groups.TryGetValue(key, out var group)) { groups[key] = group = new(); order.Add(key); }
            group.Add((vertices, fill, points, width, height));
        }
        // Copies of one outline at very different sizes don't come out identical: the level file keeps three decimals of every vertex, which on a tiny copy is a
        // large share of its box (on one level, small copies of a character each wrote their outline again, about 17 KB). So outlines with the same
        // fill and vertex count whose vertices all agree to within half a percent of the box are merged, keeping the most precise (the largest) as the one
        // written.
        const double Tolerance = 0.005;
        var representative = order.ToDictionary(k => k, k => groups[k].OrderByDescending(s => s.width * s.height).First().points);
        var merged = new List<string>();
        foreach (string key in order)
        {
            var mine = representative[key];
            string? into = merged.FirstOrDefault(other => groups[other][0].fill == groups[key][0].fill && representative[other].Count == mine.Count
                && representative[other].Zip(mine).All(pair => pair.First.Length == pair.Second.Length && pair.First.Zip(pair.Second).All(v => Math.Abs(v.First - v.Second) <= Tolerance)));
            if (into is null) { merged.Add(key); continue; }
            groups[into].AddRange(groups[key]);
            if (groups[key].Max(s => s.width * s.height) > groups[into].Where(s => !groups[key].Contains(s)).Max(s => s.width * s.height)) { representative[into] = mine; }
        }
        order = merged;
        // Then each outline at spans its shapes can all be drawn at. The game scales a list to the shape's width and height by its span in x and in y
        // (measured from 0, so from the centre out for a centred list), but only between 0.1 and 10 times that span: at the nominal span of 100 a shape
        // can only be 10 to 1,000 units each way (seen in the game: a small detail of a small picture came out 10 units wide). So x and y each get a span their shapes' widths (or heights) fit: the nominal one when it fits (nearly every outline:
        // their text, and how well it compresses, is unchanged), else the round number (1, 2 or 5 times a power of ten) nearest the middle that fits
        // (an outline written at a span like 77.942 compressed far worse), else the middle. An outline whose widths or heights are more than 100 times
        // apart is split into groups that aren't.
        // A span for one axis of a list whose shapes run from least to most units: at least most / 10 (or the largest shape is drawn too small, which shows),
        // and at most 10 times (least + Enlarged) (nothing is drawn under a tenth of the span, and up to Enlarged units too big, under a screen pixel, is invisible).
        // The nominal span when it is allowed (nearly every outline: their text, and how well it compresses, is unchanged), else the round number (1, 2 or 5
        // times a power of ten) nearest it that is (a span like 77.942 compressed far worse), else the smallest allowed.
        const double Enlarged = 0.25;
        double SpanFor(double least, double most)
        {
            double low = most / 10, high = 10 * (least + Enlarged);
            bool Allowed(double candidate) => candidate >= low * (1 - 1e-9) && candidate <= high * (1 + 1e-9);
            return Allowed(scale) ? scale
                : Enumerable.Range(-6, 16).SelectMany(e => new[] { 1.0, 2, 5 }.Select(m => m * Math.Pow(10, e))).Where(Allowed).OrderBy(c => Math.Abs(Math.Log(c / scale))).Cast<double?>().FirstOrDefault() ?? low;
        }
        int nextId = firstId;
        foreach (string key in order)
        {
            // From the largest down, a shape joins the list while a span is still allowed for every shape in it (see SpanFor): a shape far smaller than the
            // largest would be drawn more than Enlarged units too big, so it starts a list of its own (small copies came out as blobs at the nominal
            // span). Splitting every outline whose shapes were more than 100 times apart instead cost one level 14 KB, for gains of under a unit.
            var shapes = groups[key].OrderByDescending(s => Math.Max(s.width, s.height)).ToList();
            while (shapes.Count > 0)
            {
                double mostX = shapes.Max(t => t.width), mostY = shapes.Max(t => t.height);
                var mine = shapes.TakeWhile(t => mostX / 10 <= 10 * (t.width + Enlarged) && mostY / 10 <= 10 * (t.height + Enlarged)).ToList();
                if (mine.Count == 0) { mine = shapes.Take(1).ToList(); }
                shapes = shapes.Skip(mine.Count).ToList();
                double spanX = SpanFor(mine.Min(t => t.width), mostX), spanY = SpanFor(mine.Min(t => t.height), mostY);
                string id = (nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool first = true;
                // the most precise outline of the group is the one written, at the group's first shape in the file (a list must come before the shapes that refer to it, so
                // a reference before its list would find nothing), and the others are references
                var best = representative[key];
                foreach (var (vertices, fill, _, _, _) in mine.OrderBy(entry => documentOrder[entry.vertices]))
                {
                    if (!first) { vertices.ReplaceWith(new XElement("v", new XAttribute("f", fill), new XAttribute("id", id))); continue; }
                    first = false;
                    var points = best;
                    XElement written = new("v", new XAttribute("f", fill), new XAttribute("id", id));
                    for (int i = 0; i < points.Count; i++)
                    {
                        written.Add(new XAttribute("v" + i, string.Join("_", points[i].Select((v, k) => Math.Round(v * (k % 2 == 0 ? spanX : spanY), 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))));
                    }
                    written.Add(new XAttribute("n", points.Count));
                    vertices.ReplaceWith(written);
                }
            }
        }
        return nextId;
    }

    /// <summary>
    /// Renumbers the triggers so the most targeted get the smallest numbers (a trigger target is written as the
    /// trigger's number, so its digits are paid on every arm): on one level with ~2,500 heavily used triggers at the end of
    /// a 14,000-trigger list, this saved 23.7 KB of the compressed level. The triggers' arms keep their
    /// order, which is the order they run in; triggers nothing targets keep their order among themselves, after the rest.
    /// Run it last, after anything else that rewrites the trigger list.
    /// </summary>
    public static string OrderTriggersByUse(string levelXML)
    {
        XElement level = XElement.Parse(levelXML);
        XElement? list = level.Element("triggers");
        if (list is null) { return levelXML; }
        List<XElement> triggers = list.Elements().ToList();
        List<XAttribute> references = triggers.SelectMany(trigger => trigger.Elements("t")).Select(target => target.Attribute("i")!).ToList();
        int[] uses = new int[triggers.Count];
        foreach (XAttribute reference in references) { uses[int.Parse(reference.Value)]++; }
        int[] order = Enumerable.Range(0, triggers.Count).OrderByDescending(index => uses[index]).ToArray(); // stable
        int[] number = new int[triggers.Count];
        for (int position = 0; position < order.Length; position++) { number[order[position]] = position; }
        foreach (XAttribute reference in references) { reference.Value = number[int.Parse(reference.Value)].ToString(); }
        list.ReplaceNodes(order.Select(index => triggers[index]));
        string xml = level.ToString(SaveOptions.DisableFormatting);
        return Regex.Replace(Regex.Replace(xml, "\" >", "\">"), "\" />", "\"/>");
    }

    /// <summary>
    /// Writes the level XML with what the game doesn't need left out: default fields, the activate action on trigger
    /// targets, positions of triggers that have no sensor, and optionally rounded numbers, shared art outlines and
    /// straight-edged art as polygons (the game cuts every art-shape edge into curve pieces when it first draws it, so a
    /// polygon is cheaper for the same straight edges). Which fields are safe to leave out, and which aren't, is in README.md in this folder.
    /// </summary>
    public static string MinifyRelayTriggers(string levelXML, int? roundDecimals = null, bool shareArtVertices = false, bool straightArtToPolygons = false, int? pictureDecimals = null)
    {
        XElement level = XElement.Parse(levelXML);
        if (straightArtToPolygons) { StraightArtToPolygons(level); }
        // The positions and sizes of polygons and art shapes (the pictures) to fewer decimals: a tenth of a unit is under a tenth of a screen pixel, and the
        // sprites' many copies then repeat more of their numbers (about 4 KB on one art-heavy level). Applied before the vertices are shared, which scale by the box.
        if (pictureDecimals is int pictureDigits)
        {
            foreach (XElement shape in level.Descendants("sh").Where(element => element.Attribute("t")?.Value is "3" or "4"))
            {
                foreach (string name in new[] { "p0", "p1" }) { if (shape.Attribute(name) is XAttribute attribute) { attribute.Value = RoundNumber(attribute.Value, pictureDigits); } }
            }
        }
        if (shareArtVertices) { ShareArtVertices(level); }
        // I need to use a stringwriter to specify no indentation
        XElement triggers = level.Element("triggers") ?? new XElement("triggers");
        foreach (XElement trigger in triggers.Elements())
        {
            if ((trigger.Attribute("b") ?? new XAttribute("b", "1")).Value == "5")
            {
                // a local sound trigger (t="2", l other than 1) plays at its position, so it keeps x and y: without them the game logs an error every
                // frame while it plays
                bool localSound = trigger.Attribute("t")?.Value == "2" && (trigger.Attribute("l")?.Value ?? "1") != "1";
                string[] attributesToRemove = localSound ? new[] { "w", "h", "a" } : new[]{"x", "y", "w", "h", "a"};
                foreach(string attributeToRemove in attributesToRemove)
                {
                    if (trigger.Attribute(attributeToRemove) is XAttribute attr)
                    {
                        attr.Remove();
                    }
                }
                if(trigger.Attribute("sd") is XAttribute startDisabledAttr)
                {
                    if (startDisabledAttr.Value == "f")
                    {
                        startDisabledAttr.Remove();
                    }
                }
                foreach (XElement target in trigger.Elements())
                {
                    if (target.Name == "sh")
                    {
                        foreach (XElement action in target.Elements())
                        {
                            if ((action.Attribute("i") ?? new XAttribute("i", "1")).Value == "3")
                            {
                                if (action.Attribute("p0") is XAttribute opacityAttr)
                                {
                                    if (opacityAttr.Value == "100")
                                    {
                                        opacityAttr.Remove();
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        // A trigger target whose only action is "activate trigger" (a lone <a i="0"/>) can leave the action out: the game
        // reads a trigger target with no <a> as Activate. Checked in the game by loading the same level both ways (a
        // start trigger, a relay and a panel that lights up when the relay fires): the panel lit with and without the
        // <a>, and stayed dark when the target was removed altogether. Only trigger targets (<t i=..>) are collapsed;
        // shape and group targets need their action, and disable (i="1") and enable (i="2") are kept.
        foreach (XElement trigger in triggers.Elements())
        {
            foreach (XElement target in trigger.Elements().Where(element => element.Name == "t"))
            {
                XElement[] actions = target.Elements().ToArray();
                if (actions.Length == 1
                    && actions[0].Name == "a"
                    && actions[0].Attributes().Count() == 1
                    && actions[0].Attribute("i")?.Value == "0"
                    && target.Attributes().All(attribute => attribute.Name == "i"))
                {
                    target.RemoveNodes();
                }
            }
        }
        // Same default for a group target's "change opacity" (action index 1 for groups): a missing p0 means 100, checked
        // in the game (the group lit up with p0 dropped, and a reset with p0="0" still needs it). p1, the duration, must
        // stay even when it is 0: leaving it out gives a slow default fade, not an instant change.
        foreach (XElement trigger in triggers.Elements())
        {
            foreach (XElement target in trigger.Elements().Where(element => element.Name == "g"))
            {
                foreach (XElement action in target.Elements().Where(element => element.Attribute("i")?.Value == "1"))
                {
                    if (action.Attribute("p0")?.Value == "100") { action.Attribute("p0")!.Remove(); }
                }
            }
        }
        XElement shapes = level.Element("shapes") ?? new XElement("shapes");
        foreach (XElement shape in shapes.Elements())
        {
            if (shape.Attribute("p4") is XAttribute rotationAttr)
            {
                if (rotationAttr.Value == "0")
                {
                    rotationAttr.Remove();
                }
            }
            if ((shape.Attribute("i") ?? new XAttribute("i", "t")).Value == "f")
            {
                if (shape.Attribute("p5") is XAttribute fixedAttr)
                {
                    fixedAttr.Remove();
                }
                if (shape.Attribute("p6") is XAttribute sleepingAttr)
                {
                    sleepingAttr.Remove();
                }
                if (shape.Attribute("p7") is XAttribute densityAttr)
                {
                    densityAttr.Remove();
                }
                if (shape.Attribute("p11") is XAttribute collisionTypeAttr)
                {
                    collisionTypeAttr.Remove();
                }
            }
            if (shape.Attribute("p9") is XAttribute outlineAttr)
            {
                if (outlineAttr.Value == "-1")
                {
                    outlineAttr.Remove();
                }
            }
        }
        if (roundDecimals is int decimals)
        {
            foreach (XAttribute attribute in level.Descendants().SelectMany(element => element.Attributes()))
            {
                // vertices are lists of numbers joined by underscores ("x_y_handle-x_..."), everything else a single value
                attribute.Value = string.Join("_", attribute.Value.Split('_').Select(part => RoundNumber(part, decimals)));
            }
        }
        string crunchedXML = level.ToString(SaveOptions.DisableFormatting);
        // Replace '" >' with '">' and '" />' with '"/>'
        // This is because even with disabled formatting,
        // there's still a space between the last attribute and the > bracket.
        return Regex.Replace(Regex.Replace(crunchedXML, "\" >", "\">"), "\" />", "\"/>");
    }
}
