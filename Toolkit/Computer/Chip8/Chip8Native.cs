using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// Fused routines compiled as one native step (Chip8Quirks.FusedRoutines): Danm8ku's move_bullet. The routine's net effect, worked out from
// its instructions at 0x3BC to 0x41E (danm8ku.8o, move_bullet), is built straight from register bits, latches and counting, with no
// dispatch inside and no temporaries:
// - x: the fraction V6 gets V0 << 1 added (V0's bits 0 to 6 counted into V6's bits 1 to 7); a carry out moves V4 one step, up for a positive
//   V0 and down (adding 255) for a negative one (bit 7). V8 is that step (0, 1 or 255) and V3's low bits say which (0, 1 or 2).
// - y: the same with V1, V7 and V5; VD is (the step, 255 counted as 2) times 4, which is also V3's high bits, and VA is the sign and the carry
//   together. VC is a copy of V1.
// - V0 gets V2 added unless it is 250 or more, and keeps its old value if that overflows.
// - V4 > 62 goes on to the destroy call at 0x412, else V5 > 29 to the one at 0x41C, both with VF 0; otherwise the return at 0x41E with VF 1.
// Registers that nothing reads before they are written again are not written (the machine liveness says which).
public partial class Chip8Machine
{
	// Fires `above` when the register is more than `limit`. Per comparison, a latch pair per bit set from one probe (One[k] enabled while bit
	// k is 1, Zero[k] while it is 0), walked from the top bit down: a latch that matches the limit's bit passes on to the next bit, and the
	// first bit that is 1 where the limit has a 0 fires `above` (the first one that is 0 where the limit has a 1 stops: below).
	readonly Dictionary<(InstantToggleChain, int), (Relay[] one, Relay[] zero, (Relay open, Relay close) gates)> comparators = new();
	void IfAbove(Steps steps, InstantToggleChain register, int limit, Input above)
	{
		if (!comparators.TryGetValue((register, limit), out var latches))
		{
			string outer = tag; tag = "native";
			Relay[] one = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			Relay[] zero = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			for (int k = 0; k < 8; k++)
			{
				Input next = k > 0 ? one[k - 1].Input.Concat(zero[k - 1].Input) : new Input();
				if (((limit >> k) & 1) == 0) { one[k].Output.Connect(above); zero[k].Output.Connect(next); }
				else { one[k].Output.Connect(next); }
			}
			var gates = OnesOf($"above {limit}", register, register, k => one[k].Enable.Concat(zero[k].Disable));
			comparators[(register, limit)] = latches = (one, zero, gates);
			tag = outer;
		}
		steps.Add(new Input(latches.one.SelectMany(r => r.Disable)).Concat(new Input(latches.zero.SelectMany(r => r.Enable))));
		ProbeOnes(steps, latches.gates, register);
		steps.Add(latches.one[7].Input.Concat(latches.zero[7].Input));
	}

	// move_bullet's work on the registers, shared by everything that moves a bullet (built once per set of leftovers wanted): it leaves
	// OutX enabled when the bullet is past the right edge (x > 62), OutY when it is past the bottom (y > 29) and Out for either. Whoever
	// fires it disables the three afterwards.
	sealed record MoveCore(Relay Start, Relay OutX, Relay OutY, Relay Out);
	// `record`: the bullet's eight bytes (V0 to V7 for move_bullet itself, a slot's toggle rows for the native act_bullets)
	readonly Dictionary<(int, InstantToggleChain), MoveCore> moveCores = new();
	// `hooks`: what else happens at the start and on each step (x up, x down, y up, y down): the native act_bullets keeps its slot caches
	// Accelerate: a latch the acceleration runs through (enabled while ax isn't 0), OnAccelerate what else happens when it does. Cached and
	// Fresh: latches for the bounds checks (one enabled at a time): Cached checks the position caches' watchers (x 63, y 30 or 31), Fresh
	// compares the bytes and then enables Cached.
	// OnSaturate: what happens when V0 is 250 or more (it never accelerates again, until a store to its record).
	sealed record CoreHooks(Input Start, Input XUp, Input XDown, Input YUp, Input YDown, Relay Accelerate, Input OnAccelerate, Input OnSaturate,
		Relay Cached, Relay Fresh, PositionCache X, PositionCache Y);
	MoveCore MoveBulletCore(int needed, InstantToggleChain[]? record = null, CoreHooks? hooks = null)
	{
		record ??= V.Take(8).ToArray();
		needed &= (1 << 8) | (1 << 0xA) | (1 << 0xC) | (1 << 0xD);
		if (moveCores.TryGetValue((needed, record[0]), out MoveCore? known)) { return known; }
		bool Need(int r) => ((needed >> r) & 1) == 1;
		InstantToggleChain vx = record[0], vy = record[1], ax = record[2], so = record[3], x = record[4], y = record[5], xf = record[6], yf = record[7];
		string outer = tag; tag = "native";
		Steps s = new(this);
		void Clear(InstantToggleChain r) => s.Add(new Input(r.Clears.SelectMany(c => c)));
		Clear(so);
		if (hooks is not null) { s.Add(hooks.Start); }
		if (Need(8)) { Clear(V[8]); }
		if (Need(0xD)) { Clear(V[0xD]); }
		if (Need(0xA)) { Clear(V[0xA]); }
		if (Need(0xC)) { Copy(s, vy, V[0xC]); }

		// One axis: the fraction gets the velocity << 1, a carry moves the position one step by the velocity's sign
		void Axis(InstantToggleChain velocity, InstantToggleChain fraction, InstantToggleChain position, Input onUp, Input onDown)
		{
			Relay carry = NewRelay(startDisabled: true), carryGate = NewRelay(startDisabled: true);
			Relay negative = NewRelay(startDisabled: true), positive = NewRelay();
			fraction.Overflow.Connect(carryGate.Input);
			carryGate.Output.Connect(carry.Enable);
			var shifted = OnesOf("native velocity<<1 into fraction", velocity, fraction,
				k => k < 7 ? fraction.Inputs[k + 1] : negative.Enable.Concat(positive.Disable));
			s.Add(carryGate.Enable);
			ProbeOnes(s, shifted, velocity);
			s.Add(carryGate.Disable);
			Steps up = new(this), down = new(this);
			up.Add(position.Inputs[0]); up.Add(onUp);
			for (int k = 0; k < 8; k++) { down.Add(position.Inputs[k]); }
			down.Add(onDown);
			ChainSteps(up); ChainSteps(down);
			positive.Output.Connect(up.Relays[0].Input);
			negative.Output.Connect(down.Relays[0].Input);
			carry.Output.Connect(positive.Input.Concat(negative.Input));
			s.Add(carry.Input);
			s.Add(carry.Disable.Concat(negative.Disable).Concat(positive.Enable));
		}
		Input Bits(InstantToggleChain r, int value) => new(BitsOf(value).SelectMany(k => r.Sets[k]));
		Input When(bool need, Input input) => need ? input : new Input();
		Axis(vx, xf, x, Bits(so, 1).Concat(When(Need(8), Bits(V[8], 1))).Concat(hooks?.XUp ?? new Input()),
			Bits(so, 2).Concat(When(Need(8), Bits(V[8], 0xFF))).Concat(hooks?.XDown ?? new Input()));
		Axis(vy, yf, y, Bits(so, 4).Concat(When(Need(0xD), Bits(V[0xD], 4))).Concat(hooks?.YUp ?? new Input()),
			Bits(so, 8).Concat(When(Need(0xD), Bits(V[0xD], 8))).Concat(When(Need(0xA), Bits(V[0xA], 1))).Concat(hooks?.YDown ?? new Input()));

		// Acceleration: unless V0 >= 250, V0 += V2, undone if it overflows
		{
			Steps outer2 = s;
			if (hooks is not null) { s = new Steps(this); }
			Relay skip = NewRelay(startDisabled: true), accelerate = NewRelay();
			IfAbove(s, vx, 249, skip.Enable);
			skip.Output.Connect(accelerate.Disable.Concat(hooks?.OnSaturate ?? new Input()));
			s.Add(skip.Input);
			Relay overflow = NewRelay(startDisabled: true), overflowGate = NewRelay(startDisabled: true);
			vx.Overflow.Connect(overflowGate.Input);
			overflowGate.Output.Connect(overflow.Enable);
			Steps add = new(this);
			add.Add(overflowGate.Enable);
			ProbeOnes(add, OnesOf("native add V2 into V0", ax, vx, k => vx.Inputs[k]), ax);
			add.Add(overflowGate.Disable);
			// undo: V0 - V2 = V0 + ~V2 + 1 (its own carry out is lost, as the original's flag is overwritten)
			Steps undo = new(this);
			ProbeZeros(undo, ZerosOf("native sub V2 from V0", ax, vx, k => vx.Inputs[k]), ax);
			undo.Add(vx.Inputs[0]);
			ChainSteps(undo);
			overflow.Output.Connect(undo.Relays[0].Input.Concat(overflow.Disable));
			add.Add(overflow.Input);
			if (hooks is not null) { add.Add(hooks.OnAccelerate); }
			ChainSteps(add);
			accelerate.Output.Connect(add.Relays[0].Input);
			s.Add(accelerate.Input);
			s.Add(accelerate.Enable.Concat(skip.Disable));
			if (hooks is not null)
			{
				ChainSteps(s);
				hooks.Accelerate.Output.Connect(s.Relays[0].Input);
				s = outer2;
				s.Add(hooks.Accelerate.Input);
			}
		}
		Relay outX = NewRelay(startDisabled: true), outY = NewRelay(startDisabled: true), outAny = NewRelay(startDisabled: true);
		if (hooks is null)
		{
			IfAbove(s, x, 62, outX.Enable.Concat(outAny.Enable));
			IfAbove(s, y, 29, outY.Enable.Concat(outAny.Enable));
		}
		else
		{
			// a bullet that passed last tick's checks has x <= 62 and y <= 29, so after one step x > 62 is x mod 64 = 63 (63 or 255) and y > 29
			// is y mod 32 = 30 or 31 (30 or 255); after a store to its record the bytes are compared
			foreach (Relay w in hooks.X.Watchers) { w.Output.Connect(outX.Enable.Concat(outAny.Enable)); }
			foreach (Relay w in hooks.Y.Watchers) { w.Output.Connect(outY.Enable.Concat(outAny.Enable)); }
			hooks.Cached.Output.Connect(hooks.X.Watch.Input.Concat(hooks.Y.Watch.Input));
			Steps compare = new(this);
			IfAbove(compare, x, 62, outX.Enable.Concat(outAny.Enable));
			IfAbove(compare, y, 29, outY.Enable.Concat(outAny.Enable));
			ChainSteps(compare);
			hooks.Fresh.Output.Connect(hooks.Fresh.Disable.Concat(hooks.Cached.Enable).Concat(compare.Relays[0].Input));
			s.Add(hooks.Cached.Input);
			s.Add(hooks.Fresh.Input);
		}
		ChainSteps(s);
		tag = outer;
		return moveCores[(needed, record[0])] = new MoveCore(s.Relays[0], outX, outY, outAny);
	}

	// move_bullet as one step: the core, then x > 62 on to the destroy call at 0x412, else y > 29 to the one at 0x41C (VF 0 either way),
	// else the return at 0x41E (VF 1)
	void CompileMoveBullet(Steps s, Node node, int needed)
	{
		string outer = tag; tag = "native";
		MoveCore core = MoveBulletCore(needed);
		s.Add(core.Start.Input);
		Input Flag(int value, int exit) => ((machineLive[node.Succ[exit]] >> 0xF) & 1) == 1
			? new Input(V[0xF].Clears.SelectMany(c => c)).Concat(value == 1 ? V[0xF].Sets[0] : new Input()) : new Input();
		Relay leaveX = NewRelay(startDisabled: true), leaveY = NewRelay(startDisabled: true), checkY = NewRelay(), stay = NewRelay();
		(Input armX, Input disarmX) = OnLatch(core.OutX, leaveX);
		(Input armY, Input disarmY) = OnLatch(core.OutY, leaveY);
		Steps toX = new(this), toY = new(this), toStay = new(this), yTest = new(this);
		toX.Add(checkY.Disable.Concat(stay.Disable)); toX.Add(Flag(0, 0x412)); toX.Add(Go(node.Id, node.Succ[0x412]));
		toY.Add(stay.Disable); toY.Add(Flag(0, 0x41C)); toY.Add(Go(node.Id, node.Succ[0x41C]));
		toStay.Add(Flag(1, 0x41E)); toStay.Add(Go(node.Id, node.Succ[0x41E]));
		yTest.Add(armY); yTest.Add(core.OutY.Input); yTest.Add(disarmY);
		foreach (Steps chain in new[] { toX, toY, toStay, yTest }) { ChainSteps(chain); }
		leaveX.Output.Connect(toX.Relays[0].Input);
		leaveY.Output.Connect(toY.Relays[0].Input);
		checkY.Output.Connect(yTest.Relays[0].Input);
		stay.Output.Connect(toStay.Relays[0].Input);
		s.Add(armX); s.Add(core.OutX.Input); s.Add(disarmX);
		s.Add(checkY.Input);
		s.Add(stay.Input);
		s.Add(core.OutX.Disable.Concat(core.OutY.Disable).Concat(core.Out.Disable).Concat(checkY.Enable).Concat(stay.Enable));
		tag = outer;
	}

	// A table of 4-row sprites at base + ((v << 2) & 0xFF), one for each value v of an offset register (Danm8ku's movement sprites from
	// bul_nop, its clear sprites from clr_nop): a decoder over the value whose candidate v draws that sprite's rows at the decoded position,
	// rows with nothing in them skipped, rows that are RAM read from memory at their (known) address
	readonly Dictionary<int, DecoderRelays> spriteTables = new();
	// the first relay of each value's drawing (none for a value that draws nothing)
	readonly Dictionary<(int table, int value), Relay> spriteVariants = new();
	DecoderRelays SpriteTable(int baseAddress)
	{
		if (spriteTables.TryGetValue(baseAddress, out DecoderRelays? known)) { return known; }
		// the values the move core leaves in so (an x step 0, 1 or 2, plus a y step 0, 4 or 8), and bul (3, pew_pew's) in the movement table:
		// drawn through the narrow x select
		foreach (int v in new[] { 0, 1, 2, 4, 5, 6, 8, 9, 10 }.Concat(baseAddress == 0x272 ? new[] { 3 } : Array.Empty<int>()))
		{
			for (int r = 0; r < 4; r++)
			{
				int address = (baseAddress + ((v << 2) & 0xFF) + r) & 0xFFF;
				if (Ram.Contains(address) || (memory[address] & ((1 << (8 - NarrowColumns)) - 1)) != 0) { throw new NotSupportedException($"sprite {v} of the table at {baseAddress:X3} isn't within the narrow x select's columns"); }
			}
		}
		string outer = tag; tag = "native";
		// only the offset's low 6 bits matter ((v << 2) & 0xFF)
		DecoderRelays table = Decoder(64, 6, $"sprite table {baseAddress:X3}");
		for (int v = 0; v < 64; v++)
		{
			int at = baseAddress + ((v << 2) & 0xFF);
			Steps variant = new(this);
			DrawConstantRows(variant, Enumerable.Range(0, 4).Where(r => !Ram.Contains((at + r) & 0xFFF)).Select(r => (r, (int)memory[(at + r) & 0xFFF])));
			for (int r = 0; r < 4; r++)
			{
				int address = (at + r) & 0xFFF;
				if (Ram.Contains(address))
				{
					// a variable stored among the sprites (Danm8ku keeps bullet_count, player_position and pattern_state where no movement
					// offset points): its row is read from memory
					variant.Add(tempReset);
					ReadConstant(variant, address);
					variant.Add(rowLines[r].arm);
					Apply(variant, Sink("sprite", t: k => offsetLines[7 - k].Input), t: AllBits);
					variant.Add(rowLines[r].disarm);
					continue;
				}
			}
			if (variant.Relays.Count == 0) { continue; }
			ChainSteps(variant);
			table.Candidates[v].Output.Connect(variant.Relays[0].Input);
			spriteVariants[(baseAddress, v)] = variant.Relays[0];
		}
		tag = outer;
		return spriteTables[baseAddress] = table;
	}

	// draw_bullet (0x434: i := bul_nop, so <<= so twice, i += so, sprite x y 4): V3 is decoded once and each of its values draws its own
	// sprite; V3 shifted, I and the collision flag are written only where something reads them
	void CompileDrawBullet(Steps s, Node node, int needed)
	{
		bool Need(int r) => ((needed >> r) & 1) == 1;
		const int bulNop = 0x272;
		string outer = tag; tag = "native";
		DecodeFrom(s, V[4], xDecoder, 6);
		DecodeFrom(s, V[5], yDecoders[4].decoder, 5);
		s.Add(collisionLatch.Disable);
		DecodeFrom(s, V[3], SpriteTable(bulNop), 6);
		s.Add(unsetX); s.Add(yDecoders[4].unset);
		if (Need(0xF)) { s.Add(new Input(V[0xF].Clears.SelectMany(c => c))); s.Add(collisionLatch.Input); }
		if (Need(16))
		{
			// I = bul_nop + (V3 << 2): V3's bits 0 to 5 counted into I's bits 2 to 7 (before V3 itself is shifted)
			s.Add(clearI); s.Add(new Input(BitsOf(bulNop, 12).SelectMany(k => IRegister.Sets[k])));
			ProbeOnes(s, OnesOf("native V3<<2 into I", V[3], IRegister, k => k < 6 ? IRegister.Inputs[k + 2] : new Input()), V[3]);
		}
		if (Need(3)) { ShiftV3TwiceLeft(s); }
		s.Add(Go(node.Id, node.Succ[0x43E]));
		tag = outer;
	}
	// V3 <<= 2 in place: each bit set from the one two below it, top down, after it has been cleared
	void ShiftV3TwiceLeft(Steps s)
	{
		var gates = OnesOf("native V3<<2", V[3], V[3], k => k < 6 ? V[3].Sets[k + 2] : new Input());
		s.Add(gates.open);
		for (int k = 7; k >= 0; k--) { s.Add(V[3].Clears[k]); if (k >= 2) { s.Add(V[3].Probes[k - 2]); } }
		s.Add(gates.close);
	}

	// Whether a bullet slot is live: its first byte (the x velocity) isn't 255. A witness per bit of the row (armed, disarmed by the cell
	// when it holds a 1); one still armed after the cells are read fires `live`. A set of witnesses per row and loop.
	const int BulletArea = 0xBB8;
	readonly Dictionary<(int address, string loop), Relay[]> slotWitnesses = new();
	void TestSlot(Steps s, int address, string loop, Input live)
	{
		if (toggleRows.TryGetValue(address, out ToggleMemoryRow? toggled))
		{
			// a toggle row: any bit that is 0 fires `live`
			ProbeZeros(s, ZerosOf($"slot live {loop}", toggled.Byte, toggled.Byte, k => live), toggled.Byte);
			return;
		}
		Bank? bank = Banks.FirstOrDefault(b => address >= b.Start && address < b.End);
		if (bank is null) { s.Add(live); return; }   // past every bank the byte reads 0: live
		if (!slotWitnesses.TryGetValue((address, loop), out Relay[]? witnesses))
		{
			witnesses = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			IList<Output> cells = bank.Memory.RowValues(address - bank.Start);
			for (int k = 0; k < 8; k++) { cells[k].Connect(witnesses[k].Disable); witnesses[k].Output.Connect(live); }
			slotWitnesses[(address, loop)] = witnesses;
		}
		s.Add(new Input(witnesses.SelectMany(w => w.Enable)));
		s.Add(bank.Memory.RowCellRead(address - bank.Start));
		s.Add(new Input(witnesses.SelectMany(w => w.Input)));
		s.Add(new Input(witnesses.SelectMany(w => w.Disable)));
	}
	// A byte of a known address through a decoder: straight from the bits of a toggle row, else through Temp
	void DecodeAt(Steps s, int address, DecoderRelays decoder, int bits)
	{
		if (toggleRows.TryGetValue(address, out ToggleMemoryRow? toggled)) { DecodeFrom(s, toggled.Byte, decoder, bits); return; }
		s.Add(tempReset);
		ReadConstant(s, address);
		Decode(s, decoder, bits);
	}
	// A register's (or a toggle row's) low bits through a decoder without Temp: its 1s knock out candidates through gates, its 0s through
	// not relays, firing the decoder sink's own per-bit relays
	readonly Dictionary<DecoderRelays, int> decoderIds = new(ReferenceEqualityComparer.Instance);
	// shift: the value is the register's bits from that one up (a register halved, without halving it)
	void DecodeFrom(Steps s, InstantToggleChain from, DecoderRelays decoder, int bits, int shift = 0)
	{
		SinkGates sink = decoder.Sink;
		if (!decoderIds.TryGetValue(decoder, out int id)) { decoderIds[decoder] = id = decoderIds.Count; }
		string name = shift == 0 ? $"decode {id}" : $"decode {id} >> {shift}";
		bool In(int k) => k >= shift && k - shift < bits;
		var ones = OnesOf(name, from, from, k => In(k) && sink.T[k - shift] is Relay t ? t.Input : new Input());
		var zeros = ZerosOf(name, from, from, k => In(k) && sink.N[k - shift] is Relay n ? n.Input : new Input());
		s.Add(decoder.Reset);
		s.Add(sink.On);
		s.Add(zeros.arm);
		ProbeOnes(s, ones, from);
		s.Add(zeros.fire);
		s.Add(sink.Off);
		s.Add(decoder.Fire);
	}

	// A one-hot copy of a byte's low bits (a bullet's x mod 64 or y mod 32), so drawing fires the screen's select relay for the value instead
	// of decoding the byte. Three sets of relays are kept enabled for the current value, one for each thing it does: Draw fires its select
	// relay, Up and Down move it one step. Each set is in groups of 8 values with a group relay enabled for the current value's group, so a
	// sweep costs about 16 actions. A step disables its own value and enables the next; Up sweeps the groups and their members from the top
	// down and Down from the bottom up, so the value it moves to has had its turn already, and a wrap goes through a relay fired after the
	// sweep. Moving into a group turns off its other members, which may be stale; groups that are off may hold stale members. `Resync`
	// decodes the byte into the cache (it turns off every other group). A watcher is enabled while the value is the one it watches; Watch
	// fires them through relays kept with their groups (a stale member's watcher can't fire).
	sealed record PositionCache(Relay Draw, Relay Up, Relay Down, Steps Resync, Relay Watch, Relay[] Watchers);
	PositionCache BuildPositionCache(string name, int values, int bits, Relay[] select, InstantToggleChain from, int[] watch)
	{
		string outer = tag; tag = "bullet cache";
		int groups = values / 8;
		Relay[] Relays(int n) => Enumerable.Range(0, n).Select(_ => NewRelay(startDisabled: true)).ToArray();
		Relay[] d = Relays(values), u = Relays(values), n = Relays(values), gd = Relays(groups), gu = Relays(groups), gn = Relays(groups);
		Relay[] watchers = Relays(watch.Length);
		Relay?[] groupWatch = Enumerable.Range(0, groups).Select(g => watch.Any(v => v / 8 == g) ? NewRelay(startDisabled: true) : null).ToArray();
		Input Watcher(int v, bool on) => Array.IndexOf(watch, v) is int i && i >= 0 ? (on ? watchers[i].Enable : watchers[i].Disable) : new Input();
		Input GroupWatch(int g, bool on) => groupWatch[g] is Relay w ? (on ? w.Enable : w.Disable) : new Input();
		Input Member(int v, bool on) => (on ? d[v].Enable.Concat(u[v].Enable).Concat(n[v].Enable) : d[v].Disable.Concat(u[v].Disable).Concat(n[v].Disable)).Concat(Watcher(v, on));
		Input Group(int g, bool on) => (on ? gd[g].Enable.Concat(gu[g].Enable).Concat(gn[g].Enable) : gd[g].Disable.Concat(gu[g].Disable).Concat(gn[g].Disable)).Concat(GroupWatch(g, on));
		// becoming v (from a value in another group, or from nothing): its group on, the group's other members off
		Input Become(int v, int? from)
		{
			int g = v / 8;
			Input input = Member(v, true);
			if (from is int f && f / 8 == g) { return input; }
			return input.Concat(Group(g, true)).Concat(new Input(Enumerable.Range(g * 8, 8).Where(w => w != v).SelectMany(w => Member(w, false))));
		}
		Input Leave(int v, int to) => to / 8 == v / 8 ? Member(v, false) : Member(v, false).Concat(Group(v / 8, false));
		Relay wrapUp = NewRelay(startDisabled: true), wrapDown = NewRelay(startDisabled: true);
		for (int v = 0; v < values; v++)
		{
			d[v].Output.Connect(select[v].Input);
			u[v].Output.Connect(v < values - 1 ? Leave(v, v + 1).Concat(Become(v + 1, v)) : Member(v, false).Concat(Group(v / 8, false)).Concat(wrapUp.Enable));
			n[v].Output.Connect(v > 0 ? Leave(v, v - 1).Concat(Become(v - 1, v)) : Member(v, false).Concat(Group(0, false)).Concat(wrapDown.Enable));
		}
		wrapUp.Output.Connect(wrapUp.Disable.Concat(Become(0, null)));
		wrapDown.Output.Connect(wrapDown.Disable.Concat(Become(values - 1, null)));
		for (int g = 0; g < groups; g++)
		{
			int group = g;
			gd[g].Output.Connect(new Input(Enumerable.Range(group * 8, 8).SelectMany(v => d[v].Input)));
			gu[g].Output.Connect(new Input(Enumerable.Range(group * 8, 8).Reverse().SelectMany(v => u[v].Input)));
			gn[g].Output.Connect(new Input(Enumerable.Range(group * 8, 8).SelectMany(v => n[v].Input)));
		}
		Relay draw = NewRelay(), up = NewRelay(), down = NewRelay(), watchAll = NewRelay();
		for (int g = 0; g < groups; g++)
		{
			if (groupWatch[g] is not Relay w) { continue; }
			int group = g;
			w.Output.Connect(new Input(Enumerable.Range(0, watch.Length).Where(i => watch[i] / 8 == group).SelectMany(i => watchers[i].Input)));
		}
		watchAll.Output.Connect(new Input(groupWatch.OfType<Relay>().SelectMany(w => w.Input)));
		draw.Output.Connect(new Input(gd.SelectMany(g => g.Input)));
		up.Output.Connect(new Input(gu.Reverse().SelectMany(g => g.Input)).Concat(wrapUp.Input));
		down.Output.Connect(new Input(gn.SelectMany(g => g.Input)).Concat(wrapDown.Input));
		DecoderRelays decoder = Decoder(values, bits, name);
		tag = "bullet cache";
		for (int v = 0; v < values; v++)
		{
			int g = v / 8;
			decoder.Candidates[v].Output.Connect(Become(v, null).Concat(new Input(Enumerable.Range(0, groups).Where(h => h != g).SelectMany(h => Group(h, false)))));
		}
		Steps resync = new(this);
		DecodeFrom(resync, from, decoder, bits);
		tag = outer;
		return new PositionCache(draw, up, down, resync, watchAll, watchers);
	}

	// A slot's caches: its position (x and y), and which movement sprite it draws next. The move core decides so completely (an x case:
	// none, +1 or -1, and a y case), so it also sets one of three x relays and, in three copies (one per x case), one of three y relays; the
	// y relay fires that sprite's drawing. Moved is enabled when either case isn't none. A store to the slot's x or y (a new bullet) marks its
	// position dirty, to be decoded before the slot is next used; a store to its so marks its sprite dirty until the core next runs, and the
	// draw decodes so meanwhile.
	// LiveDraw and LiveAct (one for each loop, kept together): enabled while the slot holds a bullet (vx isn't 255), tested again (LiveDirty)
	// after a store to vx or an acceleration. The acceleration's latch (ax isn't 0) and FrozenCheck (ax is 6 or more) are tested again
	// (AxDirty) after a store to ax or vx, and both turn off once V0 is 250 or more.
	sealed record SlotCache(PositionCache X, PositionCache Y, Relay Dirty, Relay SpriteDirty, Relay SpriteClean, Relay Moved, Relay[] XCase, CoreHooks Hooks,
		Relay LiveDraw, Relay LiveAct, Relay LiveDirty, Relay AxDirty, Relay FrozenCheck)
	{
		public Input NotLive => LiveDraw.Disable.Concat(LiveAct.Disable);
	}
	readonly Dictionary<int, SlotCache> slotCaches = new();
	SlotCache CacheOf(int slot)
	{
		if (slotCaches.TryGetValue(slot, out SlotCache? known)) { return known; }
		SpriteTable(0x272);
		string outer = tag; tag = "bullet cache";
		int at = BulletArea + 8 * slot;
		PositionCache x = BuildPositionCache($"cache {slot} x", Width, 6, xSelect, toggleRows[at + 4].Byte, new[] { 63 });
		PositionCache y = BuildPositionCache($"cache {slot} y", Height, 5, ySelects[4], toggleRows[at + 5].Byte, new[] { 30, 31 });
		tag = "bullet cache";
		Relay dirty = NewRelay(), spriteDirty = NewRelay(), spriteClean = NewRelay(startDisabled: true), moved = NewRelay(startDisabled: true);
		ChainSteps(x.Resync); ChainSteps(y.Resync);
		x.Resync.Relays[^1].Output.Connect(y.Resync.Relays[0].Input);
		dirty.Output.Connect(dirty.Disable.Concat(x.Resync.Relays[0].Input));
		Relay liveDraw = NewRelay(startDisabled: true), liveAct = NewRelay(startDisabled: true), liveDirty = NewRelay(), axDirty = NewRelay(), accelerate = NewRelay(startDisabled: true);
		Relay frozenCheck = NewRelay(startDisabled: true);
		Relay cached = NewRelay(startDisabled: true), fresh = NewRelay();
		Input unchecked_ = fresh.Enable.Concat(cached.Disable);
		toggleRows[at + 0].Written.Connect(liveDirty.Enable.Concat(unchecked_).Concat(axDirty.Enable));
		toggleRows[at + 2].Written.Connect(axDirty.Enable);
		toggleRows[at + 4].Written.Connect(dirty.Enable.Concat(unchecked_));
		toggleRows[at + 5].Written.Connect(dirty.Enable.Concat(unchecked_));
		toggleRows[at + 3].Written.Connect(spriteDirty.Enable.Concat(spriteClean.Disable));
		Steps testLive = new(this);
		testLive.Add(liveDirty.Disable.Concat(liveDraw.Disable).Concat(liveAct.Disable));
		TestSlot(testLive, at, "cache", liveDraw.Enable.Concat(liveAct.Enable));
		ChainSteps(testLive);
		liveDirty.Output.Connect(testLive.Relays[0].Input);
		Steps testAx = new(this);
		testAx.Add(axDirty.Disable.Concat(accelerate.Disable).Concat(frozenCheck.Disable));
		InstantToggleChain ax = toggleRows[at + 2].Byte;
		ProbeOnes(testAx, OnesOf("ax nonzero", ax, ax, k => accelerate.Enable), ax);
		// only an ax of 6 or more can take a V0 under 250 to exactly 255 (see CompileActBullets)
		IfAbove(testAx, ax, 5, frozenCheck.Enable);
		ChainSteps(testAx);
		axDirty.Output.Connect(testAx.Relays[0].Input);
		Relay[] xCase = Enumerable.Range(0, 3).Select(_ => NewRelay(startDisabled: true)).ToArray();
		Relay[,] yCase = new Relay[3, 3];
		for (int i = 0; i < 3; i++)
		{
			for (int j = 0; j < 3; j++)
			{
				yCase[i, j] = NewRelay(startDisabled: true);
				int so = (i == 1 ? 1 : i == 2 ? 2 : 0) | (j == 1 ? 4 : j == 2 ? 8 : 0);
				if (spriteVariants.TryGetValue((0x272, so), out Relay? variant)) { yCase[i, j].Output.Connect(variant.Input); }
			}
			int xc = i;
			xCase[i].Output.Connect(new Input(Enumerable.Range(0, 3).SelectMany(j => yCase[xc, j].Input)));
		}
		Input YCase(int j, bool on) => new(Enumerable.Range(0, 3).SelectMany(i => on ? yCase[i, j].Enable : yCase[i, j].Disable));
		CoreHooks hooks = new(
			xCase[0].Enable.Concat(xCase[1].Disable).Concat(xCase[2].Disable).Concat(YCase(0, true)).Concat(YCase(1, false)).Concat(YCase(2, false))
				.Concat(moved.Disable).Concat(spriteDirty.Disable).Concat(spriteClean.Enable),
			xCase[0].Disable.Concat(xCase[1].Enable).Concat(moved.Enable).Concat(x.Up.Input),
			xCase[0].Disable.Concat(xCase[2].Enable).Concat(moved.Enable).Concat(x.Down.Input),
			YCase(0, false).Concat(YCase(1, true)).Concat(moved.Enable).Concat(y.Up.Input),
			YCase(0, false).Concat(YCase(2, true)).Concat(moved.Enable).Concat(y.Down.Input),
			accelerate, liveDirty.Enable, accelerate.Disable.Concat(frozenCheck.Disable), cached, fresh, x, y);
		tag = outer;
		return slotCaches[slot] = new SlotCache(x, y, dirty, spriteDirty, spriteClean, moved, xCase, hooks, liveDraw, liveAct, liveDirty, axDirty, frozenCheck);
	}

	// update_player (0x498 to 0x4E0) as one step: the position (player_position, 0x28F: V0 across, V1 down, both doubled) is loaded, the
	// player (player_sprite, 0x2AA, 3 rows) erased at half of it, decoded straight from the position's bits 1 up (V2 and V3, the halves,
	// aren't computed unless something reads them), each held key moves the position one step and keeps it in bounds (5: V1 - 1, not below
	// 2; 7: V0 - 1, not below 2; 9: V0 + 1, not above 34; 8: V1 + 1, not above 57; in that order), the position stored, and the player drawn
	// again, VF the collision. I is left at player_sprite.
	void CompileUpdatePlayer(Steps s, Node node, int needed)
	{
		bool Need(int r) => ((needed >> r) & 1) == 1;
		const int position = 0x28F, playerSprite = 0x2AA;
		if (Enumerable.Range(0, 3).Any(r => Ram.Contains(playerSprite + r))) { throw new NotSupportedException("the native update_player draws player_sprite as constant rows"); }
		string outer = tag; tag = "native";
		Input ClearChain(InstantToggleChain r) => new(r.Clears.SelectMany(c => c));
		void Draw(Steps steps)
		{
			DecodeFrom(steps, V[0], xDecoder, 6, shift: 1);
			DecodeFrom(steps, V[1], yDecoders[3].decoder, 5, shift: 1);
			steps.Add(collisionLatch.Disable);
			DrawConstantRows(steps, Enumerable.Range(0, 3).Select(r => (r, (int)memory[playerSprite + r])));
			steps.Add(unsetX); steps.Add(yDecoders[3].unset);
		}
		// the steps run only while `latch` is on
		void IfLatch(Steps steps, Relay latch, Steps then)
		{
			ChainSteps(then);
			Relay site = NewRelay(startDisabled: true);
			site.Output.Connect(then.Relays[0].Input);
			(Input arm, Input disarm) = OnLatch(latch, site);
			steps.Add(arm); steps.Add(latch.Input); steps.Add(disarm);
		}
		LoadConstant(s, position, V[0]);
		LoadConstant(s, position + 1, V[1]);
		Draw(s);
		foreach ((int key, int r, bool up, int bound, int fix) in new[] { (5, 1, false, 1, 2), (7, 0, false, 1, 2), (9, 0, true, 35, 34), (8, 1, true, 58, 57) })
		{
			Steps move = new(this), back = new(this);
			if (up) { move.Add(V[r].Inputs[0]); } else { for (int k = 0; k < 8; k++) { move.Add(V[r].Inputs[k]); } }
			CompareWithConstant(move, V[r], bound);
			back.Add(ClearChain(V[r])); back.Add(new Input(BitsOf(fix).SelectMany(k => V[r].Sets[k])));
			IfLatch(move, equalLatch, back);
			s.Add(keyLatch.Disable);
			s.Add(keyHeld[key].Input);
			IfLatch(s, keyLatch, move);
		}
		StoreConstant(s, position, V[0], null);
		StoreConstant(s, position + 1, V[1], null);
		Draw(s);
		if (Need(0xF)) { s.Add(ClearChain(V[0xF])); s.Add(collisionLatch.Input); }
		if (Need(2)) { s.Add(ClearChain(V[2])); ProbeOnes(s, OnesOf("shr", V[0], V[2], k => k > 0 ? V[2].Sets[k - 1] : new Input()), V[0]); }
		if (Need(3)) { s.Add(ClearChain(V[3])); ProbeOnes(s, OnesOf("shr", V[1], V[3], k => k > 0 ? V[3].Sets[k - 1] : new Input()), V[1]); }
		if (Need(16)) { s.Add(clearI); s.Add(new Input(BitsOf(playerSprite, 12).SelectMany(k => IRegister.Sets[k]))); }
		s.Add(Go(node.Id, node.Succ[0x4E0]));
		tag = outer;
	}

	// gogo_bullet (0x45E) as one step. Its save of V0 to V7 at VSTACK_SPACE and their load back leave the registers as they were, and nothing
	// else reads VSTACK_SPACE, so neither is done (the machine's memory there differs from the reference's, where nothing can see it). The
	// count (bullet_count, 0x28E) goes through a decoder over all 256 values, whose candidate reads that table byte onto Temp and rules the slot
	// out (`free` off) on any 0 bit; a free slot gets VA = the count, the count + 8 stored back (VA counted up 8 for the store and back down by
	// 248), V8 1. Then V3 0 and I = 0xBB8 + VA, each only if something reads it (I is set after the routine's last load, so whether loads and
	// stores move I doesn't matter).
	DecoderRelays? countDecoder;
	Relay? slotFree;
	void CompileGogoBullet(Steps s, Node node, int needed)
	{
		bool Need(int r) => ((needed >> r) & 1) == 1;
		const int count = 0x28E;
		string outer = tag; tag = "native";
		Input ClearChain(InstantToggleChain r) => new(r.Clears.SelectMany(c => c));
		if (countDecoder is null)
		{
			slotFree = NewRelay(startDisabled: true);
			countDecoder = Decoder(256, 8, "bullet count");
			tag = "native";
			SinkGates taken = Sink("slot taken", n: k => slotFree.Disable);
			for (int c = 0; c < 256; c++)
			{
				Steps test = new(this);
				test.Add(tempReset);
				ReadConstant(test, (BulletArea + c) & 0xFFF);
				Apply(test, taken, n: AllBits);
				ChainSteps(test);
				countDecoder.Candidates[c].Output.Connect(test.Relays[0].Input);
			}
		}
		Relay free = slotFree!;
		if (Need(8)) { s.Add(ClearChain(V[8])); }
		s.Add(free.Enable);
		s.Add(tempReset);
		ReadConstant(s, count);
		Decode(s, countDecoder, 8);
		Steps take = new(this);
		take.Add(free.Disable);
		LoadConstant(take, count, V[0xA]);
		take.Add(V[0xA].Inputs[3]);
		StoreConstant(take, count, V[0xA], null);
		for (int k = 3; k < 8; k++) { take.Add(V[0xA].Inputs[k]); }
		if (Need(8)) { take.Add(V[8].Sets[0]); }
		ChainSteps(take);
		// the free relay is shared by every gogo_bullet node, so each node's way on goes through a relay of its own, enabled only here
		Relay go = NewRelay(startDisabled: true);
		free.Output.Connect(go.Input);
		go.Output.Connect(go.Disable.Concat(take.Relays[0].Input));
		s.Add(go.Enable);
		s.Add(free.Input);
		s.Add(go.Disable.Concat(free.Disable));
		if (Need(3)) { s.Add(ClearChain(V[3])); }
		if (Need(16))
		{
			s.Add(clearI); s.Add(new Input(BitsOf(BulletArea, 12).SelectMany(k => IRegister.Sets[k])));
			ProbeOnes(s, OnesOf("addI", V[0xA], IRegister, k => IRegister.Inputs[k]), V[0xA]);
		}
		s.Add(Go(node.Id, node.Succ[0x48A]));
		tag = outer;
	}

	// save v7 at an unknown I (Danm8ku's pew_pew, and its table clear at a game's start), as one routine every such store calls: when I is
	// a slot of the bullet table (0xBB8 + 8 * slot) the slot's rows are cleared (which marks them stored) and V0 to V7's 1s set them, through
	// a line per register bit, a relay per 8 slots on it and the rows' own write gates (enabled for the slot); any other I goes the ordinary
	// way, through the memory. The slot is I's bits 3 to 8 decoded ((0x177 + slot) mod 64), checked against bits 0 to 2 (0) and 9 to 11 (5
	// for slots 0 to 8, 6 for the rest), all from one probe of I.
	// With drawBullet (pew_pew, which then draws bul at the new bullet's position, x and y in V4 and V5): a slot's store goes on to fill the
	// slot's position cache from the rows just stored (its dirty relay) and draw bul through it; any other I draws it by decoding V4 and V5.
	// The two share the lines and groups.
	readonly Relay?[] recordStores = new Relay?[2];
	readonly HashSet<int> recordStoreSites = new();
	// The stores that go through RecordStoreAtI
	public IReadOnlySet<int> RecordStoreSites => recordStoreSites;
	(Relay[,] lines, Relay[,,] groups, (Relay open, Relay close)[] stores)? storeLines;
	Relay RecordStoreAtI(bool drawBullet = false)
	{
		if (recordStores[drawBullet ? 1 : 0] is Relay built) { return built; }
		string outer = tag; tag = "native store";
		string variant = drawBullet ? " draw" : "";
		Relay aligned = NewRelay(), hi5 = NewRelay(), hi6 = NewRelay(), fallback = NewRelay();
		Relay[] go = Enumerable.Range(0, 32).Select(_ => NewRelay(startDisabled: true)).ToArray();
		DecoderRelays decoder = Decoder(64, 6, "slot from I" + variant);
		tag = "native store";
		for (int slot = 0; slot < 32; slot++) { decoder.Candidates[(0x177 + slot) & 63].Output.Connect(go[slot].Enable); }
		SinkGates sink = decoder.Sink;
		var ones = OnesOf("record store I" + variant, IRegister, IRegister, k => k switch
		{
			< 3 => aligned.Disable, < 9 => sink.T[k - 3]!.Input, 9 => hi6.Disable, 10 => hi5.Disable, _ => new Input(),
		});
		var zeros = ZerosOf("record store I" + variant, IRegister, IRegister, k => k switch
		{
			< 3 => new Input(), < 9 => sink.N[k - 3]!.Input, 9 => hi5.Disable, 10 => hi6.Disable, _ => hi5.Disable.Concat(hi6.Disable),
		});
		aligned.Output.Connect(hi5.Input.Concat(hi6.Input));
		hi5.Output.Connect(new Input(Enumerable.Range(0, 9).SelectMany(slot => go[slot].Input)));
		hi6.Output.Connect(new Input(Enumerable.Range(9, 23).SelectMany(slot => go[slot].Input)));

		// the slots' stores: line[k][b] (register k's bit b) fires its group relays, the enabled one the rows' write gates of its 8 slots
		IMemoryRow Row(int slot, int k) => toggleRows[BulletArea + 8 * slot + k];
		if (storeLines is null)
		{
		Relay[,] lines = new Relay[8, 8];
		Relay[,,] groups = new Relay[8, 8, 4];
		for (int k = 0; k < 8; k++)
		{
			for (int b = 0; b < 8; b++)
			{
				lines[k, b] = NewRelay();
				for (int g = 0; g < 4; g++)
				{
					groups[k, b, g] = NewRelay(startDisabled: true);
					int register = k, bit = b, group = g;
					groups[k, b, g].Output.Connect(new Input(Enumerable.Range(8 * group, 8).SelectMany(slot => Row(slot, register).Writes[bit])));
				}
				int r = k, bb = b;
				lines[k, b].Output.Connect(new Input(Enumerable.Range(0, 4).SelectMany(g => groups[r, bb, g].Input)));
			}
		}
		storeLines = (lines, groups, Enumerable.Range(0, 8).Select(k => OnesOf("record store", V[k], V[k], b => lines[k, b].Input)).ToArray());
		}
		var (_, groupRelays, stores) = storeLines.Value;
		// bul (the movement sprite table's value 3, 0x27E) at the decoded position, without its collision (the caller takes VF)
		void DrawBul(Steps steps, bool narrow)
		{
			steps.Add(collisionLatch.Disable);
			steps.Add(spriteVariants[(0x272, 3)].Input);
			steps.Add(narrow ? unsetXNarrow : unsetX); steps.Add(yDecoders[4].unset);
		}
		if (drawBullet) { SpriteTable(0x272); }
		for (int slot = 0; slot < 32; slot++)
		{
			Steps store = new(this);
			int g = slot / 8, at = slot;
			store.Add(new Input(Enumerable.Range(0, 8).SelectMany(k => Row(at, k).Clears.SelectMany(c => c))));
			Input Gates(bool on) => new Input(Enumerable.Range(0, 8).SelectMany(k => (on ? Row(at, k).EnableWrites : Row(at, k).DisableWrites).SelectMany(e => e)))
				.Concat(new Input(Enumerable.Range(0, 64).SelectMany(i => on ? groupRelays[i / 8, i % 8, g].Enable : groupRelays[i / 8, i % 8, g].Disable)));
			store.Add(Gates(true));
			for (int k = 0; k < 8; k++) { ProbeOnes(store, stores[k], V[k]); }
			store.Add(Gates(false));
			if (drawBullet)
			{
				SlotCache cache = CacheOf(slot);
				store.Add(cache.Dirty);
				store.Add(cache.X.Draw); store.Add(cache.Y.Draw);
				DrawBul(store, narrow: true);
			}
			ChainSteps(store);
			// a new bullet weighs 4 in the frame's load (Chip8Machine's heavyBullets)
			go[slot].Output.Connect(go[slot].Disable.Concat(fallback.Disable).Concat(BulletLoad?.Inputs[2] ?? new Input()).Concat(store.Relays[0].Input));
		}

		// anything else: through the memory
		Steps generic = new(this);
		List<Bank> banks = Banks.ToList();
		for (int r = 0; r < 8; r++)
		{
			Read(generic, V[r]);
			WriteMemory(generic, r, banks, () => Apply(generic, Sink("memwrite", t: AllBankWrites), t: AllBits), next: r > 0);
		}
		if (drawBullet)
		{
			DecodeFrom(generic, V[4], xDecoder, 6);
			DecodeFrom(generic, V[5], yDecoders[4].decoder, 5);
			DrawBul(generic, narrow: false);
		}
		ChainSteps(generic);
		fallback.Output.Connect(fallback.Disable.Concat(new Input(go.SelectMany(r => r.Disable))).Concat(generic.Relays[0].Input));

		Steps s = new(this);
		s.Add(aligned.Enable.Concat(hi5.Enable).Concat(hi6.Enable).Concat(fallback.Enable));
		s.Add(decoder.Reset); s.Add(sink.On); s.Add(zeros.arm);
		ProbeOnes(s, ones, IRegister);
		s.Add(zeros.fire); s.Add(sink.Off); s.Add(decoder.Fire);
		s.Add(aligned.Input);
		s.Add(fallback.Input);
		ChainSteps(s);
		Relay entry = NewRelay();
		entry.Output.Connect(s.Relays[0].Input);
		tag = outer;
		return recordStores[drawBullet ? 1 : 0] = entry;
	}

	// pew_pew (0x576: save v7, i := bul, sprite x y 4) as one step: RecordStoreAtI's store and draw, then VF (the collision) and I (bul, set
	// after the store whether or not stores move I) where something reads them
	void CompilePewPew(Steps s, Node node, int needed)
	{
		bool Need(int r) => ((needed >> r) & 1) == 1;
		string outer = tag; tag = "native";
		recordStoreSites.Add(node.Addr);
		s.Add(RecordStoreAtI(drawBullet: true));
		if (Need(0xF)) { s.Add(new Input(V[0xF].Clears.SelectMany(c => c))); s.Add(collisionLatch.Input); }
		if (Need(16)) { s.Add(clearI); s.Add(new Input(BitsOf(0x27E, 12).SelectMany(k => IRegister.Sets[k]))); }
		s.Add(Go(node.Id, node.Succ[0x57C]));
		tag = outer;
	}

	// draw_bullets (0x428) as one step: each live slot's movement sprite is drawn from its record's bytes, decoded straight from the rows.
	// What it leaves behind (slot 31's record in V0 to V7, VB 0, the last collision, I) isn't written: nothing reads it before writing it.
	void CompileDrawBullets(Steps s, Node node, int needed)
	{
		string outer = tag; tag = "native";
		foreach (int r in new[] { 3, 0xF, 16 }) { if (((needed >> r) & 1) == 1) { throw new NotSupportedException($"draw_bullets' leftover {(r == 16 ? "I" : $"V{r:X}")} is read after it"); } }
		for (int slot = 0; slot < 32; slot++)
		{
			int at = BulletArea + 8 * slot;
			SpriteTable(0x272);
			SlotCache cache = CacheOf(slot);
			// the position from the caches; the sprite from its cache, where a bullet that crossed no pixel (so 0, nothing to draw) is
			// skipped, or decoded from so after a store to it
			Steps check = new(this), draw = new(this), decode = new(this);
			check.Add(cache.Dirty); check.Add(cache.SpriteDirty); check.Add(cache.SpriteClean);
			// the cached sprite is a movement sprite (columns 0 to 3: the narrow x select); a decoded one can be any row of the table, so its x
			// goes through the full decoder
			draw.Add(cache.X.Draw); draw.Add(cache.Y.Draw);
			draw.Add(collisionLatch.Disable);
			draw.Add(new Input(cache.XCase.SelectMany(c => c.Input)));
			draw.Add(unsetXNarrow); draw.Add(yDecoders[4].unset);
			ChainSteps(draw);
			DecodeAt(decode, at + 4, xDecoder, 6); decode.Add(cache.Y.Draw);
			decode.Add(collisionLatch.Disable);
			DecodeAt(decode, at + 3, SpriteTable(0x272), 6);
			decode.Add(unsetX); decode.Add(yDecoders[4].unset);
			ChainSteps(decode);
			cache.SpriteClean.Output.Connect(cache.Moved.Input);
			cache.Moved.Output.Connect(draw.Relays[0].Input);
			cache.SpriteDirty.Output.Connect(decode.Relays[0].Input);
			ChainSteps(check);
			cache.LiveDraw.Output.Connect(check.Relays[0].Input);
			s.Add(cache.LiveDirty); s.Add(cache.LiveDraw);
		}
		LeaveSlot31(s, needed, 0);
		s.Add(Go(node.Id, node.Succ[0x444]));
		tag = outer;
	}
	// The loops' leftovers: slot 31's record in the registers something reads (except `already`, which the loop left right), and VB 0
	void LeaveSlot31(Steps s, int needed, int already)
	{
		for (int r = 0; r < 8; r++) { if (((needed >> r) & 1) == 1 && ((already >> r) & 1) == 0) { LoadConstant(s, BulletArea + 8 * 31 + r, V[r]); } }
		if (((needed >> 0xB) & 1) == 1) { s.Add(new Input(V[0xB].Clears.SelectMany(c => c))); }
	}

	// act_bullets (0x446) as one step: each live slot is moved by the move core working on the slot's own bytes (the bullet table is toggle
	// rows), so nothing is loaded or saved; a bullet that left the screen is destroyed (marked free, and erased with its clear sprite). The
	// leftovers: the last live bullet's VA, VC, VD, V8 and VF, written after the loop from its record; V0 to V7 are copied from slot 31's
	// record (V3 shifted when slot 31 was destroyed, as destroy_bullet leaves it); VB is 0.
	void CompileActBullets(Steps s, Node node, int needed)
	{
		string outer = tag; tag = "native";
		if (((needed >> 16) & 1) == 1) { throw new NotSupportedException("act_bullets' leftover I is read after it"); }
		if (toggleRows.Count == 0) { throw new NotSupportedException("act_bullets needs the bullet table as toggle rows"); }
		bool Need(int r) => ((needed >> r) & 1) == 1;
		bool NeedF = Need(0xF);
		Input ClearChain(InstantToggleChain r) => new(r.Clears.SelectMany(c => c));
		Relay slot31Destroyed = NewRelay(startDisabled: true);
		// The last live bullet's move leftovers (V8, VA, VC, VD, and VF 1 if it stayed) are written once, after the loop, from its record:
		// each live slot enables its marker (and its stay marker if it stayed), and the highest marker enabled writes them and disables the rest
		Relay[] last = Enumerable.Range(0, 32).Select(_ => NewRelay(startDisabled: true)).ToArray();
		Relay[] lastStayed = Enumerable.Range(0, 32).Select(_ => NewRelay(startDisabled: true)).ToArray();
		Relay setF = NewRelay();
		setF.Output.Connect(V[0xF].Sets[0]);
		// A bullet whose acceleration takes vx to exactly 255 and that stays on the screen isn't saved by the original (act_bullets saves only
		// when vx isn't 255 after the move): its record keeps the old values, while the registers hold the moved ones. That needs vx < 250
		// and ax = 255 - vx = ~vx, so ax of 6 or more; it is checked before the move (a pair of latches per bit, one enabled for vx's 1, one
		// for its 0, fired by ax's 1s and 0s: any bit where they agree rules it out), so is kept aside, and once the bullet stays, the
		// leftovers are taken from the moved record and the move is undone in place: the steps back, vx = ~ax, the fractions take back what
		// they got (x_f gets (ax << 1) + 2, which is minus ~ax << 1; y_f minus vy << 1, that is (~vy << 1) + 2), and so from the copy.
		InstantToggleChain keptSo = new(8, x, y, 5, 5);
		entities.AddRange(keptSo.Entities);
		Relay copy31 = NewRelay();
		for (int slot = 0; slot < 32; slot++)
		{
			int at = BulletArea + 8 * slot;
			InstantToggleChain[] record = Enumerable.Range(0, 8).Select(r => toggleRows[at + r].Byte).ToArray();
			SlotCache cache = CacheOf(slot);
			MoveCore core = MoveBulletCore(0, record, cache.Hooks);
			Relay stay = NewRelay(), destroy = NewRelay(startDisabled: true);
			Steps work = new(this), staying = new(this), destroying = new(this);
			work.Add(cache.Dirty.Input.Concat(last[slot].Enable).Concat(cache.AxDirty.Input));
			work.Add(cache.FrozenCheck.Input);
			if (BulletLoad is not null) { work.Add(BulletLoad.Inputs[0]); }
			work.Add(core.Start.Input);
			(Input arm, Input disarm) = OnLatch(core.Out, destroy);
			work.Add(arm); work.Add(core.Out.Input); work.Add(disarm);
			work.Add(stay.Input);
			work.Add(stay.Enable.Concat(core.OutX.Disable).Concat(core.OutY.Disable).Concat(core.Out.Disable));
			staying.Add(NeedF ? lastStayed[slot].Enable : new Input());

			// the frozen check, before the move
			Relay[] agreeOne = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			Relay[] agreeZero = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			Relay maybeFrozen = NewRelay(startDisabled: true), below = NewRelay(startDisabled: true), frozen = NewRelay(startDisabled: true);
			foreach (Relay agree in agreeOne.Concat(agreeZero)) { agree.Output.Connect(maybeFrozen.Disable); }
			Steps check = new(this), belowCheck = new(this), confirm = new(this), revert = new(this);
			check.Add(maybeFrozen.Enable.Concat(new Input(agreeOne.Concat(agreeZero).SelectMany(a => a.Disable))));
			var vxOnes = OnesOf("frozen vx", record[0], record[0], k => agreeOne[k].Enable);
			var vxZeros = ZerosOf("frozen vx", record[0], record[0], k => agreeZero[k].Enable);
			check.Add(vxZeros.arm); ProbeOnes(check, vxOnes, record[0]); check.Add(vxZeros.fire);
			var axOnes = OnesOf("frozen ax", record[2], record[2], k => agreeOne[k].Input);
			var axZeros = ZerosOf("frozen ax", record[2], record[2], k => agreeZero[k].Input);
			check.Add(axZeros.arm); ProbeOnes(check, axOnes, record[2]); check.Add(axZeros.fire);
			check.Add(maybeFrozen.Input);
			ChainSteps(check);
			cache.FrozenCheck.Output.Connect(check.Relays[0].Input);
			belowCheck.Add(below.Enable);
			IfAbove(belowCheck, record[0], 249, below.Disable);
			belowCheck.Add(below.Input);
			ChainSteps(belowCheck);
			maybeFrozen.Output.Connect(maybeFrozen.Disable.Concat(belowCheck.Relays[0].Input));
			confirm.Add(frozen.Enable);
			Copy(confirm, record[3], keptSo);
			ChainSteps(confirm);
			below.Output.Connect(below.Disable.Concat(confirm.Relays[0].Input));
			staying.Add(frozen.Input);
			destroying.Add(frozen.Disable);
			// undoing the move, once it stayed
			revert.Add(last[slot].Input);
			if (slot == 31)
			{
				for (int r = 0; r < 8; r++) { if (Need(r)) { Copy(revert, record[r], V[r]); } }
				revert.Add(copy31.Disable);
			}
			Steps xBack = new(this), yBack = new(this);
			for (int k = 0; k < 8; k++) { xBack.Add(record[4].Inputs[k]); yBack.Add(record[5].Inputs[k]); }
			xBack.Add(cache.X.Down); yBack.Add(cache.Y.Down);
			Steps xFore = new(this), yFore = new(this);
			xFore.Add(record[4].Inputs[0]); xFore.Add(cache.X.Up); yFore.Add(record[5].Inputs[0]); yFore.Add(cache.Y.Up);
			foreach (Steps chain in new[] { xBack, yBack, xFore, yFore }) { ChainSteps(chain); }
			ProbeOnes(revert, OnesOf("frozen steps", record[3], record[3], k => k switch
			{
				0 => xBack.Relays[0].Input, 1 => xFore.Relays[0].Input, 2 => yBack.Relays[0].Input, 3 => yFore.Relays[0].Input, _ => new Input(),
			}), record[3]);
			revert.Add(ClearChain(record[0]));
			ProbeZeros(revert, ZerosOf("frozen restore vx", record[2], record[0], k => record[0].Sets[k]), record[2]);
			ProbeOnes(revert, OnesOf("frozen restore x_f", record[2], record[6], k => k < 7 ? record[6].Inputs[k + 1] : new Input()), record[2]);
			revert.Add(record[6].Inputs[1]);
			ProbeZeros(revert, ZerosOf("frozen restore y_f", record[1], record[7], k => k < 7 ? record[7].Inputs[k + 1] : new Input()), record[1]);
			revert.Add(record[7].Inputs[1]);
			revert.Add(ClearChain(record[3]));
			Copy(revert, keptSo, record[3]);
			revert.Add(cache.SpriteDirty.Enable.Concat(cache.SpriteClean.Disable));
			ChainSteps(revert);
			frozen.Output.Connect(frozen.Disable.Concat(revert.Relays[0].Input));

			// the leftovers from this slot's record: V8 is the x step (so's bit 0: 1, bit 1: 255), VD the y step times 4 (so's bits 2 and 3),
			// VA 1 for a step up the screen (so's bit 3), VC a copy of vy
			Steps leftovers = new(this);
			Input clear = new();
			foreach (int r in new[] { 8, 0xA, 0xD }) { if (Need(r)) { clear = clear.Concat(ClearChain(V[r])); } }
			leftovers.Add(clear);
			if (Need(0xC)) { Copy(leftovers, record[1], V[0xC]); }
			Input When(int r, Input input) => Need(r) ? input : new Input();
			Input Set(int r, int value) => When(r, new Input(BitsOf(value).SelectMany(k => V[r].Sets[k])));
			if (Need(8) || Need(0xA) || Need(0xD))
			{
				ProbeOnes(leftovers, OnesOf("leftovers", record[3], record[3], k => k switch
				{
					0 => Set(8, 1), 1 => Set(8, 0xFF), 2 => Set(0xD, 4), 3 => Set(0xD, 8).Concat(Set(0xA, 1)), _ => new Input(),
				}), record[3]);
			}
			if (NeedF) { leftovers.Add(lastStayed[slot].Input); }
			ChainSteps(leftovers);
			lastStayed[slot].Output.Connect(lastStayed[slot].Disable.Concat(ClearChain(V[0xF])).Concat(setF.Input));
			last[slot].Output.Connect(last[slot].Disable.Concat(new Input(Enumerable.Range(0, slot).SelectMany(j => last[j].Disable.Concat(lastStayed[j].Disable))))
				.Concat(leftovers.Relays[0].Input));
			// destroy_bullet: vx := 255 (saved: it is the record), and the clear sprite for its offset at its position
			destroying.Add(ClearChain(record[0]).Concat(cache.NotLive)); destroying.Add(new Input(AllBits.SelectMany(k => record[0].Sets[k])));
			destroying.Add(cache.X.Draw); destroying.Add(cache.Y.Draw);
			destroying.Add(collisionLatch.Disable);
			DecodeFrom(destroying, record[3], SpriteTable(0x29E), 6);
			destroying.Add(unsetXNarrow); destroying.Add(yDecoders[4].unset);
			destroying.Add(ClearChain(V[0xF])); destroying.Add(collisionLatch.Input);
			if (slot == 31) { destroying.Add(slot31Destroyed.Enable); }
			foreach (Steps chain in new[] { work, staying, destroying }) { ChainSteps(chain); }
			cache.LiveAct.Output.Connect(work.Relays[0].Input);
			stay.Output.Connect(staying.Relays[0].Input);
			destroy.Output.Connect(stay.Disable.Concat(destroying.Relays[0].Input));
			s.Add(cache.LiveDirty); s.Add(cache.LiveAct);
		}
		s.Add(new Input(Enumerable.Range(0, 32).Reverse().SelectMany(slot => last[slot].Input)));
		// slot 31's record into V0 to V7, unless slot 31 froze (its registers are the moved values, already copied)
		Steps copies = new(this);
		for (int r = 0; r < 8; r++) { if (((needed >> r) & 1) == 1) { Copy(copies, toggleRows[BulletArea + 8 * 31 + r].Byte, V[r]); } }
		if (copies.Relays.Count > 0)
		{
			ChainSteps(copies);
			copy31.Output.Connect(copies.Relays[0].Input);
			s.Add(copy31.Input);
		}
		s.Add(copy31.Enable);
		if (((needed >> 3) & 1) == 1)
		{
			Steps shift = new(this);
			ShiftV3TwiceLeft(shift);
			ChainSteps(shift);
			slot31Destroyed.Output.Connect(slot31Destroyed.Disable.Concat(shift.Relays[0].Input));
			s.Add(slot31Destroyed.Input);
		}
		s.Add(slot31Destroyed.Disable);
		if (((needed >> 0xB) & 1) == 1) { s.Add(ClearChain(V[0xB])); }
		s.Add(Go(node.Id, node.Succ[0x45C]));
		tag = outer;
	}
}
