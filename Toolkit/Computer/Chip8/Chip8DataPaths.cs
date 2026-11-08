using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// Chip8Machine's data path: registers, Temp, direct paths (copies, compares, arithmetic, hubs), sinks, decoders and latches
public partial class Chip8Machine
{
	InstantToggleChain Register(string name)
	{
		InstantToggleChain register = new(8, x, y, 5, 5);
		entities.AddRange(register.Entities);
		// tagged by register and by which of a bit's three triggers: "on" fires for a 1 (probes, and counting a bit on), "off" when counting
		// turns a bit off, "arm" re-arms the bit's "on" when it is counted
		for (int k = 0; k < 8; k++)
		{
			foreach (ActivateTrigger t in register.Ons[k].OfType<ActivateTrigger>()) { Tags.TryAdd(t, $"registers: {name} on"); }
			foreach (ActivateTrigger t in register.Offs[k].OfType<ActivateTrigger>()) { Tags.TryAdd(t, $"registers: {name} off"); }
			foreach (ActivateTrigger t in register.Useds[k].OfType<ActivateTrigger>()) { Tags.TryAdd(t, $"registers: {name} arm"); }
		}
		TagAll(register.Entities, "registers");
		return register;
	}

	// The bus
	Relay[] tempBit = null!, tempNot = null!;
	Input[] loadTemp = null!;
	Relay tempReset = null!;
	readonly Dictionary<InstantToggleChain, (Relay open, Relay close)> readGates = new();
	void AddReadGates(InstantToggleChain register)
	{
		Relay[] gates = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
		for (int k = 0; k < 8; k++)
		{
			register.Ons[k].Connect(gates[k].Input);
			gates[k].Output.Connect(loadTemp[k]);
		}
		Relay open = NewRelay(), close = NewRelay();
		open.Output.Connect(new Input(gates.SelectMany(g => g.Enable)));
		close.Output.Connect(new Input(gates.SelectMany(g => g.Disable)));
		readGates[register] = (open, close);
	}

	// A register's bits reach the gate sets of its direct paths (copies, arithmetic, XOR) through a hub: a relay per bit, opened by a gate
	// set's open relay and closed by its close relay. A bit's On (fired by every probe of it, and when counting turns it on) then costs one
	// action for all of them when none is open, instead of one each: compares probe a register far more often than it is copied.
	// Only the CHIP-8 registers (and the delay timer) have one: anything else (a bullet table byte) feeds few gate sets, and opening a hub
	// with each costs more than it saves, so its gates hang on its bits directly.
	// The gate sets of a register (about 35 for Danm8ku's busiest) are split into groups of HubGroup, each behind its own relay per bit (open
	// only with one of its gate sets): an open hub bit then fires a relay per group and the gate sets of one group, not every gate set.
	const int HubGroup = 6;
	readonly Dictionary<InstantToggleChain, (Relay[] bits, List<(Relay[] bits, int members)> groups)> hubs = new();
	readonly Dictionary<InstantToggleChain, (Output[] bits, Input open, Input close)> directHubs = new();
	(Output[] bits, Input open, Input close) Hub(InstantToggleChain register)
	{
		if (directHubs.TryGetValue(register, out var direct)) { return direct; }
		if (Array.IndexOf(V, register) < 0 && register != DelayTimer) { return directHubs[register] = (register.Ons.ToArray(), new Input(), new Input()); }
		string outer = tag; tag = "direct paths";
		if (!hubs.TryGetValue(register, out var hub))
		{
			Relay[] bits = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			for (int k = 0; k < 8; k++) { register.Ons[k].Connect(bits[k].Input); }
			hubs[register] = hub = (bits, new());
		}
		if (hub.groups.Count == 0 || hub.groups[^1].members >= HubGroup)
		{
			Relay[] groupBits = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			for (int k = 0; k < 8; k++) { hub.bits[k].Output.Connect(groupBits[k].Input); }
			hub.groups.Add((groupBits, 0));
		}
		var (group, members) = hub.groups[^1];
		hub.groups[^1] = (group, members + 1);
		tag = outer;
		Relay[] all = hub.bits.Concat(group).ToArray();
		return (group.Select(b => b.Output).ToArray(), new Input(all.SelectMany(b => b.Enable)), new Input(all.SelectMany(b => b.Disable)));
	}

	// Direct paths that skip the bus. A copy: per (source, destination), a gate per bit from the source's On to the destination's Set, open
	// only while the copy probes the source (a source's On also fires when counting turns the bit on)
	readonly Dictionary<(InstantToggleChain from, InstantToggleChain to), (Relay open, Relay close)> copyGates = new();
	(Relay open, Relay close) CopyGates(InstantToggleChain from, InstantToggleChain to)
	{
		if (copyGates.TryGetValue((from, to), out var existing)) { return existing; }
		string outer = tag; tag = "direct paths";
		Relay[] gates = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
		var hub = Hub(from);
		for (int k = 0; k < 8; k++) { hub.bits[k].Connect(gates[k].Input); gates[k].Output.Connect(to.Sets[k]); }
		Relay open = NewRelay(), close = NewRelay();
		open.Output.Connect(new Input(gates.SelectMany(g => g.Enable)).Concat(hub.open));
		close.Output.Connect(new Input(gates.SelectMany(g => g.Disable)).Concat(hub.close));
		tag = outer;
		return copyGates[(from, to)] = (open, close);
	}
	void Copy(Steps steps, InstantToggleChain from, InstantToggleChain to)
	{
		if (from == to) { return; }
		(Relay open, Relay close) = CopyGates(from, to);
		steps.Add(new Input(to.Clears.SelectMany(c => c)));
		steps.Add(open);
		steps.Add(new Input(from.Probes.SelectMany(p => p)));
		steps.Add(close);
	}
	// A constant compare probes the register's bits: per bit, a zero witness (armed for the constant's ones; the bit's On disarms it, so one
	// still armed afterwards saw a 0) and a one witness (armed for the constant's zeros; the bit's On fires it). Either one clears equalLatch.
	readonly Dictionary<InstantToggleChain, (Relay[] zero, Relay[] one)> witnesses = new();
	(Relay[] zero, Relay[] one) Witnesses(InstantToggleChain register)
	{
		if (!witnesses.TryGetValue(register, out var w))
		{
			string outer = tag; tag = "direct paths";
			w = (Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray(), Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray());
			for (int k = 0; k < 8; k++)
			{
				register.Ons[k].Connect(w.zero[k].Disable.Concat(w.one[k].Input));
				w.zero[k].Output.Connect(w.zero[k].Disable.Concat(equalLatch.Disable));
				w.one[k].Output.Connect(equalLatch.Disable);
			}
			witnesses[register] = w;
			tag = outer;
		}
		return w;
	}
	void CompareWithConstant(Steps steps, InstantToggleChain register, int value)
	{
		var w = Witnesses(register);
		var ones = BitsOf(value).ToList(); var zeros = ZerosOf(value).ToList();
		steps.Add(equalLatch.Enable);
		steps.Add(new Input(ones.SelectMany(k => w.zero[k].Enable).Concat(zeros.SelectMany(k => w.one[k].Enable))));
		steps.Add(new Input(register.Probes.SelectMany(p => p)));
		if (ones.Count > 0) { steps.Add(new Input(ones.SelectMany(k => w.zero[k].Input))); }
		if (zeros.Count > 0) { steps.Add(new Input(zeros.SelectMany(k => w.one[k].Disable))); }
	}

	// A shared latch (the equal latch, the key latch) is checked by firing it, and it fires the branch relay of every site that checks it. So
	// its sites are in groups: the latch fires only the group relays, each enabled while one of its sites is armed, and that group fires its
	// sites. About 2 * sqrt(sites) actions when the latch is enabled instead of one per site.
	readonly Dictionary<Relay, List<(Relay group, List<Relay> members)>> latchGroups = new();
	const int LatchGroupSize = 12;
	(Input arm, Input disarm) OnLatch(Relay latch, Relay site)
	{
		if (!latchGroups.TryGetValue(latch, out var groups)) { latchGroups[latch] = groups = new(); }
		if (groups.Count == 0 || groups[^1].members.Count >= LatchGroupSize)
		{
			Relay newGroup = NewRelay(startDisabled: true);
			latch.Output.Connect(newGroup.Input);
			groups.Add((newGroup, new List<Relay>()));
		}
		var (group, members) = groups[^1];
		members.Add(site);
		group.Output.Connect(site.Input);
		return (site.Enable.Concat(group.Enable), site.Disable.Concat(group.Disable));
	}

	// Arithmetic straight from VY's bits to VX, per (operation, VY, VX): a gate per bit fired by VY's 1s (open only while the operation probes
	// VY), and a not relay per bit, armed before the probe and knocked out by VY's 1s, which then acts for VY's 0s
	readonly Dictionary<(string, InstantToggleChain, InstantToggleChain), (Relay open, Relay close)> pairGates = new();
	readonly Dictionary<(string, InstantToggleChain, InstantToggleChain), (Relay arm, Relay fire)> pairNots = new();
	(Relay open, Relay close) OnesOf(string name, InstantToggleChain from, InstantToggleChain to, Func<int, Input> onOne)
	{
		if (pairGates.TryGetValue((name, from, to), out var known)) { return known; }
		string outer = tag; tag = "direct paths " + name + " ones";
		var hub = Hub(from);
		Relay[] gates = Enumerable.Range(0, hub.bits.Length).Select(_ => NewRelay(startDisabled: true)).ToArray();
		for (int k = 0; k < gates.Length; k++) { hub.bits[k].Connect(gates[k].Input); gates[k].Output.Connect(onOne(k)); }
		Relay open = NewRelay(), close = NewRelay();
		open.Output.Connect(new Input(gates.SelectMany(g => g.Enable)).Concat(hub.open));
		close.Output.Connect(new Input(gates.SelectMany(g => g.Disable)).Concat(hub.close));
		tag = outer;
		return pairGates[(name, from, to)] = (open, close);
	}
	(Relay arm, Relay fire) ZerosOf(string name, InstantToggleChain from, InstantToggleChain to, Func<int, Input> onZero)
	{
		if (pairNots.TryGetValue((name, from, to), out var known)) { return known; }
		string outer = tag; tag = "direct paths " + name + " zeros";
		var hub = Hub(from);
		Relay[] nots = Enumerable.Range(0, hub.bits.Length).Select(_ => NewRelay(startDisabled: true)).ToArray();
		for (int k = 0; k < nots.Length; k++) { hub.bits[k].Connect(nots[k].Disable); nots[k].Output.Connect(onZero(k)); }
		Relay arm = NewRelay(), fire = NewRelay();
		arm.Output.Connect(new Input(nots.SelectMany(g => g.Enable)).Concat(hub.open));
		fire.Output.Connect(hub.close.Concat(new Input(nots.SelectMany(g => g.Input))));
		tag = outer;
		return pairNots[(name, from, to)] = (arm, fire);
	}
	void ProbeOnes(Steps steps, (Relay open, Relay close) gates, InstantToggleChain from)
	{
		steps.Add(gates.open); steps.Add(new Input(from.Probes.SelectMany(p => p))); steps.Add(gates.close);
	}
	void ProbeZeros(Steps steps, (Relay arm, Relay fire) nots, InstantToggleChain from)
	{
		steps.Add(nots.arm); steps.Add(new Input(from.Probes.SelectMany(p => p))); steps.Add(nots.fire);
	}

	// VX = VY: Temp holds VX, which arms VY's witnesses (a zero witness for each 1, a one witness for each 0), then VY is probed
	void CompareRegisters(Steps steps, InstantToggleChain vx, InstantToggleChain vy)
	{
		steps.Add(equalLatch.Enable);
		if (vx == vy) { return; }
		var w = Witnesses(vy);
		Read(steps, vx);
		Apply(steps, Sink($"witness{Array.IndexOf(V, vy)}", t: k => w.zero[k].Enable, n: k => w.one[k].Enable), t: AllBits, n: AllBits);
		steps.Add(new Input(vy.Probes.SelectMany(p => p)));
		steps.Add(new Input(AllBits.SelectMany(k => w.zero[k].Input)));
		steps.Add(new Input(AllBits.SelectMany(k => w.one[k].Disable)));
	}
	// VX ^= VY: Temp holds VX and a sink flips VX's bits by Temp (a 1 clears, a 0 sets); each 1 of VY fires both of Temp's relays for its bit
	// through gates from VY to Temp, open only for this
	readonly Dictionary<InstantToggleChain, (Relay open, Relay close)> xorGates = new();
	void Xor(Steps steps, InstantToggleChain vx, InstantToggleChain vy)
	{
		if (!xorGates.TryGetValue(vy, out var g))
		{
			string outer = tag; tag = "direct paths";
			Relay[] gates = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			var hub = Hub(vy);
			for (int k = 0; k < 8; k++) { hub.bits[k].Connect(gates[k].Input); gates[k].Output.Connect(tempBit[k].Input.Concat(tempNot[k].Input)); }
			Relay open = NewRelay(), close = NewRelay();
			open.Output.Connect(new Input(gates.SelectMany(r => r.Enable)).Concat(hub.open));
			close.Output.Connect(new Input(gates.SelectMany(r => r.Disable)).Concat(hub.close));
			xorGates[vy] = g = (open, close);
			tag = outer;
		}
		Read(steps, vx);
		int xr = Array.IndexOf(V, vx);
		SinkGates flip = Sink($"xor{xr}", t: k => vx.Clears[k], n: k => vx.Sets[k]);
		steps.Add(flip.On); steps.Add(g.open); steps.Add(new Input(vy.Probes.SelectMany(p => p))); steps.Add(g.close); steps.Add(flip.Off);
	}

	// Load Temp from a register
	void Read(Steps steps, InstantToggleChain register)
	{
		steps.Add(tempReset);
		ReadOnto(steps, register);
	}
	void ReadOnto(Steps steps, InstantToggleChain register)
	{
		steps.Add(readGates[register].open);
		steps.Add(new Input(register.Probes.SelectMany(p => p)));
		steps.Add(readGates[register].close);
	}

	// What chosen Temp bits do: per bit, the action for a 1 (fired from TempBit) and for a 0 (from TempNot)
	sealed record SinkGates(Relay On, Relay Off, Relay?[] T, Relay?[] N);
	readonly Dictionary<string, SinkGates> sinks = new();
	SinkGates Sink(string name, Func<int, Input>? t = null, Func<int, Input>? n = null)
	{
		if (sinks.TryGetValue(name, out SinkGates? existing)) { return existing; }
		string outer = tag;
		tag = name.StartsWith("decode-") ? $"decoder {name[7..]}" : "bus sinks";
		Relay on = NewRelay(), off = NewRelay();
		var tGates = new Relay?[8]; var nGates = new Relay?[8];
		for (int k = 0; k < 8; k++)
		{
			if (t is not null) { tGates[k] = NewRelay(startDisabled: true); tGates[k]!.Output.Connect(t(k)); }
			if (n is not null) { nGates[k] = NewRelay(startDisabled: true); nGates[k]!.Output.Connect(n(k)); }
		}
		IEnumerable<Relay> gates = tGates.Concat(nGates).OfType<Relay>();
		on.Output.Connect(new Input(gates.SelectMany(g => g.Enable)));
		off.Output.Connect(new Input(gates.SelectMany(g => g.Disable)));
		tag = outer;
		return sinks[name] = new SinkGates(on, off, tGates, nGates);
	}
	// Temp's bits reach the sinks' gates through groups of about sqrt(sinks) sinks: a group's relay for a bit is enabled while one of its
	// sinks is on (only one sink is on at a time), so firing a Temp bit costs about 2 sqrt(sinks) actions instead of one per sink. Wired once
	// every sink exists.
	void WireSinks()
	{
		tag = "bus sinks";
		List<SinkGates> all = sinks.Values.ToList();
		int size = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(all.Count)));
		for (int start = 0; start < all.Count; start += size)
		{
			List<SinkGates> group = all.Skip(start).Take(size).ToList();
			foreach ((Relay[] temps, Func<SinkGates, Relay?[]> gatesOf) in new (Relay[], Func<SinkGates, Relay?[]>)[] { (tempBit, g => g.T), (tempNot, g => g.N) })
			{
				for (int k = 0; k < 8; k++)
				{
					List<(SinkGates sink, Relay gate)> members = group.Where(g => gatesOf(g)[k] is not null).Select(g => (g, gatesOf(g)[k]!)).ToList();
					if (members.Count == 0) { continue; }
					Relay groupGate = NewRelay(startDisabled: true);
					temps[k].Output.Connect(groupGate.Input);
					groupGate.Output.Connect(new Input(members.SelectMany(m => m.gate.Input)));
					foreach ((SinkGates sink, _) in members) { sink.On.Output.Connect(groupGate.Enable); sink.Off.Output.Connect(groupGate.Disable); }
				}
			}
		}
	}
	static readonly int[] AllBits = Enumerable.Range(0, 8).ToArray();
	void Apply(Steps steps, SinkGates sink, IEnumerable<int>? t = null, IEnumerable<int>? n = null)
	{
		steps.Add(sink.On);
		foreach (int k in t ?? Array.Empty<int>()) { steps.Add(tempBit[k]); }
		foreach (int k in n ?? Array.Empty<int>()) { steps.Add(tempNot[k]); }
		steps.Add(sink.Off);
	}
	static IEnumerable<int> BitsOf(int value, int width = 8) => Enumerable.Range(0, width).Where(k => ((value >> k) & 1) == 1);
	static IEnumerable<int> ZerosOf(int value) => AllBits.Where(k => ((value >> k) & 1) == 0);

	// A one-hot decoder of the low `bits` bits of Temp: Reset enables every candidate, Apply knocks out the ones a bit rules out, Fire fires them
	sealed record DecoderRelays(Relay[] Candidates, Relay Reset, Relay Fire, SinkGates Sink);
	// In two stages: the high half of the bits picks a group (its candidate, left after the mismatches are knocked out, enables that group's
	// final relays) and the low half a member (its candidate fires that member in every group; only the enabled group's passes, and disables
	// its group again). About 4 * 2^(bits/2) actions instead of (bits / 2 + 2) * 2^bits: 90 instead of 340 for a 64-way decode.
	DecoderRelays Decoder(int count, int bits, string name)
	{
		string outer = tag;
		tag = $"decoder {name}";
		int loBits = bits / 2, members = 1 << loBits, groups = (count + members - 1) / members;
		Relay[] final = Enumerable.Range(0, count).Select(_ => NewRelay(startDisabled: true)).ToArray();
		Relay[] his = Enumerable.Range(0, groups).Select(_ => NewRelay(startDisabled: true)).ToArray();
		Relay[] los = Enumerable.Range(0, members).Select(_ => NewRelay(startDisabled: true)).ToArray();
		IEnumerable<int> Group(int h) => Enumerable.Range(h * members, members).Where(v => v < count);
		for (int h = 0; h < groups; h++)
		{
			his[h].Output.Connect(new Input(Group(h).SelectMany(v => final[v].Enable)));
			foreach (int v in Group(h)) { final[v].Output.Connect(new Input(Group(h).SelectMany(w => final[w].Disable))); }
		}
		for (int l = 0; l < members; l++)
		{
			int member = l;
			los[l].Output.Connect(new Input(Enumerable.Range(0, groups).Where(h => h * members + member < count).SelectMany(h => final[h * members + member].Input)));
		}
		Relay reset = NewRelay(), fire = NewRelay();
		reset.Output.Connect(new Input(his.Concat(los).SelectMany(c => c.Enable)));
		fire.Output.Connect(new Input(his.SelectMany(c => c.Input).Concat(los.SelectMany(c => c.Input))));
		// Bit k of the value rules out the candidates of its stage that disagree
		Input RuleOut(int k, int bit) => k >= bits ? new Input()
			: k < loBits ? new Input(los.Where((_, l) => ((l >> k) & 1) != bit).SelectMany(c => c.Disable))
			: new Input(his.Where((_, h) => ((h >> (k - loBits)) & 1) != bit).SelectMany(c => c.Disable));
		SinkGates sink = Sink($"decode-{name}", t: k => RuleOut(k, 1), n: k => RuleOut(k, 0));
		tag = outer;
		return new DecoderRelays(final, reset, fire, sink);
	}
	void Decode(Steps steps, DecoderRelays decoder, int bits)
	{
		steps.Add(decoder.Reset);
		Apply(steps, decoder.Sink, t: AllBits.Take(bits), n: AllBits.Take(bits));
		steps.Add(decoder.Fire);
	}

	Relay equalLatch = null!, keyLatch = null!, carryLatch = null!, collisionLatch = null!, nonZeroLatch = null!;
	Relay[] keyHeld = null!;
	DecoderRelays keyDecoder = null!;
	readonly Dictionary<InstantToggleChain, Relay> carryGates = new();
	Relay CarryGate(InstantToggleChain register)
	{
		if (carryGates.TryGetValue(register, out Relay? gate)) { return gate; }
		gate = NewRelay(startDisabled: true);
		register.Overflow.Connect(gate.Input);
		gate.Output.Connect(carryLatch.Enable);
		return carryGates[register] = gate;
	}

	// The shuffle table lookup for CXNN: candidate v loads Temp with Table[v]
	DecoderRelays? randomDecoder;
	DecoderRelays RandomDecoder()
	{
		if (randomDecoder is not null) { return randomDecoder; }
		string outer = tag;
		randomDecoder = Decoder(256, 8, "random");
		for (int v = 0; v < 256; v++)
		{
			int value = Chip8Random.Table[v];
			randomDecoder.Candidates[v].Output.Connect(tempReset.Input.Concat(new Input(BitsOf(value).SelectMany(k => tempBit[k].Enable.Concat(tempNot[k].Disable)))));
		}
		tag = outer;
		return randomDecoder;
	}
}
