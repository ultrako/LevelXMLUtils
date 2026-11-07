using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using LevelXML;
using LevelXMLUtils.Playback;

namespace LevelXMLUtils.Tests;

public class VideoDecoderTests
{
    const int W = 12, H = 7;
    const double X = 50, Y = 26, Pixel = 16;

    /// <summary>Random frames: some nearly all changing, some still, some changing in many rows of one column.</summary>
    static bool[][,] Frames(int count, int seed)
    {
        Random random = new(seed);
        bool[][,] frames = new bool[count][,];
        bool[,] last = new bool[W, H];
        for (int f = 0; f < count; f++)
        {
            bool[,] frame = (bool[,])last.Clone();
            double share = (f % 10) switch { 0 => 0, 1 => 0.9, _ => 0.15 };
            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < H; y++)
                {
                    if (random.NextDouble() < share) { frame[x, y] = !frame[x, y]; }
                }
            }
            if (f % 10 == 5) { for (int y = 0; y < H; y++) { frame[3, y] = !frame[3, y]; } }
            // Rows with the same changes (grouped by the decoder): rows 1, 3 and 6 (three bands) toggle the same few
            // columns; rows 0 and 4 the same nine of twelve (inverted, then three toggled back).
            if (f % 10 == 7)
            {
                frame = (bool[,])last.Clone();
                int[] same = Enumerable.Range(0, W).Where(_ => random.NextDouble() < 0.3).ToArray();
                foreach (int y in new[] { 1, 3, 6 }) { foreach (int x in same) { frame[x, y] = !frame[x, y]; } }
                foreach (int y in new[] { 0, 4 }) { foreach (int x in new[] { 0, 1, 2, 4, 5, 7, 8, 9, 11 }) { frame[x, y] = !frame[x, y]; } }
                frame[10, 2] = !frame[10, 2];
            }
            frames[f] = frame;
            last = frame;
        }
        return frames;
    }

    /// <summary>Bands: 1 (none), 3 (the default for 7 rows: bands of 3, 3 and 1), 7 (one row each).</summary>
    [Theory]
    [InlineData(1, false, 0)]
    [InlineData(null, false, 0)]
    [InlineData(1, true, 0)]
    [InlineData(null, true, 0)]
    [InlineData(7, true, 0)]
    [InlineData(null, false, null)]
    [InlineData(1, false, 1)]
    [InlineData(null, true, null)]
    [InlineData(7, false, 6)]
    public void TheDecoder_ShowsEveryFrame_InTheTriggerGraph(int? bands, bool groupRows, int? maxStep)
    {
        bool[][,] frames = Frames(120, 1);
        Video video = new(X, Y, Pixel, Pixel, frames, bands: bands, groupRows: groupRows, maxStep: maxStep ?? 4);
        TriggerGraph graph = TriggerGraph.FromTriggers(video.Entities.OfType<ActivateTrigger>());
        VideoDecoder decoder = video.Decoder!;
        HashSet<ActivateTrigger> addRows = decoder.AddRows.SelectMany(r => r.Entities.OfType<ActivateTrigger>()).ToHashSet();
        bool sendsAddRow = video.Counter.Value.SelectMany(o => o).Any(t => t.Targets.Any(target => target.Targeted is ActivateTrigger a && addRows.Contains(a)));
        Assert.Equal(groupRows, sendsAddRow);
        HashSet<ActivateTrigger> downs = decoder.Downs.SelectMany(r => r.Entities.OfType<ActivateTrigger>()).ToHashSet();
        bool sendsDown = video.Counter.Value.SelectMany(o => o).Any(t => t.Targets.Any(target => target.Targeted is ActivateTrigger a && downs.Contains(a)));
        Assert.Equal(decoder.MaxStep > 0, sendsDown);
        // a frame names a column command once for each row it changes in: the same trigger listed more than once
        Assert.Contains(video.Counter.Value.SelectMany(o => o), t => t.Targets.GroupBy(target => target.Targeted).Any(g => g.Count() > 1));
        for (int f = 0; f < frames.Length; f++)
        {
            graph.Pulse(TriggerGraph.IdFor(video.Counter.Value[f][0]));
            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < H; y++)
                {
                    Assert.True(frames[f][x, y] == graph.IsEnabled(TriggerGraph.IdFor(decoder.Toggles[y][x].Off[0])), $"frame {f}, pixel {x}, {y}");
                }
            }
        }
    }

    /// <summary>
    /// The exported XML, run the way the game runs triggers (every listed target in order, a target listed twice run
    /// twice), shows every frame: each pixel's shape has the opacity of its pixel.
    /// </summary>
    [Theory]
    [InlineData(1, false, 0)]
    [InlineData(null, false, 0)]
    [InlineData(null, true, 0)]
    [InlineData(null, false, null)]
    [InlineData(null, true, null)]
    public void TheExportedLevel_ShowsEveryFrame(int? bands, bool groupRows, int? maxStep)
    {
        bool[][,] frames = Frames(120, 2);
        Video video = new(X, Y, Pixel, Pixel, frames, bands: bands, groupRows: groupRows, maxStep: maxStep ?? 4);
        PlayExport(video, frames.Length, (f, x, y, opacity) => Assert.True(frames[f][x, y] == (opacity > 0), $"frame {f}, pixel {x}, {y}"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void TheGreyDecoder_SetsEveryPixelToItsLevel_InTheExportedLevel(int levels)
    {
        Random random = new(levels);
        byte[][,] frames = new byte[120][,];
        byte[,] last = new byte[W, H];
        for (int f = 0; f < frames.Length; f++)
        {
            byte[,] frame = (byte[,])last.Clone();
            // every tenth frame mostly one level (fills), the rest a few random changes
            double share = f % 10 == 1 ? 0.9 : 0.2;
            byte common = (byte)random.Next(levels);
            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < H; y++)
                {
                    if (random.NextDouble() < share) { frame[x, y] = f % 10 == 1 ? common : (byte)random.Next(levels); }
                }
            }
            frames[f] = frame;
            last = frame;
        }
        Video video = new(X, Y, Pixel, Pixel, frames, levels);
        PlayExport(video, frames.Length, (f, x, y, opacity) =>
            Assert.True(Math.Round(100.0 * frames[f][x, y] / (levels - 1)) == opacity, $"frame {f}, pixel {x}, {y}: {opacity}"));
    }

    /// <summary>
    /// Tied sounds (a repeated stretch of the song) play through one relay per tie, and every frame still plays exactly
    /// its sounds: frames 0 and 5 play tie 0 (two copies of one sound, so two triggers), frame 9 has tie 0 with different
    /// sounds (played directly), frame 3 plays a sound of its own too.
    /// </summary>
    [Fact]
    public void TiedSounds_PlayThroughOneRelay_AndEveryFrameStillPlaysItsSounds()
    {
        SoundOnset[] onsets =
        {
            new(0, 10, 0.5, 0), new(0, 10, 0.5, 0), new(0, 11, 0.25, 0),
            new(3, 12, 1), new(3, 10, 0.5, 3),
            new(5, 10, 0.5, 0), new(5, 10, 0.5, 0), new(5, 11, 0.25, 0),
            new(9, 10, 0.5, 0),
        };
        Video video = new(X, Y, Pixel, Pixel, Frames(12, 3), onsets);
        List<Entity> entities = video.Entities.ToList();
        LevelCruncher.OptimizeTriggers(entities, video.DecoderTriggers);
        HashSet<Entity> kept = entities.ToHashSet();
        List<(int, double)> Played(Trigger from)
        {
            List<(int, double)> out_ = new();
            foreach (Target target in from.Targets)
            {
                if (target.Targeted is SoundTrigger sound) { Assert.Contains(sound, kept); out_.Add(((int)sound.Sound, sound.Volume)); }
                else if (target.Targeted is ActivateTrigger relay && relay.Targets.All(t => t.Targeted is SoundTrigger)) { Assert.Contains(relay, kept); out_.AddRange(Played(relay)); }
            }
            return out_;
        }
        foreach (int f in new[] { 0, 3, 5, 9 })
        {
            var expected = onsets.Where(o => o.Frame == f).Select(o => (o.SoundId, o.Volume)).Order().ToList();
            Assert.Equal(expected, Played(video.Counter.Value[f][0]).Order().ToList());
        }
        // Frames 0 and 5 share one relay.
        ActivateTrigger RelayOf(int f) => video.Counter.Value[f][0].Targets.Select(t => t.Targeted).OfType<ActivateTrigger>()
            .Single(r => r.Targets.Count > 0 && r.Targets.All(t => t.Targeted is SoundTrigger));
        Assert.Same(RelayOf(0), RelayOf(5));
    }

    /// <summary>A video made with startOnLoad: false does not start when the level loads (a lock opens it through Start);
    /// by default the level's start trigger starts it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartOnLoad_DecidesWhetherTheLevelStartStartsTheVideo(bool startOnLoad)
    {
        Video video = new(X, Y, Pixel, Pixel, Frames(3, 1), startOnLoad: startOnLoad);
        ActivateTrigger levelStart = video.Entities.OfType<ActivateTrigger>().Single(t => t.TriggeredBy == TriggeredBy.Any);
        Trigger clock = (Trigger)video.Start.Single().Targeted;
        Assert.IsType<EveryFrameTrigger>(clock);
        Assert.Equal(startOnLoad, levelStart.Targets.Any(t => ReferenceEquals(t.Targeted, clock)));
    }

    /// <summary>Sound chords change how the sounds are wired, not what plays: every frame still starts exactly its own
    /// sounds (through any depth of chords), chords are made, and no sound or chord is listed twice by one trigger.</summary>
    [Fact]
    public void SoundChords_EveryFrameStillStartsExactlyItsSounds()
    {
        Random random = new(5);
        (int, double)[] palette = Enumerable.Range(0, 6).Select(i => (100 + i, 0.25 * (1 + i % 3))).ToArray();
        List<SoundOnset> onsets = new();
        for (int f = 0; f < 30; f++)
        {
            // A common group most frames play, plus a few others; frame 7 doubles a sound; frames 20 and 26 are a tie.
            foreach (var (id, volume) in palette.Where((_, i) => i < 3 ? f % 5 != 0 : random.Next(3) == 0)) { onsets.Add(new(f, id, volume)); }
            if (f == 7) { onsets.Add(new(f, 100, 0.25)); }
        }
        onsets.AddRange(new SoundOnset[] { new(20, 110, 0.5, 20), new(20, 111, 0.5, 20), new(26, 110, 0.5, 20), new(26, 111, 0.5, 20) });
        Video video = new(X, Y, Pixel, Pixel, Frames(30, 4), onsets, chords: 2);
        List<Entity> entities = video.Entities.ToList();
        LevelCruncher.OptimizeTriggers(entities, video.DecoderTriggers);
        HashSet<Entity> kept = entities.ToHashSet();
        // Chords: relays whose targets are all sound triggers or chords, found from the sounds outward (the frame
        // counter's relays loop back on themselves, so this can't be a recursive test).
        HashSet<ActivateTrigger> chordRelays = new(ReferenceEqualityComparer.Instance);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (ActivateTrigger a in entities.OfType<ActivateTrigger>())
            {
                if (!chordRelays.Contains(a) && a.Targets.Count > 0
                    && a.Targets.All(x => x.Targeted is SoundTrigger || x.Targeted is ActivateTrigger inner && chordRelays.Contains(inner)))
                {
                    chordRelays.Add(a);
                    grew = true;
                }
            }
        }
        bool SoundsOnly(Trigger t) => t is ActivateTrigger a && chordRelays.Contains(a);
        int chords = 0;
        List<(int, double)> Played(Trigger from)
        {
            // no sound or chord listed twice (a frame does list decoder commands more than once, on purpose)
            var soundTargets = from.Targets.Select(t => t.Targeted).Where(t => t is SoundTrigger || t is ActivateTrigger a && chordRelays.Contains(a)).ToList();
            Assert.Equal(soundTargets.Count, soundTargets.Distinct(ReferenceEqualityComparer.Instance).Count());
            List<(int, double)> played = new();
            foreach (Target target in from.Targets)
            {
                if (target.Targeted is SoundTrigger sound) { Assert.Contains(sound, kept); played.Add(((int)sound.Sound, sound.Volume)); }
                else if (target.Targeted is ActivateTrigger relay && SoundsOnly(relay)) { Assert.Contains(relay, kept); chords++; played.AddRange(Played(relay)); }
            }
            return played;
        }
        for (int f = 0; f < 30; f++)
        {
            var expected = onsets.Where(o => o.Frame == f).Select(o => (o.SoundId, o.Volume)).Order().ToList();
            Assert.Equal(expected, Played(video.Counter.Value[f][0]).Order().ToList());
        }
        Assert.True(chords > 0, "no frame played through a chord");
    }

    /// <summary>Renumbering moves the most used triggers first and keeps every arm pointing at the same trigger.</summary>
    [Fact]
    public void OrderTriggersByUse_PutsTheMostTargetedFirst_AndKeepsEveryTarget()
    {
        string xml = "<levelXML><triggers>"
            + "<t b=\"5\" n=\"A\"><t i=\"2\"/><t i=\"1\"><a i=\"2\"/></t><sh i=\"0\"/></t>"
            + "<t b=\"5\" n=\"B\"><t i=\"2\"/></t>"
            + "<t b=\"5\" n=\"C\"/>"
            + "<t b=\"3\" n=\"D\"><t i=\"2\"/><t i=\"1\"/></t>"
            + "</triggers></levelXML>";
        XElement root = XElement.Parse(LevelCruncher.OrderTriggersByUse(xml));
        List<XElement> triggers = root.Element("triggers")!.Elements().ToList();
        Assert.Equal(new[] { "C", "B", "A", "D" }, triggers.Select(t => t.Attribute("n")!.Value));
        string Named(XElement target) => triggers[int.Parse(target.Attribute("i")!.Value)].Attribute("n")!.Value;
        Assert.Equal(new[] { "C", "B" }, triggers[2].Elements("t").Select(Named));
        Assert.Equal("2", triggers[2].Element("t")!.ElementsAfterSelf("t").First().Element("a")!.Attribute("i")!.Value);
        Assert.Equal("0", triggers[2].Element("sh")!.Attribute("i")!.Value);
        Assert.Equal(new[] { "C", "B" }, triggers[3].Elements("t").Select(Named));
    }

    /// <summary>Exports the video's level the way Video's remarks describe and plays its XML, calling check for every pixel of every frame.</summary>
    static void PlayExport(Video video, int frameCount, Action<int, int, int, double> check)
    {
        List<Entity> entities = video.Entities.ToList();
        LevelCruncher.OptimizeTriggers(entities, video.DecoderTriggers);
        Level level = new(entities.ToArray()) { Character = Character.HelicopterMan };
        string xml = LevelCruncher.OrderTriggersByUse(LevelCruncher.MinifyRelayTriggers(level.ToXML()));
        XElement root = XElement.Parse(xml);
        List<XElement> triggers = root.Element("triggers")!.Elements().ToList();
        Assert.Contains(triggers, t => t.Elements().GroupBy(e => (e.Name, e.Attribute("i")!.Value)).Any(g => g.Count() > 1));

        List<XElement> shapes = root.Element("shapes")!.Elements().ToList();
        double[] opacity = shapes.Select(s => double.Parse(s.Attribute("p10")?.Value ?? "100")).ToArray();
        bool[] enabled = triggers.Select(t => t.Attribute("sd")?.Value != "t").ToArray();
        void Fire(int index)
        {
            if (!enabled[index]) { return; }
            foreach (XElement target in triggers[index].Elements())
            {
                int i = int.Parse(target.Attribute("i")!.Value);
                XElement? action = target.Element("a");
                if (target.Name == "t")
                {
                    switch (action?.Attribute("i")?.Value ?? "0")
                    {
                        case "0": Fire(i); break;
                        case "1": enabled[i] = false; break;
                        case "2": enabled[i] = true; break;
                    }
                }
                else if (target.Name == "sh" && action?.Attribute("i")?.Value == "3")
                {
                    opacity[i] = double.Parse(action.Attribute("p0")?.Value ?? "100");
                }
            }
        }
        int clock = triggers.FindIndex(t => t.Attribute("r")?.Value == "4");
        Dictionary<(int, int), int> pixelShape = new();
        for (int s = 0; s < shapes.Count; s++)
        {
            double px = (double.Parse(shapes[s].Attribute("p0")!.Value) - X) / Pixel, py = (double.Parse(shapes[s].Attribute("p1")!.Value) - Y) / Pixel;
            if (shapes[s].Attribute("p8")?.Value == "16777215" && px == Math.Floor(px) && py == Math.Floor(py)) { pixelShape[((int)px, (int)py)] = s; }
        }
        Assert.Equal(W * H, pixelShape.Count);
        for (int f = 0; f < frameCount; f++)
        {
            Fire(clock);
            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < H; y++) { check(f, x, y, opacity[pixelShape[(x, y)]]); }
            }
        }
    }
}
