using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// The instructions a Chip8Machine compiles are nodes: an address together with what is known about the registers when it runs there (a
// constant value of some of V0..VF and of I). An abstract interpreter follows the program from 0x200, where everything is 0, and works out
// each node's context and successors:
// - Most addresses have one node, whose context is what every way of reaching it agrees on (constant propagation). A skip whose register is
//   known there has one successor, and an instruction whose result is known just sets it.
// - The addresses of small loops and small routines have a node per context they are reached with (up to NodeCopies): a loop that counts a
//   register through a few values is unrolled, so its body knows the counter, and the address its loads and stores use, in every copy.
//   Danm8ku's bullet table scans (32 slots, 8 bytes apart) become 32 copies whose loads read fixed rows and whose loop tests are gone. A loop
//   whose contexts don't stay under NodeCopies at some address gets one node per address after all (the analysis runs again without it).
// - A context only keeps what is live where it is used (liveness, I included), so dead knowledge doesn't make copies.
// - A call's return node knows what the caller knew, less what the routine called (and anything it calls) may change.
public partial class Chip8Machine
{
	// Known values: Mask has bit r for each known Vr and bit 16 for I
	sealed class Ctx
	{
		public readonly int Mask;
		public readonly byte[] V = new byte[16];
		public readonly int I;
		public readonly string Key;
		public static readonly Ctx None = new(0, null, 0);
		public Ctx(int mask, byte[]? v, int i)
		{
			Mask = mask;
			for (int r = 0; r < 16; r++) { V[r] = ((mask >> r) & 1) == 1 && v is not null ? v[r] : (byte)0; }
			I = ((mask >> 16) & 1) == 1 ? i : 0;
			Key = mask == 0 ? "-" : $"{mask:X5}:{Convert.ToHexString(V)}:{I:X3}";
		}
		public bool Has(int r) => ((Mask >> r) & 1) == 1;
		public bool HasI => Has(16);
		public Ctx Set(int r, int value) { var v = (byte[])V.Clone(); v[r] = (byte)value; return new(Mask | (1 << r), v, I); }
		public Ctx Forget(int r) => new(Mask & ~(1 << r), V, I);
		public Ctx SetI(int value) => new(Mask | (1 << 16), V, value & 0xFFF);
		public Ctx ForgetI() => new(Mask & ~(1 << 16), V, I);
		public Ctx Keep(int mask) => new(Mask & mask, V, I);
		public Ctx Join(Ctx other)
		{
			int mask = Mask & other.Mask;
			for (int r = 0; r < 16; r++) { if (V[r] != other.V[r]) { mask &= ~(1 << r); } }
			if (I != other.I) { mask &= ~(1 << 16); }
			return new(mask, V, I);
		}
	}

	sealed class Node
	{
		public int Id, Addr, Copy;
		public string Key = "";
		public Ctx In = Ctx.None;
		// successor address -> node (for a call: none; its routine's entry is Callee and the node after the call is Return)
		public readonly Dictionary<int, int> Succ = new();
		public int Callee = -1, Return = -1;
		public int SortKey => Addr * 64 + Copy;
	}

	const int NodeCopies = 80, PolyLoopLimit = 24, PolyRoutineLimit = 6;
	readonly List<Node> nodes = new();
	readonly Dictionary<(int addr, string key), int> nodeIndex = new();
	int startNode;
	// the call nodes a return node can go back to
	readonly Dictionary<int, List<int>> returnsTo = new();
	public int NodeCount => nodes.Count;
	// How many nodes each address has, and how many of them know I (for diagnostics)
	public IEnumerable<(int Address, int Copies, int KnowI)> NodeCopiesByAddress =>
		nodes.GroupBy(node => node.Addr).OrderBy(g => g.Key).Select(g => (g.Key, g.Count(), g.Count(node => node.In.HasI)));
	public string? PolyBlocked { get; private set; }
	public int AddressOf(int node) => nodes[node].Addr;
	// What the machine knows register r holds when `node` runs (null: not known, read the register)
	public int? KnownAt(int node, int register) => nodes[node].In.Has(register) ? nodes[node].In.V[register] : null;
	// Whether the machine's own register r has to be right when `node` runs (read there or later before a write); if not, and the node
	// doesn't know it either, nothing reads it again before it is written
	public bool MachineHolds(int node, int register) => ((machineLive[node] >> register) & 1) == 1;
	public int SuccessorNode(int node, int address) => nodes[node].Succ[address];
	public readonly Dictionary<int, (int equal, int other)> SplitMasks = new();

	// The addresses whose nodes are per context: the bodies of small loops (an intra-routine jump back and everything that reaches it without
	// passing its target) and small routines without calls, less what the timer waits use
	readonly Dictionary<int, HashSet<int>> routineAddresses = new();
	List<HashSet<int>> SmallLoops(IReadOnlyCollection<int> reachable)
	{
		IEnumerable<int> Intra(int a) { int op = Op(a); return op >> 12 == 0x2 ? new[] { a + 2 } : op == 0x00EE ? Array.Empty<int>() : Successors(a, op); }
		var preds = new Dictionary<int, List<int>>();
		foreach (int a in reachable) { foreach (int n in Intra(a).Where(reachable.Contains)) { if (!preds.TryGetValue(n, out var list)) { preds[n] = list = new(); } list.Add(a); } }
		var loops = new List<HashSet<int>>();
		foreach (int u in reachable)
		{
			foreach (int h in Intra(u).Where(h => h <= u && reachable.Contains(h)))
			{
				var body = new HashSet<int> { h };
				var work = new Stack<int>(new[] { u });
				while (work.Count > 0 && body.Count <= PolyLoopLimit)
				{
					int b = work.Pop();
					if (!body.Add(b)) { continue; }
					foreach (int p in preds.GetValueOrDefault(b) ?? new List<int>()) { work.Push(p); }
				}
				// a counted loop: some register is stepped by a constant and compared with one in the body (the main loop of a game is a loop
				// too, but nothing counts it)
				bool counted = body.Any(b => Op(b) >> 12 == 0x7 && body.Any(t => Op(t) >> 12 is 0x3 or 0x4 && ((Op(t) >> 8) & 0xF) == ((Op(b) >> 8) & 0xF)));
				if (body.Count <= PolyLoopLimit && counted && !body.Any(b => timerWaits.Contains(b) || timerWaits.Contains(b - 2) || timerWaits.Contains(b - 4))) { loops.Add(body); }
			}
		}
		return loops;
	}

	// The registers (bits 0 to 15) and I (bit 16) that the routine at `entry`, or anything it calls, may change
	readonly Dictionary<int, int> modified = new();
	int Modified(int entry)
	{
		if (modified.TryGetValue(entry, out int known)) { return known; }
		modified[entry] = 0x1FFFF;     // while it is being worked out (recursion): anything
		int mask = 0;
		foreach (int a in routineAddresses.GetValueOrDefault(entry) ?? new HashSet<int>())
		{
			int op = Op(a);
			mask |= UseAndDefAt(a).def;
			if (op >> 12 == 0x2) { mask |= Modified(op & 0xFFF); }
		}
		return modified[entry] = mask;
	}

	// The context after the instruction at `a` runs under `c` (a skip changes nothing), and the addresses it can go on to under it. Calls and
	// returns are handled by the caller.
	(Ctx after, List<int> next) Transfer(int a, Ctx c)
	{
		if (FusedAt(a) is FusedRoutine fused)
		{
			(int use, int def) = UseAndDefAt(a);
			bool loop = fused == FusedRoutine.Danm8kuDrawBullets || fused == FusedRoutine.Danm8kuActBullets;
			Ctx forgotten = c.Keep(~(def | (loop ? use : 0)));
			if (fused == FusedRoutine.Danm8kuGogoBullet) { forgotten = forgotten.Set(3, 0); }
			if (fused == FusedRoutine.Danm8kuPewPew) { forgotten = forgotten.SetI(0x27E); }
			if (fused == FusedRoutine.Danm8kuUpdatePlayer) { forgotten = forgotten.SetI(0x2AA); }
			return (fused == FusedRoutine.Danm8kuMoveBullet && c.Has(1) ? forgotten.Set(0xC, c.V[1]) : forgotten, Successors(a, Op(a)).ToList());
		}
		int op = Op(a), x = (op >> 8) & 0xF, y = (op >> 4) & 0xF, n = op & 0xF, nn = op & 0xFF, nnn = op & 0xFFF;
		var two = new List<int> { a + 2, a + 4 };
		List<int> One(int target) => new() { target };
		switch (op >> 12)
		{
			case 0x0: return (c, op == 0x00EE ? new List<int>() : One(a + 2));
			case 0x1: return (c, One(nnn));
			case 0x3: return (c, c.Has(x) ? One(c.V[x] == nn ? a + 4 : a + 2) : two);   // (the equal way on knows VX: Refine)
			case 0x4: return (c, c.Has(x) ? One(c.V[x] != nn ? a + 4 : a + 2) : two);
			case 0x5: return (c, x == y ? One(a + 4) : c.Has(x) && c.Has(y) ? One(c.V[x] == c.V[y] ? a + 4 : a + 2) : two);
			case 0x9: return (c, x == y ? One(a + 2) : c.Has(x) && c.Has(y) ? One(c.V[x] != c.V[y] ? a + 4 : a + 2) : two);
			case 0x6: return (c.Set(x, nn), One(a + 2));
			case 0x7: return (c.Has(x) ? c.Set(x, c.V[x] + nn) : c.Forget(x), One(a + 2));
			case 0x8:
			{
				int? vx = c.Has(x) ? c.V[x] : null, vy = c.Has(y) ? c.V[y] : null;
				if (x == y) { vy = vx; }
				int? result = null, flag = null;
				bool setsFlag = n is 0x4 or 0x5 or 0x6 or 0x7 or 0xE || (n is 0x1 or 0x2 or 0x3 && quirks.LogicResetsVF);
				int? source = quirks.ShiftUsesVY ? vy : vx;
				switch (n)
				{
					case 0x0: result = vy; break;
					case 0x1: if (vx is int o1 && vy is int o2) { result = o1 | o2; } flag = quirks.LogicResetsVF ? 0 : null; break;
					case 0x2: if (vx is int a1 && vy is int a2) { result = a1 & a2; } flag = quirks.LogicResetsVF ? 0 : null; break;
					case 0x3: result = x == y ? 0 : vx is int x1 && vy is int x2 ? x1 ^ x2 : null; flag = quirks.LogicResetsVF ? 0 : null; break;
					case 0x4: if (vx is int s1 && vy is int s2) { result = (s1 + s2) & 0xFF; flag = s1 + s2 > 0xFF ? 1 : 0; } break;
					case 0x5: if (x == y) { result = 0; flag = 1; } else if (vx is int d1 && vy is int d2) { result = (d1 - d2) & 0xFF; flag = d1 >= d2 ? 1 : 0; } break;
					case 0x7: if (x == y) { result = 0; flag = 1; } else if (vx is int e1 && vy is int e2) { result = (e2 - e1) & 0xFF; flag = e2 >= e1 ? 1 : 0; } break;
					case 0x6: if (source is int r1) { result = r1 >> 1; flag = r1 & 1; } break;
					case 0xE: if (source is int r2) { result = (r2 << 1) & 0xFF; flag = r2 >> 7; } break;
				}
				Ctx after = result is int value ? c.Set(x, value) : c.Forget(x);
				if (setsFlag) { after = flag is int f ? after.Set(0xF, f) : after.Forget(0xF); }
				return (after, One(a + 2));
			}
			case 0xA: return (c.SetI(nnn), One(a + 2));
			case 0xB:
			{
				List<int> table = JumpTable(nnn);
				if (!c.Has(0)) { return (c, table); }
				int v0 = c.V[0];
				return (c, v0 % 2 == 0 && v0 / 2 < table.Count ? One(table[v0 / 2]) : new List<int>());
			}
			case 0xC: return (c.Forget(x), One(a + 2));
			case 0xD: return (c.Forget(0xF), One(a + 2));
			case 0xE: return (c, two);
			case 0xF:
				switch (nn)
				{
					case 0x07 or 0x0A: return (c.Forget(x), One(a + 2));
					case 0x1E: return (c.HasI && c.Has(x) ? c.SetI(c.I + c.V[x]) : c.ForgetI(), One(a + 2));
					case 0x29: return (c.Has(x) ? c.SetI(quirks.FontAddress + 5 * (c.V[x] & 0xF)) : c.ForgetI(), One(a + 2));
					case 0x55 or 0x65:
					{
						Ctx after = c;
						if (nn == 0x65) { for (int r = 0; r <= x; r++) { after = after.Forget(r); } }
						if (quirks.LoadStoreIncrementsI) { after = after.HasI ? after.SetI(after.I + x + 1) : after; }
						return (after, One(a + 2));
					}
					default: return (c, One(a + 2));
				}
		}
		return (c, One(a + 2));
	}

	// What a way on from a compare knows on top of the context after it: the equal way of a constant compare knows the register's value
	Ctx Refine(int a, Ctx after, int target)
	{
		// move_bullet's exits: the return with VF 1 (both bounds checks passed), the destroy calls with VF 0
		if (FusedAt(a) == FusedRoutine.Danm8kuMoveBullet) { return after.Set(0xF, target == 0x41E ? 1 : 0); }
		if (FusedAt(a) is not null) { return after; }
		int op = Op(a), x = (op >> 8) & 0xF, nn = op & 0xFF;
		bool equalWay = (op >> 12) switch { 0x3 => target == a + 4, 0x4 => target == a + 2, _ => false };
		return equalWay && !after.Has(x) ? after.Set(x, nn) : after;
	}

	void FindNodes(IReadOnlyCollection<int> reachable)
	{
		foreach (int entry in reachable.Where(a => Op(a) >> 12 == 0x2).Select(a => Op(a) & 0xFFF).Distinct()) { Modified(entry); }
		List<HashSet<int>> loops = SmallLoops(reachable);
		var smallRoutines = routineAddresses.Where(kv => kv.Value.Count <= PolyRoutineLimit && (callSitesIn.GetValueOrDefault(kv.Key) ?? new List<int>()).Count == 0)
			.SelectMany(kv => kv.Value).ToHashSet();
		// First one node per address: what each address knows whichever way it is reached. The run with copies then starts every address
		// that has one node from that, so the contexts it passes on to the copies don't change as the run goes on (a context that is still
		// being joined would start a fresh set of copies each time it changed).
		Analyse(new HashSet<int>(), null);
		var single = nodes.ToDictionary(node => node.Addr, node => node.In);
		var blocked = new HashSet<int>();
		while (true)
		{
			var poly = loops.Where(body => !body.Overlaps(blocked)).SelectMany(body => body).Concat(smallRoutines).ToHashSet();
			var overflowed = Analyse(poly, single);
			if (overflowed.Count == 0) { break; }
			// one node per address for every loop that had too many contexts somewhere (and the routines as before)
			PolyBlocked = (PolyBlocked ?? "") + $"too many contexts at {string.Join(",", overflowed.Select(o => o.ToString("X3")))}; ";
			int before = blocked.Count;
			foreach (HashSet<int> body in loops.Where(body => body.Overlaps(overflowed))) { blocked.UnionWith(body); }
			if (blocked.Count == before) { smallRoutines.ExceptWith(overflowed); }
		}
	}

	// One run of the abstract interpreter with these addresses per context; returns the addresses that had too many contexts
	HashSet<int> Analyse(HashSet<int> poly, Dictionary<int, Ctx>? single)
	{
		nodes.Clear(); nodeIndex.Clear(); returnsTo.Clear();
		var copies = new Dictionary<int, int>();
		var overflowed = new HashSet<int>();
		var work = new Queue<int>();
		var queued = new HashSet<int>();
		void Enqueue(int id) { if (queued.Add(id)) { work.Enqueue(id); } }
		int Ensure(int addr, Ctx ctx)
		{
			ctx = ctx.Keep(LiveIn(addr));
			string key = "";
			if (poly.Contains(addr))
			{
				if (nodeIndex.TryGetValue((addr, ctx.Key), out int existing)) { return existing; }
				if (copies.GetValueOrDefault(addr) < NodeCopies)
				{
					copies[addr] = copies.GetValueOrDefault(addr) + 1;
					key = ctx.Key;
				}
				else { overflowed.Add(addr); key = "*"; }
			}
			if (nodeIndex.TryGetValue((addr, key), out int id))
			{
				Node node = nodes[id];
				Ctx joined = node.In.Join(ctx);
				if (joined.Key != node.In.Key) { node.In = joined; Enqueue(id); }
				return id;
			}
			if (key != ctx.Key && single is not null && single.TryGetValue(addr, out Ctx? settled)) { ctx = settled.Join(ctx); }
			var made = new Node { Id = nodes.Count, Addr = addr, Key = key, In = ctx };
			nodes.Add(made);
			nodeIndex[(addr, key)] = made.Id;
			Enqueue(made.Id);
			return made.Id;
		}
		Ctx start = new(0x1FFFF, new byte[16], 0);
		startNode = Ensure(Chip8.ProgramStart, start);
		while (work.Count > 0)
		{
			int id = work.Dequeue();
			queued.Remove(id);
			Node node = nodes[id];
			int op = Op(node.Addr);
			node.Succ.Clear();
			if (op >> 12 == 0x2)
			{
				int entry = op & 0xFFF;
				node.Callee = Ensure(entry, node.In);
				node.Return = Ensure(node.Addr + 2, node.In.Keep(~Modified(entry)));
				continue;
			}
			(Ctx after, List<int> next) = Transfer(node.Addr, node.In);
			foreach (int target in next.Where(Reachable.Contains)) { node.Succ[target] = Ensure(target, Refine(node.Addr, after, target)); }
		}
		if (overflowed.Count > 0) { return overflowed; }

		// Only what the start reaches (a context that changed while the analysis ran can leave nodes nothing leads to any more)
		var reached = new HashSet<int>();
		var walk = new Stack<int>(new[] { startNode });
		while (walk.Count > 0)
		{
			int id = walk.Pop();
			if (!reached.Add(id)) { continue; }
			Node node = nodes[id];
			foreach (int next in node.Succ.Values) { walk.Push(next); }
			if (node.Callee >= 0) { walk.Push(node.Callee); walk.Push(node.Return); }
		}
		var kept = nodes.Where(node => reached.Contains(node.Id)).ToList();
		var renumber = kept.Select((node, i) => (node.Id, i)).ToDictionary(p => p.Id, p => p.i);
		nodes.Clear(); nodeIndex.Clear();
		foreach (Node node in kept)
		{
			node.Id = renumber[node.Id];
			foreach (int target in node.Succ.Keys.ToList()) { node.Succ[target] = renumber[node.Succ[target]]; }
			if (node.Callee >= 0) { node.Callee = renumber[node.Callee]; node.Return = renumber[node.Return]; }
			nodes.Add(node);
			nodeIndex[(node.Addr, node.Key)] = node.Id;
		}
		startNode = renumber[startNode];
		foreach (var group in nodes.GroupBy(node => node.Addr)) { int copy = 0; foreach (Node node in group.OrderBy(node => node.Id)) { node.Copy = copy++; } }

		// Which call nodes each return node can go back to: those whose routine (followed from the call's entry node without entering calls)
		// reaches it
		foreach (Node call in nodes.Where(node => node.Callee >= 0))
		{
			foreach (int ret in RoutineNodes(call.Callee).Where(id => Op(nodes[id].Addr) == 0x00EE))
			{
				if (!returnsTo.TryGetValue(ret, out var calls)) { returnsTo[ret] = calls = new(); }
				calls.Add(call.Id);
			}
		}
		return overflowed;
	}

	// Which registers (bits 0 to 15) and I (bit 16) a node reads from the machine's own registers, and which it writes there, as Compile
	// compiles it: a known value is used as a constant, not read, and a write of a known value sets the whole constant (the register may be
	// stale: an earlier write of it was dropped because nothing read it)
	(int use, int def) MachineUseAndDef(Node node)
	{
		int a = node.Addr, op = Op(a), x = (op >> 8) & 0xF, y = (op >> 4) & 0xF, nn = op & 0xFF;
		int X = 1 << x, Y = 1 << y, F = 1 << 0xF, I = 1 << 16, upToX = (2 << x) - 1, movesI = quirks.LoadStoreIncrementsI ? I : 0;
		Ctx k = node.In;
		int IfNotKnownI = k.HasI ? 0 : I;
		if (FusedAt(a) is not null) { return UseAndDefAt(a); }
		if (Decided(node)) { return (0, 0); }
		if (Folded(node, out bool flagged)) { return (0, X | (flagged ? F : 0)); }
		switch (op >> 12)
		{
			case 0x3 or 0x4: return (X, 0);
			case 0x5 or 0x9: return (X | Y, 0);
			case 0x6: return (0, X);
			case 0x7: return k.Has(x) ? (0, X) : (X, X);
			case 0x8: return (UseAndDef(op).use, UseAndDef(op).def);
			case 0xA: return (0, I);
			case 0xB: return (1, 0);
			case 0xC: return (0, X);
			case 0xD:
			{
				bool spriteKnown = k.HasI || rangeOfI.TryGetValue(a, out var range) && range.lo == range.hi;
				return (X | Y | (spriteKnown ? 0 : I), F);
			}
			case 0xE: return (X, 0);
			case 0xF:
				switch (nn)
				{
					case 0x07 or 0x0A: return (0, X);
					case 0x1E: return k.Has(x) ? (IfNotKnownI, I) : (X | I, I);
					case 0x29: return k.Has(x) ? (0, I) : (X, I);
					case 0x33: return (X | IfNotKnownI, 0);
					case 0x55: return (k.HasI ? upToX & ~k.Mask : upToX | I, movesI);
					case 0x65: return (IfNotKnownI, upToX | movesI);
					default: return (X, 0);
				}
		}
		return (0, 0);
	}
	bool Decided(Node node) => FusedAt(node.Addr) is null && Op(node.Addr) >> 12 is 0x3 or 0x4 or 0x5 or 0x9 or 0xB && node.Succ.Count <= 1;
	// An 8XY_ whose result (and flag, if it sets one) is known when it runs
	bool Folded(Node node, out bool flagged)
	{
		int op = Op(node.Addr), x = (op >> 8) & 0xF;
		flagged = (UseAndDef(op).def & (1 << 0xF)) != 0;
		if (op >> 12 != 0x8 || FusedAt(node.Addr) is not null) { return false; }
		(Ctx after, _) = Transfer(node.Addr, node.In);
		return after.Has(x) && (!flagged || after.Has(0xF));
	}
	// What must be right in the machine's registers when each node runs (bit r for Vr, bit 16 for I): read there, or later, before a write
	readonly Dictionary<int, int> machineLive = new();
	IEnumerable<int> NodeSuccessors(Node node)
	{
		if (node.Callee >= 0) { return new[] { node.Callee }; }
		if (Op(node.Addr) == 0x00EE) { return (returnsTo.GetValueOrDefault(node.Id) ?? new List<int>()).Select(c => nodes[c].Return); }
		return node.Succ.Values;
	}
	void FindMachineLiveness()
	{
		var effects = nodes.Select(MachineUseAndDef).ToArray();
		foreach (Node node in nodes) { machineLive[node.Id] = 0; }
		for (bool changed = true; changed;)
		{
			changed = false;
			foreach (Node node in Enumerable.Reverse(nodes))
			{
				int after = NodeSuccessors(node).Aggregate(0, (mask, next) => mask | machineLive[next]);
				int live = effects[node.Id].use | (after & ~effects[node.Id].def);
				if (live != machineLive[node.Id]) { machineLive[node.Id] = live; changed = true; }
			}
		}
	}
	// What must be right in the machine's registers after the node runs
	int MachineLiveAfter(Node node) => NodeSuccessors(node).Aggregate(0, (mask, next) => mask | machineLive[next]);

	// The nodes a routine reaches from `entry` without entering calls (a call goes on at its return node)
	HashSet<int> RoutineNodes(int entry)
	{
		var seen = new HashSet<int>();
		var walk = new Stack<int>(new[] { entry });
		while (walk.Count > 0)
		{
			int id = walk.Pop();
			if (!seen.Add(id)) { continue; }
			Node node = nodes[id];
			if (node.Callee >= 0) { walk.Push(node.Return); continue; }
			foreach (int next in node.Succ.Values) { walk.Push(next); }
		}
		return seen;
	}
}
