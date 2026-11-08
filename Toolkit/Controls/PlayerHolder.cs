using LevelXML;
namespace LevelXMLUtils.Controls;

public class PlayerHolder : TriggerContraption
{
    public Vehicle Handle;
    public PlayerHolder(double x, double y)
    {
        string importedXML = EmbeddedLevels.Read("PlayerHolder.levelXML");
        Level level = new(importedXML);
        foreach (Entity entity in level.Entities)
        {
            entity.X += x - 299;
            entity.Y += y - 5097;
        }
        Handle = (Vehicle)level.Groups[0];
        Entities = level.Entities;
    }
}