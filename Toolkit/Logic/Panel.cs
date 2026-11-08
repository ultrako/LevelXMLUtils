using System.Xml.Linq;
using LevelXML;
namespace LevelXMLUtils;

/// <summary>A light: <see cref="Input"/> turns it on, <see cref="Reset"/> off.</summary>
public class Panel : TriggerContraption {
	public Input Input { get; }
	public Input Reset { get; }
	// It turns green when input is on
	// It needs power because there's no other way to make
	// a panel with 0 shape count. Power can be shared so this is efficient.
	private Rectangle onRect;
	private Group? onGroup;
	private bool foreground;
	public Panel(double x=0, double y=0, double width=5, double height=5, int offColor=0xFF0000, int onColor=0x00FF00, int outlineColor=-1, bool hasOff=true, string? label=null, double labelSize=1, bool startDisabled=true, bool foreground=false) {
		List<Entity> ents = new List<Entity>();
		this.foreground = foreground;
		Rectangle? offRect = null;
		if (hasOff)
		{
            offRect = new()
            {
                X = x,
				Y = y,
				Width = width,
				Height = height,
				FillColor = offColor,
				OutlineColor = outlineColor,
				Interactive = false,
            };
            ents.Add(offRect);
		}
		if (label is not null)
		{
			TextBox text = new();
			text.Content = label;
			text.X = x - width/3;
            text.Y = y - height/3;
            text.FontSize = width * 2/3 * labelSize;
			ents.Add(text);
		}
		onRect = new()
		{
			X = x,
			Y = y,
			Width = width,
			Height = height,
			FillColor = onColor,
			OutlineColor = outlineColor,
			Opacity = 100,
			Interactive = false,
		};
		ents.Add(onRect);
		if (foreground)
		{
            onGroup = new(onRect)
            {
                Foreground = true
            };
            Entities = new[]{onGroup};
			if (offRect is Rectangle off)
			{
                Group offGroup = new(off)
                {
                    Foreground = true
                };
                Entities = new[]{offGroup}.Concat(Entities);
			}
			Input = new() { new Target<Group>(onGroup, 
				new ChangeOpacity<Group>(Opacity: 100, Duration: 0)) };
			Reset = new() { new Target<Group>(onGroup, 
				new ChangeOpacity<Group>(Opacity: 0, Duration: 0)) };
		} else
		{
			Input = new() { new Target<Shape>(onRect, 
				new ChangeOpacity<Shape>(Opacity: 100, Duration: 0)) };
			Reset = new() { new Target<Shape>(onRect, 
				new ChangeOpacity<Shape>(Opacity: 0, Duration: 0)) };
			Entities = ents;
		}
		StartDisabled = startDisabled;
	}

	public bool StartDisabled
	{
		get 
		{
			if (foreground)
			{
				return onGroup!.Opacity == 100;
			}
			else
			{
				return onRect.Opacity == 100; 
			}
		}
		set 
		{
			if (foreground)
			{
				onGroup!.Opacity = value ? 0 : 100;
			}
			else
			{
				onRect.Opacity = value ? 0 : 100;
			}
		}
	}
}

/// <summary>A row of n panels side by side from (x, y), their inputs and resets as lists in the same order.</summary>
public class PanelRow : TriggerContraption
{
	public IList<Panel> Panels { get; }
	public IList<Input> Inputs;
	public IList<Input> Resets;
	public PanelRow(int n, double x, double y, double width, double height, bool hasOff = true, int onColor = 0x00FF00)
	{
		Panels = Enumerable.Range(0, n).Select(index => new Panel(x + width * index, y, width, height, hasOff: hasOff, onColor: onColor)).ToList();
		Inputs = Panels.Select(panel => panel.Input).ToList();
		Resets = Panels.Select(panel => panel.Reset).ToList();
		Entities = Panels.SelectMany(panel => panel.Entities).ToList();
	}
}
