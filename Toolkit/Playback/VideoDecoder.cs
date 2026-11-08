using LevelXML;
namespace LevelXMLUtils.Playback;

/// <summary>
/// Plays a black-and-white video from row and column commands instead of one arm per pixel change. A frame is, for each row with changes, "row y" and then "column x" for each changed
/// pixel of that row: a column command toggles its column's pixel in the selected row.
/// <list type="bullet">
/// <item>Each pixel has a toggle driving the screen's on and off, and a gate (starts disabled) that toggles it.</item>
/// <item>"Column x" activates column x's relay in every band (a band is a run of rows); only the selected band's
/// relays are enabled, and each activates the gates of column x in its band's rows; only the selected row's gates are
/// enabled. Without bands a column command would activate a gate in every row, 40 trigger actions a changed pixel at
/// 70x40; with bands of about the square root of the height it is about 13. The frames name the same commands, so
/// the level is no bigger.</item>
/// <item>"Row y" first activates its band's select relay. That relay is disabled while its band is selected, so it
/// only fires when the band changes: it activates the band clear relay (whichever band is selected disables its column
/// relays and its own clearer, and enables its select relay again), then enables its column relays and its clearer,
/// and disables itself. The frames never name a band (a command for it cost 25 KB at 70x40).</item>
/// <item>"Row y" activates the clear relay (whichever row is selected disables its gates and its own clearer), then
/// enables row y's gates and its clearer.</item>
/// <item>"Down d" (d from 1 to <see cref="MaxStep"/>) selects the row d below the row last selected by a row command:
/// it activates the step relay (d, y) of every row y, bottom row first, and only the selected row's are enabled (the
/// row command enables row y's, its clearer disables them); the step activates "row y + d". Bottom first, or a step
/// would enable the next row's step relay before the same command reached it. A frame mostly selects the next row or
/// one a little further down, so the same few commands repeat and compress far better than row numbers: on Bad Apple at
/// 74x42 the video's commands went from 566 to 443 KB (steps up to 4; up to 1 gave 479 KB).</item>
/// <item>"Add row y" selects row y as well as the rows already selected: it enables row y's gates and its clearer
/// without the clear, and adds row y's band through the band's add relay (enabled exactly while its select relay is,
/// and it enables the band's column relays and clearer without the band clear). Rows of a frame that change in the
/// same columns are selected together and the columns sent once (with groups only of identical rows: rows that
/// share only some columns cost more bytes than they save, since deflate already finds neighbouring rows' repeats).
/// A band added this way also enables the band reset relay, which the next row command fires first (it clears every
/// band, then disables itself): otherwise bands added by a group stay selected while the row command's own band is,
/// and every column command fans out to them again (the busiest frame at 72x41 went from 23,805 actions to 45,549).</item>
/// <item>"Invert row y" toggles all of row y's pixels; a row where more than half the pixels change is inverted and
/// then the ones that don't change are toggled back (fewer commands, and a column command costs a gate activation in
/// every row while an inversion costs one per pixel: scene cuts would otherwise run 130,000 actions in a frame at
/// 68x39).</item>
/// </list>
/// A frame names the same column command once for each row (or group of rows) it changes in: the frame's trigger lists
/// it that many times, and the game runs every entry.
/// </summary>
public class VideoDecoder : TriggerContraption
{
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<Relay> Rows { get; }
    /// <summary>"Down d": selects the row d below the row last selected by a row command (index d - 1).</summary>
    public IReadOnlyList<Relay> Downs { get; }
    public int MaxStep { get; }
    /// <summary>"Add row y": selects row y without clearing the rows already selected.</summary>
    public IReadOnlyList<Relay> AddRows { get; }
    /// <summary>Whether <see cref="Encode"/> selects rows with the same changes together.</summary>
    public bool GroupRows { get; }
    public IReadOnlyList<Relay> Columns { get; }
    /// <summary>"Invert row y": activates every gate of row y (all enabled once the row is selected).</summary>
    public IReadOnlyList<Relay> Inverts { get; }
    /// <summary>Band b's select relay (bands are runs of <see cref="BandHeight"/> rows); its rows' commands activate it.</summary>
    public IReadOnlyList<Relay> Bands { get; }
    public int BandHeight { get; }
    /// <summary>[y][x]: the pixel's toggle; its <c>Off</c> trigger is enabled while the pixel is on.</summary>
    public IReadOnlyList<IReadOnlyList<InstantToggle>> Toggles { get; }
    /// <summary>Every trigger of the decoder (to keep out of LevelCruncher.OptimizeTriggers, which would fold the commands into
    /// the frames).</summary>
    public IReadOnlyList<ActivateTrigger> Triggers { get; }

    private readonly SortedSet<int> selected = new();
    private int? cursor;

    /// <param name="bands">how many bands the rows are split into (default: about the square root of the height, which
    /// keeps a column command's actions, one per band plus one per row of a band, smallest)</param>
    /// <param name="groupRows">select rows of a frame with the same changes together (on Bad Apple, 15.6 KB less at 72x41
    /// without row steps, but with them the groups break up runs of "down 1" and cost 20 KB at 74x42)</param>
    /// <param name="maxStep">the largest row step with its own command (0 for none)</param>
    public VideoDecoder(PixelScreen screen, int width, int height, double x = 0, double y = 0, int? bands = null, bool groupRows = false, int maxStep = 4)
    {
        GroupRows = groupRows;
        MaxStep = Math.Clamp(maxStep, 0, height - 1);
        Width = width;
        Height = height;
        Relay MakeRelay(bool startDisabled = false) => new(x, y, 5, 5, startDisabled, 0);
        Columns = Enumerable.Range(0, width).Select(_ => MakeRelay()).ToList();
        Rows = Enumerable.Range(0, height).Select(_ => MakeRelay()).ToList();
        AddRows = Enumerable.Range(0, height).Select(_ => MakeRelay()).ToList();
        Downs = Enumerable.Range(0, MaxStep).Select(_ => MakeRelay()).ToList();
        // steps[d - 1][y]: from row y to row y + d
        List<List<Relay>> steps = Enumerable.Range(1, MaxStep).Select(d => Enumerable.Range(0, height - d).Select(_ => MakeRelay(startDisabled: true)).ToList()).ToList();
        Inverts = Enumerable.Range(0, height).Select(_ => MakeRelay()).ToList();
        int bandCount = Math.Clamp(bands ?? (int)Math.Round(Math.Sqrt(height)), 1, height);
        BandHeight = (height + bandCount - 1) / bandCount;
        bandCount = (height + BandHeight - 1) / BandHeight;
        Bands = Enumerable.Range(0, bandCount).Select(_ => MakeRelay()).ToList();
        List<Relay> bandAdds = Enumerable.Range(0, bandCount).Select(_ => MakeRelay()).ToList();
        Relay bandReset = MakeRelay(startDisabled: true);
        Relay bandClear = MakeRelay();
        List<Relay> bandClearers = Enumerable.Range(0, bandCount).Select(_ => MakeRelay(startDisabled: true)).ToList();
        List<List<Relay>> bandColumns = Enumerable.Range(0, bandCount).Select(_ => Enumerable.Range(0, width).Select(_ => MakeRelay(startDisabled: true)).ToList()).ToList();
        for (int b = 0; b < bandCount; b++)
        {
            bandClear.Output.Connect(bandClearers[b].Input);
            foreach (Relay column in bandColumns[b]) { bandClearers[b].Output.Connect(column.Disable); }
            bandClearers[b].Output.Connect(bandClearers[b].Disable);
            Bands[b].Output.Connect(bandClear.Input);
            foreach (Relay column in bandColumns[b]) { Bands[b].Output.Connect(column.Enable); }
            Bands[b].Output.Connect(bandClearers[b].Enable);
            Bands[b].Output.Connect(Bands[b].Disable);
            Bands[b].Output.Connect(bandAdds[b].Disable);
            bandClearers[b].Output.Connect(Bands[b].Enable);
            bandClearers[b].Output.Connect(bandAdds[b].Enable);
            foreach (Relay column in bandColumns[b]) { bandAdds[b].Output.Connect(column.Enable); }
            bandAdds[b].Output.Connect(bandClearers[b].Enable);
            bandAdds[b].Output.Connect(bandAdds[b].Disable);
            bandAdds[b].Output.Connect(Bands[b].Disable);
            bandAdds[b].Output.Connect(bandReset.Enable);
        }
        bandReset.Output.Connect(bandClear.Input);
        bandReset.Output.Connect(bandReset.Disable);
        Relay clear = MakeRelay();
        List<Relay> clearers = Enumerable.Range(0, height).Select(_ => MakeRelay(startDisabled: true)).ToList();
        List<List<Relay>> gates = Enumerable.Range(0, height).Select(_ => Enumerable.Range(0, width).Select(_ => MakeRelay(startDisabled: true)).ToList()).ToList();
        List<List<InstantToggle>> toggles = Enumerable.Range(0, height).Select(_ => Enumerable.Range(0, width).Select(_ => new InstantToggle(x, y, 5, 5)).ToList()).ToList();
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                gates[py][px].Output.Connect(toggles[py][px].Input);
                toggles[py][px].On.Connect(screen.Ons[py][px]);
                toggles[py][px].Off.Connect(screen.Offs[py][px]);
            }
            clear.Output.Connect(clearers[py].Input);
            foreach (Relay gate in gates[py]) { clearers[py].Output.Connect(gate.Disable); }
            clearers[py].Output.Connect(clearers[py].Disable);
            Rows[py].Output.Connect(bandReset.Input);
            Rows[py].Output.Connect(Bands[py / BandHeight].Input);
            Rows[py].Output.Connect(clear.Input);
            foreach (Relay gate in gates[py]) { Rows[py].Output.Connect(gate.Enable); }
            Rows[py].Output.Connect(clearers[py].Enable);
            foreach (List<Relay> step in steps.Where(step => py < step.Count))
            {
                Rows[py].Output.Connect(step[py].Enable);
                clearers[py].Output.Connect(step[py].Disable);
            }
            AddRows[py].Output.Connect(bandAdds[py / BandHeight].Input);
            foreach (Relay gate in gates[py]) { AddRows[py].Output.Connect(gate.Enable); }
            AddRows[py].Output.Connect(clearers[py].Enable);
            foreach (Relay gate in gates[py]) { Inverts[py].Output.Connect(gate.Input); }
        }
        for (int d = 1; d <= MaxStep; d++)
        {
            for (int py = steps[d - 1].Count - 1; py >= 0; py--)
            {
                Downs[d - 1].Output.Connect(steps[d - 1][py].Input);
                steps[d - 1][py].Output.Connect(Rows[py + d].Input);
            }
        }
        for (int px = 0; px < width; px++)
        {
            for (int b = 0; b < bandCount; b++)
            {
                Columns[px].Output.Connect(bandColumns[b][px].Input);
                for (int py = b * BandHeight; py < Math.Min(height, (b + 1) * BandHeight); py++) { bandColumns[b][px].Output.Connect(gates[py][px].Input); }
            }
        }
        Toggles = toggles;
        // The commands first: a trigger target is written as the trigger's index, so the most used get short ones.
        List<Entity> entities = Columns.Concat(Rows).Concat(Downs).Concat(AddRows).Concat(Inverts).Concat(Bands).Concat(bandAdds).Append(bandReset).Append(clear).Concat(clearers)
            .Append(bandClear).Concat(bandClearers).Concat(bandColumns.SelectMany(row => row)).Concat(steps.SelectMany(step => step))
            .SelectMany(r => r.Entities)
            .Concat(gates.SelectMany(row => row).SelectMany(r => r.Entities))
            .Concat(toggles.SelectMany(row => row).SelectMany(t => t.Entities))
            .ToList();
        Entities = entities;
        Triggers = entities.OfType<ActivateTrigger>().ToList();
    }

    /// <summary>Appends to frame the commands that toggle every pixel set in changed ([x, y], as Video's frames).</summary>
    public void Encode(Output frame, bool[,] changed)
    {
        void Send(Relay command) => frame.Connect(command.Input);
        // Each row with changes: whether it is inverted first, and the columns to toggle after that.
        List<(int Row, bool Invert, List<int> Columns)> rows = new();
        for (int py = 0; py < Height; py++)
        {
            List<int> columns = Enumerable.Range(0, Width).Where(px => changed[px, py]).ToList();
            if (columns.Count == 0) { continue; }
            bool invert = 2 * columns.Count > Width;
            rows.Add((py, invert, invert ? Enumerable.Range(0, Width).Where(px => !changed[px, py]).ToList() : columns));
        }
        // Groups of rows toggling the same columns, in the order of their first rows (rows with nothing left after an
        // inversion, and every row without grouping, are groups of one).
        List<List<(int Row, bool Invert, List<int> Columns)>> groups = GroupRows
            ? rows.GroupBy(r => r.Columns.Count == 0 ? $"row {r.Row}" : string.Join(",", r.Columns)).Select(g => g.ToList()).ToList()
            : rows.Select(r => new List<(int, bool, List<int>)> { r }).ToList();
        foreach (var group in groups)
        {
            foreach (var row in group.Where(r => r.Invert))
            {
                Select(row.Row);
                Send(Inverts[row.Row]);
            }
            if (group[0].Columns.Count == 0) { continue; }
            Select(group[0].Row);
            foreach (var row in group.Skip(1))
            {
                if (selected.Add(row.Row)) { Send(AddRows[row.Row]); }
            }
            foreach (int px in group[0].Columns) { Send(Columns[px]); }
        }

        void Select(int py)
        {
            if (selected.Count == 1 && selected.Min == py) { return; }
            Send(cursor is int from && py - from >= 1 && py - from <= MaxStep ? Downs[py - from - 1] : Rows[py]);
            cursor = py;
            selected.Clear();
            selected.Add(py);
        }
    }
}
