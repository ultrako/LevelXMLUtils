using System.Diagnostics;
using System.Text.Json;
using LevelXML;
namespace LevelXMLUtils.Playback;

/// <summary>A sound started on a video frame (tools/audio-mosaic writes these: a song made of the game's sounds).</summary>
/// <param name="Tie">onsets with the same tie play together wherever one of them plays (a repeated stretch of the song,
/// see tools/audio-mosaic/ties.py): they are one relay, and every frame playing them activates it</param>
public record SoundOnset(int Frame, int SoundId, double Volume, int? Tie = null);

/// <summary>
/// A video played by triggers at the game's 30 frames a second, with sound: a clock trigger fires every frame and steps a
/// <see cref="UnaryCounter"/>, and each value's relay plays its frame, through a <see cref="VideoDecoder"/> (black and white)
/// or a <see cref="GreyVideoDecoder"/> (grey levels), and starts its sounds.
/// </summary>
/// <remarks>
/// Run <see cref="LevelCruncher.OptimizeTriggers(IList{Entity}, IEnumerable{ActivateTrigger}?)"/> with
/// <see cref="DecoderTriggers"/> kept, then <see cref="LevelCruncher.MinifyRelayTriggers"/> and, last,
/// <see cref="LevelCruncher.OrderTriggersByUse"/> on the XML (the frames name the decoder's commands hundreds of thousands
/// of times, and a short number for the most used saves a lot). Choose the frames with tools/video-frames/frames.py: a
/// change costs bytes, so dropping flicker and capping the changes a frame both pay.
/// </remarks>
public class Video : TriggerContraption
{
	/// <summary>The frames shown (frame, then [x, y], true for white), for a black-and-white video.</summary>
	public bool[][,] Frames { get; } = Array.Empty<bool[,]>();
	public VideoDecoder? Decoder { get; }
	public GreyVideoDecoder? GreyDecoder { get; }
	public UnaryCounter Counter { get; private set; } = default!;
	/// <summary>The relay of each frame (its targets are independent: commands, sounds and the counter's own state).</summary>
	public IReadOnlyList<ActivateTrigger> FrameTriggers { get; private set; } = Array.Empty<ActivateTrigger>();
	/// <summary>Starts the video (connected to the level's start unless it was made with startOnLoad: false).</summary>
	public Input Start { get; private set; } = default!;
	/// <summary>Fired with the last frame.</summary>
	public Output End => Counter.Value[^1];
	/// <summary>The decoder's triggers, to keep out of LevelCruncher.OptimizeTriggers.</summary>
	public IEnumerable<ActivateTrigger> DecoderTriggers => Decoder?.Triggers ?? GreyDecoder?.Triggers ?? new List<ActivateTrigger>();

	/// <summary>A black-and-white video: pixel (0, 0) centred on (x, y).</summary>
	/// <param name="frames">[frame][x, y], true for white; the screen starts black</param>
	/// <param name="bands">the decoder's band count (null: its default)</param>
	/// <param name="groupRows">whether the decoder selects rows with the same changes together</param>
	/// <param name="maxStep">the decoder's largest row step command</param>
	/// <param name="chords">with a number, groups of sounds that at least that many frames start together play through one
	/// relay (<see cref="SoundChords"/>)</param>
	/// <param name="startOnLoad">false: the video waits for <see cref="Start"/> (a lock in front of it, say)</param>
	public Video(double x, double y, double pixelWidth, double pixelHeight, bool[][,] frames, IEnumerable<SoundOnset>? sounds = null,
		int? bands = null, bool groupRows = false, int maxStep = 4, int? chords = null, bool startOnLoad = true)
	{
		Frames = frames;
		int width = frames[0].GetLength(0), height = frames[0].GetLength(1);
		PixelScreen screen = new(x, y, pixelWidth, pixelHeight, width, height);
		List<Entity> player = Player(x, y, pixelWidth, pixelHeight, frames.Length, startOnLoad);
		VideoDecoder decoder = new(screen, width, height, x, y, bands, groupRows, maxStep);
		Decoder = decoder;
		bool[,] last = new bool[width, height];
		for (int f = 0; f < frames.Length; f++)
		{
			bool[,] changed = new bool[width, height];
			for (int px = 0; px < width; px++) { for (int py = 0; py < height; py++) { changed[px, py] = frames[f][px, py] != last[px, py]; } }
			decoder.Encode(Counter.Value[f], changed);
			last = frames[f];
		}
		IEnumerable<Entity> sound = Sounds(sounds, frames.Length, x, y, chords);
		Entities = decoder.Entities.Concat(sound).Concat(screen.Entities).Concat(player).ToList();
	}

	/// <summary>A video of grey levels: each pixel a white rectangle over black at one of <paramref name="levelCount"/> opacities.</summary>
	/// <param name="levels">[frame][x, y]: each pixel's level, 0 (black) to levelCount - 1 (white)</param>
	public Video(double x, double y, double pixelWidth, double pixelHeight, byte[][,] levels, int levelCount, IEnumerable<SoundOnset>? sounds = null,
		int? chords = null, bool startOnLoad = true)
	{
		int width = levels[0].GetLength(0), height = levels[0].GetLength(1);
		List<Entity> player = Player(x, y, pixelWidth, pixelHeight, levels.Length, startOnLoad);
		GreyVideoDecoder decoder = new(x, y, pixelWidth, pixelHeight, width, height, levelCount);
		GreyDecoder = decoder;
		byte[,] last = new byte[width, height];
		for (int f = 0; f < levels.Length; f++)
		{
			decoder.Encode(Counter.Value[f], last, levels[f]);
			last = levels[f];
		}
		IEnumerable<Entity> sound = Sounds(sounds, levels.Length, x, y, chords);
		Entities = decoder.Entities.Concat(sound).Concat(player).ToList();
	}

	/// <summary>Frames from a file of one byte a pixel (0 black, anything else white), frame by frame and row by row, as
	/// tools/video-frames/frames.py write makes them.</summary>
	public static bool[][,] ReadFrames(string path, int width, int height) =>
		ReadLevels(path, width, height).Select(frame =>
		{
			bool[,] white = new bool[width, height];
			for (int px = 0; px < width; px++) { for (int py = 0; py < height; py++) { white[px, py] = frame[px, py] != 0; } }
			return white;
		}).ToArray();

	/// <summary>Grey frames from a file of one byte a pixel (its level), as frames.py writegrey makes them.</summary>
	public static byte[][,] ReadLevels(string path, int width, int height)
	{
		byte[] bytes = File.ReadAllBytes(path);
		if (bytes.Length % (width * height) != 0) { throw new ArgumentException($"{path} is not a whole number of {width}x{height} frames"); }
		return Enumerable.Range(0, bytes.Length / (width * height)).Select(f =>
		{
			byte[,] frame = new byte[width, height];
			for (int py = 0; py < height; py++) { for (int px = 0; px < width; px++) { frame[px, py] = bytes[(f * height + py) * width + px]; } }
			return frame;
		}).ToArray();
	}

	/// <summary>Frames straight from a video file with ffmpeg, at 30 a second, scaled and each pixel thresholded at half
	/// grey (frames.py chooses better frames, and smaller ones).</summary>
	public static bool[][,] ReadVideo(string path, int width, int height)
	{
		ProcessStartInfo start = new("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (string arg in new[] { "-v", "error", "-i", path, "-vf", $"fps=30,scale={width}:{height},format=gray", "-f", "rawvideo", "-" }) { start.ArgumentList.Add(arg); }
		using Process ffmpeg = Process.Start(start)!;
		using MemoryStream raw = new();
		Task<string> errors = ffmpeg.StandardError.ReadToEndAsync();
		ffmpeg.StandardOutput.BaseStream.CopyTo(raw);
		ffmpeg.WaitForExit();
		if (ffmpeg.ExitCode != 0) { throw new InvalidOperationException($"ffmpeg failed: {errors.Result}"); }
		byte[] bytes = raw.ToArray();
		return Enumerable.Range(0, bytes.Length / (width * height)).Select(f =>
		{
			bool[,] frame = new bool[width, height];
			for (int py = 0; py < height; py++) { for (int px = 0; px < width; px++) { frame[px, py] = bytes[(f * height + py) * width + px] > 127; } }
			return frame;
		}).ToArray();
	}

	/// <summary>The onsets tools/audio-mosaic/mosaic.py writes (--events): a JSON list of {frame, id, volume, copies, tie}; an
	/// onset with copies > 1 plays that many copies of its sound at once, to be louder than volume 1.</summary>
	public static List<SoundOnset> ReadOnsets(string path) =>
		JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray()
			.SelectMany(e => Enumerable.Repeat(new SoundOnset(e.GetProperty("frame").GetInt32(), e.GetProperty("id").GetInt32(), e.GetProperty("volume").GetDouble(),
				e.TryGetProperty("tie", out var tie) ? tie.GetInt32() : null), e.TryGetProperty("copies", out var copies) ? copies.GetInt32() : 1))
			.ToList();

	// The clock that plays the frames: a trigger that fires every frame once started, stepping the counter (whose values are
	// the frames' relays). At load a tiny falling square touches the start trigger, which deletes it and starts the clock.
	private List<Entity> Player(double x, double y, double pixelWidth, double pixelHeight, int frameCount, bool startOnLoad)
	{
		EveryFrameTrigger clock = new(x - pixelWidth, y, pixelWidth, pixelHeight) { TriggeredBy = TriggeredBy.Triggers };
		ActivateTrigger startOfLevel = new() { X = x, Y = y, Width = 20, Height = 20, TriggeredBy = TriggeredBy.Any, RepeatType = RepeatType.Multiple };
		Rectangle fallingSquare = new() { X = x, Y = y, Width = 5, Height = 5, Fixed = false, Density = 0.1 };
		Output started = new(new[] { startOfLevel });
		started.Connect(new Input { new Target<Shape>(fallingSquare, new DeleteSelf<Shape>()) });
		Start = new Input { new Target<Trigger>(clock, new Activate()) };
		if (startOnLoad) { started.Connect(Start); }
		Counter = new(frameCount);
		new Output(new[] { clock }).Connect(Counter.Increment);
		FrameTriggers = Counter.Value.SelectMany(o => o).ToList();
		return new Entity[] { clock, startOfLevel, fallingSquare }.Concat(Counter.Entities).ToList();
	}

	// The sound triggers, connected to the frames that start them, the most used first (a trigger target is written as the
	// trigger's index, so the most targeted get short ones)
	private IEnumerable<Entity> Sounds(IEnumerable<SoundOnset>? sounds, int frameCount, double x, double y, int? chords)
	{
		// One sound trigger per (sound, volume), fired by every frame that starts it; the same sound started twice in one frame (to
		// play louder than volume 1) gets a second trigger (whether one sound trigger listed twice plays twice is untested)
		Dictionary<(int, double, int), Sound> triggers = new();
		Dictionary<Sound, int> uses = new();
		List<(Output From, IReadOnlyList<Sound> Sounds)> plays = new();
		void Play(Output from, IEnumerable<SoundOnset> onsets)
		{
			List<Sound> started = new();
			foreach (var same in onsets.GroupBy(o => (o.SoundId, o.Volume)))
			{
				int copy = 0;
				foreach (SoundOnset onset in same)
				{
					var key = (onset.SoundId, onset.Volume, copy++);
					if (!triggers.TryGetValue(key, out Sound? sound)) { triggers[key] = sound = new Sound(onset.SoundId, x, y, onset.Volume); }
					uses[sound] = uses.GetValueOrDefault(sound) + 1;
					started.Add(sound);
				}
			}
			plays.Add((from, started));
		}
		// A tie's sounds are one relay, made from the tie's first frame; a frame whose tied onsets differ from that plays its own
		// (it shouldn't happen: the mosaic gives every copy the same sounds and volumes)
		Dictionary<int, (string Sounds, Relay Relay)> ties = new();
		List<Entity> relays = new();
		static string Key(IEnumerable<SoundOnset> onsets) => string.Join(";", onsets.Select(o => $"{o.SoundId}@{o.Volume}").Order());
		foreach (var frameOnsets in (sounds ?? Enumerable.Empty<SoundOnset>()).Where(o => o.Frame < frameCount).GroupBy(o => o.Frame))
		{
			Output frame = Counter.Value[frameOnsets.Key];
			List<SoundOnset> direct = frameOnsets.Where(o => o.Tie is null).ToList();
			foreach (var tie in frameOnsets.Where(o => o.Tie is not null).GroupBy(o => o.Tie!.Value))
			{
				string key = Key(tie);
				if (!ties.TryGetValue(tie.Key, out var made))
				{
					Relay relay = new(x, y);
					Play(relay.Output, tie);
					relays.AddRange(relay.Entities);
					ties[tie.Key] = made = (key, relay);
				}
				if (made.Sounds == key) { frame.Connect(made.Relay.Input); }
				else { direct.AddRange(tie); }
			}
			Play(frame, direct);
		}
		if (chords is int minUses) { relays.AddRange(SoundChords.Connect(plays, minUses, x, y)); }
		else { foreach (var (from, started) in plays) { foreach (Sound sound in started) { from.Connect(sound.Input); } } }
		return relays.Concat(uses.OrderByDescending(u => u.Value).SelectMany(u => u.Key.Entities)).ToList();
	}
}
