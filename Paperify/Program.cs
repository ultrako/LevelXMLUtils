using LevelXML;

double axisX = args.Length > 0 ? double.Parse(args[0]) : 2000;

var levelString = await new StreamReader(Console.OpenStandardInput()).ReadToEndAsync();
Level level = new(levelString);
List<Entity> entities = [];
double crumpleLength = 20;

foreach (Triangle triangle in level.Shapes.Concat(level.Groups.SelectMany(group => group.Items)).OfType<Triangle>())
{
    Art art = new();
    art.Vertices.Add(new(new(-triangle.Width/2.0, triangle.Height/3.0)));
    art.Vertices.Add(new(new(triangle.Width/2.0, triangle.Height/3.0)));
    art.Vertices.Add(new(new(0, -2.0*triangle.Height/3.0)));
    art.Rotation = triangle.Rotation;
    art.Width = triangle.Width;
    art.Height = triangle.Height;
    art.X = triangle.X;
    art.Y = triangle.Y;
    art.FillColor = 0xff0000;
    entities.Add(triangle);
    entities.Add(art);
}

foreach (Circle circle in level.Shapes)
{
    Art art = new();
    double radius = circle.Width/2.0;
    double circumpherence = circle.Width * Math.PI;
    int segments = (int)(circumpherence/crumpleLength);
    segments = Math.Clamp(segments, 3, int.MaxValue);
    foreach (double radians in Enumerable.Range(0, segments).Select(index => Math.Tau * (index / (double)segments)))
    {
        art.Vertices.Add(new(new(Math.Cos(radians)*radius, Math.Sin(radians)*radius)));
    }
    art.Width = circle.Width;
    art.Height = circle.Width;
    art.X = circle.X;
    art.Y = circle.Y;
    art.FillColor = 0xff0000;
    entities.Add(circle);
    entities.Add(art);
}

Console.Write(new Level([.. entities]).ToXML());