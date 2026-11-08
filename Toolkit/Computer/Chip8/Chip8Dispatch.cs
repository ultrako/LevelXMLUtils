using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// Chip8Machine's dispatch: the Next and Pending sweep trees, moving the live path, and the call stack
public partial class Chip8Machine
{
	// Make the instruction at `address` the one that runs after this one, through its Pending relay (handed over to Next after the sweep):
	// its Pending relay, the tree nodes above it, and the gate that lets the dispatch sweep the Pending tree at all
	Input Pend(int address) => pending[address].Enable.Concat(new Input(pendingAncestorsOf[address].SelectMany(n => n.Enable))).Concat(pendingGate.Enable);
	// The same from the instruction at `from`: the Next sweep goes down through the addresses, so a later instruction has already been
	// passed and can be made next directly (its Next relay and the nodes above it); an earlier one, or itself, goes through Pending
	Input Go(int from, int to) => Switch(nextAncestorsOf[from], from, to);
	// The Next tree's live path moves from the tree nodes `leaving` (enabled now) to the node `to`, from an instruction run at node `sweptAt`:
	// tree nodes stay enabled while the live leaf is under them, so only the difference changes (the nodes `to` doesn't share are disabled,
	// the ones it lacks enabled); a later node is made next directly, an earlier one through its Pending relay (which enables its path)
	Input Switch(IEnumerable<Relay> leaving, int sweptAt, int to, bool pend = false)
	{
		var keep = nextAncestorsOf[to].ToHashSet();
		var left = leaving.ToHashSet();
		Input give = new(left.Where(n => !keep.Contains(n)).SelectMany(n => n.Disable));
		if (!pend && nodes[to].SortKey > nodes[sweptAt].SortKey) { return give.Concat(Next[to].Enable).Concat(new Input(keep.Where(n => !left.Contains(n)).SelectMany(n => n.Enable))); }
		return give.Concat(Pend(to));
	}
	Relay pendingGate = null!;
	// A tree of relays over `leaves`, `branching` children a node, as deep as that takes: returns the top nodes and, per leaf, the nodes
	// above it (bottom up). A node fires its children; a self-disabling one then disables itself. `live`: the leaf whose nodes start enabled.
	// Finding the live leaf costs about (branching + 2) a level, so a narrow, deep tree is cheaper than a wide one: TreeBranching.
	const int TreeBranching = 5;
	List<Relay> SweepTree(List<Relay> leaves, int branching, bool selfDisabling, List<List<Relay>> ancestors, int live)
	{
		ancestors.Clear();
		ancestors.AddRange(leaves.Select(_ => new List<Relay>()));
		List<(Relay relay, List<int> leafIndexes)> level = leaves.Select((leaf, i) => (leaf, new List<int> { i })).ToList();
		while (level.Count > branching)
		{
			var above = new List<(Relay, List<int>)>();
			for (int start = 0; start < level.Count; start += branching)
			{
				var children = level.Skip(start).Take(branching).ToList();
				List<int> under = children.SelectMany(c => c.leafIndexes).ToList();
				Relay node = NewRelay(startDisabled: !under.Contains(live));
				// disabling itself first: a child that runs can enable it again (the Next tree's next instruction under the same node)
				if (selfDisabling) { node.Output.Connect(node.Disable); }
				node.Output.Connect(new Input(children.SelectMany(c => c.relay.Input)));
				foreach (int i in under) { ancestors[i].Add(node); }
				above.Add((node, under));
			}
			level = above;
		}
		return level.Select(l => l.relay).ToList();
	}

	// The call stack: level l's ReturnTo[l][s] is enabled when it holds call site s; SetAt[l][s] (enabled while the stack has l entries) stores s
	// there, PopAt[l] (enabled while the top entry is at l) fires level l's ReturnTo relays
	Relay[,] setAt = null!, returnTo = null!;
	Relay[] popAt = null!, spUp = null!, spDown = null!;
	Relay popTop = null!, raise = null!, lower = null!;
	readonly Dictionary<int, int> siteIndex = new();
	// Without recursion, and when no two call sites a return can go back to can be on the stack at once (one would have to be called, perhaps
	// through other routines, from the routine the other one called), the stack needs no levels: a call marks its site (OnStack, enabled
	// while the site is on the stack) and a return fires the marks of the sites it can go back to, of which exactly one is set. A call is
	// 1 action and a return a few, instead of switching a relay per site and level.
	readonly Dictionary<int, Relay> onStack = new();
	// Why the stack has levels (null: it doesn't)
	public string? StackHasLevelsBecause { get; private set; }
	bool LevellessStack(List<int> callNodes)
	{
		// The call nodes a call can lead to while it is on the stack (through the routine it enters and what that calls, without returning)
		var reach = callNodes.ToDictionary(c => c, c => RoutineNodes(nodes[c].Callee).Where(id => nodes[id].Callee >= 0).ToHashSet());
		for (bool grew = true; grew;)
		{
			grew = false;
			foreach (int c in callNodes)
			{
				foreach (int inner in reach[c].ToList()) { foreach (int m in reach[inner]) { grew |= reach[c].Add(m); } }
			}
		}
		// A call that can be made again while it is on the stack (Danm8ku's restart jumps out of a routine back into the main loop, which
		// calls it again) leaves an older copy below the newer one. That is fine as long as nothing after the call can return: then the older
		// copy is never taken off again, and the mark only has to stand for the newer one.
		foreach (int c in callNodes.Where(c => reach[c].Contains(c)))
		{
			int? ret = RoutineNodes(nodes[c].Return).Cast<int?>().FirstOrDefault(id => Op(nodes[id!.Value].Addr) == 0x00EE);
			if (ret is int r) { StackHasLevelsBecause = $"{nodes[c].Addr:X3} can call itself again, and the return at {nodes[r].Addr:X3} follows it"; return false; }
		}
		foreach ((int at, List<int> calls) in returnsTo)
		{
			foreach (int c1 in calls)
			{
				foreach (int c2 in calls.Where(c2 => c1 != c2 && reach[c1].Contains(c2)))
				{
					StackHasLevelsBecause = $"the return at {nodes[at].Addr:X3} can go back to {nodes[c1].Addr:X3} and to {nodes[c2].Addr:X3}, which can be called while {nodes[c1].Addr:X3} is on the stack";
					return false;
				}
			}
		}
		return true;
	}
	void BuildStack()
	{
		tag = "stack";
		List<int> callSites = nodes.Where(node => node.Callee >= 0).Select(node => node.Id).ToList();
		if (LevellessStack(callSites))
		{
			foreach (int c in callSites)
			{
				onStack[c] = NewRelay(startDisabled: true);
				onStack[c].Output.Connect(onStack[c].Disable.Concat(Pend(nodes[c].Return)));
			}
			return;
		}
		for (int i = 0; i < callSites.Count; i++) { siteIndex[callSites[i]] = i; }
		int levels = StackDepthNeeded(callSites.Select(c => nodes[c].Addr).Distinct().ToList());
		StackLevels = levels;
		int sites = callSites.Count;
		setAt = new Relay[levels, sites]; returnTo = new Relay[levels, sites];
		popAt = new Relay[levels]; spUp = new Relay[levels + 1]; spDown = new Relay[levels + 1];
		for (int l = 0; l <= levels; l++) { spUp[l] = NewRelay(startDisabled: l != 0); spDown[l] = NewRelay(startDisabled: l != 0); }
		for (int l = 0; l < levels; l++)
		{
			popAt[l] = NewRelay(startDisabled: true);
			for (int s = 0; s < sites; s++)
			{
				setAt[l, s] = NewRelay(startDisabled: l != 0);
				returnTo[l, s] = NewRelay(startDisabled: true);
				setAt[l, s].Output.Connect(returnTo[l, s].Enable);
				returnTo[l, s].Output.Connect(returnTo[l, s].Disable.Concat(Pend(nodes[callSites[s]].Return)));
			}
			int level = l;
			popAt[l].Output.Connect(new Input(Enumerable.Range(0, sites).SelectMany(s => returnTo[level, s].Input)));
		}
		// One more entry: the stack moves from l to l + 1 entries. Fired from the top down, so the level just enabled isn't fired again.
		for (int l = 0; l < levels; l++)
		{
			int level = l;
			spUp[l].Output.Connect(spUp[l].Disable.Concat(spUp[l + 1].Enable).Concat(spDown[l].Disable).Concat(spDown[l + 1].Enable)
				.Concat(new Input(Enumerable.Range(0, sites).SelectMany(s => setAt[level, s].Disable)))
				.Concat(l + 1 < levels ? new Input(Enumerable.Range(0, sites).SelectMany(s => setAt[level + 1, s].Enable)) : new Input())
				.Concat(popAt[l].Enable)
				.Concat(l > 0 ? popAt[l - 1].Disable : new Input()));
		}
		// One fewer: from l to l - 1, fired from the bottom up
		for (int l = 1; l <= levels; l++)
		{
			int level = l;
			spDown[l].Output.Connect(spDown[l].Disable.Concat(spDown[l - 1].Enable).Concat(spUp[l].Disable).Concat(spUp[l - 1].Enable)
				.Concat(l < levels ? new Input(Enumerable.Range(0, sites).SelectMany(s => setAt[level, s].Disable)) : new Input())
				.Concat(new Input(Enumerable.Range(0, sites).SelectMany(s => setAt[level - 1, s].Enable)))
				.Concat(popAt[l - 1].Disable)
				.Concat(l > 1 ? popAt[l - 2].Enable : new Input()));
		}
		raise = NewRelay(); lower = NewRelay(); popTop = NewRelay();
		raise.Output.Connect(new Input(Enumerable.Range(0, levels).Reverse().SelectMany(l => spUp[l].Input)));
		lower.Output.Connect(new Input(Enumerable.Range(1, levels).SelectMany(l => spDown[l].Input)));
		popTop.Output.Connect(new Input(popAt.SelectMany(p => p.Input)));
	}

	// The deepest the program can call, from its call graph (16, the CHIP-8 limit, if it can recurse)
	int StackDepthNeeded(List<int> callSites)
	{
		// The calls each routine (code reachable from an entry without returning) can make
		Dictionary<int, List<int>> callsFrom = new();
		List<int> CallsFrom(int entry)
		{
			if (callsFrom.TryGetValue(entry, out List<int>? calls)) { return calls; }
			calls = new();
			var seen = new HashSet<int>(); var work = new Stack<int>(new[] { entry });
			while (work.Count > 0)
			{
				int a = work.Pop();
				if (a < 0 || a > Chip8.MemorySize - 2 || !seen.Add(a)) { continue; }
				int op = Op(a);
				if (op >> 12 == 0x2) { calls.Add(op & 0xFFF); }
				foreach (int next in Successors(a, op).Where(n => op >> 12 != 0x2 || n == a + 2)) { work.Push(next); }
			}
			return callsFrom[entry] = calls;
		}
		var depth = new Dictionary<int, int>();
		var onPath = new HashSet<int>();
		int Depth(int entry)
		{
			if (depth.TryGetValue(entry, out int known)) { return known; }
			if (!onPath.Add(entry)) { return 1000; }
			int deepest = CallsFrom(entry).Select(callee => 1 + Depth(callee)).DefaultIfEmpty(0).Max();
			onPath.Remove(entry);
			return depth[entry] = deepest;
		}
		return Math.Clamp(Depth(Chip8.ProgramStart), 1, Chip8.StackSize);
	}
}
