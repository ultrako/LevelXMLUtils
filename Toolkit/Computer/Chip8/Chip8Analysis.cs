using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// Chip8Machine's analyses of the program at the address level: jump tables, the range of I, liveness, routines, successors
public partial class Chip8Machine
{
	// BNNN's targets: Octo's jump table idiom (jump0), a run of jump instructions starting at NNN, one per even V0. A computed jump that
	// doesn't land in a table like that isn't compiled.
	List<int> JumpTable(int start)
	{
		var entries = new List<int>();
		for (int at = start; at <= Chip8.MemorySize - 2 && entries.Count < 128 && Op(at) >> 12 == 0x1; at += 2) { entries.Add(at); }
		return entries;
	}

	// The range of values I can have entering each instruction, from an interval analysis over the program: ANNN sets one value, FX1E adds
	// 0 to 255, FX29 points into the font, FX55 and FX65 move it on (with the VIP quirk). Calls are followed: a routine's exits flow back to
	// every return site of a call to it. A DXYN where I is one value and whose bytes are never written draws constant rows; a memory access
	// whose bytes all lie in one bank only touches that bank.
	readonly Dictionary<int, (int lo, int hi)> rangeOfI = new();
	// The range of I entering an instruction, as the analysis found it (for diagnostics and tests)
	public (int lo, int hi)? RangeOfIAt(int address) => rangeOfI.TryGetValue(address, out var range) ? range : null;
	public int OpcodeAt(int address) => Op(address);
	void FindRangeOfI(IReadOnlyCollection<int> reachable)
	{
		const int Top = 0xFFFF;
		(int lo, int hi) Join((int lo, int hi) p, (int lo, int hi) q) => (Math.Min(p.lo, q.lo), Math.Max(p.hi, q.hi));
		(int, int) Shift((int lo, int hi) p, int by, int widen) => p.hi + by + widen > Top ? (0, Top) : (p.lo + by, p.hi + by + widen);
		var state = new Dictionary<int, (int lo, int hi)>();
		var work = new Queue<int>();
		void Flow(int to, (int lo, int hi) value)
		{
			if (!reachable.Contains(to)) { return; }
			if (state.TryGetValue(to, out var old)) { value = Join(old, value); if (value == old) { return; } }
			state[to] = value; work.Enqueue(to);
		}
		// The routine each return belongs to (an address reachable from the routine's entry without following calls), and the calls to each
		var routinesOf = new Dictionary<int, List<int>>();
		var callersOf = new Dictionary<int, List<int>>();
		foreach (int site in reachable.Where(a => Op(a) >> 12 == 0x2))
		{
			int entry = Op(site) & 0xFFF;
			if (!callersOf.TryGetValue(entry, out var callers)) { callersOf[entry] = callers = new(); }
			callers.Add(site);
		}
		foreach (int entry in callersOf.Keys)
		{
			var seen = new HashSet<int>(); var walk = new Stack<int>(new[] { entry });
			while (walk.Count > 0)
			{
				int a = walk.Pop();
				if (!reachable.Contains(a) || !seen.Add(a)) { continue; }
				int op = Op(a);
				if (op == 0x00EE) { if (!routinesOf.TryGetValue(a, out var list)) { routinesOf[a] = list = new(); } list.Add(entry); continue; }
				foreach (int next in Successors(a, op).Where(n => op >> 12 != 0x2 || n == a + 2)) { walk.Push(next); }
			}
		}
		var exits = new Dictionary<int, (int lo, int hi)>();
		Flow(Chip8.ProgramStart, (0, 0));
		while (work.Count > 0)
		{
			int a = work.Dequeue(), op = Op(a), x = (op >> 8) & 0xF;
			var i = state[a];
			(int lo, int hi) after = (op >> 12, op & 0xFF) switch
			{
				(0xA, _) => (op & 0xFFF, op & 0xFFF),
				(0xF, 0x1E) => Shift(i, 0, 255),
				(0xF, 0x29) => (quirks.FontAddress, quirks.FontAddress + 75),
				(0xF, 0x55 or 0x65) when quirks.LoadStoreIncrementsI => Shift(i, x + 1, 0),
				_ => i,
			};
			if (op == 0x00EE)
			{
				foreach (int entry in routinesOf.GetValueOrDefault(a) ?? new List<int>())
				{
					var exit = exits.TryGetValue(entry, out var known) ? Join(known, i) : i;
					exits[entry] = exit;
					foreach (int site in callersOf[entry]) { Flow(site + 2, exit); }
				}
				continue;
			}
			if (op >> 12 == 0x2)
			{
				Flow(op & 0xFFF, i);
				if (exits.TryGetValue(op & 0xFFF, out var exit)) { Flow(a + 2, exit); }
				continue;
			}
			foreach (int next in Successors(a, op)) { Flow(next, after); }
		}
		foreach ((int a, var range) in state) { rangeOfI[a] = range; }
	}

	// Which registers each instruction can read before they are written again (bit r for Vr), by a backward analysis over the program. A
	// return flows only to the return sites of calls to the routines it ends (a routine: what its entry reaches without entering calls). A
	// load needn't fetch a register that isn't live after it (Danm8ku's bullet loop loads 8-byte records and uses half of them).
	readonly Dictionary<int, int> liveIn = new();
	// Loads that fetch some registers only on the branch the next instruction takes (FX65 then a skip): until that skip has run, a register
	// the other branch would need may not be loaded
	public readonly SortedSet<int> SplitLoads = new();
	public bool IsLive(int address, int register) => !liveIn.TryGetValue(address, out int mask) || ((mask >> register) & 1) == 1;
	readonly Dictionary<int, List<int>> returnSitesOf = new();
	int LiveIn(int a) => liveIn.GetValueOrDefault(a, 0x1FFFF);
	int LiveOut(int a)
	{
		int op = Op(a), live = 0;
		IEnumerable<int> next = op == 0x00EE ? returnSitesOf.GetValueOrDefault(a) ?? new List<int>()
			: op >> 12 == 0x2 ? new[] { op & 0xFFF } : Successors(a, op);
		foreach (int n in next) { live |= LiveIn(n); }
		return live;
	}
	// The fused routine at `a`, if a quirk asks for one there and its code is there
	FusedRoutine? FusedAt(int a) => quirks.FusedRoutines.FirstOrDefault(f => f.At(memory, a));
	// What the instruction at `a` reads and writes, a fused routine as a whole
	(int use, int def) UseAndDefAt(int a)
	{
		FusedRoutine? fused = FusedAt(a);
		if (fused is null) { return UseAndDef(Op(a)); }
		int Bits(params int[] registers) => registers.Aggregate(0, (mask, r) => mask | (1 << r));
		// move_bullet reads the position, fraction, velocity and acceleration, and leaves V0, V3 to V8, VA, VC, VD and VF
		if (fused == FusedRoutine.Danm8kuMoveBullet) { return (Bits(0, 1, 2, 4, 5, 6, 7), Bits(0, 3, 4, 5, 6, 7, 8, 0xA, 0xC, 0xD, 0xF)); }
		// draw_bullet reads the offset and the position, and leaves V3 (shifted), VF (the collision) and I
		if (fused == FusedRoutine.Danm8kuDrawBullet) { return (Bits(3, 4, 5), Bits(3, 0xF, 16)); }
		// the loops leave slot 31's record in V0 to V7, VB 0 and I; the last live bullet's work leaves VF (and act_bullets' VA, VC, VD and V8),
		// which keep their values when no bullet is live, so they count as read too
		if (fused == FusedRoutine.Danm8kuDrawBullets) { return (Bits(0xF), Bits(0, 1, 2, 3, 4, 5, 6, 7, 0xB, 16)); }
		if (fused == FusedRoutine.Danm8kuActBullets) { return (Bits(8, 0xA, 0xC, 0xD, 0xF), Bits(0, 1, 2, 3, 4, 5, 6, 7, 0xB, 16)); }
		// gogo_bullet restores V0 to V7, so it leaves only V3 (0), V8, VA and I; it reads VA (I is the slot at VA when none is free)
		if (fused == FusedRoutine.Danm8kuGogoBullet) { return (Bits(0xA), Bits(3, 8, 0xA, 16)); }
		// update_player loads its registers from memory before using them, and leaves V0 to V3, VF and I
		if (fused == FusedRoutine.Danm8kuUpdatePlayer) { return (0, Bits(0, 1, 2, 3, 0xF, 16)); }
		// pew_pew stores V0 to V7 at I and draws at V4, V5: it leaves VF (the collision) and I (bul)
		if (fused == FusedRoutine.Danm8kuPewPew) { return (Bits(0, 1, 2, 3, 4, 5, 6, 7, 16), Bits(0xF, 16)); }
		throw new NotSupportedException($"the fused routine {fused.Name} isn't compiled");
	}
	(int use, int def) UseAndDef(int op)
	{
		int x = (op >> 8) & 0xF, y = (op >> 4) & 0xF, n = op & 0xF, nn = op & 0xFF;
		int X = 1 << x, Y = 1 << y, F = 1 << 0xF, upToX = (2 << x) - 1, I = 1 << 16, movesI = quirks.LoadStoreIncrementsI ? I : 0;
		return (op >> 12) switch
		{
			0xA => (0, I),
			0xD => (X | Y | I, F),
			0xF when nn == 0x1E => (X | I, I),
			0xF when nn == 0x29 => (X, I),
			0xF when nn == 0x33 => (X | I, 0),
			0x6 => (0, X),
			0x7 => (X, X),
			0x8 when n == 0x0 => (Y, X),
			0x8 when n is 0x1 or 0x2 or 0x3 => (X | Y, X | (quirks.LogicResetsVF ? F : 0)),
			0x8 when n is 0x4 or 0x5 or 0x7 => (X | Y, X | F),
			0x8 when n is 0x6 or 0xE => (quirks.ShiftUsesVY ? Y : X, X | F),
			0x3 or 0x4 or 0xE => (X, 0),
			0x5 or 0x9 => (X | Y, 0),
			0xB => (quirks.JumpUsesV0 ? 1 : X, 0),
			0xC => (0, X),
			0xF when nn is 0x07 or 0x0A => (0, X),
			0xF when nn == 0x55 => (upToX | I, movesI),
			0xF when nn == 0x65 => (I, upToX | movesI),
			0xF => (X, 0),
			_ => (0, 0),
		};
	}
	// What each routine is (code its entry reaches without entering calls or returning): the return sites each return can go to, and the
	// call sites inside each routine
	readonly Dictionary<int, List<int>> callSitesIn = new();
	void FindRoutines(IReadOnlyCollection<int> reachable)
	{
		foreach (int entry in reachable.Where(a => Op(a) >> 12 == 0x2).Select(a => Op(a) & 0xFFF).Distinct())
		{
			var calls = new List<int>();
			var seen = new HashSet<int>(); var walk = new Stack<int>(new[] { entry });
			while (walk.Count > 0)
			{
				int a = walk.Pop();
				if (!reachable.Contains(a) || !seen.Add(a)) { continue; }
				int op = Op(a);
				if (op >> 12 == 0x2) { calls.Add(a); }
				if (op == 0x00EE) { continue; }
				foreach (int next in Successors(a, op).Where(n => op >> 12 != 0x2 || n == a + 2)) { walk.Push(next); }
			}
			callSitesIn[entry] = calls;
			routineAddresses[entry] = seen;
			foreach (int a in seen.Where(a => Op(a) == 0x00EE))
			{
				if (!returnSitesOf.TryGetValue(a, out var sites)) { returnSitesOf[a] = sites = new(); }
				foreach (int site in reachable.Where(c => Op(c) >> 12 == 0x2 && (Op(c) & 0xFFF) == entry)) { if (!sites.Contains(site + 2)) { sites.Add(site + 2); } }
			}
		}
	}
	void FindLiveness(IReadOnlyCollection<int> reachable)
	{
		foreach (int a in reachable) { liveIn[a] = 0; }
		for (bool changed = true; changed;)
		{
			changed = false;
			foreach (int a in reachable.Reverse())
			{
				(int use, int def) = UseAndDefAt(a);
				int live = use | (LiveOut(a) & ~def);
				if (live != liveIn[a]) { liveIn[a] = live; changed = true; }
			}
		}
	}

	// The banks an access of `length` bytes from I + offset at instruction `a` can touch: just one bank when I's range starts inside it and
	// overlaps no other bank (bytes past every bank read 0 however they are reached), otherwise every bank
	List<Bank> BanksFor(int a, int offset, int length)
	{
		if (rangeOfI.TryGetValue(a, out var range) && range.hi + offset + length - 1 <= 0xFFF)
		{
			int first = range.lo + offset, last = range.hi + offset + length - 1;
			List<Bank> overlapping = Banks.Where(b => first < b.End && last >= b.Start).ToList();
			if (overlapping.Count == 1 && first >= overlapping[0].Start) { return overlapping; }
		}
		return Banks;
	}

	IEnumerable<int> Successors(int a, int op)
	{
		if (FusedAt(a) is FusedRoutine fused) { foreach (int exit in fused.Exits) { yield return exit; } yield break; }
		int nnn = op & 0xFFF;
		switch (op >> 12)
		{
			case 0xB: foreach (int target in JumpTable(nnn)) { yield return target; } yield break;
			case 0x0 when op == 0x00EE: yield break;
			case 0x1: yield return nnn; yield break;
			case 0x2: yield return nnn; yield return a + 2; yield break;
			case 0x3 or 0x4 or 0x5 or 0x9: yield return a + 2; yield return a + 4; yield break;
			case 0xE: yield return a + 2; yield return a + 4; yield break;
			default: yield return a + 2; yield break;
		}
	}

	string? Unsupported(int op)
	{
		int n = op & 0xF, nn = op & 0xFF;
		return (op >> 12) switch
		{
			0x0 when op is 0x00E0 or 0x00EE => null,
			0x0 => "machine code calls",
			0x1 or 0x2 or 0x3 or 0x4 or 0x6 or 0x7 or 0xA or 0xC => null,
			0x5 or 0x9 when n == 0 => null,
			0x8 when n is 0x0 or 0x1 or 0x2 or 0x3 or 0x4 or 0x5 or 0x6 or 0x7 or 0xE => null,
			0x8 => "unknown opcode",
			0xB when !quirks.JumpUsesV0 => "BXNN (the jump that adds VX) isn't compiled yet",
			0xB when JumpTable(op & 0xFFF).Count == 0 => "a BNNN whose target isn't a table of jumps isn't compiled",
			0xB => null,
			0xD => null,
			0xE when nn is 0x9E or 0xA1 => null,
			0xF when nn is 0x07 or 0x15 or 0x1E or 0x29 or 0x33 or 0x55 or 0x65 => null,
			0xF when nn == 0x0A => quirks.WaitForKeyRelease ? null : "FX0A waiting for a held key (only Octo's wait for a release is compiled)",
			0xF => "this FX instruction (the sound timer) isn't compiled yet",
			_ => "unknown opcode",
		};
	}
}
