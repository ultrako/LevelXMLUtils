using LevelXMLUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LevelXML;
// The original, slow LevelCruncher (each lookup scans every trigger), kept only so a test can check the fast one
// gives exactly the same result. Do not use it in levels.
public class SlowLevelCruncherReference
{
    private static IEnumerable<ActivateTrigger> findDisabledTriggersThatStayDisabled(IEnumerable<Entity> entities)
    {
        return entities
        .OfType<ActivateTrigger>()
        .Where(trigger => (bool)trigger.StartDisabled &&
            !findInboundTriggers(trigger, entities)
                .Any(inbound => inbound.Targets.Any(target => 
                    target.Targeted == trigger && 
                    target.Actions.Any(action => action is Enable))));
    }

    private static void removeEntityAndItsInboundTriggerTargets(Entity toRemove, IList<Entity> entities)
    {
        entities
        .OfType<ActivateTrigger>()
        .ToList()
        .ForEach(otherTrigger =>
        {
            var targetsToRemove = otherTrigger.Targets
                .Where(target => target.Targeted == toRemove)
                .ToList();

            targetsToRemove.ForEach(target => otherTrigger.RemoveTarget(target));
        });

        entities.Remove(toRemove);
    }

    private static void removeEntitiesAndTheirInboundTriggerTargets(IEnumerable<Entity> toRemove, IList<Entity> entities)
    {
        foreach (Entity entity in toRemove)
        {
            removeEntityAndItsInboundTriggerTargets(entity, entities);
        }
    }
    // We want to simplify relays, as long as they're not N to N and as long as their inbound triggers
    // don't share targets with it
    // and as long as they aren't being used as conditional triggers;
    // make sure their only inputs are Activates, not Enables or Disables
    private static IEnumerable<ActivateTrigger> findUnipolarSimpleRelayTriggers(IEnumerable<Entity> entities)
    {
        return entities
            .OfType<ActivateTrigger>()
            .Where(trigger => isUnipolarSimpleRelayTrigger(trigger, entities))
            ;
    }
    private static bool isUnipolarSimpleRelayTrigger(ActivateTrigger trigger, IEnumerable<Entity> entities)
    {
        IEnumerable<ActivateTrigger> inbounds = findInboundTriggers(trigger, entities);
        IEnumerable<Entity> outbounds = trigger.Targets.Select(target => target.Targeted);
        // Triggers that can be activated by physics or the player should act as "sources" for our system
        if (trigger.StartDisabled) { return false; }
        if (trigger.TriggeredBy != TriggeredBy.Triggers) { return false; }
        if (trigger.RepeatType != RepeatType.Multiple) { return false; }
        if (trigger.Delay != 0) { return false; }
        // If this trigger is enabled by other triggers or disabled by other triggers,
        // this might be being used as a conditional trigger, in which case we can't simplify it
        if (inbounds.SelectMany(inbound => inbound.Targets)
            .SelectMany(target => target.Actions)
            .Any(action => action is Enable || action is Disable))
        {
            return false;
        }
        if (inbounds.Count() > 1 && outbounds.Count() > 1) // It's bad to simplify N to N relays
        {
            return false;
        }
        return true;
    }

    private static IEnumerable<ActivateTrigger> findInboundTriggers(Entity entity, IEnumerable<Entity> entities)
    {
        return entities
            .OfType<ActivateTrigger>()
            .Where(trigger => trigger.Targets.Any(target => target.Targeted == entity));
    }

    private static bool distributeOutboundConnections(ActivateTrigger trigger, IList<Entity> entities)
    {
        IEnumerable<Entity> outbounds = trigger.Targets.Select(target => target.Targeted);
        IEnumerable<ActivateTrigger> inbounds = findInboundTriggers(trigger, entities);
        IEnumerable<Target> targets = trigger.Targets;
        IEnumerable<Entity> targetsOfInbounds = inbounds.SelectMany(inbound => inbound.Targets)
            .Select(target => target.Targeted);
        // If the inbounds share a target, that might be a case of this trigger being used to add a second action
        // to the same trigger, and so it should not be simplified away
        if (outbounds.Intersect(targetsOfInbounds).Any())
        {
            return false;
        }
        // No self looping triggers either
        if (outbounds.Where(outbound => outbound == trigger).Any()) { return false; }
        foreach (ActivateTrigger inbound in inbounds)
        {
            int index = inbound.Targets.Select(target => target.Targeted).ToList().IndexOf(trigger);
            foreach (Target target in targets.Reverse())
            {
                inbound.InsertTarget(index, target);
            }
        }
        removeEntityAndItsInboundTriggerTargets(trigger, entities);
        return true;
    }

    private static IEnumerable<ActivateTrigger> findRelayTriggersThatAreNeverActivated(IEnumerable<Entity> entities)
    {
        return entities
            .OfType<ActivateTrigger>()
            .Where(trigger => trigger.TriggeredBy == TriggeredBy.Triggers &&
                !findInboundTriggers(trigger, entities)
                    .Any(inbound => inbound.Targets.Any(target => 
                        target.Targeted == trigger && 
                        target.Actions.Any(action => action is Activate))));
    }

    private static IEnumerable<ActivateTrigger> findActivateTriggersThatHaveNoTargets(IEnumerable<Entity> entities)
    {
        return entities
            .OfType<ActivateTrigger>()
            .Where(trigger => !trigger.Targets.Any());
    }

    public static void RemoveDisabledTriggersThatStayDisabled(IList<Entity> entities)
    {
        IList<ActivateTrigger> toRemove = findDisabledTriggersThatStayDisabled(entities).ToList();
        removeEntitiesAndTheirInboundTriggerTargets(toRemove, entities);
    }

    public static void SimplifyRelayTriggers(IList<Entity> entities)
    {
        IList<ActivateTrigger> simpleRelays = findUnipolarSimpleRelayTriggers(entities).ToList();
        foreach (ActivateTrigger trigger in simpleRelays)
        {
            distributeOutboundConnections(trigger, entities);
        }
    }

    // While the previous method pushes back any relay triggers that do nothing,
    // we still need to snip off any useless chains at the start
    public static void RemoveRelayTriggersThatAreNeverActivated(IList<Entity> entities)
    {
        IList<ActivateTrigger> toRemove = findRelayTriggersThatAreNeverActivated(entities).ToList();
        removeEntitiesAndTheirInboundTriggerTargets(toRemove, entities);
    }

    /// <summary>
    /// Removes any disabled triggers that stay disabled,
    /// and simplifies any relay triggers that are not n to n,
    /// by pushing them back into their inbound triggers
    /// </summary>
    public static void OptimizeTriggers(IList<Entity> entities)
    {
        IList<ActivateTrigger> toRemove = 
            findDisabledTriggersThatStayDisabled(entities)
            .Union(findRelayTriggersThatAreNeverActivated(entities))
            .Union(findActivateTriggersThatHaveNoTargets(entities))
            .ToList();
        IList<ActivateTrigger> simpleRelays = findUnipolarSimpleRelayTriggers(entities).ToList();
        if (toRemove.Any() || simpleRelays.Any())
        {
            bool removedAny = toRemove.Any();
            foreach (ActivateTrigger trigger in simpleRelays)
            {
                if (distributeOutboundConnections(trigger, entities))
                {
                    removedAny = true;
                }
            }
            removeEntitiesAndTheirInboundTriggerTargets(toRemove, entities);
            if (removedAny)
            {
                OptimizeTriggers(entities);
            }
        }
    }

}
