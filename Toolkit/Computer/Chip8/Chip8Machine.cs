using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// A CHIP-8 program compiled into triggers. The program is fixed when the level is built, so there is no fetch or decode: every instruction the
// program can reach (followed from 0x200 through its jumps, calls and skips) is its own short trigger program, and a one-hot "next" relay per
// address says which runs next. A frame clock runs `tickrate` instructions (Dispatch fires every Next relay; the enabled one disables itself and
// runs its instruction, which enables the next one), ticks the 60 Hz timers, and does that again: two CHIP-8 frames per game frame. Every
// instruction finishes before the next starts, so the game's recursive trigger calls never pile up across instructions.
//
// The data path:
// - V0..VF, the delay timer and a frame counter (CXNN's random source) are 8-bit toggle chains. An instruction reads a register by opening that
//   register's 8 gates onto the bus and probing it; the bus loads Temp (TempBit[k] enabled for each 1, TempNot[k] for each 0). It then fires
//   chosen TempBit and TempNot relays into a sink (gates enabled only for that step) that does something with the bits: set them in a register,
//   add them into one (ripple carry; the top bit's carry is the carry flag), clear where zero (AND), disable a latch on a mismatch, or knock out
//   the decoder candidates that don't match. Reading into Temp first means an instruction can write the register it reads.
// - The screen is 64 by 32 pixel toggles. A sprite row lights or flips pixel (x + c, y + r) through two decoded one-hots: ColOff[col][c] is enabled
//   only for col = (VX + c) mod 64, RowOff[row][r] arms the gates of row (VY + r) mod 32. A pixel turning off sets the collision latch.
// - I is a 12-bit toggle chain. Memory is a MixedMemory with a 12-bit address over the bytes up to the end of the program: ROM rows, and RAM
//   rows at the addresses the program is known to write (the caller says which, usually from a reference run: MeasureWrites). Everything past
//   the rows reads 0, as CHIP-8 memory does. Reading a byte at I + r sets the memory's address from I through gates, adds r, and reads onto the
//   bus; sprites are read that way too.
// - Octo's FX0A: the dispatch stops (a run gate) until a key is released; a release relay per key, armed only while waiting, writes the key into
//   VX and opens the gate again.
// - Calls push the call site onto a stack of one-hot levels (as deep as the program's call graph needs); a return enables the instruction after the
//   site the top level holds.
// Instructions this doesn't compile yet are refused when the level is built (NotSupportedException), never skipped silently.
public partial class Chip8Machine : TriggerContraption
{
	public const int Width = Chip8.Width, Height = Chip8.Height;
	public readonly InstantToggleChain[] V = new InstantToggleChain[16];
	// RandomCounter: CXNN's random numbers come from it (Chip8Random): it steps every game frame and every CXNN, through a shuffle table
	public readonly InstantToggleChain DelayTimer, RandomCounter;
	// Reached[a] fires each time the instruction at a runs (for the addresses given as `reached`): Danm8ku's win, for the game's victory
	readonly Dictionary<int, Relay> reachRelays = new();
	public IReadOnlyDictionary<int, Output> Reached => reachRelays.ToDictionary(kv => kv.Key, kv => kv.Value.Output);
	public readonly InstantToggle[,] Pixels = new InstantToggle[Width, Height];
	// With persistence, the relay that hides a pixel's picture (fires at the end of the second frame the pixel has been off); null without
	public readonly Relay[,]? PixelHides;
	public readonly Dictionary<int, Relay> Next = new();
	// Runs one instruction (the frame clock fires it `tickrate` times between timer ticks)
	public readonly Relay Dispatch;
	// The clock: a Permanent trigger with no body, which fires every frame from when something activates it (Start)
	public readonly ActivateTrigger FrameClock;
	public readonly Input Start;
	// Key i held down / released
	public readonly Input[] KeyDown = new Input[16], KeyUp = new Input[16];
	public readonly IReadOnlyCollection<int> Reachable;
	public readonly InstantToggleChain IRegister;
	// Memory in banks: a MixedMemory per stretch of addresses that matter (the font, the program, RAM far past it), split where more than 256
	// bytes in a row don't matter, each with its own address and the gates that copy I into it
	public sealed record Bank(int Start, int End, MixedMemory Memory, Relay Open, Relay Close);
	public readonly List<Bank> Banks = new();
	public IEnumerable<Entity> MemoryEntities => Banks.SelectMany(bank => bank.Memory.Entities);
	// The addresses that are RAM rows (the rest of memory is ROM)
	public readonly IReadOnlyCollection<int> Ram;
	// Stores (FX33, FX55) whose addresses the range analysis can't bound: only a measured run covers what they write
	public readonly IReadOnlyList<int> UnboundedStores;
	public int StackLevels { get; private set; }

	readonly List<Entity> entities = new();
	readonly Dictionary<int, Relay> pending = new();
	readonly List<List<Relay>> nextAncestors = new(), pendingAncestors = new();
	readonly Dictionary<int, List<Relay>> nextAncestorsOf = new(), pendingAncestorsOf = new();
	readonly byte[] memory = new byte[Chip8.MemorySize];
	readonly Chip8Quirks quirks;
	readonly double x, y;

	// What each trigger is part of (dispatch, bus, screen, ...), for profiling where a frame's actions go; set by the section being built
	public readonly Dictionary<ActivateTrigger, string> Tags = new(ReferenceEqualityComparer.Instance);
	string tag = "other";
	void TagAll(IEnumerable<Entity> made, string name) { foreach (ActivateTrigger t in made.OfType<ActivateTrigger>()) { Tags.TryAdd(t, name); } }
	Relay NewRelay(bool startDisabled = false)
	{
		Relay relay = new(x, y, 5, 5, startDisabled: startDisabled);
		entities.AddRange(relay.Entities);
		TagAll(relay.Entities, tag);
		return relay;
	}

	// An instruction's actions in order, split over as many relays as it takes for no relay to target one trigger twice (rule #4); the first
	// relay of the list runs them all in order
	sealed class Steps
	{
		readonly Chip8Machine machine;
		public readonly List<Relay> Relays = new();
		readonly HashSet<object> targeted = new(ReferenceEqualityComparer.Instance);
		public Steps(Chip8Machine machine) { this.machine = machine; }
		public Steps Add(Input input)
		{
			if (Relays.Count == 0 || input.Any(target => targeted.Contains(target.Targeted)))
			{
				Relays.Add(machine.NewRelay());
				targeted.Clear();
			}
			Relays[^1].Output.Connect(input);
			foreach (Target target in input) { targeted.Add(target.Targeted); }
			return this;
		}
		public Steps Add(Relay relay) => Add(relay.Input);
	}

	// ram: the addresses the program writes (MeasureWrites finds them); a write anywhere else would be lost, so they had better all be there
	// persistence: a pixel's picture stays for one more game frame after the pixel goes off, so a sprite that a game erases in one frame and
	// redraws in the next (the usual way to move one) doesn't flash. Only the picture: the machine's pixels are unchanged.
	public Chip8Machine(byte[] program, Chip8Quirks quirks, int tickrate, double x, double y, double pixelSize,
		int onColor = 0xFFFFFF, int offColor = 0x000000, IReadOnlyCollection<int>? ram = null, bool persistence = true, IReadOnlyCollection<int>? reached = null,
		double gameSpeed = 1, int heavyBullets = 0, bool foregroundScreen = true)
	{
		ForegroundScreen = foregroundScreen;
		(skipCycle, skipped) = SpeedPattern(gameSpeed);
		this.heavyBullets = heavyBullets;
		if (heavyBullets > 0) { BulletLoad = new InstantToggleChain(8, x, y, 5, 5); entities.AddRange(BulletLoad.Entities); }
		foreach (int address in reached ?? Array.Empty<int>()) { reachRelays[address] = NewRelay(); }
		if (persistence) { PixelHides = new Relay[Width, Height]; }
		this.quirks = quirks;
		this.x = x; this.y = y;
		if (quirks.DisplayWait) { throw new NotSupportedException("display wait isn't compiled yet"); }
		Chip8.Font.CopyTo(memory, quirks.FontAddress);
		program.CopyTo(memory, Chip8.ProgramStart);

		tag = "dispatch";
		// Which instructions can run: everything reachable from 0x200
		var reachable = new SortedSet<int>();
		var work = new Stack<int>(new[] { Chip8.ProgramStart });
		var unsupported = new SortedDictionary<int, string>();
		var callSites = new List<int>();
		while (work.Count > 0)
		{
			int a = work.Pop();
			if (a < 0 || a > Chip8.MemorySize - 2 || !reachable.Add(a)) { continue; }
			int op = Op(a);
			foreach (int next in Successors(a, op)) { work.Push(next); }
			if (op >> 12 == 0x2) { callSites.Add(a); }
			string? why = Unsupported(op);
			if (why is not null) { unsupported[a] = $"{op:X4} at {a:X3}: {why}"; }
		}
		if (unsupported.Count > 0) { throw new NotSupportedException("can't compile yet: " + string.Join("; ", unsupported.Values)); }
		Reachable = reachable;
		callSites.Sort();

		timerWaits = quirks.FuseTimerWaits ? reachable.Where(IsTimerWait).ToList() : new List<int>();
		FindRoutines(reachable);
		FindRangeOfI(reachable);
		FindLiveness(reachable);
		FindNodes(reachable);
		FindMachineLiveness();

		// Dispatch fires the Next relays from the last node down (nodes in address order, an address's copies in turn), so an instruction can
		// enable a later successor's Next directly (it has been passed). An earlier one (a backward jump, a loop, a return) would run in the
		// same sweep, so it goes through its Pending relay, which hands over to Next after the sweep; the Pending sweep is behind a gate that
		// only a Pend opens.
		// The Next and Pending relays are the leaves of two trees of three levels, each node a relay enabled while a leaf under it is (a tree
		// node disables itself when it fires, before its children, so the instruction that runs can enable it again for its successor), so a
		// sweep fires the top nodes and the children of the live ones: about 3 * cbrt(n) actions instead of n
		int[] ascending = nodes.OrderBy(node => node.SortKey).Select(node => node.Id).ToArray();
		int[] descending = ascending.Reverse().ToArray();
		int branching = TreeBranching;
		// Only a node that can be reached by going back needs a Pending relay: the target of a backward (or self) jump, call or skip, a return
		// node, a jump table's entry, a timer wait's start
		var goneBackTo = new SortedSet<int>();
		foreach (Node node in nodes)
		{
			int op = Op(node.Addr);
			IEnumerable<int> targets = node.Callee >= 0 ? new[] { node.Callee } : node.Succ.Values;
			foreach (int target in targets) { if (op >> 12 == 0xB || nodes[target].SortKey <= node.SortKey) { goneBackTo.Add(target); } }
			if (node.Return >= 0) { goneBackTo.Add(node.Return); }
			if (timerWaits.Contains(node.Addr)) { goneBackTo.Add(node.Id); }
		}
		foreach (Node node in nodes)
		{
			Next[node.Id] = NewRelay(startDisabled: node.Id != startNode);
			if (goneBackTo.Contains(node.Id)) { pending[node.Id] = NewRelay(startDisabled: true); }
		}
		int[] pendingNodes = goneBackTo.OrderBy(id => nodes[id].SortKey).ToArray();
		int pendingBranching = TreeBranching;
		List<Relay> nextRoots = SweepTree(descending.Select(id => Next[id]).ToList(), branching, selfDisabling: false, nextAncestors, live: Array.IndexOf(descending, startNode));
		List<Relay> pendingRoots = SweepTree(pendingNodes.Select(id => pending[id]).ToList(), pendingBranching, selfDisabling: true, pendingAncestors, live: -1);
		for (int i = 0; i < descending.Length; i++) { nextAncestorsOf[descending[i]] = nextAncestors[i]; }
		for (int i = 0; i < pendingNodes.Length; i++) { pendingAncestorsOf[pendingNodes[i]] = pendingAncestors[i]; }
		foreach (int id in pendingNodes) { pending[id].Output.Connect(pending[id].Disable.Concat(Next[id].Enable).Concat(new Input(nextAncestorsOf[id].SelectMany(n => n.Enable)))); }
		pendingGate = NewRelay(startDisabled: true);
		pendingGate.Output.Connect(pendingGate.Disable.Concat(new Input(pendingRoots.SelectMany(root => root.Input))));
		Relay dispatch = NewRelay();
		dispatch.Output.Connect(new Input(nextRoots.SelectMany(root => root.Input)).Concat(pendingGate.Input));
		Dispatch = dispatch;

		tag = "bus";
		// Registers and the bus
		for (int r = 0; r < 16; r++) { V[r] = Register($"V{r:X}"); }
		DelayTimer = Register("DT");
		RandomCounter = Register("random");
		tempBit = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
		tempNot = Enumerable.Range(0, 8).Select(_ => NewRelay()).ToArray();
		tempReset = NewRelay();
		tempReset.Output.Connect(new Input(tempBit.SelectMany(t => t.Disable).Concat(tempNot.SelectMany(t => t.Enable))));
		// What a 1 read onto the bus does: load it into Temp. (Comparing and XOR don't use the bus: they probe a register through its own gates.)
		loadTemp = Enumerable.Range(0, 8).Select(k => tempBit[k].Enable.Concat(tempNot[k].Disable)).ToArray();
		foreach (InstantToggleChain register in V.Append(DelayTimer).Append(RandomCounter)) { AddReadGates(register); }

		equalLatch = NewRelay(); keyLatch = NewRelay(startDisabled: true); carryLatch = NewRelay(startDisabled: true);
		collisionLatch = NewRelay(startDisabled: true); nonZeroLatch = NewRelay(startDisabled: true);
		carryLatch.Output.Connect(V[0xF].Sets[0]);
		collisionLatch.Output.Connect(V[0xF].Sets[0]);

		tag = "keys";
		// Keys: KeyHeld[i] is enabled while key i is down; the key decoder's candidate i asks it
		keyHeld = Enumerable.Range(0, 16).Select(_ => NewRelay(startDisabled: true)).ToArray();
		tag = "dispatch";
		runGate = NewRelay();
		tag = "keys";
		runGate.Output.Connect(dispatch.Input);
		// Fused timer waits (Chip8Quirks.FuseTimerWaits): the steps of a frame half go through a frame gate that a fused wait closes for the rest
		// of the half, and each step marks which of three phases it is, (tickrate - step) mod 3, which says where the spinning would have left PC
		tag = "timer waits";
		if (timerWaits.Count > 0)
		{
			frameGate = NewRelay();
			frameGate.Output.Connect(runGate.Input);
			closeStepGroups = NewRelay();
			phase = Enumerable.Range(0, 3).Select(p => NewRelay(startDisabled: p != 1)).ToArray();
		}
		// Octo's FX0A: per register it waits into, a release relay per key (armed while waiting) that loads the key and opens the run gate
		foreach (int wx in reachable.Where(a => (Op(a) & 0xF0FF) == 0xF00A).Select(a => (Op(a) >> 8) & 0xF).Distinct())
		{
			Relay disarm = NewRelay(), arm = NewRelay(), clear = NewRelay();
			Relay[] releases = Enumerable.Range(0, 16).Select(_ => NewRelay(startDisabled: true)).ToArray();
			disarm.Output.Connect(new Input(releases.SelectMany(r => r.Disable)));
			arm.Output.Connect(new Input(releases.SelectMany(r => r.Enable)));
			clear.Output.Connect(new Input(V[wx].Clears.SelectMany(c => c)));
			for (int i = 0; i < 16; i++)
			{
				Relay set = NewRelay();
				set.Output.Connect(new Input(BitsOf(i).SelectMany(k => V[wx].Sets[k])));
				releases[i].Output.Connect(disarm.Input.Concat(clear.Input).Concat(set.Input).Concat(runGate.Enable));
			}
			waitArms[wx] = arm;
			waitReleases[wx] = releases;
		}
		for (int i = 0; i < 16; i++)
		{
			int key = i;
			KeyDown[i] = keyHeld[i].Enable;
			KeyUp[i] = keyHeld[i].Disable.Concat(new Input(waitReleases.Values.SelectMany(releases => releases[key].Input)));
			keyHeld[i].Output.Connect(keyLatch.Enable);
		}
		keyDecoder = Decoder(16, 4, "key");
		for (int i = 0; i < 16; i++) { keyDecoder.Candidates[i].Output.Connect(keyHeld[i].Input); }

		Relay persistTick = BuildScreen(pixelSize, onColor, offColor);
		BuildStack();

		// I and memory
		tag = "I and addressing";
		IRegister = new InstantToggleChain(12, x, y, 5, 5);
		entities.AddRange(IRegister.Entities);
		TagAll(IRegister.Entities, "registers");
		// RAM: every byte a reachable store (FX33, FX55) can write, from the range of I at it; a store whose range isn't bounded can't be
		// covered that way, and only the measured writes (`ram`) cover it (UnboundedStores lists them). A store to a ROM row would be lost.
		var stores = new SortedSet<int>();
		var unbounded = new List<int>();
		foreach (int a in reachable)
		{
			int op = Op(a);
			int length = (op & 0xF0FF) == 0xF033 ? 3 : (op & 0xF0FF) == 0xF055 ? ((op >> 8) & 0xF) + 1 : 0;
			if (length == 0) { continue; }
			if (rangeOfI.TryGetValue(a, out var range) && range.hi + length - 1 <= 0xFFF && range.hi - range.lo <= 0x400)
			{
				for (int address = range.lo; address <= range.hi + length - 1; address++) { stores.Add(address); }
			}
			else { unbounded.Add(a); }
		}
		UnboundedStores = unbounded;
		Ram = new SortedSet<int>((ram ?? Array.Empty<int>()).Concat(stores));
		var matters = new SortedSet<int>(Enumerable.Range(quirks.FontAddress, Chip8.Font.Length).Concat(Enumerable.Range(Chip8.ProgramStart, program.Length)).Concat(Ram));
		var stretches = new List<(int start, int end)>();
		foreach (int address in matters)
		{
			if (stretches.Count > 0 && address - stretches[^1].end <= 256) { stretches[^1] = (stretches[^1].start, address + 1); }
			else { stretches.Add((address, address + 1)); }
		}
		foreach ((int first, int end) in stretches)
		{
			// a bank starts at a multiple of 64 (the most rows per block a 12-bit address gives)
			int start = first / 64 * 64;
			// Danm8ku's bullet table (when its bullet loops are fused) is toggle rows, so the native loops count in the records themselves
			bool Toggled(int address) => quirks.FusedRoutines.Contains(FusedRoutine.Danm8kuActBullets) && FusedAt(FusedRoutine.Danm8kuActBullets.Entry) is not null
				&& address >= BulletArea && address < BulletArea + 256;
			List<IMemoryRow> rows = Enumerable.Range(start, end - start).Select(address =>
			{
				if (Toggled(address))
				{
					var row = new ToggleMemoryRow(8, x, y, 5, 5);
					toggleRows[address] = row;
					TagAll(row.Entities, "bullet table");
					return (IMemoryRow)row;
				}
				return Ram.Contains(address) ? new ReadWriteMemoryRow(8, x, y, 5, 5) : new ROMRow(8, x, y, 5, 5);
			}).ToList();
			for (int address = start; address < end; address++) { rows[address - start].SetValue(memory[address]); }
			MixedMemory bankMemory = new(rows, x, y, 5, 5, addressBits: 12, baseAddress: start, lazyWrites: true, stepsCountAddress: false);
			entities.AddRange(bankMemory.Entities);
			TagAll(bankMemory.Entities, "memory");
			foreach (var (trigger, part) in bankMemory.Parts) { if (trigger is ActivateTrigger activate && !(Tags.TryGetValue(activate, out string? t) && t == "bullet table")) { Tags[activate] = "memory: " + part; } }
			for (int k = 0; k < 8; k++) { bankMemory.Values[k].Connect(loadTemp[k]); }
			Relay[] iToAddress = Enumerable.Range(0, 12).Select(_ => NewRelay(startDisabled: true)).ToArray();
			for (int k = 0; k < 12; k++) { IRegister.Ons[k].Connect(iToAddress[k].Input); iToAddress[k].Output.Connect(bankMemory.Address[k]); }
			Relay open = NewRelay(), close = NewRelay();
			open.Output.Connect(new Input(iToAddress.SelectMany(g => g.Enable)));
			close.Output.Connect(new Input(iToAddress.SelectMany(g => g.Disable)));
			Banks.Add(new Bank(start, end, bankMemory, open, close));
			foreach (ToggleMemoryRow row in toggleRows.Values) { if (!readGates.ContainsKey(row.Byte)) { AddReadGates(row.Byte); } }
		}
		clearI = NewRelay();
		clearI.Output.Connect(new Input(IRegister.Clears.SelectMany(c => c)));
		fontDecoder = Decoder(16, 4, "font");
		for (int d = 0; d < 16; d++)
		{
			Relay set = NewRelay();
			set.Output.Connect(new Input(BitsOf(quirks.FontAddress + 5 * d, 12).SelectMany(k => IRegister.Sets[k])));
			fontDecoder.Candidates[d].Output.Connect(clearI.Input.Concat(set.Input));
		}
		tag = "decimal digits";
		// FX33: the decimal digits of each value, as relays enabled per digit bit
		digits = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 4).Select(_ => NewRelay(startDisabled: true)).ToArray()).ToArray();
		digitReset = NewRelay();
		digitReset.Output.Connect(new Input(digits.SelectMany(row => row).SelectMany(d => d.Disable)));
		for (int j = 0; j < 3; j++) { for (int k = 0; k < 4; k++) { digits[j][k].Output.Connect(new Input(Banks.SelectMany(bank => bank.Memory.Writes[k]))); } }
		decimalDecoder = Decoder(256, 8, "decimal");
		for (int v = 0; v < 256; v++)
		{
			int[] parts = { v / 100, v / 10 % 10, v % 10 };
			decimalDecoder.Candidates[v].Output.Connect(new Input(Enumerable.Range(0, 3).SelectMany(j => BitsOf(parts[j], 4).SelectMany(k => digits[j][k].Enable))));
		}

		foreach (Node node in nodes) { tag = $"instruction {Op(node.Addr) >> 12:X}"; Compile(node); }

		tag = "timers and clock";
		// The timers' 60 Hz tick: if the delay timer isn't 0, take 1 off (add 255)
		Steps tick = new(this);
		Read(tick, DelayTimer);
		tick.Add(nonZeroLatch.Disable);
		Apply(tick, Sink("nonzero", t: k => nonZeroLatch.Enable), t: AllBits);
		tick.Add(nonZeroLatch.Input);
		Relay decrement = NewRelay();
		decrement.Output.Connect(new Input(DelayTimer.Inputs.SelectMany(i => i)));
		nonZeroLatch.Output.Connect(decrement.Input);

		FrameClock = new ActivateTrigger { X = x, Y = y, Width = 5, Height = 5, TriggeredBy = TriggeredBy.Triggers, RepeatType = RepeatType.Permanent, Interval = double.NaN, StartDisabled = false };
		entities.Add(FrameClock);
		Input frame = RandomCounter.Inputs[0];
		frameParts.Add((RandomCounter.Inputs[0], false, null, false, null));
		// With fused timer waits the steps of a half go through group relays of StepGroup steps (the first group takes the rest), which a
		// fused wait turns off along with the frame gate and the end of the half turns on again: the rest of a half the game spends waiting
		// costs an action a group, not about four a step (Danm8ku runs 2,000 steps a frame). A skipped group is a multiple of 3 steps, so the
		// phase is where its steps would have left it.
		List<Relay> stepGroups = new();
		// Below full speed the second tick of some frames is skipped (SecondTickAt): its parts go through one relay a ring of skipCycle relays
		// (one enabled, each frame passing it on) turns off for the frames to skip and on again after
		// With heavyBullets, also off for a frame whose first tick's load reached it (BulletLoad counts each bullet act_bullets moves and 4 for
		// each new bullet stored, which costs about as much as moving and drawing 5 or 6; cleared at the start of each frame), so no frame runs
		// two heavy ticks: the game slows down at its busiest, much as it does in Octo
		Relay? secondTick = skipped > 0 || heavyBullets > 0 ? NewRelay() : null;
		Input second = new();
		if (BulletLoad is not null)
		{
			// through a relay: the frame clock also activates the second tick's relay, and can't give it two actions
			Relay start = NewRelay();
			start.Output.Connect(new Input(BulletLoad.Clears.SelectMany(c => c)).Concat(skipped == 0 ? secondTick!.Enable : new Input()));
			frame = frame.Concat(start.Input);
			frameParts.Add((start.Input, false, null, false, null));
		}
		if (skipped > 0 && secondTick is not null)
		{
			// swept from the top down, so the relay a frame enables has had its turn; the last one's successor goes through a relay fired first
			Relay[] ring = Enumerable.Range(0, skipCycle).Select(j => NewRelay(startDisabled: j != 0)).ToArray();
			Relay wrap = NewRelay(startDisabled: true);
			wrap.Output.Connect(wrap.Disable.Concat(ring[0].Enable));
			for (int j = 0; j < skipCycle; j++)
			{
				ring[j].Output.Connect(ring[j].Disable.Concat(j + 1 < skipCycle ? ring[j + 1].Enable : wrap.Enable)
					.Concat(SecondTickAt(j, gameSpeed) ? secondTick.Enable : secondTick.Disable));
			}
			Input sweep = wrap.Input.Concat(new Input(ring.Reverse().SelectMany(r => r.Input)));
			frame = frame.Concat(sweep);
			frameParts.Add((sweep, false, null, false, null));
		}
		for (int half = 0; half < 2; half++)
		{
			if (half == 1 && BulletLoad is not null)
			{
				Steps decide = new(this);
				IfAbove(decide, BulletLoad, heavyBullets - 1, secondTick!.Disable);
				ChainSteps(decide);
				frame = frame.Concat(decide.Relays[0].Input);
				frameParts.Add((decide.Relays[0].Input, false, null, false, null));
			}
			if (half == 1 && secondTick is not null) { frame = frame.Concat(secondTick.Input); }
			ActivateTrigger? inSecond = half == 1 ? secondTick?.Output.Single() : null;
			Input HalfInput(Input part) { if (half == 1 && secondTick is not null) { second = second.Concat(part); } else { frame = frame.Concat(part); } return part; }
			Relay? group = null;
			for (int i = 0; i < tickrate; i++)
			{
				bool first = false;
				if (frameGate is not null && (i == 0 || (tickrate - i) % StepGroup == 0))
				{
					group = NewRelay();
					stepGroups.Add(group);
					HalfInput(group.Input);
					first = true;
				}
				Relay step = NewRelay();
				if (frameGate is not null)
				{
					// the step before this one (the last of the half before, for the first) was in phase (tickrate - (i - 1)) mod 3
					int now = (tickrate - i) % 3, before = (tickrate - i + 1) % 3;
					if (now != before) { step.Output.Connect(phase![before].Disable.Concat(phase[now].Enable)); }
					step.Output.Connect(frameGate.Input);
				}
				else { step.Output.Connect(runGate.Input); }
				if (group is not null) { group.Output.Connect(step.Input); }
				else { HalfInput(step.Input); }
				frameParts.Add((step.Input, true, group?.Output.Single(), first, inSecond));
			}
			Relay tickRelay = NewRelay();
			if (frameGate is not null) { tickRelay.Output.Connect(frameGate.Enable); }
			tickRelay.Output.Connect(tick.Relays[0].Input);
			HalfInput(tickRelay.Input);
			frameParts.Add((tickRelay.Input, false, null, false, inSecond));
			tickRelays.Add(tickRelay);
		}
		if (frameGate is not null)
		{
			foreach (Relay tickRelay in tickRelays) { tickRelay.Output.Connect(new Input(stepGroups.SelectMany(g => g.Enable))); }
			closeStepGroups.Output.Connect(new Input(stepGroups.SelectMany(g => g.Disable)));
		}
		secondTick?.Output.Connect(second);
		new Output(new[] { FrameClock }).Connect(frame.Concat(persistTick.Input));
		frameParts.Add((persistTick.Input, false, null, false, null));
		ChainSteps(tick);
		WireSinks();
		Start = new Input { new Target<Trigger>(FrameClock, new Activate()) };
		Entities = entities;
	}

	// The addresses a program writes, from the reference interpreter run for `seconds` with a random key held (and released) every quarter
	// second. It can only find what that run reaches, so a program that writes somewhere new later would lose that write.
	public static SortedSet<int> MeasureWrites(byte[] program, Chip8Quirks quirks, int tickrate, int seconds = 120, int seed = 1)
	{
		Chip8 chip = new(program, quirks, seed);
		Random keys = new(seed);
		int held = -1;
		for (int frame = 0; frame < seconds * 60 && chip.Halted is null; frame++)
		{
			if (frame % 15 == 0)
			{
				if (held >= 0) { chip.Keys[held] = false; chip.ReleaseKey(held); }
				held = keys.Next(3) == 0 ? -1 : keys.Next(16);
				if (held >= 0) { chip.Keys[held] = true; }
			}
			chip.RunFrame(tickrate);
		}
		return new SortedSet<int>(chip.Written);
	}

	int Op(int a) => (memory[a] << 8) | memory[a + 1];
	// What the frame clock does, in order (each an activation of some triggers): whether it is one of the instruction steps
	// Gate: the step's group relay (with fused timer waits), First: the group's first step, where the game checks whether the group is on
	// Second: the second tick's relay, for the parts of a frame's second tick when it can be skipped (the game checks it once, before them)
	readonly List<(Input input, bool step, ActivateTrigger? gate, bool first, ActivateTrigger? second)> frameParts = new();
	public IReadOnlyList<(Input Input, bool Step, ActivateTrigger? Gate, bool First, ActivateTrigger? Second)> FrameParts => frameParts;
	// The bullets the first tick of a frame moved (heavyBullets), and from how many on a frame skips its second tick
	public readonly InstantToggleChain? BulletLoad;
	// The screen's pictures as foreground groups, drawn over the character (by default), or plain shapes, about a quarter of the cost a frame
	// to the game's drawing, for a level whose character is gone (SeatedControls.BlackHoleOnGrab)
	readonly bool ForegroundScreen;
	readonly int heavyBullets;
	const int StepGroup = 30;
	readonly int skipCycle, skipped;
	// The game's speed as a pattern of frames: the shortest cycle of frames whose ticks (2 a frame at full speed, 60 Hz) come out whole, and how
	// many of its frames skip their second tick (0.9: 9 ticks in 5 frames, one skipped). Speeds from 0.5 to 1.
	static (int cycle, int skipped) SpeedPattern(double speed)
	{
		if (speed < 0.5 || speed > 1) { throw new ArgumentOutOfRangeException(nameof(speed), "the game speed goes from 0.5 (a tick a frame) to 1"); }
		for (int cycle = 1; cycle <= 60; cycle++)
		{
			double ticks = 2 * cycle * speed;
			if (Math.Abs(ticks - Math.Round(ticks)) < 1e-9) { return (cycle, 2 * cycle - (int)Math.Round(ticks)); }
		}
		throw new ArgumentException($"no cycle of up to 60 frames gives speed {speed}");
	}
	// Whether frame `frame` (0 for the clock's first) runs its second tick at `speed`: the skipped frames spread evenly through the cycle
	public static bool SecondTickAt(int frame, double speed)
	{
		(int cycle, int skipped) = SpeedPattern(speed);
		int j = frame % cycle;
		return (j + 1) * skipped / cycle == j * skipped / cycle;
	}
	readonly List<Relay> tickRelays = new();
	// turns the step groups off (a fused timer wait)
	Relay closeStepGroups = null!;
	// The idiom a fused timer wait replaces: FX07, 3X00, 1NNN back to the FX07
	bool IsTimerWait(int a) => a <= Chip8.MemorySize - 6 && (Op(a) & 0xF0FF) == 0xF007 && Op(a + 2) == (0x3000 | (Op(a) & 0x0F00)) && Op(a + 4) == (0x1000 | a);
	List<int> timerWaits = new();
	Relay? frameGate;
	Relay[]? phase;
}
