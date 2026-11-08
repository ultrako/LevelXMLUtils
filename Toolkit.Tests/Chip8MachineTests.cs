using LevelXML;
using Xunit;
using Xunit.Abstractions;
using LevelXMLUtils.Computer;
namespace LevelXMLUtils.Tests;

// The trigger-built CHIP-8 against the reference interpreter, frame by frame with the same key presses and the same random source: every
// register, the delay timer, which instruction is next and every pixel must agree after every game frame.
public class Chip8MachineTests
{
	readonly ITestOutputHelper output;
	public Chip8MachineTests(ITestOutputHelper output) { this.output = output; }

	// Br8kout's options in the archive: tickrate 7 and no quirk flags, which in Octo means shifts take VY, FX55/65 move I, sprites wrap,
	// no VF reset and no display wait
	public static readonly Chip8Quirks Br8koutQuirks = Chip8Quirks.Octo;
	// Super Pong (offstatic, Octojam 8, CC0): tickrate 30, every Octo quirk flag off. Key 6 serves, 7 and 9 slide the paddle.
	public static readonly Chip8Quirks SuperPongQuirks = Chip8Quirks.Octo;

	// Whether register r can still be read from where the reference is: live at its PC, or, just after a load that fetched only what the
	// next instruction's branch needs, live where that skip goes
	static bool LiveNow(Chip8Machine machine, Chip8 reference, int r, int[] nextNodes)
	{
		if (nextNodes.Length != 1) { return machine.IsLive(reference.PC, r); }
		int node = nextNodes[0], pc = reference.PC;
		if (machine.KnownAt(node, r) is not null) { return true; }
		if (!machine.SplitLoads.Contains(pc - 2)) { return machine.MachineHolds(node, r); }
		// just after a load that fetched only what the next instruction's branch needs: the skip's register, and what the way it takes needs
		int op = (reference.Memory[pc] << 8) | reference.Memory[pc + 1];
		bool equal = reference.V[(op >> 8) & 0xF] == (op & 0xFF), skips = equal == (op >> 12 == 0x3);
		return r == ((op >> 8) & 0xF) || machine.MachineHolds(machine.SuccessorNode(node, pc + (skips ? 4 : 2)), r);
	}

	sealed class Rig
	{
		public readonly Chip8Machine Machine;
		public readonly TriggerGraph Graph;
		readonly ActivateTrigger[] down = new ActivateTrigger[16], up = new ActivateTrigger[16];
		// crunched: run LevelCruncher.OptimizeTriggers first, as the level does. The key drivers are click triggers so the cruncher keeps them
		// as sources (it removes relays nothing activates).
		// reached: addresses whose Reached output gets a marker, a relay that disables itself when it fires (Reached(address) says whether it has)
		readonly Dictionary<int, Relay> markers = new();
		public Rig(byte[] program, Chip8Quirks quirks, int tickrate, bool crunched = false, IReadOnlyCollection<int>? reached = null, double gameSpeed = 1, int heavyBullets = 0,
			bool persistence = true)
		{
			Machine = new Chip8Machine(program, quirks, tickrate, 0, 0, 14, ram: Chip8Machine.MeasureWrites(program, quirks, tickrate), reached: reached, gameSpeed: gameSpeed,
				heavyBullets: heavyBullets, persistence: persistence);
			List<Entity> entities = Machine.Entities.ToList();
			foreach (var (address, output) in Machine.Reached)
			{
				Relay marker = new(0, 0, 5, 5);
				marker.Output.Connect(marker.Disable);
				output.Connect(marker.Input);
				markers[address] = marker;
				entities.AddRange(marker.Entities);
			}
			for (int i = 0; i < 16; i++)
			{
				down[i] = new ActivateTrigger { TriggeredBy = TriggeredBy.Click, RepeatType = RepeatType.Multiple };
				up[i] = new ActivateTrigger { TriggeredBy = TriggeredBy.Click, RepeatType = RepeatType.Multiple };
				new Output(new[] { down[i] }).Connect(Machine.KeyDown[i]);
				new Output(new[] { up[i] }).Connect(Machine.KeyUp[i]);
				entities.Add(down[i]); entities.Add(up[i]);
			}
			// The frame's parts that aren't steps (its start, the speed ring, the second tick's decision) get a click trigger too, so the optimizer
			// keeps them as relays of their own and a test can pulse a frame part by part (FrameParts)
			foreach (var part in Machine.FrameParts.Where(p => !p.Step))
			{
				ActivateTrigger driver = new() { TriggeredBy = TriggeredBy.Click, RepeatType = RepeatType.Multiple };
				foreach (Target target in part.Input) { driver.AddTarget(target); }
				entities.Add(driver);
			}
			// What starts the clock in a level (SeatedControls.Start); without it the optimizer would remove the clock as never activated
			ActivateTrigger start = new() { TriggeredBy = TriggeredBy.Click, RepeatType = RepeatType.Once };
			new Output(new[] { start }).Connect(Machine.Start);
			entities.Add(start);
			if (crunched) { LevelCruncher.OptimizeTriggers(entities); }
			Graph = TriggerGraph.FromTriggers(entities.OfType<ActivateTrigger>());
		}
		public bool Reached(int address) => !Graph.IsEnabled(TriggerGraph.IdFor(markers[address].Output.Single()));
		public void Key(int key, bool held) => Graph.Pulse(TriggerGraph.IdFor((held ? down : up)[key]));
		public void Step() => Graph.Pulse(TriggerGraph.IdFor(Machine.Dispatch.Output.Single()));
		public int Frame()
		{
			Graph.Pulse(TriggerGraph.IdFor(Machine.FrameClock));
			Assert.Empty(Graph.Reentered);
			return Graph.ActionsCarriedOut;
		}
		// A toggle's bit is held by two triggers enabled together; read the enabler (its On output), which every read probes. The other one
		// only fires when the toggle is counted, so the optimizer removes it from a register that is only ever loaded.
		bool On(Output on) => Graph.IsEnabled(TriggerGraph.IdFor(on[0]));
		public int Value(InstantToggleChain register) => Enumerable.Range(0, 8).Sum(k => On(register.Ons[k]) ? 1 << k : 0);
		public bool Pixel(int x, int y) => On(Machine.Pixels[x, y].On);
		public int[] NextNodes => Machine.Next.Where(kv => Graph.IsEnabled(TriggerGraph.IdFor(kv.Value.Output.Single()))).Select(kv => kv.Key).ToArray();
		public int[] NextEnabled => NextNodes.Select(Machine.AddressOf).ToArray();
		// What register r holds as far as the program can tell: the value the next node knows it has (the machine skips writing a register
		// nothing reads before it is written again), or the register itself
		public int Register(int r) => NextNodes.Length == 1 && Machine.KnownAt(NextNodes[0], r) is int known ? known : Value(Machine.V[r]);
	}

	// XOR, the shifts and a jump table (BNNN) on awkward values and registers (X = Y, X or Y being VF), one instruction at a time against the
	// reference, under both settings of the quirks they depend on
	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void XorShiftsAndJumpTables_MatchTheReference(bool shiftUsesVY, bool logicResetsVF)
	{
		var ops = new List<ushort>();
		int[] values = { 0x00, 0x01, 0x80, 0xFF, 0x5A, 0xA5, 0x3C };
		(int x, int y)[] pairs = { (1, 2), (3, 3), (0xF, 4), (5, 0xF), (6, 7) };
		foreach ((int x, int y) in pairs)
		{
			foreach (int vx in values)
			{
				foreach (int op in new[] { 0x3, 0x6, 0xE })
				{
					int vy = values[(vx + op) % values.Length];
					ops.Add((ushort)(0x6000 | (x << 8) | vx)); ops.Add((ushort)(0x6000 | (y << 8) | vy));
					ops.Add((ushort)(0x8000 | (x << 8) | (y << 4) | op));
				}
			}
		}
		// A jump table of three entries, entered with V0 = 4: entry 2 sets V5 = 0x33 and jumps to the end
		int tableAt = 0x200 + 2 * (ops.Count + 3), targetsAt = tableAt + 6, endAt = targetsAt + 12;
		ops.Add(0x6004); ops.Add((ushort)(0xB000 | tableAt)); ops.Add(0x0000);
		for (int i = 0; i < 3; i++) { ops.Add((ushort)(0x1000 | (targetsAt + 4 * i))); }
		for (int i = 0; i < 3; i++) { ops.Add((ushort)(0x6511 + 0x11 * i)); ops.Add((ushort)(0x1000 | endAt)); }
		ops.Add((ushort)(0x1000 | endAt));
		byte[] program = ops.SelectMany(op => new[] { (byte)(op >> 8), (byte)op }).ToArray();
		Chip8Quirks quirks = Chip8Quirks.Octo with { ShiftUsesVY = shiftUsesVY, LogicResetsVF = logicResetsVF };
		Rig rig = new(program, quirks, 7);
		Chip8 reference = new(program, quirks);
		while (reference.PC != endAt)
		{
			int pc = reference.PC, op = (reference.Memory[pc] << 8) | reference.Memory[pc + 1];
			reference.Step(); rig.Step();
			Assert.Null(reference.Halted);
			for (int r = 0; r < 16; r++) { if (!LiveNow(rig.Machine, reference, r, rig.NextNodes)) { continue; } Assert.True(reference.V[r] == rig.Register(r), $"{op:X4} at {pc:X3}: V{r:X} is {rig.Register(r)}, the interpreter has {reference.V[r]}"); }
			Assert.True(new[] { (int)reference.PC }.SequenceEqual(rig.NextEnabled), $"{op:X4} at {pc:X3}: next is {string.Join(",", rig.NextEnabled.Select(a => a.ToString("X3")))}, the interpreter has {reference.PC:X3}");
		}
		Assert.Equal(0x33, reference.V[5]);
	}

	// A load followed by a skip on its first register loads that one first and then only what the branch taken needs (the shape of Danm8ku's
	// bullet loops), with each branch needing different registers, on records that take each branch
	[Theory]
	[InlineData(0xFF)]
	[InlineData(0x12)]
	public void BranchAwareLoads_MatchTheReference(int first)
	{
		ushort[] ops =
		{
			0xA300, 0xF765, 0x30FF, 0x1210, // 200: I = 300, load V0-V7, skip if V0 = FF, else jump to 210
			0x8030, 0x8034, 0x120C, 0x0000, // 208: V0 = V3, V0 += V3, halt (jump to self at 20C)
			0x8010, 0x8024, 0x8054, 0x8064, 0x8074, 0x8044, 0x121C, 0x0000, // 210: uses V1, V2, V4-V7, not V3; halt at 21C
		};
		ops[6] = 0x120C; ops[14] = 0x121C;
		var bytes = new byte[0x110];
		for (int i = 0; i < ops.Length; i++) { bytes[2 * i] = (byte)(ops[i] >> 8); bytes[2 * i + 1] = (byte)ops[i]; }
		byte[] record = { (byte)first, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };
		record.CopyTo(bytes, 0x100);
		Chip8Quirks quirks = Chip8Quirks.Octo with { FuseTimerWaits = true };
		Rig rig = new(bytes, quirks, 7);
		Chip8 reference = new(bytes, quirks);
		for (int i = 0; i < 12; i++)
		{
			int pc = reference.PC, op = (reference.Memory[pc] << 8) | reference.Memory[pc + 1];
			reference.Step(); rig.Step();
			Assert.Empty(rig.Graph.Reentered);
			for (int r = 0; r < 16; r++) { if (!LiveNow(rig.Machine, reference, r, rig.NextNodes)) { continue; } Assert.True(reference.V[r] == rig.Register(r), $"{op:X4} at {pc:X3}: V{r:X} is {rig.Register(r)}, the interpreter has {reference.V[r]}"); }
			Assert.True(new[] { (int)reference.PC }.SequenceEqual(rig.NextEnabled), $"{op:X4} at {pc:X3}: next is {string.Join(",", rig.NextEnabled.Select(a => a.ToString("X3")))}, the interpreter has {reference.PC:X3}");
		}
	}

	// The same check one instruction at a time, which names the first instruction that goes wrong
	[Fact]
	public void Br8kout_MatchesTheReferenceInterpreter_EveryInstruction()
	{
		byte[] program = Chip8Programs.Read("br8kout");
		Rig rig = new(program, Br8koutQuirks, 7);
		Chip8Random random = new();
		Chip8 reference = new(program, Br8koutQuirks) { RandomSource = random.Next };
		for (int i = 0; i < 3000; i++)
		{
			int pc = reference.PC, op = (reference.Memory[pc] << 8) | reference.Memory[pc + 1];
			reference.Step(); rig.Step();
			Assert.Empty(rig.Graph.Reentered);
			string at = $"instruction {i}, {op:X4} at {pc:X3}";
			for (int r = 0; r < 16; r++) { if (!LiveNow(rig.Machine, reference, r, rig.NextNodes)) { continue; } Assert.True(reference.V[r] == rig.Register(r), $"{at}: V{r:X} is {rig.Register(r)}, the interpreter has {reference.V[r]}"); }
			Assert.True(new[] { (int)reference.PC }.SequenceEqual(rig.NextEnabled), $"{at}: next is {string.Join(",", rig.NextEnabled.Select(a => a.ToString("X3")))}, the interpreter has {reference.PC:X3}");
			for (int x = 0; x < Chip8.Width; x++) { for (int y = 0; y < Chip8.Height; y++) { Assert.True(reference.Display[x, y] == rig.Pixel(x, y), $"{at}: pixel ({x}, {y})"); } }
		}
	}

	// Also run after the level's trigger optimizer, which is what the exported level is. The keys: a random one of the game's keys held for a
	// third of a second, or none, with each release also told to the interpreter (Octo's FX0A waits for one).
	// Not a check: one act_bullets (and draw_bullets) step in a busy wave 2 tick, its actions by part and its busiest triggers
	[SlowFact]
	public void Danm8ku_OneBulletLoop()
	{
		byte[] program = Chip8Programs.Read("danm8ku");
		program[0x4E7 - 0x200] = 2;
		program[0x522 - 0x200] = 0x15; program[0x523 - 0x200] = 0x24;
		Rig rig = new(program, Chip8Quirks.Danm8kuFused, 1000, crunched: true, persistence: false);
		var tagOf = rig.Machine.Tags.ToDictionary(kv => TriggerGraph.IdFor(kv.Key), kv => kv.Value);
		var triggerOf = rig.Machine.Tags.Keys.ToDictionary(t => TriggerGraph.IdFor(t));
		var parts = rig.Machine.FrameParts.Select(part => (ids: part.Input.Select(t => TriggerGraph.IdFor((ActivateTrigger)t.Targeted)).ToArray(), part.Step)).ToList();
		int best = 0;
		for (int frame = 1; frame <= 900; frame++)
		{
			if (frame % 20 == 0) { rig.Key(5, true); }
			if (frame % 20 == 10) { rig.Key(5, false); }
			foreach (var (ids, step) in parts)
			{
				int[] live = step ? rig.NextEnabled : Array.Empty<int>();
				rig.Graph.Pulse(ids);
				if (live.Length == 1 && live[0] == 0x446 && rig.Graph.ActionsCarriedOut > best)
				{
					best = rig.Graph.ActionsCarriedOut;
					output.WriteLine($"frame {frame}:");
					var byPart = rig.Graph.ActionsBySource.GroupBy(kv => tagOf.GetValueOrDefault(kv.Key, "outside")).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
					output.WriteLine($"{live[0]:X3}: {rig.Graph.ActionsCarriedOut} actions: " + string.Join(", ", byPart.OrderByDescending(kv => kv.Value).Take(14).Select(kv => $"{kv.Key} {kv.Value}")));
					foreach (var (id, count) in rig.Graph.ActionsBySource.OrderByDescending(kv => kv.Value).Take(25))
					{
						string targets = triggerOf.TryGetValue(id, out var t) ? string.Join(", ", t.Targets.Where(x => x.Targeted is ActivateTrigger).GroupBy(x => (tagOf.GetValueOrDefault(TriggerGraph.IdFor((ActivateTrigger)x.Targeted), "outside"), x.Actions.FirstOrDefault()?.GetType().Name)).Select(g => $"{g.Count()} {g.Key.Item2} {g.Key.Item1}")) : "?";
						output.WriteLine($"    {count} {tagOf.GetValueOrDefault(id)}: {targets}");
					}
				}
			}
		}
	}

	// Winning Danm8ku fires the machine's Reached output for youwin (0x711, which the level connects to the game's victory), in the frame the
	// reference gets there and not before: started past the last wave (pattern_state 16, the patterns table's ninth jump, to youwin)
	[Fact]
	public void Danm8ku_Win_FiresReached()
	{
		byte[] program = Chip8Programs.Read("danm8ku");
		program[0x4E7 - 0x200] = 16;
		Rig rig = new(program, Chip8Quirks.Danm8kuFused, 1000, reached: new[] { 0x711 });
		Chip8 reference = new(program, Chip8Quirks.Danm8kuFused);
		Chip8Random random = new();
		reference.RandomSource = random.Next;
		int wonAt = -1;
		for (int frame = 1; frame <= 600 && wonAt < 0; frame++)
		{
			// any key starts the game from the title
			if (frame % 20 == 0) { rig.Key(5, true); reference.Keys[5] = true; }
			if (frame % 20 == 10) { rig.Key(5, false); reference.Keys[5] = false; reference.ReleaseKey(5); }
			random.Frame();
			rig.Frame();
			reference.RunFrame(1000); reference.RunFrame(1000);
			bool referenceWon = reference.Executed.Contains(0x711);
			Assert.Equal(referenceWon, rig.Reached(0x711));
			if (referenceWon) { wonAt = frame; }
		}
		Assert.True(wonAt > 0, "never reached youwin");
	}

	// A fused routine changes only how many instructions a game step takes: Danm8ku with its fused routines (Chip8Quirks.Danm8kuFused) is,
	// at every game tick (its sync
	// sets the delay timer at 0x494), in the same state as the original program: every register, all of memory and the screen. Keys and the
	// random numbers step per tick here, the same for both (by frames they would differ wherever the original's logic doesn't fit a frame).
	[Theory]
	[InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
	public void FusedRoutines_LeaveEveryGameTickTheSame(int wave) => FusedRoutinesCheck(wave);

	[SlowTheory]
	[InlineData(6)] [InlineData(7)] [InlineData(8)]
	public void FusedRoutines_LeaveEveryGameTickTheSame_LaterWaves(int wave) => FusedRoutinesCheck(wave);

	void FusedRoutinesCheck(int wave)
	{
		byte[] program = Chip8Programs.Read("danm8ku");
		program[0x4E7 - 0x200] = (byte)(2 * (wave - 1));
		// the player can't die (the jump to the lose screen at 0x522 goes on to act_bullets instead), so a run lasts
		Assert.Equal(0x1534, program[0x522 - 0x200] << 8 | program[0x523 - 0x200]);
		program[0x522 - 0x200] = 0x15; program[0x523 - 0x200] = 0x24;
		var states = new List<string>[2];
		long fusedRuns = 0;
		for (int f = 0; f < 2; f++)
		{
			Chip8Quirks quirks = f == 0 ? Chip8Quirks.Octo with { FuseTimerWaits = true } : Chip8Quirks.Danm8kuFused;
			Chip8 chip = new(program, quirks);
			Chip8Random random = new(); chip.RandomSource = random.Next;
			states[f] = new();
			Random keys = new(5);
			int[] gameKeys = { 5, 7, 8, 9 };
			int held = -1, ticks = 0;
			for (int frame = 0; frame < 20000 && ticks < 1500 && chip.Halted is null; frame++)
			{
				// the title screen by frames (any key, released, starts the game), then keys and random numbers by ticks
				if (ticks == 0 && frame == 10) { chip.Keys[5] = true; }
				if (ticks == 0 && frame == 20) { chip.Keys[5] = false; chip.ReleaseKey(5); }
				for (int half = 0; half < 2; half++)
				{
					for (int i = 0; i < 1000; i++)
					{
						if (chip.PC == 0x494)
						{
							ticks++;
							states[f].Add($"{string.Join(",", chip.V)} I{chip.I:X3} DT{chip.DelayTimer} {Convert.ToHexString(chip.Memory)} {chip.Screen()}");
							random.Frame();
							if (ticks % 20 == 0)
							{
								int next = keys.Next(3) == 0 ? -1 : gameKeys[keys.Next(gameKeys.Length)];
								if (held >= 0) { chip.Keys[held] = false; chip.ReleaseKey(held); }
								if (next >= 0) { chip.Keys[next] = true; }
								held = next;
							}
						}
						chip.Step();
						if (chip.WaitedForTimer) { break; }
					}
					chip.TickTimers();
				}
			}
			if (f == 1) { fusedRuns = Math.Min(chip.OpcodeCounts.GetValueOrDefault("fused draw_bullets"), chip.OpcodeCounts.GetValueOrDefault("fused act_bullets")); }
		}
		Assert.True(fusedRuns > 0, "the bullet loops were never fused");
		int count = Math.Min(states[0].Count, states[1].Count);
		Assert.True(count > 300, $"only {count} ticks");   // the later waves reach the win screen, which has no ticks
		for (int t = 0; t < count; t++) { Assert.True(states[0][t] == states[1][t], $"tick {t + 1} differs"); }
		output.WriteLine($"wave {wave}: {count} ticks the same, the bullet loops fused {fusedRuns} times each");
	}

	// Fused timer waits change nothing anyone can see: the full state (PC, I, registers, timers, screen) after every 60 Hz frame of a fused run
	// is the plain interpreter's, which is Octo's behaviour. The machine is checked against the fused interpreter.
	[Theory]
	[InlineData("br8kout", 7, 0)]
	[InlineData("superpong", 30, 0)]
	[InlineData("danm8ku", 1000, 1)]
	[InlineData("danm8ku", 1000, 3)]
	[InlineData("danm8ku", 1000, 4)]
	[InlineData("danm8ku", 1000, 8)]
	public void FusedTimerWaits_LeaveTheSameStateAsSpinning(string game, int tickrate, int wave)
	{
		byte[] program = Chip8Programs.Read($"{game}");
		if (wave > 0) { program[0x4E7 - 0x200] = (byte)(2 * (wave - 1)); }
		int[] gameKeys = game switch { "superpong" => new[] { 6, 7, 9 }, "danm8ku" => new[] { 5, 7, 8, 9 }, _ => new[] { 7, 9 } };
		Chip8[] chips = new bool[] { false, true }.Select(fuse => new Chip8(program, Chip8Quirks.Octo with { FuseTimerWaits = fuse })).ToArray();
		Chip8Random[] randoms = chips.Select(chip => { Chip8Random random = new(); chip.RandomSource = random.Next; return random; }).ToArray();
		Random keys = new(4);
		int held = -1;
		for (int frame = 1; frame <= 3600; frame++)
		{
			if (frame % 20 == 0)
			{
				int next = keys.Next(3) == 0 ? -1 : gameKeys[keys.Next(gameKeys.Length)];
				foreach (Chip8 chip in chips)
				{
					if (held >= 0) { chip.Keys[held] = false; chip.ReleaseKey(held); }
					if (next >= 0) { chip.Keys[next] = true; }
				}
				held = next;
			}
			if (frame % 2 == 1) { foreach (Chip8Random random in randoms) { random.Frame(); } }
			string[] state = chips.Select(chip => { chip.RunFrame(tickrate); return $"{chip.PC:X3} {chip.I:X3} {string.Join(",", chip.V)} {chip.DelayTimer} {chip.SoundTimer}\n{chip.Screen()}"; }).ToArray();
			Assert.True(state[0] == state[1], $"frame {frame}: spinning gives\n{state[0]}\nfused gives\n{state[1]}");
		}
		Assert.True(game == "br8kout" || chips[1].OpcodeCounts.GetValueOrDefault("fused timer wait") > 0, "no wait was fused");
	}

	[Theory]
	[InlineData("br8kout", 7, false)]
	[InlineData("br8kout", 7, true)]
	[InlineData("superpong", 30, false)]
	[InlineData("superpong", 30, true)]
	[InlineData("danm8ku-fused", 1000, true, 600)]
	public void Game_MatchesTheReferenceInterpreter_EveryFrame(string game, int tickrate, bool crunched, int frames = 1200, double gameSpeed = 1, int heavyBullets = 0) =>
		GameCheck(game, tickrate, crunched, frames, gameSpeed, heavyBullets);

	// The rest of Danm8ku: each wave, unfused and fused, and at lower speeds (20 to 45 seconds each)
	[SlowTheory]
	[InlineData("danm8ku", 1000, true, 600)]
	// Wave 3 stores and loads its own state (bcd into three bytes, then a table lookup) and was broken while RAM came only from a fuzzed run,
	// which never reaches it: started there by patching the start of a game to set pattern_state (0x2AD; wave n is 2(n - 1)) to wave 3: 6000 at 0x4E6 becomes 6004
	[InlineData("danm8ku-wave3", 1000, true, 600)]
	[InlineData("danm8ku-wave4", 1000, true, 600)]
	[InlineData("danm8ku-wave5", 1000, true, 600)]
	[InlineData("danm8ku-wave6", 1000, true, 600)]
	[InlineData("danm8ku-wave7", 1000, true, 600)]
	[InlineData("danm8ku-wave8", 1000, true, 600)]
	[InlineData("danm8ku-wave3-fused", 1000, true, 600)]
	[InlineData("danm8ku-wave6-fused", 1000, true, 600)]
	[InlineData("danm8ku-wave8-fused", 1000, true, 600)]
	// A bullet whose acceleration takes vx to exactly 255 isn't saved by act_bullets and stays where it was (the native loop moves bullets in
	// place, so it undoes the move): wave 4's first bullet starts at vx 247 (6096, vx := 150, at 0x635 becomes 60F7) with ax 8
	[InlineData("danm8ku-wave4-frozen-fused", 1000, true, 600)]
	// at 0.9 of full speed (a frame in five runs one tick, not two)
	[InlineData("danm8ku-wave2-fused", 1000, true, 600, 0.9)]
	[InlineData("danm8ku-wave6-fused", 1000, true, 600, 0.5)]
	[InlineData("danm8ku-wave8-fused", 1000, true, 600, 0.75)]
	// a frame skips its second tick when its first one moved 12 bullets or more (low enough that wave 2 does it often)
	[InlineData("danm8ku-wave2-fused", 1000, true, 600, 1.0, 12)]
	[InlineData("danm8ku-wave5-fused", 1000, true, 600, 0.9, 12)]
	public void Game_MatchesTheReferenceInterpreter_EveryFrame_AllOfDanm8ku(string game, int tickrate, bool crunched, int frames = 1200, double gameSpeed = 1, int heavyBullets = 0) =>
		GameCheck(game, tickrate, crunched, frames, gameSpeed, heavyBullets);

	void GameCheck(string game, int tickrate, bool crunched, int frames, double gameSpeed, int heavyBullets)
	{
		// "-fused": with Danm8ku's move_bullet fused (Chip8Quirks.Danm8kuFused) in both the machine and the reference
		bool fused = game.EndsWith("-fused");
		string name = fused ? game[..^"-fused".Length] : game;
		bool frozen = name.EndsWith("-frozen");
		if (frozen) { name = name[..^"-frozen".Length]; }
		byte[] program = Chip8Programs.Read($"{name.Split('-')[0]}");
		if (frozen)
		{
			Assert.Equal(0x6096, program[0x635 - 0x200] << 8 | program[0x636 - 0x200]);
			program[0x636 - 0x200] = 247;
		}
		if (name.StartsWith("danm8ku-wave"))
		{
			Assert.Equal(0x6000, program[0x4E6 - 0x200] << 8 | program[0x4E7 - 0x200]);
			program[0x4E7 - 0x200] = (byte)(2 * (int.Parse(name["danm8ku-wave".Length..]) - 1));
		}
		Chip8Quirks quirks = fused ? Chip8Quirks.Danm8kuFused : Chip8Quirks.Octo with { FuseTimerWaits = true };
		int[] gameKeys = game switch { "superpong" => new[] { 6, 7, 9 }, _ when game.StartsWith("danm8ku") => new[] { 5, 7, 8, 9, 6 }, _ => new[] { 7, 9 } };
		Rig rig = new(program, quirks, tickrate, crunched, gameSpeed: gameSpeed, heavyBullets: heavyBullets);
		// Every store's addresses are bounded by the range analysis except Danm8ku's bullet-table clear at 4F4 (save v7 in a loop of 32, which
		// the analysis of I alone can't bound), so RAM doesn't depend on what a measured run happened to reach; that loop runs at every start, so
		// the measured run covers it, and its table must be RAM
		if (game.StartsWith("danm8ku"))
		{
			Assert.True(rig.Machine.UnboundedStores.SequenceEqual(new[] { 0x4F4 }), "stores the range analysis can't bound: " + string.Join("; ", rig.Machine.UnboundedStores.Select(a => $"{rig.Machine.OpcodeAt(a):X4} at {a:X3}, I {(rig.Machine.RangeOfIAt(a) is var (lo, hi) ? $"{lo:X3}-{hi:X3}" : "unknown")}")));
			Assert.True(Enumerable.Range(0xBB8, 256).All(rig.Machine.Ram.Contains), "the bullet table isn't all RAM");
		}
		Chip8 reference = new(program, quirks);
		// the bullets act_bullets moves in a frame's first tick (the machine skips the second tick from heavyBullets on)
		int load = 0;
		bool firstTick = false;
		reference.FusedStarted = f =>
		{
			if (firstTick && f == FusedRoutine.Danm8kuActBullets) { load += Enumerable.Range(0, 32).Count(slot => reference.Memory[0xBB8 + 8 * slot] != 255); }
			if (firstTick && f == FusedRoutine.Danm8kuPewPew && reference.I >= 0xBB8 && reference.I < 0xBB8 + 256 && (reference.I - 0xBB8) % 8 == 0) { load += 4; }
		};
		// and 4 for each record stored into a slot through the machine's native store
		reference.Stepping = pc =>
		{
			if (firstTick && rig.Machine.RecordStoreSites.Contains(pc) && reference.I >= 0xBB8 && reference.I < 0xBB8 + 256 && (reference.I - 0xBB8) % 8 == 0) { load += 4; }
		};
		int skippedTicks = 0;
		int frame = 0;
		Chip8Random random = new();
		reference.RandomSource = random.Next;
		Random keys = new(3);
		int held = -1, longest = 0;
		var frameActions = new List<int>();
		long total = 0;
		string worst = "";
		var kinds = new Dictionary<string, long>();
		var parts = new Dictionary<string, long>();
		var keySources = new Dictionary<string, long>();
		var tagOf = rig.Machine.Tags.ToDictionary(kv => TriggerGraph.IdFor(kv.Key), kv => kv.Value);
		// Persistence: what each pixel's picture shows, followed from what fired (its On shows it, its second stage hides it)
		bool[,] shown = new bool[Chip8.Width, Chip8.Height], litBefore = new bool[Chip8.Width, Chip8.Height];
		var onIds = new string[Chip8.Width, Chip8.Height]; var hideIds = new string[Chip8.Width, Chip8.Height];
		for (int x = 0; x < Chip8.Width; x++)
		{
			for (int y = 0; y < Chip8.Height; y++) { onIds[x, y] = TriggerGraph.IdFor(rig.Machine.Pixels[x, y].On[0]); hideIds[x, y] = TriggerGraph.IdFor(rig.Machine.PixelHides![x, y].Output.Single()); }
		}
		for (frame = 1; frame <= frames; frame++)
		{
			if (frame % 10 == 0)
			{
				int next = keys.Next(gameKeys.Length + 1) is int pick && pick < gameKeys.Length ? gameKeys[pick] : -1;
				if (held >= 0) { rig.Key(held, false); reference.Keys[held] = false; reference.ReleaseKey(held); }
				if (next >= 0) { rig.Key(next, true); reference.Keys[next] = true; }
				held = next;
			}
			random.Frame();
			int actions = rig.Frame();
			foreach (var (kind, count) in rig.Graph.ActionsByKind) { kinds[kind] = kinds.GetValueOrDefault(kind) + count; }
			foreach (var (id, count) in rig.Graph.ActionsBySource) { string part = tagOf.GetValueOrDefault(id, "outside the machine"); parts[part] = parts.GetValueOrDefault(part) + count; }
			foreach (var (id, count) in rig.Graph.ActionsBySource) { if (tagOf.GetValueOrDefault(id) == "keys") { keySources[id] = keySources.GetValueOrDefault(id) + count; } }
			if (actions > longest && crunched)
			{
				var memory = rig.Machine.MemoryEntities.OfType<ActivateTrigger>().Select(TriggerGraph.IdFor).ToHashSet();
				var pixels = rig.Machine.Pixels.Cast<InstantToggle>().SelectMany(p => p.Entities).OfType<ActivateTrigger>().Select(TriggerGraph.IdFor).ToHashSet();
				var byId = rig.Machine.Entities.OfType<ActivateTrigger>().Distinct().ToDictionary(TriggerGraph.IdFor);
				var worstParts = new Dictionary<string, long>();
				foreach (var (id, count) in rig.Graph.ActionsBySource) { string part = tagOf.GetValueOrDefault(id, "outside the machine"); worstParts[part] = worstParts.GetValueOrDefault(part) + count; }
				worst = $"frame {frame} ({actions}), by part: " + string.Join(", ", worstParts.OrderByDescending(kv => kv.Value).Take(14).Select(kv => $"{kv.Key} {kv.Value}")) + $"; memory {rig.Graph.ActionsBySource.Where(kv => memory.Contains(kv.Key)).Sum(kv => kv.Value)}, pixels {rig.Graph.ActionsBySource.Where(kv => pixels.Contains(kv.Key)).Sum(kv => kv.Value)}; top: "
					+ string.Join("; ", rig.Graph.ActionsBySource.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Value} by a trigger of {byId.GetValueOrDefault(kv.Key)?.TargetCount} targets{(memory.Contains(kv.Key) ? " (memory)" : "")}"));
			}
			longest = Math.Max(longest, actions); total += actions; frameActions.Add(actions);
			load = 0; firstTick = true;
			reference.RunFrame(tickrate);
			firstTick = false;
			bool heavy = heavyBullets > 0 && load >= heavyBullets;
			if (heavy && Chip8Machine.SecondTickAt(frame - 1, gameSpeed)) { skippedTicks++; }
			if (Chip8Machine.SecondTickAt(frame - 1, gameSpeed) && !heavy) { reference.RunFrame(tickrate); }
			Assert.Null(reference.Halted);
			for (int x = 0; x < Chip8.Width; x++)
			{
				for (int y = 0; y < Chip8.Height; y++)
				{
					if (rig.Graph.LastFired.Contains(hideIds[x, y])) { shown[x, y] = false; }
					else if (rig.Graph.LastFired.Contains(onIds[x, y])) { shown[x, y] = true; }
					// lit at the end of the last frame, or at any moment during this one (Octo shows 60 frames a second and the level 30, so a
					// sprite drawn and erased within one game frame would otherwise never show)
					bool expected = litBefore[x, y] || reference.TurnedOn[x, y] || reference.Display[x, y];
					if (shown[x, y] != expected) { Assert.True(false, $"frame {frame}: pixel ({x}, {y})'s picture is {(shown[x, y] ? "shown" : "hidden")}, lit at the end of the last frame {litBefore[x, y]}, turned on during this one {reference.TurnedOn[x, y]}, lit now {reference.Display[x, y]}"); }
					litBefore[x, y] = reference.Display[x, y];
					reference.TurnedOn[x, y] = false;
				}
			}
			for (int r = 0; r < 16; r++) { if (!LiveNow(rig.Machine, reference, r, rig.NextNodes)) { continue; } Assert.True(reference.V[r] == rig.Register(r), $"frame {frame}: V{r:X} is {rig.Register(r)}, the interpreter has {reference.V[r]}"); }
			Assert.True(reference.DelayTimer == rig.Value(rig.Machine.DelayTimer), $"frame {frame}: delay timer {rig.Value(rig.Machine.DelayTimer)}, the interpreter has {reference.DelayTimer}");
			Assert.True(new[] { (int)reference.PC }.SequenceEqual(rig.NextEnabled), $"frame {frame}: next is {string.Join(",", rig.NextEnabled.Select(a => a.ToString("X3")))}, the interpreter has {reference.PC:X3}");
			for (int x = 0; x < Chip8.Width; x++)
			{
				for (int y = 0; y < Chip8.Height; y++)
				{
					if (reference.Display[x, y] != rig.Pixel(x, y)) { Assert.True(false, $"frame {frame}: pixel ({x}, {y}) is {rig.Pixel(x, y)}, the interpreter has {reference.Display[x, y]}\n{reference.Screen()}"); }
				}
			}
		}
		if (heavyBullets > 0) { output.WriteLine($"{game}: {skippedTicks} second ticks skipped for load"); Assert.True(skippedTicks > 0, "no frame was heavy enough to skip a tick"); }
		if (game == "danm8ku-wave3")
		{
			// The run reached wave 3's bcd store (digits100 to digits1), which a fuzzed run from the normal start never writes (so measured RAM
			// alone made those bytes ROM, and wave 3 fired one angle from one place and left bullets on the screen)
			Assert.True(new[] { 0x5CE, 0x5CF, 0x5D0 }.All(reference.Written.Contains), "the run never reached wave 3's store");
			Assert.False(new[] { 0x5CE, 0x5CF, 0x5D0 }.Any(Chip8Machine.MeasureWrites(Chip8Programs.Read("danm8ku"), quirks, tickrate).Contains));
		}
		int Percentile(int p) => frameActions.OrderBy(v => v).ElementAt(Math.Min(frameActions.Count - 1, frameActions.Count * p / 100));
		if (worst != "") { output.WriteLine("worst " + worst); }
		if (crunched) { output.WriteLine($"{game} keys' busiest: " + string.Join("; ", keySources.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Value / frames} a frame by a trigger of {rig.Machine.Tags.First(t => TriggerGraph.IdFor(t.Key) == kv.Key).Key.TargetCount} targets"))); }
		output.WriteLine($"{game} {(crunched ? "optimized" : "as built")} actions a frame {total / frames} ({longest} at most; {Percentile(50)} median, {Percentile(90)} at the 90th percentile, {Percentile(99)} at the 99th), by part: " + string.Join(", ", parts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / frames}")));
		output.WriteLine($"{game} {(crunched ? "optimized" : "as built")} actions a frame by kind: " + string.Join(", ", kinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / frames}")));
		output.WriteLine($"{game} {(crunched ? "optimized" : "as built")}: {reference.Instructions} instructions over {frames} frames; actions a frame: mean {total / frames}, most {longest}; {rig.Graph.NodeIds.Count} triggers, RAM {Chip8.Ranges(rig.Machine.Ram)}, {rig.Machine.Reachable.Count} instructions compiled; sprites {reference.SpritesDrawn}");
	}

	// Not a check: the heaviest gameplay frames of the fused Danm8ku (frames whose step ran act_bullets; the title, the table clear and the
	// win screen left out), per wave: the percentiles, and what the heaviest 1% spend their actions on, by address
	[SlowFact]
	public void Danm8ku_GameplaySpikes()
	{
		foreach (int wave in (Environment.GetEnvironmentVariable("CHIP8_SPIKE_WAVES") ?? "2,5,6,8").Split(',').Select(int.Parse))
		{
			byte[] program = Chip8Programs.Read("danm8ku");
			program[0x4E7 - 0x200] = (byte)(2 * (wave - 1));
			// CHIP8_SPIKE_INVINCIBLE=1: the player can't die (as the tick equivalence test), so a long run goes through the waves
			if (Environment.GetEnvironmentVariable("CHIP8_SPIKE_INVINCIBLE") == "1") { program[0x522 - 0x200] = 0x15; program[0x523 - 0x200] = 0x24; }
			Rig rig = new(program, Chip8Quirks.Danm8kuFused, 1000, crunched: true,
				gameSpeed: double.Parse(Environment.GetEnvironmentVariable("CHIP8_GAME_SPEED") ?? "1", System.Globalization.CultureInfo.InvariantCulture),
				heavyBullets: int.Parse(Environment.GetEnvironmentVariable("CHIP8_HEAVY") ?? "0"),
				// as the level (Danm8ku doesn't flicker in the archive, so it has no persistence)
				persistence: false);
			var parts = rig.Machine.FrameParts.Select(part => (ids: part.Input.Select(t => TriggerGraph.IdFor((ActivateTrigger)t.Targeted)).ToArray(), part.Step,
				gate: part.Gate is null ? null : TriggerGraph.IdFor(part.Gate), part.First, second: part.Second is null ? null : TriggerGraph.IdFor(part.Second))).ToList();
			long secondTicks = 0, frameCount = 0;
			bool whole = false;
			string? secondId = parts.Select(p => p.second).FirstOrDefault(id => id is not null);
			int[] gameKeys = { 5, 7, 8, 9, 6 };
			Random keys = new(3);
			int held = -1;
			var frames = new List<(int frame, long total, Dictionary<int, long> byAddress, Dictionary<string, long> byPart)>();
			var tagOf = rig.Machine.Tags.ToDictionary(kv => TriggerGraph.IdFor(kv.Key), kv => kv.Value);
			var triggerOf = rig.Machine.Tags.Keys.ToDictionary(t => TriggerGraph.IdFor(t));
			var byId = new Dictionary<string, long>();
			int spikeFrames = int.Parse(Environment.GetEnvironmentVariable("CHIP8_SPIKE_FRAMES") ?? "900");
			var allFrames = new List<(int frame, long total, Dictionary<int, long> byAddress)>();
			for (int frame = 1; frame <= spikeFrames; frame++)
			{
				if (frame % 10 == 0)
				{
					int next = keys.Next(gameKeys.Length + 1) is int pick && pick < gameKeys.Length ? gameKeys[pick] : -1;
					if (held >= 0) { rig.Key(held, false); }
					if (next >= 0) { rig.Key(next, true); }
					held = next;
				}
				long total = 0;
				var byAddress = new Dictionary<int, long>();
				var byPart = new Dictionary<string, long>();
				bool skipping = false;
				bool? secondOn = null;
				frameCount++;
				if (whole)
				{
					// whole frames (the optimizer folds the frame start and the second tick's decision into the clock, so its parts can't be
					// pulsed one by one): a gameplay frame is one where the bullet loops ran (their caches acted), short of the table clear
					total = rig.Frame();
					foreach (var (id, count) in rig.Graph.ActionsBySource) { string part = tagOf.GetValueOrDefault(id, "outside"); byPart[part] = byPart.GetValueOrDefault(part) + count; byId[id] = byId.GetValueOrDefault(id) + count; }
					if (secondId is null || rig.Graph.IsEnabled(secondId)) { secondTicks++; }
					if (byPart.GetValueOrDefault("bullet cache") > 0 && total < 300000) { frames.Add((frame, total, byAddress, byPart)); }
					continue;
				}
				foreach (var (ids, step, gate, first, second) in parts)
				{
					// a frame's second tick runs only if its relay is on when the game gets to it
					if (second is not null)
					{
						secondOn ??= rig.Graph.IsEnabled(second);
						if (secondOn == false) { continue; }
					}
					// a step group the game finds off when it fires it (a fused wait closed it) runs none of its steps
					if (gate is not null)
					{
						if (first) { skipping = !rig.Graph.IsEnabled(gate); }
						total++;
						if (skipping) { continue; }
					}
					int[] live = step ? rig.NextEnabled : Array.Empty<int>();
					rig.Graph.Pulse(ids);
					total += rig.Graph.ActionsCarriedOut;
					foreach (var (id, count) in rig.Graph.ActionsBySource) { string part = tagOf.GetValueOrDefault(id, "outside"); byPart[part] = byPart.GetValueOrDefault(part) + count; byId[id] = byId.GetValueOrDefault(id) + count; }
					if (step && live.Length == 1) { byAddress[live[0]] = byAddress.GetValueOrDefault(live[0]) + rig.Graph.ActionsCarriedOut; byAddress[~live[0]] = byAddress.GetValueOrDefault(~live[0]) + 1; }
				}
				if (secondOn != false) { secondTicks++; }
				allFrames.Add((frame, total, byAddress));
				bool gameplay = byAddress.ContainsKey(0x446) && !byAddress.ContainsKey(0x4F4) && !byAddress.Keys.Any(a => a >= 0x2CA && a < 0x320);
				if (gameplay) { frames.Add((frame, total, byAddress, byPart)); }
			}
			var sorted = frames.OrderBy(f => f.total).ToList();
			long At(double p) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))].total;
			if (Environment.GetEnvironmentVariable("CHIP8_SPIKE_ALL") == "1")
			{
				// the heaviest frames of the whole run, gameplay or not, and what they ran
				foreach (var f in allFrames.Where(f => f.total > 80000 && f.total < 300000).OrderBy(f => f.frame).Take(60))
				{
					output.WriteLine($"  frame {f.frame}: {f.total}: " + string.Join(", ", f.byAddress.Where(kv => kv.Key >= 0).OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key:X3} {rig.Machine.OpcodeAt(kv.Key):X4} {kv.Value}")));
				}
			}
			output.WriteLine($"wave {wave}: {secondTicks * 100 / frameCount}% of frames ran a second tick (game at {(frameCount + secondTicks) * 50 / frameCount}% speed)");
			output.WriteLine($"wave {wave}: {sorted.Count} gameplay frames; median {At(0.5)}, 90th {At(0.9)}, 99th {At(0.99)}, max {sorted[^1].total} (frame {sorted[^1].frame})");
			var heavy = sorted.Skip((int)(sorted.Count * 0.98)).ToList();
			var sum = new Dictionary<int, long>();
			foreach (var f in heavy) { foreach (var (a, c) in f.byAddress) { sum[a] = sum.GetValueOrDefault(a) + c; } }
			long all = heavy.Sum(f => f.total);
			string Each(int a) => sum.TryGetValue(~a, out long n) ? $" ({sum[a] / n} each)" : "";
			var partSum = new Dictionary<string, long>();
			foreach (var f in heavy) { foreach (var (p, c) in f.byPart) { partSum[p] = partSum.GetValueOrDefault(p) + c; } }
			output.WriteLine("  by part: " + string.Join(", ", partSum.OrderByDescending(kv => kv.Value).Take(24).Select(kv => $"{kv.Key} {kv.Value / heavy.Count}")));
			if (Environment.GetEnvironmentVariable("CHIP8_SPIKE_DETAIL") is string detail)
			{
				// the busiest triggers of all frames (gameplay or not) of the parts named, with how many targets each has
				foreach (var (id, count) in byId.Where(kv => detail.Split(',').Contains(tagOf.GetValueOrDefault(kv.Key, "outside"))).OrderByDescending(kv => kv.Value).Take(40))
				{
					string targets = triggerOf.TryGetValue(id, out var t)
						? string.Join(", ", t.Targets.Where(x => x.Targeted is ActivateTrigger).GroupBy(x => (tagOf.GetValueOrDefault(TriggerGraph.IdFor((ActivateTrigger)x.Targeted), "outside"), x.Actions.FirstOrDefault()?.GetType().Name)).Select(g => $"{g.Count()} {g.Key.Item2} {g.Key.Item1}"))
						: "?";
					output.WriteLine($"    {tagOf[id]} #{id}: {count / 900} a frame, enabled at start {!(t?.StartDisabled ?? false)}: {targets}");
				}
			}
			output.WriteLine($"  heaviest {heavy.Count} frames, {all / heavy.Count} each: " + string.Join(", ", sum.Where(kv => kv.Key >= 0).OrderByDescending(kv => kv.Value).Take(int.Parse(Environment.GetEnvironmentVariable("CHIP8_SPIKE_TOP") ?? "14")).Select(kv => $"{kv.Key:X3} {rig.Machine.OpcodeAt(kv.Key):X4} {kv.Value / heavy.Count}{Each(kv.Key)}")));
		}
	}

	// Not a check: which addresses of Danm8ku cost the most in real frames of a wave (CHIP8_PROFILE_WAVE, default 6; frames 200 to 400, keys
	// as in the lockstep test). Each frame is run part by part (FrameParts, which is what the frame clock fires), so every step's actions are
	// charged to the instruction it ran.
	[SlowFact]
	public void Danm8ku_CostPerAddress()
	{
		byte[] program = Chip8Programs.Read("danm8ku");
		int wave = int.Parse(Environment.GetEnvironmentVariable("CHIP8_PROFILE_WAVE") ?? "6");
		program[0x4E7 - 0x200] = (byte)(2 * (wave - 1));
		Chip8Quirks quirks = Chip8Quirks.Danm8kuFused;
		Rig rig = new(program, quirks, 1000);
		output.WriteLine($"{rig.Machine.NodeCount} nodes; {rig.Machine.PolyBlocked}; stack: {rig.Machine.StackHasLevelsBecause ?? "no levels"}");
		output.WriteLine("splits: " + string.Join(" ", rig.Machine.SplitMasks.Select(kv => $"{rig.Machine.AddressOf(kv.Key):X3}:{kv.Value.equal:X5}/{kv.Value.other:X5}")));
		output.WriteLine("copies: " + string.Join(" ", rig.Machine.NodeCopiesByAddress.Where(c => c.Copies > 1 || c.KnowI > 0).Select(c => $"{c.Address:X3}x{c.Copies}/I{c.KnowI}")));
		var parts = rig.Machine.FrameParts.Select(part => (ids: part.Input.Select(t => TriggerGraph.IdFor((ActivateTrigger)t.Targeted)).ToArray(), part.Step,
				gate: part.Gate is null ? null : TriggerGraph.IdFor(part.Gate), part.First)).ToList();
		int[] gameKeys = { 5, 7, 8, 9, 6 };
		Random keys = new(3);
		int held = -1;
		var cost = new Dictionary<int, (long count, long actions)>();
		var tagOf = rig.Machine.Tags.ToDictionary(kv => TriggerGraph.IdFor(kv.Key), kv => kv.Value);
		var parts2 = new Dictionary<string, long>();
		long all = 0, frames = 0;
		for (int frame = 1; frame <= 400; frame++)
		{
			if (frame % 10 == 0)
			{
				int next = keys.Next(gameKeys.Length + 1) is int pick && pick < gameKeys.Length ? gameKeys[pick] : -1;
				if (held >= 0) { rig.Key(held, false); }
				if (next >= 0) { rig.Key(next, true); }
				held = next;
			}
			if (frame < 200) { rig.Frame(); continue; }
			frames++;
			bool skipping = false;
			foreach (var (ids, step, gate, first) in parts)
			{
				if (gate is not null)
				{
					if (first) { skipping = !rig.Graph.IsEnabled(gate); }
					if (skipping) { continue; }
				}
				int[] live = step ? rig.NextEnabled : Array.Empty<int>();
				rig.Graph.Pulse(ids);
				if (step && live.Length == 1)
				{
					var entry = cost.GetValueOrDefault(live[0]);
					cost[live[0]] = (entry.count + 1, entry.actions + rig.Graph.ActionsCarriedOut);
				}
				all += rig.Graph.ActionsCarriedOut;
				foreach (var (id, count) in rig.Graph.ActionsBySource) { string part = tagOf.GetValueOrDefault(id, "outside the machine"); parts2[part] = parts2.GetValueOrDefault(part) + count; }
			}
		}
		output.WriteLine($"{all / frames} actions a frame, {cost.Values.Sum(c => c.count) / frames} steps with an instruction live");
		output.WriteLine("by part: " + string.Join(", ", parts2.OrderByDescending(kv => kv.Value).Take(25).Select(kv => $"{kv.Key} {kv.Value / frames}")));
		foreach (var (pc, (count, actions)) in cost.OrderByDescending(kv => kv.Value.actions))
		{
			output.WriteLine($"{pc:X3} {rig.Machine.OpcodeAt(pc):X4} {count / (double)frames,7:F1} a frame: {actions / count,6} each, {100.0 * actions / all,5:F1}%");
		}
	}

	// Not a check: what each kind of instruction costs in a busy stretch of Danm8ku (300 frames in, then 3,000 instructions one at a time),
	// split by the part of the machine that spent it
	[SlowFact]
	public void Danm8ku_CostPerInstruction()
	{
		byte[] program = Chip8Programs.Read("danm8ku");
		Chip8Quirks quirks = Chip8Quirks.Octo with { FuseTimerWaits = true };
		// as built: the optimizer folds away the dispatch relay a single step pulses
		Rig rig = new(program, quirks, 1000);
		output.WriteLine($"stack: {rig.Machine.StackHasLevelsBecause ?? "no levels"}");
		output.WriteLine("targets of each register's bit 0 On: " + string.Join(", ", Enumerable.Range(0, 16).Select(r => $"V{r:X} {rig.Machine.V[r].Ons[0].Single().TargetCount}")) + $", DT {rig.Machine.DelayTimer.Ons[0].Single().TargetCount}");
		foreach (var bank in rig.Machine.Banks) { output.WriteLine($"bank {bank.Start:X3}-{bank.End:X3}: {bank.End - bank.Start} rows"); }
		var tagOf = rig.Machine.Tags.ToDictionary(kv => TriggerGraph.IdFor(kv.Key), kv => kv.Value);
		for (int frame = 0; frame < 300; frame++)
		{
			// any key (released) starts the game; then the ship drifts left and right
			if (frame == 10) { rig.Key(5, true); }
			if (frame == 20) { rig.Key(5, false); }
			if (frame >= 30 && frame % 40 == 30) { rig.Key(frame % 80 == 30 ? 7 : 9, true); rig.Key(frame % 80 == 30 ? 9 : 7, false); }
			rig.Frame();
		}
		var cost = new Dictionary<string, (long count, long actions, Dictionary<string, long> parts)>();
		for (int i = 0; i < 3000; i++)
		{
			int pc = rig.NextEnabled.Single(), op = rig.Machine.OpcodeAt(pc);
			string kind = (op >> 12) switch { 0x8 => $"8XY{op & 0xF:X}", 0xF => $"FX{op & 0xFF:X2}", 0xE => $"EX{op & 0xFF:X2}", 0x0 => $"{op:X4}", int n => $"{n:X}" };
			if (kind == "FX65") { kind += $" at {pc:X3}"; }
			rig.Step();
			var entry = cost.GetValueOrDefault(kind, (0, 0, new Dictionary<string, long>()));
			entry.count++; entry.actions += rig.Graph.ActionsCarriedOut;
			foreach (var (id, count) in rig.Graph.ActionsBySource) { string part = tagOf.GetValueOrDefault(id, "outside"); entry.parts[part] = entry.parts.GetValueOrDefault(part) + count; }
			cost[kind] = entry;
		}
		foreach (var (kind, (count, actions, parts)) in cost.OrderByDescending(kv => kv.Value.actions))
		{
			output.WriteLine($"{kind,-6} x{count,5}: {actions / count,6} each, {actions,8} in all; " + string.Join(", ", parts.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} {kv.Value / count}")));
		}
	}
}

// The CC0 games in Examples/Chip8Demo/programs, copied next to the tests (Toolkit.Tests.csproj)
static class Chip8Programs
{
	public static byte[] Read(string name) => System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "chip8", name + ".ch8"));
}
