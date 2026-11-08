using LevelXML;
namespace LevelXMLUtils.Playback;

/// <summary>A grid of white panels over a black background: <see cref="Ons"/>[y][x] shows pixel (x, y), <see cref="Offs"/>[y][x]
/// hides it. Pixel (0, 0) is centred on (x, y).</summary>
public class PixelScreen : TriggerContraption
{
	public IReadOnlyList<IReadOnlyList<Input>> Ons { get; }
	public IReadOnlyList<IReadOnlyList<Input>> Offs { get; }

	public PixelScreen(double x, double y, double pixelWidth, double pixelHeight, int width, int height)
	{
		Panel background = new(x + pixelWidth * (width - 1) / 2, y + pixelHeight * (height - 1) / 2, pixelWidth * width, pixelHeight * height, onColor: 0x0, hasOff: false, startDisabled: false);
		var panels = Enumerable.Range(0, height).Select(py => Enumerable.Range(0, width)
			.Select(px => new Panel(x + pixelWidth * px, y + pixelHeight * py, pixelWidth, pixelHeight, hasOff: false, onColor: 0xFFFFFF)).ToList()).ToList();
		Ons = panels.Select(row => (IReadOnlyList<Input>)row.Select(p => p.Input).ToList()).ToList();
		Offs = panels.Select(row => (IReadOnlyList<Input>)row.Select(p => p.Reset).ToList()).ToList();
		Entities = background.Entities.Concat(panels.SelectMany(row => row).SelectMany(p => p.Entities)).ToList();
	}
}
