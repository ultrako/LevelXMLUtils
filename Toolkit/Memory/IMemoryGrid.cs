using LevelXML;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Memory;

public interface IMemoryGrid
{
    public uint LastAddress {get;}
    public Input Read {get;}
    public IList<IList<Input>> DirectWrites {get;}
    public IList<IList<Input>> Clears {get;}
    public Input EnableWrite {get;}
    public IList<Input> Writes {get;}
    public Input DisableWrite {get;}
    public IList<Input> AddressClears {get;}
    public IList<Input> Address {get;}
    public IList<Output> Values {get;}
    public IEnumerable<Entity> Entities {get;}
    public IReadOnlyList<IMemoryRow> Rows {get;}
    public void SetValue(int index, uint value);
    
}