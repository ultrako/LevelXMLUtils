using LevelXML;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

public interface IMemoryRow
{
    public Input EnableRead {get;}
    public Input DisableRead {get;}
    public IList<Input> EnableWrites {get;}
    public IList<Input> DisableWrites {get;}
    public IList<Input> Clears {get;}
    public Input Read {get;}
    public IList<Input> Writes {get;}
    public IList<Input> DirectWrites {get;}
    public IList<Output> Values {get;}
    // Each cell's own read, whether or not the row is selected (null: the row doesn't have them)
    public IList<Input>? CellReads => null;
    // The trigger holding each bit, lowest first, each enabled while its bit is 1 (null: the row doesn't keep its bits that way)
    public IReadOnlyList<ActivateTrigger>? BitTriggers => null;
    public IEnumerable<Entity> Entities {get;}
    public void AddVisualization();
    public void AddClickTriggers();
    public void SetValue(uint value);
}

public static class MemoryRowExtensions
{
    /// <summary>The value a row holds at this point in a simulation: its bit triggers' enabled states, lowest bit first.</summary>
    public static uint ValueIn(this IMemoryRow row, TriggerGraph graph)
    {
        IReadOnlyList<ActivateTrigger> bits = row.BitTriggers ?? throw new NotSupportedException($"{row.GetType().Name} doesn't keep its bits in triggers it can name");
        uint value = 0;
        for (int bit = 0; bit < bits.Count; bit++) { if (graph.IsEnabled(TriggerGraph.IdFor(bits[bit]))) { value |= 1u << bit; } }
        return value;
    }
}
