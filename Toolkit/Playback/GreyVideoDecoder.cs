using LevelXML;
namespace LevelXMLUtils.Playback;

/// <summary>
/// <see cref="VideoDecoder"/> for a screen of grey levels: each pixel is one white rectangle over a black background,
/// and a frame sets pixels to levels instead of toggling them. Level g of k shows the rectangle at opacity
/// 100 * g / (k - 1).
/// <list type="bullet">
/// <item>Each pixel has a gate per level (starts disabled) that sets its rectangle to that level.</item>
/// <item>"Column x to level g" activates the level-g gates of column x in every row; only the selected row's are enabled.</item>
/// <item>"Row y" selects row y, as in VideoDecoder (a clear relay, a clearer per row).</item>
/// <item>"Fill row y with level g" activates every level-g gate of row y: a row whose pixels mostly change to one level
/// is filled and then the others set (scene cuts).</item>
/// </list>
/// A column command a frame needs more than once is listed that many times in the frame's trigger.
/// </summary>
public class GreyVideoDecoder : TriggerContraption
{
    public int Width { get; }
    public int Height { get; }
    public int Levels { get; }
    /// <summary>[y][x][g]: the gate setting pixel (x, y) to level g (it fires when the pixel is set to g).</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<Relay>>> Gates { get; }
    /// <summary>Every trigger of the decoder (to keep out of LevelCruncher.OptimizeTriggers).</summary>
    public IReadOnlyList<ActivateTrigger> Triggers { get; }

    private readonly IReadOnlyList<IReadOnlyList<Relay>> columns;
    private readonly IReadOnlyList<Relay> rows;
    private readonly IReadOnlyList<IReadOnlyList<Relay>> fills;
    private int? selectedRow;

    public GreyVideoDecoder(double x, double y, double pixelWidth, double pixelHeight, int width, int height, int levels)
    {
        Width = width;
        Height = height;
        Levels = levels;
        Relay MakeRelay(bool startDisabled = false) => new(x, y, 5, 5, startDisabled, 0);
        Rectangle background = new()
        {
            X = x + pixelWidth * (width - 1) / 2, Y = y + pixelHeight * (height - 1) / 2,
            Width = pixelWidth * width, Height = pixelHeight * height,
            FillColor = 0, OutlineColor = -1, Interactive = false,
        };
        List<List<Rectangle>> pixels = Enumerable.Range(0, height).Select(py => Enumerable.Range(0, width).Select(px => new Rectangle
        {
            X = x + pixelWidth * px, Y = y + pixelHeight * py, Width = pixelWidth, Height = pixelHeight,
            FillColor = 0xFFFFFF, OutlineColor = -1, Interactive = false, Opacity = 0,
        }).ToList()).ToList();
        columns = Enumerable.Range(0, width).Select(_ => (IReadOnlyList<Relay>)Enumerable.Range(0, levels).Select(_ => MakeRelay()).ToList()).ToList();
        rows = Enumerable.Range(0, height).Select(_ => MakeRelay()).ToList();
        fills = Enumerable.Range(0, height).Select(_ => (IReadOnlyList<Relay>)Enumerable.Range(0, levels).Select(_ => MakeRelay()).ToList()).ToList();
        Relay clear = MakeRelay();
        List<Relay> clearers = Enumerable.Range(0, height).Select(_ => MakeRelay(startDisabled: true)).ToList();
        List<List<List<Relay>>> gates = Enumerable.Range(0, height).Select(_ => Enumerable.Range(0, width).Select(_ =>
            Enumerable.Range(0, levels).Select(_ => MakeRelay(startDisabled: true)).ToList()).ToList()).ToList();
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                for (int g = 0; g < levels; g++)
                {
                    double opacity = Math.Round(100.0 * g / (levels - 1));
                    gates[py][px][g].Output.Connect(new Input { new Target<Shape>(pixels[py][px], new ChangeOpacity<Shape>(Opacity: opacity, Duration: 0)) });
                }
            }
            List<Relay> rowGates = gates[py].SelectMany(pixel => pixel).ToList();
            clear.Output.Connect(clearers[py].Input);
            foreach (Relay gate in rowGates) { clearers[py].Output.Connect(gate.Disable); }
            clearers[py].Output.Connect(clearers[py].Disable);
            rows[py].Output.Connect(clear.Input);
            foreach (Relay gate in rowGates) { rows[py].Output.Connect(gate.Enable); }
            rows[py].Output.Connect(clearers[py].Enable);
            for (int g = 0; g < levels; g++)
            {
                foreach (List<Relay> pixel in gates[py]) { fills[py][g].Output.Connect(pixel[g].Input); }
            }
        }
        for (int px = 0; px < width; px++)
        {
            for (int g = 0; g < levels; g++)
            {
                for (int py = 0; py < height; py++) { columns[px][g].Output.Connect(gates[py][px][g].Input); }
            }
        }
        Gates = gates.Select(row => (IReadOnlyList<IReadOnlyList<Relay>>)row.Select(pixel => (IReadOnlyList<Relay>)pixel).ToList()).ToList();
        // The commands first (short ids), the column commands level by level.
        List<Entity> triggers = Enumerable.Range(0, levels).SelectMany(g => columns.Select(c => c[g]))
            .Concat(rows).Concat(fills.SelectMany(f => f)).Append(clear).Concat(clearers)
            .Concat(gates.SelectMany(row => row).SelectMany(pixel => pixel))
            .SelectMany(r => r.Entities).ToList();
        Triggers = triggers.OfType<ActivateTrigger>().ToList();
        Entities = new Entity[] { background }.Concat(pixels.SelectMany(row => row)).Concat(triggers).ToList();
    }

    /// <summary>Appends to frame the commands that take the screen from previous to next ([x, y] levels).</summary>
    public void Encode(Output frame, byte[,] previous, byte[,] next)
    {
        for (int py = 0; py < Height; py++)
        {
            List<int> changed = Enumerable.Range(0, Width).Where(px => previous[px, py] != next[px, py]).ToList();
            if (changed.Count == 0) { continue; }
            if (selectedRow != py)
            {
                frame.Connect(rows[py].Input);
                selectedRow = py;
            }
            // Filling the row with the commonest new level, then setting every pixel not at it, if that is fewer commands.
            int fill = Enumerable.Range(0, Levels).MaxBy(g => changed.Count(px => next[px, py] == g));
            List<int> notFill = Enumerable.Range(0, Width).Where(px => next[px, py] != fill).ToList();
            if (notFill.Count + 1 < changed.Count)
            {
                frame.Connect(fills[py][fill].Input);
                changed = notFill;
            }
            foreach (int px in changed) { frame.Connect(columns[px][next[px, py]].Input); }
        }
    }
}
