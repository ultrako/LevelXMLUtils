using LevelXML;
using System;

double axisX = args.Count() > 0 ? double.Parse(args[0]) : 2000;

var levelString = await new StreamReader(Console.OpenStandardInput()).ReadToEndAsync();
Level level = new(levelString);

foreach (Group group in level.Groups)
{
    group.X += group.OriginX;
    group.Y += group.OriginY;
    group.OriginX = 0;
    group.OriginY = 0;
    foreach (Entity entity in group.Items)
    {
        entity.X += group.X;
        entity.Y += group.Y;
    }
    group.X = 0;
    group.Y = 0;
}

foreach (CustomShape custom in level.Shapes.Concat(level.Groups.SelectMany(group => group.Items)).OfType<CustomShape>())
{
    IList<Vertex> newVertices = custom.Vertices
        .Select(vertex => new Vertex(
            new(-vertex.Position.X,vertex.Position.Y),
            vertex.HandleOne is Point h1 ? new(-h1.X, h1.Y) : null,
            vertex.HandleTwo is Point h2 ? new(-h2.X, h2.Y) : null
        ))
        .ToList();
    custom.Vertices.Clear();
    foreach (Vertex vertex in newVertices)
    {
        custom.Vertices.Add(vertex);
    }
    custom.X = axisX - custom.X;
}

foreach (Entity entity in level.Entities.Concat(level.Groups.SelectMany(group => group.Items)))
{
    if (entity is not CustomShape && entity is not Group)
    {
        entity.X = axisX - entity.X;
        if (entity is IRotatable rotatable)
        {
            rotatable.Rotation = -rotatable.Rotation;
        }
    }
    if (entity is NonPlayerCharacter npc)
    {
        npc.Reverse = !npc.Reverse;
    }
}
level.X = axisX - level.X;

Console.Write(level.ToXML());