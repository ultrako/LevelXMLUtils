using LevelXML;
namespace LevelXMLUtils;

///<summary>
/// Used to make a Panel
///</summary>
public class RectangleArtShape : Art
{
	public RectangleArtShape(double x=0, double y=0, double width=5, double height=5, double rotation=0, double color=0x0, double outline=-1, double opacity=100) 
	{
		this.X = x;
		this.Y = y;
		Width = width;
		Height = height;
		Rotation = rotation;
		FillColor = color;
		OutlineColor = outline;
		Opacity = opacity;
		Vertices.Add(new(new(-0.5*width, -0.5*height)));
		Vertices.Add(new(new(0.5*width, -0.5*height)));
		Vertices.Add(new(new(0.5*width, 0.5*height)));
		Vertices.Add(new(new(-0.5*width, 0.5*height)));
	}
}
